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

    /// <summary>A brand kit's thumbnail: its colours as bands, the company name over them. Null on any failure.</summary>
    public static Bitmap? Swatch(IReadOnlyList<string> hexColors, string caption)
    {
        try
        {
            var info = new SKImageInfo(Width, Height, SKColorType.Bgra8888, SKAlphaType.Premul);
            using var surface = SKSurface.Create(info);
            if (surface is null) return null;
            var canvas = surface.Canvas;
            canvas.Clear(SKColors.Black);
            var colors = hexColors.Select(h => SKColor.TryParse(h, out var c) ? c : SKColors.Gray).ToList();
            if (colors.Count == 0) colors.Add(SKColors.Gray);
            var band = (float)Width / colors.Count;
            using var paint = new SKPaint { IsAntialias = false };
            for (var i = 0; i < colors.Count; i++)
            {
                paint.Color = colors[i];
                canvas.DrawRect(i * band, 0, band + 1, Height, paint);
            }
            if (caption.Length > 0)
            {
                using var font = new SKFont { Size = 20 };
                using var shadow = new SKPaint { Color = new SKColor(0, 0, 0, 160), IsAntialias = true };
                using var text = new SKPaint { Color = SKColors.White, IsAntialias = true };
                var width = font.MeasureText(caption);
                var x = Math.Max(8, (Width - width) / 2);
                canvas.DrawText(caption, x + 1, Height / 2f + 8, SKTextAlign.Left, font, shadow);
                canvas.DrawText(caption, x, Height / 2f + 7, SKTextAlign.Left, font, text);
            }
            canvas.Flush();
            using var image = surface.Snapshot();
            using var png = image.Encode(SKEncodedImageFormat.Png, 90);
            using var stream = new MemoryStream();
            png.SaveTo(stream);
            stream.Position = 0;
            return new Bitmap(stream);
        }
        catch (Exception ex)
        {
            Log.Warn("Swatch render failed.", ex);
            return null;
        }
    }
}
