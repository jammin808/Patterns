using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The two layers over a pattern: a picture in its box with a border, corners and an opacity;
/// an empty layer that the desk sees and an output does not; the boxes a frame records and the
/// pane maths that finds them; a drag that never fades; inputs, cadence and a rename.
/// </summary>
[Collection("InputBus")]
public class LayerTests
{
    private static string RedPng()
    {
        var path = Path.Combine(Path.GetTempPath(), "patterns-layer-" + Guid.NewGuid().ToString("N") + ".png");
        using var bmp = new SKBitmap(64, 64);
        bmp.Erase(SKColors.Red);
        using var img = SKImage.FromBitmap(bmp);
        using var data = img.Encode(SKEncodedImageFormat.Png, 100);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    /// <summary>A blue field with layer 1 on in the middle half of the canvas.</summary>
    private static ShowState Blue(Action<LayerConfig>? layer = null) => RenderTestHarness.State(s =>
    {
        s.Pattern.Kind = PatternKind.FlatField;
        s.Pattern.FlatField.Color = "#0000FF";
        s.Pattern.Layer1.Enabled = true;
        s.Pattern.Layer1.XPct = 25;
        s.Pattern.Layer1.YPct = 25;
        s.Pattern.Layer1.WPct = 50;
        s.Pattern.Layer1.HPct = 50;
        layer?.Invoke(s.Pattern.Layer1);
    });

    private static SKBitmap RenderWithSink(ShowState state, int width, int height, SinkKind kind, SinkState sink, string? screenId = null)
    {
        var engine = new PatternEngine();
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var ctx = new RenderContext
        {
            ViewportSize = new SKSizeI(width, height),
            ReferenceSize = new SKSizeI(width, height),
            Time = 1,
            Now = new DateTime(2026, 8, 29, 12, 0, 0),
            UtcNow = RenderTestHarness.FixedUtcNow,
            Sink = kind,
            SinkIndex = 1,
            SinkLabel = "test",
            ScreenId = screenId,
        };
        engine.Render(surface.Canvas, RenderTestHarness.Snap(state), in ctx, sink);
        surface.Canvas.Flush();
        var bmp = new SKBitmap(info);
        surface.ReadPixels(info, bmp.GetPixels(), info.RowBytes, 0, 0);
        return bmp;
    }

    [Fact]
    public void ALayerDrawsItsPictureInItsBoxWithABorderCornersAndAnOpacity()
    {
        var png = RedPng();
        try
        {
            var s = Blue(l =>
            {
                l.Source = LayerSource.Image;
                l.ImagePath = png;
                l.Fit = FitMode.Stretch;
            });
            Assert.Equal(new SKRect(100, 50, 300, 150), LayerRenderer.RectOf(s.Pattern.Layer1, new SKSizeI(400, 200)));
            using var plain = RenderTestHarness.Render(s, 400, 200);
            Assert.Equal(SKColors.Red, plain.GetPixel(200, 100));  // inside the box
            Assert.Equal(SKColors.Blue, plain.GetPixel(20, 20));   // the pattern outside it
            Assert.Equal(SKColors.Blue, plain.GetPixel(350, 100));

            // A border inside the box in its colour; a corner rounded away lets the pattern through.
            s.Pattern.Layer1.BorderPx = 10;
            s.Pattern.Layer1.BorderColor = "#00FF00";
            s.Pattern.Layer1.CornerPx = 40;
            using var bordered = RenderTestHarness.Render(s, 400, 200);
            Assert.Equal(SKColors.Lime, bordered.GetPixel(105, 100));
            Assert.Equal(SKColors.Red, bordered.GetPixel(200, 100));
            Assert.Equal(SKColors.Blue, bordered.GetPixel(101, 51));

            // Half opacity blends the picture with the pattern beneath.
            s.Pattern.Layer1.BorderPx = 0;
            s.Pattern.Layer1.CornerPx = 0;
            s.Pattern.Layer1.Opacity = 0.5;
            using var half = RenderTestHarness.Render(s, 400, 200);
            var px = half.GetPixel(200, 100);
            Assert.InRange(px.Red, 110, 145);
            Assert.InRange(px.Blue, 110, 145);

            // Off, the layer is not there; a crop shows only part of the picture, fitted.
            s.Pattern.Layer1.Enabled = false;
            using var off = RenderTestHarness.Render(s, 400, 200);
            Assert.Equal(SKColors.Blue, off.GetPixel(200, 100));
        }
        finally
        {
            File.Delete(png);
        }
    }

    [Fact]
    public void AnEmptyLayerShowsItsBoxOnTheDeskAndNothingOnAnOutputAndTheFrameRecordsWhatItDrew()
    {
        var s = Blue(l =>
        {
            l.Source = LayerSource.Image;
            l.ImagePath = "";
        });
        s.Overlays.Clock.Enabled = true;

        using var output = RenderTestHarness.Render(s, 400, 200);
        Assert.All(Enumerable.Range(50, 100), y => Assert.Equal(SKColors.Blue, output.GetPixel(100, y))); // no frame on an output

        using var sink = new SinkState();
        using var preview = RenderWithSink(s, 400, 200, SinkKind.Preview, sink);
        Assert.Contains(Enumerable.Range(50, 100), y => preview.GetPixel(100, y) != SKColors.Blue);   // the dashed frame on the desk
        Assert.Equal(HitKind.Layer1, sink.Hits[0].Kind);                                               // recorded, layers before overlays
        Assert.Equal(new SKRect(100, 50, 300, 150), sink.Hits[0].Rect);
        Assert.False(sink.Hits[0].ViewportSpace);
        Assert.Contains(sink.Hits, h => h.Kind == HitKind.Clock);
        Assert.Equal(new SKSizeI(400, 200), sink.LastCanvasSize);
        Assert.Equal(1, sink.LastCanvasScale);

        // A fade source or a tile never records: the next top-level frame starts clean.
        using var fadeSink = new SinkState();
        var engine = new PatternEngine();
        using var surface = SKSurface.Create(new SKImageInfo(400, 200));
        var ctx = new RenderContext
        {
            ViewportSize = new SKSizeI(400, 200), ReferenceSize = new SKSizeI(400, 200), Time = 1,
            Now = DateTime.Now, UtcNow = DateTime.UtcNow, Sink = SinkKind.Preview, SinkLabel = "t", IsFadeSource = true,
        };
        engine.Render(surface.Canvas, RenderTestHarness.Snap(s), in ctx, fadeSink);
        Assert.Empty(fadeSink.Hits);
    }

    [Fact]
    public void ThePaneMapFindsWhatIsUnderThePointer()
    {
        var hits = new List<HitRect>
        {
            new(HitKind.Layer1, new SKRect(100, 50, 300, 150), false),
            new(HitKind.Pip, new SKRect(300, 150, 390, 195), true),
        };
        // A pane twice the target's size with a 10 px bar on the left; the canvas fills the target.
        var map = new PaneMap(new SKSizeI(400, 200), 10, 0, 2, default, 1, new SKSizeI(400, 200));
        Assert.Equal(HitKind.Layer1, HitTester.Find(hits, in map, new SKPoint(410, 200))!.Value.Kind);
        Assert.Null(HitTester.Find(hits, in map, new SKPoint(50, 40)));
        Assert.Equal(HitKind.Pip, HitTester.Find(hits, in map, new SKPoint(710, 350))!.Value.Kind);
        Assert.Equal(new SKPoint(50, 25), map.CanvasDelta(new SKPoint(100, 50)));
        Assert.Equal(new SKPoint(50, 25), map.TargetDelta(new SKPoint(100, 50)));

        // The canvas letterboxed inside the target (a square canvas on a wide screen): the same point lands elsewhere on it.
        var boxed = new PaneMap(new SKSizeI(400, 200), 0, 0, 1, new SKPoint(100, 0), 0.5f, new SKSizeI(400, 400));
        Assert.Equal(new SKPoint(200, 200), boxed.ToCanvas(new SKPoint(200, 100)));
        Assert.Equal(new SKPoint(200, 100), boxed.CanvasDelta(new SKPoint(100, 50)));
        Assert.Equal(new SKPoint(200, 100), boxed.ToTarget(new SKPoint(200, 100)));
    }

    [Fact]
    public void PlacingALayerNeverStartsACrossfadeButANewPictureDoesAndTheBoxStillTravels()
    {
        var a = Blue(l => { l.Source = LayerSource.Image; l.ImagePath = "/x/a.png"; });
        var snapA = RenderTestHarness.Snap(a, 1);
        var b = Blue(l => { l.Source = LayerSource.Image; l.ImagePath = "/x/a.png"; l.XPct = 60; l.YPct = 10; l.WPct = 30; l.HPct = 30; });
        Assert.Equal(snapA.TransitionKeyFor(null), RenderTestHarness.Snap(b, 2).TransitionKeyFor(null));
        var c = Blue(l => { l.Source = LayerSource.Image; l.ImagePath = "/x/b.png"; });
        Assert.NotEqual(snapA.TransitionKeyFor(null), RenderTestHarness.Snap(c, 3).TransitionKeyFor(null));

        // Identity is not persistence: the box is in the show file and in a look.
        var json = JsonUtil.Serialize(b);
        Assert.Equal(60, JsonUtil.Deserialize<ShowState>(json)!.Pattern.Layer1.XPct);
        var fresh = new ShowState();
        Assert.True(LookService.Apply(LookService.Capture(b), fresh));
        Assert.True(fresh.Pattern.Layer1.Enabled);
        Assert.Equal(60, fresh.Pattern.Layer1.XPct);
        Assert.Equal("/x/a.png", fresh.Pattern.Layer1.ImagePath);
        // Copied in place too (a preset, a show load), and a layer from a newer build reads as an image.
        var target = new PatternConfig();
        ModelCopier.Copy(b.Pattern, target);
        Assert.Equal(30, target.Layer1.WPct);
        var newer = JsonUtil.Deserialize<ShowState>(json.Replace("\"Image\"", "\"Hologram\""))!;
        Assert.Equal(LayerSource.Image, newer.Pattern.Layer1.Source);
    }

    [Fact]
    public void LayersWantTheirInputsSetTheCadenceShowAnotherScreenAndFollowARename()
    {
        var s = RenderTestHarness.State();
        s.Pattern.Layer1.Enabled = true;
        s.Pattern.Layer1.Source = LayerSource.NdiFeed;
        s.Pattern.Layer1.NdiSourceName = "CAM 9";
        s.Pattern.Layer2.Enabled = true;
        s.Pattern.Layer2.Source = LayerSource.Video;
        s.Pattern.Layer2.VideoPath = "/shows/loop.mp4";
        s.Pattern.Layer2.Loop = false;
        var wanted = MediaLocator.FindWantedInputs(RenderTestHarness.Snap(s));
        Assert.Equal(new[] { "ndi:CAM 9", "vid:/shows/loop.mp4" }, wanted.Select(w => w.Key));
        Assert.False(wanted[1].Loop);
        Assert.True(wanted[1].Mute);
        Assert.Equal(RedrawCadence.Continuous, PatternEngine.CadenceOf(RenderTestHarness.Snap(s), null, DateTime.UtcNow));
        s.Pattern.Layer1.Enabled = false;
        s.Pattern.Layer2.Source = LayerSource.Image;
        Assert.Equal(RedrawCadence.Static, PatternEngine.CadenceOf(RenderTestHarness.Snap(s), null, DateTime.UtcNow));

        // Screen a shows screen b in a layer: b's picture lands in the box, and a moves when b does.
        s.Output.Placements.Add(new ScreenPlacement { ScreenId = "a" });
        s.Output.Placements.Add(new ScreenPlacement { ScreenId = "b" });
        var bOwn = ContentTargets.EnsureAssignment(s, "b");
        ContentTargets.SetOwnPattern(s, "b", true);
        bOwn.Pattern.Kind = PatternKind.FlatField;
        bOwn.Pattern.FlatField.Color = "#FF0000";
        var aOwn = ContentTargets.EnsureAssignment(s, "a");
        ContentTargets.SetOwnPattern(s, "a", true);
        aOwn.Pattern.Kind = PatternKind.FlatField;
        aOwn.Pattern.FlatField.Color = "#0000FF";
        aOwn.Pattern.Layer1.Enabled = false;
        aOwn.Pattern.Layer2.Enabled = true;
        aOwn.Pattern.Layer2.Source = LayerSource.Screen;
        aOwn.Pattern.Layer2.TargetId = "b";
        aOwn.Pattern.Layer2.XPct = 25; aOwn.Pattern.Layer2.YPct = 25; aOwn.Pattern.Layer2.WPct = 50; aOwn.Pattern.Layer2.HPct = 50;
        using var frame = RenderTestHarness.Render(s, 400, 200, screenId: "a");
        Assert.Equal(SKColors.Red, frame.GetPixel(200, 100));   // b, fitted into the box
        Assert.Equal(SKColors.Blue, frame.GetPixel(20, 20));     // a's own field around it
        Assert.Equal(RedrawCadence.Static, PatternEngine.CadenceOf(RenderTestHarness.Snap(s), "a", DateTime.UtcNow));
        bOwn.Pattern.Kind = PatternKind.Motion;
        Assert.Equal(RedrawCadence.Continuous, PatternEngine.CadenceOf(RenderTestHarness.Snap(s), "a", DateTime.UtcNow));

        // Two screens showing each other settle instead of recursing, on the cadence and on the picture.
        bOwn.Pattern.Kind = PatternKind.FlatField;
        bOwn.Pattern.Layer1.Enabled = true;
        bOwn.Pattern.Layer1.Source = LayerSource.Screen;
        bOwn.Pattern.Layer1.TargetId = "a";
        Assert.Equal(RedrawCadence.Static, PatternEngine.CadenceOf(RenderTestHarness.Snap(s), "a", DateTime.UtcNow));
        using var loop = RenderTestHarness.Render(s, 400, 200, screenId: "a");
        Assert.Equal(SKColors.Red, loop.GetPixel(200, 100));

        // A rename follows the layer's target.
        ContentTargets.RenameScreen(s, "b", "b2");
        Assert.Equal("b2", aOwn.Pattern.Layer2.TargetId);
        Assert.Equal("a", bOwn.Pattern.Layer1.TargetId);
    }

    [Fact]
    public void AnOverlayNudgeMovesItsBoxAndATickerTakesTheVerticalOneOnly()
    {
        var s = RenderTestHarness.State(x =>
        {
            x.Overlays.Clock.Enabled = true;
            x.Overlays.Clock.Anchor = Anchor9.TopLeft;
            x.Overlays.Clock.OffsetXPct = 25;
        });
        using var sink = new SinkState();
        using (RenderWithSink(s, 400, 200, SinkKind.Preview, sink))
        {
            var clock = sink.Hits.Single(h => h.Kind == HitKind.Clock);
            Assert.Equal(10 + 100, clock.Rect.Left, 1);   // the margin, then a quarter of the width
            Assert.Equal(10, clock.Rect.Top, 1);
        }

        s.Overlays.Clock.Enabled = false;
        s.Overlays.Message.Enabled = true;
        s.Overlays.Message.Scroll = true;
        s.Overlays.Message.Anchor = Anchor9.BottomCenter;
        using (RenderWithSink(s, 400, 200, SinkKind.Preview, sink))
        {
            var band = sink.Hits.Single(h => h.Kind == HitKind.Message);
            var restingTop = band.Rect.Top;
            Assert.Equal(0, band.Rect.Left, 1);
            Assert.Equal(400, band.Rect.Width, 1);
            s.Overlays.Message.OffsetXPct = 30;
            s.Overlays.Message.OffsetYPct = -10;
            using (RenderWithSink(s, 400, 200, SinkKind.Preview, sink))
            {
                var nudged = sink.Hits.Single(h => h.Kind == HitKind.Message);
                Assert.Equal(0, nudged.Rect.Left, 1);                 // full width: the horizontal nudge is ignored
                Assert.Equal(restingTop - 20, nudged.Rect.Top, 1);    // a tenth of the height up
            }
        }

        // The PiP's nudge is a share of the viewport, and its box is recorded in viewport space.
        s.Overlays.Message.Enabled = false;
        s.Overlays.Pip.Enabled = true;
        s.Overlays.Pip.Anchor = Anchor9.TopLeft;
        s.Overlays.Pip.OffsetXPct = 50;
        using (RenderWithSink(s, 400, 200, SinkKind.Preview, sink))
        {
            var pip = sink.Hits.Single(h => h.Kind == HitKind.Pip);
            Assert.True(pip.ViewportSpace);
            Assert.Equal(8 + 200, pip.Rect.Left, 1);
        }
    }
}
