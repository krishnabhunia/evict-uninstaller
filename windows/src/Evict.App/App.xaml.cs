using System.Windows;
using System.Windows.Threading;
using Evict.App.Services;
using Evict.App.ViewModels;
using Evict.App.Views;
using Evict.Core.Services;
using Evict.Core.Util;

namespace Evict.App;

public partial class App : Application
{
    private System.Threading.Timer? _historyCleanupTimer;
    private static readonly CancellationTokenSource ShutdownCancellation = new();
    private readonly Queue<string[]> _pendingStartupArgs = new();
    private MainViewModel? _readyMain;
    private bool _startupComplete;
    public static CancellationToken ShutdownToken => ShutdownCancellation.Token;
    public static AppServices Services { get; private set; } = null!;
    public static UiState UiState { get; } = new();
    public static BackgroundCoordinator Background { get; private set; } = null!;
    /// <summary>Set when the user really wants to quit, including cancellation of startup.</summary>
    public static bool IsExiting { get; set; }
    /// <summary>--tray / --scheduled-scan: the window is not shown at start.</summary>
    public static bool StartedHeadless { get; private set; }
    /// <summary>Self-cleanup has its own minimal lifecycle and never shows the startup splash.</summary>
    public static CommandLineOptions? SelfCleanupMode { get; set; }

    public static void Quit()
    {
        IsExiting = true;
        ShutdownCancellation.Cancel();
        Current.Shutdown();
    }

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (SelfCleanupMode != null) { StartSelfCleanup(SelfCleanupMode); return; }
        ShutdownMode = ShutdownMode.OnExplicitShutdown;
        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) => { Log.Error("Unobserved task exception", args.Exception); args.SetObserved(); };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unhandled exception", args.ExceptionObject as Exception);

        var options = CommandLineOptions.Parse(Program.StartupArgs);
        StartedHeadless = options.Headless;
        if (options.Exit) { Quit(); return; }
        // Listen before slow I/O. Secondary launches are queued, not dropped or allowed to
        // expose a half-initialized window; Setup's --exit is honored immediately.
        SingleInstance.StartServer(ReceiveStartupArgs);
        StartupSplashWindow? splash = null;
        MainWindow? main = null;
        MainViewModel? vm = null;
        try
        {
            ApplyTheme(Program.StartupTheme);
            if (StartupSequence.ShouldShowSplash(StartedHeadless, SelfCleanupMode != null, options.Exit))
            {
                splash = new StartupSplashWindow();
                await StartupWindowPresenter.ShowAndWaitForFrameAsync(splash, ShutdownToken);
            }
            ShutdownToken.ThrowIfCancellationRequested();
            // Construct UI-affine services on the dispatcher; only store reads move to workers.
            Services = new AppServices();
            var steps = new List<StartupStep>
            {
                new("Loading your preferences", async ct =>
                {
                    await Task.Run(() => Services.Settings.Load(), ct);
                    ct.ThrowIfCancellationRequested();
                    ApplyTheme(Services.Settings.Current.Theme);
                    UiState.Scale = UiState.Clamp(Services.Settings.Current.UiScale);
                }),
                new("Loading local history", async ct =>
                {
                    await Task.Run(() =>
                    {
                        ct.ThrowIfCancellationRequested();
                        Services.History.Load();
                        ct.ThrowIfCancellationRequested();
                        Services.UpdateHistory.Load();
                        ct.ThrowIfCancellationRequested();
                        try { Services.UpdateHistory.ResolveSelfUpdate(Services.Updater.CurrentVersionLabel); }
                        catch (Exception ex) { Log.Warn("Could not record Evict's own update: " + ex.Message); }
                    }, ct);
                }),
                new("Preparing your workspace", _ =>
                {
                    vm = new MainViewModel(Services);
                    Background = new BackgroundCoordinator(Services, vm);
                    vm.Background = Background;
                    return Task.CompletedTask;
                })
            };
            if (!StartedHeadless)
                steps.Add(new("Loading installed programs", ct => vm!.GetPage<ProgramsViewModel>(PageKey.Programs).PrepareStartupInventoryAsync(ct)));
            steps.Add(new("Preparing the first window frame", async ct =>
            {
                main = new MainWindow { DataContext = vm };
                MainWindow = main;
                if (!StartedHeadless) await StartupWindowPresenter.RevealWhenReadyAsync(main, ct);
            }));
            splash?.SetStages(steps.Select(s => s.Name));
            await StartupSequence.RunAsync(steps, progress => splash?.Report(progress), ShutdownToken);
            ShutdownToken.ThrowIfCancellationRequested();

            _startupComplete = true;
            var activateMain = splash?.IsActive == true;
            splash?.Dismiss();
            if (activateMain) main!.Activate();
            Log.Info($"{AppPaths.ProductName} ready. Elevated={ElevationHelper.IsElevated}, OS={Environment.OSVersion}, .NET={Environment.Version}");
            await CompleteStartupAsync(vm!, main!, options);
        }
        catch (OperationCanceledException) when (ShutdownToken.IsCancellationRequested) { /* intentional exit */ }
        catch (Exception ex)
        {
            Log.Error("Application startup failed", ex);
            if (IsExiting) return;
            if (_startupComplete)
            {
                vm?.ShowNotice("Evict opened, but a background startup operation failed. See the log for details.", () => Dialogs.OpenFolder(AppPaths.LogFile));
            }
            else if (splash is { IsLoaded: true })
            {
                main?.Hide();
                splash.ShowFailure(ex);
            }
            else
            {
                MessageBox.Show("Evict could not start. Details were written to:\n" + AppPaths.LogFile,
                    AppPaths.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
                Quit();
            }
        }
    }

    private void ReceiveStartupArgs(string[] args)
    {
        if (IsExiting) return;
        var options = CommandLineOptions.Parse(args);
        if (options.Exit) { Quit(); return; }
        if (_readyMain is { } vm) HandleArgs(vm, args);
        else _pendingStartupArgs.Enqueue(args);
    }

    private async Task CompleteStartupAsync(MainViewModel vm, MainWindow main, CommandLineOptions options)
    {
        _historyCleanupTimer = new System.Threading.Timer(_ => Services.UpdateHistory.PruneNow(), null,
            TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        UiState.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(UiState.Scale))
            {
                Services.Settings.Current.UiScale = UiState.Scale;
                Services.Settings.Save();
            }
        };
        try { Background.ApplySettings(forceTray: StartedHeadless); }
        catch (Exception ex) { Log.Error("Background initialization failed", ex); }
        if (StartedHeadless && !Background.HasTray)
        {
            Log.Warn("No tray icon available - showing the prepared window instead of running invisibly.");
            await StartupWindowPresenter.RevealWhenReadyAsync(main, ShutdownToken);
        }
        _readyMain = vm;
        // These operations must not delay the essential-data/first-frame readiness gate.
        _ = vm.GetPage<SettingsViewModel>(PageKey.Settings);
        if (!options.IsEmpty || options.Unknown.Count > 0) HandleArgs(vm, Program.StartupArgs, activate: !options.Headless);
        else if (Services.Settings.Current.EasyUninstallWidgetVisible) vm.ShowWidgetCommand.Execute(null);
        while (_pendingStartupArgs.Count > 0 && !IsExiting) HandleArgs(vm, _pendingStartupArgs.Dequeue());
        if (IsExiting) return;
        _ = RunPostStartupMaintenanceAsync();
        _ = vm.CheckForUpdatesOnStartupAsync(ShutdownToken);
        if (!options.ScheduledScan) _ = vm.RunMissedScheduledScanIfDueAsync();
    }

    private static async Task RunPostStartupMaintenanceAsync()
    {
        try
        {
            var contextMenu = Services.Settings.Current.ExplorerContextMenu;
            var autostart = Services.Settings.Current.StartWithWindows;
            await Task.Run(() =>
            {
                ShutdownToken.ThrowIfCancellationRequested();
                if (contextMenu && ShellIntegration.NeedsRefresh()) ShellIntegration.Register();
                ShutdownToken.ThrowIfCancellationRequested();
                if (autostart) StartupRegistration.RefreshIfStale();
                ShutdownToken.ThrowIfCancellationRequested();
                Services.Updater.CleanupAfterUpdate();
            }, ShutdownToken);
        }
        catch (OperationCanceledException) when (ShutdownToken.IsCancellationRequested) { }
        catch (Exception ex) { Log.Error("Post-startup maintenance failed", ex); }
    }

    private static bool _windowElevationTried;

    public static bool TryRestartElevatedForWindow(IReadOnlyList<string>? args = null)
    {
        if (!StartedHeadless || _windowElevationTried || Services is null) return false;
        if (Current.MainWindow is { IsVisible: true }) return false;
        _windowElevationTried = true;
        if (!StartupElevation.ShouldElevate(Services.Settings.Current.StartAsAdministrator, ElevationHelper.IsElevated, ElevationHelper.CanElevateSameUser, new CommandLineOptions())) return false;
        if (Background?.IsRecording == true) return false;
        if (!ElevationHelper.RestartElevated(StartupElevation.RelaunchArgs(args ?? Array.Empty<string>(), Environment.ProcessId))) return false;
        Log.Info("Restarting as administrator to open the window.");
        Quit();
        return true;
    }

    private void StartSelfCleanup(CommandLineOptions options)
    {
        ShutdownMode = ShutdownMode.OnMainWindowClose;
        var theme = "Light";
        try
        {
            var file = System.IO.Path.Combine(AppPaths.DataRootPath, "settings.json");
            if (System.IO.File.Exists(file))
            {
                using var doc = System.Text.Json.JsonDocument.Parse(System.IO.File.ReadAllText(file));
                if (doc.RootElement.TryGetProperty("Theme", out var t) && t.GetString() is { } s) theme = s;
            }
        }
        catch { /* default theme */ }
        ApplyTheme(theme);
        var window = new SelfCleanupWindow { DataContext = new SelfCleanupViewModel(options.Portable) };
        MainWindow = window;
        window.Show();
    }

    private static void HandleArgs(MainViewModel vm, string[] args, bool activate = true)
    {
        try
        {
            var options = CommandLineOptions.Parse(args);
            if (options.Exit) { Log.Info("Asked to exit (--exit)."); Quit(); return; }
            if (activate && !options.Headless && TryRestartElevatedForWindow(args)) return;
            if (activate && !options.Headless) vm.ShowMainWindow();
            if (!options.IsEmpty) _ = vm.HandleCommandLineAsync(options);
        }
        catch (Exception ex) { Log.Error("Handling command line failed", ex); }
    }

    public static void ApplyTheme(string theme)
    {
        var name = string.Equals(theme, "Dark", StringComparison.OrdinalIgnoreCase) ? "Dark" : "Light";
        var dict = new ResourceDictionary { Source = new Uri($"pack://application:,,,/Themes/{name}.xaml", UriKind.Absolute) };
        var merged = Current.Resources.MergedDictionaries;
        if (merged.Count > 0) merged[0] = dict; else merged.Add(dict);
    }

    private void OnDispatcherUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        Log.Error("Dispatcher exception", e.Exception);
        try
        {
            MessageBox.Show(MainWindow, "Something went wrong:\n\n" + e.Exception.Message + "\n\nDetails were written to the log file:\n" + AppPaths.LogFile,
                AppPaths.ProductName, MessageBoxButton.OK, MessageBoxImage.Error);
        }
        catch { /* ignore */ }
        e.Handled = true;
    }

    protected override void OnSessionEnding(SessionEndingCancelEventArgs e)
    {
        IsExiting = true;
        ShutdownCancellation.Cancel();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ShutdownCancellation.Cancel();
        _historyCleanupTimer?.Dispose();
        SingleInstance.Stop();
        try { Background?.Shutdown(); } catch { /* ignore */ }
        // A cancellation can happen while a worker is reading settings. Never overwrite that
        // file with partially initialized/default preferences during failed startup.
        if (_startupComplete) try { Services?.Settings.Save(); } catch { /* ignore */ }
        Log.Info("Exit.");
        base.OnExit(e);
    }
}
