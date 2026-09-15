using System.Text.Json;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 65: signal truth. A rate is a rational and 59.94 is not 60; a contract says what a link
/// is meant to carry; an observation says what Windows sends, unknowns left unknown; the
/// comparison moves a light on evidence alone — and Super Check's SIGNAL rows come from it.
/// </summary>
public class SignalTruthTests
{
    [Fact]
    public void ARateIsAnExactRationalAndADriversSpellingSnapsOntoIt()
    {
        var ntsc = SignalRate.Of(60000, 1001);
        Assert.Equal(ntsc, SignalRate.Of(59940, 1000));          // 59.94 as a driver spells it
        Assert.Equal(ntsc, SignalRate.Parse("59.94"));
        Assert.Equal(ntsc, SignalRate.Parse("60000/1001"));
        Assert.Equal("59.94", ntsc.Words);
        Assert.Equal("60000/1001", ntsc.Rational);
        Assert.NotEqual(ntsc, SignalRate.Of(60, 1));               // 59.94 ≠ 60
        Assert.Equal(SignalRate.Of(60, 1), SignalRate.Of(120, 2)); // reduced
        Assert.Equal(SignalRate.Of(50, 1), SignalRate.Parse("50p"));
        Assert.Equal(SignalRate.Of(50, 1), SignalRate.Parse("50 Hz"));
        Assert.Equal(SignalRate.Of(24000, 1001), SignalRate.Parse("23.976"));
        Assert.Equal("23.976", SignalRate.Parse("23.98").Words);   // the rounded spelling is the same rate
        Assert.Equal("29.97", SignalRate.Of(30000, 1001).Words);
        Assert.Equal("119.88", SignalRate.Of(120000, 1001).Words);
        Assert.False(SignalRate.Parse("8").IsSet);                 // not a rate a display runs at: left to mean bits
        Assert.False(SignalRate.Parse("").IsSet);
        Assert.False(SignalRate.Parse("fast").IsSet);
        Assert.False(SignalRate.Of(0, 1).IsSet);
        Assert.Equal("", SignalRate.None.Words);
        Assert.Equal("75", SignalRate.Of(75, 1).Words);
    }

    [Fact]
    public void TheFamiliesAreExplicitNotAPercentage()
    {
        Assert.Equal(TimingFamily.Film, SignalRate.Parse("23.976").Family);
        Assert.Equal(TimingFamily.Film, SignalRate.Of(24, 1).Family);
        Assert.Equal(TimingFamily.Pal, SignalRate.Of(25, 1).Family);
        Assert.Equal(TimingFamily.Pal, SignalRate.Of(50, 1).Family);
        Assert.Equal(TimingFamily.Pal, SignalRate.Of(100, 1).Family);
        Assert.Equal(TimingFamily.NtscFractional, SignalRate.Of(30000, 1001).Family);
        Assert.Equal(TimingFamily.NtscFractional, SignalRate.Of(60000, 1001).Family);
        Assert.Equal(TimingFamily.Integer, SignalRate.Of(30, 1).Family);
        Assert.Equal(TimingFamily.Integer, SignalRate.Of(60, 1).Family);
        Assert.Equal(TimingFamily.Integer, SignalRate.Of(120, 1).Family);
        Assert.Equal(TimingFamily.Other, SignalRate.Of(144, 1).Family);
        Assert.Equal(TimingFamily.Unknown, SignalRate.None.Family);
        // The render pacer's looser rule still calls 59.94 and 60 one cadence — for pacing, not for truth.
        Assert.True(OutputRate.SameFamily(59.94, 60));
        Assert.NotEqual(SignalRate.Of(60000, 1001).Family, SignalRate.Of(60, 1).Family);
    }

    [Fact]
    public void TheContractIsWordsBothWaysAndAWordThatIsNotOneIsNamed()
    {
        var c = new SignalContract();
        Assert.False(c.IsSet);
        Assert.Equal("", SignalWords.Apply("3840x2160 50 RGB 8 SDR FULL 8CH", c));
        Assert.True(c.IsSet);
        Assert.Equal((3840, 2160), (c.Width, c.Height));
        Assert.Equal(SignalRate.Of(50, 1), c.Rate);
        Assert.Equal(PixelEncoding.RGB, c.Encoding);
        Assert.Equal(8, c.BitDepth);
        Assert.Equal(QuantizationRange.Full, c.Range);
        Assert.Equal(DynamicRange.SDR, c.Dynamic);
        Assert.Equal(AudioPolicy.Multichannel, c.Audio);
        Assert.Equal("3840x2160 50 RGB 8 FULL SDR 8CH", SignalWords.Of(c));

        // Each word sets its property and the rest stay; the rate's spellings; the depth with its unit; the colour and the connector.
        Assert.Equal("", SignalWords.Apply("59.94 422 10-bit HDR10 LIMITED STEREO 2020 DP", c));
        Assert.Equal("3840x2160 59.94 422 10 LIMITED HDR10 2020 STEREO DP", SignalWords.Of(c));
        Assert.Equal(ColourSpace.Rec2020, c.Colour);
        Assert.Equal(SignalTransport.DisplayPort, c.Transport);
        Assert.Contains("Rec. 2020", SignalTruth.DesignWords(c));
        Assert.EndsWith("over DisplayPort", SignalTruth.DesignWords(c));
        var again = new SignalContract();
        Assert.Equal("", SignalWords.Apply(SignalWords.Of(c), again));
        Assert.Equal(SignalWords.Of(c), SignalWords.Of(again));

        var error = SignalWords.Apply("3840x2160 fast", c);
        Assert.Contains("'fast' is not a signal word", error);
        Assert.Contains("59.94", error);                            // the vocabulary named
        Assert.Equal("", SignalWords.Apply("DIAGNOSTIC", c));
        Assert.Equal("diagnostic", c.Fallback);
        Assert.Equal("", SignalWords.Apply("CLEAR", c));
        Assert.False(c.IsSet);
        Assert.Equal("", SignalWords.Of(c));
        Assert.Equal("none — the display's own", SignalTruth.DesignWords(c));

        var diagnostic = SignalContract.Diagnostic();
        Assert.Equal("1920×1080 · 50 Hz · RGB 4:4:4 · 8-bit · SDR · stereo", SignalTruth.DesignWords(diagnostic));
    }

    [Fact]
    public void TheContractIsSavedWithTheScreen()
    {
        var placement = new ScreenPlacement { ScreenId = "a" };
        SignalWords.Apply("3840x2160 59.94 RGB 10 HDR10", placement.Signal);
        var json = JsonUtil.Serialize(placement);
        var back = JsonSerializer.Deserialize<ScreenPlacement>(json, JsonUtil.Options)!;
        Assert.Equal("3840x2160 59.94 RGB 10 HDR10", SignalWords.Of(back.Signal));
        Assert.Equal(SignalRate.Of(60000, 1001), back.Signal.Rate);
        var compact = json.Replace(" ", "").Replace("\n", "").Replace("\r", "");
        Assert.DoesNotContain("\"Rate\":", compact);                  // the rational is what is saved, not a derived reading
        Assert.Contains("\"RateNumerator\":60000", compact);
    }

    private static SignalContract Contract(string words)
    {
        var c = new SignalContract();
        Assert.Equal("", SignalWords.Apply(words, c));
        return c;
    }

    private static SignalObservation Observed(SignalRate rate, PixelEncoding? encoding = PixelEncoding.RGB, int bits = 8, bool? hdr = false, bool? hdrCapable = false, int w = 3840, int h = 2160)
        => new(0, 0, w, h, w, h, rate, encoding, bits, hdr, hdrCapable, Monitor: "Main LED processor");

    [Fact]
    public void AContractHeldAgainstWhatWindowsSendsMatchesOnlyOnEvidence()
    {
        var contract = Contract("3840x2160 50 RGB 8 SDR");
        var match = SignalTruth.Compare("Main", contract, 3840, 2160, 0, 50, Observed(SignalRate.Of(50, 1)), 50.0);
        Assert.Equal(SignalVerdict.Match, match.Verdict);
        Assert.Equal("MATCH", match.Result);
        Assert.Equal("3840×2160 · 50 Hz · RGB 4:4:4 · 8-bit · SDR", match.Design);
        Assert.Equal("3840×2160 · the display's own rate (50 Hz)", match.Requested);
        Assert.Equal("3840×2160 · 50 Hz (50/1) · RGB 4:4:4 · 8-bit · SDR", match.Observed);
        Assert.All(match.Lines, l => Assert.NotEqual(CheckLight.Red, l.Light));
        Assert.Contains(match.Lines, l => l.Item == "Raster" && l.Light == CheckLight.Green && l.Value == "expected 3840×2160 · detected 3840×2160");
        Assert.Contains(match.Lines, l => l.Item == "Rate" && l.Light == CheckLight.Green && l.Value == "50 Hz (50/1)");
        Assert.Contains(match.Lines, l => l.Item == "Render clock" && l.Light == CheckLight.Green);
        Assert.StartsWith("DESIGN\n3840×2160", match.Text);
        Assert.Contains("RESULT\nMATCH", match.Text);

        // 59.94 observed against 50 asked: a mismatch, and the note names the families.
        var families = SignalTruth.Compare("Main", contract, 3840, 2160, 0, 60, Observed(SignalRate.Of(60000, 1001)), 60.0);
        Assert.Equal(SignalVerdict.Mismatch, families.Verdict);
        var rate = families.Lines.Single(l => l.Item == "Rate");
        Assert.Equal(CheckLight.Amber, rate.Light);                    // degraded, not the wrong picture: amber
        Assert.Equal("50 Hz asked · 59.94 Hz observed", rate.Value);
        Assert.Contains("PAL-derived", rate.Note);
        Assert.Contains("NTSC-derived", rate.Note);

        // 59.94 observed against 60 asked: not the same rate — the note says what the near miss costs.
        var sixty = SignalTruth.Compare("Main", Contract("60"), 3840, 2160, 0, 60, Observed(SignalRate.Of(60000, 1001)), 60.0);
        Assert.Equal(SignalVerdict.Mismatch, sixty.Verdict);
        Assert.Contains("every 16.7 s", sixty.Lines.Single(l => l.Item == "Rate").Note);

        // The raster wrong: red, with the fix.
        var raster = SignalTruth.Compare("Main", contract, 3840, 2160, 0, 50, Observed(SignalRate.Of(50, 1), w: 1920, h: 1080), 50.0);
        Assert.Equal(SignalVerdict.Mismatch, raster.Verdict);
        Assert.Contains(raster.Lines, l => l.Item == "Raster" && l.Light == CheckLight.Red && l.Value.Contains("detected 1920×1080") && l.Note.StartsWith("FIX:"));
    }

    [Fact]
    public void WhatWindowsDoesNotSayStaysUnknownAndIsNeverInferred()
    {
        var contract = Contract("3840x2160 50 RGB 8 SDR");
        var blind = SignalTruth.Compare("Main", contract, 3840, 2160, 0, 50, Observed(SignalRate.Of(50, 1), encoding: null, bits: 0, hdr: null, hdrCapable: null), 50.0);
        Assert.Equal(SignalVerdict.Match, blind.Verdict);              // the raster and the rate are evidence enough for MATCH
        foreach (var item in new[] { "Encoding", "Bit depth", "Dynamic range" })
        {
            var line = blind.Lines.Single(l => l.Item == item);
            Assert.Equal(CheckLight.Grey, line.Light);
            Assert.Contains("observed unknown", line.Value);
            Assert.Equal(SignalTruth.NotAvailable, line.Note);
        }
        Assert.Contains("encoding unknown", blind.Observed);
        Assert.Contains("bit depth unknown", blind.Observed);
        Assert.Contains("HDR unknown", blind.Observed);

        // A receiver that advertises HDR is a capability, not a signal: a grey line, no verdict moved.
        var capable = SignalTruth.Compare("Main", contract, 3840, 2160, 0, 50, Observed(SignalRate.Of(50, 1), hdrCapable: true), 50.0);
        Assert.Equal(SignalVerdict.Match, capable.Verdict);
        Assert.Contains(capable.Lines, l => l.Item == "HDR capability" && l.Light == CheckLight.Grey && l.Value.Contains("advertises HDR"));

        // HDR on against an SDR contract: red, and the fix names Windows' switch.
        var hdrOn = SignalTruth.Compare("Main", contract, 3840, 2160, 0, 50, Observed(SignalRate.Of(50, 1), hdr: true, hdrCapable: true), 50.0);
        Assert.Equal(SignalVerdict.Mismatch, hdrOn.Verdict);
        Assert.Contains(hdrOn.Lines, l => l.Item == "Dynamic range" && l.Light == CheckLight.Amber && l.Note.Contains("HDR switch is on"));

        // 4:2:0 observed against RGB asked: the classic soft-text mismatch, named.
        var chroma = SignalTruth.Compare("Main", contract, 3840, 2160, 0, 50, Observed(SignalRate.Of(50, 1), encoding: PixelEncoding.YCbCr420), 50.0);
        Assert.Equal(SignalVerdict.Mismatch, chroma.Verdict);
        Assert.Contains(chroma.Lines, l => l.Item == "Encoding" && l.Light == CheckLight.Amber && l.Value == "RGB 4:4:4 asked · YCbCr 4:2:0 observed" && l.Note.Contains("4:2:0 softens"));

        // The connector: Windows names it, so a contract that names one is checked; the colour space it never names.
        var wired = Contract("3840x2160 50 RGB 8 SDR 709 HDMI");
        var onDp = SignalTruth.Compare("Main", wired, 3840, 2160, 0, 50, Observed(SignalRate.Of(50, 1)) with { Connector = "DisplayPort" }, 50.0);
        Assert.Equal(SignalVerdict.Mismatch, onDp.Verdict);
        Assert.Contains(onDp.Lines, l => l.Item == "Transport" && l.Light == CheckLight.Amber && l.Value == "HDMI asked · DisplayPort observed");
        Assert.Contains(onDp.Lines, l => l.Item == "Colour space" && l.Light == CheckLight.Grey && l.Note == SignalTruth.NotAvailable);
        Assert.EndsWith("over DisplayPort", onDp.Observed);
        var onHdmi = SignalTruth.Compare("Main", wired, 3840, 2160, 0, 50, Observed(SignalRate.Of(50, 1)) with { Connector = "HDMI" }, 50.0);
        Assert.Equal(SignalVerdict.Match, onHdmi.Verdict);
        Assert.Contains(onHdmi.Lines, l => l.Item == "Transport" && l.Light == CheckLight.Green);
        Assert.Equal(SignalTransport.UsbC, SignalTruth.TransportOf("DisplayPort over USB"));
        Assert.Equal(SignalTransport.Any, SignalTruth.TransportOf(""));

        // No observation at all: nothing verified, said plainly.
        var none = SignalTruth.Compare("Main", contract, 3840, 2160, 0, 0, null, -1);
        Assert.Equal(SignalVerdict.Unverified, none.Verdict);
        Assert.Equal("not observed", none.Observed);
        Assert.Contains("OBSERVED\nnot observed", none.Text);
        Assert.Contains(none.Lines, l => l.Item == "Observed" && l.Light == CheckLight.Grey);

        // No contract: evidence alone, one grey line when there is evidence, none when there is not.
        var open = SignalTruth.Compare("Main", new SignalContract(), 3840, 2160, 0, 50, Observed(SignalRate.Of(50, 1)), 50.0);
        Assert.Equal(SignalVerdict.Unverified, open.Verdict);
        Assert.Equal("none — the display's own", open.Design);
        Assert.Single(open.Lines);
        Assert.Equal("Signal", open.Lines[0].Item);
        Assert.Empty(SignalTruth.Compare("Main", null, 3840, 2160, 0, 0, null, -1).Lines);

        // The render clock against the rate: LIMITED reads amber here as it does on the Machine page.
        var limited = SignalTruth.Compare("Main", Contract("60"), 3840, 2160, 0, 60, Observed(SignalRate.Of(60, 1)), 50.0);
        Assert.Contains(limited.Lines, l => l.Item == "Render clock" && l.Light == CheckLight.Amber && l.Note.Contains("LIMITED BY RENDER CLOCK"));
    }

    [Fact]
    public void SuperCheckCarriesTheSignalLinesAndOnlyWhereThereAreAny()
    {
        var report = SignalTruth.Compare("Main", Contract("3840x2160 50 RGB 8 SDR"), 3840, 2160, 0, 50, Observed(SignalRate.Of(60000, 1001)), 50.0);
        var rows = SuperCheck.Run(new CheckFacts { Signals = new[] { report } }).Rows.Where(r => r.Section == "SIGNAL").ToList();
        Assert.NotEmpty(rows);
        Assert.Contains(rows, r => r.Item == "Main rate" && r.Light == CheckLight.Amber && r.Value == "50 Hz asked · 59.94 Hz observed");
        Assert.Contains(rows, r => r.Item == "Main contract" && r.Light == CheckLight.Green);
        Assert.DoesNotContain(SuperCheck.Run(new CheckFacts()).Rows, r => r.Section == "SIGNAL");
        var quiet = SignalTruth.Compare("Main", null, 1920, 1080, 0, 0, null, -1);
        Assert.DoesNotContain(SuperCheck.Run(new CheckFacts { Signals = new[] { quiet } }).Rows, r => r.Section == "SIGNAL");
    }

    [Fact]
    public void TheWireSpeaksTheContractAndReadsTheView()
    {
        var set = ControlProtocol.Parse("SCREEN 2 SIGNAL 3840x2160 50 RGB 8 SDR");
        Assert.True(set.IsAction);
        Assert.Equal(ShowActionKind.ScreenSignal, set.Action.Kind);
        Assert.Equal("2", set.Action.Target);
        Assert.Equal("3840x2160 50 RGB 8 SDR", set.Action.Value);
        var read = ControlProtocol.Parse("SCREEN 2 SIGNAL");
        Assert.Equal(RemoteCommandKind.ScreenSignal, read.Kind);
        Assert.Equal("2", read.Text);
        Assert.True(ControlProtocol.IsQuery(read));
        Assert.False(ControlProtocol.IsQuery(set));
        Assert.Equal(RemoteCommandKind.ScreenSignal, ControlProtocol.Parse("screen 2 signal").Kind);
        Assert.Equal((TargetKind.Screen, ValueKind.Text), ActionSpec.For(ShowActionKind.ScreenSignal));
        Assert.NotNull(ActionSpec.DeskOnly(ShowActionKind.ScreenSignal));
    }
}
