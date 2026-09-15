using Patterns.Core.Media;
using Patterns.Rendering.Media;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The media memory in one view against one budget, the ladder's rungs at fixed shares, the
/// pools' honest bytes against their target, and the picture trim the ladder takes.
/// </summary>
public class MediaMemoryTests
{
    private const long MB = 1024L * 1024;

    [Fact]
    public void TheBudgetIsAShareOfTheAppCeilingByClass()
    {
        Assert.Equal((long)(1024 * 0.6 * MB), MediaMemory.BudgetBytes(4096));                            // a 4 GB machine: a 1 GB ceiling, 614 MB for media
        Assert.Equal((long)(2048 * 0.6 * MB), MediaMemory.BudgetBytes(8192));
        Assert.Equal((long)(3072 * 0.6 * MB), MediaMemory.BudgetBytes(16384));                           // the ceiling's cap: 1.8 GB
        Assert.Equal((long)(3072 * 0.6 * MB), MediaMemory.BudgetBytes(-1));                              // no reading: the cap
    }

    [Fact]
    public void TheRungsAreFixedSharesOfTheBudget()
    {
        const long budget = 1000 * MB;
        Assert.Equal(MemoryPressure.None, MediaMemory.LevelOf(699 * MB, budget));
        Assert.Equal(MemoryPressure.Elevated, MediaMemory.LevelOf(700 * MB, budget));
        Assert.Equal(MemoryPressure.Elevated, MediaMemory.LevelOf(849 * MB, budget));
        Assert.Equal(MemoryPressure.High, MediaMemory.LevelOf(850 * MB, budget));
        Assert.Equal(MemoryPressure.High, MediaMemory.LevelOf(999 * MB, budget));
        Assert.Equal(MemoryPressure.Critical, MediaMemory.LevelOf(1000 * MB, budget));
        Assert.Equal(MemoryPressure.Critical, MediaMemory.LevelOf(5000 * MB, budget));
        Assert.Equal(MemoryPressure.None, MediaMemory.LevelOf(5000 * MB, 0));                            // no budget: no rung
        Assert.Equal(CheckLight.Green, MediaMemory.Light(MemoryPressure.None));
        Assert.Equal(CheckLight.Amber, MediaMemory.Light(MemoryPressure.Elevated));
        Assert.Equal(CheckLight.Amber, MediaMemory.Light(MemoryPressure.High));
        Assert.Equal(CheckLight.Red, MediaMemory.Light(MemoryPressure.Critical));
        Assert.Equal("critical", MediaMemory.Word(MemoryPressure.Critical));
        Assert.Equal("", MediaMemory.Steps(MemoryPressure.None));
        Assert.Contains("pictures trimmed", MediaMemory.Steps(MemoryPressure.Elevated));
        Assert.Contains("pre-roll held back", MediaMemory.Steps(MemoryPressure.High));
        Assert.DoesNotContain("preview-only", MediaMemory.Steps(MemoryPressure.High));
        Assert.Contains("no new preview-only source opened", MediaMemory.Steps(MemoryPressure.Critical));
        Assert.Equal("no pressure", MediaMemory.LevelWords(MemoryPressure.None));
        Assert.StartsWith("HIGH pressure: ", MediaMemory.LevelWords(MemoryPressure.High));
    }

    [Fact]
    public void TheReadingAddsEveryPartAndSaysItsWords()
    {
        var r = new MediaMemory.Reading(Pictures: 200 * MB, PicturesRetiring: 20 * MB, Pools: 100 * MB, PoolsRetiring: 10 * MB, FramesRetiring: 8 * MB, Extra: 12 * MB, Budget: 1000 * MB);
        Assert.Equal(350 * MB, r.Total);
        Assert.Equal(0.35, r.Share, 3);
        Assert.Equal(MemoryPressure.None, r.Level);
        Assert.Equal("media 350 MB of 1000 MB (35 %) — no pressure", r.Words);
        Assert.Equal("pictures 200 MB (+20 MB retiring) · frame pools 100 MB (+10 MB retiring) · frames 8 MB retiring · decks and the rest 12 MB", r.Parts);
        Assert.Equal("nothing held", new MediaMemory.Reading(0, 0, 0, 0, 0, 0, 1000 * MB).Parts);
        Assert.Contains("(91 %) — HIGH pressure: retired swept", new MediaMemory.Reading(910 * MB, 0, 0, 0, 0, 0, 1000 * MB).Words);
    }

    [Fact]
    public void TheReadingComesFromTheCachesThePoolsAndWhatTheAppRegisters()
    {
        var wasExtra = MediaMemory.Extra;
        try
        {
            ImageCache.ClearForTests();
            var info = new SKImageInfo(4, 2, SKColorType.Bgra8888, SKAlphaType.Opaque);
            using var pool = new FramePool(info, 16, 4);
            MediaMemory.Extra = () => 5 * MB;
            var r = MediaMemory.Read(16384);
            Assert.Equal(pool.Bytes, r.Pools);
            Assert.Equal(5 * MB, r.Extra);
            Assert.Equal(MediaMemory.BudgetBytes(16384), r.Budget);
            Assert.Equal(MemoryPressure.None, r.Level);
            MediaMemory.Extra = () => throw new InvalidOperationException("no reading");
            Assert.Equal(0, MediaMemory.Read(16384).Extra);                                              // a reader that throws reads as nought
        }
        finally
        {
            MediaMemory.Extra = wasExtra;
        }
    }

    [Fact]
    public void ThePoolsSayWhenTheyPassTheirTarget()
    {
        const long target = 64 * MB;
        const long frame4K = 3840L * 2160 * 4;
        Assert.Equal(4, FramePool.BuffersFor(frame4K, target));                                          // the floor
        Assert.Equal(4 * frame4K, FramePool.EffectiveBytes(frame4K, target));                            // 126.6 MB: past the target, and said so
        Assert.True(FramePool.EffectiveBytes(frame4K, target) > target);
        Assert.Equal(8 * (1920L * 1080 * 4), FramePool.EffectiveBytes(1920L * 1080 * 4, target));        // eight 1080p frames: within it
        var info = new SKImageInfo(4, 2, SKColorType.Bgra8888, SKAlphaType.Opaque);
        using var small = new FramePool(info, 16, 4);
        Assert.Equal(0, FramePools.OverTarget(target));
        Assert.Equal(1, FramePools.OverTarget(100));                                                     // a target under the pool's own bytes

        var c = MemoryBudget.For(16384, 32, 4);
        var words = MemoryBudget.Describe(412, c, 3, 2, 0, 84 * MB, 253 * MB, 2, poolsOverTarget: 1, retiringDecoders: 1);
        Assert.Contains("frame pools 253 MB (2 sources; 1 over its 64 MB target)", words);
        Assert.Contains("decoders 2 of 4 (+1 retiring)", words);
        Assert.Contains("0 frames retiring", words);
        var plain = MemoryBudget.Describe(412, c, 3, 2, 0, 84 * MB, 116 * MB, 2);
        Assert.Contains("frame pools 116 MB (2 sources)", plain);
        Assert.DoesNotContain("over its", plain);
    }

    [Fact]
    public void TheTrimLetsPicturesGoLeastRecentlyDrawnFirstButNeverTheLastDrawn()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-trim-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var wasBudget = ImageCache.BudgetBytes;
        ImageCache.ClearForTests();
        RenderFence.ResetForTests();                                                                    // the premise: no frame open anywhere, none current on this thread
        try
        {
            var paths = new List<string>();
            for (var i = 0; i < 3; i++)
            {
                using var bmp = new SKBitmap(64, 64);
                bmp.Erase(new SKColor((byte)(i * 60), 0, 0));
                using var data = bmp.Encode(SKEncodedImageFormat.Png, 100);
                var path = Path.Combine(dir, $"t{i}.png");
                File.WriteAllBytes(path, data.ToArray());
                paths.Add(path);
            }
            const long one = 64 * 64 * 4;
            ImageCache.BudgetBytes = 10 * one;
            foreach (var p in paths) Assert.NotNull(ImageCache.Get(p));
            Assert.Equal(3, ImageCache.Count);
            Assert.Equal(2, ImageCache.TrimTo(one + 10));                                                // two let go, least recently drawn first
            Assert.Equal(1, ImageCache.Count);
            Assert.Equal(one, ImageCache.Bytes);
            Assert.Equal(0, ImageCache.TrimTo(0));                                                       // the last one drawn stays whatever the target
            Assert.Equal(1, ImageCache.Count);
            Assert.NotNull(ImageCache.Get(paths[2]));                                                    // and it is the newest: no decode needed (still resident)
            Assert.Equal(1, ImageCache.Count);
            Assert.Equal(0, ImageCache.GraveyardBytes);                                                  // off any frame, the trimmed went at once
        }
        finally
        {
            ImageCache.BudgetBytes = wasBudget;
            ImageCache.ClearForTests();
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }
}
