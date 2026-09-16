using Patterns.Core.Media;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The smoothing buffer for a page's frames (round 68): a 30 fps video whose frames arrive early,
/// late and in bursts comes out on a regular schedule; nothing shows before its time or twice;
/// a slow page is shown at once under Auto; the depth follows the jitter within the machine's
/// bounds and the pool's room; a stall counts once; a cut leaves nothing waiting.
/// </summary>
public class FrameSmootherTests
{
    private const double Tick = 1.0 / 60;                                                                 // a 60 Hz sink
    private const double Cadence = 1.0 / 30;

    // A 30 fps source landing 8 ms after the sink's even ticks, with a repeating stray pattern: two of
    // every eight frames land late enough to fall into the next sink tick when shown as they arrive.
    private static readonly double[] Strays = { 0, 0.011, -0.004, 0.012, -0.006, 0.003, -0.007, 0.005 };

    private static double Arrival(int n) => 10 + n * Cadence + 0.008 + Strays[n % Strays.Length];

    private static FrameSmoother Standard(WebSmoothing mode = WebSmoothing.Auto) => new(FrameSmoother.Bounds.For(MachineClass.Standard), mode);

    /// <summary>Runs a 60 Hz sink from one clock to another, offering each frame as it arrives; the (tick, id) pairs shown.</summary>
    private static List<(int Tick, long Id)> Play(FrameSmoother s, Func<int, double> arrival, int frames, double from, double to)
    {
        var shown = new List<(int, long)>();
        var next = 0;
        var ticks = (int)Math.Round((to - from) / Tick);
        for (var k = 0; k <= ticks; k++)
        {
            var t = from + k * Tick;
            while (next < frames && arrival(next) <= t)
            {
                s.Offer(next, arrival(next));
                next++;
            }
            var id = s.Pick(t);
            if (id >= 0) shown.Add((k, id));
        }
        return shown;
    }

    private static int Irregular(List<(int Tick, long Id)> shown, int skip)
    {
        var n = 0;
        for (var i = skip + 1; i < shown.Count; i++)
        {
            if (shown[i].Tick - shown[i - 1].Tick != 2) n++;
        }
        return n;
    }

    [Fact]
    public void TheBoundsFollowTheMachineClass()
    {
        Assert.Equal(new FrameSmoother.Bounds(2, 3, 5), FrameSmoother.Bounds.For(MachineClass.Small));
        Assert.Equal(new FrameSmoother.Bounds(2, 2, 4), FrameSmoother.Bounds.For(MachineClass.Standard));
        Assert.Equal(new FrameSmoother.Bounds(1, 2, 3), FrameSmoother.Bounds.For(MachineClass.Big));
        Assert.Equal(4, FrameSmoother.Bounds.For(MachineClass.Standard).Clamp(9));
        Assert.Equal(2, FrameSmoother.Bounds.For(MachineClass.Standard).Clamp(0));
    }

    [Fact]
    public void AutoShowsAtOnceUntilTheCadenceIsMeasuredThenSmoothsAVideo()
    {
        var s = Standard();
        Assert.False(s.Smoothing);
        Assert.False(s.Measured);
        Assert.Equal("auto · measuring", s.Words);
        for (var n = 0; n <= FrameSmoother.MeasuredIntervals; n++)
        {
            s.Offer(n, Arrival(n));
            s.Pick(Arrival(n));
        }
        Assert.True(s.Measured);
        Assert.True(s.Smoothing);
        Assert.Equal(30, s.Fps, 6);                                                                       // locked to the known rate
        Assert.InRange(s.MeasuredFps, 27, 33);
        Assert.StartsWith("auto → smooth 2 (67 ms)", s.Words);
    }

    [Fact]
    public void ASlowPageIsShownAtOnceUnderAutoAndAFastOneOnlyLocksToARateItIsNear()
    {
        var s = Standard();
        for (var n = 0; n < 12; n++)
        {
            s.Offer(n, 10 + n / 10.0);
        }
        Assert.True(s.Measured);
        Assert.False(s.Smoothing);
        Assert.Equal(10, s.Fps, 6);
        Assert.Equal("auto → low latency", s.Words);
        Assert.Equal(0, s.LatencySeconds);
        Assert.Equal(11, s.Pick(11.1));                                                                   // the newest, at once
        Assert.Equal(11, s.Dropped);                                                                      // four fell off the full ring, seven were due together and never showed

        // 27 fps is near no known rate: the measured mean stands.
        var odd = Standard();
        for (var n = 0; n < 40; n++) odd.Offer(n, 10 + n / 27.0);
        Assert.InRange(odd.Fps, 26.5, 27.5);
        Assert.Equal(1 / 30.0, FrameSmoother.Snap(1 / 29.9), 9);
        Assert.Equal(1 / 60.0, FrameSmoother.Snap(1 / 61.0), 9);
        Assert.Equal(1 / 27.0, FrameSmoother.Snap(1 / 27.0), 9);
    }

    [Fact]
    public void TheDueTimesAreRegularWhenTheArrivalsAreNot()
    {
        // Shown as they arrive, the late frames land a sink tick late: the cadence on the glass is 1, 3, 2, 2, 3, 1…
        // (The run ends a tick after the last frame's time: a sink that keeps asking after the source stops would count a stall, rightly.)
        var raw = Play(Standard(WebSmoothing.LowLatency), Arrival, 60, 10, 12.1);
        Assert.True(Irregular(raw, 8) >= 8, $"the unsmoothed cadence should be ragged; irregular intervals: {Irregular(raw, 8)}");

        // Smoothed, every frame shows two ticks after the one before, the whole way — the same frames, a
        // regular schedule, two frames of delay.
        var s = Standard(WebSmoothing.Smooth);
        var smooth = Play(s, Arrival, 60, 10, 12.1);
        Assert.Equal(raw.Count, smooth.Count);
        Assert.True(Irregular(smooth, 8) == 0, "irregular smoothed schedule; shown at ticks " + string.Join(",", smooth.Select(x => $"{x.Tick}:{x.Id}")));
        Assert.Equal(2, s.Depth);
        Assert.Equal(0, s.Underruns);
        Assert.Equal(0, s.Dropped);
        // Never a frame before it arrived, never before its time, never twice, in order.
        for (var i = 0; i < smooth.Count; i++)
        {
            var (tick, id) = smooth[i];
            Assert.True(10 + tick * Tick >= Arrival((int)id));
            Assert.True(10 + tick * Tick >= Arrival((int)id) + s.LatencySeconds - 0.02);                // within a stray of the ideal
            if (i > 0) Assert.Equal(smooth[i - 1].Id + 1, id);
        }
    }

    [Fact]
    public void APickNeverShowsAFrameBeforeItsTimeAndDropsOlderDueOnes()
    {
        var s = Standard(WebSmoothing.Smooth);
        Assert.Equal(2 * Cadence, s.LatencySeconds, 9);
        Assert.Equal(-1, s.Offer(1, 10));
        Assert.Equal(1, s.Held);
        Assert.Equal(-1, s.Pick(10.05));                                                                  // due at 10.067
        Assert.Equal(1, s.Pick(10.07));
        Assert.Equal(1, s.ShownId);
        Assert.Equal(0, s.Held);
        s.Offer(2, 10 + Cadence);
        s.Offer(3, 10 + 2 * Cadence);
        var dropped = new List<long>();
        Assert.Equal(3, s.Pick(10.2, dropped));                                                           // both due: the newest shows
        Assert.Equal(new long[] { 2 }, dropped);
        Assert.Equal(1, s.Dropped);
        Assert.Equal(2, s.Presented);
        Assert.Equal(-1, s.Pick(10.3));                                                                   // nothing waits: the frame on show stands
    }

    [Fact]
    public void LowLatencyShowsTheNewestAtOnce()
    {
        var s = Standard(WebSmoothing.LowLatency);
        s.Offer(1, 10);
        s.Offer(2, 10.001);
        var dropped = new List<long>();
        Assert.Equal(2, s.Pick(10.001, dropped));
        Assert.Equal(new long[] { 1 }, dropped);
        Assert.Equal(0, s.LatencySeconds);
        Assert.Equal("low latency", s.Words);
    }

    [Fact]
    public void AFullRingDropsTheOldestAndSaysWhich()
    {
        var s = Standard(WebSmoothing.Smooth);
        for (var n = 0; n < FrameSmoother.Capacity; n++) Assert.Equal(-1, s.Offer(n, 10 + n * Cadence));
        Assert.Equal(FrameSmoother.Capacity, s.Held);
        Assert.Equal(0, s.Offer(8, 10 + 8 * Cadence));
        Assert.Equal(FrameSmoother.Capacity, s.Held);
        Assert.Equal(1, s.Dropped);
        Assert.Equal(1, s.WaitingIds().First());
        Assert.Equal(8, s.WaitingIds().Last());
    }

    [Fact]
    public void DrainingRefusesCutClearsAndResumeTakesAgain()
    {
        var s = Standard(WebSmoothing.Smooth);
        s.Offer(0, 10);
        s.Offer(1, 10 + Cadence);
        s.Offer(2, 10 + 2 * Cadence);
        s.Drain();
        Assert.True(s.Draining);
        Assert.Equal(9, s.Offer(9, 10.2));                                                                // refused: the caller frees it
        Assert.Equal(1, s.RefusedDraining);
        Assert.Equal(3, s.Held);
        Assert.Equal(2, s.Pick(11));                                                                      // what was held still shows through the fade
        Assert.Equal(2, s.Dropped);
        s.Resume();
        Assert.Equal(-1, s.Offer(3, 11.1));
        Assert.Equal(1, s.Held);
        s.Offer(4, 11.1 + Cadence);
        Assert.Equal(new long[] { 3, 4 }, s.Cut());
        Assert.Equal(0, s.Held);
        Assert.True(s.Draining);
        Assert.Equal(2, s.ShownId);                                                                       // the frame on show is the caller's to fade
        s.Refuse();
        Assert.Equal(2, s.RefusedDraining);
        s.Resume();
        Assert.False(s.Draining);
    }

    [Fact]
    public void ClearKeepsDrainingAsItWasAndUnlocksTheClock()
    {
        var s = Standard(WebSmoothing.Smooth);
        s.Offer(0, 10);
        s.Offer(1, 10 + Cadence);
        s.Drain();
        Assert.Equal(new long[] { 0, 1 }, s.Clear());
        Assert.True(s.Draining);
        s.Resume();
        s.Offer(5, 20);                                                                                   // the clock starts again here, no gap counted
        Assert.Equal(20, s.WaitingFrames().Single().Ideal);
    }

    [Fact]
    public void RemoveTakesOneWaitingFrameOutAndCountsIt()
    {
        var s = Standard(WebSmoothing.Smooth);
        s.Offer(0, 10);
        s.Offer(1, 10 + Cadence);
        s.Offer(2, 10 + 2 * Cadence);
        Assert.True(s.Remove(1));
        Assert.Equal(new long[] { 0, 2 }, s.WaitingIds().ToArray());
        Assert.Equal(1, s.Dropped);
        Assert.False(s.Remove(7));
        Assert.Equal(1, s.Dropped);
    }

    [Fact]
    public void AStallCountsOnceHoweverLongItLasts()
    {
        var s = Standard(WebSmoothing.Smooth);
        static double Regular(int n) => 10 + n * Cadence + 0.008;
        var shown = Play(s, Regular, 24, 10, 10.9);                                                       // the source stops after frame 23 (at ~10.77)
        Assert.Equal(24, shown.Count);
        Assert.Equal(0, s.Underruns);
        for (var k = 0; k < 60; k++) s.Pick(10.9 + k * Tick);                                             // a second of nothing
        Assert.Equal(1, s.Underruns);
        // A frame after the gap re-locks the clock and shows after the latency; the stall is over.
        s.Offer(24, 11.9);
        Assert.Equal(-1, s.Pick(11.93));
        Assert.Equal(24, s.Pick(11.9 + 2 * Cadence + 0.001));
        Assert.Equal(1, s.Underruns);
        for (var k = 0; k < 30; k++) s.Pick(12 + k * Tick);
        Assert.Equal(2, s.Underruns);
    }

    [Fact]
    public void TheDepthFollowsTheJitterWithinTheBoundsAndThePoolsRoom()
    {
        // Every twelfth frame lands 60 ms late and the two after it catch up in a burst (+30, +5 ms):
        // against the clock's schedule that frame is late by well over one cadence, so the buffer asks
        // for three — enough that the late frame is still on time.
        static double Bursty(int n) => 10 + n * Cadence + (n % 12) switch { 9 => 0.060, 10 => 0.030, 11 => 0.005, _ => 0 };
        var s = Standard(WebSmoothing.Smooth);
        Assert.Equal(2, s.Depth);
        for (var n = 0; n < 48; n++)
        {
            s.Offer(n, Bursty(n));
            s.Pick(Bursty(n) + 0.5);
        }
        Assert.Equal(3, s.Depth);
        Assert.InRange(s.JitterSeconds, 0.040, 0.070);
        Assert.Equal(30, s.Fps, 6);                                                                       // the bursts do not move the rate: locked to 30
        Assert.Equal(3 * s.Cadence, s.LatencySeconds, 9);

        // The pool has room for one buffered frame: the depth never passes it.
        s.Cap = 1;
        Assert.Equal(1, s.Depth);
        s.Offer(48, Bursty(48));
        Assert.Equal(1, s.Depth);
        s.Cap = 8;
        for (var n = 49; n < 60; n++) s.Offer(n, Bursty(n));
        Assert.Equal(3, s.Depth);

        // A small machine starts deeper and settles to its floor once the source has been steady for a whole window.
        var small = new FrameSmoother(FrameSmoother.Bounds.For(MachineClass.Small), WebSmoothing.Smooth);
        Assert.Equal(3, small.Depth);
        for (var n = 0; n < 24; n++) small.Offer(n, 10 + n * Cadence);
        Assert.Equal(3, small.Depth);                                                                     // not yet: a quiet window is 32 intervals
        for (var n = 24; n < 60; n++) small.Offer(n, 10 + n * Cadence);
        Assert.Equal(2, small.Depth);
        Assert.Equal(2, new FrameSmoother(FrameSmoother.Bounds.For(MachineClass.Small)) { Cap = 2 }.Depth);
    }

    [Fact]
    public void TheWordsSayTheModeTheDepthAndTheDelay()
    {
        Assert.Equal("smooth 2 (67 ms)", Standard(WebSmoothing.Smooth).Words);
        Assert.Equal("low latency", Standard(WebSmoothing.LowLatency).Words);
        var big = new FrameSmoother(FrameSmoother.Bounds.For(MachineClass.Big), WebSmoothing.Smooth);
        Assert.Equal("smooth 2 (67 ms)", big.Words);
        big.Cap = 1;
        Assert.Equal("smooth 1 (33 ms)", big.Words);
    }
}
