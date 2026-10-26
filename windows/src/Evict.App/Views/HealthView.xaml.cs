using System.Windows;
using System.Windows.Controls;

namespace Evict.App.Views;

public partial class HealthView : UserControl
{
    /// <summary>Below this width (logical pixels, after the text-size zoom) Recently / Install Monitor go under the score.</summary>
    private const double TwoColumnMinWidth = 860;

    public HealthView()
    {
        InitializeComponent();
    }

    private void OnHomeSizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool twoColumns = HomeGrid.ActualWidth >= TwoColumnMinWidth;
        if (twoColumns == (Grid.GetColumn(SideColumn) == 1)) return;
        SideColumnDef.Width = twoColumns ? new GridLength(330) : new GridLength(0);
        Grid.SetColumn(SideColumn, twoColumns ? 1 : 0);
        Grid.SetRow(SideColumn, twoColumns ? 0 : 1);
        SideColumn.Margin = twoColumns ? new Thickness(20, 0, 0, 0) : new Thickness(0, 18, 0, 0);
    }
}
