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
    [InlineData(0, 50, 54.0, 0)]       // within a tenth of the display: the same clock, jittering
    [InlineData(0, 24, 60.0, 24)]      // a cinema display under a 60 Hz clock: 24
    public void TheOutputPresentsAtTheRateItsDisplayCanShow(int wanted, int displayHz, double clockHz, int expected)
        => Assert.Equal(expected, OutputRate.Present(wanted, displayHz, clockHz));

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
