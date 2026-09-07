using System.Runtime;
using System.Text.Json;
using Patterns.Core.Effects;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The runtime hygiene of the hot paths: the clone that every publish goes through is compact
/// and the same show; the spectrum's reused buffers never carry the last window into the next;
/// the collector goes to sustained low latency on air and back off air.
/// </summary>
public class RuntimeHygieneTests
{
    [Fact]
    public void TheCloneIsCompactAndStillTheWholeShow()
    {
        var state = new ShowState();
        state.Pattern.Kind = PatternKind.Fractal;
        state.Pattern.Fractal.AudioSource = AudioSourceKind.Internal;
        state.Brand.PrimaryColor = "#FF3366";
        state.Overlays.Clock.Enabled = true;
        state.Overlays.Message.Text = "Doors open at 7 — “welcome”";

        var clone = JsonUtil.Clone(state);
        Assert.NotSame(state, clone);
        Assert.Equal(JsonUtil.SerializeIdentity(state), JsonUtil.SerializeIdentity(clone));
        Assert.Equal(PatternKind.Fractal, clone.Pattern.Kind);
        Assert.Equal("Doors open at 7 — “welcome”", clone.Overlays.Message.Text);

        // The clone's wire form: no indentation, every field kept, a third smaller than the file form.
        Assert.False(JsonUtil.CloneOptions.WriteIndented);
        var compact = JsonSerializer.Serialize(state, JsonUtil.CloneOptions);
        var indented = JsonUtil.Serialize(state);
        Assert.DoesNotContain('\n', compact);
        Assert.True(compact.Length < indented.Length * 0.8, $"compact {compact.Length} vs indented {indented.Length}");
        Assert.Equal(JsonUtil.SerializeIdentity(state), JsonUtil.SerializeIdentity(JsonSerializer.Deserialize<ShowState>(compact, JsonUtil.CloneOptions)!));
        // The tolerant enum reading the file form has stays: a clone written by a newer build still reads.
        var newer = compact.Replace("\"Fractal\"", "\"FractalFromTheFuture\"");
        Assert.NotEqual(compact, newer);
        Assert.NotNull(JsonSerializer.Deserialize<ShowState>(newer, JsonUtil.CloneOptions));
    }

    private static float[] Sine(double hz, int rate, double amplitude, int n)
    {
        var s = new float[n];
        for (var i = 0; i < n; i++) s[i] = (float)(amplitude * Math.Sin(2 * Math.PI * hz * i / rate));
        return s;
    }

    /// <summary>The analysis as it was written before the buffers were kept: fresh arrays and the window computed per sample.</summary>
    private static AudioLevelFrame Reference(float[] samples, int sampleRate)
    {
        var n = Spectrum.Window;
        var re = new double[n];
        var im = new double[n];
        var start = Math.Max(0, samples.Length - n);
        var count = Math.Min(n, samples.Length);
        double sumSq = 0;
        for (var i = 0; i < count; i++)
        {
            var s = samples[start + i];
            sumSq += s * s;
            var hann = 0.5 - 0.5 * Math.Cos(2 * Math.PI * i / (n - 1));
            re[i] = s * hann;
        }
        Spectrum.Fft(re, im);
        double low = 0, mid = 0, high = 0;
        for (var k = 1; k < n / 2; k++)
        {
            var hz = k * sampleRate / (double)n;
            if (hz < Spectrum.LowHz || hz > Spectrum.HighHz) continue;
            var mag = Math.Sqrt(re[k] * re[k] + im[k] * im[k]) * 4.0 / n;
            var power = mag * mag;
            if (hz < Spectrum.LowMidHz) low += power;
            else if (hz < Spectrum.MidHighHz) mid += power;
            else high += power;
        }
        var rms = Math.Sqrt(sumSq / count);
        return new AudioLevelFrame(
            (float)Math.Clamp(rms * 2.5, 0, 1),
            (float)Math.Clamp(Math.Sqrt(low) * 1.5, 0, 1),
            (float)Math.Clamp(Math.Sqrt(mid) * 1.5, 0, 1),
            (float)Math.Clamp(Math.Sqrt(high) * 1.5, 0, 1));
    }

    private static void AssertSame(AudioLevelFrame expected, AudioLevelFrame actual)
    {
        Assert.Equal(expected.Level, actual.Level, 5);
        Assert.Equal(expected.Low, actual.Low, 5);
        Assert.Equal(expected.Mid, actual.Mid, 5);
        Assert.Equal(expected.High, actual.High, 5);
    }

    [Fact]
    public void TheSpectrumsKeptBuffersNeverCarryTheLastWindowIntoTheNext()
    {
        // A loud full window, then a short quiet buffer, then noise: each analysis equals the one
        // fresh arrays would give, so nothing of the window before is left in the buffers.
        var loud = Sine(440, 48000, 1.0, Spectrum.Window);
        var quiet = Sine(5000, 48000, 0.05, 300);
        var rng = new Random(7);
        var noise = new float[Spectrum.Window * 2];
        for (var i = 0; i < noise.Length; i++) noise[i] = (float)(rng.NextDouble() * 0.6 - 0.3);

        AssertSame(Reference(loud, 48000), Spectrum.Analyse(loud, 48000));
        var short1 = Spectrum.Analyse(quiet, 48000);
        AssertSame(Reference(quiet, 48000), short1);
        Assert.True(short1.High < Reference(loud, 48000).Mid, $"{short1}");
        AssertSame(Reference(noise, 44100), Spectrum.Analyse(noise, 44100));
        AssertSame(short1, Spectrum.Analyse(quiet, 48000));
        Assert.Equal(AudioLevelFrame.Zero, Spectrum.Analyse(new float[Spectrum.Window], 48000));

        // From another thread the same answers: the buffers are per thread, never shared.
        AudioLevelFrame fromThread = default;
        var t = new Thread(() => fromThread = Spectrum.Analyse(loud, 48000));
        t.Start();
        t.Join();
        AssertSame(Reference(loud, 48000), fromThread);
    }

    [Fact]
    public void TheCollectorRunsInSustainedLowLatencyOnAirAndRestsOffAir()
    {
        var before = GCSettings.LatencyMode;
        try
        {
            ShowGc.Apply(true);
            Assert.Equal(GCLatencyMode.SustainedLowLatency, GCSettings.LatencyMode);
            Assert.Equal("sustained low latency (outputs live)", ShowGc.Describe());
            Assert.NotNull(ShowGc.RestingMode);

            ShowGc.Apply(true);   // again, live: nothing moves
            Assert.Equal(GCLatencyMode.SustainedLowLatency, GCSettings.LatencyMode);

            ShowGc.Apply(false);
            Assert.Equal(ShowGc.RestingMode, GCSettings.LatencyMode);
            Assert.NotEqual(GCLatencyMode.SustainedLowLatency, GCSettings.LatencyMode);
            Assert.DoesNotContain("outputs live", ShowGc.Describe());

            ShowGc.Apply(false);  // off air twice: still resting
            Assert.Equal(ShowGc.RestingMode, GCSettings.LatencyMode);
        }
        finally
        {
            GCSettings.LatencyMode = before;
        }
    }
}
