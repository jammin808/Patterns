using Patterns.Core.Model;
using Patterns.Core.Rendering;
using SkiaSharp;

namespace Patterns.Core.Patterns;

/// <summary>
/// Alignment grid. Lines are placed symmetrically about the canvas center (the reference
/// point riggers align to), drawn pixel-exact with antialiasing off.
/// </summary>
public sealed class GridPattern : IPatternRenderer
{
    public void Render(SKCanvas c, in PatternFrame f)
    {
        var o = f.Config.Grid;
        var pc = f.Paints;
        c.Clear(f.Palette.Bg);

        int w = f.W, h = f.H;
        int cx = w / 2, cy = h / 2;
        var cell = Math.Max(2, o.CellSize);
        var lw = Math.Max(1, o.LineWidth);

        // Minor subdivisions first (1px, subtle).
        if (o.Subdivisions > 0)
        {
            var sub = pc.Fill(f.Palette.SubtleLine);
            var step = (float)cell / (o.Subdivisions + 1);
            if (step >= 2)
            {
                for (float x = cx % step; x < w; x += step)
                {
                    DrawUtil.LineV(c, (int)MathF.Round(x), 0, h, 1, sub);
                }
                for (float y = cy % step; y < h; y += step)
                {
                    DrawUtil.LineH(c, (int)MathF.Round(y), 0, w, 1, sub);
                }
            }
        }

        // Major lines, symmetric about center.
        var line = pc.Fill(f.Palette.Line);
        for (var x = cx % cell; x < w; x += cell)
        {
            DrawUtil.LineV(c, x - lw / 2, 0, h, lw, line);
        }
        for (var y = cy % cell; y < h; y += cell)
        {
            DrawUtil.LineH(c, y - lw / 2, 0, w, lw, line);
        }

        if (o.ShowDiagonals)
        {
            var diag = pc.StrokeAA(f.Palette.SubtleLine, 1);
            c.DrawLine(0, 0, w, h, diag);
            c.DrawLine(w, 0, 0, h, diag);
        }

        if (o.ShowBorder)
        {
            DrawUtil.BorderInside(c, new SKRectI(0, 0, w, h), lw, pc.Fill(f.Palette.Accent));
        }

        if (o.ShowCenterCross)
        {
            DrawUtil.Cross(c, cx, cy, Math.Min(w, h) / 8, lw + 2, pc.Fill(f.Palette.Accent));
        }

        if (o.ShowLabel)
        {
            DrawUtil.Chip(c, $"{w} × {h}  ·  {cell} px grid", f.Canvas, Anchor9.BottomCenter,
                ChipText(f), pc, f.Palette.Text, f.Palette.ChipBg);
        }
    }

    internal static float ChipText(in PatternFrame f) => Math.Clamp(f.H * 0.024f, 11, 40);
}

/// <summary>Pixel checkerboard via a repeating 2×2 shader — fast at any canvas size.</summary>
public sealed class CheckerboardPattern : IPatternRenderer
{
    public void Render(SKCanvas c, in PatternFrame f)
    {
        var o = f.Config.Checker;
        var pc = f.Paints;

        var a = f.Palette.Branded ? f.Palette.Accent : SKColors.White;
        var b = f.Palette.Branded ? f.Palette.Secondary : SKColors.Black;

        var phase = o.Animate && ((long)(f.Ctx.Time / Math.Max(0.05, o.IntervalSeconds)) & 1) == 1;
        if (phase) (a, b) = (b, a);

        var shader = f.Sink.Checker.Get(a, b, Math.Max(1, o.CellSize));
        var paint = pc.Fill(SKColors.White);
        paint.Shader = shader;
        c.DrawRect(SKRect.Create(0, 0, f.W, f.H), paint);
        paint.Shader = null;
    }
}

/// <summary>Caches the checkerboard shader per sink; rebuilt only when colours/cell change.</summary>
public sealed class CheckerShaderCache : IDisposable
{
    private SKShader? _shader;
    private SKBitmap? _bitmap;
    private (SKColor A, SKColor B, int Cell) _key;

    public SKShader Get(SKColor a, SKColor b, int cell)
    {
        var key = (a, b, cell);
        if (_shader is not null && key == _key) return _shader;

        _shader?.Dispose();
        _bitmap?.Dispose();

        _bitmap = new SKBitmap(2, 2, SKColorType.Bgra8888, SKAlphaType.Premul);
        _bitmap.SetPixel(0, 0, a);
        _bitmap.SetPixel(1, 1, a);
        _bitmap.SetPixel(1, 0, b);
        _bitmap.SetPixel(0, 1, b);
        _shader = _bitmap.ToShader(
            SKShaderTileMode.Repeat, SKShaderTileMode.Repeat,
            DrawUtil.Nearest, SKMatrix.CreateScale(cell, cell));
        _key = key;
        return _shader;
    }

    public void Dispose()
    {
        _shader?.Dispose();
        _bitmap?.Dispose();
    }
}

/// <summary>Uniformity / level check field.</summary>
public sealed class FlatFieldPattern : IPatternRenderer
{
    public void Render(SKCanvas c, in PatternFrame f)
    {
        var o = f.Config.FlatField;
        var baseColor = f.Color(o.Color, SKColors.White);
        var k = (float)(o.LevelPct / 100.0);
        var color = new SKColor(
            (byte)MathF.Round(baseColor.Red * k),
            (byte)MathF.Round(baseColor.Green * k),
            (byte)MathF.Round(baseColor.Blue * k));
        c.Clear(color);

        var pc = f.Paints;
        if (o.ShowBorder)
        {
            var contrast = Luma(color) > 100 ? SKColors.Black : SKColors.White;
            DrawUtil.BorderInside(c, new SKRectI(0, 0, f.W, f.H), 1, pc.Fill(contrast));
        }

        if (o.ShowLabel)
        {
            var text = $"{Describe(baseColor)}  ·  {o.LevelPct:0}%";
            DrawUtil.Chip(c, text, f.Canvas, Anchor9.BottomCenter, GridPattern.ChipText(f), pc,
                SKColors.White, f.Palette.ChipBg);
        }
    }

    private static double Luma(SKColor c) => 0.2126 * c.Red + 0.7152 * c.Green + 0.0722 * c.Blue;

    private static string Describe(SKColor c) => (c.Red, c.Green, c.Blue) switch
    {
        (255, 255, 255) => "WHITE",
        (0, 0, 0) => "BLACK",
        (255, 0, 0) => "RED",
        (0, 255, 0) => "GREEN",
        (0, 0, 255) => "BLUE",
        (0, 255, 255) => "CYAN",
        (255, 0, 255) => "MAGENTA",
        (255, 255, 0) => "YELLOW",
        _ => $"#{c.Red:X2}{c.Green:X2}{c.Blue:X2}",
    };
}

/// <summary>Circles, crosshair, safe areas, aspect markers — projector/geometry setup.</summary>
public sealed class GeometryPattern : IPatternRenderer
{
    private static readonly (float Ratio, string Label)[] Aspects = { (4f / 3f, "4:3"), (1f, "1:1"), (2.39f, "2.39:1") };

    public void Render(SKCanvas c, in PatternFrame f)
    {
        var o = f.Config.Geometry;
        var pc = f.Paints;
        c.Clear(f.Palette.Bg);

        int w = f.W, h = f.H;
        float cx = w / 2f, cy = h / 2f;

        if (o.ShowDiagonals)
        {
            var diag = pc.StrokeAA(f.Palette.SubtleLine, 1);
            c.DrawLine(0, 0, w, h, diag);
            c.DrawLine(w, 0, 0, h, diag);
        }

        if (o.ShowCrosshair)
        {
            var line = pc.Fill(f.Palette.Line);
            DrawUtil.LineH(c, h / 2, 0, w, 1, line);
            DrawUtil.LineV(c, w / 2, 0, h, 1, line);
        }

        if (o.ShowCircles)
        {
            var stroke = pc.StrokeAA(f.Palette.Line, 2);
            var r = Math.Min(w, h) / 2f - 2;
            c.DrawCircle(cx, cy, r, stroke);
            c.DrawCircle(cx, cy, r / 2, stroke);
            var cr = Math.Min(w, h) / 8f;
            var corner = pc.StrokeAA(f.Palette.Accent, 2);
            c.DrawCircle(cr + 2, cr + 2, cr, corner);
            c.DrawCircle(w - cr - 2, cr + 2, cr, corner);
            c.DrawCircle(cr + 2, h - cr - 2, cr, corner);
            c.DrawCircle(w - cr - 2, h - cr - 2, cr, corner);
        }

        if (o.ShowSafeAreas)
        {
            DrawSafe(c, f, (float)(o.ActionSafePct / 100), "ACTION", f.Palette.Accent);
            DrawSafe(c, f, (float)(o.TitleSafePct / 100), "TITLE", f.Palette.Secondary);
        }

        if (o.ShowAspectMarkers)
        {
            foreach (var (ratio, label) in Aspects)
            {
                var aw = h * ratio;
                if (aw >= w - 2) continue;
                var x0 = (w - aw) / 2f;
                var stroke = pc.StrokeAA(f.Palette.SubtleLine, 1, DrawUtil.DashLong);
                c.DrawLine(x0, 0, x0, h, stroke);
                c.DrawLine(x0 + aw, 0, x0 + aw, h, stroke);
                var font = pc.FontRegular;
                font.Size = Math.Clamp(h * 0.02f, 10, 28);
                DrawUtil.TextCentered(c, label, x0 + aw / 2, font.Size * 1.2f, font, pc.Text(f.Palette.SubtleLine));
            }
        }

        DrawUtil.BorderInside(c, new SKRectI(0, 0, w, h), 1, pc.Fill(f.Palette.Line));
    }

    private static void DrawSafe(SKCanvas c, in PatternFrame f, float pct, string label, SKColor color)
    {
        int w = f.W, h = f.H;
        var sw = w * pct;
        var sh = h * pct;
        var rect = SKRect.Create((w - sw) / 2f, (h - sh) / 2f, sw, sh);
        var pc = f.Paints;
        c.DrawRect(rect, pc.StrokeAA(color, 1, DrawUtil.DashShort));
        var font = pc.FontRegular;
        font.Size = Math.Clamp(h * 0.018f, 9, 24);
        DrawUtil.TextLeft(c, $"{label} {pct * 100:0}%", rect.Left + 8, rect.Top + font.Size + 4, font, pc.Text(color));
    }
}
