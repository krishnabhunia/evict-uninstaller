using System.Reflection;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Evict.App.Services;
using Evict.Core.Services;

namespace Evict.App.ViewModels;

public sealed partial class SettingsViewModel : ObservableObject
{
    private readonly AppServices _services;
    private readonly MainViewModel _main;
    private AppSettings S => _services.Settings.Current;

    public SettingsViewModel(AppServices services, MainViewModel main)
    {
        _services = services;
        _main = main;
        App.UiState.PropertyChanged += (_, e) => { if (e.PropertyName == nameof(UiState.Scale)) OnPropertyChanged(nameof(TextSize)); };
        UpdateStatusText = S.LastUpdateCheckUtc is { } t ? $"Last checked {t.ToLocalTime():g}." : "Not checked yet.";
        _ = RefreshScheduleStatusAsync();
    }

    public bool IsDarkTheme
    {
        get => S.Theme == "Dark";
        set { S.Theme = value ? "Dark" : "Light"; App.ApplyTheme(S.Theme); Save(); OnPropertyChanged(); }
    }

    public IReadOnlyList<KeyValuePair<double, string>> TextSizeOptions => UiState.TextSizeOptions;
    public double TextSize
    {
        get => UiState.TextSizeOptions.Select(o => o.Key).OrderBy(k => Math.Abs(k - App.UiState.Scale)).First();
        set { App.UiState.Scale = UiState.Clamp(value); OnPropertyChanged(); }
    }

    public bool ExplorerContextMenu
    {
        get => S.ExplorerContextMenu || ShellIntegration.IsRegistered(); // Setup may have registered it too
        set
        {
            var (ok, error) = value ? ShellIntegration.Register() : ShellIntegration.Unregister();
            if (ok) { S.ExplorerContextMenu = value; Save(); }
            else Dialogs.Error("Could not update the Explorer context menu: " + error);
            OnPropertyChanged();
        }
    }

    public bool HealthAutoScan { get => S.HealthAutoScan; set { S.HealthAutoScan = value; Save(); OnPropertyChanged(); } }

    // ───────────── notification area & background ─────────────

    public bool ShowTrayIcon
    {
        get => S.ShowTrayIcon;
        set
        {
            S.ShowTrayIcon = value;
            if (!value) { S.CloseToTray = false; S.MinimizeToTray = false; }
            Save(); App.Background.ApplySettings();
            OnPropertyChanged(); OnPropertyChanged(nameof(CloseToTray)); OnPropertyChanged(nameof(MinimizeToTray)); OnPropertyChanged(nameof(DetectionStatusText));
        }
    }
    public bool CloseToTray { get => S.CloseToTray; set { S.CloseToTray = value; if (value) S.ShowTrayIcon = true; Save(); App.Background.ApplySettings(); OnPropertyChanged(); OnPropertyChanged(nameof(ShowTrayIcon)); } }
    public bool MinimizeToTray { get => S.MinimizeToTray; set { S.MinimizeToTray = value; if (value) S.ShowTrayIcon = true; Save(); App.Background.ApplySettings(); OnPropertyChanged(); OnPropertyChanged(nameof(ShowTrayIcon)); } }
    public bool StartWithWindows
    {
        get => S.StartWithWindows || StartupRegistration.IsEnabled();
        set
        {
            var (ok, error) = StartupRegistration.Set(value);
            if (ok) { S.StartWithWindows = value; if (value) { S.ShowTrayIcon = true; S.CloseToTray = true; } Save(); App.Background.ApplySettings(); }
            else Dialogs.Error("Could not change the Windows start-up entry: " + error);
            OnPropertyChanged(); OnPropertyChanged(nameof(ShowTrayIcon)); OnPropertyChanged(nameof(CloseToTray));
        }
    }

    public IReadOnlyList<KeyValuePair<string, string>> InstallerDetectionOptions { get; } = new[]
    {
        new KeyValuePair<string, string>("Off", "Off – never watch for installers"),
        new KeyValuePair<string, string>("Ask", "Ask – show a notification, record when I click it"),
        new KeyValuePair<string, string>("Auto", "Automatic – record every detected installation"),
    };
    public string InstallerDetection
    {
        get => InstallerDetectionOptions.Any(o => o.Key == S.InstallerDetection) ? S.InstallerDetection : "Ask";
        set { S.InstallerDetection = value; Save(); App.Background.ApplySettings(); OnPropertyChanged(); OnPropertyChanged(nameof(DetectionStatusText)); }
    }
    public string DetectionStatusText => App.Background.DetectionRunning
        ? "Watching for new installer processes (only while Evict or its tray icon is running)."
        : "Not watching. Turn detection on and keep Evict in the notification area (or start it with Windows) to record installations automatically.";

    // ───────────── scheduled scan ─────────────

    public IReadOnlyList<KeyValuePair<string, string>> ScheduledScanOptions { get; } = new[]
    {
        new KeyValuePair<string, string>("Off", "Off"),
        new KeyValuePair<string, string>("Daily", "Every day"),
        new KeyValuePair<string, string>("Weekly", "Once a week"),
    };
    public IReadOnlyList<KeyValuePair<int, string>> HourOptions { get; } = Enumerable.Range(0, 24).Select(h => new KeyValuePair<int, string>(h, new DateTime(2000, 1, 1, h, 0, 0).ToString("t"))).ToList();
    public IReadOnlyList<KeyValuePair<int, string>> WeekdayOptions { get; } = Enumerable.Range(0, 7).Select(d => new KeyValuePair<int, string>(d, System.Globalization.CultureInfo.CurrentCulture.DateTimeFormat.GetDayName((DayOfWeek)d))).ToList();

    public string ScheduledScan
    {
        get => ScheduledScanTask.IsValidMode(S.ScheduledScan) ? S.ScheduledScan : "Off";
        set { S.ScheduledScan = value; Save(); OnPropertyChanged(); OnPropertyChanged(nameof(IsScheduledScanOn)); OnPropertyChanged(nameof(IsWeeklyScan)); _ = ApplyScheduleAsync(); }
    }
    public int ScheduledScanHour { get => Math.Clamp(S.ScheduledScanHour, 0, 23); set { S.ScheduledScanHour = Math.Clamp(value, 0, 23); Save(); OnPropertyChanged(); _ = ApplyScheduleAsync(); } }
    public int ScheduledScanWeekday { get => Math.Clamp(S.ScheduledScanWeekday, 0, 6); set { S.ScheduledScanWeekday = Math.Clamp(value, 0, 6); Save(); OnPropertyChanged(); _ = ApplyScheduleAsync(); } }
    public bool IsScheduledScanOn => ScheduledScan != "Off";
    public bool IsWeeklyScan => ScheduledScan == "Weekly";
    [ObservableProperty] private string _scheduleStatusText = "";

    private async Task ApplyScheduleAsync()
    {
        ScheduleStatusText = "Updating Task Scheduler…";
        var (ok, error) = await _services.Scheduler.ApplyAsync(ScheduledScan, ScheduledScanHour, ScheduledScanWeekday, CancellationToken.None);
        ScheduleStatusText = ok
            ? (IsScheduledScanOn ? $"Scheduled: {ScheduledScanTask.Describe(ScheduledScan, ScheduledScanHour, ScheduledScanWeekday)} (Task Scheduler library → 'Evict Software Health scan'). You get a notification with the result; nothing is deleted automatically." : "No scheduled scan.")
            : "Task Scheduler refused the change: " + error;
    }

    public async Task RefreshScheduleStatusAsync()
    {
        if (!IsScheduledScanOn) { ScheduleStatusText = "No scheduled scan."; return; }
        var exists = await _services.Scheduler.ExistsAsync(CancellationToken.None);
        ScheduleStatusText = exists
            ? $"Scheduled: {ScheduledScanTask.Describe(ScheduledScan, ScheduledScanHour, ScheduledScanWeekday)}" + (S.LastScheduledScanUtc is { } t ? $" · last run {t.ToLocalTime():g}" : "")
            : "The scheduled task is missing (removed outside Evict?). Choose the schedule again to recreate it.";
    }

    // ───────────── updates ─────────────

    public bool CheckForUpdates { get => S.CheckForUpdates; set { S.CheckForUpdates = value; Save(); OnPropertyChanged(); } }
    [ObservableProperty] private string _updateStatusText = "";
    [ObservableProperty] private bool _isCheckingForUpdates;
    [ObservableProperty] private bool _updateAvailable;
    public string EditionText => UpdateService.IsInstalledMode() ? "Installed with Setup (updates run the new installer)" : "Portable edition (updates replace Evict.exe in place)";
    public string ReleasesUrl => UpdateChecker.ReleasesUrl;

    [RelayCommand]
    private async Task CheckForUpdatesNowAsync()
    {
        if (IsCheckingForUpdates) return;
        IsCheckingForUpdates = true;
        UpdateStatusText = "Checking GitHub Releases…";
        try
        {
            var result = await _services.Updater.CheckAsync(CancellationToken.None);
            S.LastUpdateCheckUtc = DateTime.UtcNow;
            Save();
            UpdateStatusText = result.Message;
            UpdateAvailable = result.Status == UpdateStatus.UpdateAvailable;
            if (UpdateAvailable && result.Release != null)
            {
                S.SkippedUpdateVersion = null; // the user asked explicitly – show it even if skipped before
                _main.OfferUpdate(result.Release, fromUser: true);
            }
        }
        catch (Exception ex) { UpdateStatusText = "Update check failed: " + ex.Message; }
        finally { IsCheckingForUpdates = false; }
    }

    [RelayCommand] private void ShowUpdate() => _main.ShowUpdateCommand.Execute(null);
    [RelayCommand] private void OpenReleases() => Dialogs.OpenUrl(UpdateChecker.ReleasesUrl);
    public bool CreateRestorePoint { get => S.CreateRestorePoint; set { S.CreateRestorePoint = value; Save(); OnPropertyChanged(); } }
    public bool QuietUninstall { get => S.QuietUninstall; set { S.QuietUninstall = value; Save(); OnPropertyChanged(); } }
    public bool AutoCleanLeftovers { get => S.AutoCleanLeftovers; set { S.AutoCleanLeftovers = value; Save(); OnPropertyChanged(); } }
    public bool SendToRecycleBin { get => S.SendToRecycleBin; set { S.SendToRecycleBin = value; Save(); OnPropertyChanged(); } }
    public bool ShowSystemComponents { get => S.ShowSystemComponents; set { S.ShowSystemComponents = value; Save(); OnPropertyChanged(); ProgramsChanged = true; } }
    public bool ShowWindowsUpdatesInPrograms { get => S.ShowWindowsUpdatesInPrograms; set { S.ShowWindowsUpdatesInPrograms = value; Save(); OnPropertyChanged(); ProgramsChanged = true; } }
    public bool ScanAllUserProfiles { get => S.ScanAllUserProfiles; set { S.ScanAllUserProfiles = value; Save(); OnPropertyChanged(); } }
    public bool MeasureFolderSizes { get => S.MeasureFolderSizes; set { S.MeasureFolderSizes = value; Save(); OnPropertyChanged(); ProgramsChanged = true; } }
    public int LargeProgramThresholdMb { get => S.LargeProgramThresholdMb; set { S.LargeProgramThresholdMb = Math.Clamp(value, 10, 100000); Save(); OnPropertyChanged(); ProgramsChanged = true; } }
    public int RecentlyInstalledDays { get => S.RecentlyInstalledDays; set { S.RecentlyInstalledDays = Math.Clamp(value, 1, 3650); Save(); OnPropertyChanged(); ProgramsChanged = true; } }
    public int InfrequentlyUsedDays { get => S.InfrequentlyUsedDays; set { S.InfrequentlyUsedDays = Math.Clamp(value, 1, 3650); Save(); OnPropertyChanged(); ProgramsChanged = true; } }

    /// <summary>Set when a setting that changes the Programs list was modified; the Programs page refreshes on next activation.</summary>
    public bool ProgramsChanged { get; set; }

    public bool IsElevated => ElevationHelper.IsElevated;
    public string VersionText => "Version " + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? "1.0.0");
    public string DataFolder => AppPaths.DataRoot;
    public string RuntimeText => $".NET {Environment.Version} · {(Environment.Is64BitProcess ? "64-bit" : "32-bit")} · {Environment.OSVersion.VersionString}";
    public string ElevationText => IsElevated ? "Running as administrator" : "Running as a standard user – some operations will prompt or be limited";

    private void Save() => _services.Settings.Save();

    [RelayCommand] private void OpenDataFolder() => Dialogs.OpenFolder(AppPaths.DataRoot);
    [RelayCommand] private void OpenLogFile() => Dialogs.OpenFolder(AppPaths.LogFile);

    [RelayCommand]
    private void ResetDefaults()
    {
        if (!Dialogs.Confirm("Reset all settings to their defaults?\n\nThis also removes the Explorer context-menu entry, the Windows start-up entry and the scheduled scan.")) return;
        var theme = S.Theme;
        bool hadContextMenu = S.ExplorerContextMenu, hadAutostart = S.StartWithWindows, hadSchedule = IsScheduledScanOn;
        var fresh = new AppSettings();
        foreach (var p in typeof(AppSettings).GetProperties())
        {
            if (p.Name == nameof(AppSettings.SettingsVersion) || !p.CanWrite) continue;
            p.SetValue(S, p.GetValue(fresh));
        }
        if (theme != S.Theme) App.ApplyTheme(S.Theme);
        Save();
        App.UiState.Scale = UiState.Clamp(S.UiScale);
        if (hadContextMenu) ShellIntegration.Unregister();
        if (hadAutostart) StartupRegistration.Set(false);
        if (hadSchedule) _ = ApplyScheduleAsync(); else ScheduleStatusText = "No scheduled scan.";
        App.Background.ApplySettings();
        ProgramsChanged = true;
        OnPropertyChanged(string.Empty);
    }
}
