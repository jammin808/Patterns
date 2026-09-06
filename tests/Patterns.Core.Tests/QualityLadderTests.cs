using Patterns.Core.Effects;
using Patterns.Core.Model;
using Patterns.Core.Particles;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The adaptive quality ladder — three slow seconds down, thirty clean seconds up, the modes that
/// lock a level, what each level does to particles, iterations and the CPU raster, its words —
/// and the memory ceilings in numbers with their rows on the super-check.
/// </summary>
public class QualityLadderTests
{
    private static readonly DateTime T0 = new(2026, 9, 6, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void ThreeSlowSecondsStepDownAndThirtyCleanOnesStepBack()
    {
        var ladder = new QualityLadder();
        Assert.Equal(0, ladder.Level);
        Assert.Equal(1.0, ladder.Factor);

        Assert.False(ladder.Observe(30, "Output 1 (Main)", T0));
        Assert.False(ladder.Observe(30, "Output 1 (Main)", T0.AddSeconds(1)));
        Assert.Equal(0, ladder.Level);                                        // two slow seconds are a moment
        Assert.True(ladder.Observe(30, "Output 1 (Main)", T0.AddSeconds(2)));
        Assert.Equal(1, ladder.Level);
        Assert.Equal(0.7, ladder.Factor);
        Assert.Equal(1, ladder.StepsDown);
        Assert.Contains("Output 1 (Main)", ladder.Cause);
        Assert.Equal(T0.AddSeconds(2), ladder.ChangedUtc);

        // A clean second breaks a slow run: two slow, one clean, two slow is no step.
        ladder.Observe(30, "Output 1 (Main)", T0);
        ladder.Observe(30, "Output 1 (Main)", T0);
        ladder.Observe(4, "Output 1 (Main)", T0);
        ladder.Observe(30, "Output 1 (Main)", T0);
        ladder.Observe(30, "Output 1 (Main)", T0);
        Assert.Equal(1, ladder.Level);

        // Twenty-nine clean seconds hold; the thirtieth steps back.
        for (var i = 0; i < 29; i++) Assert.False(ladder.Observe(9, "Output 1 (Main)", T0.AddSeconds(10 + i)));
        Assert.Equal(1, ladder.Level);
        Assert.True(ladder.Observe(9, "Output 1 (Main)", T0.AddSeconds(40)));
        Assert.Equal(0, ladder.Level);
        Assert.Contains("30 clean seconds", ladder.Cause);
        for (var i = 0; i < 60; i++) ladder.Observe(9, "Output 1 (Main)", T0);
        Assert.Equal(0, ladder.Level);                                        // never below full

        // Nine slow seconds reach the bottom; more never go past it.
        for (var i = 0; i < 9; i++) ladder.Observe(60, "Output 2", T0);
        Assert.Equal(QualityLadder.Lowest, ladder.Level);
        Assert.Equal(0.35, ladder.Factor);
        for (var i = 0; i < 9; i++) Assert.False(ladder.Observe(60, "Output 2", T0));
        Assert.Equal(QualityLadder.Lowest, ladder.Level);
        Assert.Equal(4, ladder.StepsDown);
    }

    [Fact]
    public void AModeLocksALevelAndAutoTakesOverFromWhereItIs()
    {
        var ladder = new QualityLadder();
        Assert.True(ladder.SetMode(QualityMode.Full));
        Assert.False(ladder.SetMode(QualityMode.Full));
        for (var i = 0; i < 10; i++) Assert.False(ladder.Observe(90, "Output 1", T0));
        Assert.Equal(0, ladder.Level);                                        // Full never steps

        Assert.True(ladder.SetMode(QualityMode.Economy));
        Assert.Equal(2, ladder.Level);
        Assert.Equal(0.5, ladder.Factor);
        Assert.Equal("locked on the Machine page", ladder.Cause);
        for (var i = 0; i < 60; i++) ladder.Observe(2, "Output 1", T0);
        Assert.Equal(2, ladder.Level);                                        // locked: clean seconds change nothing

        Assert.True(ladder.SetMode(QualityMode.Balanced));
        Assert.Equal(1, ladder.Level);

        Assert.True(ladder.SetMode(QualityMode.Auto));
        Assert.Equal(1, ladder.Level);                                        // Auto starts from where the lock left it
        for (var i = 0; i < 30; i++) ladder.Observe(2, "Output 1", T0);
        Assert.Equal(0, ladder.Level);

        Assert.Equal(0, QualityLadder.LockedLevel(QualityMode.Full));
        Assert.Equal(1, QualityLadder.LockedLevel(QualityMode.Balanced));
        Assert.Equal(2, QualityLadder.LockedLevel(QualityMode.Economy));
        Assert.Equal(-1, QualityLadder.LockedLevel(QualityMode.Auto));

        ladder.Reset();
        Assert.Equal(QualityMode.Auto, ladder.Mode);
        Assert.Equal(0, ladder.Level);
        Assert.Equal(0, ladder.StepsDown);
    }

    [Fact]
    public void EachLevelScalesTheEffectsWithFloors()
    {
        Assert.Equal(1.0, QualityLadder.FactorOf(0));
        Assert.Equal(0.7, QualityLadder.FactorOf(1));
        Assert.Equal(0.5, QualityLadder.FactorOf(2));
        Assert.Equal(0.35, QualityLadder.FactorOf(3));
        Assert.Equal(0.35, QualityLadder.FactorOf(9));
        Assert.Equal(700, QualityLadder.Particles(1000, 0.7));
        Assert.Equal(1, QualityLadder.Particles(1, 0.35));
        Assert.Equal(32, QualityLadder.Iterations(64, 0.5));
        Assert.Equal(8, QualityLadder.Iterations(10, 0.35));
        Assert.Equal(1, QualityLadder.RasterScale(1));
        Assert.Equal(0.707, QualityLadder.RasterScale(0.5), 3);
        Assert.Equal(0.592, QualityLadder.RasterScale(0.35), 3);
        Assert.Equal(0.5, QualityLadder.RasterScale(0.01));
        Assert.Equal("70%", QualityLadder.Percent(0.7));
        Assert.Equal("35%", QualityLadder.Percent(0.35));

        var o = new FractalOptions { Iterations = 64 };
        Assert.Equal(64, FractalView.Of(o, 1, AudioLevelFrame.Zero).Iterations);
        Assert.Equal(32, FractalView.Of(o, 1, AudioLevelFrame.Zero, quality: 0.5).Iterations);
        Assert.Equal(8, FractalView.Of(new FractalOptions { Iterations = 12 }, 1, AudioLevelFrame.Zero, quality: 0.35).Iterations);
        Assert.Equal(new SKSizeI(240, 135), FractalRaster.SizeFor(FractalQuality.Balanced, new SKSizeI(1920, 1080)));
        Assert.Equal(new SKSizeI(120, 68), FractalRaster.SizeFor(FractalQuality.Balanced, new SKSizeI(1920, 1080), 0.5));
        Assert.Equal(new SKSizeI(32, 18), FractalRaster.SizeFor(FractalQuality.Fast, new SKSizeI(1920, 1080), 0.05));   // never under 32 wide
    }

    [Fact]
    public void TheParticleFieldFollowsTheQualityWithoutReseeding()
    {
        var state = RenderTestHarness.State();
        var snap = RenderTestHarness.Snap(state);
        using var sim = new ParticleSim();
        var o = new ParticleOptions { Count = 200, Seed = 7 };
        sim.Configure(o, snap, new SKSizeI(320, 180));
        Assert.Equal(200, sim.Count);
        Assert.Equal(200, sim.ActiveCount);

        sim.Quality = 0.5;
        Assert.Equal(100, sim.ActiveCount);
        Assert.Equal(200, sim.Count);                                         // the field is not re-seeded, part of it rests
        sim.Quality = 0.35;
        Assert.Equal(70, sim.ActiveCount);
        sim.Quality = 0.001;
        Assert.Equal(0.05, sim.Quality);                                      // clamped
        Assert.Equal(10, sim.ActiveCount);
        sim.Quality = 1;
        Assert.Equal(200, sim.ActiveCount);

        // Both draw paths run: the whole field, then the active share through its own arrays.
        var info = new SKImageInfo(320, 180, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var paints = new Rendering.PaintCache();
        sim.Advance(1.0);
        sim.Render(surface.Canvas, paints);
        sim.Quality = 0.5;
        sim.Advance(1.5);
        sim.Render(surface.Canvas, paints);
        sim.Quality = 0.7;
        sim.Advance(2.0);
        sim.Render(surface.Canvas, paints);
        Assert.Equal(140, sim.ActiveCount);
    }

    [Fact]
    public void TheWordsReadTheModeTheLevelAndTheCause()
    {
        var ladder = new QualityLadder();
        Assert.StartsWith("Auto: full quality", ladder.Describe());
        for (var i = 0; i < 3; i++) ladder.Observe(40, "Output 1 (Main)", T0);
        var down = ladder.Describe();
        Assert.StartsWith("Auto, level 1 of 3:", down);
        Assert.Contains("70%", down);
        Assert.Contains("Output 1 (Main): frames past 25 ms for 3 s", down);
        Assert.Contains("back up a level after 30 clean seconds", down);
        for (var i = 0; i < 30; i++) ladder.Observe(4, "Output 1 (Main)", T0);
        Assert.Contains("Stepped down 1 time this session and came back", ladder.Describe());

        ladder.SetMode(QualityMode.Full);
        Assert.StartsWith("Full: locked at full quality", ladder.Describe());
        ladder.SetMode(QualityMode.Economy);
        var economy = ladder.Describe();
        Assert.StartsWith("Economy: locked at level 2 of 3", economy);
        Assert.Contains("50%", economy);
    }

    [Fact]
    public void TheMemoryCeilingsInNumbers()
    {
        var big = MemoryBudget.For(16384, 10, 4);
        Assert.Equal(3072, big.AppCeilingMB);                                 // a quarter of 16 GB, capped at 3 GB
        Assert.Equal(1024, MemoryBudget.For(4096, 10, 4).AppCeilingMB);
        Assert.Equal(512, MemoryBudget.For(1024, 10, 4).AppCeilingMB);        // the floor
        Assert.Equal(3072, MemoryBudget.For(-1, 10, 4).AppCeilingMB);         // no reading: the cap
        Assert.Equal(10, big.ImageCachePictures);
        Assert.Equal(4, big.DecoderCap);
        Assert.Equal(400, big.HeldFrameMs);

        Assert.Equal(CheckLight.Green, MemoryBudget.Light(412, big));
        Assert.Equal(CheckLight.Amber, MemoryBudget.Light(3200, big));
        Assert.Equal(CheckLight.Red, MemoryBudget.Light(4000, big));
        Assert.Equal(CheckLight.Grey, MemoryBudget.Light(-1, big));

        Assert.Equal("This app 412 MB of a 3.0 GB ceiling (16 GB machine) · pictures 3 of 10 cached · decoders 2 of 4 · 0 frames held for fades",
            MemoryBudget.Describe(412, big, 3, 2, 0));
        Assert.Equal("This app: no reading yet · ceiling 3.0 GB · 1 frame held for fades",
            MemoryBudget.Describe(-1, MemoryBudget.For(-1, 10, 4), -1, -1, 1));
        Assert.Equal("", MemoryBudget.Advice(412, big));
        Assert.Contains("over its ceiling", MemoryBudget.Advice(3200, big));
        Assert.Contains("far over its ceiling", MemoryBudget.Advice(4000, big));
        Assert.Equal("1.8 GB", MemoryBudget.Mb(1800));
        Assert.Equal("412 MB", MemoryBudget.Mb(412));
    }

    [Fact]
    public void TheSuperCheckRowsReadTheLadderAndTheCeiling()
    {
        Assert.DoesNotContain(SuperCheck.Run(new CheckFacts()).Rows, r => r.Item == "Quality ladder");
        Assert.DoesNotContain(SuperCheck.Run(new CheckFacts()).Rows, r => r.Item == "Memory ceiling");

        var full = SuperCheck.Run(new CheckFacts { QualityMode = QualityMode.Auto, QualityLevel = 0, QualityWords = "Auto: full quality" }).Rows.Single(r => r.Item == "Quality ladder");
        Assert.Equal(CheckLight.Green, full.Light);
        Assert.Equal("Auto: full", full.Value);
        Assert.Equal("Auto: full quality", full.Note);

        var one = SuperCheck.Run(new CheckFacts { QualityMode = QualityMode.Auto, QualityLevel = 1, QualityWords = "Auto, level 1 of 3" }).Rows.Single(r => r.Item == "Quality ladder");
        Assert.Equal(CheckLight.Green, one.Light);
        Assert.Equal("Auto: level 1 of 3 (70%)", one.Value);

        var two = SuperCheck.Run(new CheckFacts { QualityMode = QualityMode.Economy, QualityLevel = 2, QualityWords = "Economy" }).Rows.Single(r => r.Item == "Quality ladder");
        Assert.Equal(CheckLight.Amber, two.Light);
        Assert.Equal("Economy: level 2 of 3 (50%)", two.Value);
        Assert.Contains("well below what the show set", two.Note);

        var green = SuperCheck.Run(new CheckFacts { RamTotalMB = 16384, RamAppMB = 412, ImagesCached = 3, Decoders = 2, DecoderCap = 4, HeldFrames = 0 }).Rows.Single(r => r.Item == "Memory ceiling");
        Assert.Equal(CheckLight.Green, green.Light);
        Assert.StartsWith("This app 412 MB of a 3.0 GB ceiling", green.Value);
        Assert.Equal("", green.Note);

        var red = SuperCheck.Run(new CheckFacts { RamTotalMB = 16384, RamAppMB = 4000, ImagesCached = 10, Decoders = 4, HeldFrames = 2 }).Rows.Single(r => r.Item == "Memory ceiling");
        Assert.Equal(CheckLight.Red, red.Light);
        Assert.Contains("far over", red.Note);
        Assert.Contains("Quality ladder", SuperCheck.ToText(SuperCheck.Run(new CheckFacts { QualityWords = "x" })));
    }
}
