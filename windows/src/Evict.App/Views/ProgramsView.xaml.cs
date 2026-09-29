using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Evict.App.ViewModels;

namespace Evict.App.Views;

public partial class ProgramsView : UserControl
{
    public ProgramsView()
    {
        InitializeComponent();
    }

    private void OnIconLoaded(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ProgramItemViewModel vm }) vm.EnsureIcon();
    }

    private void OnLoadingRow(object? sender, DataGridRowEventArgs e)
    {
        if (e.Row.Item is ProgramItemViewModel vm) vm.EnsureIcon();
    }

    private void OnRowDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (DataContext is ProgramsViewModel vm && vm.SelectedItem != null && e.OriginalSource is DependencyObject d && FindRow(d) != null)
        {
            vm.ShowDetails = true;
        }
    }

    private static DataGridRow? FindRow(DependencyObject d)
    {
        while (d != null && d is not DataGridRow) d = System.Windows.Media.VisualTreeHelper.GetParent(d);
        return d as DataGridRow;
    }
}
