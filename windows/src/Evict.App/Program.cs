using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
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
    private const string MutexName = @"Local\EvictUninstaller.SingleInstance";
    private static Mutex? _mutex;
    public static string[] StartupArgs { get; private set; } = Array.Empty<string>();

    [STAThread]
    public static int Main(string[] args)
    {
        StartupArgs = args;

        // Removing Evict's own leftovers (uninstaller / portable Settings): no single-instance check, no data folder.
        var options = CommandLineOptions.Parse(args);
        if (options.SelfCleanup != null) return RunSelfCleanup(options);

        // Setup asks a running Evict to close this way – one running as administrator cannot be force-closed by it.
        if (options.Exit)
        {
            SingleInstance.TryForward(args);
            return 0;
        }

        // The elevated copy started by "Start as administrator": the copy that started it is still exiting and holds the lock.
        if (options.WaitPid is { } waitPid)
        {
            try { using var p = Process.GetProcessById(waitPid); p.WaitForExit(10_000); } catch { /* already gone */ }
        }

        if (!AcquireSingleInstance())
        {
            // Another instance is running: hand over the arguments (or just ask it to come to the front).
            SingleInstance.TryForward(args);
            return 0;
        }

        if (StartupElevation.ShouldElevate(ReadStartAsAdministrator(), ElevationHelper.IsElevated, ElevationHelper.CanElevateSameUser, options))
        {
            if (ElevationHelper.RestartElevated(StartupElevation.RelaunchArgs(args, Environment.ProcessId)))
            {
                ReleaseSingleInstance();
                return 0;
            }
            // UAC prompt declined: carry on with the user's own rights (the banner offers "Restart as administrator").
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

    /// <summary>
    /// Takes the single-instance lock; false when another Evict holds it. Every process of this user may open the lock –
    /// also a copy (or Setup) without administrator rights while this one runs as administrator, which the default
    /// security of an elevated process would refuse.
    /// </summary>
    private static bool AcquireSingleInstance(bool failClosed = false)
    {
        bool createdNew;
        try
        {
            using var identity = WindowsIdentity.GetCurrent();
            var security = new MutexSecurity();
            security.AddAccessRule(new MutexAccessRule(identity.User!, MutexRights.Synchronize | MutexRights.Modify, AccessControlType.Allow));
            _mutex = MutexAcl.Create(true, MutexName, out createdNew, security);
            return createdNew;
        }
        catch (UnauthorizedAccessException)
        {
            return false; // held by an elevated Evict from before 1.7.0 (default security)
        }
        catch
        {
            try
            {
                _mutex = new Mutex(true, MutexName, out createdNew);
                return createdNew;
            }
            catch (UnauthorizedAccessException) { return false; }
            catch { return !failClosed; }
        }
    }

    /// <summary>The "Start as administrator" setting, read before WPF starts (on by default, also when unreadable).</summary>
    private static bool ReadStartAsAdministrator()
    {
        try
        {
            var store = new SettingsStore();
            store.Load();
            return store.Current.StartAsAdministrator;
        }
        catch
        {
            return true;
        }
    }

    /// <summary>Called before re-launching elevated so the new process is not rejected as a duplicate.</summary>
    public static void ReleaseSingleInstance()
    {
        SingleInstance.Stop();
        try { _mutex?.ReleaseMutex(); } catch { /* not owned */ }
        try { _mutex?.Dispose(); } catch { /* ignore */ }
        _mutex = null;
    }

    /// <summary>Restores argument forwarding and ownership when a restart or update could not start.</summary>
    public static bool RestoreSingleInstance()
    {
        if (_mutex != null) return true;
        if (!AcquireSingleInstance(failClosed: true))
        {
            ReleaseSingleInstance();
            return false;
        }
        SingleInstance.RestartServer();
        return true;
    }
}
