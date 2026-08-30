using Patterns.Core.Model;
using SkiaSharp;

namespace Patterns.Core.Rendering;

/// <summary>
/// 4-corner (perspective) warp maths: builds the homography that maps an output rectangle
/// onto its four displaced corners — a light keystone for casually placed projectors.
/// Pure and unit tested (Heckbert's unit-square-to-quad construction).
/// </summary>
public static class WarpMath
{
    /// <summary>
    /// Matrix mapping the rect (0,0,w,h) onto the quad tl→tr→bl→br. Returns identity-shaped
    /// affine when the quad is the rect itself.
    /// </summary>
    public static SKMatrix QuadWarp(float w, float h, SKPoint tl, SKPoint tr, SKPoint bl, SKPoint br)
    {
        // Unit square → quad (Heckbert, Fundamentals of Texture Mapping, 1989).
        float sx = tl.X - tr.X - bl.X + br.X;
        float sy = tl.Y - tr.Y - bl.Y + br.Y;

        float g = 0, hh = 0;
        if (Math.Abs(sx) > 1e-6f || Math.Abs(sy) > 1e-6f)
        {
            float dx1 = tr.X - br.X, dy1 = tr.Y - br.Y;
            float dx2 = bl.X - br.X, dy2 = bl.Y - br.Y;
            var den = dx1 * dy2 - dx2 * dy1;
            if (Math.Abs(den) > 1e-9f)
            {
                g = (sx * dy2 - dx2 * sy) / den;
                hh = (dx1 * sy - sx * dy1) / den;
            }
        }

        var a = tr.X - tl.X + g * tr.X;
        var b = bl.X - tl.X + hh * bl.X;
        var c = tl.X;
        var d = tr.Y - tl.Y + g * tr.Y;
        var e = bl.Y - tl.Y + hh * bl.Y;
        var f = tl.Y;

        var unitToQuad = new SKMatrix
        {
            ScaleX = a, SkewX = b, TransX = c,
            SkewY = d, ScaleY = e, TransY = f,
            Persp0 = g, Persp1 = hh, Persp2 = 1,
        };

        // Prepend rect → unit square.
        return unitToQuad.PreConcat(SKMatrix.CreateScale(1f / Math.Max(1e-6f, w), 1f / Math.Max(1e-6f, h)));
    }

    /// <summary>The warp for a placement's corner offsets over a w×h output. Identity when unwarped.</summary>
    public static SKMatrix ForPlacement(ScreenPlacement p, float w, float h)
        => QuadWarp(w, h,
            new SKPoint(p.WarpTlx, p.WarpTly),
            new SKPoint(w + p.WarpTrx, p.WarpTry),
            new SKPoint(p.WarpBlx, h + p.WarpBly),
            new SKPoint(w + p.WarpBrx, h + p.WarpBry));
}
