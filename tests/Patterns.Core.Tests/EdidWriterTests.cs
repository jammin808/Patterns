using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 65.8: the EDID a planned screen presents — built from its contract, valid, and parsed
/// back by the same parser that reads a real display's, so what a processor loads is what
/// Patterns can later recognise on the wire.
/// </summary>
public class EdidWriterTests
{
    [Fact]
    public void AStandardFormatGetsTheStandardTotalsAndAnExactRate()
    {
        var t = EdidWriter.Timing(1920, 1080, SignalRate.Of(50, 1));
        Assert.True(t.Standard);
        Assert.Equal((2640, 1125, 148500), (t.HTotal, t.VTotal, t.PixelClockKHz));
        Assert.Equal(SignalRate.Of(50, 1), t.Rate);
        Assert.True(t.FitsDtd);
        Assert.Equal("1920×1080p50 · 148.5 MHz · 2640×1125 total (CTA-861 timing)", t.Words);

        var ntsc = EdidWriter.Timing(1920, 1080, SignalRate.Of(60000, 1001));
        Assert.Equal((2200, 1125, 148350), (ntsc.HTotal, ntsc.VTotal, ntsc.PixelClockKHz));   // the 10 kHz step a descriptor has
        Assert.Equal(SignalRate.Of(60000, 1001), ntsc.Rate);                                // and the parser snaps it back to 59.94

        var uhd = EdidWriter.Timing(3840, 2160, SignalRate.Of(60, 1));
        Assert.Equal((4400, 2250, 594000), (uhd.HTotal, uhd.VTotal, uhd.PixelClockKHz));
    }

    [Fact]
    public void AnUnknownRasterGetsReducedBlankingWithTheClockOnTheStepAndTheRateExact()
    {
        var t = EdidWriter.Timing(1920, 1200, SignalRate.Of(50, 1));
        Assert.False(t.Standard);
        Assert.Equal(2080, t.HTotal);
        Assert.Equal(0, t.PixelClockKHz % 10);
        Assert.Equal(SignalRate.Of(50, 1), t.Rate);                    // exactly 50, not 49.98
        Assert.InRange(t.VBlank, 6 + 3 + 6, 60);
        Assert.True(t.FitsDtd);

        var wall = EdidWriter.Timing(7680, 2160, SignalRate.Of(50, 1));
        Assert.False(wall.FitsDtd);                                    // past what an 18-byte descriptor can say
        Assert.Equal(SignalRate.Of(50, 1), wall.Rate);
        Assert.Equal(7840, wall.HTotal);

        var odd = EdidWriter.Timing(1000, 700, SignalRate.Of(24000, 1001));
        Assert.True(odd.FitsDtd);
        Assert.Equal(SignalRate.Of(24000, 1001), odd.Rate);            // a fractional rate lands within the snap
    }

    [Fact]
    public void ThePlannedEdidBuildsValidAndParsesBackAsThePlan()
    {
        var plan = new EdidPlan(1920, 1080, SignalRate.Of(50, 1), "PATTERNS LED", ProductCode: 0x0A1B, Serial: 77, Audio: AudioPolicy.Stereo, Transport: SignalTransport.HDMI);
        var bytes = EdidWriter.Build(plan);
        Assert.Equal(256, bytes.Length);
        Assert.True(Edid.LooksLikeEdid(bytes));
        var edid = Edid.Parse(bytes);
        Assert.Empty(edid.Problems);
        Assert.True(edid.ChecksumsValid);
        Assert.Equal("PTN", edid.Manufacturer);
        Assert.Equal(0x0A1B, edid.ProductCode);
        Assert.Equal(77u, edid.SerialNumber);
        Assert.Equal("PTN-0A1B", edid.SerialText);
        Assert.Equal("PATTERNS LED", edid.Name);
        Assert.Equal("1.4", edid.Version);
        Assert.Equal("HDMI-a", edid.Interface);
        Assert.Equal(8, edid.ColourDepthBits);
        Assert.Equal(new[] { "RGB 4:4:4" }, edid.BaseEncodings);           // RGB only: the source cannot choose chroma subsampling the plan did not ask for
        var preferred = edid.Preferred!;
        Assert.Equal((1920, 1080), (preferred.HActive, preferred.VActive));
        Assert.Equal(SignalRate.Of(50, 1), preferred.Rate);
        Assert.Equal(new[] { "base", "CTA-861" }, edid.Blocks);
        var cta = edid.Cta!;
        var vic = Assert.Single(cta.Video);
        Assert.Equal(31, vic.Vic);                                          // 1080p50, native
        Assert.True(vic.Native);
        Assert.True(cta.BasicAudio);
        Assert.Equal(("LPCM", 2), (cta.Audio.Single().Format, cta.Audio.Single().Channels));
        Assert.Equal(new[] { "FL/FR" }, cta.Speakers);
        Assert.NotNull(cta.Hdmi);
        Assert.Equal("1.0.0.0", cta.Hdmi!.PhysicalAddress);
        Assert.False(cta.Hdmi.DeepColour30);
        Assert.Equal(150, cta.Hdmi.MaxTmdsMHz);
        Assert.Null(cta.HdmiForum);
        Assert.Null(cta.Hdr);
        Assert.Empty(cta.Colorimetry);
        Assert.Equal(new[] { SignalRate.Of(50, 1) }, edid.RatesOffered);   // one rate, one raster: the plan and nothing else
        Assert.Equal(new[] { (1920, 1080) }, edid.RastersOffered);
        Assert.Equal(new[] { 8 }, edid.BitDepthsOffered);
        Assert.False(edid.OffersHdr);

        // Deterministic: the same plan is the same bytes and the same hash.
        Assert.Equal(edid.Hash, Edid.Parse(EdidWriter.Build(plan)).Hash);
        var hex = EdidWriter.Hex(bytes);
        Assert.Equal(16, hex.TrimEnd().Split('\n').Length);
        Assert.StartsWith("00 FF FF FF FF FF FF 00 42 8E 1B 0A", hex);
        var summary = EdidWriter.Summary(plan, bytes);
        Assert.Contains("contract: 1920x1080 50 RGB 8 SDR 709 STEREO HDMI", summary);
        Assert.Contains("timing: 1920×1080p50 · 148.5 MHz", summary);
        Assert.Contains($"sha-256: {edid.Hash}", summary);
        Assert.Contains("use: load the .bin", summary);
    }

    [Fact]
    public void TheContractsWordsShapeTheBlocks()
    {
        var c = new SignalContract();
        Assert.Equal("", SignalWords.Apply("3840x2160 59.94 420 10 HDR10 2020 8CH HDMI LIMITED", c));
        var plan = EdidPlan.ForContract(c, "Main wall", 1920, 1080, EdidPlan.ProductCodeFor("screen-a"));
        Assert.Equal((3840, 2160), (plan.Width, plan.Height));
        Assert.Equal(SignalRate.Of(60000, 1001), plan.Rate);
        var edid = Edid.Parse(EdidWriter.Build(plan));
        Assert.Empty(edid.Problems);
        var cta = edid.Cta!;
        var vic = Assert.Single(cta.Video);
        Assert.Equal(97, vic.Vic);                                          // 2160p60 — 59.94 rides the 60 Hz code, as CTA-861 has it
        Assert.True(vic.Ycc420Only);                                        // in the 4:2:0 block alone: the source has no other way to send it
        Assert.Equal(new[] { "YCbCr 4:2:0", "RGB 4:4:4" }.OrderBy(x => x), edid.EncodingsOffered.OrderBy(x => x));
        Assert.Equal(8, cta.Audio.Single().Channels);
        Assert.Contains("RLC/RRC", cta.Speakers);
        Assert.True(cta.Hdmi!.DeepColour30);
        Assert.NotNull(cta.HdmiForum);                                      // 594 MHz is HDMI 2.x
        Assert.True(cta.HdmiForum!.Scdc);
        Assert.True(cta.HdmiForum.Ycc420DeepColour30);
        Assert.True(cta.Hdr!.Pq);
        Assert.False(cta.Hdr.Hlg);
        Assert.InRange(cta.Hdr.MaxLuminanceNits!.Value, 900, 1100);
        Assert.Contains("BT.2020 RGB", cta.Colorimetry);
        Assert.True(cta.QuantisationSelectableRgb);
        Assert.True(cta.QuantisationSelectableYcc);
        Assert.Equal(10, edid.ColourDepthBits);
        Assert.True(edid.OffersDynamic(DynamicRange.HDR10));
        Assert.True(edid.OffersColour(ColourSpace.Rec2020));
        Assert.True(edid.OffersRate(SignalRate.Of(60000, 1001)));
        Assert.True(edid.OffersRate(SignalRate.Of(60, 1)));

        // DisplayPort: no HDMI blocks, the interface byte says so; an empty contract makes the conservative plan.
        var dp = Edid.Parse(EdidWriter.Build(new EdidPlan(2560, 1440, SignalRate.Of(60, 1), "Foyer", Transport: SignalTransport.DisplayPort, Audio: AudioPolicy.None, Colour: ColourSpace.DciP3)));
        Assert.Equal("DisplayPort", dp.Interface);
        Assert.Null(dp.Cta!.Hdmi);
        Assert.False(dp.Cta.BasicAudio);
        Assert.Empty(dp.Cta.Audio);
        Assert.Empty(dp.Cta.Video);                                         // no VIC for 2560×1440: the descriptor carries the timing
        Assert.Equal(SignalRate.Of(60, 1), dp.Preferred!.Rate);
        Assert.Contains("DCI-P3", dp.Cta.Colorimetry);
        var plain = EdidPlan.ForContract(null, "Side", 1920, 1080, 5);
        Assert.Equal("1920x1080 60 RGB 8 SDR 709 STEREO HDMI", plain.Words);
        Assert.NotEqual(0, EdidPlan.ProductCodeFor("planned:1"));
        Assert.Equal(EdidPlan.ProductCodeFor("planned:1"), EdidPlan.ProductCodeFor("planned:1"));
        Assert.NotEqual(EdidPlan.ProductCodeFor("planned:1"), EdidPlan.ProductCodeFor("planned:2"));
        Assert.Equal(0x428E, EdidWriter.ManufacturerId("PTN"));
    }

    [Fact]
    public void TheExportWritesTheThreeFilesAndTheWireReadsTheEdid()
    {
        var plan = new EdidPlan(1920, 1080, SignalRate.Of(50, 1), "Stage left LED");
        var bytes = EdidWriter.Build(plan);
        var dir = Path.Combine(Path.GetTempPath(), "patterns-edid-" + Guid.NewGuid().ToString("N"));
        try
        {
            var name = EdidWriter.SafeFileName("Stage left LED");
            Assert.Equal("patterns-edid-stage-left-led", name);
            var written = EdidWriter.Export(dir, name, plan, bytes);
            Assert.Equal(new[] { "patterns-edid-stage-left-led.bin", "patterns-edid-stage-left-led.hex", "patterns-edid-stage-left-led.txt" }, written.Select(Path.GetFileName));
            Assert.Equal(bytes, File.ReadAllBytes(written[0]));
            Assert.StartsWith("00 FF FF FF FF FF FF 00", File.ReadAllText(written[1]));
            Assert.Contains("sha-256: " + Edid.Hash(bytes), File.ReadAllText(written[2]));
            Assert.Equal("patterns-edid-2", EdidWriter.SafeFileName("  2 ?? "));
        }
        finally
        {
            if (Directory.Exists(dir)) Directory.Delete(dir, true);
        }

        var read = ControlProtocol.Parse("SCREEN 2 EDID");
        Assert.Equal(RemoteCommandKind.ScreenEdid, read.Kind);
        Assert.Equal("2", read.Text);
        Assert.True(ControlProtocol.IsQuery(read));
    }

    [Fact]
    public void ARasterPastADescriptorGoesToDisplayIdWithAStandardPictureInTheBase()
    {
        var plan = new EdidPlan(7680, 2160, SignalRate.Of(50, 1), "LED wall", Transport: SignalTransport.DisplayPort);
        var bytes = EdidWriter.Build(plan);
        Assert.Equal(384, bytes.Length);
        var edid = Edid.Parse(bytes);
        Assert.Empty(edid.Problems);
        Assert.True(edid.ChecksumsValid);
        Assert.Equal(new[] { "base", "CTA-861", "DisplayID" }, edid.Blocks);
        Assert.Equal((1920, 1080), (edid.Preferred!.HActive, edid.Preferred.VActive));          // the descriptor's fallback picture
        Assert.Equal(SignalRate.Of(50, 1), edid.Preferred.Rate);
        var wall = Assert.Single(edid.DisplayId!.Timings);
        Assert.Equal((7680, 2160), (wall.HActive, wall.VActive));
        Assert.Equal(SignalRate.Of(50, 1), wall.Rate);
        Assert.True(wall.Preferred);
        Assert.Equal("2.0", edid.DisplayId.Version);
        Assert.Equal((7680, 2160), edid.RastersOffered[0]);
        Assert.Contains("7680×2160", EdidWriter.Summary(plan, bytes));
        Assert.Contains("(CVT reduced blanking)", EdidWriter.Summary(plan, bytes));
    }
}
