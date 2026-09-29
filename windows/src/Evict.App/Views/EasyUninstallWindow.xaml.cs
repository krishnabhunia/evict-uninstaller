using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Shapes;
using Evict.App.Services;
using Evict.App.ViewModels;
using Evict.Core.Services;
using Evict.Core.Util;

namespace Evict.App.Views;

/// <summary>
/// Floating always-on-top widget: drag the target onto any window (or drop a file on it) to uninstall
/// the program that owns it. Identifies the program via WindowFromPoint → process image path.
/// </summary>
public partial class EasyUninstallWindow : Window
{
    private readonly MainViewModel _main;
    private bool _dragging;
    private DragCursorWindow? _follower;

    public EasyUninstallWindow(MainViewModel main)
    {
        _main = main;
        InitializeComponent();
        var s = App.Services.Settings.Current;
        var work = SystemParameters.WorkArea;
        if (s.WidgetLeft >= 0 && s.WidgetTop >= 0 && s.WidgetLeft < work.Right - 40 && s.WidgetTop < work.Bottom - 40)
        {
            Left = s.WidgetLeft; Top = s.WidgetTop;
        }
        else
        {
            Left = work.Right - Width - 24; Top = work.Bottom - Height - 24;
        }
        LocationChanged += (_, _) =>
        {
            var cur = App.Services.Settings.Current;
            cur.WidgetLeft = Left; cur.WidgetTop = Top;
        };
        Closed += (_, _) => { App.Services.Settings.Save(); _follower?.Close(); };
    }

    private void OnHeaderMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ButtonState == MouseButtonState.Pressed) DragMove();
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();

    // ───────────────────────────── target drag ─────────────────────────────

    private void OnTargetMouseDown(object sender, MouseButtonEventArgs e)
    {
        _dragging = true;
        Target.CaptureMouse();
        Halo.Opacity = 0.4;
        _follower ??= new DragCursorWindow();
        MoveFollower();
        _follower.Show();
        Hint.Text = "Release over the program's window…";
        e.Handled = true;
    }

    private void OnTargetMouseMove(object sender, MouseEventArgs e)
    {
        if (!_dragging) return;
        MoveFollower();
    }

    private void OnTargetMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (!_dragging) return;
        _dragging = false;
        Target.ReleaseMouseCapture();
        Halo.Opacity = 1;
        Hint.Text = "Drag the target onto a program's window";
        _follower?.Hide();

        try
        {
            if (!GetCursorPos(out var pt)) return;
            var hwnd = WindowFromPoint(pt);
            if (hwnd == IntPtr.Zero) return;
            var root = GetAncestor(hwnd, GA_ROOT);
            if (root == IntPtr.Zero) root = hwnd;
            _ = GetWindowThreadProcessId(root, out uint pid);
            HandleTargetProcess((int)pid, root);
        }
        catch (Exception ex)
        {
            Log.Error("Easy Uninstall target failed", ex);
            Dialogs.Error("Could not identify the program under the cursor: " + ex.Message);
        }
    }

    private void MoveFollower()
    {
        if (_follower is null || !GetCursorPos(out var pt)) return;
        // Convert device pixels → DIPs using this window's DPI transform.
        var source = PresentationSource.FromVisual(this);
        var m = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var dip = m.Transform(new Point(pt.X, pt.Y));
        _follower.Left = dip.X - _follower.Width / 2;
        _follower.Top = dip.Y - _follower.Height / 2;
    }

    private void HandleTargetProcess(int pid, IntPtr hwnd)
    {
        if (pid == Environment.ProcessId)
        {
            Dialogs.Info("That is Evict itself. Drop the target onto the window of the program you want to remove.");
            return;
        }
        var path = ProcessUtil.GetImagePath(pid);
        var cls = GetClassName(hwnd);
        var exe = PathUtil.LeafName(path ?? "");

        if (exe.Equals("explorer.exe", StringComparison.OrdinalIgnoreCase) || cls is "Progman" or "WorkerW" or "Shell_TrayWnd" or "Shell_SecondaryTrayWnd")
        {
            Dialogs.Info("The desktop, taskbar and File Explorer belong to Windows itself.\n\nTo uninstall a program from a desktop icon, drag that shortcut onto the widget instead.");
            return;
        }
        if (exe.Equals("ApplicationFrameHost.exe", StringComparison.OrdinalIgnoreCase) || (path != null && path.Contains(@"\WindowsApps\", StringComparison.OrdinalIgnoreCase)))
        {
            Dialogs.Info("This is a Microsoft Store app. Use the Windows Apps page to remove it.");
            _main.Navigate(PageKey.WindowsApps);
            ActivateMain();
            return;
        }
        if (path is null)
        {
            Dialogs.Info("Windows would not reveal which program owns that window (it may be running with higher privileges). Try 'Restart as administrator', or drop the program's shortcut onto the widget.");
            return;
        }
        var windows = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        if (PathUtil.IsUnder(path, windows))
        {
            Dialogs.Info($"\"{exe}\" is part of Windows and cannot be uninstalled.");
            return;
        }

        ActivateMain();
        _ = _main.UninstallByPathAsync(path);
    }

    private static void ActivateMain()
    {
        var w = Application.Current.MainWindow;
        if (w is null) return;
        if (w.WindowState == WindowState.Minimized) w.WindowState = WindowState.Normal;
        w.Show();
        w.Activate();
    }

    // ───────────────────────────── drop ─────────────────────────────

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Link : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0)
        {
            ActivateMain();
            _ = _main.UninstallByPathAsync(files[0]);
        }
    }

    // ───────────────────────────── Win32 ─────────────────────────────

    [StructLayout(LayoutKind.Sequential)]
    private struct POINT { public int X; public int Y; }

    private const uint GA_ROOT = 2;

    [DllImport("user32.dll")] [return: MarshalAs(UnmanagedType.Bool)] private static extern bool GetCursorPos(out POINT lpPoint);
    [DllImport("user32.dll")] private static extern IntPtr WindowFromPoint(POINT point);
    [DllImport("user32.dll")] private static extern IntPtr GetAncestor(IntPtr hwnd, uint gaFlags);
    [DllImport("user32.dll")] private static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint lpdwProcessId);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] private static extern int GetClassName(IntPtr hWnd, StringBuilder lpClassName, int nMaxCount);

    private static string GetClassName(IntPtr hwnd)
    {
        var sb = new StringBuilder(256);
        return GetClassName(hwnd, sb, sb.Capacity) > 0 ? sb.ToString() : "";
    }

    /// <summary>Small crosshair that follows the mouse while dragging. Click-through so WindowFromPoint ignores it.</summary>
    private sealed class DragCursorWindow : Window
    {
        private const int GWL_EXSTYLE = -20;
        private const int WS_EX_TRANSPARENT = 0x20;
        private const int WS_EX_TOOLWINDOW = 0x80;
        private const int WS_EX_NOACTIVATE = 0x08000000;

        [DllImport("user32.dll", EntryPoint = "GetWindowLongPtrW")] private static extern IntPtr GetWindowLongPtr(IntPtr hWnd, int nIndex);
        [DllImport("user32.dll", EntryPoint = "SetWindowLongPtrW")] private static extern IntPtr SetWindowLongPtr(IntPtr hWnd, int nIndex, IntPtr dwNewLong);

        public DragCursorWindow()
        {
            WindowStyle = WindowStyle.None;
            AllowsTransparency = true;
            Background = Brushes.Transparent;
            ResizeMode = ResizeMode.NoResize;
            ShowInTaskbar = false;
            ShowActivated = false;
            Topmost = true;
            IsHitTestVisible = false;
            Width = 44; Height = 44;
            var accent = Application.Current.TryFindResource("Brush.Accent") as Brush ?? Brushes.RoyalBlue;
            var danger = Application.Current.TryFindResource("Brush.Danger") as Brush ?? Brushes.Red;
            var g = new Grid();
            g.Children.Add(new Ellipse { Stroke = accent, StrokeThickness = 3, Fill = new SolidColorBrush(Color.FromArgb(180, 255, 255, 255)) });
            g.Children.Add(new Ellipse { Width = 22, Height = 22, Stroke = accent, StrokeThickness = 2 });
            g.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = danger });
            g.Children.Add(new Rectangle { Width = 2, Height = 11, Fill = accent, VerticalAlignment = VerticalAlignment.Top });
            g.Children.Add(new Rectangle { Width = 2, Height = 11, Fill = accent, VerticalAlignment = VerticalAlignment.Bottom });
            g.Children.Add(new Rectangle { Width = 11, Height = 2, Fill = accent, HorizontalAlignment = HorizontalAlignment.Left });
            g.Children.Add(new Rectangle { Width = 11, Height = 2, Fill = accent, HorizontalAlignment = HorizontalAlignment.Right });
            Content = g;
            SourceInitialized += (_, _) =>
            {
                var hwnd = new WindowInteropHelper(this).Handle;
                var ex = GetWindowLongPtr(hwnd, GWL_EXSTYLE).ToInt64();
                SetWindowLongPtr(hwnd, GWL_EXSTYLE, new IntPtr(ex | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_NOACTIVATE));
            };
        }
    }
}
