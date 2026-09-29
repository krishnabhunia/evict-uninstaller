namespace Evict.Core.Services;

/// <summary>Well-known folders used by the application for its own data.</summary>
public static class AppPaths
{
    public const string ProductName = "Evict Uninstaller";
    public const string ShortName = "Evict";

    public static string DataRoot
    {
        get
        {
            var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            if (string.IsNullOrEmpty(local)) local = Path.GetTempPath();
            var dir = Path.Combine(local, ShortName);
            try { Directory.CreateDirectory(dir); } catch { /* ignore */ }
            return dir;
        }
    }

    public static string SettingsFile => Path.Combine(DataRoot, "settings.json");
    public static string HistoryFile => Path.Combine(DataRoot, "history.json");
    public static string InstallLogsDir
    {
        get
        {
            var d = Path.Combine(DataRoot, "install-logs");
            try { Directory.CreateDirectory(d); } catch { /* ignore */ }
            return d;
        }
    }
    public static string LogFile => Path.Combine(DataRoot, "evict.log");
}
