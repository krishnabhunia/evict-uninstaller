using System.Globalization;
using System.Xml.Linq;
using System.Runtime.InteropServices;
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
    public static string BuildTaskXml(string exePath, string mode, int hour, int weekday, string? userSid, DateTime? localNow = null)
    {
        TaskNameForUser(userSid!); // An unresolved identity must never create a shared/default-principal task.
        if (!IsValidMode(mode) || mode.Equals("Off", StringComparison.OrdinalIgnoreCase)) throw new ArgumentException("mode must be Daily or Weekly", nameof(mode));
        hour = Math.Clamp(hour, 0, 23);
        weekday = Math.Clamp(weekday, 0, 6);
        var now = localNow ?? DateTime.Now;
        var next = now.Date.AddHours(hour);
        if (mode.Equals("Weekly", StringComparison.OrdinalIgnoreCase))
            next = next.AddDays((weekday - (int)now.DayOfWeek + 7) % 7);
        if (next <= now) next = next.AddDays(mode.Equals("Daily", StringComparison.OrdinalIgnoreCase) ? 1 : 7);
        var start = next.ToString("yyyy-MM-dd'T'HH:mm:ss", CultureInfo.InvariantCulture);
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
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            var command = XDocument.Parse(xml).Root?.Element(ns + "Actions")?.Elements().Single().Element(ns + "Command")?.Value;
            return command != null && PathUtil.LeafName(command).Equals("Evict.exe", StringComparison.OrdinalIgnoreCase)
                && IsOwnedTask(xml, userSid, command);
        }
        catch { return false; }
    }

    internal static bool IsOwnedTask(string xml, string sid, string exePath)
    {
        try
        {
            TaskNameForUser(sid);
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            var root = XDocument.Parse(xml).Root;
            var principals = root?.Element(ns + "Principals")?.Elements().ToArray();
            var actions = root?.Element(ns + "Actions")?.Elements().ToArray();
            return root?.Name == ns + "Task"
                && principals is { Length: 1 } && principals[0].Name == ns + "Principal"
                && string.Equals(principals[0].Element(ns + "UserId")?.Value, sid, StringComparison.OrdinalIgnoreCase)
                && actions is { Length: 1 } && actions[0].Name == ns + "Exec"
                && string.Equals(actions[0].Element(ns + "Command")?.Value.Trim().Trim('"'), exePath, StringComparison.OrdinalIgnoreCase)
                && actions[0].Element(ns + "Arguments")?.Value.Trim() == "--scheduled-scan";
        }
        catch { return false; }
    }

    internal static bool Matches(string xml, bool enabled, string exePath, string mode, int hour, int weekday, string sid)
    {
        try
        {
            if (!enabled || !IsValidMode(mode) || mode.Equals("Off", StringComparison.OrdinalIgnoreCase)
                || hour is < 0 or > 23 || weekday is < 0 or > 6 || !IsOwnedTask(xml, sid, exePath)) return false;
            XNamespace ns = "http://schemas.microsoft.com/windows/2004/02/mit/task";
            var root = XDocument.Parse(xml).Root!;
            var principal = root.Element(ns + "Principals")!.Elements().Single();
            if (principal.Element(ns + "LogonType")?.Value != "InteractiveToken"
                || principal.Element(ns + "RunLevel")?.Value != "LeastPrivilege") return false;
            var settings = root.Element(ns + "Settings");
            if (settings?.Element(ns + "Enabled")?.Value != "true") return false;
            var triggers = root.Element(ns + "Triggers")?.Elements().ToArray();
            if (triggers is not { Length: 1 } || triggers[0].Name != ns + "CalendarTrigger") return false;
            var trigger = triggers[0];
            if (trigger.Element(ns + "Enabled")?.Value != "true"
                || trigger.Element(ns + "Repetition") != null || trigger.Element(ns + "EndBoundary") != null
                || trigger.Element(ns + "RandomDelay") is { Value: not "PT0S" }) return false;
            if (!DateTime.TryParse(trigger.Element(ns + "StartBoundary")?.Value, CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind, out var start) || start.Kind != DateTimeKind.Unspecified
                || start.TimeOfDay != TimeSpan.FromHours(hour)) return false;
            var schedules = trigger.Elements().Where(e => e.Name.LocalName.StartsWith("ScheduleBy", StringComparison.Ordinal)).ToArray();
            if (schedules.Length != 1) return false;
            var schedule = schedules[0];
            if (mode.Equals("Daily", StringComparison.OrdinalIgnoreCase))
                return schedule.Name == ns + "ScheduleByDay" && schedule.Element(ns + "DaysInterval")?.Value == "1";
            var days = schedule.Element(ns + "DaysOfWeek")?.Elements().ToArray();
            return schedule.Name == ns + "ScheduleByWeek" && schedule.Element(ns + "WeeksInterval")?.Value == "1"
                && days is { Length: 1 } && days[0].Name == ns + ((DayOfWeek)weekday).ToString();
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

internal sealed record ScheduledTaskRead(bool Exists, string? Xml = null, bool Enabled = false, string? Error = null)
{
    public bool IsKnown => Error is null;
    public static ScheduledTaskRead Missing => new(false);
}

/// <summary>Reads tasks through the native scheduler API; explicit changes use schtasks and verify the result.</summary>
public sealed class ScheduledScanService
{
    private static string SchTasks => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "schtasks.exe");
    private readonly Func<string> _userSid;
    private readonly Func<string, CancellationToken, Task<ProcessResult>> _run;
    private readonly Func<string, CancellationToken, Task<ScheduledTaskRead>> _read;
    private readonly Func<string> _definitionPath;
    private readonly Func<string> _exePath;
    private readonly SemaphoreSlim _mutation = new(1, 1);
    private sealed record Change(string Name, ScheduledTaskRead Before, ScheduledTaskRead After);

    public ScheduledScanService()
    {
        _userSid = CurrentUserSid;
        _run = (args, ct) => ProcessRunner.RunCapturedAsync(SchTasks, args, ct, TimeSpan.FromSeconds(30));
        _read = ReadNativeAsync;
        _definitionPath = () => Path.Combine(AppPaths.DataRoot, "scheduled-scan.xml");
        _exePath = () => UpdateService.ExePath;
    }

    internal ScheduledScanService(Func<string> userSid, Func<string, CancellationToken, Task<ProcessResult>> run,
        string definitionPath, string exePath, Func<string, CancellationToken, Task<ScheduledTaskRead>> read)
    {
        _userSid = userSid;
        _run = run;
        _read = read;
        _definitionPath = () => definitionPath;
        _exePath = () => exePath;
    }

    public Task<(bool Ok, string? Error)> ApplyAsync(string mode, int hour, int weekday, CancellationToken ct) =>
        ApplyAndSaveAsync(mode, hour, weekday, () => (true, null), ct);

    public Task<(bool Ok, string? Error)> ApplyAndSaveAsync(string mode, int hour, int weekday,
        Func<(bool Ok, string? Error)> save, CancellationToken ct) =>
        RunSerializedAsync(() => ApplyCoreAsync(mode, hour, weekday, save, ct), ct);

    // Kept for explicit callers; status refresh never invokes migration.
    public Task<(bool Ok, string? Error)> MigrateLegacyAsync(string mode, int hour, int weekday, CancellationToken ct) =>
        RunSerializedAsync(async () =>
        {
            var sid = _userSid();
            var legacy = await ReadCheckedAsync(ScheduledScanTask.LegacyTaskName, ct).ConfigureAwait(false);
            if (!legacy.Exists || !ScheduledScanTask.LegacyTaskBelongsToUser(legacy.Xml!, sid)) return (true, null);
            return await ApplyCoreAsync(mode, hour, weekday, () => (true, null), ct).ConfigureAwait(false);
        }, ct);

    private async Task<(bool Ok, string? Error)> RunSerializedAsync(Func<Task<(bool Ok, string? Error)>> action, CancellationToken ct)
    {
        try
        {
            await _mutation.WaitAsync(ct).ConfigureAwait(false);
            try { return await action().ConfigureAwait(false); }
            finally { _mutation.Release(); }
        }
        catch (Exception ex) { return (false, ex is OperationCanceledException ? "The schedule change was cancelled." : ex.Message); }
    }

    private async Task<(bool Ok, string? Error)> ApplyCoreAsync(string mode, int hour, int weekday,
        Func<(bool Ok, string? Error)> save, CancellationToken ct)
    {
        var changes = new List<Change>();
        try
        {
            if (!ScheduledScanTask.IsValidMode(mode) || hour is < 0 or > 23 || weekday is < 0 or > 6)
                return (false, "Choose a valid scan frequency, hour and weekday.");
            ArgumentNullException.ThrowIfNull(save);
            var sid = _userSid();
            var name = ScheduledScanTask.TaskNameForUser(sid);
            var exe = _exePath();
            var previous = await ReadCheckedAsync(name, ct).ConfigureAwait(false);
            var legacy = await ReadCheckedAsync(ScheduledScanTask.LegacyTaskName, ct).ConfigureAwait(false);
            if (previous.Exists && !ScheduledScanTask.IsOwnedTask(previous.Xml!, sid, exe))
                return (false, "The existing task does not match this Evict installation. It was not changed.");
            var ownedLegacy = legacy.Exists && ScheduledScanTask.LegacyTaskBelongsToUser(legacy.Xml!, sid);
            var off = mode.Equals("Off", StringComparison.OrdinalIgnoreCase);
            var requestedXml = off ? null : ScheduledScanTask.BuildTaskXml(exe, mode, hour, weekday, sid);
            if (!off || previous.Exists)
                await MutateAsync(name, previous, requestedXml, changes, ct).ConfigureAwait(false);
            var current = await ReadCheckedAsync(name, ct).ConfigureAwait(false);
            if (off ? current.Exists : !current.Exists || !ScheduledScanTask.Matches(current.Xml!, current.Enabled, exe, mode, hour, weekday, sid))
                throw new InvalidOperationException("Windows did not confirm the requested schedule.");
            if (ownedLegacy)
                await MutateAsync(ScheduledScanTask.LegacyTaskName, legacy, null, changes, ct).ConfigureAwait(false);
            var legacyAfter = await ReadCheckedAsync(ScheduledScanTask.LegacyTaskName, ct).ConfigureAwait(false);
            if (legacyAfter.Exists && ScheduledScanTask.LegacyTaskBelongsToUser(legacyAfter.Xml!, sid))
                throw new InvalidOperationException("The old schedule is still present.");
            // Recheck after legacy cleanup and before publishing settings.
            var final = await ReadCheckedAsync(name, ct).ConfigureAwait(false);
            if (!Same(current, final)) throw new InvalidOperationException("The task changed before its preferences could be saved.");
            ct.ThrowIfCancellationRequested();
            var saved = save();
            if (!saved.Ok) throw new IOException(saved.Error ?? "The schedule preferences could not be saved.");
            return (true, null);
        }
        catch (Exception ex)
        {
            var recovery = await RollBackAsync(changes).ConfigureAwait(false);
            var reason = ex is OperationCanceledException ? "The schedule change was cancelled." : ex.Message;
            return (false, reason + (recovery is null ? " Previous task settings were preserved." : " Recovery could not be confirmed: " + recovery));
        }
    }

    private async Task MutateAsync(string name, ScheduledTaskRead before, string? xml,
        List<Change> changes, CancellationToken ct)
    {
        if (!Same(before, await ReadCheckedAsync(name, ct).ConfigureAwait(false)))
            throw new InvalidOperationException("The task was changed by another process. No replacement was attempted.");
        ProcessResult? result = null;
        Exception? commandError = null;
        try { result = await WriteTaskAsync(name, xml, ct).ConfigureAwait(false); }
        catch (Exception ex) { commandError = ex; }
        ScheduledTaskRead after;
        try { after = await _read(name, CancellationToken.None).ConfigureAwait(false); }
        catch (Exception ex) { after = new(false, Error: ex.Message); }
        if (!Same(before, after)) changes.Add(new(name, before, after));
        if (!after.IsKnown) throw new InvalidOperationException("The result of the task operation could not be read: " + after.Error, commandError);
        if (commandError != null) throw commandError;
        if (result is null || !result.Success) throw new InvalidOperationException(result is null ? "No task result was returned." : Clean(result));
        if (xml is null && after.Exists) throw new InvalidOperationException("Windows still reports the task after removal.");
    }

    private async Task<string?> RollBackAsync(List<Change> changes)
    {
        var errors = new List<string>();
        foreach (var change in changes.AsEnumerable().Reverse())
        {
            try
            {
                // Do not overwrite a foreign edit or an unknown state after an antivirus/permission block.
                var current = await ReadCheckedAsync(change.Name, CancellationToken.None).ConfigureAwait(false);
                if (Same(current, change.Before)) continue;
                var sid = _userSid();
                if (current.Exists && !(change.Name == ScheduledScanTask.LegacyTaskName
                    ? ScheduledScanTask.LegacyTaskBelongsToUser(current.Xml!, sid)
                    : ScheduledScanTask.IsOwnedTask(current.Xml!, sid, _exePath())))
                    throw new InvalidOperationException("the task no longer belongs to this Evict installation");
                if (!change.After.IsKnown || !Same(current, change.After))
                    throw new InvalidOperationException("task state is unknown or changed after the failed operation");
                var result = await WriteTaskAsync(change.Name, change.Before.Exists ? change.Before.Xml : null, CancellationToken.None).ConfigureAwait(false);
                var restored = await ReadCheckedAsync(change.Name, CancellationToken.None).ConfigureAwait(false);
                if (!result.Success || !Same(restored, change.Before))
                    throw new InvalidOperationException(!result.Success ? Clean(result) : "the previous task definition could not be verified");
            }
            catch (Exception ex) { errors.Add(change.Name + ": " + ex.Message); }
        }
        return errors.Count == 0 ? null : string.Join("; ", errors);
    }

    private async Task<ProcessResult> WriteTaskAsync(string name, string? xml, CancellationToken ct)
    {
        if (xml is null) return await _run($"/Delete /F /TN \"{name}\"", ct).ConfigureAwait(false);
        var path = _definitionPath();
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path, xml, System.Text.Encoding.Unicode);
        try { return await _run($"/Create /F /TN \"{name}\" /XML \"{path}\"", ct).ConfigureAwait(false); }
        finally { try { File.Delete(path); } catch { /* Do not hide the registration result. */ } }
    }

    private async Task<ScheduledTaskRead> ReadCheckedAsync(string name, CancellationToken ct)
    {
        var state = await _read(name, ct).ConfigureAwait(false);
        if (!state.IsKnown) throw new InvalidOperationException("Could not inspect the Windows task: " + state.Error);
        if (state.Exists && string.IsNullOrEmpty(state.Xml)) throw new InvalidOperationException("Windows returned an empty task definition.");
        return state;
    }

    private static bool Same(ScheduledTaskRead left, ScheduledTaskRead right)
    {
        if (!left.IsKnown || !right.IsKnown || left.Exists != right.Exists) return false;
        if (!left.Exists) return true;
        if (left.Enabled != right.Enabled) return false;
        try { return XNode.DeepEquals(XDocument.Parse(left.Xml!), XDocument.Parse(right.Xml!)); }
        catch { return false; }
    }

    public async Task<bool> MatchesAsync(string mode, int hour, int weekday, CancellationToken ct)
    {
        try
        {
            if (!ScheduledScanTask.IsValidMode(mode)) return false;
            var sid = _userSid();
            var current = await ReadCheckedAsync(ScheduledScanTask.TaskNameForUser(sid), ct).ConfigureAwait(false);
            var legacy = await ReadCheckedAsync(ScheduledScanTask.LegacyTaskName, ct).ConfigureAwait(false);
            if (legacy.Exists && ScheduledScanTask.LegacyTaskBelongsToUser(legacy.Xml!, sid)) return false;
            return mode.Equals("Off", StringComparison.OrdinalIgnoreCase)
                ? !current.Exists
                : current.Exists && ScheduledScanTask.Matches(current.Xml!, current.Enabled, _exePath(), mode, hour, weekday, sid);
        }
        catch { return false; }
    }

    public async Task<bool> ExistsAsync(CancellationToken ct)
    {
        try
        {
            var sid = _userSid();
            var current = await ReadCheckedAsync(ScheduledScanTask.TaskNameForUser(sid), ct).ConfigureAwait(false);
            if (current.Exists && ScheduledScanTask.IsOwnedTask(current.Xml!, sid, _exePath())) return true;
            var legacy = await ReadCheckedAsync(ScheduledScanTask.LegacyTaskName, ct).ConfigureAwait(false);
            return legacy.Exists && ScheduledScanTask.LegacyTaskBelongsToUser(legacy.Xml!, sid);
        }
        catch { return false; }
    }

    public static string CurrentUserSid()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return identity.User?.Value ?? throw new InvalidOperationException("The current user's security identifier could not be read.");
    }

    private static Task<ScheduledTaskRead> ReadNativeAsync(string name, CancellationToken ct) => Task.Run(() =>
    {
        object? service = null, folder = null, task = null;
        try
        {
            ct.ThrowIfCancellationRequested();
            var type = Type.GetTypeFromProgID("Schedule.Service") ?? throw new InvalidOperationException("Windows Task Scheduler is unavailable.");
            service = Activator.CreateInstance(type) ?? throw new InvalidOperationException("Windows Task Scheduler could not be opened.");
            ((dynamic)service).Connect();
            folder = ((dynamic)service).GetFolder("\\");
            try { task = ((dynamic)folder).GetTask(name); }
            catch (COMException ex) when (ex.HResult == unchecked((int)0x80070002)) { return ScheduledTaskRead.Missing; }
            return new ScheduledTaskRead(true, (string)((dynamic)task).Xml, (bool)((dynamic)task).Enabled);
        }
        catch (Exception ex) { return new ScheduledTaskRead(false, Error: ex.Message); }
        finally
        {
            foreach (var instance in new[] { task, folder, service })
                if (instance != null && Marshal.IsComObject(instance))
                    try { Marshal.FinalReleaseComObject(instance); } catch { }
        }
    }, ct);

    private static string Clean(ProcessResult result)
    {
        var text = (result.StdErr + "\n" + result.StdOut).Trim();
        return string.IsNullOrWhiteSpace(text) ? $"schtasks exit code {result.ExitCode}" : text;
    }
}
