using System.Diagnostics;
using Evict.Core.Interop;
using Evict.Core.Models;
using Evict.Core.Util;

namespace Evict.Core.Services;

/// <summary>
/// Force Uninstall: for programs whose own uninstaller is missing or broken. Terminates the program's
/// processes, then hands a fingerprint to the leftover scanner so *everything* it owns can be removed.
/// </summary>
public sealed class ForceUninstallService
{
    private readonly LeftoverScanner _scanner = new();

    /// <summary>Processes whose image lives inside the folder (best effort – some system processes cannot be queried).</summary>
    public static List<(int Pid, string Name, string Path)> FindProcessesUnder(string folder)
    {
        var list = new List<(int, string, string)>();
        if (string.IsNullOrEmpty(folder)) return list;
        int self = Environment.ProcessId;
        foreach (var p in Process.GetProcesses())
        {
            try
            {
                if (p.Id == self) continue;
                var path = NativeMethods.GetProcessImagePath(p.Id);
                if (path != null && PathUtil.IsUnder(path, folder)) list.Add((p.Id, p.ProcessName, path));
            }
            catch { /* ignore */ }
            finally { p.Dispose(); }
        }
        return list;
    }

    public static int KillProcessesUnder(string folder, out List<string> errors)
    {
        errors = new List<string>();
        int killed = 0;
        foreach (var (pid, name, _) in FindProcessesUnder(folder))
        {
            try
            {
                using var p = Process.GetProcessById(pid);
                p.Kill(entireProcessTree: true);
                if (p.WaitForExit(5000)) killed++;
                else errors.Add($"{name} ({pid}) did not exit.");
            }
            catch (Exception ex) { errors.Add($"{name} ({pid}): {ex.Message}"); }
        }
        return killed;
    }

    /// <summary>
    /// Scans everything belonging to the program or path. Returns the leftover list for the user to review;
    /// the actual deletion is done through <see cref="LeftoverCleaner"/> like any other cleanup.
    /// </summary>
    public async Task<(ProgramFingerprint Fingerprint, LeftoverScanResult Result)> ScanAsync(InstalledProgram? program, string? path, LeftoverScanOptions options, IProgress<ProgressReport>? progress, CancellationToken ct)
    {
        ProgramFingerprint fp;
        if (program != null) fp = LeftoverScanner.Fingerprint(program);
        else if (!string.IsNullOrWhiteSpace(path)) fp = LeftoverScanner.FingerprintFromPath(path);
        else throw new ArgumentException("Either a program or a path is required.");

        var result = await _scanner.ScanAsync(fp, options, progress, ct).ConfigureAwait(false);

        // For Force Uninstall the registry entry itself must go too (the scanner only reports it when orphaned).
        if (program != null && UninstallRunner.RegistryEntryExists(program))
        {
            var sub = @"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + program.KeyName;
            var display = RegistryPaths.Display(program.Hive, program.View, sub);
            if (!result.Items.Any(i => i.Path.Equals(display, StringComparison.OrdinalIgnoreCase)))
            {
                result.Items.Insert(0, new LeftoverItem
                {
                    Kind = LeftoverKind.RegistryKey, Path = display, Hive = program.Hive, RegView = program.View, SubKey = sub,
                    Confidence = LeftoverConfidence.High, Detail = "Programs & Features entry", ProgramName = program.DisplayName,
                });
            }
        }

        // MSI products: offer msiexec's own forced removal as a hint.
        if (program?.IsMsi == true && !string.IsNullOrEmpty(program.MsiProductCode))
            result.Warnings.Add($"This is a Windows Installer product ({program.MsiProductCode}). If leftovers remain, run: msiexec /x {program.MsiProductCode} /qn");

        return (fp, result);
    }
}
