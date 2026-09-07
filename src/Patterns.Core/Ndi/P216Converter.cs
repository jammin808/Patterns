using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;

namespace Patterns.Core.Ndi;

/// <summary>
/// Converts Skia RGBA-1010102 pixels to NDI P216 (semi-planar 4:2:2 YCbCr, 16 bits per
/// component, BT.709 limited range scaled to 16 bits). The row function is pure and unit
/// tested; the frame function parallelises over rows. On a machine with 256-bit vectors the
/// row runs eight pixels at a time (<see cref="ConvertRowVector"/>) — the same arithmetic in
/// the same order as the scalar row (<see cref="ConvertRowScalar"/>), so the two agree to the
/// bit; the tail of a row and an older machine take the scalar row.
/// </summary>
public static class P216Converter
{
    // BT.709: Y' = 0.2126 R + 0.7152 G + 0.0722 B
    private const float Kr = 0.2126f;
    private const float Kg = 0.7152f;
    private const float Kb = 0.0722f;
    private const float Inv = 1f / 1023f;
    private const float CbScale = 1f / (2f * (1f - Kb));
    private const float CrScale = 1f / (2f * (1f - Kr));
    private const float Y16Base = 16f * 256f;
    private const float Y16Span = 219f * 256f;
    private const float C16Base = 128f * 256f;
    private const float C16Span = 224f * 256f;

    /// <summary>True when the eight-pixel path is in use on this machine.</summary>
    public static bool Vectorised => Vector256.IsHardwareAccelerated;

    /// <summary>
    /// One row: <paramref name="rgba"/> holds width packed 1010102 pixels
    /// (R bits 0–9, G 10–19, B 20–29). Writes width Y values and width interleaved
    /// Cb/Cr values (one pair per two pixels, 4:2:2).
    /// </summary>
    public static void ConvertRow(ReadOnlySpan<uint> rgba, Span<ushort> yOut, Span<ushort> cbcrOut)
    {
        if (Vectorised && rgba.Length >= 8) ConvertRowVector(rgba, yOut, cbcrOut);
        else ConvertRowScalar(rgba, yOut, cbcrOut);
    }

    /// <summary>
    /// The row eight pixels at a time, the tail one at a time. Runs on any machine — in software
    /// where there are no 256-bit registers — so a test can hold it against the scalar row anywhere.
    /// </summary>
    public static void ConvertRowVector(ReadOnlySpan<uint> rgba, Span<ushort> yOut, Span<ushort> cbcrOut)
    {
        var width = rgba.Length;
        var mask = Vector256.Create(0x3FFu);
        var inv = Vector256.Create(Inv);
        var kr = Vector256.Create(Kr);
        var kg = Vector256.Create(Kg);
        var kb = Vector256.Create(Kb);
        var cbScale = Vector256.Create(CbScale);
        var crScale = Vector256.Create(CrScale);
        var zero = Vector256<float>.Zero;
        var one = Vector256.Create(1f);
        var half = Vector256.Create(0.5f);
        var minusHalf = Vector256.Create(-0.5f);
        var y16Base = Vector256.Create(Y16Base);
        var y16Span = Vector256.Create(Y16Span);
        var c16Base = Vector256.Create(C16Base);
        var c16Span = Vector256.Create(C16Span);
        var swapPairs = Vector256.Create(1, 0, 3, 2, 5, 4, 7, 6);
        Span<int> cbi = stackalloc int[8];
        Span<int> cri = stackalloc int[8];
        Span<int> yi = stackalloc int[8];
        ref var src = ref MemoryMarshal.GetReference(rgba);
        var x = 0;
        for (; x + 8 <= width; x += 8)
        {
            var px = Vector256.LoadUnsafe(ref src, (nuint)x);
            var r = Vector256.ConvertToSingle(px & mask) * inv;
            var g = Vector256.ConvertToSingle((px >> 10) & mask) * inv;
            var b = Vector256.ConvertToSingle((px >> 20) & mask) * inv;
            var y = kr * r + kg * g + kb * b;
            var cb = (b - y) * cbScale;
            var cr = (r - y) * crScale;
            // The chroma of a pair is the mean of the two: each lane meets its neighbour.
            cb = (cb + Vector256.Shuffle(cb, swapPairs)) * half;
            cr = (cr + Vector256.Shuffle(cr, swapPairs)) * half;
            var yq = Vector256.Min(Vector256.Max(y, zero), one) * y16Span + y16Base + half;
            var cbq = Vector256.Min(Vector256.Max(cb, minusHalf), half) * c16Span + c16Base + half;
            var crq = Vector256.Min(Vector256.Max(cr, minusHalf), half) * c16Span + c16Base + half;
            Vector256.ConvertToInt32(yq).CopyTo(yi);
            Vector256.ConvertToInt32(cbq).CopyTo(cbi);
            Vector256.ConvertToInt32(crq).CopyTo(cri);
            for (var k = 0; k < 8; k += 2)
            {
                yOut[x + k] = (ushort)yi[k];
                yOut[x + k + 1] = (ushort)yi[k + 1];
                cbcrOut[x + k] = (ushort)cbi[k];
                cbcrOut[x + k + 1] = (ushort)cri[k];
            }
        }
        if (x < width) ConvertRowScalar(rgba[x..], yOut[x..], cbcrOut[x..]);
    }

    /// <summary>The row one pixel at a time — the reference the vector row matches, the path of the tail and of a machine without 256-bit vectors.</summary>
    public static void ConvertRowScalar(ReadOnlySpan<uint> rgba, Span<ushort> yOut, Span<ushort> cbcrOut)
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
        var r = (px & 0x3FF) * Inv;
        var g = ((px >> 10) & 0x3FF) * Inv;
        var b = ((px >> 20) & 0x3FF) * Inv;
        var y = Kr * r + Kg * g + Kb * b;
        var cb = (b - y) * CbScale;
        var cr = (r - y) * CrScale;
        return (y, cb, cr);
    }

    /// <summary>Limited-range luma scaled to 16 bits: v · (219 · 256) + 16 · 256, rounded half up — the vector row's operations in the vector row's order.</summary>
    private static ushort ToY16(float v)
        => (ushort)Math.Clamp((int)(Math.Clamp(v, 0f, 1f) * Y16Span + Y16Base + 0.5f), 0, 65535);

    /// <summary>Limited-range chroma scaled to 16 bits: c · (224 · 256) + 128 · 256, rounded half up — the vector row's operations in the vector row's order.</summary>
    private static ushort ToC16(float c)
        => (ushort)Math.Clamp((int)(Math.Clamp(c, -0.5f, 0.5f) * C16Span + C16Base + 0.5f), 0, 65535);

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
