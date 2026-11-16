using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 99.7: a link set up by a typed address looks again at who is there. A Stagewatch that restarts opens its web port before its
/// first hello is heard, so a redial in that gap takes the bare address as its target; the link's evaluator reads the one rule its dial
/// reads (<see cref="StagewatchAt"/>) on each beat, and once a live hello from that address and port names the node it ends that
/// connection once and dials it again as the node — the key proved there with LINK to the id meant, never sent while the target was
/// the address. Only a link whose node's dial would prove the key moves; a keyless one, one whose key is bound elsewhere and one that
/// carries the key at the address already stay the address's own record. A move whose dial the node refuses goes back to the address's
/// public view at once, once. The scripted peer plays the Stagewatch on another machine over TLS; its hello is heard from the address it
/// is dialled at, as a beacon's datagram is.
/// </summary>
public sealed partial class PeerLinkWireTests
{
    /// <summary>
    /// The interop lane's S6.1, the rig restarted, in one desk: the key bound to the node, the address dialled before any hello (the
    /// key held back, the public view read, BIND KEY offered for the address), then the node's hello from that address: one more dial,
    /// as the node, the key proved with LINK and the record, STATE, the Eye's card and the journal naming the node; a second hello and
    /// more beats dial nothing further.
    /// </summary>
    [AvaloniaFact]
    public void ATypedAddressWhoseNodeIsHeardThereLaterIsDialledAgainOnceAsThatNode()
    {
        using var files = new KitFiles();
        var key = "swk_" + RandomNumberGenerator.GetString("abcdefghijkmnpqrstuvwxyz23456789", 32);
        var cert = files.Cert("peer");
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        (vm.State.Control.HttpPort, vm.State.Control.TcpPort) = TestPorts.Two();
        Dispatcher.UIThread.RunJobs();
        var link = services.Kernel.Stagewatch;
        var config = vm.State.Control.Stagewatch;
        var peer = ScriptedPeer.Start();
        try
        {
            var port = ListenAsStagewatch(peer, files, key, cert, level: 1);
            var addr = StagewatchKeyFor.Address("127.0.0.1", port);
            config.KeyFor = StagewatchKeyFor.Node(StagewatchId);                                 // its first answer bound it to the node, before the restart
            DialAsAnotherMachine(link, config, port, key);

            // The gap: no hello yet. The address is its own record, read in its public view; the key stays on this desk.
            PumpUntil(() => link.Record is { State: "live" } r && r.PeerName == "127.0.0.1" && r.Show == "live", 20000, link);
            Assert.Equal(1, Dials(peer));
            Assert.Null(link.Record.PeerId);
            Assert.Contains("keyless", link.Record.Flags);
            Assert.Equal(addr, link.KeyBindTarget);                                              // what 2515017 left the operator with

            // The node's hello, heard from the address it is dialled at: the link dials it again as the node, once.
            void Hello() => HearStagewatch(services, port, cert.Fingerprint);
            PumpUntil(() =>
            {
                Hello();
                return link.Record is { State: "live", PeerId: StagewatchId, Level: 1, Proof: "mutual" };
            }, 20000, link);
            var record = link.Record!;
            Assert.Equal(("Hall A", StagewatchId), (record.PeerName, link.NodeId));
            Assert.Equal(2, Dials(peer));
            Assert.Equal("", link.KeyBindTarget);
            Assert.Equal("key", link.Key.Word);

            // What the fake heard: the key was proved once, on the second upgrade, and never sent on any.
            var upgrades = peer.Events.Where(e => Str(e, "ev") == "upgrade").ToList();
            Assert.All(upgrades, e => Assert.Equal("none", Str(e, "authorization")));
            Assert.Null(upgrades[0]["query"]!["link"]?.GetValue<string>());                       // the address: no LINK, no key
            var links = peer.Events.Where(e => Str(e, "ev") == "link" && Str(e, "role") == "acceptor").ToList();
            Assert.Equal("1", upgrades[1]["query"]!["link"]?.GetValue<string>());
            Assert.Equal(upgrades[1]["conn"]!.GetValue<int>(), Assert.Single(links)["conn"]!.GetValue<int>());

            // Every reader names the node: STATE's row, the Eye's one card linked, and one journal row and one log line for the retarget.
            var row = JsonDocument.Parse(new CommandRouter(services).StateJson()).RootElement.GetProperty("bus").GetProperty("stagewatch");
            Assert.Equal(record.Words, row.GetProperty("link").GetProperty("words").GetString());
            Assert.Equal(StagewatchId, row.GetProperty("link").GetProperty("peer").GetProperty("id").GetString());
            var linked = Assert.Single(services.Apps(DateTime.UtcNow), a => a.Linked);
            Assert.Equal(StagewatchId, linked.Id);
            var said = $"Hall A (Stagewatch {StagewatchId}) is heard at 127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)} — dialling it again as that Stagewatch.";
            Assert.Single(services.Journal.Tail(500), e => e.Kind == "StagewatchLink" && e.Outcome == "retarget" && e.Message == said);
            Assert.Equal(1, LinkLog(services, b.Dir).Split(" | ").Count(l => l.EndsWith(said, StringComparison.Ordinal)));

            // A second hello and more beats: nothing new, so nothing more is dialled.
            for (var i = 0; i < 30; i++)
            {
                Hello();
                PumpFor(100);
            }
            Assert.Equal(2, Dials(peer));
            Assert.Equal(StagewatchId, link.Record!.PeerId);

            TestApp.FlushFiles(services);
            var desk = new[] { File.ReadAllText(Path.Combine(b.Dir, "patterns.log")), File.ReadAllText(services.Journal.Path), new CommandRouter(services).StateJson(), link.Record?.ToJson() ?? "" };
            var printed = string.Join("\n", peer.Events.Select(e => e.ToJsonString()));
            foreach (var canary in new[] { key, Convert.ToHexStringLower(PeerLink.KeyKey(key)), "Bearer " })
                Assert.All(desk.Append(printed), r => Assert.DoesNotContain(canary, r, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            config.KeyFor = "";
            TearDown(b, link, config, peer);
        }
    }

    /// <summary>
    /// Where the node's own dial would be refused now, the address's public view is kept — the room's time on the wall — and the record
    /// says why in place of BIND KEY: the key held for the node (it is presented to it only when the hold ends; LINK AGAIN ends it, and
    /// the link then dials the node), or a certificate changed against the node's pin (offered for ACCEPT, which a person types; then
    /// the link dials the node with that pin).
    /// </summary>
    [AvaloniaTheory]
    [InlineData("held")]
    [InlineData("changed")]
    public void ANodeWhoseOwnDialWouldBeRefusedKeepsTheAddressesPublicViewAndSaysWhy(string why)
    {
        using var files = new KitFiles();
        var key = "swk_" + RandomNumberGenerator.GetString("abcdefghijkmnpqrstuvwxyz23456789", 32);
        var cert = files.Cert("peer");
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        (vm.State.Control.HttpPort, vm.State.Control.TcpPort) = TestPorts.Two();
        Dispatcher.UIThread.RunJobs();
        var link = services.Kernel.Stagewatch;
        var config = vm.State.Control.Stagewatch;
        var peer = ScriptedPeer.Start();
        var kid = PeerLink.Kid(PeerLink.KeyKey(key));
        try
        {
            var port = ListenAsStagewatch(peer, files, key, cert, level: 1);
            config.KeyFor = StagewatchKeyFor.Node(StagewatchId);
            var now = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
            if (why == "held") link.Holds.Hold("node:" + StagewatchId, kid, now + 120_000, "key", now);
            else config.SetPin("node:" + StagewatchId, TlsFingerprint.Prefix + new string('e', 64), PinTrust.Person);
            DialAsAnotherMachine(link, config, port, key);
            PumpUntil(() => link.Record is { State: "live" } r && r.PeerName == "127.0.0.1" && r.Show == "live", 20000, link);

            void Hello() => HearStagewatch(services, port, cert.Fingerprint);
            var because = why == "held" ? "this desk's key is held for it" : "pinned for it — nothing of this desk's goes to it; compare it on its screen and ACCEPT it";
            PumpUntil(() =>
            {
                Hello();
                return link.Record.Words.Contains("is heard at this address, but " + (why == "held" ? because : "it shows"), StringComparison.Ordinal);
            }, 10000, link);
            for (var i = 0; i < 30; i++)
            {
                Hello();
                PumpFor(100);
            }
            var record = link.Record;
            Assert.Equal(1, Dials(peer));                                                         // kept: no dial into the refusal
            Assert.Equal(("live", "127.0.0.1"), (record.State, record.PeerName));
            Assert.Equal("live", record.Show);                                                   // the room's time still followed
            Assert.Contains("Hall A (Stagewatch " + StagewatchId + ") is heard at this address", record.Words, StringComparison.Ordinal);
            Assert.Contains(because, record.Words, StringComparison.Ordinal);
            Assert.DoesNotContain("BIND KEY", record.Words, StringComparison.Ordinal);
            Assert.Equal("", link.KeyBindTarget);                                                // the press that would bind the key to the address is gone
            Assert.DoesNotContain(peer.Events, e => Str(e, "ev") == "link");
            var kept = record.Words[(record.PeerName.Length + " · public view — ".Length)..];
            Assert.Single(services.Journal.Tail(500), e => e.Kind == "StagewatchLink" && e.Outcome == "kept" && e.Message == kept);
            Assert.Equal((false, kept), (link.Route.Can, link.Route.Why));                         // the speaker route says the record's why, never "offers no TLS"
            Assert.Equal(record.Words, JsonDocument.Parse(new CommandRouter(services).StateJson()).RootElement.GetProperty("bus").GetProperty("stagewatch").GetProperty("link").GetProperty("words").GetString());

            // What the operator does about it, and the node is then dialled with its key.
            if (why == "held")
            {
                var again = link.LinkAgain();
                Assert.True(again.Done, again.Words);
            }
            else
            {
                Assert.Equal(("node:" + StagewatchId, TlsFingerprint.Prefix + cert.Fingerprint), (link.PendingPinKey, link.PendingFingerprint));
                var again = link.LinkAgain();
                Assert.False(again.Done);
                Assert.Contains(kept + " — the address's public view is read meanwhile; LINK AGAIN does not lift a changed certificate", again.Words, StringComparison.Ordinal);
                var accepted = link.AcceptCertificate(cert.Fingerprint[..8]);
                Assert.True(accepted.Accepted, accepted.Words);
            }
            // The record goes live in the core's step on the reader's thread and the LINK verdict's key standing is applied in the
            // next (LinkVerdict → Apply), so a reader on another thread can see the record live a few tens of ms before the key's
            // word: the wait reads both facts, and a word other than "key" still fails below.
            PumpUntil(() =>
            {
                Hello();
                return link.Record is { State: "live", PeerId: StagewatchId, Level: 1, Proof: "mutual" } && link.Key.Word.Length > 0;
            }, 20000, link);
            Assert.Equal(2, Dials(peer));
            Assert.Equal("key", link.Key.Word);
        }
        finally
        {
            config.KeyFor = "";
            TearDown(b, link, config, peer);
        }
    }

    /// <summary>
    /// 13.4.3: a hello alone never moves a link that its node's dial would not prove — the address stays its own record, dialled once,
    /// however many hellos name a node there: a link with no key (nothing would be verified, so nothing a hello claims names the address's
    /// public view), one whose key is bound to another Stagewatch (the node's dial would hold it back too), and one whose key is bound to
    /// the address and proved there with LINK already (ending it would only cost its commands and speaker queue).
    /// </summary>
    [AvaloniaTheory]
    [InlineData("none")]
    [InlineData("elsewhere")]
    [InlineData("address")]
    public void AnAddressLinkWhoseNodesDialWouldProveNothingMoreStaysTheAddressesOwnRecord(string how)
    {
        using var files = new KitFiles();
        var key = "swk_" + RandomNumberGenerator.GetString("abcdefghijkmnpqrstuvwxyz23456789", 32);
        var cert = files.Cert("peer");
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        (vm.State.Control.HttpPort, vm.State.Control.TcpPort) = TestPorts.Two();
        Dispatcher.UIThread.RunJobs();
        var link = services.Kernel.Stagewatch;
        var config = vm.State.Control.Stagewatch;
        var peer = ScriptedPeer.Start();
        try
        {
            var port = ListenAsStagewatch(peer, files, key, cert, level: 1);
            if (how == "elsewhere") config.KeyFor = StagewatchKeyFor.Node("b0b0b0b0b0b0");
            if (how == "address") config.KeyFor = StagewatchKeyFor.Address("127.0.0.1", port);
            DialAsAnotherMachine(link, config, port, how == "none" ? "" : key);
            PumpUntil(() => link.Record is { State: "live" } r && r.PeerName == "127.0.0.1" && r.Show == "live" && (how != "address" || r.Proof == "mutual"), 20000, link);
            var before = link.Record.PeerId;
            for (var i = 0; i < 30; i++)
            {
                HearStagewatch(services, port, cert.Fingerprint);
                PumpFor(100);
            }
            var record = link.Record;
            Assert.Equal(1, Dials(peer));
            Assert.Equal(("live", "127.0.0.1", "live", before), (record.State, record.PeerName, record.Show, record.PeerId));
            if (how != "address") Assert.Null(record.PeerId);                                    // nothing verified names the address
            Assert.DoesNotContain(services.Journal.Tail(500), e => e.Kind == "StagewatchLink" && e.Outcome is "retarget" or "kept");
            Assert.DoesNotContain("is heard at this address", record.Words, StringComparison.Ordinal);
            if (how == "address") Assert.Equal(("mutual", "key"), (record.Proof, link.Key.Word));
        }
        finally
        {
            config.KeyFor = "";
            TearDown(b, link, config, peer);
        }
    }

    /// <summary>
    /// A move a hello asked for, whose dial the node refuses: a hello that names another id from the address (a forged datagram, or a
    /// node picked out of two) moves the link, the acceptor answers LINK as itself, not the id meant — and the link goes back to the
    /// address at once, its public view and the room's time read again, rather than wait refused for a person. The record says why, and
    /// that process's hellos move nothing again: one move, one way back, three dials in all, one row of each.
    /// </summary>
    [AvaloniaFact]
    public void AMoveTheNodeRefusesGoesBackToTheAddressesPublicViewOnce()
    {
        const string forged = "b0b0b0b0b0b0";
        using var files = new KitFiles();
        var key = "swk_" + RandomNumberGenerator.GetString("abcdefghijkmnpqrstuvwxyz23456789", 32);
        var cert = files.Cert("peer");
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        (vm.State.Control.HttpPort, vm.State.Control.TcpPort) = TestPorts.Two();
        Dispatcher.UIThread.RunJobs();
        var link = services.Kernel.Stagewatch;
        var config = vm.State.Control.Stagewatch;
        var peer = ScriptedPeer.Start();
        try
        {
            var port = ListenAsStagewatch(peer, files, key, cert, level: 1);
            config.KeyFor = StagewatchKeyFor.Node(forged);
            DialAsAnotherMachine(link, config, port, key);
            PumpUntil(() => link.Record is { State: "live" } r && r.PeerName == "127.0.0.1" && r.Show == "live", 20000, link);

            void Hello() => HearStagewatch(services, port, cert.Fingerprint, forged);
            bool Back() => services.Journal.Tail(500).Any(e => e.Kind == "StagewatchLink" && e.Outcome == "back");
            PumpUntil(() =>
            {
                Hello();
                return Back() && link.Record is { State: "live" } r && r.PeerName == "127.0.0.1" && r.Show == "live" && Dials(peer) == 3;
            }, 20000, link);
            for (var i = 0; i < 30; i++)
            {
                Hello();
                PumpFor(100);
            }
            var record = link.Record;
            Assert.Equal(3, Dials(peer));                                                         // the address, the node once, the address again
            Assert.Equal(("live", "127.0.0.1", "live"), (record.State, record.PeerName, record.Show));
            Assert.Null(record.PeerId);
            Assert.Contains($"Hall A (Stagewatch {forged}) is heard at this address, but its own dial from here was refused (other-id)", record.Words, StringComparison.Ordinal);
            Assert.DoesNotContain("BIND KEY", record.Words, StringComparison.Ordinal);
            Assert.Equal("", link.KeyBindTarget);
            var rows = services.Journal.Tail(500).Where(e => e.Kind == "StagewatchLink").ToList();
            Assert.Single(rows, e => e.Outcome == "retarget");
            Assert.Single(rows, e => e.Outcome == "back" && e.Message.Contains("refused the dial as that Stagewatch (other-id)", StringComparison.Ordinal));
            Assert.Single(rows, e => e.Outcome == "kept");
            var upgrades = peer.Events.Where(e => Str(e, "ev") == "upgrade").ToList();
            Assert.All(upgrades, e => Assert.Equal("none", Str(e, "authorization")));             // the key was never sent, to either
            TestApp.FlushFiles(services);
            var desk = new[] { File.ReadAllText(Path.Combine(b.Dir, "patterns.log")), File.ReadAllText(services.Journal.Path), new CommandRouter(services).StateJson() };
            foreach (var canary in new[] { key, Convert.ToHexStringLower(PeerLink.KeyKey(key)) })
                Assert.All(desk, r => Assert.DoesNotContain(canary, r, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            config.KeyFor = "";
            TearDown(b, link, config, peer);
        }
    }

    /// <summary>The scripted Stagewatch's hello, heard from 127.0.0.1 — the address the link dials it at — naming its one TLS port and certificate (and an id, its own unless named).</summary>
    private static void HearStagewatch(AppServices services, int port, string fingerprint, string id = StagewatchId)
    {
        var p = port.ToString(CultureInfo.InvariantCulture);
        var hello = $"{{\"t\":\"main\",\"v\":1,\"proto\":3,\"id\":\"{id}\",\"inst\":\"0a1b2c3d\",\"name\":\"Hall A\",\"host\":\"STAGE-PC\",\"ports\":{{\"http\":{p},\"ws\":{p},\"https\":{p}}},\"caps\":[\"context\"],\"tls\":\"{TlsFingerprint.Prefix}{fingerprint}\"}}";
        services.Kernel.Beacon.ShowBus.Hear(Encoding.UTF8.GetBytes(hello), "127.0.0.1", DateTime.UtcNow, "not-this-desk");
    }
}
