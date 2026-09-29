using System.ComponentModel;
using System.Windows;
using Evict.App.ViewModels;

namespace Evict.App.Views;

public partial class SelfCleanupWindow : Window
{
    public SelfCleanupWindow()
    {
        InitializeComponent();
        App.UiState.ApplyToDialog(this);
        // Started by the uninstaller with no owner: centre on screen, show in the taskbar, come to the front once.
        WindowStartupLocation = WindowStartupLocation.CenterScreen;
        ShowInTaskbar = true;
        Activated += (_, _) => Topmost = false;
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is SelfCleanupViewModel old) old.RequestClose -= Close;
            if (e.NewValue is SelfCleanupViewModel vm) vm.RequestClose += Close;
        };
    }

    protected override void OnClosing(CancelEventArgs e)
    {
        if (DataContext is SelfCleanupViewModel vm)
        {
            // Closing mid-way would end the process while files are being deleted.
            if (vm.Step == SelfCleanupStep.Working) { e.Cancel = true; return; }
            vm.OnClosing();
        }
        base.OnClosing(e);
    }
}
