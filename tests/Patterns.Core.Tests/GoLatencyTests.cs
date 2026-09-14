using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>The GO on the clock: the press, the publish, the slowest sink's first frame; the preview when no output drew; a GO nobody showed; the words and the window.</summary>
public class GoLatencyTests
{
    [Fact]
    public void AGoIsThePressThePublishAndTheSlowestSinksFirstFrame()
    {
        var g = new GoLatency();
        Assert.Equal("GO to first drawn frame: no GO yet.", g.Describe());
        Assert.Equal("", g.GlanceWords);
        g.Pressed("07", 10, 11, 100.000, 6);
        Assert.True(g.IsOpen);
        Assert.Equal(11, g.PendingVersion);
        // The preview showed it first and one output after; nothing closes until every output has.
        g.Resolve(new[] { (SinkKind.Preview, 0, (double?)100.010), (SinkKind.Output, 1, (double?)100.028), (SinkKind.Output, 2, (double?)null) }, 100.5);
        Assert.True(g.IsOpen);
        g.Resolve(new[] { (SinkKind.Preview, 0, (double?)100.010), (SinkKind.Output, 1, (double?)100.028), (SinkKind.Output, 2, (double?)100.034) }, 100.6);
        Assert.False(g.IsOpen);
        Assert.Equal(-1, g.PendingVersion);
        var go = g.Last!.Value;
        Assert.Equal("07", go.Cue);
        Assert.Equal(6, go.PublishMs);
        Assert.Equal(28, go.FrameMs, 3);
        Assert.Equal(34, go.TotalMs, 3);
        Assert.Equal("OUT 2", go.Sink);
        Assert.Equal("GO 07 34 ms (6 ms to the publish, 28 ms to OUT 2's frame)", go.Words);
        Assert.Equal("GO→frame 34 ms", g.GlanceWords);
        Assert.Equal(1, g.Gos);
        Assert.Equal(0, g.SlowGos);
        Assert.Equal("GO to first drawn frame 34.0 ms · worst GO 07 34 ms (6 ms to the publish, 28 ms to OUT 2's frame) in the last sixty · 0 past 50 ms this session", g.Describe());
        g.Resolve(Array.Empty<(SinkKind, int, double?)>(), 101);            // nothing open: nothing happens
        Assert.Equal(1, g.Gos);
    }

    [Fact]
    public void WithNoOutputThePreviewIsTheFrameAndAGoThatDrewNothingSaysSo()
    {
        var g = new GoLatency();
        g.Pressed("02", 4, 5, 50.0, 3);
        g.Resolve(new[] { (SinkKind.Preview, 0, (double?)50.020) }, 50.1);
        var go = g.Last!.Value;
        Assert.Equal("PVW", go.Sink);
        Assert.Equal(17, go.FrameMs, 3);
        Assert.Equal("GO 02 20 ms (3 ms to the publish, 17 ms to PVW's frame)", go.Words);
        // A cue whose steps changed nothing on the screens: no publish, nothing to draw, closed at once.
        g.Pressed("03", 5, 5, 60.0, 2);
        Assert.False(g.IsOpen);
        Assert.Equal("GO 03 2 ms (2 ms to the publish; nothing to draw)", g.Last!.Value.Words);
        Assert.Equal("", g.GlanceWords);
        Assert.Equal(2, g.Gos);
    }

    [Fact]
    public void AGoNobodyShowedIsClosedAfterTwoSecondsOrByTheNextPressAndTheWindowIsSixty()
    {
        var g = new GoLatency();
        g.Pressed("01", 0, 1, 10.0, 4);
        g.Resolve(new[] { (SinkKind.Output, 1, (double?)null) }, 11.0);
        Assert.True(g.IsOpen);
        g.Resolve(new[] { (SinkKind.Output, 1, (double?)null) }, 12.5);
        Assert.False(g.IsOpen);
        Assert.Equal("GO 01 4 ms (4 ms to the publish; no frame seen in 2 s)", g.Last!.Value.Words);
        // Two outputs, one of them never showed it: the one that did names the frame, and the note says so.
        g.Pressed("02", 1, 2, 20.0, 5);
        g.Resolve(new[] { (SinkKind.Output, 1, (double?)20.030), (SinkKind.Output, 2, (double?)null) }, 22.5);
        Assert.Equal("GO 02 30 ms (5 ms to the publish, 25 ms to OUT 1's frame; 1 of 2 sinks never showed it)", g.Last!.Value.Words);
        // The next press closes one still open.
        g.Pressed("03", 2, 3, 30.0, 5);
        g.Pressed("04", 3, 4, 31.0, 5);
        Assert.Equal(3, g.Gos);                                                    // 03 closed by 04's press; 04 still open
        Assert.Contains("the next GO came first", g.Last!.Value.Words);
        // A slow one counts for the session and is the worst until it slides out of the window.
        g.Pressed("05", 4, 5, 40.0, 60);
        g.Resolve(new[] { (SinkKind.Output, 1, (double?)40.120) }, 40.2);
        Assert.Equal(1, g.SlowGos);
        Assert.Equal(120, g.WorstEver!.Value.TotalMs, 3);
        for (var i = 0; i < GoLatency.Window; i++)
        {
            g.Pressed("06", 10 + i, 11 + i, 100.0 + i, 2);
            g.Resolve(new[] { (SinkKind.Output, 1, (double?)(100.0 + i + 0.010)) }, 100.5 + i);
        }
        Assert.Equal(GoLatency.Window, g.InWindow);
        Assert.Equal(10, g.Worst!.Value.TotalMs, 3);
        Assert.Equal(120, g.WorstEver!.Value.TotalMs, 3);
        g.Reset();
        Assert.Equal(0, g.Gos);
        Assert.Null(g.WorstEver);
        Assert.Equal("GO to first drawn frame: no GO yet.", g.Describe());
    }
}
