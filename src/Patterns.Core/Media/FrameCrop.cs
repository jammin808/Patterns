using System.Globalization;
using Patterns.Core.Model;

namespace Patterns.Core.Media;

/// <summary>
/// How much of a frame to cut away on each side, as a share of the frame: the area of interest
/// of an input. Each side may take up to 90 %, and at least a twentieth of every axis always
/// survives (the left and the top win when two sides overlap). Pure: the fractions a source
/// draws with (the render side turns them into its rectangle), the shape the picture takes, and the composition of a box picked on the picture
/// as it shows are all here, and all unit tested.
/// </summary>
public readonly record struct FrameCrop(double LeftPct, double TopPct, double RightPct, double BottomPct)
{
    public static readonly FrameCrop None = default;

    /// <summary>The most one side may cut away.</summary>
    public const double MaxPct = 90;

    /// <summary>The least of an axis that survives whatever the sides say.</summary>
    public const double MinKeptPct = 5;

    public bool Any => LeftPct > 0 || TopPct > 0 || RightPct > 0 || BottomPct > 0;

    public static FrameCrop From(PipOverlay pip)
        => new(pip.CropLeftPct, pip.CropTopPct, pip.CropRightPct, pip.CropBottomPct);

    /// <summary>The four sides as fractions of the frame, clamped so at least a twentieth of each axis survives.</summary>
    public (double L, double T, double R, double B) Fractions()
    {
        var l = Math.Clamp(LeftPct, 0, MaxPct) / 100.0;
        var r = Math.Clamp(RightPct, 0, MaxPct) / 100.0;
        var t = Math.Clamp(TopPct, 0, MaxPct) / 100.0;
        var b = Math.Clamp(BottomPct, 0, MaxPct) / 100.0;
        const double keep = MinKeptPct / 100.0;
        if (l + r > 1 - keep) r = Math.Max(0, 1 - keep - l);
        if (t + b > 1 - keep) b = Math.Max(0, 1 - keep - t);
        return (l, t, r, b);
    }

    /// <summary>
    /// A box drawn on the picture as it shows now — its sides as shares (0–1) of the visible
    /// part — becomes the new crop: the sides compose with this one, so a second pick refines
    /// the first instead of starting over. A box drawn backwards is put the right way round.
    /// </summary>
    public FrameCrop Within(double left01, double top01, double right01, double bottom01)
    {
        var (l, t, r, b) = Fractions();
        var w = 1 - l - r;
        var h = 1 - t - b;
        var x0 = Math.Clamp(Math.Min(left01, right01), 0, 1);
        var x1 = Math.Clamp(Math.Max(left01, right01), 0, 1);
        var y0 = Math.Clamp(Math.Min(top01, bottom01), 0, 1);
        var y1 = Math.Clamp(Math.Max(top01, bottom01), 0, 1);
        return new FrameCrop(
            (l + w * x0) * 100,
            (t + h * y0) * 100,
            (r + w * (1 - x1)) * 100,
            (b + h * (1 - y1)) * 100);
    }

    /// <summary>"Keeps 60% × 70% of the picture (from 10% in, 20% down)." — the desk's words for a crop.</summary>
    public string Summary()
    {
        if (!Any) return "The whole picture.";
        var (l, t, r, b) = Fractions();
        var c = CultureInfo.InvariantCulture;
        var w = ((1 - l - r) * 100).ToString("0", c);
        var h = ((1 - t - b) * 100).ToString("0", c);
        var from = l > 0 || t > 0
            ? $" (from {(l * 100).ToString("0", c)}% in, {(t * 100).ToString("0", c)}% down)"
            : "";
        return $"Keeps {w}% × {h}% of the picture{from}.";
    }
}
