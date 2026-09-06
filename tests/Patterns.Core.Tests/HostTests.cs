using System.Runtime.InteropServices;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>The ring of frames two processes share: whole frames in, whole frames out, the newest winning, and a second opening by address.</summary>
public class SharedFrameRingTests
{
    private static void Fill(IntPtr pixels, int bytes, byte value)
    {
        var buffer = new byte[bytes];
        Array.Fill(buffer, value);
        Marshal.Copy(buffer, 0, pixels, bytes);
    }

    [Fact]
    public void AFrameGoesInWholeAndComesOutWholeAndTheNewestWins()
    {
        using var ring = SharedFrameRing.Create(SharedFrameRing.NameFor("test"), 8, 4);
        Assert.Equal(8 * 4 * 4, ring.FrameBytes);
        Assert.Equal(32, ring.Stride);
        Assert.Equal(0, ring.LatestSeq);
        Assert.False(ring.IsClosed);
        var dest = new byte[ring.FrameBytes];
        Assert.False(ring.TryRead(0, dest, out _));
        Assert.Equal(-1, ring.WaitForFrame(0, 10));

        // Four frames written before any is read: the reader gets the newest, whole.
        for (var n = 1; n <= 4; n++)
        {
            var slot = ring.BeginWrite();
            Assert.InRange(slot, 0, SharedFrameRing.Slots - 1);
            Fill(ring.PixelsOf(slot), ring.FrameBytes, (byte)n);
            Assert.Equal(n, ring.EndWrite(slot, n));
        }
        Assert.Equal(4, ring.LatestSeq);
        Assert.True(ring.TryRead(0, dest, out var seq));
        Assert.Equal(4, seq);
        Assert.All(dest, b => Assert.Equal(4, b));
        Assert.False(ring.TryRead(4, dest, out _));          // nothing newer than what was read
        Assert.Equal(4, ring.WaitForFrame(3, 10));
        Assert.Equal(-1, ring.WaitForFrame(4, 10));

        // The slot being written is never the one just read, and a frame in progress is never taken.
        var next = ring.BeginWrite();
        Assert.NotEqual((int)(4 % SharedFrameRing.Slots), next);
        Assert.True(ring.TryRead(0, dest, out seq));
        Assert.Equal(4, seq);
        Assert.Throws<ArgumentException>(() => ring.TryRead(0, new byte[3], out _));
        Assert.Throws<ArgumentOutOfRangeException>(() => SharedFrameRing.Create(SharedFrameRing.NameFor("bad"), 0, 4));
        Assert.Equal(64 + SharedFrameRing.Slots * (64 + 8L * 4 * 4), SharedFrameRing.CapacityFor(8, 4));
    }

    [Fact]
    public async Task ASecondOpeningSeesTheSameFramesByAddressAndTheOwnersCloseEndsIt()
    {
        var ring = SharedFrameRing.Create(SharedFrameRing.NameFor("test"), 16, 9);
        try
        {
            using var other = SharedFrameRing.Open(ring.Address);
            Assert.Equal((16, 9), (other.Width, other.Height));
            Assert.Equal(ring.FrameBytes, other.FrameBytes);
            var slot = ring.BeginWrite();
            Fill(ring.PixelsOf(slot), ring.FrameBytes, 0x5A);
            ring.EndWrite(slot, 1);
            var dest = new byte[other.FrameBytes];
            Assert.True(other.TryRead(0, dest, out var seq));
            Assert.Equal(1, seq);
            Assert.All(dest, b => Assert.Equal(0x5A, b));

            // A reader waiting on the other side sees the next frame come in.
            var waiter = Task.Run(() => other.WaitForFrame(1, 3000));
            Thread.Sleep(40);
            slot = ring.BeginWrite();
            ring.EndWrite(slot, 2);
            Assert.Equal(2, await waiter);

            // The owner closing is the end for the reader — a waiting one is woken by it, not left to its timeout.
            var closing = Task.Run(() => other.WaitForFrame(2, 3000));
            Thread.Sleep(40);
            ring.Dispose();
            Assert.True(other.IsClosed);
            Assert.Equal(-1, await closing);
            Assert.Equal(-1, other.WaitForFrame(2, 10));
            Assert.Equal(OperatingSystem.IsWindows(), other.Signalled);   // the writer's event wakes a Windows reader; elsewhere it polls
        }
        finally
        {
            ring.Dispose();
        }

        if (!OperatingSystem.IsWindows())
        {
            var junk = SharedFrameRing.FilePathFor(SharedFrameRing.NameFor("junk"));
            File.WriteAllBytes(junk, new byte[512]);
            try
            {
                Assert.Throws<InvalidDataException>(() => SharedFrameRing.Open(junk));
            }
            finally
            {
                File.Delete(junk);
            }
        }
        Assert.StartsWith("patterns-test-", SharedFrameRing.NameFor("test"));
        Assert.NotEqual(SharedFrameRing.NameFor("test"), SharedFrameRing.NameFor("test"));
    }
}

/// <summary>The words a host and the desk exchange, and the payloads that ride them.</summary>
public class HostProtocolTests
{
    [Fact]
    public void LinesAreOneWordAndTheRestWithBreaksFlattened()
    {
        Assert.Equal("STARTED", HostProtocol.Line(HostProtocol.Started));
        Assert.Equal("STATUS live at 30 fps", HostProtocol.Line(HostProtocol.Status, "live at 30 fps"));
        Assert.Equal("START {   \"a\": 1 }", HostProtocol.Line(HostProtocol.Start, "{\r\n \"a\": 1 }"));
        Assert.Equal(("BEAT", "{\"frames\":3}"), HostProtocol.Parse("beat {\"frames\":3}"));
        Assert.Equal(("QUIT", ""), HostProtocol.Parse("  QUIT  "));
        Assert.Equal(("", ""), HostProtocol.Parse(null));
        Assert.Equal(("", ""), HostProtocol.Parse("   "));
        Assert.Equal(("ERROR", "libvlc Streaming needs libVLC"), HostProtocol.Parse("ERROR libvlc Streaming needs libVLC"));
        Assert.Equal((HostProtocol.ErrorLibVlc, "Streaming needs libVLC"), HostProtocol.SplitError("libvlc Streaming needs libVLC"));
        Assert.Equal((HostProtocol.ErrorStart, "Encoder failed to start"), HostProtocol.SplitError("START Encoder failed to start"));
        Assert.Equal((HostProtocol.ErrorEncoder, ""), HostProtocol.SplitError("encoder"));
        Assert.True(HostProtocol.HelloTimeout > HostProtocol.BeatTimeout);
        Assert.True(HostProtocol.StartTimeout > HostProtocol.BeatTimeout);

        var t = new DateTime(2026, 9, 6, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(HostProtocol.IsSilent(t, t.AddSeconds(5)));
        Assert.True(HostProtocol.IsSilent(t, t + HostProtocol.BeatTimeout + TimeSpan.FromMilliseconds(1)));
        Assert.True(HostProtocol.BeatTimeout > HostProtocol.BeatEvery * 3);
    }

    [Fact]
    public void ThePlanTheHelloAndTheBeatRoundTripAsJsonOnOneLine()
    {
        var plan = new EncoderPlan(EncoderPlan.Rendered, StreamMrl.RenderedMrl, new[] { ":demux=rawvideo", ":sout=#transcode{vcodec=h264}:std{access=rtmp,dst=rtmp://x/y}" }, "patterns-stream-1-1", 1280, 720, 30);
        var line = HostProtocol.Line(HostProtocol.Start, JsonUtil.Serialize(plan));
        Assert.DoesNotContain('\n', line);
        var (word, rest) = HostProtocol.Parse(line);
        Assert.Equal(HostProtocol.Start, word);
        var back = JsonUtil.Deserialize<EncoderPlan>(rest)!;
        Assert.Equal(plan.Kind, back.Kind);
        Assert.Equal(plan.Mrl, back.Mrl);
        Assert.Equal(plan.Options, back.Options);
        Assert.Equal((plan.Ring, plan.Width, plan.Height, plan.Fps), (back.Ring, back.Width, back.Height, back.Fps));
        Assert.True(back.UsesRing);
        Assert.False(new EncoderPlan(EncoderPlan.Capture, "screen://", Array.Empty<string>(), "", 1920, 1080, 30).UsesRing);
        Assert.True(new EncoderPlan(EncoderPlan.Null, "", Array.Empty<string>(), "ring", 64, 36, 30).UsesRing);

        Assert.Equal(new HostBeat(12, "Playing"), JsonUtil.Deserialize<HostBeat>(JsonUtil.Serialize(new HostBeat(12, "Playing"))));
        Assert.Equal(new HostHello(42, "encoder", true), JsonUtil.Deserialize<HostHello>(JsonUtil.Serialize(new HostHello(42, "encoder", true))));
    }
}
