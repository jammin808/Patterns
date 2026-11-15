using System.Security.Cryptography;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Patterns.App.Services;
using Patterns.Core.Menus;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

public sealed partial class PeerLinkWireTests
{
    /// <summary>
    /// Show Bus 1.3 §13.4.3 and §13.16.6 through the link: a Stagewatch proved at a typed address, then another node answering LINK at
    /// the same address as another id, is refused as another id and nothing of the key is proved to it. Every reader offers the one
    /// press — the facts the menus read, the LINK group's entry and the Nodes page's ACCEPT NEW ID — bound to the id it was shown: a
    /// press for another id is refused with nothing changed. The press shown accepts it: Done, journalled, the wait gone, and the next
    /// dials reach the new node with no LINK ask and no bearer — the key held for the old node is never proved or sent to the new one,
    /// and the link's words say where it is held.
    /// </summary>
    [AvaloniaFact]
    public void AcceptNewIdBindsTheNodeThatAnsweredAndNeverProvesTheOldNodesKeyToIt()
    {
        const string other = "c0c0c0c0c0c0";
        using var files = new KitFiles();
        var key = "swk_" + RandomNumberGenerator.GetString("abcdefghijkmnpqrstuvwxyz23456789", 32);
        var cert = files.Cert("peer");
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        (vm.State.Control.HttpPort, vm.State.Control.TcpPort) = TestPorts.Two();
        Avalonia.Threading.Dispatcher.UIThread.RunJobs();
        var link = services.Kernel.Stagewatch;
        var config = vm.State.Control.Stagewatch;
        var peer = ScriptedPeer.Start();
        try
        {
            var port = ListenAsStagewatch(peer, files, key, cert, level: 1);
            DialAsAnotherMachine(link, config, port, key);
            PumpUntil(() => link.Record is { Level: 1, Proof: "mutual", State: "live" }, 20000, link);

            // Another node at the same address, the same certificate, answering LINK as another id.
            TestApp.Pump(peer.Ask(new JsonObject { ["do"] = "close", ["end"] = "a" }));
            TestApp.Pump(peer.Ask(new JsonObject
            {
                ["do"] = "listen", ["carrier"] = "ws", ["tls"] = "only", ["cert"] = cert.CertFile, ["certKey"] = cert.KeyFile, ["node"] = Node("main", other),
                ["level"] = 1, ["port"] = port, ["clock"] = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),
                ["keys"] = new JsonArray(new JsonObject { ["file"] = files.Write("key-other", key), ["kind"] = "key" }),
            }));
            PumpUntil(() => link.OtherId == other, 20000, link);
            Assert.Equal("refused", link.Record!.State);
            var proofs = Proofs(peer);

            // Every reader offers the one press, bound to the id it shows (the menus read the cards the Nodes tick keeps).
            PumpUntil(() => DeskMenuFacts.Desk(services).Stagewatch.LinkedId.Length > 0, 10000, link);
            var facts = DeskMenuFacts.Desk(services);
            Assert.Equal(other, facts.Stagewatch.OtherId);
            var entry = DeskMenus.LinkGroup(facts, facts.Stagewatch.LinkedId, true, "Hall A")?.Entries.FirstOrDefault(e => e.Id == "link.newid");
            Assert.True(entry is not null, "no Accept new id entry: " + facts.Stagewatch.LinkedId + " · " + link.Record.ToJson());
            Assert.Equal((ShowActionKind.StagewatchAcceptId, other, ""), (entry!.Action!.Value.Kind, entry.Action.Value.Value, entry.Because));
            vm.LinkBoxes.Look();
            Assert.Equal((true, other), (vm.LinkBoxes.HasOtherId, vm.LinkBoxes.OtherId));
            Assert.Contains(other, vm.LinkBoxes.OtherIdWords, StringComparison.Ordinal);

            // A press drawn for another id changes nothing.
            var stale = services.Actions.Execute(new ShowAction(ShowActionKind.StagewatchAcceptId, "", "ffffffffffff"), ActionOrigin.Desk);
            Assert.Equal(ActionStatus.Refused, stale.Status);
            Assert.Contains($"The node answering now is {other}", stale.Message, StringComparison.Ordinal);
            Assert.Equal(other, link.OtherId);

            // The Nodes page's button is the menu entry's press: accepted, journalled once, the wait gone.
            var dials = Dials(peer);
            vm.LinkBoxes.AcceptNewIdCommand.Execute(null);
            Assert.Equal("", link.OtherId);
            Assert.False(vm.LinkBoxes.HasOtherId);
            Assert.Single(services.Journal.Tail(500), e => e.Message.Contains($"Accepted {other}", StringComparison.Ordinal));

            // The new node is dialled with no LINK ask and no bearer: the old node's key goes nowhere near it.
            PumpUntil(() => Dials(peer) > dials && link.Record is { State: "live", PeerId: other }, 20000, link);
            var after = peer.Events.Where(e => Str(e, "ev") == "upgrade").Skip(dials).ToList();
            Assert.All(after, e => Assert.Equal("none", Str(e, "authorization")));
            Assert.All(after, e => Assert.Null(e["query"]?["link"]));
            Assert.Equal(proofs, Proofs(peer));
            var heldWords = PeerLinkMachine.KeyHeldForWords(StagewatchId, other);
            PumpUntil(() => link.Record?.Words.Contains(heldWords, StringComparison.Ordinal) == true, 10000, link);
            Assert.Contains("keyless", link.Record!.Flags);
        }
        finally
        {
            TearDown(b, link, config, peer);
        }
    }

    /// <summary>The link messages that carried a proof, as the peer read them: what a connector proved to whichever node was there.</summary>
    private static int Proofs(ScriptedPeer peer)
        => peer.Events.Count(e => Str(e, "ev") == "read" && (Str(e, "message") ?? "").Contains("\"proof\"", StringComparison.Ordinal));
}
