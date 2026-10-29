using System.Threading;
using System.Windows;
using System.Windows.Threading;
using Evict.App.Views;
using Evict.Core.Services;
using Evict.Core.Util;

namespace Evict.App.Services;

/// <summary>
/// Design SP1: the start-up splash runs on its own UI thread, started by <see cref="Program.Main"/> before the App
/// exists, so it appears at once and keeps animating while settings, history and programs load on the main thread.
/// Every call is safe from any thread and does nothing when no splash is shown.
/// </summary>
public static class StartupSplash
{
    private static SplashWindow? _window;
    private static Dispatcher? _dispatcher;

    public static bool IsShown => _dispatcher is not null;

    private static readonly object Gate = new();
    /// <summary>Start gave up waiting: a splash that finishes building afterwards must not appear.</summary>
    private static bool _abandoned;

    public static void Start(string version, bool elevated)
    {
        if (_dispatcher is not null) return;
        // Not disposed: after a timeout the splash thread may still Set() it.
        var ready = new ManualResetEventSlim();
        var thread = new Thread(() =>
        {
            SplashWindow? window = null;
            try
            {
                window = new SplashWindow(version, elevated);
                lock (Gate)
                {
                    if (_abandoned) return;   // too late: the App already went on without it
                    window.Closed += (_, _) => Dispatcher.CurrentDispatcher.BeginInvokeShutdown(DispatcherPriority.Background);
                    _window = window;
                    _dispatcher = Dispatcher.CurrentDispatcher;
                }
                window.Show();
            }
            catch (Exception ex)
            {
                Log.Warn("Splash screen unavailable: " + ex.Message);
                lock (Gate) { _window = null; _dispatcher = null; }
                return;
            }
            finally { ready.Set(); }
            Dispatcher.Run();
        })
        { IsBackground = true, Name = "Evict splash" };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        if (!ready.Wait(TimeSpan.FromSeconds(3)))
        {
            lock (Gate)
            {
                if (_dispatcher is null)
                {
                    _abandoned = true;
                    Log.Warn("Splash screen took too long to appear; starting without it.");
                }
            }
        }
    }

    public static void Report(StartupStep step) => Post(w => w.Show(step));

    /// <summary><paramref name="openNow"/> runs on the main thread.</summary>
    public static void ShowSlow(Action openNow) => Post(w => w.ShowSlow(OnMain(openNow)));

    /// <summary>The actions run on the main thread.</summary>
    public static void ShowError(string message, Action open, Action showLog) => Post(w => w.ShowError(message, OnMain(open), OnMain(showLog)));

    public static void Close()
    {
        Post(w => w.FadeOutAndClose());
        _dispatcher = null;
    }

    private static void Post(Action<SplashWindow> action)
    {
        var dispatcher = _dispatcher;
        var window = _window;
        if (dispatcher is null || window is null || dispatcher.HasShutdownStarted) return;
        try { dispatcher.BeginInvoke(() => { try { action(window); } catch (Exception ex) { Log.Warn("Splash: " + ex.Message); } }); }
        catch { /* splash thread already gone */ }
    }

    /// <summary>Button clicks happen on the splash thread; the work belongs to the App's thread.</summary>
    private static Action OnMain(Action action) => () =>
    {
        var main = Application.Current?.Dispatcher;
        if (main is null) action();
        else main.BeginInvoke(action);
    };
}
