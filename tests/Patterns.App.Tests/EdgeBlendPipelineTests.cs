using Avalonia;
using Patterns.App.Rendering;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>The edge blend on the real output pipeline, rendered to raster, and the viewports that carry it.</summary>
public class EdgeBlendPipelineTests
{
    private static SnapshotBus WhiteBus()
    {
        var state = new ShowState();
        state.Pattern.Canvas.FollowOutput = true;
        state.Pattern.Kind = PatternKind.FlatField;
        state.Pattern.FlatField.Color = "#FFFFFF";
        state.Pattern.FlatField.ShowLabel = false;
        state.Pattern.FlatField.ShowBorder = false;
        state.Overlays.Clock.Enabled = false;
        state.Overlays.Info.Enabled = false;
        state.Countdown.Enabled = false;
        var bus = new SnapshotBus(state);
        bus.Publish(state);
        return bus;
    }

    private static SKBitmap Frame(SnapshotBus bus, PipelineViewport viewport, int w, int h)
    {
        using var pipeline = new RenderPipeline(bus, viewport);
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        pipeline.Render(surface.Canvas, w, h, renderScaling: 1.0);
        var bmp = new SKBitmap(info);
        surface.ReadPixels(info, bmp.GetPixels(), info.RowBytes, 0, 0);
        return bmp;
    }

    private static PipelineViewport Output() => new(SinkKind.Output, SKSizeI.Empty, default, null, 1, "test");

    [Fact]
    public void ARightZoneFallsMonotonicallyToBlackAndTheRestStaysWhite()
    {
        using var bmp = Frame(WhiteBus(), Output() with { BlendRightPx = 100, BlendCurve = BlendCurve.Linear }, 400, 200);
        Assert.Equal(255, bmp.GetPixel(10, 100).Red);
        Assert.Equal(255, bmp.GetPixel(299, 100).Red);
        var last = 256;
        for (var x = 300; x < 400; x += 5)
        {
            var v = bmp.GetPixel(x, 100).Red;
            Assert.True(v <= last, $"column {x}: {v} after {last}");
            last = v;
        }
        Assert.True(bmp.GetPixel(399, 100).Red < 8, $"outer edge: {bmp.GetPixel(399, 100).Red}");
        Assert.InRange(bmp.GetPixel(350, 100).Red, 118, 138); // halfway through a linear zone
    }

    [Fact]
    public void TwoLinearZonesAddUpToOneFlatPicture()
    {
        // The left projector fades out over its right zone; the right projector fades in over its
        // left zone. Column by column the two signals sum to white.
        using var left = Frame(WhiteBus(), Output() with { BlendRightPx = 120, BlendCurve = BlendCurve.Linear }, 400, 200);
        using var right = Frame(WhiteBus(), Output() with { BlendLeftPx = 120, BlendCurve = BlendCurve.Linear }, 400, 200);
        for (var i = 0; i < 120; i += 3)
        {
            var sum = left.GetPixel(280 + i, 100).Red + right.GetPixel(i, 100).Red;
            Assert.InRange(sum, 250, 260);
        }
    }

    [Fact]
    public void GammaLiftsTheMiddleOfTheZone()
    {
        using var raw = Frame(WhiteBus(), Output() with { BlendRightPx = 100, BlendCurve = BlendCurve.Linear, BlendGamma = 1.0 }, 400, 200);
        using var lifted = Frame(WhiteBus(), Output() with { BlendRightPx = 100, BlendCurve = BlendCurve.Linear, BlendGamma = 2.2 }, 400, 200);
        Assert.True(lifted.GetPixel(350, 100).Red > raw.GetPixel(350, 100).Red + 40);
        Assert.Equal(255, lifted.GetPixel(200, 100).Red);
    }

    [Fact]
    public void AMonitorOrPreviewNeverFadesItsEdges()
    {
        var monitor = new PipelineViewport(SinkKind.Monitor, new SKSizeI(400, 200), default, null, 0, "mon")
        {
            FitReference = true, BlendRightPx = 100,
        };
        Assert.False(monitor.HasBlend);
        using var bmp = Frame(WhiteBus(), monitor, 400, 200);
        Assert.Equal(255, bmp.GetPixel(395, 100).Red);

        var preview = PipelineViewport.Preview with { BlendRightPx = 100 };
        Assert.False(preview.HasBlend);
        using var pv = Frame(WhiteBus(), preview, 400, 200);
        Assert.Equal(255, pv.GetPixel(395, 100).Red);
    }

    [Fact]
    public void TheZoneFollowsTheRotationAndSitsUnderTheTrims()
    {
        // A portrait projector rotated 90°: arrangement-space "left" is the physical top edge.
        using var rot = Frame(WhiteBus(), Output() with
        {
            Rotation = OutputRotation.Rot90, BlendLeftPx = 60, BlendCurve = BlendCurve.Linear,
        }, 200, 300);
        Assert.True(rot.GetPixel(100, 1).Red < 12, $"top edge {rot.GetPixel(100, 1).Red}");
        Assert.Equal(255, rot.GetPixel(100, 150).Red);
        Assert.Equal(255, rot.GetPixel(100, 298).Red);
        Assert.Equal(255, rot.GetPixel(2, 150).Red);

        // With a brightness trim the picture is dimmer everywhere, and the zone's outer edge is
        // still black: the fade multiplies the trimmed picture rather than the other way round.
        using var trimmed = Frame(WhiteBus(), Output() with
        {
            BrightnessPct = 50, BlendRightPx = 100, BlendCurve = BlendCurve.Linear,
        }, 400, 200);
        Assert.InRange(trimmed.GetPixel(100, 100).Red, 120, 136);
        Assert.True(trimmed.GetPixel(399, 100).Red < 8);
        Assert.InRange(trimmed.GetPixel(350, 100).Red, 55, 72);
    }

    // ---- the viewports ------------------------------------------------------------------

    private static ScreenInfo Info(string id, int x, int y, int w, int h)
        => new(id, id, new PixelRect(x, y, w, h), 1.0, false, 0);

    [Fact]
    public void TwoOverlappingProjectorsWithAutomaticBlendFormOneCanvasAndFadeTheirSharedZone()
    {
        var screens = new List<ScreenInfo> { Info("a", 0, 0, 1920, 1080), Info("b", 1920, 0, 1920, 1080) };
        var placements = new[]
        {
            new ScreenPlacement { ScreenId = "a", X = 0, Y = 0, BlendAuto = true, BlendGamma = 2.2 },
            new ScreenPlacement { ScreenId = "b", X = 1720, Y = 0, BlendAuto = true },
        };

        var result = OutputWindowManager.BuildViewports(placements, screens);
        Assert.Equal(2, result.Count);
        var a = result.First(x => x.Screen.Id == "a").Viewport;
        var b = result.First(x => x.Screen.Id == "b").Viewport;
        Assert.Equal(new SKSizeI(3640, 1080), a.ReferenceSize);
        Assert.Equal("a+b", a.ScreenId);
        Assert.Equal(new SKPointI(0, 0), a.ViewportOrigin);
        Assert.Equal(new SKPointI(1720, 0), b.ViewportOrigin);
        Assert.Equal(200, a.BlendRightPx);
        Assert.Equal(0, a.BlendLeftPx);
        Assert.Equal(200, b.BlendLeftPx);
        Assert.Equal(0, b.BlendRightPx);
        Assert.Equal(2.2, a.BlendGamma);
        Assert.True(a.HasBlend && b.HasBlend);

        // The rig on the snapshot agrees: one canvas, the union's width.
        var state = new ShowState();
        foreach (var p in placements) state.Output.Placements.Add(p);
        var rig = RigGeometry.Build(state, new Dictionary<string, ScreenGeometry>
        {
            ["a"] = new(1920, 1080, "A"),
            ["b"] = new(1920, 1080, "B"),
        });
        Assert.Equal(new SKSizeI(3640, 1080), rig.SizeOf("a+b"));
        Assert.Equal("a+b", rig.TargetOf("a"));
    }

    [Fact]
    public void WithoutTheFlagAnOverlapIsTwoScreensAndNoBlend()
    {
        var screens = new List<ScreenInfo> { Info("a", 0, 0, 1920, 1080), Info("b", 1920, 0, 1920, 1080) };
        var placements = new[]
        {
            new ScreenPlacement { ScreenId = "a", X = 0, Y = 0 },
            new ScreenPlacement { ScreenId = "b", X = 1720, Y = 0 },
        };
        var result = OutputWindowManager.BuildViewports(placements, screens);
        Assert.All(result, x => Assert.Equal(SKSizeI.Empty, x.Viewport.ReferenceSize));
        Assert.All(result, x => Assert.False(x.Viewport.HasBlend));

        // Typed widths apply without touching the grouping.
        placements[0].BlendRightPx = 96;
        var manual = OutputWindowManager.BuildViewports(placements, screens);
        var a = manual.First(x => x.Screen.Id == "a").Viewport;
        Assert.Equal(96, a.BlendRightPx);
        Assert.Equal(SKSizeI.Empty, a.ReferenceSize);
        Assert.Equal("a", a.ScreenId);
    }

    // ---- beyond two projectors ---------------------------------------------------------------

    private static ScreenPlacement Blending(string id, int x, int y)
        => new() { ScreenId = id, X = x, Y = y, BlendAuto = true, BlendCurve = BlendCurve.Linear };

    [Fact]
    public void ARowOfThreeIsOneCanvasAndEveryJoinAddsUpToOneFlatPicture()
    {
        // Three 400×200 projectors, each 100 px into the next: a 1000×200 canvas, the middle one fading both sides.
        var screens = new List<ScreenInfo> { Info("a", 0, 0, 400, 200), Info("b", 400, 0, 400, 200), Info("c", 800, 0, 400, 200) };
        var placements = new[] { Blending("a", 0, 0), Blending("b", 300, 0), Blending("c", 600, 0) };
        var result = OutputWindowManager.BuildViewports(placements, screens);
        var a = result.First(x => x.Screen.Id == "a").Viewport;
        var b = result.First(x => x.Screen.Id == "b").Viewport;
        var c = result.First(x => x.Screen.Id == "c").Viewport;
        Assert.Equal(new SKSizeI(1000, 200), b.ReferenceSize);
        Assert.Equal(a.ScreenId, b.ScreenId);
        Assert.Equal(b.ScreenId, c.ScreenId);
        Assert.Equal(new SKPointI(300, 0), b.ViewportOrigin);
        Assert.Equal((0, 100), (a.BlendLeftPx, a.BlendRightPx));
        Assert.Equal((100, 100), (b.BlendLeftPx, b.BlendRightPx));
        Assert.Equal((100, 0), (c.BlendLeftPx, c.BlendRightPx));

        var bus = WhiteBus();
        using var left = Frame(bus, a, 400, 200);
        using var middle = Frame(bus, b, 400, 200);
        using var right = Frame(bus, c, 400, 200);
        Assert.Equal(255, middle.GetPixel(200, 100).Red);        // the middle projector's own picture, full
        Assert.Equal(255, left.GetPixel(100, 100).Red);
        for (var i = 0; i < 100; i += 4)
        {
            Assert.InRange(left.GetPixel(300 + i, 100).Red + middle.GetPixel(i, 100).Red, 250, 260);     // the A|B join
            Assert.InRange(middle.GetPixel(300 + i, 100).Red + right.GetPixel(i, 100).Red, 250, 260);    // the B|C join
        }

        // The rig on the snapshot agrees: one canvas, the union's size, every member inside it.
        var state = new ShowState();
        foreach (var p in placements) state.Output.Placements.Add(p);
        var rig = RigGeometry.Build(state, new Dictionary<string, ScreenGeometry>
        {
            ["a"] = new(400, 200, "A"),
            ["b"] = new(400, 200, "B"),
            ["c"] = new(400, 200, "C"),
        });
        Assert.Equal(new SKSizeI(1000, 200), rig.SizeOf(b.ScreenId!));
        Assert.Equal(b.ScreenId, rig.TargetOf("c"));
    }

    [Fact]
    public void AGridOfFourAddsUpToOneFlatPictureInTheCornerAllFourShare()
    {
        // 2 × 2 of 400×200, 100 px along and 50 px across: every projector fades a side and a top or
        // bottom, and in the shared corner the four products sum to one — (l + r) × (t + b) = 1.
        var screens = new List<ScreenInfo>
        {
            Info("tl", 0, 0, 400, 200), Info("tr", 400, 0, 400, 200), Info("bl", 0, 200, 400, 200), Info("br", 400, 200, 400, 200),
        };
        var placements = new[] { Blending("tl", 0, 0), Blending("tr", 300, 0), Blending("bl", 0, 150), Blending("br", 300, 150) };
        var result = OutputWindowManager.BuildViewports(placements, screens);
        PipelineViewport Of(string id) => result.First(x => x.Screen.Id == id).Viewport;
        var tl = Of("tl");
        var tr = Of("tr");
        var bl = Of("bl");
        var br = Of("br");
        Assert.Equal(new SKSizeI(700, 350), tl.ReferenceSize);
        Assert.All(new[] { tr, bl, br }, v => Assert.Equal(tl.ScreenId, v.ScreenId));
        Assert.Equal((0, 0, 100, 50), (tl.BlendLeftPx, tl.BlendTopPx, tl.BlendRightPx, tl.BlendBottomPx));
        Assert.Equal((100, 0, 0, 50), (tr.BlendLeftPx, tr.BlendTopPx, tr.BlendRightPx, tr.BlendBottomPx));
        Assert.Equal((0, 50, 100, 0), (bl.BlendLeftPx, bl.BlendTopPx, bl.BlendRightPx, bl.BlendBottomPx));
        Assert.Equal((100, 50, 0, 0), (br.BlendLeftPx, br.BlendTopPx, br.BlendRightPx, br.BlendBottomPx));

        var bus = WhiteBus();
        using var fTl = Frame(bus, tl, 400, 200);
        using var fTr = Frame(bus, tr, 400, 200);
        using var fBl = Frame(bus, bl, 400, 200);
        using var fBr = Frame(bus, br, 400, 200);
        // The corner region is canvas x 300..399, y 150..199: in each projector's own pixels …
        foreach (var i in new[] { 0, 25, 50, 75, 99 })
        {
            foreach (var j in new[] { 0, 12, 25, 37, 49 })
            {
                var sum = fTl.GetPixel(300 + i, 150 + j).Red + fTr.GetPixel(i, 150 + j).Red
                        + fBl.GetPixel(300 + i, j).Red + fBr.GetPixel(i, j).Red;
                Assert.InRange(sum, 244, 266);
            }
        }
        // … and along a plain side join (outside the corner) two projectors still make one.
        for (var i = 0; i < 100; i += 10)
        {
            Assert.InRange(fTl.GetPixel(300 + i, 60).Red + fTr.GetPixel(i, 60).Red, 250, 260);
            Assert.InRange(fTl.GetPixel(100, 150 + i / 2).Red + fBl.GetPixel(100, i / 2).Red, 250, 260);
        }
        Assert.Equal(255, fBr.GetPixel(300, 150).Red);   // the bottom-right projector's own picture, full
    }
}
