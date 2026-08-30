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

/// <summary>Full path → file name, for compact playlist rows. Null/blank-safe.</summary>
public sealed class FileNameConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string s || string.IsNullOrWhiteSpace(s)) return "";
        try
        {
            // Split on both separators — Windows paths must shorten on any host.
            var trimmed = s.TrimEnd('/', '\\');
            var cut = trimmed.LastIndexOfAny(new[] { '/', '\\' });
            var name = cut >= 0 ? trimmed[(cut + 1)..] : trimmed;
            return name.Length > 0 ? name : s;
        }
        catch
        {
            return s;
        }
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}

/// <summary>Media path → a compact kind label for playlist rows (VID/AUD/IMG).</summary>
public sealed class MediaKindConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is not string s || string.IsNullOrWhiteSpace(s) ? ""
            : Patterns.Core.Services.PlaylistSequencer.IsVideoPath(s) ? "VID"
            : Patterns.Core.Services.PlaylistSequencer.IsAudioPath(s) ? "AUD"
            : "IMG";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}

/// <summary>Non-empty string → true (chip/panel visibility for optional text).</summary>
public sealed class NotEmptyConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is string s && !string.IsNullOrWhiteSpace(s);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}

/// <summary>value != parameter — the inverse of EnumEq, for "everything but X" panels.</summary>
public sealed class EnumNotEqualsConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => !Equals(value, parameter);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}

/// <summary>Hotkey slot number → key label: 0 → "No key", n → "Fn".</summary>
public sealed class HotkeyLabelConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is int i && i > 0 ? $"F{i}" : "No key";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}

/// <summary>Number → true when &gt; 0 (badge/panel visibility for optional slots).</summary>
public sealed class PositiveConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value switch { int i => i > 0, double d => d > 0, _ => false };

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
