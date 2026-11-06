using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Windows.Data;
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
    /// <summary>"Failed today 16:41 — why?" from the update history (design SH1·2); empty when there is nothing to say.</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasLastResultNote))]
    private string _lastResultNote = "";
    public bool HasLastResultNote => LastResultNote.Length > 0;
    public Guid? LastHistoryId { get; set; }
    public string Name => Package.Name;
    public string Id => Package.Id;
    public string Installed => Package.InstalledVersion ?? "—";
    public string Available => Package.AvailableVersion ?? "—";
    public string Source => Package.Source ?? "";
}

/// <summary>One row of the Hidden updates window (design VM1): an offer that is not listed, or an exclusion.</summary>
public sealed class HiddenUpdateRowViewModel
{
    public HiddenUpdateRowViewModel(int srNo, HiddenUpdate? hidden, SoftwareUpdateExclusion? exclusion)
    {
        SrNo = srNo;
        Hidden = hidden;
        Exclusion = exclusion;
    }

    public int SrNo { get; }
    public HiddenUpdate? Hidden { get; }
    public SoftwareUpdateExclusion? Exclusion { get; }
    public HiddenUpdateReason Reason => Hidden?.Reason ?? HiddenUpdateReason.Excluded;
    public string Name => Hidden?.Package.Name ?? Exclusion?.Name ?? "";
    public string Id => Hidden?.Package.Id ?? Exclusion?.Id ?? "";
    public string Source => Hidden?.Package.Source ?? Exclusion?.Source ?? "";
    public string Versions => Hidden is { } h && Reason != HiddenUpdateReason.Excluded
        ? $"{h.Package.InstalledVersion} → {h.Package.AvailableVersion}" : "All versions";
    public string ReasonText => SoftwareUpdateFilter.ReasonText(Reason);
    public string ReasonDetail => Hidden is { } h ? SoftwareUpdateFilter.ReasonDetail(h) : SoftwareUpdateFilter.ReasonDetail(new HiddenUpdate(new UpgradablePackage { Id = Id, Name = Name }, HiddenUpdateReason.Excluded, null));
    public string ComesBack => SoftwareUpdateFilter.ComesBackText(Reason);
    /// <summary>Filter group in the window: "Already up to date", "Skipped versions" or "Excluded".</summary>
    public string Group => Reason switch
    {
        HiddenUpdateReason.SkippedVersion => SoftwareUpdaterViewModel.HiddenFilterSkipped,
        HiddenUpdateReason.Excluded => SoftwareUpdaterViewModel.HiddenFilterExcluded,
        _ => SoftwareUpdaterViewModel.HiddenFilterUpToDate,
    };
}

public sealed partial class SoftwareUpdaterViewModel : ObservableObject, IActivatable
{
    private readonly AppServices _services;
    private bool _loaded;
    private IReadOnlyList<UpgradablePackage> _lastPackages = Array.Empty<UpgradablePackage>();
    public ObservableCollection<SoftwareUpdateExclusion> Exclusions { get; } = new();
    public string ExclusionsLabel => $"Excluded software ({Exclusions.Count})";

    // ── Hidden updates (design VM1, option B) ──
    public const string HiddenFilterAll = "All";
    public const string HiddenFilterUpToDate = "Already up to date";
    public const string HiddenFilterSkipped = "Skipped versions";
    public const string HiddenFilterExcluded = "Excluded";
    public IReadOnlyList<string> HiddenFilters { get; } = new[] { HiddenFilterAll, HiddenFilterUpToDate, HiddenFilterSkipped, HiddenFilterExcluded };
    public ObservableCollection<HiddenUpdateRowViewModel> HiddenItems { get; } = new();
    public string HiddenLabel => $"Hidden updates ({HiddenItems.Count})";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasHiddenSummary))]
    private string? _hiddenSummary;
    public bool HasHiddenSummary => !string.IsNullOrEmpty(HiddenSummary);
    [ObservableProperty] private string _hiddenFilter = HiddenFilterAll;
    partial void OnHiddenFilterChanged(string value) => CollectionViewSource.GetDefaultView(HiddenItems).Refresh();

    /// <summary>"IDM installed fine, but Windows still reports 6.43.18 …" after an update that changed nothing (rule 1).</summary>
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasUnchangedNotice))]
    private string? _unchangedNotice;
    public bool HasUnchangedNotice => !string.IsNullOrEmpty(UnchangedNotice);
    private List<UpgradablePackage> _lastUnchanged = new();
    public bool CanEditSelection => !IsBusy && !IsUpdating;

    private CancellationTokenSource? _cts;

    public SoftwareUpdaterViewModel(AppServices services)
    {
        _services = services;
        WingetAvailable = WingetService.IsAvailable;
        foreach (var exclusion in services.Settings.Current.SoftwareUpdateExclusions ?? new())
            Exclusions.Add(exclusion);
        CollectionViewSource.GetDefaultView(HiddenItems).Filter = o =>
            HiddenFilter == HiddenFilterAll || (o is HiddenUpdateRowViewModel row && row.Group == HiddenFilter);
        HookHistory();
        RebuildItems();
    }

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
        : Items.Count == 0 ? (LastChecked is null ? "Not checked yet." : (HiddenItems.Count > 0 ? "No updates available outside your hidden updates." : "Everything is up to date.")) : $"{Items.Count} update(s) available";

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
        WingetAvailable = WingetService.IsAvailable;
        // Settings → Software Updater toggles apply when the page is shown again.
        if (!IsBusy && !IsUpdating) RebuildItems();
        if (!_loaded && WingetAvailable) _ = CheckAsync();
    }

    partial void OnIsBusyChanged(bool value) { RefreshCommands(); OnPropertyChanged(nameof(Summary)); }
    partial void OnIsUpdatingChanged(bool value) => RefreshCommands();

    private void RefreshCommands()
    {
        OnPropertyChanged(nameof(CanEditSelection));
        OnPropertyChanged(nameof(CanRetrySelected));
        UpdateSelectedCommand.NotifyCanExecuteChanged();
        SelectAllCommand.NotifyCanExecuteChanged();
        SelectNoneCommand.NotifyCanExecuteChanged();
        ExcludeSelectedCommand.NotifyCanExecuteChanged();
        SkipSelectedVersionsCommand.NotifyCanExecuteChanged();
        ManageExclusionsCommand.NotifyCanExecuteChanged();
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
            // A decision about an offer ends when winget offers other versions for that package (design VM1).
            if (err is null)
            {
                var records = HiddenRecords;
                var pruned = SoftwareUpdateFilter.Prune(records, _lastPackages);
                if (pruned.Count != records.Count) SaveHiding(null, pruned);
            }
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
        SkipSelectedVersionsCommand.NotifyCanExecuteChanged();
    }

    private List<HiddenUpdateRecord> HiddenRecords => _services.Settings.Current.HiddenSoftwareUpdates ?? new();

    /// <summary>The same classification the Health tile uses: exclusions, skipped versions and the automatic rules.</summary>
    private SoftwareUpdateView Classify() => SoftwareUpdateFilter.Classify(_lastPackages, Exclusions, HiddenRecords,
        SoftwareUpdateHidingOptions.From(_services.Settings.Current));

    private void RebuildItems()
    {
        var previous = Items.ToList();
        foreach (var item in previous) item.PropertyChanged -= ItemChanged;
        Items.Clear();
        var view = Classify();
        foreach (var package in view.Visible)
        {
            var old = previous.FirstOrDefault(i => i.Id == package.Id && i.Source == (package.Source ?? ""));
            var item = new UpgradeItemViewModel(package) { IsSelected = old?.IsSelected ?? true };
            item.PropertyChanged += ItemChanged;
            Items.Add(item);
        }
        RebuildHidden(view);
        UpdateSelection();
        OnPropertyChanged(nameof(Summary));
        RebuildHistory();
    }

    private void RebuildHidden(SoftwareUpdateView view)
    {
        HiddenItems.Clear();
        int n = 0;
        foreach (var hidden in view.Hidden.OrderBy(h => h.Reason == HiddenUpdateReason.Excluded).ThenBy(h => h.Package.Name, StringComparer.CurrentCultureIgnoreCase))
            HiddenItems.Add(new HiddenUpdateRowViewModel(++n, hidden,
                hidden.Reason == HiddenUpdateReason.Excluded ? Exclusions.FirstOrDefault(e => e.Matches(hidden.Package)) : null));
        // Exclusions winget does not offer right now still belong in the window, so they can be restored.
        int notOffered = 0;
        foreach (var exclusion in Exclusions.Where(e => !view.Hidden.Any(h => h.Reason == HiddenUpdateReason.Excluded && e.Matches(h.Package))))
        {
            HiddenItems.Add(new HiddenUpdateRowViewModel(++n, null, exclusion));
            notOffered++;
        }
        HiddenSummary = SoftwareUpdateFilter.Summary(view.Hidden, notOffered);
        OnPropertyChanged(nameof(HiddenLabel));
    }

    private bool SaveExclusions(List<SoftwareUpdateExclusion> updated) => SaveHiding(updated, null);

    /// <summary>Saves exclusions and/or hidden-update records together; a failed write keeps the previous settings.</summary>
    private bool SaveHiding(List<SoftwareUpdateExclusion>? exclusions, List<HiddenUpdateRecord>? records)
    {
        var settings = _services.Settings.Current;
        var previousExclusions = settings.SoftwareUpdateExclusions;
        var previousRecords = settings.HiddenSoftwareUpdates;
        if (exclusions != null) settings.SoftwareUpdateExclusions = exclusions;
        if (records != null) settings.HiddenSoftwareUpdates = records;
        if (!_services.Settings.TrySave())
        {
            settings.SoftwareUpdateExclusions = previousExclusions;
            settings.HiddenSoftwareUpdates = previousRecords;
            Error = "Could not save hidden updates. Check that Evict's settings folder is writable and try again.";
            return false;
        }
        if (exclusions != null)
        {
            Exclusions.Clear();
            foreach (var exclusion in exclusions) Exclusions.Add(exclusion);
            OnPropertyChanged(nameof(ExclusionsLabel));
        }
        RebuildItems();
        return true;
    }

    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private void SkipSelectedVersions()
    {
        if (!CanUpdate()) return;
        SaveHiding(null, SoftwareUpdateFilter.SkipVersions(HiddenRecords, Items.Where(i => i.IsSelected).Select(i => i.Package), DateTime.UtcNow));
    }

    [RelayCommand]
    private void SkipVersion(UpgradeItemViewModel? item)
    {
        if (item is null || !CanEditSelection) return;
        SaveHiding(null, SoftwareUpdateFilter.SkipVersions(HiddenRecords, new[] { item.Package }, DateTime.UtcNow));
    }

    /// <summary>Lists hidden offers again (window "Restore selected" / "Restore all").</summary>
    public bool RestoreHidden(IEnumerable<HiddenUpdateRowViewModel> rows)
    {
        if (!CanEditSelection) return false;
        var selected = rows.ToList();
        var restoreExclusions = selected.Where(r => r.Reason == HiddenUpdateReason.Excluded).Select(r => r.Exclusion).OfType<SoftwareUpdateExclusion>().ToHashSet();
        var records = SoftwareUpdateFilter.Restore(HiddenRecords, selected.Where(r => r.Reason != HiddenUpdateReason.Excluded).Select(r => r.Hidden).OfType<HiddenUpdate>(), DateTime.UtcNow);
        return SaveHiding(restoreExclusions.Count > 0 ? Exclusions.Where(e => !restoreExclusions.Contains(e)).ToList() : null, records);
    }

    [RelayCommand]
    private void UndoUnchanged()
    {
        if (_lastUnchanged.Count == 0 || !CanEditSelection) return;
        var records = SoftwareUpdateFilter.Restore(HiddenRecords,
            _lastUnchanged.Select(p => new HiddenUpdate(p, HiddenUpdateReason.AlreadyUpToDate, null)), DateTime.UtcNow);
        if (SaveHiding(null, records)) DismissUnchanged();
    }

    [RelayCommand]
    private void DismissUnchanged()
    {
        _lastUnchanged = new();
        UnchangedNotice = null;
    }

    /// <summary>
    /// Rule 1 of design VM1: after updates, a fresh check that still offers exactly the same versions means the
    /// update cannot change the installed version, so the offer is treated as already up to date.
    /// </summary>
    private List<UpgradablePackage> HideUnchanged(IReadOnlyList<UpgradablePackage> attempted)
    {
        if (attempted.Count == 0 || Error != null || !_services.Settings.Current.HideUnchangedSoftwareUpdates) return new();
        var unchanged = SoftwareUpdateFilter.FindUnchanged(attempted, _lastPackages);
        if (unchanged.Count == 0) return unchanged;
        if (!SaveHiding(null, SoftwareUpdateFilter.MarkAlreadyUpToDate(HiddenRecords, unchanged, DateTime.UtcNow))) return new();
        _lastUnchanged = unchanged;
        UnchangedNotice = unchanged.Count == 1
            ? $"{unchanged[0].Name} installed fine, but Windows still reports {unchanged[0].InstalledVersion}. Evict now treats it as already up to date and hides the {unchanged[0].AvailableVersion} offer. It comes back by itself when winget offers a different version."
            : $"{string.Join(", ", unchanged.Select(p => p.Name))} installed fine, but their installed versions did not change. Evict now treats them as already up to date; each comes back by itself when winget offers a different version.";
        return unchanged;
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
        var targets = Items.Where(i => i.IsSelected).ToList();
        if (targets.Count == 0) return;
        await RunUpdatesAsync(targets, targets.Count == 1 ? "You · Update 1 program" : $"You · Update {targets.Count} programs");
    }

    /// <summary>Runs winget for <paramref name="targets"/>; every finished one is written to the update history at once (design SH1·4).</summary>
    private async Task RunUpdatesAsync(List<UpgradeItemViewModel> targets, string startedBy)
    {
        if (targets.Count == 0 || IsBusy || IsUpdating) return;
        bool includeUnknown = IncludeUnknown;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsUpdating = true;
        LogLines.Clear();
        int ok = 0;
        var succeeded = new List<UpgradablePackage>();
        TotalCount = targets.Count; DoneCount = 0; OverallProgress = 0;
        int parallel = Math.Min(ParallelUpdates, targets.Count);
        var live = new Dictionary<UpgradeItemViewModel, SoftwareUpdateHistoryEntry>();
        var output = new Dictionary<UpgradeItemViewModel, List<string>>();
        foreach (var t in targets)
        {
            t.Status = "Waiting…"; t.IsUpdating = false; t.LastResultNote = "";
            var entry = new SoftwareUpdateHistoryEntry
            {
                Name = t.Name, PackageId = t.Id, Source = string.IsNullOrEmpty(t.Package.Source) ? "winget" : t.Package.Source,
                FromVersion = t.Package.InstalledVersion, ToVersion = t.Package.AvailableVersion,
                StartedUtc = DateTime.UtcNow, Outcome = SoftwareUpdateOutcome.Waiting, StartedBy = startedBy,
                Message = "Starts when a slot is free.",
            };
            live[t] = entry;
            output[t] = new List<string>();
            _liveEntries[entry.Id] = entry;
        }
        await Task.Run(() => { foreach (var entry in live.Values) _services.UpdateHistory.Add(entry); });
        RebuildHistory();
        StatusText = parallel > 1 ? $"Updating {targets.Count} programs, {parallel} at a time…" : $"Updating {targets.Count} program(s)…";
        var ui = System.Windows.Application.Current.Dispatcher;
        var gate = new SemaphoreSlim(parallel);

        async Task FinishAsync(UpgradeItemViewModel t, bool success, string msg, bool cancelled, bool outcomeKnown = true)
        {
            var entry = live[t];
            await ui.InvokeAsync(() =>
            {
                bool neverStarted = entry.Outcome == SoftwareUpdateOutcome.Waiting;
                var (outcome, message) = SoftwareUpdateHistory.Describe(t.Name, t.Package.InstalledVersion, success, msg, cancelled);
                if (!neverStarted && (cancelled || !outcomeKnown))
                {
                    outcome = SoftwareUpdateOutcome.Interrupted;
                    message = "The installer result could not be confirmed. Check for updates again before retrying.";
                }
                else if (neverStarted && cancelled) message = "Cancelled before this queued update started.";
                entry.Outcome = outcome;
                entry.Message = message;
                entry.FinishedUtc = DateTime.UtcNow;
                lock (output[t]) entry.Output = SoftwareUpdateHistory.TrimOutput(output[t]);
                _liveEntries.Remove(entry.Id);
            });
            await Task.Run(() => _services.UpdateHistory.Add(entry));
        }

        App.Background?.SuspendDetection(true);
        try
        {
            var tasks = targets.Select(async t =>
            {
                bool entered = false;
                try
                {
                    await gate.WaitAsync(ct);
                    entered = true;
                    if (ct.IsCancellationRequested) throw new OperationCanceledException(ct);
                    await ui.InvokeAsync(() =>
                    {
                        t.IsUpdating = true;
                        t.Status = "Updating…";
                        var entry = live[t];
                        entry.StartedUtc = DateTime.UtcNow;
                        entry.Outcome = SoftwareUpdateOutcome.Running;
                        entry.Message = "winget is updating it now.";
                        RebuildHistory();
                        StatusText = $"Updating… {DoneCount} of {TotalCount} done, {targets.Count(i => i.IsUpdating)} running";
                    });
                    await Task.Run(() => _services.UpdateHistory.Add(live[t]));
                    var result = await _services.Winget.UpgradeWithResultAsync(t.Package, ct, line =>
                    {
                        if (string.IsNullOrWhiteSpace(line)) return;
                        var clean = line.Trim();
                        if (clean.All(c => c is '-' or '\\' or '|' or '/' or ' ' or '█' or '▒')) return;
                        lock (output[t]) { output[t].Add(clean); if (output[t].Count > 200) output[t].RemoveAt(0); }
                        ui.BeginInvoke(() => { LogLines.Add($"[{t.Name}] {clean}"); if (LogLines.Count > 600) LogLines.RemoveAt(0); });
                    }, includeUnknown: includeUnknown);
                    bool success = result.Ok;
                    string msg = result.Message;
                    await ui.InvokeAsync(() =>
                    {
                        t.IsUpdating = false;
                        t.Status = msg;
                        if (success)
                        {
                            ok++; t.IsSelected = false;
                            // A restart-pending install may only show its new version after Windows restarts: never judge it as unchanged.
                            if (!WingetService.IsRestartToFinish(msg)) succeeded.Add(t.Package);
                        }
                        DoneCount++;
                        OverallProgress = 100.0 * DoneCount / Math.Max(1, TotalCount);
                        StatusText = $"Updating… {DoneCount} of {TotalCount} done, {targets.Count(i => i.IsUpdating)} running";
                    });
                    await FinishAsync(t, success, msg, cancelled: false, outcomeKnown: result.OutcomeKnown);
                }
                catch (OperationCanceledException)
                {
                    await ui.InvokeAsync(() => { t.IsUpdating = false; t.Status = "Cancelled."; DoneCount++; OverallProgress = 100.0 * DoneCount / Math.Max(1, TotalCount); });
                    await FinishAsync(t, false, "Cancelled.", true);
                }
                catch (Exception ex)
                {
                    await ui.InvokeAsync(() => { t.IsUpdating = false; t.Status = "Failed: " + ex.Message; DoneCount++; OverallProgress = 100.0 * DoneCount / Math.Max(1, TotalCount); });
                    await FinishAsync(t, false, ex.Message, false, outcomeKnown: false);
                }
                finally { if (entered) gate.Release(); }
            }).ToList();
            await Task.WhenAll(tasks);
            StatusText = SoftwareUpdateHistory.Summary(live.Values.ToList()) ?? "No update results were recorded.";
        }
        catch (OperationCanceledException) { StatusText = "Cancelled."; }
        finally
        {
            _cts?.Dispose();
            _cts = null;
            gate.Dispose();
            IsUpdating = false;
            foreach (var t in Items) t.IsUpdating = false;
            foreach (var t in targets) _liveEntries.Remove(live[t].Id);
            App.Background?.SuspendDetection(false);
            RebuildHistory();
        }
        if (ok > 0)
        {
            await CheckAsync();
            var unchanged = HideUnchanged(succeeded);
            foreach (var t in targets.Where(t => unchanged.Contains(t.Package)))
            {
                var entry = live[t];
                entry.Message = $"No change: Windows still reports {t.Package.InstalledVersion} after the update, so Evict now treats {t.Name} as already up to date.";
                await Task.Run(() => _services.UpdateHistory.Add(entry));
            }
            if (unchanged.Count > 0) RebuildHistory();
        }
    }

    [RelayCommand]
    private void CancelUpdate()
    {
        if (!IsUpdating || _cts is null) return;
        if (Dialogs.Confirm("Cancel the remaining updates?\n\nQueued updates will be cancelled. An installer already running may continue outside Evict; its result will be shown as Interrupted until checked again."))
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
