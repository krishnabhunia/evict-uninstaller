using System.Collections.ObjectModel;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Evict.App.Services;
using Evict.App.Views;
using Evict.Core.Models;
using Evict.Core.Services;
using Evict.Core.Util;

namespace Evict.App.ViewModels;

public enum TileState { Idle, Busy, Good, Attention, Unavailable }

public sealed partial class HealthTileViewModel : ObservableObject
{
    public required string Key { get; init; }
    public required string Title { get; init; }
    public required string Glyph { get; init; }
    public required string ActionText { get; init; }
    /// <summary>Penalty per finding for the health score.</summary>
    public int Weight { get; init; } = 2;
    public int MaxPenalty { get; init; } = 25;

    [ObservableProperty] private int _count;
    [ObservableProperty] private string _detail = "Not scanned yet";
    [ObservableProperty] private TileState _state = TileState.Idle;

    public bool IsBusy => State == TileState.Busy;
    public bool IsGood => State == TileState.Good;
    public bool IsAttention => State == TileState.Attention;
    public bool HasResult => State is TileState.Good or TileState.Attention;
    public string CountText => State is TileState.Good or TileState.Attention ? Count.ToString("N0") : State == TileState.Busy ? "…" : "–";
    public int Penalty => Math.Min(MaxPenalty, Count * Weight);

    partial void OnStateChanged(TileState value)
    {
        OnPropertyChanged(nameof(IsBusy)); OnPropertyChanged(nameof(IsGood)); OnPropertyChanged(nameof(IsAttention));
        OnPropertyChanged(nameof(HasResult)); OnPropertyChanged(nameof(CountText));
    }
    partial void OnCountChanged(int value) => OnPropertyChanged(nameof(CountText));

    public void Set(int count, string detail, bool unavailable = false)
    {
        Count = count;
        Detail = detail;
        State = unavailable ? TileState.Unavailable : count == 0 ? TileState.Good : TileState.Attention;
    }
}

/// <summary>Software Health – the home page. Runs every check concurrently and turns them into a score plus one-click actions.</summary>
public sealed partial class HealthViewModel : ObservableObject, IActivatable
{
    private readonly AppServices _services;
    private readonly MainViewModel _main;
    private bool _scannedOnce;

    public HealthViewModel(AppServices services, MainViewModel main)
    {
        _services = services;
        _main = main;
        Tiles = new ObservableCollection<HealthTileViewModel>
        {
            new() { Key = "outdated", Title = "Outdated programs", Glyph = "", ActionText = "Update", Weight = 3, MaxPenalty = 12 },
            new() { Key = "residual", Title = "Leftovers from earlier uninstalls", Glyph = "", ActionText = "Clean up", Weight = 1, MaxPenalty = 5 },
            new() { Key = "broken", Title = "Broken uninstall entries", Glyph = "", ActionText = "Review", Weight = 3, MaxPenalty = 10 },
            new() { Key = "bundleware", Title = "Possible bundleware", Glyph = "", ActionText = "Review", Weight = 2, MaxPenalty = 12 },
            new() { Key = "extensions", Title = "Extensions with broad permissions", Glyph = "", ActionText = "Review", Weight = 1, MaxPenalty = 8 },
            new() { Key = "unused", Title = "Large programs not used recently", Glyph = "", ActionText = "Review", Weight = 1, MaxPenalty = 8 },
            new() { Key = "bloat", Title = "Pre-installed Store apps flagged as bloatware", Glyph = "", ActionText = "Review", Weight = 1, MaxPenalty = 10 },
            new() { Key = "startup", Title = "Programs starting at sign-in", Glyph = "", ActionText = "Manage", Weight = 0, MaxPenalty = 0 },
        };
    }

    public ObservableCollection<HealthTileViewModel> Tiles { get; }

    [ObservableProperty] private bool _isScanning;
    [ObservableProperty] private int _score = -1;
    [ObservableProperty] private string _scoreLabel = "Not scanned yet";
    [ObservableProperty] private string _summary = "Run a scan to see what needs attention.";
    [ObservableProperty] private DateTime? _lastScan;

    public bool HasScore => Score >= 0;
    public string LastScanText => LastScan is { } d ? "Scanned " + ProgramItemViewModel.Relative(d) : "";
    public int IssueCount => Tiles.Where(t => t.Weight > 0 && t.State == TileState.Attention).Sum(t => t.Count);

    partial void OnScoreChanged(int value) => OnPropertyChanged(nameof(HasScore));

    public void OnActivated()
    {
        if (!_scannedOnce && _services.Settings.Current.HealthAutoScan) _ = ScanAsync();
    }

    private Task? _currentScan;

    /// <summary>Runs a scan, or joins the one already in progress (so callers can await completion either way).</summary>
    [RelayCommand]
    public Task ScanAsync()
    {
        if (_currentScan is { IsCompleted: false }) return _currentScan;
        _currentScan = ScanCoreAsync();
        return _currentScan;
    }

    /// <summary>One-line result for notifications: "Score 82/100 · 3 outdated programs · 2 leftovers".</summary>
    public string NotificationSummary()
    {
        var parts = Tiles.Where(t => t.Weight > 0 && t.State == TileState.Attention).OrderByDescending(t => t.Penalty).Take(3)
            .Select(t => $"{t.Count:N0} {t.Title.ToLowerInvariant()}");
        var detail = string.Join(" · ", parts);
        return $"Score {Score}/100 ({ScoreLabel})" + (detail.Length > 0 ? " · " + detail : " · nothing needs attention");
    }

    private async Task ScanCoreAsync()
    {
        IsScanning = true;
        _scannedOnce = true;
        foreach (var t in Tiles) t.State = TileState.Busy;

        var programs = _main.GetPage<ProgramsViewModel>(PageKey.Programs);
        var s = _services.Settings.Current;

        var programsTask = RunTile("broken", async () =>
        {
            await programs.EnsureFullyLoadedAsync();
            var list = programs.Items.Select(i => i.Program).ToList();
            Tile("broken").Set(list.Count(p => p.IsBrokenEntry), "Programs & Features entries whose files and uninstaller are gone.");
            Tile("bundleware").Set(list.Count(p => p.IsBundleSuspect), list.Any(p => p.IsKnownBundleware)
                ? $"{list.Count(p => p.IsKnownBundleware)} match the known-bundleware list; the rest were installed alongside other software."
                : "Programs installed within minutes of another vendor's program.");
            var unused = list.Where(p => InstalledProgramsService.IsInfrequentlyUsed(p, s.InfrequentlyUsedDays) && InstalledProgramsService.IsLarge(p, s.LargeProgramThresholdMb)).ToList();
            Tile("unused").Set(unused.Count, unused.Count == 0 ? $"Nothing over {s.LargeProgramThresholdMb} MB has gone unused for {s.InfrequentlyUsedDays}+ days." : $"{SizeFormatter.Format(unused.Sum(p => p.SizeBytes ?? 0))} in programs not launched for {s.InfrequentlyUsedDays}+ days.");
            return list;
        }, "bundleware", "unused");

        var residualTask = RunTile("residual", async () =>
        {
            var list = await programsTask;
            var result = await _services.Residual.ScanAsync(list ?? new List<InstalledProgram>(), _services.History.Entries,
                new ResidualScanOptions { FromHistory = true, BrokenEntries = true, UnmatchedFolders = false, ScanAllUserProfiles = s.ScanAllUserProfiles }, null, CancellationToken.None);
            Tile("residual").Set(result.Items.Count, result.Items.Count == 0 ? "Nothing left behind by the uninstalls Evict knows about." : $"{SizeFormatter.Format(result.TotalBytes)} of files and {result.RegistryCount} registry entries from programs removed earlier.");
            return true;
        });

        var extTask = RunTile("extensions", async () =>
        {
            var exts = await _services.Browser.GetExtensionsAsync(false, CancellationToken.None);
            int risky = exts.Count(ExtensionItemViewModel.IsRiskyInfo);
            Tile("extensions").Set(risky, exts.Count == 0 ? "No browser extensions found." : $"{exts.Count} extensions across your browsers; {risky} can read every site you visit.");
            return true;
        });

        var updTask = RunTile("outdated", async () =>
        {
            if (!WingetService.IsAvailable) { Tile("outdated").Set(0, "winget (App Installer) is not available – install it from the Microsoft Store.", unavailable: true); return false; }
            var (pkgs, err) = await _services.Winget.GetUpgradesAsync(false, CancellationToken.None);
            if (pkgs.Count == 0 && err != null) { Tile("outdated").Set(0, err, unavailable: true); return false; }
            Tile("outdated").Set(pkgs.Count, pkgs.Count == 0 ? "Every program winget knows about is up to date." : string.Join(", ", pkgs.Take(4).Select(p => p.Name)) + (pkgs.Count > 4 ? $" and {pkgs.Count - 4} more" : ""));
            return true;
        });

        var appxTask = RunTile("bloat", async () =>
        {
            var (pk, err) = await _services.Appx.GetPackagesAsync(false, null, CancellationToken.None);
            if (pk.Count == 0 && err != null) { Tile("bloat").Set(0, err, unavailable: true); return false; }
            int bloat = pk.Count(p => p.IsKnownBloatware && !p.NonRemovable);
            Tile("bloat").Set(bloat, bloat == 0 ? "No known bloatware packages installed." : string.Join(", ", pk.Where(p => p.IsKnownBloatware && !p.NonRemovable).Take(4).Select(p => p.DisplayName ?? p.Name)) + (bloat > 4 ? $" and {bloat - 4} more" : ""));
            return true;
        });

        var startupTask = RunTile("startup", async () =>
        {
            var items = await Task.Run(() => _services.Startup.GetItems());
            int enabled = items.Count(i => i.Enabled);
            Tile("startup").Set(enabled, $"{items.Count} startup entries, {enabled} enabled. Fewer startup apps = faster sign-in.");
            Tile("startup").State = TileState.Good; // informational, never a penalty
            return true;
        });

        await Task.WhenAll(programsTask, residualTask, extTask, updTask, appxTask, startupTask);

        int penalty = Tiles.Where(t => t.State == TileState.Attention).Sum(t => t.Penalty);
        Score = Math.Clamp(100 - penalty, 0, 100);
        ScoreLabel = Score >= 90 ? "Excellent" : Score >= 75 ? "Good" : Score >= 55 ? "Fair" : "Needs attention";
        int issues = IssueCount;
        Summary = issues == 0 ? "Nothing needs your attention right now." : $"{issues} item(s) across {Tiles.Count(t => t.State == TileState.Attention)} area(s) could be cleaned up.";
        LastScan = DateTime.Now;
        OnPropertyChanged(nameof(LastScanText));
        OnPropertyChanged(nameof(IssueCount));
        _main.SetBadge(PageKey.Health, Tiles.Count(t => t.Weight > 0 && t.State == TileState.Attention));
        IsScanning = false;
    }

    private HealthTileViewModel Tile(string key) => Tiles.First(t => t.Key == key);

    private async Task<T?> RunTile<T>(string key, Func<Task<T>> work, params string[] alsoKeys)
    {
        try
        {
            return await work();
        }
        catch (Exception ex)
        {
            Log.Warn($"Health tile {key}: {ex.Message}");
            Tile(key).Set(0, "Could not check: " + ex.Message, unavailable: true);
            foreach (var k in alsoKeys) Tile(k).Set(0, "Could not check: " + ex.Message, unavailable: true);
            return default;
        }
    }

    [RelayCommand]
    private void Act(HealthTileViewModel? tile)
    {
        if (tile is null) return;
        switch (tile.Key)
        {
            case "outdated": _main.Navigate(PageKey.SoftwareUpdater); break;
            case "residual": _main.GetPage<ToolsViewModel>(PageKey.Tools).OpenResidualCleaner(); _ = ScanAsync(); break;
            case "broken": _main.GetPage<ProgramsViewModel>(PageKey.Programs).SelectedTab = ProgramTab.Broken; _main.Navigate(PageKey.Programs); break;
            case "bundleware": _main.GetPage<ProgramsViewModel>(PageKey.Programs).SelectedTab = ProgramTab.Bundleware; _main.Navigate(PageKey.Programs); break;
            case "unused": _main.GetPage<ProgramsViewModel>(PageKey.Programs).SelectedTab = ProgramTab.Infrequent; _main.Navigate(PageKey.Programs); break;
            case "extensions": _main.Navigate(PageKey.BrowserExtensions); break;
            case "bloat": _main.GetPage<WindowsAppsViewModel>(PageKey.WindowsApps).OnlyBloatware = true; _main.Navigate(PageKey.WindowsApps); break;
            case "startup":
                new StartupWindow { DataContext = new StartupViewModel(_services), Owner = Application.Current.MainWindow }.ShowDialog();
                break;
        }
    }
}
