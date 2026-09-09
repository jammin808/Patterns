using System.Runtime.InteropServices;
using Patterns.Core.Model;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.Core.Effects;

/// <summary>The CPU path's reactive frame: a low-resolution bitmap and the buffer it is written through, reused frame to frame.</summary>
public sealed class ReactiveSurface : IDisposable
{
    public ReactiveSurface(SKSizeI size)
    {
        Bitmap = new SKBitmap(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        Pixels = new int[size.Width * size.Height];
    }

    public SKBitmap Bitmap { get; }

    public int[] Pixels { get; }

    public SKSizeI Size => new(Bitmap.Width, Bitmap.Height);

    public void Dispose() => Bitmap.Dispose();
}

/// <summary>
/// Draws a reactive scene on the CPU — NDI, the stream, thumbnails, and any sink whose shader
/// would not compile. This is the path that matters most for the client's stream and the one the
/// graphics card never helps with, so it draws small and upscales: the scenes are closed-form, one
/// sample a pixel, and a 240-pixel working width at fifty frames is a few million samples a second
/// rather than a hundred.
/// </summary>
public static class ReactiveRaster
{
    private static int _parallelism = DefaultParallelism;

    /// <summary>
    /// Half the cores, at least one — the same rule the fractal raster keeps, and for the same
    /// reason: a frame is drawn per CPU sink, and one that took every core would starve the audio,
    /// the decoders and the desk on the small machines that use this path.
    /// </summary>
    public static int DefaultParallelism => Math.Max(1, Environment.ProcessorCount / 2);

    /// <summary>How many rows render at once; the picture is the same at any value.</summary>
    public static int Parallelism
    {
        get => _parallelism;
        set => _parallelism = Math.Max(1, value);
    }

    /// <summary>The working width per quality; the height follows the canvas' shape, and the ladder's scale shrinks it.</summary>
    public static SKSizeI SizeFor(ReactiveQuality quality, SKSizeI canvas, double scale = 1)
    {
        var width = quality switch
        {
            ReactiveQuality.Fast => 160,
            ReactiveQuality.Fine => 360,
            _ => 240,
        };
        if (scale < 1) width = Math.Max(32, (int)Math.Round(width * Math.Clamp(scale, 0.1, 1)));
        var w = Math.Max(1, Math.Min(width, canvas.Width));
        var h = Math.Max(1, (int)Math.Round(w * canvas.Height / (double)Math.Max(1, canvas.Width)));
        return new SKSizeI(w, h);
    }

    /// <summary>Fills the surface (a new one when the size changed) from the view; returns the surface to draw.</summary>
    public static ReactiveSurface Render(ReactiveSurface? reuse, SKSizeI size, ReactiveScene scene, IReadOnlyList<SKColor> palette, in ReactiveView view)
    {
        var surface = reuse;
        if (surface is null || surface.Size != size)
        {
            reuse?.Dispose();
            surface = new ReactiveSurface(size);
        }
        var w = size.Width;
        var h = size.Height;
        var pixels = surface.Pixels;
        var colors = palette.Count > 0 ? palette : new[] { SKColors.White };
        // The pixel's place with the centre at 0 and the height at 1: a wide canvas sees more of
        // the scene rather than a stretched one, which is what a span across three screens wants.
        var scale = 1.0 / h;
        var v = view;
        var options = new ParallelOptions { MaxDegreeOfParallelism = Parallelism };
        Parallel.For(0, h, options, y =>
        {
            var row = y * w;
            var py = (y + 0.5 - h / 2.0) * scale;
            for (var x = 0; x < w; x++)
            {
                var px = (x + 0.5 - w / 2.0) * scale;
                var s = ReactiveField.Sample(scene, px, py, in v);
                pixels[row + x] = (int)(uint)ReactiveField.Map(s, colors, v.Brightness);
            }
        });
        Marshal.Copy(pixels, 0, surface.Bitmap.GetPixels(), pixels.Length);
        surface.Bitmap.NotifyPixelsChanged();
        return surface;
    }

    /// <summary>The colours a scene cycles through: the brand kit, or the list as written; white when neither reads.</summary>
    public static SKColor[] PaletteOf(string csv)
    {
        var parsed = ColorUtil.ParseList(csv, SKColors.White);
        return parsed.Length == 0 ? new[] { SKColors.White } : parsed.Take(ReactiveField.PaletteColors).ToArray();
    }
}
