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
        if (_main.LastUpdateCheckResult is { } result) RefreshUpdateResult(result);
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

    public IReadOnlyList<KeyValuePair<string, string>> RunningProgramOptions { get; } = new[]
    {
        new KeyValuePair<string, string>("Ask", "Ask me to close it (recommended)"),
        new KeyValuePair<string, string>("Close", "Close it automatically (force-close if it does not respond)"),
        new KeyValuePair<string, string>("Ignore", "Leave it running"),
    };

    public string RunningProgramAction
    {
        get => S.RunningProgramAction;
        set { S.RunningProgramAction = value is "Ask" or "Close" or "Ignore" ? value : "Ask"; Save(); OnPropertyChanged(); }
    }

    public IReadOnlyList<KeyValuePair<string, string>> InstallerDetectionOptions { get; } = new[]
    {
        new KeyValuePair<string, string>("Off", "Off – never watch for installers"),
        new KeyValuePair<string, string>("Ask", "Record and notify - keep the log unless I discard it"),
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
            ? (IsScheduledScanOn ? $"Scheduled: {ScheduledScanTask.Describe(ScheduledScan, ScheduledScanHour, ScheduledScanWeekday)} (for this Windows user). You get a notification with the result; nothing is deleted automatically." : "No scheduled scan.")
            : "Task Scheduler refused the change: " + error;
    }

    public async Task RefreshScheduleStatusAsync()
    {
        var migration = await _services.Scheduler.MigrateLegacyAsync(ScheduledScan, ScheduledScanHour, ScheduledScanWeekday, CancellationToken.None);
        if (!migration.Ok)
        {
            ScheduleStatusText = "Could not update the old scheduled scan: " + migration.Error;
            return;
        }
        if (!IsScheduledScanOn) { ScheduleStatusText = "No scheduled scan."; return; }
        var exists = await _services.Scheduler.ExistsAsync(CancellationToken.None);
        ScheduleStatusText = exists
            ? $"Scheduled: {ScheduledScanTask.Describe(ScheduledScan, ScheduledScanHour, ScheduledScanWeekday)}" + (S.LastScheduledScanUtc is { } t ? $" · last run {t.ToLocalTime():g}" : "")
            : "The scheduled task is missing (removed outside Evict?). Choose the schedule again to recreate it.";
    }

    // ───────────── updates ─────────────

    public bool CheckForUpdates { get => S.CheckForUpdates; set { S.CheckForUpdates = value; Save(); OnPropertyChanged(); } }
    public bool IncludeBetaUpdates { get => _main.IncludeBetaUpdates; set => _main.IncludeBetaUpdates = value; }
    public string UpdateChannelText => IncludeBetaUpdates ? "Stable releases and optional beta/prerelease builds. Every installation requires your approval." : "Stable releases only. Enable beta updates to try prerelease builds.";

    public void RefreshUpdateChannel()
    {
        UpdateAvailable = false;
        HasUpdateNetworkAccessIssue = false;
        UpdateStatusText = "Update channel changed. Check again to see releases in this channel.";
        OnPropertyChanged(nameof(IncludeBetaUpdates));
        OnPropertyChanged(nameof(UpdateChannelText));
    }
    [ObservableProperty] private string _updateStatusText = "";
    [ObservableProperty] private bool _isCheckingForUpdates;
    public bool CanChangeUpdateChannel => !IsCheckingForUpdates && !_main.IsCheckingForUpdates;
    partial void OnIsCheckingForUpdatesChanged(bool value) => RefreshUpdateCheckState();
    public void RefreshUpdateCheckState() => OnPropertyChanged(nameof(CanChangeUpdateChannel));
    [ObservableProperty] private bool _updateAvailable;
    [ObservableProperty] private bool _hasUpdateNetworkAccessIssue;
    public string ExecutablePath => UpdateService.ExePath;
    public string EditionText => UpdateService.IsInstalledMode() ? "Installed with Setup (updates run the new installer)" : "Portable edition (updates replace Evict.exe in place)";
    public string ReleasesUrl => UpdateChecker.ReleasesUrl;

    public void RefreshUpdateResult(UpdateCheckResult result)
    {
        UpdateStatusText = result.Message;
        UpdateAvailable = result.Status == UpdateStatus.UpdateAvailable;
        HasUpdateNetworkAccessIssue = result.NetworkAccessBlocked;
    }

    [RelayCommand(IncludeCancelCommand = true)]
    private async Task CheckForUpdatesNowAsync(CancellationToken ct)
    {
        if (IsCheckingForUpdates || _main.IsCheckingForUpdates) return;
        IsCheckingForUpdates = true;
        HasUpdateNetworkAccessIssue = false;
        UpdateAvailable = false;
        UpdateStatusText = "Checking GitHub Releases…";
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(ct, App.ShutdownToken);
        try
        {
            var result = await _main.CheckForUpdatesAsync(cancellation.Token);
            if (result is null) { RefreshUpdateChannel(); return; }
            RefreshUpdateResult(result);
            if (UpdateAvailable && result.Release != null)
            {
                S.SkippedUpdateVersion = null; // the user asked explicitly – show it even if skipped before
                Save();
                IsCheckingForUpdates = false;
                _main.OfferUpdate(result.Release, fromUser: true);
            }
        }
        catch (OperationCanceledException) when (cancellation.IsCancellationRequested) { UpdateStatusText = "Update check cancelled."; }
        catch (Exception ex) { UpdateStatusText = "Update check failed: " + ex.Message; }
        finally { IsCheckingForUpdates = false; }
    }

    [RelayCommand] private void ShowUpdate() => _main.ShowUpdateCommand.Execute(null);
    [RelayCommand] private void OpenReleases() => Dialogs.OpenUrl(UpdateChecker.ReleasesUrl);
    [RelayCommand] private void OpenUpdateConnectionHelp() => Dialogs.OpenUrl(UpdateNetworkDiagnostics.FirewallHelpUrl);
    [RelayCommand] private void OpenApplicationFolder() => Dialogs.OpenFolder(Path.GetDirectoryName(ExecutablePath) ?? AppContext.BaseDirectory);
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

    public bool StartAsAdministrator { get => S.StartAsAdministrator; set { S.StartAsAdministrator = value; Save(); OnPropertyChanged(); } }

    /// <summary>A standard account would be elevated as a different user, so the setting has no effect there.</summary>
    public string StartAsAdministratorHint =>
        !IsElevated && !ElevationHelper.CanElevateSameUser
            ? "Your Windows account is not an administrator, so Evict starts with your own rights (an administrator password would run it under that administrator's account and clean the wrong profile)."
            : "Windows asks for permission each time the Evict window opens. Starts hidden in the notification area (sign-in, scheduled scans) ask only when you open the window. While Evict runs as administrator, Windows blocks dragging files onto it from Explorer – use the Browse buttons instead.";
    public string VersionText => "Version " + _services.Updater.CurrentVersionLabel;
    public string DataFolder => AppPaths.DataRoot;
    public string RuntimeText => $".NET {Environment.Version} · {(Environment.Is64BitProcess ? "64-bit" : "32-bit")} · {Environment.OSVersion.VersionString}";
    public string ElevationText => IsElevated ? "Running as administrator" : "Running as a standard user – some operations will prompt or be limited";

    private void Save() => _services.Settings.Save();

    [RelayCommand] private void OpenDataFolder() => Dialogs.OpenFolder(AppPaths.DataRoot);

    /// <summary>
    /// Installed copies are removed by their uninstaller (which asks about leftovers). A portable copy closes and
    /// restarts itself in "--self-cleanup" mode, which removes its data once this process has exited.
    /// </summary>
    [RelayCommand]
    private void RemoveEvict()
    {
        if (UpdateService.IsInstalledMode())
        {
            if (Dialogs.Confirm("Evict was installed with Setup. Uninstall it from Programs and Features (or Settings → Apps) – the uninstaller then asks which of Evict's settings, history and backups to remove.\n\nOpen Programs and Features now?"))
            {
                try { System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo("control.exe", "appwiz.cpl") { UseShellExecute = true }); } catch { /* ignore */ }
            }
            return;
        }
        if (!Dialogs.Confirm("Evict will close and then let you choose which of its files and registry entries to remove from this PC (settings, history, registry backups, browser-settings backups, Windows' records of Evict).\n\nEvict.exe itself is not deleted – remove it afterwards.\n\nContinue?", destructive: true))
            return;
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(UpdateService.ExePath, $"--self-cleanup ask --portable --wait-pid {Environment.ProcessId}") { UseShellExecute = false });
        }
        catch (Exception ex) { Dialogs.Error("Could not start the clean-up: " + ex.Message); return; }
        App.Quit();
    }
    [RelayCommand] private void OpenLogFile() => Dialogs.OpenFolder(AppPaths.LogFile);

    [RelayCommand]
    private async Task ResetDefaultsAsync()
    {
        if (!Dialogs.Confirm("Reset all settings to their defaults?\n\nThis also removes the Explorer context-menu entry, the Windows start-up entry and the scheduled scan.")) return;
        var theme = S.Theme;
        bool hadContextMenu = ExplorerContextMenu, hadAutostart = StartWithWindows;
        var previousSchedule = (S.ScheduledScan, S.ScheduledScanHour, S.ScheduledScanWeekday);
        var errors = new List<string>();
        var contextResult = hadContextMenu ? ShellIntegration.Unregister() : (true, (string?)null);
        if (!contextResult.Item1) errors.Add("Explorer context menu: " + contextResult.Item2);
        var startupResult = hadAutostart ? StartupRegistration.Set(false) : (true, (string?)null);
        if (!startupResult.Item1) errors.Add("Windows start-up entry: " + startupResult.Item2);
        // Query actual Task Scheduler state even if the settings file says Off (Setup or an older copy may own it).
        var scheduleResult = await _services.Scheduler.ApplyAsync("Off", 0, 0, CancellationToken.None);
        if (!scheduleResult.Ok) errors.Add("Scheduled scan: " + scheduleResult.Error);
        var fresh = new AppSettings();
        foreach (var p in typeof(AppSettings).GetProperties())
        {
            if (p.Name == nameof(AppSettings.SettingsVersion) || !p.CanWrite) continue;
            p.SetValue(S, p.GetValue(fresh));
        }
        if (!contextResult.Item1) S.ExplorerContextMenu = ShellIntegration.IsRegistered();
        if (!startupResult.Item1) S.StartWithWindows = StartupRegistration.IsEnabled();
        if (!scheduleResult.Ok)
            (S.ScheduledScan, S.ScheduledScanHour, S.ScheduledScanWeekday) = previousSchedule;
        if (theme != S.Theme) App.ApplyTheme(S.Theme);
        Save();
        _main.UpdateChannelChanged();
        App.UiState.Scale = UiState.Clamp(S.UiScale);
        ScheduleStatusText = scheduleResult.Ok ? "No scheduled scan." : "Could not remove the scheduled scan: " + scheduleResult.Error;
        App.Background.ApplySettings();
        ProgramsChanged = true;
        OnPropertyChanged(string.Empty);
        if (errors.Count > 0) Dialogs.Error("Settings were reset, but some Windows integrations could not be removed:\n\n" + string.Join("\n", errors));
    }
}
