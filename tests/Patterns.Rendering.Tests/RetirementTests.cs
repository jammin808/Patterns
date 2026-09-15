using Patterns.Core.Media;
using Patterns.Rendering.Media;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Retirement behind the fence: a picture the cache lets go waits for the sinks that drew it and
/// no other; a frame retired off any frame goes at once and one under a frame waits for that
/// sink's next; the dead-sink rule is the one timing assumption, and nothing is freed for being
/// many or for being old.
/// </summary>
public class RetirementTests : IDisposable
{
    private const long T0 = 5_000_000_000L;
    private long _now = T0;

    private static long Sec(double seconds) => (long)(seconds * System.Diagnostics.Stopwatch.Frequency);

    public RetirementTests()
    {
        RenderFence.ResetForTests();
        RenderFence.Clock = () => _now;
        ImageCache.ClearForTests();
    }

    public void Dispose()
    {
        ImageCache.ClearForTests();
        RenderFence.Clock = null;
        RenderFence.ResetForTests();
    }

    private static string Picture(string dir, string name, byte shade)
    {
        using var bmp = new SKBitmap(64, 64);
        bmp.Erase(new SKColor(shade, 0, 0));
        using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
        var path = Path.Combine(dir, name);
        File.WriteAllBytes(path, data.ToArray());
        return path;
    }

    [Fact]
    public void APictureLetGoWaitsForTheSinksThatDrewItAndNoOther()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-retire-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var wasBudget = ImageCache.BudgetBytes;
        try
        {
            var p0 = Picture(dir, "a.png", 40);
            var p1 = Picture(dir, "b.png", 80);
            const long one = 64 * 64 * 4;
            ImageCache.BudgetBytes = one + 100;                                                          // room for one picture

            var drawer = RenderFence.Register();
            RenderFence.Advance(drawer);                                                                 // a sink's frame runs on this thread
            var first = ImageCache.Get(p0)!;                                                             // and draws the first picture: noted with the fetch

            var other = RenderFence.Register();
            RenderFence.Advance(other);                                                                  // another sink's frame runs now
            Assert.NotNull(ImageCache.Get(p1));                                                          // its picture evicts the first
            Assert.Equal(1, ImageCache.Count);
            Assert.Equal(1, RetiredFrames.CountOf(RetiredFrames.Kind.Picture));                          // which waits behind the fence
            Assert.Equal(one, RetiredFrames.BytesOf(RetiredFrames.Kind.Picture));
            Assert.Equal(one, ImageCache.GraveyardBytes);
            Assert.NotEqual(IntPtr.Zero, first.Handle);                                                  // not disposed: the drawer's frame may still read it

            RenderFence.Advance(other);                                                                  // the other sink moves on: it never drew the picture, nothing changes
            RetiredFrames.Sweep();
            Assert.Equal(1, RetiredFrames.Count);
            _now += Sec(1.5);                                                                            // time alone is nothing
            RetiredFrames.Sweep();
            Assert.Equal(1, RetiredFrames.Count);
            Assert.True(RetiredFrames.OldestMs >= 1500);

            RenderFence.Advance(drawer);                                                                 // the drawer's next frame: the draw is over
            RetiredFrames.Sweep();
            Assert.Equal(0, RetiredFrames.Count);
            Assert.Equal(IntPtr.Zero, first.Handle);                                                     // and only now is the picture gone
            Assert.Equal(0, RenderFence.HungFrames);
            Assert.Equal(-1, RetiredFrames.OldestMs);
            RenderFence.Unregister(drawer);
            RenderFence.Unregister(other);
        }
        finally
        {
            ImageCache.BudgetBytes = wasBudget;
            ImageCache.ClearForTests();
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void AFrameNobodyDrewGoesAtOnceAndOneAFrameDrewWaitsForThatFrameToClose()
    {
        RetiredFrames.ClearForTests();
        var loose = SKImage.Create(new SKImageInfo(4, 4));
        RetiredFrames.Retire(loose, new long[RenderFence.MaxSinks]);                                     // no frame ever fetched it: nothing could be drawing it
        Assert.Equal(0, RetiredFrames.Count);
        Assert.Equal(IntPtr.Zero, loose.Handle);

        var sink = RenderFence.Register("preview");
        RenderFence.BeginFrame(sink);
        var held = SKImage.Create(new SKImageInfo(4, 4));
        var table = new long[RenderFence.MaxSinks];
        RenderFence.Touch(table);                                                                        // the running frame fetched it
        RetiredFrames.Retire(held, table);                                                               // replaced while that frame is open
        Assert.Equal(1, RetiredFrames.CountOf(RetiredFrames.Kind.Frame));
        Assert.Equal(4 * 4 * 4, RetiredFrames.Bytes);
        Assert.NotEqual(IntPtr.Zero, held.Handle);
        RetiredFrames.Sweep();
        Assert.Equal(1, RetiredFrames.Count);

        // Time is nothing: two seconds on, the frame is open still — hung, and what it drew is in quarantine.
        _now += Sec(2.1);
        RetiredFrames.Sweep();
        Assert.Equal(1, RetiredFrames.Count);
        Assert.NotEqual(IntPtr.Zero, held.Handle);
        Assert.Equal(1, RenderFence.HungSinks);
        Assert.Equal(4 * 4 * 4, RetiredFrames.QuarantinedBytes);

        // The frame closes: released, and the record says the sink hung and recovered.
        RenderFence.EndFrame(sink);
        RetiredFrames.Sweep();
        Assert.Equal(0, RetiredFrames.Count);
        Assert.Equal(IntPtr.Zero, held.Handle);
        Assert.Equal(0, RetiredFrames.QuarantinedBytes);
        Assert.Equal(1, RenderFence.HungFrames);
        Assert.Collection(RenderFence.Faults,
            f => { Assert.Equal(FenceFaultKind.Hung, f.Kind); Assert.Equal("preview", f.Label); Assert.True(f.OpenMs >= 2000); },
            f => Assert.Equal(FenceFaultKind.Recovered, f.Kind));
        RenderFence.Unregister(sink);
    }
}
