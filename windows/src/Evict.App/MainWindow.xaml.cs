using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using Evict.App.Services;
using Evict.Core.Services;

namespace Evict.App;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        var s = App.Services.Settings.Current;
        if (s.WindowWidth >= MinWidth && s.WindowHeight >= MinHeight)
        {
            Width = s.WindowWidth;
            Height = s.WindowHeight;
        }
        if (s.WindowMaximized) WindowState = WindowState.Maximized;
        StateChanged += (_, _) => UpdateMaxRestoreGlyph();
        SourceInitialized += (_, _) => TryRoundCorners();

        // Text size / zoom: scale the body (not the title bar, whose caption height is fixed).
        ApplyScale(App.UiState.Scale);
        App.UiState.PropertyChanged += (_, args) => { if (args.PropertyName == nameof(UiState.Scale)) ApplyScale(App.UiState.Scale); };
        BodyScroll.SizeChanged += (_, _) => FitBody();
        PreviewKeyDown += OnPreviewKeyDown;
        Closing += (_, e) =>
        {
            var cur = App.Services.Settings.Current;
            cur.WindowMaximized = WindowState == WindowState.Maximized;
            if (WindowState == WindowState.Normal) { cur.WindowWidth = Width; cur.WindowHeight = Height; }
            App.Services.Settings.Save();

            if (!App.IsExiting && cur.CloseToTray && App.Background.HasTray)
            {
                e.Cancel = true;            // keep running in the notification area
                Hide();
                App.Background.OnHiddenToTray();
                return;
            }
            App.Quit();                      // ShutdownMode is OnExplicitShutdown
        };
        StateChanged += (_, _) =>
        {
            if (WindowState == WindowState.Minimized && App.Services.Settings.Current.MinimizeToTray && App.Background.HasTray && OwnedWindows.Count == 0) Hide();
        };
        UpdateMaxRestoreGlyph();
    }

    private void UpdateMaxRestoreGlyph()
    {
        MaxRestoreButton.Content = WindowState == WindowState.Maximized ? "" : "";
        MaxRestoreButton.ToolTip = WindowState == WindowState.Maximized ? "Restore" : "Maximize";
    }

    // The body keeps a minimum logical size (sidebar + a usable content column). At large text sizes that is
    // bigger than the window, so BodyScroll shows scrollbars instead of squeezing the pages.
    private const double BodyMinLogicalWidth = 760;   // sidebar 236 + a usable content column
    private const double BodyMinLogicalHeight = 480;

    private void ApplyScale(double s)
    {
        Body.LayoutTransform = Math.Abs(s - 1.0) < 0.001 ? Transform.Identity : new ScaleTransform(s, s);
        FitBody();
    }

    private void FitBody()
    {
        var s = App.UiState.Scale;
        double vw = BodyScroll.ViewportWidth, vh = BodyScroll.ViewportHeight;
        if (vw <= 0 || vh <= 0) { vw = BodyScroll.ActualWidth; vh = BodyScroll.ActualHeight; }
        if (vw <= 0 || vh <= 0) return;
        Body.Width = Math.Max(BodyMinLogicalWidth, Math.Floor(vw / s));
        Body.Height = Math.Max(BodyMinLogicalHeight, Math.Floor(vh / s));
    }

    private void OnBodyScrollChanged(object sender, System.Windows.Controls.ScrollChangedEventArgs e)
    {
        if (e.ViewportWidthChange != 0 || e.ViewportHeightChange != 0) FitBody();
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if ((Keyboard.Modifiers & ModifierKeys.Control) == 0) return;
        switch (e.Key)
        {
            case Key.OemPlus or Key.Add: App.UiState.Step(+1); e.Handled = true; break;
            case Key.OemMinus or Key.Subtract: App.UiState.Step(-1); e.Handled = true; break;
            case Key.D0 or Key.NumPad0: App.UiState.Reset(); e.Handled = true; break;
        }
    }

    private void OnMinimize(object sender, RoutedEventArgs e) => WindowState = WindowState.Minimized;
    private void OnMaximizeRestore(object sender, RoutedEventArgs e) => WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    private void OnClose(object sender, RoutedEventArgs e) => Close();

    // Windows 11 rounded corners for a WindowChrome window (harmless no-op on Windows 10).
    [DllImport("dwmapi.dll", PreserveSig = true)]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attr, ref int attrValue, int attrSize);

    private void TryRoundCorners()
    {
        try
        {
            var hwnd = new WindowInteropHelper(this).Handle;
            int preference = 2; // DWMWCP_ROUND
            DwmSetWindowAttribute(hwnd, 33 /* DWMWA_WINDOW_CORNER_PREFERENCE */, ref preference, sizeof(int));
        }
        catch (Exception ex)
        {
            Log.Warn("Rounded corners unavailable: " + ex.Message);
        }
    }
}
