using Patterns.Core.Media;
using SkiaSharp;

namespace Patterns.Core.Rendering;

/// <summary>
/// The desk's pointer on a web page, drawn wherever the page is shown: the room sees where the
/// operator points, and a ripple for a moment after each click. Pure — the source says where the
/// pointer is, this draws it in the picture's box through its crop; a page nobody is over draws
/// nothing.
/// </summary>
public static class WebPointer
{
    public static readonly TimeSpan RippleLength = TimeSpan.FromMilliseconds(450);

    /// <summary>The arrow's outline, on a 16-unit grid from its tip.</summary>
    private static readonly (float X, float Y)[] Arrow =
    {
        (0, 0), (0, 15), (4, 11.5f), (6.5f, 17), (9, 16), (6.5f, 10.5f), (11, 10.5f),
    };

    /// <summary>The arrow's height for a picture of this size — readable on a wall, discreet on a pane.</summary>
    public static float SizeFor(SKRect dest) => Math.Max(12f, Math.Min(dest.Width, dest.Height) * 0.032f);

    /// <summary>Draws the pointer, and a click ripple while one is fresh; false when the source has no pointer over the page.</summary>
    public static bool Draw(SKCanvas c, SKRect dest, in FrameCrop crop, IWebSource source, DateTime utcNow, PaintCache pc)
    {
        if (source.PointerNorm is not { } norm) return false;
        if (WebPointerMap.ToRect(dest, in crop, norm) is not { } at) return false;
        var size = SizeFor(dest);

        if (source.LastClickUtc is { } clickUtc)
        {
            var age = (utcNow - clickUtc).TotalMilliseconds;
            if (age >= 0 && age < RippleLength.TotalMilliseconds)
            {
                var k = (float)(age / RippleLength.TotalMilliseconds);
                var radius = size * (0.5f + 2.2f * k);
                var alpha = (byte)(220 * (1 - k));
                c.DrawCircle(at, radius, pc.StrokeAA(new SKColor(255, 255, 255, alpha), Math.Max(1.5f, size * 0.14f)));
                c.DrawCircle(at, radius, pc.StrokeAA(new SKColor(0, 0, 0, (byte)(alpha / 3)), Math.Max(3f, size * 0.24f)));
                c.DrawCircle(at, radius, pc.StrokeAA(new SKColor(255, 255, 255, alpha), Math.Max(1.5f, size * 0.14f)));
            }
        }

        var scale = size / 16f;
        using var path = new SKPath();
        path.MoveTo(at.X + Arrow[0].X * scale, at.Y + Arrow[0].Y * scale);
        for (var i = 1; i < Arrow.Length; i++) path.LineTo(at.X + Arrow[i].X * scale, at.Y + Arrow[i].Y * scale);
        path.Close();
        c.DrawPath(path, pc.StrokeAA(SKColors.Black, Math.Max(2f, size * 0.2f)));
        c.DrawPath(path, pc.FillAA(SKColors.White));
        return true;
    }
}
