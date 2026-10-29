using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;
using System.Windows.Shapes;
using Evict.Core.Util;

namespace Evict.App.Views;

/// <summary>
/// Design SP1 · S1 "brand card": the start-up splash. Built in code with fixed colours and no application resources,
/// because it runs on its own UI thread before (and while) the App loads – it stays smooth while the main thread is busy.
/// </summary>
public sealed class SplashWindow : Window
{
    private static readonly Brush Navy = Frozen("#14243A");
    private static readonly Brush NavyBorder = Frozen("#293C54");
    private static readonly Brush Coral = Frozen("#E66B48");
    private static readonly Brush CoralHover = Frozen("#D45C3A");
    private static readonly Brush Muted = Frozen("#C1D1DC");
    private static readonly Brush Eyebrow = Frozen("#FFB79F");
    private static readonly Brush Green = Frozen("#80E2BB");
    private static readonly Brush White = Brushes.White;

    private readonly TextBlock _status = new() { FontSize = 14, FontWeight = FontWeights.SemiBold, Foreground = White, TextTrimming = TextTrimming.CharacterEllipsis };
    private readonly TextBlock _count = new() { FontSize = 12, Foreground = Muted, VerticalAlignment = VerticalAlignment.Bottom };
    private readonly TextBlock _detail = new() { FontSize = 12, Foreground = Muted, TextWrapping = TextWrapping.Wrap, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 10) };
    private readonly TextBlock _eyebrow = new() { FontSize = 11, FontWeight = FontWeights.Bold, Foreground = Eyebrow, Visibility = Visibility.Collapsed, Margin = new Thickness(0, 0, 0, 6) };
    private readonly Border _track = new() { Height = 6, CornerRadius = new CornerRadius(3), Background = NavyBorder, Margin = new Thickness(0, 10, 0, 10) };
    private readonly Border _fill = new() { Height = 6, CornerRadius = new CornerRadius(3), Background = Coral, HorizontalAlignment = HorizontalAlignment.Left, Width = 0 };
    private readonly Grid _footer = new();
    private readonly StackPanel _buttons = new() { Orientation = Orientation.Horizontal, Visibility = Visibility.Collapsed };
    private readonly DockPanel _stepsRow = new() { LastChildFill = true };
    private double _progress;
    private bool _closing;

    public SplashWindow(string version, bool elevated)
    {
        Title = "Evict";
        Width = 520;
        Height = 320;
        WindowStyle = WindowStyle.None;
        AllowsTransparency = true;
        Background = Brushes.Transparent;
        ResizeMode = ResizeMode.NoResize;
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        FontFamily = new FontFamily("Segoe UI Variable Text, Segoe UI");
        UseLayoutRounding = true;
        TextOptions.SetTextFormattingMode(this, TextFormattingMode.Display);
        // No Icon: the taskbar shows Evict.exe's own icon (pack:// resources are not ready before the App exists).

        var root = new Border
        {
            Background = Navy, BorderBrush = NavyBorder, BorderThickness = new Thickness(1), CornerRadius = new CornerRadius(14),
            Padding = new Thickness(40, 36, 40, 28),
        };
        root.MouseLeftButtonDown += (_, e) => { if (e.ButtonState == MouseButtonState.Pressed) try { DragMove(); } catch { /* released */ } };

        var layout = new DockPanel { LastChildFill = false };

        // Logo tile, name, version
        var header = new StackPanel { Orientation = Orientation.Horizontal };
        var tile = new Border { Width = 52, Height = 52, CornerRadius = new CornerRadius(14), Background = Coral };
        tile.Child = new System.Windows.Shapes.Path
        {
            Data = Geometry.Parse("M3,6 L21,6 M8,6 L8,4 L16,4 L16,6 M19,6 L18,20 L6,20 L5,6 M10,11 L10,17 M14,11 L14,17"),
            Stroke = White, StrokeThickness = 2, StrokeStartLineCap = PenLineCap.Round, StrokeEndLineCap = PenLineCap.Round, StrokeLineJoin = PenLineJoin.Round,
            Width = 28, Height = 28, Stretch = Stretch.Uniform, Margin = new Thickness(12),
        };
        header.Children.Add(tile);
        var names = new StackPanel { Margin = new Thickness(14, 0, 0, 0), VerticalAlignment = VerticalAlignment.Center };
        names.Children.Add(new TextBlock { Text = "Evict", FontSize = 26, FontWeight = FontWeights.Bold, Foreground = White });
        names.Children.Add(new TextBlock { Text = "Uninstaller · v" + version, FontSize = 13, Foreground = Muted, Margin = new Thickness(0, 2, 0, 0) });
        header.Children.Add(names);
        DockPanel.SetDock(header, Dock.Top);
        layout.Children.Add(header);

        // Bottom block: eyebrow, detail, status + count, bar, footer
        var bottom = new StackPanel();
        bottom.Children.Add(_eyebrow);
        bottom.Children.Add(_detail);
        var statusRow = new DockPanel();
        DockPanel.SetDock(_count, Dock.Right);
        statusRow.Children.Add(_count);
        statusRow.Children.Add(_status);
        bottom.Children.Add(statusRow);
        _track.Child = _fill;
        bottom.Children.Add(_track);

        var admin = new StackPanel { Orientation = Orientation.Horizontal, Visibility = elevated ? Visibility.Visible : Visibility.Collapsed };
        admin.Children.Add(new Ellipse { Width = 7, Height = 7, Fill = Green, VerticalAlignment = VerticalAlignment.Center });
        admin.Children.Add(new TextBlock { Text = "Administrator", FontSize = 12, Foreground = Muted, Margin = new Thickness(6, 0, 0, 0) });
        DockPanel.SetDock(admin, Dock.Right);
        _stepsRow.Children.Add(admin);
        _stepsRow.Children.Add(new TextBlock { Text = StartupSplashRules.StepsLine, FontSize = 12, Foreground = Muted, TextTrimming = TextTrimming.CharacterEllipsis });
        _footer.Children.Add(_stepsRow);
        _footer.Children.Add(_buttons);
        bottom.Children.Add(_footer);
        DockPanel.SetDock(bottom, Dock.Bottom);
        layout.Children.Add(bottom);

        root.Child = layout;
        Content = root;
        _track.SizeChanged += (_, _) => _fill.Width = _track.ActualWidth * _progress;
        Show(StartupStep.Starting);
    }

    /// <summary>Normal progress (also clears a slow / error state).</summary>
    public void Show(StartupStep step)
    {
        var (text, _, showProgress) = StartupSplashRules.Describe(step);
        _status.Text = text;
        _count.Text = showProgress ? StartupSplashRules.CountText(step) : "";
        _track.Visibility = showProgress ? Visibility.Visible : Visibility.Hidden;
        _eyebrow.Visibility = Visibility.Collapsed;
        _buttons.Visibility = Visibility.Collapsed;
        _stepsRow.Visibility = Visibility.Visible;
        if (step == StartupStep.WaitingForAdministrator)
            SetDetail("Approve the Windows prompt so Evict can remove machine-wide programs and make restore points – or choose No to continue without.");
        else SetDetail(null);
        if (showProgress) AnimateTo(StartupSplashRules.Progress(step));
    }

    /// <summary>Over 8 s: say why and offer "Open now".</summary>
    public void ShowSlow(Action openNow)
    {
        _eyebrow.Text = "TAKING LONGER THAN USUAL";
        _eyebrow.Visibility = Visibility.Visible;
        _status.Text = "Still reading installed programs…";
        SetDetail("Measuring folder sizes takes longer the first time. You can open Evict now; the list fills in.");
        SetButtons(("Open now", true, openNow));
    }

    /// <summary>Something failed: never a blank window – explain and let the user open Evict or the log.</summary>
    public void ShowError(string message, Action open, Action showLog)
    {
        _eyebrow.Text = "SOMETHING FAILED";
        _eyebrow.Visibility = Visibility.Visible;
        _status.Text = "Evict could not finish starting";
        _count.Text = "";
        _track.Visibility = Visibility.Hidden;
        SetDetail(message);
        SetButtons(("Open Evict", true, open), ("Show log", false, showLog));
    }

    /// <summary>Fades out, then closes (and ends the splash thread).</summary>
    public void FadeOutAndClose()
    {
        if (_closing) return;
        _closing = true;
        var fade = new DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(160));
        fade.Completed += (_, _) => Close();
        BeginAnimation(OpacityProperty, fade);
    }

    private void SetDetail(string? text)
    {
        _detail.Text = text ?? "";
        _detail.Visibility = string.IsNullOrEmpty(text) ? Visibility.Collapsed : Visibility.Visible;
    }

    private void SetButtons(params (string Text, bool Primary, Action Click)[] buttons)
    {
        _buttons.Children.Clear();
        foreach (var (text, primary, click) in buttons)
        {
            var b = new Button
            {
                Content = text, MinHeight = 36, Padding = new Thickness(16, 6, 16, 6), Margin = new Thickness(0, 0, 8, 0), Cursor = Cursors.Hand,
                Foreground = White, Background = primary ? Coral : Brushes.Transparent, BorderBrush = primary ? Coral : Frozen("#3A4E68"),
                FontSize = 13, FontWeight = FontWeights.SemiBold, Template = ButtonTemplate(primary),
            };
            b.Click += (_, _) => click();
            _buttons.Children.Add(b);
        }
        _stepsRow.Visibility = Visibility.Collapsed;
        _buttons.Visibility = Visibility.Visible;
        if (_buttons.Children.Count > 0) ((Button)_buttons.Children[0]).Focus();
    }

    private static ControlTemplate ButtonTemplate(bool primary)
    {
        var border = new FrameworkElementFactory(typeof(Border), "Bd");
        border.SetValue(Border.CornerRadiusProperty, new CornerRadius(8));
        border.SetValue(Border.BorderThicknessProperty, new Thickness(1));
        border.SetBinding(Border.BackgroundProperty, new System.Windows.Data.Binding("Background") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.BorderBrushProperty, new System.Windows.Data.Binding("BorderBrush") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        border.SetBinding(Border.PaddingProperty, new System.Windows.Data.Binding("Padding") { RelativeSource = System.Windows.Data.RelativeSource.TemplatedParent });
        var content = new FrameworkElementFactory(typeof(ContentPresenter));
        content.SetValue(HorizontalAlignmentProperty, HorizontalAlignment.Center);
        content.SetValue(VerticalAlignmentProperty, VerticalAlignment.Center);
        border.AppendChild(content);
        var template = new ControlTemplate(typeof(Button)) { VisualTree = border };
        var hover = new Trigger { Property = IsMouseOverProperty, Value = true };
        hover.Setters.Add(new Setter(Border.BackgroundProperty, primary ? CoralHover : NavyBorder, "Bd"));
        template.Triggers.Add(hover);
        return template;
    }

    private void AnimateTo(double share)
    {
        _progress = share;
        if (_track.ActualWidth <= 0) return;
        _fill.BeginAnimation(WidthProperty, new DoubleAnimation(_track.ActualWidth * share, TimeSpan.FromMilliseconds(220)) { EasingFunction = new QuadraticEase() });
    }

    private static Brush Frozen(string hex)
    {
        var b = new SolidColorBrush((Color)ColorConverter.ConvertFromString(hex));
        b.Freeze();
        return b;
    }
}
