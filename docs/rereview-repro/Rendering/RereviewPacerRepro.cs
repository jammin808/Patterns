using Patterns.Rendering;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>Re-review reproducer (kept under docs/rereview-repro, outside the test projects), RT-4: what a 60 Hz output shows when the follow asks it for 50.</summary>
public class RereviewPacerRepro
{
    [Fact]
    public void RT4_FiftyOnASixtyHertzClockHoldsAFrameForTwoRefreshesTenTimesASecond()
    {
        var state = new FramePacerState();
        var shown = new List<int>();
        for (var k = 0; k < 60; k++)
            if (FramePacer.ShouldPresent(100.0 + k / 60.0 + 0.0005, 50, state, out _)) shown.Add(k);
        var gaps = shown.Zip(shown.Skip(1), (a, c) => c - a).ToList();
        Assert.InRange(shown.Count, 49, 51);
        Assert.Equal(10, gaps.Count(g => g == 2));                               // ten held frames a second: a 10 Hz judder

        var full = new FramePacerState();
        var all = 0;
        for (var k = 0; k < 60; k++)
            if (FramePacer.ShouldPresent(100.0 + k / 60.0 + 0.0005, 60, full, out _)) all++;
        Assert.Equal(60, all);                                                    // at 60 every refresh presents
    }
}
