using System.Globalization;
using Avalonia;
using Avalonia.Data.Converters;
using Avalonia.Media;

namespace DeployNest.Linux.Controls;

public static class Converters
{
    /// <summary>Resource key ("Icon.Home") → icon geometry.</summary>
    public static readonly IValueConverter IconKey = new FuncValueConverter<string?, Geometry?>(key =>
        key is not null && Application.Current?.TryGetResource(key, null, out var value) == true ? value as Geometry : null);

    /// <summary>True when the enum value's name equals the converter parameter (used for style classes).</summary>
    public static readonly IValueConverter EnumIs = new EnumIsConverter();

    private sealed class EnumIsConverter : IValueConverter
    {
        public object Convert(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            value is not null && string.Equals(value.ToString(), parameter as string, StringComparison.Ordinal);

        public object ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture) =>
            throw new NotSupportedException();
    }
}
