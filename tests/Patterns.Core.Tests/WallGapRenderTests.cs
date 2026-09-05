using Patterns.Core.Model;
using Patterns.Core.Patterns;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The wall's dead strips through the engine: content laid out across them, an output with the
/// strips cut out, a monitor shading them, and the LED wall and video wall patterns putting their
/// tiles on the real panels.
/// </summary>
public class WallGapRenderTests
{
    private const string Wall = "planned:wall";

    /// <summary>Three LED pillars of 200 × 200 px packed in a 600 × 200 raster, 100 px of air between them: an 800 × 200 surface.</summary>
    private static ShowState Pillars(Action<ShowState>? mutate = null) => RenderTestHarness.State(s =>
    {
        var p = new ScreenPlacement { ScreenId = Wall, X = 0, Y = 0, Planned = true, PlannedWidth = 600, PlannedHeight = 200 };
        p.Gaps.Add(new WallGap { Axis = GapAxis.Vertical, At = 200, Size = 100 });
        p.Gaps.Add(new WallGap { Axis = GapAxis.Vertical, At = 400, Size = 100 });
        s.Output.Placements.Add(p);
        mutate?.Invoke(s);
    });

    private static ShowSnapshot Snap(ShowState state)
        => new() { State = state, Version = 1, Rig = RigGeometry.Build(state, RigGeometry.NoDisplays) };

    private static RenderContext Ctx(int w, int h, SKSizeI reference, SinkKind sink) => new()
    {
        ViewportSize = new SKSizeI(w, h),
        ReferenceSize = reference,
        Time = 1.0,
        Now = new DateTime(2026, 8, 29, 12, 0, 0),
        UtcNow = RenderTestHarness.FixedUtcNow,
        Sink = sink,
        SinkIndex = 1,
        SinkLabel = "wall",
        ScreenId = Wall,
    };

    /// <summary>The output's raster: the engine draws the surface and cuts the strips out.</summary>
    private static SKBitmap RenderOutput(ShowSnapshot snap)
    {
        var engine = new PatternEngine();
        using var sink = new SinkState();
        var info = new SKImageInfo(600, 200, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var ctx = Ctx(600, 200, snap.Rig.SizeOf(Wall), SinkKind.Output);
        engine.RenderWall(surface.Canvas, snap, in ctx, sink, snap.Rig.GapsOf(Wall), snap.Rig.RasterRectOf(Wall));
        surface.Canvas.Flush();
        var bmp = new SKBitmap(info);
        surface.ReadPixels(info, bmp.GetPixels(), info.RowBytes, 0, 0);
        return bmp;
    }

    private static bool Red(SKColor c) => c.Red > 200 && c.Green < 60 && c.Blue < 60;

    private static byte MaxBlue(SKBitmap bmp, int cx, int cy, int half = 8)
    {
        byte max = 0;
        for (var y = cy - half; y <= cy + half; y++)
        for (var x = cx - half; x <= cx + half; x++)
        {
            max = Math.Max(max, bmp.GetPixel(x, y).Blue);
        }
        return max;
    }

    private static byte MaxRed(SKBitmap bmp, int cx, int cy, int half = 8)
    {
        byte max = 0;
        for (var y = cy - half; y <= cy + half; y++)
        for (var x = cx - half; x <= cx + half; x++)
        {
            max = Math.Max(max, bmp.GetPixel(x, y).Red);
        }
        return max;
    }

    [Fact]
    public void TheRigCarriesTheStripsAndTheSurfaceGrowsByThem()
    {
        var snap = Snap(Pillars());
        Assert.Equal(new SKSizeI(800, 200), snap.Rig.SizeOf(Wall));
        Assert.Equal(new SKSizeI(600, 200), snap.Rig.RasterSizeOf(Wall));
        Assert.Equal(new SKSizeI(800, 200), snap.Rig.SizeOf(null));           // the program takes the surface's shape
        Assert.Equal(2, snap.Rig.GapsOf(Wall).Count);
        Assert.Same(snap.Rig.GapsOf(Wall), snap.Rig.GapsOf(null));
        Assert.Equal(new SKRectI(0, 0, 600, 200), snap.Rig.RasterRectOf(Wall));
        Assert.Equal(new SKSizeI(800, 200), snap.Rig.ViewportForTarget(Wall).ViewportSize);
    }

    [Fact]
    public void ContentSpansTheStripsTheOutputCutsThemAndAMonitorShadesThem()
    {
        var snap = Snap(Pillars(s =>
        {
            s.Pattern.Kind = PatternKind.FlatField;
            s.Pattern.FlatField.Color = "#FF0000";
            s.Pattern.FlatField.ShowLabel = false;
            s.Pattern.Canvas.FollowOutput = true;
        }));

        // A feed of the target: the whole surface, red across the strips.
        using (var feed = RenderTestHarness.Render(snap, 800, 200, reference: new SKSizeI(800, 200), screenId: Wall, sinkKind: SinkKind.Output))
        {
            Assert.True(Red(feed.GetPixel(100, 100)));
            Assert.True(Red(feed.GetPixel(250, 100)), "a feed carries the surface whole — the strip is content on it");
            Assert.True(Red(feed.GetPixel(799, 100)));
        }

        // The output: 600 px of real pixels, red end to end — the strips are gone, nothing black between the pillars.
        using (var raster = RenderOutput(snap))
        {
            Assert.Equal(600, raster.Width);
            for (var x = 0; x < 600; x += 25)
            {
                Assert.True(Red(raster.GetPixel(x, 100)), $"raster x={x} is {raster.GetPixel(x, 100)}");
            }
        }

        // A monitor of it: the surface with the strips shaded dark, so the desk sees where the wall has no pixels.
        using (var monitor = RenderTestHarness.Render(snap, 800, 200, reference: new SKSizeI(800, 200), screenId: Wall, sinkKind: SinkKind.Monitor))
        {
            Assert.True(Red(monitor.GetPixel(100, 100)));
            Assert.True(Red(monitor.GetPixel(400, 100)));
            Assert.False(Red(monitor.GetPixel(250, 100)), $"the strip is shaded on a monitor, got {monitor.GetPixel(250, 100)}");
            Assert.False(Red(monitor.GetPixel(550, 100)));
        }
    }

    [Fact]
    public void TheLedWallPatternPutsItsTilesOnTheRealPanelsAndDrawsTheAirBlack()
    {
        var snap = Snap(Pillars(s =>
        {
            s.Pattern.Kind = PatternKind.LedWall;
            s.Pattern.LedWall.TileWidth = 200;
            s.Pattern.LedWall.TileHeight = 200;
            s.Pattern.LedWall.Columns = 3;
            s.Pattern.LedWall.Rows = 1;
            s.Pattern.LedWall.ShowInfo = false;
            s.Pattern.LedWall.ShowCenterCross = false;
            s.Pattern.LedWall.ShowPixelGrid = false;
        }));
        var layout = CanvasResolver.Led(snap.State.Pattern.LedWall);
        var gaps = snap.Rig.GapsOf(Wall);

        // The maths: the pattern's raster is the screen's, so its canvas is the surface and tile 2 sits past the first strip.
        Assert.True(CanvasResolver.WallSpansGaps(snap.State.Pattern, gaps, layout.Canvas));
        Assert.Equal(new SKSizeI(800, 200), CanvasResolver.Resolve(snap.State.Pattern, new SKSizeI(800, 200), gaps));
        Assert.Equal(new SKRect(300, 0, 500, 200), LedWallPattern.TileRect(layout, 1, 0, 600, 200, gaps));
        Assert.Equal(new SKRect(600, 0, 800, 200), LedWallPattern.TileRect(layout, 2, 0, 600, 200, gaps));
        Assert.Equal(new SKRect(200, 0, 400, 200), LedWallPattern.TileRect(layout, 1, 0, 600, 200, GapMap.Empty));

        // The surface: white tile numbers at the tiles' real places, black hatched air between them.
        using (var surface = RenderTestHarness.Render(snap, 800, 200, reference: new SKSizeI(800, 200), screenId: Wall, sinkKind: SinkKind.Output))
        {
            Assert.True(MaxBlue(surface, 100, 100) > 150, "tile 1-1's number");
            Assert.True(MaxBlue(surface, 400, 100) > 150, "tile 1-2's number sits past the first strip");
            Assert.True(MaxBlue(surface, 700, 100) > 150, "tile 1-3's number sits past both");
            Assert.True(MaxBlue(surface, 250, 100) < 60, $"the air is black (blue {MaxBlue(surface, 250, 100)})");
            Assert.True(MaxRed(surface, 250, 100) > 60, "…with its amber hatch");
        }

        // The output: the numbers land on the panels, packed as the processor is fed — nothing black between them.
        using (var raster = RenderOutput(snap))
        {
            Assert.True(MaxBlue(raster, 100, 100) > 150);
            Assert.True(MaxBlue(raster, 300, 100) > 150, "tile 1-2's number on the middle pillar");
            Assert.True(MaxBlue(raster, 500, 100) > 150, "tile 1-3's number on the last pillar");
            Assert.True(MaxRed(raster, 215, 100) < 60, "no hatched air inside the raster");
            Assert.True(MaxRed(raster, 415, 100) < 60);
        }

        // A wall built for another raster keeps its packed canvas: the tiles are not moved.
        snap.State.Pattern.LedWall.Columns = 4;
        var other = CanvasResolver.Led(snap.State.Pattern.LedWall);
        Assert.False(CanvasResolver.WallSpansGaps(snap.State.Pattern, gaps, other.Canvas));
        Assert.Equal(new SKSizeI(800, 200), CanvasResolver.Resolve(snap.State.Pattern, new SKSizeI(800, 200), gaps));
    }

    [Fact]
    public void TheVideoWallPatternMovesItsElementsPastTheBezelsAndDrawsTheRing()
    {
        // A 2 × 1 wall of 400 × 200 displays fed as one 800 × 200 raster, 40 px of bezel between them.
        var state = RenderTestHarness.State(s =>
        {
            var p = new ScreenPlacement { ScreenId = Wall, X = 0, Y = 0, Planned = true, PlannedWidth = 800, PlannedHeight = 200 };
            p.Gaps.Add(new WallGap { Axis = GapAxis.Vertical, At = 400, Size = 40 });
            s.Output.Placements.Add(p);
            s.Pattern.Kind = PatternKind.VideoWall;
            s.Pattern.VideoWall.ElementWidth = 400;
            s.Pattern.VideoWall.ElementHeight = 200;
            s.Pattern.VideoWall.Columns = 2;
            s.Pattern.VideoWall.Rows = 1;
            s.Pattern.VideoWall.ShowInfo = false;
        });
        var snap = Snap(state);
        var gaps = snap.Rig.GapsOf(Wall);
        Assert.Equal(new SKSizeI(840, 200), snap.Rig.SizeOf(Wall));
        Assert.Equal(new SKRectI(440, 0, 840, 200), VideoWallPattern.ElementRect(1, 0, 400, 200, gaps));
        Assert.Equal(new SKRectI(400, 0, 800, 200), VideoWallPattern.ElementRect(1, 0, 400, 200, GapMap.Empty));

        using var surface = RenderTestHarness.Render(snap, 840, 200, reference: new SKSizeI(840, 200), screenId: Wall, sinkKind: SinkKind.Output);
        // Element 2's number sits in the middle of its moved rect; the bezel strip is black and hatched.
        Assert.True(MaxBlue(surface, 640, 100) > 150, "element 2's number past the bezel");
        Assert.True(MaxBlue(surface, 420, 100) < 60, "the bezel is black");
        Assert.True(MaxRed(surface, 420, 100) > 60, "…and hatched");
        // The ring across the wall: drawn on the surface, so it lands on element 2 at 45° from the
        // wall's centre (the strip itself is nothing, and drawn as nothing on top of everything).
        var r = Math.Min(840, 200) * 0.42f;
        var px = (int)Math.Round(420 + r * Math.Cos(Math.PI / 4));
        var py = (int)Math.Round(100 - r * Math.Sin(Math.PI / 4));
        var ring = false;
        for (var y = py - 3; y <= py + 3 && !ring; y++)
        for (var x = px - 3; x <= px + 3 && !ring; x++)
        {
            var c = surface.GetPixel(x, y);
            ring = c.Blue > 200 && c.Green > 150 && c.Red < 120;
        }
        Assert.True(ring, $"the accent ring passes ({px},{py}) on element 2");
    }

    [Fact]
    public void AMultiviewTileOfTheWallShadesTheStripsAndAJoinedCanvasMovesItsMembers()
    {
        var snap = Snap(Pillars(s =>
        {
            s.Pattern.Kind = PatternKind.FlatField;
            s.Pattern.FlatField.Color = "#FF0000";
            s.Pattern.FlatField.ShowLabel = false;
            s.Pattern.Canvas.FollowOutput = true;
            var mv = new ScreenPlacement { ScreenId = "planned:mv", X = 0, Y = 4000, Planned = true, PlannedWidth = 800, PlannedHeight = 200, UseCustomPattern = true };
            s.Output.Placements.Add(mv);
            s.Independent.Add(new OutputAssignment { ScreenId = "planned:mv", Pattern = new PatternConfig { Kind = PatternKind.Multiview } });
            var tiles = s.Independent[^1].Pattern.Multiview;
            tiles.ShowLabels = false;
            tiles.ShowTally = false;
            tiles.Tiles.Add(new MultiviewTileConfig { Source = MultiviewSource.Screen, ScreenId = Wall });
        }));
        using (var mv = RenderTestHarness.Render(snap, 800, 200, reference: new SKSizeI(800, 200), screenId: "planned:mv", sinkKind: SinkKind.Output))
        {
            // The tile draws the 800 × 200 surface into the 800 × 200 multiview (a little inset by the cell gap): red on the pillars, dark on the air.
            Assert.True(Red(mv.GetPixel(100, 100)), $"pillar 1 in the tile, got {mv.GetPixel(100, 100)}");
            Assert.True(Red(mv.GetPixel(400, 100)), "pillar 2 in the tile");
            Assert.False(Red(mv.GetPixel(250, 100)), $"the strip is shaded in the tile, got {mv.GetPixel(250, 100)}");
        }

        // Two planned displays joined, 40 px of bezel: the right member moves past it and the canvas grows.
        var state = RenderTestHarness.State(s =>
        {
            s.Output.Placements.Add(new ScreenPlacement { ScreenId = "planned:l", X = 0, Y = 0, Planned = true, PlannedWidth = 400, PlannedHeight = 200 });
            s.Output.Placements.Add(new ScreenPlacement { ScreenId = "planned:r", X = 400, Y = 0, Planned = true, PlannedWidth = 400, PlannedHeight = 200 });
            s.Output.CanvasNames.Add(new CanvasNameConfig { MemberKey = CanvasNameConfig.KeyFor(new[] { "planned:l", "planned:r" }), SeamGapX = 40 });
        });
        var geo = RigGeometry.Build(state, RigGeometry.NoDisplays);
        var key = CanvasNameConfig.KeyFor(new[] { "planned:l", "planned:r" });
        Assert.Equal(new SKSizeI(840, 200), geo.SizeOf(key));
        Assert.Equal(new SKSizeI(800, 200), geo.RasterSizeOf(key));
        var right = geo.ViewportForTile("planned:r");
        Assert.Equal(new SKPointI(440, 0), right.Origin);
        Assert.Equal(new SKSizeI(400, 200), right.ViewportSize);
        Assert.Equal(new SKSizeI(840, 200), right.ReferenceSize);
        Assert.Equal(new SKPointI(0, 0), geo.ViewportForTile("planned:l").Origin);
        Assert.Single(geo.GapsOf(key).Slices(geo.RasterRectOf("planned:r")));   // no strip runs through a member: one run, moved
    }
}
