using System.Windows;
using Evict.App.ViewModels;

namespace Evict.App.Views;

public partial class UpdateWindow : Window
{
    public UpdateWindow()
    {
        InitializeComponent();
        App.UiState.ApplyToDialog(this);
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is UpdateViewModel old) old.RequestClose -= Close;
            if (e.NewValue is UpdateViewModel vm) vm.RequestClose += Close;
        };
    }
}
