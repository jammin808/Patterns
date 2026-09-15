using Patterns.Audio;
using Xunit;

namespace Patterns.Audio.Tests;

/// <summary>A flush moves the ring's epoch: a reader born before it restarts at the flush mark, one born after counts nothing.</summary>
public class AudioRingEpochTests
{
    [Fact]
    public void AFlushIsAnEpochNoReaderHearsAcross()
    {
        var ring = new AudioRing(channels: 2, capacityFrames: 100);
        var reader = ring.OpenReader(latencyFrames: 10);
        var samples = new float[40];
        for (var i = 0; i < samples.Length; i++) samples[i] = i + 1;                                   // 1..40: the sound before the seek
        ring.Write(samples);
        var got = new float[10];
        Assert.Equal(10, ring.Read(reader, got));
        Assert.Equal(21, got[0]);                                                                        // started 10 frames (20 samples) behind

        ring.Flush();                                                                                    // the seek
        Assert.Equal(1, ring.Epoch);
        Assert.Equal(0, ring.Read(reader, got));                                                         // nothing written since: silence, not the samples left before the flush
        Assert.All(got, v => Assert.Equal(0, v));
        Assert.Equal(1, reader.Flushes);
        var fresh = new float[6];
        for (var i = 0; i < fresh.Length; i++) fresh[i] = 100 + i;                                       // the sound after the seek
        ring.Write(fresh);
        Assert.Equal(6, ring.Read(reader, got));
        Assert.Equal(100, got[0]);                                                                       // the first new sample, never a pre-flush one
        Assert.Equal(105, got[5]);

        var late = ring.OpenReader(latencyFrames: 50);                                                   // a reader opened after the flush cannot reach behind it either
        ring.Write(new float[] { 200, 201 });
        var lateGot = new float[8];
        Assert.Equal(8, ring.Read(late, lateGot));
        Assert.Equal(100, lateGot[0]);                                                                   // its latency would have reached into the old sound: clamped at the flush
        Assert.Equal(0, late.Flushes);                                                                   // it never crossed one
    }
}
