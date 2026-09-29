using System.Windows;
using System.Windows.Media;
using CommunityToolkit.Mvvm.ComponentModel;

namespace Evict.App.Services;

/// <summary>Application-wide UI state that is not a user setting per se: current zoom (text size) factor.</summary>
public sealed partial class UiState : ObservableObject
{
    public const double MinScale = 0.8;
    public const double MaxScale = 3.0;
    /// <summary>Default text size for new installations (and for settings files written before this default existed).</summary>
    public const double DefaultScale = 1.2;

    [ObservableProperty] private double _scale = DefaultScale;

    public static readonly IReadOnlyList<KeyValuePair<double, string>> TextSizeOptions = new[]
    {
        new KeyValuePair<double, string>(0.8, "80 %  – smallest"),
        new KeyValuePair<double, string>(0.9, "90 %"),
        new KeyValuePair<double, string>(1.0, "100 %"),
        new KeyValuePair<double, string>(1.1, "110 %"),
        new KeyValuePair<double, string>(1.2, "120 %  – default"),
        new KeyValuePair<double, string>(1.3, "130 %"),
        new KeyValuePair<double, string>(1.4, "140 %"),
        new KeyValuePair<double, string>(1.5, "150 %"),
        new KeyValuePair<double, string>(1.75, "175 %"),
        new KeyValuePair<double, string>(2.0, "200 %"),
        new KeyValuePair<double, string>(2.5, "250 %"),
        new KeyValuePair<double, string>(3.0, "300 %  – largest"),
    };

    public static double Clamp(double v) => Math.Round(Math.Clamp(double.IsFinite(v) ? v : DefaultScale, MinScale, MaxScale), 2);

    /// <summary>Ctrl + / Ctrl − : 10 % steps up to 150 %, then 25 % steps (so 300 % is reachable in a few presses).</summary>
    public void Step(int direction)
    {
        var cur = Scale;
        double delta = cur >= 1.5 || (direction > 0 && cur >= 1.45) ? 0.25 : 0.1;
        Scale = Clamp(cur + direction * delta);
    }

    public void Reset() => Scale = DefaultScale;

    /// <summary>
    /// Scales a dialog window once at creation: its content gets a LayoutTransform and its size grows to match,
    /// so the window still shows the same amount of UI at every text size.
    /// </summary>
    public void ApplyToDialog(Window window)
    {
        // Not settable from a Style (plain CLR property), so every dialog gets it here.
        window.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var s = Scale;
        if (window.Content is FrameworkElement root) root.LayoutTransform = new ScaleTransform(s, s);
        if (Math.Abs(s - 1.0) < 0.001) return;
        var work = SystemParameters.WorkArea;
        window.MinWidth = Math.Min(window.MinWidth * s, work.Width);
        window.MinHeight = Math.Min(window.MinHeight * s, work.Height);
        window.Width = Math.Min(window.Width * s, work.Width - 40);
        window.Height = Math.Min(window.Height * s, work.Height - 40);
    }
}
