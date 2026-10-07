using System.Security.Principal;
using System.Text;
using Evict.Core.Util;

namespace Evict.Core.Services;

/// <summary>
/// Puts files and folders that a cleanup sent to the Recycle Bin back where they were – the rollback for leftover
/// removal. Works on the Recycle Bin's own records: for every deleted item Windows keeps "$R…" (the item) and "$I…"
/// (original path + deletion time) in X:\$Recycle.Bin\&lt;user SID&gt;.
/// </summary>
public static class RecycleBinService
{
    /// <summary>Original path and deletion time (UTC) from a "$I…" record (Vista–8: version 1, Windows 10+: version 2).</summary>
    public static (string Path, DateTime DeletedUtc)? ParseInfoRecord(byte[] data)
    {
        try
        {
            if (data.Length < 24) return null;
            long version = BitConverter.ToInt64(data, 0);
            var deleted = DateTime.FromFileTimeUtc(BitConverter.ToInt64(data, 16));
            string path;
            if (version == 1)
            {
                int len = Math.Min(520, data.Length - 24);
                path = Encoding.Unicode.GetString(data, 24, len);
            }
            else if (version == 2)
            {
                if (data.Length < 28) return null;
                int chars = BitConverter.ToInt32(data, 24);
                int bytes = Math.Min(chars * 2, data.Length - 28);
                if (bytes <= 0) return null;
                path = Encoding.Unicode.GetString(data, 28, bytes);
            }
            else return null;
            int nul = path.IndexOf('\0');
            if (nul >= 0) path = path[..nul];
            return path.Length > 0 ? (path, deleted) : null;
        }
        catch { return null; }
    }

    public sealed class RestoreResult
    {
        public int Restored { get; set; }
        public List<string> RestoredPaths { get; } = new();
        public List<string> NotFound { get; } = new();
        public List<string> Errors { get; } = new();
        public List<string> Warnings { get; } = new();
    }

    /// <summary>
    /// Restores the newest Recycle Bin entry of each path deleted at or after <paramref name="deletedAfterUtc"/>.
    /// Paths that were deleted permanently (not recycled) are reported as not found; a path that exists again is left alone.
    /// </summary>
    public static RestoreResult Restore(IEnumerable<string> originalPaths, DateTime deletedAfterUtc)
    {
        var result = new RestoreResult();
        var wanted = new HashSet<string>(originalPaths.Select(Normalize), StringComparer.OrdinalIgnoreCase);
        if (wanted.Count == 0) return result;

        string? sid = null;
        try { using var id = WindowsIdentity.GetCurrent(); sid = id.User?.Value; } catch { /* not Windows */ }
        if (sid == null) { result.Errors.Add("The Recycle Bin could not be opened."); return result; }

        // Newest record per original path.
        var found = new Dictionary<string, (string InfoFile, DateTime Deleted)>(StringComparer.OrdinalIgnoreCase);
        foreach (var drive in DriveInfo.GetDrives())
        {
            try
            {
                if (!drive.IsReady || drive.DriveType != DriveType.Fixed) continue;
                var bin = Path.Combine(drive.RootDirectory.FullName, "$Recycle.Bin", sid);
                if (!Directory.Exists(bin)) continue;
                foreach (var info in Directory.EnumerateFiles(bin, "$I*"))
                {
                    var rec = ParseInfoRecord(File.ReadAllBytes(info));
                    if (rec is not { } r || r.DeletedUtc < deletedAfterUtc.AddMinutes(-1)) continue;
                    var key = Normalize(r.Path);
                    if (!wanted.Contains(key)) continue;
                    if (!found.TryGetValue(key, out var prev) || r.DeletedUtc > prev.Deleted) found[key] = (info, r.DeletedUtc);
                }
            }
            catch (Exception ex) { Log.Warn($"Recycle Bin on {drive.Name}: {ex.Message}"); }
        }

        // Parents before children, so a restored folder is in place before files that were inside it come back.
        foreach (var path in wanted.OrderBy(p => p.Length))
        {
            if (!found.TryGetValue(path, out var rec)) { result.NotFound.Add(path); continue; }
            try
            {
                var dir = Path.GetDirectoryName(rec.InfoFile)!;
                var item = Path.Combine(dir, "$R" + Path.GetFileName(rec.InfoFile)[2..]);
                if (File.Exists(path) || Directory.Exists(path)) { result.Errors.Add($"{path}: something with this name exists again – left in the Recycle Bin."); continue; }
                var parent = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(parent)) Directory.CreateDirectory(parent);
                if (Directory.Exists(item)) RestoreItem(item, path, rec.InfoFile, result, Directory.Move);
                else if (File.Exists(item)) RestoreItem(item, path, rec.InfoFile, result, File.Move);
                else { result.NotFound.Add(path); continue; }
            }
            catch (Exception ex) { result.Errors.Add($"{path}: {ex.Message}"); }
        }
        return result;
    }

    // Moving the data is the restore. A stale metadata record cannot make that successful move retryable.
    internal static void RestoreItem(string item, string path, string infoFile, RestoreResult result,
        Action<string, string> moveItem, Action<string>? deleteInfo = null)
    {
        moveItem(item, path);
        result.Restored++;
        result.RestoredPaths.Add(path);
        try { (deleteInfo ?? File.Delete)(infoFile); }
        catch (Exception ex) { result.Warnings.Add($"{path}: restored, but its old Recycle Bin metadata could not be removed: {ex.Message}"); }
    }

    private static string Normalize(string path) => PathUtil.TryCanonicalizeAbsolute(path, out var canonical)
        ? canonical : path.Trim().TrimEnd('\\');
}
