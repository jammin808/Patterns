using Patterns.Core.Ndi;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

public class P216ConverterTests
{
    private static uint Pack(int r10, int g10, int b10)
        => (uint)(r10 & 0x3FF) | ((uint)(g10 & 0x3FF) << 10) | ((uint)(b10 & 0x3FF) << 20);

    private static (ushort Y, ushort Cb, ushort Cr) ConvertSolid(uint px)
    {
        Span<uint> row = stackalloc uint[2] { px, px };
        Span<ushort> y = stackalloc ushort[2];
        Span<ushort> c = stackalloc ushort[2];
        P216Converter.ConvertRow(row, y, c);
        return (y[0], c[0], c[1]);
    }

    [Fact]
    public void WhiteHitsLimitedRangeTop()
    {
        var (y, cb, cr) = ConvertSolid(Pack(1023, 1023, 1023));
        Assert.InRange(y, 235 * 256 - 4, 235 * 256 + 4);
        Assert.InRange(cb, 128 * 256 - 4, 128 * 256 + 4);
        Assert.InRange(cr, 128 * 256 - 4, 128 * 256 + 4);
    }

    [Fact]
    public void BlackHitsLimitedRangeBottom()
    {
        var (y, cb, cr) = ConvertSolid(Pack(0, 0, 0));
        Assert.InRange(y, 16 * 256 - 4, 16 * 256 + 4);
        Assert.InRange(cb, 128 * 256 - 4, 128 * 256 + 4);
        Assert.InRange(cr, 128 * 256 - 4, 128 * 256 + 4);
    }

    [Fact]
    public void PureRedMatchesBt709()
    {
        var (y, cb, cr) = ConvertSolid(Pack(1023, 0, 0));
        // Y' = 0.2126 → (16 + 219·0.2126)·256 ≈ 16015 ; Cr = +0.5 → (128+112)·256 = 61440
        Assert.InRange(y, 16015 - 8, 16015 + 8);
        Assert.InRange(cr, 61440 - 8, 61440 + 8);
        Assert.True(cb < 128 * 256); // red pulls Cb below centre
    }

    [Fact]
    public void ChromaPairsAreAveraged()
    {
        Span<uint> row = stackalloc uint[2] { Pack(1023, 0, 0), Pack(0, 0, 1023) };
        Span<ushort> y = stackalloc ushort[2];
        Span<ushort> c = stackalloc ushort[2];
        P216Converter.ConvertRow(row, y, c);

        var (_, cbRed, crRed) = ConvertSolid(Pack(1023, 0, 0));
        var (_, cbBlue, crBlue) = ConvertSolid(Pack(0, 0, 1023));
        Assert.InRange(c[0], (cbRed + cbBlue) / 2 - 4, (cbRed + cbBlue) / 2 + 4);
        Assert.InRange(c[1], (crRed + crBlue) / 2 - 4, (crRed + crBlue) / 2 + 4);
    }

    [Fact]
    public void TheEightPixelRowMatchesTheScalarRowToTheBitAtEveryWidth()
    {
        // The same arithmetic in the same order: on any machine (the vector row runs in software
        // where there are no 256-bit registers) every width from one pixel to a full HD row agrees
        // exactly, tail included, and the public row equals the scalar row whichever path it took.
        var rng = new Random(1010102);
        var widths = Enumerable.Range(1, 40).Concat(new[] { 64, 255, 256, 1919, 1920 }).ToArray();
        foreach (var width in widths)
        {
            var row = new uint[width];
            for (var x = 0; x < width; x++) row[x] = Pack(rng.Next(1024), rng.Next(1024), rng.Next(1024));
            // Every primary and every extreme in the first eight, so the clamps see their corners.
            if (width >= 8)
            {
                row[0] = Pack(0, 0, 0);
                row[1] = Pack(1023, 1023, 1023);
                row[2] = Pack(1023, 0, 0);
                row[3] = Pack(0, 1023, 0);
                row[4] = Pack(0, 0, 1023);
                row[5] = Pack(1023, 1023, 0);
                row[6] = Pack(0, 1023, 1023);
                row[7] = Pack(1023, 0, 1023);
            }
            var yV = new ushort[width];
            var cV = new ushort[width];
            var yS = new ushort[width];
            var cS = new ushort[width];
            var yP = new ushort[width];
            var cP = new ushort[width];
            P216Converter.ConvertRowVector(row, yV, cV);
            P216Converter.ConvertRowScalar(row, yS, cS);
            P216Converter.ConvertRow(row, yP, cP);
            for (var x = 0; x < width; x++)
            {
                Assert.True(yV[x] == yS[x], $"width {width}: Y at {x} vector {yV[x]} scalar {yS[x]}");
                Assert.True(cV[x] == cS[x], $"width {width}: CbCr at {x} vector {cV[x]} scalar {cS[x]}");
                Assert.True(yP[x] == yS[x], $"width {width}: Y at {x} row {yP[x]} scalar {yS[x]}");
                Assert.True(cP[x] == cS[x], $"width {width}: CbCr at {x} row {cP[x]} scalar {cS[x]}");
            }
        }

        // The corners themselves, in numbers: black and white at the limited-range ends, red's Cr at the top.
        var corners = new[] { Pack(0, 0, 0), Pack(1023, 1023, 1023), Pack(1023, 0, 0), Pack(0, 0, 1023), Pack(1023, 0, 0), Pack(1023, 0, 0), Pack(0, 0, 0), Pack(0, 0, 0) };
        var y = new ushort[8];
        var c = new ushort[8];
        P216Converter.ConvertRowVector(corners, y, c);
        Assert.Equal(16 * 256, y[0]);
        Assert.Equal(235 * 256, y[1]);
        Assert.InRange(y[4], 16015 - 2, 16015 + 2);
        Assert.Equal(128 * 256, c[6]);            // black pair: chroma at centre
        Assert.Equal(61440, c[5]);                // a red pair: Cr = +0.5 → (128 + 112) · 256
        Assert.True(c[4] < 128 * 256);            // a red pair pulls Cb below centre
        _ = P216Converter.Vectorised;             // true on a 256-bit machine, false elsewhere; both paths above ran
    }

    [Fact]
    public void EndToEndThroughSkiaSurfaceValidatesBitPacking()
    {
        // Render a solid colour into a real RGBA-1010102 surface and convert — this catches
        // any mismatch between Skia's channel packing and the converter's unpacking.
        var info = new SKImageInfo(8, 2, SKColorType.Rgba1010102, SKAlphaType.Opaque);
        using var surface = SKSurface.Create(info);
        if (surface is null)
        {
            return; // colour type unsupported on this raster build — the sender falls back to 8-bit
        }
        surface.Canvas.Clear(new SKColor(255, 0, 0));
        surface.Canvas.Flush();
        using var pixmap = surface.PeekPixels();
        Assert.NotNull(pixmap);

        var p216 = System.Runtime.InteropServices.Marshal.AllocHGlobal(info.Width * info.Height * 4);
        try
        {
            P216Converter.ConvertFrame(pixmap!.GetPixels(), pixmap.RowBytes, info.Width, info.Height, p216);
            unsafe
            {
                var yPlane = (ushort*)p216;
                var cPlane = yPlane + info.Width * info.Height;
                Assert.InRange(yPlane[3], 16015 - 40, 16015 + 40);   // red luma
                Assert.InRange(cPlane[1], 61440 - 40, 61440 + 40);   // red Cr
            }
        }
        finally
        {
            System.Runtime.InteropServices.Marshal.FreeHGlobal(p216);
        }
    }
}
