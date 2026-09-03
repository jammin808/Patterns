using System.Text.RegularExpressions;
using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>Content targets: joined canvases as first-class holders of content, and the fixes around them.</summary>
public class ContentTargetTests
{
    private static readonly string Key = CanvasNameConfig.KeyFor(new[] { "a", "b" });

    private static ShowState Rig()
    {
        var state = new ShowState();
        state.Output.Placements.Add(new ScreenPlacement { ScreenId = "a", X = 0, Y = 0, Enabled = true });
        state.Output.Placements.Add(new ScreenPlacement { ScreenId = "b", X = 1920, Y = 0, Enabled = true });
        state.Output.Placements.Add(new ScreenPlacement { ScreenId = "c", X = 6000, Y = 0, Enabled = true });
        return state;
    }

    [Fact]
    public void ACanvasKeyIsATargetAndAScreenIdIsNot()
    {
        Assert.True(ContentTargets.IsCanvasKey(Key));
        Assert.False(ContentTargets.IsCanvasKey("a"));
        Assert.Equal(new[] { "a", "b" }, ContentTargets.Members(Key));
    }

    [Fact]
    public void OwnPatternOnACanvasCreatesItsRowOnDemandAndResolvesThroughTheSnapshot()
    {
        var state = Rig();
        Assert.False(ContentTargets.UsesOwnPattern(state, Key));
        Assert.Empty(state.Output.CanvasNames);

        var assignment = ContentTargets.EnsureAssignment(state, Key);
        ContentTargets.SetOwnPattern(state, Key, true);
        assignment.Pattern.Kind = PatternKind.ColorBars;
        state.Pattern.Kind = PatternKind.Grid;

        Assert.Single(state.Output.CanvasNames);
        Assert.True(ContentTargets.UsesOwnPattern(state, Key));
        var snap = new ShowSnapshot { State = state, Version = 1 };
        Assert.Equal(PatternKind.ColorBars, snap.PatternFor(Key).Kind);
        Assert.Equal(PatternKind.Grid, snap.PatternFor("a").Kind);   // a member alone never resolves the canvas
        Assert.Equal(PatternKind.Grid, snap.PatternFor("c").Kind);
        Assert.Equal(PatternKind.Grid, snap.PatternFor(null).Kind);

        ContentTargets.SetOwnPattern(state, Key, false);
        Assert.Equal(PatternKind.Grid, snap.PatternFor(Key).Kind);   // the row stays, the flag decides
        Assert.Same(assignment, ContentTargets.EnsureAssignment(state, Key));
    }

    [Fact]
    public void ActiveCustomTargetsIncludeACanvasOnlyWhileEveryMemberIsOn()
    {
        var state = Rig();
        ContentTargets.EnsureAssignment(state, Key);
        ContentTargets.SetOwnPattern(state, Key, true);
        state.Output.Placements.First(p => p.ScreenId == "c").UseCustomPattern = true;

        Assert.Equal(new[] { "c", Key }, ContentTargets.ActiveCustomTargets(state).ToArray());

        state.Output.Placements.First(p => p.ScreenId == "b").Enabled = false;
        Assert.Equal(new[] { "c" }, ContentTargets.ActiveCustomTargets(state).ToArray());

        state.Output.Placements.First(p => p.ScreenId == "c").Enabled = false;
        Assert.Empty(ContentTargets.ActiveCustomTargets(state));
    }

    [Fact]
    public void MediaOnAJoinedCanvasIsFoundForDecoding()
    {
        var state = Rig();
        state.Pattern.Kind = PatternKind.Grid;
        var assignment = ContentTargets.EnsureAssignment(state, Key);
        ContentTargets.SetOwnPattern(state, Key, true);
        assignment.Pattern.Kind = PatternKind.Media;
        assignment.Pattern.Media.Source = MediaSource.Video;
        assignment.Pattern.Media.VideoPath = "walk-in.mp4";

        var media = MediaLocator.FindActiveMedia(state, MediaSource.Video);
        Assert.NotNull(media);
        Assert.Equal("walk-in.mp4", media!.VideoPath);

        state.Output.Placements.First(p => p.ScreenId == "a").Enabled = false; // half a canvas is no canvas
        Assert.Null(MediaLocator.FindActiveMedia(state, MediaSource.Video));
    }

    [Fact]
    public void LooksCarryWhichCanvasesShowTheirOwnPattern()
    {
        var state = Rig();
        var assignment = ContentTargets.EnsureAssignment(state, Key);
        ContentTargets.SetOwnPattern(state, Key, true);
        assignment.Pattern.Kind = PatternKind.Focus;
        var json = LookService.Capture(state);

        var other = Rig();
        Assert.True(LookService.Apply(json, other));
        Assert.True(ContentTargets.UsesOwnPattern(other, Key));
        Assert.Equal(PatternKind.Focus, new ShowSnapshot { State = other, Version = 1 }.PatternFor(Key).Kind);

        // Applying a look without the canvas takes the flag away again.
        var plain = LookService.Capture(Rig());
        Assert.True(LookService.Apply(plain, other));
        Assert.False(ContentTargets.UsesOwnPattern(other, Key));
    }

    [Fact]
    public void ACutShownWhileFadesWereOffIsNotReplayedAsACutWhenFadesComeBack()
    {
        var engine = new PatternEngine();
        using var sink = new SinkState();
        var info = new SKImageInfo(80, 60, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);

        ShowState Flat(string color, bool fades)
        {
            var s = new ShowState();
            s.Transition.Enabled = fades;
            s.Transition.DurationMs = 1000;
            s.Pattern.Kind = PatternKind.FlatField;
            s.Pattern.FlatField.Color = color;
            s.Pattern.FlatField.ShowLabel = false;
            s.Pattern.Canvas.FollowOutput = true;
            return s;
        }

        void Render(ShowSnapshot snap, double time)
        {
            var ctx = new RenderContext
            {
                ViewportSize = new SKSizeI(80, 60), ReferenceSize = new SKSizeI(80, 60),
                Time = time, Now = DateTime.Now, UtcNow = DateTime.UtcNow,
                Sink = SinkKind.Output, SinkIndex = 1, SinkLabel = "t",
            };
            engine.Render(surface.Canvas, snap, in ctx, sink);
        }

        // Fades off: a red frame, then a CUT to blue (the cut is a property of that snapshot).
        Render(new ShowSnapshot { State = Flat("#FF0000", fades: false), Version = 1 }, 1.0);
        Render(new ShowSnapshot { State = Flat("#0000FF", fades: false), Version = 2, CutAtVersion = 2 }, 1.1);
        Assert.Equal(2, sink.TransitionSeenVersion);

        // Fades on again, content changes to green: a fade from blue, not a replay of the old cut.
        Render(new ShowSnapshot { State = Flat("#00FF00", fades: true), Version = 3, CutAtVersion = 2 }, 1.2);
        Assert.NotNull(sink.TransitionFrom);
        Assert.True(sink.TransitionEndClock > 1.2);
    }

    [Fact]
    public void AnOlderShowFileIsReportedAsMigratedAndItsLooksGetIdsOnce()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-migrate-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var store = new SettingsStore(dir);

        var state = new ShowState { SchemaVersion = 3 };
        state.LooksAndCues.Looks.Add(new LookConfig { Name = "Walk-in", Json = LookService.Capture(state) });
        state.Stingers.Items.Add(new StingerItemConfig { Name = "Sting", Path = "sting.wav" });
        // A file from before schema 4 has no ids at all.
        var json = Regex.Replace(JsonUtil.Serialize(state), @"(,)?\s*""Id"": ""[0-9a-fA-F]{32}""(,)?",
            m => m.Groups[1].Success && m.Groups[2].Success ? "," : "");
        Assert.DoesNotContain("\"Id\"", json);
        File.WriteAllText(store.SettingsPath, json);

        var loaded = store.Load();
        Assert.True(store.LastLoadMigrated);
        Assert.Equal(ShowState.CurrentSchemaVersion, loaded.SchemaVersion);
        var lookId = loaded.LooksAndCues.Looks[0].Id;
        var stingerId = loaded.Stingers.Items[0].Id;
        Assert.Equal(32, lookId.Length);
        Assert.Equal(32, stingerId.Length);

        // Written back, the same ids come up next time — cues and the journal can rely on them.
        store.Save(loaded);
        var again = store.Load();
        Assert.False(store.LastLoadMigrated);
        Assert.Equal(lookId, again.LooksAndCues.Looks[0].Id);
        Assert.Equal(stingerId, again.Stingers.Items[0].Id);

        // An id written as an empty string (a hand-edited file) is minted too.
        var blank = JsonUtil.Serialize(again).Replace($"\"Id\": \"{lookId}\"", "\"Id\": \"\"");
        File.WriteAllText(store.SettingsPath, blank);
        var minted = store.Load();
        Assert.Equal(32, minted.LooksAndCues.Looks[0].Id.Length);
    }
}
