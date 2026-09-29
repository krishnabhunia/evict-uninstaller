using System.Windows;
using Evict.App.ViewModels;

namespace Evict.App.Views;

public partial class ForceUninstallWindow : Window
{
    public ForceUninstallWindow()
    {
        InitializeComponent();
        App.UiState.ApplyToDialog(this);
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ForceUninstallViewModel old) old.RequestClose -= Close;
            if (e.NewValue is ForceUninstallViewModel vm) vm.RequestClose += Close;
        };
        Closing += (_, e) =>
        {
            if (DataContext is ForceUninstallViewModel { Step: ForceStep.Cleaning }) e.Cancel = true;
        };
    }
}
