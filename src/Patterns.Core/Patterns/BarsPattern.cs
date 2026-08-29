using Patterns.Core.Model;
using Patterns.Core.Rendering;
using SkiaSharp;

namespace Patterns.Core.Patterns;

/// <summary>
/// Colour bars. The SMPTE variant follows the RP 219 HD layout (7/12 + 1/12 + 1/12 + 3/12
/// bands, W/8 flanks, PLUGE) with BT.709 narrow-range 8-bit values; the FullRange switch
/// stretches 16–235 → 0–255 for graphics-pipeline use. Full-height variants are classic
/// 100%/75% and EBU bars.
/// </summary>
public sealed class BarsPattern : IPatternRenderer
{
    // Narrow-range (16–235) 8-bit values.
    private const byte NLo = 16;   // 0%
    private const byte NHi = 235;  // 100%
    private const byte N75 = 180;  // 75%
    private const byte G40 = 104;  // 40% grey
    private const byte G15 = 49;   // 15% grey
    private const byte PlugeMinus = 12;  // −2%
    private const byte PlugePlus2 = 20;  // +2%
    private const byte PlugePlus4 = 25;  // +4%

    public void Render(SKCanvas c, in PatternFrame f)
    {
        switch (f.Config.Bars.Variant)
        {
            case BarsVariant.Smpte:
                RenderSmpte(c, in f);
                break;
            case BarsVariant.Ebu100:
                RenderFullHeight(c, in f, amplitude: 255, includeBlack: true);
                break;
            case BarsVariant.Bars75:
                RenderFullHeight(c, in f, amplitude: 191, includeBlack: false);
                break;
            default:
                RenderFullHeight(c, in f, amplitude: 255, includeBlack: false);
                break;
        }
    }

    private static void RenderFullHeight(SKCanvas c, in PatternFrame f, byte amplitude, bool includeBlack)
    {
        var pc = f.Paints;
        int w = f.W, h = f.H;
        Span<SKColor> colors = stackalloc SKColor[8];
        var n = BuildBars(colors, amplitude, includeBlack, whiteAlwaysFull: amplitude < 255);
        for (var i = 0; i < n; i++)
        {
            var x0 = (int)Math.Round(w * (double)i / n);
            var x1 = (int)Math.Round(w * (double)(i + 1) / n);
            c.DrawRect(SKRect.Create(x0, 0, x1 - x0, h), pc.Fill(colors[i]));
        }
    }

    private static int BuildBars(Span<SKColor> dst, byte a, bool includeBlack, bool whiteAlwaysFull)
    {
        var i = 0;
        dst[i++] = whiteAlwaysFull ? SKColors.White : new SKColor(a, a, a); // white
        dst[i++] = new SKColor(a, a, 0);   // yellow
        dst[i++] = new SKColor(0, a, a);   // cyan
        dst[i++] = new SKColor(0, a, 0);   // green
        dst[i++] = new SKColor(a, 0, a);   // magenta
        dst[i++] = new SKColor(a, 0, 0);   // red
        dst[i++] = new SKColor(0, 0, a);   // blue
        if (includeBlack) dst[i++] = SKColors.Black;
        return i;
    }

    private void RenderSmpte(SKCanvas c, in PatternFrame f)
    {
        var pc = f.Paints;
        var full = f.Config.Bars.FullRange;
        int w = f.W, h = f.H;

        SKColor Gray(byte v) => Mono(v, full);
        SKColor Rgb(byte r, byte g, byte b) => Map(r, g, b, full);

        var d = w / 8f;                 // flank width
        var barW = (w - 2 * d) / 7f;    // 7 equal bars over the middle 3/4

        var h1 = h * 7f / 12f;
        var h2 = h * 1f / 12f;
        var h3 = h * 1f / 12f;

        // Band 1: 40% grey flanks + 75% bars.
        FillX(c, pc, 0, d, 0, h1, Gray(G40));
        Span<SKColor> bars75 = stackalloc SKColor[8];
        BuildNarrowBars(bars75);
        for (var i = 0; i < 7; i++)
        {
            FillX(c, pc, d + i * barW, d + (i + 1) * barW, 0, h1, full ? Stretch(bars75[i]) : bars75[i]);
        }
        FillX(c, pc, w - d, w, 0, h1, Gray(G40));

        // Band 2: 100% cyan flank, 75% white run, 100% blue flank.
        var y2 = h1;
        FillX(c, pc, 0, d, y2, y2 + h2, Rgb(NLo, NHi, NHi));
        FillX(c, pc, d, w - d, y2, y2 + h2, Gray(N75));
        FillX(c, pc, w - d, w, y2, y2 + h2, Rgb(NLo, NLo, NHi));

        // Band 3: 100% yellow flank, black→white luminance ramp, 100% red flank.
        var y3 = y2 + h2;
        FillX(c, pc, 0, d, y3, y3 + h3, Rgb(NHi, NHi, NLo));
        DrawRamp(c, pc, d, w - d, y3, y3 + h3, Gray(NLo), Gray(NHi));
        FillX(c, pc, w - d, w, y3, y3 + h3, Rgb(NHi, NLo, NLo));

        // Band 4: 15% grey flanks, white block, blacks and PLUGE.
        var y4 = y3 + h3;
        var y5 = h;
        FillX(c, pc, 0, d, y4, y5, Gray(G15));
        FillX(c, pc, w - d, w, y4, y5, Gray(G15));

        var x = d;
        void Block(float widthInBars, SKColor col)
        {
            var bw = widthInBars * barW;
            FillX(c, pc, x, x + bw, y4, y5, col);
            x += bw;
        }

        Block(1.5f, Gray(NLo));
        Block(2f, Gray(NHi));
        Block(1f, Gray(NLo));
        // PLUGE: −2 / 0 / +2 / 0 / +4 over 1.5 bars.
        Block(0.3f, Gray(PlugeMinus));
        Block(0.3f, Gray(NLo));
        Block(0.3f, Gray(PlugePlus2));
        Block(0.3f, Gray(NLo));
        Block(0.3f, Gray(PlugePlus4));
        Block(1f, Gray(NLo));
    }

    private static void BuildNarrowBars(Span<SKColor> dst)
    {
        dst[0] = new SKColor(N75, N75, N75);
        dst[1] = new SKColor(N75, N75, NLo);
        dst[2] = new SKColor(NLo, N75, N75);
        dst[3] = new SKColor(NLo, N75, NLo);
        dst[4] = new SKColor(N75, NLo, N75);
        dst[5] = new SKColor(N75, NLo, NLo);
        dst[6] = new SKColor(NLo, NLo, N75);
    }

    private static void FillX(SKCanvas c, PaintCache pc, float x0, float x1, float y0, float y1, SKColor col)
    {
        var xi0 = (int)MathF.Round(x0);
        var xi1 = (int)MathF.Round(x1);
        var yi0 = (int)MathF.Round(y0);
        var yi1 = (int)MathF.Round(y1);
        c.DrawRect(SKRect.Create(xi0, yi0, xi1 - xi0, yi1 - yi0), pc.Fill(col));
    }

    private static void DrawRamp(SKCanvas c, PaintCache pc, float x0, float x1, float y0, float y1, SKColor from, SKColor to)
    {
        using var shader = SKShader.CreateLinearGradient(
            new SKPoint(x0, 0), new SKPoint(x1, 0),
            new[] { from, to }, SKShaderTileMode.Clamp);
        var paint = pc.Fill(SKColors.White);
        paint.Shader = shader;
        c.DrawRect(SKRect.Create(x0, y0, x1 - x0, y1 - y0), paint);
        paint.Shader = null;
    }

    /// <summary>Narrow-range value → display colour, optionally stretched to full range.</summary>
    internal static SKColor Mono(byte v, bool fullRange)
    {
        if (!fullRange) return new SKColor(v, v, v);
        var s = StretchByte(v);
        return new SKColor(s, s, s);
    }

    internal static SKColor Map(byte r, byte g, byte b, bool fullRange)
        => fullRange ? new SKColor(StretchByte(r), StretchByte(g), StretchByte(b)) : new SKColor(r, g, b);

    private static SKColor Stretch(SKColor c) => new(StretchByte(c.Red), StretchByte(c.Green), StretchByte(c.Blue));

    internal static byte StretchByte(byte v)
        => (byte)Math.Clamp((int)Math.Round((v - 16) * 255.0 / 219.0), 0, 255);
}
