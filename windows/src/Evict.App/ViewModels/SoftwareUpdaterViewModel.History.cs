using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Windows.Data;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Evict.App.Services;
using Evict.Core.Models;
using Evict.Core.Services;

namespace Evict.App.ViewModels;

/// <summary>One row of the History tab (design SH1·1) and the source of its details panel (SH1·3).</summary>
public sealed class UpdateHistoryItemViewModel
{
    private static readonly CultureInfo En = CultureInfo.InvariantCulture;

    public UpdateHistoryItemViewModel(SoftwareUpdateHistoryEntry entry, DateTime nowLocal)
    {
        Entry = entry;
        DayLabel = SoftwareUpdateHistory.DayLabel(entry.StartedUtc.ToLocalTime(), nowLocal);
    }

    public SoftwareUpdateHistoryEntry Entry { get; }
    public Guid Id => Entry.Id;
    public string DayLabel { get; }
    public string Time => Entry.StartedUtc.ToLocalTime().ToString("HH:mm", En);
    public string Name => Entry.Name;
    public string PackageId => Entry.PackageId;
    public string FromVersion => Entry.FromVersion ?? "?";
    public string ToVersion => Entry.ToVersion ?? "?";
    public string VersionChange => $"{FromVersion} → {ToVersion}";
    /// <summary>Outcome name, used by the result-pill styles.</summary>
    public string Kind => Entry.Outcome.ToString();
    public string Result => SoftwareUpdateHistory.OutcomeText(Entry.Outcome);
    public string Took => Entry.IsFinished ? SoftwareUpdateHistory.DurationText(Entry.Duration) : "—";
    public string Message => Entry.Message;
    public string StartedText => Entry.StartedUtc.ToLocalTime().ToString("ddd d MMM, HH:mm", En);
    public string Source => string.IsNullOrEmpty(Entry.Source) ? "winget" : Entry.Source!;
    public string StartedBy => string.IsNullOrEmpty(Entry.StartedBy) ? "—" : Entry.StartedBy;
    public string OutputText => Entry.Output.Count == 0 ? "No output was recorded." : string.Join(Environment.NewLine, Entry.Output);
    public string OutputHeader => Entry.IsEvict ? "UPDATE STEPS" : "WINGET OUTPUT";
    /// <summary>Only finished winget updates that did not work can be tried again from here; Evict's own goes through Settings.</summary>
    public bool CanRetry => !Entry.IsEvict && Entry.Outcome is SoftwareUpdateOutcome.Failed or SoftwareUpdateOutcome.Cancelled or SoftwareUpdateOutcome.Interrupted;
    public bool IsEvictEntry => Entry.IsEvict;

    public bool Matches(string filter, string search)
    {
        bool kind = filter switch
        {
            "Updated" => Entry.IsSuccess,
            "Failed" => Entry.Outcome == SoftwareUpdateOutcome.Failed,
            "Cancelled" => Entry.Outcome == SoftwareUpdateOutcome.Cancelled,
            "Interrupted" => Entry.Outcome == SoftwareUpdateOutcome.Interrupted,
            "Restart required" => Entry.Outcome == SoftwareUpdateOutcome.UpdatedRestartNeeded,
            _ => true,
        };
        if (!kind) return false;
        if (string.IsNullOrWhiteSpace(search)) return true;
        var s = search.Trim();
        return Name.Contains(s, StringComparison.OrdinalIgnoreCase) || PackageId.Contains(s, StringComparison.OrdinalIgnoreCase)
            || VersionChange.Contains(s, StringComparison.OrdinalIgnoreCase) || Message.Contains(s, StringComparison.OrdinalIgnoreCase);
    }
}

public sealed partial class SoftwareUpdaterViewModel
{
    /// <summary>"Updates" or "History" (design SH1 choice H1: the history is a tab of this page).</summary>
    [ObservableProperty] private string _selectedTab = "Updates";
    /// <summary>All · Updated · Failed · Cancelled.</summary>
    [ObservableProperty] private string _historyFilter = "All";
    [ObservableProperty] private string _historySearch = "";
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasSelectedHistoryItem))]
    [NotifyPropertyChangedFor(nameof(CanRetrySelected))]
    private UpdateHistoryItemViewModel? _selectedHistoryItem;

    /// <summary>"Try this update again" is shown only when winget can actually run it.</summary>
    public bool CanRetrySelected => SelectedHistoryItem is { CanRetry: true } && WingetAvailable && CanEditSelection;
    partial void OnWingetAvailableChanged(bool value) => OnPropertyChanged(nameof(CanRetrySelected));

    /// <summary>Unfinished rows of the run on screen (Waiting / Updating…), shown on top of the saved history.</summary>
    private readonly Dictionary<Guid, SoftwareUpdateHistoryEntry> _liveEntries = new();
    private ICollectionView? _historyView;

    public ObservableCollection<UpdateHistoryItemViewModel> HistoryItems { get; } = new();
    /// <summary>History rows grouped by day: "Today · Mon 19 Oct", "Yesterday · …", "Fri 16 Oct".</summary>
    public ICollectionView HistoryView
    {
        get
        {
            if (_historyView is null)
            {
                _historyView = CollectionViewSource.GetDefaultView(HistoryItems);
                _historyView.GroupDescriptions.Add(new PropertyGroupDescription(nameof(UpdateHistoryItemViewModel.DayLabel)));
            }
            return _historyView;
        }
    }

    [ObservableProperty] private int _historyCount;
    [ObservableProperty] private int _historyUpdatedCount;
    [ObservableProperty] private int _historyFailedCount;
    [ObservableProperty] private int _historyCancelledCount;
    [ObservableProperty] private bool _historyIsEmpty = true;
    [ObservableProperty] private bool _historyHasNoMatch;
    [ObservableProperty] private string? _recentSummary;
    [ObservableProperty] private string _lastRunText = "";

    public bool HasHistoryRows => !HistoryIsEmpty;
    public bool HasRecentSummary => RecentSummary != null;
    public bool HasSelectedHistoryItem => SelectedHistoryItem != null;
    public string? HistoryPersistenceError => _services.UpdateHistory.PersistenceError;
    public bool HasHistoryPersistenceError => HistoryPersistenceError != null;
    public bool CanClearHistory => _services.UpdateHistory.LoadError is null && _services.UpdateHistory.Entries.Any(e => e.IsFinished);
    public IReadOnlyList<string> HistoryFilters { get; } = new[] { "All", "Updated", "Failed", "Cancelled", "Interrupted", "Restart required" };
    public string HistoryKeepText =>
        "Completed attempts are kept locally for a rolling 7 days (168 hours). Cleanup runs at startup, every minute and on refresh. Active attempts are kept until their result is known.";

    partial void OnHistoryIsEmptyChanged(bool value) => OnPropertyChanged(nameof(HasHistoryRows));
    partial void OnRecentSummaryChanged(string? value) => OnPropertyChanged(nameof(HasRecentSummary));
    partial void OnHistoryFilterChanged(string value) => RebuildHistory();
    partial void OnHistorySearchChanged(string value) => RebuildHistory();
    partial void OnSelectedTabChanged(string value)
    {
        if (value == "History")
        {
            _ = RefreshHistoryAsync();
        }
    }

    private void HookHistory()
    {
        _services.UpdateHistory.Changed += () =>
        {
            var dispatcher = System.Windows.Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess()) RebuildHistory();
            else dispatcher.BeginInvoke(RebuildHistory);
        };
        RebuildHistory();
    }

    /// <summary>Rebuilds the History tab, the Updates tab summary and the per-row notes from the store.</summary>
    private void RebuildHistory()
    {
        var now = DateTime.Now;
        var saved = _services.UpdateHistory.Entries.Where(e => SoftwareUpdateHistory.IsKept(e, now) && !_liveEntries.ContainsKey(e.Id)).ToList();
        var all = _liveEntries.Values.OrderByDescending(e => e.StartedUtc).Concat(saved).ToList();
        var keepSelection = SelectedHistoryItem?.Id;

        HistoryItems.Clear();
        foreach (var entry in all)
        {
            var row = new UpdateHistoryItemViewModel(entry, now);
            if (row.Matches(HistoryFilter, HistorySearch)) HistoryItems.Add(row);
        }
        SelectedHistoryItem = keepSelection is { } id ? HistoryItems.FirstOrDefault(r => r.Id == id) : null;

        HistoryCount = all.Count;
        HistoryUpdatedCount = saved.Count(e => e.IsSuccess);
        HistoryFailedCount = saved.Count(e => e.Outcome == SoftwareUpdateOutcome.Failed);
        HistoryCancelledCount = saved.Count(e => e.Outcome == SoftwareUpdateOutcome.Cancelled);
        HistoryIsEmpty = all.Count == 0 && _services.UpdateHistory.LoadError is null;
        HistoryHasNoMatch = all.Count > 0 && HistoryItems.Count == 0;
        RecentSummary = SoftwareUpdateHistory.Summary(saved);
        LastRunText = saved.FirstOrDefault() is { } newest ? "· last run " + SoftwareUpdateHistory.WhenText(newest.StartedUtc, now) : "";
        OnPropertyChanged(nameof(HistoryKeepText));
        OnPropertyChanged(nameof(HistoryPersistenceError));
        OnPropertyChanged(nameof(HasHistoryPersistenceError));
        OnPropertyChanged(nameof(CanClearHistory));

        var running = _liveEntries.Values.Select(e => e.PackageId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var item in Items)
        {
            if (item.IsUpdating || running.Contains(item.Id)) continue;
            var last = SoftwareUpdateHistory.LatestFor(saved, item.Id);
            item.LastResultNote = SoftwareUpdateHistory.LastResultNote(last, now);
            item.LastHistoryId = last?.Id;
        }
    }

    [RelayCommand] private void ShowHistory() { HistoryFilter = "All"; SelectedTab = "History"; }

    /// <summary>The "Failed today 16:41 — why?" link on an Updates row opens that entry's details.</summary>
    [RelayCommand]
    private void OpenHistoryEntry(UpgradeItemViewModel? item)
    {
        if (item?.LastHistoryId is not { } id) return;
        HistorySearch = "";
        HistoryFilter = "All";
        SelectedTab = "History";
        RebuildHistory();
        SelectedHistoryItem = HistoryItems.FirstOrDefault(r => r.Id == id);
    }

    [RelayCommand] private void CloseHistoryDetails() => SelectedHistoryItem = null;

    [RelayCommand]
    private void CopyHistoryDetails()
    {
        if (SelectedHistoryItem is { } row) Dialogs.CopyToClipboard(SoftwareUpdateHistory.DetailsText(row.Entry));
    }

    [RelayCommand]
    private async Task RetryHistoryItemAsync()
    {
        WingetAvailable = WingetService.IsAvailable;
        if (!WingetAvailable)
        {
            // Same path as the Updates tab: point to App Installer instead of recording another failure.
            SelectedTab = "Updates";
            return;
        }
        if (SelectedHistoryItem is not { CanRetry: true } row || IsBusy || IsUpdating) return;
        var entry = row.Entry;
        // Recheck the source and installed version, and honour exclusions changed since this attempt.
        await CheckAsync();
        if (Error != null || IsBusy || IsUpdating) return;
        var source = entry.Source is null or "winget" ? "winget" : entry.Source;
        var target = Items.FirstOrDefault(i => string.Equals(i.Id, entry.PackageId, StringComparison.OrdinalIgnoreCase)
            && string.Equals(string.IsNullOrEmpty(i.Source) ? "winget" : i.Source, source, StringComparison.OrdinalIgnoreCase));
        SelectedHistoryItem = null;
        SelectedTab = "Updates";
        if (target is null)
        {
            StatusText = "No eligible update was found for this package and source. It may already be current, unavailable or excluded.";
            return;
        }
        foreach (var item in Items) item.IsSelected = ReferenceEquals(item, target);
        StatusText = $"Latest update found for {target.Name}. Review it and choose Update selected.";
    }

    [RelayCommand]
    private async Task RefreshHistoryAsync()
    {
        await Task.Run(() => _services.UpdateHistory.PruneNow());
        RebuildHistory();
    }

    [RelayCommand]
    private async Task RetryHistoryPersistenceAsync()
    {
        await Task.Run(() => _services.UpdateHistory.RetryPersistence());
        RebuildHistory();
    }

    [RelayCommand]
    private void HistorySettings() => new Evict.App.Views.SoftwareUpdateHistorySettingsWindow
    {
        Owner = System.Windows.Application.Current.MainWindow, DataContext = this,
    }.ShowDialog();

    [RelayCommand]
    private async Task ClearHistoryAsync()
    {
        if (!CanClearHistory) return;
        if (!Dialogs.Confirm($"Clear the update history?\n\nThis removes completed entries from the last {SoftwareUpdateHistory.KeepDays} days. Your programs stay exactly as they are — only the record goes. It can't be undone.", destructive: true)) return;
        SelectedHistoryItem = null;
        await Task.Run(() => _services.UpdateHistory.Clear());
        RebuildHistory();
    }

    [RelayCommand]
    private void ExportHistory()
    {
        var entries = _services.UpdateHistory.Entries;
        if (entries.Count == 0) return;
        var dlg = new Microsoft.Win32.SaveFileDialog { FileName = $"evict-update-history-{DateTime.Now:yyyy-MM-dd}.csv", Filter = "CSV file|*.csv" };
        if (dlg.ShowDialog() != true) return;
        try { File.WriteAllText(dlg.FileName, SoftwareUpdateHistory.ToCsv(entries), new System.Text.UTF8Encoding(true)); }
        catch (Exception ex) { Dialogs.Error("Could not save the file: " + ex.Message); }
    }
}
