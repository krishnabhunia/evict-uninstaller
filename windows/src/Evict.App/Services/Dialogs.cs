using System.Diagnostics;
using System.Windows;
using Evict.Core.Services;

namespace Evict.App.Services;

/// <summary>Thin wrappers around MessageBox / shell so view models stay free of UI plumbing.</summary>
public static class Dialogs
{
    private static Window? Owner => Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive) ?? Application.Current?.MainWindow;

    public static bool Confirm(string message, string title = AppPaths.ProductName, bool destructive = false)
    {
        var owner = Owner;
        var result = owner != null
            ? MessageBox.Show(owner, message, title, MessageBoxButton.YesNo, destructive ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No)
            : MessageBox.Show(message, title, MessageBoxButton.YesNo, destructive ? MessageBoxImage.Warning : MessageBoxImage.Question, MessageBoxResult.No);
        return result == MessageBoxResult.Yes;
    }

    public static void Info(string message, string title = AppPaths.ProductName)
    {
        var owner = Owner;
        if (owner != null) MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Information);
        else MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Information);
    }

    public static void Error(string message, string title = AppPaths.ProductName)
    {
        var owner = Owner;
        if (owner != null) MessageBox.Show(owner, message, title, MessageBoxButton.OK, MessageBoxImage.Error);
        else MessageBox.Show(message, title, MessageBoxButton.OK, MessageBoxImage.Error);
    }

    public static void OpenFolder(string? path)
    {
        if (string.IsNullOrEmpty(path)) return;
        try
        {
            if (Directory.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else if (File.Exists(path)) Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else Info("The folder no longer exists:\n" + path);
        }
        catch (Exception ex) { Error("Could not open folder: " + ex.Message); }
    }

    public static void OpenUrl(string? url)
    {
        if (string.IsNullOrWhiteSpace(url)) return;
        try
        {
            if (!url.StartsWith("http", StringComparison.OrdinalIgnoreCase) && !url.StartsWith("ms-", StringComparison.OrdinalIgnoreCase)) url = "https://" + url;
            Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
        }
        catch (Exception ex) { Error("Could not open link: " + ex.Message); }
    }

    /// <summary>Opens regedit positioned at the given key (uses Regedit's LastKey setting).</summary>
    public static void OpenRegistryKey(string fullPath)
    {
        try
        {
            using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(@"Software\Microsoft\Windows\CurrentVersion\Applets\Regedit");
            key?.SetValue("LastKey", fullPath);
            Process.Start(new ProcessStartInfo("regedit.exe") { UseShellExecute = true });
        }
        catch (Exception ex) { Error("Could not open Registry Editor: " + ex.Message); }
    }

    public static void CopyToClipboard(string? text)
    {
        if (string.IsNullOrEmpty(text)) return;
        try { Clipboard.SetText(text); } catch { /* clipboard busy */ }
    }
}
