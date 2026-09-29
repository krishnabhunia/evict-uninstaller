using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;
using Evict.Core.Util;

namespace Evict.App.Converters;

/// <summary>bool → Visibility. ConverterParameter "Invert" flips the result. Also accepts int/long/string/null (truthiness).</summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }
    public bool UseHidden { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool b = Truthy(value);
        if (Invert || (parameter is string s && s.Equals("Invert", StringComparison.OrdinalIgnoreCase))) b = !b;
        return b ? Visibility.Visible : (UseHidden ? Visibility.Hidden : Visibility.Collapsed);
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is Visibility v && v == Visibility.Visible ? !Invert : Invert;

    internal static bool Truthy(object? value) => value switch
    {
        null => false,
        bool b => b,
        int i => i != 0,
        long l => l != 0,
        double d => d != 0,
        string s => s.Length > 0,
        System.Collections.ICollection c => c.Count > 0,
        _ => true,
    };
}

/// <summary>Anything → bool by truthiness (non-zero, non-empty, non-null).</summary>
public sealed class TruthyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => BoolToVisibilityConverter.Truthy(value);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => !BoolToVisibilityConverter.Truthy(value);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => !(value is bool b && b);
}

/// <summary>value == parameter (string comparison of ToString()) → bool / Visibility.</summary>
public sealed class EqualsConverter : IValueConverter
{
    public bool ToVisibility { get; set; }
    public bool Invert { get; set; }

    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool eq = string.Equals(value?.ToString(), parameter?.ToString(), StringComparison.OrdinalIgnoreCase);
        if (Invert) eq = !eq;
        if (ToVisibility) return eq ? Visibility.Visible : Visibility.Collapsed;
        return eq;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        // Used by RadioButton-style bindings: when checked, push the parameter back.
        if (value is bool b && b && parameter != null && targetType.IsEnum) return Enum.Parse(targetType, parameter.ToString()!, true);
        return Binding.DoNothing;
    }
}

public sealed class BytesConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        long l => SizeFormatter.Format(l),
        int i => SizeFormatter.Format(i),
        double d => SizeFormatter.Format((long)d),
        _ => parameter as string ?? "—",
    };
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

public sealed class DateConverter : IValueConverter
{
    public string Format { get; set; } = "dd MMM yyyy";
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value switch
    {
        DateTime d when d > DateTime.MinValue => d.ToString(parameter as string ?? Format, culture),
        _ => "—",
    };
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Compensates for the invisible resize border WPF adds when a WindowChrome window is maximized.</summary>
public sealed class MaximizedMarginConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is WindowState.Maximized ? new Thickness(7) : new Thickness(0);
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Picks a brush resource by name from the current theme: ConverterParameter = "Success|Danger" chosen by truthiness.</summary>
public sealed class BoolToBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var parts = (parameter as string ?? "Brush.Success|Brush.Danger").Split('|');
        var key = BoolToVisibilityConverter.Truthy(value) ? parts[0] : (parts.Length > 1 ? parts[1] : parts[0]);
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Health score (0–100) → arc geometry inside a 120×120 box with an 11px stroke (used by the score ring).</summary>
public sealed class ScoreArcConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int score = value is int i ? i : 0;
        if (score <= 0) return Geometry.Empty;
        double fraction = Math.Min(0.9999, score / 100.0);
        const double cx = 60, cy = 60, r = 54.5;
        double angle = fraction * 2 * Math.PI;
        double sx = cx, sy = cy - r;
        double ex = cx + r * Math.Sin(angle), ey = cy - r * Math.Cos(angle);
        int large = fraction > 0.5 ? 1 : 0;
        var inv = CultureInfo.InvariantCulture;
        var data = string.Format(inv, "M {0},{1} A {2},{2} 0 {3} 1 {4},{5}", sx, sy, r, large, ex, ey);
        return Geometry.Parse(data);
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Health score → colour: green ≥ 75, amber ≥ 55, red below.</summary>
public sealed class ScoreBrushConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int score = value is int i ? i : -1;
        var key = score < 0 ? "Brush.Text.Tertiary" : score >= 75 ? "Brush.Success" : score >= 55 ? "Brush.Warning" : "Brush.Danger";
        return Application.Current.TryFindResource(key) as Brush ?? Brushes.Gray;
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>Multiplies a double by the parameter (used for proportional widths).</summary>
public sealed class MultiplyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double v = value is double d ? d : 0;
        double f = parameter is string s && double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out var p) ? p : 1;
        return Math.Max(0, v * f);
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}

/// <summary>
/// Pluralises a count. Two-part parameter "item|items" → "3 items". Three-part parameter
/// "Remove|Remove {0} app|Remove {0} apps" → zero / one / many forms with the count substituted.
/// </summary>
public sealed class CountLabelConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        int n = value is int i ? i : value is long l ? (int)l : 0;
        var parts = (parameter as string ?? "item|items").Split('|');
        if (parts.Length >= 3)
        {
            var template = n == 0 ? parts[0] : n == 1 ? parts[1] : parts[2];
            return template.Replace("{0}", n.ToString("N0", culture));
        }
        return $"{n:N0} {(n == 1 ? parts[0] : parts[^1])}";
    }
    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) => Binding.DoNothing;
}
