using Patterns.Core.Model;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.Core.Rendering;

/// <summary>
/// Per-sink paint/font pool. The render hot path mutates these cached objects instead of
/// allocating — a sink renders on exactly one thread, so this is safe by construction.
/// </summary>
public sealed class PaintCache : IDisposable
{
    private readonly SKPaint _fill = new() { IsAntialias = false, Style = SKPaintStyle.Fill };
    private readonly SKPaint _stroke = new() { IsAntialias = false, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
    private readonly SKPaint _fillAA = new() { IsAntialias = true, Style = SKPaintStyle.Fill };
    private readonly SKPaint _strokeAA = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 1 };
    private readonly SKPaint _text = new() { IsAntialias = true, Style = SKPaintStyle.Fill };

    public SKFont FontRegular { get; } = new(Typefaces.Regular, 16) { Subpixel = true, Edging = SKFontEdging.SubpixelAntialias };
    public SKFont FontBold { get; } = new(Typefaces.SemiBold, 16) { Subpixel = true, Edging = SKFontEdging.SubpixelAntialias };

    public SKPath ScratchPath { get; } = new();

    /// <summary>Aliased fill — for pixel-exact rects.</summary>
    public SKPaint Fill(SKColor color)
    {
        _fill.Color = color;
        _fill.Shader = null;
        _fill.BlendMode = SKBlendMode.SrcOver;
        return _fill;
    }

    /// <summary>Aliased 1px stroke — for pixel-exact hairlines.</summary>
    public SKPaint Stroke(SKColor color, float width = 1)
    {
        _stroke.Color = color;
        _stroke.StrokeWidth = width;
        _stroke.PathEffect = null;
        return _stroke;
    }

    public SKPaint FillAA(SKColor color)
    {
        _fillAA.Color = color;
        _fillAA.Shader = null;
        _fillAA.BlendMode = SKBlendMode.SrcOver;
        return _fillAA;
    }

    public SKPaint StrokeAA(SKColor color, float width = 1, SKPathEffect? dash = null)
    {
        _strokeAA.Color = color;
        _strokeAA.StrokeWidth = width;
        _strokeAA.PathEffect = dash;
        return _strokeAA;
    }

    public SKPaint Text(SKColor color)
    {
        _text.Color = color;
        return _text;
    }

    public void Dispose()
    {
        _fill.Dispose();
        _stroke.Dispose();
        _fillAA.Dispose();
        _strokeAA.Dispose();
        _text.Dispose();
        FontRegular.Dispose();
        FontBold.Dispose();
        ScratchPath.Dispose();
    }
}

/// <summary>Resolved colours for one frame. Measurement lines stay neutral; accents take branding.</summary>
public readonly struct Palette
{
    public required SKColor Bg { get; init; }
    public required SKColor Line { get; init; }
    public required SKColor SubtleLine { get; init; }
    public required SKColor Accent { get; init; }
    public required SKColor Secondary { get; init; }
    public required SKColor Text { get; init; }
    public required SKColor ChipBg { get; init; }
    public required bool Branded { get; init; }

    public static Palette Resolve(ShowSnapshot s)
    {
        var brand = s.State.Brand;
        var branded = brand.ApplyToPatterns;
        var accent = s.Color(brand.PrimaryColor, new SKColor(0x3E, 0xC1, 0xF3));
        var secondary = s.Color(brand.SecondaryColor, new SKColor(0xF0, 0x3E, 0xAE));
        return new Palette
        {
            Bg = branded ? s.Color(brand.BackgroundColor, SKColors.Black) : SKColors.Black,
            Line = SKColors.White,
            SubtleLine = new SKColor(0xFF, 0xFF, 0xFF, 0x46),
            Accent = accent,
            Secondary = secondary,
            Text = branded ? s.Color(brand.TextColor, SKColors.White) : SKColors.White,
            ChipBg = new SKColor(0x00, 0x00, 0x00, 0xB4),
            Branded = branded,
        };
    }
}
