using Patterns.Core.Model;
using SkiaSharp;

namespace Patterns.Core.Rendering;

/// <summary>The blend zone on each edge of one output, in that output's own pixels (0 = no fade).</summary>
public readonly record struct BlendWidths(int Left, int Top, int Right, int Bottom)
{
    public static readonly BlendWidths None = default;

    public bool Any => Left > 0 || Top > 0 || Right > 0 || Bottom > 0;

    /// <summary>The zones leave a full picture between them: the two sides' zones do not meet, nor the top's and the bottom's.</summary>
    public bool FitsIn(int width, int height) => Left + Right < width && Top + Bottom < height;

    /// <summary>The zone on a named edge — "left", "top", "right", "bottom".</summary>
    public int On(string edge) => edge switch
    {
        "left" => Left,
        "top" => Top,
        "right" => Right,
        _ => Bottom,
    };
}

/// <summary>
/// Edge-blend geometry, pure. A projector's picture fades to black across the zone it shares
/// with a neighbour, and the neighbour fades the other way, so the light adds up to one flat
/// picture across the join. The widths are the overlaps in the arrangement — derived here from
/// the arranged rects — or numbers the operator typed. Every projector is its own case: a row of
/// three gives the middle one a zone on each side, a grid gives each a side and a top or bottom,
/// and where two zones meet in a corner the fades multiply — which is what four projectors
/// sharing a corner need, since (left + right) × (top + bottom) is one.
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
            var edge = EdgeOf(me, o, out var zone);
            switch (edge)
            {
                case "left": left = Math.Max(left, zone); break;
                case "right": right = Math.Max(right, zone); break;
                case "top": top = Math.Max(top, zone); break;
                case "bottom": bottom = Math.Max(bottom, zone); break;
            }
        }
        return new BlendWidths(left, top, right, bottom);
    }

    /// <summary>
    /// The edge of <paramref name="me"/> a neighbour's overlap lies along — "left", "top",
    /// "right", "bottom" — with the zone's width, or "" when the two share no edge zone: no
    /// overlap, the same place twice, or a pure corner (a square overlap, which the two edges'
    /// own zones already cover).
    /// </summary>
    public static string EdgeOf(SKRectI me, SKRectI o, out int zone)
    {
        zone = 0;
        var w = Math.Min(me.Right, o.Right) - Math.Max(me.Left, o.Left);
        var h = Math.Min(me.Bottom, o.Bottom) - Math.Max(me.Top, o.Top);
        if (w <= 0 || h <= 0 || w > me.Width || h > me.Height) return "";
        if (w == me.Width && h == me.Height) return ""; // the same place twice: nothing to blend
        if (h > w)
        {
            zone = w;
            return o.Left < me.Left ? "left" : "right";
        }
        if (w > h)
        {
            zone = h;
            return o.Top < me.Top ? "top" : "bottom";
        }
        return "";
    }

    /// <summary>The edge a neighbour fades to meet mine: my left is its right, my top its bottom.</summary>
    public static string Facing(string edge) => edge switch
    {
        "left" => "right",
        "right" => "left",
        "top" => "bottom",
        "bottom" => "top",
        _ => "",
    };

    /// <summary>The widths an output actually uses: the derived overlaps when automatic, else the typed ones.</summary>
    public static BlendWidths Resolve(ScreenPlacement placement, in BlendWidths derived)
        => placement.BlendAuto
            ? derived
            : new BlendWidths(placement.BlendLeftPx, placement.BlendTopPx, placement.BlendRightPx, placement.BlendBottomPx);
}

/// <summary>A word about one screen's blend: a join that is right, or what is wrong with it.</summary>
public readonly record struct BlendNote(bool Warning, string Text);

/// <summary>
/// Audits the blend of one screen against its neighbours — pure, from the arrangement and the
/// placements: each join it has, whether both sides fade it by the same width with the same
/// curve, and whether its own zones leave a picture between them. What the Screens page reads
/// under Edge blend, so a rig of three, four or a grid is checked join by join before doors.
/// </summary>
public static class BlendAudit
{
    public static IReadOnlyList<BlendNote> For(
        string id,
        IReadOnlyList<ArrangedScreen> arranged,
        Func<string, ScreenPlacement?> placementOf,
        Func<string, string> nameOf)
    {
        var notes = new List<BlendNote>();
        var meIndex = -1;
        for (var i = 0; i < arranged.Count; i++)
        {
            if (arranged[i].Id == id) meIndex = i;
        }
        if (meIndex < 0 || placementOf(id) is not { } mine) return notes;
        var me = arranged[meIndex];
        var others = arranged.Where(a => a.Id != id).ToList();
        var used = EdgeBlend.Resolve(mine, EdgeBlend.Derive(me.Rect, others.Select(a => a.Rect)));

        if (!used.FitsIn(me.Rect.Width, me.Rect.Height))
        {
            var pair = used.Left + used.Right >= me.Rect.Width
                ? $"left and right zones ({used.Left} + {used.Right} px) meet on a {me.Rect.Width} px wide projector"
                : $"top and bottom zones ({used.Top} + {used.Bottom} px) meet on a {me.Rect.Height} px tall projector";
            notes.Add(new BlendNote(true, $"This screen's {pair} — the overlaps are too wide to leave a picture between them."));
        }

        foreach (var other in others)
        {
            var edge = EdgeBlend.EdgeOf(me.Rect, other.Rect, out var overlap);
            if (edge.Length == 0) continue;
            var theirs = placementOf(other.Id);
            var name = nameOf(other.Id);
            var myZone = used.On(edge);
            var facing = EdgeBlend.Facing(edge);
            var theirZone = theirs is null
                ? 0
                : EdgeBlend.Resolve(theirs, EdgeBlend.Derive(other.Rect, arranged.Where(a => a.Id != other.Id).Select(a => a.Rect))).On(facing);

            if (myZone == 0 && theirZone == 0)
            {
                notes.Add(new BlendNote(true, $"{name} overlaps this screen on the {edge} by {overlap} px and neither fades the join — it will show twice as bright: tick Automatic on both."));
                continue;
            }
            if (myZone == 0)
            {
                notes.Add(new BlendNote(true, $"{name} overlaps this screen on the {edge} by {overlap} px and fades its side, but this screen does not fade its {edge} edge — tick Automatic, or type {overlap} in {Cap(edge)} px."));
                continue;
            }
            if (theirZone == 0)
            {
                notes.Add(new BlendNote(true, $"This screen fades its {edge} edge {myZone} px into {name}, but {name} does not fade its {facing} edge — the join will be bright: tick Automatic on {name}."));
                continue;
            }
            if (myZone != theirZone)
            {
                notes.Add(new BlendNote(true, $"This screen fades {myZone} px and {name} {theirZone} px across the same {edge} join — the light will not add up flat: use the overlap, {overlap} px, on both."));
                continue;
            }
            if (theirs is not null && theirs.BlendCurve != mine.BlendCurve)
            {
                notes.Add(new BlendNote(true, $"{Cap(edge)} join with {name}: {myZone} px, but this screen's curve is {mine.BlendCurve} and {name}'s is {theirs.BlendCurve} — use the same curve on both."));
                continue;
            }
            notes.Add(new BlendNote(false, $"{Cap(edge)} join with {name}: {myZone} px, both fade, {mine.BlendCurve}."));
        }
        return notes;
    }

    /// <summary>The notes as one line each, warnings first.</summary>
    public static string Summary(IReadOnlyList<BlendNote> notes)
        => string.Join("\n", notes.OrderByDescending(n => n.Warning).Select(n => (n.Warning ? "⚠ " : "✓ ") + n.Text));

    private static string Cap(string edge) => edge.Length == 0 ? edge : char.ToUpperInvariant(edge[0]) + edge[1..];
}
