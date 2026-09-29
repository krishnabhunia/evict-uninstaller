using System.Globalization;

namespace Evict.Core.Services;

/// <summary>Pure helpers for the Task Scheduler job that runs "Evict.exe --scheduled-scan". Unit tested.</summary>
public static class ScheduledScanTask
{
    public const string TaskName = "Evict Software Health scan";
    public static readonly string[] Modes = { "Off", "Daily", "Weekly" };
    private static readonly string[] DayCodes = { "SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT" };

    public static bool IsValidMode(string? mode) => mode != null && Modes.Contains(mode, StringComparer.OrdinalIgnoreCase);

    /// <summary>schtasks arguments that create (or replace) the job. Hour 0–23, weekday 0 = Sunday … 6 = Saturday.</summary>
    public static string BuildCreateArguments(string exePath, string mode, int hour, int weekday)
    {
        if (!IsValidMode(mode) || mode.Equals("Off", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("mode must be Daily or Weekly", nameof(mode));
        hour = Math.Clamp(hour, 0, 23);
        weekday = Math.Clamp(weekday, 0, 6);
        var tr = $"\\\"{exePath}\\\" --scheduled-scan";
        var schedule = mode.Equals("Daily", StringComparison.OrdinalIgnoreCase) ? "/SC DAILY" : $"/SC WEEKLY /D {DayCodes[weekday]}";
        return $"/Create /F /TN \"{TaskName}\" /TR \"{tr}\" {schedule} /ST {hour:00}:00";
    }

    public static string BuildDeleteArguments() => $"/Delete /F /TN \"{TaskName}\"";
    public static string BuildCreateFromXmlArguments(string xmlPath) => $"/Create /F /TN \"{TaskName}\" /XML \"{xmlPath}\"";

    /// <summary>
    /// Task Scheduler XML for the job: interactive token of the current user (no password), least privilege, runs on
    /// battery, starts as soon as possible after a missed schedule, one instance at a time.
    /// </summary>
    public static string BuildTaskXml(string exePath, string mode, int hour, int weekday, string? userSid)
    {
        if (!IsValidMode(mode) || mode.Equals("Off", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("mode must be Daily or Weekly", nameof(mode));
        hour = Math.Clamp(hour, 0, 23);
        weekday = Math.Clamp(weekday, 0, 6);
        var start = new DateTime(DateTime.Today.Year, DateTime.Today.Month, DateTime.Today.Day, hour, 0, 0).ToString("yyyy-MM-dd'T'HH:mm:ss");
        var dayNames = new[] { "Sunday", "Monday", "Tuesday", "Wednesday", "Thursday", "Friday", "Saturday" };
        var schedule = mode.Equals("Daily", StringComparison.OrdinalIgnoreCase)
            ? "<ScheduleByDay><DaysInterval>1</DaysInterval></ScheduleByDay>"
            : $"<ScheduleByWeek><WeeksInterval>1</WeeksInterval><DaysOfWeek><{dayNames[weekday]} /></DaysOfWeek></ScheduleByWeek>";
        var userId = string.IsNullOrEmpty(userSid) ? "" : $"<UserId>{System.Security.SecurityElement.Escape(userSid)}</UserId>";
        var exe = System.Security.SecurityElement.Escape(exePath);
        return $@"<?xml version=""1.0"" encoding=""UTF-16""?>
<Task version=""1.2"" xmlns=""http://schemas.microsoft.com/windows/2004/02/mit/task"">
  <RegistrationInfo><Description>Evict Uninstaller – scheduled Software Health scan (result is shown as a notification).</Description></RegistrationInfo>
  <Triggers><CalendarTrigger><StartBoundary>{start}</StartBoundary><Enabled>true</Enabled>{schedule}</CalendarTrigger></Triggers>
  <Principals><Principal id=""Author"">{userId}<LogonType>InteractiveToken</LogonType><RunLevel>LeastPrivilege</RunLevel></Principal></Principals>
  <Settings>
    <MultipleInstancesPolicy>IgnoreNew</MultipleInstancesPolicy>
    <DisallowStartIfOnBatteries>false</DisallowStartIfOnBatteries>
    <StopIfGoingOnBatteries>false</StopIfGoingOnBatteries>
    <AllowHardTerminate>true</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT1H</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author""><Exec><Command>{exe}</Command><Arguments>--scheduled-scan</Arguments></Exec></Actions>
</Task>";
    }
    public static string BuildQueryArguments() => $"/Query /TN \"{TaskName}\" /FO LIST";

    /// <summary>"Daily at 10:00" / "Weekly on Sunday at 10:00" / "Off".</summary>
    public static string Describe(string mode, int hour, int weekday)
    {
        if (!IsValidMode(mode) || mode.Equals("Off", StringComparison.OrdinalIgnoreCase)) return "Off";
        var time = new DateTime(2000, 1, 1, Math.Clamp(hour, 0, 23), 0, 0).ToString("t", CultureInfo.CurrentCulture);
        return mode.Equals("Daily", StringComparison.OrdinalIgnoreCase)
            ? $"Daily at {time}"
            : $"Weekly on {CultureInfo.CurrentCulture.DateTimeFormat.GetDayName((DayOfWeek)Math.Clamp(weekday, 0, 6))} at {time}";
    }
}

/// <summary>Creates / removes the per-user Task Scheduler job through schtasks.exe (no administrator rights needed).</summary>
public sealed class ScheduledScanService
{
    private static string SchTasks => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");

    public async Task<(bool Ok, string? Error)> ApplyAsync(string mode, int hour, int weekday, CancellationToken ct)
    {
        try
        {
            if (!ScheduledScanTask.IsValidMode(mode) || mode.Equals("Off", StringComparison.OrdinalIgnoreCase))
            {
                if (!await ExistsAsync(ct).ConfigureAwait(false)) return (true, null);
                var del = await ProcessRunner.RunCapturedAsync(SchTasks, ScheduledScanTask.BuildDeleteArguments(), ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                if (!del.Success && await ExistsAsync(ct).ConfigureAwait(false)) return (false, Clean(del));
                Log.Info("Scheduled scan removed.");
                return (true, null);
            }
            // Task definition as XML: lets the job run on battery and catch up a missed start (schtasks' switches cannot express that).
            var xmlPath = Path.Combine(AppPaths.DataRoot, "scheduled-scan.xml");
            File.WriteAllText(xmlPath, ScheduledScanTask.BuildTaskXml(UpdateService.ExePath, mode, hour, weekday, CurrentUserSid()), System.Text.Encoding.Unicode); // UTF-16 LE + BOM, as declared
            var res = await ProcessRunner.RunCapturedAsync(SchTasks, ScheduledScanTask.BuildCreateFromXmlArguments(xmlPath), ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
            if (!res.Success)
            {
                // Fallback to the plain switches (older builds / odd policies).
                var args = ScheduledScanTask.BuildCreateArguments(UpdateService.ExePath, mode, hour, weekday);
                var res2 = await ProcessRunner.RunCapturedAsync(SchTasks, args, ct, TimeSpan.FromSeconds(30)).ConfigureAwait(false);
                if (!res2.Success) return (false, Clean(res));
            }
            Log.Info($"Scheduled scan set: {ScheduledScanTask.Describe(mode, hour, weekday)}");
            return (true, null);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<bool> ExistsAsync(CancellationToken ct)
    {
        try
        {
            var res = await ProcessRunner.RunCapturedAsync(SchTasks, ScheduledScanTask.BuildQueryArguments(), ct, TimeSpan.FromSeconds(20)).ConfigureAwait(false);
            return res.Success;
        }
        catch { return false; }
    }

    private static string? CurrentUserSid()
    {
        try { return System.Security.Principal.WindowsIdentity.GetCurrent().User?.Value; } catch { return null; }
    }

    private static string Clean(ProcessResult r)
    {
        var text = (r.StdErr + "\n" + r.StdOut).Trim();
        return string.IsNullOrEmpty(text) ? $"schtasks exit code {r.ExitCode}" : text.Replace("ERROR: ", "");
    }
}
