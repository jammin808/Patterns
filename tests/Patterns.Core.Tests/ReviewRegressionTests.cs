using Patterns.Core.Model;
using Patterns.Core.Ndi;
using Patterns.Core.Particles;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>Regression tests for the issues found in the pre-release code review.</summary>
public class ReviewRegressionTests
{
    private static ShowState BlackField() => RenderTestHarness.State(s =>
    {
        s.Pattern.Kind = PatternKind.FlatField;
        s.Pattern.FlatField.Color = "#000000";
        s.Pattern.FlatField.ShowLabel = false;
        s.Pattern.FlatField.ShowBorder = false;
    });

    [Fact]
    public void IdentifySurvivesTheSnapshotCloneAndDrawsOnOutputs()
    {
        // The bug: the identify deadline lived on ShowState behind [JsonIgnore], so the
        // JSON snapshot clone silently dropped it and no output ever drew a badge.
        var state = BlackField();
        var bus = new SnapshotBus(state)
        {
            IdentifyUntilUtc = RenderTestHarness.FixedUtcNow.AddSeconds(3),
        };
        bus.Publish(state);
        Assert.NotNull(bus.Current.IdentifyUntilUtc);

        using var output = RenderTestHarness.Render(bus.Current, 400, 300);
        var border = output.GetPixel(2, 150);
        Assert.NotEqual(SKColors.Black, border); // accent identify frame is visible

        // Preview shows no badge (a "screen 0" badge would only confuse).
        using var preview = RenderTestHarness.Render(bus.Current, 400, 300, sinkKind: SinkKind.Preview);
        Assert.Equal(SKColors.Black, preview.GetPixel(2, 150));
        Assert.Equal(SKColors.Black, preview.GetPixel(200, 150));
    }

    [Fact]
    public void ScrollingTickerCoversTheWholeWidth()
    {
        // The bug: the marquee loop only marched copies rightward from the lead position,
        // so text never crossed the left part of the canvas.
        var state = RenderTestHarness.State(s =>
        {
            s.Pattern.Kind = PatternKind.FlatField;
            s.Pattern.FlatField.Color = "#000000";
            s.Pattern.FlatField.ShowLabel = false;
            s.Overlays.Message.Enabled = true;
            s.Overlays.Message.Text = "COVERAGE CHECK";
            s.Overlays.Message.Scroll = true;
            s.Overlays.Message.ScrollPxPerSec = 200;
            s.Overlays.Message.SizePct = 20;
            s.Overlays.Message.Anchor = Anchor9.Center;
        });
        var snap = RenderTestHarness.Snap(state);

        var leftQuarterHit = false;
        for (var t = 0.0; t < 12 && !leftQuarterHit; t += 0.25)
        {
            using var bmp = RenderTestHarness.Render(snap, 480, 200, time: t);
            for (var x = 2; x < 120 && !leftQuarterHit; x += 2)
            {
                for (var y = 80; y < 120; y += 2)
                {
                    if (bmp.GetPixel(x, y) != SKColors.Black)
                    {
                        leftQuarterHit = true;
                        break;
                    }
                }
            }
        }
        Assert.True(leftQuarterHit, "scrolling text never reached the left quarter of the canvas");
    }

    [Fact]
    public void ParticleAdvanceIsFrameRateInvariant()
    {
        // The bug: per-sink variable-dt integration made span halves and NDI drift apart.
        // Fixed-timestep quantization must make different render cadences bit-identical.
        var snap = new ShowSnapshot { State = new ShowState(), Version = 1 };
        var opts = new ParticleOptions { Count = 400, Seed = 7, Emitter = ParticleEmitter.TopEdge };
        var canvas = new SKSizeI(800, 450);

        using var at60 = new ParticleSim();
        using var at24 = new ParticleSim();
        at60.Configure(opts, snap, canvas);
        at24.Configure(opts, snap, canvas);

        const double start = 100.0;
        for (var t = start; t <= start + 10; t += 1.0 / 60) at60.Advance(t);
        for (var t = start; t <= start + 10; t += 1.0 / 24) at24.Advance(t);
        at60.Advance(start + 10);
        at24.Advance(start + 10);

        for (var i = 0; i < at60.Count; i += 5)
        {
            Assert.Equal(at60.PositionOf(i), at24.PositionOf(i));
        }
    }

    [Fact]
    public void NdiReprobeClearsANegativeResult()
    {
        // On this machine the runtime is absent: Available is false, and re-probing must not
        // throw and must leave the sender in the help-text state rather than a stale one.
        NdiInterop.ReprobeIfUnavailable();
        Assert.False(NdiInterop.Available);
        NdiInterop.ReprobeIfUnavailable();
        Assert.False(NdiInterop.Available);
    }
}
