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

    /// <summary>
    /// A question with several answers, one button per answer (stacked, so longer labels fit). The first answer is the
    /// highlighted default. Returns the chosen index; closing the window counts as <paramref name="cancelIndex"/>
    /// (the last answer when not given).
    /// </summary>
    public static int Choose(string heading, string message, IReadOnlyList<string> options, int cancelIndex = -1, string title = AppPaths.ProductName)
    {
        if (options.Count == 0) return -1;
        if (cancelIndex < 0 || cancelIndex >= options.Count) cancelIndex = options.Count - 1;
        int chosen = cancelIndex;

        var window = new Window
        {
            Title = title,
            Width = 560,
            SizeToContent = SizeToContent.Height,
            ResizeMode = ResizeMode.NoResize,
            Owner = Owner,
            Icon = new System.Windows.Media.Imaging.BitmapImage(new Uri("pack://application:,,,/Assets/app.ico")),
        };
        if (Application.Current?.TryFindResource("Window.Dialog") is Style style) window.Style = style;

        var panel = new System.Windows.Controls.StackPanel { Margin = new Thickness(24) };
        panel.Children.Add(Text(heading, "Text.Section", new Thickness(0, 0, 0, 10)));
        panel.Children.Add(Text(message, "Text.Body", new Thickness(0, 0, 0, 16)));
        for (int i = 0; i < options.Count; i++)
        {
            int index = i;
            var button = new System.Windows.Controls.Button
            {
                Content = new System.Windows.Controls.TextBlock { Text = options[i], TextWrapping = TextWrapping.Wrap },
                HorizontalAlignment = HorizontalAlignment.Stretch,
                HorizontalContentAlignment = HorizontalAlignment.Left,
                Margin = new Thickness(0, 0, 0, 8),
                Padding = new Thickness(14, 8, 14, 8),
                IsDefault = i == 0,
                IsCancel = i == cancelIndex,
            };
            if (Application.Current?.TryFindResource(i == 0 ? "Button.Primary" : "Button.Secondary") is Style bs) button.Style = bs;
            button.Click += (_, _) => { chosen = index; window.Close(); };
            panel.Children.Add(button);
        }
        window.Content = panel;
        App.UiState.ApplyToDialog(window);
        if (window.Owner == null) window.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        window.ShowDialog();
        return chosen;

        static System.Windows.Controls.TextBlock Text(string text, string styleKey, Thickness margin)
        {
            var tb = new System.Windows.Controls.TextBlock { Text = text, TextWrapping = TextWrapping.Wrap, Margin = margin };
            if (Application.Current?.TryFindResource(styleKey) is Style st) tb.Style = st;
            return tb;
        }
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
