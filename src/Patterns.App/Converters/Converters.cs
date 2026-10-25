using Patterns.Rendering;
using System.Globalization;
using Avalonia.Data;
using Avalonia.Data.Converters;
using Avalonia.Media;
using Patterns.App.Services;
using Patterns.Core.Services;

namespace Patterns.App.Converters;

/// <summary>
/// The line beside a library row on the Audio page (the after-policy's problem, note or phrase)
/// and, with parameter "summary", the phrase under a stinger chip on the Show panel. Bound to the
/// row's AfterKey so it follows every edit of the kind, the policy or the target; computed against
/// the live show, the same rules the validator applies.
/// </summary>
public sealed class StingerAfterTextConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is not string key || key.Length == 0) return "";
        var state = AppServices.Instance?.State;
        if (state is null) return "";
        var id = key.Split('|')[0];
        var item = state.Stingers.Items.FirstOrDefault(i => i.Id == id);
        if (item is null || !item.IsSting) return "";
        if (string.Equals(parameter as string, "summary", StringComparison.OrdinalIgnoreCase))
        {
            return "→ " + StingerLibrary.AfterSummary(state, item);
        }
        return StingerLibrary.AfterProblem(state, item)
               ?? StingerLibrary.AfterNote(state, item)
               ?? $"Ends: {StingerLibrary.AfterSummary(state, item)}.";
    }

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}

/// <summary>
/// Round 89: a page's address box. What the operator typed goes into the show as the address the show holds — a
/// private one (a Stagewatch window's, one with a pass) kept on this machine and held as <c>kept://…</c>, never its
/// pass; any other address as typed. Bound with <c>UpdateSourceTrigger=LostFocus</c>, so an address is secured once,
/// whole, when the box is left — never a half-typed one a keystroke at a time. The box then shows the held address.
/// </summary>
public sealed class KeptAddressConverter : IValueConverter
{
    /// <summary>
    /// This machine's book, for the converters Avalonia builds from the resources (no services can be handed in); null
    /// without a desk (the designer), when the fence's own form stands in — a pass never reaches the model either way.
    /// </summary>
    internal static WebPasses? Book => AppServices.Instance?.WebPasses;

    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value as string ?? "";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var typed = value as string ?? "";
        return Book?.Secure(typed) ?? WindowAddresses.Held(typed);
    }
}

/// <summary>
/// Round 89: a line of the wire typed into the show (a device's trigger: "WEB OPEN 10.0.0.5:9924/w/… ON lobby") goes in
/// with every private address in it kept on this machine and held as <c>kept://…</c>; every other word as typed. Bound
/// with <c>UpdateSourceTrigger=LostFocus</c>, so the line is secured once, whole, when the box is left.
/// </summary>
public sealed class KeptLineConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture) => value as string ?? "";

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var typed = value as string ?? "";
        return KeptAddressConverter.Book?.SecureText(typed)
               ?? WindowAddresses.RewriteBareText(WindowAddresses.RewriteText(typed, WindowAddresses.Held), WindowAddresses.Held);
    }
}

/// <summary>
/// Bridges NumericUpDown's decimal? to int/double model properties (and back) so compiled
/// bindings stay type-safe everywhere numbers are edited.
/// </summary>
public sealed class NumberConverter : IValueConverter
{
    /// <summary>
    /// To the control: a NumericUpDown wants a decimal?, a Slider a double. The target type
    /// decides — a Slider fed a decimal shows the value but its double never comes back.
    /// </summary>
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        if (value is null) return null;
        var number = AsDouble(value);
        if (number is null) return BindingOperations.DoNothing;
        var t = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (t == typeof(double)) return number.Value;
        if (t == typeof(float)) return (float)number.Value;
        return (decimal)number.Value;
    }

    /// <summary>Back to the model: from a decimal (NumericUpDown) or a double (Slider) to the property's type.</summary>
    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
    {
        var number = AsDouble(value);
        if (number is null)
        {
            return BindingOperations.DoNothing; // empty box — keep the current model value
        }
        var t = Nullable.GetUnderlyingType(targetType) ?? targetType;
        if (t == typeof(int)) return (int)Math.Round(number.Value);
        if (t == typeof(double)) return number.Value;
        if (t == typeof(decimal)) return (decimal)number.Value;
        return BindingOperations.DoNothing;
    }

    private static double? AsDouble(object? value) => value switch
    {
        int i => i,
        long l => l,
        float f => float.IsFinite(f) ? f : null,
        double d => double.IsFinite(d) ? d : null,
        decimal m => (double)m,
        _ => null,
    };
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
            var cut = trimmed.AsSpan().LastIndexOfAny(Separators.Path);
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

/// <summary>true → the sandbox accent frame around the preview; false → no frame.</summary>
public sealed class SandboxBorderConverter : IValueConverter
{
    public object? Convert(object? value, Type targetType, object? parameter, CultureInfo culture)
        => value is true ? new Avalonia.Thickness(2) : new Avalonia.Thickness(0);

    public object? ConvertBack(object? value, Type targetType, object? parameter, CultureInfo culture)
        => BindingOperations.DoNothing;
}
