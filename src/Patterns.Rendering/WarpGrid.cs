using System.Globalization;
using Patterns.Core.Model;
using SkiaSharp;

namespace Patterns.Rendering;

/// <summary>
/// The mesh warp: a lattice of control points over the projector's picture — columns × rows,
/// each pulled from its rest by an offset in the projector's own pixels — for a dome, a set
/// piece, a lens whose distortion is not a clean bow. The offsets live on the placement as one
/// line of text ("dx,dy;dx,dy;…", row by row), so the show file carries them and a change is one
/// edit. The lattice is drawn as a grid of Coons patches with Catmull-Rom tangents between the
/// points, so the surface is smooth across the cells; the four edge bends fold into the edge
/// points, so bends and mesh compose. Pure and unit tested; the pipeline draws what this makes.
/// </summary>
public static class WarpGrid
{
    public const int MinSize = 2;
    public const int MaxSize = 17;

    /// <summary>The densities the page offers.</summary>
    public static readonly int[] Densities = { 3, 5, 9, 17 };

    public static int ClampSize(int n) => Math.Clamp(n, MinSize, MaxSize);

    /// <summary>The offsets, two per point (dx, dy), row by row — a short or empty line reads as zero from where it ends.</summary>
    public static float[] Parse(string? text, int columns, int rows)
    {
        columns = ClampSize(columns);
        rows = ClampSize(rows);
        var offsets = new float[columns * rows * 2];
        if (string.IsNullOrWhiteSpace(text)) return offsets;
        var pairs = text.Split(';', StringSplitOptions.TrimEntries);
        for (var k = 0; k < pairs.Length && k < columns * rows; k++)
        {
            var xy = pairs[k].Split(',', StringSplitOptions.TrimEntries);
            if (xy.Length != 2) continue;
            if (float.TryParse(xy[0], NumberStyles.Float, CultureInfo.InvariantCulture, out var dx)) offsets[k * 2] = dx;
            if (float.TryParse(xy[1], NumberStyles.Float, CultureInfo.InvariantCulture, out var dy)) offsets[k * 2 + 1] = dy;
        }
        return offsets;
    }

    /// <summary>The offsets as the placement's line: "" when every point rests.</summary>
    public static string Format(IReadOnlyList<float> offsets)
    {
        if (offsets.All(v => v == 0)) return "";
        var parts = new string[offsets.Count / 2];
        for (var k = 0; k < parts.Length; k++)
        {
            parts[k] = offsets[k * 2].ToString("0.#", CultureInfo.InvariantCulture) + "," + offsets[k * 2 + 1].ToString("0.#", CultureInfo.InvariantCulture);
        }
        return string.Join(";", parts);
    }

    /// <summary>Whether the placement's mesh pulls any point.</summary>
    public static bool HasMesh(ScreenPlacement p) => Parse(p.WarpMesh, p.WarpMeshColumns, p.WarpMeshRows).Any(v => v != 0);

    /// <summary>Where point (i, j) rests over a w×h picture.</summary>
    public static SKPoint Rest(int i, int j, int columns, int rows, float w, float h)
        => new(w * i / (columns - 1), h * j / (rows - 1));

    /// <summary>The bow an edge point takes from the placement's bends: a parabola through the bow at the middle, nothing at the corners.</summary>
    private static SKPoint Bend(int i, int j, int columns, int rows, int top, int right, int bottom, int left)
    {
        float dx = 0, dy = 0;
        var tx = (float)i / (columns - 1);
        var ty = (float)j / (rows - 1);
        var bowX = 1 - (2 * tx - 1) * (2 * tx - 1);
        var bowY = 1 - (2 * ty - 1) * (2 * ty - 1);
        if (j == 0) dy -= top * bowX;
        if (j == rows - 1) dy += bottom * bowX;
        if (i == 0) dx -= left * bowY;
        if (i == columns - 1) dx += right * bowY;
        return new SKPoint(dx, dy);
    }

    /// <summary>Every point of the lattice as it stands — rest, the mesh's offsets, the bends' bows on the edges — row by row.</summary>
    public static SKPoint[] Nodes(int columns, int rows, float w, float h, IReadOnlyList<float> offsets, int top = 0, int right = 0, int bottom = 0, int left = 0)
    {
        columns = ClampSize(columns);
        rows = ClampSize(rows);
        var nodes = new SKPoint[columns * rows];
        for (var j = 0; j < rows; j++)
        {
            for (var i = 0; i < columns; i++)
            {
                var k = j * columns + i;
                var rest = Rest(i, j, columns, rows, w, h);
                var bend = Bend(i, j, columns, rows, top, right, bottom, left);
                var dx = k * 2 < offsets.Count ? offsets[k * 2] : 0;
                var dy = k * 2 + 1 < offsets.Count ? offsets[k * 2 + 1] : 0;
                nodes[k] = new SKPoint(rest.X + dx + bend.X, rest.Y + dy + bend.Y);
            }
        }
        return nodes;
    }

    public static SKPoint[] NodesOf(ScreenPlacement p, float w, float h)
        => Nodes(p.WarpMeshColumns, p.WarpMeshRows, w, h, Parse(p.WarpMesh, p.WarpMeshColumns, p.WarpMeshRows), p.WarpTopBow, p.WarpRightBow, p.WarpBottomBow, p.WarpLeftBow);

    /// <summary>The point nearest to a place, within a reach; -1 when none is that close.</summary>
    public static int Nearest(IReadOnlyList<SKPoint> nodes, SKPoint at, float within)
    {
        var best = -1;
        var bestD = within * within;
        for (var k = 0; k < nodes.Count; k++)
        {
            var dx = nodes[k].X - at.X;
            var dy = nodes[k].Y - at.Y;
            var d = dx * dx + dy * dy;
            if (d <= bestD)
            {
                bestD = d;
                best = k;
            }
        }
        return best;
    }

    /// <summary>The line with one point's offset changed — set, not added — clamped to the placement's reach.</summary>
    public static string Moved(string? text, int columns, int rows, int index, float dx, float dy)
    {
        var offsets = Parse(text, columns, rows);
        if (index < 0 || index * 2 + 1 >= offsets.Length) return Format(offsets);
        offsets[index * 2] = Math.Clamp(dx, -4096, 4096);
        offsets[index * 2 + 1] = Math.Clamp(dy, -4096, 4096);
        return Format(offsets);
    }

    /// <summary>The line with one point nudged by a step.</summary>
    public static string Nudged(string? text, int columns, int rows, int index, float dx, float dy)
    {
        var offsets = Parse(text, columns, rows);
        if (index < 0 || index * 2 + 1 >= offsets.Length) return Format(offsets);
        return Moved(text, columns, rows, index, offsets[index * 2] + dx, offsets[index * 2 + 1] + dy);
    }

    /// <summary>
    /// The offsets over another density: each new point takes the old field's offset where it
    /// stands, bilinearly — a coarse mesh made fine keeps its shape, a fine one made coarse keeps
    /// what the coarse points can carry.
    /// </summary>
    public static string Resampled(string? text, int columns, int rows, int newColumns, int newRows)
    {
        columns = ClampSize(columns);
        rows = ClampSize(rows);
        newColumns = ClampSize(newColumns);
        newRows = ClampSize(newRows);
        var old = Parse(text, columns, rows);
        if (old.All(v => v == 0)) return "";
        var result = new float[newColumns * newRows * 2];
        for (var j = 0; j < newRows; j++)
        {
            var v = (float)j / (newRows - 1) * (rows - 1);
            var j0 = Math.Min((int)Math.Floor(v), rows - 1);
            var j1 = Math.Min(j0 + 1, rows - 1);
            var fy = v - j0;
            for (var i = 0; i < newColumns; i++)
            {
                var u = (float)i / (newColumns - 1) * (columns - 1);
                var i0 = Math.Min((int)Math.Floor(u), columns - 1);
                var i1 = Math.Min(i0 + 1, columns - 1);
                var fx = u - i0;
                for (var c = 0; c < 2; c++)
                {
                    var a = old[(j0 * columns + i0) * 2 + c];
                    var b = old[(j0 * columns + i1) * 2 + c];
                    var d = old[(j1 * columns + i0) * 2 + c];
                    var e = old[(j1 * columns + i1) * 2 + c];
                    result[(j * newColumns + i) * 2 + c] = (a * (1 - fx) + b * fx) * (1 - fy) + (d * (1 - fx) + e * fx) * fy;
                }
            }
        }
        return Format(result);
    }

    /// <summary>
    /// One Coons patch per cell — the twelve control points clockwise from the cell's top-left, as
    /// Skia takes them — with the texture's four corners for the cell's share of the picture. The
    /// handles are Catmull-Rom tangents (a third of the way to the neighbours' difference), so a
    /// curve runs smoothly from cell to cell and a lattice at rest draws straight.
    /// </summary>
    public static IReadOnlyList<(SKPoint[] Cubics, SKPoint[] Texture)> Patches(IReadOnlyList<SKPoint> nodes, int columns, int rows, float w, float h)
    {
        columns = ClampSize(columns);
        rows = ClampSize(rows);
        var patches = new List<(SKPoint[], SKPoint[])>((columns - 1) * (rows - 1));
        SKPoint N(int i, int j) => nodes[j * columns + i];
        SKPoint Th(int i, int j)
        {
            var a = N(Math.Max(0, i - 1), j);
            var b = N(Math.Min(columns - 1, i + 1), j);
            var span = Math.Min(columns - 1, i + 1) - Math.Max(0, i - 1);
            return span == 0 ? default : new SKPoint((b.X - a.X) / span, (b.Y - a.Y) / span);
        }
        SKPoint Tv(int i, int j)
        {
            var a = N(i, Math.Max(0, j - 1));
            var b = N(i, Math.Min(rows - 1, j + 1));
            var span = Math.Min(rows - 1, j + 1) - Math.Max(0, j - 1);
            return span == 0 ? default : new SKPoint((b.X - a.X) / span, (b.Y - a.Y) / span);
        }
        static SKPoint Add(SKPoint p, SKPoint t, float k) => new(p.X + t.X * k, p.Y + t.Y * k);
        for (var j = 0; j < rows - 1; j++)
        {
            for (var i = 0; i < columns - 1; i++)
            {
                var tl = N(i, j);
                var tr = N(i + 1, j);
                var br = N(i + 1, j + 1);
                var bl = N(i, j + 1);
                var cubics = new[]
                {
                    tl, Add(tl, Th(i, j), 1f / 3), Add(tr, Th(i + 1, j), -1f / 3),
                    tr, Add(tr, Tv(i + 1, j), 1f / 3), Add(br, Tv(i + 1, j + 1), -1f / 3),
                    br, Add(br, Th(i + 1, j + 1), -1f / 3), Add(bl, Th(i, j + 1), 1f / 3),
                    bl, Add(bl, Tv(i, j + 1), -1f / 3), Add(tl, Tv(i, j), 1f / 3),
                };
                var x0 = w * i / (columns - 1);
                var x1 = w * (i + 1) / (columns - 1);
                var y0 = h * j / (rows - 1);
                var y1 = h * (j + 1) / (rows - 1);
                var texture = new[] { new SKPoint(x0, y0), new SKPoint(x1, y0), new SKPoint(x1, y1), new SKPoint(x0, y1) };
                patches.Add((cubics, texture));
            }
        }
        return patches;
    }

    /// <summary>The lattice's lines as point pairs — every row and every column, cell by cell — for the overlay on the projector and the editor.</summary>
    public static IReadOnlyList<(SKPoint A, SKPoint B)> Lines(IReadOnlyList<SKPoint> nodes, int columns, int rows)
    {
        columns = ClampSize(columns);
        rows = ClampSize(rows);
        var lines = new List<(SKPoint, SKPoint)>();
        for (var j = 0; j < rows; j++)
        {
            for (var i = 0; i < columns; i++)
            {
                if (i + 1 < columns) lines.Add((nodes[j * columns + i], nodes[j * columns + i + 1]));
                if (j + 1 < rows) lines.Add((nodes[j * columns + i], nodes[(j + 1) * columns + i]));
            }
        }
        return lines;
    }

    /// <summary>"point 3,2 of 5×5 — pulled 12 px right, 4 px up" for the page's line.</summary>
    public static string Describe(int index, int columns, int rows, IReadOnlyList<float> offsets)
    {
        if (index < 0 || index * 2 + 1 >= offsets.Count) return $"{columns}×{rows} lattice — click a point to pull it; arrows nudge it, Shift for ten.";
        var i = index % columns;
        var j = index / columns;
        var dx = offsets[index * 2];
        var dy = offsets[index * 2 + 1];
        var where = dx == 0 && dy == 0 ? "at rest" : $"pulled {(dx == 0 ? "" : $"{Math.Abs(dx):0.#} px {(dx > 0 ? "right" : "left")}")}{(dx != 0 && dy != 0 ? ", " : "")}{(dy == 0 ? "" : $"{Math.Abs(dy):0.#} px {(dy > 0 ? "down" : "up")}")}";
        return $"point {i + 1},{j + 1} of {columns}×{rows} — {where}";
    }
}
