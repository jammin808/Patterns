using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 25: what the desk's own speakers play.
///
/// The fault: a clip on the programme, another on a confidence screen's own picture and a third
/// loaded into the preview are three decoders, and every one of them opens an audio output. Played
/// together they are a mix nobody chose, and the more carefully a show is built the worse it gets.
/// The rule these pin is that one of them is heard at a time, that it is the programme unless the
/// operator says otherwise, and that none of it ever reaches the room.
/// </summary>
public class AudioMonitorTests
{
    private static ShowState Show()
    {
        var state = new ShowState();
        state.Output.Placements.Add(new ScreenPlacement { ScreenId = "a", Planned = true, PlannedWidth = 1920, PlannedHeight = 1080, Enabled = true });
        state.Output.Placements.Add(new ScreenPlacement { ScreenId = "b", Planned = true, PlannedWidth = 1920, PlannedHeight = 1080, X = 1920, Enabled = true });
        state.Pattern.Kind = PatternKind.Media;
        state.Pattern.Media.Source = MediaSource.Video;
        state.Pattern.Media.VideoPath = "/clips/programme.mp4";
        state.Pattern.Media.Mute = false;
        return state;
    }

    private static void OwnClip(ShowState state, string target, string path)
    {
        ContentTargets.SetOwnPattern(state, target, true);
        var cfg = ContentTargets.EnsureAssignment(state, target).Pattern;
        cfg.Kind = PatternKind.Media;
        cfg.Media.Source = MediaSource.Video;
        cfg.Media.VideoPath = path;
        cfg.Media.Mute = false;
    }

    private static ShowSnapshot Snap(ShowState state) => new() { State = state, Version = 1 };

    private static MediaLocator.WantedInput Of(IEnumerable<MediaLocator.WantedInput> list, string path)
        => list.Single(w => w.Target == path);

    [Fact]
    public void EveryPictureThatWantsAClipIsRecordedAgainstIt()
    {
        var state = Show();
        OwnClip(state, "b", "/clips/confidence.mp4");
        var wanted = MediaLocator.FindWantedInputs(Snap(state));

        Assert.Equal(new[] { MediaBus.Program }, Of(wanted, "/clips/programme.mp4").Buses);
        Assert.Equal(new[] { MediaBus.Output("b") }, Of(wanted, "/clips/confidence.mp4").Buses);

        // The same clip on the programme and on a screen is one decoder on two buses — the
        // programme's settings stand, and both buses are remembered.
        var same = Show();
        OwnClip(same, "b", "/clips/programme.mp4");
        var one = Assert.Single(MediaLocator.FindWantedInputs(Snap(same)), w => w.Kind == MediaLocator.WantedKind.VideoFile);
        Assert.Equal(new[] { MediaBus.Program, MediaBus.Output("b") }, one.Buses);
    }

    [Fact]
    public void TheProgrammeIsWhatTheDeskHearsUnlessItIsToldOtherwise()
    {
        var state = Show();
        OwnClip(state, "b", "/clips/confidence.mp4");
        Assert.Equal(AudioMonitor.Program, state.Monitor.Source);

        var wanted = AudioMonitorRule.Apply(state, MediaLocator.FindWantedInputs(Snap(state)));
        Assert.False(Of(wanted, "/clips/programme.mp4").Mute);
        Assert.True(Of(wanted, "/clips/confidence.mp4").Mute, "the confidence screen's clip is not in the mix");

        // Listening to that screen instead: the two swap over, and nothing plays twice.
        state.Monitor.Source = AudioMonitor.Output;
        state.Monitor.OutputId = "b";
        wanted = AudioMonitorRule.Apply(state, MediaLocator.FindWantedInputs(Snap(state)));
        Assert.True(Of(wanted, "/clips/programme.mp4").Mute);
        Assert.False(Of(wanted, "/clips/confidence.mp4").Mute);

        // Silence: nothing at the desk at all.
        state.Monitor.Source = AudioMonitor.Silent;
        wanted = AudioMonitorRule.Apply(state, MediaLocator.FindWantedInputs(Snap(state)));
        Assert.All(wanted, w => Assert.True(w.Mute));
    }

    [Fact]
    public void AScreenThatSimplyFollowsTheShowSoundsLikeTheShow()
    {
        // Picking a screen that has no picture of its own must not be silence — that reads as a
        // fault. It is showing the programme, so that is what it sounds like.
        var state = Show();
        state.Monitor.Source = AudioMonitor.Output;
        state.Monitor.OutputId = "a";
        Assert.Equal(AudioMonitor.Program, AudioMonitorRule.Effective(state).Source);
        var wanted = AudioMonitorRule.Apply(state, MediaLocator.FindWantedInputs(Snap(state)));
        Assert.False(Of(wanted, "/clips/programme.mp4").Mute);

        // An output named but never picked, likewise.
        state.Monitor.OutputId = "";
        Assert.Equal(AudioMonitor.Program, AudioMonitorRule.Effective(state).Source);

        // And one that has gone from the rig.
        state.Monitor.OutputId = "a-screen-that-left";
        Assert.Equal(AudioMonitor.Program, AudioMonitorRule.Effective(state).Source);
    }

    [Fact]
    public void TheOperatorsOwnMuteStillWinsAndTheRoomIsNeverTouched()
    {
        // The monitor only ever takes sound away at the desk: a clip the operator muted stays
        // muted whatever they are listening to.
        var state = Show();
        state.Pattern.Media.Mute = true;
        var wanted = AudioMonitorRule.Apply(state, MediaLocator.FindWantedInputs(Snap(state)));
        Assert.True(Of(wanted, "/clips/programme.mp4").Mute);

        // And nothing about the monitor is part of what a picture looks like, so choosing one
        // never crossfades a screen or changes a snapshot's identity.
        var a = Show();
        var b = Show();
        b.Monitor.Source = AudioMonitor.Preview;
        b.Monitor.OutputId = "b";
        Assert.Equal(JsonUtil.SerializeIdentity(a.Pattern), JsonUtil.SerializeIdentity(b.Pattern));
    }

    [Fact]
    public void TheRuleReadsAMountOnSeveralBusesAsAllOfThem()
    {
        var program = new[] { MediaBus.Program };
        var screen = new[] { MediaBus.Output("b") };
        var both = new[] { MediaBus.Program, MediaBus.Sandbox };

        var pgm = new AudioMonitorRule.MonitorPick(AudioMonitor.Program, "");
        var pvw = new AudioMonitorRule.MonitorPick(AudioMonitor.Preview, "");
        var outB = new AudioMonitorRule.MonitorPick(AudioMonitor.Output, "b");
        var quiet = new AudioMonitorRule.MonitorPick(AudioMonitor.Silent, "");

        Assert.True(AudioMonitorRule.Hears(pgm, program));
        Assert.False(AudioMonitorRule.Hears(pgm, screen));
        Assert.True(AudioMonitorRule.Hears(outB, screen));
        Assert.False(AudioMonitorRule.Hears(outB, program));
        Assert.False(AudioMonitorRule.Hears(pvw, program));

        // A clip that is on the programme and in the preview is heard either way round — the
        // preview holds the same file, so silencing it would be silencing what is being checked.
        Assert.True(AudioMonitorRule.Hears(pgm, both));
        Assert.True(AudioMonitorRule.Hears(pvw, both));
        Assert.False(AudioMonitorRule.Hears(outB, both));

        // Silence means silence, and a mount with no bus at all falls back to the programme.
        Assert.False(AudioMonitorRule.Hears(quiet, program));
        Assert.False(AudioMonitorRule.Hears(quiet, both));
        Assert.True(AudioMonitorRule.Hears(pgm, Array.Empty<MediaBus>()));
        Assert.False(AudioMonitorRule.Hears(pvw, null));
    }

    [Fact]
    public void TheLineSaysWhatIsBeingHeard()
    {
        var state = Show();
        state.Output.Placements[1].CustomLabel = "Stage left";
        Assert.Contains("the programme", AudioMonitorRule.Words(state));

        state.Monitor.Source = AudioMonitor.Preview;
        Assert.Contains("preview", AudioMonitorRule.Words(state));

        state.Monitor.Source = AudioMonitor.Output;
        Assert.Contains("pick which one", AudioMonitorRule.Words(state));
        state.Monitor.OutputId = "b";
        Assert.Contains("Stage left", AudioMonitorRule.Words(state));

        state.Monitor.Source = AudioMonitor.Silent;
        Assert.Contains("room still hears", AudioMonitorRule.Words(state));

        Assert.Equal("Stage left", AudioMonitorRule.LabelFor(state, "b"));
        Assert.Equal("a", AudioMonitorRule.LabelFor(state, "a"));
        Assert.Equal("the programme", AudioMonitorRule.LabelFor(state, ""));
    }
}
