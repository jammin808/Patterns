using Patterns.Core.Model;
using Patterns.Core.Rendering;
using SkiaSharp;

namespace Patterns.Core.Patterns;

/// <summary>Animated smoothness/judder diagnostics. All motion is derived from the shared show clock.</summary>
public sealed class MotionPattern : IPatternRenderer
{
    private const string ZonePlateSksl = """
        uniform float2 center;
        uniform float k;
        uniform float t;
        half4 main(float2 p) {
            float2 d = p - center;
            float v = 0.5 + 0.5 * sin(dot(d, d) * k - t);
            return half4(v, v, v, 1.0);
        }
        """;

    public void Render(SKCanvas c, in PatternFrame f)
    {
        switch (f.Config.Motion.Variant)
        {
            case MotionVariant.BouncingBox: RenderBouncingBox(c, in f); break;
            case MotionVariant.FrameFlash: RenderFrameFlash(c, in f); break;
            case MotionVariant.ZonePlate: RenderZonePlate(c, in f); break;
            case MotionVariant.ScrollingGrid: RenderScrollingGrid(c, in f); break;
            default: RenderMovingBar(c, in f); break;
        }

        if (f.Config.Motion.ShowFps)
        {
            DrawUtil.Chip(c, $"{f.Ctx.MeasuredFps:0.0} fps · worst {f.Sink.Fps.WorstMs:0.0} ms",
                f.Canvas, Anchor9.TopRight, GridPattern.ChipText(f), f.Paints, f.Palette.Text, f.Palette.ChipBg);
        }
    }

    private static void RenderMovingBar(SKCanvas c, in PatternFrame f)
    {
        var o = f.Config.Motion;
        var pc = f.Paints;
        c.Clear(SKColors.Black);
        int w = f.W, h = f.H;
        var span = o.Vertical ? h : w;
        var thick = Math.Min(o.BarThickness, span);

        // Judder mode steps a whole number of pixels per rendered frame; otherwise time-based.
        double pos = o.PxPerFrame > 0
            ? (f.Ctx.Frame * o.PxPerFrame) % span
            : (f.Ctx.Time * o.SpeedPxPerSec) % span;
        var p = (int)pos;

        var bar = pc.Fill(f.Palette.Branded ? f.Palette.Accent : SKColors.White);
        if (o.Vertical)
        {
            DrawUtil.LineH(c, p, 0, w, Math.Min(thick, h - p), bar);
            if (p + thick > h) DrawUtil.LineH(c, 0, 0, w, p + thick - h, bar);
        }
        else
        {
            DrawUtil.LineV(c, p, 0, h, Math.Min(thick, w - p), bar);
            if (p + thick > w) DrawUtil.LineV(c, 0, 0, h, p + thick - w, bar);
        }

        // Faint track marks every 10% so speed is judgeable.
        var mark = pc.Fill(new SKColor(0xFF, 0xFF, 0xFF, 0x30));
        for (var i = 1; i < 10; i++)
        {
            if (o.Vertical) DrawUtil.LineH(c, h * i / 10, 0, w / 40, 1, mark);
            else DrawUtil.LineV(c, w * i / 10, 0, h / 40, 1, mark);
        }

        var mode = o.PxPerFrame > 0 ? $"{o.PxPerFrame} px/frame" : $"{o.SpeedPxPerSec:0} px/s";
        DrawUtil.Chip(c, $"BAR · {mode}", f.Canvas, Anchor9.BottomCenter, GridPattern.ChipText(f), pc,
            f.Palette.Text, f.Palette.ChipBg);
    }

    private static void RenderBouncingBox(SKCanvas c, in PatternFrame f)
    {
        var o = f.Config.Motion;
        var pc = f.Paints;
        c.Clear(SKColors.Black);
        int w = f.W, h = f.H;

        var bw = (float)(Math.Min(w, h) * o.BoxSizePct / 100 * 1.7);
        var bh = bw * 0.55f;
        var vx = o.SpeedPxPerSec;
        var vy = o.SpeedPxPerSec * 0.731; // incommensurate so the path fills the screen

        var x = Triangle(f.Ctx.Time * vx, Math.Max(1, w - bw));
        var y = Triangle(f.Ctx.Time * vy, Math.Max(1, h - bh));

        var rect = SKRect.Create((float)x, (float)y, bw, bh);
        c.DrawRoundRect(rect, bh * 0.16f, bh * 0.16f, pc.FillAA(f.Palette.Accent));

        var font = pc.FontBold;
        font.Size = bh * 0.34f;
        DrawUtil.TextCentered(c, $"{f.Ctx.MeasuredFps:0.0} fps", rect.MidX, rect.MidY - bh * 0.16f, font, pc.Text(SKColors.Black));
        var sub = pc.FontRegular;
        sub.Size = bh * 0.2f;
        DrawUtil.TextCentered(c, $"frame {f.Ctx.Frame}", rect.MidX, rect.MidY + bh * 0.22f, sub, pc.Text(new SKColor(0, 0, 0, 0xB0)));
    }

    private static double Triangle(double u, double span)
    {
        var m = u % (2 * span);
        return m < span ? m : 2 * span - m;
    }

    private static void RenderFrameFlash(SKCanvas c, in PatternFrame f)
    {
        var pc = f.Paints;
        var even = (f.Ctx.Frame & 1) == 0;
        c.Clear(even ? SKColors.Black : SKColors.White);
        var fg = even ? SKColors.White : SKColors.Black;
        int w = f.W, h = f.H;

        // Opposite-phase corner blocks catch tearing and half-updates.
        var s = Math.Min(w, h) / 8;
        var corner = pc.Fill(even ? SKColors.White : SKColors.Black);
        c.DrawRect(SKRect.Create(0, 0, s, s), corner);
        c.DrawRect(SKRect.Create(w - s, h - s, s, s), corner);
        var opposite = pc.Fill(even ? SKColors.Black : SKColors.White);
        c.DrawRect(SKRect.Create(w - s, 0, s, s), opposite);
        c.DrawRect(SKRect.Create(0, h - s, s, s), opposite);

        var font = pc.FontBold;
        font.Size = h * 0.2f;
        DrawUtil.FixedDigitsCentered(c, (f.Ctx.Frame % 1000).ToString("000"), w / 2f, h / 2f, font, pc.Text(fg));

        var sub = pc.FontRegular;
        sub.Size = Math.Clamp(h * 0.03f, 10, 40);
        DrawUtil.TextCentered(c, "alternating every frame — a steady grey means dropped frames", w / 2f, h * 0.68f, sub, pc.Text(fg));
    }

    private void RenderZonePlate(SKCanvas c, in PatternFrame f)
    {
        var o = f.Config.Motion;
        var sink = f.Sink;
        int w = f.W, h = f.H;

        if (sink.ZonePlateEffect is null && !sink.ZonePlateUnavailable)
        {
            var effect = SKRuntimeEffect.CreateShader(ZonePlateSksl, out var errors);
            if (effect is null)
            {
                Services.Log.Warn($"Zone plate shader unavailable: {errors}");
                sink.ZonePlateUnavailable = true;
            }
            else
            {
                sink.ZonePlateEffect = effect;
            }
        }

        var minDim = Math.Min(w, h);
        if (sink.ZonePlateEffect is { } fx)
        {
            var k = (float)(o.ZonePlateScale * Math.PI / (2.0 * minDim));
            var uniforms = new SKRuntimeEffectUniforms(fx)
            {
                ["center"] = new[] { w / 2f, h / 2f },
                ["k"] = k,
                ["t"] = (float)(f.Ctx.Time * 6.0),
            };
            using var shader = fx.ToShader(uniforms);
            var paint = f.Paints.Fill(SKColors.White);
            paint.Shader = shader;
            c.DrawRect(SKRect.Create(0, 0, w, h), paint);
            paint.Shader = null;
        }
        else
        {
            // Fallback: animated concentric rings.
            c.Clear(SKColors.Black);
            var stroke = f.Paints.StrokeAA(SKColors.White, 2);
            var phase = (float)(f.Ctx.Time * 40 % 24);
            for (float r = 24 - phase; r < minDim * 0.7f; r += 24)
            {
                if (r > 0) c.DrawCircle(w / 2f, h / 2f, r, stroke);
            }
        }
    }

    private static void RenderScrollingGrid(SKCanvas c, in PatternFrame f)
    {
        var o = f.Config.Motion;
        var pc = f.Paints;
        c.Clear(SKColors.Black);
        int w = f.W, h = f.H;
        const int cell = 96;

        var offset = (float)(f.Ctx.Time * o.SpeedPxPerSec % cell);
        var line = pc.Fill(SKColors.White);
        if (o.Vertical)
        {
            for (var y = (int)offset - cell; y < h; y += cell)
            {
                if (y >= 0) DrawUtil.LineH(c, y, 0, w, 1, line);
            }
            for (var x = 0; x < w; x += cell)
            {
                DrawUtil.LineV(c, x, 0, h, 1, pc.Fill(new SKColor(0xFF, 0xFF, 0xFF, 0x50)));
            }
        }
        else
        {
            for (var x = (int)offset - cell; x < w; x += cell)
            {
                if (x >= 0) DrawUtil.LineV(c, x, 0, h, 1, line);
            }
            for (var y = 0; y < h; y += cell)
            {
                DrawUtil.LineH(c, y, 0, w, 1, pc.Fill(new SKColor(0xFF, 0xFF, 0xFF, 0x50)));
            }
        }
    }
}

/// <summary>Full-field colour cycling — channel checks and LED burn-in.</summary>
public sealed class ColorCyclePattern : IPatternRenderer
{
    public void Render(SKCanvas c, in PatternFrame f)
    {
        var o = f.Config.ColorCycle;
        var brand = f.Snapshot.State.Brand;

        Span<SKColor> brandColors = stackalloc SKColor[5];
        SKColor[]? list = null;
        int count;
        if (o.UseBrandColors)
        {
            brandColors[0] = f.Color(brand.PrimaryColor, SKColors.White);
            brandColors[1] = f.Color(brand.SecondaryColor, SKColors.Gray);
            brandColors[2] = f.Color(brand.AccentColor, SKColors.Yellow);
            brandColors[3] = SKColors.White;
            brandColors[4] = SKColors.Black;
            count = 5;
        }
        else
        {
            list = f.Sink.CycleColors.Get(o.ColorsCsv);
            count = list.Length;
        }

        var interval = Math.Max(0.1, o.IntervalSeconds);
        var slot = f.Ctx.Time / interval;
        var idx = (int)(slot % count);
        var color = list is null ? brandColors[idx] : list[idx];

        if (o.Fade)
        {
            var next = list is null ? brandColors[(idx + 1) % count] : list[(idx + 1) % count];
            var t = (float)(slot - Math.Floor(slot));
            color = Lerp(color, next, t);
        }

        c.Clear(color);

        if (o.ShowLabel)
        {
            var luma = 0.2126 * color.Red + 0.7152 * color.Green + 0.0722 * color.Blue;
            var text = luma > 110 ? SKColors.Black : SKColors.White;
            var bg = luma > 110 ? new SKColor(255, 255, 255, 0x90) : f.Palette.ChipBg;
            DrawUtil.Chip(c, $"#{color.Red:X2}{color.Green:X2}{color.Blue:X2} · {idx + 1}/{count}",
                f.Canvas, Anchor9.BottomCenter, GridPattern.ChipText(f), f.Paints, text, bg);
        }
    }

    private static SKColor Lerp(SKColor a, SKColor b, float t) => new(
        (byte)(a.Red + (b.Red - a.Red) * t),
        (byte)(a.Green + (b.Green - a.Green) * t),
        (byte)(a.Blue + (b.Blue - a.Blue) * t));
}

/// <summary>Caches the parsed colour list per sink (renderers never parse hex per frame).</summary>
public sealed class CycleColorCache
{
    private SKColor[] _colors = { SKColors.White };
    private string? _key;

    public SKColor[] Get(string csv)
    {
        if (_key == csv) return _colors;
        _colors = Services.ColorUtil.ParseList(csv, SKColors.White);
        _key = csv;
        return _colors;
    }
}
