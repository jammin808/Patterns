using Avalonia.Media.Imaging;
using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.App.Services;

/// <summary>Renders pattern configs through the real engine into small preview bitmaps.</summary>
public static class ThumbnailRenderer
{
    public const int Width = 256;
    public const int Height = 144;

    /// <summary>Renders a thumbnail off the UI thread; returns null on any failure.</summary>
    public static Bitmap? Render(ShowState baseState, PatternConfig pattern)
    {
        try
        {
            var state = JsonUtil.Clone(baseState);
            ModelCopier.Copy(pattern, state.Pattern);
            state.Blackout = false;
            // Thumbnails show the pattern itself, not the show overlays.
            state.Overlays.Clock.Enabled = false;
            state.Overlays.Info.Enabled = false;
            state.Overlays.Message.Enabled = false;
            state.Overlays.Logo.Enabled = false;
            state.Countdown.Enabled = false;

            var snap = new ShowSnapshot { State = state, Version = 1 };
            var engine = new PatternEngine();
            using var sink = new SinkState();
            var info = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            if (surface is null) return null;

            var ctx = new RenderContext
            {
                ViewportSize = new SKSizeI(Width, Height),
                ReferenceSize = new SKSizeI(Width, Height),
                Time = 1.2,
                Now = DateTime.Now,
                UtcNow = DateTime.UtcNow,
                Frame = 1,
                Sink = SinkKind.Thumbnail,
                SinkLabel = "thumb",
                SinkIndex = 0,
            };
            engine.Render(surface.Canvas, snap, in ctx, sink);
            surface.Canvas.Flush();

            using var image = surface.Snapshot();
            using var png = image.Encode(SKEncodedImageFormat.Png, 90);
            using var stream = new MemoryStream();
            png.SaveTo(stream);
            stream.Position = 0;
            return new Bitmap(stream);
        }
        catch (Exception ex)
        {
            Log.Warn("Thumbnail render failed.", ex);
            return null;
        }
    }
}
