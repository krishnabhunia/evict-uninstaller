using System.Windows;
using System.Windows.Input;

namespace Evict.App.Views;

/// <summary>The Info window (design IN1). One copy at a time; the main window reopens it on the asked tab.</summary>
public partial class InfoWindow : Window
{
    public InfoWindow()
    {
        InitializeComponent();
        App.UiState.ApplyToDialog(this);
        // F1 inside the window keeps it open (it would otherwise reach the owner and reopen the window).
        PreviewKeyDown += (_, e) => { if (e.Key == Key.F1) e.Handled = true; };
    }

    private void OnClose(object sender, RoutedEventArgs e) => Close();
}
