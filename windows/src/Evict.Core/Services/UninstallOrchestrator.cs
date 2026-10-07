using Evict.Core.Models;
using Evict.Core.Util;

namespace Evict.Core.Services;

public enum JobStatus { Pending, CreatingRestorePoint, Uninstalling, Scanning, Completed, CompletedWithWarnings, Failed, Skipped, Cancelled }

public sealed class UninstallJob
{
    public required InstalledProgram Program { get; init; }
    public ProgramFingerprint? Fingerprint { get; set; }
    public UninstallRunResult? RunResult { get; set; }
    /// <summary>How the last uninstaller run ended (null: none ran – no uninstaller registered, or skipped before).</summary>
    public UninstallOutcome? Outcome { get; set; }
    /// <summary>The command of the last run (used for "Try again").</summary>
    public UninstallCommand? LastCommand { get; set; }
    /// <summary>Every command that was tried, in order (shown in the log).</summary>
    public List<string> Attempts { get; } = new();
    /// <summary>The user chose Force uninstall after the uninstaller failed: leftovers are scanned although the program is still installed.</summary>
    public bool ForceRemoval { get; set; }
    /// <summary>True only after reviewed cleanup removed items and the installation was verified absent.</summary>
    public bool ForceRemovalVerified { get; set; }
    public LeftoverScanResult? Scan { get; set; }
    public JobStatus Status { get; set; } = JobStatus.Pending;
    public string Message { get; set; } = "";
}

public sealed class UninstallBatchOptions
{
    public bool CreateRestorePoint { get; set; } = true;
    public bool Quiet { get; set; }
    public bool ScanLeftovers { get; set; } = true;
    public LeftoverScanOptions ScanOptions { get; set; } = new();
}

/// <summary>
/// The steps of one uninstall job: capture the fingerprint → run the uninstaller (again, or another way, when it failed)
/// → scan for leftovers. The wizard decides between the steps what happens after a failure.
/// </summary>
public sealed class UninstallOrchestrator
{
    private readonly UninstallRunner _runner = new();
    private readonly LeftoverScanner _scanner = new();
    private readonly RestorePointService _restore = new();

    public Task<RestorePointResult> CreateRestorePointAsync(IEnumerable<string> programNames, CancellationToken ct)
    {
        var names = programNames.Take(3).ToList();
        var desc = $"Evict: uninstall {string.Join(", ", names)}";
        return _restore.CreateAsync(desc, ct);
    }

    /// <summary>
    /// Runs the uninstaller – <paramref name="command"/> when given (retry / alternative), otherwise the best command for
    /// the program – and records the outcome. Throws <see cref="OperationCanceledException"/> when Evict's Cancel was used.
    /// </summary>
    public async Task<UninstallOutcome> RunUninstallerAsync(UninstallJob job, UninstallCommand? command, bool quiet, IProgress<ProgressReport>? progress, CancellationToken ct)
    {
        // The fingerprint must be taken before the first run removes the registry entry it is built from.
        job.Fingerprint ??= LeftoverScanner.Fingerprint(job.Program);
        job.Status = JobStatus.Uninstalling;
        job.Message = "Running the program's uninstaller…";

        command ??= UninstallCommandParser.Resolve(job.Program, quiet, UninstallRunner.ReadHeadPublic);
        UninstallRunResult result;
        if (command is null)
            result = new UninstallRunResult { Error = "This entry has no uninstall command." };
        else
        {
            job.LastCommand = command;
            job.Attempts.Add(command.Display);
            result = await _runner.RunAsync(job.Program, command, progress, ct).ConfigureAwait(false);
        }
        job.RunResult = result;
        if (result.Cancelled) throw new OperationCanceledException(ct);

        var outcome = UninstallRecoveryRules.Evaluate(result, stillInstalled: UninstallRunner.StillInstalled(job.Program));
        job.Outcome = outcome;
        job.Message = outcome switch
        {
            UninstallOutcome.Succeeded => "Uninstaller finished." + (result.Note is { } n && result.ExitCode is 1605 ? " " + n : ""),
            UninstallOutcome.RebootRequired => "Uninstaller finished – a restart is needed to complete it.",
            _ => char.ToUpperInvariant(UninstallRecoveryRules.Describe(outcome, result, job.Program.DisplayName)[0])
                 + UninstallRecoveryRules.Describe(outcome, result, job.Program.DisplayName)[1..] + ".",
        };
        Log.Info($"Uninstall {job.Program.DisplayName}: {outcome} (exit {result.ExitCode?.ToString() ?? "–"}, entry removed: {result.RegistryEntryRemoved})");
        return outcome;
    }

    /// <summary>Powerful Scan for the job's leftovers.</summary>
    public async Task ScanAsync(UninstallJob job, UninstallBatchOptions options, IProgress<ProgressReport>? progress, CancellationToken ct)
    {
        job.Fingerprint ??= LeftoverScanner.Fingerprint(job.Program);
        job.Status = JobStatus.Scanning;
        job.Scan = await _scanner.ScanAsync(job.Fingerprint, options.ScanOptions, progress, ct).ConfigureAwait(false);
        job.Message += $" Found {job.Scan.Items.Count} leftover item(s).";
    }

    /// <summary>Final status: uninstalled cleanly, or with warnings (forced, no uninstaller, reboot pending).</summary>
    public static void Complete(UninstallJob job)
    {
        bool clean = job.Outcome is UninstallOutcome.Succeeded && !job.ForceRemoval;
        job.Status = clean || (job.ForceRemoval && job.ForceRemovalVerified) ? JobStatus.Completed : JobStatus.CompletedWithWarnings;
    }

    public static bool CanAutoClean(UninstallJob job) =>
        job.Program.HasUninstaller && !job.ForceRemoval && job.Outcome == UninstallOutcome.Succeeded;

    /// <summary>Conservative completion check after force cleanup. Unreadable or unknown installation state is not success.</summary>
    public static bool VerifyForcedRemoval(UninstallJob job, bool cleanupChanged)
    {
        if (!job.ForceRemoval || !cleanupChanged) return false;
        try
        {
            using var baseKey = Microsoft.Win32.RegistryKey.OpenBaseKey(job.Program.Hive, job.Program.View);
            using var key = baseKey.OpenSubKey(@"SOFTWARE\Microsoft\Windows\CurrentVersion\Uninstall\" + job.Program.KeyName);
            if (key != null) return false;
            var folder = job.Fingerprint?.InstallLocation ?? job.Program.InstallLocation;
            var exe = job.Fingerprint?.PrimaryExecutable ?? job.Program.PrimaryExecutable;
            if (string.IsNullOrWhiteSpace(folder) && string.IsNullOrWhiteSpace(exe) && !job.Program.IsBrokenEntry) return false;
            if (!string.IsNullOrWhiteSpace(folder) && !ConfirmedMissing(folder)) return false;
            if (!string.IsNullOrWhiteSpace(exe) && !ConfirmedMissing(exe)) return false;
            return true;
        }
        catch { return false; }
    }

    private static bool ConfirmedMissing(string path)
    {
        try { File.GetAttributes(Path.GetFullPath(path.Trim().Trim('"'))); return false; }
        catch (FileNotFoundException) { return true; }
        catch (DirectoryNotFoundException) { return true; }
        catch { return false; }
    }
}
