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

public enum PageKey { Health, Programs, WindowsApps, BrowserExtensions, SoftwareUpdater, InstallMonitor, Tools, History, Settings }

/// <summary>One entry in the menu (RD1): Home, Apps, Updates, Clean, History, Settings. An entry can hold several pages (shown as tabs).</summary>
public sealed partial class NavItemViewModel : ObservableObject
{
    public required PageKey Key { get; init; }
    public required string Title { get; init; }
    public required string Glyph { get; init; }
    /// <summary>The pages this entry shows as tabs (the first one is <see cref="Key"/>).</summary>
    public PageKey[] Pages { get; init; } = [];
    /// <summary>Badge drawn in the accent colour (Updates) instead of grey.</summary>
    public bool BadgeIsAccent { get; init; }
    /// <summary>The tab that was open last, so clicking the entry again returns to it.</summary>
    public PageKey LastPage { get; set; }

    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string? _badge;
}

/// <summary>A tab under the page header (e.g. Apps → Programs / Store apps / Browser extensions).</summary>
public sealed partial class PageTabViewModel : ObservableObject
{
    public required PageKey Key { get; init; }
    public required string Title { get; init; }
    [ObservableProperty] private bool _isSelected;
    [ObservableProperty] private string? _badge;
}

public sealed partial class MainViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly UpdateCheckCoordinator _updateChecks;
    private readonly Dictionary<PageKey, ObservableObject> _pages = new();
    private EasyUninstallWindow? _widget;

    public MainViewModel(AppServices services)
    {
        _services = services;
        _updateChecks = new UpdateCheckCoordinator(_services.Updater.CheckAsync,
            () => _services.Settings.Current.CheckForUpdates,
            () => _services.Settings.Current.IncludeBetaUpdates);
        _updateChecks.CheckingChanged += value => IsCheckingForUpdates = value;
        NavItems = new ObservableCollection<NavItemViewModel>
        {
            new() { Key = PageKey.Health, Title = "Home", Glyph = "\uE80F", Pages = [PageKey.Health] },
            new() { Key = PageKey.Programs, Title = "Apps", Glyph = "\uE71D", Pages = [PageKey.Programs, PageKey.WindowsApps, PageKey.BrowserExtensions] },
            new() { Key = PageKey.SoftwareUpdater, Title = "Updates", Glyph = "\uE895", Pages = [PageKey.SoftwareUpdater], BadgeIsAccent = true },
            new() { Key = PageKey.Tools, Title = "Clean", Glyph = "\uE74D", Pages = [PageKey.Tools, PageKey.InstallMonitor] },
            new() { Key = PageKey.History, Title = "History", Glyph = "\uE81C", Pages = [PageKey.History] },
        };
        SettingsNav = new NavItemViewModel { Key = PageKey.Settings, Title = "Settings", Glyph = "\uE713", Pages = [PageKey.Settings] };
        foreach (var item in AllNav)
        {
            item.LastPage = item.Key;
            item.PropertyChanged += (s, e) =>
            {
                if (_navigating || e.PropertyName != nameof(NavItemViewModel.IsSelected)) return;
                if (s is NavItemViewModel { IsSelected: true } n) Navigate(n.LastPage);
            };
        }
        Navigate(PageKey.Health);
    }

    /// <summary>Menu entries above the line; Settings sits on its own at the bottom of the menu.</summary>
    public ObservableCollection<NavItemViewModel> NavItems { get; }
    public NavItemViewModel SettingsNav { get; }
    private IEnumerable<NavItemViewModel> AllNav => NavItems.Append(SettingsNav);
    private bool _navigating;

    [ObservableProperty] private ObservableObject? _currentPage;
    [ObservableProperty] private PageKey _currentKey = PageKey.Health;
    [ObservableProperty] private string _currentTitle = "Home";
    [ObservableProperty] private string _currentSubtitle = "";
    /// <summary>Tabs under the header for the open menu entry; empty when it has a single page.</summary>
    [ObservableProperty] private IReadOnlyList<PageTabViewModel> _currentTabs = [];
    public bool HasTabs => CurrentTabs.Count > 1;
    partial void OnCurrentTabsChanged(IReadOnlyList<PageTabViewModel> value) => OnPropertyChanged(nameof(HasTabs));

    /// <summary>Header titles and subtitles (plain words, RD1).</summary>
    public static (string Group, string Tab, string Subtitle) PageInfo(PageKey key) => key switch
    {
        PageKey.Health => ("Home", "Home", "A quick check of everything Evict looks after"),
        PageKey.Programs => ("Apps", "Programs", "Everything installed on this PC – tick several to remove them together"),
        PageKey.WindowsApps => ("Apps", "Store apps", "Apps from the Microsoft Store and the ones Windows comes with"),
        PageKey.BrowserExtensions => ("Apps", "Browser extensions", "Add-ons in Chrome, Edge, Brave, Vivaldi, Opera and Firefox"),
        PageKey.SoftwareUpdater => ("Updates", "Updates", "Keep your programs up to date"),
        PageKey.Tools => ("Clean", "Tools", "Free up space and tidy Windows – everything goes to the Recycle Bin first"),
        PageKey.InstallMonitor => ("Clean", "Install Monitor", "Record what an installer adds, so it can be removed completely later"),
        PageKey.History => ("History", "History", "Everything Evict has done on this PC"),
        PageKey.Settings => ("Settings", "Settings", "How Evict behaves"),
        _ => (key.ToString(), key.ToString(), ""),
    };
    [ObservableProperty] private bool _isWidgetVisible;

    public bool IsElevated => ElevationHelper.IsElevated;
    public bool ShowAdminBanner => !ElevationHelper.IsElevated && !_adminBannerDismissed;
    private bool _adminBannerDismissed;

    /// <summary>"Evict Uninstaller 1.5.0" – also what the taskbar and Alt+Tab show.</summary>
    public string WindowTitle => $"{AppPaths.ProductName} {_services.Updater.CurrentVersionLabel}" + (IsElevated ? "  (Administrator)" : "");
    /// <summary>Same source as the update check, so the number shown is the one compared with GitHub Releases.</summary>
    public string VersionText => "v" + _services.Updater.CurrentVersionLabel;

    public bool IncludeBetaUpdates
    {
        get => _services.Settings.Current.IncludeBetaUpdates;
        set
        {
            if (value == IncludeBetaUpdates || IsCheckingForUpdates) return;
            _services.Settings.Current.IncludeBetaUpdates = value;
            _services.Settings.Save();
            UpdateChannelChanged();
        }
    }

    [ObservableProperty] private bool _isCheckingForUpdates;
    partial void OnIsCheckingForUpdatesChanged(bool value)
    {
        OpenUpdatesCommand.NotifyCanExecuteChanged();
        if (_pages.TryGetValue(PageKey.Settings, out var settings) && settings is SettingsViewModel vm) vm.RefreshUpdateCheckState();
    }

    public void UpdateChannelChanged()
    {
        LastUpdateCheckResult = null;
        OnPropertyChanged(nameof(IncludeBetaUpdates));
        OnPropertyChanged(nameof(HasAvailableUpdate));
        if (AvailableUpdate?.IsPreview == true && !IncludeBetaUpdates)
        {
            AvailableUpdate = null;
            ShowUpdateBanner = false;
        }
        if (_pages.TryGetValue(PageKey.Settings, out var settings) && settings is SettingsViewModel vm) vm.RefreshUpdateChannel();
    }

    [RelayCommand] private void OpenUpdateSettings() => OpenSettings("Updates");

    /// <summary>Opens Settings at one of <see cref="SettingsSections.All"/>.</summary>
    public void OpenSettings(string section)
    {
        Navigate(PageKey.Settings);
        GetPage<SettingsViewModel>(PageKey.Settings).SelectedSection = section;
    }

    private bool CanOpenUpdates() => !IsCheckingForUpdates;

    [RelayCommand(CanExecute = nameof(CanOpenUpdates))]
    private async Task OpenUpdatesAsync()
    {
        if (AvailableUpdate is { } release && (!release.IsPreview || IncludeBetaUpdates))
        {
            ShowUpdate();
            return;
        }

        OpenSettings("Updates");
        await GetPage<SettingsViewModel>(PageKey.Settings).CheckForUpdatesNowCommand.ExecuteAsync(null);
    }

    public void Navigate(PageKey key)
    {
        var group = AllNav.First(n => n.Pages.Contains(key));
        _navigating = true;
        try
        {
            foreach (var n in AllNav) if (n.IsSelected != (n == group)) n.IsSelected = n == group;
            group.LastPage = key;
            if (CurrentTabs.Count == 0 || !CurrentTabs.Any(t => t.Key == key))
            {
                var tabs = group.Pages.Select(p => new PageTabViewModel { Key = p, Title = PageInfo(p).Tab }).ToList();
                foreach (var t in tabs)
                    t.PropertyChanged += (s, e) =>
                    {
                        if (!_navigating && e.PropertyName == nameof(PageTabViewModel.IsSelected) && s is PageTabViewModel { IsSelected: true } tab) Navigate(tab.Key);
                    };
                CurrentTabs = tabs;
            }
            foreach (var t in CurrentTabs) t.IsSelected = t.Key == key;
        }
        finally { _navigating = false; }

        var info = PageInfo(key);
        CurrentKey = key;
        CurrentTitle = info.Group;
        CurrentSubtitle = info.Subtitle;
        CurrentPage = GetOrCreate(key);
        if (CurrentPage is IActivatable a) a.OnActivated();
    }

    [RelayCommand] private void NavigateTo(PageKey key) => Navigate(key);

    /// <summary>Ctrl+K / the search box in the menu.</summary>
    [RelayCommand]
    private void OpenSearch()
    {
        var owner = Application.Current.MainWindow;
        if (owner is { IsVisible: true } && owner.OwnedWindows.OfType<SearchWindow>().Any()) return;
        new SearchWindow { DataContext = new SearchViewModel(this), Owner = owner is { IsVisible: true } ? owner : null }.Show();
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
            PageKey.SoftwareUpdater => WithUpdateBadge(new SoftwareUpdaterViewModel(_services)),
            PageKey.InstallMonitor => new InstallMonitorViewModel(_services, this),
            PageKey.Tools => new ToolsViewModel(_services, this),
            PageKey.History => new HistoryViewModel(_services, this),
            PageKey.Settings => new SettingsViewModel(_services, this),
            _ => throw new ArgumentOutOfRangeException(nameof(key)),
        };
        _pages[key] = vm;
        return vm;
    }

    /// <summary>The Updates menu entry shows how many programs have an update (accent badge).</summary>
    private SoftwareUpdaterViewModel WithUpdateBadge(SoftwareUpdaterViewModel vm)
    {
        vm.Items.CollectionChanged += (_, _) => SetBadge(PageKey.SoftwareUpdater, vm.Items.Count);
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
        if (App.TryRestartElevatedForWindow()) return;
        if (!w.IsVisible) w.Show();
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Activate();
        w.Topmost = true; w.Topmost = false;
    }

    /// <summary>Quits for real (bypasses "close to tray").</summary>
    public void ExitApplication() => App.Quit();

    public void SetBadge(PageKey key, int count)
    {
        var text = count > 0 ? count.ToString() : null;
        var item = AllNav.FirstOrDefault(n => n.Pages.Contains(key));
        if (item is null) return;
        if (item.Pages.Length == 1) { item.Badge = text; return; }
        _tabBadges[key] = text;
        foreach (var t in CurrentTabs) if (_tabBadges.TryGetValue(t.Key, out var b)) t.Badge = b;
        // The menu entry shows the first tab's count (Apps → number of programs).
        item.Badge = _tabBadges.GetValueOrDefault(item.Key);
    }
    private readonly Dictionary<PageKey, string?> _tabBadges = new();

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
            var title = health.Tiles.Any(t => t.State == TileState.Unavailable) ? "Software Health scan incomplete"
                : issues == 0 ? $"Software Health {health.Score}/100 - all good" : $"Software Health {health.Score}/100 - {issues} item(s) need attention";
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
            if (!Program.RestoreSingleInstance()) { App.Quit(); return; }
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
    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasAvailableUpdate))]
    [NotifyPropertyChangedFor(nameof(AvailableUpdateButtonText))]
    private ReleaseInfo? _availableUpdate;
    public bool HasAvailableUpdate => AvailableUpdate is { } release && (!release.IsPreview || IncludeBetaUpdates);
    public string AvailableUpdateButtonText => AvailableUpdate is { } release ? $"Update to v{release.DisplayVersion}" : "";

    [ObservableProperty] private bool _showUpdateBanner;
    [ObservableProperty] private string? _updateBannerText;
    /// <summary>Green "Evict was updated" notice shown once after a self-update (--updated).</summary>
    [ObservableProperty] private bool _showUpdatedNotice;
    public string UpdatedNoticeText => $"Evict was updated to version {_services.Updater.CurrentVersionLabel}.";

    /// <summary>One background check per application start; shutdown cancellation and failures stay silent.</summary>
    public async Task CheckForUpdatesOnStartupAsync(CancellationToken ct = default)
    {
        try
        {
            var result = await _updateChecks.CheckOnStartupAsync(ct);
            if (result is null) return;
            RecordUpdateCheck(result);
            if (result.Status == UpdateStatus.UpdateAvailable && result.Release != null) OfferUpdate(result.Release, fromUser: false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { /* normal shutdown */ }
        catch (Exception ex) { Log.Warn("Start-up update check failed: " + ex.Message); }
    }

    /// <summary>A manual check replaces a pending startup check and shares its in-flight request guard.</summary>
    public async Task<UpdateCheckResult?> CheckForUpdatesAsync(CancellationToken ct)
    {
        var result = await _updateChecks.CheckNowAsync(ct);
        if (result != null) RecordUpdateCheck(result);
        return result;
    }

    public UpdateCheckResult? LastUpdateCheckResult { get; private set; }

    private void RecordUpdateCheck(UpdateCheckResult result)
    {
        LastUpdateCheckResult = result;
        _services.Settings.Current.LastUpdateCheckUtc = DateTime.UtcNow;
        _services.Settings.Save();
        if (result.Status == UpdateStatus.UpToDate) { AvailableUpdate = null; ShowUpdateBanner = false; }
        Log.Info("Update check: " + result.Message);
        if (_pages.TryGetValue(PageKey.Settings, out var settings) && settings is SettingsViewModel vm) vm.RefreshUpdateResult(result);
    }

    /// <summary>Shows the banner (start-up) or the dialog directly (user clicked "Check now").</summary>
    public void OfferUpdate(ReleaseInfo release, bool fromUser)
    {
        if (release.IsPreview && !IncludeBetaUpdates) return;
        AvailableUpdate = release;
        var skipped = string.Equals(_services.Settings.Current.SkippedUpdateVersion, release.TagName, StringComparison.OrdinalIgnoreCase);
        UpdateBannerText = $"Evict {release.DisplayVersion}{(release.IsPreview ? " (beta/prerelease)" : "")} is available (you have {_services.Updater.CurrentVersionLabel}).";
        if (fromUser) ShowUpdate();
        else ShowUpdateBanner = !skipped;
    }

    [RelayCommand]
    private void ShowUpdate()
    {
        if (AvailableUpdate is null || AvailableUpdate.IsPreview && !IncludeBetaUpdates) return;
        var vm = new UpdateViewModel(_services, AvailableUpdate, this);
        new UpdateWindow { DataContext = vm, Owner = Application.Current.MainWindow }.ShowDialog();
        if (AvailableUpdate != null && string.Equals(_services.Settings.Current.SkippedUpdateVersion, AvailableUpdate.TagName, StringComparison.OrdinalIgnoreCase)) ShowUpdateBanner = false;
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
