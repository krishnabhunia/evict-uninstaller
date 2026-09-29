using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Evict.App.Services;
using Evict.App.Views;
using Evict.Core.Models;
using Evict.Core.Services;
using Evict.Core.Util;

namespace Evict.App.ViewModels;

public enum ProgramTab { All, Recent, Large, Infrequent, Bundleware, Broken }

public sealed partial class ProgramsViewModel : ObservableObject, IActivatable
{
    private readonly AppServices _services;
    private readonly MainViewModel _main;
    private CancellationTokenSource? _loadCts;
    private bool _loadedOnce;
    private readonly TaskCompletionSource _firstLoad = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private TaskCompletionSource _fullLoad = new(TaskCreationOptions.RunContinuationsAsynchronously);

    /// <summary>Completes once the program list has been populated at least once (starts a load if needed).</summary>
    public async Task EnsureLoadedAsync()
    {
        if (!_loadedOnce && !IsBusy) _ = RefreshAsync();
        await _firstLoad.Task;
    }

    /// <summary>Completes when sizes, usage data and bundleware flags have been merged in (starts a load if needed).</summary>
    public async Task EnsureFullyLoadedAsync()
    {
        if (!_loadedOnce && !IsBusy) _ = RefreshAsync();
        await _fullLoad.Task;
    }

    /// <summary>Opens the uninstall wizard for one program (used by the widget, context menu and command line).</summary>
    public Task LaunchWizardForAsync(InstalledProgram program) => RunUninstallAsync(new List<InstalledProgram> { program });

    public ProgramsViewModel(AppServices services, MainViewModel main)
    {
        _services = services;
        _main = main;
        Items = new ObservableCollection<ProgramItemViewModel>();
        ProgramsView = CollectionViewSource.GetDefaultView(Items);
        ProgramsView.Filter = FilterItem;
        ProgramsView.SortDescriptions.Add(new SortDescription(nameof(ProgramItemViewModel.Name), ListSortDirection.Ascending));
    }

    public ObservableCollection<ProgramItemViewModel> Items { get; }
    public ICollectionView ProgramsView { get; }

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private double _progress;
    [ObservableProperty] private bool _progressIndeterminate = true;
    [ObservableProperty] private string _searchText = "";
    [ObservableProperty] private ProgramTab _selectedTab = ProgramTab.All;
    [ObservableProperty] private ProgramItemViewModel? _selectedItem;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private bool? _allSelected = false;
    [ObservableProperty] private bool _showDetails = true;
    [ObservableProperty] private string _summaryText = "";

    public int CountAll => Items.Count;
    public int CountRecent => Items.Count(i => InstalledProgramsService.IsRecentlyInstalled(i.Program, Settings.RecentlyInstalledDays));
    public int CountLarge => Items.Count(i => InstalledProgramsService.IsLarge(i.Program, Settings.LargeProgramThresholdMb));
    public int CountInfrequent => Items.Count(i => InstalledProgramsService.IsInfrequentlyUsed(i.Program, Settings.InfrequentlyUsedDays));
    public int CountBundleware => Items.Count(i => i.Program.IsBundleSuspect);
    public int CountBroken => Items.Count(i => i.Program.IsBrokenEntry);
    public int VisibleCount => ProgramsView.Cast<object>().Count();

    public string TabDescription => SelectedTab switch
    {
        ProgramTab.Recent => $"Programs installed in the last {Settings.RecentlyInstalledDays} days.",
        ProgramTab.Large => $"Programs using {Settings.LargeProgramThresholdMb} MB or more of disk space.",
        ProgramTab.Infrequent => $"Programs not launched for {Settings.InfrequentlyUsedDays}+ days (based on Windows usage counters – a heuristic).",
        ProgramTab.Bundleware => "Programs installed within minutes of another vendor's program – often unwanted 'bundled offers'. Review before removing.",
        ProgramTab.Broken => "Entries whose install folder and uninstaller no longer exist. Remove the entry or run a Force Uninstall to clean leftovers.",
        _ => "All programs registered in Programs & Features (64-bit, 32-bit and per-user).",
    };

    private AppSettings Settings => _services.Settings.Current;

    public void OnActivated()
    {
        var settings = _main.GetPage<SettingsViewModel>(PageKey.Settings);
        if (!_loadedOnce || settings.ProgramsChanged)
        {
            settings.ProgramsChanged = false;
            _ = RefreshAsync();
        }
    }

    partial void OnSearchTextChanged(string value) => ApplyFilter();
    partial void OnIsBusyChanged(bool value) => UninstallSelectedCommand.NotifyCanExecuteChanged();
    partial void OnSelectedItemChanged(ProgramItemViewModel? value) => value?.EnsureIcon();
    partial void OnSelectedTabChanged(ProgramTab value)
    {
        ApplyFilter();
        OnPropertyChanged(nameof(TabDescription));
    }

    private void ApplyFilter()
    {
        ProgramsView.Refresh();
        OnPropertyChanged(nameof(VisibleCount));
        UpdateSummary();
    }

    private bool FilterItem(object o)
    {
        if (o is not ProgramItemViewModel item) return false;
        if (!item.Matches(SearchText)) return false;
        return SelectedTab switch
        {
            ProgramTab.Recent => InstalledProgramsService.IsRecentlyInstalled(item.Program, Settings.RecentlyInstalledDays),
            ProgramTab.Large => InstalledProgramsService.IsLarge(item.Program, Settings.LargeProgramThresholdMb),
            ProgramTab.Infrequent => InstalledProgramsService.IsInfrequentlyUsed(item.Program, Settings.InfrequentlyUsedDays),
            ProgramTab.Bundleware => item.Program.IsBundleSuspect,
            ProgramTab.Broken => item.Program.IsBrokenEntry,
            _ => true,
        };
    }

    [RelayCommand]
    public async Task RefreshAsync()
    {
        _loadCts?.Cancel();
        var cts = _loadCts = new CancellationTokenSource();
        if (_fullLoad.Task.IsCompleted) _fullLoad = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        IsBusy = true;
        ProgressIndeterminate = true;
        StatusText = "Loading programs…";
        try
        {
            var opts = new ProgramsQueryOptions
            {
                IncludeSystemComponents = Settings.ShowSystemComponents,
                IncludeUpdates = Settings.ShowWindowsUpdatesInPrograms,
                MeasureMissingSizes = Settings.MeasureFolderSizes,
                ReadUsageData = true,
            };
            var progress = new Progress<ProgressReport>(r =>
            {
                StatusText = r.Message;
                if (r.Percent is { } p) { ProgressIndeterminate = false; Progress = p; }
            });

            // Phase 1: fast registry read so the list appears immediately.
            var quick = await Task.Run(() => _services.Programs.Enumerate(new ProgramsQueryOptions { IncludeSystemComponents = opts.IncludeSystemComponents, IncludeUpdates = opts.IncludeUpdates }), cts.Token);
            if (cts.IsCancellationRequested) return;
            Populate(quick);
            _loadedOnce = true;
            _firstLoad.TrySetResult();

            // Phase 2: sizes + usage + bundleware, then refresh computed columns in place.
            var full = await _services.Programs.GetProgramsAsync(opts, progress, cts.Token);
            if (cts.IsCancellationRequested) return;
            MergeEnriched(full);
            StatusText = "";
            _fullLoad.TrySetResult();
        }
        catch (OperationCanceledException) { /* superseded */ }
        catch (Exception ex)
        {
            StatusText = "Failed to load programs: " + ex.Message;
            Log.Error("Programs load failed", ex);
            _firstLoad.TrySetResult();
            _fullLoad.TrySetResult();
        }
        finally
        {
            if (_loadCts == cts) IsBusy = false;
        }
    }

    private void Populate(List<InstalledProgram> programs)
    {
        var selected = Items.Where(i => i.IsSelected).Select(i => i.Program.Id).ToHashSet();
        foreach (var old in Items) old.PropertyChanged -= ItemOnPropertyChanged;
        Items.Clear();
        foreach (var p in programs)
        {
            var vm = new ProgramItemViewModel(p, _services.Icons) { IsSelected = selected.Contains(p.Id) };
            vm.PropertyChanged += ItemOnPropertyChanged;
            Items.Add(vm);
        }
        RaiseCounts();
        ApplyFilter();
        UpdateSelectionState();
    }

    private void MergeEnriched(List<InstalledProgram> enriched)
    {
        var byId = enriched.ToDictionary(p => p.Id);
        foreach (var item in Items.ToList())
        {
            if (byId.TryGetValue(item.Program.Id, out var e))
            {
                item.Program.SizeBytes = e.SizeBytes;
                item.Program.SizeIsMeasured = e.SizeIsMeasured;
                item.Program.LastUsed = e.LastUsed;
                item.Program.RunCount = e.RunCount;
                item.Program.IsBundleSuspect = e.IsBundleSuspect;
                item.Program.IsKnownBundleware = e.IsKnownBundleware;
                item.Program.BundleGroupNote = e.BundleGroupNote;
                item.RefreshComputed();
            }
        }
        RaiseCounts();
        ApplyFilter();
    }

    private void RaiseCounts()
    {
        OnPropertyChanged(nameof(CountAll));
        OnPropertyChanged(nameof(CountRecent));
        OnPropertyChanged(nameof(CountLarge));
        OnPropertyChanged(nameof(CountInfrequent));
        OnPropertyChanged(nameof(CountBundleware));
        OnPropertyChanged(nameof(CountBroken));
    }

    private void UpdateSummary()
    {
        var visible = ProgramsView.Cast<ProgramItemViewModel>().ToList();
        long bytes = visible.Sum(v => v.Program.SizeBytes ?? 0);
        SummaryText = $"{visible.Count} programs · {SizeFormatter.Format(bytes)}";
    }

    private void ItemOnPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(ProgramItemViewModel.IsSelected)) UpdateSelectionState();
    }

    private bool _updatingSelection;
    private void UpdateSelectionState()
    {
        if (_updatingSelection) return;
        _updatingSelection = true;
        try
        {
            SelectedCount = Items.Count(i => i.IsSelected);
            var visible = ProgramsView.Cast<ProgramItemViewModel>().ToList();
            int sel = visible.Count(v => v.IsSelected);
            AllSelected = visible.Count == 0 ? false : sel == 0 ? false : sel == visible.Count ? true : null;
            UninstallSelectedCommand.NotifyCanExecuteChanged();
        }
        finally { _updatingSelection = false; }
    }

    partial void OnAllSelectedChanged(bool? value)
    {
        if (_updatingSelection || value is null) return;
        _updatingSelection = true;
        try
        {
            foreach (var v in ProgramsView.Cast<ProgramItemViewModel>()) v.IsSelected = value.Value;
        }
        finally { _updatingSelection = false; }
        UpdateSelectionState();
    }

    public IReadOnlyList<ProgramItemViewModel> SelectedItems => Items.Where(i => i.IsSelected).ToList();

    private bool CanUninstallSelected() => SelectedCount > 0 && !IsBusy;

    [RelayCommand(CanExecute = nameof(CanUninstallSelected))]
    private async Task UninstallSelectedAsync()
    {
        var programs = SelectedItems.Select(i => i.Program).ToList();
        await RunUninstallAsync(programs);
    }

    [RelayCommand]
    private async Task UninstallOneAsync(ProgramItemViewModel? item)
    {
        var target = item ?? SelectedItem;
        if (target is null) return;
        await RunUninstallAsync(new List<InstalledProgram> { target.Program });
    }

    private async Task RunUninstallAsync(List<InstalledProgram> programs)
    {
        if (programs.Count == 0) return;
        var wizard = new UninstallWizardViewModel(_services, programs);
        var window = new UninstallWizardWindow { DataContext = wizard, Owner = System.Windows.Application.Current.MainWindow };
        window.ShowDialog();
        if (wizard.AnythingChanged)
        {
            foreach (var i in Items) i.IsSelected = false;
            await RefreshAsync();
            _main.GetPage<HistoryViewModel>(PageKey.History).Reload();
        }
    }

    [RelayCommand]
    private void ForceUninstall(ProgramItemViewModel? item)
    {
        var target = item ?? SelectedItem;
        if (target is null) return;
        var vm = new ForceUninstallViewModel(_services, target.Program, Items.Select(i => i.Program).ToList());
        var window = new ForceUninstallWindow { DataContext = vm, Owner = System.Windows.Application.Current.MainWindow };
        window.ShowDialog();
        if (vm.AnythingChanged)
        {
            _ = RefreshAsync();
            _main.GetPage<HistoryViewModel>(PageKey.History).Reload();
        }
    }

    [RelayCommand]
    private void RemoveEntry(ProgramItemViewModel? item)
    {
        var target = item ?? SelectedItem;
        if (target is null) return;
        if (!Dialogs.Confirm($"Remove the Programs & Features entry for \"{target.Name}\"?\n\nThis only deletes the registry entry – no files are touched. Use Force Uninstall to remove leftover files as well.", destructive: true))
            return;
        if (UninstallRunner.DeleteRegistryEntry(target.Program, out var error))
        {
            _services.History.Add(new UninstallHistoryEntry
            {
                ProgramName = target.Name, Publisher = target.Program.Publisher, Version = target.Program.DisplayVersion,
                Method = UninstallMethod.RegistryEntryOnly, Succeeded = true, Notes = "Registry entry removed", InstallLocation = target.Program.InstallLocation,
            });
            _ = RefreshAsync();
            _main.GetPage<HistoryViewModel>(PageKey.History).Reload();
        }
        else
        {
            Dialogs.Error("Could not remove the entry: " + error + (ElevationHelper.IsElevated ? "" : "\n\nTry restarting Evict as administrator."));
        }
    }

    [RelayCommand]
    private void OpenInstallFolder(ProgramItemViewModel? item) => Dialogs.OpenFolder((item ?? SelectedItem)?.Program.InstallLocation);

    [RelayCommand]
    private void OpenRegistryKey(ProgramItemViewModel? item)
    {
        var target = item ?? SelectedItem;
        if (target != null) Dialogs.OpenRegistryKey(target.Program.RegistryPath);
    }

    [RelayCommand]
    private void CopyUninstallString(ProgramItemViewModel? item) => Dialogs.CopyToClipboard((item ?? SelectedItem)?.Program.UninstallString);

    [RelayCommand]
    private void OpenWebsite(ProgramItemViewModel? item)
    {
        var target = item ?? SelectedItem;
        if (target is null) return;
        var url = target.Program.UrlInfoAbout ?? target.Program.HelpLink;
        if (string.IsNullOrWhiteSpace(url)) url = "https://www.google.com/search?q=" + Uri.EscapeDataString(target.Name + " " + (target.Program.Publisher ?? ""));
        Dialogs.OpenUrl(url);
    }

    [RelayCommand]
    private void SearchWeb(ProgramItemViewModel? item)
    {
        var target = item ?? SelectedItem;
        if (target is null) return;
        Dialogs.OpenUrl("https://www.google.com/search?q=" + Uri.EscapeDataString("what is " + target.Name + " " + (target.Program.Publisher ?? "")));
    }

    [RelayCommand]
    private void ClearSelection()
    {
        foreach (var i in Items) i.IsSelected = false;
    }

    [RelayCommand]
    private void ToggleDetails() => ShowDetails = !ShowDetails;

    [RelayCommand]
    private void SelectTab(string? tab)
    {
        if (Enum.TryParse<ProgramTab>(tab, out var t)) SelectedTab = t;
    }
}
