using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Evict.App.Services;
using Evict.Core.Models;
using Evict.Core.Services;
using Evict.Core.Util;

namespace Evict.App.ViewModels;

public enum WizardStep { Confirm, Running, Review, Cleaning, Done }

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
    }

    public ObservableCollection<UninstallJobViewModel> Jobs { get; }
    public LeftoverReviewViewModel Review { get; }
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

    // Done-step summary
    [ObservableProperty] private int _programsUninstalled;
    [ObservableProperty] private int _programsWithWarnings;
    [ObservableProperty] private int _leftoversRemoved;
    [ObservableProperty] private int _leftoversFailed;
    /// <summary>"Registry: 7 of 8 keys/values removed and verified gone · 1 needs administrator rights" (null when no registry items).</summary>
    [ObservableProperty] private string? _registrySummary;
    [ObservableProperty] private long _bytesReclaimed;
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
                Log("Restore point: " + rp.Message);
                foreach (var j in Jobs) { j.Job.Status = JobStatus.Pending; j.Sync(); }
            }

            int index = 0;
            foreach (var jvm in Jobs)
            {
                ct.ThrowIfCancellationRequested();
                index++;
                ProgressIndeterminate = true;
                StatusText = $"Uninstalling {jvm.Name} ({index}/{Jobs.Count})…";
                Log($"Starting {jvm.Name}");
                jvm.Job.Status = JobStatus.Uninstalling; jvm.Sync();

                using var timer = new Timer(_ => System.Windows.Application.Current?.Dispatcher.BeginInvoke(jvm.Sync), null, 500, 500);
                await _services.Orchestrator.RunAsync(jvm.Job, options, progress, ct);
                jvm.Sync();
                Log($"{jvm.Name}: {jvm.Job.Message}");
                if (jvm.Job.RunResult?.Command is { } cmd) Log("  command: " + cmd);
                AnythingChanged = true;
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

            Review.Load(leftovers, selectLowConfidence: false);
            if (AutoClean)
            {
                await CleanAsync(Review.Items.Where(i => !i.IsLow).Select(i => i.Item).ToList());
            }
            else
            {
                Step = WizardStep.Review;
            }
        }
        catch (OperationCanceledException)
        {
            Log("Cancelled by user.");
            foreach (var j in Jobs.Where(j => j.Job.Status == JobStatus.Pending)) { j.Job.Status = JobStatus.Skipped; j.Job.Message = "Skipped"; j.Sync(); }
            FinishWithoutCleanup();
        }
        catch (Exception ex)
        {
            Log("Error: " + ex.Message);
            Errors.Add(ex.Message);
            FinishWithoutCleanup();
        }
    }

    [RelayCommand]
    private async Task RemoveSelectedLeftoversAsync()
    {
        var selected = Review.SelectedLeftovers;
        if (selected.Count == 0) { FinishWithoutCleanup(); return; }
        if (!Dialogs.Confirm($"Permanently remove {selected.Count} leftover item(s)?" + (SendToRecycleBin ? "\n\nFiles and folders go to the Recycle Bin; registry entries are deleted." : "\n\nFiles are deleted permanently (not sent to the Recycle Bin)."), destructive: true))
            return;
        await CleanAsync(selected);
    }

    private async Task CleanAsync(IReadOnlyList<LeftoverItem> items)
    {
        Step = WizardStep.Cleaning;
        ProgressIndeterminate = false;
        Progress = 0;
        var progress = new Progress<ProgressReport>(r => { StatusText = r.Message; if (r.Percent is { } p) Progress = p; });
        var result = await _services.Cleaner.CleanAsync(items, new CleanupOptions { SendToRecycleBin = SendToRecycleBin }, progress, CancellationToken.None);
        LeftoversRemoved = result.Removed;
        LeftoversFailed = result.Failed;
        BytesReclaimed = result.BytesReclaimed;
        foreach (var (item, error) in result.Errors) Errors.Add($"{item.Path}: {error}");
        Log($"Cleanup: {result.Removed} removed, {result.Failed} failed, {SizeFormatter.Format(result.BytesReclaimed)} reclaimed.");
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
    }

    [RelayCommand]
    private void SkipCleanup() => FinishWithoutCleanup();

    private void FinishWithoutCleanup() => Finish(0);

    private void Finish(int leftoversConsidered)
    {
        ProgramsUninstalled = Jobs.Count(j => j.Job.Status == JobStatus.Completed);
        ProgramsWithWarnings = Jobs.Count(j => j.Job.Status is JobStatus.CompletedWithWarnings or JobStatus.Failed);
        RebootRecommended = Jobs.Any(j => j.Job.RunResult?.ExitCode is 3010 or 1641) || Errors.Any(e => e.Contains("restart", StringComparison.OrdinalIgnoreCase));
        OnPropertyChanged(nameof(BytesReclaimedText));

        foreach (var j in Jobs)
        {
            if (j.Job.Status is JobStatus.Pending or JobStatus.Skipped) continue;
            var found = j.Job.Scan?.Items.Count ?? 0;
            _services.History.Add(new UninstallHistoryEntry
            {
                ProgramName = j.Name,
                Publisher = j.Job.Program.Publisher,
                Version = j.Job.Program.DisplayVersion,
                Method = QuietMode ? UninstallMethod.Quiet : UninstallMethod.Standard,
                ExitCode = j.Job.RunResult?.ExitCode,
                Succeeded = j.Job.Status == JobStatus.Completed,
                LeftoversFound = found,
                LeftoversRemoved = Jobs.Count == 1 ? LeftoversRemoved : Math.Min(found, LeftoversRemoved),
                BytesReclaimed = Jobs.Count == 1 ? BytesReclaimed : 0,
                Notes = j.Job.Message,
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
