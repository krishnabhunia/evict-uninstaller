using System.Text;

namespace Evict.Core.Util;

/// <summary>
/// Cheap .lnk inspection without COM: a shortcut stores its target both as an ANSI LocalBasePath and,
/// on modern Windows, as a Unicode string list. We look for the install folder or a known executable
/// name in either encoding. Good enough for "does this shortcut point into that program?".
/// </summary>
public static class ShortcutInspector
{
    private static readonly Encoding Latin1 = Encoding.Latin1;

    public static bool References(string lnkPath, string? installLocation, IReadOnlyCollection<string> exeNames)
    {
        byte[] bytes;
        try
        {
            var fi = new FileInfo(lnkPath);
            if (!fi.Exists || fi.Length > 512 * 1024) return false;
            bytes = File.ReadAllBytes(lnkPath);
        }
        catch { return false; }
        return ReferencesBytes(bytes, installLocation, exeNames);
    }

    internal static bool ReferencesBytes(byte[] bytes, string? installLocation, IReadOnlyCollection<string> exeNames)
    {
        string ansi = Latin1.GetString(bytes);
        string unicode;
        try { unicode = Encoding.Unicode.GetString(bytes); } catch { unicode = ""; }

        if (!string.IsNullOrEmpty(installLocation))
        {
            var loc = PathUtil.NormalizeForCompare(installLocation);
            if (loc.Length >= 6)
            {
                var needle = loc.EndsWith('\\') ? loc : loc + "\\";
                if (ansi.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
                if (unicode.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
                // Working-directory field stores the folder itself, null terminated.
                if (ansi.Contains(loc + "\0", StringComparison.OrdinalIgnoreCase)) return true;
                if (unicode.Contains(loc + "\0", StringComparison.OrdinalIgnoreCase)) return true;
            }
        }

        foreach (var exe in exeNames)
        {
            if (string.IsNullOrWhiteSpace(exe) || IsGenericExeName(exe)) continue;
            var needle = "\\" + exe;
            if (ansi.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
            if (unicode.Contains(needle, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    private static readonly string[] GenericExePrefixes =
    {
        "uninstall", "unins", "setup", "install", "update", "updater", "launcher", "helper", "crash", "report",
        "service", "server", "host", "daemon", "agent", "tray", "notif", "config", "start", "run", "app",
        "main", "client", "console", "cmd", "powershell", "explorer", "iexplore", "chrome", "msedge", "firefox",
        "rundll32", "msiexec", "wscript", "cscript", "java", "javaw", "python", "node", "electron",
    };

    public static bool IsGenericExeName(string exeName)
    {
        var n = Path.GetFileNameWithoutExtension(exeName).ToLowerInvariant();
        if (n.Length < 4) return true;
        return GenericExePrefixes.Any(p => n.StartsWith(p, StringComparison.Ordinal)) || n.All(char.IsDigit);
    }
}
