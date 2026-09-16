using Avalonia;
using Patterns.App.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>Round 65.12: a display's bounds cross from the desk's UI geometry to the platform assembly's raster rectangle at one seam, and nothing is lost on the way.</summary>
public class PlatformSeamsTests
{
    [Fact]
    public void TheSeamKeepsTheOriginAndTheSize()
    {
        var r = new PixelRect(10, 20, 300, 400).ToRaster();
        Assert.Equal((10, 20, 310, 420), (r.Left, r.Top, r.Right, r.Bottom));
        Assert.Equal((300, 400), (r.Width, r.Height));
        Assert.True(r.Contains(10, 20));
        Assert.False(r.Contains(310, 420));
    }
}
