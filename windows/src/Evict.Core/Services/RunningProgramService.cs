using System.Diagnostics;
using Evict.Core.Interop;
using Evict.Core.Models;
using Evict.Core.Util;

namespace Evict.Core.Services;

/// <summary>A process of the program that is about to be uninstalled.</summary>
public sealed record RunningProcess(int Pid, string Name, string Path, string? WindowTitle);

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
    public static List<RunningProcess> Find(InstalledProgram program)
    {
        var folders = RunningProgramRules.OwnedFolders(program);
        var list = new List<RunningProcess>();
        if (folders.Count == 0) return list;
        int self = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == self || p.SessionId == 0) continue;
                var path = NativeMethods.GetProcessImagePath(p.Id);
                if (!RunningProgramRules.BelongsTo(path, folders)) continue;
                string? title = null;
                try { title = string.IsNullOrWhiteSpace(p.MainWindowTitle) ? null : p.MainWindowTitle; } catch { /* exited */ }
                list.Add(new RunningProcess(p.Id, p.ProcessName, path!, title));
            }
            catch { /* exited or not accessible */ }
            finally { p.Dispose(); }
        }
        return list;
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
            return path == null || string.Equals(path, rp.Path, StringComparison.OrdinalIgnoreCase);
        }
        catch { return false; }
    }

    /// <summary>
    /// Asks the processes to close the way clicking × would (the program can still ask to save unsaved work) and waits
    /// up to <paramref name="wait"/>. Returns the processes that are still running.
    /// </summary>
    public static async Task<List<RunningProcess>> CloseGracefullyAsync(IReadOnlyList<RunningProcess> processes, TimeSpan wait, CancellationToken ct)
    {
        foreach (var rp in processes)
        {
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

    /// <summary>Ends the processes (and their child processes). Returns one line per process that could not be ended.</summary>
    public static List<string> ForceClose(IReadOnlyList<RunningProcess> processes)
    {
        var errors = new List<string>();
        foreach (var rp in StillRunning(processes))
        {
            try
            {
                using var p = Process.GetProcessById(rp.Pid);
                p.Kill(entireProcessTree: true);
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
