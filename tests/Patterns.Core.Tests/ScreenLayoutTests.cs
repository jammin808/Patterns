using Patterns.Core.Rendering;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

public class ScreenLayoutTests
{
    private static SKRectI R(int x, int y, int w, int h) => SKRectI.Create(x, y, w, h);

    [Fact]
    public void FlushEdgesTouch_GapsDoNot()
    {
        var a = R(0, 0, 1920, 1080);
        Assert.True(ScreenLayout.Touching(a, R(1920, 0, 1920, 1080)));      // flush right
        Assert.True(ScreenLayout.Touching(a, R(1921, 0, 1920, 1080)));      // 1px tolerance
        Assert.False(ScreenLayout.Touching(a, R(1930, 0, 1920, 1080)));     // gap
        Assert.True(ScreenLayout.Touching(a, R(0, 1080, 1920, 1080)));      // flush below
        Assert.True(ScreenLayout.Touching(a, R(600, 1080, 1920, 1080)));    // flush below, offset
        // Vertical offset but still enough shared edge:
        Assert.True(ScreenLayout.Touching(a, R(1920, 900, 1920, 1080)));
        // Corner-only contact (shared edge < minimum) must NOT connect:
        Assert.False(ScreenLayout.Touching(a, R(1920, 1075, 1920, 1080)));
    }

    [Fact]
    public void GroupsFindConnectedRuns()
    {
        var screens = new List<ArrangedScreen>
        {
            new("a", R(0, 0, 1920, 1080)),
            new("b", R(1920, 0, 1920, 1080)),     // touches a
            new("c", R(3840, 0, 1920, 1080)),     // touches b → one canvas of three
            new("d", R(6000, 0, 1920, 1080)),     // stand-alone
        };
        var groups = ScreenLayout.Groups(screens);
        Assert.Equal(2, groups.Count);
        Assert.Equal(3, groups[0].Count);
        Assert.Single(groups[1]);
        Assert.Equal(new SKRectI(0, 0, 5760, 1080), ScreenLayout.Union(groups[0]));
    }

    [Fact]
    public void VerticalStacksGroupToo()
    {
        var screens = new List<ArrangedScreen>
        {
            new("top", R(0, 0, 1920, 1080)),
            new("bottom", R(0, 1080, 1920, 1080)),
        };
        var groups = ScreenLayout.Groups(screens);
        var g = Assert.Single(groups);
        Assert.Equal(new SKRectI(0, 0, 1920, 2160), ScreenLayout.Union(g));
    }

    [Fact]
    public void SnapPullsEdgesFlushAndAlignsTops()
    {
        var others = new List<SKRectI> { R(0, 0, 1920, 1080) };
        // 14px short of flush, 9px vertical misalignment — inside the threshold.
        var snapped = ScreenLayout.Snap(R(1934, 9, 1920, 1080), others, threshold: 24);
        Assert.Equal(1920, snapped.Left);
        Assert.Equal(0, snapped.Top);
        Assert.True(ScreenLayout.Touching(snapped, others[0]));
    }

    [Fact]
    public void SnapLeavesFarRectsAlone()
    {
        var others = new List<SKRectI> { R(0, 0, 1920, 1080) };
        var moving = R(2400, 300, 1920, 1080);
        Assert.Equal(moving, ScreenLayout.Snap(moving, others, threshold: 24));
    }

    [Fact]
    public void SnapBelowAlignsLefts()
    {
        var others = new List<SKRectI> { R(100, 0, 1920, 1080) };
        var snapped = ScreenLayout.Snap(R(112, 1094, 1920, 1080), others, threshold: 24);
        Assert.Equal(1080, snapped.Top);   // flush under
        Assert.Equal(100, snapped.Left);   // left-aligned
    }

    [Fact]
    public void OverlapDetectionIgnoresTouching()
    {
        var others = new[] { R(0, 0, 100, 100) };
        Assert.False(ScreenLayout.OverlapsAny(R(100, 0, 100, 100), others)); // flush = fine
        Assert.True(ScreenLayout.OverlapsAny(R(50, 50, 100, 100), others));  // real overlap
    }

    [Fact]
    public void DefaultLayoutKeepsScreensDisconnected()
    {
        var sizes = new List<SKSizeI> { new(1920, 1080), new(2560, 1440), new(1080, 1920) };
        var points = ScreenLayout.DefaultLayout(sizes);
        var arranged = sizes.Select((s, i) => new ArrangedScreen($"s{i}", SKRectI.Create(points[i].X, points[i].Y, s.Width, s.Height))).ToList();
        Assert.Equal(3, ScreenLayout.Groups(arranged).Count); // nothing connected by default
    }
}
