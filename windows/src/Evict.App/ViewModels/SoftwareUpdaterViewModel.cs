using System.Collections.ObjectModel;
using System.ComponentModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Evict.App.Services;
using Evict.Core.Models;
using Evict.Core.Services;

namespace Evict.App.ViewModels;

public sealed partial class UpgradeItemViewModel : ObservableObject
{
    public UpgradeItemViewModel(UpgradablePackage pkg) => Package = pkg;
    public UpgradablePackage Package { get; }
    [ObservableProperty] private bool _isSelected = true;
    [ObservableProperty] private string _status = "";
    [ObservableProperty] private bool _isUpdating;
    public string Name => Package.Name;
    public string Id => Package.Id;
    public string Installed => Package.InstalledVersion ?? "—";
    public string Available => Package.AvailableVersion ?? "—";
    public string Source => Package.Source ?? "";
}

public sealed partial class SoftwareUpdaterViewModel : ObservableObject, IActivatable
{
    private readonly AppServices _services;
    private bool _loaded;
    private IReadOnlyList<UpgradablePackage> _lastPackages = Array.Empty<UpgradablePackage>();
    public ObservableCollection<SoftwareUpdateExclusion> Exclusions { get; } = new();
    public string ExclusionsLabel => $"Excluded software ({Exclusions.Count})";
    public bool CanEditSelection => !IsBusy && !IsUpdating;

    private CancellationTokenSource? _cts;

    public SoftwareUpdaterViewModel(AppServices services)
    {
        _services = services;
        History = new SoftwareUpdateHistoryViewModel(services.SoftwareUpdateHistory,
            CheckLatestFromHistoryAsync, () => CanEditSelection, () => SelectedTabIndex = 0);
        WingetAvailable = WingetService.IsAvailable;
        foreach (var exclusion in services.Settings.Current.SoftwareUpdateExclusions ?? new())
            Exclusions.Add(exclusion);
    }

    public SoftwareUpdateHistoryViewModel History { get; }
    [ObservableProperty] private int _selectedTabIndex;
    [ObservableProperty] private bool _hasCompletedSession;
    [ObservableProperty] private string _batchResultSummary = "";
    public ObservableCollection<UpgradeItemViewModel> Items { get; } = new();
    public ObservableCollection<string> LogLines { get; } = new();

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private bool _isUpdating;
    [ObservableProperty] private string _statusText = "";
    [ObservableProperty] private bool _wingetAvailable;
    [ObservableProperty] private bool _includeUnknown;
    [ObservableProperty] private int _selectedCount;
    [ObservableProperty] private string? _error;
    [ObservableProperty] private DateTime? _lastChecked;

    public string Summary => IsBusy ? "Checking for updates." : Error != null ? "Updates could not be checked."
        : Items.Count == 0 ? (LastChecked is null ? "Not checked yet." : (Exclusions.Count > 0 ? "No updates available outside your exclusions." : "Everything is up to date.")) : $"{Items.Count} update(s) available";

    /// <summary>How many updates run concurrently (setting). MSI-based installers still serialise themselves; winget retries those.</summary>
    public IReadOnlyList<KeyValuePair<int, string>> ParallelOptions { get; } = new[]
    {
        new KeyValuePair<int, string>(1, "1 at a time"),
        new KeyValuePair<int, string>(2, "2 at a time"),
        new KeyValuePair<int, string>(3, "3 at a time"),
        new KeyValuePair<int, string>(4, "4 at a time"),
        new KeyValuePair<int, string>(6, "6 at a time"),
    };
    public int ParallelUpdates
    {
        get => Math.Clamp(_services.Settings.Current.ParallelUpdates, 1, 6);
        set { _services.Settings.Current.ParallelUpdates = Math.Clamp(value, 1, 6); _services.Settings.Save(); OnPropertyChanged(); }
    }
    [ObservableProperty] private int _doneCount;
    [ObservableProperty] private int _totalCount;
    [ObservableProperty] private double _overallProgress;
    public string LastCheckedText => LastChecked is { } d ? "Last checked " + ProgramItemViewModel.Relative(d) : "";

    public void OnActivated()
    {
        History.Refresh();
        WingetAvailable = WingetService.IsAvailable;
        if (!_loaded && WingetAvailable) _ = CheckAsync();
    }

    partial void OnIsBusyChanged(bool value) { RefreshCommands(); OnPropertyChanged(nameof(Summary)); }
    partial void OnIsUpdatingChanged(bool value) => RefreshCommands();

    private void RefreshCommands()
    {
        OnPropertyChanged(nameof(CanEditSelection));
        UpdateSelectedCommand.NotifyCanExecuteChanged();
        SelectAllCommand.NotifyCanExecuteChanged();
        SelectNoneCommand.NotifyCanExecuteChanged();
        ExcludeSelectedCommand.NotifyCanExecuteChanged();
        ManageExclusionsCommand.NotifyCanExecuteChanged();
        CancelUpdateCommand.NotifyCanExecuteChanged();
    }

    partial void OnErrorChanged(string? value) => OnPropertyChanged(nameof(Summary));

    [RelayCommand]
    public async Task CheckAsync()
    {
        if (!WingetAvailable || IsBusy || IsUpdating) return;
        IsBusy = true;
        Error = null;
        StatusText = "Checking for updates with winget… (this can take a minute)";
        try
        {
            var (pkgs, err) = await _services.Winget.GetUpgradesAsync(IncludeUnknown, CancellationToken.None);
            _lastPackages = pkgs.ToList();
            RebuildItems();
            Error = err;
            if (err is null) LastChecked = DateTime.Now;
            _loaded = err is null;
            UpdateSelection();
            StatusText = "";
        }
        catch (Exception ex) { Error = ex.Message; }
        finally
        {
            IsBusy = false;
            OnPropertyChanged(nameof(Summary));
            OnPropertyChanged(nameof(LastCheckedText));
        }
    }

    private void ItemChanged(object? s, PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(UpgradeItemViewModel.IsSelected)) UpdateSelection();
    }

    private void UpdateSelection()
    {
        SelectedCount = Items.Count(i => i.IsSelected);
        UpdateSelectedCommand.NotifyCanExecuteChanged();
        ExcludeSelectedCommand.NotifyCanExecuteChanged();
    }

    private void RebuildItems()
    {
        var previous = Items.ToList();
        foreach (var item in previous) item.PropertyChanged -= ItemChanged;
        Items.Clear();
        foreach (var package in SoftwareUpdateExclusion.Visible(_lastPackages, Exclusions))
        {
            var old = previous.FirstOrDefault(i => i.Id == package.Id && i.Source == (package.Source ?? ""));
            var item = new UpgradeItemViewModel(package) { IsSelected = old?.IsSelected ?? true };
            item.PropertyChanged += ItemChanged;
            Items.Add(item);
        }
        UpdateSelection();
        OnPropertyChanged(nameof(Summary));
    }

    private bool SaveExclusions(List<SoftwareUpdateExclusion> updated)
    {
        var previous = _services.Settings.Current.SoftwareUpdateExclusions;
        _services.Settings.Current.SoftwareUpdateExclusions = updated;
        if (!_services.Settings.TrySave())
        {
            _services.Settings.Current.SoftwareUpdateExclusions = previous;
            Error = "Could not save exclusions. Check that Evict's settings folder is writable and try again.";
            return false;
        }
        Exclusions.Clear();
        foreach (var exclusion in updated) Exclusions.Add(exclusion);
        OnPropertyChanged(nameof(ExclusionsLabel));
        RebuildItems();
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private void ExcludeSelected()
    {
        if (!CanUpdate()) return;
        SaveExclusions(SoftwareUpdateExclusion.Add(Exclusions, Items.Where(i => i.IsSelected).Select(i => i.Package)));
    }

    public bool RestoreExclusions(IEnumerable<SoftwareUpdateExclusion> selected)
    {
        if (!CanEditSelection) return false;
        var restoring = selected.ToHashSet();
        return SaveExclusions(Exclusions.Where(e => !restoring.Contains(e)).ToList());
    }

    [RelayCommand(CanExecute = nameof(CanEditSelection))]
    private void ManageExclusions()
    {
        if (!CanEditSelection) return;
        new Evict.App.Views.SoftwareUpdateExclusionsWindow
        {
            DataContext = this, Owner = System.Windows.Application.Current.MainWindow
        }.ShowDialog();
    }

    private bool CanUpdate() => SelectedCount > 0 && !IsBusy && !IsUpdating;

    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private async Task UpdateSelectedAsync()
    {
        if (!CanUpdate()) return;
        var targets = Items.Where(i => i.IsSelected).ToList();
        if (targets.Count == 0) return;
        using var cancellation = new CancellationTokenSource();
        _cts = cancellation;
        IsUpdating = true;
        HasCompletedSession = false;
        LogLines.Clear();
        TotalCount = targets.Count; DoneCount = 0; OverallProgress = 0;
        var ui = System.Windows.Application.Current.Dispatcher;
        var latest = new Dictionary<Guid, SoftwareUpdateAttempt>();
        void ApplyProgress(SoftwareUpdateAttempt attempt)
        {
            if (latest.TryGetValue(attempt.Id, out var previous)
                && previous.Outcome != SoftwareUpdateOutcome.InProgress
                && attempt.Outcome == SoftwareUpdateOutcome.InProgress) return;
            latest[attempt.Id] = attempt;
            var identity = new SoftwareUpdateExclusion(attempt.PackageId, attempt.Name, attempt.Source);
            var item = targets.FirstOrDefault(t => identity.Matches(t.Package));
            if (item != null)
            {
                item.IsUpdating = attempt.Outcome == SoftwareUpdateOutcome.InProgress;
                item.Status = attempt.Message;
                if (attempt.Outcome is SoftwareUpdateOutcome.Updated or SoftwareUpdateOutcome.RestartRequired) item.IsSelected = false;
            }
            DoneCount = latest.Values.Count(r => r.Outcome != SoftwareUpdateOutcome.InProgress);
            OverallProgress = 100.0 * DoneCount / Math.Max(1, TotalCount);
            StatusText = $"Updating… {DoneCount} of {TotalCount} finished";
        }
        foreach (var item in targets) { item.Status = "Waiting…"; item.IsUpdating = false; }
        StatusText = $"Updating {targets.Count} program(s)…";
        bool anyUpdated = false;
        App.Background?.SuspendDetection(true);
        try
        {
            var session = new SoftwareUpdateSession(_services.SoftwareUpdateHistory, _services.Winget);
            var results = await session.RunAsync(targets.Select(t => t.Package).ToArray(),
                ParallelUpdates, IncludeUnknown, cancellation.Token,
                attempt => { _ = ui.BeginInvoke(new Action(() => ApplyProgress(attempt))); },
                (package, line) =>
                {
                    if (string.IsNullOrWhiteSpace(line)) return;
                    var clean = line.Trim();
                    if (clean.All(c => c is '-' or '\\' or '|' or '/' or ' ' or '█' or '▒' or '▓')) return;
                    _ = ui.BeginInvoke(new Action(() =>
                    {
                        LogLines.Add($"[{package.Name}] {clean}");
                        if (LogLines.Count > 600) LogLines.RemoveAt(0);
                    }));
                });
            foreach (var result in results) ApplyProgress(result);
            int updated = results.Count(r => r.Outcome == SoftwareUpdateOutcome.Updated);
            int restart = results.Count(r => r.Outcome == SoftwareUpdateOutcome.RestartRequired);
            int failed = results.Count(r => r.Outcome == SoftwareUpdateOutcome.Failed);
            int cancelled = results.Count(r => r.Outcome == SoftwareUpdateOutcome.Cancelled);
            int interrupted = results.Count(r => r.Outcome == SoftwareUpdateOutcome.Interrupted);
            BatchResultSummary = $"{updated} updated · {restart} restart required · {failed} failed · {cancelled} cancelled · {interrupted} interrupted";
            StatusText = BatchResultSummary;
            HasCompletedSession = true;
            anyUpdated = updated + restart > 0;
        }
        finally
        {
            IsUpdating = false;
            foreach (var item in targets) item.IsUpdating = false;
            _cts = null;
            History.Refresh();
            App.Background?.SuspendDetection(false);
        }
        if (anyUpdated) await CheckAsync();
    }

    [RelayCommand] private void ShowUpdateHistory() { SelectedTabIndex = 1; History.Refresh(); }

    private async Task CheckLatestFromHistoryAsync(SoftwareUpdateAttempt attempt)
    {
        if (!CanEditSelection) return;
        SelectedTabIndex = 0;
        await CheckAsync();
        var identity = new SoftwareUpdateExclusion(attempt.PackageId, attempt.Name, attempt.Source);
        foreach (var item in Items) item.IsSelected = identity.Matches(item.Package);
        if (Error != null) return;
        StatusText = Exclusions.Any(e => e.Matches(attempt.PackageId, attempt.Source))
            ? "This program is excluded. Restore it from Excluded software to include it in a new check."
            : Items.Any(i => i.IsSelected)
                ? "Review the latest available version, then select Update. No update has been started."
                : "No applicable update was found for this program.";
    }

    [RelayCommand(CanExecute = nameof(IsUpdating))]
    private void CancelUpdate()
    {
        if (!IsUpdating || !Dialogs.Confirm("Queued updates will not start. Installers already running may continue until they finish.\n\n" +
            "Their final results will be kept in history. If a result cannot be confirmed, the attempt will be marked Interrupted.",
            "Cancel remaining updates?")) return;
        _cts?.Cancel();
    }

    [RelayCommand(CanExecute = nameof(CanEditSelection))]
    private void SelectAll()
    {
        if (!CanEditSelection) return;
        foreach (var i in Items) i.IsSelected = true;
    }

    [RelayCommand(CanExecute = nameof(CanEditSelection))]
    private void SelectNone()
    {
        if (!CanEditSelection) return;
        foreach (var i in Items) i.IsSelected = false;
    }

    [RelayCommand]
    private void InstallWinget() => Dialogs.OpenUrl("ms-windows-store://pdp/?ProductId=9NBLGGH4NNS1");

    [RelayCommand]
    private void OpenPackagePage(UpgradeItemViewModel? item)
    {
        if (item is null) return;
        Dialogs.OpenUrl("https://winget.run/pkg/" + item.Id.Replace('.', '/'));
    }
}
