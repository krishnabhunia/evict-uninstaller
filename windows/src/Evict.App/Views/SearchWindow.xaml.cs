using System.Windows;
using System.Windows.Input;
using Evict.App.ViewModels;

namespace Evict.App.Views;

public partial class SearchWindow : Window
{
    private bool _closing;

    public SearchWindow()
    {
        InitializeComponent();
        Loaded += (_, _) => { QueryBox.Focus(); Keyboard.Focus(QueryBox); };
        Deactivated += (_, _) => SafeClose();   // click outside = close, like the RD1 palette
        Closing += (_, _) => _closing = true;
    }

    private SearchViewModel Vm => (SearchViewModel)DataContext;

    private void OnQueryKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Down: Vm.Move(+1); ResultsList.ScrollIntoView(Vm.Selected); e.Handled = true; break;
            case Key.Up: Vm.Move(-1); ResultsList.ScrollIntoView(Vm.Selected); e.Handled = true; break;
            case Key.Enter: RunSelected(); e.Handled = true; break;
            case Key.Escape: SafeClose(); e.Handled = true; break;
        }
    }

    private void OnResultClick(object sender, MouseButtonEventArgs e) => RunSelected();

    private void RunSelected()
    {
        var item = Vm.Selected;
        if (item is null) return;
        SafeClose();
        // Run after the window has gone so dialogs opened by a tool get the main window as owner.
        Dispatcher.BeginInvoke(item.Run);
    }

    private void SafeClose()
    {
        if (_closing) return;
        _closing = true;
        Close();
    }
}
