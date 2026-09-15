using Patterns.Core.Media;
using Patterns.Rendering.Media;
using Patterns.Core.Model;
using SkiaSharp;

namespace Patterns.Rendering;

/// <summary>Draws another target's picture into a layer's box — the engine supplies it, since only the engine can render a target.</summary>
public delegate bool LayerScreenDrawer(SKCanvas canvas, SKRect dest, string targetId, in PatternFrame f);

/// <summary>
/// The two layers over a pattern: each a box (a share of the canvas) holding a still, a clip,
/// an NDI feed, a capture device or another target's picture, fitted, cropped, rounded, bordered
/// and faded as set. Drawn in canvas space after the pattern and before the overlays; on a
/// monitor pane an empty layer shows its box so the desk can place it, on an output it shows
/// nothing at all.
/// </summary>
public static class LayerRenderer
{
    public static readonly SKColor FrameColor = new(0x3E, 0xC1, 0xF3, 0xA0);

    /// <summary>The layer's box in canvas pixels.</summary>
    public static SKRect RectOf(LayerConfig l, SKSizeI canvas) => SKRect.Create(
        (float)(canvas.Width * l.XPct / 100),
        (float)(canvas.Height * l.YPct / 100),
        (float)(canvas.Width * l.WPct / 100),
        (float)(canvas.Height * l.HPct / 100));

    /// <summary>Draws both layers; true when either drew anything — a picture, an empty box, or one still leaving.</summary>
    public static bool Render(SKCanvas c, in PatternFrame f, LayerScreenDrawer? drawScreen)
    {
        var one = Appear(c, in f, f.Config.Layer1, AppearKey.Layer1, HitKind.Layer1, "Layer 1", drawScreen);
        var two = Appear(c, in f, f.Config.Layer2, AppearKey.Layer2, HitKind.Layer2, "Layer 2", drawScreen);
        return one || two;
    }

    /// <summary>
    /// One layer through the sink's arrival tracker (round 63): switched on it arrives over its
    /// time — a fade, or a slide in from the nearest edge — switched off it leaves the same way
    /// from the snapshot that had it, and a new picture in it leaves the old and arrives the new
    /// together. A frame that is not the sink's own picture draws what its snapshot says.
    /// </summary>
    private static bool Appear(SKCanvas c, in PatternFrame f, LayerConfig l, AppearKey key, HitKind kind, string name, LayerScreenDrawer? drawScreen)
    {
        if (Appearances.Bypasses(f.Ctx))
        {
            if (!l.Enabled) return false;
            Draw(c, in f, l, kind, name, drawScreen, 1f, default);
            return true;
        }
        var animate = l.Appear.Kind != AppearKind.Cut && Appearances.Animates(in f);
        var identity = l.Enabled ? Appearances.LayerIdentity(l) : 0;
        var p = f.Sink.Appearances.Read(key, l.Enabled, identity, f.Ctx.Time, Appearances.Seconds(l.Appear, f.Snapshot), animate, f.Snapshot);
        var drew = false;
        if (p.DrawsOutgoing)
        {
            var was = Appearances.OutgoingFrame(in f, p.Outgoing!);
            var old = key == AppearKey.Layer1 ? was.Config.Layer1 : was.Config.Layer2;
            if (old.Enabled)
            {
                Draw(c, in was, old, kind, name, drawScreen, p.Out, Slide(old, in f, p.Out));
                drew = true;
            }
        }
        if (!p.DrawsCurrent || !l.Enabled) return drew;
        Draw(c, in f, l, kind, name, drawScreen, p.In, p.In >= 1f ? default : Slide(l, in f, p.In));
        return true;
    }

    private static SKPoint Slide(LayerConfig l, in PatternFrame f, float presence)
        => l.Appear.Kind == AppearKind.Slide ? Appearances.SlideOffset(RectOf(l, f.Canvas), f.Canvas, presence) : default;

    private static void Draw(SKCanvas c, in PatternFrame f, LayerConfig l, HitKind kind, string name, LayerScreenDrawer? drawScreen, float presence, SKPoint slide)
    {
        if (!l.Enabled) return;
        var rect = RectOf(l, f.Canvas);
        if (rect.Width < 1 || rect.Height < 1) return;
        var pc = f.Paints;
        var alpha = (byte)Math.Clamp(l.Opacity * presence * 255, 0, 255);
        var corner = (float)Math.Min(l.CornerPx, Math.Min(rect.Width, rect.Height) / 2);

        var outer = c.Save();
        if (slide != default) c.Translate(slide.X, slide.Y);   // an arrival sliding in from the edge; the hit box stays where the layer lives
        var save = c.Save();
        bool drew;
        var picture = rect;
        try
        {
            if (corner > 0) c.ClipRoundRect(new SKRoundRect(rect, corner, corner), SKClipOperation.Intersect, antialias: true);
            else c.ClipRect(rect);
            drew = DrawSource(c, in f, l, rect, alpha, drawScreen, out picture);
        }
        finally
        {
            c.RestoreToCount(save);
        }

        if (!drew && f.Ctx.Sink is SinkKind.Preview or SinkKind.Monitor && !f.Ctx.InMultiview && !f.Ctx.InLayer)
        {
            // Nothing to show yet: the desk sees the box (a dashed frame and the name) to place it.
            using var dash = SKPathEffect.CreateDash(new[] { 8f, 6f }, 0);
            using var frame = new SKPaint
            {
                Style = SKPaintStyle.Stroke,
                Color = FrameColor.WithAlpha((byte)Math.Clamp(FrameColor.Alpha * presence, 0, 255)),
                StrokeWidth = Math.Max(1.5f, f.H * 0.003f),
                IsAntialias = true,
                PathEffect = dash,
            };
            c.DrawRoundRect(rect, corner, corner, frame);
            var font = pc.FontRegular;
            font.Size = Math.Clamp(rect.Height * 0.12f, 10, 28);
            DrawUtil.TextCentered(c, $"{name} — {Status(l)}", rect.MidX, rect.MidY, font, pc.Text(new SKColor(0x8A, 0x93, 0xA3)));
        }

        if (l.BorderPx > 0)
        {
            var bw = (float)Math.Min(l.BorderPx, Math.Min(rect.Width, rect.Height) / 2);
            var inner = SKRect.Inflate(rect, -bw / 2, -bw / 2);
            var r = Math.Max(0, corner - bw / 2);
            c.DrawRoundRect(inner, r, r, pc.StrokeAA(f.Color(l.BorderColor, SKColors.White).WithAlpha(alpha), bw));
        }
        c.RestoreToCount(outer);

        if (!f.Ctx.IsFadeSource && !f.Ctx.InMultiview && !f.Ctx.InLayer)
        {
            f.Sink.Hits.Add(new HitRect(kind, rect, false));
            // A web layer's page sits on top of its own box: a plain press clicks into the page,
            // Alt takes hold of the box (HitTester looks through pages then).
            if (drew && l.Source == LayerSource.Web)
            {
                f.Sink.Hits.Add(new HitRect(HitKind.WebPage, picture, false, InputKeys.Web(l.WebUrl), CropOf(l), rect));
            }
        }
    }

    private static FrameCrop CropOf(LayerConfig l) => new(l.CropLeftPct, l.CropTopPct, l.CropRightPct, l.CropBottomPct);

    private static string Status(LayerConfig l) => l.Source switch
    {
        LayerSource.Image => l.ImagePath.Length == 0 ? "choose an image" : "image not found",
        LayerSource.Video => l.VideoPath.Length == 0 ? "choose a clip" : "opening…",
        LayerSource.NdiFeed => l.NdiSourceName.Length == 0 ? "choose an NDI source" : "waiting for the feed",
        LayerSource.Capture => l.CaptureDevice.Length == 0 ? "choose a device" : "opening…",
        LayerSource.Web => l.WebUrl.Length == 0
            ? "enter a page address"
            : WebInput.AvailabilityNote.Length > 0 ? WebInput.AvailabilityNote : "opening the page…",
        LayerSource.Arcade => "the arcade — first frame…",
        _ => l.TargetId.Length == 0 ? "choose a screen" : "not in this rig",
    };

    /// <summary>Draws the layer's picture inside its clipped box; <paramref name="picture"/> is where the picture itself landed (the box when nothing drew).</summary>
    private static bool DrawSource(SKCanvas c, in PatternFrame f, LayerConfig l, SKRect rect, byte alpha, LayerScreenDrawer? drawScreen, out SKRect picture)
    {
        picture = rect;
        var crop = CropOf(l);
        switch (l.Source)
        {
            case LayerSource.Image:
            {
                var image = ImageCache.Get(l.ImagePath);
                if (image is null) return false;
                var src = crop.SourceRect(new SKSizeI(image.Width, image.Height));
                var dest = DrawUtil.Fit(new SKSizeI(Math.Max(1, (int)src.Width), Math.Max(1, (int)src.Height)), rect, l.Fit);
                c.DrawImage(image, src, dest, DrawUtil.Smooth, f.Paints.FillAA(SKColors.White.WithAlpha(alpha)));
                picture = dest;
                return true;
            }
            case LayerSource.Screen:
            {
                if (drawScreen is null || l.TargetId.Length == 0 || f.Ctx.InLayer) return false;
                if (alpha == 255) return drawScreen(c, rect, l.TargetId, in f);
                using var layerPaint = new SKPaint { Color = new SKColor(255, 255, 255, alpha) };
                c.SaveLayer(rect, layerPaint);
                try
                {
                    return drawScreen(c, rect, l.TargetId, in f);
                }
                finally
                {
                    c.Restore();
                }
            }
            default:
            {
                var key = l.Source switch
                {
                    LayerSource.Video => InputKeys.Video(l.VideoPath),
                    LayerSource.NdiFeed => InputKeys.Ndi(l.NdiSourceName),
                    LayerSource.Web => InputKeys.Web(l.WebUrl),
                    LayerSource.Arcade => InputKeys.Arcade(),
                    _ => InputKeys.Capture(l.CaptureDevice),
                };
                var source = InputBus.Resolve(key, f.Ctx.IsFadeSource);
                if (source?.FrameSize is not { } size || size.Width <= 0 || size.Height <= 0) return false;
                var cropped = crop.SourceRect(size);
                var dest = DrawUtil.Fit(new SKSizeI(Math.Max(1, (int)cropped.Width), Math.Max(1, (int)cropped.Height)), rect, l.Fit);
                using var paint = new SKPaint { Color = new SKColor(255, 255, 255, alpha), IsAntialias = true };
                var drawn = source.Draw(c, dest, paint, in crop);
                if (!drawn.Drew) return false;
                f.Sink.Stages.NoteLive(in drawn);
                picture = dest;
                if (l.Source == LayerSource.Web && f.Snapshot.State.Web.ShowPointer && source is IWebSource web)
                {
                    WebPointer.Draw(c, dest, in crop, web, f.Ctx.UtcNow, f.Paints);
                }
                return true;
            }
        }
    }
}
