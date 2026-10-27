using System.ComponentModel;
using System.Runtime.InteropServices;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows;
using Evict.App.ViewModels;

namespace Evict.App.Views;

public partial class UninstallWizardWindow : Window
{
    public UninstallWizardWindow()
    {
        InitializeComponent();
        App.UiState.ApplyToDialog(this);
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is UninstallWizardViewModel old) old.RequestClose -= Close;
            if (e.NewValue is UninstallWizardViewModel vm) vm.RequestClose += Close;
        };
        Closing += OnClosing;
        SourceInitialized += (_, _) => DockToOwner();
    }

    /// <summary>RD1 U1: full height, against the right edge of the main window (or of the screen when it is maximized).</summary>
    private void DockToOwner()
    {
        try
        {
            WindowStartupLocation = WindowStartupLocation.Manual;
            var owner = Owner;
            Rect area = owner is { IsVisible: true, WindowState: WindowState.Normal }
                ? new Rect(owner.Left, owner.Top, owner.ActualWidth, owner.ActualHeight)
                : WorkAreaOf(owner) ?? SystemParameters.WorkArea;
            var width = Math.Min(Math.Max(MinWidth, Width), Math.Max(MinWidth, area.Width * 0.6));
            Width = width;
            Height = Math.Max(MinHeight, area.Height);
            Left = area.Right - width;
            Top = area.Top;
        }
        catch (Exception ex)
        {
            Core.Services.Log.Warn("Could not dock the uninstall panel: " + ex.Message);
        }
    }

    /// <summary>The work area of the monitor the window is on, in WPF units (a maximized owner may be on a second screen).</summary>
    private static Rect? WorkAreaOf(Window? window)
    {
        if (window is null) return null;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return null;
        var monitor = MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST);
        var info = new MONITORINFO { cbSize = Marshal.SizeOf<MONITORINFO>() };
        if (monitor == IntPtr.Zero || !GetMonitorInfo(monitor, ref info)) return null;
        var source = PresentationSource.FromVisual(window);
        var toDip = source?.CompositionTarget?.TransformFromDevice ?? Matrix.Identity;
        var topLeft = toDip.Transform(new Point(info.rcWork.Left, info.rcWork.Top));
        var bottomRight = toDip.Transform(new Point(info.rcWork.Right, info.rcWork.Bottom));
        return new Rect(topLeft, bottomRight);
    }

    private const uint MONITOR_DEFAULTTONEAREST = 2;

    [StructLayout(LayoutKind.Sequential)]
    private struct RECT { public int Left, Top, Right, Bottom; }

    [StructLayout(LayoutKind.Sequential)]
    private struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }

    [DllImport("user32.dll")]
    private static extern IntPtr MonitorFromWindow(IntPtr hwnd, uint flags);

    [DllImport("user32.dll", SetLastError = true)]
    private static extern bool GetMonitorInfo(IntPtr monitor, ref MONITORINFO info);

    private void OnCloseClick(object sender, RoutedEventArgs e) => Close();

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // Don't let the user close the window mid-uninstall by accident.
        if (DataContext is UninstallWizardViewModel { Step: WizardStep.Running or WizardStep.Cleaning })
        {
            e.Cancel = true;
            App.Services.Settings.Save();
            MessageBox.Show(this, "Please wait for the current operation to finish (or use 'Cancel remaining').", "Evict", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
