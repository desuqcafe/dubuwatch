using System.Globalization;
using Avalonia.Data.Converters;

namespace Desuwatch.App.Converters;

/// <summary>
/// Returns true when the bound enum value equals the string name supplied
/// as <c>ConverterParameter</c>. Two-way support lets a <c>RadioButton</c>'s
/// <c>IsChecked</c> drive the source enum: when the radio is toggled on,
/// the converter returns the matching enum member back to the binding.
///
/// Usage:
/// <code>
/// &lt;RadioButton IsChecked="{Binding CloseAction,
///     Converter={x:Static conv:EnumEqualsConverter.Instance},
///     ConverterParameter=Ask}" /&gt;
/// </code>
/// </summary>
public sealed class EnumEqualsConverter : IValueConverter
{
	public static readonly EnumEqualsConverter Instance = new();

	public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		if (value is null || parameter is null) return false;
		return string.Equals(value.ToString(), parameter.ToString(), StringComparison.Ordinal);
	}

	public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
	{
		if (value is not true || parameter is null) return Avalonia.Data.BindingOperations.DoNothing;

		try
		{
			var parsed = Enum.Parse(targetType, parameter.ToString()!);
			return parsed;
		}
		catch
		{
			return Avalonia.Data.BindingOperations.DoNothing;
		}
	}
}