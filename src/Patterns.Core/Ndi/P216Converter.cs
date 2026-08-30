namespace Patterns.Core.Ndi;

/// <summary>
/// Converts Skia RGBA-1010102 pixels to NDI P216 (semi-planar 4:2:2 YCbCr, 16 bits per
/// component, BT.709 limited range scaled to 16 bits). The row function is pure and unit
/// tested; the frame function parallelises over rows.
/// </summary>
public static class P216Converter
{
    // BT.709: Y' = 0.2126 R + 0.7152 G + 0.0722 B
    private const float Kr = 0.2126f;
    private const float Kg = 0.7152f;
    private const float Kb = 0.0722f;

    /// <summary>
    /// One row: <paramref name="rgba"/> holds width packed 1010102 pixels
    /// (R bits 0–9, G 10–19, B 20–29). Writes width Y values and width interleaved
    /// Cb/Cr values (one pair per two pixels, 4:2:2).
    /// </summary>
    public static void ConvertRow(ReadOnlySpan<uint> rgba, Span<ushort> yOut, Span<ushort> cbcrOut)
    {
        var width = rgba.Length;
        for (var x = 0; x < width; x += 2)
        {
            var (y0, cb0, cr0) = PixelToYcc(rgba[x]);
            yOut[x] = ToY16(y0);

            float cb;
            float cr;
            if (x + 1 < width)
            {
                var (y1, cb1, cr1) = PixelToYcc(rgba[x + 1]);
                yOut[x + 1] = ToY16(y1);
                cb = (cb0 + cb1) * 0.5f;
                cr = (cr0 + cr1) * 0.5f;
            }
            else
            {
                cb = cb0;
                cr = cr0;
            }

            cbcrOut[x] = ToC16(cb);
            cbcrOut[x + 1 < width ? x + 1 : x] = ToC16(cr);
        }
    }

    private static (float Y, float Cb, float Cr) PixelToYcc(uint px)
    {
        const float inv = 1f / 1023f;
        var r = (px & 0x3FF) * inv;
        var g = ((px >> 10) & 0x3FF) * inv;
        var b = ((px >> 20) & 0x3FF) * inv;
        var y = Kr * r + Kg * g + Kb * b;
        var cb = (b - y) / (2f * (1f - Kb));
        var cr = (r - y) / (2f * (1f - Kr));
        return (y, cb, cr);
    }

    /// <summary>Limited-range luma scaled to 16 bits: (16 + 219·v) · 256.</summary>
    private static ushort ToY16(float v)
        => (ushort)Math.Clamp((int)MathF.Round((16f + 219f * Math.Clamp(v, 0f, 1f)) * 256f), 0, 65535);

    /// <summary>Limited-range chroma scaled to 16 bits: (128 + 224·c) · 256.</summary>
    private static ushort ToC16(float c)
        => (ushort)Math.Clamp((int)MathF.Round((128f + 224f * Math.Clamp(c, -0.5f, 0.5f)) * 256f), 0, 65535);

    /// <summary>
    /// Whole frame into a P216 buffer: Y plane (width·height ushorts) followed by the
    /// interleaved CbCr plane. Parallelised over rows.
    /// </summary>
    public static unsafe void ConvertFrame(IntPtr rgbaPixels, int rowBytes, int width, int height, IntPtr p216)
    {
        var src = (byte*)rgbaPixels;
        var dst = (ushort*)p216;
        var yPlane = dst;
        var cbcrPlane = dst + (long)width * height;

        Parallel.For(0, height, row =>
        {
            var rgba = new ReadOnlySpan<uint>(src + (long)row * rowBytes, width);
            var yOut = new Span<ushort>(yPlane + (long)row * width, width);
            var cOut = new Span<ushort>(cbcrPlane + (long)row * width, width);
            ConvertRow(rgba, yOut, cOut);
        });
    }
}
