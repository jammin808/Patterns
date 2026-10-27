using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Show Bus 1.3 §13.20's scenarios through the Stagewatch link itself — the desk's <see cref="StagewatchLinkService"/>, its carrier
/// and its core together, as a desk runs them — against the scripted peer as a Stagewatch older than LINK (level 0). The peer emits
/// a stuck beat only to a connection a key opened, and answers a side call only with a session credential, so a connection that
/// carries the key is relinked within a beat or two by its 401 (13.7.3): S11 and S13 run on a connection that carries none, and
/// S12's beat runs against the core here and through the link against the C# Stagewatch (<see cref="ShowBusAppTests"/>).
/// </summary>
public sealed partial class PeerLinkWireTests
{
    /// <summary>
    /// S13 and S11 through the link, on this machine with no key: a pong that answers a ping the link sent is a clock sample, and
    /// one that answers none is counted on the link's record and never a sample. Silence is stale past <c>link.stale.ms</c>; past
    /// <c>link.drop.ms</c> the link drops it on its evaluator's own timer, counts one failed dial and dials again within 13.15.1's
    /// first wait.
    /// </summary>
    [AvaloniaFact]
    public void TheStagewatchLinkMatchesTheScriptedPeersPongsAndDropsItsSilence()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        var link = services.Kernel.Stagewatch;
        var config = vm.State.Control.Stagewatch;
        var peer = ScriptedPeer.Start();
        try
        {
            var listening = TestApp.Pump(peer.Ask(new JsonObject
            {
                ["do"] = "listen", ["carrier"] = "ws", ["tls"] = "plain", ["node"] = Node("main", StagewatchId), ["level"] = 0,
                ["clock"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
            }));
            config.Host = "127.0.0.1:" + Int(listening, "port").ToString(CultureInfo.InvariantCulture);
            config.ApiKey = "";
            config.Enabled = true;
            Dispatcher.UIThread.RunJobs();
            link.Reconcile();

            // S13: what answers a ping sent is a sample; what answers none is counted, and no sample.
            PumpUntil(() => link.Record is { State: "live", Clock.Samples: >= 1 }, 15000, link);
            TestApp.Pump(peer.Ask(new JsonObject { ["do"] = "set", ["pongs"] = "unmatched" }));
            PumpUntil(() => link.Record is { Counts.Unmatched: >= 1 }, 10000, link);
            var samples = link.Record!.Clock.Samples;                                                 // every pong before the first unmatched one is in
            PumpUntil(() => link.Record is { Counts.Unmatched: >= 3 }, 10000, link);
            Assert.True(samples == link.Record!.Clock.Samples, $"a sample came of an unmatched pong ({samples} before): {link.Record.ToJson()} · the pings and pongs: "
                + string.Join(" ", peer.Events.Where(e => Str(e, "ev") is "read" or "wrote").Select(e => Str(e, "message"))));
            TestApp.Pump(peer.Ask(new JsonObject { ["do"] = "set", ["pongs"] = true }));
            PumpUntil(() => link.Record!.Clock.Samples > samples, 10000, link);

            // S11: silent — stale, then dropped and dialled again within the first wait.
            Assert.Equal(0, link.Record!.Attempt);
            var dials = Dials(peer);
            TestApp.Pump(peer.Ask(new JsonObject { ["do"] = "set", ["pongs"] = false }));
            var quietFrom = Environment.TickCount64;
            PumpUntil(() => link.Record is { State: "stale" }, 10000, link);
            Assert.InRange(Environment.TickCount64 - quietFrom, (long)ShowBusLiveness.StaleAfter.TotalMilliseconds - 2000, (long)ShowBusLiveness.DropAfter.TotalMilliseconds);
            Assert.True(dials == Dials(peer), "dialled again before the silence was down: " + LinkLog(services, b.Dir));
            PumpUntil(() => link.Record is { State: "down" }, 15000, link);
            var droppedAt = Environment.TickCount64;
            Assert.InRange(droppedAt - quietFrom, (long)ShowBusLiveness.DropAfter.TotalMilliseconds - 2000, (long)ShowBusLiveness.DropAfter.TotalMilliseconds + 3000);
            PumpUntil(() => Dials(peer) > dials, 10000, link);
            Assert.True(Environment.TickCount64 - droppedAt < ShowBusLiveness.Redial(0, 1).TotalMilliseconds + PeerLink.CheckEvery.TotalMilliseconds + 1500,
                "dialled again later than 13.15.1's first wait allows");
            Assert.Equal(1, link.Record!.Attempt);                                                    // the drop, one failed dial
            Assert.Equal(0, link.CoreWritesWaiting);
        }
        finally
        {
            config.Enabled = false;
            link.Reconcile();
            Dispatcher.UIThread.RunJobs();
            TestApp.Pump(peer.DisposeAsync().AsTask().ContinueWith(_ => true, TaskScheduler.Default));
            b.Dispose();
        }
    }

    /// <summary>
    /// S19 through the link, with a canary key on this machine: the scripted peer's verdict takes the key over the upgrade's bearer
    /// and the link settles; each keyed side call is answered 401 with relink, and the link ends and dials again with no hold
    /// (13.7.3). Across all of it the key is in nothing the peer printed — it reports only that a bearer came — and in no line of
    /// this desk's log, no journal row, not STATE, the context, the link's record or its words; the core asked for nothing the link
    /// did not take.
    /// </summary>
    [AvaloniaFact]
    public void TheStagewatchLinksKeyReachesTheScriptedPeerOnlyAsItsBearerAndNoReaderOfThisDesk()
    {
        using var files = new KitFiles();
        var key = "swk_" + RandomNumberGenerator.GetString("abcdefghijkmnpqrstuvwxyz23456789", 32);
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        var link = services.Kernel.Stagewatch;
        var config = vm.State.Control.Stagewatch;
        var peer = ScriptedPeer.Start();
        try
        {
            var listening = TestApp.Pump(peer.Ask(new JsonObject
            {
                ["do"] = "listen", ["carrier"] = "ws", ["tls"] = "plain", ["node"] = Node("main", StagewatchId), ["level"] = 0,
                ["keys"] = new JsonArray(new JsonObject { ["file"] = files.Write("key", key), ["kind"] = "key" }),
            }));
            config.Host = "127.0.0.1:" + Int(listening, "port").ToString(CultureInfo.InvariantCulture);
            config.ApiKey = key;
            config.Enabled = true;
            Dispatcher.UIThread.RunJobs();
            link.Reconcile();

            PumpUntil(() => link.Key.Word == "key", 15000, link);
            PumpUntil(() => Count(peer, "served") >= 2 && Count(peer, "upgrade") >= 2, 30000, link);
            Assert.Equal(TimeSpan.Zero, link.KeyHeld);                                                // a relink holds nothing
            Assert.False(link.Key.Refused);
            Assert.All(peer.Events.Where(e => Str(e, "ev") == "upgrade"), e => Assert.Equal("bearer", Str(e, "authorization")));
            Assert.All(peer.Events.Where(e => Str(e, "ev") == "served"), e => Assert.Equal(401, e["status"]!.GetValue<int>()));

            TestApp.FlushFiles(services);
            var readers = new[]
            {
                string.Join("\n", peer.Events.Select(e => e.ToJsonString())),
                File.ReadAllText(Path.Combine(b.Dir, "patterns.log")),
                File.Exists(services.Journal.Path) ? File.ReadAllText(services.Journal.Path) : "",
                new CommandRouter(services).StateJson(),
                services.ContextJson(),
                link.Record?.ToJson() ?? "",
                link.Status,
                link.Sight.Words,
            };
            foreach (var canary in new[] { key, Convert.ToHexStringLower(PeerLink.KeyKey(key)) })
                Assert.All(readers, r => Assert.DoesNotContain(canary, r, StringComparison.OrdinalIgnoreCase));
            Assert.Equal(0, link.CoreWritesWaiting);
        }
        finally
        {
            config.Enabled = false;
            link.Reconcile();
            Dispatcher.UIThread.RunJobs();
            TestApp.Pump(peer.DisposeAsync().AsTask().ContinueWith(_ => true, TaskScheduler.Default));
            b.Dispose();
        }
    }

    /// <summary>
    /// S5 through the link, over TLS: a Stagewatch whose certificate a person pinned is dialled over TLS against that pin whatever
    /// a hello says of it — a hello that names no TLS, or another certificate — each said as a note on the link and never a
    /// refusal. The peer plays another machine's Stagewatch on this one.
    /// </summary>
    [AvaloniaFact]
    public void APinnedStagewatchIsDialledOverTlsWhateverAForgedHelloSaysOfIt()
    {
        using var files = new KitFiles();
        var key = "swk_" + RandomNumberGenerator.GetString("abcdefghijkmnpqrstuvwxyz23456789", 32);
        var cert = files.Cert("peer");
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        var link = services.Kernel.Stagewatch;
        var config = vm.State.Control.Stagewatch;
        var peer = ScriptedPeer.Start();
        try
        {
            var listening = TestApp.Pump(peer.Ask(new JsonObject
            {
                ["do"] = "listen", ["carrier"] = "ws", ["tls"] = "only", ["cert"] = cert.CertFile, ["certKey"] = cert.KeyFile, ["node"] = Node("main", StagewatchId),
                ["level"] = 0, ["keys"] = new JsonArray(new JsonObject { ["file"] = files.Write("key", key), ["kind"] = "key" }),
            }));
            var port = Int(listening, "port");
            link.IsLocal = _ => false;                                                                // loopback plays another machine
            config.SetPin("node:" + StagewatchId, TlsFingerprint.Prefix + cert.Fingerprint, PinTrust.Person);
            config.Host = "127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
            config.ApiKey = key;
            void Hello(string? tls)
            {
                var hello = new JsonObject
                {
                    ["t"] = "main", ["v"] = 1, ["proto"] = 3, ["id"] = StagewatchId, ["inst"] = "0a1b2c3d", ["name"] = "Hall A", ["host"] = "STAGE-PC",
                    ["ports"] = new JsonObject { ["http"] = port, ["ws"] = port }, ["caps"] = new JsonArray("context"),
                };
                if (tls is not null) hello["tls"] = tls;
                services.Kernel.Beacon.ShowBus.Hear(Encoding.UTF8.GetBytes(hello.ToJsonString()), "127.0.0.1", DateTime.UtcNow, "not-this-desk");
            }
            Hello(null);
            config.Enabled = true;
            Dispatcher.UIThread.RunJobs();
            link.Reconcile();

            // A hello that names no TLS: TLS against the pin all the same, the note said, nothing refused.
            PumpUntil(() =>
            {
                Hello(null);
                return link.Carrier == "tls" && link.Status.Contains("names no TLS", StringComparison.Ordinal);
            }, 20000, link);
            Assert.Equal("", link.PendingFingerprint);
            PumpUntil(() => Dials(peer) >= 1, 5000, link);                                           // the peer says its upgrade on its own line, after it

            // A hello that names another certificate: the pinned one is dialled, and the note says the pin decides.
            var other = TlsFingerprint.Prefix + new string('c', 64);
            PumpUntil(() =>
            {
                Hello(other);
                return link.Carrier == "tls" && link.Status.Contains("names another certificate", StringComparison.Ordinal);
            }, 20000, link);
            Assert.Equal("", link.PendingFingerprint);
            Assert.DoesNotContain("CERTIFICATE CHANGED", link.TlsWords, StringComparison.Ordinal);
            Assert.All(peer.Events.Where(e => Str(e, "ev") == "connected"), e => Assert.True(e["tls"]!.GetValue<bool>()));
        }
        finally
        {
            config.Enabled = false;
            link.Reconcile();
            link.IsLocal = System.Net.IPAddress.IsLoopback;
            Dispatcher.UIThread.RunJobs();
            TestApp.Pump(peer.DisposeAsync().AsTask().ContinueWith(_ => true, TaskScheduler.Default));
            b.Dispose();
        }
    }

    /// <summary>
    /// S6's first half through the link: a Stagewatch pinned by a person that now shows another certificate is CERTIFICATE CHANGED —
    /// refused on the link's record, nothing of this desk's sent (no upgrade, no message, no side call reaches the peer) — and the
    /// certificate it shows is looked at again no sooner than <c>peerlink.cert.probe.ms</c>. Accepting it, typed, is the desk's
    /// verb's, and reading the public view meanwhile is a MAY this link does not take.
    /// </summary>
    [AvaloniaFact]
    public void ACertificateChangedAtTheScriptedPeerGetsNothingOfThisDesksAndIsLookedAtNoSoonerThanItsProbe()
    {
        using var files = new KitFiles();
        var key = "swk_" + RandomNumberGenerator.GetString("abcdefghijkmnpqrstuvwxyz23456789", 32);
        var cert = files.Cert("peer");
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        var link = services.Kernel.Stagewatch;
        var config = vm.State.Control.Stagewatch;
        var peer = ScriptedPeer.Start();
        try
        {
            var listening = TestApp.Pump(peer.Ask(new JsonObject
            {
                ["do"] = "listen", ["carrier"] = "ws", ["tls"] = "only", ["cert"] = cert.CertFile, ["certKey"] = cert.KeyFile, ["node"] = Node("main", StagewatchId),
                ["level"] = 0, ["keys"] = new JsonArray(new JsonObject { ["file"] = files.Write("key", key), ["kind"] = "key" }),
            }));
            var port = Int(listening, "port").ToString(CultureInfo.InvariantCulture);
            link.IsLocal = _ => false;
            config.SetPin("addr:127.0.0.1:" + port, TlsFingerprint.Prefix + new string('a', 64), PinTrust.Person);
            config.Host = "127.0.0.1:" + port;
            config.ApiKey = key;
            config.Enabled = true;
            Dispatcher.UIThread.RunJobs();
            link.Reconcile();

            PumpUntil(() => link.PendingFingerprint == TlsFingerprint.Prefix + cert.Fingerprint, 15000, link);
            Assert.StartsWith("CERTIFICATE CHANGED", link.TlsWords, StringComparison.Ordinal);
            Assert.Equal(("refused", "certificate"), (link.Record!.State, link.Record.Refusal));
            PumpFor(3000);
            Assert.Equal(0, link.CertificateProbes);
            Assert.DoesNotContain(peer.Events, e => Str(e, "ev") is "upgrade" or "read" or "served");
            Assert.DoesNotContain(config.Pins, p => p.Fingerprint == TlsFingerprint.Prefix + cert.Fingerprint);
        }
        finally
        {
            config.Enabled = false;
            link.Reconcile();
            link.IsLocal = System.Net.IPAddress.IsLoopback;
            Dispatcher.UIThread.RunJobs();
            TestApp.Pump(peer.DisposeAsync().AsTask().ContinueWith(_ => true, TaskScheduler.Default));
            b.Dispose();
        }
    }

    // ---- the desk's side -----------------------------------------------------------------------------------------------

    /// <summary>The link's dials the peer took: each WebSocket upgrade (a side call is a connection of its own, and no dial).</summary>
    private static int Dials(ScriptedPeer peer) => Count(peer, "upgrade");

    /// <summary>The desk's log lines of its Stagewatch link, for a wait that did not come.</summary>
    private static string LinkLog(AppServices services, string dir)
    {
        TestApp.FlushFiles(services);
        return string.Join(" | ", File.ReadAllLines(Path.Combine(dir, "patterns.log")).Where(l => l.Contains("Stagewatch link", StringComparison.Ordinal)));
    }

    private static int Count(ScriptedPeer peer, string ev) => peer.Events.Count(e => Str(e, "ev") == ev);

    /// <summary>Runs the desk's dispatcher until <paramref name="done"/> holds; a wait that runs out says what the link said.</summary>
    private static void PumpUntil(Func<bool> done, int timeoutMs, StagewatchLinkService link)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!done())
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
            if (Environment.TickCount64 > deadline)
                throw new TimeoutException("the condition never came — " + link.Status + " · " + (link.Record?.ToJson() ?? "no record"));
        }
    }

    private static void PumpFor(int ms)
    {
        var until = Environment.TickCount64 + ms;
        while (Environment.TickCount64 < until)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
    }
}
