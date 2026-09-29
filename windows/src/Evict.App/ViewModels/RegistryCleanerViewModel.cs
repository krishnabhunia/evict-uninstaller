using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Evict.App.Services;
using Evict.Core.Models;
using Evict.Core.Services;
using Evict.Core.Util;
using Microsoft.Win32;

namespace Evict.App.ViewModels;

public sealed partial class RegistryIssueItemViewModel : ObservableObject
{
    public RegistryIssueItemViewModel(RegistryGroupViewModel group, LeftoverItem item)
    {
        Group = group;
        Item = item;
        _isSelected = !group.Group.Advanced && item.Confidence == LeftoverConfidence.High && !NeedsElevation;
    }

    public RegistryGroupViewModel Group { get; }
    public LeftoverItem Item { get; }
    [ObservableProperty] private bool _isSelected;
    partial void OnIsSelectedChanged(bool value) => Group.OnItemSelectionChanged();

    public string Path => Item.Path;
    public string Detail => Item.Detail ?? "";
    public string KindText => Item.Kind == LeftoverKind.RegistryKey ? "Key" : "Value";
    public string ConfidenceText => Item.Confidence == LeftoverConfidence.High ? "Safe" : "Review";
    public bool NeedsElevation => Item.Hive == RegistryHive.LocalMachine && !ElevationHelper.IsElevated;
}

public sealed partial class RegistryGroupViewModel : ObservableObject
{
    public RegistryGroupViewModel(RegistryIssueGroup group)
    {
        Group = group;
        Items = new ObservableCollection<RegistryIssueItemViewModel>(group.Items.Select(i => new RegistryIssueItemViewModel(this, i)));
    }

    public RegistryIssueGroup Group { get; }
    public ObservableCollection<RegistryIssueItemViewModel> Items { get; }
    public string Title => Group.Title;
    public string Description => Group.Description;
    public bool Advanced => Group.Advanced;
    public bool IsPrivacy => Group.IsPrivacy;
    public bool NeedsElevation => Group.TouchesLocalMachine && !ElevationHelper.IsElevated;
    public bool HasItems => Items.Count > 0;
    public bool HasError => Group.Error != null;
    public string? Error => Group.Error;
    public string CountText => Items.Count == 0 ? "nothing found" : Items.Count == 1 ? "1 entry" : $"{Items.Count:N0} entries";
    public int SelectedCount => Items.Count(i => i.IsSelected);

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
        OnPropertyChanged(nameof(SelectedCount));
        SelectionChanged?.Invoke();
    }
}

public sealed partial class RegistryBackupViewModel : ObservableObject
{
    public RegistryBackupViewModel(RegistryBackupInfo info) => Info = info;
    public RegistryBackupInfo Info { get; }
    public string Label => Info.Label;
    public string DateText => Info.Created.ToString("dd MMM yyyy HH:mm");
    public string SizeText => SizeFormatter.Format(Info.SizeBytes);
    public bool NeedsElevation => Info.TouchesLocalMachine && !ElevationHelper.IsElevated;
    public string ScopeText => Info.TouchesLocalMachine ? "this PC + your account" : "your account";
}

/// <summary>Registry Cleaner: scan → review per category → back up to .reg → delete; restore any backup later.</summary>
public sealed partial class RegistryCleanerViewModel : ObservableObject
{
    private readonly AppServices _services;
    private CancellationTokenSource? _cts;

    public RegistryCleanerViewModel(AppServices services)
    {
        _services = services;
        ReloadBackups();
    }

    public ObservableCollection<RegistryGroupViewModel> Groups { get; } = new();
    public ObservableCollection<RegistryBackupViewModel> Backups { get; } = new();
    public ObservableCollection<string> Errors { get; } = new();

    [ObservableProperty] private CleanupStep _step = CleanupStep.Idle;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private string _selectionText = "";
    [ObservableProperty] private int _removed;
    [ObservableProperty] private int _failed;
    [ObservableProperty] private string? _lastBackupFile;

    public bool IsElevated => ElevationHelper.IsElevated;
    public bool IsBusy => Step is CleanupStep.Scanning or CleanupStep.Cleaning;
    public bool CanClean => Step == CleanupStep.Review && Groups.Any(g => g.SelectedCount > 0);
    public bool HasBackups => Backups.Count > 0;
    public bool AnythingChanged { get; private set; }
    public string TotalFoundText => $"{Groups.Sum(g => g.Items.Count):N0}";
    public string BusyTitle => Step == CleanupStep.Cleaning ? "Backing up and removing entries…" : "Scanning the registry…";
    public event Action? RequestClose;

    partial void OnStepChanged(CleanupStep value) { OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(CanClean)); OnPropertyChanged(nameof(BusyTitle)); }

    private void ReloadBackups()
    {
        Backups.Clear();
        foreach (var b in RegistryBackupService.List()) Backups.Add(new RegistryBackupViewModel(b));
        OnPropertyChanged(nameof(HasBackups));
    }

    [RelayCommand]
    public async Task ScanAsync()
    {
        if (IsBusy) return;
        Step = CleanupStep.Scanning;
        Progress = 0;
        Groups.Clear();
        Errors.Clear();
        _cts = new CancellationTokenSource();
        var progress = new Progress<ProgressReport>(r => { StatusText = r.Message; if (r.Percent is { } p) Progress = p; });
        try
        {
            var groups = await new RegistryCleanerService().ScanAsync(progress, _cts.Token);
            // Categories with findings first, the rest (collapsed, "nothing found") below.
            foreach (var g in groups.OrderBy(g => g.Items.Count == 0).ThenBy(g => g.Advanced))
            {
                var vm = new RegistryGroupViewModel(g);
                vm.SelectionChanged += UpdateSelection;
                Groups.Add(vm);
            }
            Step = CleanupStep.Review;
            UpdateSelection();
            OnPropertyChanged(nameof(TotalFoundText));
            StatusText = $"Found {groups.Sum(g => g.Items.Count):N0} entr{(groups.Sum(g => g.Items.Count) == 1 ? "y" : "ies")} in {groups.Count(g => g.Items.Count > 0)} categories.";
        }
        catch (OperationCanceledException) { Step = CleanupStep.Idle; StatusText = "Scan cancelled."; }
        catch (Exception ex)
        {
            Log.Error("Registry scan failed", ex);
            Step = CleanupStep.Idle;
            StatusText = "Scan failed: " + ex.Message;
        }
        finally { _cts?.Dispose(); _cts = null; }
    }

    private void UpdateSelection()
    {
        int count = Groups.Sum(g => g.SelectedCount);
        SelectionText = count == 0 ? "Nothing selected." : $"{count:N0} entr{(count == 1 ? "y" : "ies")} selected – a .reg backup is written first.";
        OnPropertyChanged(nameof(CanClean));
    }

    [RelayCommand]
    private async Task CleanAsync()
    {
        if (!CanClean) return;
        var selection = Groups.SelectMany(g => g.Items.Where(i => i.IsSelected).Select(i => i.Item)).ToList();
        int adminNeeded = IsElevated ? 0 : selection.Count(i => i.Hive == RegistryHive.LocalMachine);
        var text = $"Remove {selection.Count:N0} registry entr{(selection.Count == 1 ? "y" : "ies")}?\n\nEverything is exported to a .reg backup first – you can put it back with \"Restore\"." +
                   (adminNeeded > 0 ? $"\n\n{adminNeeded} of them are machine-wide and need administrator rights – they will fail unless you restart Evict as administrator." : "");
        if (!Dialogs.Confirm(text, destructive: true)) return;

        Step = CleanupStep.Cleaning;
        Errors.Clear();
        var progress = new Progress<ProgressReport>(r => { StatusText = r.Message; if (r.Percent is { } p) Progress = p; });
        try
        {
            var result = await _services.Cleaner.CleanAsync(selection, new CleanupOptions { BackupLabel = "Registry Cleaner", SendToRecycleBin = false }, progress, CancellationToken.None);
            Removed = result.Removed;
            Failed = result.Failed;
            LastBackupFile = result.RegistryBackupFile;
            foreach (var (item, error) in result.Errors.Take(200)) Errors.Add($"{item.Path}: {error}");
            AnythingChanged = result.Removed > 0;
            Step = CleanupStep.Done;
            StatusText = $"Removed {Removed:N0} entr{(Removed == 1 ? "y" : "ies")}" + (Failed > 0 ? $", {Failed:N0} could not be removed." : ".");
            _services.History.Add(new UninstallHistoryEntry
            {
                ProgramName = "Registry Cleaner", Method = UninstallMethod.Force, Succeeded = Failed == 0,
                LeftoversFound = selection.Count, LeftoversRemoved = Removed,
                Notes = StatusText + (LastBackupFile != null ? $" Backup: {LastBackupFile}" : ""),
            });
            ReloadBackups();
        }
        catch (Exception ex)
        {
            Log.Error("Registry cleaning failed", ex);
            Step = CleanupStep.Review;
            StatusText = "Cleaning failed: " + ex.Message;
        }
    }

    [RelayCommand]
    private async Task UndoLastAsync()
    {
        if (LastBackupFile is null) return;
        await RestoreFileAsync(LastBackupFile, "the entries this cleanup removed");
    }

    [RelayCommand]
    private async Task RestoreBackupAsync(RegistryBackupViewModel? backup)
    {
        if (backup is null) return;
        await RestoreFileAsync(backup.Info.FilePath, $"the backup \"{backup.Label}\" from {backup.DateText}");
    }

    private async Task RestoreFileAsync(string file, string what)
    {
        if (!Dialogs.Confirm($"Put back {what}?\n\nExisting entries with the same names are overwritten with the backed-up values.")) return;
        StatusText = "Restoring…";
        var (ok, message) = await RegistryBackupService.RestoreAsync(file);
        StatusText = message;
        if (ok) Dialogs.Info(message); else Dialogs.Error(message);
    }

    [RelayCommand]
    private void DeleteBackup(RegistryBackupViewModel? backup)
    {
        if (backup is null) return;
        if (!Dialogs.Confirm($"Delete the backup \"{backup.Label}\" from {backup.DateText}? You will no longer be able to restore it.", destructive: true)) return;
        RegistryBackupService.Delete(backup.Info.FilePath);
        ReloadBackups();
    }

    [RelayCommand]
    private void OpenItem(RegistryIssueItemViewModel? item)
    {
        if (item?.Item.Hive is not { } hive || item.Item.SubKey is null) return;
        Dialogs.OpenRegistryKey(RegFileFormat.KeyPath(hive, item.Item.RegView, item.Item.SubKey));
    }

    [RelayCommand] private void OpenBackupFolder() { System.IO.Directory.CreateDirectory(RegistryBackupService.BackupDir); Dialogs.OpenFolder(RegistryBackupService.BackupDir); }
    [RelayCommand] private void Cancel() { if (Step == CleanupStep.Scanning) _cts?.Cancel(); else if (!IsBusy) RequestClose?.Invoke(); }
    [RelayCommand] private void Rescan() => _ = ScanAsync();
    [RelayCommand] private void ToggleExpand(RegistryGroupViewModel? group) { if (group != null) group.IsExpanded = !group.IsExpanded; }
}
