using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;
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

        foreach (var input in paths)
        {
            if (!PathUtil.TryCanonicalizeAbsolute(input, out var p) || PathUtil.HasReparsePoint(p))
            {
                result.Errors.Add((input, "Refusing to shred a relative path or a path through a junction/symlink."));
                continue;
            }
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
        foreach (var file in files.Distinct(StringComparer.OrdinalIgnoreCase))
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
                ct.ThrowIfCancellationRequested();
                DeleteEmptyTree(dir, dir, ct);
                result.FoldersRemoved++;
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex) { result.Errors.Add((dir, ex.Message)); }
        }
        return result;
    }

    /// <summary>Overwrites and deletes one file, returning the number of bytes written.</summary>
    public static long ShredFile(string path, ShredMethod method, CancellationToken ct)
    {
        if (!PathUtil.TryCanonicalizeAbsolute(path, out var canonical) || PathUtil.HasReparsePoint(canonical))
            throw new IOException("Refusing to overwrite a file through a junction/symlink.");
        path = canonical;
        var info = new FileInfo(path);
        if (!info.Exists) return 0;
        long written = 0;
        int passes = (int)method;

        using (var fs = new FileStream(path, FileMode.Open, FileAccess.Write, FileShare.None, BufferSize, FileOptions.WriteThrough))
        {
            if (!PathUtil.OpenedFileMatchesPath(fs.SafeFileHandle, path))
                throw new IOException("The opened file resolved outside the selected path or has other hard links; it was not overwritten.");
            long length = fs.Length;
            if (length > 0)
            {
                var buffer = new byte[BufferSize];
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
            }
            fs.SetLength(0);
            // Change timestamps through the verified handle, never through a path that could be replaced.
            if (OperatingSystem.IsWindows())
            {
                long stamp = new DateTime(2000, 1, 1).ToFileTime();
                SetFileTime(fs.SafeFileHandle, ref stamp, ref stamp, ref stamp);
            }
        }

        var renamed = RenameRandom(path);
        File.Delete(renamed);
        return written;
    }

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetFileTime(SafeFileHandle handle, ref long creationTime, ref long lastAccessTime, ref long lastWriteTime);

    private static void DeleteEmptyTree(string directory, string root, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!PathUtil.IsUnder(directory, root) || PathUtil.HasReparsePoint(directory))
            throw new IOException("Refusing to traverse a junction/symlink while removing folders.");
        foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
        {
            ct.ThrowIfCancellationRequested();
            var attributes = File.GetAttributes(entry);
            if ((attributes & FileAttributes.ReparsePoint) != 0)
            {
                if ((attributes & FileAttributes.Directory) != 0) Directory.Delete(entry, false);
                else File.Delete(entry);
            }
            else if ((attributes & FileAttributes.Directory) != 0) DeleteEmptyTree(entry, root, ct);
            else throw new IOException("A file could not be shredded; its folder was kept.");
        }
        // Never recursively delete a parent to hide errors from shredding its children.
        Directory.Delete(RenameRandom(directory), false);
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
