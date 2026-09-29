using System.Text.RegularExpressions;

namespace Evict.Core.Util;

/// <summary>
/// Pure rules deciding whether a registry entry that lives in a *shared* place (Classes, CLSID, shell verbs,
/// firewall rules…) belongs to a program. Unit tested; no registry access.
/// </summary>
public static partial class RegistryLeftoverRules
{
    [GeneratedRegex(@"(?i)(?:""(?<p>[^""]+)""|(?<p>[a-z]:\\[^""]*?\.(?:exe|dll|ocx|cpl|com|bat|cmd|ico|scr))(?=$|[\s,;|""])|(?<p>%[a-z_]+%\\[^""]*?\.(?:exe|dll|ocx|ico))(?=$|[\s,;|""]))")]
    private static partial Regex PathRegex();

    /// <summary>Every file path mentioned in a registry string (command lines, "path,index" icon specs, "App=…|" firewall rules).</summary>
    public static List<string> ExtractPaths(string? data)
    {
        var list = new List<string>();
        if (string.IsNullOrWhiteSpace(data)) return list;
        foreach (Match m in PathRegex().Matches(data))
        {
            var p = m.Groups["p"].Value.Trim();
            int comma = p.LastIndexOf(',');
            if (comma > 2 && int.TryParse(p[(comma + 1)..].Trim(), out _)) p = p[..comma];   // "C:\x\app.exe,0"
            try { p = Environment.ExpandEnvironmentVariables(p); } catch { /* keep */ }
            if (p.Length >= 4 && p[1] == ':' ) list.Add(p);
        }
        // Firewall rule syntax: "v2.31|Action=Allow|App=C:\...\app.exe|Name=…|"
        foreach (var part in data.Split('|'))
        {
            if (part.StartsWith("App=", StringComparison.OrdinalIgnoreCase))
            {
                var p = part[4..].Trim();
                try { p = Environment.ExpandEnvironmentVariables(p); } catch { /* keep */ }
                if (p.Length >= 4 && p[1] == ':' && !list.Contains(p, StringComparer.OrdinalIgnoreCase)) list.Add(p);
            }
        }
        return list;
    }

    /// <summary>True when the string references a file inside the program's folder.</summary>
    public static bool ReferencesFolder(string? data, string? folder)
    {
        if (string.IsNullOrWhiteSpace(folder)) return false;
        return ExtractPaths(data).Any(p => PathUtil.IsUnder(p, folder));
    }

    /// <summary>
    /// ProgIDs look like "Vendor.App.Document.1" or "AppName.File". A ProgID belongs to the program when its first
    /// segment, or its first two segments joined, exactly match one of the program's name keys.
    /// Fuzzy matching is never used here: Classes is shared by every program.
    /// </summary>
    public static Models.LeftoverConfidence? MatchProgId(string progId, IReadOnlyList<CandidateKey> keys)
    {
        if (string.IsNullOrWhiteSpace(progId) || progId.StartsWith('.') || progId.StartsWith('{') || progId.StartsWith('*')) return null;
        var parts = progId.Split('.', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length < 2) return null;   // bare words ("Directory", "Folder", "txtfile") are Windows' own classes
        var candidates = new List<string> { parts[0] };
        if (parts.Length >= 2) candidates.Add(parts[0] + parts[1]);
        Models.LeftoverConfidence? best = null;
        foreach (var c in candidates)
        {
            var m = NameNormalizer.Match(c, keys, allowFuzzy: false);
            if (m is null || m == Models.LeftoverConfidence.Low) continue;
            if (best is null || m < best) best = m;
        }
        return best;
    }

    /// <summary>A usable folder for the program: its install location, else the folder of its main or uninstall exe (never a system folder).</summary>
    public static string? EffectiveFolder(string? installLocation, string? primaryExe, string? uninstallExe)
    {
        foreach (var candidate in new[] { installLocation, ParentOf(primaryExe), ParentOf(uninstallExe) })
        {
            if (string.IsNullOrWhiteSpace(candidate)) continue;
            var norm = PathUtil.NormalizeForCompare(candidate);
            if (norm.Length < 4 || PathUtil.IsProtectedRoot(norm, checkProtectedNames: true)) continue;
            return norm;
        }
        return null;
    }

    private static string? ParentOf(string? path) => string.IsNullOrWhiteSpace(path) ? null : PathUtil.ParentPath(path.Trim().Trim('"'));
}
