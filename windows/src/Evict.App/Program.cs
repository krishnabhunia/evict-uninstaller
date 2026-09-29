using System.Threading;
using Evict.App.Services;

namespace Evict.App;

/// <summary>
/// Explicit entry point so we control the single-instance mutex and STA thread before WPF starts.
/// A second launch (Explorer context menu, command line) forwards its arguments to the running window.
/// </summary>
public static class Program
{
    private static Mutex? _mutex;
    public static string[] StartupArgs { get; private set; } = Array.Empty<string>();

    [STAThread]
    public static int Main(string[] args)
    {
        StartupArgs = args;
        bool createdNew;
        try
        {
            _mutex = new Mutex(true, @"Local\EvictUninstaller.SingleInstance", out createdNew);
        }
        catch
        {
            createdNew = true;
        }

        if (!createdNew)
        {
            // Another instance is running: hand over the arguments (or just ask it to come to the front).
            SingleInstance.TryForward(args);
            return 0;
        }

        var app = new App();
        app.InitializeComponent();
        int rc = app.Run();
        ReleaseSingleInstance();
        return rc;
    }

    /// <summary>Called before re-launching elevated so the new process is not rejected as a duplicate.</summary>
    public static void ReleaseSingleInstance()
    {
        SingleInstance.Stop();
        try { _mutex?.ReleaseMutex(); } catch { /* not owned */ }
        try { _mutex?.Dispose(); } catch { /* ignore */ }
        _mutex = null;
    }
}
