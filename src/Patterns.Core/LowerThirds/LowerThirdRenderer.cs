using Patterns.Core.Effects;
using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Particles;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.Core.LowerThirds;

/// <summary>
/// What one element keeps on one sink between frames: its particle sim, its fractal raster,
/// its gradient, and the paints whose blur filters are built once per size.
/// </summary>
public sealed class LowerThirdElementCache : IDisposable
{
    private sealed class BlurPaint : IDisposable
    {
        public readonly SKPaint Paint;
        private SKMaskFilter? _filter;
        private float _sigma = -1;

        public BlurPaint(SKPaintStyle style)
        {
            Paint = new SKPaint { IsAntialias = true, Style = style, StrokeCap = SKStrokeCap.Round };
        }

        public SKPaint For(float sigma)
        {
            if (Math.Abs(_sigma - sigma) > 0.01f)
            {
                _filter?.Dispose();
                _filter = sigma > 0.01f ? SKMaskFilter.CreateBlur(SKBlurStyle.Normal, sigma) : null;
                Paint.MaskFilter = _filter;
                _sigma = sigma;
            }
            return Paint;
        }

        public void Dispose()
        {
            Paint.MaskFilter = null;
            _filter?.Dispose();
            _filter = null;
            Paint.Dispose();
        }
    }

    private readonly BlurPaint _shadow = new(SKPaintStyle.Fill);
    private readonly BlurPaint _glow = new(SKPaintStyle.Stroke);
    private readonly BlurPaint _textGlow = new(SKPaintStyle.Fill);
    private readonly BlurPaint _halo = new(SKPaintStyle.Stroke);
    private SKShader? _gradient;
    private string _gradientKey = "";

    public ParticleSim? Sim;
    public long SimVersion = -1;
    public SKSizeI SimCanvas;
    public FractalSurface? Fractal;
    public string FractalColorsKey = "";
    public SKColor[] FractalColors = Array.Empty<SKColor>();

    /// <summary>The snapshot version an element threw at: it sits out until the design changes.</summary>
    public long FailedVersion = -1;

    public readonly SKPaint Box = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    public readonly SKPaint Layer = new();

    public SKPaint ShadowPaint(float blurPx) => _shadow.For(blurPx * 0.5f);
    public SKPaint GlowPaint(float glowPx) => _glow.For(glowPx * 0.5f);
    public SKPaint TextGlowPaint(float glowPx) => _textGlow.For(glowPx * 0.4f);
    public SKPaint HaloPaint(float sigma) => _halo.For(sigma);

    public SKShader GradientFor(string key, Func<SKShader> make)
    {
        if (key != _gradientKey || _gradient is null)
        {
            _gradient?.Dispose();
            _gradient = make();
            _gradientKey = key;
        }
        return _gradient;
    }

    public void Dispose()
    {
        Sim?.Dispose();
        Sim = null;
        Fractal?.Dispose();
        Fractal = null;
        _gradient?.Dispose();
        _gradient = null;
        _shadow.Dispose();
        _glow.Dispose();
        _textGlow.Dispose();
        _halo.Dispose();
        Box.Dispose();
        Layer.Dispose();
    }
}

/// <summary>
/// Draws the lower third on air over the canvas — the same on every sink, so a span, an NDI
/// send and the stream all carry it. Elements draw in design pixels inside a box that is
/// anchored and scaled to the canvas; each is moved by its pose (its keys at this instant),
/// and one element's failure never takes the others down.
/// </summary>
public static class LowerThirdRenderer
{
    /// <summary>Designs are drawn at this many lines and scaled to the canvas.</summary>
    public const double ReferenceHeight = 1080;

    /// <summary>The design box on a canvas, and the scale the elements draw at.</summary>
    public static SKRect BoxOf(LowerThirdDesign d, SKSizeI canvas, out float scale)
    {
        scale = (float)(canvas.Height / ReferenceHeight * d.ScalePct / 100.0);
        var w = (float)d.Width * scale;
        var h = (float)d.Height * scale;
        var mx = (float)d.MarginX * scale;
        var my = (float)d.MarginY * scale;
        float x = d.Anchor switch
        {
            Anchor9.TopLeft or Anchor9.MiddleLeft or Anchor9.BottomLeft => mx,
            Anchor9.TopRight or Anchor9.MiddleRight or Anchor9.BottomRight => canvas.Width - mx - w,
            _ => (canvas.Width - w) / 2f,
        };
        float y = d.Anchor switch
        {
            Anchor9.TopLeft or Anchor9.TopCenter or Anchor9.TopRight => my,
            Anchor9.BottomLeft or Anchor9.BottomCenter or Anchor9.BottomRight => canvas.Height - my - h,
            _ => (canvas.Height - h) / 2f,
        };
        return SKRect.Create(x, y, w, h);
    }

    /// <summary>The lower third on air, at the frame's instant.</summary>
    public static void Render(SKCanvas c, in PatternFrame f)
    {
        var cfg = f.Snapshot.State.LowerThirds;
        var design = cfg.Active;
        if (design is null || LowerThirdClock.Instants(cfg) is not { } at) return;
        Render(c, in f, design, at.ShownAt, at.HiddenAt, f.Ctx.Time);
    }

    /// <summary>A design at an instant of its own timeline (the desk's preview scrubs this).</summary>
    public static void Render(SKCanvas c, in PatternFrame f, LowerThirdDesign design, double shownAt, double? hiddenAt, double time)
    {
        var timing = LowerThirdClock.Evaluate(design, shownAt, hiddenAt, time);
        if (!timing.Visible) return;
        var box = BoxOf(design, f.Canvas, out var scale);
        var surge = EffectImpulses.SurgeAt(time);
        var caches = f.Sink.LowerThirds;
        Sweep(f.Sink, design, f.Snapshot.Version);

        var save = c.Save();
        c.Translate(box.Left, box.Top);
        c.Scale(scale);
        foreach (var e in design.Elements)
        {
            if (!e.Enabled) continue;
            var pose = LowerThirdClock.PoseOf(e, design, in timing);
            if (pose.Opacity <= 0.002f || pose.Scale <= 0.001f || pose.Reveal <= 0.001f) continue;
            if (!caches.TryGetValue(e.Id, out var cache))
            {
                cache = new LowerThirdElementCache();
                caches[e.Id] = cache;
            }
            if (cache.FailedVersion == f.Snapshot.Version) continue;
            var elementSave = c.Save();
            try
            {
                DrawElement(c, in f, design, e, cache, in pose, time, in surge, timing.Phase == LowerThirdPhase.Out);
            }
            catch (Exception ex)
            {
                // One bad element never takes the picture down: it sits out until the design changes.
                cache.FailedVersion = f.Snapshot.Version;
                Log.Warn($"Lower-third element '{e.Name}' failed to draw — skipped until the design changes.", ex);
            }
            finally
            {
                c.RestoreToCount(elementSave);
            }
        }
        c.RestoreToCount(save);
    }

    /// <summary>The words a text element shows.</summary>
    public static string TextOf(LowerThirdElement e, LowerThirdDesign d, DateTime now, string brandCompany)
    {
        var date = d.DateText.Length > 0 ? d.DateText : now.ToString("ddd d MMM yyyy");
        var time = d.TimeText.Length > 0 ? d.TimeText : now.ToString("HH:mm");
        return e.TextKind switch
        {
            LowerThirdTextKind.Name => d.PersonName,
            LowerThirdTextKind.Role => d.PersonRole,
            LowerThirdTextKind.Company => d.Company.Length > 0 ? d.Company : brandCompany,
            LowerThirdTextKind.Date => date,
            LowerThirdTextKind.Time => time,
            LowerThirdTextKind.DateAndTime => $"{date} · {time}",
            _ => e.Text,
        };
    }

    /// <summary>A colour word (primary, secondary, accent, text, background) from the brand kit, or a hex colour.</summary>
    public static SKColor ColorOf(in PatternFrame f, string value, SKColor fallback)
    {
        var brand = f.Snapshot.State.Brand;
        return value switch
        {
            "primary" => f.Color(brand.PrimaryColor, f.Palette.Accent),
            "secondary" => f.Color(brand.SecondaryColor, f.Palette.Secondary),
            "accent" => f.Color(brand.AccentColor, new SKColor(0xFF, 0xB0, 0x20)),
            "text" => f.Color(brand.TextColor, SKColors.White),
            "background" => f.Color(brand.BackgroundColor, SKColors.Black),
            _ => f.Color(value, fallback),
        };
    }

    private static void Sweep(SinkState sink, LowerThirdDesign design, long version)
    {
        if (sink.LowerThirdsSweptVersion == version) return;
        sink.LowerThirdsSweptVersion = version;
        if (sink.LowerThirds.Count == 0) return;
        var keep = new HashSet<string>();
        foreach (var e in design.Elements) keep.Add(e.Id);
        foreach (var id in sink.LowerThirds.Keys.ToList())
        {
            if (keep.Contains(id)) continue;
            sink.LowerThirds[id].Dispose();
            sink.LowerThirds.Remove(id);
        }
    }

    // ---- one element ----------------------------------------------------------------------------

    private static float Reach(LowerThirdElement e)
        => MathF.Max((float)e.GlowPx, (float)e.ShadowPx + MathF.Max(MathF.Abs((float)e.ShadowDx), MathF.Abs((float)e.ShadowDy))) * 1.5f + 6;

    private static void DrawElement(SKCanvas c, in PatternFrame f, LowerThirdDesign d, LowerThirdElement e, LowerThirdElementCache cache,
        in ElementPose pose, double time, in EffectSurge surge, bool leaving)
    {
        var rect = SKRect.Create((float)e.X, (float)e.Y, MathF.Max(1, (float)e.W), MathF.Max(1, (float)e.H));
        var alpha = Math.Clamp(pose.Opacity * (float)e.Opacity, 0f, 1f);

        c.Translate(pose.X, pose.Y);
        if (Math.Abs(pose.Scale - 1) > 0.0005f || Math.Abs(pose.Rotate) > 0.01f)
        {
            c.Translate(rect.MidX, rect.MidY);
            c.RotateDegrees(pose.Rotate);
            c.Scale(pose.Scale);
            c.Translate(-rect.MidX, -rect.MidY);
        }

        var reach = Reach(e);
        var outer = SKRect.Create(rect.Left - reach, rect.Top - reach, rect.Width + 2 * reach, rect.Height + 2 * reach);
        if (pose.Reveal < 0.999f)
        {
            c.ClipRect(SKRect.Create(outer.Left, outer.Top, rect.Width * MathF.Max(0, pose.Reveal) + reach, outer.Height));
        }
        var layered = alpha < 0.999f;
        if (layered)
        {
            // One layer per element, so a translucent element's parts never double up where they overlap.
            cache.Layer.Color = SKColors.White.WithAlpha((byte)Math.Round(alpha * 255));
            c.SaveLayer(outer, cache.Layer);
        }

        var boxed = e.HasBox;
        if (boxed) DrawBoxBack(c, in f, e, cache, rect, in surge);
        switch (e.Kind)
        {
            case LowerThirdElementKind.Text:
                DrawText(c, in f, d, e, cache, rect, in surge);
                break;
            case LowerThirdElementKind.Image:
            case LowerThirdElementKind.Logo:
                DrawImage(c, in f, e, rect);
                break;
            case LowerThirdElementKind.Media:
                DrawMedia(c, in f, e, rect, leaving);
                break;
            case LowerThirdElementKind.Particles:
                DrawParticles(c, in f, e, cache, rect, time);
                break;
            case LowerThirdElementKind.Fractal:
                DrawFractal(c, in f, e, cache, rect, time, in surge);
                break;
        }
        if (boxed) DrawBoxFront(c, in f, e, cache, rect, time);
        if (layered) c.Restore();
    }

    private static void DrawBoxBack(SKCanvas c, in PatternFrame f, LowerThirdElement e, LowerThirdElementCache cache, SKRect rect, in EffectSurge surge)
    {
        var r = (float)e.CornerPx;
        if (e.ShadowPx > 0 || e.ShadowDx != 0 || e.ShadowDy != 0)
        {
            var shadow = cache.ShadowPaint((float)e.ShadowPx);
            shadow.Color = ColorOf(in f, e.ShadowColor, new SKColor(0, 0, 0, 0xA0));
            var offset = SKRect.Create(rect.Left + (float)e.ShadowDx, rect.Top + (float)e.ShadowDy, rect.Width, rect.Height);
            c.DrawRoundRect(offset, r, r, shadow);
        }
        if (e.GlowPx > 0)
        {
            var glow = cache.GlowPaint((float)e.GlowPx);
            var gc = ColorOf(in f, e.GlowColor, f.Palette.Accent);
            var boost = Math.Clamp(0.85f + 0.6f * surge.Glow, 0f, 1.5f);
            glow.Color = gc.WithAlpha((byte)Math.Clamp(gc.Alpha * boost, 0, 255));
            glow.StrokeWidth = (float)Math.Max(2, e.BorderPx) + (float)e.GlowPx * 0.5f;
            c.DrawRoundRect(rect, r, r, glow);
        }
        if (e.Fill != LowerThirdFill.None)
        {
            var box = cache.Box;
            if (e.Fill == LowerThirdFill.Gradient)
            {
                var c1 = ColorOf(in f, e.FillColor, new SKColor(0x1B, 0x21, 0x30));
                var c2 = ColorOf(in f, e.FillColor2, f.Palette.Accent);
                var key = $"{rect.Left}|{rect.Top}|{rect.Width}|{rect.Height}|{c1}|{c2}|{e.Gradient}";
                box.Shader = cache.GradientFor(key, () =>
                {
                    var (p0, p1) = e.Gradient switch
                    {
                        LowerThirdGradient.TopBottom => (new SKPoint(rect.Left, rect.Top), new SKPoint(rect.Left, rect.Bottom)),
                        LowerThirdGradient.Diagonal => (new SKPoint(rect.Left, rect.Top), new SKPoint(rect.Right, rect.Bottom)),
                        _ => (new SKPoint(rect.Left, rect.Top), new SKPoint(rect.Right, rect.Top)),
                    };
                    return SKShader.CreateLinearGradient(p0, p1, new[] { c1, c2 }, SKShaderTileMode.Clamp);
                });
                box.Color = SKColors.White;
            }
            else
            {
                box.Shader = null;
                box.Color = ColorOf(in f, e.FillColor, new SKColor(0x1B, 0x21, 0x30));
            }
            c.DrawRoundRect(rect, r, r, box);
            box.Shader = null;
        }
    }

    private static void DrawBoxFront(SKCanvas c, in PatternFrame f, LowerThirdElement e, LowerThirdElementCache cache, SKRect rect, double time)
    {
        var r = (float)e.CornerPx;
        var pc = f.Paints;
        if (e.BorderPx > 0)
        {
            c.DrawRoundRect(rect, r, r, pc.StrokeAA(ColorOf(in f, e.BorderColor, SKColors.White), (float)e.BorderPx));
        }
        if (!e.Chaser) return;

        // The chaser: a bright run travelling round the edge, a soft halo under a crisp head.
        var path = pc.ScratchPath;
        path.Reset();
        path.AddRoundRect(rect, r, r);
        using var measure = new SKPathMeasure(path, false);
        var length = measure.Length;
        if (length <= 1) return;
        var segment = length * (float)e.ChaserLengthPct / 100f;
        var start = (float)((time * e.ChaserSpeed * length) % length);
        if (start < 0) start += length;
        using var run = new SKPath();
        var end = start + segment;
        if (end <= length)
        {
            measure.GetSegment(start, end, run, true);
        }
        else
        {
            measure.GetSegment(start, length, run, true);
            measure.GetSegment(0, end - length, run, true);
        }
        var color = ColorOf(in f, e.ChaserColor, SKColors.White);
        var width = (float)Math.Max(2, e.BorderPx > 0 ? e.BorderPx : 3);
        var halo = cache.HaloPaint(width * 1.5f);
        halo.Color = color.WithAlpha((byte)(color.Alpha * 0.6));
        halo.StrokeWidth = width * 3;
        c.DrawPath(run, halo);
        c.DrawPath(run, pc.StrokeAA(color, width));
    }

    private static void DrawText(SKCanvas c, in PatternFrame f, LowerThirdDesign d, LowerThirdElement e, LowerThirdElementCache cache, SKRect rect, in EffectSurge surge)
    {
        var text = TextOf(e, d, f.Ctx.Now, f.Snapshot.State.Brand.CompanyName);
        if (text.Length == 0) return;
        if (e.Uppercase) text = text.ToUpperInvariant();
        var pc = f.Paints;
        var family = e.FontFamily.Length > 0 ? e.FontFamily : f.Snapshot.State.Brand.FontFamily;
        var font = pc.FontFor(family, e.Bold);
        var size = (float)e.FontSizePx;
        font.Size = size;
        var padX = MathF.Min(rect.Width * 0.1f, size * 0.35f);
        var avail = MathF.Max(1, rect.Width - 2 * padX);
        var width = font.MeasureText(text);
        if (e.Shrink && width > avail)
        {
            size = MathF.Max(size * 0.4f, size * avail / width);
            font.Size = size;
        }
        var m = font.Metrics;
        var baseline = rect.MidY - (m.Ascent + m.Descent) / 2;
        float x;
        SKTextAlign align;
        switch (e.Align)
        {
            case LowerThirdAlign.Center:
                x = rect.MidX;
                align = SKTextAlign.Center;
                break;
            case LowerThirdAlign.Right:
                x = rect.Right - padX;
                align = SKTextAlign.Right;
                break;
            default:
                x = rect.Left + padX;
                align = SKTextAlign.Left;
                break;
        }
        var color = ColorOf(in f, e.TextColor, f.Palette.Text);
        if (!e.HasBox)
        {
            // A plain text element wears its glow and shadow on the letters.
            if (e.ShadowPx > 0 || e.ShadowDx != 0 || e.ShadowDy != 0)
            {
                var shadow = cache.ShadowPaint((float)e.ShadowPx);
                shadow.Color = ColorOf(in f, e.ShadowColor, new SKColor(0, 0, 0, 0xA0));
                c.DrawText(text, x + (float)e.ShadowDx, baseline + (float)e.ShadowDy, align, font, shadow);
            }
            if (e.GlowPx > 0)
            {
                var glow = cache.TextGlowPaint((float)e.GlowPx);
                var gc = ColorOf(in f, e.GlowColor, f.Palette.Accent);
                var boost = Math.Clamp(0.9f + 0.7f * surge.Glow, 0f, 1.6f);
                glow.Color = gc.WithAlpha((byte)Math.Clamp(gc.Alpha * boost, 0, 255));
                c.DrawText(text, x, baseline, align, font, glow);
            }
        }
        c.DrawText(text, x, baseline, align, font, pc.Text(color));
    }

    private static void ClipBox(SKCanvas c, SKRect rect, double cornerPx)
    {
        if (cornerPx > 0.5)
        {
            c.ClipRoundRect(new SKRoundRect(rect, (float)cornerPx, (float)cornerPx), SKClipOperation.Intersect, true);
        }
        else
        {
            c.ClipRect(rect);
        }
    }

    private static void DrawImage(SKCanvas c, in PatternFrame f, LowerThirdElement e, SKRect rect)
    {
        var path = e.Kind == LowerThirdElementKind.Logo ? f.Snapshot.State.Brand.LogoPath : e.Path;
        var img = ImageCache.Get(path);
        c.Save();
        ClipBox(c, rect, e.CornerPx);
        if (img is null)
        {
            // A picture not chosen yet: a quiet placeholder so the design still reads on the desk.
            if (e.Kind == LowerThirdElementKind.Image) c.DrawRect(rect, f.Paints.FillAA(new SKColor(0xFF, 0xFF, 0xFF, 0x22)));
        }
        else
        {
            var dest = DrawUtil.Fit(new SKSizeI(img.Width, img.Height), rect, e.Fit);
            c.DrawImage(img, dest, DrawUtil.Smooth, f.Paints.FillAA(SKColors.White));
        }
        c.Restore();
    }

    private static void DrawMedia(SKCanvas c, in PatternFrame f, LowerThirdElement e, SKRect rect, bool leaving)
    {
        var pc = f.Paints;
        var key = InputKeys.Video(e.Path);
        // On the way out the clip may already be unmounted: the retired source still has its last frames.
        var source = key.Length > 0 ? InputBus.Resolve(key, f.Ctx.IsFadeSource || leaving) : null;
        c.Save();
        ClipBox(c, rect, e.CornerPx);
        var drawn = false;
        if (source?.FrameSize is { } size)
        {
            var dest = DrawUtil.Fit(size, rect, e.Fit);
            drawn = source.DrawFrame(c, dest, pc.FillAA(SKColors.White));
        }
        if (!drawn)
        {
            var img = e.Path.Length > 0 && !PlaylistSequencer.IsVideoPath(e.Path) ? ImageCache.Get(e.Path) : null;
            if (img is not null)
            {
                var dest = DrawUtil.Fit(new SKSizeI(img.Width, img.Height), rect, e.Fit);
                c.DrawImage(img, dest, DrawUtil.Smooth, pc.FillAA(SKColors.White));
            }
            else
            {
                c.DrawRect(rect, pc.FillAA(new SKColor(0, 0, 0, 0x66)));
            }
        }
        c.Restore();
    }

    private static void DrawParticles(SKCanvas c, in PatternFrame f, LowerThirdElement e, LowerThirdElementCache cache, SKRect rect, double time)
    {
        var sim = cache.Sim ??= new ParticleSim();
        var canvas = new SKSizeI(Math.Max(8, (int)rect.Width), Math.Max(8, (int)rect.Height));
        if (cache.SimVersion != f.Snapshot.Version || cache.SimCanvas != canvas)
        {
            sim.Configure(e.Particles, f.Snapshot, canvas);
            cache.SimVersion = f.Snapshot.Version;
            cache.SimCanvas = canvas;
        }
        sim.Advance(time);
        c.Save();
        ClipBox(c, rect, e.CornerPx);
        c.Translate(rect.Left, rect.Top);
        sim.Render(c, f.Paints);
        c.Restore();
    }

    private static void DrawFractal(SKCanvas c, in PatternFrame f, LowerThirdElement e, LowerThirdElementCache cache, SKRect rect, double time, in EffectSurge surge)
    {
        var o = e.Fractal;
        if (cache.FractalColorsKey != o.ColorsCsv || cache.FractalColors.Length == 0)
        {
            cache.FractalColors = ColorUtil.ParseList(o.ColorsCsv, SKColors.White);
            cache.FractalColorsKey = o.ColorsCsv;
        }
        var audio = o.AudioSource == AudioSourceKind.None ? AudioLevelFrame.Zero : AudioLevels.Read(f.Ctx.UtcNow);
        var view = FractalView.Of(o, time, audio, surge: surge);
        var size = FractalRaster.SizeFor(o.Quality, new SKSizeI(Math.Max(8, (int)rect.Width), Math.Max(8, (int)rect.Height)));
        cache.Fractal = FractalRaster.Render(cache.Fractal, size, o.Kind, cache.FractalColors, view);
        using var image = SKImage.FromBitmap(cache.Fractal.Bitmap);
        if (image is null) return;
        c.Save();
        ClipBox(c, rect, e.CornerPx);
        c.DrawImage(image, rect, DrawUtil.Smooth, f.Paints.Fill(SKColors.White));
        c.Restore();
    }
}
