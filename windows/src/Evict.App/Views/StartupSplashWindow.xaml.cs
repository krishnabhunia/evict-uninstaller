using System.ComponentModel;
using System.Diagnostics;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Threading;
using Evict.App.Services;
using Evict.Core.Services;

namespace Evict.App.Views;

public partial class StartupSplashWindow : Window
{
    private readonly DispatcherTimer _timer;
    private readonly Stopwatch _elapsed = Stopwatch.StartNew();
    private readonly List<string> _stages = new();
    private StartupProgress? _progress;
    private bool _finished;

    public StartupSplashWindow()
    {
        InitializeComponent();
        var version = typeof(App).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion.Split('+')[0]
                      ?? typeof(App).Assembly.GetName().Version?.ToString(3) ?? "";
        VersionLabel.Text = "Uninstaller · v" + version;
        _timer = new DispatcherTimer(TimeSpan.FromSeconds(1), DispatcherPriority.Background,
            (_, _) => { if (_elapsed.Elapsed >= TimeSpan.FromSeconds(8)) SlowLabel.Visibility = Visibility.Visible; }, Dispatcher);
        Closed += (_, _) => _timer.Stop();
    }

    public void SetStages(IEnumerable<string> names)
    {
        _stages.Clear();
        _stages.AddRange(names);
        DrawChecklist();
    }

    public void Report(StartupProgress progress)
    {
        _progress = progress;
        StageLabel.Text = progress.IsComplete ? "Opening your workspace…" : progress.Stage;
        StageCount.Text = progress.Summary;
        BusyIndicator.IsIndeterminate = !progress.IsComplete;
        if (progress.IsComplete) { BusyIndicator.Maximum = 1; BusyIndicator.Value = 1; }
        DrawChecklist();
    }

    private void DrawChecklist()
    {
        ChecklistRows.Children.Clear();
        for (var i = 0; i < _stages.Count; i++)
        {
            var completed = i < (_progress?.Completed ?? 0);
            var active = i == (_progress?.Completed ?? 0);
            var label = new TextBlock
            {
                Text = (completed ? "✓  " : active ? "•  " : "○  ") + _stages[i],
                Margin = new Thickness(0, 5, 0, 5), TextWrapping = TextWrapping.Wrap,
                FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal
            };
            label.SetResourceReference(TextBlock.ForegroundProperty, completed || active ? "Brush.Text.Primary" : "Brush.Text.Secondary");
            ChecklistRows.Children.Add(label);
        }
    }

    public void ShowFailure(Exception error)
    {
        _timer.Stop();
        StageLabel.Text = "Evict could not finish starting";
        BusyIndicator.IsIndeterminate = false;
        BusyIndicator.Value = 0;
        SlowLabel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Visible;
        ErrorLabel.Text = error is StartupStageException failure
            ? failure.Message + " Close Evict and try again. Details are in the log."
            : "Startup failed. Close Evict and try again. Details are in the log.";
        LogButton.Visibility = Visibility.Visible;
        CancelButton.Content = "Exit";
        ChecklistPanel.Visibility = Visibility.Visible;
    }

    public void Dismiss()
    {
        _finished = true;
        _timer.Stop();
        Close();
    }

    private void OnStyleChanged(object sender, SelectionChangedEventArgs e)
    {
        // SelectionChanged can fire while InitializeComponent is still assigning named fields.
        if (ChecklistPanel is null || BrandPanel is null) return;
        ChecklistPanel.Visibility = StylePicker.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
        BrandPanel.Visibility = StylePicker.SelectedIndex == 2 ? Visibility.Visible : Visibility.Collapsed;
        if (ErrorPanel?.Visibility == Visibility.Visible) ChecklistPanel.Visibility = Visibility.Visible;
    }

    private void OnShowLog(object sender, RoutedEventArgs e) => Dialogs.OpenFolder(AppPaths.LogFile);
    private void OnCancel(object sender, RoutedEventArgs e) => App.Quit();
    private void OnKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.Escape) { e.Handled = true; App.Quit(); }
    }
    private void OnDrag(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.Primitives.ButtonBase) return;
        if (e.ButtonState == MouseButtonState.Pressed)
            try { DragMove(); } catch (InvalidOperationException) { }
    }
    private void OnClosing(object? sender, CancelEventArgs e)
    {
        if (_finished || App.IsExiting) return;
        e.Cancel = true;
        Dispatcher.BeginInvoke(new Action(App.Quit));
    }
}
