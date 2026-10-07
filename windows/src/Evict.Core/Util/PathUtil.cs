using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;

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
        if (!TryCanonicalizeAbsolute(path, out var p) || !TryCanonicalizeAbsolute(root, out var r)) return false;
        p = p.Replace('/', '\\');
        r = r.Replace('/', '\\');
        if (!r.EndsWith('\\')) r += "\\";
        return (p + "\\").StartsWith(r, StringComparison.OrdinalIgnoreCase);
    }

    public static string NormalizeForCompare(string path) => TryCanonicalizeAbsolute(path, out var canonical) ? canonical : "";

    /// <summary>Canonical absolute path, independent of the current directory. Ambiguous Win32 spellings fail closed.</summary>
    public static bool TryCanonicalizeAbsolute(string? path, out string canonical)
    {
        canonical = "";
        if (string.IsNullOrWhiteSpace(path)) return false;
        var cleaned = Environment.ExpandEnvironmentVariables(path.Trim().Trim('"').Trim());
        if (cleaned.Length == 0) return false;
        var p = cleaned.Replace('/', '\\');
        if (p.StartsWith(@"\\?\") || p.StartsWith(@"\\.\") || p.Any(c => c < ' ' || "<>\"|?*".Contains(c))) return false;
        string root;
        string rest;
        if (p.Length >= 3 && char.IsLetter(p[0]) && p[1] == ':' && p[2] == '\\')
        {
            root = char.ToUpperInvariant(p[0]) + @":\";
            rest = p[3..];
        }
        else if (p.StartsWith(@"\\"))
        {
            var parts = p[2..].Split('\\', StringSplitOptions.RemoveEmptyEntries);
            if (parts.Length < 2 || parts.Take(2).Any(s => s is "." or ".." || s.EndsWith('.') || s.EndsWith(' ') || s.Contains(':'))) return false;
            root = @"\\" + parts[0] + "\\" + parts[1];
            rest = string.Join("\\", parts.Skip(2));
        }
        else if (!OperatingSystem.IsWindows() && cleaned.StartsWith('/'))
        {
            try { canonical = Path.GetFullPath(cleaned).TrimEnd('/'); if (canonical.Length == 0) canonical = "/"; return true; }
            catch { return false; }
        }
        else return false; // drive-relative paths (C:Foo), rooted-relative paths and ordinary relative paths

        var segments = new List<string>();
        foreach (var segment in rest.Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            if (segment == ".") continue;
            if (segment == "..")
            {
                if (segments.Count > 0) segments.RemoveAt(segments.Count - 1);
                continue;
            }
            if (segment.EndsWith('.') || segment.EndsWith(' ') || segment.Contains(':')) return false;
            segments.Add(segment);
        }
        canonical = root.TrimEnd('\\') + (segments.Count > 0 ? "\\" + string.Join("\\", segments) : root.EndsWith('\\') ? "\\" : "");
        if (OperatingSystem.IsWindows()) canonical = ExpandShortPath(canonical);
        return true;
    }

    private static string ExpandShortPath(string path)
    {
        var existing = path;
        var suffix = new Stack<string>();
        while (!string.IsNullOrEmpty(existing))
        {
            var buffer = new StringBuilder(260);
            uint length = GetLongPathName(existing, buffer, (uint)buffer.Capacity);
            if (length >= buffer.Capacity && length < 32768)
            {
                buffer = new StringBuilder((int)length + 1);
                length = GetLongPathName(existing, buffer, (uint)buffer.Capacity);
            }
            if (length > 0 && length < buffer.Capacity)
            {
                var expanded = buffer.ToString();
                foreach (var segment in suffix) expanded = Path.Combine(expanded, segment);
                return expanded;
            }
            var parent = Path.GetDirectoryName(existing);
            if (string.IsNullOrEmpty(parent) || parent == existing) break;
            suffix.Push(Path.GetFileName(existing));
            existing = parent;
        }
        return path;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetLongPathName(string shortPath, StringBuilder longPath, uint bufferLength);

    /// <summary>Checks the object actually opened before a destructive write, including a concurrent parent link swap.</summary>
    internal static bool OpenedFileMatchesPath(SafeFileHandle handle, string expectedPath)
    {
        if (!OperatingSystem.IsWindows()) return !HasReparsePoint(expectedPath);
        if (!GetFileInformationByHandle(handle, out var information) || information.NumberOfLinks != 1) return false;
        var buffer = new StringBuilder(32768);
        var length = GetFinalPathNameByHandle(handle, buffer, (uint)buffer.Capacity, 0);
        if (length == 0 || length >= buffer.Capacity) return false;
        var actual = buffer.ToString();
        if (actual.StartsWith(@"\\?\UNC\", StringComparison.OrdinalIgnoreCase)) actual = @"\\" + actual[8..];
        else if (actual.StartsWith(@"\\?\")) actual = actual[4..];
        return IsUnder(actual, expectedPath) && IsUnder(expectedPath, actual);
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern uint GetFinalPathNameByHandle(SafeFileHandle handle, StringBuilder path, uint bufferLength, uint flags);

    [StructLayout(LayoutKind.Sequential)]
    private struct OpenedFileInformation
    {
        public uint Attributes;
        public uint CreationLow, CreationHigh, AccessLow, AccessHigh, WriteLow, WriteHigh;
        public uint VolumeSerialNumber, SizeHigh, SizeLow, NumberOfLinks, IndexHigh, IndexLow;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool GetFileInformationByHandle(SafeFileHandle handle, out OpenedFileInformation information);

    /// <summary>True if an existing path component is a junction/symlink, or cannot be safely inspected.</summary>
    public static bool HasReparsePoint(string path)
    {
        if (!TryCanonicalizeAbsolute(path, out var p)) return true;
        while (!string.IsNullOrEmpty(p))
        {
            try { if ((File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0) return true; }
            catch (FileNotFoundException) { }
            catch (DirectoryNotFoundException) { }
            catch { return true; }
            var parent = Path.GetDirectoryName(p);
            if (parent == p) break;
            p = parent ?? "";
        }
        return false;
    }

    /// <summary>Depth of a path from its root: "C:\" → 0, "C:\Program Files" → 1, "C:\Program Files\Foo" → 2.</summary>
    /// <remarks>Implemented without <see cref="Path"/> so the logic is identical (and testable) on every OS.</remarks>
    public static int Depth(string path)
    {
        var p = NormalizeForCompare(path).Replace('/', '\\');
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
        if (!OperatingSystem.IsWindows() && path.StartsWith('/')) return Path.GetDirectoryName(path.TrimEnd('/'));
        var p = path.Replace('/', '\\').TrimEnd('\\');
        int i = p.LastIndexOf('\\');
        if (i <= 0) return null;
        var parent = p[..i];
        return parent.Length == 2 && parent[1] == ':' ? parent + "\\" : parent;
    }

    /// <summary>Paths that must never be deleted as a whole, regardless of what a scanner thinks.</summary>
    public static bool IsProtectedRoot(string path, bool checkProtectedNames = true)
    {
        if (!TryCanonicalizeAbsolute(path, out var p) || HasReparsePoint(p)) return true;
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
