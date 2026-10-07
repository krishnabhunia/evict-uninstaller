using System.Diagnostics;
using Evict.Core.Interop;
using Evict.Core.Models;
using Evict.Core.Util;

namespace Evict.Core.Services;

/// <summary>A process of the program that is about to be uninstalled.</summary>
public sealed record RunningProcess(int Pid, string Name, string Path, string? WindowTitle)
{
    internal InstalledProgram? TargetProgram { get; init; }
    internal string? TargetPath { get; init; }
}

/// <summary>
/// Finds and closes a program's running processes before its uninstaller runs – an uninstaller cannot remove files that
/// are in use, and many either fail or silently leave the program half-removed.
/// </summary>
public static class RunningProgramService
{
    /// <summary>
    /// Processes in the user's session whose executable lies in a folder the program owns. Services (session 0) are
    /// left to the program's own uninstaller, which stops them properly; Evict itself is never listed.
    /// </summary>
    public static List<RunningProcess> Find(InstalledProgram program) => Find(program, null);

    public static List<RunningProcess> Find(InstalledProgram? program, string? selectedPath, bool includeServices = false)
    {
        var scope = RunningProgramRules.CreateScope(program, selectedPath, CurrentInventory());
        var list = new List<RunningProcess>();
        if (scope.Folders.Count == 0 && scope.Executables.Count == 0) return list;
        int self = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == self || !includeServices && p.SessionId == 0) continue;
                var path = NativeMethods.GetProcessImagePath(p.Id);
                if (!scope.Contains(path)) continue;
                string? title = null;
                try { title = string.IsNullOrWhiteSpace(p.MainWindowTitle) ? null : p.MainWindowTitle; } catch { /* exited */ }
                list.Add(new RunningProcess(p.Id, p.ProcessName, path!, title) { TargetProgram = program, TargetPath = selectedPath });
            }
            catch { /* exited or not accessible */ }
            finally { p.Dispose(); }
        }
        return list;
    }

    private static IReadOnlyList<InstalledProgram> CurrentInventory() => new InstalledProgramsService().Enumerate(new ProgramsQueryOptions
        { IncludeSystemComponents = true, IncludeUpdates = true, MeasureMissingSizes = false, ReadUsageData = false, RequireCompleteInventory = true });

    private static List<RunningProcess> ApprovedNow(IEnumerable<RunningProcess> processes)
    {
        var inventory = CurrentInventory();
        return processes.Where(rp => (rp.TargetProgram != null || rp.TargetPath != null)
            && RunningProgramRules.CreateScope(rp.TargetProgram, rp.TargetPath, inventory).Contains(rp.Path)
            && IsRunning(rp)).ToList();
    }

    /// <summary>The processes that are still running (same PID and same executable – PIDs are reused).</summary>
    public static List<RunningProcess> StillRunning(IEnumerable<RunningProcess> processes) =>
        processes.Where(IsRunning).ToList();

    private static bool IsRunning(RunningProcess rp)
    {
        try
        {
            using var p = Process.GetProcessById(rp.Pid);
            if (p.HasExited) return false;
            var path = NativeMethods.GetProcessImagePath(rp.Pid);
            return RunningProgramRules.SameExecutable(path, rp.Path);
        }
        catch { return false; }
    }

    /// <summary>
    /// Asks the processes to close the way clicking × would (the program can still ask to save unsaved work) and waits
    /// up to <paramref name="wait"/>. Returns the processes that are still running.
    /// </summary>
    public static async Task<List<RunningProcess>> CloseGracefullyAsync(IReadOnlyList<RunningProcess> processes, TimeSpan wait, CancellationToken ct)
    {
        foreach (var rp in ApprovedNow(processes))
        {
            if (!IsRunning(rp)) continue;
            int asked = NativeMethods.PostCloseToProcessWindows(rp.Pid);
            Log.Info($"Asked {rp.Name} ({rp.Pid}) to close ({asked} window(s)).");
        }
        var deadline = DateTime.UtcNow + wait;
        var remaining = StillRunning(processes);
        while (remaining.Count > 0 && DateTime.UtcNow < deadline)
        {
            await Task.Delay(500, ct).ConfigureAwait(false);
            remaining = StillRunning(remaining);
        }
        return remaining;
    }

    /// <summary>Ends only approved target processes. Unrelated child processes are never included.</summary>
    public static List<string> ForceClose(IReadOnlyList<RunningProcess> processes)
    {
        var errors = new List<string>();
        var approved = ApprovedNow(processes);
        foreach (var rp in StillRunning(processes).Except(approved))
            errors.Add($"{rp.Name}.exe (PID {rp.Pid}) was left running because exclusive ownership could not be verified.");
        foreach (var rp in approved)
        {
            try
            {
                using var p = Process.GetProcessById(rp.Pid);
                _ = p.Handle; // keep this process identity alive while checking the image, even if its PID is reused
                if (!IsRunning(rp)) continue;
                p.Kill(entireProcessTree: false);
                if (!p.WaitForExit(5000)) errors.Add($"{rp.Name}.exe (PID {rp.Pid}) did not exit.");
                else Log.Info($"Force-closed {rp.Name} ({rp.Pid}).");
            }
            catch (Exception ex)
            {
                errors.Add($"{rp.Name}.exe (PID {rp.Pid}): {ex.Message}" + (ElevationHelper.IsElevated ? "" : " – it may be running as administrator"));
            }
        }
        return errors;
    }
}
