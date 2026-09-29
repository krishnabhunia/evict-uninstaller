using System.ComponentModel;
using System.Diagnostics;
using Evict.Core.Models;
using Evict.Core.Util;
using Microsoft.Win32;

namespace Evict.Core.Services;

/// <summary>
/// Launches a program's own uninstaller and waits until it has finished – including the common
/// pattern where the uninstaller copies itself to %TEMP% (NSIS "Au_.exe") and the original process exits early.
/// </summary>
public sealed class UninstallRunner
{
    public static readonly TimeSpan PostExitGrace = TimeSpan.FromSeconds(90);

    public async Task<UninstallRunResult> RunAsync(InstalledProgram program, bool quiet, IProgress<ProgressReport>? progress, CancellationToken ct)
    {
        var result = new UninstallRunResult();
        var sw = Stopwatch.StartNew();

        var cmd = UninstallCommandParser.Resolve(program, quiet, ReadHead);
        if (cmd is null)
        {
            result.Error = "This entry has no uninstall command. Use Force Uninstall instead.";
            return result;
        }
        result.Command = cmd.Display;

        // Validate the executable exists (msiexec / rundll32 live in System32 and may be given without a path).
        var exe = cmd.FileName;
        if (Path.IsPathRooted(exe) && !File.Exists(exe))
        {
            result.Error = $"The uninstaller was not found: {exe}";
            return result;
        }

        progress?.Report(new ProgressReport($"Running uninstaller: {cmd.Display}"));
        Log.Info($"Uninstall {program.DisplayName}: {cmd.Display}");

        int? exitCode;
        try
        {
            var workDir = SafeDir(exe);
            exitCode = await ProcessRunner.RunShellAndWaitAsync(exe, cmd.Arguments, workDir, ct).ConfigureAwait(false);
            result.Launched = true;
        }
        catch (OperationCanceledException)
        {
            result.Cancelled = true;
            result.Launched = true;
            result.Duration = sw.Elapsed;
            return result;
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            result.Error = "The UAC prompt was cancelled.";
            result.Duration = sw.Elapsed;
            return result;
        }
        catch (Exception ex)
        {
            result.Error = "Could not start the uninstaller: " + ex.Message;
            result.Duration = sw.Elapsed;
            return result;
        }

        result.ExitCode = exitCode;

        // Wait for detached helpers (NSIS Au_.exe, InstallShield setup.exe, MSI child) to finish.
        progress?.Report(new ProgressReport("Waiting for the uninstaller to finish…"));
        result.RegistryEntryRemoved = await WaitForCompletionAsync(program, cmd, sw, ct).ConfigureAwait(false);

        result.Duration = sw.Elapsed;
        if (!result.RegistryEntryRemoved && exitCode is 0)
            result.Note = "The uninstaller finished but the registry entry is still present – the program may need a reboot, or it uses a delayed uninstall.";
        if (exitCode is 3010 or 1641)
            result.Note = "The uninstaller requested a reboot to complete.";
        if (exitCode is 1602)
            result.Note = "The uninstall was cancelled by the user.";
        if (exitCode is 1605)
            result.Note = "Windows Installer reports this product is not installed (already removed).";
        return result;
    }

    private static async Task<bool> WaitForCompletionAsync(InstalledProgram program, UninstallCommand cmd, Stopwatch sw, CancellationToken ct)
    {
        var helperNames = HelperProcessNames(cmd);

        // If the uninstaller ran for a while, its own exit is a strong signal – only wait briefly for
        // detached helpers. A very fast exit usually means it re-launched itself from %TEMP%.
        var deadline = DateTime.UtcNow + (sw.Elapsed > TimeSpan.FromSeconds(8) ? TimeSpan.FromSeconds(6) : PostExitGrace);

        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            if (!RegistryEntryExists(program)) return true;
            if (!AnyHelperRunning(helperNames))
            {
                // Give a 2-second grace for the registry write.
                await Task.Delay(2000, ct).ConfigureAwait(false);
                return !RegistryEntryExists(program);
            }
            await Task.Delay(1000, ct).ConfigureAwait(false);
        }
        return !RegistryEntryExists(program);
    }

    /// <summary>Process names (without .exe) that indicate the uninstall is still in progress.</summary>
    private static List<string> HelperProcessNames(UninstallCommand cmd)
    {
        var names = new List<string> { "Au_", "msiexec", "_iu14D2N", "_uninst" };
        var self = Path.GetFileNameWithoutExtension(cmd.FileName);
        if (!string.IsNullOrEmpty(self) && !self.Equals("msiexec", StringComparison.OrdinalIgnoreCase)
            && !self.Equals("rundll32", StringComparison.OrdinalIgnoreCase) && !self.Equals("cmd", StringComparison.OrdinalIgnoreCase))
            names.Add(self);
        return names;
    }

    private static bool AnyHelperRunning(List<string> names)
    {
        try
        {
            foreach (var p in Process.GetProcesses())
            {
                try
                {
                    var n = p.ProcessName;
                    if (names.Any(h => n.StartsWith(h, StringComparison.OrdinalIgnoreCase)))
                    {
                        // msiexec has a resident service instance; only count instances with a window or recently started.
                        if (n.Equals("msiexec", StringComparison.OrdinalIgnoreCase))
                        {
                            try { if ((DateTime.Now - p.StartTime) > TimeSpan.FromMinutes(30)) continue; } catch { continue; }
                        }
                        return true;
                    }
                }
                catch { /* process exited */ }
                finally { p.Dispose(); }
            }
        }
        catch { /* ignore */ }
        return false;
    }

    public static bool RegistryEntryExists(InstalledProgram program)
    {
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(program.Hive, program.View);
            using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + program.KeyName);
            return key is not null;
        }
        catch { return false; }
    }

    private static string? SafeDir(string exe)
    {
        try { return Path.IsPathRooted(exe) ? Path.GetDirectoryName(exe) : null; } catch { return null; }
    }

    private static byte[]? ReadHead(string path)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
            var buf = new byte[Math.Min(fs.Length, 512 * 1024)];
            int read = fs.Read(buf, 0, buf.Length);
            return read == buf.Length ? buf : buf[..read];
        }
        catch { return null; }
    }

    /// <summary>Removes just the registry entry (for broken / orphaned entries).</summary>
    public static bool DeleteRegistryEntry(InstalledProgram program, out string? error)
    {
        error = null;
        try
        {
            using var baseKey = RegistryKey.OpenBaseKey(program.Hive, program.View);
            using var uninstall = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall", writable: true);
            if (uninstall is null) { error = "Uninstall key not accessible."; return false; }
            uninstall.DeleteSubKeyTree(program.KeyName, throwOnMissingSubKey: false);
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }
}
