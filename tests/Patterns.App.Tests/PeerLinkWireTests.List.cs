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
/// Show Bus 1.3 §13.16.5 at this desk's own wire: the acceptor's list. The scripted peer links as Stagewatch would — HELLO, then LINK
/// with a key — beside a deck that only said HELLO, from loopback with this machine's trust off (restored in finally by the desk).
/// The app link is on the list and in STATE's decks rows at level 1 with its t, id, verified and scope at the end of the row, and in
/// no deck reader (the decks, the Remote page's strip, the navigator's row, the deck signature's count); the Nodes page's CLOSE, from
/// its row's right-click menu, ends it with its words and one journal row, refused from every other origin; it stays grey for
/// peer.forget.ms and leaves; a refusal is counted this minute.
/// </summary>
public sealed partial class PeerLinkWireTests
{
    private static JsonObject DialDeck(AcceptorDesk desk, string name) => new()
    {
        ["do"] = "dial", ["carrier"] = "line", ["port"] = desk.Port, ["tls"] = false, ["pin"] = null,
        ["node"] = Node("main", "a1b2c3d4e5f6"), ["key"] = null, ["meant"] = Node("patterns", desk.Self), ["name"] = name,
    };

    private static JsonElement[] DeckRows(AcceptorDesk desk)
    {
        using var doc = JsonDocument.Parse(new CommandRouter(desk.Services).StateJson());
        return doc.RootElement.GetProperty("decks").EnumerateArray().Select(e => e.Clone()).ToArray();
    }

    /// <summary>STATE's <c>bus.heard</c>: how many other show apps the desk hears on the show network.</summary>
    private static int BusHeard(AcceptorDesk desk)
    {
        using var doc = JsonDocument.Parse(new CommandRouter(desk.Services).StateJson());
        return doc.RootElement.GetProperty("bus").GetProperty("heard").GetInt32();
    }

    /// <summary>The apps other than the linked Stagewatch whose hello this desk hears now (another desk's beacon on this machine, say).</summary>
    private static int HeardElsewhere(AcceptorDesk desk)
        => ShowBusView.Published(desk.Services.Apps(DateTime.UtcNow)).Count(a => a.Id != StagewatchId && ShowBusView.HeardLive(a));

    [AvaloniaFact]
    public void AnAppLinkIsOnTheAcceptorsListAndInStateAtLevelOneNeverADeckAndTheOperatorClosesItFromItsRow()
    {
        using var files = new KitFiles();
        using var desk = new AcceptorDesk();
        var key = NewKey();
        var planted = LinkAcceptor.Key(key, "main:" + StagewatchId, "drive")!;
        desk.Control.LinkKeys = [planted];

        // A deck that said HELLO and proved nothing, and a Stagewatch that said HELLO and then proved a key by LINK over TLS.
        TestApp.Pump(desk.Peer.Ask(DialDeck(desk, "FOH deck")));
        var (conn, linked) = Dialled(desk, DialDesk(desk, tls: true, files.Write("key", key), "key"));
        Assert.Equal(("link", "key"), (Str(linked, "ev"), Str(linked, "verified")));
        ReadOn(desk, conn, l => l.StartsWith("STATE {", StringComparison.Ordinal));
        PumpWhile(() => desk.Control.Decks.Count == 0 || desk.Control.AppLinkList.Count == 0, 5000);

        // Every deck reader drops the proved app: the decks, the Remote page's strip, the navigator's row.
        var deck = Assert.Single(desk.Control.Decks);
        Assert.Equal("FOH deck", deck.Name);
        Assert.DoesNotContain("Stagewatch", CompanionWords.DecksLine(desk.Control.Decks), StringComparison.Ordinal);
        using (var nav = JsonDocument.Parse(JsonSerializer.Serialize(Assert.IsAssignableFrom<object>(desk.Services.Actions.NavRow()))))
            Assert.Equal(new[] { "FOH deck" }, nav.RootElement.GetProperty("decks").EnumerateArray().Select(d => d.GetProperty("name").GetString()));
        var link = Assert.Single(desk.Control.AppLinkList);
        Assert.Equal(("Stagewatch", "main:" + StagewatchId, "key", planted.Kid, "drive", true, CheckLight.Green), (link.Name, link.Client, link.Verified, link.Kid, link.Scope, link.Tls, link.Light));
        Assert.Equal(Str(linked, "lid"), link.Lid);

        // STATE's decks rows: the deck at level 0 (paired by nothing: scope none), then the app link at level 1, its fields at the end.
        var rows = DeckRows(desk);
        Assert.Equal(2, rows.Length);
        var deckRow = rows.Single(r => r.GetProperty("level").GetInt32() == 0);
        Assert.Equal(("FOH deck", "", "", "", "none"), (deckRow.GetProperty("name").GetString(), deckRow.GetProperty("t").GetString(), deckRow.GetProperty("id").GetString(), deckRow.GetProperty("verified").GetString(), deckRow.GetProperty("scope").GetString()));
        Assert.Equal(new[] { "name", "module", "address", "where", "recording", "level", "t", "id", "verified", "scope" }, deckRow.EnumerateObject().Select(p => p.Name));
        var appRow = rows.Single(r => r.GetProperty("level").GetInt32() == 1);
        Assert.Equal(new[] { "name", "module", "address", "where", "recording", "level", "t", "id", "verified", "scope", "tls", "kid", "light", "mark", "words", "ended", "told" }, appRow.EnumerateObject().Select(p => p.Name));
        Assert.False(appRow.GetProperty("told").GetBoolean());                                   // round 100.5: told its last word, closing — not yet
        Assert.Equal(("main", StagewatchId, "key", "drive", "green", "✓", false), (appRow.GetProperty("t").GetString(), appRow.GetProperty("id").GetString(), appRow.GetProperty("verified").GetString(), appRow.GetProperty("scope").GetString(), appRow.GetProperty("light").GetString(), appRow.GetProperty("mark").GetString(), appRow.GetProperty("ended").GetBoolean()));

        // The Run strip's glance and the assistant's facts name it as an app link; the brief says closing it is the operator's.
        Assert.Contains("APP LINK ✓", desk.Services.GlanceWords, StringComparison.Ordinal);
        var facts = desk.Services.GatherFacts().ShowNetwork;
        Assert.Contains(facts, l => l.StartsWith("App link ✓ Stagewatch (Stagewatch " + StagewatchId, StringComparison.Ordinal));
        Assert.Contains(facts, l => l.Contains("never proposed", StringComparison.Ordinal) && l.Contains("CLOSE", StringComparison.Ordinal));

        // The feed: LINK proved the connector's id, so it is an app every reader takes — no hello names it here — its
        // link the acceptor's row: the composition, STATE's bus.apps, the Nodes card, the brief, and the Eye's one card with one edge.
        var app = Assert.Single(desk.Services.Apps(DateTime.UtcNow), a => a.Id == StagewatchId);
        Assert.Equal(("Stagewatch", ShowBusState.Live, "", false), (app.KindWords, app.State, app.Heard, app.Linked));
        Assert.Equal(link.Lid, Assert.Single(app.Accepted).Lid);
        Assert.True(ShowBusView.HoldsLink(app));
        using (var state = JsonDocument.Parse(new CommandRouter(desk.Services).StateJson()))
        {
            var busApp = Assert.Single(state.RootElement.GetProperty("bus").GetProperty("apps").EnumerateArray(), r => r.GetProperty("id").GetString() == StagewatchId);
            Assert.Equal(("main", "green", ShowBusView.AcceptedWord), (busApp.GetProperty("kind").GetString(), busApp.GetProperty("light").GetString(), busApp.GetProperty("headline").GetString()));
        }
        Assert.Contains(facts, l => l.StartsWith("Stagewatch Stagewatch — " + ShowBusView.AcceptedWord + " — green", StringComparison.Ordinal));
        desk.Services.Eye.Refresh();
        var card = Assert.Single(desk.Services.Eye.Graph.Nodes, n => n.Id == "app:" + StagewatchId);
        Assert.Equal((EyeOrigin.Link, CheckLight.Green, link.Lid), (card.Origin, card.Light, card.Route!.Item));
        var edge = Assert.Single(desk.Services.Eye.Graph.Edges, e => e.From == card.Id);
        Assert.Equal((EyeGraph.DeskId, EyeOrigin.Link), (edge.To, edge.Origin));
        // Heard means its hello is heard: STATE's bus.heard counts no app live only by a link this desk's wire accepted.
        Assert.Equal(HeardElsewhere(desk), BusHeard(desk));

        // Its hello heard as well: still one card and one edge to the desk — the link's, never a green "heard" edge beside it — and
        // counted heard now. A still desk holding the link and hearing the hello rebuilds nothing, tick after tick, and the apps'
        // revision the gather hands the hash in the apps' place stands.
        var hello = new JsonObject
        {
            ["t"] = "main", ["v"] = 1, ["proto"] = 3, ["id"] = StagewatchId, ["inst"] = "0a1b2c3d", ["name"] = "Hall A", ["host"] = "STAGE-PC",
            ["ports"] = new JsonObject { ["http"] = 9924, ["ws"] = 9924 }, ["caps"] = new JsonArray("context"),
        };
        desk.Services.Kernel.Beacon.ShowBus.Hear(Encoding.UTF8.GetBytes(hello.ToJsonString()), "127.0.0.1", DateTime.UtcNow, "not-this-desk");
        Assert.Equal("live", Assert.Single(desk.Services.Apps(DateTime.UtcNow), a => a.Id == StagewatchId).Heard);
        desk.Services.Eye.Refresh();
        var heardEdge = Assert.Single(desk.Services.Eye.Graph.Edges, e => e.From == card.Id);
        Assert.Equal((EyeGraph.DeskId, EyeOrigin.Link), (heardEdge.To, heardEdge.Origin));
        Assert.StartsWith("✓ linked to this desk by LINK", heardEdge.Words, StringComparison.Ordinal);
        Assert.Equal(HeardElsewhere(desk) + 1, BusHeard(desk));
        // The count begins once the desk has taken its own first readings (its status tick, its sampler's sample of the process's
        // contained errors), never on a desk younger than its own 1 s clocks.
        var still = StillDesk.Baseline(desk.Services);
        var rev = desk.Services.Eye.AppsRev;
        Assert.True(rev > 0 && rev == desk.Services.AppsRev, $"the gather read revision {rev}; the desk's apps stand at {desk.Services.AppsRev}");
        for (var tick = 0; tick < 10; tick++)
        {
            Thread.Sleep(100);
            Dispatcher.UIThread.RunJobs();
            desk.Services.Eye.Refresh();
        }
        // A rebuild names every node that moved, with the contained errors counted at both ends of the window.
        StillDesk.AssertStill(desk.Services, still, 10, $"a still link and hello (the apps' revision {rev} → {desk.Services.Eye.AppsRev})");
        Assert.Equal(rev, desk.Services.Eye.AppsRev);

        // CLOSE is the desk operator's: refused from a deck, the wire and a running order, and the link stands.
        foreach (var origin in new[] { new ActionOrigin(OriginKind.Companion, "FOH deck"), new ActionOrigin(OriginKind.Tcp, "Stagewatch"), ActionOrigin.Schedule })
            Assert.Equal(ActionStatus.Refused, desk.Services.Actions.Execute(new ShowAction(ShowActionKind.AppLinkClose, link.Lid), origin).Status);
        Assert.Equal(1, desk.Control.AppLinks);

        // A link that asks for the action feed is listed recording, as a deck would be; NAV DECK is operate's and a drive key is refused it.
        Send(desk, conn, "@r1 RECORD ON");
        ReadOn(desk, conn, l => l.StartsWith("OK @r1", StringComparison.Ordinal));
        Send(desk, conn, "@n1 NAV DECK Cues");
        Assert.Equal("ERR @n1 " + LinkAcceptor.ScopeWords("drive", desk.Services.Kernel.Beacon.MachineName), Str(ReadOn(desk, conn, l => l.Contains("@n1", StringComparison.Ordinal)), "line"));
        Assert.True(Assert.Single(desk.Control.AppLinkList).Recording);
        Assert.True(Assert.Single(DeckRows(desk), r => r.GetProperty("level").GetInt32() == 1).GetProperty("recording").GetBoolean());
        Assert.Contains("recording the desk's actions", Assert.Single(desk.Control.AppLinkList).Words, StringComparison.Ordinal);

        // The Nodes page's row, closed from its right-click menu: the link hears why as its last line and runs nothing from the press on —
        // a drive verb it sends while the desk holds the connection open is never run, a second press is refused — and it goes grey.
        var vm = desk.Booted.Vm;
        vm.AppLinks.Look();
        var tableRow = Assert.Single(vm.AppLinks.Rows);
        Assert.True(tableRow.CanClose && tableRow.CanRevoke);
        var spoken = Rows(desk.Services, nameof(ShowActionKind.StageMessage)).Count;
        desk.Control.CloseLinger = TimeSpan.FromSeconds(1);
        vm.MenuFor("applink", tableRow)!.Find("link.close")!.ChooseCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        ReadOn(desk, conn, l => l == "ERR LINK refused — " + AcceptorWords.ClosedWords(desk.Services.Kernel.Beacon.MachineName));
        var again = desk.Services.Actions.Execute(new ShowAction(ShowActionKind.AppLinkClose, link.Lid), ActionOrigin.Desk);
        Assert.Equal(ActionStatus.Refused, again.Status);
        Assert.Contains("closing already", again.Message, StringComparison.Ordinal);
        Send(desk, conn, "@s9-11aa22bb STAGE MESSAGE Run anyway");
        ClosedByDesk(desk, conn);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(spoken, Rows(desk.Services, nameof(ShowActionKind.StageMessage)).Count);
        Assert.DoesNotContain(desk.Peer.Events, e => Str(e, "ev") == "read" && (Str(e, "line") ?? "").Contains("@s9-11aa22bb", StringComparison.Ordinal));
        PumpWhile(() => desk.Control.AppLinks != 0 || desk.Control.AppLinkList.All(r => !r.Ended), 5000);
        var ended = Assert.Single(desk.Control.AppLinkList);
        Assert.Equal((CheckLight.Grey, "○", "closed by this desk's operator", "drive"), (ended.Light, ended.Mark, ended.EndWords, ended.Scope));
        Assert.True(Assert.Single(DeckRows(desk), r => r.GetProperty("level").GetInt32() == 1).GetProperty("ended").GetBoolean());
        Assert.Equal("", AcceptorWords.Glance(desk.Control.AppLinkList));
        Assert.Contains(desk.Control.LinkKeys, k => k.Kid == planted.Kid);                    // the key stays in force: revoking it is the wall
        // Requested at the press, observed by the grey row: one journal row from the desk, the second press's refusal beside the three others'.
        Assert.Single(Rows(desk.Services, nameof(ShowActionKind.AppLinkClose)), r => r.Target == link.Lid && r.Outcome == "Requested" && r.Origin == "desk");
        Assert.Equal(4, Rows(desk.Services, nameof(ShowActionKind.AppLinkClose)).Count(r => r.Outcome == "Refused"));
        // A stale press changes nothing and says why.
        Assert.Equal(ActionStatus.Refused, desk.Services.Actions.Execute(new ShowAction(ShowActionKind.AppLinkClose, link.Lid), ActionOrigin.Desk).Status);

        // A wrong proof is counted on the list this minute, with its words; nothing is a row of its own.
        Dialled(desk, DialDesk(desk, tls: false, files.Write("wrong", NewKey()), "key"));
        PumpWhile(() => desk.Control.LinkRefusals.Count == 0, 5000);
        var refusal = Assert.Single(desk.Control.LinkRefusals);
        Assert.Equal(("refused", "main:" + StagewatchId, 1), (refusal.Class, refusal.Client, refusal.Count));
        vm.AppLinks.Look();
        Assert.Contains(PeerLink.ProofWords, vm.AppLinks.RefusalWords, StringComparison.Ordinal);

        // Past peer.forget.ms the ended link leaves the list and STATE; the deck is still the one deck.
        desk.Control.AcceptorNow = () => DateTime.UtcNow.Add(AcceptorList.ForgetAfter).AddSeconds(1);
        Assert.Empty(desk.Control.AppLinkList);
        Assert.DoesNotContain(DeckRows(desk), r => r.GetProperty("level").GetInt32() == 1);
        Assert.Single(desk.Control.Decks);

        // No key, no K and no proof in the list's readers.
        foreach (var reader in new[] { new CommandRouter(desk.Services).StateJson(), string.Join("\n", facts), vm.AppLinks.RefusalWords, ended.Words })
            Assert.DoesNotContain(key, reader, StringComparison.Ordinal);
    }
}
