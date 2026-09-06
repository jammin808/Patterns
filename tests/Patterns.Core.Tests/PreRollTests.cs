using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>The pre-roll's pure half: which clips the standby cue's look would put up, the look it resolves to, and the strip's words.</summary>
public class PreRollTests
{
    private static ShowState ClipState()
        => RenderTestHarness.State(s =>
        {
            s.Pattern.Kind = PatternKind.Media;
            s.Pattern.Media.Source = MediaSource.Video;
            s.Pattern.Media.VideoPath = "/shows/vt.mp4";
            s.Pattern.Media.Loop = false;
            s.Pattern.Media.Mute = false;
            s.Pattern.Media.VolumePct = 80;
            s.Pattern.Layer1.Enabled = true;
            s.Pattern.Layer1.Source = LayerSource.Video;
            s.Pattern.Layer1.VideoPath = "/shows/bug.mov";
            s.Pattern.Layer1.Loop = true;
            s.Pattern.Layer1.Mute = true;
            s.Pattern.Layer2.Enabled = true;
            s.Pattern.Layer2.Source = LayerSource.Video;
            s.Pattern.Layer2.VideoPath = "/shows/vt.mp4";        // the pattern's clip again: one mount, the pattern's settings
        });

    [Fact]
    public void TheStandbyLooksClipsAreWantedAsThePoolWillWantThem()
    {
        var state = ClipState();
        var wants = PreRoll.WantedFor(LookService.Capture(state));
        Assert.Equal(2, wants.Count);
        Assert.Equal(InputKeys.Video("/shows/vt.mp4"), wants[0].Key);
        Assert.Equal(MediaLocator.WantedKind.VideoFile, wants[0].Kind);
        Assert.Equal("/shows/vt.mp4", wants[0].Target);
        Assert.False(wants[0].Loop);
        Assert.False(wants[0].Mute);
        Assert.Equal(80, wants[0].VolumePct);
        Assert.Equal(InputKeys.Video("/shows/bug.mov"), wants[1].Key);
        Assert.True(wants[1].Loop);
        Assert.True(wants[1].Mute);

        // The same keys, loop and format the pool will want once the look is on air — so the mount carries over.
        var live = MediaLocator.FindWantedInputs(RenderTestHarness.Snap(state));
        Assert.Equal(live.Select(w => (w.Key, w.Loop, w.Format)), wants.Select(w => (w.Key, w.Loop, w.Format)));
    }

    [Fact]
    public void OnlyFileClipsPreRollAndABlackoutOrJunkWantsNothing()
    {
        Assert.Empty(PreRoll.WantedFor((string?)null));
        Assert.Empty(PreRoll.WantedFor(""));
        Assert.Empty(PreRoll.WantedFor("{ not a look"));
        Assert.Empty(PreRoll.WantedFor(LookService.Capture(RenderTestHarness.State(s => s.Pattern.Kind = PatternKind.Grid))));

        var blackout = ClipState();
        blackout.Blackout = true;
        Assert.Empty(PreRoll.WantedFor(LookService.Capture(blackout)));

        var capture = RenderTestHarness.State(s =>
        {
            s.Pattern.Kind = PatternKind.Media;
            s.Pattern.Media.Source = MediaSource.Capture;
            s.Pattern.Media.CaptureDevice = "Decklink";
            s.Pattern.Layer1.Enabled = true;
            s.Pattern.Layer1.Source = LayerSource.NdiFeed;
            s.Pattern.Layer1.NdiSourceName = "CAM 1";
        });
        Assert.Empty(PreRoll.WantedFor(LookService.Capture(capture)));

        var disabledLayer = ClipState();
        disabledLayer.Pattern.Kind = PatternKind.Grid;
        disabledLayer.Pattern.Layer1.Enabled = false;
        Assert.Equal(new[] { InputKeys.Video("/shows/vt.mp4") }, PreRoll.WantedFor(LookService.Capture(disabledLayer)).Select(w => w.Key));   // layer 2 still carries it
    }

    [Fact]
    public void TheStandbyCuesFirstApplyLookNamesTheLookByIdOrName()
    {
        var state = ClipState();
        var look = new LookConfig { Name = "VT in", Json = LookService.Capture(state) };
        state.LooksAndCues.Looks.Add(look);

        Assert.Null(PreRoll.LookOf(state, null));
        var plain = new RunCueConfig { Number = "01.010", Name = "Plain" };
        plain.Actions.Add(new CueActionConfig { Kind = CueActionKind.Note, Target = "hello" });
        Assert.Null(PreRoll.LookOf(state, plain));
        Assert.Empty(PreRoll.WantedFor(state, plain));

        var byName = new RunCueConfig { Number = "01.020", Name = "By name" };
        byName.Actions.Add(new CueActionConfig { Kind = CueActionKind.Note, Target = "first a note" });
        byName.Actions.Add(new CueActionConfig { Kind = CueActionKind.ApplyLook, Target = "vt IN" });
        Assert.Same(look, PreRoll.LookOf(state, byName));
        Assert.Equal(2, PreRoll.WantedFor(state, byName).Count);

        var byId = new RunCueConfig { Number = "01.030", Name = "By id" };
        byId.Actions.Add(new CueActionConfig { Kind = CueActionKind.ApplyLook, Target = look.Id });
        Assert.Same(look, PreRoll.LookOf(state, byId));

        var unknown = new RunCueConfig { Number = "01.040", Name = "Unknown" };
        unknown.Actions.Add(new CueActionConfig { Kind = CueActionKind.ApplyLook, Target = "no such look" });
        Assert.Null(PreRoll.LookOf(state, unknown));
        Assert.Empty(PreRoll.WantedFor(state, unknown));
    }

    [Fact]
    public void TheStripsWordsReadTheWorstClipFirst()
    {
        Assert.Equal(("", ""), PreRoll.Words(Array.Empty<PreRoll.State>()));
        Assert.Equal(("PRE-ROLLED", ""), PreRoll.Words(new[] { PreRoll.State.Ready }));
        Assert.Equal(("CLIP ON AIR", ""), PreRoll.Words(new[] { PreRoll.State.OnAir }));
        Assert.Equal(("PRE-ROLLED", ""), PreRoll.Words(new[] { PreRoll.State.Ready, PreRoll.State.OnAir }));
        Assert.Equal(("", "PRE-ROLLING…"), PreRoll.Words(new[] { PreRoll.State.Ready, PreRoll.State.Opening }));
        Assert.Equal(("", "CLIP NOT OPEN"), PreRoll.Words(new[] { PreRoll.State.Missing }));
        Assert.Equal(("", "CLIP NOT OPEN"), PreRoll.Words(new[] { PreRoll.State.Ready, PreRoll.State.Missing, PreRoll.State.Opening }));
        Assert.Equal(("", "CLIPS NOT OPEN"), PreRoll.Words(new[] { PreRoll.State.Missing, PreRoll.State.Missing }));
    }
}
