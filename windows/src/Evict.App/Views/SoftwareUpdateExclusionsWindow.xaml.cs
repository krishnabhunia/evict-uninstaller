using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using Evict.App.ViewModels;

namespace Evict.App.Views;

/// <summary>Hidden updates (design VM1): already up to date, skipped versions and excluded programs, each restorable.</summary>
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
            RestoreSelectedButton.IsEnabled = HiddenList.SelectedItems.Count > 0;
    }

    private void RestoreSelected(object sender, RoutedEventArgs e)
    {
        if (DataContext is SoftwareUpdaterViewModel vm)
            vm.RestoreHidden(HiddenList.SelectedItems.Cast<HiddenUpdateRowViewModel>().ToArray());
    }

    /// <summary>Restores every row the current filter shows.</summary>
    private void RestoreAll(object sender, RoutedEventArgs e)
    {
        if (DataContext is SoftwareUpdaterViewModel vm)
            vm.RestoreHidden(CollectionViewSource.GetDefaultView(vm.HiddenItems).Cast<HiddenUpdateRowViewModel>().ToArray());
    }

    private void CloseWindow(object sender, RoutedEventArgs e) => Close();
}
