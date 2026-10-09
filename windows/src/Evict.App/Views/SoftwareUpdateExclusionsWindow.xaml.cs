using System.Windows;
using System.Windows.Controls;
using Evict.App.ViewModels;
using Evict.Core.Services;

namespace Evict.App.Views;

public partial class SoftwareUpdateExclusionsWindow : Window
{
    public SoftwareUpdateExclusionsWindow()
    {
        InitializeComponent();
        App.UiState.ApplyToDialog(this);
    }

    private void SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (RestoreSelectedButton != null)
            RestoreSelectedButton.IsEnabled = ExcludedList.SelectedItems.Count > 0;
    }

    private void RestoreSelected(object sender, RoutedEventArgs e)
    {
        if (DataContext is SoftwareUpdaterViewModel vm)
            vm.RestoreExclusions(ExcludedList.SelectedItems.Cast<SoftwareUpdateExclusion>().ToArray());
    }

    private void RestoreAll(object sender, RoutedEventArgs e)
    {
        if (DataContext is SoftwareUpdaterViewModel vm) vm.RestoreExclusions(vm.Exclusions.ToArray());
    }

    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
}
