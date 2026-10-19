using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Evict.App.Services;
using Evict.App.Views;
using Evict.Core.Models;
using Evict.Core.Services;

namespace Evict.App.ViewModels;

public sealed class SoftwareUpdateHistoryItemViewModel
{
    public SoftwareUpdateHistoryItemViewModel(SoftwareUpdateAttempt attempt) => Attempt = attempt;
    public SoftwareUpdateAttempt Attempt { get; }
    public Guid Id => Attempt.Id;
    public string Name => Attempt.Name;
    public string PackageId => Attempt.PackageId;
    public string Source => string.IsNullOrWhiteSpace(Attempt.Source) ? "Not reported" : Attempt.Source;
    public string PreviousVersion => Attempt.PreviousVersion ?? "Unknown";
    public string TargetVersion => Attempt.TargetVersion ?? "Unknown";
    public string VersionChange => PreviousVersion + " → " + TargetVersion;
    public SoftwareUpdateOutcome Outcome => Attempt.Outcome;
    public string Result => Outcome switch
    {
        SoftwareUpdateOutcome.Updated => "Updated",
        SoftwareUpdateOutcome.RestartRequired => "Restart required",
        SoftwareUpdateOutcome.Failed => "Failed",
        SoftwareUpdateOutcome.Cancelled => "Cancelled",
        SoftwareUpdateOutcome.Interrupted => "Interrupted",
        _ => "In progress",
    };
    public string Message => Attempt.Message;
    public string When => (Attempt.FinishedAtUtc ?? Attempt.StartedAtUtc).ToLocalTime().ToString("dd MMM, HH:mm");
    public string Started => Attempt.StartedAtUtc.ToLocalTime().ToString("dd MMM yyyy, HH:mm:ss zzz");
    public string Finished => Attempt.FinishedAtUtc?.ToLocalTime().ToString("dd MMM yyyy, HH:mm:ss zzz") ?? "Not yet";
    public string Duration => Outcome == SoftwareUpdateOutcome.InProgress ? "In progress"
        : Outcome == SoftwareUpdateOutcome.Interrupted ? "Not confirmed"
        : TimeSpan.FromSeconds(Math.Max(0, (Attempt.FinishedAtUtc!.Value - Attempt.StartedAtUtc).TotalSeconds)).ToString(@"hh\:mm\:ss");
    public string ExitCode => Attempt.ExitCode is { } code ? $"{code} (0x{unchecked((uint)code):X8})"
        : Outcome == SoftwareUpdateOutcome.Cancelled ? "Not started" : "Not confirmed";
    public string Expires => Attempt.FinishedAtUtc is { } end
        ? (end + SoftwareUpdateHistoryStore.Retention).ToLocalTime().ToString("dd MMM yyyy, HH:mm:ss zzz")
        : "7 days after this attempt finishes";
    public string Output => Attempt.Output.Count > 0 ? string.Join(Environment.NewLine, Attempt.Output) : "No installer output was captured.";
    public string CopyText => $"{Name}\n{PackageId}\nSource: {Source}\nRequested change: {VersionChange}\nResult: {Result}\n{Message}\nStarted: {Started}\nFinished: {Finished}\nDuration: {Duration}\nExit code: {ExitCode}\nRemoved from history: {Expires}\n\nActivity log:\n{Output}";
}

public sealed partial class SoftwareUpdateHistoryViewModel : ObservableObject
{
    private readonly SoftwareUpdateHistoryStore _store;
    private readonly Func<SoftwareUpdateAttempt, Task> _recheck;
    private readonly Func<bool> _canRecheck;
    private readonly Action _showActive;
    private IReadOnlyList<SoftwareUpdateAttempt> _snapshot = Array.Empty<SoftwareUpdateAttempt>();

    public SoftwareUpdateHistoryViewModel(SoftwareUpdateHistoryStore store, Func<SoftwareUpdateAttempt, Task> recheck,
        Func<bool> canRecheck, Action showActive)
    {
        _store = store; _recheck = recheck; _canRecheck = canRecheck; _showActive = showActive;
        _store.Changed += StoreChanged;
        Refresh();
    }

    public ObservableCollection<SoftwareUpdateHistoryItemViewModel> Items { get; } = new();
    public IReadOnlyList<KeyValuePair<SoftwareUpdateOutcome?, string>> Filters { get; } =
        new KeyValuePair<SoftwareUpdateOutcome?, string>[]
        {
            new(null, "All results"), new(SoftwareUpdateOutcome.Updated, "Updated"),
            new(SoftwareUpdateOutcome.Failed, "Failed"), new(SoftwareUpdateOutcome.RestartRequired, "Restart required"),
            new(SoftwareUpdateOutcome.Cancelled, "Cancelled"), new(SoftwareUpdateOutcome.Interrupted, "Interrupted"),
            new(SoftwareUpdateOutcome.InProgress, "In progress"),
        };
    [ObservableProperty] private SoftwareUpdateOutcome? _selectedOutcome;
    [ObservableProperty] private string? _feedback;
    public KeyValuePair<SoftwareUpdateOutcome?, string> SelectedFilter
    {
        get => Filters.First(option => option.Key == SelectedOutcome);
        set => SelectedOutcome = value.Key;
    }
    partial void OnSelectedOutcomeChanged(SoftwareUpdateOutcome? value)
    {
        OnPropertyChanged(nameof(SelectedFilter));
        Refresh();
    }

    public int Count => _snapshot.Count;
    public string TabTitle => $"Update history ({Count})";
    public string Summary => $"{Count} attempt(s) · {_snapshot.Count(r => r.Outcome == SoftwareUpdateOutcome.Updated)} updated · " +
        $"{_snapshot.Count(r => r.Outcome == SoftwareUpdateOutcome.Failed)} failed · " +
        $"{_snapshot.Count(r => r.Outcome == SoftwareUpdateOutcome.InProgress)} active";
    public string WindowText => $"{_store.CutoffUtc.ToLocalTime():dd MMM} – {DateTimeOffset.Now:dd MMM yyyy}";
    public string TimeZoneText => "Times shown in " + TimeZoneInfo.Local.DisplayName;
    public string CutoffText => $"Completed records at or before {_store.CutoffUtc.ToLocalTime():dd MMM yyyy, HH:mm zzz} are automatically removed.";
    public string? PersistenceError => _store.LoadError ?? _store.SaveError;
    public bool HasPersistenceError => PersistenceError != null;
    public bool ShowUnavailable => _store.LoadError != null && Count == 0;
    public bool ShowEmpty => !ShowUnavailable && Count == 0;
    public bool ShowNoResults => !ShowUnavailable && Count > 0 && Items.Count == 0;
    public bool HasVisibleItems => Items.Count > 0;
    public bool HasCompletedRecords => _store.LoadError is null && _snapshot.Any(r => r.Outcome != SoftwareUpdateOutcome.InProgress);

    private void StoreChanged()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        _ = dispatcher.BeginInvoke((Action)Refresh);
    }

    public void Refresh()
    {
        _store.PruneExpired();
        _snapshot = _store.Snapshot();
        Items.Clear();
        foreach (var record in _snapshot.Where(r => SelectedOutcome is null || r.Outcome == SelectedOutcome))
            Items.Add(new(record));
        OnPropertyChanged(string.Empty);
        ClearHistoryCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand] private void ResetFilter() => SelectedOutcome = null;
    [RelayCommand] private void RetryPersistence()
    {
        Feedback = _store.RetrySave() ? "History saved. Results are kept for 7 days." : null;
        Refresh();
    }
    [RelayCommand] private void ShowSettings() =>
        new SoftwareUpdateHistorySettingsWindow { DataContext = this, Owner = Application.Current.MainWindow }.ShowDialog();

    [RelayCommand(CanExecute = nameof(HasCompletedRecords))]
    private void ClearHistory()
    {
        if (!HasCompletedRecords) return;
        if (!Dialogs.Confirm("Permanently remove completed software update attempts from this device?\n\n" +
            "Installed programs, exclusions and active updates stay unchanged. This cannot be undone. " +
            "New attempts will continue to be recorded for 7 days.", "Clear update history?", destructive: true)) return;
        Feedback = _store.ClearCompleted() ? "Update history cleared. Active updates were preserved." : "History could not be cleared; the existing records were kept.";
        Refresh();
    }

    [RelayCommand] private void ShowDetails(SoftwareUpdateHistoryItemViewModel? item)
    {
        if (item is null) return;
        using var vm = new SoftwareUpdateHistoryDetailsViewModel(_store, item.Id, _recheck, _canRecheck, _showActive);
        var window = new SoftwareUpdateHistoryDetailsWindow { DataContext = vm, Owner = Application.Current.MainWindow };
        vm.RequestClose += window.Close;
        window.ShowDialog();
    }

    [RelayCommand] private void ShowAvailableUpdates() => _showActive();
}

public sealed partial class SoftwareUpdateHistoryDetailsViewModel : ObservableObject, IDisposable
{
    private readonly SoftwareUpdateHistoryStore _store;
    private readonly Guid _id;
    private readonly Func<SoftwareUpdateAttempt, Task> _recheck;
    private readonly Func<bool> _canRecheck;
    private readonly Action _showActive;
    private readonly System.Windows.Threading.DispatcherTimer _timer;
    [ObservableProperty] private SoftwareUpdateHistoryItemViewModel? _item;
    [ObservableProperty] private string? _copyStatus;
    public event Action? RequestClose;

    public SoftwareUpdateHistoryDetailsViewModel(SoftwareUpdateHistoryStore store, Guid id,
        Func<SoftwareUpdateAttempt, Task> recheck, Func<bool> canRecheck, Action showActive)
    {
        _store = store; _id = id; _recheck = recheck; _canRecheck = canRecheck; _showActive = showActive;
        _timer = new() { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += OnTick;
        _timer.Start();
        Refresh();
    }

    private void OnTick(object? sender, EventArgs e) => Refresh();
    public bool HasRecord => Item != null;
    public bool IsExpired => !HasRecord;
    public bool IsRunning => Item?.Outcome == SoftwareUpdateOutcome.InProgress;
    public bool CanRecheck => (Item?.Outcome is SoftwareUpdateOutcome.Failed or SoftwareUpdateOutcome.Cancelled or SoftwareUpdateOutcome.Interrupted) && _canRecheck();
    private void Refresh()
    {
        var record = _store.Snapshot().FirstOrDefault(r => r.Id == _id);
        if (record is null) Item = null;
        else if (Item is null || Item.Attempt.Outcome != record.Outcome || Item.Attempt.Message != record.Message
            || Item.Attempt.FinishedAtUtc != record.FinishedAtUtc || !Item.Attempt.Output.SequenceEqual(record.Output))
            Item = new(record);
        OnPropertyChanged(nameof(HasRecord)); OnPropertyChanged(nameof(IsExpired)); OnPropertyChanged(nameof(IsRunning));
        OnPropertyChanged(nameof(CanRecheck));
        RecheckCommand.NotifyCanExecuteChanged();
        CopyDetailsCommand.NotifyCanExecuteChanged();
    }

    [RelayCommand(CanExecute = nameof(HasRecord))]
    private void CopyDetails()
    {
        try { if (Item != null) System.Windows.Clipboard.SetText(Item.CopyText); CopyStatus = "Details copied."; }
        catch (Exception ex) { CopyStatus = "Could not copy to the clipboard. Try again. " + ex.Message; }
    }

    [RelayCommand(CanExecute = nameof(CanRecheck))]
    private async Task RecheckAsync()
    {
        if (!CanRecheck || Item is null) return;
        var record = Item.Attempt;
        RequestClose?.Invoke();
        await _recheck(record);
    }

    [RelayCommand] private void GoToActiveUpdate() { RequestClose?.Invoke(); _showActive(); }
    public void Dispose() { _timer.Stop(); _timer.Tick -= OnTick; }
}
