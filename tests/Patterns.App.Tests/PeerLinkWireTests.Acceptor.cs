using System.Diagnostics;
using System.Net;
using System.Net.Security;
using System.Net.Sockets;
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
/// Show Bus 1.3 §13.6.1–13.6.4 at this desk's own wire, 9697: the scripted peer dials the desk as Stagewatch would and the desk is
/// the acceptor, on real sockets from loopback with this machine's trust off (and restored in finally). The pairing token is proved
/// as a code; a key reaches the desk only as the test plants it, since this build mints none. S1 both ways, plain and over TLS
/// against the desk's certificate; S2 and S3 in one set of words, the unknown kid never counted; S8 and S9; 13.5.6's table per
/// (address, kid) beside the legacy proof; S15's bounds; S16's unpaired flood, the handshake's seconds and the app links' sub-cap;
/// S19's canaries in every reader; S20's loopback that LINK never trusts.
/// </summary>
public sealed partial class PeerLinkWireTests
{
    private const string KeyAlphabet = "abcdefghijkmnpqrstuvwxyz23456789";

    /// <summary>A desk listening on free ports with a pairing token, this machine's trust off; disposed with its peer and trust restored.</summary>
    private sealed class AcceptorDesk : IDisposable
    {
        private readonly bool _trusted = ControlService.TrustLoopback;
        private readonly bool _plainAuth = ControlService.PlainAuthOnLoopback;

        public AcceptorDesk(bool trustLoopback = false)
        {
            ControlService.TrustLoopback = trustLoopback;
            ControlService.PlainAuthOnLoopback = false;
            TestApp.Booted? booted = null;
            try
            {
                booted = TestApp.Boot();
                Booted = booted;
                Token = PairingToken.New();
                Booted.Vm.State.Control.HttpPort = FreePort();
                Booted.Vm.State.Control.TcpPort = FreePort();
                Booted.Vm.State.Control.Token = Token;
                Dispatcher.UIThread.RunJobs();
                Peer = ScriptedPeer.Start();
            }
            catch
            {
                // A desk that did not come up leaves no static changed behind it: Dispose is never called for it.
                booted?.Dispose();
                ControlService.TrustLoopback = _trusted;
                ControlService.PlainAuthOnLoopback = _plainAuth;
                throw;
            }
        }

        public TestApp.Booted Booted { get; }

        public AppServices Services => Booted.Services;

        public ControlService Control => Booted.Services.Control;

        public ScriptedPeer Peer { get; }

        public string Token { get; }

        public int Port => Booted.Vm.State.Control.TcpPort;

        public string Self => Booted.Services.Kernel.ShowBusId;

        /// <summary>The desk's certificate as a connector pins it: 64 hex.</summary>
        public string Pin => Booted.Services.Kernel.Tls.Fingerprint["sha256:".Length..];

        public void Dispose()
        {
            try
            {
                TestApp.Pump(Peer.DisposeAsync().AsTask().ContinueWith(_ => true, TaskScheduler.Default));
                Booted.Dispose();
            }
            finally
            {
                ControlService.TrustLoopback = _trusted;
                ControlService.PlainAuthOnLoopback = _plainAuth;
            }
        }
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static string NewKey() => "ptk_" + RandomNumberGenerator.GetString(KeyAlphabet, 32);

    /// <summary>The peer dialling this desk as Stagewatch, proving what the file holds, meaning this desk's id.</summary>
    private static JsonObject DialDesk(AcceptorDesk desk, bool tls, string file, string kind, JsonObject? attack = null)
    {
        var dial = new JsonObject
        {
            ["do"] = "dial", ["carrier"] = "line", ["port"] = desk.Port, ["tls"] = tls, ["pin"] = tls ? desk.Pin : null,
            ["node"] = Node("main", StagewatchId), ["key"] = new JsonObject { ["file"] = file, ["kind"] = kind },
            ["meant"] = Node("patterns", desk.Self), ["name"] = "Stagewatch",
        };
        if (attack is not null) dial["attack"] = attack;
        return dial;
    }

    /// <summary>Dials and waits for the connector's end of LINK on that connection: its <c>link</c> event, or its <c>refused</c>.</summary>
    private static (int Conn, JsonObject End) Dialled(AcceptorDesk desk, JsonObject dial)
    {
        var connected = TestApp.Pump(desk.Peer.Ask(dial));
        var conn = Int(connected, "conn");
        var end = TestApp.Pump(desk.Peer.Wait(e => Str(e, "ev") is "link" or "refused" && Str(e, "role") == "connector" && Int(e, "conn") == conn));
        return (conn, end);
    }

    private static JsonObject ReadOn(AcceptorDesk desk, int conn, Func<string, bool> match)
        => TestApp.Pump(desk.Peer.Wait(e => Str(e, "ev") == "read" && Int(e, "conn") == conn && match(Str(e, "line") ?? "")));

    private static void ClosedByDesk(AcceptorDesk desk, int conn)
        => Assert.Equal("other", Str(TestApp.Pump(desk.Peer.Wait(e => Str(e, "ev") == "closed" && Int(e, "conn") == conn)), "by"));

    private static void Send(AcceptorDesk desk, int conn, string line)
        => TestApp.Pump(desk.Peer.Ask(new JsonObject { ["do"] = "send", ["conn"] = conn, ["line"] = line }));

    private static void PumpWhile(Func<bool> waiting, int timeoutMs)
    {
        var clock = Stopwatch.StartNew();
        while (waiting())
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
            if (clock.ElapsedMilliseconds > timeoutMs) throw new TimeoutException("the condition never came");
        }
    }

    private static string Journal(AppServices services)
    {
        TestApp.FlushFiles(services);
        return File.Exists(services.Journal.Path) ? File.ReadAllText(services.Journal.Path) : "";
    }

    private static IReadOnlyList<ShowLogEntry> Rows(AppServices services, string kind)
    {
        TestApp.FlushFiles(services);
        return services.Journal.Tail(500).Where(r => r.Kind == kind).ToList();
    }

    [AvaloniaFact]
    public void ALinkOnTheWireProvesTheTokenAndAKeyBothWaysPlainAndOverTlsAndRunsAVerbWithNoSecretInAnyReader()
    {
        using var files = new KitFiles();
        using var desk = new AcceptorDesk();
        var key = NewKey();
        desk.Control.LinkKeys = [LinkAcceptor.Key(key, "main:" + StagewatchId, "operate")!];

        // S1 on plain: the token proved as a code, both ways; the verdict says so and the STATE greeting follows it, never before.
        var (plain, linked) = Dialled(desk, DialDesk(desk, tls: false, files.Write("code", desk.Token), "code"));
        Assert.Equal(("link", "code", "operate", 1), (Str(linked, "ev"), Str(linked, "verified"), Str(linked, "scope"), Int(linked, "level")));
        Assert.Equal(desk.Services.Kernel.Beacon.Instance, Str(linked, "inst"));
        var lid = Str(linked, "lid")!;
        var reads = desk.Peer.Events.Where(e => Str(e, "ev") == "read" && Int(e, "conn") == plain).Select(e => Str(e, "line") ?? "").ToList();
        var verdictAt = reads.FindIndex(l => l.StartsWith("OK @l2 paired {", StringComparison.Ordinal));
        Assert.True(verdictAt >= 0, string.Join(" | ", reads));
        Assert.DoesNotContain(reads.Take(verdictAt), l => l.StartsWith("STATE ", StringComparison.Ordinal));
        Assert.StartsWith("OK @l1 LINK 1 patterns " + desk.Self + " ", reads.Single(l => l.StartsWith("OK @l1 ", StringComparison.Ordinal)), StringComparison.Ordinal);
        Assert.EndsWith(" plain", reads.Single(l => l.StartsWith("OK @l1 ", StringComparison.Ordinal)), StringComparison.Ordinal);
        ReadOn(desk, plain, l => l.StartsWith("STATE {", StringComparison.Ordinal));
        // One lid at both ends: the desk's journal row names the lid the connector derived.
        PumpWhile(() => !Rows(desk.Services, "Link").Any(r => r.Message.EndsWith("lid " + lid, StringComparison.Ordinal)), 5000);
        Assert.Equal(1, desk.Control.AppLinks);

        // A command applied, and a matched pong.
        Send(desk, plain, "@s1-11aa22bb BLACKOUT ON");
        ReadOn(desk, plain, l => l == "OK @s1-11aa22bb");
        PumpWhile(() => !desk.Services.State.Blackout, 5000);
        Send(desk, plain, "@p1 PING 123456");
        Assert.StartsWith("OK @p1 PONG 123456 ", Str(ReadOn(desk, plain, l => l.StartsWith("OK @p1", StringComparison.Ordinal)), "line"), StringComparison.Ordinal);

        // S1 over TLS, pinned: the offer's bind is the desk's own certificate, a planted key proves, and its verb runs at operate.
        var (overTls, keyed) = Dialled(desk, DialDesk(desk, tls: true, files.Write("key", key), "key"));
        Assert.Equal(("link", "key", "operate"), (Str(keyed, "ev"), Str(keyed, "verified"), Str(keyed, "scope")));
        Assert.EndsWith(" tls:sha256:" + desk.Pin, Str(ReadOn(desk, overTls, l => l.StartsWith("OK @l1 ", StringComparison.Ordinal)), "line"), StringComparison.Ordinal);
        Send(desk, overTls, "@s2-11aa22bb BLACKOUT OFF");
        ReadOn(desk, overTls, l => l == "OK @s2-11aa22bb");
        PumpWhile(() => desk.Services.State.Blackout, 5000);
        Assert.Equal(2, desk.Control.AppLinks);

        // 13.5.3: a key below operate reads and runs nothing more, refused in words that name its scope.
        var follower = NewKey();
        desk.Control.LinkKeys = [.. desk.Control.LinkKeys, LinkAcceptor.Key(follower, "main:" + StagewatchId, "follow")!];
        var (reader, followed) = Dialled(desk, DialDesk(desk, tls: true, files.Write("follower", follower), "key"));
        Assert.Equal("follow", Str(followed, "scope"));
        ReadOn(desk, reader, l => l.StartsWith("STATE {", StringComparison.Ordinal));
        Send(desk, reader, "@s3-11aa22bb BLACKOUT ON");
        Assert.Equal("ERR @s3-11aa22bb " + LinkAcceptor.ScopeWords("follow", desk.Services.Kernel.Beacon.MachineName),
            Str(ReadOn(desk, reader, l => l.Contains("@s3-11aa22bb", StringComparison.Ordinal)), "line"));
        Send(desk, reader, "@q1 STATUS");
        Assert.StartsWith("OK @q1 {", Str(ReadOn(desk, reader, l => l.StartsWith("OK @q1", StringComparison.Ordinal) || l.StartsWith("ERR @q1", StringComparison.Ordinal)), "line"), StringComparison.Ordinal);
        Assert.False(desk.Services.State.Blackout);

        // S19: no key, no form of the token, no K, no proof and no nonce in the log, the journal, STATE or the context.
        var wrote = desk.Peer.Events.Where(e => Str(e, "ev") is "wrote" or "read").Select(e => Str(e, "line") ?? "").ToList();
        var proofs = wrote.Where(l => l.Contains(" LINK PROOF ", StringComparison.Ordinal)).Select(l => l[^64..])
            .Concat(wrote.Where(l => l.Contains(" paired {", StringComparison.Ordinal)).Select(l => System.Text.Json.JsonDocument.Parse(l[(l.IndexOf('{', StringComparison.Ordinal))..]).RootElement.GetProperty("proof").GetString()!))
            .ToList();
        var nonces = wrote.Where(l => l.Contains(" LINK 1 ", StringComparison.Ordinal)).Select(l => l.Split(' ')).Select(w => w.Length >= 7 ? w[^2] : "").Where(n => n.Length == 32).ToList();
        Assert.Equal(6, proofs.Count);                                                            // three client proofs and three server proofs
        Assert.Equal(6, nonces.Count);                                                            // three cnonces and three snonces
        TestApp.FlushFiles(desk.Services);
        var readers = new[]
        {
            File.ReadAllText(Path.Combine(desk.Booted.Dir, "patterns.log")),
            Journal(desk.Services),
            new CommandRouter(desk.Services).StateJson(),
            desk.Services.ContextJson(),
        };
        var canaries = CodeForms(desk.Token).Concat([key, follower, Convert.ToHexStringLower(PeerLink.KeyKey(key)), Convert.ToHexStringLower(PeerLink.CodeKey(PeerLink.Canon(desk.Token)!))])
            .Concat(proofs).Concat(nonces);
        foreach (var canary in canaries)
            Assert.All(readers, r => Assert.DoesNotContain(canary, r, StringComparison.OrdinalIgnoreCase));
        Assert.Contains(Rows(desk.Services, "Link"), r => r.Target == "main:" + StagewatchId
            && r.Message == "main:" + StagewatchId + " from 127.0.0.1 linked by LINK — code (kid code), scope operate, line plain, lid " + lid);
    }

    [AvaloniaFact]
    public void AWrongProofAndAnUnknownKidAreRefusedInTheSameWordsTheConnectionEndedAndOnlyTheKnownKidCounted()
    {
        using var files = new KitFiles();
        using var desk = new AcceptorDesk();
        var bound = NewKey();
        desk.Control.LinkKeys = [LinkAcceptor.Key(bound, "playmap:0a1b2c3d", "drive")!];

        // S2: a wrong token is refused in 13.6.3's words and the desk ends the connection; the table counts its pair.
        var (wrong, refused) = Dialled(desk, DialDesk(desk, tls: false, files.Write("wrong", PairingToken.New()), "code"));
        Assert.Equal(("refused", "refused", PeerLink.ProofWords), (Str(refused, "ev"), Str(refused, "class"), Str(refused, "words")));
        ClosedByDesk(desk, wrong);
        Assert.Equal(1, desk.Control.WrongProofPairs);

        // S3: a kid this desk does not hold: the same words byte for byte, the connection ended, nothing counted. (That the compare
        // takes the same time is PeerLink.Verify's fixed-time compare against a K of the unknown kid's own; no clock is read here.)
        var (unknown, alike) = Dialled(desk, DialDesk(desk, tls: true, files.Write("unknown", NewKey()), "key"));
        Assert.Equal((Str(refused, "class"), Str(refused, "words")), (Str(alike, "class"), Str(alike, "words")));
        var said = desk.Peer.Events.Where(e => Str(e, "ev") == "read" && (Str(e, "line") ?? "").StartsWith("ERR @l2 ", StringComparison.Ordinal)).Select(e => Str(e, "line")).ToList();
        Assert.Equal(2, said.Count);
        Assert.Equal(said[0], said[1]);
        ClosedByDesk(desk, unknown);
        Assert.Equal(1, desk.Control.WrongProofPairs);

        // 13.6.7: a token LINK cannot prove is no open desk — the code's kid is unknown here, refused in the same words, uncounted.
        desk.Booted.Vm.State.Control.Token = "K7QM-3XWD-P9R\u00c4";
        Dispatcher.UIThread.RunJobs();
        var (shapeless, unprovable) = Dialled(desk, DialDesk(desk, tls: false, files.Write("shapeless", PairingToken.New()), "code"));
        Assert.Equal(("refused", PeerLink.ProofWords), (Str(unprovable, "class"), Str(unprovable, "words")));
        ClosedByDesk(desk, shapeless);
        Assert.Equal(1, desk.Control.WrongProofPairs);
        // And a desk with no token and no key is open: LINK is answered open, refusing nothing (the connection is the desk's already).
        desk.Booted.Vm.State.Control.Token = "";
        var keys = desk.Control.LinkKeys;
        desk.Control.LinkKeys = [];
        Dispatcher.UIThread.RunJobs();
        var openConnected = TestApp.Pump(desk.Peer.Ask(DialDesk(desk, tls: false, files.Write("none", PairingToken.New()), "code")));
        var open = TestApp.Pump(desk.Peer.Wait(e => Str(e, "ev") == "open" && Int(e, "conn") == Int(openConnected, "conn")));
        Assert.Equal(LinkAcceptor.OpenWords, Str(open, "words"));
        desk.Control.LinkKeys = keys;
        desk.Booted.Vm.State.Control.Token = desk.Token;
        Dispatcher.UIThread.RunJobs();

        // 13.5.2: a key bound to another connector: a binding refusal, the connection ended, never counted.
        var (other, binding) = Dialled(desk, DialDesk(desk, tls: false, files.Write("bound", bound), "key"));
        Assert.Equal(("binding", "this key speaks for PlayMap 0a1b2c3d"), (Str(binding, "class"), Str(binding, "words")));
        ClosedByDesk(desk, other);
        Assert.Equal((1, 0), (desk.Control.WrongProofPairs, desk.Control.AppLinks));
        Assert.DoesNotContain(desk.Control.Decks, d => d.Paired);
    }

    [AvaloniaFact]
    public void AReplayedLinkAForgedKidAServerProofAsAClientsAndASecondProofAreRefused()
    {
        using var files = new KitFiles();
        using var desk = new AcceptorDesk();
        var key = NewKey();
        var planted = LinkAcceptor.Key(key, "main:" + StagewatchId, "operate")!;
        desk.Control.LinkKeys = [planted];
        var code = files.Write("code", desk.Token);

        // A second proof on a connection whose LINK verified is refused, and the link it has stays (13.6.3: one proof a connection).
        var (honest, linked) = Dialled(desk, DialDesk(desk, tls: false, code, "code", new JsonObject { ["twice"] = true }));
        Assert.Equal("link", Str(linked, "ev"));
        var again = TestApp.Pump(desk.Peer.Wait(e => Str(e, "ev") == "refused" && Int(e, "conn") == honest));
        Assert.Equal(("malformed", LinkAcceptor.OneLinkWords), (Str(again, "class"), Str(again, "words")));
        Send(desk, honest, "@s1-11aa22bb BLACKOUT ON");
        ReadOn(desk, honest, l => l == "OK @s1-11aa22bb");

        // S8: that LINK replayed as it was sent — its cnonce, kid and client proof — meets a fresh snonce and does not verify.
        var (replayed, replay) = Dialled(desk, DialDesk(desk, tls: false, code, "code", new JsonObject { ["replay"] = honest }));
        Assert.Equal(("refused", PeerLink.ProofWords), (Str(replay, "class"), Str(replay, "words")));
        ClosedByDesk(desk, replayed);

        // S8: a forged kid — the planted key's, proved with the token — is a wrong proof for that kid.
        var (forged, forgery) = Dialled(desk, DialDesk(desk, tls: false, code, "code", new JsonObject { ["kid"] = planted.Kid }));
        Assert.Equal(("refused", PeerLink.ProofWords), (Str(forgery, "class"), Str(forgery, "words")));
        ClosedByDesk(desk, forged);

        // S9: the connector's server proof offered as its client proof.
        var (reflected, role) = Dialled(desk, DialDesk(desk, tls: true, code, "code", new JsonObject { ["role"] = true }));
        Assert.Equal(("refused", PeerLink.ProofWords), (Str(role, "class"), Str(role, "words")));
        ClosedByDesk(desk, reflected);
        Assert.Equal(1, desk.Control.AppLinks);                                                   // the honest link alone
    }

    [AvaloniaFact]
    public void FiveWrongProofsLockTheirPairForTheMinuteAndNeitherAnotherKeyNorTheLegacyProof()
    {
        using var files = new KitFiles();
        using var desk = new AcceptorDesk();
        var now = 5_000_000L;
        desk.Control.LinkNowMs = () => now;
        var key = NewKey();
        desk.Control.LinkKeys = [LinkAcceptor.Key(key, "main:" + StagewatchId, "operate")!];
        var wrongToken = files.Write("wrong", PairingToken.New());
        for (var i = 0; i < LinkAcceptor.WrongProofsPerMinute; i++)
        {
            var (_, refused) = Dialled(desk, DialDesk(desk, tls: false, wrongToken, "code"));
            Assert.Equal("refused", Str(refused, "class"));
        }

        // The pair (this address, code) is locked: the right token is refused before its proof is checked.
        var code = files.Write("code", desk.Token);
        var (_, locked) = Dialled(desk, DialDesk(desk, tls: false, code, "code"));
        Assert.Equal(("locked", LinkAcceptor.ProofsLocked), (Str(locked, "class"), Str(locked, "words")));
        Assert.Single(Rows(desk.Services, "LinkLocked"));

        // Another kid from the same address proves, and the legacy proof pairs a deck: the table locks no other door.
        Assert.Equal("link", Str(Dialled(desk, DialDesk(desk, tls: false, files.Write("key", key), "key")).End, "ev"));
        using (var deck = TestApp.Pump(Lines.Dial(desk.Port, tls: false)))
        {
            TestApp.Pump(deck.Send("HELLO FOH deck").ContinueWith(_ => true, TaskScheduler.Default));
            var nonce = TestApp.Pump(deck.NextMatching(l => l.StartsWith("NONCE ", StringComparison.Ordinal)))["NONCE ".Length..];
            TestApp.Pump(deck.Send("AUTH-PROOF " + WireProof.Proof(desk.Token, nonce, "FOH deck")).ContinueWith(_ => true, TaskScheduler.Default));
            Assert.Equal("OK paired", TestApp.Pump(deck.NextMatching(l => l.StartsWith("OK paired", StringComparison.Ordinal) || l.StartsWith("ERR", StringComparison.Ordinal))));
        }

        // The minute over, the token proves again.
        now += 60_001;
        var (_, unlocked) = Dialled(desk, DialDesk(desk, tls: false, code, "code"));
        Assert.Equal(("link", "code"), (Str(unlocked, "ev"), Str(unlocked, "verified")));
    }

    [AvaloniaFact]
    public void AnUnpairedFloodHoldsFourPlacesAnAddressALinkWithoutItsProofIsClosedAtTheHandshakeAndAppLinksKeepToTheirSubCap()
    {
        using var files = new KitFiles();
        using var desk = new AcceptorDesk();
        var code = files.Write("code", desk.Token);
        desk.Control.WireLimits = new WireLimits(FirstByteSeconds: 4, MaxAppLinksPerAddress: 1);

        // S16: five connections that say nothing; four hold their places to the first byte's seconds, one is closed at once.
        var clock = Stopwatch.StartNew();
        var flood = Enumerable.Range(0, 5).Select(_ => Task.Run(() =>
        {
            using var c = new TcpClient();
            c.Connect(IPAddress.Loopback, desk.Port);
            c.ReceiveTimeout = 9000;
            var n = 0;
            try
            {
                n = c.GetStream().Read(new byte[16], 0, 16);
            }
            catch (IOException)
            {
                // a reset is the door closed too
            }
            return (Bytes: n, Ms: clock.ElapsedMilliseconds);
        })).ToArray();
        var ended = flood.Select(t => TestApp.Pump(t)).ToList();
        Assert.All(ended, e => Assert.Equal(0, e.Bytes));                                        // nothing written to any of them
        Assert.Single(ended, e => e.Ms < 2500);                                                   // the fifth, at once
        Assert.Equal(4, ended.Count(e => e.Ms >= 3500));                                          // the four, at the first byte's seconds

        // 13.6.3: four LINKs with no proof hold their address's places: a connection that spoke before them and asks LINK now is
        // refused busy and closed — at most four unfinished. Their places are apart from the silent ones, so a deck from the same
        // machine still connects and pairs while they wait.
        desk.Control.WireLimits = new WireLimits(HandshakeSeconds: 5, MaxAppLinksPerAddress: 1);
        var late = TestApp.Pump(Lines.Dial(desk.Port, tls: false));
        var asks = new List<Lines>();
        try
        {
            TestApp.Pump(late.Send("HELLO late").ContinueWith(_ => true, TaskScheduler.Default));
            TestApp.Pump(late.NextMatching(x => x == "OK"));
            string Ask(Lines l)
            {
                TestApp.Pump(l.Send("@l1 LINK 1 main " + StagewatchId + " " + Nonce() + " code").ContinueWith(_ => true, TaskScheduler.Default));
                return TestApp.Pump(l.NextMatching(x => x.StartsWith("OK @l1", StringComparison.Ordinal) || x.StartsWith("ERR @l1", StringComparison.Ordinal)));
            }
            for (var i = 0; i < 4; i++)
            {
                asks.Add(TestApp.Pump(Lines.Dial(desk.Port, tls: false)));
                Assert.StartsWith("OK @l1 LINK 1 patterns ", Ask(asks[i]), StringComparison.Ordinal);
            }
            Assert.Equal("ERR @l1 " + LinkAcceptor.Refusal("busy", LinkAcceptor.UnfinishedWords), Ask(late));
            using var waitingDeck = TestApp.Pump(Lines.Dial(desk.Port, tls: false));
            TestApp.Pump(waitingDeck.Send("HELLO FOH deck").ContinueWith(_ => true, TaskScheduler.Default));
            var deckNonce = TestApp.Pump(waitingDeck.NextMatching(l => l.StartsWith("NONCE ", StringComparison.Ordinal)))["NONCE ".Length..];
            TestApp.Pump(waitingDeck.Send("AUTH-PROOF " + WireProof.Proof(desk.Token, deckNonce, "FOH deck")).ContinueWith(_ => true, TaskScheduler.Default));
            Assert.Equal("OK paired", TestApp.Pump(waitingDeck.NextMatching(l => l.StartsWith("OK paired", StringComparison.Ordinal) || l.StartsWith("ERR", StringComparison.Ordinal))));
        }
        finally
        {
            late.Dispose();
            foreach (var l in asks) l.Dispose();
        }

        // 13.6.3: a LINK whose proof does not come within the handshake's seconds is closed.
        desk.Control.WireLimits = new WireLimits(HandshakeSeconds: 0.5, MaxAppLinksPerAddress: 1);
        using (var slow = TestApp.Pump(Lines.Dial(desk.Port, tls: false)))
        {
            TestApp.Pump(slow.Send("@l1 LINK 1 main " + StagewatchId + " " + Nonce() + " code").ContinueWith(_ => true, TaskScheduler.Default));
            TestApp.Pump(slow.NextMatching(x => x.StartsWith("OK @l1 LINK", StringComparison.Ordinal)));
            var waited = Stopwatch.StartNew();
            var closed = TestApp.Pump(Task.Run(async () =>
            {
                try
                {
                    while (true) await slow.Next();
                }
                catch (IOException)
                {
                    return waited.ElapsedMilliseconds;
                }
            }));
            Assert.InRange(closed, 300, 4000);
        }

        // The app links' sub-cap: one from this address, then busy — and a deck still pairs on the port the links did not take.
        var (first, linked) = Dialled(desk, DialDesk(desk, tls: false, code, "code"));
        Assert.Equal("link", Str(linked, "ev"));
        var (_, busy) = Dialled(desk, DialDesk(desk, tls: false, code, "code"));
        Assert.Equal(("busy", desk.Control.WireLimits.AppLinksBusyWords(1)), (Str(busy, "class"), Str(busy, "words")));
        using (var deck = TestApp.Pump(Lines.Dial(desk.Port, tls: false)))
        {
            TestApp.Pump(deck.Send("HELLO FOH deck").ContinueWith(_ => true, TaskScheduler.Default));
            var nonce = TestApp.Pump(deck.NextMatching(l => l.StartsWith("NONCE ", StringComparison.Ordinal)))["NONCE ".Length..];
            TestApp.Pump(deck.Send("AUTH-PROOF " + WireProof.Proof(desk.Token, nonce, "FOH deck")).ContinueWith(_ => true, TaskScheduler.Default));
            Assert.Equal("OK paired", TestApp.Pump(deck.NextMatching(l => l.StartsWith("OK paired", StringComparison.Ordinal) || l.StartsWith("ERR", StringComparison.Ordinal))));
        }
        // A link that closes gives its slot back.
        TestApp.Pump(desk.Peer.Ask(new JsonObject { ["do"] = "close", ["conn"] = first }));
        PumpWhile(() => desk.Control.AppLinks > 0, 5000);
        Assert.Equal("link", Str(Dialled(desk, DialDesk(desk, tls: false, code, "code")).End, "ev"));
    }

    [AvaloniaFact]
    public void AFinishedLinkKeepsTheWiresOwnBoundsALineTooLongEndsItAndASlowReaderIsNeverCut()
    {
        using var files = new KitFiles();
        using var desk = new AcceptorDesk();
        var code = files.Write("code", desk.Token);
        Assert.Equal(65536, ControlService.WireLineBytes);                                        // link.command.bytes
        Assert.Equal(256, WirePeer.MaxQueuedReplies);                                             // link.replies.max

        // S15 on a link: the wire's own bounds (round 65's writer, its line ceiling), held for a connection that finished LINK — a
        // regression leg, since a link is a wire connection like any other once it has its verdict. A slow reader — linked, then
        // reading nothing — hears the newest STATE, never a backlog, and the desk keeps on.
        var (deaf, linked) = Dialled(desk, DialDesk(desk, tls: false, code, "code", new JsonObject { ["reads"] = false }));
        Assert.Equal("link", Str(linked, "ev"));
        for (var i = 0; i < 40; i++)
        {
            desk.Booted.Vm.State.Blackout = i % 2 == 0;
            Dispatcher.UIThread.RunJobs();
        }
        var (other, again) = Dialled(desk, DialDesk(desk, tls: false, code, "code"));
        Assert.Equal("link", Str(again, "ev"));
        Assert.DoesNotContain(desk.Peer.Events, e => Str(e, "ev") == "closed" && Int(e, "conn") == deaf);

        // S15: a command line past link.command.bytes is answered with an ERR and the connection closed.
        Send(desk, other, "@x1 " + new string('A', ControlService.WireLineBytes + 16));
        var said = Str(ReadOn(desk, other, l => l.StartsWith("ERR", StringComparison.Ordinal)), "line")!;
        Assert.EndsWith("closed", said, StringComparison.Ordinal);
        ClosedByDesk(desk, other);
    }

    [AvaloniaFact]
    public void LoopbackTrustNeverStandsForALinksProofAndABrowsersRequestToTheWireIsClosedOverTlsToo()
    {
        using var files = new KitFiles();
        using var desk = new AcceptorDesk(trustLoopback: true);                                        // the desk trusts this machine's own connections

        // S20: trusted or not, a LINK verifies by its proof alone — an unknown kid from this machine is refused as from any other.
        var (unknown, refused) = Dialled(desk, DialDesk(desk, tls: false, files.Write("unknown", NewKey()), "key"));
        Assert.Equal(("refused", PeerLink.ProofWords), (Str(refused, "class"), Str(refused, "words")));
        ClosedByDesk(desk, unknown);
        Assert.Equal(0, desk.Control.AppLinks);

        // 13.6.3 and 13.6.4 on the one show laptop: a trusted connection is greeted at its start, which its connector discards, so
        // a LINK that verifies hears the whole STATE again after its verdict.
        var (trusted, linked) = Dialled(desk, DialDesk(desk, tls: false, files.Write("code", desk.Token), "code"));
        Assert.Equal(("link", "code"), (Str(linked, "ev"), Str(linked, "verified")));
        ReadOn(desk, trusted, l => l.StartsWith("OK @l2 paired {", StringComparison.Ordinal));
        PumpWhile(() =>
        {
            var reads = desk.Peer.Events.Where(e => Str(e, "ev") == "read" && Int(e, "conn") == trusted).Select(e => Str(e, "line") ?? "").ToList();
            var verdictAt = reads.FindIndex(l => l.StartsWith("OK @l2 paired {", StringComparison.Ordinal));
            return !reads.Skip(verdictAt + 1).Any(l => l.StartsWith("STATE {", StringComparison.Ordinal));
        }, 5000);
        Assert.Equal(1, desk.Control.AppLinks);

        // S20, a regression leg (round 89.7's door, inside TLS since 98.1): a page's request — another site's Origin and Host —
        // reaches the wire's port as HTTP, plain or inside TLS, and is closed on its request line before its body is read as a line,
        // trusted loopback or not.
        foreach (var tls in new[] { false, true })
        {
            var heard = TestApp.Pump(Task.Run(() =>
            {
                using var client = new TcpClient();
                client.Connect(IPAddress.Loopback, desk.Port);
                Stream stream = client.GetStream();
                if (tls)
                {
                    var ssl = new SslStream(stream, false, (_, _, _, _) => true);
                    ssl.AuthenticateAsClient("localhost");
                    stream = ssl;
                }
                stream.Write(Encoding.ASCII.GetBytes("POST /api/cmd HTTP/1.1\r\nHost: evil.example\r\nOrigin: http://evil.example\r\nContent-Length: 11\r\n\r\nBLACKOUT ON\n"));
                stream.Flush();
                using var reader = new StreamReader(stream, Encoding.UTF8);
                var lines = new List<string>();
                try
                {
                    while (reader.ReadLine() is { } line) lines.Add(line);
                }
                catch (IOException)
                {
                    // the door closed
                }
                stream.Dispose();
                return lines;
            }));
            Assert.Contains(ControlProtocol.Err(ControlProtocol.HttpOnWire), heard);
            Assert.DoesNotContain(heard, l => l.StartsWith("OK", StringComparison.Ordinal));
        }
        Dispatcher.UIThread.RunJobs();
        Assert.False(desk.Services.State.Blackout);
    }
}
