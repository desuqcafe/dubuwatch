using System.Globalization;
using Avalonia.Data.Converters;

namespace Desuwatch.App.Converters;

/// <summary>
/// Formats a byte count for two-tone display: the numeric portion and the
/// unit are returned separately so the view can style them differently
/// (large tabular number + small muted unit).
///
/// Use <see cref="Number"/> to get "2.47" and <see cref="Unit"/> to get "GB"
/// for a value of 2_651_000_000 bytes.
/// </summary>
public static class BytesSplitConverter
{
    private static readonly string[] Units = { "B", "KB", "MB", "GB", "TB" };

    public static readonly IValueConverter Number = new NumberPart();
    public static readonly IValueConverter Unit = new UnitPart();

    private static (double value, string unit) Split(object? raw, CultureInfo culture)
    {
        if (raw is null) return (0, "B");
        double bytes = System.Convert.ToDouble(raw, culture);
        if (bytes < 0) bytes = 0;

        var unit = 0;
        while (bytes >= 1024 && unit < Units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }

        return (bytes, Units[unit]);
    }

    private sealed class NumberPart : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var (n, u) = Split(value, culture);
            return u == "B" ? n.ToString("0", culture) : n.ToString("0.##", culture);
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    private sealed class UnitPart : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => Split(value, culture).unit;

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}

/// <summary>
/// Same two-part split as <see cref="BytesSplitConverter"/> but formatted as
/// a per-second rate ("1.2" + "MB/s"). Used for the "rate" column in the
/// app list where the header is already labeled as a rate.
/// </summary>
public static class RateFormatConverter
{
    private static readonly string[] Units = { "B/s", "KB/s", "MB/s", "GB/s" };

    public static readonly IValueConverter Number = new NumberPart();
    public static readonly IValueConverter Unit = new UnitPart();
    public static readonly IValueConverter Combined = new CombinedPart();

    private static (double value, string unit) Split(object? raw, CultureInfo culture)
    {
        if (raw is null) return (0, "B/s");
        double bytes = System.Convert.ToDouble(raw, culture);
        if (bytes < 0) bytes = 0;

        var unit = 0;
        while (bytes >= 1024 && unit < Units.Length - 1)
        {
            bytes /= 1024;
            unit++;
        }

        return (bytes, Units[unit]);
    }

    private sealed class NumberPart : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var (n, u) = Split(value, culture);
            return u == "B/s" ? n.ToString("0", culture) : n.ToString("0.#", culture);
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    private sealed class UnitPart : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
            => Split(value, culture).unit;

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }

    private sealed class CombinedPart : IValueConverter
    {
        public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        {
            var (n, u) = Split(value, culture);
            return u == "B/s"
                ? $"{n:0} {u}"
                : $"{n:0.#} {u}";
        }

        public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
            => throw new NotSupportedException();
    }
}