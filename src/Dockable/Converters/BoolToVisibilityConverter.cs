using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace Dockable.Converters;

/// <summary>
/// Converts a value to <see cref="Visibility"/>. A null or <c>false</c> value is
/// "not present"; anything else is "present". Pass ConverterParameter="Invert"
/// to flip the result (e.g. show a fallback only when an icon is absent).
/// </summary>
public sealed class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        bool present = value switch
        {
            null => false,
            bool b => b,
            _ => true,
        };

        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase))
            present = !present;

        return present ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Visible only when every bound bool is true; otherwise Collapsed.</summary>
public sealed class AllTrueToVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        foreach (var v in values)
            if (v is not true)
                return Visibility.Collapsed;
        return Visibility.Visible;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>Multiplies a double by the (double) ConverterParameter. Used to center a content-sized
/// hover label: offset by half the icon width, then back by half the label's own width.</summary>
public sealed class MultiplyConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        double v = value is double d ? d : 0;
        double factor = parameter is double p
            ? p
            : double.TryParse(parameter as string, NumberStyles.Float, CultureInfo.InvariantCulture, out double f) ? f : 1;
        return v * factor;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

/// <summary>
/// Visible only for icon-bearing items whose icon has not loaded yet.
/// Bindings (in order): ShowIconArea (bool), Icon (object?).
/// </summary>
public sealed class FallbackVisibilityConverter : IMultiValueConverter
{
    public object Convert(object[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        bool showIconArea = values.Length > 0 && values[0] is true;
        bool hasIcon = values.Length > 1 && values[1] is not null;
        return (showIconArea && !hasIcon) ? Visibility.Visible : Visibility.Collapsed;
    }

    public object[] ConvertBack(object value, Type[] targetTypes, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
