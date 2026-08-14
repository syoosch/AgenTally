using System.Globalization;
using System.Windows;
using System.Windows.Data;

namespace AgenTally.UI.Infrastructure;

public sealed class NullableNumberConverter : IValueConverter
{
    public object Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture)
    {
        if (value is null || value == DependencyProperty.UnsetValue)
        {
            return "—";
        }

        return value is IFormattable formattable
            ? formattable.ToString("N0", CultureInfo.InvariantCulture)
            : value.ToString() ?? "—";
    }

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) => Binding.DoNothing;
}

public sealed class UtcToLocalDateTimeConverter : IValueConverter
{
    public object Convert(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) => value is DateTimeOffset utc
        ? utc.ToLocalTime().ToString(
            "yyyy-MM-dd HH:mm:ss",
            CultureInfo.CurrentCulture)
        : "—";

    public object ConvertBack(
        object? value,
        Type targetType,
        object? parameter,
        CultureInfo culture) => Binding.DoNothing;
}
