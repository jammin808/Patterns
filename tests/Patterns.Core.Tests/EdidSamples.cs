using Patterns.Core.Services;

namespace Patterns.Core.Tests;

/// <summary>
/// Synthetic EDIDs for the tests — a "Patterns LED" processor: an E-EDID 1.4 base block with a
/// 1920×1080p50 preferred timing, a CTA-861 extension with video formats (one native, one
/// 4:2:0-only), LPCM audio, speakers, the HDMI block with deep colour, BT.2020 colorimetry, HDR
/// static metadata (PQ and HLG) and a 1080p60 detailed timing, and a DisplayID 2.0 extension
/// with a 7680×2160p50 Type VII timing (past the 4095 a DTD can say) and a 2×1 tiled topology.
/// Every checksum computed, so a parser that trusts them is exercised honestly.
/// </summary>
public static class EdidSamples
{
    public static byte[] PatternsLed(bool withDisplayId = true)
    {
        var blocks = new List<byte[]> { BaseBlock(withDisplayId ? 2 : 1), CtaBlock() };
        if (withDisplayId) blocks.Add(DisplayIdBlock());
        return blocks.SelectMany(b => b).ToArray();
    }

    /// <summary>The 18-byte detailed timing descriptor for 1920×1080 at 148.5 MHz with the given horizontal blanking (720 → 50 Hz, 280 → 60 Hz).</summary>
    public static byte[] Dtd1080(int hBlank, int hSyncOffset)
    {
        var d = new byte[18];
        const int clock = 14850;                         // 148.5 MHz in 10 kHz units
        d[0] = (byte)(clock & 0xFF);
        d[1] = (byte)(clock >> 8);
        d[2] = 1920 & 0xFF;
        d[3] = (byte)(hBlank & 0xFF);
        d[4] = (byte)(((1920 >> 8) << 4) | (hBlank >> 8));
        d[5] = 1080 & 0xFF;
        d[6] = 45;
        d[7] = (byte)((1080 >> 8) << 4);
        d[8] = (byte)(hSyncOffset & 0xFF);
        d[9] = 44;
        d[10] = 0x45;                                    // vsync offset 4, width 5
        d[11] = (byte)((hSyncOffset >> 8) << 6);
        d[12] = 0x4C;                                    // 1100 mm × 620 mm
        d[13] = 0x6C;
        d[14] = 0x42;
        d[17] = 0x1E;                                    // digital separate sync, positive polarities
        return d;
    }

    private static byte[] BaseBlock(int extensions)
    {
        var b = new byte[128];
        new byte[] { 0x00, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0xFF, 0x00 }.CopyTo(b, 0);
        b[8] = 0x42; b[9] = 0x8E;                        // "PTN"
        b[10] = 0x01; b[11] = 0x00;                      // product 0x0001
        b[12] = 0x39; b[13] = 0x30; b[14] = 0x00; b[15] = 0x00;   // serial 12345
        b[16] = 20; b[17] = 36;                          // week 20, 2026
        b[18] = 1; b[19] = 4;                            // E-EDID 1.4
        b[20] = 0xA2;                                    // digital, 8 bits per colour, HDMI-a
        b[21] = 110; b[22] = 62;                         // cm
        b[23] = 0x78;                                    // gamma 2.2
        b[24] = 0x1A;                                    // RGB + YCbCr 4:4:4 + 4:2:2, preferred timing
        for (var i = 38; i < 54; i++) b[i] = 0x01;       // no standard timings
        Dtd1080(720, 528).CopyTo(b, 54);                 // 1920×1080p50 preferred
        Descriptor(0xFC, "PATTERNS LED").CopyTo(b, 72);
        Descriptor(0xFF, "PTN-0001").CopyTo(b, 90);
        b[108 + 3] = 0x10;                               // a dummy descriptor
        b[126] = (byte)extensions;
        b[127] = Edid.ChecksumFor(b.AsSpan(0, 127));
        return b;
    }

    private static byte[] Descriptor(byte tag, string text)
    {
        var d = new byte[18];
        d[3] = tag;
        var i = 5;
        foreach (var c in text) d[i++] = (byte)c;
        if (i < 18) d[i++] = 0x0A;
        while (i < 18) d[i++] = 0x20;
        return d;
    }

    private static byte[] CtaBlock()
    {
        var b = new byte[128];
        b[0] = 0x02;
        b[1] = 0x03;
        var blocks = new List<byte>();
        blocks.AddRange(new byte[] { 0x44, 0x9F, 0x10, 0x60, 0x5F });                       // video: VIC 31 native, 16, 96, 95
        blocks.AddRange(new byte[] { 0x23, 0x09, 0x07, 0x07 });                             // audio: LPCM 2ch 32/44/48 kHz 16/20/24-bit
        blocks.AddRange(new byte[] { 0x83, 0x01, 0x00, 0x00 });                             // speakers: FL/FR
        blocks.AddRange(new byte[] { 0x67, 0x03, 0x0C, 0x00, 0x10, 0x00, 0x20, 0x2D });     // HDMI LLC: 1.0.0.0, DC_36, 225 MHz
        blocks.AddRange(new byte[] { 0xE3, 0x05, 0x80, 0x00 });                             // colorimetry: BT.2020 RGB
        blocks.AddRange(new byte[] { 0xE6, 0x06, 0x0D, 0x01, 0x50, 0x00, 0x00 });           // HDR static metadata: SDR, PQ, HLG; type 1; max ~283 nits
        blocks.AddRange(new byte[] { 0xE2, 0x0E, 0x61 });                                   // YCbCr 4:2:0 video: VIC 97 (2160p60) 4:2:0 only
        var dtdOffset = 4 + blocks.Count;
        b[2] = (byte)dtdOffset;
        b[3] = 0xF1;                                     // underscan, basic audio, YCbCr 4:4:4 and 4:2:2, one native DTD
        blocks.CopyTo(b, 4);
        Dtd1080(280, 88).CopyTo(b, dtdOffset);           // 1920×1080p60
        b[127] = Edid.ChecksumFor(b.AsSpan(0, 127));
        return b;
    }

    private static byte[] DisplayIdBlock()
    {
        var b = new byte[128];
        b[0] = 0x70;
        b[1] = 0x20;                                     // DisplayID 2.0
        b[3] = 0x02;                                     // generic display
        b[4] = 0x00;                                     // no DisplayID extensions of its own
        var blocks = new List<byte>();
        // Type VII detailed timing: 7680×2160p50 — 7840 × 2220 total at 870.24 MHz.
        const int clockKHz = 7840 * 2220 * 50 / 1000;   // 870240
        var units = clockKHz - 1;
        blocks.AddRange(new byte[] { 0x22, 0x00, 20 });
        blocks.AddRange(new byte[]
        {
            (byte)(units & 0xFF), (byte)((units >> 8) & 0xFF), (byte)((units >> 16) & 0xFF),
            0x80,                                        // preferred
            0xFF, 0x1D,                                  // hactive 7680
            0x9F, 0x00,                                  // hblank 160
            0x1F, 0x00, 0x1F, 0x00,                      // hsync offset 32, width 32
            0x6F, 0x08,                                  // vactive 2160
            0x3B, 0x00,                                  // vblank 60
            0x07, 0x00, 0x07, 0x00,                      // vsync offset 8, width 8
        });
        // Tiled topology: 2 × 1 tiles, this is tile 1,1 of 3840 × 2160, one enclosure.
        blocks.AddRange(new byte[] { 0x28, 0x00, 22 });
        blocks.AddRange(new byte[] { 0x01, 0x01, 0x00, 0x00, 0xFF, 0x0E, 0x6F, 0x08 });
        blocks.AddRange(new byte[14]);
        b[2] = (byte)blocks.Count;
        blocks.CopyTo(b, 5);
        // The DisplayID section's own checksum over version … last block byte.
        var sum = 0;
        for (var i = 1; i < 5 + blocks.Count; i++) sum += b[i];
        b[5 + blocks.Count] = (byte)((256 - (sum & 0xFF)) & 0xFF);
        b[127] = Edid.ChecksumFor(b.AsSpan(0, 127));
        return b;
    }
}
