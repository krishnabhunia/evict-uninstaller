using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Evict.App.Services;
using Evict.Core.Models;
using Evict.Core.Services;
using Evict.Core.Util;

namespace Evict.App.ViewModels;

/// <summary>Review = leftover files, folders, services…; RegistryReview = leftover registry entries (own step, backed up before removal).</summary>
public enum WizardStep { Confirm, Running, Review, RegistryReview, Cleaning, Done }

public sealed partial class UninstallJobViewModel : ObservableObject
{
    public UninstallJobViewModel(UninstallJob job) => Job = job;
    public UninstallJob Job { get; }
    public string Name => Job.Program.DisplayName;
    public string Publisher => Job.Program.Publisher ?? "";
    public string Version => Job.Program.DisplayVersion ?? "";
    public string SizeText => SizeFormatter.Format(Job.Program.SizeBytes);

    [ObservableProperty] private JobStatus _status;
    [ObservableProperty] private string _message = "Waiting…";

    public string StatusGlyph => Status switch
    {
        JobStatus.Pending => "",
        JobStatus.CreatingRestorePoint or JobStatus.Uninstalling or JobStatus.Scanning => "",
        JobStatus.Completed => "",
        JobStatus.CompletedWithWarnings => "",
        JobStatus.Failed => "",
        JobStatus.Cancelled or JobStatus.Skipped => "",
        _ => "",
    };
    public bool IsRunning => Status is JobStatus.CreatingRestorePoint or JobStatus.Uninstalling or JobStatus.Scanning;
    public bool IsOk => Status == JobStatus.Completed;
    public bool IsWarn => Status == JobStatus.CompletedWithWarnings;
    public bool IsFail => Status is JobStatus.Failed or JobStatus.Cancelled;

    public void Sync()
    {
        Status = Job.Status;
        Message = Job.Message;
        OnPropertyChanged(nameof(StatusGlyph));
        OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(IsOk));
        OnPropertyChanged(nameof(IsWarn));
        OnPropertyChanged(nameof(IsFail));
    }
}

public sealed partial class UninstallWizardViewModel : ObservableObject
{
    private readonly AppServices _services;
    private CancellationTokenSource? _cts;

    public UninstallWizardViewModel(AppServices services, IReadOnlyList<InstalledProgram> programs)
    {
        _services = services;
        Jobs = new ObservableCollection<UninstallJobViewModel>(programs.Select(p => new UninstallJobViewModel(new UninstallJob { Program = p })));
        var s = services.Settings.Current;
        _createRestorePoint = s.CreateRestorePoint && ElevationHelper.IsElevated;
        _quietMode = s.QuietUninstall;
        _autoClean = s.AutoCleanLeftovers;
        _sendToRecycleBin = s.SendToRecycleBin;
        _scanLeftovers = true;
        Review = new LeftoverReviewViewModel();
        RegistryReview = new LeftoverReviewViewModel();
        _ = DetectRunningAsync(programs);
    }

    private async Task DetectRunningAsync(IReadOnlyList<InstalledProgram> programs)
    {
        try
        {
            var names = await Task.Run(() => programs.Where(p => RunningProgramService.Find(p).Count > 0).Select(p => p.DisplayName).ToList());
            if (names.Count == 0) return;
            RunningNowText = "Running now: " + string.Join(", ", names) + (_services.Settings.Current.RunningProgramAction switch
            {
                "Close" => " – Evict closes it before uninstalling (Settings → Uninstalling).",
                "Ignore" => " – it stays open, so its uninstaller may fail or leave files behind (Settings → Uninstalling).",
                _ => " – Evict asks you to close it before uninstalling. Save your work first.",
            });
        }
        catch (Exception ex) { Core.Services.Log.Warn("Running-program check failed: " + ex.Message); }
    }

    public ObservableCollection<UninstallJobViewModel> Jobs { get; }
    public LeftoverReviewViewModel Review { get; }
    public LeftoverReviewViewModel RegistryReview { get; }
    public ObservableCollection<string> LogLines { get; } = new();

    [ObservableProperty] private WizardStep _step = WizardStep.Confirm;
    [ObservableProperty] private bool _createRestorePoint;
    [ObservableProperty] private bool _quietMode;
    [ObservableProperty] private bool _autoClean;
    [ObservableProperty] private bool _sendToRecycleBin;
    [ObservableProperty] private bool _scanLeftovers;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _progressIndeterminate = true;
    [ObservableProperty] private string _restorePointMessage = "";
    [ObservableProperty] private bool _canCancel;
    /// <summary>A System Restore point was created before this run – "Open System Restore" can undo the uninstallers' changes.</summary>
    [ObservableProperty] private bool _restorePointCreated;
    /// <summary>Leftovers were removed in this run – "Undo leftover removal" puts recycled files and backed-up registry entries back.</summary>
    [ObservableProperty] private bool _canUndoCleanup;
    /// <summary>"Running now: Notepad++ – Evict asks to close it first." on the first page (empty when nothing runs).</summary>
    [ObservableProperty] private string _runningNowText = "";
    private readonly CleanupUndo _undo = new();
    private readonly UninstallCleanupLedger _cleanupLedger = new();
    private readonly HashSet<LeftoverItem> _cleanupFailures = new();
    private readonly Dictionary<UninstallJob, (Guid Id, DateTime Timestamp)> _historyEntries = new();

    // Done-step summary
    [ObservableProperty] private int _programsUninstalled;
    [ObservableProperty] private int _programsWithWarnings;
    [ObservableProperty] private int _leftoversRemoved;
    [ObservableProperty] private int _leftoversFailed;
    /// <summary>"Registry: 7 of 8 keys/values removed and verified gone · 1 needs administrator rights" (null when no registry items).</summary>
    [ObservableProperty] private string? _registrySummary;
    /// <summary>.reg file with every registry entry this run removed (enables "Undo registry changes").</summary>
    [ObservableProperty] private string? _registryBackupFile;
    [ObservableProperty] private long _bytesReclaimed;
    [ObservableProperty] private long _bytesRemoved;
    [ObservableProperty] private bool _rebootRecommended;
    public ObservableCollection<string> Errors { get; } = new();

    public bool AnythingChanged { get; private set; }
    public bool IsElevated => ElevationHelper.IsElevated;
    public bool IsSingle => Jobs.Count == 1;
    public string Title => IsSingle ? $"Uninstall {Jobs[0].Name}" : $"Uninstall {Jobs.Count} programs";
    public string Subtitle => IsSingle
        ? $"{Jobs[0].Publisher}{(Jobs[0].Version.Length > 0 ? " · " + Jobs[0].Version : "")}{(Jobs[0].Job.Program.SizeBytes is { } ? " · " + Jobs[0].SizeText : "")}"
        : $"{Jobs.Count} programs · {SizeFormatter.Format(Jobs.Sum(j => j.Job.Program.SizeBytes ?? 0))} on disk";
    public string RestorePointHint => IsElevated
        ? "Creates a System Restore point before anything is removed (Windows may skip it if one was created in the last 24 hours)."
        : "Requires administrator rights – restart Evict as administrator to enable.";
    public string BytesReclaimedText => SizeFormatter.Format(BytesReclaimed);
    public string CleanupSpaceText => $"{SizeFormatter.Format(BytesRemoved)} removed from original locations; {BytesReclaimedText} permanently deleted. Recycled data still occupies disk space.";

    public event Action? RequestClose;

    public string LogText => string.Join(Environment.NewLine, LogLines);

    private void Log(string line)
    {
        LogLines.Add($"[{DateTime.Now:HH:mm:ss}] {line}");
        OnPropertyChanged(nameof(LogText));
        Core.Services.Log.Info("Wizard: " + line);
    }

    [RelayCommand]
    private async Task StartAsync()
    {
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        Step = WizardStep.Running;
        CanCancel = true;
        ProgressIndeterminate = true;

        var progress = new Progress<ProgressReport>(r =>
        {
            StatusText = r.Message;
            if (r.Percent is { } p) { ProgressIndeterminate = false; Progress = p; }
        });

        var options = new UninstallBatchOptions
        {
            CreateRestorePoint = CreateRestorePoint,
            Quiet = QuietMode,
            ScanLeftovers = ScanLeftovers,
            ScanOptions = new LeftoverScanOptions { ScanAllUserProfiles = _services.Settings.Current.ScanAllUserProfiles },
        };

        try
        {
            if (CreateRestorePoint)
            {
                StatusText = "Creating a System Restore point…";
                foreach (var j in Jobs) { j.Job.Status = JobStatus.CreatingRestorePoint; j.Sync(); }
                var rp = await _services.Orchestrator.CreateRestorePointAsync(Jobs.Select(j => j.Name), ct);
                RestorePointMessage = rp.Message;
                RestorePointCreated = rp.Succeeded;
                Log("Restore point: " + rp.Message);
                foreach (var j in Jobs) { j.Job.Status = JobStatus.Pending; j.Sync(); }
                if (!rp.Succeeded && !ConfirmWithoutRestorePoint(rp.Message))
                {
                    Log("Stopped before anything was changed: no restore point.");
                    Step = WizardStep.Confirm;
                    CanCancel = false;
                    StatusText = "";
                    return;
                }
            }

            int index = 0;
            foreach (var jvm in Jobs)
            {
                ct.ThrowIfCancellationRequested();
                if (jvm.Job.Status != JobStatus.Pending) continue; // "Stop here" skipped the rest
                index++;
                ProgressIndeterminate = true;
                StatusText = $"Uninstalling {jvm.Name} ({index}/{Jobs.Count})…";
                Log($"Starting {jvm.Name}");
                jvm.Job.Status = JobStatus.Uninstalling; jvm.Sync();

                using var timer = new Timer(_ => System.Windows.Application.Current?.Dispatcher.BeginInvoke(jvm.Sync), null, 500, 500);
                bool proceed = await RunJobAsync(jvm, options, progress, ct);
                jvm.Sync();
                Log($"{jvm.Name}: {jvm.Job.Message}");
                foreach (var cmd in jvm.Job.Attempts) Log("  command: " + cmd);
                AnythingChanged = true;
                if (!proceed) StopRemaining(jvm);
            }

            var leftovers = Jobs.Where(j => j.Job.Scan != null).SelectMany(j => j.Job.Scan!.Items).ToList();
            var warnings = Jobs.Where(j => j.Job.Scan != null).SelectMany(j => j.Job.Scan!.Warnings).Distinct().ToList();
            foreach (var w in warnings) Log("  note: " + w);

            CanCancel = false;
            if (leftovers.Count == 0)
            {
                Log("No leftovers found.");
                FinishWithoutCleanup();
                return;
            }

            Review.Load(leftovers.Where(i => !i.IsRegistry), selectLowConfidence: false);
            RegistryReview.Load(leftovers.Where(i => i.IsRegistry), selectLowConfidence: false);
            OnPropertyChanged(nameof(HasFileLeftovers));
            OnPropertyChanged(nameof(HasRegistryLeftovers));
            OnPropertyChanged(nameof(NextToRegistryText));
            // A forced removal deletes a program that is still installed: always let the user see the list first.
            if (AutoClean && Jobs.All(j => j.Job.Scan is null || UninstallOrchestrator.CanAutoClean(j.Job)))
            {
                await CleanAsync(Review.Items.Concat(RegistryReview.Items).Where(i => i.IsHigh).Select(i => i.Item).ToList());
            }
            else
            {
                if (AutoClean) Log("Automatic removal is off for this run: a program was force-uninstalled – review the list.");
                Step = Review.HasItems ? WizardStep.Review : WizardStep.RegistryReview;
            }
        }
        catch (OperationCanceledException)
        {
            Log("Cancelled by user.");
            foreach (var j in Jobs.Where(j => j.Job.Status is JobStatus.Pending or JobStatus.Uninstalling or JobStatus.Scanning))
            {
                j.Job.Status = j.Job.Status == JobStatus.Pending ? JobStatus.Skipped : JobStatus.Cancelled;
                j.Job.Message = j.Job.Status == JobStatus.Skipped ? "Skipped" : "Cancelled.";
                j.Sync();
            }
            FinishWithoutCleanup();
        }
        catch (Exception ex)
        {
            Log("Error: " + ex.Message);
            Core.Services.Log.Error("Uninstall wizard failed", ex);
            Errors.Add(ex.Message);
            foreach (var j in Jobs.Where(j => j.Job.Status is JobStatus.Uninstalling or JobStatus.Scanning)) { j.Job.Status = JobStatus.Failed; j.Job.Message = ex.Message; j.Sync(); }
            FinishWithoutCleanup();
            OfferRollback("Something went wrong during the uninstall: " + ex.Message);
        }
    }

    // ───────────────────────────── one program ─────────────────────────────

    /// <summary>
    /// Close the program if it runs → run its uninstaller → on failure ask: try again, another way, force uninstall or
    /// skip → scan for leftovers. Returns false when the user chose to stop the remaining programs.
    /// </summary>
    private async Task<bool> RunJobAsync(UninstallJobViewModel jvm, UninstallBatchOptions options, IProgress<ProgressReport> progress, CancellationToken ct)
    {
        var job = jvm.Job;
        if (!await CloseRunningProgramAsync(jvm, ct))
        {
            Skip(jvm, "Skipped – the program is still running.");
            return AskContinue(jvm);
        }

        if (job.Program.HasUninstaller)
        {
            UninstallCommand? command = null;
            int busyRetries = 0;
            while (true)
            {
                var outcome = await _services.Orchestrator.RunUninstallerAsync(job, command, options.Quiet, progress, ct);
                jvm.Sync();
                if (UninstallRecoveryRules.IsSuccess(outcome)) break;

                if (outcome == UninstallOutcome.AnotherInstallInProgress && busyRetries < 3)
                {
                    busyRetries++;
                    Log($"{jvm.Name}: Windows Installer is busy with another installation – retrying in 15 s ({busyRetries}/3).");
                    StatusText = $"Windows Installer is busy – retrying {jvm.Name} in 15 seconds ({busyRetries}/3)…";
                    await Task.Delay(TimeSpan.FromSeconds(15), ct);
                    command = job.LastCommand;
                    continue;
                }

                Log($"{jvm.Name}: {job.Message}");
            AskAgain:
                var alternatives = UninstallRecoveryRules.Alternatives(job.Program, job.LastCommand, UninstallRunner.ReadHeadPublic).Take(2).ToList();
                var choices = new List<string>();
                // Some uninstallers finish in the background (a helper removes the entry or files a little later).
                bool canRecheck = outcome is UninstallOutcome.StillInstalled or UninstallOutcome.Failed;
                if (canRecheck) choices.Add("Check again – the uninstaller may still be finishing");
                bool canRetry = job.LastCommand != null;
                if (canRetry) choices.Add("Try again");
                choices.AddRange(alternatives.Select(a => a.Label));
                choices.Add("Force uninstall – find its files, folders and registry entries and remove them (you review the list first)");
                choices.Add("Skip this program – keep it installed");
                var reason = UninstallRecoveryRules.Describe(outcome, job.RunResult!, jvm.Name);
                int pick = Dialogs.Choose($"{jvm.Name} was not uninstalled",
                    $"{char.ToUpperInvariant(reason[0])}{reason[1..]}.\n\nNothing else has been changed for this program yet. What should Evict do?",
                    choices, cancelIndex: choices.Count - 1);

                int i = pick;
                if (canRecheck && i == 0)
                {
                    job.RunResult!.RegistryEntryRemoved = !UninstallRunner.RegistryEntryExists(job.Program);
                    outcome = UninstallRecoveryRules.Evaluate(job.RunResult, UninstallRunner.StillInstalled(job.Program));
                    job.Outcome = outcome;
                    Log($"{jvm.Name}: checked again – {outcome}.");
                    if (UninstallRecoveryRules.IsSuccess(outcome)) { job.Message = "Uninstaller finished."; break; }
                    goto AskAgain;
                }
                if (canRecheck) i--;
                if (canRetry && i == 0)
                {
                    command = job.LastCommand;
                    Log($"{jvm.Name}: trying again.");
                    if (!await CloseRunningProgramAsync(jvm, ct)) { Skip(jvm, "Skipped – the program is still running."); return AskContinue(jvm); }
                    continue;
                }
                if (canRetry) i--;
                if (i < alternatives.Count) { command = alternatives[i].Command; Log($"{jvm.Name}: trying another way – {alternatives[i].Label}."); continue; }
                i -= alternatives.Count;
                if (i == 0)
                {
                    job.ForceRemoval = true;
                    job.Message = "Force uninstall: the uninstaller did not work – leftovers are removed instead.";
                    Log($"{jvm.Name}: force uninstall chosen.");
                    break;
                }
                Skip(jvm, "Skipped – " + reason + ". Nothing was removed.");
                return AskContinue(jvm);
            }
        }
        else
        {
            int choice = Dialogs.Choose($"{jvm.Name} has no uninstaller",
                "Evict cannot uninstall this program normally. Force uninstall scans its files and registry entries for you to review before removing anything. This requires a scan even when Powerful Scan was turned off.",
                new[] { "Force uninstall - scan and review its items", "Skip this program - change nothing" }, cancelIndex: 1);
            if (choice != 0)
            {
                Skip(jvm, "Skipped - no uninstaller is registered.");
                return AskContinue(jvm);
            }
            job.ForceRemoval = true;
            job.Fingerprint ??= LeftoverScanner.Fingerprint(job.Program);
            job.RunResult = new UninstallRunResult { Note = "No uninstaller registered – only leftovers can be removed." };
            job.Message = "No uninstaller registered.";
        }

        if (options.ScanLeftovers || job.ForceRemoval)
        {
            // A force uninstall removes files of a program that may still run: close it once more.
            if (job.ForceRemoval && !await CloseRunningProgramAsync(jvm, ct))
            {
                Skip(jvm, "Skipped - the program is still running. No force removal was performed.");
                return AskContinue(jvm);
            }
            await _services.Orchestrator.ScanAsync(job, options, progress, ct);
        }
        UninstallOrchestrator.Complete(job);
        return true;
    }

    private void Skip(UninstallJobViewModel jvm, string message)
    {
        jvm.Job.Status = JobStatus.Skipped;
        jvm.Job.Message = message;
        jvm.Sync();
    }

    /// <summary>After a skipped program in a batch: carry on with the others, or stop here.</summary>
    private bool AskContinue(UninstallJobViewModel current)
    {
        int remaining = Jobs.Count(j => j != current && j.Job.Status == JobStatus.Pending);
        if (remaining == 0) return true;
        return Dialogs.Choose($"{current.Name} was skipped",
            $"{remaining} more program(s) are waiting to be uninstalled.",
            new[] { $"Continue with the remaining {remaining} program(s)", "Stop here" }, cancelIndex: 1) == 0;
    }

    private void StopRemaining(UninstallJobViewModel current)
    {
        foreach (var j in Jobs.Where(j => j != current && j.Job.Status == JobStatus.Pending))
        {
            j.Job.Status = JobStatus.Skipped;
            j.Job.Message = "Not started – you chose to stop.";
            j.Sync();
        }
        Log("Remaining programs were not started.");
    }

    private bool ConfirmWithoutRestorePoint(string message) =>
        Dialogs.Choose("The System Restore point was not created",
            message + "\n\nWithout it, Windows cannot undo what the uninstaller changes. Leftover files still go to the Recycle Bin and registry entries are still backed up.",
            new[] { "Continue without a restore point", "Cancel – change nothing" }, cancelIndex: 1) == 0;

    // ───────────────────────────── running program ─────────────────────────────

    /// <summary>
    /// The program's processes must end before its uninstaller runs. Depending on Settings: ask (default), close
    /// automatically, or leave running. Returns false when the user chose to skip the program.
    /// </summary>
    private async Task<bool> CloseRunningProgramAsync(UninstallJobViewModel jvm, CancellationToken ct)
    {
        var mode = _services.Settings.Current.RunningProgramAction;
        if (mode == "Ignore") return true;
        var running = await Task.Run(() => RunningProgramService.Find(jvm.Job.Program), ct);
        bool justWait = false;
        while (running.Count > 0)
        {
            var list = string.Join("\n", RunningProgramRules.Summarize(running.Select(r => (r.Name, r.WindowTitle))).Select(l => "  •  " + l));
            Log($"{jvm.Name} is running: " + string.Join(", ", running.Select(r => $"{r.Name} ({r.Pid})")));

            if (mode != "Close" && !justWait)
            {
                int pick = Dialogs.Choose($"{jvm.Name} is running",
                    $"{list}\n\nIts uninstaller cannot remove files that are in use. Evict asks it to close first – save your work if it asks.",
                    new[] { "Close it and uninstall", "I closed it myself – check again", "Uninstall anyway (it may fail or leave files behind)", "Skip this program" },
                    cancelIndex: 3);
                if (pick == 1) { running = await Task.Run(() => RunningProgramService.StillRunning(running), ct); if (running.Count == 0) running = await Task.Run(() => RunningProgramService.Find(jvm.Job.Program), ct); continue; }
                if (pick == 2) { Log($"{jvm.Name}: uninstalling while it runs."); return true; }
                if (pick == 3) return false;
            }

            justWait = false;
            StatusText = $"Closing {jvm.Name}…";
            var left = await RunningProgramService.CloseGracefullyAsync(running, TimeSpan.FromSeconds(10), ct);
            if (left.Count == 0) { Log($"{jvm.Name} closed."); return true; }

            if (mode != "Close")
            {
                var leftList = string.Join("\n", RunningProgramRules.Summarize(left.Select(r => (r.Name, r.WindowTitle))).Select(l => "  •  " + l));
                int pick = Dialogs.Choose($"{jvm.Name} did not close",
                    $"{leftList}\n\nIt may be waiting for you to save your work, or it only minimizes to the notification area.",
                    new[] { "Force close – unsaved work in it is lost", "Wait 10 more seconds", "Uninstall anyway", "Skip this program" },
                    cancelIndex: 3);
                if (pick == 1) { running = left; justWait = true; continue; }
                if (pick == 2) { Log($"{jvm.Name}: uninstalling while it runs."); return true; }
                if (pick == 3) return false;
            }

            StatusText = $"Force-closing {jvm.Name}…";
            var errors = await Task.Run(() => RunningProgramService.ForceClose(left), ct);
            if (errors.Count == 0) { Log($"{jvm.Name} force-closed."); return true; }
            foreach (var e in errors) Log("  could not close " + e);
            if (mode == "Close") { Log($"{jvm.Name}: uninstalling while it runs."); return true; }
            int last = Dialogs.Choose($"{jvm.Name} could not be closed",
                string.Join("\n", errors.Select(e => "  •  " + e)) + (IsElevated ? "" : "\n\nRestarting Evict as administrator may help."),
                new[] { "Uninstall anyway", "Skip this program" }, cancelIndex: 1);
            return last == 0;
        }
        return true;
    }

    public bool HasFileLeftovers => Review.HasItems;
    public bool HasRegistryLeftovers => RegistryReview.HasItems;
    public string NextToRegistryText => $"Next: registry ({RegistryReview.TotalCount})";

    /// <summary>Files step → registry step (nothing is removed yet; both selections are applied together).</summary>
    [RelayCommand]
    private void NextToRegistry()
    {
        if (RegistryReview.HasItems) Step = WizardStep.RegistryReview;
    }

    [RelayCommand]
    private void BackToFiles()
    {
        if (Review.HasItems) Step = WizardStep.Review;
    }

    [RelayCommand]
    private async Task RemoveSelectedLeftoversAsync() => await RemoveAsync(includeRegistry: true);

    /// <summary>Registry step: remove only the selected files/folders and leave the registry as it is.</summary>
    [RelayCommand]
    private async Task SkipRegistryAsync() => await RemoveAsync(includeRegistry: false);

    private async Task RemoveAsync(bool includeRegistry)
    {
        var files = Review.SelectedLeftovers;
        var registry = includeRegistry ? RegistryReview.SelectedLeftovers : Array.Empty<LeftoverItem>();
        var selected = files.Concat(registry).ToList();
        if (selected.Count == 0) { FinishWithoutCleanup(); return; }
        var parts = new List<string>();
        if (files.Count > 0) parts.Add(SendToRecycleBin ? $"{files.Count} file/folder item(s) go to the Recycle Bin." : $"{files.Count} file/folder item(s) are deleted permanently (not sent to the Recycle Bin).");
        if (registry.Count > 0) parts.Add($"{registry.Count} registry entr{(registry.Count == 1 ? "y is" : "ies are")} backed up to a .reg file, then deleted – \"Undo registry changes\" on the last page puts them back.");
        if (!Dialogs.Confirm($"Remove {selected.Count} leftover item(s)?\n\n" + string.Join("\n", parts), destructive: true))
            return;
        await CleanAsync(selected);
    }

    private async Task CleanAsync(IReadOnlyList<LeftoverItem> items)
    {
        Step = WizardStep.Cleaning;
        ProgressIndeterminate = false;
        Progress = 0;
        var progress = new Progress<ProgressReport>(r => { StatusText = r.Message; if (r.Percent is { } p) Progress = p; });
        var label = "Uninstall " + (IsSingle ? Jobs[0].Name : $"{Jobs.Count} programs");
        try
        {
            _undo.BeginIfFirst();
            var result = await _services.Cleaner.CleanAsync(items, new CleanupOptions { SendToRecycleBin = SendToRecycleBin, BackupLabel = label }, progress, CancellationToken.None);
            _undo.Record(items, result, SendToRecycleBin);
            _cleanupLedger.Record(result);
            foreach (var item in result.RemovedItems) _cleanupFailures.Remove(item);
            foreach (var (item, _) in result.Errors) _cleanupFailures.Add(item);
            RegistryBackupFile = _undo.RegistryBackupFile;
            CanUndoCleanup = _undo.CanUndo;
            if (result.RegistryBackupFile != null) Log("Registry backup: " + result.RegistryBackupFile);
            UpdateCleanupTotals();
            foreach (var (item, error) in result.Errors) Errors.Add($"{item.Path}: {error}");
            Log($"Cleanup: {result.Removed} removed, {result.Failed} failed. {CleanupSpaceText}");
            int regTotal = items.Count(i => i.Kind is LeftoverKind.RegistryKey or LeftoverKind.RegistryValue or LeftoverKind.StartupEntry);
            int adminNeeded = result.Errors.Count(e => e.Error.Contains("Administrator rights", StringComparison.Ordinal));
            if (regTotal > 0)
            {
                RegistrySummary = $"Registry: {result.RegistryVerified} of {regTotal} key(s)/value(s) removed and verified gone"
                                  + (adminNeeded > 0 ? $" · {adminNeeded} need administrator rights (Restart as administrator → Tools → Residual Cleaner)." : ".");
                Log(RegistrySummary);
            }
            if (adminNeeded > 0) Log($"{adminNeeded} item(s) need administrator rights – restart Evict as administrator and run Tools → Residual Cleaner to remove them.");
            AnythingChanged = true;
            Finish(items.Count);

            if (result.Failed > 0) await OfferRetryOrRollbackAsync(result);
        }
        catch (Exception ex)
        {
            Log("Cleanup failed: " + ex.Message);
            Errors.Add(ex.Message);
            foreach (var item in items) _cleanupFailures.Add(item);
            UpdateCleanupTotals();
            Finish(items.Count);
            OfferRollback("The cleanup stopped: " + ex.Message);
        }
    }

    private void UpdateCleanupTotals()
    {
        LeftoversRemoved = _cleanupLedger.RemovedCount;
        LeftoversFailed = _cleanupFailures.Count;
        BytesRemoved = _cleanupLedger.BytesRemoved;
        BytesReclaimed = _cleanupLedger.BytesReclaimed;
        OnPropertyChanged(nameof(BytesReclaimedText));
        OnPropertyChanged(nameof(CleanupSpaceText));
    }

    /// <summary>Some leftovers could not be removed: retry them (after closing what still runs), undo the cleanup, or keep it.</summary>
    private async Task OfferRetryOrRollbackAsync(CleanupResult result)
    {
        var sample = string.Join("\n", result.Errors.Take(5).Select(e => $"  •  {e.Item.Path}: {e.Error}")) + (result.Errors.Count > 5 ? $"\n  … and {result.Errors.Count - 5} more" : "");
        var options = new List<string> { "Retry the failed items (closes the program's processes first)" };
        if (CanUndoCleanup) options.Add("Undo – put back everything this cleanup removed");
        options.Add("Keep it as it is (the failed items are listed on the summary)");
        int pick = Dialogs.Choose($"{result.Failed} leftover item(s) could not be removed", sample, options, cancelIndex: options.Count - 1);
        if (pick == 0)
        {
            var retry = result.Errors.Select(e => e.Item).ToList();
            foreach (var j in Jobs.Where(j => j.Job.Scan?.Items.Any(retry.Contains) == true))
            {
                var running = await Task.Run(() => RunningProgramService.Find(j.Job.Program));
                if (running.Count == 0) continue;
                var closeErrors = await Task.Run(() => RunningProgramService.ForceClose(running));
                foreach (var error in closeErrors) Log("  could not close " + error);
            }
            foreach (var (item, _) in result.Errors) Errors.Remove(Errors.FirstOrDefault(x => x.StartsWith(item.Path + ":", StringComparison.Ordinal)) ?? "");
            Log($"Retrying {retry.Count} item(s).");
            await CleanAsync(retry);
        }
        else if (pick == 1 && CanUndoCleanup)
        {
            await UndoCleanupAsync(confirm: false);
        }
    }

    [RelayCommand]
    private void SkipCleanup() => FinishWithoutCleanup();

    [RelayCommand]
    private Task UndoCleanupAsync() => UndoCleanupAsync(confirm: true);

    /// <summary>
    /// Rollback of the leftover removal: files and folders go back from the Recycle Bin to where they were, registry
    /// entries are imported from the backup taken before they were deleted. Permanently deleted files, services and
    /// scheduled tasks cannot come back – the message says so.
    /// </summary>
    private async Task UndoCleanupAsync(bool confirm)
    {
        if (!CanUndoCleanup) return;
        if (confirm && !Dialogs.Confirm("Put back the leftovers this uninstall removed?\n\nFiles and folders come back from the Recycle Bin; registry entries are restored from the backup. This does not reinstall the program itself.")) return;

        bool hadRegistry = RegistryBackupFile != null;
        var (ok, lines) = await _undo.UndoAsync();
        _cleanupLedger.Restore(_undo.LastRestoredItems);
        if (hadRegistry && _undo.RegistryBackupFile == null) RegistrySummary = "Registry: the removed entries were restored from the backup.";
        RegistryBackupFile = _undo.RegistryBackupFile;
        CanUndoCleanup = _undo.CanUndo;
        UpdateCleanupTotals();
        Finish(Review.TotalCount + RegistryReview.TotalCount);
        if (RestorePointCreated) lines.Add("\nTo undo what the program's own uninstaller changed, use Open System Restore.");
        var text = string.Join("\n", lines);
        Log("Undo leftover removal: " + text.Replace("\n", " "));
        if (ok) Dialogs.Info(text); else Dialogs.Error(text);
    }

    /// <summary>Windows' System Restore wizard – undoes what the uninstallers changed, back to the restore point of this run.</summary>
    [RelayCommand]
    private void OpenSystemRestore()
    {
        try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("rstrui.exe") { UseShellExecute = true }); }
        catch (Exception ex) { Dialogs.Error("Could not open System Restore: " + ex.Message); }
    }

    /// <summary>After an unexpected error: offer to undo whatever was already removed.</summary>
    private void OfferRollback(string reason)
    {
        if (!CanUndoCleanup && !RestorePointCreated) return;
        var options = new List<string>();
        if (CanUndoCleanup) options.Add("Undo – put back the removed leftovers");
        if (RestorePointCreated) options.Add("Open System Restore");
        options.Add("Keep it as it is");
        int pick = Dialogs.Choose("The uninstall stopped with an error", reason, options, cancelIndex: options.Count - 1);
        var chosen = options[pick];
        if (chosen.StartsWith("Undo", StringComparison.Ordinal)) _ = UndoCleanupAsync(confirm: false);
        else if (chosen.StartsWith("Open", StringComparison.Ordinal)) OpenSystemRestore();
    }

    private void FinishWithoutCleanup() => Finish(0);

    private void Finish(int leftoversConsidered)
    {
        foreach (var j in Jobs.Where(j => j.Job.ForceRemoval && (j.Job.Status is JobStatus.Completed or JobStatus.CompletedWithWarnings)))
        {
            var totals = _cleanupLedger.ForItems(j.Job.Scan?.Items.AsEnumerable() ?? Enumerable.Empty<LeftoverItem>());
            j.Job.ForceRemovalVerified = UninstallOrchestrator.VerifyForcedRemoval(j.Job, totals.Count > 0);
            UninstallOrchestrator.Complete(j.Job);
            j.Sync();
        }
        ProgramsUninstalled = Jobs.Count(j => j.Job.Status == JobStatus.Completed);
        ProgramsWithWarnings = Jobs.Count(j => j.Job.Status is JobStatus.CompletedWithWarnings or JobStatus.Failed or JobStatus.Cancelled
                                               || (j.Job.Status == JobStatus.Skipped && j.Job.Attempts.Count > 0));
        RebootRecommended = Jobs.Any(j => j.Job.RunResult?.ExitCode is 3010 or 1641) || Errors.Any(e => e.Contains("restart", StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(BytesReclaimedText));

        // Stable operation IDs let retries and partial restores correct the existing persisted row.
        foreach (var j in Jobs)
        {
            if (j.Job.Status is JobStatus.Pending || (j.Job.Status == JobStatus.Skipped && j.Job.Attempts.Count == 0)) continue;
            var found = j.Job.Scan?.Items.Count ?? 0;
            var owned = j.Job.Scan?.Items ?? new List<LeftoverItem>();
            var totals = _cleanupLedger.ForItems(owned);
            int failed = owned.Count(_cleanupFailures.Contains);
            if (!_historyEntries.TryGetValue(j.Job, out var operation))
            {
                operation = (Guid.NewGuid(), DateTime.Now);
                _historyEntries[j.Job] = operation;
            }
            _services.History.Upsert(new UninstallHistoryEntry
            {
                Id = operation.Id,
                Timestamp = operation.Timestamp,
                ProgramName = j.Name,
                Publisher = j.Job.Program.Publisher,
                Version = j.Job.Program.DisplayVersion,
                Method = j.Job.ForceRemoval ? UninstallMethod.Force : QuietMode ? UninstallMethod.Quiet : UninstallMethod.Standard,
                ExitCode = j.Job.RunResult?.ExitCode,
                Succeeded = j.Job.Status == JobStatus.Completed && failed == 0,
                LeftoversFound = found,
                LeftoversRemoved = totals.Count,
                BytesRemoved = totals.BytesRemoved,
                BytesReclaimed = totals.BytesReclaimed,
                Notes = j.Job.Message + (found > 0 ? $" Cleanup: {totals.Count} removed, {failed} failed." : "")
                    + (j.Job.ForceRemoval && !j.Job.ForceRemovalVerified ? " Complete application removal could not be verified." : ""),
                InstallLocation = j.Job.Program.InstallLocation,
            });
        }
        CanCancel = false;
        Step = WizardStep.Done;
        StatusText = "";
    }

    [RelayCommand]
    private void Cancel()
    {
        if (Step == WizardStep.Running && CanCancel)
        {
            _cts?.Cancel();
            StatusText = "Cancelling after the current program…";
            CanCancel = false;
            return;
        }
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Close() => RequestClose?.Invoke();

    [RelayCommand]
    private void CopyLog() => Dialogs.CopyToClipboard(string.Join(Environment.NewLine, LogLines));
}
