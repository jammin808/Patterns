using Patterns.Core.Geometry;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.Rendering;

/// <summary>
/// The edge between the core's geometry and the canvas: a pixel rectangle, size, point or
/// colour becomes its Skia twin here and nowhere else, by a call that copies four numbers.
/// The rules stay in the core, argued in its own types; the paint stays on this side.
/// </summary>
public static class SkiaGeometry
{
    public static SKSizeI ToSk(this RasterSize s) => new(s.Width, s.Height);

    public static SKPointI ToSk(this RasterPoint p) => new(p.X, p.Y);

    public static SKRectI ToSk(this RasterRect r) => new(r.Left, r.Top, r.Right, r.Bottom);

    /// <summary>The same rectangle in the canvas's floating type — a clip, a draw.</summary>
    public static SKRect ToSkRect(this RasterRect r) => new(r.Left, r.Top, r.Right, r.Bottom);

    public static SKColor ToSk(this Rgba c) => new(c.R, c.G, c.B, c.A);

    public static RasterSize ToRaster(this SKSizeI s) => new(s.Width, s.Height);

    public static RasterPoint ToRaster(this SKPointI p) => new(p.X, p.Y);

    public static RasterRect ToRaster(this SKRectI r) => new(r.Left, r.Top, r.Right, r.Bottom);

    public static Rgba ToRgba(this SKColor c) => new(c.Red, c.Green, c.Blue, c.Alpha);

    /// <summary>A colour of the show for the canvas, from the snapshot's cache: parsed once, converted per call, never allocated.</summary>
    public static SKColor Color(this ShowSnapshot snap, string? hex, SKColor fallback) => snap.Colour(hex, fallback.ToRgba()).ToSk();
}

/// <summary>The show's hex words as canvas colours: the core's <see cref="Rgba"/> parse, converted at the edge.</summary>
public static class ColorUtil
{
    public static bool TryParse(string? hex, out SKColor color)
    {
        var ok = Rgba.TryParse(hex, out var c);
        color = c.ToSk();
        return ok;
    }

    public static SKColor Parse(string? hex, SKColor fallback) => Rgba.Parse(hex, fallback.ToRgba()).ToSk();

    /// <summary>Splits a comma/space separated hex list; guarantees at least one colour.</summary>
    public static SKColor[] ParseList(string? csv, SKColor fallback)
    {
        var list = Rgba.ParseList(csv, fallback.ToRgba());
        var colors = new SKColor[list.Length];
        for (var i = 0; i < list.Length; i++) colors[i] = list[i].ToSk();
        return colors;
    }
}
