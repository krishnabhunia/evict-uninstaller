using System.Windows;
using Evict.App.ViewModels;

namespace Evict.App.Views;

/// <summary>Version picker (design VP1): choose the exact version to update or roll back to.</summary>
public partial class VersionPickerWindow : Window
{
    public VersionPickerWindow()
    {
        InitializeComponent();
        App.UiState.ApplyToDialog(this);
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is VersionPickerViewModel old) old.RequestClose -= Close;
            if (e.NewValue is VersionPickerViewModel vm) vm.RequestClose += Close;
        };
        Closing += (_, e) =>
        {
            // A version change that already started its download is cancelled first, never left half done.
            if (DataContext is VersionPickerViewModel { IsBusy: true } vm) { e.Cancel = true; vm.InstallCancelCommand.Execute(null); }
        };
    }
}
