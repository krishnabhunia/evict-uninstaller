using System.Windows;
using System.Windows.Threading;
using CommunityToolkit.Mvvm.ComponentModel;
using Evict.App.ViewModels;
using Evict.Core.Models;
using Evict.Core.Services;
using WF = System.Windows.Forms;

namespace Evict.App.Services;

/// <summary>
/// Everything Evict does while its window is closed or hidden: the tray icon, installer detection with automatic
/// Install-Monitor recording, and notifications. One instance per process, owned by <see cref="App"/>.
/// </summary>
public sealed partial class BackgroundCoordinator : ObservableObject
{
    private readonly AppServices _services;
    private readonly MainViewModel _main;
    private readonly InstallerDetector _detector = new();
    private readonly Dispatcher _ui = Application.Current.Dispatcher;
    private CancellationTokenSource? _recordingCts;
    private int _recordingPid;
    private bool _closeToTrayHintShown;

    public BackgroundCoordinator(AppServices services, MainViewModel main)
    {
        _services = services;
        _main = main;
        _detector.Detected += d => _ui.BeginInvoke(() => OnInstallerDetected(d));
        // Everything Evict launches itself (uninstallers, Install Monitor runs, winget) is never reported as a new installer.
        ProcessRunner.ProcessStarted += pid => _detector.IgnoreProcess(pid);
    }

    public TrayIcon? Tray { get; private set; }
    public bool HasTray => Tray != null;
    public bool DetectionRunning => _detector.IsRunning && !_detector.IsPaused;

    // ── recording state (shown on the Install Monitor page and in the tray menu) ──
    [ObservableProperty] private bool _isRecording;
    [ObservableProperty] private string? _recordingTitle;
    [ObservableProperty] private string _recordingStatus = "";
    [ObservableProperty] private double _recordingProgress;
    [ObservableProperty] private bool _recordingIndeterminate = true;
    public InstallLog? LastRecordedLog { get; private set; }
    public event Action<InstallLog>? Recorded;

    // ───────────────────────────── lifecycle ─────────────────────────────

    /// <summary>Applies the current settings: creates/destroys the tray icon and starts/stops installer detection.</summary>
    public void ApplySettings(bool forceTray = false)
    {
        var s = _services.Settings.Current;
        if (s.ShowTrayIcon || forceTray) EnsureTray(); else DisposeTray();

        var detect = !string.Equals(s.InstallerDetection, "Off", StringComparison.OrdinalIgnoreCase);
        if (detect) { if (!_detector.IsRunning) _detector.Start(); _detector.IsPaused = false; }
        else if (_detector.IsRunning) _detector.Stop();
        if (Tray != null) Tray.DetectionChecked = detect;
        OnPropertyChanged(nameof(HasTray));
        OnPropertyChanged(nameof(DetectionRunning));
    }

    private void EnsureTray()
    {
        if (Tray != null) return;
        try
        {
            var s = _services.Settings.Current;
            Tray = new TrayIcon(!string.Equals(s.InstallerDetection, "Off", StringComparison.OrdinalIgnoreCase));
            Tray.OpenRequested += () => _main.ShowMainWindow();
            Tray.ScanRequested += () => { _main.ShowMainWindow(); _main.Navigate(PageKey.Health); _ = _main.GetPage<HealthViewModel>(PageKey.Health).ScanAsync(); };
            Tray.WidgetRequested += () => _main.ShowWidgetCommand.Execute(null);
            Tray.RecordRequested += RecordManually;
            Tray.SettingsRequested += () => { _main.ShowMainWindow(); _main.Navigate(PageKey.Settings); };
            Tray.ExitRequested += () => _main.ExitApplication();
            Tray.DetectionToggled += on =>
            {
                _services.Settings.Current.InstallerDetection = on ? (_services.Settings.Current.InstallerDetection == "Auto" ? "Auto" : "Ask") : "Off";
                _services.Settings.Save();
                ApplySettings();
            };
        }
        catch (Exception ex) { Log.Error("Tray icon could not be created", ex); Tray = null; }
    }

    private void DisposeTray()
    {
        Tray?.Dispose();
        Tray = null;
    }

    public void Shutdown()
    {
        try { _recordingCts?.Cancel(); } catch { /* ignore */ }
        _detector.Dispose();
        DisposeTray();
    }

    /// <summary>Called by the main window when the user closes it while "close to tray" is on.</summary>
    public void OnHiddenToTray()
    {
        if (_closeToTrayHintShown) return;
        _closeToTrayHintShown = true;
        Notify("Evict is still running", "It keeps watching for installers from the notification area. Right-click the icon → Exit Evict to quit.", () => _main.ShowMainWindow());
    }

    /// <summary>Balloon/toast when a tray icon exists; otherwise an in-window notice bar.</summary>
    public void Notify(string title, string text, Action? onClick = null, bool warning = false)
    {
        if (Tray != null) Tray.ShowBalloon(title, text, onClick, warning ? WF.ToolTipIcon.Warning : WF.ToolTipIcon.Info);
        else _main.ShowNotice(title + " – " + text, onClick);
    }

    // ───────────────────────────── installer detection ─────────────────────────────

    private void OnInstallerDetected(DetectedInstaller d)
    {
        var mode = _services.Settings.Current.InstallerDetection;
        if (string.Equals(mode, "Off", StringComparison.OrdinalIgnoreCase)) return;
        if (IsRecording)
        {
            Notify("Another installer started", $"{d.DisplayName} started while \"{RecordingTitle}\" is still being recorded. Only one installation can be recorded at a time.", null, warning: true);
            return;
        }
        if (string.Equals(mode, "Auto", StringComparison.OrdinalIgnoreCase))
        {
            _ = RecordAsync(d);
            return;
        }
        // "Ask" still starts recording at once – waiting for a click would miss the installation.
        // The prompt only decides whether the log is kept.
        _ = RecordAsync(d);
        _main.PendingInstaller = d;
        Notify("Recording installation: " + d.DisplayName,
            $"{d.FileName} just started and Evict is recording what it installs, so it can be removed completely later. Click to open Evict if you don't want this log.",
            () => _main.ShowMainWindow());
        if (Tray is null && Application.Current.MainWindow is { IsVisible: false }) _main.ShowMainWindow(); // no tray → the banner is the only prompt
    }

    /// <summary>Stops the recording started for the pending installer without saving a log.</summary>
    public void DiscardRecording(DetectedInstaller d)
    {
        if (IsRecording && _recordingPid == d.ProcessId) CancelRecording();
    }

    /// <summary>Records a running installation; safe to call from the UI thread (returns immediately, work continues in the background).</summary>
    public async Task RecordAsync(DetectedInstaller d)
    {
        if (!_ui.CheckAccess()) { await _ui.InvokeAsync(() => RecordAsync(d)); return; }
        if (IsRecording) return;
        _recordingPid = d.ProcessId;
        IsRecording = true;
        RecordingTitle = d.DisplayName;
        RecordingIndeterminate = true;
        RecordingStatus = "Taking snapshot…";
        Tray?.SetRecording(d.DisplayName);
        _recordingCts = new CancellationTokenSource();
        var tree = _detector.Track(d.ProcessId);
        var progress = new Progress<ProgressReport>(r =>
        {
            RecordingStatus = r.Message;
            if (r.Percent is { } p) { RecordingIndeterminate = false; RecordingProgress = p; }
        });
        try
        {
            var log = await _services.Monitor.MonitorRunningInstallAsync(d, tree, progress, _recordingCts.Token);
            LastRecordedLog = log;
            Recorded?.Invoke(log);
            var summary = $"{log.CreatedDirectories.Count} folders, {log.CreatedFiles.Count} files and {log.CreatedRegistryKeys.Count} registry keys were recorded.";
            RecordingStatus = "Recorded " + log.Title + ": " + summary;
            Notify("Recorded: " + log.Title, summary + " Click to open Install Monitor.", () => ShowLog(log));
            if (_main.IsPageLoaded(PageKey.InstallMonitor)) _main.GetPage<InstallMonitorViewModel>(PageKey.InstallMonitor).Reload();
        }
        catch (OperationCanceledException) { RecordingStatus = "Recording cancelled."; }
        catch (Exception ex)
        {
            Log.Error("Automatic recording failed", ex);
            RecordingStatus = "Recording failed: " + ex.Message;
            Notify("Recording failed", ex.Message, null, warning: true);
        }
        finally
        {
            _detector.Untrack(tree);
            if (_main.PendingInstaller?.ProcessId == d.ProcessId) _main.PendingInstaller = null;
            IsRecording = false;
            RecordingTitle = null;
            Tray?.SetRecording(null);
            _recordingCts?.Dispose(); _recordingCts = null;
        }
    }

    /// <summary>Software Updater runs installers through winget/msiexec (not children of Evict) – don't report those.</summary>
    public void SuspendDetection(bool suspend) => _detector.IsPaused = suspend;

    public void CancelRecording() { try { _recordingCts?.Cancel(); } catch { /* ignore */ } }

    private void ShowLog(InstallLog log)
    {
        _main.ShowMainWindow();
        _main.Navigate(PageKey.InstallMonitor);
        var vm = _main.GetPage<InstallMonitorViewModel>(PageKey.InstallMonitor);
        vm.Reload();
        vm.SelectedLog = vm.Logs.FirstOrDefault(l => l.Log.Id == log.Id) ?? vm.SelectedLog;
    }

    private void RecordManually()
    {
        _main.ShowMainWindow();
        _main.Navigate(PageKey.InstallMonitor);
        _main.GetPage<InstallMonitorViewModel>(PageKey.InstallMonitor).MonitorNewInstallCommand.Execute(null);
    }
}
