namespace Evict.Core.Services;

public static class DirectorySizeCalculator
{
    private static readonly EnumerationOptions Options = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint,
        ReturnSpecialDirectories = false,
    };

    /// <summary>Sum of file sizes under <paramref name="path"/>. Returns null if the folder does not exist.</summary>
    public static long? Measure(string? path, CancellationToken ct = default, long? stopAfterBytes = null)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            if (!Directory.Exists(path)) return null;
            long total = 0;
            var dir = new DirectoryInfo(path);
            foreach (var f in dir.EnumerateFiles("*", Options))
            {
                ct.ThrowIfCancellationRequested();
                try { total += f.Length; } catch { /* vanished */ }
                if (stopAfterBytes is { } cap && total > cap) return total;
            }
            return total;
        }
        catch (OperationCanceledException) { throw; }
        catch { return null; }
    }

    public static Task<long?> MeasureAsync(string? path, CancellationToken ct = default) =>
        Task.Run(() => Measure(path, ct), ct);

    /// <summary>Number of files and folders directly under the path (used for "empty folder" checks).</summary>
    public static bool IsEmptyDirectory(string path)
    {
        try { return Directory.Exists(path) && !Directory.EnumerateFileSystemEntries(path).Any(); }
        catch { return false; }
    }
}
