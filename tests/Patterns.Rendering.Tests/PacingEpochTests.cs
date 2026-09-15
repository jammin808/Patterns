using Patterns.Rendering;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 64: a missed presentation slot means one thing — a frame that should have been presented
/// during the current continuous run at one rate, and was not. A run that ends (static content,
/// a canvas off the tree) and starts again, a target that changes, a sink that becomes paced or
/// unpaced: each begins a new epoch whose first vsync presents and misses nothing.
/// </summary>
public class PacingEpochTests
{
    private static int Run(FramePacerState state, double from, double seconds, int vsyncHz, int target, out int presented)
    {
        var missed = 0;
        presented = 0;
        var beats = (int)Math.Round(seconds * vsyncHz);
        for (var i = 0; i < beats; i++)
        {
            if (FramePacer.ShouldPresent(from + i / (double)vsyncHz, target, state, out var m)) presented++;
            missed += m;
        }
        return missed;
    }

    [Fact]
    public void AStaticMinuteBetweenTwoAnimatedRunsInventsNoMissedSlots()
    {
        var state = new FramePacerState();
        Assert.Equal(0, Run(state, 1000, 1, 60, 50, out var shown));                                     // a second at 50 on a 60 Hz clock
        Assert.InRange(shown, 49, 51);
        state.Leave();                                                                                    // static: the cadence left Continuous
        Assert.False(state.Active);
        Assert.True(FramePacer.ShouldPresent(1061, 50, state, out var missed));                          // a minute on: animating again
        Assert.Equal(0, missed);                                                                          // the old pacer counted the three thousand slots that went by
        Assert.Equal(2, state.Epochs);
        Assert.Equal(0, Run(state, 1061 + 1 / 60.0, 1, 60, 50, out _));                                  // and the run counts cleanly from there
    }

    [Theory]
    [InlineData(60, 50)]
    [InlineData(50, 30)]
    [InlineData(30, 60)]
    public void ATargetChangeStartsANewEpochWithoutSyntheticMisses(int first, int second)
    {
        var state = new FramePacerState();
        Assert.Equal(0, Run(state, 500, 2, 120, first, out _));
        Assert.True(FramePacer.ShouldPresent(502 + 1 / 120.0, second, state, out var missed));           // the target changed between two vsyncs
        Assert.Equal(0, missed);
        Assert.Equal(second, state.TargetFps);
        Assert.Equal(0, Run(state, 502 + 2 / 120.0, 2, 120, second, out _));
    }

    [Fact]
    public void PacingOnAndOffAndOnAgainCountsNothingAcrossTheGap()
    {
        var state = new FramePacerState();
        Assert.Equal(0, Run(state, 10, 1, 60, 50, out _));
        Assert.True(FramePacer.ShouldPresent(11, 0, state, out var m0));                                 // unpaced now: every beat, no epoch
        Assert.Equal(0, m0);
        Assert.False(state.Active);
        Assert.Equal(0, Run(state, 11 + 1 / 60.0, 5, 60, 0, out var every));
        Assert.Equal(300, every);                                                                         // every vsync while unpaced
        Assert.True(FramePacer.ShouldPresent(16 + 1 / 60.0, 50, state, out var m1));                      // paced again
        Assert.Equal(0, m1);
        Assert.Equal(0, Run(state, 16 + 2 / 60.0, 1, 60, 50, out _));
    }

    [Fact]
    public void ADetachedCanvasComesBackToAFreshEpochAndARealDropIsStillCounted()
    {
        var state = new FramePacerState();
        Assert.Equal(0, Run(state, 0, 1, 60, 60, out _));
        state.Leave();                                                                                    // off the tree
        Assert.True(FramePacer.ShouldPresent(30, 60, state, out var back));                               // back half a minute later
        Assert.Equal(0, back);

        // Inside a run a real gap is a real drop: three slots skipped are three slots missed.
        FramePacer.ShouldPresent(30 + 1 / 60.0, 60, state, out _);
        Assert.True(FramePacer.ShouldPresent(30 + 5 / 60.0, 60, state, out var dropped));
        Assert.Equal(3, dropped);
    }
}
