using Avalonia;
using Patterns.App.Rendering;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>Rotation and trim behaviour of the real output pipeline, rendered to raster.</summary>
public class RotationTrimPipelineTests
{
    private static SnapshotBus BusFor(Action<ShowState> setup)
    {
        var state = new ShowState();
        state.Pattern.Canvas.FollowOutput = true;
        setup(state);
        var bus = new SnapshotBus(state);
        bus.Publish(state);
        return bus;
    }

    private static SKBitmap RenderPipelineFrame(SnapshotBus bus, PipelineViewport viewport, int w, int h)
    {
        using var pipeline = new RenderPipeline(bus, viewport);
        var info = new SKImageInfo(w, h, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        pipeline.Render(surface.Canvas, w, h, renderScaling: 1.0);
        var bmp = new SKBitmap(info);
        surface.ReadPixels(info, bmp.GetPixels(), info.RowBytes, 0, 0);
        return bmp;
    }

    private static PipelineViewport Output() => new(SinkKind.Output, SKSizeI.Empty, default, null, 1, "test");

    private static SnapshotBus RampBus() => BusFor(s =>
    {
        s.Pattern.Kind = PatternKind.Ramp;
        s.Pattern.Ramp.Variant = RampVariant.GrayHorizontal;
        s.Pattern.Ramp.ShowMarkers = false;
    });

    [Fact]
    public void Rot90TurnsAHorizontalRampVertical()
    {
        // Portrait 200×300 window; the engine renders 300×200 content, blitted rotated.
        using var bmp = RenderPipelineFrame(RampBus(), Output() with { Rotation = OutputRotation.Rot90 }, 200, 300);

        var top = bmp.GetPixel(100, 8);
        var bottom = bmp.GetPixel(100, 292);
        Assert.True(bottom.Red > top.Red + 100, $"expected dark→bright downwards, got {top} → {bottom}");

        // Rows are uniform (the ramp axis maps to Y, not X).
        var left = bmp.GetPixel(20, 150);
        var right = bmp.GetPixel(180, 150);
        Assert.InRange(Math.Abs(left.Red - right.Red), 0, 6);
    }

    [Fact]
    public void Rot270RunsTheOtherWay()
    {
        using var bmp = RenderPipelineFrame(RampBus(), Output() with { Rotation = OutputRotation.Rot270 }, 200, 300);
        var top = bmp.GetPixel(100, 8);
        var bottom = bmp.GetPixel(100, 292);
        Assert.True(top.Red > bottom.Red + 100, $"expected bright→dark downwards, got {top} → {bottom}");
    }

    [Fact]
    public void Rot180FlipsTheRamp()
    {
        using var flipped = RenderPipelineFrame(RampBus(), Output() with { Rotation = OutputRotation.Rot180 }, 300, 200);
        Assert.True(flipped.GetPixel(8, 100).Red > flipped.GetPixel(292, 100).Red + 100);

        using var plain = RenderPipelineFrame(RampBus(), Output(), 300, 200);
        Assert.True(plain.GetPixel(292, 100).Red > plain.GetPixel(8, 100).Red + 100);
    }

    private static SnapshotBus WhiteBus() => BusFor(s =>
    {
        s.Pattern.Kind = PatternKind.FlatField;
        s.Pattern.FlatField.Color = "#FFFFFF";
        s.Pattern.FlatField.ShowLabel = false;
    });

    [Fact]
    public void BrightnessTrimDarkensTheWholeFrame()
    {
        using var bmp = RenderPipelineFrame(WhiteBus(), Output() with { BrightnessPct = 50 }, 160, 120);
        var c = bmp.GetPixel(80, 60);
        Assert.InRange(c.Red, 124, 131);
        Assert.InRange(c.Green, 124, 131);
        Assert.InRange(c.Blue, 124, 131);
    }

    [Fact]
    public void RedTrimOnlyPullsRed()
    {
        using var bmp = RenderPipelineFrame(WhiteBus(), Output() with { TrimRPct = 50 }, 160, 120);
        var c = bmp.GetPixel(80, 60);
        Assert.InRange(c.Red, 124, 131);
        Assert.Equal(255, c.Green);
        Assert.Equal(255, c.Blue);
    }

    [Fact]
    public void NeutralTrimsLeaveWhiteAlone()
    {
        using var bmp = RenderPipelineFrame(WhiteBus(), Output(), 160, 120);
        var c = bmp.GetPixel(80, 60);
        Assert.Equal(new SKColor(255, 255, 255), new SKColor(c.Red, c.Green, c.Blue));
    }

    [Fact]
    public void TrimsAndRotationComposeWithoutFaults()
    {
        using var bmp = RenderPipelineFrame(RampBus(),
            Output() with { Rotation = OutputRotation.Rot90, BrightnessPct = 50, Gamma = 1.4, TrimGPct = 80 }, 200, 300);
        // Still a downward ramp, just trimmed.
        Assert.True(bmp.GetPixel(100, 292).Red > bmp.GetPixel(100, 8).Red + 40);
    }
}

/// <summary>Portrait rotation in the arrangement math.</summary>
public class RotationArrangementTests
{
    private static ScreenInfo Info(string id, int w, int h)
        => new(id, id, new PixelRect(0, 0, w, h), 1.0, false, 0);

    [Fact]
    public void EffectiveSizeSwapsForPortraitRotations()
    {
        var info = Info("a", 1920, 1080);
        Assert.Equal(new SKSizeI(1920, 1080), OutputWindowManager.EffectiveSize(new ScreenPlacement { ScreenId = "a" }, info));
        Assert.Equal(new SKSizeI(1920, 1080), OutputWindowManager.EffectiveSize(new ScreenPlacement { ScreenId = "a", Rotation = OutputRotation.Rot180 }, info));
        Assert.Equal(new SKSizeI(1080, 1920), OutputWindowManager.EffectiveSize(new ScreenPlacement { ScreenId = "a", Rotation = OutputRotation.Rot90 }, info));
        Assert.Equal(new SKSizeI(1080, 1920), OutputWindowManager.EffectiveSize(new ScreenPlacement { ScreenId = "a", Rotation = OutputRotation.Rot270 }, info));
    }

    [Fact]
    public void RotatedScreenSpansWithItsPortraitFootprint()
    {
        var screens = new List<ScreenInfo> { Info("a", 1920, 1080), Info("b", 1920, 1080) };
        var placements = new[]
        {
            new ScreenPlacement { ScreenId = "a", X = 0, Y = 0, Rotation = OutputRotation.Rot90 }, // 1080×1920
            new ScreenPlacement { ScreenId = "b", X = 1080, Y = 0 },                               // flush right of it
        };

        var result = OutputWindowManager.BuildViewports(placements, screens);

        Assert.Equal(2, result.Count);
        foreach (var (_, vp) in result)
        {
            Assert.Equal(new SKSizeI(3000, 1920), vp.ReferenceSize); // one joined canvas
        }
        Assert.Equal(OutputRotation.Rot90, result.First(x => x.Screen.Id == "a").Viewport.Rotation);
        Assert.Equal(new SKPointI(1080, 0), result.First(x => x.Screen.Id == "b").Viewport.ViewportOrigin);
    }
}

/// <summary>Pure DSP checks on the tone generator.</summary>
public class ToneSampleProviderTests
{
    private static float[] Read(ToneSampleProvider p, int samples)
    {
        var buf = new float[samples * 2];
        p.Read(buf, 0, buf.Length);
        return buf;
    }

    [Fact]
    public void ProducesTheRequestedFrequency()
    {
        var p = new ToneSampleProvider { Frequency = 1000 };
        p.SetTargets(0.5f, 0.5f);
        Read(p, 4800); // settle past the attack ramp
        var buf = Read(p, 48000); // 1 second

        var crossings = 0;
        for (var i = 2; i < buf.Length; i += 2)
        {
            if ((buf[i - 2] <= 0 && buf[i] > 0) || (buf[i - 2] >= 0 && buf[i] < 0)) crossings++;
        }
        // 1 kHz for 1 s ≈ 2000 zero crossings.
        Assert.InRange(crossings, 1960, 2040);
    }

    [Fact]
    public void LevelLandsAtTheTargetRms()
    {
        var amp = ToneSampleProvider.DbToAmplitude(-18);
        var p = new ToneSampleProvider { Frequency = 440 };
        p.SetTargets(amp, amp);
        Read(p, 9600);
        var buf = Read(p, 48000);

        double sumL = 0, sumR = 0;
        for (var i = 0; i < buf.Length; i += 2)
        {
            sumL += buf[i] * buf[i];
            sumR += buf[i + 1] * buf[i + 1];
        }
        var rmsL = Math.Sqrt(sumL / (buf.Length / 2));
        var expected = amp / Math.Sqrt(2);
        Assert.InRange(rmsL, expected * 0.97, expected * 1.03);
        Assert.InRange(Math.Sqrt(sumR / (buf.Length / 2)), expected * 0.97, expected * 1.03);
    }

    [Fact]
    public void ChannelsAreIndependent()
    {
        var p = new ToneSampleProvider { Frequency = 440 };
        p.SetTargets(0.5f, 0f);
        Read(p, 9600);
        var buf = Read(p, 24000);

        double maxL = 0, maxR = 0;
        for (var i = 0; i < buf.Length; i += 2)
        {
            maxL = Math.Max(maxL, Math.Abs(buf[i]));
            maxR = Math.Max(maxR, Math.Abs(buf[i + 1]));
        }
        Assert.True(maxL > 0.45);
        Assert.True(maxR < 0.001);
    }

    [Fact]
    public void ReleaseRampsDownWithoutResidue()
    {
        var p = new ToneSampleProvider { Frequency = 1000 };
        p.SetTargets(0.8f, 0.8f);
        Read(p, 9600);
        p.SetTargets(0, 0);
        Read(p, 9600); // 200 ms of release
        var buf = Read(p, 4800);

        double max = 0;
        foreach (var s in buf)
        {
            max = Math.Max(max, Math.Abs(s));
        }
        Assert.True(max < 0.005, $"expected silence after release, peak {max:0.0000}");
    }

    [Fact]
    public void AttackNeverOvershoots()
    {
        var p = new ToneSampleProvider { Frequency = 100 };
        p.SetTargets(0.5f, 0.5f);
        var buf = Read(p, 48000);
        foreach (var s in buf)
        {
            Assert.True(Math.Abs(s) <= 0.5005f);
        }
    }

    [Theory]
    [InlineData(0, 1.0)]
    [InlineData(-20, 0.1)]
    [InlineData(-60, 0.001)]
    [InlineData(-120, 0.001)] // clamped
    public void DbConversionIsExact(double db, double expected)
        => Assert.Equal(expected, ToneSampleProvider.DbToAmplitude(db), 4);
}
