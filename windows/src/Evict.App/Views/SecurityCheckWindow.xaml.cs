using System.Windows;
using Evict.App.ViewModels;

namespace Evict.App.Views;

public partial class SecurityCheckWindow : Window
{
    public SecurityCheckWindow()
    {
        InitializeComponent();
        App.UiState.ApplyToDialog(this);
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is SecurityCheckViewModel old) old.RequestClose -= Close;
            if (e.NewValue is SecurityCheckViewModel vm) vm.RequestClose += Close;
        };
    }
}
