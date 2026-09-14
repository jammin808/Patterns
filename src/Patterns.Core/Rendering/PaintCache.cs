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

    private readonly Dictionary<(string Family, bool Bold), SKFont> _familyFonts = new();

    /// <summary>
    /// Font for a system family name (overlay text); empty/unknown falls back to the built-in
    /// Inter so text never disappears. Cached per sink.
    /// </summary>
    public SKFont FontFor(string? family, bool bold)
    {
        if (string.IsNullOrWhiteSpace(family))
        {
            return bold ? FontBold : FontRegular;
        }
        var key = (family, bold);
        if (_familyFonts.TryGetValue(key, out var cached)) return cached;

        SKTypeface? tf = null;
        try
        {
            tf = SKFontManager.Default.MatchFamily(family, bold ? SKFontStyle.Bold : SKFontStyle.Normal);
        }
        catch
        {
            // Fall through to the embedded face.
        }
        tf ??= bold ? Typefaces.SemiBold : Typefaces.Regular;
        var font = new SKFont(tf, 16) { Subpixel = true, Edging = SKFontEdging.SubpixelAntialias };
        _familyFonts[key] = font;
        return font;
    }

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

    private SKRoundRect? _roundRect;

    /// <summary>One round rect to clip or draw with, set to the rect and radius asked for — never a new object per frame.</summary>
    public SKRoundRect RoundRect(SKRect rect, float radius)
    {
        _roundRect ??= new SKRoundRect();
        _roundRect.SetRect(rect, radius, radius);
        return _roundRect;
    }

    /// <summary>How many shaped texts a sink keeps before it starts again: the overlays' still words at their sizes, not a clock's every second.</summary>
    public const int BlobsKept = 64;

    private readonly Dictionary<(string Text, SKFont Font, float Size), SKTextBlob> _blobs = new();
    private readonly Dictionary<(string[] Letters, SKFont Font, float Size, float Spacing), (SKTextBlob Blob, float Width)> _spaced = new();

    /// <summary>
    /// A text shaped once at a font's size and drawn every frame after as one blob: Skia's string
    /// draw shapes a new blob per call, and a badge or a chip that never changes was paying that
    /// every frame of every sink. Bounded — a text that changes every second cycles the table,
    /// which is cheaper than what it replaces.
    /// </summary>
    public SKTextBlob TextBlob(string text, SKFont font)
    {
        var key = (text, font, font.Size);
        if (_blobs.TryGetValue(key, out var hit)) return hit;
        if (_blobs.Count >= BlobsKept) ForgetBlobs();
        var blob = SKTextBlob.Create(text, font) ?? SKTextBlob.Create(" ", font)!;
        _blobs[key] = blob;
        return blob;
    }

    /// <summary>A word drawn letter by letter with a set spacing — the badge's name — as one positioned blob, with its width; built when the size or the spacing changes.</summary>
    public (SKTextBlob Blob, float Width) SpacedWord(string[] letters, SKFont font, float spacing)
    {
        var key = (letters, font, font.Size, spacing);
        if (_spaced.TryGetValue(key, out var hit)) return hit;
        if (_spaced.Count >= BlobsKept) ForgetBlobs();
        using var builder = new SKTextBlobBuilder();
        var glyphs = new List<ushort>(letters.Length);
        var positions = new List<SKPoint>(letters.Length);
        float x = 0;
        foreach (var letter in letters)
        {
            var advance = font.MeasureText(letter);
            var letterGlyphs = font.GetGlyphs(letter);
            if (letterGlyphs.Length == 1)
            {
                glyphs.Add(letterGlyphs[0]);
                positions.Add(new SKPoint(x, 0));
            }
            else if (letterGlyphs.Length > 1)
            {
                // A letter of several glyphs (a ligature's parts): each at its own advance inside the letter.
                var widths = font.GetGlyphWidths(letterGlyphs);
                var gx = x;
                for (var i = 0; i < letterGlyphs.Length; i++)
                {
                    glyphs.Add(letterGlyphs[i]);
                    positions.Add(new SKPoint(gx, 0));
                    gx += widths[i];
                }
            }
            x += advance + spacing;
        }
        var run = builder.AllocatePositionedRun(font, glyphs.Count);
        run.SetGlyphs(glyphs.ToArray());
        run.SetPositions(positions.ToArray());
        var blob = builder.Build() ?? SKTextBlob.Create(" ", font)!;
        var result = (blob, Math.Max(0, x - spacing));
        _spaced[key] = result;
        return result;
    }

    /// <summary>The shaped texts kept so far (tests read it).</summary>
    public int BlobCount => _blobs.Count + _spaced.Count;

    private void ForgetBlobs()
    {
        foreach (var b in _blobs.Values) b.Dispose();
        _blobs.Clear();
        foreach (var s in _spaced.Values) s.Blob.Dispose();
        _spaced.Clear();
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
        foreach (var f in _familyFonts.Values)
        {
            f.Dispose();
        }
        _familyFonts.Clear();
        ScratchPath.Dispose();
        ForgetBlobs();
        _roundRect?.Dispose();
        _roundRect = null;
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
