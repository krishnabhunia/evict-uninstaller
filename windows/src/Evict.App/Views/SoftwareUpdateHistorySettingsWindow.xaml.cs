using System.Windows;

namespace Evict.App.Views;

public partial class SoftwareUpdateHistorySettingsWindow : Window
{
    public SoftwareUpdateHistorySettingsWindow() => InitializeComponent();
    private void Done(object sender, RoutedEventArgs e) => Close();
}
