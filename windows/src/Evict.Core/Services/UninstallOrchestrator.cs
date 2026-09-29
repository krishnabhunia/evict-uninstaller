using Evict.Core.Models;

namespace Evict.Core.Services;

public enum JobStatus { Pending, CreatingRestorePoint, Uninstalling, Scanning, Completed, CompletedWithWarnings, Failed, Skipped, Cancelled }

public sealed class UninstallJob
{
    public required InstalledProgram Program { get; init; }
    public ProgramFingerprint? Fingerprint { get; set; }
    public UninstallRunResult? RunResult { get; set; }
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

/// <summary>Runs one uninstall job end to end: capture fingerprint → run uninstaller → scan for leftovers.</summary>
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

    public async Task RunAsync(UninstallJob job, UninstallBatchOptions options, IProgress<ProgressReport>? progress, CancellationToken ct)
    {
        try
        {
            // 1. Fingerprint before the entry disappears.
            job.Fingerprint = LeftoverScanner.Fingerprint(job.Program);

            // 2. Run the program's own uninstaller (or skip for broken entries with no command).
            job.Status = JobStatus.Uninstalling;
            job.Message = "Running the program's uninstaller…";
            if (job.Program.HasUninstaller)
            {
                job.RunResult = await _runner.RunAsync(job.Program, options.Quiet, progress, ct).ConfigureAwait(false);
                if (job.RunResult.Cancelled) { job.Status = JobStatus.Cancelled; job.Message = "Cancelled."; return; }
                if (!job.RunResult.Launched)
                {
                    job.Message = job.RunResult.Error ?? "The uninstaller could not be started.";
                    // Still scan – the user may want to clean up manually.
                }
                else
                {
                    job.Message = job.RunResult.LikelySucceeded
                        ? "Uninstaller finished."
                        : $"Uninstaller exited with code {job.RunResult.ExitCode}." + (job.RunResult.Note is { } n ? " " + n : "");
                }
            }
            else
            {
                job.RunResult = new UninstallRunResult { Note = "No uninstaller registered – only leftovers can be removed." };
                job.Message = "No uninstaller registered.";
            }

            // 3. Powerful scan.
            if (options.ScanLeftovers)
            {
                job.Status = JobStatus.Scanning;
                job.Scan = await _scanner.ScanAsync(job.Fingerprint, options.ScanOptions, progress, ct).ConfigureAwait(false);
                job.Message += $" Found {job.Scan.Items.Count} leftover item(s).";
            }

            bool ok = job.RunResult.LikelySucceeded || !job.Program.HasUninstaller;
            job.Status = ok ? JobStatus.Completed : JobStatus.CompletedWithWarnings;
        }
        catch (OperationCanceledException)
        {
            job.Status = JobStatus.Cancelled;
            job.Message = "Cancelled.";
            throw;
        }
        catch (Exception ex)
        {
            job.Status = JobStatus.Failed;
            job.Message = ex.Message;
            Log.Error($"Uninstall job failed for {job.Program.DisplayName}", ex);
        }
    }
}
