using System.Text.Json;
using System.Text.Json.Serialization;

namespace Evict.Core.Services;

public sealed class AppSettings
{
    /// <summary>Bumped when a default changes in a way that should also reach existing settings files (see SettingsStore.Migrate).</summary>
    public const int CurrentVersion = 2;
    public int SettingsVersion { get; set; } = 0;

    public string Theme { get; set; } = "Light";                 // Light | Dark
    public bool CreateRestorePoint { get; set; } = true;
    public bool QuietUninstall { get; set; } = false;
    public bool AutoCleanLeftovers { get; set; } = false;        // false → show review step
    public bool SendToRecycleBin { get; set; } = true;
    public bool ShowSystemComponents { get; set; } = false;
    public bool ShowWindowsUpdatesInPrograms { get; set; } = false;
    public bool ScanAllUserProfiles { get; set; } = true;
    public bool MeasureFolderSizes { get; set; } = true;          // compute sizes when EstimatedSize is missing
    public int LargeProgramThresholdMb { get; set; } = 500;
    public int RecentlyInstalledDays { get; set; } = 30;
    public int InfrequentlyUsedDays { get; set; } = 60;
    public bool ConfirmBeforeUninstall { get; set; } = true;
    public bool HideFrameworkAppx { get; set; } = true;
    public bool ShowSystemAppx { get; set; } = false;
    public string ShredMethod { get; set; } = "Dod3Pass";
    /// <summary>UI zoom / text size factor (0.8 – 3.0). Default 120 %.</summary>
    public double UiScale { get; set; } = 1.2;
    public bool ExplorerContextMenu { get; set; } = false;
    public bool EasyUninstallWidgetVisible { get; set; } = false;
    public double WidgetLeft { get; set; } = -1;
    public double WidgetTop { get; set; } = -1;
    public bool HealthAutoScan { get; set; } = true;
    /// <summary>Check GitHub Releases for a newer version at start-up.</summary>
    public bool CheckForUpdates { get; set; } = true;

    /// <summary>How many winget upgrades run at the same time (1–6).</summary>
    public int ParallelUpdates { get; set; } = 3;

    // ── tray & background (1.3.0) ──
    public bool ShowTrayIcon { get; set; } = true;
    public bool MinimizeToTray { get; set; } = false;
    /// <summary>Closing the window keeps Evict in the tray (installer detection and scheduled scans keep working).</summary>
    public bool CloseToTray { get; set; } = false;
    /// <summary>HKCU Run entry "Evict" → Evict.exe --tray (start hidden in the tray at sign-in).</summary>
    public bool StartWithWindows { get; set; } = false;
    /// <summary>Off | Ask | Auto – what to do when a running installer is detected.</summary>
    public string InstallerDetection { get; set; } = "Ask";
    /// <summary>Off | Daily | Weekly – Task Scheduler job that runs "Evict.exe --scheduled-scan".</summary>
    public string ScheduledScan { get; set; } = "Off";
    public int ScheduledScanHour { get; set; } = 10;
    public int ScheduledScanWeekday { get; set; } = 0; // 0 = Sunday … 6 = Saturday (weekly only)
    public DateTime? LastScheduledScanUtc { get; set; }
    public DateTime? LastUpdateCheckUtc { get; set; }
    /// <summary>A version the user chose to skip ("v1.3.0"); the banner stays hidden for it.</summary>
    public string? SkippedUpdateVersion { get; set; }
    public double WindowWidth { get; set; } = 1240;
    public double WindowHeight { get; set; } = 800;
    public bool WindowMaximized { get; set; } = false;
}

public sealed class SettingsStore
{
    private static readonly JsonSerializerOptions Options = new()
    {
        WriteIndented = true,
        DefaultIgnoreCondition = JsonIgnoreCondition.Never,
        PropertyNameCaseInsensitive = true,
    };

    private readonly object _gate = new();
    public AppSettings Current { get; private set; } = new();

    public void Load()
    {
        lock (_gate)
        {
            try
            {
                if (File.Exists(AppPaths.SettingsFile))
                {
                    var json = File.ReadAllText(AppPaths.SettingsFile);
                    Current = JsonSerializer.Deserialize<AppSettings>(json, Options) ?? new AppSettings();
                }
            }
            catch
            {
                Current = new AppSettings();
            }
            Migrate(Current);
        }
    }

    /// <summary>Applies default changes to settings files written by older versions.</summary>
    internal static void Migrate(AppSettings s)
    {
        if (s.SettingsVersion < 2)
        {
            // 1.3.0: default text size became 120 %. Users who never touched the setting still have the old default (100 %).
            if (Math.Abs(s.UiScale - 1.0) < 0.001) s.UiScale = 1.2;
        }
        s.SettingsVersion = AppSettings.CurrentVersion;
    }

    public void Save()
    {
        lock (_gate)
        {
            try
            {
                var json = JsonSerializer.Serialize(Current, Options);
                File.WriteAllText(AppPaths.SettingsFile, json);
            }
            catch
            {
                // Settings are a convenience; never crash for them.
            }
        }
    }
}
