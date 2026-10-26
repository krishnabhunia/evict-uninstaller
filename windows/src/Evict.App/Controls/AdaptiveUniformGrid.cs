using System.Windows;
using System.Windows.Controls.Primitives;

namespace Evict.App.Controls;

/// <summary>
/// A UniformGrid that picks its column count from the width it gets (RD1 Home cards, Clean tiles), so cards never
/// shrink below <see cref="MinColumnWidth"/> in a small window or at a large text size.
/// </summary>
public sealed class AdaptiveUniformGrid : UniformGrid
{
    public double MinColumnWidth { get; set; } = 220;
    public int MaxColumns { get; set; } = 3;

    protected override Size MeasureOverride(Size constraint)
    {
        if (!double.IsInfinity(constraint.Width) && constraint.Width > 0)
        {
            int columns = Math.Clamp((int)(constraint.Width / Math.Max(1, MinColumnWidth)), 1, Math.Max(1, MaxColumns));
            if (Columns != columns) Columns = columns;
        }
        return base.MeasureOverride(constraint);
    }
}
