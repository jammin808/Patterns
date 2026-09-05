using Patterns.Core.Model;
using SkiaSharp;

namespace Patterns.Core.Rendering;

/// <summary>
/// Pixel-exact drawing helpers. Convention: coordinates are integer pixel indices;
/// a “line at x” fills exactly the pixel column x (half-pixel offsets are handled here,
/// with antialiasing off).
/// </summary>
public static class DrawUtil
{
    public static readonly SKSamplingOptions Smooth = new(SKFilterMode.Linear, SKMipmapMode.Linear);
    public static readonly SKSamplingOptions Nearest = new(SKFilterMode.Nearest, SKMipmapMode.None);

    public static readonly SKPathEffect DashShort = SKPathEffect.CreateDash(new float[] { 4, 4 }, 0);
    public static readonly SKPathEffect DashLong = SKPathEffect.CreateDash(new float[] { 10, 6 }, 0);

    private static readonly string[] CharCache = BuildCharCache();

    private static string[] BuildCharCache()
    {
        var arr = new string[128];
        for (var c = 32; c < 128; c++) arr[c] = ((char)c).ToString();
        return arr;
    }

    public static string CharString(char c) => c < 128 ? CharCache[c] : c.ToString();

    // ---- pixel-exact primitives --------------------------------------------

    /// <summary>Horizontal 1px-multiple line occupying rows [y, y+width).</summary>
    public static void LineH(SKCanvas c, int y, int x0, int x1, int width, SKPaint fill)
        => c.DrawRect(SKRect.Create(x0, y, x1 - x0, width), fill);

    /// <summary>Vertical 1px-multiple line occupying columns [x, x+width).</summary>
    public static void LineV(SKCanvas c, int x, int y0, int y1, int width, SKPaint fill)
        => c.DrawRect(SKRect.Create(x, y0, width, y1 - y0), fill);

    /// <summary>Border of exactly <paramref name="width"/> px drawn inside the rect.</summary>
    public static void BorderInside(SKCanvas c, SKRectI r, int width, SKPaint fill)
    {
        if (r.Width <= 0 || r.Height <= 0) return;
        var w = Math.Min(width, Math.Min(r.Width, r.Height) / 2);
        if (w <= 0) w = 1;
        c.DrawRect(SKRect.Create(r.Left, r.Top, r.Width, w), fill);                     // top
        c.DrawRect(SKRect.Create(r.Left, r.Bottom - w, r.Width, w), fill);              // bottom
        c.DrawRect(SKRect.Create(r.Left, r.Top + w, w, r.Height - 2 * w), fill);        // left
        c.DrawRect(SKRect.Create(r.Right - w, r.Top + w, w, r.Height - 2 * w), fill);   // right
    }

    /// <summary>Center cross with arms of the given half-length and pixel thickness.</summary>
    public static void Cross(SKCanvas c, int cx, int cy, int halfLen, int thickness, SKPaint fill)
    {
        var t = Math.Max(1, thickness);
        var off = t / 2;
        LineH(c, cy - off, cx - halfLen, cx + halfLen, t, fill);
        LineV(c, cx - off, cy - halfLen, cy + halfLen, t, fill);
    }

    /// <summary>Diagonal hatch inside a rect (used for blend zones and bezel areas).</summary>
    public static void Hatch(SKCanvas c, SKRect r, float spacing, SKPaint strokeAA)
    {
        c.Save();
        c.ClipRect(r);
        var span = r.Width + r.Height;
        for (var d = -r.Height; d < span; d += spacing)
        {
            c.DrawLine(r.Left + d, r.Top, r.Left + d + r.Height, r.Bottom, strokeAA);
        }
        c.Restore();
    }

    // ---- layout helpers -----------------------------------------------------

    public static SKRect Fit(SKSizeI content, SKRect bounds, FitMode mode)
    {
        float cw = content.Width, ch = content.Height;
        if (cw <= 0 || ch <= 0) return bounds;
        switch (mode)
        {
            case FitMode.Stretch:
                return bounds;
            case FitMode.Center:
            {
                var l = bounds.MidX - cw / 2;
                var t = bounds.MidY - ch / 2;
                return SKRect.Create(l, t, cw, ch);
            }
            case FitMode.Fill:
            {
                var s = Math.Max(bounds.Width / cw, bounds.Height / ch);
                var w = cw * s;
                var h = ch * s;
                return SKRect.Create(bounds.MidX - w / 2, bounds.MidY - h / 2, w, h);
            }
            case FitMode.Fit:
            default:
            {
                var s = Math.Min(bounds.Width / cw, bounds.Height / ch);
                var w = cw * s;
                var h = ch * s;
                return SKRect.Create(bounds.MidX - w / 2, bounds.MidY - h / 2, w, h);
            }
        }
    }

    /// <summary>Places a box of the given size at one of nine anchors with a margin.</summary>
    public static SKRect Anchored(SKSizeI canvas, float w, float h, Anchor9 anchor, float margin)
    {
        float x = anchor switch
        {
            Anchor9.TopLeft or Anchor9.MiddleLeft or Anchor9.BottomLeft => margin,
            Anchor9.TopRight or Anchor9.MiddleRight or Anchor9.BottomRight => canvas.Width - margin - w,
            _ => (canvas.Width - w) / 2f,
        };
        float y = anchor switch
        {
            Anchor9.TopLeft or Anchor9.TopCenter or Anchor9.TopRight => margin,
            Anchor9.BottomLeft or Anchor9.BottomCenter or Anchor9.BottomRight => canvas.Height - margin - h,
            _ => (canvas.Height - h) / 2f,
        };
        return SKRect.Create(x, y, w, h);
    }

    /// <summary>An anchored box nudged by a share of the canvas on each axis (a dragged overlay).</summary>
    public static SKRect Anchored(SKSizeI canvas, float w, float h, Anchor9 anchor, float margin, double offsetXPct, double offsetYPct)
    {
        var r = Anchored(canvas, w, h, anchor, margin);
        if (offsetXPct == 0 && offsetYPct == 0) return r;
        return SKRect.Create(
            r.Left + (float)(canvas.Width * offsetXPct / 100),
            r.Top + (float)(canvas.Height * offsetYPct / 100), w, h);
    }

    public static SKColor Hue(int index, int count, float saturation = 78, float value = 100)
        => SKColor.FromHsv(count <= 0 ? 0 : index * 360f / count, saturation, value);

    // ---- text ---------------------------------------------------------------

    /// <summary>Draws text with its horizontal center at cx and vertical center at cy.</summary>
    public static void TextCentered(SKCanvas c, string text, float cx, float cy, SKFont font, SKPaint paint)
    {
        var m = font.Metrics;
        var baseline = cy - (m.Ascent + m.Descent) / 2;
        c.DrawText(text, cx, baseline, SKTextAlign.Center, font, paint);
    }

    public static void TextLeft(SKCanvas c, string text, float x, float baseline, SKFont font, SKPaint paint)
        => c.DrawText(text, x, baseline, SKTextAlign.Left, font, paint);

    /// <summary>Measures digit-run text using a fixed advance per digit (no jitter as values change).</summary>
    public static float MeasureFixedDigits(string text, SKFont font)
    {
        var dw = font.MeasureText("0");
        float total = 0;
        foreach (var ch in text)
        {
            total += char.IsAsciiDigit(ch) ? dw : font.MeasureText(CharString(ch));
        }
        return total;
    }

    /// <summary>Draws a digit run with fixed per-digit advance, centered on cx/cy.</summary>
    public static void FixedDigitsCentered(SKCanvas c, string text, float cx, float cy, SKFont font, SKPaint paint)
    {
        var dw = font.MeasureText("0");
        var total = MeasureFixedDigits(text, font);
        var m = font.Metrics;
        var baseline = cy - (m.Ascent + m.Descent) / 2;
        var x = cx - total / 2;
        foreach (var ch in text)
        {
            var s = CharString(ch);
            if (char.IsAsciiDigit(ch))
            {
                var w = font.MeasureText(s);
                c.DrawText(s, x + (dw - w) / 2, baseline, SKTextAlign.Left, font, paint);
                x += dw;
            }
            else
            {
                c.DrawText(s, x, baseline, SKTextAlign.Left, font, paint);
                x += font.MeasureText(s);
            }
        }
    }

    /// <summary>The rect a <see cref="Chip"/> of the given text width occupies — same padding, same margin rule.</summary>
    public static SKRect ChipBounds(float textWidth, SKSizeI canvas, Anchor9 anchor, float textSize, float margin = -1,
        double offsetXPct = 0, double offsetYPct = 0)
    {
        var padX = textSize * 0.7f;
        var padY = textSize * 0.42f;
        var boxW = textWidth + padX * 2;
        var boxH = textSize + padY * 2;
        if (margin < 0) margin = Math.Max(8, canvas.Height * 0.02f);
        return Anchored(canvas, boxW, boxH, anchor, margin, offsetXPct, offsetYPct);
    }

    /// <summary>Rounded translucent chip with centered text; returns the chip rect.</summary>
    public static SKRect Chip(
        SKCanvas c, string text, SKSizeI canvas, Anchor9 anchor, float textSize,
        PaintCache pc, SKColor textColor, SKColor bg, float margin = -1, SKFont? fontOverride = null,
        double offsetXPct = 0, double offsetYPct = 0)
    {
        var font = fontOverride ?? pc.FontBold;
        font.Size = textSize;
        var rect = ChipBounds(font.MeasureText(text), canvas, anchor, textSize, margin, offsetXPct, offsetYPct);
        if (bg.Alpha > 0) c.DrawRoundRect(rect, rect.Height * 0.24f, rect.Height * 0.24f, pc.FillAA(bg));
        TextCentered(c, text, rect.MidX, rect.MidY, font, pc.Text(textColor));
        return rect;
    }
}
