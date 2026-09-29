using System.Windows;
using Evict.App.ViewModels;

namespace Evict.App.Views;

public partial class StartupWindow : Window
{
    public StartupWindow()
    {
        InitializeComponent();
        App.UiState.ApplyToDialog(this);
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is StartupViewModel old) old.RequestClose -= Close;
            if (e.NewValue is StartupViewModel vm) vm.RequestClose += Close;
        };
    }
}
