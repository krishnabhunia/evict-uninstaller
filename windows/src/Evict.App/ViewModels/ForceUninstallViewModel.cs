using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Evict.App.Services;
using Evict.Core.Models;
using Evict.Core.Services;
using Evict.Core.Util;

namespace Evict.App.ViewModels;

public enum ForceStep { Target, Scanning, Review, Cleaning, Done }

/// <summary>Force Uninstall: pick a program (or a folder / exe), scan everything it owns, remove it.</summary>
public sealed partial class ForceUninstallViewModel : ObservableObject
{
    private readonly AppServices _services;
    private CancellationTokenSource? _cts;

    public ForceUninstallViewModel(AppServices services, InstalledProgram? preselected, IReadOnlyList<InstalledProgram> allPrograms)
    {
        _services = services;
        Programs = new ObservableCollection<InstalledProgram>(allPrograms.OrderBy(p => p.DisplayName, StringComparer.CurrentCultureIgnoreCase));
        _selectedProgram = preselected;
        _useProgram = preselected != null || allPrograms.Count > 0;
        _sendToRecycleBin = services.Settings.Current.SendToRecycleBin;
        Review = new LeftoverReviewViewModel();
    }

    /// <summary>Entry point from History: only a name and a folder are known.</summary>
    public ForceUninstallViewModel(AppServices services, string displayName, string? installLocation)
        : this(services, null, Array.Empty<InstalledProgram>())
    {
        _useProgram = false;
        _targetPath = installLocation ?? "";
        _customName = displayName;
    }

    public ObservableCollection<InstalledProgram> Programs { get; }
    public LeftoverReviewViewModel Review { get; }
    public ObservableCollection<string> Warnings { get; } = new();
    public ObservableCollection<string> Errors { get; } = new();
    public ObservableCollection<string> RunningProcesses { get; } = new();

    [ObservableProperty] private ForceStep _step = ForceStep.Target;
    [ObservableProperty] private bool _useProgram;
    [ObservableProperty] private InstalledProgram? _selectedProgram;
    [ObservableProperty] private string _targetPath = "";
    [ObservableProperty] private string _customName = "";
    [ObservableProperty] private bool _sendToRecycleBin;
    [ObservableProperty] private bool _killProcesses = true;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private int _removed;
    [ObservableProperty] private int _failed;
    [ObservableProperty] private long _bytesReclaimed;
    [ObservableProperty] private long _bytesRemoved;
    /// <summary>Something was removed that can be put back (Recycle Bin / registry backup).</summary>
    [ObservableProperty] private bool _canUndo;
    private readonly CleanupUndo _undo = new();
    private readonly UninstallCleanupLedger _cleanupLedger = new();
    private Guid? _historyId;
    private DateTime _historyTimestamp;

    public bool AnythingChanged { get; private set; }
    public string BytesReclaimedText => SizeFormatter.Format(BytesReclaimed);
    public string CleanupSpaceText => $"{SizeFormatter.Format(BytesRemoved)} removed from original locations; {BytesReclaimedText} permanently deleted. Recycled data still occupies disk space.";
    public string TargetName => UseProgram ? SelectedProgram?.DisplayName ?? "" : (CustomName.Length > 0 ? CustomName : PathUtil.LeafName(TargetPath));
    public bool CanScan => UseProgram ? SelectedProgram != null : TargetPath.Trim().Length > 0 && (Directory.Exists(TargetPath.Trim()) || File.Exists(TargetPath.Trim()));

    public event Action? RequestClose;

    /// <summary>Skips the scan step and shows a ready-made list (used by Install Monitor logs).</summary>
    public void PreloadLeftovers(IEnumerable<LeftoverItem> items)
    {
        Review.Load(items, selectLowConfidence: false);
        Step = ForceStep.Review;
    }

    partial void OnUseProgramChanged(bool value) { OnPropertyChanged(nameof(CanScan)); OnPropertyChanged(nameof(TargetName)); }
    partial void OnSelectedProgramChanged(InstalledProgram? value) { OnPropertyChanged(nameof(CanScan)); OnPropertyChanged(nameof(TargetName)); }
    partial void OnTargetPathChanged(string value) { OnPropertyChanged(nameof(CanScan)); OnPropertyChanged(nameof(TargetName)); }
    partial void OnCustomNameChanged(string value) => OnPropertyChanged(nameof(TargetName));

    [RelayCommand]
    private void BrowseFolder()
    {
        var dlg = new Microsoft.Win32.OpenFolderDialog { Title = "Select the program's folder" };
        if (dlg.ShowDialog() == true) TargetPath = dlg.FolderName;
    }

    [RelayCommand]
    private void BrowseFile()
    {
        var dlg = new Microsoft.Win32.OpenFileDialog { Title = "Select the program's main executable", Filter = "Programs (*.exe)|*.exe|All files (*.*)|*.*" };
        if (dlg.ShowDialog() == true) TargetPath = dlg.FileName;
    }

    [RelayCommand]
    private async Task ScanAsync()
    {
        if (!CanScan) return;
        _cts = new CancellationTokenSource();
        Step = ForceStep.Scanning;
        Warnings.Clear();
        var progress = new Progress<ProgressReport>(r => { StatusText = r.Message; if (r.Percent is { } p) Progress = p; });
        try
        {
            var program = UseProgram ? SelectedProgram : null;
            var selectedPath = UseProgram ? null : TargetPath.Trim();
            var (_, result) = await _services.Force.ScanAsync(program, selectedPath,
                new LeftoverScanOptions { ScanAllUserProfiles = _services.Settings.Current.ScanAllUserProfiles }, progress, _cts.Token);
            foreach (var w in result.Warnings) Warnings.Add(w);

            RunningProcesses.Clear();
            var running = await Task.Run(() => ForceUninstallService.FindProcessesFor(program, selectedPath));
            foreach (var (pid, name, path) in running) RunningProcesses.Add($"{name} (PID {pid}) - {path}");
            Review.Load(result.Items, selectLowConfidence: false);
            Step = ForceStep.Review;
        }
        catch (OperationCanceledException)
        {
            Step = ForceStep.Target;
        }
        catch (Exception ex)
        {
            Dialogs.Error("Scan failed: " + ex.Message);
            Step = ForceStep.Target;
        }
    }

    [RelayCommand]
    private async Task RemoveAsync()
    {
        var items = Review.SelectedLeftovers;
        if (items.Count == 0) return;
        var name = TargetName;
        if (!Dialogs.Confirm($"Force-remove {items.Count} item(s) belonging to \"{name}\"?\n\nThis bypasses the program's own uninstaller. Make sure the program is not something you still need.", destructive: true))
            return;

        Step = ForceStep.Cleaning;
        Progress = 0;
        Errors.Clear();
        Removed = 0;
        Failed = 0;
        BytesReclaimed = 0;
        var progress = new Progress<ProgressReport>(r => { StatusText = r.Message; if (r.Percent is { } p) Progress = p; });

        try
        {
            if (KillProcesses)
            {
                var program = UseProgram ? SelectedProgram : null;
                var selectedPath = UseProgram ? null : TargetPath.Trim();
                if (program != null || !string.IsNullOrEmpty(selectedPath))
                {
                    StatusText = "Closing running processes…";
                    await Task.Run(() =>
                    {
                        ForceUninstallService.KillProcessesFor(program, selectedPath, out var errs);
                        foreach (var e in errs) System.Windows.Application.Current.Dispatcher.Invoke(() => Errors.Add(e));
                    });
                }
            }

            var result = await CleanAndRecordAsync(items, progress);
            if (result.Failed > 0) result = await OfferRetryOrUndoAsync(result, progress);

            PersistHistory();
            Step = ForceStep.Done;
        }
        catch (Exception ex)
        {
            Errors.Add("Removal stopped: " + ex.Message);
            Failed = Math.Max(1, items.Count - Removed);
            CanUndo = _undo.CanUndo;
            PersistHistory();
            Step = ForceStep.Done;
            Dialogs.Error("Force removal stopped: " + ex.Message);
        }
    }

    private void PersistHistory()
    {
        if (_historyId == null) { _historyId = Guid.NewGuid(); _historyTimestamp = DateTime.Now; }
        _services.History.Upsert(new UninstallHistoryEntry
        {
            Id = _historyId.Value,
            Timestamp = _historyTimestamp,
            ProgramName = TargetName,
            Publisher = SelectedProgram?.Publisher,
            Version = SelectedProgram?.DisplayVersion,
            Method = UninstallMethod.Force,
            Succeeded = Failed == 0 && Removed > 0,
            LeftoversFound = Review.TotalCount,
            LeftoversRemoved = Removed,
            BytesReclaimed = BytesReclaimed,
            BytesRemoved = BytesRemoved,
            InstallLocation = UseProgram ? SelectedProgram?.InstallLocation : TargetPath,
            Notes = "Force uninstall",
        });
    }

    private async Task<CleanupResult> CleanAndRecordAsync(IReadOnlyList<LeftoverItem> items, IProgress<ProgressReport> progress)
    {
        _undo.BeginIfFirst();
        var result = await _services.Cleaner.CleanAsync(items, new CleanupOptions { SendToRecycleBin = SendToRecycleBin, BackupLabel = "Force uninstall " + TargetName }, progress, CancellationToken.None);
        _undo.Record(items, result, SendToRecycleBin);
        _cleanupLedger.Record(result);
        CanUndo = _undo.CanUndo;
        Removed = _cleanupLedger.RemovedCount;
        Failed = result.Failed;
        BytesRemoved = _cleanupLedger.BytesRemoved;
        BytesReclaimed = _cleanupLedger.BytesReclaimed;
        OnPropertyChanged(nameof(BytesReclaimedText));
        OnPropertyChanged(nameof(CleanupSpaceText));
        foreach (var (item, error) in result.Errors) Errors.Add($"{item.Path}: {error}");
        AnythingChanged = true;
        return result;
    }

    /// <summary>Some items could not be removed: retry them (ending the program's processes first), undo everything, or keep it.</summary>
    private async Task<CleanupResult> OfferRetryOrUndoAsync(CleanupResult result, IProgress<ProgressReport> progress)
    {
        while (result.Failed > 0)
        {
            var sample = string.Join("\n", result.Errors.Take(5).Select(e => $"  •  {e.Item.Path}: {e.Error}")) + (result.Errors.Count > 5 ? $"\n  … and {result.Errors.Count - 5} more" : "");
            var options = new List<string> { "Retry the failed items (ends the program's processes first)" };
            if (CanUndo) options.Add("Undo – put back everything that was removed");
            options.Add("Keep it as it is");
            int pick = Dialogs.Choose($"{result.Failed} item(s) could not be removed", sample, options, cancelIndex: options.Count - 1);
            if (pick == 0)
            {
                Errors.Clear();
                var program = UseProgram ? SelectedProgram : null;
                var selectedPath = UseProgram ? null : TargetPath.Trim();
                var errors = await Task.Run(() => { ForceUninstallService.KillProcessesFor(program, selectedPath, out var lines); return lines; });
                foreach (var error in errors) Errors.Add(error);
                result = await CleanAndRecordAsync(result.Errors.Select(e => e.Item).ToList(), progress);
                continue;
            }
            if (pick == 1 && CanUndo) await UndoAsync(confirm: false);
            break;
        }
        return result;
    }

    [RelayCommand]
    private Task Undo() => UndoAsync(confirm: true);

    private async Task UndoAsync(bool confirm)
    {
        if (!CanUndo) return;
        if (confirm && !Dialogs.Confirm("Put back what this force uninstall removed?\n\nFiles and folders come back from the Recycle Bin; registry entries are restored from the backup.")) return;
        var (ok, lines) = await _undo.UndoAsync();
        _cleanupLedger.Restore(_undo.LastRestoredItems);
        CanUndo = _undo.CanUndo;
        Removed = _cleanupLedger.RemovedCount;
        BytesRemoved = _cleanupLedger.BytesRemoved;
        BytesReclaimed = _cleanupLedger.BytesReclaimed;
        OnPropertyChanged(nameof(BytesReclaimedText));
        OnPropertyChanged(nameof(CleanupSpaceText));
        if (_historyId != null) PersistHistory();
        var text = string.Join("\n", lines);
        Core.Services.Log.Info("Force uninstall undo: " + text.Replace("\n", " "));
        if (ok) Dialogs.Info(text); else Dialogs.Error(text);
    }

    [RelayCommand]
    private void Back() => Step = ForceStep.Target;

    [RelayCommand]
    private void Cancel()
    {
        if (Step == ForceStep.Scanning) { _cts?.Cancel(); return; }
        RequestClose?.Invoke();
    }

    [RelayCommand]
    private void Close() => RequestClose?.Invoke();
}
