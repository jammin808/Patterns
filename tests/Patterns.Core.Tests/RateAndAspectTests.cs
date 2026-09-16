using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 63: "when a screen refresh rate is changed, it still keeps displaying different data to
/// Windows: from 60 Hz to 50 Hz in Patterns, the Tech Info Chip still reads that Patterns is
/// trying to push 60." The rule an output paces by, and the words for a size's shape the chip
/// gained beside it.
/// </summary>
public class RateAndAspectTests
{
    [Theory]
    [InlineData(0, 0, 60.0, 0)]        // nothing known: every beat, as before
    [InlineData(60, 0, 60.0, 60)]      // asked for 60, display unknown: 60
    [InlineData(60, 50, 60.0, 50)]     // asked for 60 on a 50 Hz display: the glass wins
    [InlineData(30, 50, 60.0, 30)]     // asked for 30 on a 50 Hz display: 30
    [InlineData(0, 50, 60.0, 50)]      // the display's own rate, and the clock beats faster: pace to the display
    [InlineData(0, 60, 60.0, 0)]       // the display's own rate at the clock's own beat: unpaced — pacing here would drop frames
    [InlineData(0, 50, 0.0, 0)]        // the clock not yet measured: unpaced until it is
    [InlineData(0, 120, 60.0, 0)]      // a display faster than the clock: every beat is all it can get
    [InlineData(0, 50, 51.0, 0)]       // within the family: the same clock, read a hair high
    [InlineData(0, 50, 54.0, 50)]      // a clock beating 54 is not a 50 Hz clock: paced to the display (round 64 — the family rule, not a tenth)
    [InlineData(0, 60, 59.94, 0)]      // 59.94 and 60 are one family: unpaced
    [InlineData(0, 24, 60.0, 24)]      // a cinema display under a 60 Hz clock: 24
    public void TheOutputPresentsAtTheRateItsDisplayCanShow(int wanted, int displayHz, double clockHz, int expected)
        => Assert.Equal(expected, OutputRate.Present(wanted, displayHz, clockHz));

    [Theory]
    [InlineData(59.94, 60, true)]
    [InlineData(29.97, 30, true)]
    [InlineData(23.976, 24, true)]
    [InlineData(60.4, 60, true)]       // a measured clock, read a hair high
    [InlineData(50, 60, false)]
    [InlineData(60, 75, false)]
    [InlineData(60, 120, false)]
    [InlineData(48, 50, false)]
    [InlineData(24, 25, false)]
    [InlineData(0, 60, false)]         // unknown is never the same as anything
    public void RatesAreOneFamilyWithinTheToleranceAndDifferentCadencesOutsideIt(double a, double b, bool same)
    {
        Assert.Equal(same, OutputRate.SameFamily(a, b));
#pragma warning disable S2234 // the family is symmetric: the pair is asked both ways on purpose
        Assert.Equal(same, OutputRate.SameFamily(b, a));
#pragma warning restore S2234
    }

    [Theory]
    [InlineData(0, 60, 50.0, true, 60)]      // the display's own 60 under a 50 Hz clock: limited — the review's case
    [InlineData(0, 60, 60.2, false, 60)]     // the clock is the display's own
    [InlineData(0, 60, 59.94, false, 60)]    // one family
    [InlineData(0, 50, 60.0, false, 50)]     // a slower display under a faster clock: paced, never limited
    [InlineData(60, 60, 50.0, true, 60)]     // asked for 60 on a 60 Hz display under a 50 Hz clock: limited
    [InlineData(30, 60, 50.0, false, 30)]    // asked for 30: the clock has beats enough
    [InlineData(0, 120, 60.0, true, 120)]    // a 120 Hz display under a 60 Hz clock: limited, and said so
    [InlineData(0, 60, 0.0, false, 60)]      // the clock not measured: unknown, never a claim either way
    [InlineData(0, 0, 50.0, false, 0)]       // no display known: nothing to need
    public void AnOutputIsLimitedWhenItsDisplayNeedsMoreBeatsThanTheMeasuredClockSupplies(int present, int displayHz, double clockHz, bool limited, int needed)
    {
        var limit = OutputRate.ClockLimit(present, displayHz, clockHz);
        Assert.Equal(limited, limit.Limited);
        Assert.Equal(needed, limit.NeededHz);
        if (limited) Assert.Contains("LIMITED BY RENDER CLOCK", limit.Words("Output 2"));
        else Assert.Equal("", limit.Words("Output 2"));
    }

    [Fact]
    public void TheBudgetsReadingsNameTheLimitedOutputsAndTheHealthRowSaysSo()
    {
        var limited = new FrameBudgetReading(Patterns.Core.Model.SinkKind.Output, 2, "Output 2", 100, 0, 100, 5, 8, "", 50, TargetFps: 0, DisplayHz: 60, ClockHz: 50.0);
        var served = new FrameBudgetReading(Patterns.Core.Model.SinkKind.Output, 1, "Output 1", 100, 0, 100, 5, 8, "", 50, TargetFps: 50, DisplayHz: 50, ClockHz: 50.0);
        var pane = new FrameBudgetReading(Patterns.Core.Model.SinkKind.Preview, 0, "Preview", 100, 0, 100, 5, 8, "", 50, ClockHz: 50.0);
        var readings = new[] { limited, served, pane };
        var words = Assert.Single(FrameBudgets.ClockLimited(readings));
        Assert.Equal("Output 2: 60 Hz needed, render clock 50.0 Hz — LIMITED BY RENDER CLOCK", words);
        Assert.Equal(50.0, FrameBudgets.ClockHz(readings));

        var amber = SuperCheck.Run(new CheckFacts { RenderClockHz = 50.0, ClockLimited = FrameBudgets.ClockLimited(readings) }).Rows.Single(r => r.Item == "Render clock");
        Assert.Equal(CheckLight.Amber, amber.Light);
        Assert.Contains("LIMITED BY RENDER CLOCK", amber.Value);
        Assert.Contains("one display's refresh for every window", amber.Note);
        var green = SuperCheck.Run(new CheckFacts { RenderClockHz = 60.0 }).Rows.Single(r => r.Item == "Render clock");
        Assert.Equal(CheckLight.Green, green.Light);
        Assert.Contains("60.0 Hz", green.Value);
        Assert.DoesNotContain(SuperCheck.Run(new CheckFacts()).Rows, r => r.Item == "Render clock");   // not measured: no number, no row
    }

    [Theory]
    [InlineData(1920, 1080, "16:9")]
    [InlineData(3840, 2160, "16:9")]
    [InlineData(1366, 768, "16:9")]     // the laptop panel that is 16:9 to everyone who owns one
    [InlineData(1920, 1200, "16:10")]
    [InlineData(1024, 768, "4:3")]
    [InlineData(2560, 1080, "21:9")]
    [InlineData(3440, 1440, "21:9")]
    [InlineData(1080, 1920, "9:16")]
    [InlineData(1000, 1000, "1:1")]
    [InlineData(5120, 1440, "32:9")]
    [InlineData(1000, 700, "10:7")]     // no name: the reduced pair while it reads
    [InlineData(1234, 555, "2.22:1")]   // a pair nobody would read: the decimal
    [InlineData(0, 1080, "")]
    public void ASizeHasTheShapeAnEngineerCallsIt(int w, int h, string words)
        => Assert.Equal(words, AspectWords.Of(w, h));
}
