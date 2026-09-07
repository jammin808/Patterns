using Patterns.Core.Model;
using Patterns.Core.Particles;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 18: the particles given the treatments the fractals had, where they were missing — one
/// sim per field on a sink so a crossfade or a monitor wall never re-seeds in the hot path, a
/// catch-up bounded per frame, the quality ladder on the draw alone, a late sink joining a
/// running leader's timeline, a backwards clock that re-anchors instead of freezing, and the
/// dispose and NaN fences.
/// </summary>
public class ParticleResilienceTests
{
    private static ShowSnapshot Snap(long version = 1) => new() { State = new ShowState(), Version = version };

    private static ParticleOptions Options(int count = 300, int seed = 5) => new()
    {
        Count = count,
        Seed = seed,
        Emitter = ParticleEmitter.TopEdge,
        SpeedMin = 40,
        SpeedMax = 120,
    };

    private static long Target(double seconds) => (long)(seconds / ParticleSim.StepSeconds);

    private static void AssertSameField(ParticleSim a, ParticleSim b, int stride = 3)
    {
        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i += stride)
        {
            Assert.Equal(a.PositionOf(i), b.PositionOf(i));
            Assert.Equal(a.AgeOf(i), b.AgeOf(i));
        }
    }

    [Fact]
    public void OneSinkKeepsASimPerFieldAndNeverReconfiguresInTheHotPath()
    {
        using var cache = new ParticleSimCache();
        var snap = Snap(1);
        var o = Options();
        var big = new SKSizeI(800, 450);
        var tile = new SKSizeI(400, 225);
        var a = cache.Get(o, snap, big);
        var b = cache.Get(o, snap, tile);            // a monitor wall's tile of the same scene: another field
        Assert.NotSame(a, b);
        Assert.Equal(2, cache.Count);
        a.Advance(1.0);
        b.Advance(1.0);
        var pa = a.PositionOf(3);
        Assert.Same(a, cache.Get(o, snap, big));
        Assert.Same(b, cache.Get(o, snap, tile));
        Assert.Equal(pa, a.PositionOf(3));           // found, not rebuilt

        // A publish with the same scene: the same sim with its field kept, and both versions
        // found after that — a crossfade draws the old snapshot under the new every frame.
        var next = Snap(2);
        Assert.Same(a, cache.Get(o, next, big));
        Assert.Equal(pa, a.PositionOf(3));
        Assert.Same(a, cache.Get(o, snap, big));
        Assert.Same(a, cache.Get(o, next, big));
        Assert.Equal(2, cache.Count);
        Assert.Same(a, cache.Latest);

        // Another scene at the same version and canvas is a field of its own; past the capacity the least recently drawn goes.
        var c = cache.Get(Options(seed: 9), next, big);
        Assert.NotSame(a, c);
        Assert.Equal(3, cache.Count);
        for (var seed = 10; seed < 10 + ParticleSimCache.Capacity; seed++) cache.Get(Options(seed: seed), next, big);
        Assert.Equal(ParticleSimCache.Capacity, cache.Count);
        Assert.True(a.IsDisposed && b.IsDisposed && c.IsDisposed, "the three oldest fields went");
        Assert.False(cache.Latest!.IsDisposed);
    }

    [Fact]
    public void ALateSinkJoinsTheLeadersTimelineAndStaysInStep()
    {
        var snap = Snap(3);
        var o = Options(count: 500);
        var canvas = new SKSizeI(1280, 720);
        using var early = new ParticleSimCache();
        using var late = new ParticleSimCache();
        var leader = early.Get(o, snap, canvas);
        leader.Advance(0.5);
        leader.Advance(12.0);
        Assert.Equal(Target(12.0), leader.StepsDone);
        Assert.Same(leader, snap.ParticleLeaders.Leader(leader.ConfigKey));

        var joiner = late.Get(o, snap, canvas);      // an output opened twelve seconds after the PGM pane started the field
        Assert.NotSame(leader, joiner);
        Assert.Equal(leader.StepsDone, joiner.StepsDone);
        AssertSameField(leader, joiner);
        joiner.Advance(12.0);                        // its first frame has nothing to catch up
        leader.Advance(12.4);
        joiner.Advance(12.4);
        AssertSameField(leader, joiner);             // in step from then on, respawns and all
        leader.Advance(20.0);
        joiner.Advance(20.0);
        AssertSameField(leader, joiner);

        // A sink under a snapshot nobody has drawn the field under anchors on its own.
        var fresh = Snap(4);
        using var alone = new ParticleSimCache();
        var solo = alone.Get(o, fresh, canvas);
        solo.Advance(20.0);
        Assert.Same(solo, fresh.ParticleLeaders.Leader(solo.ConfigKey));
        Assert.NotEqual(leader.PositionOf(3), solo.PositionOf(3));

        // A disposed leader leads no one: the next sim to start the field takes the lead.
        leader.Dispose();
        using var another = new ParticleSimCache();
        var third = another.Get(o, snap, canvas);
        Assert.Same(third, snap.ParticleLeaders.Leader(third.ConfigKey));
    }

    [Fact]
    public void ACatchUpIsBoundedPerFrameAndAHopelessOneReanchors()
    {
        using var sim = new ParticleSim();
        sim.Configure(Options(count: 20000), Snap(), new SKSizeI(1920, 1080));
        Assert.Equal(150, sim.StepsPerFrame);        // three million updates a frame, 20 000 particles
        sim.Advance(0.1);
        Assert.Equal(Target(0.1), sim.StepsDone);
        sim.Advance(10.0);                           // ten seconds behind: one frame does its share
        Assert.Equal(Target(0.1) + 150, sim.StepsDone);
        var frames = 0;
        while (sim.StepsDone < Target(10.0) && frames++ < 20) sim.Advance(10.0);
        Assert.Equal(Target(10.0), sim.StepsDone);
        Assert.InRange(frames, 6, 8);
        // Hopelessly behind: re-anchored on the quantised grid near the clock rather than ground through.
        sim.Advance(40.0);
        Assert.Equal(Target(40.0) / 512 * 512 + 150, sim.StepsDone);

        // A small field catches up whole in one frame: the budget is in updates, not steps.
        using var small = new ParticleSim();
        small.Configure(Options(count: 300), Snap(), new SKSizeI(800, 450));
        Assert.Equal((int)ParticleSim.MaxBehindSteps, small.StepsPerFrame);
        small.Advance(0.1);
        small.Advance(15.0);
        Assert.Equal(Target(15.0), small.StepsDone);
    }

    [Fact]
    public void TheLadderHidesParticlesAndNeverStopsThem()
    {
        using var full = new ParticleSim();
        using var low = new ParticleSim();
        var o = Options(count: 400);
        var canvas = new SKSizeI(800, 450);
        full.Configure(o, Snap(), canvas);
        low.Configure(o, Snap(), canvas);
        low.Quality = 0.35;
        Assert.Equal(140, low.ActiveCount);
        Assert.Equal(400, full.ActiveCount);
        for (var t = 0.1; t < 6; t += 1.0 / 60) { full.Advance(t); low.Advance(t); }
        AssertSameField(full, low);                  // the hidden particles moved too: a step up shows them where they should be
        using var surface = SKSurface.Create(new SKImageInfo(320, 180, SKColorType.Bgra8888, SKAlphaType.Premul));
        var paints = new PaintCache();
        low.Render(surface.Canvas, paints);          // the active share through its own arrays
        full.Render(surface.Canvas, paints);         // the whole field
        low.Quality = 1;
        AssertSameField(full, low);
    }

    [Fact]
    public void ABackwardsClockReanchorsInsteadOfFreezing()
    {
        using var sim = new ParticleSim();
        sim.Configure(Options(), Snap(), new SKSizeI(800, 450));
        sim.Advance(5.0);
        Assert.Equal(Target(5.0), sim.StepsDone);
        sim.Advance(1.0);                            // the designer scrubbed back to one second
        Assert.Equal(Target(1.0), sim.StepsDone);
        var p = sim.PositionOf(4);
        sim.Advance(1.5);
        Assert.Equal(Target(1.5), sim.StepsDone);
        Assert.NotEqual(p, sim.PositionOf(4));       // still moving; a frozen field was the old behaviour
    }

    [Fact]
    public void ADisposedSimDrawsNothingAndAPoisonedParticleIsBornAgain()
    {
        var sim = new ParticleSim();
        sim.Configure(Options(), Snap(), new SKSizeI(800, 450));
        sim.StepFixed(float.PositiveInfinity);       // every particle poisoned by one step — and born again by the fence
        for (var i = 0; i < sim.Count; i++)
        {
            var (x, y) = sim.PositionOf(i);
            Assert.True(float.IsFinite(x) && float.IsFinite(y), $"particle {i} at {x},{y}");
            var (vx, vy) = sim.VelocityOf(i);
            Assert.True(float.IsFinite(vx) && float.IsFinite(vy));
        }
        sim.Advance(1.0);
        var done = sim.StepsDone;
        sim.Dispose();
        Assert.True(sim.IsDisposed);
        using var surface = SKSurface.Create(new SKImageInfo(64, 36, SKColorType.Bgra8888, SKAlphaType.Premul));
        sim.Render(surface.Canvas, new PaintCache()); // nothing, never a freed handle
        sim.Advance(2.0);
        Assert.Equal(done, sim.StepsDone);
        sim.Configure(Options(seed: 8), Snap(), new SKSizeI(800, 450));
        Assert.Equal(done, sim.StepsDone);           // a disposed sim never rebuilds
    }

    [Fact]
    public void TheRandomStreamIsTheSimsOwnAndAJoinCarriesIt()
    {
        var canvas = new SKSizeI(800, 450);
        using var a = new ParticleSim();
        using var b = new ParticleSim();
        a.Configure(Options(seed: 77), Snap(), canvas);
        b.Configure(Options(seed: 77), Snap(), canvas);
        for (var i = 0; i < 600; i++) { a.StepFixed(1f / 60f); b.StepFixed(1f / 60f); }
        AssertSameField(a, b, 1);                    // bit for bit through hundreds of respawns
        using var c = new ParticleSim();
        c.Configure(Options(seed: 78), Snap(), canvas);
        for (var i = 0; i < 600; i++) c.StepFixed(1f / 60f);
        Assert.NotEqual(a.PositionOf(1), c.PositionOf(1));

        Assert.False(c.JoinTimeline(a));             // another field
        using var d = new ParticleSim();
        d.Configure(Options(seed: 77), Snap(), canvas);
        Assert.False(d.JoinTimeline(a));             // a leader that has not started its clock
        Assert.False(a.JoinTimeline(a));
        a.Advance(3.0);
        Assert.True(d.JoinTimeline(a));
        Assert.Equal(a.StepsDone, d.StepsDone);
        AssertSameField(a, d, 1);
        a.Advance(3.5);
        d.Advance(3.5);
        AssertSameField(a, d, 1);                    // the random stream came with the field
    }
}
