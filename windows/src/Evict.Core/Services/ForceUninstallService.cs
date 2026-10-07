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

    /// <summary>Only processes in a validated, exclusive scope; shared or protected folders are excluded.</summary>
    public static List<(int Pid, string Name, string Path)> FindProcessesFor(InstalledProgram? program, string? selectedPath) =>
        RunningProgramService.Find(program, selectedPath, includeServices: true).Select(p => (p.Pid, p.Name, p.Path)).ToList();

    public static List<(int Pid, string Name, string Path)> FindProcessesUnder(string folder) => FindProcessesFor(null, folder);

    public static int KillProcessesFor(InstalledProgram? program, string? selectedPath, out List<string> errors)
    {
        var running = RunningProgramService.Find(program, selectedPath, includeServices: true);
        errors = RunningProgramService.ForceClose(running);
        return running.Count - RunningProgramService.StillRunning(running).Count;
    }

    public static int KillProcessesUnder(string folder, out List<string> errors) => KillProcessesFor(null, folder, out errors);

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
