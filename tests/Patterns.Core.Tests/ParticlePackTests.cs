using Patterns.Core.Model;
using Patterns.Core.Particles;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>A drifting field covers the whole screen; the packs apply and render; the classics are untouched.</summary>
public class ParticleCoverageTests
{
    /// <summary>A 4×4 grid of how often particles were seen in each cell over 30 s, as a share of the mean cell, after the pre-warmed field has flowed through.</summary>
    internal static double[,] Coverage(string preset, double wind, int seed = 7, int w = 1920, int h = 1080)
    {
        using var sim = new ParticleSim();
        var o = new ParticleOptions();
        ParticlePresets.Apply(preset, o);
        o.WindX = wind;
        o.Seed = seed;
        sim.Configure(o, RenderTestHarness.Snap(new ShowState()), new SKSizeI(w, h));
        var cells = new int[4, 4];
        var steps = (int)(30 / ParticleSim.StepSeconds);
        for (var s = 0; s < steps; s++)
        {
            sim.StepFixed(ParticleSim.StepSeconds);
            if (s < 10 * 120 || s % 60 != 0) continue;
            for (var i = 0; i < sim.Count; i++)
            {
                var (x, y) = sim.PositionOf(i);
                if (x < 0 || y < 0 || x >= w || y >= h) continue;
                cells[(int)(x / (w / 4.0)), (int)(y / (h / 4.0))]++;
            }
        }
        var total = 0.0;
        foreach (var c in cells) total += c;
        var mean = Math.Max(1, total / 16);
        var share = new double[4, 4];
        for (var cx = 0; cx < 4; cx++)
        {
            for (var cy = 0; cy < 4; cy++) share[cx, cy] = cells[cx, cy] / mean;
        }
        return share;
    }

    [Theory]
    [InlineData("Snow", 40)]
    [InlineData("Snow", -40)]
    [InlineData("Confetti", 25)]
    [InlineData("Autumn leaves", 35)]
    public void ADriftingEdgeFieldCoversEveryCellOfTheScreen(string preset, double wind)
    {
        var share = Coverage(preset, wind);
        for (var cy = 0; cy < 4; cy++)
        {
            for (var cx = 0; cx < 4; cx++)
            {
                Assert.True(share[cx, cy] >= 0.3, $"{preset} with wind {wind}: cell {cx},{cy} holds {share[cx, cy]:0.00} of the mean");
            }
        }
    }

    [Fact]
    public void AFieldWithoutDriftIsBornOnItsEdgeAsBefore()
    {
        var o = new ParticleOptions();
        ParticlePresets.Apply("Snow", o);
        o.WindX = 0;
        Assert.Equal(0, EdgeFlux.Estimate(o, 1920, 1080).SideFraction);
        Assert.Equal(EdgeFlux.None, EdgeFlux.Estimate(new ParticleOptions { Emitter = ParticleEmitter.FullArea, WindX = 100 }, 1920, 1080));
        Assert.Equal(EdgeFlux.None, EdgeFlux.Estimate(new ParticleOptions { Emitter = ParticleEmitter.Center, WindX = 100 }, 1920, 1080));
        var share = Coverage("Snow", 0);
        for (var cy = 0; cy < 4; cy++)
        {
            for (var cx = 0; cx < 4; cx++) Assert.InRange(share[cx, cy], 0.5, 1.6);
        }
    }

    [Fact]
    public void TheSideShareFollowsTheDriftAndTheCrossingTimeIsRight()
    {
        var o = new ParticleOptions();
        ParticlePresets.Apply("Snow", o);
        o.WindX = 40;
        var right = EdgeFlux.Estimate(o, 1920, 1080);
        Assert.InRange(right.SideFraction, 0.1, EdgeFlux.MaxSideFraction);
        Assert.True(right.FromLeft);
        Assert.True(right.AccelShare > 0.95, "straight down at birth: every bit of the drift is the wind's");
        Assert.InRange(right.Cross, 10, 30);

        o.WindX = -40;
        Assert.False(EdgeFlux.Estimate(o, 1920, 1080).FromLeft);

        o.WindX = 0;
        o.DirectionDeg = 45; // slanted at birth, no wind: all of the drift is there from the start
        var slanted = EdgeFlux.Estimate(o, 1920, 1080);
        Assert.True(slanted.SideFraction > 0.1);
        Assert.True(slanted.FromLeft);
        Assert.Equal(0, slanted.AccelShare);

        var embers = new ParticleOptions();
        ParticlePresets.Apply("Embers", embers); // rises from the bottom, wind to the right
        var rising = EdgeFlux.Estimate(embers, 1920, 1080);
        Assert.True(rising.SideFraction >= EdgeFlux.MinSideFraction, $"embers side share {rising.SideFraction:0.000}"); // a gentle drift: a small, real share
        Assert.True(rising.FromLeft);
        Assert.True(rising.V0 > 0 && rising.A > 0, "along the flow, up is positive");

        // A wider canvas needs a smaller side share than a taller one for the same drift.
        o.WindX = 40;
        o.DirectionDeg = 90;
        Assert.True(EdgeFlux.Estimate(o, 3840, 1080).SideFraction < EdgeFlux.Estimate(o, 1920, 1080).SideFraction);

        Assert.Equal(5f, EdgeFlux.TimeToTravel(100, 0, 500));
        Assert.Equal(10f, EdgeFlux.TimeToTravel(0, 10, 500), 3);
        Assert.Equal(float.PositiveInfinity, EdgeFlux.TimeToTravel(100, -10, 5000));   // decelerates to a stop first
        Assert.Equal(float.PositiveInfinity, EdgeFlux.TimeToTravel(0, 0, 100));
        Assert.Equal(11.708f, EdgeFlux.TimeToTravel(-50, 10, 100), 2);              // thrown back first, then falls through
        Assert.Equal(0f, EdgeFlux.TimeToTravel(100, 0, 0));
    }

    [Fact]
    public void TheSimStaysDeterministicAcrossSinksWithDrift()
    {
        using var a = new ParticleSim();
        using var b = new ParticleSim();
        var o = new ParticleOptions();
        ParticlePresets.Apply("Confetti", o);
        var canvas = new SKSizeI(1280, 720);
        a.Configure(o, RenderTestHarness.Snap(new ShowState()), canvas);
        b.Configure(o, RenderTestHarness.Snap(new ShowState()), canvas);
        for (var i = 0; i < 2400; i++)
        {
            a.StepFixed(ParticleSim.StepSeconds);
            b.StepFixed(ParticleSim.StepSeconds);
        }
        for (var i = 0; i < a.Count; i += 5) Assert.Equal(a.PositionOf(i), b.PositionOf(i));
    }
}

public class ParticlePackTests
{
    [Fact]
    public void ThePacksAreFiledInOrderWithUniqueNamesAndTheClassicsComeFirst()
    {
        Assert.Equal(new[] { "Classic", "Awards", "Modern", "Nature", "Moods", "Starcloth", "Night sky", "Feel-good" }, ParticlePresets.Categories);
        Assert.Equal(new[] { "Snow", "Confetti", "Starfield", "Rain", "Bokeh", "Embers", "Fireflies" }, ParticlePresets.Names.Take(7));
        Assert.Equal(ParticlePresets.Names.Length, ParticlePresets.Names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.True(ParticlePresets.Packs.Count >= 30);
        Assert.All(ParticlePresets.Categories, c => Assert.True(ParticlePresets.In(c).Count() >= 3, c));
        Assert.Contains("Fireworks", ParticlePresets.Names);
        Assert.Contains("Starcloth", ParticlePresets.Names);
    }

    [Fact]
    public void TheClassicsSetWhatTheyAlwaysDid()
    {
        var o = new ParticleOptions();
        ParticlePresets.Apply("Snow", o);
        Assert.Equal((ParticleEmitter.TopEdge, ParticleShape.Circle, 900, 1.5, 5.0, 30.0, 95.0, 90.0, 18.0, 8.0, 12.0, 0.7, false),
            (o.Emitter, o.Shape, o.Count, o.SizeMin, o.SizeMax, o.SpeedMin, o.SpeedMax, o.DirectionDeg, o.SpreadDeg, o.GravityY, o.WindX, o.Wobble, o.Glow));
        Assert.Equal("#FFFFFF,#E8F2FF", o.ColorsCsv);
        Assert.Equal("Snow", o.Preset);
        ParticlePresets.Apply("Rain", o);
        Assert.Equal((ParticleShape.Streak, 1400, 900.0, 1400.0, 83.0, -40.0), (o.Shape, o.Count, o.SpeedMin, o.SpeedMax, o.DirectionDeg, o.WindX));
        o.BackgroundColor = "#123456";
        o.UseBrandColors = true;
        ParticlePresets.Apply("Embers", o);
        Assert.Equal("#123456", o.BackgroundColor);   // the operator's, never a scene's
        Assert.True(o.UseBrandColors);
        ParticlePresets.Apply("no such scene", o);
        Assert.Equal(ParticleEmitter.TopEdge, o.Emitter); // unknown = Snow, as it always was
        Assert.Equal(900, o.Count);
        Assert.Equal("no such scene", o.Preset);
    }

    [Fact]
    public void EveryPackAppliesAndRendersClean()
    {
        foreach (var pack in ParticlePresets.Packs)
        {
            var state = new ShowState();
            state.Pattern.Kind = PatternKind.Particles;
            ParticlePresets.Apply(pack.Name, state.Pattern.Particles);
            Assert.Equal(pack.Name, state.Pattern.Particles.Preset);
            Assert.True(state.Pattern.Particles.SizeMax >= state.Pattern.Particles.SizeMin, pack.Name);
            Assert.True(state.Pattern.Particles.SpeedMax >= state.Pattern.Particles.SpeedMin, pack.Name);
            using var bmp = RenderTestHarness.Render(state, 320, 180, time: 2.0);
            Assert.NotNull(bmp);
            Assert.Equal(320, bmp.Width);
        }
    }

    [Fact]
    public void AStarclothStaysWhereItIs()
    {
        using var sim = new ParticleSim();
        var o = new ParticleOptions();
        ParticlePresets.Apply("Starcloth", o);
        sim.Configure(o, RenderTestHarness.Snap(new ShowState()), new SKSizeI(1920, 1080));
        var before = Enumerable.Range(0, sim.Count).Select(sim.PositionOf).ToArray();
        for (var i = 0; i < 1200; i++) sim.StepFixed(ParticleSim.StepSeconds); // ten seconds
        for (var i = 0; i < sim.Count; i += 9)
        {
            var (x0, y0) = before[i];
            var (x1, y1) = sim.PositionOf(i);
            Assert.True(Math.Abs(x1 - x0) + Math.Abs(y1 - y0) < 120, $"star {i} moved {Math.Abs(x1 - x0) + Math.Abs(y1 - y0):0} px");
        }
    }
}
