using Patterns.Core.Model;
using Patterns.Core.Rendering;
using SkiaSharp;

namespace Patterns.Core.Patterns;

/// <summary>Grayscale/RGB ramps and stepped wedges for gamma, banding and bit-depth checks.</summary>
public sealed class RampPattern : IPatternRenderer
{
    public void Render(SKCanvas c, in PatternFrame f)
    {
        var o = f.Config.Ramp;
        var pc = f.Paints;
        c.Clear(SKColors.Black);
        int w = f.W, h = f.H;

        switch (o.Variant)
        {
            case RampVariant.GrayVertical:
                Gradient(c, pc, SKRect.Create(0, 0, w, h), SKColors.Black, SKColors.White, vertical: true);
                if (o.ShowMarkers) MarkersV(c, in f);
                break;

            case RampVariant.Rgb:
            {
                var band = h / 4f;
                Gradient(c, pc, SKRect.Create(0, 0 * band, w, band), SKColors.Black, SKColors.White, false);
                Gradient(c, pc, SKRect.Create(0, 1 * band, w, band), SKColors.Black, SKColors.Red, false);
                Gradient(c, pc, SKRect.Create(0, 2 * band, w, band), SKColors.Black, new SKColor(0, 255, 0), false);
                Gradient(c, pc, SKRect.Create(0, 3 * band, w, h - 3 * band), SKColors.Black, SKColors.Blue, false);
                if (o.ShowMarkers) MarkersH(c, in f);
                break;
            }

            case RampVariant.Steps:
            {
                var n = Math.Max(2, o.Steps);
                var font = pc.FontRegular;
                font.Size = Math.Clamp(h * 0.02f, 9, 22);
                for (var i = 0; i < n; i++)
                {
                    var v = (byte)Math.Round(i * 255.0 / (n - 1));
                    var x0 = (int)Math.Round(w * (double)i / n);
                    var x1 = (int)Math.Round(w * (double)(i + 1) / n);
                    c.DrawRect(SKRect.Create(x0, 0, x1 - x0, h), pc.Fill(new SKColor(v, v, v)));
                    if (o.ShowMarkers && n <= 32 && x1 - x0 > font.Size * 2.2f)
                    {
                        var text = pc.Text(v > 127 ? SKColors.Black : SKColors.White);
                        DrawUtil.TextCentered(c, v.ToString(), (x0 + x1) / 2f, h * 0.94f, font, text);
                    }
                }
                break;
            }

            default:
                Gradient(c, pc, SKRect.Create(0, 0, w, h), SKColors.Black, SKColors.White, vertical: false);
                if (o.ShowMarkers) MarkersH(c, in f);
                break;
        }
    }

    private static void Gradient(SKCanvas c, PaintCache pc, SKRect rect, SKColor from, SKColor to, bool vertical)
    {
        using var shader = SKShader.CreateLinearGradient(
            new SKPoint(rect.Left, rect.Top),
            vertical ? new SKPoint(rect.Left, rect.Bottom) : new SKPoint(rect.Right, rect.Top),
            new[] { from, to }, SKShaderTileMode.Clamp);
        var paint = pc.Fill(SKColors.White);
        paint.Shader = shader;
        c.DrawRect(rect, paint);
        paint.Shader = null;
    }

    private static void MarkersH(SKCanvas c, in PatternFrame f)
    {
        var pc = f.Paints;
        var font = pc.FontRegular;
        font.Size = Math.Clamp(f.H * 0.018f, 9, 20);
        for (var pct = 0; pct <= 100; pct += 10)
        {
            var x = (int)Math.Round((f.W - 1) * pct / 100.0);
            DrawUtil.LineV(c, x, 0, (int)(font.Size * 1.6f), 1, pc.Fill(new SKColor(255, 160, 32)));
            var tx = Math.Clamp(x, font.Size * 1.4f, f.W - font.Size * 1.4f);
            DrawUtil.TextCentered(c, pct.ToString(), tx, font.Size * 2.4f, font, pc.Text(new SKColor(255, 160, 32)));
        }
    }

    private static void MarkersV(SKCanvas c, in PatternFrame f)
    {
        var pc = f.Paints;
        var font = pc.FontRegular;
        font.Size = Math.Clamp(f.H * 0.018f, 9, 20);
        for (var pct = 0; pct <= 100; pct += 10)
        {
            var y = (int)Math.Round((f.H - 1) * pct / 100.0);
            DrawUtil.LineH(c, y, 0, (int)(font.Size * 1.6f), 1, pc.Fill(new SKColor(255, 160, 32)));
            var ty = Math.Clamp(y, font.Size, f.H - font.Size);
            DrawUtil.TextCentered(c, pct.ToString(), font.Size * 3f, ty, font, pc.Text(new SKColor(255, 160, 32)));
        }
    }
}

/// <summary>Siemens star, line pairs and type samples — focus and pixel-mapping checks.</summary>
public sealed class FocusPattern : IPatternRenderer
{
    public void Render(SKCanvas c, in PatternFrame f)
    {
        var o = f.Config.Focus;
        var pc = f.Paints;
        c.Clear(SKColors.Black);
        int w = f.W, h = f.H;
        float cx = w / 2f, cy = h / 2f;

        // Alternating 8px border hash (like camera charts) — instant crop check.
        var hash = pc.Fill(SKColors.White);
        const int hashLen = 8;
        for (var x = 0; x < w; x += hashLen * 2)
        {
            DrawUtil.LineH(c, 0, x, Math.Min(x + hashLen, w), 2, hash);
            DrawUtil.LineH(c, h - 2, x, Math.Min(x + hashLen, w), 2, hash);
        }
        for (var y = 0; y < h; y += hashLen * 2)
        {
            DrawUtil.LineV(c, 0, y, Math.Min(y + hashLen, h), 2, hash);
            DrawUtil.LineV(c, w - 2, y, Math.Min(y + hashLen, h), 2, hash);
        }

        if (o.ShowStar)
        {
            DrawSiemensStar(c, pc, cx, cy, Math.Min(w, h) * 0.33f, 72);
        }

        if (o.ShowLinePairs)
        {
            var block = Math.Min(w, h) / 6;
            DrawLinePairBlock(c, pc, 12, 12, block, vertical: true);
            DrawLinePairBlock(c, pc, w - 12 - block, 12, block, vertical: false);
            DrawLinePairBlock(c, pc, 12, h - 12 - block, block, vertical: false);
            DrawLinePairBlock(c, pc, w - 12 - block, h - 12 - block, block, vertical: true);
        }

        if (o.ShowText)
        {
            float y = h * 0.16f;
            var x = w * 0.05f;
            Span<int> sizes = stackalloc int[] { 10, 14, 20, 28, 40 };
            foreach (var size in sizes)
            {
                if (y > h * 0.8f) break;
                var font = pc.FontRegular;
                font.Size = size;
                DrawUtil.TextLeft(c, $"{size}px  The quick brown fox 0123456789", x, y, font, pc.Text(SKColors.White));
                y += size * 1.6f;
            }
        }

        DrawUtil.Chip(c, $"{w} × {h}", f.Canvas, Anchor9.BottomCenter, GridPattern.ChipText(f), pc,
            f.Palette.Text, f.Palette.ChipBg);
    }

    private static void DrawSiemensStar(SKCanvas c, PaintCache pc, float cx, float cy, float radius, int spokes)
    {
        var path = pc.ScratchPath;
        path.Reset();
        var paint = pc.FillAA(SKColors.White);
        for (var i = 0; i < spokes; i += 2)
        {
            var a0 = (float)(i * 2 * Math.PI / spokes);
            var a1 = (float)((i + 1) * 2 * Math.PI / spokes);
            path.MoveTo(cx, cy);
            path.LineTo(cx + radius * MathF.Cos(a0), cy + radius * MathF.Sin(a0));
            path.LineTo(cx + radius * MathF.Cos(a1), cy + radius * MathF.Sin(a1));
            path.Close();
        }
        c.DrawPath(path, paint);
        c.DrawCircle(cx, cy, radius * 0.02f + 2, pc.FillAA(SKColors.Black));
        path.Reset();
    }

    private static void DrawLinePairBlock(SKCanvas c, PaintCache pc, int x, int y, int size, bool vertical)
    {
        // Four sub-bands: 1px, 2px, 3px, 4px on/off pairs.
        var paint = pc.Fill(SKColors.White);
        var band = size / 4;
        for (var b = 0; b < 4; b++)
        {
            var pitch = b + 1;
            var o0 = b * band;
            if (vertical)
            {
                for (var i = 0; i < band; i += pitch * 2)
                {
                    DrawUtil.LineV(c, x + o0 + i, y, y + size, pitch, paint);
                }
            }
            else
            {
                for (var i = 0; i < band; i += pitch * 2)
                {
                    DrawUtil.LineH(c, y + o0 + i, x, x + size, pitch, paint);
                }
            }
        }
    }
}
