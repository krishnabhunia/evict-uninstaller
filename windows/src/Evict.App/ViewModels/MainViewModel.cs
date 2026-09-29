using System.Collections.ObjectModel;
using System.Reflection;
using System.Windows;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Evict.App.Services;
using Evict.App.Views;
using Evict.Core.Models;
using Evict.Core.Services;
using Evict.Core.Util;

namespace Evict.App.ViewModels;

public enum PageKey { Health, Programs, WindowsApps, BrowserExtensions, SoftwareUpdater, InstallMonitor, Tools, History, Settings }

public sealed partial class NavItemViewModel : ObservableObject
{
    public required PageKey Key { get; init; }
    public required string Title { get; init; }
    public required string Glyph { get; init; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string? _badge;
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly Dictionary<PageKey, ObservableObject> _pages = new();
    private EasyUninstallWindow? _widget;

    public MainViewModel(AppServices services)
    {
        _services = services;
        NavItems = new ObservableCollection<NavItemViewModel>
        {
            new() { Key = PageKey.Health, Title = "Software Health", Glyph = "" },
            new() { Key = PageKey.Programs, Title = "Programs", Glyph = "" },
            new() { Key = PageKey.WindowsApps, Title = "Windows Apps", Glyph = "" },
            new() { Key = PageKey.BrowserExtensions, Title = "Browser Extensions", Glyph = "" },
            new() { Key = PageKey.SoftwareUpdater, Title = "Software Updater", Glyph = "" },
            new() { Key = PageKey.InstallMonitor, Title = "Install Monitor", Glyph = "" },
            new() { Key = PageKey.Tools, Title = "Tools", Glyph = "" },
            new() { Key = PageKey.History, Title = "History", Glyph = "" },
            new() { Key = PageKey.Settings, Title = "Settings", Glyph = "" },
        };
        foreach (var item in NavItems)
        {
            item.PropertyChanged += (s, e) =>
            {
                if (e.PropertyName == nameof(NavItemViewModel.IsSelected) && s is NavItemViewModel { IsSelected: true } n) Navigate(n.Key);
            };
        }
        Navigate(PageKey.Health);
    }

    public ObservableCollection<NavItemViewModel> NavItems { get; }

    [ObservableProperty] private ObservableObject? _currentPage;
    [ObservableProperty] private string _currentTitle = "Software Health";
    [ObservableProperty] private bool _isWidgetVisible;

    public bool IsElevated => ElevationHelper.IsElevated;
    public bool ShowAdminBanner => !ElevationHelper.IsElevated && !_adminBannerDismissed;
    private bool _adminBannerDismissed;

    public string WindowTitle => AppPaths.ProductName + (IsElevated ? "  (Administrator)" : "");
    public string VersionText => "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");

    public void Navigate(PageKey key)
    {
        foreach (var n in NavItems) if (n.Key == key && !n.IsSelected) n.IsSelected = true;
        CurrentTitle = NavItems.First(n => n.Key == key).Title;
        CurrentPage = GetOrCreate(key);
        if (CurrentPage is IActivatable a) a.OnActivated();
    }

    private ObservableObject GetOrCreate(PageKey key)
    {
        if (_pages.TryGetValue(key, out var vm)) return vm;
        vm = key switch
        {
            PageKey.Health => new HealthViewModel(_services, this),
            PageKey.Programs => new ProgramsViewModel(_services, this),
            PageKey.WindowsApps => new WindowsAppsViewModel(_services),
            PageKey.BrowserExtensions => new BrowserExtensionsViewModel(_services),
            PageKey.SoftwareUpdater => new SoftwareUpdaterViewModel(_services),
            PageKey.InstallMonitor => new InstallMonitorViewModel(_services, this),
            PageKey.Tools => new ToolsViewModel(_services, this),
            PageKey.History => new HistoryViewModel(_services, this),
            PageKey.Settings => new SettingsViewModel(_services, this),
            _ => throw new ArgumentOutOfRangeException(nameof(key)),
        };
        _pages[key] = vm;
        return vm;
    }

    public T GetPage<T>(PageKey key) where T : ObservableObject => (T)GetOrCreate(key);
    public bool IsPageLoaded(PageKey key) => _pages.ContainsKey(key);

    /// <summary>Tray icon, installer detection and notifications (set by App right after construction).</summary>
    public BackgroundCoordinator Background { get; set; } = null!;

    /// <summary>Shows and activates the main window (it may be hidden in the tray or minimized).</summary>
    public void ShowMainWindow()
    {
        var w = Application.Current.MainWindow;
        if (w is null) return;
        if (!w.IsVisible) w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
        w.Topmost = true; w.Topmost = false;
    }

    /// <summary>Quits for real (bypasses "close to tray").</summary>
    public void ExitApplication() => App.Quit();

    public void SetBadge(PageKey key, int count)
    {
        var item = NavItems.FirstOrDefault(n => n.Key == key);
        if (item != null) item.Badge = count > 0 ? count.ToString() : null;
    }

    // ───────────────────────────── command line / IPC ─────────────────────────────

    public async Task HandleCommandLineAsync(CommandLineOptions o)
    {
        if (o.Page is { } page)
        {
            var key = page switch
            {
                "programs" => PageKey.Programs, "apps" or "windowsapps" => PageKey.WindowsApps, "extensions" => PageKey.BrowserExtensions,
                "updater" or "updates" => PageKey.SoftwareUpdater, "monitor" => PageKey.InstallMonitor, "tools" => PageKey.Tools,
                "history" => PageKey.History, "settings" => PageKey.Settings, _ => PageKey.Health,
            };
            Navigate(key);
        }
        if (o.Widget) ShowWidget();
        if (o.Updated) ShowUpdatedNotice = true;
        if (o.ScheduledScan) await RunScheduledScanAsync();
        else if (o.Scan)
        {
            Navigate(PageKey.Health);
            await GetPage<HealthViewModel>(PageKey.Health).ScanAsync();
        }
        if (o.UninstallFile != null) await UninstallByPathAsync(o.UninstallFile);
        else if (o.UninstallName != null)
        {
            var programs = GetPage<ProgramsViewModel>(PageKey.Programs);
            Navigate(PageKey.Programs);
            await programs.EnsureLoadedAsync();
            var match = ProgramMatcher.FindByName(programs.Items.Select(i => i.Program), o.UninstallName);
            if (match != null) await programs.LaunchWizardForAsync(match);
            else Dialogs.Info($"No installed program matches \"{o.UninstallName}\".");
        }
    }

    /// <summary>
    /// Task Scheduler entry point (--scheduled-scan): scan silently, notify, and – if this process exists only for the
    /// scan – exit again after a while unless the user opened the window.
    /// </summary>
    public async Task RunScheduledScanAsync()
    {
        var health = GetPage<HealthViewModel>(PageKey.Health);
        try
        {
            await health.ScanAsync();
            _services.Settings.Current.LastScheduledScanUtc = DateTime.UtcNow;
            _services.Settings.Save();
            int issues = health.IssueCount;
            var title = issues == 0 ? $"Software Health {health.Score}/100 – all good" : $"Software Health {health.Score}/100 – {issues} item(s) need attention";
            Log.Info("Scheduled scan: " + health.NotificationSummary());
            Background.Notify(title, health.NotificationSummary() + ". Click to open Evict.", () => { ShowMainWindow(); Navigate(PageKey.Health); }, warning: issues > 0);
        }
        catch (Exception ex) { Log.Error("Scheduled scan failed", ex); }

        if (App.StartedHeadless && !_services.Settings.Current.StartWithWindows)
        {
            // Give the notification time to be seen/clicked, then leave quietly.
            await Task.Delay(TimeSpan.FromSeconds(90));
            var w = Application.Current.MainWindow;
            if ((w == null || !w.IsVisible) && !Background.IsRecording) App.Quit();
        }
    }

    /// <summary>If the PC was off when Task Scheduler wanted to run the scan, run it now (a while after start-up).</summary>
    public async Task RunMissedScheduledScanIfDueAsync()
    {
        var s = _services.Settings.Current;
        if (!ScheduledScanTask.IsValidMode(s.ScheduledScan) || s.ScheduledScan.Equals("Off", StringComparison.OrdinalIgnoreCase)) return;
        if (s.LastScheduledScanUtc is null)
        {
            s.LastScheduledScanUtc = DateTime.UtcNow; // start the clock the first time the schedule is active
            _services.Settings.Save();
            return;
        }
        var period = s.ScheduledScan.Equals("Daily", StringComparison.OrdinalIgnoreCase) ? TimeSpan.FromDays(1) : TimeSpan.FromDays(7);
        if (DateTime.UtcNow - s.LastScheduledScanUtc.Value < period + TimeSpan.FromHours(2)) return;
        await Task.Delay(TimeSpan.FromSeconds(30));
        Log.Info("Running the missed scheduled scan.");
        await RunScheduledScanAsync();
    }

    /// <summary>Used by the Explorer context menu, drag & drop and the Easy Uninstall widget.</summary>
    public async Task UninstallByPathAsync(string path)
    {
        var target = path.EndsWith(".lnk", StringComparison.OrdinalIgnoreCase) ? ShortcutResolver.ResolveTarget(path) ?? path : path;
        var programs = GetPage<ProgramsViewModel>(PageKey.Programs);
        Navigate(PageKey.Programs);
        await programs.EnsureLoadedAsync();
        var match = ProgramMatcher.FindByPath(programs.Items.Select(i => i.Program), target);
        if (match != null)
        {
            programs.SearchText = "";
            await programs.LaunchWizardForAsync(match);
            return;
        }

        if (Dialogs.Confirm($"\"{PathUtil.LeafName(target)}\" does not belong to any program in Programs & Features.\n\nOpen Force Uninstall for its folder instead?"))
        {
            var vm = new ForceUninstallViewModel(_services, null, programs.Items.Select(i => i.Program).ToList()) { UseProgram = false, TargetPath = target };
            new ForceUninstallWindow { DataContext = vm, Owner = Application.Current.MainWindow }.ShowDialog();
            if (vm.AnythingChanged) { _ = programs.RefreshAsync(); GetPage<HistoryViewModel>(PageKey.History).Reload(); }
        }
    }

    // ───────────────────────────── Easy Uninstall widget ─────────────────────────────

    [RelayCommand]
    private void ShowWidget()
    {
        if (_widget is { IsLoaded: true }) { _widget.Activate(); return; }
        _widget = new EasyUninstallWindow(this);
        _widget.Closed += (_, _) => { _widget = null; IsWidgetVisible = false; _services.Settings.Current.EasyUninstallWidgetVisible = false; _services.Settings.Save(); };
        _widget.Show();
        IsWidgetVisible = true;
        _services.Settings.Current.EasyUninstallWidgetVisible = true;
        _services.Settings.Save();
    }

    [RelayCommand]
    private void ToggleWidget()
    {
        if (_widget != null) _widget.Close();
        else ShowWidget();
    }

    [RelayCommand]
    private void RestartAsAdministrator()
    {
        if (IsElevated) return;
        Program.ReleaseSingleInstance();
        if (ElevationHelper.RestartElevated())
        {
            App.Quit();
        }
        else
        {
            Dialogs.Info("Administrator rights were not granted. You can keep using Evict, but some operations will be limited.");
        }
    }

    [RelayCommand]
    private void DismissAdminBanner()
    {
        _adminBannerDismissed = true;
        OnPropertyChanged(nameof(ShowAdminBanner));
    }

    // ───────────────────────────── updates ─────────────────────────────

    /// <summary>Latest release found by the start-up check (or Settings → Check now); drives the blue banner.</summary>
    [ObservableProperty] private ReleaseInfo? _availableUpdate;
    [ObservableProperty] private bool _showUpdateBanner;
    [ObservableProperty] private string? _updateBannerText;
    /// <summary>Green "Evict was updated" notice shown once after a self-update (--updated).</summary>
    [ObservableProperty] private bool _showUpdatedNotice;
    public string UpdatedNoticeText => $"Evict was updated to version {_services.Updater.CurrentVersion.ToString(3)}.";

    /// <summary>Runs shortly after the window is shown; silent on every failure.</summary>
    public async Task CheckForUpdatesOnStartupAsync()
    {
        var s = _services.Settings.Current;
        if (!s.CheckForUpdates) return;
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(4)); // let the first page load first
            var result = await _services.Updater.CheckAsync(CancellationToken.None);
            s.LastUpdateCheckUtc = DateTime.UtcNow;
            _services.Settings.Save();
            Log.Info("Update check: " + result.Message);
            if (result.Status == UpdateStatus.UpdateAvailable && result.Release != null) OfferUpdate(result.Release, fromUser: false);
        }
        catch (Exception ex) { Log.Warn("Start-up update check failed: " + ex.Message); }
    }

    /// <summary>Shows the banner (start-up) or the dialog directly (user clicked "Check now").</summary>
    public void OfferUpdate(ReleaseInfo release, bool fromUser)
    {
        AvailableUpdate = release;
        var skipped = string.Equals(_services.Settings.Current.SkippedUpdateVersion, release.TagName, StringComparison.OrdinalIgnoreCase);
        UpdateBannerText = $"Evict {release.Version.ToString(3)} is available (you have {_services.Updater.CurrentVersion.ToString(3)}).";
        if (fromUser) ShowUpdate();
        else ShowUpdateBanner = !skipped;
    }

    [RelayCommand]
    private void ShowUpdate()
    {
        if (AvailableUpdate is null) return;
        var vm = new UpdateViewModel(_services, AvailableUpdate);
        new UpdateWindow { DataContext = vm, Owner = Application.Current.MainWindow }.ShowDialog();
        if (string.Equals(_services.Settings.Current.SkippedUpdateVersion, AvailableUpdate.TagName, StringComparison.OrdinalIgnoreCase)) ShowUpdateBanner = false;
    }

    [RelayCommand] private void DismissUpdateBanner() => ShowUpdateBanner = false;

    // ───────────────────────────── installer detected (in-window prompt) ─────────────────────────────

    [ObservableProperty] private DetectedInstaller? _pendingInstaller;
    public bool ShowInstallerBanner => PendingInstaller != null;
    public string InstallerBannerText => PendingInstaller is { } d ? $"Recording the installation of {d.DisplayName} ({d.FileName}) so it can be removed completely later." : "";
    partial void OnPendingInstallerChanged(DetectedInstaller? value) { OnPropertyChanged(nameof(ShowInstallerBanner)); OnPropertyChanged(nameof(InstallerBannerText)); }

    /// <summary>Keep recording – just hide the banner.</summary>
    [RelayCommand] private void RecordPendingInstaller() => PendingInstaller = null;

    /// <summary>Don't record this one: stop the running recording without saving a log.</summary>
    [RelayCommand]
    private void DismissInstallerBanner()
    {
        var d = PendingInstaller;
        PendingInstaller = null;
        if (d != null) Background.DiscardRecording(d);
    }

    // ───────────────────────────── generic notice bar (used when there is no tray icon) ─────────────────────────────

    [ObservableProperty] private string? _noticeText;
    private Action? _noticeAction;
    public bool HasNotice => NoticeText != null;
    public bool NoticeHasAction => _noticeAction != null;
    partial void OnNoticeTextChanged(string? value) { OnPropertyChanged(nameof(HasNotice)); OnPropertyChanged(nameof(NoticeHasAction)); }

    public void ShowNotice(string text, Action? onClick)
    {
        _noticeAction = onClick;
        NoticeText = text;
        var w = Application.Current.MainWindow;
        if (w is { IsVisible: false }) ShowMainWindow();
    }

    [RelayCommand] private void NoticeAction() { var a = _noticeAction; NoticeText = null; _noticeAction = null; a?.Invoke(); }
    [RelayCommand] private void DismissNotice() { NoticeText = null; _noticeAction = null; }
    [RelayCommand] private void DismissUpdatedNotice() => ShowUpdatedNotice = false;
    [RelayCommand] private void OpenReleases() => Dialogs.OpenUrl(UpdateChecker.ReleasesUrl);

    [RelayCommand]
    private void OpenLogFolder() => Dialogs.OpenFolder(AppPaths.DataRoot);
}

/// <summary>Pages that want a hook each time they are shown.</summary>
public interface IActivatable
{
    void OnActivated();
}
