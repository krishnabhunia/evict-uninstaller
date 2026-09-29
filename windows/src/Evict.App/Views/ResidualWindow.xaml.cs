using System.Windows;
using Evict.App.ViewModels;

namespace Evict.App.Views;

public partial class ResidualWindow : Window
{
    public ResidualWindow()
    {
        InitializeComponent();
        App.UiState.ApplyToDialog(this);
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is ResidualViewModel old) old.RequestClose -= Close;
            if (e.NewValue is ResidualViewModel vm) vm.RequestClose += Close;
        };
        Closing += (_, e) =>
        {
            if (DataContext is ResidualViewModel { Step: ResidualStep.Cleaning }) e.Cancel = true;
        };
    }
}
