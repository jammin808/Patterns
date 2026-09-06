using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// A hairline on a miniature: the wall's tiles and the panes draw a target at its own size into a
/// canvas scaled to fit (a 1920-wide target on a 96-pixel tile is 0.05), and a one-pixel line drawn
/// without antialiasing at 0.05 device pixels drops out — the grid the operator saw "not raster
/// properly on the switcher view". A pattern reads the device scale and widens its hairlines to the
/// device's own pixel; on an output nothing changes.
/// </summary>
public class HairlineTests
{
    [Fact]
    public void TheHairlineRuleWidensToOneDevicePixelAndNoMore()
    {
        Assert.Equal(1, PatternFrame.HairlineFor(1f, 1));        // an output: as asked
        Assert.Equal(3, PatternFrame.HairlineFor(1f, 3));
        Assert.Equal(20, PatternFrame.HairlineFor(96f / 1920f, 1));   // a wall tile: 20 canvas px = 1 device px
        Assert.Equal(20, PatternFrame.HairlineFor(96f / 1920f, 3));
        Assert.Equal(2, PatternFrame.HairlineFor(0.5f, 1));
        Assert.Equal(4, PatternFrame.HairlineFor(0.3f, 1));
        Assert.Equal(1, PatternFrame.HairlineFor(2f, 1));         // an upscale never thins a line
        Assert.Equal(1, PatternFrame.HairlineFor(0f, 1));         // a frame built by hand reads as an output

        Assert.True(PatternFrame.ResolvesAt(1f, 24));
        Assert.False(PatternFrame.ResolvesAt(96f / 1920f, 24));   // 1.2 device px apart: a fill, not a grid
        Assert.True(PatternFrame.ResolvesAt(96f / 1920f, 96));    // 4.8 device px apart: lines
        Assert.True(PatternFrame.ResolvesAt(0f, 4));
    }

    /// <summary>The grid drawn by the engine into a 96×54 tile the way a wall tile draws it: every one of the twenty lines lands with the device scale, next to none without it.</summary>
    [Fact]
    public void AGridOnAWallTileKeepsEveryLine()
    {
        var with = ColumnsLit(96f / 1920f);
        var without = ColumnsLit(0f);   // the old way: the sink says nothing, the lines are a canvas pixel wide
        Assert.InRange(with, 18, 22);
        Assert.True(without <= 2, $"without the device scale the tile shows {without} of 20 lines — that was the bug");
    }

    private static int ColumnsLit(float deviceScale)
    {
        var state = new ShowState();
        state.Pattern.Kind = PatternKind.Grid;
        state.Pattern.Grid.CellSize = 96;
        state.Pattern.Grid.LineWidth = 1;
        state.Pattern.Grid.Subdivisions = 0;
        state.Pattern.Grid.ShowLabel = false;
        state.Pattern.Grid.ShowCenterCross = false;
        state.Pattern.Grid.ShowBorder = false;
        state.Pattern.Grid.ShowDiagonals = false;
        var snap = new ShowSnapshot { State = state, Version = 1 };
        using var sink = new SinkState();
        var engine = new PatternEngine();
        var info = new SKImageInfo(96, 54, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var c = surface.Canvas;
        const float scale = 96f / 1920f;
        c.Save();
        c.Scale(scale);
        c.ClipRect(SKRect.Create(0, 0, 1920, 1080));
        var ctx = new RenderContext
        {
            ViewportSize = new SKSizeI(1920, 1080),
            ReferenceSize = new SKSizeI(1920, 1080),
            Time = 0,
            Now = DateTime.Now,
            UtcNow = DateTime.UtcNow,
            Sink = SinkKind.Monitor,
            SinkLabel = "tile",
            DeviceScale = deviceScale,
        };
        engine.Render(c, snap, in ctx, sink);
        c.Restore();
        c.Flush();
        using var bmp = new SKBitmap(info);
        surface.ReadPixels(info, bmp.GetPixels(), info.RowBytes, 0, 0);
        // Row 25 of 54 is canvas row 500: between the horizontal lines at 444 and 540.
        var bg = bmp.GetPixel(2, 25);
        var lit = 0;
        for (var x = 0; x < info.Width; x++)
        {
            if (bmp.GetPixel(x, 25) != bg) lit++;
        }
        return lit;
    }
}
