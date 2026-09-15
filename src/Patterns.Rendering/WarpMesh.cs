using Patterns.Core.Model;
using SkiaSharp;

namespace Patterns.Rendering;

/// <summary>
/// The edge bends: one number per edge, in the projector's own pixels, bowing that edge of the
/// picture outward (positive) or inward (negative) at its middle — a curved screen, a lens's
/// pincushion or barrel, a cyclorama's sweep — on top of the four-corner keystone. The four bows
/// make the twelve control points of one Coons patch (the corners, and two cubic handles per
/// edge), which the pipeline draws the finished picture through; the keystone stays the
/// perspective it always was, applied over the patch, so straight lines inside stay straight and
/// only the edges curve. Pure and unit tested.
/// </summary>
public static class WarpMesh
{
    /// <summary>
    /// The twelve points of the patch over a w×h picture, clockwise from the top-left corner:
    /// TL, top's two handles, TR, right's two, BR, bottom's two, BL, left's two — Skia's order. A
    /// cubic with both handles pushed by 4/3 of the bow passes through its middle at exactly the
    /// bow: (P0 + 3P1 + 3P2 + P3) / 8 puts 6/8 of the handles' push at the midpoint.
    /// </summary>
    public static SKPoint[] Cubics(float w, float h, float top, float right, float bottom, float left)
    {
        const float k = 4f / 3f;
        var t = k * top;
        var r = k * right;
        var b = k * bottom;
        var l = k * left;
        return new[]
        {
            new SKPoint(0, 0),
            new SKPoint(w / 3f, -t), new SKPoint(2f * w / 3f, -t),
            new SKPoint(w, 0),
            new SKPoint(w + r, h / 3f), new SKPoint(w + r, 2f * h / 3f),
            new SKPoint(w, h),
            new SKPoint(2f * w / 3f, h + b), new SKPoint(w / 3f, h + b),
            new SKPoint(0, h),
            new SKPoint(-l, 2f * h / 3f), new SKPoint(-l, h / 3f),
        };
    }

    /// <summary>The texture corners the patch maps the picture's corners to: TL, TR, BR, BL — the same order as the patch's corners.</summary>
    public static SKPoint[] TextureCorners(float w, float h) => new[] { new SKPoint(0, 0), new SKPoint(w, 0), new SKPoint(w, h), new SKPoint(0, h) };

    /// <summary>A cubic's point at t, from its four control points.</summary>
    public static SKPoint At(SKPoint p0, SKPoint p1, SKPoint p2, SKPoint p3, float t)
    {
        var u = 1 - t;
        var a = u * u * u;
        var b = 3 * u * u * t;
        var c = 3 * u * t * t;
        var d = t * t * t;
        return new SKPoint(a * p0.X + b * p1.X + c * p2.X + d * p3.X, a * p0.Y + b * p1.Y + c * p2.Y + d * p3.Y);
    }

    /// <summary>The middle of an edge of the patch — "top", "right", "bottom", "left" — where the bow is measured.</summary>
    public static SKPoint EdgeMidpoint(SKPoint[] cubics, string edge)
    {
        var i = edge switch { "top" => 0, "right" => 3, "bottom" => 6, _ => 9 };
        return At(cubics[i], cubics[i + 1], cubics[i + 2], cubics[(i + 3) % 12], 0.5f);
    }

    public static bool HasBend(ScreenPlacement p) => p.WarpTopBow != 0 || p.WarpRightBow != 0 || p.WarpBottomBow != 0 || p.WarpLeftBow != 0;

    /// <summary>The patch for a placement over a w×h picture.</summary>
    public static SKPoint[] ForPlacement(ScreenPlacement p, float w, float h) => Cubics(w, h, p.WarpTopBow, p.WarpRightBow, p.WarpBottomBow, p.WarpLeftBow);
}

/// <summary>
/// Black-level matching, pure. Two projectors' blacks do not blend: a projector's black is light,
/// and where two pictures overlap the floor is two blacks bright; where four share a corner, four.
/// The seams show on a dark scene as brighter bands and a brighter square in the middle. The cure
/// every blender uses is to raise the black everywhere else to the brightest floor — a pedestal
/// added outside the zones — so the whole canvas sits on one floor. Each projector adds, in each
/// region of its own picture, the share that brings that region's coverage up to the canvas's
/// deepest overlap: (deepest − coverage) / coverage blacks; nothing where the coverage is the
/// deepest already. The operator finds the one number (the pedestal, as a percentage of white)
/// on a black test picture, sliding it until the seams go.
/// </summary>
public static class BlackLevel
{
    /// <summary>How many projectors' light reaches the deepest region this output takes part in: 1, 2 (a row or a stack), or 4 (a grid's corner).</summary>
    public static int MaxCoverage(in BlendWidths widths)
        => (widths.Left > 0 || widths.Right > 0 ? 2 : 1) * (widths.Top > 0 || widths.Bottom > 0 ? 2 : 1);

    /// <summary>
    /// The regions of a w×h picture by how many projectors cover them: the picture between the
    /// zones (1), each side and top or bottom band (2), and each corner where a side band meets a
    /// top or bottom band (4). Empty zones make no bands. Nine cells at most; rects in the
    /// picture's own pixels.
    /// </summary>
    public static IReadOnlyList<(SKRectI Rect, int Coverage)> Cells(int width, int height, in BlendWidths widths)
    {
        var l = Math.Clamp(widths.Left, 0, width);
        var r = Math.Clamp(widths.Right, 0, width - l);
        var t = Math.Clamp(widths.Top, 0, height);
        var b = Math.Clamp(widths.Bottom, 0, height - t);
        var cols = new List<(int X, int W, bool Band)>();
        if (l > 0) cols.Add((0, l, true));
        if (width - l - r > 0) cols.Add((l, width - l - r, false));
        if (r > 0) cols.Add((width - r, r, true));
        var rows = new List<(int Y, int H, bool Band)>();
        if (t > 0) rows.Add((0, t, true));
        if (height - t - b > 0) rows.Add((t, height - t - b, false));
        if (b > 0) rows.Add((height - b, b, true));
        var cells = new List<(SKRectI, int)>();
        foreach (var row in rows)
        {
            foreach (var col in cols)
            {
                cells.Add((SKRectI.Create(col.X, row.Y, col.W, row.H), (col.Band ? 2 : 1) * (row.Band ? 2 : 1)));
            }
        }
        return cells;
    }

    /// <summary>
    /// The pedestal one projector adds in a region, as a fraction of white to send: the black
    /// (a fraction of white) times the shortfall of that region's coverage against the deepest,
    /// per projector covering it, then through the inverse of the blend gamma so the light — not
    /// the signal — comes out even. 0 where the region is the deepest already.
    /// </summary>
    public static double Signal(double blackPct, int coverage, int maxCoverage, double gamma)
    {
        if (blackPct <= 0 || coverage <= 0 || coverage >= maxCoverage) return 0;
        var light = blackPct / 100.0 * (maxCoverage - coverage) / (double)coverage;
        gamma = Math.Clamp(gamma, 0.5, 3.0);
        var signal = Math.Abs(gamma - 1.0) < 1e-6 ? light : Math.Pow(light, 1.0 / gamma);
        return Math.Clamp(signal, 0, 1);
    }

    /// <summary>The pedestal as an 8-bit grey to add.</summary>
    public static byte Level(double blackPct, int coverage, int maxCoverage, double gamma)
        => (byte)Math.Clamp(Math.Round(255 * Signal(blackPct, coverage, maxCoverage, gamma)), 0, 255);
}

/// <summary>
/// A grid of projectors laid out to blend: columns × rows of pictures, each overlapping its
/// neighbour by the zone width, so the auto blend finds the zones and the joins come out equal.
/// The layout the Screens page's ARRANGE AS A BLEND GRID makes — pure, over the pictures' sizes.
/// </summary>
public static class BlendGridLayout
{
    /// <summary>Where each of <paramref name="sizes"/> goes, left to right then top to bottom, overlapping by <paramref name="overlap"/> px; a short list leaves the last cells empty.</summary>
    public static IReadOnlyList<SKPointI> Positions(IReadOnlyList<SKSizeI> sizes, int columns, int rows, int overlap)
    {
        columns = Math.Max(1, columns);
        rows = Math.Max(1, rows);
        overlap = Math.Max(0, overlap);
        var positions = new List<SKPointI>(sizes.Count);
        var y = 0;
        var i = 0;
        for (var row = 0; row < rows && i < sizes.Count; row++)
        {
            var x = 0;
            var tallest = 0;
            for (var col = 0; col < columns && i < sizes.Count; col++, i++)
            {
                positions.Add(new SKPointI(x, y));
                x += Math.Max(1, sizes[i].Width - overlap);
                tallest = Math.Max(tallest, sizes[i].Height);
            }
            y += Math.Max(1, tallest - overlap);
        }
        return positions;
    }

    /// <summary>The words for a layout: "2 × 2, 200 px overlaps: the canvas is 3640 × 1960".</summary>
    public static string Describe(IReadOnlyList<SKSizeI> sizes, IReadOnlyList<SKPointI> positions, int columns, int rows, int overlap)
    {
        var right = 0;
        var bottom = 0;
        for (var i = 0; i < positions.Count && i < sizes.Count; i++)
        {
            right = Math.Max(right, positions[i].X + sizes[i].Width);
            bottom = Math.Max(bottom, positions[i].Y + sizes[i].Height);
        }
        return $"{columns} × {rows}, {overlap} px overlaps: the canvas is {right} × {bottom}.";
    }
}
