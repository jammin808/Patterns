using Patterns.Rendering.Media;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 69: the picture cache's idle clock — a picture no sink fetched for longer than the grace goes,
/// one the show names stays, one drawn within the window stays whatever the grace, and the snapshot
/// reads every picture's age for the ledger.
/// </summary>
public class ImageCacheResidencyTests : IDisposable
{
    public ImageCacheResidencyTests()
    {
        RenderFence.ResetForTests();
        ImageCache.ClearForTests();
    }

    public void Dispose()
    {
        ImageCache.ClearForTests();
        RenderFence.ResetForTests();
    }

    private static string Picture(string dir, string name, byte shade)
    {
        using var bmp = new SKBitmap(32, 32);
        bmp.Erase(new SKColor(shade, 0, 0));
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [Fact]
    public void AnIdlePictureGoesWhenItsGraceIsUpANamedOrDrawnOneStays()
    {
        var dir = Directory.CreateTempSubdirectory("residency");
        long now = 1_000_000;
        ImageCache.Clock = () => now;
        try
        {
            var idle = Picture(dir.FullName, "idle.png", 10);
            var named = Picture(dir.FullName, "named.png", 20);
            var drawn = Picture(dir.FullName, "drawn.png", 30);
            Assert.NotNull(ImageCache.Get(idle));
            Assert.NotNull(ImageCache.Get(named));
            now += 100_000;                                                    // a hundred seconds on, a sink fetches the third
            Assert.NotNull(ImageCache.Get(drawn));
            var swept = ImageCache.IdleSwept;

            // Every picture's age reads from the clock.
            var snapshot = ImageCache.Snapshot(now + 500);
            Assert.Equal(3, snapshot.Count);
            Assert.Equal(100_500, snapshot.Single(s => s.Path == idle).IdleMs);
            Assert.Equal(500, snapshot.Single(s => s.Path == drawn).IdleMs);
            Assert.All(snapshot, s => Assert.True(s.Bytes > 0));

            // Within the grace nothing goes.
            Assert.Equal(0, ImageCache.SweepIdle(200_000, keep: p => p == named, nowTicks: now + 500));
            Assert.Equal(3, ImageCache.Count);

            // Past it: the idle picture goes, the named one is kept by the show, the one a sink just fetched stays.
            Assert.Equal(1, ImageCache.SweepIdle(60_000, keep: p => p == named, nowTicks: now + 500));
            Assert.Equal(2, ImageCache.Count);
            Assert.Contains(ImageCache.Snapshot(), s => s.Path == named);
            Assert.Contains(ImageCache.Snapshot(), s => s.Path == drawn);
            Assert.DoesNotContain(ImageCache.Snapshot(), s => s.Path == idle);
            Assert.Equal(swept + 1, ImageCache.IdleSwept);

            // A grace of nought never takes a picture drawn within the window — the floor under critical pressure — nor one the show names.
            Assert.Equal(0, ImageCache.SweepIdle(0, keep: p => p == named, nowTicks: now + 1_000));
            Assert.Equal(2, ImageCache.Count);
            // Past the window with no grace and nothing named, everything goes at once.
            Assert.Equal(2, ImageCache.SweepIdle(0, keep: null, nowTicks: now + ImageCache.DrawnWithinMs + 1));
            Assert.Equal(0, ImageCache.Count);
        }
        finally
        {
            ImageCache.Clock = null;
            dir.Delete(recursive: true);
        }
    }
}
