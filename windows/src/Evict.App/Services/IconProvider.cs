using System.Collections.Concurrent;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Evict.Core.Models;
using Evict.Core.Util;

namespace Evict.App.Services;

/// <summary>Extracts program icons from executables / .ico files and caches them as frozen bitmaps.</summary>
public sealed class IconProvider
{
    private readonly ConcurrentDictionary<string, ImageSource?> _cache = new(StringComparer.OrdinalIgnoreCase);

    [DllImport("shell32.dll", CharSet = CharSet.Unicode, ExactSpelling = true)]
    private static extern uint ExtractIconExW(string lpszFile, int nIconIndex, out IntPtr phiconLarge, out IntPtr phiconSmall, uint nIcons);

    [DllImport("user32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool DestroyIcon(IntPtr hIcon);

    /// <summary>Icon for a program, or null if none could be extracted. Safe to call from any thread.</summary>
    public ImageSource? GetIcon(InstalledProgram p)
    {
        foreach (var (path, index) in Candidates(p))
        {
            if (string.IsNullOrEmpty(path)) continue;
            var key = $"{path}|{index}";
            var img = _cache.GetOrAdd(key, _ => Extract(path, index));
            if (img != null) return img;
        }
        return null;
    }

    public ImageSource? GetIconForPath(string path, int index = 0)
    {
        if (string.IsNullOrEmpty(path)) return null;
        return _cache.GetOrAdd($"{path}|{index}", _ => Extract(path, index));
    }

    private static IEnumerable<(string Path, int Index)> Candidates(InstalledProgram p)
    {
        var (iconPath, idx) = PathUtil.SplitIconPath(p.DisplayIcon);
        if (!string.IsNullOrEmpty(iconPath) && File.Exists(iconPath)) yield return (iconPath, idx);
        if (!string.IsNullOrEmpty(p.PrimaryExecutable) && File.Exists(p.PrimaryExecutable)) yield return (p.PrimaryExecutable!, 0);
        var cmd = UninstallCommandParser.Parse(p.UninstallString);
        if (cmd is not null && !UninstallCommandParser.IsMsiExec(cmd) && File.Exists(cmd.FileName)) yield return (cmd.FileName, 0);
    }

    private static ImageSource? Extract(string path, int index)
    {
        try
        {
            if (path.EndsWith(".ico", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".png", StringComparison.OrdinalIgnoreCase))
            {
                var bmp = new BitmapImage();
                bmp.BeginInit();
                bmp.CacheOption = BitmapCacheOption.OnLoad;
                bmp.UriSource = new Uri(path);
                bmp.DecodePixelWidth = 32;
                bmp.EndInit();
                bmp.Freeze();
                return bmp;
            }

            uint count = ExtractIconExW(path, index, out var large, out var small, 1);
            if (count == 0 || count == uint.MaxValue)
            {
                // Negative resource ids are also allowed; try index 0 as a fallback.
                if (index != 0) count = ExtractIconExW(path, 0, out large, out small, 1);
                if (count == 0 || count == uint.MaxValue) return null;
            }
            try
            {
                var h = large != IntPtr.Zero ? large : small;
                if (h == IntPtr.Zero) return null;
                var src = Imaging.CreateBitmapSourceFromHIcon(h, Int32Rect.Empty, BitmapSizeOptions.FromWidthAndHeight(32, 32));
                src.Freeze();
                return src;
            }
            finally
            {
                if (large != IntPtr.Zero) DestroyIcon(large);
                if (small != IntPtr.Zero) DestroyIcon(small);
            }
        }
        catch
        {
            return null;
        }
    }
}
