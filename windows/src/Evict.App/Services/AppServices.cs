using Evict.Core.Services;

namespace Evict.App.Services;

/// <summary>Composition root: one instance of every service, shared by all view models.</summary>
public sealed class AppServices
{
    public SettingsStore Settings { get; } = new();
    public HistoryStore History { get; } = new();
    public InstalledProgramsService Programs { get; } = new();
    public UninstallOrchestrator Orchestrator { get; } = new();
    public LeftoverScanner Scanner { get; } = new();
    public LeftoverCleaner Cleaner { get; } = new();
    public RestorePointService RestorePoints { get; } = new();
    public AppxService Appx { get; } = new();
    public BrowserExtensionService Browser { get; } = new();
    public WindowsUpdatesService Updates { get; } = new();
    public WingetService Winget { get; } = new();
    public InstallMonitorService Monitor { get; } = new();
    public ForceUninstallService Force { get; } = new();
    public FileShredder Shredder { get; } = new();
    public StartupService Startup { get; } = new();
    public ResidualScanner Residual { get; } = new();
    public IconProvider Icons { get; } = new();
    public UpdateService Updater { get; } = new();
    public ScheduledScanService Scheduler { get; } = new();
    public SystemCleanupService Cleanup { get; } = new();
}
