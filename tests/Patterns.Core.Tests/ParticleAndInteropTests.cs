using System.Runtime.InteropServices;
using Patterns.Core.Model;
using Patterns.Core.Ndi;
using Patterns.Core.Particles;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

public class ParticleSimTests
{
    private static ShowSnapshot Snap() => new() { State = new ShowState(), Version = 1 };

    private static ParticleOptions Options(int seed = 42) => new()
    {
        Count = 300,
        Seed = seed,
        Emitter = ParticleEmitter.TopEdge,
        SpeedMin = 40,
        SpeedMax = 120,
    };

    [Fact]
    public void SameSeedSameSteps_SamePositions()
    {
        using var a = new ParticleSim();
        using var b = new ParticleSim();
        var canvas = new SKSizeI(800, 450);
        a.Configure(Options(), Snap(), canvas);
        b.Configure(Options(), Snap(), canvas);

        for (var i = 0; i < 120; i++)
        {
            a.StepFixed(1f / 60f);
            b.StepFixed(1f / 60f);
        }

        Assert.Equal(a.Count, b.Count);
        for (var i = 0; i < a.Count; i += 7)
        {
            Assert.Equal(a.PositionOf(i), b.PositionOf(i));
        }
    }

    [Fact]
    public void ReconfigureWithSameOptionsDoesNotResetField()
    {
        using var sim = new ParticleSim();
        var canvas = new SKSizeI(800, 450);
        var opts = Options();
        sim.Configure(opts, Snap(), canvas);
        sim.StepFixed(0.5f);
        var pos = sim.PositionOf(11);

        sim.Configure(opts, Snap(), canvas); // identical config — must be a no-op
        Assert.Equal(pos, sim.PositionOf(11));

        opts.Seed = 43; // real change — field reseeds
        sim.Configure(opts, Snap(), canvas);
        Assert.NotEqual(pos, sim.PositionOf(11));
    }

    [Fact]
    public void ParticlesStayNearCanvasThroughLongRuns()
    {
        using var sim = new ParticleSim();
        var canvas = new SKSizeI(640, 360);
        sim.Configure(Options(), Snap(), canvas);
        for (var i = 0; i < 60 * 30; i++)
        {
            sim.StepFixed(1f / 60f);
        }
        for (var i = 0; i < sim.Count; i++)
        {
            var (x, y) = sim.PositionOf(i);
            Assert.InRange(x, -2000, canvas.Width + 2000);
            Assert.InRange(y, -2000, canvas.Height + 2000);
        }
    }
}

public class NdiInteropLayoutTests
{
    [Fact]
    public void StructSizesMatchNativeAbi()
    {
        if (!Environment.Is64BitProcess) return;
        // A marshalling regression here would silently corrupt NDI frames — pin the ABI.
        Assert.Equal(24, Marshal.SizeOf<NdiInterop.SendCreate>());
        Assert.Equal(72, Marshal.SizeOf<NdiInterop.VideoFrameV2>());
    }

    [Fact]
    public void VideoFrameFieldOffsetsMatchHeader()
    {
        Assert.Equal(0, (int)Marshal.OffsetOf<NdiInterop.VideoFrameV2>(nameof(NdiInterop.VideoFrameV2.Xres)));
        Assert.Equal(8, (int)Marshal.OffsetOf<NdiInterop.VideoFrameV2>(nameof(NdiInterop.VideoFrameV2.FourCc)));
        Assert.Equal(20, (int)Marshal.OffsetOf<NdiInterop.VideoFrameV2>(nameof(NdiInterop.VideoFrameV2.PictureAspectRatio)));
        Assert.Equal(32, (int)Marshal.OffsetOf<NdiInterop.VideoFrameV2>(nameof(NdiInterop.VideoFrameV2.Timecode)));
        Assert.Equal(40, (int)Marshal.OffsetOf<NdiInterop.VideoFrameV2>(nameof(NdiInterop.VideoFrameV2.Data)));
        Assert.Equal(48, (int)Marshal.OffsetOf<NdiInterop.VideoFrameV2>(nameof(NdiInterop.VideoFrameV2.LineStrideInBytes)));
        Assert.Equal(56, (int)Marshal.OffsetOf<NdiInterop.VideoFrameV2>(nameof(NdiInterop.VideoFrameV2.Metadata)));
        Assert.Equal(64, (int)Marshal.OffsetOf<NdiInterop.VideoFrameV2>(nameof(NdiInterop.VideoFrameV2.Timestamp)));
    }

    [Fact]
    public void FourCcIsLittleEndianBgrx()
        => Assert.Equal(0x58524742, NdiInterop.FourCcBgrx); // 'B','G','R','X'

    [Fact]
    public void Utf8AllocatorNullTerminates()
    {
        var ptr = NdiInterop.Utf8("Pattern Sender ✓");
        try
        {
            Assert.Equal("Pattern Sender ✓", Marshal.PtrToStringUTF8(ptr));
        }
        finally
        {
            Marshal.FreeHGlobal(ptr);
        }
    }
}

public class CadenceTests
{
    private static ShowSnapshot Snap(Action<ShowState> mutate)
    {
        var s = new ShowState();
        s.Overlays.Clock.Enabled = false;
        s.Countdown.Enabled = false;
        mutate(s);
        return new ShowSnapshot { State = s, Version = 1 };
    }

    [Fact]
    public void StaticPatternIsStatic()
        => Assert.Equal(RedrawCadence.Static, PatternEngine.CadenceOf(Snap(s => s.Pattern.Kind = PatternKind.Grid), null, DateTime.UtcNow));

    [Fact]
    public void ClockNeedsPerSecond()
        => Assert.Equal(RedrawCadence.PerSecond, PatternEngine.CadenceOf(Snap(s =>
        {
            s.Pattern.Kind = PatternKind.Grid;
            s.Overlays.Clock.Enabled = true;
        }), null, DateTime.UtcNow));

    [Fact]
    public void MotionIsContinuous()
        => Assert.Equal(RedrawCadence.Continuous, PatternEngine.CadenceOf(Snap(s => s.Pattern.Kind = PatternKind.Motion), null, DateTime.UtcNow));

    [Fact]
    public void AnimatedCheckerIsContinuous()
        => Assert.Equal(RedrawCadence.Continuous, PatternEngine.CadenceOf(Snap(s =>
        {
            s.Pattern.Kind = PatternKind.Checkerboard;
            s.Pattern.Checker.Animate = true;
        }), null, DateTime.UtcNow));

    [Fact]
    public void IdentifyForcesContinuous()
        => Assert.Equal(RedrawCadence.Continuous, PatternEngine.CadenceOf(Snap(s =>
        {
            s.Pattern.Kind = PatternKind.Grid;
            s.IdentifyUntilUtc = DateTime.UtcNow.AddSeconds(3);
        }), null, DateTime.UtcNow));

    [Fact]
    public void BlackoutIsStaticEvenWithMotion()
        => Assert.Equal(RedrawCadence.Static, PatternEngine.CadenceOf(Snap(s =>
        {
            s.Pattern.Kind = PatternKind.Motion;
            s.Blackout = true;
        }), null, DateTime.UtcNow));

    [Fact]
    public void IndependentScreenGetsItsOwnCadence()
    {
        var snap = Snap(s =>
        {
            s.Pattern.Kind = PatternKind.Grid;
            s.Output.Mode = OutputMode.Independent;
            var a = new OutputAssignment { ScreenId = "s1" };
            a.Pattern.Kind = PatternKind.Motion;
            s.Independent.Add(a);
        });
        Assert.Equal(RedrawCadence.Continuous, PatternEngine.CadenceOf(snap, "s1", DateTime.UtcNow));
        Assert.Equal(RedrawCadence.Static, PatternEngine.CadenceOf(snap, null, DateTime.UtcNow));
    }
}
