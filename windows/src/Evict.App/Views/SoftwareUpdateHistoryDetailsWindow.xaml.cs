using System.Windows;

namespace Evict.App.Views;

public partial class SoftwareUpdateHistoryDetailsWindow : Window
{
    public SoftwareUpdateHistoryDetailsWindow()
    {
        InitializeComponent();
        App.UiState.ApplyToDialog(this);
    }
    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
}
