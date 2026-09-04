using System.Globalization;

namespace Patterns.Core.Media;

/// <summary>
/// One mode a capture device offers — a size and a rate — and its stored key ("1920x1080@60",
/// "1920x1080@59.94"). Pure: the picker, the show file and the decoder options all speak the key.
/// </summary>
public readonly record struct CaptureFormat(int Width, int Height, double Fps)
{
    public string Key => $"{Width}x{Height}@{Fps.ToString("0.##", CultureInfo.InvariantCulture)}";

    /// <summary>What the operator reads: "1920×1080 @ 60".</summary>
    public string Label => $"{Width}×{Height} @ {Fps.ToString("0.##", CultureInfo.InvariantCulture)}";

    /// <summary>Parses a key; false for anything that is not "WxH@F" with sane numbers.</summary>
    public static bool TryParse(string? key, out CaptureFormat format)
    {
        format = default;
        if (string.IsNullOrWhiteSpace(key)) return false;
        var at = key.IndexOf('@');
        var x = key.IndexOf('x');
        if (at < 0 || x < 0 || x > at) return false;
        if (!int.TryParse(key[..x], NumberStyles.None, CultureInfo.InvariantCulture, out var w) ||
            !int.TryParse(key[(x + 1)..at], NumberStyles.None, CultureInfo.InvariantCulture, out var h) ||
            !double.TryParse(key[(at + 1)..], NumberStyles.Float, CultureInfo.InvariantCulture, out var fps))
        {
            return false;
        }
        if (w < 16 || h < 16 || w > 16384 || h > 16384 || fps <= 0 || fps > 1000) return false;
        format = new CaptureFormat(w, h, fps);
        return true;
    }
}
