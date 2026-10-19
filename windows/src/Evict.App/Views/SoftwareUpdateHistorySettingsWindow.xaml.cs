using System.Windows;

namespace Evict.App.Views;

public partial class SoftwareUpdateHistorySettingsWindow : Window
{
    public SoftwareUpdateHistorySettingsWindow()
    {
        InitializeComponent();
        App.UiState.ApplyToDialog(this);
    }
    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
}
