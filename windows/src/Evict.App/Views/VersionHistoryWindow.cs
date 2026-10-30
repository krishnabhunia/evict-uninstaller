using System.Windows;
using System.Windows.Controls;
using Evict.Core.Services;

namespace Evict.App.Views;

/// <summary>
/// Explicit version chooser. It never downloads or installs a release merely by opening.
/// Only checksum-backed Windows releases are shown.
/// </summary>
public sealed class VersionHistoryWindow : Window
{
    private readonly UpdateService _updater;
    private readonly ComboBox _direction = new() { Width = 170, Margin = new Thickness(0, 0, 12, 0) };
    private readonly ComboBox _channel = new() { Width = 170 };
    private readonly ListBox _versions = new() { MinHeight = 210, Margin = new Thickness(0, 12, 0, 8) };
    private readonly TextBlock _status = new() { TextWrapping = TextWrapping.Wrap };
    private readonly Button _choose = new() { Content = "Continue with selected version", MinWidth = 180, IsEnabled = false };
    private IReadOnlyList<ReleaseInfo> _available = [];

    public ReleaseInfo? SelectedRelease { get; private set; }
    public bool IsRollback => _direction.SelectedIndex == 1;

    public VersionHistoryWindow(UpdateService updater)
    {
        _updater = updater;
        Title = "Evict · Version history";
        Width = 620;
        Height = 500;
        MinWidth = 530;
        MinHeight = 400;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(22) };
        Content = root;

        var footer = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancel = new Button { Content = "Cancel", MinWidth = 85, Margin = new Thickness(10, 0, 0, 0) };
        cancel.Click += (_, _) => Close();
        _choose.Click += (_, _) =>
        {
            if (_versions.SelectedItem is not VersionOption choice) return;
            SelectedRelease = choice.Release;
            DialogResult = true;
        };
        footer.Children.Add(_choose);
        footer.Children.Add(cancel);
        DockPanel.SetDock(footer, Dock.Bottom);
        root.Children.Add(footer);

        var heading = new StackPanel();
        DockPanel.SetDock(heading, Dock.Top);
        root.Children.Add(heading);
        heading.Children.Add(new TextBlock { Text = "Choose an exact version", FontSize = 20, FontWeight = FontWeights.SemiBold });
        heading.Children.Add(new TextBlock
        {
            Text = $"Installed: Evict {_updater.CurrentVersionLabel}. Updates are manual; you choose the release before any download.",
            Margin = new Thickness(0, 5, 0, 12),
            TextWrapping = TextWrapping.Wrap
        });

        var filters = new StackPanel { Orientation = Orientation.Horizontal };
        _direction.Items.Add("Newer versions");
        _direction.Items.Add("Previous versions");
        _direction.SelectedIndex = 0;
        _channel.Items.Add("Stable and beta");
        _channel.Items.Add("Stable only");
        _channel.Items.Add("Beta only");
        _channel.SelectedIndex = 0;
        _direction.SelectionChanged += (_, _) => Refresh();
        _channel.SelectionChanged += (_, _) => Refresh();
        filters.Children.Add(_direction);
        filters.Children.Add(_channel);
        heading.Children.Add(filters);

        var content = new DockPanel();
        root.Children.Add(content);
        DockPanel.SetDock(_status, Dock.Bottom);
        content.Children.Add(_status);
        _versions.DisplayMemberPath = nameof(VersionOption.Label);
        _versions.SelectionChanged += (_, _) =>
        {
            _choose.IsEnabled = _versions.SelectedItem is VersionOption;
            if (_versions.SelectedItem is VersionOption selected)
                _status.Text = selected.Release.IsPreview
                    ? "Beta builds may contain unfinished changes. The next step requires confirmation."
                    : "The next step requires confirmation; no update has started.";
        };
        content.Children.Add(_versions);
        Loaded += async (_, _) =>
        {
            _status.Text = "Loading releases from GitHub…";
            try
            {
                _available = await _updater.ListVersionsAsync(CancellationToken.None);
                Refresh();
            }
            catch (Exception ex)
            {
                _status.Text = "Could not load releases: " + ex.Message;
            }
        };
    }

    private void Refresh()
    {
        if (_direction.SelectedIndex < 0 || _channel.SelectedIndex < 0) return;
        var older = _direction.SelectedIndex == 1;
        var installedMode = UpdateService.IsInstalledMode();
        var releases = UpdateChecker.SelectVersions(_available, _updater.CurrentVersionLabel, older, includePrereleases: true, installedMode);
        if (_channel.SelectedIndex == 1) releases = releases.Where(r => !r.IsPreview).ToList();
        if (_channel.SelectedIndex == 2) releases = releases.Where(r => r.IsPreview).ToList();
        _versions.ItemsSource = releases.Select(r => new VersionOption(r,
            $"v{r.DisplayVersion}  ·  {(r.IsPreview ? "Beta" : "Stable")}" +
            (r.PublishedAt is { } published ? $"  ·  {published.LocalDateTime:d}" : ""))).ToList();
        _choose.IsEnabled = false;
        _status.Text = releases.Count == 0
            ? "No checksum-verified Windows release matches this filter."
            : older && installedMode
                ? "Installed edition rollback requires a manual installer review; the selected release page will open."
                : $"{releases.Count} verified version(s). Select one; installation requires another confirmation.";
    }

    private sealed record VersionOption(ReleaseInfo Release, string Label);
}
