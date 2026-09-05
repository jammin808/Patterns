using Patterns.Core.Audio;
using Patterns.Core.Effects;
using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>The master clock's audio side: the drift estimator, the lock, the converter, the delay line, the sync marks, the offsets.</summary>
/// <summary>In the InputBus collection: the sync-marks test flips the process-wide flag the cadence tests read, so they must not run beside it.</summary>
[Collection("InputBus")]
public class SyncTests
{
    [Fact]
    public void TheMasterClockDerivesWallTimeFromTheMonotonicClock()
    {
        var a = ShowClock.UtcNow;
        var b = ShowClock.UtcNow;
        Assert.True(b >= a, "never runs backwards");
        Assert.True(Math.Abs((ShowClock.UtcNow - DateTime.UtcNow).TotalSeconds) < 2, "agrees with the wall to within a couple of seconds");
        Assert.Equal(5.0, ShowClock.SecondsAt(ShowClock.UtcAt(5.0)), 6);
        Assert.True(Math.Abs(ShowClock.SecondsAt(ShowClock.UtcNow) - ShowClock.Seconds) < 0.05);
    }

    [Fact]
    public void TheEstimatorReadsADeviceFiftyPartsPerMillionFast()
    {
        var est = new DriftEstimator(48000);
        Assert.False(est.Confident);
        for (var t = 0.0; t <= 30; t += 0.4)
        {
            est.Observe((long)(t * 48000 * (1 + 50e-6)), t);
            if (t < DriftEstimator.MinWindowSeconds) Assert.False(est.Confident);
        }
        Assert.True(est.Confident);
        Assert.InRange(est.Ppm, 47, 53);
        est.Reset();
        Assert.False(est.Confident);
        Assert.Equal(0, est.Ppm);
    }

    /// <summary>A device 100 ppm fast, the source held to the master: the lag stays under a millisecond and the ratio lands on the drift.</summary>
    [Fact]
    public void TheLockHoldsASourceToTheMasterAgainstAFastDevice()
    {
        const double ppm = 100;
        static (double Lag, double Ratio) Run(bool locked)
        {
            var est = new DriftEstimator(48000);
            var ctrl = new SyncController();
            var ratio = 1.0;
            double master = 0, sourceSeconds = 0, devFrames = 0;
            const double dt = 0.4;
            var lag = 0.0;
            for (var i = 0; i < 300; i++) // two minutes
            {
                master += dt;
                var played = dt * 48000 * (1 + ppm * 1e-6); // the device's clock runs fast: it eats more frames per master second
                devFrames += played;
                sourceSeconds += played / ratio / 48000;      // the converter hands out `ratio` output frames per input frame
                est.Observe((long)devFrames, master);
                lag = sourceSeconds - master;
                if (locked) ratio = ctrl.Update(lag, dt, est.Confident ? est.Ppm : 0);
            }
            return (lag, ratio);
        }

        var (lockedLag, ratio) = Run(locked: true);
        Assert.True(Math.Abs(lockedLag) < 0.001, $"locked lag {lockedLag * 1000:0.00} ms");
        Assert.InRange(ratio, 1 + (ppm - 30) * 1e-6, 1 + (ppm + 30) * 1e-6);

        var (freeLag, _) = Run(locked: false);
        Assert.InRange(freeLag, 0.010, 0.014); // 100 ppm over two minutes: twelve milliseconds adrift

        // The correction is bounded: a wild reading cannot bend the pitch.
        var wild = new SyncController();
        Assert.Equal(1 + SyncController.MaxCorrectionPpm * 1e-6, wild.Update(10, 1), 9);
        Assert.Equal(SyncController.MaxCorrectionPpm, wild.CorrectionPpm, 3);
    }

    private static float[] Sine(int frames, double hz, int rate, int channels)
    {
        var buf = new float[frames * channels];
        for (var i = 0; i < frames; i++)
        {
            var s = (float)Math.Sin(2 * Math.PI * hz * i / rate);
            for (var c = 0; c < channels; c++) buf[i * channels + c] = s;
        }
        return buf;
    }

    private static Func<float[], int, int, int> Reader(float[] source, int channels)
    {
        var pos = 0;
        return (buffer, offset, frames) =>
        {
            var have = source.Length / channels - pos;
            var n = Math.Min(frames, have);
            Array.Copy(source, pos * channels, buffer, offset, n * channels);
            pos += n;
            return n;
        };
    }

    private static double ZeroCrossingsPerSecond(float[] mono, int rate)
    {
        var crossings = 0;
        for (var i = 1; i < mono.Length; i++)
        {
            if ((mono[i - 1] < 0 && mono[i] >= 0) || (mono[i - 1] >= 0 && mono[i] < 0)) crossings++;
        }
        return crossings / 2.0 / (mono.Length / (double)rate);
    }

    [Fact]
    public void TheConverterIsTransparentAtOneAndShiftsPitchByTheRatio()
    {
        const int rate = 48000;
        var input = Sine(rate, 440, rate, 1);

        var flat = new SampleRateConverter(1);
        var outFlat = new float[rate - 8];
        var produced = flat.Read(outFlat, 0, outFlat.Length, Reader(input, 1));
        Assert.Equal(outFlat.Length, produced);
        for (var i = 0; i < 1000; i++) Assert.Equal(input[i + 1], outFlat[i], 4); // the input itself, a frame late
        Assert.Equal(1.0, flat.Ratio);

        var shifted = new SampleRateConverter(1) { Ratio = 1.001 };
        var outShifted = new float[rate];
        var got = shifted.Read(outShifted, 0, rate, Reader(input, 1));
        Assert.Equal(rate, got);
        Assert.InRange(shifted.InputFramesConsumed, rate / 1.001 - 8, rate / 1.001 + 8);
        Assert.InRange(ZeroCrossingsPerSecond(outShifted, rate), 440 / 1.001 - 0.6, 440 / 1.001 + 0.6);
        var maxStepIn = 0f;
        var maxStepOut = 0f;
        for (var i = 1; i < rate; i++)
        {
            maxStepIn = Math.Max(maxStepIn, Math.Abs(input[i] - input[i - 1]));
            maxStepOut = Math.Max(maxStepOut, Math.Abs(outShifted[i] - outShifted[i - 1]));
        }
        Assert.True(maxStepOut <= maxStepIn * 1.05, "no click, no step");

        // Stereo stays interleaved and the source's end ends the converter.
        var stereo = new SampleRateConverter(2);
        var shortIn = Sine(100, 440, rate, 2);
        var outStereo = new float[400];
        var frames = stereo.Read(outStereo, 0, 200, Reader(shortIn, 2));
        Assert.InRange(frames, 90, 97);
        Assert.True(stereo.Ended);
        for (var i = 0; i < frames; i++) Assert.Equal(outStereo[i * 2], outStereo[i * 2 + 1], 6);
    }

    [Fact]
    public void TheDelayLineHoldsSamplesBackExactly()
    {
        var delay = new DelayBuffer(10, 1);
        var buf = new float[32];
        buf[0] = 1;
        delay.Process(buf, 0, buf.Length);
        Assert.Equal(0, buf[0]);
        Assert.Equal(1, buf[10]);
        Assert.Equal(1, buf.Sum());
        Assert.Equal(4800, DelayBuffer.FramesFor(100, 48000));
        Assert.Equal(0, DelayBuffer.FramesFor(-5, 48000));

        var stereo = new DelayBuffer(3, 2);
        var s = new float[20];
        s[0] = 1;
        s[1] = -1;
        stereo.Process(s, 0, s.Length);
        Assert.Equal((1f, -1f), (s[6], s[7]));
        var none = new DelayBuffer(0, 2);
        var same = new float[] { 1, 2, 3, 4 };
        none.Process(same, 0, 4);
        Assert.Equal(new float[] { 1, 2, 3, 4 }, same);
    }

    [Fact]
    public void TheSyncMarksFlashOnTheMasterGridOnEverySink()
    {
        try
        {
            SyncMarks.Enabled = false;
            Assert.False(SyncMarks.IsFlash(4.01));
            SyncMarks.Enabled = true;
            Assert.True(SyncMarks.IsFlash(4.01));
            Assert.False(SyncMarks.IsFlash(4.06));
            Assert.False(SyncMarks.IsFlash(5.0));
            Assert.Equal(6.0, SyncMarks.NextMark(4.01));
            Assert.Equal(8.0, SyncMarks.NextMark(6.0));
            Assert.Equal(4.0, SyncMarks.MarkBefore(5.9));

            var state = RenderTestHarness.State(s => s.Pattern.Kind = PatternKind.Grid);
            using var flash = RenderTestHarness.Render(state, 160, 90, time: 4.01, sinkKind: SinkKind.Ndi);
            Assert.Equal(SKColors.White, flash.GetPixel(80, 45));
            Assert.Equal(SKColors.White, flash.GetPixel(3, 3));
            using var between = RenderTestHarness.Render(state, 160, 90, time: 4.5, sinkKind: SinkKind.Ndi);
            Assert.NotEqual(SKColors.White, between.GetPixel(3, 3));
            using var thumb = RenderTestHarness.Render(state, 160, 90, time: 4.01, sinkKind: SinkKind.Thumbnail);
            Assert.NotEqual(SKColors.White, thumb.GetPixel(3, 3)); // a thumbnail is not the show
            Assert.Equal(RedrawCadence.Continuous, PatternEngine.CadenceOf(RenderTestHarness.Snap(state), null, DateTime.UtcNow));
        }
        finally
        {
            SyncMarks.Enabled = false;
        }
        Assert.NotEqual(RedrawCadence.Continuous, PatternEngine.CadenceOf(RenderTestHarness.Snap(RenderTestHarness.State(s => s.Pattern.Kind = PatternKind.Grid)), null, DateTime.UtcNow));
    }

    [Fact]
    public void TheOffsetsLiveOnTheShowAndReachTheStreamAndTheCheck()
    {
        var audio = new AudioPlayerConfig();
        Assert.Equal(0, audio.DelayFor("Speakers"));
        audio.SetDelay("Speakers", 120);
        audio.SetDelay("(computer output)", 3000);
        Assert.Equal(120, audio.DelayFor("speakers"));
        Assert.Equal(2000, audio.DelayFor("(computer output)"));
        Assert.Equal(2, audio.OutputDelays.Count);
        audio.SetDelay("Speakers", 0);
        Assert.Single(audio.OutputDelays);
        audio.VideoAudioDelayMs = -5000;
        Assert.Equal(-1000, audio.VideoAudioDelayMs);
        Assert.True(audio.SyncLock);
        var json = JsonUtil.Serialize(audio);
        var back = JsonUtil.Deserialize<AudioPlayerConfig>(json)!;
        Assert.Equal(2000, back.DelayFor("(computer output)"));
        Assert.Equal(-1000, back.VideoAudioDelayMs);

        var stream = new StreamConfig { AudioDevice = "Line In", AudioDelayMs = 120 };
        var plan = StreamMrl.Build(stream, SKRectI.Create(0, 0, 1920, 1080), new[] { "rtmp://x/y" })!;
        Assert.Contains(":audio-desync=120", plan.Options);
        Assert.Contains(":audio-desync=120", StreamMrl.BuildRendered(stream, new[] { "rtmp://x/y" })!.Options);
        stream.AudioDelayMs = 0;
        Assert.DoesNotContain(StreamMrl.Build(stream, SKRectI.Create(0, 0, 1920, 1080), new[] { "rtmp://x/y" })!.Options, o => o.StartsWith(":audio-desync"));
        Assert.DoesNotContain(StreamMrl.Build(new StreamConfig { AudioDelayMs = 50 }, SKRectI.Create(0, 0, 1920, 1080), new[] { "rtmp://x/y" })!.Options, o => o.StartsWith(":audio-desync"));

        // The super-check's master-clock row.
        Assert.Equal(CheckLight.Amber, SuperCheck.Run(new CheckFacts { SyncLock = false }).Rows.Single(r => r.Item == "Master clock").Light);
        Assert.Equal(CheckLight.Grey, SuperCheck.Run(new CheckFacts()).Rows.Single(r => r.Item == "Master clock").Light);
        var locked = SuperCheck.Run(new CheckFacts { SyncLines = new[] { "Speakers: clock +42 ppm · correction +41 ppm · lag +0.3 ms" }, SyncWorstLagMs = 0.3 });
        Assert.Equal(CheckLight.Green, locked.Rows.Single(r => r.Item == "Master clock").Light);
        var pulling = SuperCheck.Run(new CheckFacts { SyncLines = new[] { "HDMI: clock measuring · correction +2000 ppm · lag −40.0 ms" }, SyncWorstLagMs = 40 });
        Assert.Equal(CheckLight.Amber, pulling.Rows.Single(r => r.Item == "Master clock").Light);
    }
}
