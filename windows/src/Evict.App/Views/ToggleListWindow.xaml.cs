using System.Windows;
using Evict.App.ViewModels;

namespace Evict.App.Views;

public partial class ToggleListWindow : Window
{
    public ToggleListWindow()
    {
        InitializeComponent();
        App.UiState.ApplyToDialog(this);
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ToggleListViewModel old) old.RequestClose -= Close;
            if (e.NewValue is ToggleListViewModel vm) vm.RequestClose += Close;
        };
    }
}
