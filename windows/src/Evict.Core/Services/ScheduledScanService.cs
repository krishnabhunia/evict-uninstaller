using System.Globalization;
using System.Xml.Linq;
using System.Text.RegularExpressions;
using Evict.Core.Util;

namespace Evict.Core.Services;

/// <summary>Pure helpers for the Task Scheduler job that runs "Evict.exe --scheduled-scan". Unit tested.</summary>
public static class ScheduledScanTask
{
    public const string LegacyTaskName = "Evict Software Health scan";
    public static string TaskNameForUser(string userSid)
    {
        if (!Regex.IsMatch(userSid ?? "", @"^S-1-(?:\d+-)+\d+$", RegexOptions.CultureInvariant))
            throw new ArgumentException("A valid user security identifier is required.", nameof(userSid));
        return LegacyTaskName + " - " + userSid;
    }
    public static readonly string[] Modes = { "Off", "Daily", "Weekly" };
    private static readonly string[] DayCodes = { "SUN", "MON", "TUE", "WED", "THU", "FRI", "SAT" };

    public static bool IsValidMode(string? mode) => mode != null && Modes.Contains(mode, StringComparer.OrdinalIgnoreCase);

    /// <summary>schtasks arguments that create (or replace) the job. Hour 0–23, weekday 0 = Sunday … 6 = Saturday.</summary>
    public static string BuildCreateArguments(string exePath, string mode, int hour, int weekday, string userSid)
    {
        if (!IsValidMode(mode) || mode.Equals("Off", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("mode must be Daily or Weekly", nameof(mode));
        hour = Math.Clamp(hour, 0, 23);
        weekday = Math.Clamp(weekday, 0, 6);
        var tr = $"\\\"{exePath}\\\" --scheduled-scan";
        var schedule = mode.Equals("Daily", StringComparison.OrdinalIgnoreCase) ? "/SC DAILY" : $"/SC WEEKLY /D {DayCodes[weekday]}";
        return $"/Create /F /TN \"{TaskNameForUser(userSid)}\" /TR \"{tr}\" {schedule} /ST {hour:00}:00";
    }

    public static string BuildDeleteArguments(string userSid) => $"/Delete /F /TN \"{TaskNameForUser(userSid)}\"";
    public static string BuildCreateFromXmlArguments(string xmlPath, string userSid) => $"/Create /F /TN \"{TaskNameForUser(userSid)}\" /XML \"{xmlPath}\"";

    /// <summary>
    /// Task Scheduler XML for the job: interactive token of the current user (no password), least privilege, runs on
    /// battery, starts as soon as possible after a missed schedule, one instance at a time.
    /// </summary>
    public static string BuildTaskXml(string exePath, string mode, int hour, int weekday, string? userSid)
    {
        TaskNameForUser(userSid!); // An unresolved identity must never create a shared/default-principal task.
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
    <AllowHardTerminate>false</AllowHardTerminate>
    <StartWhenAvailable>true</StartWhenAvailable>
    <RunOnlyIfNetworkAvailable>false</RunOnlyIfNetworkAvailable>
    <IdleSettings><StopOnIdleEnd>false</StopOnIdleEnd><RestartOnIdle>false</RestartOnIdle></IdleSettings>
    <AllowStartOnDemand>true</AllowStartOnDemand>
    <Enabled>true</Enabled>
    <Hidden>false</Hidden>
    <RunOnlyIfIdle>false</RunOnlyIfIdle>
    <WakeToRun>false</WakeToRun>
    <ExecutionTimeLimit>PT0S</ExecutionTimeLimit>
    <Priority>7</Priority>
  </Settings>
  <Actions Context=""Author""><Exec><Command>{exe}</Command><Arguments>--scheduled-scan</Arguments></Exec></Actions>
</Task>";
    }
    public static string BuildQueryArguments(string userSid) => $"/Query /TN \"{TaskNameForUser(userSid)}\" /FO LIST";

    internal static bool LegacyTaskBelongsToUser(string xml, string userSid)
    {
        try
        {
            TaskNameForUser(userSid);
            var doc = XDocument.Parse(xml);
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            return doc.Root?.Name == ns + "Task"
                && doc.Descendants(ns + "Principal").SingleOrDefault()?.Element(ns + "UserId")?.Value.Equals(userSid, StringComparison.OrdinalIgnoreCase) == true
                && doc.Descendants(ns + "Exec").SingleOrDefault() is { } exec
                && PathUtil.LeafName(exec.Element(ns + "Command")?.Value).Equals("Evict.exe", StringComparison.OrdinalIgnoreCase)
                && exec.Element(ns + "Arguments")?.Value.Trim() == "--scheduled-scan";
        }
        catch { return false; }
    }

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
    private readonly Func<string> _userSid;
    private readonly Func<string, CancellationToken, Task<ProcessResult>> _run;
    private readonly Func<string> _definitionPath;
    private readonly Func<string> _exePath;
    private readonly SemaphoreSlim _mutation = new(1, 1);

    public ScheduledScanService()
    {
        _userSid = CurrentUserSid;
        _run = (args, ct) => ProcessRunner.RunCapturedAsync(SchTasks, args, ct, TimeSpan.FromSeconds(30));
        _definitionPath = () => Path.Combine(AppPaths.DataRoot, "scheduled-scan.xml");
        _exePath = () => UpdateService.ExePath;
    }

    internal ScheduledScanService(Func<string> userSid, Func<string, CancellationToken, Task<ProcessResult>> run,
        string definitionPath, string exePath)
    {
        _userSid = userSid;
        _run = run;
        _definitionPath = () => definitionPath;
        _exePath = () => exePath;
    }

    /// <summary>Upgrade only a positively identified legacy task owned by this account. Other users' tasks are left intact.</summary>
    public Task<(bool Ok, string? Error)> MigrateLegacyAsync(string mode, int hour, int weekday, CancellationToken ct) =>
        RunSerializedAsync(() => MigrateLegacyCoreAsync(mode, hour, weekday, ct), ct);

    private async Task<(bool Ok, string? Error)> MigrateLegacyCoreAsync(string mode, int hour, int weekday, CancellationToken ct)
    {
        try
        {
            var sid = _userSid();
            if (!await OwnedLegacyTaskExistsAsync(sid, ct).ConfigureAwait(false)) return (true, null);
            return await ApplyCoreAsync(mode, hour, weekday, ct).ConfigureAwait(false);
        }
        catch (Exception ex) { return (false, "Could not migrate this user's old scheduled scan: " + ex.Message); }
    }

    public Task<(bool Ok, string? Error)> ApplyAsync(string mode, int hour, int weekday, CancellationToken ct) =>
        RunSerializedAsync(() => ApplyCoreAsync(mode, hour, weekday, ct), ct);

    private async Task<(bool Ok, string? Error)> RunSerializedAsync(Func<Task<(bool Ok, string? Error)>> action, CancellationToken ct)
    {
        try
        {
            await _mutation.WaitAsync(ct).ConfigureAwait(false);
            try { return await action().ConfigureAwait(false); }
            finally { _mutation.Release(); }
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    private async Task<(bool Ok, string? Error)> ApplyCoreAsync(string mode, int hour, int weekday, CancellationToken ct)
    {
        try
        {
            var sid = _userSid();
            if (!ScheduledScanTask.IsValidMode(mode) || mode.Equals("Off", StringComparison.OrdinalIgnoreCase))
            {
                if (await CurrentTaskExistsAsync(sid, ct).ConfigureAwait(false))
                {
                    var del = await _run(ScheduledScanTask.BuildDeleteArguments(sid), ct).ConfigureAwait(false);
                    if (!del.Success && await CurrentTaskExistsAsync(sid, ct).ConfigureAwait(false)) return (false, Clean(del));
                }
                var legacyError = await RemoveOwnedLegacyTaskAsync(sid, ct).ConfigureAwait(false);
                if (legacyError != null) return (false, legacyError);
                Log.Info("Scheduled scan removed.");
                return (true, null);
            }
            // Task definition as XML: lets the job run on battery and catch up a missed start (schtasks' switches cannot express that).
            var xmlPath = _definitionPath();
            File.WriteAllText(xmlPath, ScheduledScanTask.BuildTaskXml(_exePath(), mode, hour, weekday, sid), System.Text.Encoding.Unicode); // UTF-16 LE + BOM, as declared
            var res = await _run(ScheduledScanTask.BuildCreateFromXmlArguments(xmlPath, sid), ct).ConfigureAwait(false);
            if (!res.Success)
            {
                // Plain CLI switches cannot express the no-termination policy; do not silently downgrade it.
                return (false, Clean(res));
            }
            var migrationError = await RemoveOwnedLegacyTaskAsync(sid, ct).ConfigureAwait(false);
            if (migrationError != null) return (false, migrationError);
            Log.Info($"Scheduled scan set: {ScheduledScanTask.Describe(mode, hour, weekday)}");
            return (true, null);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    public async Task<bool> ExistsAsync(CancellationToken ct)
    {
        try
        {
            var sid = _userSid();
            return await CurrentTaskExistsAsync(sid, ct).ConfigureAwait(false) || await OwnedLegacyTaskExistsAsync(sid, ct).ConfigureAwait(false);
        }
        catch { return false; }
    }

    public static string CurrentUserSid()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new InvalidOperationException("The current user's security identifier could not be read.");
    }

    private async Task<bool> CurrentTaskExistsAsync(string sid, CancellationToken ct) =>
        (await _run(ScheduledScanTask.BuildQueryArguments(sid), ct).ConfigureAwait(false)).Success;

    private async Task<bool> OwnedLegacyTaskExistsAsync(string sid, CancellationToken ct)
    {
        var legacy = await _run($"/Query /TN \"{ScheduledScanTask.LegacyTaskName}\" /XML", ct).ConfigureAwait(false);
        return legacy.Success && ScheduledScanTask.LegacyTaskBelongsToUser(legacy.StdOut, sid);
    }

    private async Task<string?> RemoveOwnedLegacyTaskAsync(string sid, CancellationToken ct)
    {
        if (!await OwnedLegacyTaskExistsAsync(sid, ct).ConfigureAwait(false)) return null;
        var res = await _run($"/Delete /F /TN \"{ScheduledScanTask.LegacyTaskName}\"", ct).ConfigureAwait(false);
        return res.Success ? null : "Could not remove the old schedule owned by this user: " + Clean(res);
    }

    private static string Clean(ProcessResult r)
    {
        var text = (r.StdErr + "\n" + r.StdOut).Trim();
        return string.IsNullOrEmpty(text) ? $"schtasks exit code {r.ExitCode}" : text.Replace("ERROR: ", "");
    }
}
