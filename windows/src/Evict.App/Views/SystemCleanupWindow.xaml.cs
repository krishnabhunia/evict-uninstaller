using System.Windows;
using Evict.App.ViewModels;

namespace Evict.App.Views;

public partial class SystemCleanupWindow : Window
{
    public SystemCleanupWindow()
    {
        InitializeComponent();
        App.UiState.ApplyToDialog(this);
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is SystemCleanupViewModel old) old.RequestClose -= Close;
            if (e.NewValue is SystemCleanupViewModel vm) vm.RequestClose += Close;
        };
    }
}
