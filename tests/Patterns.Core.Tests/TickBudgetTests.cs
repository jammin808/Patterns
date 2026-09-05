using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The desk's tick budget: a minute of ticks kept with the slowest area of each, the worst and
/// the average over it, the slow ones counted for the session, the faults carried past, and the
/// line the STABILITY block reads.
/// </summary>
public class TickBudgetTests
{
    [Fact]
    public void KeepsTheLastTickTheWorstAndTheAverageWithTheAreaThatTookTheTime()
    {
        var b = new TickBudget();
        Assert.Equal(0, b.Ticks);
        Assert.Equal(-1, b.LastMs);
        Assert.Equal(-1, b.WorstMs);
        Assert.Equal(-1, b.AverageMs);
        Assert.Equal("", b.WorstArea);

        b.Record(2, "media", 0.8);
        b.Record(9, "tallies", 6.5);
        b.Record(4, "run", 1.2);
        Assert.Equal(3, b.Ticks);
        Assert.Equal(3, b.InWindow);
        Assert.Equal(4, b.LastMs);
        Assert.Equal(9, b.WorstMs);
        Assert.Equal("tallies", b.WorstArea);
        Assert.Equal(5, b.AverageMs);
        Assert.Equal(0, b.SlowTicks);
        Assert.Equal(9, b.WorstEverMs);
        Assert.Equal("tallies", b.WorstEverArea);
    }

    [Fact]
    public void TheWindowIsAMinuteAndTheSessionRemembersTheRest()
    {
        var b = new TickBudget();
        for (var i = 0; i < 10; i++) b.Record(40, "remote", 38);   // a blocked resolver, ten seconds of it
        for (var i = 0; i < 70; i++) b.Record(2, "clock", 0.3);
        Assert.Equal(80, b.Ticks);
        Assert.Equal(TickBudget.Window, b.InWindow);
        Assert.Equal(2, b.WorstMs);                                  // the slow ticks fell out of the minute
        Assert.Equal("clock", b.WorstArea);
        Assert.Equal(2, b.AverageMs, 3);
        Assert.Equal(10, b.SlowTicks);                               // but the session still counts them
        Assert.Equal(40, b.WorstEverMs);
        Assert.Equal("remote", b.WorstEverArea);
    }

    [Fact]
    public void ANegativeOrUnknownAreaIsTakenQuietly()
    {
        var b = new TickBudget();
        b.Record(-3);
        Assert.Equal(0, b.LastMs);
        Assert.Equal("", b.WorstArea);
        b.Record(20, "media");                                       // an area with no time is no area
        Assert.Equal("", b.WorstArea);
        Assert.Equal(1, b.SlowTicks);
    }

    [Fact]
    public void TheLineReadsTheAverageTheWorstItsAreaTheSlowCountAndTheFaults()
    {
        var b = new TickBudget();
        Assert.Equal("Desk tick: not measured yet.", b.Describe());
        b.Record(1.0, "media", 0.4);
        b.Record(1.4, "tallies", 0.9);
        Assert.Equal("Desk tick 1.2 ms · worst 1.4 ms (tallies) in the last minute · 0 past 16 ms this session", b.Describe());
        b.Record(22, "install", 20);
        b.RecordFault();
        Assert.Equal("Desk tick 8.1 ms · worst 22.0 ms (install) in the last minute · 1 past 16 ms this session · 1 area failed and the tick carried on — see the log", b.Describe());
        b.RecordFault();
        Assert.Contains("2 areas failed", b.Describe());
        Assert.Equal(2, b.Faults);
    }

    [Fact]
    public void ResetStartsTheSessionOver()
    {
        var b = new TickBudget();
        b.Record(30, "devices", 29);
        b.RecordFault();
        b.Reset();
        Assert.Equal(0, b.Ticks);
        Assert.Equal(0, b.SlowTicks);
        Assert.Equal(0, b.Faults);
        Assert.Equal(-1, b.WorstMs);
        Assert.Equal(-1, b.WorstEverMs);
        Assert.Equal("", b.WorstEverArea);
        Assert.Equal("Desk tick: not measured yet.", b.Describe());
    }
}
