using System.Runtime.InteropServices;
using Patterns.Core.Model;
using SkiaSharp;

namespace Patterns.Core.Effects;

/// <summary>
/// The view of the plane one frame draws: the centre, the plane units per canvas height, the
/// animated Julia constant, the palette drift and the brightness — with the sound folded in.
/// Pure, so every sink computes the same view from the same clock.
/// </summary>
public readonly record struct FractalView(double CenterX, double CenterY, double Span, double JuliaRe, double JuliaIm,
                                          int Iterations, double PaletteOffset, double Brightness, double Time)
{
    /// <summary>Plane units across the whole canvas height at zoom 1.</summary>
    public const double BaseSpan = 2.4;

    public static FractalView Of(FractalOptions o, double time, AudioLevelFrame audio, int iterationCap = 1024)
    {
        var amount = o.AudioSource == AudioSourceKind.None ? 0 : o.AudioAmount;
        var speed = o.Speed;
        var pulse = 1 + 0.08 * Math.Sin(time * speed * 0.6) + audio.Level * 0.35 * amount;
        var span = BaseSpan / (o.Zoom * Math.Max(0.5, pulse));
        var cr = o.JuliaReal + 0.06 * Math.Sin(time * speed) * (1 + audio.Mid * amount);
        var ci = o.JuliaImag + 0.06 * Math.Cos(time * speed * 0.77);
        var offset = time * speed * 0.05 + audio.Low * 0.3 * amount;
        var bright = 1 + audio.High * 0.5 * amount;
        return new FractalView(o.CenterX, o.CenterY, span, cr, ci, Math.Min(o.Iterations, iterationCap), offset, bright, time);
    }

    /// <summary>Plane units per pixel on a canvas of this height.</summary>
    public double UnitsPerPixel(int height) => Span / Math.Max(1, height);

    public (double X, double Y) ToPlane(double px, double py, int w, int h)
    {
        var upp = UnitsPerPixel(h);
        return (CenterX + (px - w / 2.0) * upp, CenterY + (py - h / 2.0) * upp);
    }
}

/// <summary>Palette lookup shared by the CPU path and the tests; the shader does the same in SkSL.</summary>
public static class FractalColor
{
    /// <summary>A cyclic ramp through the palette; <paramref name="u"/> wraps.</summary>
    public static SKColor Cycle(IReadOnlyList<SKColor> palette, double u, double brightness)
    {
        if (palette.Count == 0) return SKColors.White;
        var f = (u - Math.Floor(u)) * palette.Count;
        var i = (int)Math.Floor(f);
        var k = f - i;
        var a = palette[i % palette.Count];
        var b = palette[(i + 1) % palette.Count];
        return Mix(a, b, k, brightness);
    }

    public static SKColor Map(FractalKind kind, double v, IReadOnlyList<SKColor> palette, double offset, double brightness)
    {
        switch (kind)
        {
            case FractalKind.Newton:
            {
                if (palette.Count == 0) return SKColors.White;
                var root = Math.Clamp((int)Math.Floor(v * 3), 0, 2);
                var speed = v * 3 - root;
                var shade = 1 - speed * 0.8;
                var rotate = (int)Math.Floor(offset * palette.Count);
                var index = ((root + rotate) % palette.Count + palette.Count) % palette.Count;
                return Mix(palette[index], palette[index], 0, shade * brightness);
            }
            case FractalKind.DomainWarp:
                return Cycle(palette, v * 1.5 + offset, brightness);
            default:
                return v >= 1 ? SKColors.Black : Cycle(palette, v * 3 + offset, brightness);
        }
    }

    private static SKColor Mix(SKColor a, SKColor b, double k, double brightness)
    {
        static byte Ch(byte x, byte y, double k, double g) => (byte)Math.Clamp((x + (y - x) * k) * g, 0, 255);
        return new SKColor(Ch(a.Red, b.Red, k, brightness), Ch(a.Green, b.Green, k, brightness), Ch(a.Blue, b.Blue, k, brightness));
    }
}

/// <summary>The CPU path's frame: a low-resolution bitmap and the buffer it is written through, reused frame to frame.</summary>
public sealed class FractalSurface : IDisposable
{
    public FractalSurface(SKSizeI size)
    {
        Bitmap = new SKBitmap(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
        Pixels = new int[size.Width * size.Height];
    }

    public SKBitmap Bitmap { get; }

    public int[] Pixels { get; }

    public SKSizeI Size => new(Bitmap.Width, Bitmap.Height);

    public void Dispose() => Bitmap.Dispose();
}

/// <summary>Draws a fractal on the CPU at a modest resolution — NDI, thumbnails, and any sink whose shader would not compile.</summary>
public static class FractalRaster
{
    /// <summary>The working width per quality; the height follows the canvas' shape.</summary>
    public static SKSizeI SizeFor(FractalQuality quality, SKSizeI canvas)
    {
        var width = quality switch
        {
            FractalQuality.Fast => 160,
            FractalQuality.Fine => 320,
            _ => 240,
        };
        var w = Math.Max(1, Math.Min(width, canvas.Width));
        var h = Math.Max(1, (int)Math.Round(w * canvas.Height / (double)Math.Max(1, canvas.Width)));
        return new SKSizeI(w, h);
    }

    /// <summary>Fills the surface (allocating a new one when the size changed) from the view; returns the surface to draw.</summary>
    public static FractalSurface Render(FractalSurface? reuse, SKSizeI size, FractalKind kind, IReadOnlyList<SKColor> palette, FractalView view)
    {
        var surface = reuse;
        if (surface is null || surface.Size != size)
        {
            reuse?.Dispose();
            surface = new FractalSurface(size);
        }
        var w = size.Width;
        var h = size.Height;
        var pixels = surface.Pixels;
        var colors = palette.Count > 0 ? palette : new[] { SKColors.White };
        Parallel.For(0, h, y =>
        {
            var row = y * w;
            for (var x = 0; x < w; x++)
            {
                var (px, py) = view.ToPlane(x + 0.5, y + 0.5, w, h);
                var v = FractalMath.Sample(kind, px, py, view.JuliaRe, view.JuliaIm, view.Iterations, view.Time);
                pixels[row + x] = (int)(uint)FractalColor.Map(kind, v, colors, view.PaletteOffset, view.Brightness);
            }
        });
        Marshal.Copy(pixels, 0, surface.Bitmap.GetPixels(), pixels.Length);
        surface.Bitmap.NotifyPixelsChanged();
        return surface;
    }
}
