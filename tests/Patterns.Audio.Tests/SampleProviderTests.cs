using NAudio.Wave;
using Patterns.Audio;
using Xunit;

namespace Patterns.Audio.Tests;

/// <summary>A source of one value forever, in the mix format.</summary>
internal sealed class ConstantSource : ISampleProvider
{
    private readonly float _value;
    public ConstantSource(float value) => _value = value;
    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(AudioMix.Rate, AudioMix.Channels);
    public int Read(float[] buffer, int offset, int count)
    {
        for (var i = 0; i < count; i++) buffer[offset + i] = _value;
        return count;
    }
}

/// <summary>
/// The sample providers are pure DSP over the mix format: a gain scales, a delay prepends its
/// silence, a tee copies what passed into a ring, a tone lands on its amplitude — with no
/// device, no desk and no UI, on any host.
/// </summary>
public class SampleProviderTests
{
    private static float[] Read(ISampleProvider p, int samples)
    {
        var buffer = new float[samples];
        var got = p.Read(buffer, 0, samples);
        Assert.Equal(samples, got);
        return buffer;
    }

    [Fact]
    public void TheMixFormatIsFortyEightKilohertzStereo()
    {
        Assert.Equal(48000, AudioMix.Rate);
        Assert.Equal(2, AudioMix.Channels);
        Assert.Equal("(computer output)", AudioOutputs.DefaultDeviceKey);
    }

    [Fact]
    public void AGainScalesEverySample()
    {
        var gain = new GainSampleProvider(new ConstantSource(1f), 0.5f);
        var block = Read(gain, 960);
        Assert.All(block, s => Assert.Equal(0.5f, s, 3));
        Assert.Equal(0.5f, gain.Gain, 3);
    }

    [Fact]
    public void ADelayPrependsItsOwnSilenceAndThenPassesTheSource()
    {
        var delay = new DelaySampleProvider(new ConstantSource(1f), 10);              // 10 ms at 48 kHz stereo = 960 samples of silence
        Assert.Equal(10, delay.DelayMs);
        var block = Read(delay, 960 * 2);
        Assert.All(block.Take(960), s => Assert.Equal(0f, s));
        Assert.All(block.Skip(960), s => Assert.Equal(1f, s));
    }

    [Fact]
    public void ATeeCopiesWhatPassedIntoTheRingForAnotherLaneToRead()
    {
        var ring = new AudioRing(AudioMix.Channels, AudioMix.Rate);
        var tee = new TeeSampleProvider(new ConstantSource(0.25f), ring);
        Read(tee, 480 * 2);
        Assert.Equal(480 * 2, ring.Written);                                           // the ring counts interleaved samples
        var reader = ring.OpenReader(latencyFrames: 480);                                 // a reader sits behind the head by its latency: the frames just written
        var heard = new float[480 * 2];
        var samples = ring.Read(reader, heard);
        Assert.True(samples > 0);
        Assert.All(heard.Take(samples), s => Assert.Equal(0.25f, s, 3));
    }

    [Fact]
    public void ATeeWithNoTapIsJustTheSource()
    {
        var tee = new TeeSampleProvider(new ConstantSource(0.75f), null);
        Assert.Null(tee.Tap);
        Assert.All(Read(tee, 96), s => Assert.Equal(0.75f, s, 3));
    }

    [Fact]
    public void ATonelandsOnItsAmplitudeWithoutAStep()
    {
        var tone = new ToneSampleProvider();
        tone.SetTargets(0.5f, 0f);
        var first = Read(tone, 96);                                                    // the ramp: never a full-amplitude sample on the first frame
        Assert.True(Math.Abs(first[0]) < 0.5f);
        Read(tone, 48000 * 2);                                                         // a second in: settled
        var settled = Read(tone, 4800);
        var leftPeak = 0f; var rightPeak = 0f;
        for (var i = 0; i < settled.Length; i += 2)
        {
            leftPeak = Math.Max(leftPeak, Math.Abs(settled[i]));
            rightPeak = Math.Max(rightPeak, Math.Abs(settled[i + 1]));
        }
        Assert.InRange(leftPeak, 0.45f, 0.51f);
        Assert.Equal(0f, rightPeak, 3);
    }
}
