using System.Diagnostics;
using System.Threading;
using Evict.App.Services;
using Evict.Core.Services;
using Evict.Core.Util;

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

        // Removing Evict's own leftovers (uninstaller / portable Settings): no single-instance check, no data folder.
        var options = CommandLineOptions.Parse(args);
        if (options.SelfCleanup != null) return RunSelfCleanup(options);

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

    private static int RunSelfCleanup(CommandLineOptions options)
    {
        Log.Enabled = false;
        if (options.WaitPid is { } pid)
        {
            try { using var p = Process.GetProcessById(pid); p.WaitForExit(30_000); } catch { /* already gone */ }
        }
        if (options.SelfCleanupAsk)
        {
            App.SelfCleanupMode = options;
            var app = new App();
            app.InitializeComponent();
            int rc = app.Run();
            SelfCleanupService.ScheduleExtractionCleanup();
            return rc;
        }
        try { SelfCleanupService.Run(SelfCleanupService.ParseParts(options.SelfCleanup), UpdateService.ExeDirectory); }
        catch { /* the uninstaller carries on regardless */ }
        SelfCleanupService.ScheduleExtractionCleanup();
        return 0;
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
