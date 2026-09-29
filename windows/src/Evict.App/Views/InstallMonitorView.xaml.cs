using System.Windows;
using System.Windows.Controls;
using Evict.App.ViewModels;

namespace Evict.App.Views;

public partial class InstallMonitorView : UserControl
{
    public InstallMonitorView()
    {
        InitializeComponent();
    }

    private void OnDragOver(object sender, DragEventArgs e)
    {
        e.Effects = e.Data.GetDataPresent(DataFormats.FileDrop) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        if (DataContext is not InstallMonitorViewModel vm) return;
        if (e.Data.GetData(DataFormats.FileDrop) is string[] files && files.Length > 0 && File.Exists(files[0]))
            await vm.MonitorAsync(files[0]);
    }
}
