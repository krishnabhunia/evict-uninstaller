namespace Evict.Core.Util;

public static class PathUtil
{
    /// <summary>Trims quotes/whitespace and expands %ENV% variables. Returns null for empty input.</summary>
    public static string? Clean(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var p = path.Trim().Trim('"').Trim();
        if (p.Length == 0) return null;
        try { p = Environment.ExpandEnvironmentVariables(p); } catch { /* ignore */ }
        // Drop a trailing ",0" icon index if someone stored DisplayIcon in InstallLocation.
        return p.TrimEnd('\\', '/') is { Length: > 0 } t ? t : p;
    }

    /// <summary>"C:\App\app.exe,0" → ("C:\App\app.exe", 0). Handles negative resource ids and quotes.</summary>
    public static (string Path, int Index) SplitIconPath(string? displayIcon)
    {
        if (string.IsNullOrWhiteSpace(displayIcon)) return ("", 0);
        var s = displayIcon.Trim();
        int index = 0;
        int comma = s.LastIndexOf(',');
        if (comma > 0 && comma < s.Length - 1 && int.TryParse(s[(comma + 1)..].Trim(), out var idx))
        {
            index = idx;
            s = s[..comma];
        }
        s = s.Trim().Trim('"');
        try { s = Environment.ExpandEnvironmentVariables(s); } catch { /* ignore */ }
        return (s, index);
    }

    public static bool IsUnder(string? path, string? root)
    {
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(root)) return false;
        var p = NormalizeForCompare(path);
        var r = NormalizeForCompare(root);
        if (r.Length == 0) return false;
        if (!r.EndsWith('\\')) r += "\\";
        return (p + "\\").StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeForCompare(string path)
    {
        var p = path.Trim().Trim('"').Replace('/', '\\');
        while (p.Length > 3 && p.EndsWith('\\')) p = p[..^1];
        return p;
    }

    /// <summary>Depth of a path from its root: "C:\" → 0, "C:\Program Files" → 1, "C:\Program Files\Foo" → 2.</summary>
    /// <remarks>Implemented without <see cref="Path"/> so the logic is identical (and testable) on every OS.</remarks>
    public static int Depth(string path)
    {
        var p = NormalizeForCompare(path);
        string rest;
        if (p.Length >= 2 && char.IsLetter(p[0]) && p[1] == ':')
            rest = p[2..];                                   // drive-letter path
        else if (p.StartsWith(@"\\"))
        {
            // UNC: \\server\share\rest
            var parts = p.TrimStart('\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
            return Math.Max(0, parts.Length - 2);
        }
        else rest = p;
        return rest.Split('\\', StringSplitOptions.RemoveEmptyEntries).Length;
    }

    /// <summary>Last path segment, treating both separators as separators regardless of OS.</summary>
    public static string LeafName(string? path)
    {
        if (string.IsNullOrEmpty(path)) return "";
        var p = path.Replace('/', '\\').TrimEnd('\\');
        int i = p.LastIndexOf('\\');
        return i < 0 ? p : p[(i + 1)..];
    }

    /// <summary>Parent folder, or null at a root. Separator-agnostic.</summary>
    public static string? ParentPath(string? path)
    {
        if (string.IsNullOrEmpty(path)) return null;
        var p = path.Replace('/', '\\').TrimEnd('\\');
        int i = p.LastIndexOf('\\');
        if (i <= 0) return null;
        var parent = p[..i];
        return parent.Length == 2 && parent[1] == ':' ? parent + "\\" : parent;
    }

    /// <summary>Paths that must never be deleted as a whole, regardless of what a scanner thinks.</summary>
    public static bool IsProtectedRoot(string path, bool checkProtectedNames = true)
    {
        var p = NormalizeForCompare(path);
        if (Depth(p) <= 1) return true; // drive roots and first-level folders (Program Files, Users, Windows…)

        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (!string.IsNullOrEmpty(windows) && IsUnder(p, windows)) return true;

        var leaf = LeafName(p);
        if (checkProtectedNames && NameNormalizer.ProtectedNames.Contains(leaf)) return true;

        // User profile roots (C:\Users\Krishna) and their direct AppData folders.
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        if (!string.IsNullOrEmpty(profile))
        {
            var usersRoot = ParentPath(profile);
            if (usersRoot != null && IsUnder(p, usersRoot) && Depth(p) - Depth(usersRoot) <= 3)
            {
                // C:\Users\X (depth 2), C:\Users\X\AppData (3), C:\Users\X\AppData\Local (4) ⇒ protected
                return true;
            }
        }
        return false;
    }

    public static string Combine(params string[] parts) => Path.Combine(parts);
}
