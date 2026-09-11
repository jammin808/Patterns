using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The two things a restart turns on: the record of what the audience was seeing, and the rule
/// that gets a sink to draw the frame after the one that armed a crossfade.
/// </summary>
public class RestartRecordTests
{
    private static ShowSnapshot Snap(ShowState state, long version = 1, long cutAt = 0, bool isTake = true) => new()
    {
        State = JsonUtil.Clone(state),
        Version = version,
        CutAtVersion = cutAt,
        IsTake = isTake,
    };

    // ---- the record ---------------------------------------------------------------------

    [Fact]
    public void TheRecordCarriesTheProgramItself()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-rec-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var air = new ShowState();
            air.Pattern.Kind = PatternKind.LedWall;
            air.Brand.PrimaryColor = "#123456";
            air.Overlays.Clock.Enabled = true;

            var store = new RecoveryStore(dir);
            store.Write(new RecoverySnapshot(true, false, DateTime.UtcNow, Air: air, Sandboxed: true,
                BlackTargets: new[] { "screen-2" }, Streaming: true));

            var read = store.Read();
            Assert.NotNull(read);
            Assert.True(read!.Live);
            Assert.True(read.Sandboxed);
            Assert.True(read.Streaming);
            Assert.Equal(new[] { "screen-2" }, read.BlackTargets);

            // A look would have carried the pattern and the overlays and left the brand kit
            // behind — which is how a restart used to repaint a client's wall in someone else's
            // colours. The record is the program itself, so everything comes back.
            Assert.NotNull(read.Air);
            Assert.Equal(PatternKind.LedWall, read.Air!.Pattern.Kind);
            Assert.Equal("#123456", read.Air.Brand.PrimaryColor);
            Assert.True(read.Air.Overlays.Clock.Enabled);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void ARecordFromAnOlderBuildStillReads()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-rec-old-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            // Exactly what a build before the state vehicle wrote: a look, and none of the new
            // fields. Upgrading over a live sidecar must still put the show back.
            var onAir = new ShowState();
            onAir.Pattern.Kind = PatternKind.ColorBars;
            File.WriteAllText(Path.Combine(dir, "patterns.recovery.json"), JsonUtil.Serialize(new
            {
                Live = true,
                AudioPlaying = false,
                UpdatedUtc = DateTime.UtcNow,
                AirLook = LookService.Capture(onAir),
            }));

            var read = new RecoveryStore(dir).Read();
            Assert.NotNull(read);
            Assert.True(read!.Live);
            Assert.Null(read.Air);
            Assert.Null(read.Sandboxed);
            Assert.False(read.Streaming);
            Assert.Null(read.BlackTargets);
            Assert.Contains("ColorBars", read.AirLook!);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void AStaleRecordIsNotActedOn()
    {
        var now = new DateTime(2026, 9, 10, 20, 0, 0, DateTimeKind.Utc);
        Assert.True(RecoveryStore.IsFresh(new RecoverySnapshot(true, false, now.AddHours(-11)), now));
        Assert.False(RecoveryStore.IsFresh(new RecoverySnapshot(true, false, now.AddHours(-13)), now));
    }

    // ---- the frame after the one that arms a fade -----------------------------------------

    [Fact]
    public void ASinkIsAskedForFramesBeforeTheFrameThatStartsAFadeNotAfterIt()
    {
        var before = new ShowState();
        before.Pattern.Kind = PatternKind.Grid;
        var after = JsonUtil.Clone(before);
        after.Pattern.Kind = PatternKind.ColorBars;

        var sink = new SinkState();
        var first = Snap(before);
        var second = Snap(after, version: 2);

        // Nothing drawn yet: there is no outgoing picture, so there is no fade to come.
        Assert.Equal(SinkState.NoKey, sink.TransitionKey);
        Assert.False(PatternEngine.WillStartFade(first, null, sink, SinkKind.Monitor));

        // The sink has drawn the grid. The next frame will start the crossfade to the bars —
        // and that frame draws the grid at full opacity over it, so the sink must already be
        // asking for more. Before this rule it asked afterwards and never got another frame:
        // the miniature sat on the grid until some unrelated edit published again.
        sink.TransitionKey = first.TransitionKeyFor(null);
        sink.TransitionSeenVersion = first.Version;
        Assert.True(PatternEngine.WillStartFade(second, null, sink, SinkKind.Monitor));

        // The same content again is not a fade.
        Assert.False(PatternEngine.WillStartFade(Snap(before, version: 3), null, sink, SinkKind.Monitor));
    }

    [Fact]
    public void NothingThatDoesNotFadeAsksForTheFrames()
    {
        var before = new ShowState();
        before.Pattern.Kind = PatternKind.Grid;
        var after = JsonUtil.Clone(before);
        after.Pattern.Kind = PatternKind.ColorBars;

        var sink = new SinkState();
        sink.TransitionKey = Snap(before).TransitionKeyFor(null);
        sink.TransitionSeenVersion = 1;

        // A CUT switches instead of fading.
        Assert.False(PatternEngine.WillStartFade(Snap(after, version: 2, cutAt: 2), null, sink, SinkKind.Monitor));

        // Transitions off: the new picture is simply drawn, on the frame already scheduled.
        var noFade = JsonUtil.Clone(after);
        noFade.Transition.Enabled = false;
        Assert.False(PatternEngine.WillStartFade(Snap(noFade, version: 2), null, sink, SinkKind.Monitor));

        // A thumbnail never crossfades.
        Assert.False(PatternEngine.WillStartFade(Snap(after, version: 2), null, sink, SinkKind.Thumbnail));
    }
}
