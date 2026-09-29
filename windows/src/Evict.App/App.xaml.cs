using System.Windows;
using System.Windows.Threading;
using Evict.App.Services;
using Evict.App.ViewModels;
using Evict.Core.Services;
using Evict.Core.Util;

namespace Evict.App;

public partial class App : Application
{
    public static AppServices Services { get; private set; } = null!;
    public static UiState UiState { get; } = new();
    public static BackgroundCoordinator Background { get; private set; } = null!;
    /// <summary>Set when the user really wants to quit (tray → Exit, or close with "close to tray" off).</summary>
    public static bool IsExiting { get; set; }
    /// <summary>--tray / --scheduled-scan: the window is not shown at start.</summary>
    public static bool StartedHeadless { get; private set; }

    /// <summary>The one way to quit: marks the exit as intentional so "close to tray" does not intercept it.</summary>
    public static void Quit()
    {
        IsExiting = true;
        Current.Shutdown();
    }

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        // The window can be hidden in the tray, so the process lifetime is controlled explicitly.
        ShutdownMode = ShutdownMode.OnExplicitShutdown;

        DispatcherUnhandledException += OnDispatcherUnhandledException;
        TaskScheduler.UnobservedTaskException += (_, args) => { Log.Error("Unobserved task exception", args.Exception); args.SetObserved(); };
        AppDomain.CurrentDomain.UnhandledException += (_, args) => Log.Error("Unhandled exception", args.ExceptionObject as Exception);

        Services = new AppServices();
        Services.Settings.Load();
        Services.History.Load();
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
        if (!StartedHeadless) main.Show();

        // Tray icon + installer detection (a headless start always gets a tray icon, otherwise the process would be invisible).
        Background.ApplySettings(forceTray: StartedHeadless);
        if (StartedHeadless && !Background.HasTray) { Log.Warn("No tray icon available – showing the window instead of running invisibly."); main.Show(); }
        if (Services.Settings.Current.StartWithWindows) StartupRegistration.RefreshIfStale();

        SingleInstance.StartServer(args => HandleArgs(mainVm, args));
        if (Program.StartupArgs.Length > 0) HandleArgs(mainVm, Program.StartupArgs, activate: !startOptions.Headless);
        else if (Services.Settings.Current.EasyUninstallWidgetVisible) mainVm.ShowWidgetCommand.Execute(null);

        // Housekeeping after a self-update, then the (optional) update check in the background.
        Services.Updater.CleanupAfterUpdate();
        if (!startOptions.ScheduledScan)
        {
            _ = mainVm.CheckForUpdatesOnStartupAsync();
            _ = mainVm.RunMissedScheduledScanIfDueAsync();
        }
    }

    private static void HandleArgs(MainViewModel vm, string[] args, bool activate = true)
    {
        try
        {
            var options = CommandLineOptions.Parse(args);
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
        base.OnSessionEnding(e);
    }

    protected override void OnExit(ExitEventArgs e)
    {
        try { Background?.Shutdown(); } catch { /* ignore */ }
        try { Services?.Settings.Save(); } catch { /* ignore */ }
        Log.Info("Exit.");
        base.OnExit(e);
    }
}
