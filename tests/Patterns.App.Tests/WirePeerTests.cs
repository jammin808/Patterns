using System.Text;
using Patterns.App.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 65: one writer per remote client. Replies in order and never dropped up to a ceiling,
/// STATE latest-wins, no line ever interleaved with another whatever threads say and push, and a
/// peer that cannot take its lines closed rather than kept.
/// </summary>
public class WirePeerTests
{
    /// <summary>A stream whose writes wait at a gate until the test opens it — a peer whose socket is full.</summary>
    private sealed class GatedStream : Stream
    {
        private readonly SemaphoreSlim _gate = new(0);
        public readonly MemoryStream Written = new();
        public volatile bool Disposed;
        public int Writes;

        public void Open(int writes) => _gate.Release(writes);

        public override async ValueTask WriteAsync(ReadOnlyMemory<byte> buffer, CancellationToken ct = default)
        {
            Interlocked.Increment(ref Writes);
            await _gate.WaitAsync(ct);
            lock (Written)
            {
                Written.Write(buffer.Span);
            }
        }

        public override void Write(byte[] buffer, int offset, int count) => WriteAsync(buffer.AsMemory(offset, count)).AsTask().GetAwaiter().GetResult();
        public override void Flush() { }
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        protected override void Dispose(bool disposing) { Disposed = true; base.Dispose(disposing); }

        public string[] Lines()
        {
            lock (Written)
            {
                return Encoding.UTF8.GetString(Written.ToArray()).Split('\n', StringSplitOptions.RemoveEmptyEntries);
            }
        }
    }

    private static void Until(Func<bool> done, string what, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!done())
        {
            if (Environment.TickCount64 > deadline) throw new TimeoutException(what);
            Thread.Sleep(2);
        }
    }

    [Fact]
    public void RepliesGoInOrderAndAStateIsLatestWinsBehindThem()
    {
        var stream = new GatedStream();
        using var peer = new WirePeer(stream);
        peer.Say("OK 1");
        Until(() => peer.Pending == 0 && stream.Writes == 1, "the first reply taken by the writer and held at the gate");
        peer.Say("OK 2");
        peer.Push("STATE a");
        peer.Say("OK 3");
        peer.Push("STATE b");
        Assert.Equal(3, peer.Pending);                                // OK 2, OK 3 and one state — the newest
        stream.Open(10);
        Until(() => peer.Written == 4, "four lines written");
        Assert.Equal(new[] { "OK 1", "OK 2", "OK 3", "STATE b" }, stream.Lines());
        Assert.False(peer.Closed);
        Assert.Equal("", peer.ClosedBecause);
    }

    [Fact]
    public void APeerThatTakesNoRepliesIsClosedAtTheCeilingNotKept()
    {
        var stream = new GatedStream();
        using var peer = new WirePeer(stream);
        peer.Say("OK 0");
        Until(() => stream.Writes == 1, "the first reply held at the gate");
        for (var i = 0; i < WirePeer.MaxQueuedReplies; i++) peer.Say($"OK {i + 1}");
        Assert.False(peer.Closed);
        Assert.Equal(WirePeer.MaxQueuedReplies, peer.Pending);
        peer.Say("one too many");
        Assert.True(peer.Closed);
        Assert.Contains($"{WirePeer.MaxQueuedReplies} replies waiting", peer.ClosedBecause);
        Assert.True(stream.Disposed);
        peer.Say("after the close");                                   // a no-op, never a throw
        peer.Push("STATE after");
        Assert.Equal(0, peer.Pending);
    }

    [Fact]
    public void ALineThatCannotBeWrittenWithinTheDeadlineClosesThePeer()
    {
        var was = WirePeer.WriteDeadline;
        WirePeer.WriteDeadline = TimeSpan.FromMilliseconds(150);
        try
        {
            var stream = new GatedStream();
            using var peer = new WirePeer(stream);
            peer.Push("STATE stuck");
            Until(() => peer.Closed, "the peer closed on the deadline");
            Assert.Contains("took longer than", peer.ClosedBecause);
            Assert.True(stream.Disposed);
        }
        finally
        {
            WirePeer.WriteDeadline = was;
        }
    }

    [Fact]
    public void ManyThreadsSayingAndPushingNeverInterleaveALine()
    {
        var stream = new GatedStream();
        stream.Open(1_000_000);
        using var peer = new WirePeer(stream);
        const int threads = 8, perThread = 150;
        var said = new List<string>[threads];
        Parallel.For(0, threads, t =>
        {
            said[t] = new List<string>();
            for (var i = 0; i < perThread; i++)
            {
                // A controller that reads: eight threads together stay under the ceiling that closes a peer that does not.
                while (peer.Pending > WirePeer.MaxQueuedReplies / 4) Thread.SpinWait(200);
                var line = $"OK t{t}-{i:000} " + new string((char)('a' + t), 40);
                said[t].Add(line);
                peer.Say(line);
                peer.Push($"STATE t{t}-{i:000} " + new string('s', 120));
            }
        });
        Until(() => peer.Pending == 0 && stream.Writes == peer.Written, "everything written", 10000);
        Assert.False(peer.Closed, peer.ClosedBecause);
        var lines = stream.Lines();
        Assert.Equal(peer.Written, lines.Length);
        var replies = lines.Where(l => l.StartsWith("OK ")).ToList();
        Assert.Equal(threads * perThread, replies.Count);                        // every reply, once
        foreach (var line in lines)
        {
            Assert.True(line.StartsWith("OK t") || line.StartsWith("STATE t"), $"a line that is neither: '{line}'");
            var payload = line.Split(' ', 3)[2];
            Assert.True(payload.Length is 40 or 120 && payload.Distinct().Count() == 1, $"a line cut or mixed: '{line}'");
        }
        for (var t = 0; t < threads; t++)
        {
            // Each thread's replies in the order it said them: one FIFO for everyone keeps every sender's order.
            Assert.Equal(said[t], replies.Where(r => r.StartsWith($"OK t{t}-")).ToList());
        }
        Assert.True(lines.Count(l => l.StartsWith("STATE ")) <= threads * perThread);   // latest-wins: never more states than pushes
    }

    [Fact]
    public void ADisposedPeerTakesNothingMoreAndClosesItsSocket()
    {
        var stream = new GatedStream();
        var owner = new GatedStream();
        var peer = new WirePeer(stream, owner);
        peer.Dispose();
        Assert.True(peer.Closed);
        Assert.Equal("", peer.ClosedBecause);                          // closed by its owner, not by a fault of its own
        Assert.True(stream.Disposed);
        Assert.True(owner.Disposed);
        peer.Say("late");
        peer.Push("STATE late");
        Assert.Equal(0, peer.Pending);
        peer.Dispose();                                                // twice is fine
    }
}
