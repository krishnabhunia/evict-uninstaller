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
    private CancellationTokenSource? _cts;

    public SoftwareUpdaterViewModel(AppServices services)
    {
        _services = services;
        WingetAvailable = WingetService.IsAvailable;
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

    public string Summary => Items.Count == 0 ? (LastChecked is null ? "Not checked yet." : "Everything is up to date.") : $"{Items.Count} update(s) available";

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
        if (!_loaded && WingetAvailable) _ = CheckAsync();
    }

    partial void OnIsBusyChanged(bool value) => UpdateSelectedCommand.NotifyCanExecuteChanged();
    partial void OnIsUpdatingChanged(bool value) => UpdateSelectedCommand.NotifyCanExecuteChanged();

    [RelayCommand]
    public async Task CheckAsync()
    {
        if (!WingetAvailable) return;
        IsBusy = true;
        Error = null;
        StatusText = "Checking for updates with winget… (this can take a minute)";
        try
        {
            var (pkgs, err) = await _services.Winget.GetUpgradesAsync(IncludeUnknown, CancellationToken.None);
            foreach (var i in Items) i.PropertyChanged -= ItemChanged;
            Items.Clear();
            foreach (var p in pkgs)
            {
                var vm = new UpgradeItemViewModel(p);
                vm.PropertyChanged += ItemChanged;
                Items.Add(vm);
            }
            Error = err;
            LastChecked = DateTime.Now;
            _loaded = true;
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
    }

    private bool CanUpdate() => SelectedCount > 0 && !IsBusy && !IsUpdating;

    [RelayCommand(CanExecute = nameof(CanUpdate))]
    private async Task UpdateSelectedAsync()
    {
        var targets = Items.Where(i => i.IsSelected).ToList();
        if (targets.Count == 0) return;
        _cts = new CancellationTokenSource();
        var ct = _cts.Token;
        IsUpdating = true;
        LogLines.Clear();
        int ok = 0, fail = 0, started = 0;
        TotalCount = targets.Count; DoneCount = 0; OverallProgress = 0;
        int parallel = Math.Min(ParallelUpdates, targets.Count);
        foreach (var t in targets) { t.Status = "Waiting…"; t.IsUpdating = false; }
        StatusText = parallel > 1 ? $"Updating {targets.Count} programs, {parallel} at a time…" : $"Updating {targets.Count} program(s)…";
        var ui = System.Windows.Application.Current.Dispatcher;
        var gate = new SemaphoreSlim(parallel);
        App.Background?.SuspendDetection(true);
        try
        {
            var tasks = targets.Select(async t =>
            {
                await gate.WaitAsync(ct);
                try
                {
                    if (ct.IsCancellationRequested) { await ui.InvokeAsync(() => t.Status = "Cancelled."); return; }
                    await ui.InvokeAsync(() =>
                    {
                        started++;
                        t.IsUpdating = true;
                        t.Status = "Updating…";
                        StatusText = $"Updating… {DoneCount} of {TotalCount} done, {started - DoneCount} running";
                    });
                    var (success, msg) = await _services.Winget.UpgradeAsync(t.Package, ct, line =>
                    {
                        if (string.IsNullOrWhiteSpace(line)) return;
                        var clean = line.Trim();
                        if (clean.All(c => c is '-' or '\\' or '|' or '/' or ' ' or '█' or '▒')) return;
                        ui.BeginInvoke(() => { LogLines.Add($"[{t.Name}] {clean}"); if (LogLines.Count > 600) LogLines.RemoveAt(0); });
                    });
                    await ui.InvokeAsync(() =>
                    {
                        t.IsUpdating = false;
                        t.Status = msg;
                        if (success) { ok++; t.IsSelected = false; } else fail++;
                        DoneCount++;
                        OverallProgress = 100.0 * DoneCount / Math.Max(1, TotalCount);
                        StatusText = $"Updating… {DoneCount} of {TotalCount} done, {started - DoneCount} running";
                    });
                }
                catch (OperationCanceledException) { await ui.InvokeAsync(() => { t.IsUpdating = false; t.Status = "Cancelled."; }); }
                catch (Exception ex) { await ui.InvokeAsync(() => { t.IsUpdating = false; t.Status = "Failed: " + ex.Message; fail++; DoneCount++; }); }
                finally { gate.Release(); }
            }).ToList();
            await Task.WhenAll(tasks);
            StatusText = ct.IsCancellationRequested
                ? $"Cancelled – {ok} updated, {fail} failed."
                : $"Updated {ok} package(s)" + (fail > 0 ? $", {fail} failed." : ".");
        }
        catch (OperationCanceledException) { StatusText = "Cancelled."; }
        finally
        {
            IsUpdating = false;
            foreach (var t in Items) t.IsUpdating = false;
            App.Background?.SuspendDetection(false);
        }
        if (ok > 0) await CheckAsync();
    }

    [RelayCommand]
    private void CancelUpdate() => _cts?.Cancel();

    [RelayCommand]
    private void SelectAll()
    {
        foreach (var i in Items) i.IsSelected = true;
    }

    [RelayCommand]
    private void SelectNone()
    {
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
