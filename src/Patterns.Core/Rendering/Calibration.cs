using System.Globalization;
using System.Text;
using SkiaSharp;

namespace Patterns.Core.Rendering;

/// <summary>A camera's picture as the calibration reads it: one grey byte per pixel.</summary>
public sealed class GreyFrame
{
    public GreyFrame(int width, int height, byte[]? pixels = null)
    {
        Width = Math.Max(1, width);
        Height = Math.Max(1, height);
        Pixels = pixels is { } p && p.Length == Width * Height ? p : new byte[Width * Height];
    }

    public int Width { get; }
    public int Height { get; }
    public byte[] Pixels { get; }

    public byte At(int x, int y) => Pixels[y * Width + x];

    /// <summary>A frame with fewer pixels, each the mean of a block — the solver never needs the camera's full count.</summary>
    public GreyFrame Downsampled(int factor)
    {
        if (factor <= 1) return this;
        var w = Math.Max(1, Width / factor);
        var h = Math.Max(1, Height / factor);
        var result = new byte[w * h];
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var sum = 0;
                for (var dy = 0; dy < factor; dy++)
                {
                    for (var dx = 0; dx < factor; dx++) sum += Pixels[(y * factor + dy) * Width + x * factor + dx];
                }
                result[y * w + x] = (byte)(sum / (factor * factor));
            }
        }
        return new GreyFrame(w, h, result);
    }
}

/// <summary>What one pattern of the structured-light sequence shows.</summary>
public enum CalStep
{
    /// <summary>Every pixel lit: the reference for "this projector reaches here".</summary>
    White,
    /// <summary>Nothing lit: the reference for the room's own light.</summary>
    Black,
    /// <summary>Vertical stripes: one bit of each column's Gray code.</summary>
    Column,
    /// <summary>Horizontal stripes: one bit of each row's Gray code.</summary>
    Row,
}

/// <summary>One pattern: the step, the bit it carries (for the stripes), and whether it is the inverse — every bit is shown both ways, so a pixel's bit is read as which of the two was brighter, never against a threshold.</summary>
public readonly record struct CalPattern(CalStep Step, int Bit, bool Inverse)
{
    public string Name => Step switch
    {
        CalStep.White => "white",
        CalStep.Black => "black",
        CalStep.Column => $"columns bit {Bit}{(Inverse ? " inverse" : "")}",
        _ => $"rows bit {Bit}{(Inverse ? " inverse" : "")}",
    };
}

/// <summary>
/// Gray-code structured light, pure: the sequence of patterns a projector shows, whether a
/// pixel is lit in each, and the decode — from the camera's frames, which projector pixel lights
/// each camera pixel. A Gray code changes one bit between neighbours, so a camera pixel on a
/// stripe's edge is wrong by one column at most, never by half the picture.
/// </summary>
public static class GrayCode
{
    public static int Bits(int size) => size <= 1 ? 1 : (int)Math.Ceiling(Math.Log2(size));

    public static int Encode(int n) => n ^ (n >> 1);

    public static int Decode(int gray)
    {
        var n = gray;
        for (var mask = n >> 1; mask != 0; mask >>= 1) n ^= mask;
        return n;
    }

    /// <summary>White, black, then each column bit and its inverse from the top bit down, then each row bit the same.</summary>
    public static IReadOnlyList<CalPattern> Sequence(int width, int height)
    {
        var list = new List<CalPattern> { new(CalStep.White, 0, false), new(CalStep.Black, 0, false) };
        for (var b = Bits(width) - 1; b >= 0; b--)
        {
            list.Add(new CalPattern(CalStep.Column, b, false));
            list.Add(new CalPattern(CalStep.Column, b, true));
        }
        for (var b = Bits(height) - 1; b >= 0; b--)
        {
            list.Add(new CalPattern(CalStep.Row, b, false));
            list.Add(new CalPattern(CalStep.Row, b, true));
        }
        return list;
    }

    public static bool IsLit(in CalPattern pattern, int x, int y)
    {
        switch (pattern.Step)
        {
            case CalStep.White: return true;
            case CalStep.Black: return false;
            case CalStep.Column: return (((Encode(x) >> pattern.Bit) & 1) == 1) != pattern.Inverse;
            default: return (((Encode(y) >> pattern.Bit) & 1) == 1) != pattern.Inverse;
        }
    }

    /// <summary>The lit runs of a stripe pattern along its axis, as (start, length) pairs — what the output draws as rects.</summary>
    public static IReadOnlyList<(int Start, int Length)> Runs(in CalPattern pattern, int extent)
    {
        var runs = new List<(int, int)>();
        if (pattern.Step is CalStep.White) { runs.Add((0, extent)); return runs; }
        if (pattern.Step is CalStep.Black) return runs;
        var start = -1;
        for (var i = 0; i <= extent; i++)
        {
            var lit = i < extent && (pattern.Step == CalStep.Column ? IsLit(pattern, i, 0) : IsLit(pattern, 0, i));
            if (lit && start < 0) start = i;
            else if (!lit && start >= 0)
            {
                runs.Add((start, i - start));
                start = -1;
            }
        }
        return runs;
    }

    /// <summary>
    /// The camera's frames, one per pattern in the sequence's order, read into which projector
    /// pixel lights each camera pixel. A camera pixel counts only where white beat black by the
    /// contrast asked for; each bit is which of the pair was brighter.
    /// </summary>
    public static Correspondence Decode(IReadOnlyList<CalPattern> sequence, IReadOnlyList<GreyFrame> frames, int projectorWidth, int projectorHeight, int minContrast = 24)
    {
        if (frames.Count != sequence.Count) throw new ArgumentException($"{sequence.Count} patterns but {frames.Count} frames.", nameof(frames));
        var w = frames[0].Width;
        var h = frames[0].Height;
        var result = new Correspondence(w, h, projectorWidth, projectorHeight);
        var white = frames[sequence.ToList().FindIndex(p => p.Step == CalStep.White)];
        var black = frames[sequence.ToList().FindIndex(p => p.Step == CalStep.Black)];
        var columnBits = Bits(projectorWidth);
        var rowBits = Bits(projectorHeight);
        var indexOf = new Dictionary<CalPattern, int>();
        for (var i = 0; i < sequence.Count; i++) indexOf[sequence[i]] = i;
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var k = y * w + x;
                if (white.Pixels[k] - black.Pixels[k] < minContrast) continue;
                var gx = 0;
                for (var b = columnBits - 1; b >= 0; b--)
                {
                    var on = frames[indexOf[new CalPattern(CalStep.Column, b, false)]].Pixels[k];
                    var off = frames[indexOf[new CalPattern(CalStep.Column, b, true)]].Pixels[k];
                    gx = (gx << 1) | (on > off ? 1 : 0);
                }
                var gy = 0;
                for (var b = rowBits - 1; b >= 0; b--)
                {
                    var on = frames[indexOf[new CalPattern(CalStep.Row, b, false)]].Pixels[k];
                    var off = frames[indexOf[new CalPattern(CalStep.Row, b, true)]].Pixels[k];
                    gy = (gy << 1) | (on > off ? 1 : 0);
                }
                var px = Decode(gx);
                var py = Decode(gy);
                if (px < 0 || px >= projectorWidth || py < 0 || py >= projectorHeight) continue;
                result.Set(x, y, px, py);
            }
        }
        return result;
    }
}

/// <summary>Which projector pixel lights each camera pixel — the decode's answer, and what a synthetic room hands the solver directly.</summary>
public sealed class Correspondence
{
    public Correspondence(int width, int height, int projectorWidth, int projectorHeight)
    {
        Width = width;
        Height = height;
        ProjectorWidth = projectorWidth;
        ProjectorHeight = projectorHeight;
        Valid = new bool[width * height];
        Px = new ushort[width * height];
        Py = new ushort[width * height];
    }

    public int Width { get; }
    public int Height { get; }
    public int ProjectorWidth { get; }
    public int ProjectorHeight { get; }
    public bool[] Valid { get; }
    public ushort[] Px { get; }
    public ushort[] Py { get; }

    public void Set(int x, int y, int px, int py)
    {
        var k = y * Width + x;
        Valid[k] = true;
        Px[k] = (ushort)Math.Clamp(px, 0, ushort.MaxValue);
        Py[k] = (ushort)Math.Clamp(py, 0, ushort.MaxValue);
    }

    public bool IsValid(int x, int y) => x >= 0 && y >= 0 && x < Width && y < Height && Valid[y * Width + x];

    public int ValidCount => Valid.Count(v => v);

    public double CoverageFraction => ValidCount / (double)(Width * Height);

    /// <summary>The camera pixels this projector reaches, as a box; empty when none.</summary>
    public SKRectI Bounds
    {
        get
        {
            int l = int.MaxValue, t = int.MaxValue, r = -1, b = -1;
            for (var y = 0; y < Height; y++)
            {
                for (var x = 0; x < Width; x++)
                {
                    if (!Valid[y * Width + x]) continue;
                    l = Math.Min(l, x); t = Math.Min(t, y); r = Math.Max(r, x); b = Math.Max(b, y);
                }
            }
            return r < 0 ? SKRectI.Empty : new SKRectI(l, t, r + 1, b + 1);
        }
    }

    /// <summary>
    /// The projector point a camera point lights, between the four camera pixels around it
    /// (each pixel's sample stands at its centre, and a decoded pixel is a pixel's left edge, so
    /// half a pixel is added back); null when any of the four was not lit by this projector —
    /// the fit takes over there, which is unbiased where a one-sided mean would lean inward.
    /// </summary>
    public SKPoint? Lookup(SKPoint camera)
    {
        var fx = camera.X - 0.5f;
        var fy = camera.Y - 0.5f;
        var x0 = (int)Math.Floor(fx);
        var y0 = (int)Math.Floor(fy);
        if (!IsValid(x0, y0) || !IsValid(x0 + 1, y0) || !IsValid(x0, y0 + 1) || !IsValid(x0 + 1, y0 + 1)) return null;
        var tx = fx - x0;
        var ty = fy - y0;
        SKPoint P(int x, int y)
        {
            var k = y * Width + x;
            return new SKPoint(Px[k] + 0.5f, Py[k] + 0.5f);
        }
        var a = P(x0, y0);
        var b = P(x0 + 1, y0);
        var c = P(x0, y0 + 1);
        var d = P(x0 + 1, y0 + 1);
        var top = new SKPoint(a.X + (b.X - a.X) * tx, a.Y + (b.Y - a.Y) * tx);
        var bottom = new SKPoint(c.X + (d.X - c.X) * tx, c.Y + (d.Y - c.Y) * tx);
        return new SKPoint(top.X + (bottom.X - top.X) * ty, top.Y + (bottom.Y - top.Y) * ty);
    }

    /// <summary>Whether this projector lit any camera pixel within a reach of a point.</summary>
    public bool AnyNear(SKPoint camera, int radius)
    {
        var cx = (int)Math.Round(camera.X);
        var cy = (int)Math.Round(camera.Y);
        for (var y = cy - radius; y <= cy + radius; y++)
        {
            for (var x = cx - radius; x <= cx + radius; x++)
            {
                if (IsValid(x, y)) return true;
            }
        }
        return false;
    }
}

/// <summary>A 3×3 in doubles for the fits: multiply, invert, map — SkiaSharp's floats are for the drawing.</summary>
public readonly struct Homography
{
    public readonly double[] M; // row-major, 9

    public Homography(double[] m) => M = m;

    public static Homography Identity => new(new double[] { 1, 0, 0, 0, 1, 0, 0, 0, 1 });

    public SKPoint Map(SKPoint p)
    {
        var w = M[6] * p.X + M[7] * p.Y + M[8];
        if (Math.Abs(w) < 1e-12) w = 1e-12;
        return new SKPoint((float)((M[0] * p.X + M[1] * p.Y + M[2]) / w), (float)((M[3] * p.X + M[4] * p.Y + M[5]) / w));
    }

    public Homography Then(Homography next) => Multiply(next, this);

    public static Homography Multiply(Homography a, Homography b)
    {
        var r = new double[9];
        for (var i = 0; i < 3; i++)
        {
            for (var j = 0; j < 3; j++)
            {
                r[i * 3 + j] = a.M[i * 3] * b.M[j] + a.M[i * 3 + 1] * b.M[3 + j] + a.M[i * 3 + 2] * b.M[6 + j];
            }
        }
        return new Homography(r);
    }

    public Homography Inverse()
    {
        var m = M;
        var det = m[0] * (m[4] * m[8] - m[5] * m[7]) - m[1] * (m[3] * m[8] - m[5] * m[6]) + m[2] * (m[3] * m[7] - m[4] * m[6]);
        if (Math.Abs(det) < 1e-18) return Identity;
        var r = new double[9];
        r[0] = (m[4] * m[8] - m[5] * m[7]) / det;
        r[1] = (m[2] * m[7] - m[1] * m[8]) / det;
        r[2] = (m[1] * m[5] - m[2] * m[4]) / det;
        r[3] = (m[5] * m[6] - m[3] * m[8]) / det;
        r[4] = (m[0] * m[8] - m[2] * m[6]) / det;
        r[5] = (m[2] * m[3] - m[0] * m[5]) / det;
        r[6] = (m[3] * m[7] - m[4] * m[6]) / det;
        r[7] = (m[1] * m[6] - m[0] * m[7]) / det;
        r[8] = (m[0] * m[4] - m[1] * m[3]) / det;
        return new Homography(r);
    }

    /// <summary>The homography taking the unit square's corners (0,0), (1,0), (1,1), (0,1) onto a quad.</summary>
    public static Homography UnitSquareTo(SKPoint tl, SKPoint tr, SKPoint br, SKPoint bl)
    {
        var sk = WarpMath.QuadWarp(1, 1, tl, tr, bl, br);
        return new Homography(new double[] { sk.ScaleX, sk.SkewX, sk.TransX, sk.SkewY, sk.ScaleY, sk.TransY, sk.Persp0, sk.Persp1, sk.Persp2 });
    }

    /// <summary>
    /// The least-squares homography from one set of points onto another (four or more pairs), the
    /// direct linear transform with both sets normalised first so the numbers stay sane.
    /// </summary>
    public static Homography Fit(IReadOnlyList<(SKPoint From, SKPoint To)> pairs)
    {
        if (pairs.Count < 4) return Identity;
        var tf = Normaliser(pairs.Select(p => p.From).ToList());
        var tt = Normaliser(pairs.Select(p => p.To).ToList());
        var ata = new double[8, 8];
        var atb = new double[8];
        foreach (var (from, to) in pairs)
        {
            var f = tf.Map(from);
            var t = tt.Map(to);
            double x = f.X, y = f.Y, X = t.X, Y = t.Y;
            var r1 = new[] { x, y, 1, 0, 0, 0, -X * x, -X * y };
            var r2 = new[] { 0, 0, 0, x, y, 1, -Y * x, -Y * y };
            for (var i = 0; i < 8; i++)
            {
                for (var j = 0; j < 8; j++) ata[i, j] += r1[i] * r1[j] + r2[i] * r2[j];
                atb[i] += r1[i] * X + r2[i] * Y;
            }
        }
        var h = Solve(ata, atb);
        if (h is null) return Identity;
        var normalised = new Homography(new[] { h[0], h[1], h[2], h[3], h[4], h[5], h[6], h[7], 1 });
        // from → normalised from → normalised to → to
        return Multiply(tt.Inverse(), Multiply(normalised, tf));
    }

    private static Homography Normaliser(IReadOnlyList<SKPoint> points)
    {
        double cx = points.Average(p => p.X), cy = points.Average(p => p.Y);
        var mean = points.Average(p => Math.Sqrt((p.X - cx) * (p.X - cx) + (p.Y - cy) * (p.Y - cy)));
        var s = mean < 1e-9 ? 1 : Math.Sqrt(2) / mean;
        return new Homography(new[] { s, 0, -s * cx, 0, s, -s * cy, 0, 0, 1 });
    }

    private static double[]? Solve(double[,] a, double[] b)
    {
        var n = b.Length;
        var m = new double[n, n + 1];
        for (var i = 0; i < n; i++)
        {
            for (var j = 0; j < n; j++) m[i, j] = a[i, j];
            m[i, n] = b[i];
        }
        for (var col = 0; col < n; col++)
        {
            var pivot = col;
            for (var r = col + 1; r < n; r++) if (Math.Abs(m[r, col]) > Math.Abs(m[pivot, col])) pivot = r;
            if (Math.Abs(m[pivot, col]) < 1e-14) return null;
            if (pivot != col)
            {
                for (var j = 0; j <= n; j++) (m[col, j], m[pivot, j]) = (m[pivot, j], m[col, j]);
            }
            for (var r = 0; r < n; r++)
            {
                if (r == col) continue;
                var f = m[r, col] / m[col, col];
                if (f == 0) continue;
                for (var j = col; j <= n; j++) m[r, j] -= f * m[col, j];
            }
        }
        var x = new double[n];
        for (var i = 0; i < n; i++) x[i] = m[i, n] / m[i, i];
        return x;
    }
}

/// <summary>One projector as the camera saw it: its screen, its raster, and which of its pixels lit which camera pixel.</summary>
public sealed record ProjectorSample(string ScreenId, string Name, int Width, int Height, Correspondence Map);

/// <summary>The canvas in the camera's picture: where its four corners are — the picture the room should see, square to the camera.</summary>
public sealed record CanvasQuad(SKPoint TL, SKPoint TR, SKPoint BR, SKPoint BL)
{
    public Homography FromUnit => Homography.UnitSquareTo(TL, TR, BR, BL);

    public static CanvasQuad Rect(float left, float top, float right, float bottom)
        => new(new SKPoint(left, top), new SKPoint(right, top), new SKPoint(right, bottom), new SKPoint(left, bottom));
}

/// <summary>One projector solved: where its raster sits in the canvas, the mesh that puts the canvas on the wall through it, its blend mask in its own raster, and the words.</summary>
public sealed record ProjectorSolution(string ScreenId, string Name, int X, int Y, int MeshColumns, int MeshRows, string Mesh, GreyFrame BlendMask, double CanvasCoverage, double FitResidualPx, int LocalNodes, string Words);

/// <summary>Everything the calibration made: the canvas's size in pixels, each projector's solution, and the report the page and the assistant read.</summary>
public sealed record CalibrationSolution(int CanvasWidth, int CanvasHeight, CanvasQuad Canvas, IReadOnlyList<ProjectorSolution> Projectors, string Report);

/// <summary>
/// The solver, pure. From what the camera saw of each projector: a homography camera → projector
/// per projector (the whole-picture fit, robust to a few bad pixels), the canvas's pixel size (as
/// the tightest projector measures it, so no share is wider than a raster), each projector's place in the canvas (the box its
/// coverage makes there), its mesh (each lattice point's content, a canvas point, mapped through
/// the camera to the projector pixel that lights it — the decoded pixel where the camera saw one,
/// the fit elsewhere), and its blend mask (in the camera's picture, each projector's weight is its
/// distance into its own coverage against every projector's, so the light adds to one across every
/// overlap and fades to nothing at every edge; carried back into the projector's raster).
/// </summary>
public static class Calibrator
{
    /// <summary>The canvas as the union of everything the projectors reach, square to the camera.</summary>
    public static CanvasQuad AutoCanvas(IReadOnlyList<ProjectorSample> samples)
    {
        var box = SKRectI.Empty;
        foreach (var s in samples)
        {
            var b = s.Map.Bounds;
            if (b.IsEmpty) continue;
            box = box.IsEmpty ? b : SKRectI.Union(box, b);
        }
        return box.IsEmpty ? CanvasQuad.Rect(0, 0, 1, 1) : CanvasQuad.Rect(box.Left, box.Top, box.Right, box.Bottom);
    }

    public static CalibrationSolution Solve(IReadOnlyList<ProjectorSample> samples, CanvasQuad canvas, int meshColumns = 9, int meshRows = 9, int maskWidth = 480, int maskHeight = 270, double blendPower = 2)
    {
        meshColumns = WarpGrid.ClampSize(meshColumns);
        meshRows = WarpGrid.ClampSize(meshRows);
        var toCamera = canvas.FromUnit;            // unit canvas → camera
        var toUnit = toCamera.Inverse();           // camera → unit canvas
        var fits = samples.Select(s => (Sample: s, Fit: FitProjector(s))).ToList();

        // The canvas's pixel size: the canvas as the tightest projector measures it, so no projector's
        // share of the canvas is wider than its raster — a share wider than the raster would be cut
        // off at the raster's edge before its blend had faded, and the room would see the cut.
        double canvasW = double.MaxValue, canvasH = double.MaxValue;
        foreach (var (s, fit) in fits)
        {
            if (fit is null) continue;
            var tl = fit.Value.Fit.Map(canvas.TL);
            var tr = fit.Value.Fit.Map(canvas.TR);
            var bl = fit.Value.Fit.Map(canvas.BL);
            canvasW = Math.Min(canvasW, Distance(tl, tr));
            canvasH = Math.Min(canvasH, Distance(tl, bl));
        }
        if (canvasW == double.MaxValue) canvasW = 0;
        if (canvasH == double.MaxValue) canvasH = 0;
        var cw = Math.Max(160, (int)Math.Round(canvasW));
        var ch = Math.Max(90, (int)Math.Round(canvasH));

        // Distances into each projector's coverage, in the camera's picture, for the blend.
        var distances = samples.Select(s => DistanceInto(s.Map)).ToList();

        var solutions = new List<ProjectorSolution>();
        for (var index = 0; index < fits.Count; index++)
        {
            var (s, fit) = fits[index];
            if (fit is null)
            {
                solutions.Add(new ProjectorSolution(s.ScreenId, s.Name, 0, 0, meshColumns, meshRows, "", new GreyFrame(maskWidth, maskHeight), 0, double.NaN, 0,
                    $"{s.Name}: the camera saw too little of it to fit — is it on, and in the camera's view?"));
                continue;
            }
            var (cameraToProjector, residual) = fit.Value;
            var projectorToCamera = cameraToProjector.Inverse();

            // Where the raster's corners land in the canvas: its box there is its place in the arrangement.
            var corners = new[] { new SKPoint(0, 0), new SKPoint(s.Width, 0), new SKPoint(s.Width, s.Height), new SKPoint(0, s.Height) }
                .Select(p => projectorToCamera.Map(p)).Select(c => toUnit.Map(c)).Select(u => new SKPoint(u.X * cw, u.Y * ch)).ToList();
            var x0 = (int)Math.Round(corners.Min(p => p.X));
            var y0 = (int)Math.Round(corners.Min(p => p.Y));
            var coverage = Math.Clamp((corners.Max(p => p.X) - corners.Min(p => p.X)) * (corners.Max(p => p.Y) - corners.Min(p => p.Y)) / ((double)cw * ch), 0, 1);

            // The mesh: each lattice point's content is the canvas pixel at its rest in the raster's box.
            var offsets = new float[meshColumns * meshRows * 2];
            var local = 0;
            for (var j = 0; j < meshRows; j++)
            {
                for (var i = 0; i < meshColumns; i++)
                {
                    var rest = WarpGrid.Rest(i, j, meshColumns, meshRows, s.Width, s.Height);
                    var unit = new SKPoint((x0 + rest.X) / cw, (y0 + rest.Y) / ch);
                    var camera = toCamera.Map(unit);
                    var fitted = cameraToProjector.Map(camera);
                    var target = fitted;
                    if (s.Map.Lookup(camera) is { } seen && Distance(seen, fitted) < 60)
                    {
                        target = seen;
                        local++;
                    }
                    var k = (j * meshColumns + i) * 2;
                    offsets[k] = target.X - rest.X;
                    offsets[k + 1] = target.Y - rest.Y;
                }
            }

            // The blend mask over the raster: each raster point's canvas point, its camera point, its weight against every projector.
            var mask = new GreyFrame(maskWidth, maskHeight);
            for (var my = 0; my < maskHeight; my++)
            {
                for (var mx = 0; mx < maskWidth; mx++)
                {
                    var rx = (mx + 0.5f) * s.Width / maskWidth;
                    var ry = (my + 0.5f) * s.Height / maskHeight;
                    var unit = new SKPoint((x0 + rx) / cw, (y0 + ry) / ch);
                    var camera = toCamera.Map(unit);
                    var weight = Weight(index, camera, distances, samples, blendPower);
                    mask.Pixels[my * maskWidth + mx] = (byte)Math.Clamp(Math.Round(weight * 255), 0, 255);
                }
            }

            var words = $"{s.Name}: covers {coverage * 100:0}% of the canvas, sits at {x0},{y0}; the fit is {residual:0.0} px {(residual < 1.5 ? "(excellent)" : residual < 4 ? "(good)" : "(loose — a curved surface, or the camera moved)")}; {local} of {meshColumns * meshRows} lattice points from what the camera saw.";
            solutions.Add(new ProjectorSolution(s.ScreenId, s.Name, x0, y0, meshColumns, meshRows, WarpGrid.Format(offsets), mask, coverage, residual, local, words));
        }

        var report = new StringBuilder();
        report.Append("Camera calibration: a canvas of ").Append(cw).Append('×').Append(ch).Append(" from the camera's view, ").Append(samples.Count).Append(samples.Count == 1 ? " projector" : " projectors").AppendLine(".");
        foreach (var sol in solutions) report.Append("  ").AppendLine(sol.Words);
        for (var a = 0; a < samples.Count; a++)
        {
            for (var b = a + 1; b < samples.Count; b++)
            {
                var overlap = OverlapPixels(samples[a].Map, samples[b].Map);
                if (overlap > 0) report.Append("  ").Append(samples[a].Name).Append(" and ").Append(samples[b].Name).Append(" overlap over ").Append(overlap).AppendLine(" camera pixels; the blend runs there.");
            }
        }
        var unlit = UnlitCorners(samples, canvas);
        if (unlit.Count > 0) report.Append("  The canvas's ").Append(string.Join(" and ", unlit)).AppendLine(" reach no projector: shrink the canvas, or move a projector.");
        report.Append("  Apply puts each projector in its place with its mesh and its blend mask; the arrangement's own zones are off for them — the masks blend.");
        return new CalibrationSolution(cw, ch, canvas, solutions, report.ToString());
    }

    /// <summary>The homography camera → projector from a projector's correspondence, and its residual in projector pixels; null when too little was seen.</summary>
    public static (Homography Fit, double ResidualPx)? FitProjector(ProjectorSample s)
    {
        var pairs = new List<(SKPoint, SKPoint)>();
        var step = Math.Max(1, (int)Math.Sqrt(s.Map.Width * s.Map.Height / 2000.0));
        for (var y = 0; y < s.Map.Height; y += step)
        {
            for (var x = 0; x < s.Map.Width; x += step)
            {
                if (!s.Map.IsValid(x, y)) continue;
                var k = y * s.Map.Width + x;
                pairs.Add((new SKPoint(x + 0.5f, y + 0.5f), new SKPoint(s.Map.Px[k] + 0.5f, s.Map.Py[k] + 0.5f)));
            }
        }
        if (pairs.Count < 16) return null;
        var fit = Homography.Fit(pairs);
        // One pass of pruning: the worst tenth (a stripe's edge, a reflection) out, then the fit again.
        var errors = pairs.Select(p => Distance(fit.Map(p.Item1), p.Item2)).ToList();
        var cut = errors.OrderBy(e => e).ElementAt((int)(errors.Count * 0.9));
        var kept = pairs.Where((p, i) => errors[i] <= cut).ToList();
        if (kept.Count >= 16) fit = Homography.Fit(kept);
        var residual = kept.Count > 0 ? kept.Average(p => Distance(fit.Map(p.Item1), p.Item2)) : errors.Average();
        return (fit, residual);
    }

    /// <summary>How far into a projector's coverage each camera pixel is, in camera pixels — a chamfer distance, 0 outside.</summary>
    public static float[] DistanceInto(Correspondence map)
    {
        var w = map.Width;
        var h = map.Height;
        var d = new float[w * h];
        const float far = 1e9f;
        for (var k = 0; k < d.Length; k++) d[k] = map.Valid[k] ? far : 0;
        // Outside the picture counts as outside the coverage.
        for (var y = 0; y < h; y++)
        {
            for (var x = 0; x < w; x++)
            {
                var k = y * w + x;
                if (d[k] == 0) continue;
                var best = d[k];
                if (x == 0 || y == 0) best = Math.Min(best, 1);
                if (x > 0) best = Math.Min(best, d[k - 1] + 1);
                if (y > 0) best = Math.Min(best, d[k - w] + 1);
                if (x > 0 && y > 0) best = Math.Min(best, d[k - w - 1] + 1.4142f);
                if (x < w - 1 && y > 0) best = Math.Min(best, d[k - w + 1] + 1.4142f);
                d[k] = best;
            }
        }
        for (var y = h - 1; y >= 0; y--)
        {
            for (var x = w - 1; x >= 0; x--)
            {
                var k = y * w + x;
                if (d[k] == 0) continue;
                var best = d[k];
                if (x == w - 1 || y == h - 1) best = Math.Min(best, 1);
                if (x < w - 1) best = Math.Min(best, d[k + 1] + 1);
                if (y < h - 1) best = Math.Min(best, d[k + w] + 1);
                if (x < w - 1 && y < h - 1) best = Math.Min(best, d[k + w + 1] + 1.4142f);
                if (x > 0 && y < h - 1) best = Math.Min(best, d[k + w - 1] + 1.4142f);
                d[k] = best;
            }
        }
        return d;
    }

    /// <summary>Projector <paramref name="index"/>'s share of the light at a camera point: its distance into its coverage to the power, over everyone's.</summary>
    public static double Weight(int index, SKPoint camera, IReadOnlyList<float[]> distances, IReadOnlyList<ProjectorSample> samples, double power)
    {
        var x = (int)Math.Round(camera.X);
        var y = (int)Math.Round(camera.Y);
        double mine = 0, all = 0;
        for (var i = 0; i < samples.Count; i++)
        {
            var map = samples[i].Map;
            if (x < 0 || y < 0 || x >= map.Width || y >= map.Height) continue;
            var d = Math.Pow(distances[i][y * map.Width + x], power);
            all += d;
            if (i == index) mine = d;
        }
        return all <= 0 ? 0 : mine / all;
    }

    private static int OverlapPixels(Correspondence a, Correspondence b)
    {
        if (a.Width != b.Width || a.Height != b.Height) return 0;
        var n = 0;
        for (var k = 0; k < a.Valid.Length; k++) if (a.Valid[k] && b.Valid[k]) n++;
        return n;
    }

    private static List<string> UnlitCorners(IReadOnlyList<ProjectorSample> samples, CanvasQuad canvas)
    {
        // The auto canvas is the box around the light, and a keystoned picture's corner sits a
        // few camera pixels inside that box: a corner is "reached" when light lies within a
        // couple of percent of the canvas's size — a projector's edge, not a projector's absence.
        var w = Math.Max(Math.Abs(canvas.TR.X - canvas.TL.X), Math.Abs(canvas.BR.X - canvas.BL.X));
        var h = Math.Max(Math.Abs(canvas.BL.Y - canvas.TL.Y), Math.Abs(canvas.BR.Y - canvas.TR.Y));
        var radius = Math.Max(8, (int)Math.Round(0.03 * Math.Min(w, h)));
        var unlit = new List<string>();
        foreach (var (name, point) in new[] { ("top-left", canvas.TL), ("top-right", canvas.TR), ("bottom-right", canvas.BR), ("bottom-left", canvas.BL) })
        {
            var lit = samples.Any(s => s.Map.AnyNear(point, radius));
            if (!lit) unlit.Add(name + " corner");
        }
        return unlit;
    }

    private static double Distance(SKPoint a, SKPoint b) => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}

/// <summary>
/// A room that is not there: projectors whose pictures land on the camera through homographies
/// you choose. It shows the solver a run without a projector or a camera — the tests, and a
/// try-it-out on the page — and it says what the right answer is, so the solver is checked
/// against the truth and not against itself.
/// </summary>
public sealed class CalibrationSimulator
{
    private readonly List<(string Id, string Name, int Width, int Height, Homography ToCamera)> _projectors = new();

    public CalibrationSimulator(int cameraWidth, int cameraHeight)
    {
        CameraWidth = cameraWidth;
        CameraHeight = cameraHeight;
    }

    public int CameraWidth { get; }
    public int CameraHeight { get; }

    /// <summary>A projector whose raster corners land at these camera points.</summary>
    public CalibrationSimulator AddProjector(string id, string name, int width, int height, SKPoint tl, SKPoint tr, SKPoint br, SKPoint bl)
    {
        var unit = Homography.UnitSquareTo(tl, tr, br, bl);
        var rasterToUnit = new Homography(new[] { 1.0 / width, 0, 0, 0, 1.0 / height, 0, 0, 0, 1 });
        _projectors.Add((id, name, width, height, Homography.Multiply(unit, rasterToUnit)));
        return this;
    }

    public int Count => _projectors.Count;

    public (string Id, string Name, int Width, int Height) ProjectorAt(int index) => (_projectors[index].Id, _projectors[index].Name, _projectors[index].Width, _projectors[index].Height);

    /// <summary>The projector's raster point → the camera's point.</summary>
    public Homography ToCamera(int index) => _projectors[index].ToCamera;

    /// <summary>What the camera sees with one projector showing a pattern and the rest dark: 200 lit, 40 unlit inside a picture, 20 elsewhere.</summary>
    public GreyFrame Frame(int index, in CalPattern pattern)
    {
        var (_, _, w, h, toCamera) = _projectors[index];
        var toRaster = toCamera.Inverse();
        var frame = new GreyFrame(CameraWidth, CameraHeight);
        for (var y = 0; y < CameraHeight; y++)
        {
            for (var x = 0; x < CameraWidth; x++)
            {
                var p = toRaster.Map(new SKPoint(x + 0.5f, y + 0.5f));
                var px = (int)Math.Floor(p.X);
                var py = (int)Math.Floor(p.Y);
                byte v = 20;
                if (px >= 0 && py >= 0 && px < w && py < h) v = GrayCode.IsLit(pattern, px, py) ? (byte)200 : (byte)40;
                frame.Pixels[y * CameraWidth + x] = v;
            }
        }
        return frame;
    }

    /// <summary>The truth: which raster pixel lights each camera pixel.</summary>
    public Correspondence Analytic(int index)
    {
        var (_, _, w, h, toCamera) = _projectors[index];
        var toRaster = toCamera.Inverse();
        var map = new Correspondence(CameraWidth, CameraHeight, w, h);
        for (var y = 0; y < CameraHeight; y++)
        {
            for (var x = 0; x < CameraWidth; x++)
            {
                var p = toRaster.Map(new SKPoint(x + 0.5f, y + 0.5f));
                var px = (int)Math.Floor(p.X);
                var py = (int)Math.Floor(p.Y);
                if (px >= 0 && py >= 0 && px < w && py < h) map.Set(x, y, px, py);
            }
        }
        return map;
    }

    /// <summary>Every projector run through the sequence and decoded — the samples the solver takes.</summary>
    public IReadOnlyList<ProjectorSample> Samples()
    {
        var samples = new List<ProjectorSample>();
        for (var i = 0; i < _projectors.Count; i++)
        {
            var (id, name, w, h, _) = _projectors[i];
            var sequence = GrayCode.Sequence(w, h);
            var frames = sequence.Select(p => Frame(i, p)).ToList();
            samples.Add(new ProjectorSample(id, name, w, h, GrayCode.Decode(sequence, frames, w, h)));
        }
        return samples;
    }
}
