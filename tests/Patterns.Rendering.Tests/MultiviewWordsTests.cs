using Patterns.Core.Model;
using Patterns.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>A wall's badges and captions are worked out once per snapshot, not once per tile per frame.</summary>
[Collection("InputBus")]
public class MultiviewWordsTests
{
    private static void Render(ShowSnapshot snap, MultiviewOptions opts, SinkState sink, int w = 320, int h = 180)
    {
        var engine = new PatternEngine();
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var ctx = new RenderContext
        {
            ViewportSize = new SKSizeI(w, h),
            ReferenceSize = new SKSizeI(w, h),
            Time = 5.0,
            Now = new DateTime(2026, 9, 12, 12, 0, 0),
            UtcNow = RenderTestHarness.FixedUtcNow,
            Sink = SinkKind.Output,
            SinkIndex = 0,
            SinkLabel = "mv",
        };
        var frame = new PatternFrame
        {
            Snapshot = snap,
            Config = snap.State.Pattern,
            Ctx = ctx,
            Sink = sink,
            Canvas = new SKSizeI(w, h),
            Palette = Palette.Resolve(snap),
        };
        engine.RenderMultiview(surface.Canvas, in frame, sink, opts);
        surface.Canvas.Flush();
    }

    [Fact]
    public void TheWallsWordsAreWorkedOutOncePerSnapshotAndOncePerWall()
    {
        var state = RenderTestHarness.State(s => s.Pattern.Kind = PatternKind.FlatField);
        var opts = new MultiviewOptions { ShowLabels = true, ShowTally = true };   // no tiles of its own: the default wall
        using var sink = new SinkState();

        var first = RenderTestHarness.Snap(state, version: 1);
        Render(first, opts, sink);
        Render(first, opts, sink);
        Render(first, opts, sink);
        Assert.Equal(1, sink.MultiviewWords.Builds);                                 // three frames, one set of words
        var words = sink.MultiviewWords.For(first, null, opts);
        Assert.Same(words, sink.MultiviewWords.For(first, null, opts));
        Assert.NotEmpty(words);
        Assert.Equal(MultiviewTally.Name(first, words[0].Tile), words[0].Name);        // the tally's own words
        Assert.Equal(MultiviewTally.Kind(first, words[0].Tile), words[0].Kind);
        Assert.Equal(MultiviewTally.Badges(first, words[0].Tile), words[0].Badges);

        var second = RenderTestHarness.Snap(state, version: 2);
        Render(second, opts, sink);
        Assert.Equal(2, sink.MultiviewWords.Builds);                                 // the snapshot moved: worked out again

        var another = new MultiviewOptions { ShowLabels = true };
        another.Tiles.Add(new MultiviewTileConfig { Source = MultiviewSource.Clock });
        Render(second, another, sink);
        Assert.Equal(3, sink.MultiviewWords.Builds);                                 // another wall: its own words
        Assert.Single(sink.MultiviewWords.For(second, null, another));
        Render(second, another, sink);
        Assert.Equal(3, sink.MultiviewWords.Builds);
    }
}
