using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Security;
using System.Net.Sockets;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Show Bus 1.3 §13.20's scripted peer (<c>tools/showbus/peerlink-peer.mjs</c>, Node 22) against the honest ends of this desk's
/// PeerLink core over real loopback sockets. The test plays the carrier — the part a link of this desk plays around the
/// core — and the core decides: <see cref="PeerLinkMachine"/> says what to write and what the link is, and <see cref="PeerLink"/> makes the
/// transcript, the proofs, the session credential and the legacy proof, and reads the answers. A link on <c>line</c>, plain
/// on this machine and over TLS, and on <c>ws</c> with its side calls, each verified both ways with one lid at both ends; the
/// acceptor's end with the core's checks against the peer's connector, a wrong code refused in 13.6.3's words; a level-0
/// acceptor (S22) proved by AUTH-PROOF on its 32-hex NONCE, legacy and amber. Every secret is a canary made for the test (S19):
/// none of the code — as typed, in its canonical form and as the legacy key — the key, K or S crosses the wire in clear, and none
/// of them nor a proof reaches the link's record or what the machine asks to write. A proof crosses the wire by design (LINK
/// PROOF, AUTH-PROOF, the verdict's), so it is held to the record and the writes alone.
/// </summary>
public sealed partial class PeerLinkWireTests
{
    private const string SelfId = "9f8e7d6c";
    private const string StagewatchId = "3f9c2a1b7d0e";

    [Fact]
    public async Task ALineLinkOnThisMachineVerifiesBothWaysWithOneLidAndAMatchedPongAndACommandApplied()
    {
        using var files = new KitFiles();
        var code = PairingToken.New();
        await using var peer = ScriptedPeer.Start();
        var listening = await peer.Ask(new JsonObject
        {
            ["do"] = "listen", ["carrier"] = "line", ["tls"] = "plain", ["node"] = Node("main", StagewatchId), ["inst"] = "a41f09c2",
            ["keys"] = new JsonArray(new JsonObject { ["file"] = files.Write("code", code), ["kind"] = "code" }),
            ["can"] = new JsonArray("final", "receipts"), ["clock"] = 1505,
        });
        Assert.Equal("listening", Str(listening, "ev"));
        using var lines = await Lines.Dial(Int(listening, "port"), tls: false);
        var steps = new Steps();
        var m = new PeerLinkMachine(Settings(LinkCarrier.Line, "code", local: true), null, steps.Now());
        var k = PeerLink.CodeKey(PeerLink.Canon(code)!);
        m.NoTls(steps.Next());                                                                    // a local link may be plain (13.3.4)
        var proved = await LinkOnLine(lines, m, steps, k, "code", null);
        var lid = proved.Lid;

        var link = await peer.Wait(e => Str(e, "ev") == "link");
        Assert.Equal((lid, "code", "line"), (Str(link, "lid"), Str(link, "verified"), Str(link, "carrier")));
        Assert.Equal(LinkState.Live, m.State(steps.Next()));
        Assert.True(m.Follows(steps.Now()));
        var record = m.Record(steps.Now());
        Assert.Equal(("mutual", "local", "code", 1, "green"), (record.Proof, record.Tls, record.Kid, record.Level, record.Light));
        Assert.Equal(new LinkFloor(false, 1, "mutual"), m.Kept.Floor);
        Assert.Equal(("main", StagewatchId), (m.Kept.NodeKind, m.Kept.NodeId));

        // A matched pong is a clock sample: the peer stamps the time the test gives it (13.20), and t4 is the injected clock's.
        m.Ping(steps.At(1000), 1000);
        await lines.Send("PING 1000");
        var pong = ClockPing.ParsePong((await lines.NextMatching(l => l.StartsWith("OK PONG ", StringComparison.Ordinal)))[3..]);
        Assert.NotNull(pong);
        m.Pong(steps.At(1012), pong.Value.T1, pong.Value.T2, pong.Value.T3, stuck: false);
        Assert.Equal(new LinkRecordClock(499, 7, 0, 1, false), m.Record(steps.Now()).Clock);

        // A command with its request id, written because the machine said so, applied by the peer's answer read by 13.12.3.
        var id = m.NextId();
        Assert.Null(m.Send(steps.Next(), id));
        var write = Assert.Single(m.Drain());
        Assert.Equal(("command", id), (write.Verb, write.Detail));
        await lines.Send("@" + id + " BLACKOUT ON");
        var answer = PeerLink.ReadLine(await lines.NextMatching(l => l.Contains("@" + id, StringComparison.Ordinal)));
        Assert.Equal(id, answer!.Value.Id);
        m.Answer(steps.Next(), id, answer.Value.Reading);
        Assert.Equal(LinkOutcome.Applied, m.OutcomeOf(id));
        Assert.Equal(0, m.InFlight);

        NoSecretCrossed(peer, m, steps, [.. CodeForms(code), Convert.ToHexStringLower(k), proved.Session], [proved.ClientProof, proved.ServerProof]);
    }

    [Fact]
    public async Task ALineLinkOverTlsIsPinnedByTheProofThatVerifiedOverItsCertificate()
    {
        using var files = new KitFiles();
        var code = PairingToken.New();
        var cert = files.Cert("peer");
        await using var peer = ScriptedPeer.Start();
        var listening = await peer.Ask(new JsonObject
        {
            ["do"] = "listen", ["carrier"] = "line", ["tls"] = "only", ["cert"] = cert.CertFile, ["certKey"] = cert.KeyFile, ["node"] = Node("main", StagewatchId),
            ["keys"] = new JsonArray(new JsonObject { ["file"] = files.Write("code", code), ["kind"] = "code" }),
        });
        Assert.Equal(cert.Fingerprint, Str(listening, "fingerprint"));
        using var lines = await Lines.Dial(Int(listening, "port"), tls: true);
        Assert.Equal(cert.Fingerprint, lines.Shown);
        var steps = new Steps();
        var m = new PeerLinkMachine(Settings(LinkCarrier.Line, "code", local: false) with { NodeId = StagewatchId, Kind = "main" }, null, steps.Now());
        var k = PeerLink.CodeKey(PeerLink.Canon(code)!);
        Assert.Equal("tls", m.Dial(steps.Next()));
        m.Certificate(steps.Next(), lines.Shown!);
        var proved = await LinkOnLine(lines, m, steps, k, "code", lines.Shown);

        var link = await peer.Wait(e => Str(e, "ev") == "link");
        Assert.Equal(proved.Lid, Str(link, "lid"));
        Assert.Equal("pinned", m.Tls());
        Assert.Equal(new LinkPin(StagewatchId, cert.Fingerprint, PinTrust.Proof), m.Kept.Pin);
        Assert.Equal(new LinkFloor(true, 1, "mutual"), m.Kept.Floor);
        Assert.DoesNotContain("plain", m.Flags(steps.Next()));
        NoSecretCrossed(peer, m, steps, [.. CodeForms(code), Convert.ToHexStringLower(k), proved.Session], [proved.ClientProof, proved.ServerProof]);
    }

    [Fact]
    public async Task AWsLinkVerifiesWithAKeyAndItsSideCallsCarryTheSessionCredential()
    {
        using var files = new KitFiles();
        var key = "swk_" + RandomNumberGenerator.GetString("abcdefghijkmnpqrstuvwxyz23456789", 32);
        var k = PeerLink.KeyKey(key);
        var kid = PeerLink.Kid(k);
        await using var peer = ScriptedPeer.Start();
        var listening = await peer.Ask(new JsonObject
        {
            ["do"] = "listen", ["carrier"] = "ws", ["tls"] = "plain", ["node"] = Node("main", StagewatchId),
            ["keys"] = new JsonArray(new JsonObject { ["file"] = files.Write("key", key), ["kind"] = "key", ["bound"] = "patterns:" + SelfId }),
        });
        var port = Int(listening, "port");
        using var ws = new ClientWebSocket();
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{port}/ws?node={SelfId}&t=patterns&link={PeerLink.Version}"), Timeout());
        var steps = new Steps();
        var m = new PeerLinkMachine(Settings(LinkCarrier.Ws, kid, local: true) with { NodeId = StagewatchId, Kind = "main" }, null, steps.Now());
        m.NoTls(steps.Next());
        Assert.Equal(LinkState.Connecting, m.State(steps.Now()));                                // a local ws link proves itself on plain

        var first = await Receive(ws);
        var offer = PeerLink.ReadOfferMessage(first);
        Assert.NotNull(offer);
        m.LinkOffered(steps.Next(), offer);
        Assert.Equal(new[] { ("link", "proof") }, m.Drain().Select(w => (w.Verb!, w.Credential!)));
        var cnonce = Nonce();
        var t = PeerLink.Transcript("ws", PeerLink.Version, offer.Speaks, offer.T + ":" + offer.Id, "patterns:" + SelfId, kid, offer.SNonce, cnonce, offer.Bind)!;
        await SendJson(ws, new JsonObject
        {
            ["type"] = "link", ["client"] = Node("patterns", SelfId), ["cnonce"] = cnonce, ["kid"] = kid, ["proof"] = PeerLink.ClientProof(k, t),
        });
        var verdict = await Receive(ws);
        m.Verdict(steps.Next(), verdict, p => PeerLink.Verify(PeerLink.ServerProof(k, t), p));
        Assert.Equal(LinkState.Live, m.State(steps.Now()));
        Assert.Equal(PeerLink.Lid(t), PeerLink.Text(verdict, "lid"));
        Assert.Equal((PeerLink.Lid(t), kid), (Str(await peer.Wait(e => Str(e, "ev") == "link"), "lid"), Str(await peer.Wait(e => Str(e, "ev") == "link"), "kid")));
        var frame = await Receive(ws);
        Assert.Equal("state", PeerLink.Text(frame, "type"));
        m.Frame(steps.Next());
        Assert.Equal(LinkShow.Live, m.Show);

        // 13.7.1: a side call carries the session credential, never the key; on this machine in clear it is taken, a wrong one is 401 relink.
        var session = PeerLink.Session(k, t);
        using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(4) };
        var good = await SideCall(http, port, $"ShowBus lid={PeerLink.Lid(t)}, s={session}");
        Assert.Equal(200, good.Status);
        var wrong = await SideCall(http, port, $"ShowBus lid={PeerLink.Lid(t)}, s={new string('0', 64)}");
        Assert.Equal(401, wrong.Status);
        var reading = PeerLink.ReadHttp(wrong.Status, wrong.Body);
        Assert.True(reading.Relink);
        m.SideCall(steps.Next(), reading);                                                        // 13.7.3: the relink ends S and the link, with no hold
        Assert.Equal(LinkState.Down, m.State(steps.Now()));
        Assert.Null(m.HeldUntilWall(steps.Now()));

        var upgrade = await peer.Wait(e => Str(e, "ev") == "upgrade");
        Assert.Equal("none", Str(upgrade, "authorization"));                                     // no bearer: the key is proved, never sent
        Assert.All(peer.Events.Where(e => Str(e, "ev") == "served"), e => Assert.Equal("showbus", Str(e, "authorization")));
        NoSecretCrossed(peer, m, steps, [key, Convert.ToHexStringLower(k), session], [PeerLink.ClientProof(k, t), PeerLink.ServerProof(k, t)]);
        await ws.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "", Timeout());
    }

    [Fact]
    public async Task TheAcceptorsEndChecksThePeersProofAndAnswersWithItsOwnAndRefusesAWrongCodeInSection1363sWords()
    {
        using var files = new KitFiles();
        var code = PairingToken.New();
        var k = PeerLink.CodeKey(PeerLink.Canon(code)!);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            await using var peer = ScriptedPeer.Start();

            // The honest connector: its proof verifies, the core's verdict carries the server proof, and one lid stands at both ends.
            var dialled = peer.Ask(Dial(port, files.Write("code", code)));
            using (var accepted = await Lines.Accept(listener))
            {
                var (lid, ok) = await Acceptor(accepted, k);
                Assert.True(ok);
                Assert.Equal("connected", Str(await dialled, "ev"));
                var link = await peer.Wait(e => Str(e, "ev") == "link" && Str(e, "role") == "connector");
                Assert.Equal((lid, "code"), (Str(link, "lid"), Str(link, "verified")));

                // 13.12.6 at the acceptor: a repeated id is answered from the record, run once.
                var book = new ReceiptBook();
                for (var i = 0; i < 2; i++)
                {
                    await peer.Ask(new JsonObject { ["do"] = "send", ["conn"] = Int(link, "conn"), ["line"] = "@s1-11aa22bb BLACKOUT ON" });
                    var (id, rest) = WireReceipt.Split(await accepted.Next(), out var malformed);
                    Assert.False(malformed);
                    Assert.Equal("BLACKOUT ON", rest);
                    var held = book.Begin("main:" + StagewatchId, id, i);
                    if (held is null) book.Keep("main:" + StagewatchId, id, "{\"ok\":true,\"stage\":\"applied\",\"effect\":\"\"}", i);
                    await accepted.Send(held is null ? "OK @" + id : "OK @" + id + " " + held);
                }
                await peer.Wait(e => Str(e, "ev") == "read" && (Str(e, "line") ?? "").Contains("\"repeat\":true", StringComparison.Ordinal));
            }

            // A connector proving another code: refused in 13.6.3's words, the same an unknown kid would hear.
            var wrongly = peer.Ask(Dial(port, files.Write("other", PairingToken.New())));
            using (var refused = await Lines.Accept(listener))
            {
                var (_, ok) = await Acceptor(refused, k);
                Assert.False(ok);
                await wrongly;
                var said = await peer.Wait(e => Str(e, "ev") == "refused" && Str(e, "role") == "connector");
                Assert.Equal(("refused", PeerLink.ProofWords), (Str(said, "class"), Str(said, "words")));
            }
            Assert.DoesNotContain(peer.Events, e => Str(e, "ev") == "wrote" && (Str(e, "line") ?? "").Contains(PeerLink.Canon(code)!, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            listener.Stop();
        }
    }

    [Fact]
    public async Task ALevelZeroAcceptorIsProvedByAuthProofOnItsNonceAndTheLinkIsLegacyAndAmber()
    {
        using var files = new KitFiles();
        var code = PairingToken.New();
        await using var peer = ScriptedPeer.Start();
        var listening = await peer.Ask(new JsonObject
        {
            ["do"] = "listen", ["carrier"] = "line", ["tls"] = "plain", ["node"] = Node("patterns", "7a7a7a7a"), ["level"] = 0,
            ["keys"] = new JsonArray(new JsonObject { ["file"] = files.Write("code", code), ["kind"] = "code" }),
        });
        using var lines = await Lines.Dial(Int(listening, "port"), tls: false);
        var steps = new Steps();
        var m = new PeerLinkMachine(Settings(LinkCarrier.Line, "code", local: true), null, steps.Now());
        m.NoTls(steps.Next());
        string? nonce = null;
        await WriteAsked(lines, m, Nonce(), "code", null);
        for (var settled = false; !settled;)
        {
            var line = await lines.Next();
            if (line.StartsWith("NONCE ", StringComparison.Ordinal))
            {
                nonce = line[6..];
                m.Nonce(steps.Next(), nonce);
            }
            else if (line.StartsWith("STATE ", StringComparison.Ordinal))
            {
                m.Frame(steps.Next());                                                            // before OK paired: discarded and counted (13.6.3)
            }
            else if (line.StartsWith("ERR @l1 ", StringComparison.Ordinal) || line.StartsWith("OK @pa1", StringComparison.Ordinal))
            {
                m.LineAnswer(steps.Next(), line);
                settled = line.StartsWith("OK @pa1", StringComparison.Ordinal);
            }
            foreach (var w in m.Drain().Where(w => w.Verb == "AUTH-PROOF"))
            {
                Assert.Equal("auth-proof", w.Credential);
                await lines.Send("@pa1 AUTH-PROOF " + PeerLink.LegacyProof(code, nonce, "Patterns"));
            }
        }
        Assert.Equal(LinkState.Live, m.State(steps.Next()));
        Assert.Contains("legacy", m.Flags(steps.Now()));
        Assert.Equal(CheckLight.Amber, m.Light(steps.Now()));
        Assert.True(m.Follows(steps.Now()));
        Assert.Equal(1, m.Record(steps.Now()).Counts.Malformed);
        Assert.Equal("auth-proof", Str(await peer.Wait(e => Str(e, "ev") == "paired"), "how"));
        Assert.DoesNotContain(peer.Events, e => Str(e, "ev") == "read" && (Str(e, "line") ?? "").StartsWith("AUTH ", StringComparison.Ordinal));
        NoSecretCrossed(peer, m, steps, [.. CodeForms(code), Convert.ToHexStringLower(PeerLink.CodeKey(PeerLink.Canon(code)!))], [PeerLink.LegacyProof(code, nonce, "Patterns")!]);
    }

    /// <summary>
    /// The core at level 0 against the scripted peer as a Stagewatch older than LINK, the test playing the carrier as the Stagewatch
    /// link does and the core deciding: the bearer goes in the upgrade only because the link is local (13.3.4), and the verdict that
    /// takes it settles the link (13.15.2 at level 0). S13: a pong that answers a ping sent is a clock sample; one that answers
    /// none (<c>pongs</c> "unmatched") is counted and never a sample. S12: the stuck beat makes the show stuck, a pong never clears it,
    /// a state frame does. S11: silence is stale past <c>link.stale.ms</c> and down past <c>link.drop.ms</c> on the evaluator's own
    /// runs. S19: the key reaches nothing the peer read off the wire but the upgrade's bearer, said there by its kind alone, and
    /// neither the record nor a write.
    /// </summary>
    [Fact]
    public async Task AnOlderStagewatchAtLevelZeroHasItsPongsMatchedItsStallSaidAndItsSilenceDropped()
    {
        using var files = new KitFiles();
        var key = "swk_" + RandomNumberGenerator.GetString("abcdefghijkmnpqrstuvwxyz23456789", 32);
        var kid = PeerLink.Kid(PeerLink.KeyKey(key));
        await using var peer = ScriptedPeer.Start();
        var listening = await peer.Ask(new JsonObject
        {
            ["do"] = "listen", ["carrier"] = "ws", ["tls"] = "plain", ["node"] = Node("main", StagewatchId), ["level"] = 0, ["clock"] = 5,
            ["keys"] = new JsonArray(new JsonObject { ["file"] = files.Write("key", key), ["kind"] = "key", ["bound"] = "patterns:" + SelfId }),
        });
        var steps = new Steps();
        var m = new PeerLinkMachine(Settings(LinkCarrier.Ws, kid, local: true) with { NodeId = StagewatchId, Kind = "main" }, null, steps.Now());
        Assert.NotNull(m.Dial(steps.Next()));
        m.NoTls(steps.Next());
        using var ws = new ClientWebSocket();
        ws.Options.SetRequestHeader("Authorization", "Bearer " + key);
        await ws.ConnectAsync(new Uri($"ws://127.0.0.1:{Int(listening, "port")}/ws?node={SelfId}&t=patterns"), Timeout());

        // Its first message is its verdict, not link: level 0, the bearer the core asks for, and the key taken — settled.
        var verdict = await Receive(ws);
        Assert.Equal(("auth", "key"), (PeerLink.Text(verdict, "type"), PeerLink.Text(verdict.GetProperty("node"), "verified")));
        m.FirstMessage(steps.Next(), frame: false);
        Assert.Equal(new[] { "bearer" }, m.Drain().Select(w => w.Credential).OfType<string>());
        m.Settled(steps.Next());
        Assert.Equal("state", PeerLink.Text(await Receive(ws), "type"));
        m.Frame(steps.Next());
        Assert.Equal((LinkState.Live, LinkShow.Live), (m.State(steps.Now()), m.Show));
        Assert.Equal("bearer", Str(await peer.Wait(e => Str(e, "ev") == "upgrade"), "authorization"));

        async Task<JsonElement> Ping()
        {
            var now = steps.Next();
            m.Ping(now, now.Wall);
            await SendJson(ws, new JsonObject { ["cmd"] = "ping", ["t"] = now.Wall });
            return await Receive(ws);
        }
        void Pong(JsonElement pong)
            => m.Pong(steps.Next(), pong.GetProperty("t").GetInt64(), pong.GetProperty("t2").GetInt64(), pong.GetProperty("t3").GetInt64(), stuck: false);

        // S13: matched, a sample; unmatched, counted and no sample.
        Pong(await Ping());
        Assert.Equal(1, m.Clock.Samples);
        await peer.Ask(new JsonObject { ["do"] = "set", ["pongs"] = "unmatched" });
        Pong(await Ping());
        Pong(await Ping());
        Assert.Equal(1, m.Clock.Samples);
        Assert.Equal(2, m.Record(steps.Now()).Counts.Unmatched);
        await peer.Ask(new JsonObject { ["do"] = "set", ["pongs"] = true });

        // S12: the stuck beat; a pong never clears it on ws; a state frame does.
        await peer.Ask(new JsonObject { ["do"] = "set", ["stuck"] = true });
        var beat = await Receive(ws);
        Assert.Equal(("beat", false), (PeerLink.Text(beat, "type"), beat.GetProperty("live").GetBoolean()));
        m.Beat(steps.Next(), live: false);
        Assert.Equal(LinkShow.Stuck, m.Show);
        Pong(await Ping());
        Assert.Equal(LinkShow.Stuck, m.Show);
        await peer.Ask(new JsonObject { ["do"] = "set", ["stuck"] = false });
        Assert.Equal("state", PeerLink.Text(await Receive(ws), "type"));
        m.Frame(steps.Next());
        Assert.Equal(LinkShow.Live, m.Show);

        // S11: the peer answers no ping; the evaluator runs each second on its own timer — stale past 6 s, down past 12 s.
        await peer.Ask(new JsonObject { ["do"] = "set", ["pongs"] = false });
        var heard = steps.Now().Mono;
        await PingUnanswered();
        var stale = (long)ShowBusLiveness.StaleAfter.TotalMilliseconds;
        var drop = (long)ShowBusLiveness.DropAfter.TotalMilliseconds;
        var states = new List<(long Silent, LinkState State, bool Closed)>();
        for (var at = heard + 1000; ; at = Math.Min(at + 1000, heard + drop + 1))
        {
            var ev = m.Evaluate(steps.At(at));
            states.Add((at - heard, m.State(steps.Now()), ev.Closed));
            if (ev.Closed || at == heard + drop + 1) break;
        }
        Assert.Equal(LinkState.Live, states.Single(x => x.Silent == stale).State);
        Assert.Equal(LinkState.Stale, states.Single(x => x.Silent == stale + 1000).State);
        Assert.Equal(LinkState.Stale, states.Single(x => x.Silent == drop).State);
        Assert.Equal((drop + 1, LinkState.Down, true), states[^1]);

        NoSecretCrossed(peer, m, steps, [key, Convert.ToHexStringLower(PeerLink.KeyKey(key))], []);
        ws.Abort();

        async Task PingUnanswered()
        {
            var at = steps.Next();
            m.Ping(at, at.Wall);
            await SendJson(ws, new JsonObject { ["cmd"] = "ping", ["t"] = at.Wall });
        }
    }

    // ---- the carrier the test plays ------------------------------------------------------------------------------------

    /// <summary>
    /// LINK on <c>line</c> as the machine asks it: HELLO and the LINK ask, then the proof once the acceptor's offer came, then
    /// the verdict, taken only with the server proof the core checks over T. Returns the lid, both proofs and S, the last
    /// three as canaries for <see cref="NoSecretCrossed"/>.
    /// </summary>
    private static async Task<(string Lid, string ClientProof, string ServerProof, string Session)> LinkOnLine(Lines lines, PeerLinkMachine m, Steps steps, byte[] k, string kid, string? fingerprint)
    {
        var cnonce = Nonce();
        await WriteAsked(lines, m, cnonce, kid, null);
        string? t = null;
        for (;;)
        {
            var line = await lines.Next();
            if (line.StartsWith("NONCE ", StringComparison.Ordinal)) m.Nonce(steps.Next(), line[6..]);
            else if (line.StartsWith("STATE ", StringComparison.Ordinal)) m.Frame(steps.Next());
            else if (line.StartsWith("OK @l1 ", StringComparison.Ordinal))
            {
                m.LineAnswer(steps.Next(), line);
                var offer = PeerLink.ReadOfferLine(line)!;
                t = PeerLink.Transcript("line", PeerLink.Version, offer.Speaks, offer.T + ":" + offer.Id, "patterns:" + SelfId, kid, offer.SNonce, cnonce, PeerLink.Bind(fingerprint));
                Assert.NotNull(t);
                await WriteAsked(lines, m, cnonce, kid, PeerLink.ClientProof(k, t));
            }
            else if (line.StartsWith("OK @l2 ", StringComparison.Ordinal))
            {
                var server = PeerLink.ServerProof(k, t!);
                m.LineAnswer(steps.Next(), line, p => PeerLink.Verify(server, p));
                Assert.True(m.Follows(steps.Now()), "the verdict's proof verified: " + line);
                return (PeerLink.Lid(t!), PeerLink.ClientProof(k, t!), server, PeerLink.Session(k, t!));
            }
            else if (line.StartsWith("ERR ", StringComparison.Ordinal))
            {
                Assert.Fail("the peer refused LINK: " + line);
            }
        }
    }

    /// <summary>Writes what the machine drained: HELLO, the LINK ask (no credential), the proof (its credential), never a key or code.</summary>
    private static async Task WriteAsked(Lines lines, PeerLinkMachine m, string cnonce, string kid, string? proof)
    {
        foreach (var w in m.Drain())
        {
            if (w.Verb == "HELLO") await lines.Send("HELLO Patterns");
            else if (w.Verb == "LINK" && w.Credential is null) await lines.Send($"@l1 LINK {PeerLink.Version} patterns {SelfId} {cnonce} {kid}");
            else if (w.Verb == "LINK" && w.Credential == "proof") await lines.Send("@l2 LINK PROOF " + proof);
        }
    }

    /// <summary>The acceptor's end of LINK on <c>line</c>, made of the core's parsers and proofs: the offer, the check of the connector's proof, and the verdict with its own.</summary>
    private static async Task<(string Lid, bool Verified)> Acceptor(Lines lines, byte[] k)
    {
        Assert.Equal("HELLO Stagewatch", await lines.Next());
        await lines.Send("OK");
        var asked = PeerLink.ReadLinkLine(await lines.Next());
        var ask = asked.Ask;
        Assert.NotNull(ask);
        Assert.Equal(("main", StagewatchId, "code"), (ask.T, ask.Id, ask.Kid));
        var snonce = Nonce();
        await lines.Send($"OK @l1 LINK {PeerLink.Speaks(ask.Offer)} patterns {SelfId} {snonce} plain");
        var proved = PeerLink.ReadLinkLine(await lines.Next());
        Assert.NotNull(proved.Proof);
        var t = PeerLink.Transcript("line", ask.Offer, PeerLink.Speaks(ask.Offer)!.Value, "patterns:" + SelfId, ask.T + ":" + ask.Id, ask.Kid, snonce, ask.CNonce, "plain")!;
        if (!PeerLink.Verify(PeerLink.ClientProof(k, t), proved.Proof))
        {
            await lines.Send("ERR @l2 LINK refused — " + PeerLink.ProofWords);
            return ("", false);
        }
        var verdict = new JsonObject
        {
            ["v"] = 1, ["verified"] = "code", ["scope"] = "operate", ["proof"] = PeerLink.ServerProof(k, t), ["lid"] = PeerLink.Lid(t),
            ["level"] = PeerLink.Version, ["can"] = new JsonArray(), ["inst"] = "a1b2c3d4", ["beatMs"] = 2000,
        };
        await lines.Send("OK @l2 paired " + verdict.ToJsonString());
        return (PeerLink.Lid(t), true);
    }

    /// <summary>
    /// S19 (13.19.5): none of the <paramref name="secrets"/> — the code in each of its forms, the key, K, S — is in anything the
    /// peer read off the wire, nor in the link's record or what the machine asked to write; none of the
    /// <paramref name="proofs"/>, which cross the wire by design, is in the record or the writes.
    /// </summary>
    private static void NoSecretCrossed(ScriptedPeer peer, PeerLinkMachine m, Steps steps, string[] secrets, string[] proofs)
    {
        var seen = string.Join("\n", peer.Events.Where(e => Str(e, "ev") is "read" or "upgrade" or "served").Select(e => e.ToJsonString()));
        var record = m.Record(steps.Next()).ToJson();
        var asked = string.Join("\n", m.Drain().Select(w => w.ToString()));
        Assert.All(secrets.Concat(proofs), canary => Assert.True(canary.Length >= 8, "a canary too short to mean anything: " + canary));
        foreach (var canary in secrets)
            Assert.DoesNotContain(canary, seen, StringComparison.OrdinalIgnoreCase);
        foreach (var canary in secrets.Concat(proofs))
        {
            Assert.DoesNotContain(canary, record, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(canary, asked, StringComparison.OrdinalIgnoreCase);
        }
    }

    /// <summary>A pairing code's three forms: as typed, its canonical form (the input to K, 13.5.1) and the legacy key (13.6.8).</summary>
    private static string[] CodeForms(string code) => [code, PeerLink.Canon(code)!, PairingToken.Normal(code)];

    private static JsonObject Dial(int port, string codeFile) => new()
    {
        ["do"] = "dial", ["carrier"] = "line", ["port"] = port, ["tls"] = false, ["node"] = Node("main", StagewatchId),
        ["key"] = new JsonObject { ["file"] = codeFile, ["kind"] = "code" }, ["meant"] = Node("patterns", SelfId), ["name"] = "Stagewatch",
    };

    private static LinkSettings Settings(LinkCarrier carrier, string key, bool local)
        => new() { Carrier = carrier, Key = key, Local = local, Name = "Hall A", Address = "127.0.0.1", Inst = "a1b2c3d4", IdPrefix = "p" };

    private static JsonObject Node(string t, string id) => new() { ["t"] = t, ["id"] = id };

    private static string Nonce() => Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(PeerLink.NonceBytes));

    private static CancellationToken Timeout() => new CancellationTokenSource(TimeSpan.FromSeconds(5)).Token;

    private static async Task<JsonElement> Receive(ClientWebSocket ws)
    {
        var buffer = new byte[64 * 1024];
        using var message = new MemoryStream();
        for (;;)
        {
            var r = await ws.ReceiveAsync(buffer, Timeout());
            message.Write(buffer, 0, r.Count);
            if (r.EndOfMessage) break;
        }
        using var doc = JsonDocument.Parse(message.ToArray());
        return doc.RootElement.Clone();
    }

    private static Task SendJson(ClientWebSocket ws, JsonObject message)
        => ws.SendAsync(Encoding.UTF8.GetBytes(message.ToJsonString()), WebSocketMessageType.Text, true, Timeout());

    private static async Task<(int Status, string Body)> SideCall(HttpClient http, int port, string authorization)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"http://127.0.0.1:{port}/context");
        Assert.True(request.Headers.TryAddWithoutValidation("Authorization", authorization));
        using var response = await http.SendAsync(request);
        return ((int)response.StatusCode, await response.Content.ReadAsStringAsync());
    }

    private static string? Str(JsonObject e, string name) => e.TryGetPropertyValue(name, out var v) && v is JsonValue j && j.TryGetValue<string>(out var s) ? s : null;

    private static int Int(JsonObject e, string name) => e[name]!.GetValue<int>();

    /// <summary>The injected clock the machine reads: ms that only move forward, one number for its monotonic and wall clocks.</summary>
    private sealed class Steps
    {
        private long _ms;

        public LinkNow Now() => _ms;

        public LinkNow Next() => ++_ms;

        public LinkNow At(long ms)
        {
            _ms = Math.Max(_ms, ms);
            return _ms;
        }
    }

    /// <summary>One connection's lines, LF-ended, each read within a few seconds; over TLS when asked, its certificate's fingerprint kept.</summary>
    private sealed class Lines : IDisposable
    {
        private readonly TcpClient _client;
        private readonly Stream _stream;
        private readonly StreamReader _reader;

        private Lines(TcpClient client, Stream stream, string? shown)
        {
            _client = client;
            _stream = stream;
            _reader = new StreamReader(stream, new UTF8Encoding(false));
            Shown = shown;
        }

        /// <summary>The fingerprint of the certificate the TLS handshake showed (13.8.1); null on plain.</summary>
        public string? Shown { get; }

        public static async Task<Lines> Dial(int port, bool tls)
        {
            var client = new TcpClient();
            await client.ConnectAsync(IPAddress.Loopback, port, Timeout());
            if (!tls) return new Lines(client, client.GetStream(), null);
            string? shown = null;
            var ssl = new SslStream(client.GetStream(), false);
            await ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
            {
                TargetHost = "127.0.0.1",
                // 13.8.1: a connector checks a certificate by its fingerprint and nothing else; the machine decides what it means.
                RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                {
                    shown = certificate is null ? null : Convert.ToHexStringLower(SHA256.HashData(certificate.GetRawCertData()));
                    return shown is not null;
                },
            }, Timeout());
            return new Lines(client, ssl, shown);
        }

        public static async Task<Lines> Accept(TcpListener listener)
        {
            var client = await listener.AcceptTcpClientAsync(Timeout());
            return new Lines(client, client.GetStream(), null);
        }

        public async Task Send(string line)
        {
            var bytes = Encoding.UTF8.GetBytes(line + "\n");
            await _stream.WriteAsync(bytes, Timeout());
            await _stream.FlushAsync(Timeout());
        }

        public async Task<string> Next() => await _reader.ReadLineAsync(Timeout()) ?? throw new IOException("the peer closed the connection");

        public async Task<string> NextMatching(Func<string, bool> match)
        {
            for (;;)
            {
                var line = await Next();
                if (match(line)) return line;
            }
        }

        public void Dispose()
        {
            _reader.Dispose();
            _stream.Dispose();
            _client.Dispose();
        }
    }

    /// <summary>The kit's files: a key, a code or a certificate the peer reads, each its owner's alone (0600), as 13.20 asks — never an argument.</summary>
    private sealed class KitFiles : IDisposable
    {
        private readonly TempFolder _folder = TempRun.Folder("patterns-tests-peerlink-");

        public string Write(string name, string text)
        {
            var path = Path.Combine(_folder.Path, name);
            File.WriteAllText(path, text, new UTF8Encoding(false));
            if (!OperatingSystem.IsWindows()) File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
            return path;
        }

        /// <summary>A self-signed P-256 certificate for the peer's TLS: its PEM files and its fingerprint.</summary>
        public (string CertFile, string KeyFile, string Fingerprint) Cert(string name)
        {
            using var ec = ECDsa.Create(ECCurve.NamedCurves.nistP256);
            var request = new CertificateRequest("CN=" + name, ec, HashAlgorithmName.SHA256);
            using var cert = request.CreateSelfSigned(DateTimeOffset.UtcNow.AddDays(-1), DateTimeOffset.UtcNow.AddDays(30));
            return (Write(name + ".crt", cert.ExportCertificatePem()), Write(name + ".key", ec.ExportPkcs8PrivateKeyPem()), Convert.ToHexStringLower(SHA256.HashData(cert.RawData)));
        }

        public void Dispose() => _folder.Dispose();
    }

    /// <summary>
    /// The scripted peer as a process the test drives: one JSON command a line on its stdin, one event a line on its stdout, every
    /// event kept in order. Node runs the file from this tree; it takes no arguments.
    /// </summary>
    private sealed class ScriptedPeer : IAsyncDisposable
    {
        private readonly Process _process;
        private readonly List<JsonObject> _events = new();
        private readonly StringBuilder _stderr = new();
        private readonly object _gate = new();
        private readonly Task _reader;
        private int _tags;

        private ScriptedPeer(Process process)
        {
            _process = process;
            _process.ErrorDataReceived += (_, e) =>
            {
                if (e.Data is { } line)
                    lock (_gate) _stderr.AppendLine(line);
            };
            _process.BeginErrorReadLine();
            _reader = Task.Run(async () =>
            {
                while (await _process.StandardOutput.ReadLineAsync() is { } line)
                {
                    if (JsonNode.Parse(line) is JsonObject e)
                        lock (_gate) _events.Add(e);
                }
            });
        }

        /// <summary>Node 22 on the PATH running this tree's peer; the test job sets Node up (build.yml), and a machine without it fails here, never skips.</summary>
        public static ScriptedPeer Start()
        {
            var script = Path.Combine(Root(), "tools", "showbus", "peerlink-peer.mjs");
            var start = new ProcessStartInfo("node")
            {
                RedirectStandardInput = true,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                StandardOutputEncoding = Encoding.UTF8,
                StandardErrorEncoding = Encoding.UTF8,
                StandardInputEncoding = new UTF8Encoding(false),
            };
            start.ArgumentList.Add(script);
            try
            {
                return new ScriptedPeer(Process.Start(start) ?? throw new InvalidOperationException("node did not start"));
            }
            catch (System.ComponentModel.Win32Exception e)
            {
                throw new InvalidOperationException("the scripted peer needs Node 22 on the PATH (Show Bus 13.20; CI's test job sets it up): " + e.Message, e);
            }
        }

        public IReadOnlyList<JsonObject> Events
        {
            get
            {
                lock (_gate) return _events.ToList();
            }
        }

        /// <summary>Sends a command under a tag of its own and waits for the event that answers it; a refusal fails the test with the peer's words.</summary>
        public async Task<JsonObject> Ask(JsonObject command)
        {
            var tag = "t" + Interlocked.Increment(ref _tags).ToString(System.Globalization.CultureInfo.InvariantCulture);
            command["tag"] = tag;
            await _process.StandardInput.WriteLineAsync(command.ToJsonString());
            await _process.StandardInput.FlushAsync();
            var answer = await Wait(e => Str(e, "re") == tag);
            Assert.True(Str(answer, "ev") != "error", "the peer refused " + command.ToJsonString() + ": " + Str(answer, "words"));
            return answer;
        }

        /// <summary>The first event <paramref name="match"/> takes, waiting at most a few seconds.</summary>
        public async Task<JsonObject> Wait(Func<JsonObject, bool> match)
        {
            var clock = Stopwatch.StartNew();
            for (;;)
            {
                var found = Events.FirstOrDefault(match);
                if (found is not null) return found;
                if (clock.ElapsedMilliseconds > 5000 || _process.HasExited)
                {
                    string faults;
                    lock (_gate) faults = _stderr.ToString();
                    Assert.Fail("the peer said no such event in 5 s; its events: " + string.Join(" ", Events.Select(e => e.ToJsonString())) + " " + faults);
                }
                await Task.Delay(10);
            }
        }

        public async ValueTask DisposeAsync()
        {
            try
            {
                if (!_process.HasExited)
                {
                    await _process.StandardInput.WriteLineAsync("{\"do\":\"quit\"}");
                    await _process.StandardInput.FlushAsync();
                }
                using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                await _process.WaitForExitAsync(cts.Token);
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or InvalidOperationException)
            {
                if (!_process.HasExited) _process.Kill(entireProcessTree: true);
            }
            await _reader.WaitAsync(TimeSpan.FromSeconds(3));
            _process.Dispose();
        }

        private static string Root()
        {
            var dir = new DirectoryInfo(AppContext.BaseDirectory);
            while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Patterns.sln"))) dir = dir.Parent;
            return dir?.FullName ?? throw new InvalidOperationException("no Patterns.sln above " + AppContext.BaseDirectory);
        }
    }
}
