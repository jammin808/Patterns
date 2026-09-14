using System.Diagnostics;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>The page switch's budget: the press, the handler, the build inside it and the frame after; sixty kept, the worst and the average; the words.</summary>
public class SwitchBudgetTests
{
    private static long Ticks(double ms) => (long)(ms * Stopwatch.Frequency / 1000.0);

    [Fact]
    public void ASwitchIsThePressTheHandlerTheBuildAndTheFrame()
    {
        var b = new SwitchBudget();
        Assert.Equal(0, b.Switches);
        Assert.Null(b.Last);
        Assert.Null(b.Worst);
        Assert.Equal(-1, b.AverageMs);
        const long t0 = 1_000_000L;
        b.Begin("Machine", t0);
        Assert.True(b.IsOpen);
        b.Built("Machine", 30);
        b.Built("Pattern", 500);                                       // another page built meanwhile (the warm-up): not this switch's
        Assert.Equal(30, b.OpenBuildMs);
        b.Handler(42);
        b.Framed(t0 + Ticks(60));
        Assert.False(b.IsOpen);
        var s = b.Last!.Value;
        Assert.Equal("Machine", s.Page);
        Assert.Equal(42, s.HandlerMs);
        Assert.Equal(30, s.BuildMs);
        Assert.Equal(12, s.HandlerNetMs);
        Assert.Equal(18, s.FrameMs, 3);
        Assert.Equal(60, s.TotalMs, 3);
        Assert.Equal("Machine 60 ms (30 ms building the page, 12 ms handler, 18 ms to the frame)", s.Words);
        Assert.Equal(1, b.Switches);
        Assert.Equal(1, b.SlowSwitches);
        Assert.Equal(s, b.Worst!.Value);
        Assert.Equal(s, b.WorstEver!.Value);
        b.Framed(t0 + Ticks(90));                                      // a frame with no switch open is nobody's
        Assert.Equal(1, b.Switches);
    }

    [Fact]
    public void ASwitchWithNoFrameSeenIsClosedByTheNextAndSaysSo()
    {
        var b = new SwitchBudget();
        b.Begin("Cues", 0);
        b.Handler(3);
        b.Begin("Looks", Ticks(500));                                  // the frame never came (a hidden window): closed as it stands
        Assert.Equal(1, b.Switches);
        var first = b.Last!.Value;
        Assert.Equal("Cues", first.Page);
        Assert.Equal(-1, first.FrameMs);
        Assert.Equal(3, first.TotalMs);
        Assert.Equal("Cues 3 ms (3 ms handler, no frame seen)", first.Words);
        b.Handler(2);
        b.Framed(Ticks(500) + Ticks(8));
        Assert.Equal(2, b.Switches);
        Assert.Equal(0, b.SlowSwitches);
        Assert.Equal("Looks", b.Last!.Value.Page);
        Assert.Equal(8, b.Last!.Value.TotalMs, 3);
        Assert.Equal((3 + 8) / 2.0, b.AverageMs, 3);
    }

    [Fact]
    public void TheWindowIsSixtySwitchesAndTheSessionRemembersTheWorst()
    {
        var b = new SwitchBudget();
        b.Begin("Media", 0);
        b.Handler(150);
        b.Framed(Ticks(170));                                          // the worst ever
        for (var i = 0; i < SwitchBudget.Window; i++)
        {
            var t = Ticks(1000 + i * 100);
            b.Begin("Panel", t);
            b.Handler(1);
            b.Framed(t + Ticks(5));
        }
        Assert.Equal(SwitchBudget.Window, b.InWindow);
        Assert.Equal(5, b.Worst!.Value.TotalMs, 3);                    // the 170 ms one has slid out of the window
        Assert.Equal(170, b.WorstEver!.Value.TotalMs, 3);              // and the session still knows it
        Assert.Equal(1, b.SlowSwitches);
        Assert.Equal("Page switch 5.0 ms · worst Panel 5 ms (1 ms handler, 4 ms to the frame) in the last sixty · 1 past 16 ms this session", b.Describe());
        b.Reset();
        Assert.Equal(0, b.Switches);
        Assert.Null(b.WorstEver);
        Assert.Equal("Page switch: none yet.", b.Describe());
    }
}
