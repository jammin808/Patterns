using Patterns.Core.Model;
using SkiaSharp;

namespace Patterns.Core.Rendering;

/// <summary>The blend zone on each edge of one output, in that output's own pixels (0 = no fade).</summary>
public readonly record struct BlendWidths(int Left, int Top, int Right, int Bottom)
{
    public static readonly BlendWidths None = default;

    public bool Any => Left > 0 || Top > 0 || Right > 0 || Bottom > 0;
}

/// <summary>
/// Edge-blend geometry, pure. A projector's picture fades to black across the zone it shares
/// with a neighbour, and the neighbour fades the other way, so the light adds up to one flat
/// picture across the join. The widths are the overlaps in the arrangement — derived here from
/// the arranged rects — or numbers the operator typed.
/// </summary>
public static class EdgeBlend
{
    /// <summary>
    /// The overlap of <paramref name="me"/> with each neighbour, assigned to the edge it covers: a
    /// strip taller than it is wide lies along a side (left when the neighbour reaches further
    /// left, else right), a strip wider than tall along the top or bottom. A square overlap is a
    /// pure corner — the two edges' own zones already cover it as their product — and is skipped.
    /// Two neighbours on one edge: the wider zone wins. A flush edge (no shared area) is 0.
    /// </summary>
    public static BlendWidths Derive(SKRectI me, IEnumerable<SKRectI> others)
    {
        int left = 0, top = 0, right = 0, bottom = 0;
        foreach (var o in others)
        {
            var w = Math.Min(me.Right, o.Right) - Math.Max(me.Left, o.Left);
            var h = Math.Min(me.Bottom, o.Bottom) - Math.Max(me.Top, o.Top);
            if (w <= 0 || h <= 0 || w > me.Width || h > me.Height) continue;
            if (w == me.Width && h == me.Height) continue; // the same place twice: nothing to blend
            if (h > w)
            {
                if (o.Left < me.Left) left = Math.Max(left, w);
                else right = Math.Max(right, w);
            }
            else if (w > h)
            {
                if (o.Top < me.Top) top = Math.Max(top, h);
                else bottom = Math.Max(bottom, h);
            }
        }
        return new BlendWidths(left, top, right, bottom);
    }

    /// <summary>The widths an output actually uses: the derived overlaps when automatic, else the typed ones.</summary>
    public static BlendWidths Resolve(ScreenPlacement placement, in BlendWidths derived)
        => placement.BlendAuto
            ? derived
            : new BlendWidths(placement.BlendLeftPx, placement.BlendTopPx, placement.BlendRightPx, placement.BlendBottomPx);
}
