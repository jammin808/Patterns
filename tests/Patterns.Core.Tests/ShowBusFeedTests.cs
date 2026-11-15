using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The Eye design's EYE.3: the show network's feed. One composition for every reader — the hello table,
/// the Stagewatch link, the node devices, the links this desk's wire accepted by LINK and the twin's nodes — each folded by evidence of
/// identity alone: an accepted link is the app's that says the id LINK proved, whatever its kind; a link proved to a hello of any kind
/// is that app; the apps' revision moves only when what they say moves; and the Eye's desk says a Super Check row without the reading
/// that runs on.
/// </summary>
public class ShowBusFeedTests
{
    private const string PlayMap = "0a1b2c3d";
    private const string Messenger = "77aa77aa";
    private static readonly DateTime Now = new(2026, 11, 15, 16, 0, 0, DateTimeKind.Utc);

    private static ShowBusPeer Heard(string kind, string id, string address, DateTime heardUtc)
        => new(kind, id, id, "host-" + id, address, new ShowBusPorts { Http = 9924, Wire = 9941 }, Array.Empty<string>(), 3, 0, heardUtc) { Addresses = new[] { address } };

    private static AppLinkRow Accepted(string client, string name, bool tls = true, bool local = false, string lid = "L1")
        => new(lid, client, name, "10.0.0.7", local, tls, "key", "e63c317763b1454f", "drive", Now.AddMinutes(-5));

    private static LinkRecord Record(string peerId, int level, long heardMs = 0, string t = "main")
        => new(t, peerId, "Hall A", "ws", level, "none", "", level >= 1 ? "mutual" : "legacy", "", "post", "live", "", "live",
            level >= 1 ? Array.Empty<string>() : new[] { "legacy" }, "green", LinkRecord.MarkOf(CheckLight.Green), "Hall A · live", 0, heardMs, 0, null,
            new LinkRecordClock(null, null, null, 0, false), new LinkCounts(0, 0, 0, 0, 0, 0), null);

    private static ShowBusFeed Feed(IEnumerable<ShowBusPeer> peers, IEnumerable<AppLinkRow> accepted, StagewatchSight? sight = null)
        => new(peers.ToList(), sight ?? StagewatchSight.None) { Accepted = accepted.ToList() };

    [Fact]
    public void AnAcceptedLinkFoldsIntoTheHeardAppOfAnyKindAndSaysItIsThereWhileItsHelloIsNotHeard()
    {
        var gone = Heard("playmap", PlayMap, "10.0.0.7", Now.AddMinutes(-2));
        Assert.Equal(ShowBusState.Gone, gone.StateAt(Now));
        var apps = ShowBusView.Compose(Feed(new[] { gone }, new[] { Accepted("playmap:" + PlayMap, "Stage left") }), Now);

        // One app for the id: the hello's and the link's, never two cards.
        var app = Assert.Single(apps);
        Assert.Equal((PlayMap, "PlayMap", "gone"), (app.Id, app.KindWords, app.Heard));
        Assert.Single(app.Accepted);
        // The link LINK verified says the app is there (precedence over its hello), green, its hello's silence an i note beside it.
        Assert.Equal(ShowBusState.Live, app.State);
        Assert.True(ShowBusView.HoldsLink(app));
        Assert.False(app.Linked);                                                              // the Stagewatch link's flag stays its own
        var lit = ShowBusView.Light(app);
        Assert.Equal(CheckLight.Green, lit.Light);
        Assert.Contains(lit.Notes, n => n.StartsWith("i its hello is not heard", StringComparison.Ordinal));
        Assert.Equal(ShowBusView.AcceptedWord, lit.StateWord());
        // Every reader says it: the context's peers row (its carrier and what proved it), the Nodes card, the brief, STATE's row.
        var peer = Assert.Single(ShowBusView.Peers(ShowBusView.Published(apps)));
        Assert.Equal(("line", "key", "green"), (peer.Link, peer.Verified, peer.Light));
        var card = ShowAppCard.Of(lit);
        Assert.Contains("linked to this desk by LINK · key e63c317763b1454f · scope drive", card.Line, StringComparison.Ordinal);
        Assert.Contains(ShowBusView.BriefLines(apps), l => l.Contains("linked to this desk by LINK", StringComparison.Ordinal));
        Assert.Equal("green", Assert.Single(ShowBusView.StateRows(apps)).Light);

        // Once ended, the row is kept grey on the app and says so — the app no longer held, its hello's age what is left.
        var ended = Accepted("playmap:" + PlayMap, "Stage left") with { EndedUtc = Now, EndWords = "closed by the operator" };
        var after = Assert.Single(ShowBusView.Compose(Feed(new[] { gone }, new[] { ended }), Now));
        Assert.Equal(ShowBusState.Gone, after.State);
        Assert.False(ShowBusView.HoldsLink(after));
        Assert.Equal(CheckLight.Grey, ShowBusView.Light(after).Light);                         // not needed: gone is grey, never red
        Assert.Contains("its link to this desk by LINK ended — closed by the operator", ShowAppCard.Of(after).Line, StringComparison.Ordinal);
    }

    [Fact]
    public void AnIdNoHelloNamesIsAnAppOfItsOwnWhileItsLinkIsOpenAndAPlainOneIsAmberNeverRed()
    {
        var apps = ShowBusView.Compose(Feed(Array.Empty<ShowBusPeer>(), new[]
        {
            Accepted("messenger:" + Messenger, "Messages", tls: false),                         // plain from another machine
            Accepted("main:3f9c2a1b7d0e", "Hall A", lid: "L2") with { EndedUtc = Now },         // ended, and no source names it
        }), Now);

        var app = Assert.Single(apps);
        Assert.Equal((Messenger, "AGMessage", "Messages", ""), (app.Id, app.KindWords, app.Name, app.Heard));
        Assert.Equal(ShowBusState.Live, app.State);
        Assert.Equal(CheckLight.Amber, ShowBusView.Light(app).Light);
        Assert.Contains("plain", ShowAppCard.Of(app).Line, StringComparison.Ordinal);

        // The Eye draws it once: its card from the apps, the acceptor's edge to the desk beside it, both of the link.
        var graph = EyeGraph.Build(new EyeFacts { ShowApps = apps, AppLinks = apps[0].Accepted });
        var node = Assert.Single(graph.Nodes, n => n.Kind == EyeKind.ShowApp);
        Assert.Equal(("app:" + Messenger, EyeOrigin.Link, CheckLight.Amber), (node.Id, node.Origin, node.Light));
        Assert.StartsWith("AGMessage — linked to this desk's wire by LINK, not heard on the show network", node.Words[0], StringComparison.Ordinal);
        Assert.Equal("L1", node.Route!.Item);
        Assert.Single(graph.Edges, e => e.From == node.Id && e.To == EyeGraph.DeskId);
    }

    [Fact]
    public void ALinkLinkProvedIsTheAppOfAnyKindItsHelloSaysAndAnAddressAloneLinksOnlyAStagewatch()
    {
        var playmap = Heard("playmap", PlayMap, "10.0.0.7", Now);
        var sight = new StagewatchSight("Stage left", "10.0.0.7:9924", ShowBusState.Live, null, 0, null, "");
        // By its address alone a PlayMap is not the link's: the doors the link dials are a Stagewatch's.
        Assert.Null(EyeNetwork.LinkedId(new[] { playmap }, sight));
        Assert.DoesNotContain(ShowBusView.Compose(Feed(new[] { playmap }, Array.Empty<AppLinkRow>(), sight), Now), a => a.Id == PlayMap && a.Linked);
        // LINK proved the t:id: the hello that says that id under that kind is the link's, a PlayMap as well as a Stagewatch.
        var proved = sight with { Link = Record(PlayMap, 1, t: "playmap") };
        Assert.Equal(PlayMap, EyeNetwork.LinkedId(new[] { playmap }, proved));
        var app = Assert.Single(ShowBusView.Compose(Feed(new[] { playmap }, Array.Empty<AppLinkRow>(), proved), Now));
        Assert.True(app.Linked && ShowBusView.LinkedApp(app));
        Assert.Equal("PlayMap", app.KindWords);
        // A hello saying the proved id under another kind is another claim: never folded onto the link.
        Assert.Null(EyeNetwork.LinkedId(new[] { playmap }, sight with { Link = Record(PlayMap, 1) }));
        // This desk's own nodes are linked by the twin, never the Stagewatch link's app.
        Assert.False(ShowBusView.LinkedApp(new EyeShowApp("node:00112233445566778899aabbccddeeff", "Patterns node", "Timer", ShowBusState.Live, true, "")));
    }

    [Fact]
    public void TheAppsRevisionMovesOnlyWhenWhatTheAppsSayMovesAndTheEyesHashReadsIt()
    {
        var revision = new ShowBusAppsRevision();
        EyeShowApp App(long contextRev, long heardMs, string name = "Stage left") => new(PlayMap, "PlayMap", name, ShowBusState.Live, false, "")
        {
            ContextRev = contextRev,
            Link = Record(PlayMap, 1, heardMs),
            DeviceId = "dev1",
        };
        var (first, rev) = revision.Take(new[] { App(1, 100) });
        Assert.Equal(1, rev);
        // A record heard again and a context read again under its revision: nothing the apps say moved, so the revision stands.
        var (again, same) = revision.Take(new[] { App(1, 900) });
        Assert.Equal(1, same);
        Assert.NotSame(first, again);                                                          // the composition comes back as it came: its record read live
        Assert.Equal(900, again[0].Link!.HeardMs);
        // A new context revision, a new name: one move each.
        Assert.Equal(2, revision.Take(new[] { App(2, 900) }).Rev);
        Assert.Equal(3, revision.Take(new[] { App(2, 900, "Stage right") }).Rev);
        Assert.Equal(3, revision.Rev);

        // The Eye's hash reads the revision in the apps' place; apps handed in with none are read themselves.
        var key = new EyeFacts { ShowApps = new[] { App(1, 0) }, AppsRev = 7 }.Key();
        Assert.Equal(key, new EyeFacts { ShowApps = new[] { App(5, 0, "moved") }, AppsRev = 7 }.Key());
        Assert.NotEqual(key, new EyeFacts { ShowApps = new[] { App(1, 0) }, AppsRev = 8 }.Key());
        Assert.NotEqual(new EyeFacts { ShowApps = new[] { App(1, 0) } }.Key(), new EyeFacts { ShowApps = new[] { App(5, 0) } }.Key());
    }

    [Fact]
    public void TheEyesDeskSaysASuperCheckRowByItsItemMarkAndNoteNeverItsRunningReading()
    {
        var tick = new CheckRow("Health", "Desk tick", CheckLight.Amber, "96.6 ms · worst 221.5 ms", "the desk stutters");
        var later = tick with { Value = "51.6 ms · worst 221.5 ms" };
        Assert.Equal("Desk tick: △ the desk stutters", EyeFacts.AttentionWords(tick));
        Assert.Equal(EyeFacts.AttentionWords(tick), EyeFacts.AttentionWords(later));
        // A row with no note: its item and its mark, the reading left to the Super Check — a running reading moves no picture.
        var fence = new CheckRow("Render", "Frame fence", CheckLight.Red, "3 hung");
        Assert.Equal("Frame fence: ✕", EyeFacts.AttentionWords(fence));
        Assert.Equal(EyeFacts.AttentionWords(fence), EyeFacts.AttentionWords(fence with { Value = "4 hung" }));
    }

    [Fact]
    public void AHeardAppWithAnOpenAcceptedLinkHasOneEdgeToTheDeskTheLinksAndIsHeardOnlyWhileItsHelloIs()
    {
        foreach (var at in new[] { Now, Now.AddMinutes(-2) })
        {
            var hello = Heard("main", "3f9c2a1b7d0e", "10.0.0.7", at);
            var row = Accepted("main:3f9c2a1b7d0e", "Hall A");
            var apps = ShowBusView.Compose(Feed(new[] { hello }, new[] { row }), Now);
            var app = Assert.Single(apps);
            Assert.Equal(ShowBusState.Live, app.State);
            // Heard means its hello is heard: STATE's bus.heard counts it only while the hello is live, never by the link alone.
            Assert.Equal(at == Now, ShowBusView.HeardLive(app));

            var graph = EyeGraph.Build(new EyeFacts { ShowApps = apps, AppLinks = new[] { row } });
            var edge = Assert.Single(graph.Edges, e => e.From == "app:3f9c2a1b7d0e" && e.To == EyeGraph.DeskId);
            Assert.Equal(EyeOrigin.Link, edge.Origin);
            Assert.StartsWith("✓ linked to this desk by LINK", edge.Words, StringComparison.Ordinal);
            // The hello's age is the card's i note, never a green "heard" edge.
            var card = Assert.Single(graph.Nodes, n => n.Id == "app:3f9c2a1b7d0e");
            Assert.Equal(CheckLight.Green, card.Light);
            Assert.Equal(at != Now, card.Words.Any(w => w.StartsWith("i its hello is not heard", StringComparison.Ordinal)));
        }
        // An app known only by its accepted link is not heard.
        Assert.False(ShowBusView.HeardLive(Assert.Single(ShowBusView.Compose(Feed(Array.Empty<ShowBusPeer>(), new[] { Accepted("main:3f9c2a1b7d0e", "Hall A") }), Now))));
    }

    [Fact]
    public void APlainAcceptedLinkSaysWhyOnTheCardItLeadsAndBesideALinkThisDeskDialsIsOnlyAnINote()
    {
        // The accepted link is what says the app is there: amber over its green, the reason in the headline every reader says.
        var lead = Assert.Single(ShowBusView.Compose(Feed(new[] { Heard("main", "3f9c2a1b7d0e", "10.0.0.7", Now) }, new[] { Accepted("main:3f9c2a1b7d0e", "Hall A", tls: false) }), Now));
        var lit = ShowBusView.Light(lead);
        Assert.Equal(CheckLight.Amber, lit.Light);
        Assert.Equal(ShowBusView.AcceptedWord + " — proved, not private — from another machine with no TLS", lit.Headline);
        Assert.Equal(lit.Headline, Assert.Single(ShowBusView.StateRows(new[] { lead })).Headline);

        // The Stagewatch link this desk dials, live and green, says the app is there: the plain link the Stagewatch dialled into this
        // desk is its own edge's amber and an i note — never the card's, never bus.stagewatch's.
        var sight = new StagewatchSight("Hall A", "10.0.0.7:9924", ShowBusState.Live, null, 0, null, "") { Link = Record("3f9c2a1b7d0e", 1) };
        var linked = Assert.Single(ShowBusView.Compose(Feed(new[] { Heard("main", "3f9c2a1b7d0e", "10.0.0.7", Now) }, new[] { Accepted("main:3f9c2a1b7d0e", "Hall A", tls: false) }, sight), Now));
        Assert.True(linked.Linked);
        var both = ShowBusView.Light(linked);
        Assert.Equal(CheckLight.Green, both.Light);
        Assert.Contains(both.Notes, n => n.StartsWith("i its link to this desk by LINK is △ amber — proved, not private", StringComparison.Ordinal));
        Assert.Equal("green", ShowBusView.StagewatchLight(linked, false));
    }

    [Fact]
    public void ARowLinkProvedUnderAnotherKindIsAnINoteNeverTheAppsLink()
    {
        var gone = Heard("playmap", PlayMap, "10.0.0.7", Now.AddMinutes(-2));
        var app = Assert.Single(ShowBusView.Compose(Feed(new[] { gone }, new[] { Accepted("main:" + PlayMap, "Hall A") }), Now));
        Assert.Equal(("PlayMap", ShowBusState.Gone), (app.KindWords, app.State));                 // the hello's kind and state stand
        Assert.False(ShowBusView.Accepts(app));
        Assert.False(ShowBusView.HoldsLink(app));
        Assert.Single(app.Accepted);
        Assert.Contains(ShowBusView.Light(app).Notes, n => n == ShowBusView.KindNote(app) && n.StartsWith("i LINK proved Stagewatch " + PlayMap, StringComparison.Ordinal));
    }

    [Fact]
    public void AnAcceptedLinkPastTheAppsTheEyeDrawsIsCountedOnTheMoreCardAndDrawnNowhereElse()
    {
        var rows = Enumerable.Range(0, EyeLayout.MaxApps + 1)
            .Select(i => Accepted("main:" + i.ToString("x8", System.Globalization.CultureInfo.InvariantCulture), "Hall " + i.ToString("d3", System.Globalization.CultureInfo.InvariantCulture), lid: "L" + i.ToString(System.Globalization.CultureInfo.InvariantCulture)))
            .ToList();
        var apps = ShowBusView.Compose(Feed(Array.Empty<ShowBusPeer>(), rows), Now);
        Assert.Equal(EyeLayout.MaxApps + 1, apps.Count);
        var graph = EyeGraph.Build(new EyeFacts { ShowApps = apps, AppLinks = rows });
        Assert.Equal(EyeLayout.MaxApps, graph.Nodes.Count(n => n.Kind == EyeKind.ShowApp && n.Id != EyeGraph.MoreId));
        Assert.Equal(EyeLayout.MaxApps, graph.Edges.Count(e => e.To == EyeGraph.DeskId && e.Origin == EyeOrigin.Link));
        Assert.Equal("+1 more app heard", Assert.Single(graph.Nodes, n => n.Id == EyeGraph.MoreId).Label);
    }
}
