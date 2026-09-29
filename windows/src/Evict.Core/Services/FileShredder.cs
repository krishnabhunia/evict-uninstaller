using System.Security.Cryptography;
using Evict.Core.Models;
using Evict.Core.Util;

namespace Evict.Core.Services;

/// <summary>
/// Secure file deletion: overwrites file contents with one or more passes, truncates, renames the file to a
/// random name (hiding the original name in the MFT) and deletes it. Note: on SSDs wear-levelling means
/// overwritten sectors may survive – the UI shows this caveat.
/// </summary>
public sealed class FileShredder
{
    private const int BufferSize = 1024 * 1024;

    public Task<ShredResult> ShredAsync(IEnumerable<string> paths, ShredMethod method, IProgress<ProgressReport>? progress, CancellationToken ct) =>
        Task.Run(() => Shred(paths, method, progress, ct), ct);

    public ShredResult Shred(IEnumerable<string> paths, ShredMethod method, IProgress<ProgressReport>? progress, CancellationToken ct)
    {
        var result = new ShredResult();
        var files = new List<string>();
        var dirs = new List<string>();

        foreach (var p in paths)
        {
            if (File.Exists(p)) files.Add(p);
            else if (Directory.Exists(p))
            {
                if (PathUtil.IsProtectedRoot(p, checkProtectedNames: true))
                {
                    result.Errors.Add((p, "Refusing to shred a protected system folder."));
                    continue;
                }
                dirs.Add(p);
                try
                {
                    files.AddRange(Directory.EnumerateFiles(p, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true, AttributesToSkip = FileAttributes.ReparsePoint }));
                }
                catch (Exception ex) { result.Errors.Add((p, ex.Message)); }
            }
        }

        int done = 0;
        foreach (var file in files)
        {
            ct.ThrowIfCancellationRequested();
            done++;
            progress?.Report(new ProgressReport($"Shredding {Path.GetFileName(file)} ({done}/{files.Count})", 100.0 * done / Math.Max(1, files.Count)));
            try
            {
                result.BytesOverwritten += ShredFile(file, method, ct);
                result.FilesShredded++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { result.Errors.Add((file, ex.Message)); }
        }

        foreach (var dir in dirs.OrderByDescending(d => d.Length))
        {
            try
            {
                // Rename sub-directories too, so their names do not linger in the MFT.
                foreach (var sub in Directory.EnumerateDirectories(dir, "*", new EnumerationOptions { RecurseSubdirectories = true, IgnoreInaccessible = true }).OrderByDescending(s => s.Length))
                {
                    try { Directory.Delete(RenameRandom(sub), recursive: true); } catch { /* try parent */ }
                }
                Directory.Delete(RenameRandom(dir), recursive: true);
                result.FoldersRemoved++;
            }
            catch (Exception ex) { result.Errors.Add((dir, ex.Message)); }
        }
        return result;
    }

    /// <summary>Overwrites and deletes one file, returning the number of bytes written.</summary>
    public static long ShredFile(string path, ShredMethod method, CancellationToken ct)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return 0;
        info.Attributes = FileAttributes.Normal;
        long length = info.Length;
        long written = 0;
        int passes = (int)method;

        if (length > 0)
        {
            var buffer = new byte[BufferSize];
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None, BufferSize, FileOptions.WriteThrough))
            {
                for (int pass = 0; pass < passes; pass++)
                {
                    ct.ThrowIfCancellationRequested();
                    FillPattern(buffer, pass, passes);
                    fs.Position = 0;
                    long remaining = length;
                    while (remaining > 0)
                    {
                        ct.ThrowIfCancellationRequested();
                        int chunk = (int)Math.Min(remaining, buffer.Length);
                        if (IsRandomPass(pass, passes)) RandomNumberGenerator.Fill(buffer.AsSpan(0, chunk));
                        fs.Write(buffer, 0, chunk);
                        remaining -= chunk;
                        written += chunk;
                    }
                    fs.Flush(flushToDisk: true);
                }
                fs.SetLength(0);
            }
        }

        // Scrub the file name and timestamps, then delete.
        try
        {
            var stamp = new DateTime(2000, 1, 1);
            File.SetCreationTime(path, stamp); File.SetLastWriteTime(path, stamp); File.SetLastAccessTime(path, stamp);
        }
        catch { /* ignore */ }
        var renamed = RenameRandom(path);
        File.Delete(renamed);
        return written;
    }

    private static bool IsRandomPass(int pass, int passes) => passes switch
    {
        1 => false,
        3 => pass == 2,
        _ => pass is 1 or 3 or 5 or 6,
    };

    private static void FillPattern(byte[] buffer, int pass, int passes)
    {
        byte value = passes switch
        {
            1 => 0x00,
            3 => pass switch { 0 => 0x00, 1 => 0xFF, _ => 0x00 },
            _ => pass switch { 0 => 0x00, 2 => 0xFF, 4 => 0xAA, _ => 0x55 },
        };
        Array.Fill(buffer, value);
    }

    private static string RenameRandom(string path)
    {
        var dir = Path.GetDirectoryName(path);
        if (string.IsNullOrEmpty(dir)) return path;
        for (int attempt = 0; attempt < 3; attempt++)
        {
            var target = Path.Combine(dir, Path.GetRandomFileName().Replace(".", ""));
            try
            {
                if (File.Exists(path)) File.Move(path, target);
                else if (Directory.Exists(path)) Directory.Move(path, target);
                else return path;
                return target;
            }
            catch (IOException) when (attempt < 2) { /* collision – retry */ }
            catch { return path; }
        }
        return path;
    }
}
