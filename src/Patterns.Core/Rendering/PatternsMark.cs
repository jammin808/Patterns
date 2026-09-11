using SkiaSharp;

namespace Patterns.Core.Rendering;

/// <summary>
/// The app's own mark, in one place: the icon (a dark tile, a light 4×4 grid, a cyan cross) and
/// the PATTERNS wordmark, letter-spaced.
///
/// It lived inside the badge overlay, which was right while the badge was the only thing that
/// drew it. A test card that carries the mark as PART OF THE CARD rather than as a sticker over
/// one cannot reach into an overlay for it, and two hand-drawn copies of a logo drift — the grid
/// gains a line in one of them a year later and nobody notices which is the real one. So the mark
/// is its own thing and both draw it.
///
/// Drawn by hand out of primitives rather than loaded from a file: it has to be crisp on a 4 K
/// wall, legible on a 96-pixel monitor-wall tile, and present on the three sinks that have no
/// graphics card — and it must never be the thing that is missing because a file did not travel.
///
/// These are the APP's colours and never the show's brand kit. The mark names the maker; the kit
/// dresses the client's show.
/// </summary>
public static class PatternsMark
{
    public static readonly SKColor Card = new(0x12, 0x15, 0x1C);
    public static readonly SKColor Tile = new(0x17, 0x1A, 0x21);
    public static readonly SKColor Cyan = new(0x3E, 0xC1, 0xF3);
    public static readonly SKColor Magenta = new(0xF0, 0x3E, 0xAE);
    public static readonly SKColor Mist = new(0xB8, 0xC0, 0xCC);

    /// <summary>The wordmark, a letter at a time, because it is set with its own tracking.</summary>
    public static readonly string[] Letters = { "P", "A", "T", "T", "E", "R", "N", "S" };

    /// <summary>The tracking the wordmark is set at, for a given type size.</summary>
    public static float SpacingFor(float textSize) => textSize * 0.14f;

    /// <summary>How wide the wordmark comes out at this font and tracking.</summary>
    public static float MeasureWordmark(SKFont font, float spacing)
    {
        float w = 0;
        foreach (var letter in Letters) w += font.MeasureText(letter) + spacing;
        return w - spacing;
    }

    /// <summary>The wordmark from a left edge and a baseline.</summary>
    public static void Wordmark(SKCanvas c, float x, float baseline, SKFont font, SKPaint paint, float spacing)
    {
        foreach (var letter in Letters)
        {
            c.DrawText(letter, x, baseline, SKTextAlign.Left, font, paint);
            x += font.MeasureText(letter) + spacing;
        }
    }

    /// <summary>The wordmark centred on a point.</summary>
    public static void WordmarkCentered(SKCanvas c, float cx, float baseline, SKFont font, SKPaint paint)
    {
        var spacing = SpacingFor(font.Size);
        Wordmark(c, cx - MeasureWordmark(font, spacing) / 2f, baseline, font, paint, spacing);
    }

    /// <summary>The app's icon by hand: a dark rounded tile, a light 4×4 grid, a cyan cross across the middle.</summary>
    public static void Icon(SKCanvas c, PaintCache pc, SKRect r, byte alpha = 255)
    {
        var s = r.Width;
        if (s < 2) return;
        var radius = s * 0.2f;
        c.DrawRoundRect(r, radius, radius, pc.FillAA(Tile.WithAlpha(alpha)));
        var save = c.Save();
        c.ClipRoundRect(new SKRoundRect(r, radius, radius), antialias: true);
        var grid = pc.StrokeAA(Mist.WithAlpha((byte)(alpha * 0.7f)), Math.Max(1f, s * 0.05f));
        for (var i = 1; i < 4; i++)
        {
            var p = s * i / 4f;
            c.DrawLine(r.Left + p, r.Top, r.Left + p, r.Bottom, grid);
            c.DrawLine(r.Left, r.Top + p, r.Right, r.Top + p, grid);
        }
        c.RestoreToCount(save);
        var arm = s * 0.42f;
        var t = Math.Max(1.5f, s * 0.085f);
        var cross = pc.FillAA(Cyan.WithAlpha(alpha));
        c.DrawRoundRect(SKRect.Create(r.MidX - arm / 2, r.MidY - t / 2, arm, t), t / 2, t / 2, cross);
        c.DrawRoundRect(SKRect.Create(r.MidX - t / 2, r.MidY - arm / 2, t, arm), t / 2, t / 2, cross);
    }
}
