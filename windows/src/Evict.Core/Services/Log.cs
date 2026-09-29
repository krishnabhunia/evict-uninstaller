using System.Diagnostics;

namespace Evict.Core.Services;

/// <summary>Minimal append-only diagnostic log (also mirrored to Debug output).</summary>
public static class Log
{
    private static readonly object Gate = new();

    public static void Info(string message) => Write("INFO ", message);
    public static void Warn(string message) => Write("WARN ", message);
    public static void Error(string message, Exception? ex = null) => Write("ERROR", ex is null ? message : $"{message}: {ex}");

    private static void Write(string level, string message)
    {
        var line = $"{DateTime.Now:yyyy-MM-dd HH:mm:ss.fff} [{level}] {message}";
        Debug.WriteLine(line);
        lock (Gate)
        {
            try
            {
                var f = AppPaths.LogFile;
                if (File.Exists(f) && new FileInfo(f).Length > 2 * 1024 * 1024)
                    File.Move(f, f + ".old", overwrite: true);
                File.AppendAllText(f, line + Environment.NewLine);
            }
            catch { /* never throw from logging */ }
        }
    }
}
