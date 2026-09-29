namespace Evict.Core.Util;

/// <summary>
/// Pure decisions behind the Registry Cleaner: which file an entry points at, and whether that file is
/// *definitely* gone. Everything uncertain (network or removable drives, unexpanded variables, bare file names)
/// counts as "not missing" – a cleaner must never delete an entry it cannot prove is broken. Unit tested.
/// </summary>
public static class RegistryCleanerRules
{
    /// <summary>Programs that run something else: the entry is broken only if what they run is missing.</summary>
    private static readonly HashSet<string> HostPrograms = new(StringComparer.OrdinalIgnoreCase)
    {
        "rundll32.exe", "regsvr32.exe", "cmd.exe", "powershell.exe", "pwsh.exe", "wscript.exe", "cscript.exe",
        "mshta.exe", "conhost.exe", "explorer.exe", "java.exe", "javaw.exe", "dllhost.exe", "wmic.exe",
        "schtasks.exe", "reg.exe", "sc.exe", "start.exe", "py.exe", "python.exe", "pythonw.exe", "node.exe",
    };

    /// <summary>Hosts whose argument is not a file (a product code, a verb…) – such entries are never judged.</summary>
    private static readonly HashSet<string> OpaqueHosts = new(StringComparer.OrdinalIgnoreCase) { "msiexec.exe" };

    /// <summary>
    /// The file an entry launches or loads: the first absolute path in it, or – when that is a host such as
    /// rundll32 or cmd – the next absolute path. Null when the entry names no absolute file we can judge.
    /// </summary>
    public static string? MainTarget(string? data)
    {
        if (string.IsNullOrWhiteSpace(data)) return null;
        var paths = RegistryLeftoverRules.ExtractPaths(data);
        if (paths.Count == 0)
        {
            var cmd = UninstallCommandParser.Parse(data);
            if (cmd != null && IsAbsoluteLocal(cmd.FileName)) paths.Add(cmd.FileName);
        }
        if (paths.Count == 0) return null;
        var first = paths[0];
        var leaf = PathUtil.LeafName(first);
        if (OpaqueHosts.Contains(leaf)) return null;
        if (HostPrograms.Contains(leaf)) return paths.Count > 1 ? paths[1] : null;
        return first;
    }

    public static bool IsHostProgram(string path) => HostPrograms.Contains(PathUtil.LeafName(path)) || OpaqueHosts.Contains(PathUtil.LeafName(path));

    /// <summary>"C:\…" – not UNC, not relative, not an unexpanded %VARIABLE%.</summary>
    public static bool IsAbsoluteLocal(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return false;
        var p = path.Trim().Trim('"');
        return p.Length >= 3 && char.IsAsciiLetter(p[0]) && p[1] == ':' && (p[2] == '\\' || p[2] == '/') && !p.Contains('%') && !p.Contains('*') && !p.Contains('?');
    }

    /// <summary>
    /// True only when the path is local, its drive is present and fixed (<paramref name="driveAvailable"/>), and
    /// neither the path nor its 32/64-bit System32 twin exists.
    /// </summary>
    public static bool IsDefinitelyMissing(string? path, Func<string, bool> exists, Func<string, bool> driveAvailable, string? windowsDir)
    {
        if (!IsAbsoluteLocal(path)) return false;
        var p = path!.Trim().Trim('"').Replace('/', '\\').TrimEnd('\\');
        if (p.Length == 2) p += "\\";
        if (!driveAvailable(char.ToUpperInvariant(p[0]) + ":\\")) return false;
        if (exists(p)) return false;
        foreach (var twin in SystemFolderTwins(p, windowsDir))
            if (exists(twin)) return false;
        return true;
    }

    /// <summary>A 32-bit program's "C:\Windows\System32\x.dll" is really in SysWOW64 (and the reverse via Sysnative).</summary>
    public static IEnumerable<string> SystemFolderTwins(string path, string? windowsDir)
    {
        if (string.IsNullOrEmpty(windowsDir)) yield break;
        var win = windowsDir.TrimEnd('\\');
        string[] folders = { "System32", "SysWOW64", "Sysnative" };
        foreach (var from in folders)
        {
            var prefix = win + "\\" + from + "\\";
            if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) continue;
            var rest = path[prefix.Length..];
            foreach (var to in folders)
                if (!to.Equals(from, StringComparison.OrdinalIgnoreCase)) yield return win + "\\" + to + "\\" + rest;
        }
    }

    public static bool IsUnderWindows(string path, string? windowsDir) =>
        !string.IsNullOrEmpty(windowsDir) && PathUtil.IsUnder(path, windowsDir);

    private static readonly string[] MuiExtensions = { ".exe", ".dll", ".com", ".scr", ".cpl", ".msc", ".bat", ".cmd" };

    /// <summary>
    /// MuiCache value names are "C:\Path\app.exe.FriendlyAppName" (or ".ApplicationCompany", or just the path on
    /// older Windows). Returns the program path, or null for resource references ("@shell32.dll,-1") and other names.
    /// </summary>
    public static string? MuiCachePath(string? valueName)
    {
        if (string.IsNullOrWhiteSpace(valueName) || valueName.StartsWith('@')) return null;
        int best = -1, bestLen = 0;
        foreach (var ext in MuiExtensions)
        {
            int i = valueName.LastIndexOf(ext, StringComparison.OrdinalIgnoreCase);
            if (i > best) { best = i; bestLen = ext.Length; }
        }
        if (best < 0) return null;
        var end = best + bestLen;
        if (end < valueName.Length && valueName[end] != '.') return null; // "…exeFoo" is not a path
        var path = valueName[..end];
        return IsAbsoluteLocal(path) ? path : null;
    }

    /// <summary>Value names that record where a program was installed (vendor keys under SOFTWARE).</summary>
    public static readonly string[] InstallPathValueNames =
    {
        "InstallDir", "InstallLocation", "InstallPath", "Install_Dir", "Install Path", "InstallFolder",
        "InstallDirectory", "InstallationPath", "InstallRoot", "AppPath", "ApplicationPath", "ExePath", "HomeDir", "Path",
    };

    /// <summary>First install-location value that holds an absolute local path (value names compared case-insensitively).</summary>
    public static string? InstallPathOf(IReadOnlyDictionary<string, string?> values)
    {
        foreach (var name in InstallPathValueNames)
        {
            foreach (var (k, v) in values)
            {
                if (!k.Equals(name, StringComparison.OrdinalIgnoreCase) || v is null) continue;
                var candidate = v.Trim().Trim('"');
                // "Path" is sometimes a PATH-style list or a file; take the first entry of a list.
                if (candidate.Contains(';')) candidate = candidate.Split(';')[0];
                if (IsAbsoluteLocal(candidate)) return candidate;
            }
        }
        return null;
    }

    /// <summary>Top-level SOFTWARE keys the cleaner never judges or deletes (Windows, drivers, shared runtimes).</summary>
    public static bool IsProtectedSoftwareKey(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return true;
        var n = name.Trim();
        if (NameNormalizer.ProtectedNames.Contains(n)) return true;
        return n.StartsWith("Microsoft", StringComparison.OrdinalIgnoreCase)
            || n.StartsWith("Windows", StringComparison.OrdinalIgnoreCase)
            || ExtraProtected.Contains(n);
    }

    private static readonly HashSet<string> ExtraProtected = new(StringComparer.OrdinalIgnoreCase)
    {
        "Classes", "Clients", "Policies", "RegisteredApplications", "WOW6432Node", "ODBC", "Khronos", "Partner",
        "AppDataLow", "System", "Evict", "Chromium", "DefaultUserEnvironment", "Keyboard Layout",
        "Printers", "Environment", "Console", "Control Panel", "EUDC", "Network", "Volatile Environment", "SyncEngines",
        "IM Providers", "Netscape", "Licenses", "Sysinternals",
    };

    /// <summary>UserAssist value names are ROT13-encoded paths.</summary>
    public static string Rot13(string s) => new(s.Select(c => c switch
    {
        >= 'a' and <= 'z' => (char)('a' + (c - 'a' + 13) % 26),
        >= 'A' and <= 'Z' => (char)('A' + (c - 'A' + 13) % 26),
        _ => c,
    }).ToArray());
}
