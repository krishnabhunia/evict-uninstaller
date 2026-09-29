using System.Windows;
using Evict.App.ViewModels;

namespace Evict.App.Views;

public partial class RegistryCleanerWindow : Window
{
    public RegistryCleanerWindow()
    {
        InitializeComponent();
        App.UiState.ApplyToDialog(this);
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is RegistryCleanerViewModel old) old.RequestClose -= Close;
            if (e.NewValue is RegistryCleanerViewModel vm) vm.RequestClose += Close;
        };
    }
}
