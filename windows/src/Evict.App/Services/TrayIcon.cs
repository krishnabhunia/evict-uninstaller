using System.Windows;
using Evict.Core.Services;
using SD = System.Drawing;
using WF = System.Windows.Forms;

namespace Evict.App.Services;

/// <summary>
/// Notification-area icon with a context menu and balloon notifications (Windows 10/11 render balloon tips as toasts).
/// Wraps Windows Forms' NotifyIcon so no third-party package is needed; everything else in the app stays WPF.
/// </summary>
public sealed class TrayIcon : IDisposable
{
    private readonly WF.NotifyIcon _icon;
    private readonly WF.ToolStripMenuItem _detectItem;
    private readonly WF.ToolStripMenuItem _recordingItem;
    private Action? _balloonClick;
    private bool _disposed;

    public event Action? OpenRequested;
    public event Action? ScanRequested;
    public event Action? WidgetRequested;
    public event Action? RecordRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;
    public event Action<bool>? DetectionToggled;

    static TrayIcon()
    {
        try { WF.Application.EnableVisualStyles(); } catch { /* harmless */ }
    }

    public TrayIcon(bool detectionOn)
    {
        var menu = new WF.ContextMenuStrip();
        menu.Items.Add(Item("Open Evict", () => OpenRequested?.Invoke(), bold: true));
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(Item("Run Software Health scan", () => ScanRequested?.Invoke()));
        menu.Items.Add(Item("Show Easy Uninstall widget", () => WidgetRequested?.Invoke()));
        menu.Items.Add(Item("Record an installation…", () => RecordRequested?.Invoke()));
        menu.Items.Add(new WF.ToolStripSeparator());
        _detectItem = new WF.ToolStripMenuItem("Detect installers automatically") { CheckOnClick = true, Checked = detectionOn };
        _detectItem.CheckedChanged += (_, _) => DetectionToggled?.Invoke(_detectItem.Checked);
        menu.Items.Add(_detectItem);
        _recordingItem = new WF.ToolStripMenuItem("Not recording") { Enabled = false, Visible = false };
        menu.Items.Add(_recordingItem);
        menu.Items.Add(new WF.ToolStripSeparator());
        menu.Items.Add(Item("Settings", () => SettingsRequested?.Invoke()));
        menu.Items.Add(Item("Exit Evict", () => ExitRequested?.Invoke()));

        _icon = new WF.NotifyIcon
        {
            Icon = LoadIcon(),
            Text = AppPaths.ProductName,
            ContextMenuStrip = menu,
            Visible = true,
        };
        _icon.DoubleClick += (_, _) => OpenRequested?.Invoke();
        _icon.MouseClick += (_, e) => { if (e.Button == WF.MouseButtons.Left) OpenRequested?.Invoke(); };
        _icon.BalloonTipClicked += (_, _) => { var a = _balloonClick; _balloonClick = null; a?.Invoke(); };
        _icon.BalloonTipClosed += (_, _) => _balloonClick = null;
    }

    private static WF.ToolStripMenuItem Item(string text, Action onClick, bool bold = false)
    {
        var item = new WF.ToolStripMenuItem(text);
        if (bold) item.Font = new SD.Font(item.Font, SD.FontStyle.Bold);
        item.Click += (_, _) => onClick();
        return item;
    }

    private static SD.Icon LoadIcon()
    {
        try
        {
            var res = Application.GetResourceStream(new Uri("pack://application:,,,/Assets/app.ico", UriKind.Absolute));
            if (res != null)
            {
                using var stream = res.Stream;
                return new SD.Icon(stream, new SD.Size(32, 32));
            }
        }
        catch (Exception ex) { Log.Warn("Tray icon resource failed: " + ex.Message); }
        try { return SD.Icon.ExtractAssociatedIcon(UpdateService.ExePath) ?? SD.SystemIcons.Application; } catch { return SD.SystemIcons.Application; }
    }

    public bool DetectionChecked { get => _detectItem.Checked; set => _detectItem.Checked = value; }

    /// <summary>Shows/hides the "Recording: X" status line in the menu and updates the hover text.</summary>
    public void SetRecording(string? what)
    {
        _recordingItem.Visible = what != null;
        _recordingItem.Text = what == null ? "Not recording" : "● Recording: " + what;
        var text = what == null ? AppPaths.ProductName : $"{AppPaths.ProductName} – recording {what}";
        _icon.Text = text.Length > 127 ? text[..127] : text;
    }

    /// <summary>Balloon / toast notification. <paramref name="onClick"/> runs on the UI thread when the user clicks it.</summary>
    public void ShowBalloon(string title, string text, Action? onClick = null, WF.ToolTipIcon icon = WF.ToolTipIcon.Info, int timeoutMs = 10000)
    {
        if (_disposed) return;
        _balloonClick = onClick;
        try { _icon.ShowBalloonTip(timeoutMs, title, text.Length > 250 ? text[..250] : text, icon); }
        catch (Exception ex) { Log.Warn("Balloon failed: " + ex.Message); }
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _icon.Visible = false; _icon.Dispose(); } catch { /* ignore */ }
    }
}

/// <summary>"Start with Windows" via the per-user Run key (no administrator rights needed).</summary>
public static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private const string ValueName = "Evict";

    public static bool IsEnabled()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            return k?.GetValue(ValueName) is string;
        }
        catch { return false; }
    }

    public static (bool Ok, string? Error) Set(bool enabled)
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(RunKey);
            if (enabled) k.SetValue(ValueName, $"\"{UpdateService.ExePath}\" --tray");
            else k.DeleteValue(ValueName, throwOnMissingValue: false);
            return (true, null);
        }
        catch (Exception ex) { return (false, ex.Message); }
    }

    /// <summary>Keeps the Run command pointing at the current exe after a portable move/update.</summary>
    public static void RefreshIfStale()
    {
        try
        {
            using var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(RunKey);
            if (k?.GetValue(ValueName) is string cmd && !cmd.Contains(UpdateService.ExePath, StringComparison.OrdinalIgnoreCase)) Set(true);
        }
        catch { /* ignore */ }
    }
}
