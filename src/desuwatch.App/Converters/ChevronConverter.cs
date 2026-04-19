using System;
using System.Globalization;
using Avalonia.Data.Converters;

namespace Desuwatch.App.Converters;

/// <summary>
/// Maps a boolean IsExpanded flag to a chevron glyph: ▾ when expanded,
/// ▸ when collapsed. Singleton instance for XAML consumption via x:Static.
/// </summary>
public sealed class ChevronConverter : IValueConverter
{
	public static readonly ChevronConverter Instance = new();

	public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		return value is true ? "▾" : "▸";
	}

	public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		throw new NotSupportedException();
	}
}