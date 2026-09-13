using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The live desk's table: armed and live at once refuses the work that is not the show, in words
/// that say what lifts it; armed alone or live alone refuses nothing; content is never refused.
/// </summary>
public class LivePolicyTests
{
    [Fact]
    public void OnlyArmedAndLiveTogetherRefuseAnything()
    {
        foreach (var kind in LivePolicy.Refused)
        {
            Assert.Null(LivePolicy.Refusal(kind, armed: false, outputsLive: false));
            Assert.Null(LivePolicy.Refusal(kind, armed: true, outputsLive: false));    // rehearsing on a dark rig: calibrate away
            Assert.Null(LivePolicy.Refusal(kind, armed: false, outputsLive: true));    // the pictures up, nothing armed: the operator's afternoon
            var why = LivePolicy.Refusal(kind, armed: true, outputsLive: true);
            Assert.NotNull(why);
            Assert.StartsWith("Not while the stack is armed and the outputs are live:", why);
            Assert.EndsWith("DISARM first (or OUTPUTS OFF), then again.", why);
        }
        Assert.False(LivePolicy.IsLive(true, false));
        Assert.True(LivePolicy.IsLive(true, true));
    }

    [Fact]
    public void TheTableNamesTheWorkThatIsNotTheShowAndNothingElse()
    {
        Assert.Equal(new[]
        {
            ShowActionKind.CalibrateRun, ShowActionKind.CalibrateDemo, ShowActionKind.CalibrateApply, ShowActionKind.CalibrateUndo,
            ShowActionKind.UpdateApply, ShowActionKind.Restart,
        }, LivePolicy.Refused);
        Assert.Contains("structured-light patterns", LivePolicy.Refusal(ShowActionKind.CalibrateRun, true, true));
        Assert.Contains("takes the desk down", LivePolicy.Refusal(ShowActionKind.UpdateApply, true, true));
        Assert.Contains("takes the desk down", LivePolicy.Refusal(ShowActionKind.Restart, true, true));
        // Content and the show's own verbs flow whatever the state: the game on a tile, the join wall, a look, a cue, a message, the wall switch.
        foreach (var kind in new[]
        {
            ShowActionKind.CueGo, ShowActionKind.CueFire, ShowActionKind.ApplyLook, ShowActionKind.MessageOn, ShowActionKind.PatternKind,
            ShowActionKind.ArcadeStart, ShowActionKind.ArcadeWindow, ShowActionKind.DeviceSend, ShowActionKind.TwinTakeOver, ShowActionKind.TwinTakeBack,
            ShowActionKind.OutputsOn, ShowActionKind.OutputsOff, ShowActionKind.BlackoutOn, ShowActionKind.VideoRestart, ShowActionKind.CalibrateCancel,
        })
        {
            Assert.Null(LivePolicy.Refusal(kind, true, true));
        }
    }
}
