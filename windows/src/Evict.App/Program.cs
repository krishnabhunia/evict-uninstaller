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
    // Reuse the existing pre-elevation settings read, so the first splash frame uses the saved theme.
    public static string StartupTheme { get; private set; } = "Light";

    [STAThread]
    public static int Main(string[] args)
    {
        StartupArgs = args;
        var options = CommandLineOptions.Parse(args);
        if (options.SelfCleanup != null) return RunSelfCleanup(options);
        if (options.Exit)
        {
            SingleInstance.TryForward(args);
            return 0;
        }
        if (options.WaitPid is { } waitPid)
        {
            try { using var p = Process.GetProcessById(waitPid); p.WaitForExit(10_000); } catch { /* already gone */ }
        }
        if (!AcquireSingleInstance())
        {
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
        }
        try
        {
            var app = new App();
            app.InitializeComponent();
            return app.Run();
        }
        finally { ReleaseSingleInstance(); }
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
        catch (UnauthorizedAccessException) { return false; }
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

    private static bool ReadStartAsAdministrator()
    {
        try
        {
            var store = new SettingsStore();
            store.Load();
            StartupTheme = store.Current.Theme;
            return store.Current.StartAsAdministrator;
        }
        catch { return true; }
    }

    public static void ReleaseSingleInstance()
    {
        SingleInstance.Stop();
        try { _mutex?.ReleaseMutex(); } catch { /* not owned */ }
        try { _mutex?.Dispose(); } catch { /* ignore */ }
        _mutex = null;
    }

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
