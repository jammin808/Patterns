using Patterns.Core.Model;
using Patterns.Core.Rendering;
using SkiaSharp;

namespace Patterns.Core.Patterns;

public static class BlendMath
{
    /// <summary>Fade-in weight (0→1) across a blend zone for the selected curve. Monotonic, 0↦0, 1↦1.</summary>
    public static double Curve(BlendCurve curve, double t)
    {
        t = Math.Clamp(t, 0, 1);
        return curve switch
        {
            BlendCurve.Cosine => 0.5 - 0.5 * Math.Cos(Math.PI * t),
            BlendCurve.SCurve => t * t * (3 - 2 * t),
            BlendCurve.Gamma22 => Math.Pow(t, 2.2),
            _ => t,
        };
    }

    /// <summary>
    /// The light an output should show <paramref name="t"/> of the way into its blend zone (0 at
    /// the outer edge, 1 where its full picture begins), as the signal to send: the curve raised
    /// to 1/gamma, so two projectors with that gamma add up to flat light across the join. Gamma 1
    /// is the raw curve — what the Projection blend pattern's ramps show.
    /// </summary>
    public static double Weight(BlendCurve curve, double t, double gamma)
    {
        var w = Curve(curve, t);
        gamma = Math.Clamp(gamma, 0.5, 3.0);
        return Math.Abs(gamma - 1.0) < 1e-6 ? w : Math.Pow(w, 1.0 / gamma);
    }
}

/// <summary>
/// Edge-blend setup pattern: a continuous alignment grid over the whole canvas, hue-coded
/// projector regions, hatched overlap zones with centerlines, and the blend curve drawn as
/// opposing fade ramps inside each zone — along the row, and across it for a grid of rows.
/// GrayCheck mode fills flat 50% grey to expose double brightness where a blend is off.
/// </summary>
public sealed class BlendPattern : IPatternRenderer
{
    public void Render(SKCanvas c, in PatternFrame f)
    {
        var o = f.Config.Blend;
        var layout = CanvasResolver.Blend(o);
        var pc = f.Paints;
        int w = f.W, h = f.H;
        var horizontal = layout.Orientation == BlendOrientation.Horizontal;
        var rows = layout.Rows;

        if (o.GrayCheck)
        {
            c.Clear(new SKColor(128, 128, 128));
            // Only zone-edge ticks — anything else would defeat the check.
            var tick = pc.Fill(SKColors.Black);
            for (var i = 1; i < layout.Projectors; i++)
            {
                var z0 = layout.OriginOf(i);
                var z1 = layout.OriginOf(i - 1) + layout.AxisNative;
                DrawTick(c, tick, z0, horizontal, w, h);
                DrawTick(c, tick, z1 - 1, horizontal, w, h);
            }
            for (var j = 1; j < rows; j++)
            {
                var z0 = layout.OriginAcrossOf(j);
                var z1 = layout.OriginAcrossOf(j - 1) + layout.AcrossNative;
                DrawTick(c, tick, z0, !horizontal, w, h);
                DrawTick(c, tick, z1 - 1, !horizontal, w, h);
            }
            if (o.ShowInfo) InfoChip(c, in f, layout);
            return;
        }

        c.Clear(SKColors.Black);

        // Continuous alignment grid across the whole canvas — geometry must line up through zones.
        if (o.ShowGrids)
        {
            var grid = pc.Fill(new SKColor(0xFF, 0xFF, 0xFF, 0x64));
            var cell = Math.Max(8, o.GridSize);
            for (var x = (w / 2) % cell; x < w; x += cell)
            {
                DrawUtil.LineV(c, x, 0, h, 1, grid);
            }
            for (var y = (h / 2) % cell; y < h; y += cell)
            {
                DrawUtil.LineH(c, y, 0, w, 1, grid);
            }
        }

        // Per-projector dashed frame + label, hue-coded — every projector of every row.
        for (var j = 0; j < rows; j++)
        {
            for (var i = 0; i < layout.Projectors; i++)
            {
                var n = layout.NumberOf(i, j);
                var color = o.HueCode ? DrawUtil.Hue(n - 1, layout.Count) : f.Palette.Accent;
                var a0 = layout.OriginOf(i);
                var a1 = a0 + layout.AxisNative;
                var b0 = layout.OriginAcrossOf(j);
                var b1 = b0 + layout.AcrossNative;
                var rect = horizontal
                    ? SKRect.Create(a0 + 1.5f, b0 + 1.5f, a1 - a0 - 3, b1 - b0 - 3)
                    : SKRect.Create(b0 + 1.5f, a0 + 1.5f, b1 - b0 - 3, a1 - a0 - 3);
                c.DrawRect(rect, pc.StrokeAA(color, 3, DrawUtil.DashLong));

                var font = pc.FontBold;
                font.Size = Math.Clamp(Math.Min(rect.Width, rect.Height) * 0.08f, 14, 90);
                var cxP = rect.MidX;
                var cyP = rect.Top + rect.Height * 0.14f;
                DrawUtil.TextCentered(c, $"P{n}", cxP + 2, cyP + 2, font, pc.Text(SKColors.Black));
                DrawUtil.TextCentered(c, $"P{n}", cxP, cyP, font, pc.Text(color));
            }
        }

        // Overlap zones along the row — the full height (or width) of the canvas, through every row.
        for (var i = 1; i < layout.Projectors; i++)
        {
            var z0 = layout.OriginOf(i);                                // right/lower proj start
            var z1 = layout.OriginOf(i - 1) + layout.AxisNative;        // left/upper proj end
            DrawZone(c, in f, o, z0, z1, horizontal, w, h);
        }

        // Overlap zones across the rows — the full length of the canvas, through every column.
        for (var j = 1; j < rows; j++)
        {
            var z0 = layout.OriginAcrossOf(j);
            var z1 = layout.OriginAcrossOf(j - 1) + layout.AcrossNative;
            DrawZone(c, in f, o, z0, z1, !horizontal, w, h);
        }

        if (o.ShowInfo) InfoChip(c, in f, layout);
    }

    /// <summary>One overlap zone: hatched, its edges and centreline marked, the curve as ramps, its width named.</summary>
    private static void DrawZone(SKCanvas c, in PatternFrame f, BlendOptions o, int z0, int z1, bool horizontal, int w, int h)
    {
        var pc = f.Paints;
        var zoneRect = horizontal
            ? SKRect.Create(z0, 0, z1 - z0, h)
            : SKRect.Create(0, z0, w, z1 - z0);

        DrawUtil.Hatch(c, zoneRect, 24, pc.StrokeAA(new SKColor(0xFF, 0xFF, 0xFF, 0x2A), 1));

        if (o.ShowMarkers)
        {
            var edge = pc.Fill(f.Palette.Accent);
            if (horizontal)
            {
                DrawUtil.LineV(c, z0, 0, h, 1, edge);
                DrawUtil.LineV(c, z1 - 1, 0, h, 1, edge);
                var mid = (z0 + z1) / 2;
                c.DrawLine(mid + 0.5f, 0, mid + 0.5f, h, pc.StrokeAA(f.Palette.Secondary, 1, DrawUtil.DashShort));
            }
            else
            {
                DrawUtil.LineH(c, z0, 0, w, 1, edge);
                DrawUtil.LineH(c, z1 - 1, 0, w, 1, edge);
                var mid = (z0 + z1) / 2;
                c.DrawLine(0, mid + 0.5f, w, mid + 0.5f, pc.StrokeAA(f.Palette.Secondary, 1, DrawUtil.DashShort));
            }
        }

        if (o.ShowRamps)
        {
            DrawRamps(c, pc, o.Curve, z0, z1, horizontal, w, h);
        }

        var zfont = pc.FontRegular;
        zfont.Size = Math.Clamp(Math.Min(w, h) * 0.022f, 10, 30);
        var label = $"OVERLAP {z1 - z0}px";
        var lx = horizontal ? (z0 + z1) / 2f : w / 2f;
        var ly = horizontal ? h * 0.94f : (z0 + z1) / 2f;
        DrawUtil.TextCentered(c, label, lx + 1, ly + 1, zfont, pc.Text(SKColors.Black));
        DrawUtil.TextCentered(c, label, lx, ly, zfont, pc.Text(f.Palette.Text));
    }

    /// <summary>
    /// Opposing fade ramps: upper strip = fade-out of the earlier projector, lower strip =
    /// fade-in of the later one, both following the selected curve. Drawn per pixel column —
    /// blend is a static pattern, rendered once.
    /// </summary>
    private static void DrawRamps(SKCanvas c, PaintCache pc, BlendCurve curve, int z0, int z1, bool horizontal, int w, int h)
    {
        var zw = z1 - z0;
        if (zw <= 2) return;

        if (horizontal)
        {
            var stripH = (int)Math.Clamp(h * 0.1f, 8, 160);
            var yOut = (int)(h * 0.3f) - stripH;
            var yIn = (int)(h * 0.7f);
            for (var x = 0; x < zw; x++)
            {
                var t = (double)x / (zw - 1);
                var vIn = (byte)Math.Round(255 * BlendMath.Curve(curve, t));
                var vOut = (byte)Math.Round(255 * BlendMath.Curve(curve, 1 - t));
                DrawUtil.LineV(c, z0 + x, yOut, yOut + stripH, 1, pc.Fill(new SKColor(vOut, vOut, vOut)));
                DrawUtil.LineV(c, z0 + x, yIn, yIn + stripH, 1, pc.Fill(new SKColor(vIn, vIn, vIn)));
            }
        }
        else
        {
            var stripW = (int)Math.Clamp(w * 0.1f, 8, 160);
            var xOut = (int)(w * 0.3f) - stripW;
            var xIn = (int)(w * 0.7f);
            for (var y = 0; y < zw; y++)
            {
                var t = (double)y / (zw - 1);
                var vIn = (byte)Math.Round(255 * BlendMath.Curve(curve, t));
                var vOut = (byte)Math.Round(255 * BlendMath.Curve(curve, 1 - t));
                DrawUtil.LineH(c, z0 + y, xOut, xOut + stripW, 1, pc.Fill(new SKColor(vOut, vOut, vOut)));
                DrawUtil.LineH(c, z0 + y, xIn, xIn + stripW, 1, pc.Fill(new SKColor(vIn, vIn, vIn)));
            }
        }
    }

    private static void DrawTick(SKCanvas c, SKPaint paint, int pos, bool horizontal, int w, int h)
    {
        if (horizontal)
        {
            DrawUtil.LineV(c, pos, 0, (int)(h * 0.03f), 1, paint);
            DrawUtil.LineV(c, pos, h - (int)(h * 0.03f), h, 1, paint);
        }
        else
        {
            DrawUtil.LineH(c, pos, 0, (int)(w * 0.03f), 1, paint);
            DrawUtil.LineH(c, pos, w - (int)(w * 0.03f), w, 1, paint);
        }
    }

    private static void InfoChip(SKCanvas c, in PatternFrame f, BlendLayout layout)
    {
        var o = f.Config.Blend;
        var rig = layout.Rows > 1 ? $"{layout.Projectors}×{layout.Rows}" : $"{layout.Projectors}";
        var overlap = layout.Rows > 1 ? $"overlap {layout.Overlap}/{layout.OverlapAcross}px" : $"overlap {layout.Overlap}px";
        var text = $"{rig} × {o.NativeWidth}×{o.NativeHeight} · {overlap} · {o.Curve} · canvas {f.W}×{f.H}";
        DrawUtil.Chip(c, text, f.Canvas, Anchor9.TopCenter, GridPattern.ChipText(f), f.Paints,
            f.Palette.Text, f.Palette.ChipBg);
    }
}
