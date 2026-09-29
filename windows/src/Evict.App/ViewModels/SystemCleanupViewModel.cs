using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Evict.App.Services;
using Evict.Core.Models;
using Evict.Core.Services;
using Evict.Core.Util;

namespace Evict.App.ViewModels;

public enum CleanupStep { Idle, Scanning, Review, Cleaning, Done }

public sealed partial class CleanupItemViewModel : ObservableObject
{
    public CleanupItemViewModel(CleanupGroupViewModel group, CleanupItem item)
    {
        Group = group;
        Item = item;
        _isSelected = item.Confidence != LeftoverConfidence.Low && !group.OpensSettings && !group.NeedsElevation;
    }

    public CleanupGroupViewModel Group { get; }
    public CleanupItem Item { get; }
    [ObservableProperty] private bool _isSelected;
    partial void OnIsSelectedChanged(bool value) => Group.OnItemSelectionChanged();

    public string Path => Item.Path;
    public string Name => PathUtil.LeafName(Item.Path);
    public string SizeText => SizeFormatter.Format(Item.Size);
    public string Detail => Item.Detail ?? (Item.IsDirectory ? "folder" : "file");
    public string ConfidenceText => Item.Confidence switch { LeftoverConfidence.High => "Safe", LeftoverConfidence.Medium => "Likely", _ => "Review" };
    public bool IsReview => Item.Confidence == LeftoverConfidence.Low;
}

public sealed partial class CleanupGroupViewModel : ObservableObject
{
    public CleanupGroupViewModel(CleanupGroup group)
    {
        Group = group;
        Items = new ObservableCollection<CleanupItemViewModel>(group.Items.Select(i => new CleanupItemViewModel(this, i)));
        IsExpanded = false;
    }

    public CleanupGroup Group { get; }
    public ObservableCollection<CleanupItemViewModel> Items { get; }
    public string Title => Group.Title;
    public string Description => Group.Description;
    public bool RequiresAdmin => Group.RequiresAdmin;
    public bool NeedsElevation => Group.RequiresAdmin && !ElevationHelper.IsElevated;
    public bool HasSpecialAction => Group.SpecialAction != null;
    public bool OpensSettings => Group.SpecialAction?.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase) == true;
    public bool HasItems => Items.Count > 0;
    public bool HasError => Group.Error != null;
    public string? Error => Group.Error;
    public string? Note => Group.Note;
    public string SizeText => SizeFormatter.Format(Group.TotalSize);
    public string CountText => Items.Count == 1 ? "1 item" : $"{Items.Count:N0} items";
    public string SelectedSizeText => SizeFormatter.Format(Items.Where(i => i.IsSelected).Sum(i => i.Item.Size));
    public int SelectedCount => Items.Count(i => i.IsSelected);
    public long SelectedBytes => Items.Where(i => i.IsSelected).Sum(i => i.Item.Size);

    [ObservableProperty] private bool _isExpanded;

    /// <summary>Tri-state header checkbox.</summary>
    public bool? IsSelected
    {
        get
        {
            if (Items.Count == 0) return false;
            int n = SelectedCount;
            return n == 0 ? false : n == Items.Count ? true : null;
        }
        set
        {
            var v = value ?? false;
            _bulk = true;
            try { foreach (var i in Items) i.IsSelected = v; }
            finally { _bulk = false; }
            OnItemSelectionChanged();
        }
    }

    private bool _bulk;
    public event Action? SelectionChanged;
    internal void OnItemSelectionChanged()
    {
        if (_bulk) return;
        OnPropertyChanged(nameof(IsSelected));
        OnPropertyChanged(nameof(SelectedSizeText));
        OnPropertyChanged(nameof(SelectedCount));
        SelectionChanged?.Invoke();
    }
}

/// <summary>System Cleanup tool: scan → review per category → clean, with the Installer-cache backup option.</summary>
public sealed partial class SystemCleanupViewModel : ObservableObject
{
    private readonly AppServices _services;
    private CancellationTokenSource? _cts;

    public SystemCleanupViewModel(AppServices services)
    {
        _services = services;
    }

    public ObservableCollection<CleanupGroupViewModel> Groups { get; } = new();
    public ObservableCollection<string> Errors { get; } = new();

    [ObservableProperty] private CleanupStep _step = CleanupStep.Idle;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _progressIndeterminate = true;
    [ObservableProperty] private bool _moveInstallerCacheToBackup = true;
    [ObservableProperty] private string _selectionText = "";
    [ObservableProperty] private long _bytesFreed;
    [ObservableProperty] private int _removed;
    [ObservableProperty] private int _failed;
    [ObservableProperty] private string? _backupFolder;

    public bool IsElevated => ElevationHelper.IsElevated;
    public bool IsBusy => Step is CleanupStep.Scanning or CleanupStep.Cleaning;
    public bool CanClean => Step == CleanupStep.Review && Groups.Any(g => g.SelectedCount > 0);
    public bool AnythingChanged { get; private set; }
    public string BytesFreedText => SizeFormatter.Format(BytesFreed);
    public string TotalFoundText => SizeFormatter.Format(Groups.Sum(g => g.Group.TotalSize));
    public event Action? RequestClose;

    public string BusyTitle => Step == CleanupStep.Cleaning ? "Cleaning up…" : "Measuring system junk…";
    partial void OnStepChanged(CleanupStep value) { OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(CanClean)); OnPropertyChanged(nameof(BusyTitle)); }

    [RelayCommand]
    public async Task ScanAsync()
    {
        if (IsBusy) return;
        Step = CleanupStep.Scanning;
        ProgressIndeterminate = false;
        Progress = 0;
        Groups.Clear();
        Errors.Clear();
        _cts = new CancellationTokenSource();
        var progress = new Progress<ProgressReport>(r => { StatusText = r.Message; if (r.Percent is { } p) Progress = p; });
        try
        {
            StatusText = "Reading installed Store apps…";
            IReadOnlyCollection<string>? families = null;
            try
            {
                var (packages, _) = await _services.Appx.GetPackagesAsync(allUsers: false, null, _cts.Token);
                if (packages.Count > 0) families = packages.Select(p => p.PackageFamilyName).Where(f => !string.IsNullOrEmpty(f)).Select(f => f!).Distinct().ToList();
            }
            catch (Exception ex) { Log.Warn("Appx list for cleanup failed: " + ex.Message); }

            var groups = await _services.Cleanup.ScanAsync(families, progress, _cts.Token);
            foreach (var g in groups)
            {
                var vm = new CleanupGroupViewModel(g);
                vm.SelectionChanged += UpdateSelection;
                Groups.Add(vm);
            }
            Step = CleanupStep.Review;
            UpdateSelection();
            OnPropertyChanged(nameof(TotalFoundText));
            StatusText = $"Found {SizeFormatter.Format(groups.Sum(g => g.TotalSize))} in {groups.Count(g => g.Items.Count > 0)} categories.";
        }
        catch (OperationCanceledException) { Step = CleanupStep.Idle; StatusText = "Scan cancelled."; }
        catch (Exception ex)
        {
            Log.Error("System cleanup scan failed", ex);
            Step = CleanupStep.Idle;
            StatusText = "Scan failed: " + ex.Message;
        }
        finally { _cts?.Dispose(); _cts = null; }
    }

    private void UpdateSelection()
    {
        long bytes = Groups.Sum(g => g.SelectedBytes);
        int count = Groups.Sum(g => g.SelectedCount);
        SelectionText = count == 0 ? "Nothing selected." : $"{count:N0} item(s) selected · {SizeFormatter.Format(bytes)}";
        OnPropertyChanged(nameof(CanClean));
    }

    [RelayCommand]
    private async Task CleanAsync()
    {
        if (!CanClean) return;
        var selection = Groups.SelectMany(g => g.Items.Where(i => i.IsSelected).Select(i => (g.Group, i.Item))).ToList();
        var adminNeeded = Groups.Where(g => g.NeedsElevation && g.SelectedCount > 0).Select(g => g.Title).ToList();
        var text = $"Remove {selection.Count:N0} item(s) ({SizeFormatter.Format(selection.Sum(s => s.Item.Size))})?" +
                   (selection.Any(s => s.Group.Category == CleanupCategory.InstallerCache) && MoveInstallerCacheToBackup ? "\n\nInstaller-cache files are moved to the backup folder, not deleted." : "") +
                   (adminNeeded.Count > 0 ? "\n\nThese categories need administrator rights and will probably fail: " + string.Join(", ", adminNeeded) + "." : "");
        if (!Dialogs.Confirm(text, destructive: true)) return;

        Step = CleanupStep.Cleaning;
        Errors.Clear();
        _cts = new CancellationTokenSource();
        var progress = new Progress<ProgressReport>(r => { StatusText = r.Message; if (r.Percent is { } p) Progress = p; });
        try
        {
            var result = await _services.Cleanup.CleanAsync(selection, MoveInstallerCacheToBackup, progress, _cts.Token);
            Removed = result.Removed; Failed = result.Failed; BytesFreed = result.BytesFreed; BackupFolder = result.BackupFolder;
            foreach (var e in result.Errors.Take(200)) Errors.Add(e);
            AnythingChanged = result.Removed > 0;
            OnPropertyChanged(nameof(BytesFreedText));
            Step = CleanupStep.Done;
            StatusText = $"Removed {Removed:N0} item(s), {Failed:N0} failed, {BytesFreedText} freed" +
                         (result.BytesMoved > 0 ? $", {SizeFormatter.Format(result.BytesMoved)} of installer packages moved to the backup folder." : ".");
            _services.History.Add(new UninstallHistoryEntry
            {
                ProgramName = "System Cleanup", Method = UninstallMethod.Force, Succeeded = Failed == 0,
                LeftoversFound = selection.Count, LeftoversRemoved = Removed, BytesReclaimed = BytesFreed,
                Notes = StatusText + (BackupFolder != null ? $" Installer-cache backup: {BackupFolder}" : ""),
            });
        }
        catch (OperationCanceledException) { Step = CleanupStep.Review; StatusText = "Cancelled."; }
        catch (Exception ex)
        {
            Log.Error("System cleanup failed", ex);
            Step = CleanupStep.Review;
            StatusText = "Cleanup failed: " + ex.Message;
        }
        finally { _cts?.Dispose(); _cts = null; }
    }

    [RelayCommand] private void OpenItem(CleanupItemViewModel? item) { if (item != null) Dialogs.OpenFolder(item.Item.IsDirectory ? item.Path : PathUtil.ParentPath(item.Path)); }
    [RelayCommand] private void OpenSpecial(CleanupGroupViewModel? group) { if (group?.Group.SpecialAction is { } a && a.StartsWith("ms-settings:", StringComparison.OrdinalIgnoreCase)) Dialogs.OpenUrl(a); }
    [RelayCommand] private void OpenBackupFolder() => Dialogs.OpenFolder(BackupFolder ?? SystemCleanupService.BackupRoot);
    [RelayCommand] private void Cancel() { if (IsBusy) _cts?.Cancel(); else RequestClose?.Invoke(); }
    [RelayCommand] private void Close() => RequestClose?.Invoke();
    [RelayCommand] private void Rescan() => _ = ScanAsync();
    [RelayCommand] private void ToggleExpand(CleanupGroupViewModel? group) { if (group != null) group.IsExpanded = !group.IsExpanded; }
}
