using System.Globalization;
using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 96: Show Bus 1.3 §13.20's connector scenarios through the Stagewatch link at level 1 — the desk's
/// <see cref="StagewatchLinkService"/> dialling <c>/ws?…&amp;link=1</c> against the scripted peer as a Stagewatch 2.22 acceptor, which
/// speaks LINK on /ws as Stagewatch's own acceptor does (the offer first, one link message, the verdict with the server proof, S on
/// side calls). The peer plays another machine on this one over TLS, so the rules that hold off this machine are the ones under test.
/// </summary>
public sealed partial class PeerLinkWireTests
{
    /// <summary>
    /// S1 and S19 through the link: the key is proved with LINK and never sent — every upgrade asks for LINK with no Authorization
    /// header — both proofs verify, the certificate is pinned by the proof and the floor recorded for the Stagewatch's id and its typed
    /// address, each kept with the settings; every side call carries the link's S (13.7.1) and the peer took each one; a matched pong
    /// is a clock sample. The key, its K and the bearer word are in nothing the peer printed; they, the link's S, both proofs and both
    /// nonces — made again here from what crossed the wire — are in no reader of this desk (13.19.5).
    /// </summary>
    [AvaloniaFact]
    public void TheStagewatchLinkProvesItsKeyWithLinkPinsByTheProofAndCarriesOnlyItsSession()
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
            var port = ListenAsStagewatch(peer, files, key, cert, level: 1);
            DialAsAnotherMachine(link, config, port, key);

            PumpUntil(() => link.Record is { Level: 1, Proof: "mutual", State: "live", Clock.Samples: >= 1 } && Count(peer, "served") >= 1, 20000, link);
            var record = link.Record!;
            Assert.Equal(("pinned", "key"), (record.Tls, link.Key.Word));
            Assert.DoesNotContain("legacy", record.Flags);
            Assert.DoesNotContain("keyless", record.Flags);
            Assert.Equal("tls", link.Carrier);
            Assert.Single(peer.Events, e => Str(e, "ev") == "link" && Str(e, "role") == "acceptor");
            Assert.All(peer.Events.Where(e => Str(e, "ev") == "upgrade"), e =>
                Assert.Equal(("none", "1"), (Str(e, "authorization"), e["query"]!["link"]?.GetValue<string>())));
            Assert.All(peer.Events.Where(e => Str(e, "ev") == "served"), e => Assert.Equal(("showbus", 200), (Str(e, "authorization"), e["status"]!.GetValue<int>())));

            // Pinned by the proof and floored, kept for the next run and a restart; a proof's pin starts nothing again.
            var fp = TlsFingerprint.Prefix + cert.Fingerprint;
            PumpUntil(() => config.Pins.Any(p => p.Fingerprint == fp && p.Trust == PinTrust.Proof) && config.Floors.Count >= 2, 5000, link);
            Assert.Equal("", config.PinsKey);
            var addr = "addr:127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
            Assert.Equal(new[] { addr, "node:" + StagewatchId }.Order(), config.Floors.Select(f => f.Key).Order());
            Assert.All(config.Floors, f => Assert.Equal((true, 1, "mutual"), (f.Tls, f.Level, f.Proof)));
            Assert.True(link.HasFloor);
            var upgrades = Dials(peer);
            PumpFor(1500);
            Assert.Equal(upgrades, Dials(peer));                                                      // the same link runs on

            TestApp.FlushFiles(services);
            var desk = new[]
            {
                File.ReadAllText(Path.Combine(b.Dir, "patterns.log")),
                File.Exists(services.Journal.Path) ? File.ReadAllText(services.Journal.Path) : "",
                new CommandRouter(services).StateJson(),
                services.ContextJson(),
                link.Record?.ToJson() ?? "",
                link.Status,
                link.TlsWords,
            };
            var printed = string.Join("\n", peer.Events.Select(e => e.ToJsonString()));
            foreach (var canary in new[] { key, Convert.ToHexStringLower(PeerLink.KeyKey(key)), "Bearer " })
                Assert.All(desk.Append(printed), r => Assert.DoesNotContain(canary, r, StringComparison.OrdinalIgnoreCase));
            // What the peer printed holds the nonces and proofs as they crossed the wire, by design; none of them, nor S, reaches this desk.
            foreach (var canary in LinkSecrets(peer, key))
                Assert.All(desk, r => Assert.DoesNotContain(canary, r, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            TearDown(b, link, config, peer);
        }
    }

    /// <summary>
    /// 13.6.3 and S4's second half through the link: an acceptor whose verdict's proof is this desk's own client proof (a machine
    /// between that cannot make the server's) is no link — nothing of its verdict is taken, no certificate pinned, no floor recorded,
    /// no side call made — and the link says the proof did not verify and dials again as after a failed dial.
    /// </summary>
    [AvaloniaFact]
    public void AVerdictWhoseProofDoesNotVerifyIsNeitherTakenPinnedNorFloored()
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
            var port = ListenAsStagewatch(peer, files, key, cert, level: 1, attack: new JsonObject { ["verdict"] = "reflect" });
            DialAsAnotherMachine(link, config, port, key);

            PumpUntil(() => Count(peer, "attack") >= 1 && link.Record is { Attempt: >= 1 }, 15000, link);
            Assert.Contains(PeerLink.ProofWords, link.Record!.Words + " " + link.Status, StringComparison.Ordinal);
            Assert.NotEqual(1, link.Record.Level);
            Assert.False(link.HasFloor);
            Assert.DoesNotContain(config.Pins, p => p.Trust == PinTrust.Proof);
            Assert.Empty(config.Floors);
            Assert.DoesNotContain(peer.Events, e => Str(e, "ev") == "served");
            Assert.NotEqual("key", link.Key.Word);
        }
        finally
        {
            TearDown(b, link, config, peer);
        }
    }

    /// <summary>
    /// S2 and S18 through the link: a key the acceptor refuses — one it does not hold, in 13.6.3's words, or one bound to another
    /// connector, refused <c>binding</c> — is held at least <c>peerlink.hold.refused.ms</c> and kept for a restart; the socket stays a
    /// public viewer and carries no S, so no side call carries a credential; the attempt is not reset.
    /// </summary>
    [AvaloniaTheory]
    [InlineData(false)]
    [InlineData(true)]
    public void ARefusedOrBoundKeyIsHeldAndTheSocketReadsThePublicViewWithNoSession(bool boundElsewhere)
    {
        using var files = new KitFiles();
        var key = "swk_" + RandomNumberGenerator.GetString("abcdefghijkmnpqrstuvwxyz23456789", 32);
        var held = "swk_" + RandomNumberGenerator.GetString("abcdefghijkmnpqrstuvwxyz23456789", 32);
        var cert = files.Cert("peer");
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        var link = services.Kernel.Stagewatch;
        var config = vm.State.Control.Stagewatch;
        var peer = ScriptedPeer.Start();
        try
        {
            // Refused: the peer holds another key. Bound: it holds this one, bound to another connector.
            var port = boundElsewhere
                ? ListenAsStagewatch(peer, files, key, cert, level: 1, bound: "patterns:0f0f0f0f0f0f")
                : ListenAsStagewatch(peer, files, held, cert, level: 1);
            DialAsAnotherMachine(link, config, port, key);

            // The peer's own event line may be read after the desk heard the verdict: the wait takes both.
            PumpUntil(() => link.Key.Refused && link.KeyHeld > TimeSpan.Zero
                && peer.Events.Any(e => Str(e, "ev") == "refused" && Str(e, "role") == "acceptor"), 15000, link);
            var refused = Assert.Single(peer.Events, e => Str(e, "ev") == "refused" && Str(e, "role") == "acceptor");
            Assert.Equal(boundElsewhere ? "binding" : "refused", Str(refused, "class"));
            Assert.Equal(boundElsewhere ? "binding" : "key", link.Record!.Refusal);
            Assert.True(link.KeyHeld >= StagewatchFrame.RefusedHold - TimeSpan.FromSeconds(5), "held " + link.KeyHeld);
            Assert.True(link.Holds.Holding(PeerLink.Kid(PeerLink.KeyKey(key)), DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()), "the hold is kept for a restart");
            PumpFor(2500);
            Assert.All(peer.Events.Where(e => Str(e, "ev") == "served"), e => Assert.Equal("none", Str(e, "authorization")));
            Assert.Equal(1, Dials(peer));                                                             // a public viewer, not dialled again
            Assert.NotEqual("mutual", link.Record.Proof);
            if (!boundElsewhere) Assert.Contains(PeerLink.ProofWords, link.Status, StringComparison.Ordinal);
        }
        finally
        {
            TearDown(b, link, config, peer);
        }
    }

    /// <summary>
    /// S17 through the link: a key revoked while the link is live is refused at once on the socket — the run's key refused and
    /// held, S ended (no side call after it carries one) — and the socket stays a public viewer.
    /// </summary>
    [AvaloniaFact]
    public void AKeyRevokedOnALiveLinkEndsItsSessionAtOnce()
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
            var port = ListenAsStagewatch(peer, files, key, cert, level: 1);
            DialAsAnotherMachine(link, config, port, key);
            PumpUntil(() => link.Record is { Level: 1, Proof: "mutual" } && Count(peer, "served") >= 1, 20000, link);

            TestApp.Pump(peer.Ask(new JsonObject { ["do"] = "set", ["revoke"] = PeerLink.Kid(PeerLink.KeyKey(key)) }));
            PumpUntil(() => link.Key.Refused && link.KeyHeld > TimeSpan.Zero, 10000, link);
            var at = peer.Events.Count;
            PumpUntil(() => peer.Events.Skip(at).Any(e => Str(e, "ev") == "served"), 15000, link);
            Assert.All(peer.Events.Skip(at).Where(e => Str(e, "ev") == "served"), e => Assert.Equal("none", Str(e, "authorization")));
            Assert.Contains("keyless", link.Record!.Flags);
            Assert.Equal(1, Dials(peer));
        }
        finally
        {
            TearDown(b, link, config, peer);
        }
    }

    /// <summary>
    /// S7 through the link: a Stagewatch whose floor says it proved itself with PeerLink 1 over TLS, and now answers LINK with a
    /// frame (LINK stripped), is refused red as a floor — no bearer, no proof, no side call — until a person lowers the floor; then it
    /// is read as the older Stagewatch it says it is, its public view keyless with the key held back for Trust, and every floor kept
    /// for it with the settings is gone, at the other address it was verified at too; another Stagewatch's stays.
    /// </summary>
    [AvaloniaFact]
    public void AStagewatchThatNowOffersLessThanItsFloorIsRefusedUntilAPersonLowersIt()
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
            var port = ListenAsStagewatch(peer, files, key, cert, level: 0);
            // The floor at this address, the same Stagewatch's at another address it was verified at, and another Stagewatch's.
            config.SetFloor("addr:127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture), tls: true, level: 1, proof: "mutual", node: StagewatchId);
            config.SetFloor("addr:192.0.2.7:9100", tls: true, level: 1, proof: "mutual", node: StagewatchId);
            config.SetFloor("node:other-stagewatch", tls: true, level: 1, proof: "mutual", node: "other-stagewatch");
            DialAsAnotherMachine(link, config, port, key);

            PumpUntil(() => link.Record is { State: "refused", Refusal: "floor" }, 15000, link);
            Assert.Equal("red", link.Record!.Light);                                                  // the stage follows it
            Assert.All(peer.Events.Where(e => Str(e, "ev") == "upgrade"), e => Assert.Equal("none", Str(e, "authorization")));
            Assert.DoesNotContain(peer.Events, e => Str(e, "ev") is "served" or "link");
            PumpFor(1500);
            Assert.Equal(1, Dials(peer));                                                             // refused: nothing more is dialled

            var (done, _) = link.LowerFloor();
            Assert.True(done);
            Assert.Equal("node:other-stagewatch", Assert.Single(config.Floors).Key);                 // the node's floor at every address
            PumpUntil(() => link.Record is { State: "live", Level: 0 } && link.TrustFingerprint.Length > 0, 15000, link);
            Assert.Contains("keyless", link.Record!.Flags);
            Assert.All(peer.Events.Where(e => Str(e, "ev") == "upgrade"), e => Assert.Equal("none", Str(e, "authorization")));
        }
        finally
        {
            TearDown(b, link, config, peer);
        }
    }

    /// <summary>
    /// S10 through the link: a verified link the Stagewatch ends is linked again from nothing — a fresh upgrade asking for LINK with no
    /// Authorization header, a new lid — and no side call after it carries the ended link's S: every one the peer served since is taken
    /// with the new link's session. Nothing holds the key.
    /// </summary>
    [AvaloniaFact]
    public void ALinkTheStagewatchEndsIsLinkedAgainFreshAndNoSideCallCarriesItsOldSession()
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
            var port = ListenAsStagewatch(peer, files, key, cert, level: 1);
            DialAsAnotherMachine(link, config, port, key);
            PumpUntil(() => link.Record is { Level: 1, Proof: "mutual", State: "live" } && Count(peer, "served") >= 1, 20000, link);
            var first = Assert.Single(peer.Events, e => Str(e, "ev") == "link" && Str(e, "role") == "acceptor");

            TestApp.Pump(peer.Ask(new JsonObject { ["do"] = "close", ["conn"] = first["conn"]!.GetValue<int>() }));
            PumpUntil(() => peer.Events.Count(e => Str(e, "ev") == "link" && Str(e, "role") == "acceptor") >= 2
                && link.Record is { Level: 1, Proof: "mutual", State: "live" }, 30000, link);
            var second = peer.Events.Where(e => Str(e, "ev") == "link" && Str(e, "role") == "acceptor").ElementAt(1);
            Assert.NotEqual(Str(first, "lid"), Str(second, "lid"));
            var since = second["n"]!.GetValue<int>();
            PumpUntil(() => peer.Events.Any(e => Str(e, "ev") == "served" && e["n"]!.GetValue<int>() > since), 15000, link);
            Assert.All(peer.Events.Where(e => Str(e, "ev") == "served" && e["n"]!.GetValue<int>() > since), e =>
                Assert.Equal(("showbus", 200), (Str(e, "authorization"), e["status"]!.GetValue<int>())));
            Assert.Equal(2, Dials(peer));
            Assert.All(peer.Events.Where(e => Str(e, "ev") == "upgrade"), e =>
                Assert.Equal(("none", "1"), (Str(e, "authorization"), e["query"]!["link"]?.GetValue<string>())));
            Assert.False(link.Key.Refused);
        }
        finally
        {
            TearDown(b, link, config, peer);
        }
    }

    /// <summary>
    /// S6 through the link: once LINK's proof pinned a Stagewatch's certificate, a Stagewatch at that address that shows another is
    /// CERTIFICATE CHANGED — refused on the link's record, and nothing of this desk's reaches it: no upgrade, no link message, no side
    /// call. The proof's pin stands; the certificate shown is waiting for a person's typed accept, never pinned by the desk.
    /// </summary>
    [AvaloniaFact]
    public void AStagewatchPinnedByItsProofThatShowsAnotherCertificateGetsNothingOfThisDesks()
    {
        using var files = new KitFiles();
        var key = "swk_" + RandomNumberGenerator.GetString("abcdefghijkmnpqrstuvwxyz23456789", 32);
        var cert = files.Cert("peer");
        var other = files.Cert("other");
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        var link = services.Kernel.Stagewatch;
        var config = vm.State.Control.Stagewatch;
        var peer = ScriptedPeer.Start();
        try
        {
            var port = ListenAsStagewatch(peer, files, key, cert, level: 1);
            DialAsAnotherMachine(link, config, port, key);
            var fp = TlsFingerprint.Prefix + cert.Fingerprint;
            PumpUntil(() => link.Record is { Level: 1, Proof: "mutual", State: "live" } && config.Pins.Any(p => p.Fingerprint == fp && p.Trust == PinTrust.Proof), 20000, link);

            TestApp.Pump(peer.Ask(new JsonObject { ["do"] = "close", ["end"] = "a" }));
            var from = peer.Events.Count;
            ListenAsStagewatch(peer, files, key, other, level: 1, port: port);
            PumpUntil(() => link.PendingFingerprint == TlsFingerprint.Prefix + other.Fingerprint, 30000, link);
            Assert.StartsWith("CERTIFICATE CHANGED", link.TlsWords, StringComparison.Ordinal);
            Assert.Equal(("refused", "certificate"), (link.Record!.State, link.Record.Refusal));
            PumpFor(2000);
            Assert.DoesNotContain(peer.Events.Skip(from), e => Str(e, "ev") is "upgrade" or "read" or "link" or "served");
            Assert.Contains(config.Pins, p => p.Fingerprint == fp && p.Trust == PinTrust.Proof);
            Assert.DoesNotContain(config.Pins, p => p.Fingerprint == TlsFingerprint.Prefix + other.Fingerprint);
        }
        finally
        {
            TearDown(b, link, config, peer);
        }
    }

    /// <summary>
    /// S13 and S12 through the link at level 1: only a pong that answers one of this desk's pings is a clock sample — unmatched ones are
    /// counted, never sampled — and the stuck beat says the show is stuck until a state frame says it is live again; a pong between
    /// clears nothing.
    /// </summary>
    [AvaloniaFact]
    public void AtLevelOneOnlyAMatchedPongIsASampleAndTheStuckBeatHoldsUntilAFrame()
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
            var port = ListenAsStagewatch(peer, files, key, cert, level: 1);
            DialAsAnotherMachine(link, config, port, key);
            PumpUntil(() => link.Record is { Level: 1, Proof: "mutual", State: "live", Clock.Samples: >= 1 }, 20000, link);

            // S13: unmatched pongs are counted and sampled never.
            TestApp.Pump(peer.Ask(new JsonObject { ["do"] = "set", ["pongs"] = "unmatched" }));
            PumpUntil(() => link.Record is { Counts.Unmatched: >= 1 }, 10000, link);
            var samples = link.Record!.Clock.Samples;
            PumpUntil(() => link.Record is { Counts.Unmatched: >= 3 }, 10000, link);
            Assert.Equal(samples, link.Record!.Clock.Samples);
            TestApp.Pump(peer.Ask(new JsonObject { ["do"] = "set", ["pongs"] = true }));
            PumpUntil(() => link.Record!.Clock.Samples > samples, 10000, link);

            // S12: stuck by the beat; a matched pong after it leaves it stuck; a state frame makes it live.
            TestApp.Pump(peer.Ask(new JsonObject { ["do"] = "set", ["stuck"] = true }));
            PumpUntil(() => link.Sight.Stuck, 10000, link);
            var stuckAt = link.Record!.Clock.Samples;
            PumpUntil(() => link.Record!.Clock.Samples > stuckAt, 10000, link);
            Assert.True(link.Sight.Stuck, "a pong cleared the stuck beat: " + link.Record!.ToJson());
            TestApp.Pump(peer.Ask(new JsonObject { ["do"] = "set", ["stuck"] = false }));
            PumpUntil(() => !link.Sight.Stuck, 10000, link);
            Assert.Equal(1, Dials(peer));
        }
        finally
        {
            TearDown(b, link, config, peer);
        }
    }

    // ---- the desk's side -----------------------------------------------------------------------------------------------

    /// <summary>
    /// What 13.19.5 keeps from every reader of this desk, made again from what crossed the wire on each verified link: the session
    /// credential S, both proofs and both nonces. The client proof made from the transcript is the one the peer read, so the
    /// transcript, and S with it, is the link's own.
    /// </summary>
    private static List<string> LinkSecrets(ScriptedPeer peer, string key)
    {
        var k = PeerLink.KeyKey(key);
        var secrets = new List<string>();
        var events = peer.Events;
        foreach (var linked in events.Where(e => Str(e, "ev") == "link" && Str(e, "role") == "acceptor"))
        {
            var conn = linked["conn"]!.GetValue<int>();
            JsonObject? Message(string ev) => events.Where(e => Str(e, "ev") == ev && e["conn"]?.GetValue<int>() == conn && Str(e, "message") is not null)
                .Select(e => JsonNode.Parse(Str(e, "message")!) as JsonObject).FirstOrDefault(m => m?["type"]?.GetValue<string>() == "link");
            var offerMessage = Message("wrote")!;
            var said = Message("read")!;
            using var offerDoc = System.Text.Json.JsonDocument.Parse(offerMessage.ToJsonString());
            var offer = PeerLink.ReadOfferMessage(offerDoc.RootElement)!;
            var cnonce = said["cnonce"]!.GetValue<string>();
            var kid = said["kid"]!.GetValue<string>();
            var proof = said["proof"]!.GetValue<string>();
            var t = PeerLink.Transcript("ws", PeerLink.Version, offer.Speaks, offer.T + ":" + offer.Id, "patterns:" + said["client"]!["id"]!.GetValue<string>(),
                kid, offer.SNonce, cnonce, offer.Bind)!;
            Assert.Equal(proof, PeerLink.ClientProof(k, t));
            secrets.AddRange([PeerLink.Session(k, t), proof, PeerLink.ServerProof(k, t), cnonce, offer.SNonce]);
        }
        Assert.NotEmpty(secrets);
        return secrets;
    }

    /// <summary>The peer as a Stagewatch on this machine over TLS alone, holding <paramref name="key"/> (bound when named): its port.</summary>
    private static int ListenAsStagewatch(ScriptedPeer peer, KitFiles files, string key, (string CertFile, string KeyFile, string Fingerprint) cert, int level,
        string? bound = null, JsonObject? attack = null, int port = 0)
    {
        var keyRow = new JsonObject { ["file"] = files.Write("key-" + level.ToString(CultureInfo.InvariantCulture), key), ["kind"] = "key" };
        if (bound is not null) keyRow["bound"] = bound;
        var listen = new JsonObject
        {
            ["do"] = "listen", ["carrier"] = "ws", ["tls"] = "only", ["cert"] = cert.CertFile, ["certKey"] = cert.KeyFile, ["node"] = Node("main", StagewatchId),
            ["level"] = level, ["keys"] = new JsonArray(keyRow), ["clock"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
        };
        if (attack is not null) listen["attack"] = attack;
        if (port > 0) listen["port"] = port;
        return Int(TestApp.Pump(peer.Ask(listen)), "port");
    }

    /// <summary>The link turned on at the peer's address with <paramref name="key"/>, loopback playing another machine.</summary>
    private static void DialAsAnotherMachine(StagewatchLinkService link, StagewatchLinkConfig config, int port, string key)
    {
        link.IsLocal = _ => false;
        config.Host = "127.0.0.1:" + port.ToString(CultureInfo.InvariantCulture);
        config.ApiKey = key;
        config.Enabled = true;
        Dispatcher.UIThread.RunJobs();
        link.Reconcile();
    }

    private static void TearDown(TestApp.Booted b, StagewatchLinkService link, StagewatchLinkConfig config, ScriptedPeer peer)
    {
        config.Enabled = false;
        link.Reconcile();
        link.IsLocal = System.Net.IPAddress.IsLoopback;
        Dispatcher.UIThread.RunJobs();
        TestApp.Pump(peer.DisposeAsync().AsTask().ContinueWith(_ => true, TaskScheduler.Default));
        b.Dispose();
    }
}
