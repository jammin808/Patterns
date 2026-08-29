using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Particles;
using Patterns.Core.Rendering;
using SkiaSharp;

namespace Patterns.Core.Patterns;

/// <summary>User graphics and video, composited through the engine (so they reach NDI and spans too).</summary>
public sealed class MediaPattern : IPatternRenderer
{
    public void Render(SKCanvas c, in PatternFrame f)
    {
        var o = f.Config.Media;
        var pc = f.Paints;
        c.Clear(f.Color(o.BackgroundColor, SKColors.Black));
        var bounds = SKRect.Create(0, 0, f.W, f.H);

        if (o.Source == MediaSource.Video)
        {
            var video = VideoService.Current;
            if (video is null)
            {
                var note = string.IsNullOrEmpty(VideoService.AvailabilityNote)
                    ? "Choose a video file in the Media panel."
                    : VideoService.AvailabilityNote;
                PlaceholderCard(c, in f, "No video playing", note);
                return;
            }

            var size = video.FrameSize;
            var dest = size is { } s ? DrawUtil.Fit(s, bounds, o.Fit) : bounds;
            if (!video.DrawFrame(c, dest, pc.FillAA(SKColors.White)))
            {
                PlaceholderCard(c, in f, "Video", video.StatusText);
            }
            return;
        }

        var image = ImageCache.Get(o.ImagePath);
        if (image is null)
        {
            PlaceholderCard(c, in f, "No image",
                string.IsNullOrWhiteSpace(o.ImagePath)
                    ? "Choose an image in the Media panel (PNG, JPEG, BMP, WebP)."
                    : $"Could not load: {o.ImagePath}");
            return;
        }

        if (o.Fit == FitMode.Tile)
        {
            for (float y = 0; y < f.H; y += image.Height)
            {
                for (float x = 0; x < f.W; x += image.Width)
                {
                    c.DrawImage(image, SKRect.Create(x, y, image.Width, image.Height), DrawUtil.Smooth, pc.FillAA(SKColors.White));
                }
            }
        }
        else
        {
            var dest = DrawUtil.Fit(new SKSizeI(image.Width, image.Height), bounds, o.Fit);
            c.DrawImage(image, dest, DrawUtil.Smooth, pc.FillAA(SKColors.White));
        }
    }

    internal static void PlaceholderCard(SKCanvas c, in PatternFrame f, string title, string detail)
    {
        var pc = f.Paints;
        float w = Math.Min(f.W * 0.72f, 860);
        float h = Math.Min(f.H * 0.3f, 200);
        var rect = SKRect.Create((f.W - w) / 2, (f.H - h) / 2, w, h);
        c.DrawRoundRect(rect, 14, 14, pc.FillAA(new SKColor(0x14, 0x16, 0x1C)));
        c.DrawRoundRect(rect, 14, 14, pc.StrokeAA(new SKColor(0x3A, 0x40, 0x4E), 1.5f));

        var tf = pc.FontBold;
        tf.Size = Math.Max(15, h * 0.19f);
        DrawUtil.TextCentered(c, title, rect.MidX, rect.Top + h * 0.34f, tf, pc.Text(SKColors.White));
        var bf = pc.FontRegular;
        bf.Size = Math.Max(11, h * 0.11f);
        var text = detail.Length > 96 ? detail[..95] + "…" : detail;
        DrawUtil.TextCentered(c, text, rect.MidX, rect.Top + h * 0.64f, bf, pc.Text(new SKColor(0xA8, 0xB2, 0xC2)));
    }
}

/// <summary>The particle mini-studio output.</summary>
public sealed class ParticlePattern : IPatternRenderer
{
    public void Render(SKCanvas c, in PatternFrame f)
    {
        var o = f.Config.Particles;
        c.Clear(o.UseBrandColors
            ? f.Color(f.Snapshot.State.Brand.BackgroundColor, SKColors.Black)
            : f.Color(o.BackgroundColor, SKColors.Black));

        var sim = f.Sink.Particles ??= new ParticleSim();
        sim.Configure(o, f.Snapshot, f.Canvas);
        sim.Advance(f.Ctx.Time);
        sim.Render(c, f.Paints);
    }
}
