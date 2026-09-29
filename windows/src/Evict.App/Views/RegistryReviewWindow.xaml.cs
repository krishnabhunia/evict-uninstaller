using System.Windows;
using Evict.App.ViewModels;

namespace Evict.App.Views;

public partial class RegistryReviewWindow : Window
{
    public RegistryReviewWindow()
    {
        InitializeComponent();
        App.UiState.ApplyToDialog(this);
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is RegistryReviewViewModel old) old.RequestClose -= Close;
            if (e.NewValue is RegistryReviewViewModel vm) vm.RequestClose += Close;
        };
    }
}
