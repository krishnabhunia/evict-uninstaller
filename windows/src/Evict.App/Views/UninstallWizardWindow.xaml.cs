using System.ComponentModel;
using System.Windows;
using Evict.App.ViewModels;

namespace Evict.App.Views;

public partial class UninstallWizardWindow : Window
{
    public UninstallWizardWindow()
    {
        InitializeComponent();
        App.UiState.ApplyToDialog(this);
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is UninstallWizardViewModel old) old.RequestClose -= Close;
            if (e.NewValue is UninstallWizardViewModel vm) vm.RequestClose += Close;
        };
        Closing += OnClosing;
    }

    private void OnClosing(object? sender, CancelEventArgs e)
    {
        // Don't let the user close the window mid-uninstall by accident.
        if (DataContext is UninstallWizardViewModel { Step: WizardStep.Running or WizardStep.Cleaning })
        {
            e.Cancel = true;
            App.Services.Settings.Save();
            MessageBox.Show(this, "Please wait for the current operation to finish (or use 'Cancel remaining').", "Evict", MessageBoxButton.OK, MessageBoxImage.Information);
        }
    }
}
