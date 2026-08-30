using Avalonia;
using Patterns.App.Services;
using Patterns.Core.Model;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>Pure viewport-building math for the arrangement → output-window mapping.</summary>
public class ArrangementTests
{
    private static ScreenInfo Info(string id, int x, int y, int w, int h, bool primary = false, int index = 0)
        => new(id, id, new PixelRect(x, y, w, h), 1.0, primary, index);

    private static ScreenPlacement Place(string id, int x, int y, bool enabled = true)
        => new() { ScreenId = id, X = x, Y = y, Enabled = enabled };

    [Fact]
    public void TouchingScreensBecomeOneSpannedCanvas()
    {
        var screens = new List<ScreenInfo> { Info("a", 0, 0, 1920, 1080), Info("b", 500, 900, 1920, 1080) };
        var placements = new[] { Place("a", 0, 0), Place("b", 1920, 0) }; // arranged flush

        var result = OutputWindowManager.BuildViewports(placements, screens);

        Assert.Equal(2, result.Count);
        foreach (var (_, vp) in result)
        {
            Assert.Equal(new SKSizeI(3840, 1080), vp.ReferenceSize);
            Assert.Null(vp.ScreenId); // grouped canvases show the program
        }
        Assert.Equal(new SKPointI(0, 0), result.First(x => x.Screen.Id == "a").Viewport.ViewportOrigin);
        Assert.Equal(new SKPointI(1920, 0), result.First(x => x.Screen.Id == "b").Viewport.ViewportOrigin);
    }

    [Fact]
    public void SeparatedScreensAreIndependentOutputs()
    {
        var screens = new List<ScreenInfo> { Info("a", 0, 0, 1920, 1080), Info("b", 1920, 0, 2560, 1440) };
        var placements = new[] { Place("a", 0, 0), Place("b", 2200, 0) }; // gap → no group

        var result = OutputWindowManager.BuildViewports(placements, screens);

        Assert.Equal(2, result.Count);
        foreach (var (screen, vp) in result)
        {
            Assert.Equal(SKSizeI.Empty, vp.ReferenceSize);   // follow own size
            Assert.Equal(screen.Id, vp.ScreenId);            // per-screen pattern lookup allowed
        }
    }

    [Fact]
    public void DisabledAndUnknownScreensAreSkipped()
    {
        var screens = new List<ScreenInfo> { Info("a", 0, 0, 1920, 1080) };
        var placements = new[]
        {
            Place("a", 0, 0, enabled: false),
            Place("ghost-from-saved-show", 4000, 0),
        };

        Assert.Empty(OutputWindowManager.BuildViewports(placements, screens));
    }

    [Fact]
    public void MixedRigWorks_SpanPlusConfidenceMonitor()
    {
        var screens = new List<ScreenInfo>
        {
            Info("l", 0, 0, 1920, 1080), Info("r", 1920, 0, 1920, 1080), Info("mon", 4000, 0, 1280, 720),
        };
        var placements = new[] { Place("l", 0, 0), Place("r", 1920, 0), Place("mon", 5000, 0) };

        var result = OutputWindowManager.BuildViewports(placements, screens);

        Assert.Equal(2, result.Count(x => x.Viewport.ReferenceSize == new SKSizeI(3840, 1080)));
        var mon = result.First(x => x.Screen.Id == "mon").Viewport;
        Assert.Equal(SKSizeI.Empty, mon.ReferenceSize);
        Assert.Equal("mon", mon.ScreenId);
    }

    [Fact]
    public void NumberingFollowsArrangementOrder()
    {
        var screens = new List<ScreenInfo> { Info("a", 0, 0, 1920, 1080), Info("b", 1920, 0, 1920, 1080) };
        // "b" arranged left of "a".
        var placements = new[] { Place("a", 2000, 0), Place("b", 0, 0) };

        var result = OutputWindowManager.BuildViewports(placements, screens);
        Assert.Equal(1, result.First(x => x.Screen.Id == "b").Viewport.SinkIndex);
        Assert.Equal(2, result.First(x => x.Screen.Id == "a").Viewport.SinkIndex);
    }
}
