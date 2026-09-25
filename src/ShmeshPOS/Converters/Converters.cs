using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace ShmeshPOS.Converters;

/// <summary>true → Visible. Pass ConverterParameter="Invert" to reverse.</summary>
public class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (parameter is "Invert") flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>non-null → Visible. Pass ConverterParameter="Invert" to reverse.</summary>
public class NullToVisibilityConverter : IValueConverter
{
    public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var visible = value is not null && value is not string { Length: 0 };
        if (parameter is "Invert") visible = !visible;
        return visible ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>
/// Joins the non-empty bound values with " · ", e.g. product code, SKU and barcode on one line.
/// Optional ConverterParameter "Code|SKU|Barcode" prefixes each value with its label.
/// </summary>
public class JoinNonEmptyConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture)
    {
        var labels = (parameter as string)?.Split('|') ?? [];
        return string.Join("  ·  ", values
            .Select((v, i) => (Text: v?.ToString(), Label: i < labels.Length ? labels[i] : null))
            .Where(x => !string.IsNullOrWhiteSpace(x.Text))
            .Select(x => string.IsNullOrEmpty(x.Label) ? x.Text : $"{x.Label} {x.Text}"));
    }

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>True when all bound values are equal (case-insensitive for strings), e.g. highlighting the chosen color swatch.</summary>
public class EqualsConverter : IMultiValueConverter
{
    public object Convert(object?[] values, Type targetType, object? parameter, CultureInfo culture) =>
        values.Length > 1 && values.Skip(1).All(v =>
            v is string s && values[0] is string first ? string.Equals(s, first, StringComparison.OrdinalIgnoreCase) : Equals(v, values[0]));

    public object[] ConvertBack(object? value, Type[] targetTypes, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}

/// <summary>Shows a UTC timestamp from the database in the register's local time.</summary>
public class UtcToLocalConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        value is DateTime utc ? DateTime.SpecifyKind(utc, DateTimeKind.Utc).ToLocalTime() : value;

    public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
        throw new NotSupportedException();
}
