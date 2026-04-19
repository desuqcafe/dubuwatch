using System.Globalization;
using Avalonia.Data.Converters;

namespace Desuwatch.App.Converters;

/// <summary>
/// Formats a byte count as a human-readable string (B / KB / MB / GB).
/// Uses binary units (1024) for consistency with Windows' own reporting.
/// </summary>
public sealed class BytesHumanizeConverter : IValueConverter
{
    public static readonly BytesHumanizeConverter Instance = new();

    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return "0 B";

        double bytes = System.Convert.ToDouble(value, culture);
        if (bytes < 0) bytes = 0;

        var unit = 0;
        while (bytes >= 1024 && unit < Units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }

        return unit == 0
            ? $"{bytes:0} {Units[unit]}"
            : $"{bytes:0.##} {Units[unit]}";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
