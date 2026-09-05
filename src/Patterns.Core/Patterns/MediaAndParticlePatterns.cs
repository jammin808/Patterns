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

        if (o.Source == MediaSource.Playlist)
        {
            var now = f.Snapshot.PlaylistNow;
            if (now is null)
            {
                PlaceholderCard(c, in f, "Playlist",
                    "Add media files or folders in the Media panel — they cycle and loop automatically.");
                return;
            }

            if (now.IsVideo)
            {
                if (Services.PlaylistSequencer.IsAudioPath(now.Path))
                {
                    AudioCard(c, in f, now.Path);
                    return;
                }
                var key = InputKeys.Video(now.Path);
                var video = InputBus.Resolve(key, f.Ctx.IsFadeSource);
                if (video is null || !DrawInput(c, in f, o, video, bounds, o.Fit, pc.FillAA(SKColors.White), key, out _))
                {
                    PlaceholderCard(c, in f, System.IO.Path.GetFileName(now.Path), video?.StatusText ?? "Starting video…");
                }
            }
            else
            {
                var img = ImageCache.Get(now.Path);
                if (img is null)
                {
                    PlaceholderCard(c, in f, "Missing media", now.Path);
                }
                else
                {
                    DrawImageInput(c, in f, o, img, bounds, o.Fit, pc.FillAA(SKColors.White));
                }
            }
            return;
        }

        if (o.Source == MediaSource.Video)
        {
            if (Services.PlaylistSequencer.IsAudioPath(o.VideoPath))
            {
                AudioCard(c, in f, o.VideoPath);
                return;
            }
            var key = InputKeys.Video(o.VideoPath);
            var video = InputBus.Resolve(key, f.Ctx.IsFadeSource);
            if (video is null)
            {
                var note = string.IsNullOrEmpty(VideoService.AvailabilityNote)
                    ? "Choose a video or audio file in the Media panel."
                    : VideoService.AvailabilityNote;
                PlaceholderCard(c, in f, "No video playing", note);
                return;
            }
            if (!DrawInput(c, in f, o, video, bounds, o.Fit, pc.FillAA(SKColors.White), key, out _))
            {
                PlaceholderCard(c, in f, "Video", video.StatusText);
            }
            return;
        }

        if (o.Source == MediaSource.NdiFeed)
        {
            var key = InputKeys.Ndi(o.NdiSourceName);
            var feed = InputBus.Resolve(key, f.Ctx.IsFadeSource);
            if (feed is null)
            {
                var note = string.IsNullOrEmpty(NdiInput.AvailabilityNote)
                    ? "Choose an NDI source in the Media panel."
                    : NdiInput.AvailabilityNote;
                PlaceholderCard(c, in f, "No NDI feed", note);
                return;
            }
            if (!DrawInput(c, in f, o, feed, bounds, o.Fit, pc.FillAA(SKColors.White), key, out _))
            {
                PlaceholderCard(c, in f, o.NdiSourceName.Length > 0 ? o.NdiSourceName : "NDI", feed.StatusText);
            }
            return;
        }

        if (o.Source == MediaSource.Capture)
        {
            var key = InputKeys.Capture(o.CaptureDevice);
            var cap = InputBus.Resolve(key, f.Ctx.IsFadeSource);
            if (cap is null)
            {
                var note = string.IsNullOrEmpty(VideoService.AvailabilityNote)
                    ? "Choose a capture device in the Media panel."
                    : VideoService.AvailabilityNote;
                PlaceholderCard(c, in f, "No capture device", note);
                return;
            }
            if (!DrawInput(c, in f, o, cap, bounds, o.Fit, pc.FillAA(SKColors.White), key, out _))
            {
                PlaceholderCard(c, in f, o.CaptureDevice.Length > 0 ? o.CaptureDevice : "Capture", cap.StatusText);
            }
            return;
        }

        if (o.Source == MediaSource.Web)
        {
            var key = InputKeys.Web(o.WebUrl);
            var page = InputBus.Resolve(key, f.Ctx.IsFadeSource);
            var name = o.WebUrl.Length > 0 ? Services.WebAddress.ShortName(o.WebUrl) : "No web page";
            if (page is null)
            {
                var note = WebInput.AvailabilityNote.Length > 0
                    ? WebInput.AvailabilityNote
                    : o.WebUrl.Length == 0 ? "Enter a page address in the Media panel." : "Opening the page…";
                PlaceholderCard(c, in f, name, note);
                return;
            }
            if (!DrawInput(c, in f, o, page, bounds, o.Fit == FitMode.Tile ? FitMode.Fit : o.Fit, pc.FillAA(SKColors.White), key, out var placed))
            {
                PlaceholderCard(c, in f, name, page.StatusText);
                return;
            }
            // The desk clicks into the page through this box, and the room sees the pointer when
            // asked — while the page is the right way up: a turned or mirrored page has no pointer.
            if (!placed.Transformed)
            {
                var crop = o.Crop;
                if (!f.Ctx.IsFadeSource && !f.Ctx.InMultiview && !f.Ctx.InLayer)
                {
                    f.Sink.Hits.Add(new HitRect(HitKind.WebPage, placed.Dest, false, key, crop, bounds));
                }
                if (o.WebShowPointer && page is IWebSource web) WebPointer.Draw(c, placed.Dest, in crop, web, f.Ctx.UtcNow, pc);
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
            // Tiles repeat the area of interest at its own size (a turn or a flip does not apply to a tiling).
            var src = o.Crop.SourceRect(new SKSizeI(image.Width, image.Height));
            var tw = Math.Max(1f, src.Width);
            var th = Math.Max(1f, src.Height);
            for (float y = 0; y < f.H; y += th)
            {
                for (float x = 0; x < f.W; x += tw)
                {
                    c.DrawImage(image, src, SKRect.Create(x, y, tw, th), DrawUtil.Smooth, pc.FillAA(SKColors.White));
                }
            }
        }
        else
        {
            DrawImageInput(c, in f, o, image, bounds, o.Fit, pc.FillAA(SKColors.White));
        }
    }

    /// <summary>
    /// Where an input's picture lands through its area of interest, flips and turn: the box on
    /// the canvas, and — once the canvas is centred on that box and turned — the box of the
    /// picture's own (unturned) shape around the origin to draw into.
    /// </summary>
    internal readonly record struct InputPlacement(SKRect Dest, SKRect Local, bool Transformed);

    /// <summary>The placement for a frame of this size: the part that survives the crop takes the picture's place, turned when asked.</summary>
    internal static InputPlacement Place(MediaOptions o, SKSizeI frame, SKRect bounds, FitMode fit)
    {
        var src = o.Crop.SourceRect(frame);
        var cw = Math.Max(1, (int)Math.Round(src.Width));
        var ch = Math.Max(1, (int)Math.Round(src.Height));
        var quarter = o.RotateQuarters & 3;
        var swapped = quarter is 1 or 3;
        var shown = swapped ? new SKSizeI(ch, cw) : new SKSizeI(cw, ch);
        var dest = DrawUtil.Fit(shown, bounds, fit);
        var transformed = quarter != 0 || o.FlipHorizontal || o.FlipVertical;
        var local = swapped
            ? SKRect.Create(-dest.Height / 2, -dest.Width / 2, dest.Height, dest.Width)
            : SKRect.Create(-dest.Width / 2, -dest.Height / 2, dest.Width, dest.Height);
        return new InputPlacement(dest, local, transformed);
    }

    /// <summary>Centres the canvas on the box and turns and flips it; -1 when nothing is to be done (the flips apply before the turn).</summary>
    private static int BeginTransform(SKCanvas c, in InputPlacement p, MediaOptions o)
    {
        if (!p.Transformed) return -1;
        var save = c.Save();
        c.Translate(p.Dest.MidX, p.Dest.MidY);
        c.RotateDegrees(90 * (o.RotateQuarters & 3));
        c.Scale(o.FlipHorizontal ? -1 : 1, o.FlipVertical ? -1 : 1);
        return save;
    }

    /// <summary>A live input's newest frame through the area of interest, flips and turn; false when the source has no frame yet.</summary>
    private static bool DrawInput(SKCanvas c, in PatternFrame f, MediaOptions o, IVideoFrameSource source, SKRect bounds, FitMode fit, SKPaint paint, string key, out InputPlacement placed)
    {
        if (source.FrameSize is not { } size)
        {
            // No frame yet: nothing to place; a source paints its own waiting state into the bounds, if any.
            placed = new InputPlacement(bounds, bounds, false);
            return source.DrawFrame(c, bounds, paint);
        }
        placed = Place(o, size, bounds, fit);
        var crop = o.Crop;
        var save = BeginTransform(c, in placed, o);
        var drew = source.DrawFrame(c, placed.Transformed ? placed.Local : placed.Dest, paint, in crop);
        if (save >= 0) c.RestoreToCount(save);
        NoteInput(in f, in placed, key, in crop, bounds);
        return drew;
    }

    /// <summary>A still through the area of interest, flips and turn.</summary>
    private static void DrawImageInput(SKCanvas c, in PatternFrame f, MediaOptions o, SKImage image, SKRect bounds, FitMode fit, SKPaint paint)
    {
        var size = new SKSizeI(image.Width, image.Height);
        var placed = Place(o, size, bounds, fit);
        var crop = o.Crop;
        var src = crop.SourceRect(size);
        var save = BeginTransform(c, in placed, o);
        c.DrawImage(image, src, placed.Transformed ? placed.Local : placed.Dest, DrawUtil.Smooth, paint);
        if (save >= 0) c.RestoreToCount(save);
        NoteInput(in f, in placed, "", in crop, bounds);
    }

    /// <summary>The desk's handle on the picture — the area-of-interest pick reads where it is and what it already cuts; only the top-level draw records it.</summary>
    private static void NoteInput(in PatternFrame f, in InputPlacement placed, string key, in FrameCrop crop, SKRect bounds)
    {
        if (f.Ctx.IsFadeSource || f.Ctx.InMultiview || f.Ctx.InLayer) return;
        f.Sink.Hits.Add(new HitRect(HitKind.MediaPicture, placed.Dest, false, key, crop, bounds));
    }

    /// <summary>Audio-only media shows a clean card instead of "waiting for first frame".</summary>
    private static void AudioCard(SKCanvas c, in PatternFrame f, string path)
    {
        var mount = InputBus.For(InputKeys.Video(path));
        var status = mount is { IsPlaying: true }
            ? "Audio playing — sound only"
            : mount?.StatusText ?? VideoService.AvailabilityNote;
        if (string.IsNullOrEmpty(status)) status = "Starting audio…";
        PlaceholderCard(c, in f, "♪  " + System.IO.Path.GetFileName(path), status);
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
        // Configure only when something could have changed — keeps the 60 fps path allocation-free.
        if (f.Sink.ParticlesConfiguredVersion != f.Snapshot.Version || f.Sink.ParticlesConfiguredCanvas != f.Canvas)
        {
            sim.Configure(o, f.Snapshot, f.Canvas);
            f.Sink.ParticlesConfiguredVersion = f.Snapshot.Version;
            f.Sink.ParticlesConfiguredCanvas = f.Canvas;
        }
        sim.Advance(f.Ctx.Time);
        sim.Render(c, f.Paints);
        Effects.EffectFlash.Draw(c, f.W, f.H, Effects.EffectImpulses.SurgeAt(f.Ctx.Time).Flash, f.Paints);
    }
}
