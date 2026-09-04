using SkiaSharp;

namespace Patterns.Core.Rendering;

/// <summary>
/// A screen in arrangement space: stable id + arranged rect in device pixels. <paramref name="Blend"/>
/// marks a projector whose edges blend automatically: it may overlap its neighbours, and the
/// overlap joins them into one canvas instead of being a mistake.
/// </summary>
public readonly record struct ArrangedScreen(string Id, SKRectI Rect, bool Blend = false);

/// <summary>
/// Pure math for the graphical screen arrangement: edge snapping while dragging,
/// touch detection, and grouping touching screens into spanned canvases.
/// Fully unit tested; the UI control and the output manager both consume this.
/// </summary>
public static class ScreenLayout
{
    /// <summary>Minimum shared edge length (px) for two touching screens to count as connected.</summary>
    public const int MinSharedEdge = 8;

    /// <summary>Gap tolerance (px) — rects within this of flush count as touching.</summary>
    public const int TouchTolerance = 1;

    /// <summary>True when the rects sit flush along an edge with enough shared span to connect.</summary>
    public static bool Touching(SKRectI a, SKRectI b)
    {
        var vOverlap = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        var hOverlap = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);

        // Left/right adjacency.
        if (vOverlap >= MinSharedEdge &&
            (Math.Abs(a.Right - b.Left) <= TouchTolerance || Math.Abs(b.Right - a.Left) <= TouchTolerance))
        {
            return true;
        }

        // Top/bottom adjacency.
        if (hOverlap >= MinSharedEdge &&
            (Math.Abs(a.Bottom - b.Top) <= TouchTolerance || Math.Abs(b.Bottom - a.Top) <= TouchTolerance))
        {
            return true;
        }

        return false;
    }

    /// <summary>The rects share real area — at least <see cref="MinSharedEdge"/> on both axes, not a flush edge.</summary>
    public static bool Overlapping(SKRectI a, SKRectI b)
    {
        var w = Math.Min(a.Right, b.Right) - Math.Max(a.Left, b.Left);
        var h = Math.Min(a.Bottom, b.Bottom) - Math.Max(a.Top, b.Top);
        return w >= MinSharedEdge && h >= MinSharedEdge;
    }

    /// <summary>
    /// Two screens form one canvas when they touch — or when they overlap and either side blends
    /// automatically: two projectors share their overlap and both draw it, faded. A rig with no
    /// blending projector never regroups on an overlap.
    /// </summary>
    public static bool Connected(in ArrangedScreen a, in ArrangedScreen b)
        => Touching(a.Rect, b.Rect) || ((a.Blend || b.Blend) && Overlapping(a.Rect, b.Rect));

    /// <summary>Connected components of touching (or blend-overlapping) screens. Singletons are their own group.</summary>
    public static List<List<ArrangedScreen>> Groups(IReadOnlyList<ArrangedScreen> screens)
    {
        var groups = new List<List<ArrangedScreen>>();
        var assigned = new bool[screens.Count];

        for (var i = 0; i < screens.Count; i++)
        {
            if (assigned[i]) continue;
            var group = new List<ArrangedScreen>();
            var stack = new Stack<int>();
            stack.Push(i);
            assigned[i] = true;
            while (stack.Count > 0)
            {
                var cur = stack.Pop();
                group.Add(screens[cur]);
                for (var j = 0; j < screens.Count; j++)
                {
                    if (!assigned[j] && Connected(screens[cur], screens[j]))
                    {
                        assigned[j] = true;
                        stack.Push(j);
                    }
                }
            }
            group.Sort((x, y) => x.Rect.Left != y.Rect.Left ? x.Rect.Left - y.Rect.Left : x.Rect.Top - y.Rect.Top);
            groups.Add(group);
        }

        groups.Sort((g1, g2) => g1[0].Rect.Left != g2[0].Rect.Left
            ? g1[0].Rect.Left - g2[0].Rect.Left
            : g1[0].Rect.Top - g2[0].Rect.Top);
        return groups;
    }

    public static SKRectI Union(IReadOnlyList<ArrangedScreen> group)
    {
        var u = group[0].Rect;
        for (var i = 1; i < group.Count; i++)
        {
            u = new SKRectI(
                Math.Min(u.Left, group[i].Rect.Left),
                Math.Min(u.Top, group[i].Rect.Top),
                Math.Max(u.Right, group[i].Rect.Right),
                Math.Max(u.Bottom, group[i].Rect.Bottom));
        }
        return u;
    }

    /// <summary>
    /// Snaps a dragged rect to neighbouring edges within the threshold: primary snap makes an
    /// edge flush (connecting), and once snapped on one axis, the perpendicular axis snaps to
    /// top/left, bottom/right, or centre alignment when close. Returns the original position
    /// when nothing is in range.
    /// </summary>
    public static SKRectI Snap(SKRectI moving, IReadOnlyList<SKRectI> others, int threshold)
    {
        var bestDx = int.MaxValue;
        var bestDy = int.MaxValue;

        foreach (var o in others)
        {
            var vOverlapLoose = Math.Min(moving.Bottom, o.Bottom) - Math.Max(moving.Top, o.Top);
            var hOverlapLoose = Math.Min(moving.Right, o.Right) - Math.Max(moving.Left, o.Left);

            // Horizontal edge snaps (need rough vertical overlap so we snap to actual neighbours).
            if (vOverlapLoose > -threshold)
            {
                Consider(ref bestDx, o.Left - moving.Right, threshold);   // my right → their left
                Consider(ref bestDx, o.Right - moving.Left, threshold);   // my left → their right
            }

            // Vertical edge snaps.
            if (hOverlapLoose > -threshold)
            {
                Consider(ref bestDy, o.Top - moving.Bottom, threshold);   // my bottom → their top
                Consider(ref bestDy, o.Bottom - moving.Top, threshold);   // my top → their bottom
            }
        }

        var snapped = moving;
        if (bestDx != int.MaxValue)
        {
            snapped = new SKRectI(snapped.Left + bestDx, snapped.Top, snapped.Right + bestDx, snapped.Bottom);
        }
        if (bestDy != int.MaxValue)
        {
            snapped = new SKRectI(snapped.Left, snapped.Top + bestDy, snapped.Right, snapped.Bottom + bestDy);
        }

        // Alignment pass on the perpendicular axis: line up tops/bottoms/centres with the
        // nearest neighbour we are now flush against.
        foreach (var o in others)
        {
            if (Math.Abs(snapped.Right - o.Left) <= TouchTolerance || Math.Abs(o.Right - snapped.Left) <= TouchTolerance)
            {
                var dTop = o.Top - snapped.Top;
                var dBottom = o.Bottom - snapped.Bottom;
                var dCenter = (o.Top + o.Bottom) / 2 - (snapped.Top + snapped.Bottom) / 2;
                var d = SmallestWithin(threshold, dTop, dCenter, dBottom);
                if (d is { } dy2)
                {
                    snapped = new SKRectI(snapped.Left, snapped.Top + dy2, snapped.Right, snapped.Bottom + dy2);
                }
            }
            else if (Math.Abs(snapped.Bottom - o.Top) <= TouchTolerance || Math.Abs(o.Bottom - snapped.Top) <= TouchTolerance)
            {
                var dLeft = o.Left - snapped.Left;
                var dRight = o.Right - snapped.Right;
                var dCenter = (o.Left + o.Right) / 2 - (snapped.Left + snapped.Right) / 2;
                var d = SmallestWithin(threshold, dLeft, dCenter, dRight);
                if (d is { } dx2)
                {
                    snapped = new SKRectI(snapped.Left + dx2, snapped.Top, snapped.Right + dx2, snapped.Bottom);
                }
            }
        }

        return snapped;
    }

    private static void Consider(ref int best, int delta, int threshold)
    {
        if (Math.Abs(delta) <= threshold && (best == int.MaxValue || Math.Abs(delta) < Math.Abs(best)))
        {
            best = delta;
        }
    }

    private static int? SmallestWithin(int threshold, params int[] deltas)
    {
        int? best = null;
        foreach (var d in deltas)
        {
            if (Math.Abs(d) <= threshold && (best is null || Math.Abs(d) < Math.Abs(best.Value)))
            {
                best = d;
            }
        }
        return best;
    }

    public static bool OverlapsAny(SKRectI rect, IEnumerable<SKRectI> others)
    {
        foreach (var o in others)
        {
            var w = Math.Min(rect.Right, o.Right) - Math.Max(rect.Left, o.Left);
            var h = Math.Min(rect.Bottom, o.Bottom) - Math.Max(rect.Top, o.Top);
            if (w > TouchTolerance && h > TouchTolerance) return true;
        }
        return false;
    }

    /// <summary>
    /// Default arrangement: screens in a row, top-aligned, with a clear gap so nothing is
    /// connected until the operator drags screens together deliberately.
    /// </summary>
    public static IReadOnlyList<SKPointI> DefaultLayout(IReadOnlyList<SKSizeI> sizes, int gap = 120)
    {
        var points = new List<SKPointI>(sizes.Count);
        var x = 0;
        foreach (var s in sizes)
        {
            points.Add(new SKPointI(x, 0));
            x += s.Width + gap;
        }
        return points;
    }
}
