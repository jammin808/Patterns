using System.Runtime.InteropServices;
using Patterns.Core.Audio;
using Patterns.Ndi;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>The fan-out ring behind the mixer lanes, and the NDI audio frame's layout.</summary>
public class AudioRingTests
{
    private static float[] Ramp(int start, int count)
    {
        var a = new float[count];
        for (var i = 0; i < count; i++) a[i] = start + i;
        return a;
    }

    [Fact]
    public void TwoReadersEachHearTheWholeStreamFromTheirOwnPlace()
    {
        var ring = new AudioRing(channels: 2, capacityFrames: 100);
        var a = ring.OpenReader(latencyFrames: 10);
        var b = ring.OpenReader(latencyFrames: 10);
        var silence = new float[8];
        Assert.Equal(0, ring.Read(a, silence));            // nothing written: silence, not started
        Assert.All(silence, v => Assert.Equal(0, v));

        ring.Write(Ramp(0, 40));                            // 20 frames
        var got = new float[20];
        Assert.Equal(20, ring.Read(a, got));                // a starts 10 frames (20 samples) behind: samples 20..39
        Assert.Equal(20f, got[0]);
        Assert.Equal(39f, got[19]);
        Assert.Equal(0, ring.LagFrames(a));
        Assert.Equal(20, ring.Read(b, got));                // b's own place, the same samples
        Assert.Equal(20f, got[0]);

        // Nothing new: an underrun fills with silence and is counted; the next write is heard from where it left off.
        Assert.Equal(0, ring.Read(a, got));
        Assert.Equal(1, a.Underruns);
        ring.Write(Ramp(40, 6));
        var few = new float[10];
        Assert.Equal(6, ring.Read(a, few));
        Assert.Equal(40f, few[0]);
        Assert.Equal(45f, few[5]);
        Assert.Equal(0f, few[6]);
    }

    [Fact]
    public void AReaderThatFallsOutOfTheRingSnapsForwardAndTheRingWrapsCleanly()
    {
        var ring = new AudioRing(channels: 1, capacityFrames: 16);
        var r = ring.OpenReader(latencyFrames: 4);
        ring.Write(Ramp(0, 8));
        var got = new float[4];
        Assert.Equal(4, ring.Read(r, got));                 // 4 behind: samples 4..7
        Assert.Equal(4f, got[0]);
        // The writer runs on for 40 samples while the reader is away: the reader snaps to 4 behind.
        for (var i = 0; i < 5; i++) ring.Write(Ramp(8 + i * 8, 8));
        Assert.Equal(4, ring.Read(r, got));
        Assert.Equal(1, r.Snaps);
        Assert.Equal(44f, got[0]);
        Assert.Equal(47f, got[3]);
        // A write larger than the ring keeps the newest sixteen (124..139) and counts the whole write; the reader snaps four behind.
        ring.Write(Ramp(100, 40));
        Assert.Equal(88, ring.Written);
        var all = new float[4];
        ring.Read(r, all);
        Assert.Equal(2, r.Snaps);
        Assert.Equal(128f, all[0]);
        Assert.Equal(131f, all[3]);
    }

    [Fact]
    public void TheNdiAudioFrameMatchesTheHeadersLayout()
    {
        if (!Environment.Is64BitProcess) return;
        Assert.Equal(64, Marshal.SizeOf<NdiInterop.AudioFrameV3>());
        Assert.Equal(0, (int)Marshal.OffsetOf<NdiInterop.AudioFrameV3>(nameof(NdiInterop.AudioFrameV3.SampleRate)));
        Assert.Equal(16, (int)Marshal.OffsetOf<NdiInterop.AudioFrameV3>(nameof(NdiInterop.AudioFrameV3.Timecode)));
        Assert.Equal(24, (int)Marshal.OffsetOf<NdiInterop.AudioFrameV3>(nameof(NdiInterop.AudioFrameV3.FourCc)));
        Assert.Equal(32, (int)Marshal.OffsetOf<NdiInterop.AudioFrameV3>(nameof(NdiInterop.AudioFrameV3.Data)));
        Assert.Equal(40, (int)Marshal.OffsetOf<NdiInterop.AudioFrameV3>(nameof(NdiInterop.AudioFrameV3.ChannelStrideInBytes)));
        Assert.Equal(48, (int)Marshal.OffsetOf<NdiInterop.AudioFrameV3>(nameof(NdiInterop.AudioFrameV3.Metadata)));
        Assert.Equal(56, (int)Marshal.OffsetOf<NdiInterop.AudioFrameV3>(nameof(NdiInterop.AudioFrameV3.Timestamp)));
        Assert.Equal('F' | ('L' << 8) | ('T' << 16) | ('P' << 24), NdiInterop.FourCcFltp);
    }
}
