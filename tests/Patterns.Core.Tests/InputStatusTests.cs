using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>Round 65.11: the far end's word read through a pattern, held against the contract as the third witness, and the wire's RECEIVED.</summary>
public class InputStatusTests
{
    private static SignalContract Contract() { var c = new SignalContract(); SignalWords.Apply("3840x2160 50 RGB 8 SDR", c); return c; }

    [Fact]
    public void ThePatternReadsWhatTheBoxSaysAndNothingMore()
    {
        Assert.True(InputStatus.TryRead(@"(?<w>\d+)x(?<h>\d+)@(?<hz>[\d.]+)\s*(?<enc>RGB|444|422|420)?\s*(?<bits>\d+)?", "INPUT 3840x2160@59.94 422 10", out var r, out var problem));
        Assert.Equal("", problem);
        Assert.Equal((3840, 2160), (r.Width, r.Height));
        Assert.Equal(SignalRate.Of(60000, 1001), r.Rate);
        Assert.Equal(PixelEncoding.YCbCr422, r.Encoding);
        Assert.Equal(10, r.BitDepth);
        Assert.Equal(DynamicRange.Any, r.Dynamic);                                                       // unsaid stays unsaid

        // PJLink class 2's IRES answer through the profile's own pattern: the raster alone.
        var pj = ProfileSession.For(new DeviceConfig { Profile = DeviceProfile.PjLink });
        Assert.Equal("%2IRES ?", pj.InputQuery);
        Assert.True(InputStatus.TryRead(pj.InputPattern!, "%2IRES=1920x1080", out var res, out _));
        Assert.Equal((1920, 1080), (res.Width, res.Height));
        Assert.False(res.Rate.IsSet);
        Assert.False(InputStatus.TryRead(pj.InputPattern!, "%2IRES=ERR1", out _, out _));            // a class 1 projector: no answer
        Assert.False(InputStatus.TryRead(pj.InputPattern!, "%1POWR=1", out _, out _));

        // A pattern that does not compile is a problem named, never a throw; one that matches nothing useful is false.
        Assert.False(InputStatus.TryRead("(?<w>[", "x", out _, out var bad));
        Assert.Contains("does not compile", bad);
        Assert.False(InputStatus.TryRead(@"OK (?<other>\w+)", "OK fine", out _, out _));
    }

    [Fact]
    public void TheDeviceAsksOnlyWhenItCarriesAScreen()
    {
        var pj = ProfileSession.For(new DeviceConfig { Profile = DeviceProfile.PjLink });
        Assert.Null(InputStatus.QueryFor(new DeviceConfig { Profile = DeviceProfile.PjLink }, pj));                                                    // no screen: never asked
        Assert.Equal("%2IRES ?", InputStatus.QueryFor(new DeviceConfig { Profile = DeviceProfile.PjLink, InputScreen = "2" }, pj));                    // the profile's own
        Assert.Equal("GET /api/input", InputStatus.QueryFor(new DeviceConfig { Profile = DeviceProfile.PjLink, InputScreen = "2", InputQuery = "GET /api/input" }, pj));
        Assert.Null(InputStatus.QueryFor(new DeviceConfig { Profile = DeviceProfile.PjLink, InputScreen = "2", InputQuery = "-" }, pj));               // a dash: none
        var lines = ProfileSession.For(new DeviceConfig { Profile = DeviceProfile.Lines });
        Assert.Null(InputStatus.QueryFor(new DeviceConfig { Profile = DeviceProfile.Lines, InputScreen = "LED wall" }, lines));                          // plain lines know no words of their own
        Assert.Null(InputStatus.PatternFor(new DeviceConfig { Profile = DeviceProfile.Lines }, lines));
        Assert.Equal(TimeSpan.FromSeconds(30), InputStatus.EveryFor(new DeviceConfig()));
        Assert.Equal(TimeSpan.FromSeconds(120), InputStatus.EveryFor(new DeviceConfig { InputEverySeconds = 120 }));
        Assert.Equal(5, new DeviceConfig { InputEverySeconds = 1 }.InputEverySeconds);
    }

    [Fact]
    public void TheFarEndsWordIsTheThirdWitness()
    {
        var contract = Contract();
        var observed = new SignalObservation(0, 0, 3840, 2160, 3840, 2160, SignalRate.Of(50, 1), PixelEncoding.RGB, 8, false);

        // Agreed: green lines, MATCH stands.
        var agreed = new SignalContract(); SignalWords.Apply("3840x2160 50 RGB 8", agreed);
        var same = SignalTruth.Compare("Screen 2", contract, 3840, 2160, 50, 50, observed, 50, received: agreed, receivedBy: "Brompton SX40", receivedAtUtc: new DateTime(2026, 9, 15, 13, 2, 0, DateTimeKind.Utc));
        Assert.Equal(SignalVerdict.Match, same.Verdict);
        Assert.Contains(same.Lines, l => l.Item == "Received raster" && l.Light == CheckLight.Green && l.Value == "3840×2160 — Brompton SX40");
        Assert.Contains(same.Lines, l => l.Item == "Received rate" && l.Light == CheckLight.Green);
        Assert.StartsWith("3840×2160 · 50 Hz · RGB 4:4:4 · 8-bit — Brompton SX40 at ", same.Received);
        Assert.Contains("RECEIVED\n3840×2160", same.Text);
        Assert.Contains("· RECEIVED 3840×2160", same.BriefLine);

        // The box receives another raster: red, and MISMATCH whatever Windows believes it sends.
        var other = new SignalContract(); SignalWords.Apply("1920x1080 50", other);
        var wrong = SignalTruth.Compare("Screen 2", contract, 3840, 2160, 50, 50, observed, 50, received: other, receivedBy: "engineer");
        Assert.Equal(SignalVerdict.Mismatch, wrong.Verdict);
        var raster = wrong.Lines.Single(l => l.Item == "Received raster");
        Assert.Equal(CheckLight.Red, raster.Light);
        Assert.Equal("expected 3840×2160 · engineer receives 1920×1080", raster.Value);
        Assert.Contains("FIX:", raster.Note);

        // Without an observation the far end's word still judges: a disagreement is MISMATCH, an agreement stays UNVERIFIED (Windows has not spoken).
        var blind = SignalTruth.Compare("Screen 2", contract, 3840, 2160, 50, 50, null, 0, received: other, receivedBy: "engineer");
        Assert.Equal(SignalVerdict.Mismatch, blind.Verdict);
        Assert.Equal(SignalVerdict.Unverified, SignalTruth.Compare("Screen 2", contract, 3840, 2160, 50, 50, null, 0, received: agreed, receivedBy: "engineer").Verdict);

        // Nothing said: no received lines, no RECEIVED column.
        var quiet = SignalTruth.Compare("Screen 2", contract, 3840, 2160, 50, 50, observed, 50);
        Assert.DoesNotContain(quiet.Lines, l => l.Item.StartsWith("Received"));
        Assert.Equal("", quiet.Received);
    }

    [Fact]
    public void TheWireSaysReceived()
    {
        Assert.Equal(new ShowAction(ShowActionKind.ScreenReceived, "2", "3840x2160 50 RGB 8"), ControlProtocol.Parse("SCREEN 2 RECEIVED 3840x2160 50 RGB 8").Action);
        Assert.Equal(new ShowAction(ShowActionKind.ScreenReceived, "2", "CLEAR"), ControlProtocol.Parse("SCREEN 2 RECEIVED CLEAR").Action);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("SCREEN 2 RECEIVED").Kind);
    }
}
