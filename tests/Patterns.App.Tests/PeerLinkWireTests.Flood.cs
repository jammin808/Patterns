using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Show Bus 1.3 §13.6.3 and §13.8.5's silent flood, timed from the desk's own accept: the connections dial one after another on one
/// task and are read with awaited reads, so no pool worker is held blocking while the desk's accept and its handlers wait for one, and
/// the assertions compare the closes with each other and with each connection's own dial — never with a clock the test's own
/// scheduling (its workers, the desk's thread it pumps) can move. Two legs make that scheduling bad on purpose: every idle pool worker
/// held, and the desk's thread busy for three seconds while the flood dials.
/// </summary>
public sealed partial class PeerLinkWireTests
{
    /// <summary>How far apart the held ones' closes may land, and how much sooner than the first byte's seconds after the one was shut.</summary>
    private const long FloodToleranceMs = 1000;

    /// <summary>How much later than the first byte's seconds after the one was shut the held ones may close with every idle worker held: the desk's timer itself then waits for the pool to make a worker.</summary>
    private const long FloodLateMs = 2000;

    /// <summary>How much later than its own deadline a close may land, timed from its own dial, while the pool has workers to give: the one shut at its accept, the held ones at the first byte's seconds.</summary>
    private const long OwnDialLateMs = 1500;

    /// <summary>Show Bus §14 <c>peerlink.unpaired.per.address</c>, the contract's number written here apart from the product's constant, so a flood one beyond it holds the desk to the contract.</summary>
    private const int SilentPlaces = 4;

    /// <summary>How early a close may land against its own dial's seconds: the desk's timer and this stopwatch read different clocks.</summary>
    private const long TimerSlackMs = 50;

    /// <summary>One silent connection of a flood, on the flood's stopwatch: its dial's start and return, its end, what it heard, and whether the desk shut it (false: the read's own bound ran out first).</summary>
    private readonly record struct SilentEnd(long DialMs, long ConnectedMs, long EndMs, int Heard, bool Shut);

    /// <summary>A flood's ends, the instant its last connect returned (the clock's start), and the thread pool beside it before and after.</summary>
    private sealed record SilentFlood(IReadOnlyList<SilentEnd> Ends, long StartMs, string PoolBefore, string PoolAfter)
    {
        /// <summary>What a failure says: each connection's connect-returned and close instants from the clock's start, and the pool — staggered connects say the workers were short, five late closes together say the desk's accept hop was.</summary>
        public override string ToString()
        {
            var inv = CultureInfo.InvariantCulture;
            var each = Ends.Select((e, i) => string.Create(inv,
                $"#{i} dialled {e.DialMs - StartMs:+0;-0} ms, connected {e.ConnectedMs - StartMs:+0;-0} ms, ended {e.EndMs - StartMs:+0;-0} ms, heard {e.Heard}{(e.Shut ? "" : ", not shut within its 9 s")}"));
            return string.Create(inv, $"the clock started when the last connect returned, {StartMs} ms after the first dial; ") + string.Join("; ", each)
                + "; the pool before: " + PoolBefore + "; after: " + PoolAfter;
        }
    }

    private static string Pool() => string.Create(CultureInfo.InvariantCulture, $"{ThreadPool.ThreadCount} threads, {ThreadPool.PendingWorkItemCount} items waiting");

    /// <summary>
    /// The flood started: <paramref name="count"/> connections from this machine that say nothing, dialled one after another on one task,
    /// then each read awaited on its own under its own 9 s bound, each end timed as it happens.
    /// </summary>
    private static Task<(SilentEnd[] Ends, long StartMs)> StartFlood(int port, int count) => Task.Run(async () =>
    {
        var clock = Stopwatch.StartNew();
        var dialled = new List<(TcpClient Client, long DialMs, long ConnectedMs)>();
        try
        {
            for (var i = 0; i < count; i++)
            {
                var client = new TcpClient();
                var dial = clock.ElapsedMilliseconds;
                dialled.Add((client, dial, -1));
                await client.ConnectAsync(IPAddress.Loopback, port);
                dialled[^1] = (client, dial, clock.ElapsedMilliseconds);
            }
            var start = clock.ElapsedMilliseconds;
            async Task<SilentEnd> Read((TcpClient Client, long DialMs, long ConnectedMs) c)
            {
                var heard = 0;
                var chunk = new byte[16];
                using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(9));
                try
                {
                    int n;
                    while ((n = await c.Client.GetStream().ReadAsync(chunk, bound.Token)) > 0) heard += n;
                }
                catch (IOException)
                {
                    // a reset is the door closed too
                }
                catch (OperationCanceledException)
                {
                    return new SilentEnd(c.DialMs, c.ConnectedMs, clock.ElapsedMilliseconds, heard, Shut: false);
                }
                return new SilentEnd(c.DialMs, c.ConnectedMs, clock.ElapsedMilliseconds, heard, Shut: true);
            }
            return (await Task.WhenAll(dialled.Select(Read)), start);
        }
        finally
        {
            foreach (var c in dialled) c.Client.Dispose();
        }
    });

    /// <summary>The flood's ends once it is over, the desk's thread pumped while it runs.</summary>
    private static SilentFlood Collected(Func<Task<(SilentEnd[] Ends, long StartMs)>?> started, string poolBefore)
    {
        PumpWhile(() => started() is not { IsCompleted: true }, 30000);
        var (ends, start) = started()!.GetAwaiter().GetResult();
        return new SilentFlood(ends, start, poolBefore, Pool());
    }

    private static SilentFlood Flood(int port, int count)
    {
        var before = Pool();
        var flood = StartFlood(port, count);
        return Collected(() => flood, before);
    }

    /// <summary>
    /// The wire's promise from the desk's own accept, whatever scheduled it: nothing is written to any of the flood; exactly one — the
    /// connection beyond the address's silent places — is shut apart from the others, at least a second ahead of them, before any of
    /// them could have closed; the others are held the first byte's seconds after it was shut, together, and never less than those
    /// seconds from their own dial, before which the desk cannot have accepted them. While the pool has workers to give
    /// (<paramref name="poolHeld"/> false) each close is held to its own dial too: the one at its accept, the others at the first byte's
    /// seconds — a desk that accepted the whole flood late would pass the comparisons with each other, never these.
    /// </summary>
    private static void HeldFromTheDesksOwnAccept(SilentFlood flood, double firstByteSeconds, string leg = "", bool poolHeld = false)
    {
        var seconds = (long)(firstByteSeconds * 1000);
        var late = poolHeld ? FloodLateMs : OwnDialLateMs;
        var said = (leg.Length > 0 ? leg + "; " : "") + flood.ToString();
        Assert.True(flood.Ends.All(e => e.Shut && e.Heard == 0), "each of the flood shut by the desk with nothing written to it: " + said);
        var order = flood.Ends.OrderBy(e => e.EndMs).ToArray();
        var (one, held) = (order[0], order[1..]);
        Assert.True(held[0].EndMs - one.EndMs >= 1000 && held[^1].EndMs - held[0].EndMs <= FloodToleranceMs,
            "exactly one shut apart from the others, at least a second ahead of them: " + said);
        Assert.True(held.All(e => e.EndMs - one.EndMs >= seconds - FloodToleranceMs),
            "the one shut before any of the others could have closed: they were held the first byte's seconds after it: " + said);
        Assert.True(held.All(e => e.EndMs - one.EndMs <= seconds + late), "the others shut at the first byte's seconds, not held past them: " + said);
        Assert.True(held.All(e => e.EndMs - e.DialMs >= seconds - TimerSlackMs), "none of the others shut sooner than the first byte's seconds from its own dial: " + said);
        if (poolHeld) return;
        Assert.True(one.EndMs - one.DialMs <= OwnDialLateMs, "the one shut at its accept, timed from its own dial: " + said);
        Assert.True(held.All(e => e.EndMs - e.DialMs <= seconds + OwnDialLateMs), "the others shut at the first byte's seconds from their own dial: " + said);
    }

    /// <summary>
    /// Every idle pool worker held in a blocking read on a loopback socket nobody answers — clients that block a worker each, at the
    /// pool's whole width — each counted as it starts; released when disposed. Items are queued one at a time: one that starts within the quiet wait
    /// took an idle worker (or one the pool makes at once below its minimum); none starting within it means none is idle, and that last
    /// item returns at once whenever a worker reaches it. At most <see cref="MostHeld"/> are held, whatever the pool's pace.
    /// </summary>
    private sealed class HeldPool : IDisposable
    {
        /// <summary>The most workers held: far above the pool's idle width at the start of a test, so the quiet wait ends the holding.</summary>
        private const int MostHeld = 512;

        private readonly TcpListener _deaf = new(IPAddress.Loopback, 0);
        private readonly List<TcpClient> _clients = [];
        private readonly object _gate = new();
        private bool _enough;
        private int _held;

        public HeldPool()
        {
            _deaf.Start();
            var port = ((IPEndPoint)_deaf.LocalEndpoint).Port;
            while (true)
            {
                var before = Held;
                ThreadPool.QueueUserWorkItem(_ => Hold(port));
                var quiet = Stopwatch.StartNew();
                while (Held == before && quiet.ElapsedMilliseconds < 200) Thread.Sleep(1);
                if (Held == before || Held >= MostHeld) break;
            }
            lock (_gate) _enough = true;
        }

        /// <summary>How many workers are held.</summary>
        public int Held
        {
            get
            {
                lock (_gate) return _held;
            }
        }

        private void Hold(int port)
        {
            var client = new TcpClient();
            lock (_gate)
            {
                if (_enough)
                {
                    client.Dispose();
                    return;
                }
                _clients.Add(client);
                _held++;
            }
            try
            {
                client.Connect(IPAddress.Loopback, port);
                _ = client.GetStream().Read(new byte[1], 0, 1);
            }
            catch (Exception ex) when (ex is IOException or SocketException or ObjectDisposedException or InvalidOperationException)
            {
                // released: the socket was closed under the read
            }
        }

        public void Dispose()
        {
            TcpClient[] clients;
            lock (_gate)
            {
                _enough = true;
                clients = [.. _clients];
            }
            foreach (var c in clients) c.Dispose();
            _deaf.Stop();
        }
    }

    [AvaloniaFact]
    public void ASilentFloodIsTimedFromTheDesksOwnAcceptWithEveryIdleWorkerHeldAndWithTheDesksThreadBusy()
    {
        using var desk = new AcceptorDesk();
        const double firstByte = 2;
        desk.Control.WireLimits = new WireLimits(FirstByteSeconds: firstByte);
        const int count = SilentPlaces + 1;

        // Every idle worker held before the flood: the desk's handlers, its timers and the flood's own continuations wait for the pool
        // to make more, and the promise is still kept from the desk's own accept.
        using (var held = new HeldPool())
        {
            HeldFromTheDesksOwnAccept(Flood(desk.Port, count), firstByte, string.Create(CultureInfo.InvariantCulture, $"{held.Held} idle workers held"), poolHeld: true);
        }

        // The desk's thread busy for three seconds while the flood dials: the flood is started from the job itself, so whatever of the
        // accept waits for a pass of that thread waits the whole job, and the promise is still kept from the desk's own accept.
        var before = Pool();
        Task<(SilentEnd[] Ends, long StartMs)>? started = null;
        Dispatcher.UIThread.Post(() =>
        {
            started = StartFlood(desk.Port, count);
            Thread.Sleep(3000);
        });
        HeldFromTheDesksOwnAccept(Collected(() => started, before), firstByte, "the desk's thread busy for 3 s");
    }
}
