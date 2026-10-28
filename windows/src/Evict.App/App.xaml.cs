using System.Windows;
using System.Windows.Threading;
using Evict.App.Services;
using Evict.App.ViewModels;
using Evict.Core.Services;
using Evict.Core.Util;

namespace Evict.App;

public partial class App : Application
{
    private System.Threading.Timer? _historyCleanupTimer;
    private static readonly CancellationTokenSource ShutdownCancellation = new();
    public static CancellationToken ShutdownToken => ShutdownCancellation.Token;
    public static AppServices Services { get; private set; } = null!;
    public static UiState UiState { get; } = new();
    public static BackgroundCoordinator Background { get; private set; } = null!;
    /// <summary>Set when the user really wants to quit (tray → Exit, or close with "close to tray" off).</summary>
    public static bool IsExiting { get; set; }
    /// <summary>--tray / --scheduled-scan: the window is not shown at start.</summary>
    public static bool StartedHeadless { get; private set; }
    /// <summary>Set by <c>--self-cleanup ask</c>: only the "remove Evict's leftovers" window runs.</summary>
    public static CommandLineOptions? SelfCleanupMode { get; set; }

    /// <summary>The one way to quit: marks the exit as intentional so "close to tray" does not intercept it.</summary>
    public static void Quit()
    {
        IsExiting = true;
        ShutdownCancellation.Cancel();
        Current.Shutdown();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        if (SelfCleanupMode != null) { StartSelfCleanup(SelfCleanupMode); return; }
        // The window can be hidden in the tray, so the process lifetime is controlled explicitly.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) => { Log.Error("Unobserved task exception", args.Exception); args.SetObserved(); };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unhandled exception", args.ExceptionObject as Exception);

        Services = new AppServices();
        StartupSplash.Report(StartupStep.Settings);
        Services.Settings.Load();
        StartupSplash.Report(StartupStep.History);
        Services.History.Load();
        Services.UpdateHistory.Load();
        // A self-update handed over before the last exit is resolved now: did this start reach the new version?
        try { Services.UpdateHistory.ResolveSelfUpdate(Services.Updater.CurrentVersionLabel); }
        catch (Exception ex) { Log.Warn("Could not record Evict's own update: " + ex.Message); }
        _historyCleanupTimer = new System.Threading.Timer(_ => Services.UpdateHistory.PruneNow(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        ApplyTheme(Services.Settings.Current.Theme);
        UiState.Scale = UiState.Clamp(Services.Settings.Current.UiScale);
        UiState.PropertyChanged += (_, args) =>
        {
            if (args.PropertyName == nameof(UiState.Scale))
            {
                Services.Settings.Current.UiScale = UiState.Scale;
                Services.Settings.Save();
            }
        };

        Log.Info($"{AppPaths.ProductName} starting. Elevated={ElevationHelper.IsElevated}, OS={Environment.OSVersion}, .NET={Environment.Version}, args=[{string.Join(" ", Program.StartupArgs)}]");

        // Keep the Explorer context-menu command pointing at this exe if it moved.
        if (Services.Settings.Current.ExplorerContextMenu && ShellIntegration.NeedsRefresh()) ShellIntegration.Register();

        var startOptions = CommandLineOptions.Parse(Program.StartupArgs);
        StartedHeadless = startOptions.Headless;

        var mainVm = new MainViewModel(Services);
        Background = new BackgroundCoordinator(Services, mainVm);
        mainVm.Background = Background;
        var main = new MainWindow { DataContext = mainVm };
        MainWindow = main;
        var windowShown = StartedHeadless ? Task.CompletedTask : ShowWhenReadyAsync(mainVm, main);

        // Tray icon + installer detection (a headless start always gets a tray icon, otherwise the process would be invisible).
        Background.ApplySettings(forceTray: StartedHeadless);
        if (StartedHeadless && !Background.HasTray) { Log.Warn("No tray icon available – showing the window instead of running invisibly."); main.Show(); }
        if (Services.Settings.Current.StartWithWindows) StartupRegistration.RefreshIfStale();

        // Inspect schedule status even for a hidden launch; opening Settings must not register or migrate tasks.
        _ = mainVm.GetPage<SettingsViewModel>(PageKey.Settings);

        SingleInstance.StartServer(args => HandleArgs(mainVm, args));
        // Arguments (uninstall a file, open a page, the widget) act once the window is up: dialogs need a shown owner.
        _ = windowShown.ContinueWith(_ =>
        {
            if (!startOptions.IsEmpty || startOptions.Unknown.Count > 0) HandleArgs(mainVm, Program.StartupArgs, activate: !startOptions.Headless);
            else if (Services.Settings.Current.EasyUninstallWidgetVisible) mainVm.ShowWidgetCommand.Execute(null);
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.FromCurrentSynchronizationContext());

        // Check once on every service-running launch, including tray and scheduled-scan launches.
        // The check is silent and cancellable; a short-lived scheduled scan can exit before the delay ends.
        Services.Updater.CleanupAfterUpdate();
        _ = mainVm.CheckForUpdatesOnStartupAsync(ShutdownToken);
        if (!startOptions.ScheduledScan) _ = mainVm.RunMissedScheduledScanIfDueAsync();
    }

    /// <summary>
    /// Design SP1: with the splash up, the program list is read first and the window is drawn hidden (DWM cloak), then
    /// shown and the splash fades out – no half-drawn or coloured flash. "Open now" after 8 s; opens anyway after 20 s.
    /// Without a splash (it could not start) the window is shown at once, as before.
    /// </summary>
    private static async Task ShowWhenReadyAsync(MainViewModel mainVm, MainWindow main)
    {
        if (!StartupSplash.IsShown) { main.Show(); return; }
        try
        {
            StartupSplash.Report(StartupStep.Programs);
            var programs = mainVm.GetPage<ProgramsViewModel>(PageKey.Programs);
            var load = programs.EnsureLoadedAsync();
            var openNow = new TaskCompletionSource();
            var giveUp = Task.Delay(StartupSplashRules.OpenAnywayAfter);
            if (await Task.WhenAny(load, Task.Delay(StartupSplashRules.OfferOpenNowAfter)) != load)
            {
                StartupSplash.ShowSlow(() => openNow.TrySetResult());
                await Task.WhenAny(load, openNow.Task, giveUp);
            }
            if (load.IsFaulted) throw load.Exception!.GetBaseException();
            // A failed inventory completes the load normally and records the error: surface it on the splash.
            if (load.IsCompleted && programs.LoadError is { } loadError) throw loadError;
        }
        catch (Exception ex)
        {
            Log.Error("Start-up preparation failed", ex);
            var choice = new TaskCompletionSource();
            StartupSplash.ShowError(StartupSplashRules.ErrorText(ex), () => choice.TrySetResult(),
                () => { try { Dialogs.OpenFolder(AppPaths.LogFile); } catch { /* best effort */ } });
            await choice.Task;
        }

        StartupSplash.Report(StartupStep.Window);
        if (main.IsVisible) { StartupSplash.Close(); return; }   // a second launch already brought it up
        var revealed = new TaskCompletionSource();
        main.Revealed += (_, _) => { StartupSplash.Close(); revealed.TrySetResult(); };
        main.StartCloaked = true;
        main.Show();
        // Never wait on the reveal for ever (e.g. the cloak call failed and ContentRendered came first).
        await Task.WhenAny(revealed.Task, Task.Delay(TimeSpan.FromSeconds(3)));
        main.EnsureRevealed();
        StartupSplash.Close();
    }

    private static bool _windowElevationTried;

    /// <summary>
    /// A start hidden in the notification area (sign-in autostart, scheduled scan) never shows a UAC prompt. The first
    /// time its window is opened, "Start as administrator" applies: an elevated copy takes over (with these arguments) and
    /// this one exits. Not while an installation is being recorded, and asked at most once per process.
    /// </summary>
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

    /// <summary>Minimal start: theme + the self-cleanup window. No services, no settings writes, no log file.</summary>
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
        var window = new Views.SelfCleanupWindow { DataContext = new SelfCleanupViewModel(options.Portable) };
        MainWindow = window;
        window.Show();
    }

    private static void HandleArgs(MainViewModel vm, string[] args, bool activate = true)
    {
        try
        {
            var options = CommandLineOptions.Parse(args);
            if (options.Exit) { Log.Info("Asked to exit (--exit)."); Quit(); return; }
            // Started hidden in the tray without administrator rights: the first window to open follows "Start as administrator".
            if (activate && !options.Headless && TryRestartElevatedForWindow(args)) return;
            // A forwarded --tray / --scheduled-scan (e.g. sign-in autostart while Evict is open) must not pop the window up.
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
        IsExiting = true; // sign-out / shutdown must not be diverted to the tray
        ShutdownCancellation.Cancel();
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        ShutdownCancellation.Cancel();
        _historyCleanupTimer?.Dispose();
        try { Background?.Shutdown(); } catch { /* ignore */ }
        try { Services?.Settings.Save(); } catch { /* ignore */ }
        Log.Info("Exit.");
        base.OnExit(e);
    }
}
