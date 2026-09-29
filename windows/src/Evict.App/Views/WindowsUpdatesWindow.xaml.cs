using System.Windows;
using Evict.App.ViewModels;

namespace Evict.App.Views;

public partial class WindowsUpdatesWindow : Window
{
    public WindowsUpdatesWindow()
    {
        InitializeComponent();
        App.UiState.ApplyToDialog(this);
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is WindowsUpdatesViewModel old) old.RequestClose -= Close;
            if (e.NewValue is WindowsUpdatesViewModel vm) vm.RequestClose += Close;
        };
    }
}
