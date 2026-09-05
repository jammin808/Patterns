using Patterns.Core.Model;
using SkiaSharp;

namespace Patterns.Core.Rendering;

/// <summary>
/// One dead strip of a wall, resolved: the raster pixel it stands before, its width, and how
/// much dead width lies before it on the same axis (so its place on the virtual surface is
/// <see cref="At"/> + <see cref="Before"/>).
/// </summary>
public readonly record struct WallStrip(int At, int Size, int Before)
{
    /// <summary>Where the strip begins on the virtual surface.</summary>
    public int VirtualStart => At + Before;

    /// <summary>The first real pixel after the strip on the virtual surface.</summary>
    public int VirtualEnd => At + Before + Size;
}

/// <summary>A run of real pixels: where it sits in the raster, and where the same pixels sit on the virtual surface.</summary>
public readonly record struct WallSlice(SKRectI Raster, SKRectI Virtual);

/// <summary>
/// The dead strips of one content target — the bezels between the displays of a video wall,
/// the air between the pillars of an LED wall — resolved into the maths every sink shares.
/// The raster is what the outputs are fed; the virtual surface is the raster with the strips
/// put back, and content is laid out on it, so a line across the wall is straight in the room
/// and a thing that moves across a gap goes behind it and comes out where it should. Immutable,
/// built once per snapshot. <see cref="Empty"/> when a target has no strips, and then every
/// question answers "the raster, unchanged".
/// </summary>
public sealed class GapMap
{
    public static readonly GapMap Empty = new(SKSizeI.Empty, Array.Empty<WallStrip>(), Array.Empty<WallStrip>());

    private readonly WallStrip[] _vertical;   // each at an x, sorted
    private readonly WallStrip[] _horizontal; // each at a y, sorted

    private GapMap(SKSizeI raster, WallStrip[] vertical, WallStrip[] horizontal)
    {
        Raster = raster;
        _vertical = vertical;
        _horizontal = horizontal;
        var dx = 0;
        foreach (var s in vertical) dx += s.Size;
        var dy = 0;
        foreach (var s in horizontal) dy += s.Size;
        Virtual = new SKSizeI(raster.Width + dx, raster.Height + dy);
    }

    /// <summary>The pixels the outputs are fed.</summary>
    public SKSizeI Raster { get; }

    /// <summary>The raster with the strips put back: the surface content is laid out on.</summary>
    public SKSizeI Virtual { get; }

    /// <summary>The strips between columns, each at an x, left to right.</summary>
    public IReadOnlyList<WallStrip> Vertical => _vertical;

    /// <summary>The strips between rows, each at a y, top to bottom.</summary>
    public IReadOnlyList<WallStrip> Horizontal => _horizontal;

    public bool IsEmpty => _vertical.Length == 0 && _horizontal.Length == 0;

    public int Count => _vertical.Length + _horizontal.Length;

    /// <summary>
    /// Resolves gaps against a raster: sorted along each axis, one per position (the widest
    /// wins), and those with no width or standing outside the raster dropped — a gap at the
    /// raster's edge is no gap.
    /// </summary>
    public static GapMap Build(SKSizeI raster, IEnumerable<(GapAxis Axis, int At, int Size)> gaps)
    {
        var v = new SortedDictionary<int, int>();
        var h = new SortedDictionary<int, int>();
        foreach (var g in gaps)
        {
            if (g.Size <= 0 || g.At <= 0) continue;
            var limit = g.Axis == GapAxis.Vertical ? raster.Width : raster.Height;
            if (g.At >= limit) continue;
            var into = g.Axis == GapAxis.Vertical ? v : h;
            into[g.At] = into.TryGetValue(g.At, out var have) ? Math.Max(have, g.Size) : g.Size;
        }
        return new GapMap(raster, Resolve(v), Resolve(h));
    }

    private static WallStrip[] Resolve(SortedDictionary<int, int> sorted)
    {
        var result = new WallStrip[sorted.Count];
        var i = 0;
        var before = 0;
        foreach (var (at, size) in sorted)
        {
            result[i++] = new WallStrip(at, size, before);
            before += size;
        }
        return result;
    }

    /// <summary>A stand-alone screen's map: its own strips in its own raster.</summary>
    public static GapMap ForScreen(SKSizeI raster, IEnumerable<WallGap> gaps)
        => Build(raster, gaps.Select(g => (g.Axis, g.At, g.Size)));

    /// <summary>
    /// A joined canvas's map: the seams between its members — every member's left edge inside
    /// the union a vertical strip of <paramref name="seamX"/>, every top edge inside it a
    /// horizontal one of <paramref name="seamY"/> — plus each member's own strips, moved to
    /// where the member sits. Member rects are in the union's own space (its top-left at 0,0).
    /// </summary>
    public static GapMap ForCanvas(SKSizeI union, IEnumerable<(SKRectI Rect, IEnumerable<WallGap> Gaps)> members, int seamX, int seamY)
    {
        var list = new List<(GapAxis, int, int)>();
        foreach (var (rect, gaps) in members)
        {
            if (seamX > 0 && rect.Left > 0) list.Add((GapAxis.Vertical, rect.Left, seamX));
            if (seamY > 0 && rect.Top > 0) list.Add((GapAxis.Horizontal, rect.Top, seamY));
            foreach (var g in gaps)
            {
                var at = g.Axis == GapAxis.Vertical ? rect.Left + g.At : rect.Top + g.At;
                list.Add((g.Axis, at, g.Size));
            }
        }
        return Build(union, list);
    }

    /// <summary>Where raster column x sits on the virtual surface.</summary>
    public int VirtualX(int rasterX) => rasterX + BeforeOf(_vertical, rasterX);

    /// <summary>Where raster row y sits on the virtual surface.</summary>
    public int VirtualY(int rasterY) => rasterY + BeforeOf(_horizontal, rasterY);

    private static int BeforeOf(WallStrip[] strips, int raster)
    {
        var sum = 0;
        foreach (var s in strips)
        {
            if (s.At > raster) break;
            sum += s.Size;
        }
        return sum;
    }

    public SKPointI VirtualOrigin(SKPointI raster) => new(VirtualX(raster.X), VirtualY(raster.Y));

    /// <summary>The span a raster region takes on the virtual surface — the strips inside it included.</summary>
    public SKRectI VirtualRect(SKRectI raster)
    {
        var l = VirtualX(raster.Left);
        var t = VirtualY(raster.Top);
        if (raster.Width <= 0 || raster.Height <= 0) return SKRectI.Create(l, t, Math.Max(0, raster.Width), Math.Max(0, raster.Height));
        var r = VirtualX(raster.Right - 1) + 1;
        var b = VirtualY(raster.Bottom - 1) + 1;
        return new SKRectI(l, t, r, b);
    }

    /// <summary>
    /// The runs of real pixels inside a raster region, each with its place on the virtual
    /// surface, rows then columns: one slice when no strip cuts through the region.
    /// </summary>
    public IReadOnlyList<WallSlice> Slices(SKRectI raster)
    {
        var xs = Cuts(_vertical, raster.Left, raster.Right);
        var ys = Cuts(_horizontal, raster.Top, raster.Bottom);
        var result = new List<WallSlice>((xs.Count + 1) * (ys.Count + 1));
        var y0 = raster.Top;
        for (var yi = 0; yi <= ys.Count; yi++)
        {
            var y1 = yi < ys.Count ? ys[yi] : raster.Bottom;
            var x0 = raster.Left;
            for (var xi = 0; xi <= xs.Count; xi++)
            {
                var x1 = xi < xs.Count ? xs[xi] : raster.Right;
                var run = new SKRectI(x0, y0, x1, y1);
                if (run.Width > 0 && run.Height > 0) result.Add(new WallSlice(run, VirtualRect(run)));
                x0 = x1;
            }
            y0 = y1;
        }
        return result;
    }

    private static List<int> Cuts(WallStrip[] strips, int start, int end)
    {
        var cuts = new List<int>();
        foreach (var s in strips)
        {
            if (s.At > start && s.At < end) cuts.Add(s.At);
        }
        return cuts;
    }

    /// <summary>
    /// The strips that cross a region of the virtual surface, as rects in the region's own
    /// coordinates (its top-left at 0,0): what a monitor shades so the desk sees where the wall
    /// has no pixels.
    /// </summary>
    public IEnumerable<SKRectI> StripsIn(SKRectI virtualRegion)
    {
        foreach (var s in _vertical)
        {
            var l = Math.Max(s.VirtualStart, virtualRegion.Left);
            var r = Math.Min(s.VirtualEnd, virtualRegion.Right);
            if (r > l) yield return new SKRectI(l - virtualRegion.Left, 0, r - virtualRegion.Left, virtualRegion.Height);
        }
        foreach (var s in _horizontal)
        {
            var t = Math.Max(s.VirtualStart, virtualRegion.Top);
            var b = Math.Min(s.VirtualEnd, virtualRegion.Bottom);
            if (b > t) yield return new SKRectI(0, t - virtualRegion.Top, virtualRegion.Width, b - virtualRegion.Top);
        }
    }

    /// <summary>The words the Screens page reads: what is laid out, what is shown, how many strips.</summary>
    public string Summary
    {
        get
        {
            if (IsEmpty) return "No gaps — content is laid out on the raster as it is.";
            var parts = new List<string>(2);
            if (_vertical.Length > 0) parts.Add($"{_vertical.Length} vertical");
            if (_horizontal.Length > 0) parts.Add($"{_horizontal.Length} horizontal");
            return $"{string.Join(" · ", parts)} — content laid out on {Virtual.Width}×{Virtual.Height}, the outputs show {Raster.Width}×{Raster.Height} of it.";
        }
    }
}
