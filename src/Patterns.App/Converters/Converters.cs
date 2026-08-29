using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Patterns.Core.Services;

namespace Patterns.App.Converters;

/// <summary>
/// Bridges NumericUpDown's decimal? to int/double model properties (and back) so compiled
/// bindings stay type-safe everywhere numbers are edited.
/// </summary>
public sealed class NumberConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch
        {
            int i => (decimal?)i,
            double d => double.IsFinite(d) ? (decimal?)(decimal)d : null,
            decimal m => (decimal?)m,
            null => null,
            _ => BindingOperations.DoNothing,
        };

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not decimal m)
        {
            return BindingOperations.DoNothing; // empty box — keep the current model value
        }
        var t = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (t == typeof(int)) return (int)Math.Round(m);
        if (t == typeof(double)) return (double)m;
        if (t == typeof(decimal)) return m;
        return BindingOperations.DoNothing;
    }
}

/// <summary>Model hex string ("#RRGGBB") ⇄ Avalonia Color for the colour pickers.</summary>
public sealed class HexColorConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is string s && ColorUtil.TryParse(s, out var c))
        {
            return Color.FromArgb(c.Alpha, c.Red, c.Green, c.Blue);
        }
        return Colors.White;
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is Color c)
        {
            return c.A == 255 ? $"#{c.R:X2}{c.G:X2}{c.B:X2}" : $"#{c.A:X2}{c.R:X2}{c.G:X2}{c.B:X2}";
        }
        return BindingOperations.DoNothing;
    }
}

/// <summary>value == parameter (enums in XAML via x:Static). One-way, for panel visibility.</summary>
public sealed class EnumEqualsConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => Equals(value, parameter);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}

/// <summary>true/false → "On|Off"-style labels; parameter "A|B".</summary>
public sealed class BoolToTextConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var parts = (parameter as string ?? "On|Off").Split('|');
        return value is true ? parts[0] : parts.Length > 1 ? parts[1] : "";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
