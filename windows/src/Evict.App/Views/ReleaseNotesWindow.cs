using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Evict.Core.Services;

namespace Evict.App.Views;

/// <summary>Read-only, version-specific implementation list; no installation side effects.</summary>
public sealed class ReleaseNotesWindow : Window
{
    public ReleaseNotesWindow(ReleaseInfo release, string installed)
    {
        Title = $"Evict · What's New · v{release.DisplayVersion}";
        Width = 760;
        Height = 620;
        MinWidth = 540;
        MinHeight = 380;
        WindowStartupLocation = WindowStartupLocation.CenterOwner;
        var root = new DockPanel { Margin = new Thickness(24) };
        Content = root;
        var close = new Button { Content = "Close", MinWidth = 95, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0), IsCancel = true };
        close.Click += (_, _) => Close();
        DockPanel.SetDock(close, Dock.Bottom);
        root.Children.Add(close);
        var heading = new StackPanel();
        DockPanel.SetDock(heading, Dock.Top);
        root.Children.Add(heading);
        heading.Children.Add(new TextBlock { Text = $"What's New in Evict v{release.DisplayVersion}", FontSize = 23, FontWeight = FontWeights.Bold });
        heading.Children.Add(new TextBlock { Text = $"Windows · {(release.IsPreview ? "Beta" : "Stable")} · {(release.PublishedAt is { } date ? date.LocalDateTime.ToString("dd MMM yyyy") : "Publication date unavailable")}", Margin = new Thickness(0, 5, 0, 0) });
        heading.Children.Add(new TextBlock { Text = $"Installed: v{installed}   |   Selected: v{release.DisplayVersion}", Margin = new Thickness(0, 5, 0, 14) });
        var stack = new StackPanel();
        if (string.IsNullOrWhiteSpace(release.Body))
        {
            stack.Children.Add(new TextBlock { Text = "The publisher supplied no implementation details for this version.", TextWrapping = TextWrapping.Wrap });
        }
        else
        {
            foreach (var line in release.Body.Replace("\r", "").Split('\n'))
            {
                var raw = line.Trim();
                if (raw.Length == 0 || raw.StartsWith("<!--")) continue;
                var isHeading = raw.StartsWith('#');
                var content = raw.TrimStart('#', ' ', '-', '*').Replace("**", "");
                var block = new TextBlock { Text = (raw.StartsWith("- ") ? "• " : "") + content, TextWrapping = TextWrapping.Wrap,
                    Margin = new Thickness(isHeading ? 0 : 12, isHeading ? 14 : 5, 0, 3),
                    FontSize = isHeading ? 17 : 13.5, FontWeight = isHeading ? FontWeights.SemiBold : FontWeights.Normal };
                stack.Children.Add(block);
            }
        }
        root.Children.Add(new ScrollViewer { Content = stack, VerticalScrollBarVisibility = ScrollBarVisibility.Auto });
    }
}
