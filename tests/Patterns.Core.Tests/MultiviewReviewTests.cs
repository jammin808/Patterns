using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>The preview on the monitor wall: a Preview tile beside the program, the review filling the multiview, the slate without a sandbox, the verbs.</summary>
public class MultiviewReviewTests
{
    private static ShowState Flat(string color) => RenderTestHarness.State(s =>
    {
        s.Pattern.Kind = PatternKind.FlatField;
        s.Pattern.FlatField.Color = color;
        s.Pattern.FlatField.ShowLabel = false;
        s.Pattern.Canvas.FollowOutput = true;
    });

    private static SKBitmap Render(ShowSnapshot snap, MultiviewOptions opts, int w = 320, int h = 180)
    {
        var engine = new PatternEngine();
        using var sink = new SinkState();
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var ctx = new RenderContext
        {
            ViewportSize = new SKSizeI(w, h),
            ReferenceSize = new SKSizeI(w, h),
            Time = 5.0,
            Now = new DateTime(2026, 9, 5, 12, 0, 0),
            UtcNow = new DateTime(2026, 9, 5, 10, 0, 0, DateTimeKind.Utc),
            Sink = SinkKind.Output,
            SinkIndex = 0,
            SinkLabel = "mv-review",
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
        using var image = surface.Snapshot();
        return SKBitmap.FromImage(image);
    }

    private static bool Red(SKColor c) => c.Red > 200 && c.Green < 60 && c.Blue < 60;
    private static bool Blue(SKColor c) => c.Blue > 200 && c.Red < 60 && c.Green < 60;

    [Fact]
    public void APreviewTileShowsTheSandboxAndTheReviewFillsTheWall()
    {
        var bus = new SnapshotBus(Flat("#0000FF"));
        bus.Publish(Flat("#0000FF"));           // the program: blue
        bus.PublishSandbox(Flat("#FF0000"));    // the desk is building red
        Assert.NotNull(bus.Current.PreviewSource);
        Assert.Same(bus.Sandbox, bus.Current.PreviewSource!());

        var opts = new MultiviewOptions { ShowLabels = false, ShowTally = false };
        opts.Tiles.Add(new MultiviewTileConfig { Source = MultiviewSource.Program });
        opts.Tiles.Add(new MultiviewTileConfig { Source = MultiviewSource.Preview });
        using (var bmp = Render(bus.Current, opts))
        {
            Assert.True(Blue(bmp.GetPixel(80, 90)), $"the program tile is blue, got {bmp.GetPixel(80, 90)}");
            Assert.True(Red(bmp.GetPixel(240, 90)), $"the preview tile is red, got {bmp.GetPixel(240, 90)}");
        }

        // The preview follows the desk without a program publish: the accessor reads the bus, not a copy.
        bus.PublishSandbox(Flat("#00FF00"));
        using (var bmp = Render(bus.Current, opts))
        {
            var c = bmp.GetPixel(240, 90);
            Assert.True(c.Green > 200 && c.Red < 60, $"the preview tile follows the sandbox, got {c}");
            Assert.True(Blue(bmp.GetPixel(80, 90)));
        }

        // The review: the preview fills the whole multiview, the program tile gone, a chip in the corner.
        bus.PublishSandbox(Flat("#FF0000"));
        bus.ReviewOnMultiview = true;
        bus.Publish(Flat("#0000FF"));
        Assert.True(bus.Current.ReviewOnMultiview);
        using (var bmp = Render(bus.Current, opts))
        {
            Assert.True(Red(bmp.GetPixel(160, 90)), $"the review is the preview full-frame, got {bmp.GetPixel(160, 90)}");
            Assert.True(Red(bmp.GetPixel(80, 90)), "no program tile during a review");
            Assert.False(Red(bmp.GetPixel(12, 12)), "the REVIEW chip sits in the corner");
        }

        // No sandbox: the tile and the review read a slate, never the program in disguise.
        bus.ClearSandbox();
        using (var bmp = Render(bus.Current, opts))
        {
            var c = bmp.GetPixel(160, 90);
            Assert.False(Red(c) || Blue(c), $"a slate without a sandbox, got {c}");
        }
        bus.ReviewOnMultiview = false;
        bus.Publish(Flat("#0000FF"));
        using (var bmp = Render(bus.Current, opts))
        {
            Assert.True(Blue(bmp.GetPixel(80, 90)));
            var c = bmp.GetPixel(240, 90);
            Assert.False(Red(c) || Blue(c), $"the preview tile is a slate without a sandbox, got {c}");
        }
    }

    [Fact]
    public void TheVerbsAndTheFeedbackKnowTheReview()
    {
        Assert.Equal(new RemoteCommand(RemoteCommandKind.ReviewOn, 0, ""), ControlProtocol.Parse("REVIEW ON"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.ReviewOff, 0, ""), ControlProtocol.Parse("review off"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.ReviewToggle, 0, ""), ControlProtocol.Parse("REVIEW"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.ReviewToggle, 0, ""), ControlProtocol.Parse("REVIEW TOGGLE"));
        Assert.Equal("REVIEW ON", OscMap.ToLine(OscMessage.Of("/patterns/review", 1)));
        Assert.Equal("REVIEW OFF", OscMap.ToLine(OscMessage.Of("/patterns/review/off")));
        Assert.Equal("REVIEW TOGGLE", OscMap.ToLine(OscMessage.Of("/patterns/review")));
        Assert.Contains(OscMap.Reference, r => r.Address.StartsWith("/patterns/review"));
        var fed = OscFeedback.FromState("{\"review\":true}");
        var m = Assert.Single(fed, x => x.Address == "/patterns/state/review");
        Assert.Equal(1, m.Args[0]);
    }
}
