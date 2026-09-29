using System.Windows;
using Evict.App.ViewModels;

namespace Evict.App.Views;

public partial class FileShredderWindow : Window
{
    public FileShredderWindow()
    {
        InitializeComponent();
        App.UiState.ApplyToDialog(this);
        DataContextChanged += (_, e) =>
        {
            if (e.OldValue is FileShredderViewModel old) old.RequestClose -= Close;
            if (e.NewValue is FileShredderViewModel vm) vm.RequestClose += Close;
        };
        Closing += (_, e) =>
        {
            if (DataContext is FileShredderViewModel { IsBusy: true }) e.Cancel = true;
        };
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDrop(object sender, DragEventArgs e)
    {
        if (DataContext is FileShredderViewModel vm && e.Data.GetData(DataFormats.FileDrop) is string[] files)
            vm.AddPaths(files);
    }
}
