using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 65.7: the EDID read and not judged — the base block's identity and timings, the CTA
/// extension's formats, audio, colour and HDR, the DisplayID extension's large timing and tiling,
/// the checksums and the hash; a fault a problem named, never a throw.
/// </summary>
public class EdidTests
{
    [Fact]
    public void TheBaseBlockReadsIdentityParametersAndThePreferredTiming()
    {
        var bytes = EdidSamples.PatternsLed();
        var edid = Edid.Parse(bytes);
        Assert.Empty(edid.Problems);
        Assert.Equal("PTN", edid.Manufacturer);
        Assert.Equal(1, edid.ProductCode);
        Assert.Equal(12345u, edid.SerialNumber);
        Assert.Equal("PTN-0001", edid.SerialText);
        Assert.Equal("PATTERNS LED", edid.Name);
        Assert.Equal("PTN 0001 · PATTERNS LED", edid.Identity);
        Assert.Equal((20, 2026), (edid.Week, edid.Year));
        Assert.Equal("1.4", edid.Version);
        Assert.True(edid.Digital);
        Assert.Equal("HDMI-a", edid.Interface);
        Assert.Equal(8, edid.ColourDepthBits);
        Assert.Equal((110, 62), (edid.WidthCm, edid.HeightCm));
        Assert.Equal(2.2, edid.Gamma, 2);
        Assert.Equal(new[] { "RGB 4:4:4", "YCbCr 4:4:4", "YCbCr 4:2:2" }, edid.BaseEncodings);
        Assert.Empty(edid.EstablishedTimings);
        Assert.Empty(edid.StandardTimings);

        var preferred = Assert.Single(edid.DetailedTimings);
        Assert.True(preferred.Preferred);
        Assert.Equal((1920, 1080, 720, 45), (preferred.HActive, preferred.VActive, preferred.HBlank, preferred.VBlank));
        Assert.Equal(148500, preferred.PixelClockKHz);
        Assert.Equal(SignalRate.Of(50, 1), preferred.Rate);
        Assert.Equal("1920×1080p50 (preferred)", preferred.Words);
        Assert.Same(preferred, edid.Preferred);

        Assert.Equal(2, edid.ExtensionCount);
        Assert.Equal(new[] { "base", "CTA-861", "DisplayID" }, edid.Blocks);
        Assert.Equal(new[] { true, true, true }, edid.BlockChecksums);
        Assert.True(edid.ChecksumsValid);
        Assert.Equal(384, edid.Length);
        Assert.Equal(64, edid.Hash.Length);
        Assert.Equal(edid.Hash, Edid.Parse(bytes).Hash);                      // the same bytes hash the same
        Assert.Equal(edid.Hash[..8], edid.ShortHash);
        Assert.StartsWith("PTN 0001 · PATTERNS LED · 1920×1080p50 preferred · 2 extensions · HDMI-a · ", edid.Summary);
    }

    [Fact]
    public void TheCtaBlockReadsFormatsAudioColourAndHdrAsCapability()
    {
        var edid = Edid.Parse(EdidSamples.PatternsLed());
        var cta = edid.Cta!;
        Assert.Equal(3, cta.Revision);
        Assert.True(cta.Underscan);
        Assert.True(cta.BasicAudio);
        Assert.True(cta.YCbCr444);
        Assert.True(cta.YCbCr422);
        Assert.Equal(1, cta.NativeCount);
        Assert.Equal(new[] { "video", "audio", "speakers", "HDMI", "colorimetry", "HDR static metadata", "YCbCr 4:2:0 video" }, cta.Blocks);

        Assert.Equal(new[] { 31, 16, 96, 95, 97 }, cta.Video.Select(v => v.Vic));
        var native = cta.Video.Single(v => v.Vic == 31);
        Assert.True(native.Native);
        Assert.Equal((1920, 1080), (native.Width, native.Height));
        Assert.Equal(SignalRate.Of(50, 1), native.Rate);
        var uhd60 = cta.Video.Single(v => v.Vic == 97);
        Assert.True(uhd60.Ycc420Only);
        Assert.Equal("VIC 97 3840×2160p60 4:2:0 only", uhd60.Words);
        Assert.Equal("VIC 31 1920×1080p50 native", native.Words);

        var audio = Assert.Single(cta.Audio);
        Assert.Equal(("LPCM", 2), (audio.Format, audio.Channels));
        Assert.Equal(new[] { 32, 44, 48 }, audio.SampleRatesKHz);
        Assert.Equal(new[] { 16, 20, 24 }, audio.BitDepths);
        Assert.Equal(new[] { "FL/FR" }, cta.Speakers);
        Assert.Equal(2, edid.AudioChannels);

        Assert.NotNull(cta.Hdmi);
        Assert.Equal("1.0.0.0", cta.Hdmi!.PhysicalAddress);
        Assert.True(cta.Hdmi.DeepColour36);
        Assert.False(cta.Hdmi.DeepColour30);
        Assert.Equal(225, cta.Hdmi.MaxTmdsMHz);
        Assert.Null(cta.HdmiForum);

        Assert.Equal(new[] { "BT.2020 RGB" }, cta.Colorimetry);
        Assert.NotNull(cta.Hdr);
        Assert.True(cta.Hdr!.Sdr);
        Assert.True(cta.Hdr.Pq);
        Assert.True(cta.Hdr.Hlg);
        Assert.False(cta.Hdr.TraditionalHdr);
        Assert.True(cta.Hdr.StaticMetadataType1);
        Assert.InRange(cta.Hdr.MaxLuminanceNits!.Value, 280, 285);
        Assert.Null(cta.Hdr.MinLuminanceNits);
        Assert.StartsWith("PQ (HDR10), HLG · up to 28", cta.Hdr.Words);

        var timing = Assert.Single(cta.Timings);
        Assert.Equal(SignalRate.Of(60, 1), timing.Rate);
        Assert.Equal("CTA", timing.Source);

        // What the display offers, as capability: the CTA convention makes every 60 Hz format a 59.94 one too.
        var rates = edid.RatesOffered;
        Assert.Contains(SignalRate.Of(50, 1), rates);
        Assert.Contains(SignalRate.Of(60, 1), rates);
        Assert.Contains(SignalRate.Of(60000, 1001), rates);
        Assert.Contains(SignalRate.Of(30, 1), rates);
        Assert.Contains(SignalRate.Of(30000, 1001), rates);
        Assert.True(edid.OffersRate(SignalRate.Parse("59.94")));
        Assert.False(edid.OffersRate(SignalRate.Of(25, 1)));
        Assert.Equal(new[] { "RGB 4:4:4", "YCbCr 4:4:4", "YCbCr 4:2:2", "YCbCr 4:2:0" }, edid.EncodingsOffered);
        Assert.True(edid.OffersEncoding(PixelEncoding.YCbCr420));
        Assert.Equal(new[] { 8, 12 }, edid.BitDepthsOffered);
        Assert.True(edid.OffersBitDepth(12));
        Assert.False(edid.OffersBitDepth(10));
        Assert.True(edid.OffersHdr);
        Assert.True(edid.OffersDynamic(DynamicRange.HDR10));
        Assert.True(edid.OffersDynamic(DynamicRange.HLG));
        Assert.True(edid.OffersColour(ColourSpace.Rec2020));
        Assert.False(edid.OffersColour(ColourSpace.DciP3));
        Assert.True(edid.OffersColour(ColourSpace.Rec709));
        Assert.True(edid.OffersRaster(3840, 2160));
        Assert.False(edid.OffersRaster(2560, 1440));
    }

    [Fact]
    public void TheDisplayIdBlockReadsALargeTimingAndTheTiling()
    {
        var edid = Edid.Parse(EdidSamples.PatternsLed());
        var id = edid.DisplayId!;
        Assert.Equal("2.0", id.Version);
        Assert.Equal("generic display", id.ProductType);
        Assert.Equal(new[] { "Type VII timings", "tiled topology" }, id.Blocks);
        var wall = Assert.Single(id.Timings);
        Assert.Equal((7680, 2160), (wall.HActive, wall.VActive));
        Assert.True(wall.HActive > 4095);                                     // what an 18-byte DTD cannot say
        Assert.Equal(870240, wall.PixelClockKHz);
        Assert.Equal(SignalRate.Of(50, 1), wall.Rate);
        Assert.True(wall.Preferred);
        Assert.Equal("DisplayID 2", wall.Source);
        Assert.NotNull(id.Tiled);
        Assert.Equal((2, 1, 1, 1, 3840, 2160, true), (id.Tiled!.TotalHorizontal, id.Tiled.TotalVertical, id.Tiled.Column, id.Tiled.Row, id.Tiled.TileWidth, id.Tiled.TileHeight, id.Tiled.SingleEnclosure));
        Assert.Contains((7680, 2160), edid.RastersOffered);
        Assert.Equal((7680, 2160), edid.RastersOffered[0]);                   // largest first
        var advertised = edid.AdvertisedWords;
        Assert.Contains("7680×2160 / 3840×2160 / 1920×1080", advertised);
        Assert.Contains("50 / 59.94 / 60 Hz", advertised);
        Assert.Contains("RGB 4:4:4 / YCbCr 4:4:4 / YCbCr 4:2:2 / YCbCr 4:2:0", advertised);
        Assert.Contains("8-bit / 12-bit", advertised);
        Assert.Contains("HDR: PQ (HDR10), HLG", advertised);
        Assert.Contains("colorimetry: BT.2020 RGB", advertised);
        Assert.Contains("audio: up to 2 channels", advertised);
        Assert.Contains("tile 1,1 of 2×1 · 3840×2160 each · one enclosure", advertised);

        var plain = Edid.Parse(EdidSamples.PatternsLed(withDisplayId: false));
        Assert.Null(plain.DisplayId);
        Assert.Equal(1, plain.ExtensionCount);
        Assert.NotEqual(edid.Hash, plain.Hash);
    }

    [Fact]
    public void AFaultIsAProblemNamedNeverAThrow()
    {
        var bytes = EdidSamples.PatternsLed();
        bytes[100] ^= 0x55;                                                    // a byte flipped in the base block
        var bad = Edid.Parse(bytes);
        Assert.Contains(bad.Problems, p => p.Contains("base block's checksum"));
        Assert.False(bad.ChecksumsValid);
        Assert.Equal("PTN 0001 · PATTERNS LED", bad.Identity);                // what parsed stands
        Assert.Contains("CHECKSUM BAD", bad.Summary);

        var truncated = Edid.Parse(EdidSamples.PatternsLed()[..100]);
        Assert.Contains(truncated.Problems, p => p.Contains("only 100 bytes"));
        Assert.Equal("???", truncated.Manufacturer);

        var empty = Edid.Parse(Array.Empty<byte>());
        Assert.Contains("no EDID", empty.Problems);
        Assert.Equal("", empty.Hash);
        Assert.Contains("no EDID", Edid.Parse(null).Problems);                  // null is nothing, never a throw
    }

    [Fact]
    public void AnExtensionThatIsNotThereIsSaidAndTheHeaderIsChecked()
    {
        var bytes = EdidSamples.PatternsLed(withDisplayId: false);
        bytes[126] = 2;                                                        // says two extensions, carries one
        bytes[127] = Edid.ChecksumFor(bytes.AsSpan(0, 127));
        var short1 = Edid.Parse(bytes);
        Assert.Contains(short1.Problems, p => p.Contains("says 2 extensions, 1 present"));
        Assert.NotNull(short1.Cta);
        var noHeader = EdidSamples.PatternsLed();
        noHeader[0] = 0x01;
        Assert.Contains(Edid.Parse(noHeader).Problems, p => p.Contains("header"));
        Assert.False(Edid.LooksLikeEdid(noHeader));
        Assert.True(Edid.LooksLikeEdid(EdidSamples.PatternsLed()));
        Assert.Equal((3840, 2160), (Edid.Vic(96).Width, Edid.Vic(96).Height));
        Assert.Equal(SignalRate.Of(50, 1), Edid.Vic(96).Rate);
        Assert.Equal(0, Edid.Vic(250).Width);
    }
}
