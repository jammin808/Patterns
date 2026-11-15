using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 99 (the Eye design's EYE.2; Show Bus 1.3 §6 and §13.16): the network part of the Eye. One card per app id, folded only on
/// evidence of identity — a LINK verdict's id, a level-0 device's address and context id agreeing, a Patterns process's instance —
/// and never by an address alone; a deck that names an app stays a deck. A link item with a row to a node is that link's edge, its
/// other rows re-anchored; a cross-node end is placed on what it names or counted; a claim is worded as one; a quiet link's counting
/// things are unknown; and this desk's export names its own things by short ids and carries no secret.
/// </summary>
public class EyeNetworkTests
{
    private const string Desk = "9f8e7d6c";
    private const string Hall = "3f9c2a1b7d0e";
    private const string PlayMap = "0a1b2c3d";
    private const string Messenger = "77aa77aa";

    private static LinkRecord Record(string peerId, int level, string state = "live", string light = "green", string words = "Stagewatch Hall A · live")
        => new("main", peerId, "Hall A", "ws", level, "none", "", level >= 1 ? "mutual" : "legacy", "", "post", state, "", "live",
            level >= 1 ? Array.Empty<string>() : new[] { "legacy" }, light, LinkRecord.MarkOf(ShowContext.LightOf(light)), words, 0, 0, 0, null,
            new LinkRecordClock(null, null, null, 0, false), new LinkCounts(0, 0, 0, 0, 0, 0), null);

    private static ShowBusPeer Heard(string kind, string id, string address, ShowBusPorts? ports = null)
        => new(kind, id, id, "host-" + id, address, ports ?? new ShowBusPorts { Http = 9924 }, Array.Empty<string>(), 3, 0, DateTime.UtcNow) { Addresses = new[] { address } };

    private static ShowContext Context(string kind, string id, string name, IEnumerable<ShowContextItem> items, IEnumerable<ShowContextLink>? links = null, IEnumerable<ShowContextPeer>? peers = null)
        => new()
        {
            Node = new ShowContextNode { Kind = kind, Id = id, Name = name },
            Light = "green",
            Headline = name + " — fine",
            Items = items.ToList(),
            Links = (links ?? Array.Empty<ShowContextLink>()).ToList(),
            Peers = (peers ?? Array.Empty<ShowContextPeer>()).ToList(),
        };

    private static ShowContextItem Item(string id, string kind, string light = "green", string state = "") => new() { Id = id, Kind = kind, Label = id, Light = light, State = state };

    private static ShowContextLink Row(string from, string to, string kind, string light = "green") => new() { From = from, To = to, Kind = kind, Light = light };

    // ---- the fold ----------------------------------------------------------------------------------------------------------

    [Fact]
    public void ALinkFoldsByTheIdItsLinkProvedAndAnAddressAloneNeverFolds()
    {
        // Two Stagewatches answer at one address (an id renewed, a datagram's source forged): by the address alone neither is the link's.
        var peers = new[] { Heard("main", "aaaaaa11", "10.0.0.5"), Heard("main", "bbbbbb22", "10.0.0.5") };
        var sight = new StagewatchSight("Hall A", "10.0.0.5:9924", ShowBusState.Live, null, 0, null, "");
        Assert.Null(EyeNetwork.LinkedId(peers, sight));
        // A level-0 record names an id, but nothing proved who answered: still neither.
        Assert.Null(EyeNetwork.LinkedId(peers, sight with { Link = Record("bbbbbb22", 0) }));
        // A LINK verdict proves the id: that hello is the link's, whichever address it came from.
        Assert.Equal("bbbbbb22", EyeNetwork.LinkedId(peers, sight with { Link = Record("bbbbbb22", 1) }));
        Assert.Equal("bbbbbb22", EyeNetwork.LinkedId(new[] { Heard("main", "bbbbbb22", "10.0.0.9") }, sight with { Link = Record("bbbbbb22", 1) }));
        Assert.True(EyeNetwork.Proved(Record("bbbbbb22", 1)));
        Assert.False(EyeNetwork.Proved(Record("bbbbbb22", 0)));
        Assert.False(EyeNetwork.Proved(Record("", 1)));

        // Through the view every reader takes: one app, linked, its card the proved id's.
        var apps = ShowBusView.Apps(new[] { Heard("main", "bbbbbb22", "10.0.0.9") }, DateTime.UtcNow, sight with { Link = Record("bbbbbb22", 1) });
        var linked = Assert.Single(apps);
        Assert.Equal(("bbbbbb22", true), (linked.Id, linked.Linked));
    }

    [Fact]
    public void ALevelZeroDeviceFoldsOnlyWhenItsAddressAndItsContextIdAgree()
    {
        static ShowBusNodeSight Device(string contextId, string host = "10.0.0.9")
        {
            var kept = KeptContext.Next(null, Context("playmap", contextId, "Stage left", new[] { Item("out1", "output") }));
            return new ShowBusNodeSight("dev1", "PlayMap Stage left", host, 9941, true, true, true, false, false, kept, Record(contextId, 0), "", 0, 0, 0, "");
        }
        var heard = new[] { Heard("playmap", PlayMap, "10.0.0.9", new ShowBusPorts { Wire = 9941 }) };
        var apps = ShowBusView.Apps(heard, DateTime.UtcNow, StagewatchSight.None, Array.Empty<ShowBusNodeSight>());
        Assert.Equal(0, EyeNetwork.DeviceFolds(heard, apps, Device(PlayMap)));
        Assert.Equal(-1, EyeNetwork.DeviceFolds(heard, apps, Device("ffffffff")));            // the context claims another id
        Assert.Equal(-1, EyeNetwork.DeviceFolds(heard, apps, Device(PlayMap, "10.0.0.77")));   // another address
        // Folded, one app; disagreeing, two — the device an app of its own and the heard one heard.
        Assert.Single(ShowBusView.Apps(heard, DateTime.UtcNow, StagewatchSight.None, new[] { Device(PlayMap) }));
        Assert.Equal(2, ShowBusView.Apps(heard, DateTime.UtcNow, StagewatchSight.None, new[] { Device("ffffffff") }).Count);
    }

    [Fact]
    public void ALevelZeroDeckNeverFoldsAndItsWordsSayBothObservationsApart()
    {
        var hall = new EyeShowApp(Hall, "Stagewatch", "Hall A", ShowBusState.Live, false, "") { Addresses = new[] { "10.0.0.5" } };
        var deck = new EyeDeck("Stagewatch", "", "10.0.0.5:51234", true);
        var g = EyeGraph.Build(new EyeFacts { ShowApps = new[] { hall }, Decks = new[] { deck } });
        var node = Assert.Single(g.Nodes, n => n.Kind == EyeKind.Deck);
        Assert.Contains("says it is Stagewatch — from 10.0.0.5, where Stagewatch Hall A's hello comes from; not proved (PeerLink not run)", node.Words);
        Assert.Single(g.Nodes, n => n.Id == "app:" + Hall);                                     // the app's card stands apart: two things, one edge each
        Assert.Single(g.Edges, e => e.From == node.Id && e.To == EyeGraph.DeskId);
        // A deck from elsewhere says only what it said; a deck that names no app says nothing of the kind.
        Assert.Equal("says it is Stagewatch — not proved (PeerLink not run)", EyeNetwork.DeckWords(deck with { Address = "10.0.0.8:4000" }, new[] { hall }));
        Assert.Equal("", EyeNetwork.DeckWords(new EyeDeck("Companion", "3.20.0", "10.0.0.5", true), new[] { hall }));
    }

    [Fact]
    public void APatternsNodesTwinContextBeaconAndHelloAreOneCard()
    {
        var kept = KeptContext.Next(null, Context("patterns", "1a2b3c4d", "Stage Left", new[] { Item("stage", "stage"), Item("desk", "twin") }));
        var twin = Assert.Single(ShowBusView.Nodes(new[] { ("abcdef0123456789", "STAGE-PC", kept, DateTime.UtcNow) }, DateTime.UtcNow));
        Assert.Equal("node:abcdef0123456789", twin.Id);
        var g = EyeGraph.Build(new EyeFacts
        {
            ShowBusId = Desk,
            ShowApps = new[] { twin, new EyeShowApp(Hall, "Stagewatch", "Hall A", ShowBusState.Live, false, "")
            {
                Context = Context("main", Hall, "Hall A", new[] { Item("timer", "timer") }, new[] { Row("timer", "@1a2b3c4d/stage", "feeds") }),
            } },
            Nodes = new[] { new EyeNodeHeard("abcdef0123456789", "timer", "STAGE-PC", true, true, "linked") { ShowBusId = "1a2b3c4d" } },
        });
        var card = Assert.Single(g.Nodes, n => n.Id == "node:abcdef0123456789");
        Assert.Equal((EyeKind.ShowApp, EyePlane.Network), (card.Kind, card.Band));
        Assert.Contains(card.Words, w => w.Contains("heard on the beacon as timer STAGE-PC · its hello says id 1a2b3c4d", StringComparison.Ordinal));
        Assert.Single(g.Edges, e => e.From == card.Id && e.To == EyeGraph.DeskId);           // one card, one edge — the beacon's is folded in
        Assert.NotNull(g.Find("node:abcdef0123456789:stage"));
        // Another app's end @<the node's id>/stage lands on the node's own thing.
        var claim = Assert.Single(g.Edges, e => e.From == $"app:{Hall}:timer" && e.To == "node:abcdef0123456789:stage");
        Assert.Equal((EyeOrigin.Told, "Stagewatch Hall A"), (claim.Origin, claim.Claim));
        // The layout clusters its things under the one card.
        Assert.Equal("node:abcdef0123456789", EyeNetwork.CardOf(card.App));

        // A hello from its instance that names another id is said on the card, never taken; and an app's context that says another
        // node's id of itself is a claim, never a name the Eye places that node by.
        var odd = EyeGraph.Build(new EyeFacts
        {
            ShowBusId = Desk,
            ShowApps = new[]
            {
                twin,
                new EyeShowApp(Hall, "Stagewatch", "Hall A", ShowBusState.Live, false, "")
                {
                    Context = Context("main", PlayMap, "Hall A", new[] { Item("timer", "timer") }, new[] { Row("timer", "@5e6f7a8b/stage", "feeds") }),
                },
                new EyeShowApp(Messenger, "AGMessage", "Crew", ShowBusState.Live, false, "")
                {
                    Context = Context("messenger", Messenger, "Crew", new[] { Item("hall:a", "hall") }, new[] { Row("hall:a", "@" + PlayMap, "feeds") }),
                },
            },
            Nodes = new[] { new EyeNodeHeard("abcdef0123456789", "timer", "STAGE-PC", true, true, "linked") { ShowBusId = "5e6f7a8b" } },
        });
        Assert.Contains(odd.Find("node:abcdef0123456789")!.Words, w => w.StartsWith("i a hello from its instance says id 5e6f7a8b, its context over the twin link says 1a2b3c4d", StringComparison.Ordinal));
        Assert.DoesNotContain(odd.Edges, e => e.Claim.Length > 0 && (e.To.StartsWith("node:", StringComparison.Ordinal) || e.To.StartsWith("app:" + Hall, StringComparison.Ordinal)));
        Assert.Equal(2, odd.Unplaced);
    }

    [Fact]
    public void TheSameIdTwiceIsOneAppTheLinkHolderLeading()
    {
        var heard = new EyeShowApp(Hall, "Stagewatch", "Hall A", ShowBusState.Live, false, "") { Heard = "live", Addresses = new[] { "10.0.0.5" }, Needed = true };
        var linked = new EyeShowApp(Hall, "Stagewatch", "Hall A", ShowBusState.Live, true, "") { Link = Record(Hall, 1) };
        var one = Assert.Single(EyeNetwork.Fold(new[] { heard, linked }));
        Assert.True(one.Linked);
        Assert.Equal(("live", true), (one.Heard, one.Needed));
        Assert.Equal("10.0.0.5", Assert.Single(one.Addresses));
        var g = EyeGraph.Build(new EyeFacts { ShowApps = new[] { heard, linked } });
        Assert.Single(g.Nodes, n => n.Id == "app:" + Hall);
        Assert.Single(g.Edges, e => e.From == "app:" + Hall && e.To == EyeGraph.DeskId && e.Origin == EyeOrigin.Link);
    }

    [Fact]
    public void AConflictIsOneAmberCardAndOnlyAnINoteBesideALinkTheIdProved()
    {
        var conflicted = new EyeShowApp(Hall, "Stagewatch", "Hall A", ShowBusState.Live, true, "") { Conflict = "two machines answer as " + Hall };
        Assert.True(ShowBusView.ConflictAmber(conflicted));
        Assert.Equal(CheckLight.Amber, ShowBusView.CardLight(conflicted));
        var proved = conflicted with { Link = Record(Hall, 1) };
        Assert.False(ShowBusView.ConflictAmber(proved));
        Assert.True(ShowBusView.ConflictNoted(proved));
        var card = Assert.Single(EyeGraph.Build(new EyeFacts { ShowApps = new[] { proved } }).Nodes, n => n.Id == "app:" + Hall);
        Assert.Equal(CheckLight.Green, card.Light);
        Assert.Contains(card.Words, w => w.StartsWith("i two machines answer as", StringComparison.Ordinal));
        // A level-0 record proves nothing: the conflict stays amber.
        Assert.True(ShowBusView.ConflictAmber(conflicted with { Link = Record(Hall, 0) }));
    }

    // ---- a link is an edge; rows re-anchored; ends --------------------------------------------------------------------------

    private static EyeFacts PlayMapRoom(IEnumerable<ShowContextLink> links, IEnumerable<ShowContextItem>? extra = null) => new()
    {
        ShowBusId = Desk,
        Screens = new[] { new EyeScreen { Id = "s2", Number = "2", Label = "LED wall" } },
        ShowApps = new[]
        {
            new EyeShowApp(PlayMap, "PlayMap", "Stage left", ShowBusState.Live, false, "")
            {
                Context = Context("playmap", PlayMap, "Stage left",
                    new[] { Item("link:patterns", "link", "green", "Patterns FOH · scope drive"), Item("air", "air"), Item("out3", "output") }.Concat(extra ?? Array.Empty<ShowContextItem>()),
                    links),
            },
        },
    };

    [Fact]
    public void ALinkItemWithARowToANodeIsAnEdgeAndItsOtherRowsReAnchor()
    {
        var g = EyeGraph.Build(PlayMapRoom(new[] { Row("link:patterns", "@" + Desk, "follows"), Row("link:patterns", "air", "controls") }));
        var card = "app:" + PlayMap;
        Assert.Null(g.Find(card + ":link:patterns"));                                          // a link is never a thing
        var edge = Assert.Single(g.Edges, e => e.From == card && e.To == EyeGraph.DeskId && e.Kind == EyeEdgeKind.Follows);
        Assert.Equal((EyeOrigin.Told, "PlayMap Stage left"), (edge.Origin, edge.Claim));
        Assert.Equal("PlayMap Stage left says: Patterns FOH · scope drive", edge.Words);        // the link item's own words, verbatim
        // PlayMap's link:patterns -controls-> air becomes desk -controls-> PlayMap's air: this desk's blackout controls that air.
        Assert.Single(g.Edges, e => e.From == EyeGraph.DeskId && e.To == card + ":air" && e.Kind == EyeEdgeKind.Controls);
        Assert.Equal(0, g.Unplaced);
    }

    [Fact]
    public void ALinkThisDeskHoldsIsDrawnOnceItsRecordLeading()
    {
        var facts = PlayMapRoom(new[] { Row("link:patterns", "@" + Desk, "follows"), Row("link:patterns", "air", "controls") });
        var driven = facts.ShowApps[0] with { DeviceId = "dev1", Device = "PlayMap Stage left", Link = Record(PlayMap, 0, light: "amber", words: "PlayMap Stage left · live") };
        var card = "app:" + PlayMap;
        bool Between(EyeEdge e, string mine) => (e.From == card && e.To == mine) || (e.From == mine && e.To == card);
        var g = EyeGraph.Build(new EyeFacts { ShowBusId = Desk, ShowApps = new[] { driven } });
        var only = Assert.Single(g.Edges, e => Between(e, EyeGraph.DeskId));
        Assert.Equal((EyeOrigin.Link, CheckLight.Amber), (only.Origin, only.Light));            // the record's light, verbatim, never the told green
        // Precedence: the record sets the light and mark; the far end's own word for the same link follows, an i note where it differs.
        Assert.EndsWith(" · i PlayMap Stage left says: Patterns FOH · scope drive", only.Words, StringComparison.Ordinal);
        var agreeing = EyeGraph.Build(new EyeFacts { ShowBusId = Desk, ShowApps = new[] { driven with { Link = Record(PlayMap, 0, light: "green", words: "PlayMap Stage left · live") } } });
        var same = Assert.Single(agreeing.Edges, e => Between(e, EyeGraph.DeskId));
        Assert.Equal(CheckLight.Green, same.Light);
        Assert.EndsWith(" · PlayMap Stage left says: Patterns FOH · scope drive", same.Words, StringComparison.Ordinal);
        Assert.DoesNotContain(" · i ", same.Words, StringComparison.Ordinal);

        // As the live desk draws it: the node device is a node of this desk's, and the record's edge runs from it. PlayMap's own item for
        // that link is still the same link — one edge between the card and this desk's side, the record's; the item's rows re-anchor there.
        var g2 = EyeGraph.Build(new EyeFacts
        {
            ShowBusId = Desk,
            ShowApps = new[] { driven },
            Devices = new[] { new EyeDevice("dev1", "PlayMap Stage left", "showbus-node", "10.0.0.9:9941", true, true, "linked", CheckLight.Amber, "") },
        });
        Assert.NotNull(g2.Find("device:dev1"));
        var record = Assert.Single(g2.Edges, e => (Between(e, EyeGraph.DeskId) || Between(e, "device:dev1")) && e.Origin is EyeOrigin.Link or EyeOrigin.Told);
        Assert.Equal(("device:dev1", EyeOrigin.Link, CheckLight.Amber, ""), (record.From, record.Origin, record.Light, record.Claim));
        Assert.DoesNotContain(g2.Edges, e => e.Claim.Length > 0 && (Between(e, EyeGraph.DeskId) || Between(e, "device:dev1")));
        Assert.Single(g2.Edges, e => e.From == "device:dev1" && e.To == card + ":air" && e.Kind == EyeEdgeKind.Controls);
    }

    [Fact]
    public void EndsResolveToTheThingThenTheCardElseTheyAreCountedAndBrokenOnesAreRefusedAtParse()
    {
        var own = EyeNetwork.ShortId("screen:s2");
        var g = EyeGraph.Build(PlayMapRoom(new[]
        {
            Row("out3", $"@{Desk}/{own}", "shows"),                                             // this desk's screen
            Row("out3", "@" + Desk, "feeds"),                                                    // this desk
            Row("out3", "@" + Messenger, "feeds"),                                               // an app the Eye does not show: counted
            Row("air", $"@{Desk}/not-a-thing", "feeds"),                                         // an item this desk has not: its card, the desk
        }));
        var card = "app:" + PlayMap;
        Assert.Single(g.Edges, e => e.From == card + ":out3" && e.To == "screen:s2" && e.Kind == EyeEdgeKind.Shows);
        Assert.Single(g.Edges, e => e.From == card + ":out3" && e.To == EyeGraph.DeskId && e.Kind == EyeEdgeKind.Feeds);
        Assert.Single(g.Edges, e => e.From == card + ":air" && e.To == EyeGraph.DeskId);
        Assert.Equal(1, g.Unplaced);

        Assert.True(EyeNetwork.TryEnd("@3f9c2a1b7d0e/window:lobby", out var end));
        Assert.Equal(new EyeEnd("3f9c2a1b7d0e", "window:lobby"), end);
        Assert.True(EyeNetwork.TryEnd("@3f9c2a1b7d0e", out end));
        Assert.Equal("", end.Item);
        foreach (var bad in new[] { "@", "@abc", "@" + new string('a', 41), "@has space", "@3f9c2a1b7d0e/", "@3f9c2a1b7d0e/" + new string('x', 61), "@3f9c2a1b7d0e/a\u0001b", "@" + new string('a', 40) + "/" + new string('b', 80) })
            Assert.False(EyeNetwork.TryEnd(bad, out _), bad);
        var parsed = ShowContext.Parse("{\"v\":1,\"node\":{\"t\":\"playmap\",\"id\":\"0a1b2c3d\",\"name\":\"x\"},\"items\":[{\"id\":\"out3\",\"kind\":\"output\"}],"
            + "\"links\":[{\"from\":\"out3\",\"to\":\"@abc\",\"kind\":\"feeds\"},{\"from\":\"out3\",\"to\":\"@3f9c2a1b7d0e/timer\",\"kind\":\"feeds\"}]}")!;
        Assert.Equal("@3f9c2a1b7d0e/timer", Assert.Single(parsed.Links).To);
        Assert.Equal(1, parsed.LinksRefused);
    }

    [Fact]
    public void TheWindowChainCrossesAppsAsEachAppsClaim()
    {
        // §3.3: Hall A's timer feeds its window lobby; PlayMap says it draws Hall A's window on Out 3, which drives its projector.
        var facts = new EyeFacts
        {
            ShowBusId = Desk,
            ShowApps = new[]
            {
                new EyeShowApp(Hall, "Stagewatch", "Hall A", ShowBusState.Live, false, "")
                {
                    Context = Context("main", Hall, "Hall A", new[] { Item("timer", "timer"), Item("window:lobby", "screen"), Item("window:stage", "screen") },
                        new[] { Row("timer", "window:lobby", "feeds"), Row("timer", "window:stage", "feeds") }),
                },
                new EyeShowApp(PlayMap, "PlayMap", "Stage left", ShowBusState.Live, false, "")
                {
                    Context = Context("playmap", PlayMap, "Stage left", new[] { Item("out3", "output"), Item("pj1", "projector") },
                        new[] { Row($"@{Hall}/window:stage", "out3", "feeds"), Row("out3", "pj1", "drives") }),
                },
            },
        };
        var g = EyeGraph.Build(facts);
        Assert.Single(g.Edges, e => e.From == $"app:{Hall}:timer" && e.To == $"app:{Hall}:window:stage" && e.Origin == EyeOrigin.Told && e.Claim.Length == 0);
        var cross = Assert.Single(g.Edges, e => e.From == $"app:{Hall}:window:stage" && e.To == $"app:{PlayMap}:out3");
        Assert.Equal((EyeOrigin.Told, "PlayMap Stage left", EyeEdgeKind.Feeds), (cross.Origin, cross.Claim, cross.Kind));
        Assert.StartsWith("PlayMap Stage left says:", cross.Words, StringComparison.Ordinal);
        Assert.Single(g.Edges, e => e.From == $"app:{PlayMap}:out3" && e.To == $"app:{PlayMap}:pj1" && e.Kind == EyeEdgeKind.Drives);
        // An end on a thing the other app does not draw falls back to its card, never a node of its own.
        var g2 = EyeGraph.Build(new EyeFacts
        {
            ShowApps = new[] { facts.ShowApps[0] with { Context = Context("main", Hall, "Hall A", new[] { Item("timer", "timer") }) }, facts.ShowApps[1] },
        });
        var fallen = Assert.Single(g2.Edges, e => e.From == "app:" + Hall && e.To == $"app:{PlayMap}:out3" && e.Claim == "PlayMap Stage left");
        Assert.Equal("PlayMap Stage left says: feeds — Stagewatch Hall A's window:stage", fallen.Words);   // the item it named, kept in the words
    }

    [Fact]
    public void APeersRowIsAGreyMentionOrARelayedEdgeNeverTheRelayingAppsColour()
    {
        var facts = new EyeFacts
        {
            ShowBusId = Desk,
            ShowApps = new[]
            {
                new EyeShowApp(Hall, "Stagewatch", "Hall A", ShowBusState.Live, false, "")
                {
                    Context = Context("main", Hall, "Hall A", new[] { Item("timer", "timer", "red") }, null, new[]
                    {
                        new ShowContextPeer { Kind = "messenger", Id = Messenger, Name = "AGMessage", Light = "red", Link = "ws", Verified = "key" },
                        new ShowContextPeer { Kind = "playmap", Id = PlayMap, Name = "Stage left", Light = "red" },
                        new ShowContextPeer { Kind = "patterns", Id = Desk, Name = "FOH", Light = "green", Link = "line", Verified = "key" },
                    }),
                },
                new EyeShowApp(PlayMap, "PlayMap", "Stage left", ShowBusState.Live, false, ""),
            },
        };
        var g = EyeGraph.Build(facts);
        var mention = Assert.Single(g.Nodes, n => n.Id == "app:" + Messenger);
        Assert.Equal((CheckLight.Grey, EyeOrigin.Relayed, false), (mention.Light, mention.Origin, mention.Counted));
        Assert.Equal("AGMessage AGMessage — heard by Stagewatch Hall A", mention.Label);
        var proved = Assert.Single(g.Edges, e => e.From == mention.Id && e.To == "app:" + Hall);
        Assert.Equal((EyeEdgeKind.Follows, CheckLight.Red, EyeOrigin.Relayed, "Stagewatch Hall A"), (proved.Kind, proved.Light, proved.Origin, proved.Claim));
        var heard = Assert.Single(g.Edges, e => e.From == "app:" + PlayMap && e.To == "app:" + Hall);
        Assert.Equal((EyeEdgeKind.Hears, CheckLight.Grey), (heard.Kind, heard.Light));          // a row no link proves: grey
        Assert.DoesNotContain(g.Edges, e => e.Origin == EyeOrigin.Relayed && (e.From == EyeGraph.DeskId || e.To == EyeGraph.DeskId));   // this desk's own record says its links
        Assert.Equal(CheckLight.Grey, g.Find("app:" + PlayMap)!.Light);                         // its card keeps its own light, not the red it was said in
    }

    // ---- counting things, a link down ---------------------------------------------------------------------------------------

    [Fact]
    public void AQuietLinksCountingThingsAreUnknownAndADownLinkDrawsNoThings()
    {
        var context = Context("main", Hall, "Hall A", new[] { Item("timer", "timer", "green", "12:30 left"), Item("cue:lx", "department", "green", "standing by") });
        var quiet = new EyeShowApp(Hall, "Stagewatch", "Hall A", ShowBusState.Stale, true, "") { Context = context, Link = Record(Hall, 1, "stale", "amber") };
        var g = EyeGraph.Build(new EyeFacts { ShowApps = new[] { quiet } });
        var timer = g.Find($"app:{Hall}:timer")!;
        Assert.Equal((CheckLight.Amber, "? — its link is quiet — no count is current"), (timer.Light, timer.Sub));
        var cue = g.Find($"app:{Hall}:cue:lx")!;
        Assert.Equal((CheckLight.Green, "standing by"), (cue.Light, cue.Sub));
        Assert.Contains("as it last said — its link is quiet", cue.Words);
        // The show needs it: its counts are red.
        Assert.Equal(CheckLight.Red, EyeGraph.Build(new EyeFacts { ShowApps = new[] { quiet with { Needed = true } } }).Find($"app:{Hall}:timer")!.Light);
        // Down, held or refused: nothing it said is current, so no thing is drawn — and the 'left' count says so.
        var down = EyeGraph.Build(new EyeFacts { ShowApps = new[] { quiet with { State = ShowBusState.Live, Link = Record(Hall, 1, "down", "red") } } });
        Assert.DoesNotContain(down.Nodes, n => n.Kind == EyeKind.ShowAppItem);
        Assert.Equal(2, down.NamedBy("app:" + Hall));
        Assert.Equal(new ShowContextItem { Id = "x", Kind = "media", Light = "green" }, EyeNetwork.Counting(new ShowContextItem { Id = "x", Kind = "media", Light = "green" }, "why", true));
    }

    // ---- the relay; the export ----------------------------------------------------------------------------------------------

    [Fact]
    public void ANodesRelayOfItsDesksStagewatchIsALinkItemThatLendsTheNodeNoLight()
    {
        var gone = new StagewatchSight("Hall A", "via the desk", ShowBusState.Gone, null, 0, null, "");
        var c = NodeContextBuilder.Build("1a2b3c4d", "Stage Left", DateTime.UtcNow, new NodeContextFacts { Role = "caller", DeskName = "FOH", DeskLight = CheckLight.Green, Linked = true, Stagewatch = gone, StagewatchViaDesk = true }, Array.Empty<ShowLogEntry>());
        var relay = Assert.Single(c.Items, i => i.Kind == "link");
        Assert.Equal((NodeContextBuilder.RelayItem, "grey"), (relay.Id, relay.Light));
        Assert.EndsWith(" · " + NodeContextBuilder.RelayWords, relay.State, StringComparison.Ordinal);
        Assert.NotEqual("red", c.Light);                                                         // a Stagewatch gone from the desk's relay is no fault of the node's
        // Its own link, by contrast, is its own: a gone Stagewatch it follows itself is red.
        var own = NodeContextBuilder.Build("1a2b3c4d", "Stage Left", DateTime.UtcNow, new NodeContextFacts { Role = "caller", Stagewatch = gone with { Address = "10.0.0.5:9924" } }, Array.Empty<ShowLogEntry>());
        Assert.Contains(own.Items, i => i.Id == "stagewatch" && i.Light == "red");
    }

    [Fact]
    public void TheExportNamesItsOwnThingsByShortIdsWithTheirLinksAndNeverABorrowedThing()
    {
        var room = PlayMapRoom(new[] { Row("link:patterns", "@" + Desk, "follows") });
        // This desk's own things carry the canary words in their own labels — a path, a window's address, a Stagewatch key, an app key.
        var g = EyeGraph.Build(new EyeFacts
        {
            ShowBusId = Desk,
            Screens = room.Screens,
            Sources = new[]
            {
                new EyeSource("vid:C:\\shows\\gala\\walk-in.mp4", "Walk-in C:\\shows\\gala\\walk-in-canary.mp4", "video", true, "", CheckLight.Green),
                new EyeSource("web:https://10.0.0.5:9924/w/abcdef0123456789", "Lobby http://10.0.0.5:9924/w/canary0123456789", "web", true, "", CheckLight.Green),
                new EyeSource("ndi:keyed", "Keyed swk_canary0123456789 ptk_canary0123456789", "ndi", true, "", CheckLight.Green),
            },
            ShowApps = room.ShowApps,
        });
        var accepted = new[] { new AppLinkRow("l1", "main:" + Hall, "Hall A", "10.0.0.5", false, true, "key", "52f6aa", "drive", DateTime.UtcNow) { Drove = true } };
        var peers = new[] { new ShowContextPeer { Kind = "main", Id = Hall, Name = "Hall A", Light = "green", Headline = "keyed by swk_canary0123456789 from C:\\keys\\canary.key" } };
        var c = ShowContextBuilder.FromEye(g, Desk, "FOH", DateTime.UtcNow, Array.Empty<ShowLogEntry>(), peers, null, accepted);
        Assert.All(c.Items, i => Assert.True(i.Id.Length <= EyeNetwork.ShortChars && i.Id.All(ch => char.IsAsciiLetterOrDigit(ch) || ch is '-' or '_' or ':' or '.'), i.Id));
        Assert.DoesNotContain(c.Items, i => i.Id.StartsWith("app:", StringComparison.Ordinal));
        Assert.Contains(c.Items, i => i.Id == "screen:s2");                                      // a short id is the Eye's own
        Assert.Contains(c.Items, i => i.Id == EyeNetwork.ShortId("source:vid:C:\\shows\\gala\\walk-in.mp4") && i.Id.StartsWith("source:", StringComparison.Ordinal));
        // The accepted link: one item link:<peer id>, one row from the connector's node, K its use.
        var link = Assert.Single(c.Items, i => i.Id == "link:" + Hall);
        Assert.Equal(("link", "green"), (link.Kind, link.Light));
        Assert.Contains(c.Links, l => l.From == "@" + Hall && l.To == "link:" + Hall && l.Kind == "controls");
        Assert.All(c.Links.Where(l => !EyeNetwork.IsEnd(l.From) && !EyeNetwork.IsEnd(l.To)), l => Assert.Contains(c.Items, i => i.Id == l.From));
        // The canary: no key, no window's address, no path anywhere in what another app reads.
        var json = c.ToJson();
        Assert.False(EyeNetwork.Secretish(json), json);
        foreach (var raw in new[] { "canary", "swk_", "ptk_", "/w/", "walk-in", "C:\\\\" }) Assert.DoesNotContain(raw, json, StringComparison.Ordinal);
        Assert.Contains(c.Items, i => i.Id.StartsWith("source:", StringComparison.Ordinal) && i.Label == "(held — not published)");   // said as held, never dropped silently
        Assert.Equal("Hall A", Assert.Single(c.Peers).Name);
        Assert.Equal(EyeNetwork.ShortId("source:x/y"), EyeNetwork.ShortId("source:x/y"));      // stable
        Assert.NotEqual(EyeNetwork.ShortId("source:x/y"), EyeNetwork.ShortId("source:x/z"));
    }

    [Fact]
    public void ALinkOfThisDesksOwnToAnotherAppsThingIsExportedWithItsFarEndNamed()
    {
        // No reader draws one yet (the window chain's own @ edges wait for their producer): an own edge between this desk's screen and
        // PlayMap's Out 3 is exported as a row whose far end is @<PlayMap's id>/<the item PlayMap gave it>; a told edge never is.
        var room = PlayMapRoom(new[] { Row("link:patterns", "@" + Desk, "follows") });
        var card = "app:" + PlayMap;
        var g = EyeGraph.Build(new EyeFacts { ShowBusId = Desk, Screens = room.Screens, ShowApps = room.ShowApps });
        Assert.Contains(g.Edges, e => e.From == card && e.To == card + ":out3");
        var own = g.Relit(n => n, e => e.From == card && e.To == card + ":out3" ? e with { From = card + ":out3", To = "screen:s2", Kind = EyeEdgeKind.Feeds, Origin = EyeOrigin.Own } : e);
        var c = ShowContextBuilder.FromEye(own, Desk, "FOH", DateTime.UtcNow, Array.Empty<ShowLogEntry>(), Array.Empty<ShowContextPeer>());
        var row = Assert.Single(c.Links, l => EyeNetwork.IsEnd(l.From) || EyeNetwork.IsEnd(l.To));
        Assert.Equal(($"@{PlayMap}/out3", "screen:s2", "feeds"), (row.From, row.To, row.Kind));
        Assert.DoesNotContain(ShowContextBuilder.FromEye(g, Desk, "FOH", DateTime.UtcNow, Array.Empty<ShowLogEntry>(), Array.Empty<ShowContextPeer>()).Links, l => EyeNetwork.IsEnd(l.From) || EyeNetwork.IsEnd(l.To));
    }

    [Fact]
    public void AKindNotOfSixsWordsAndAPeersIdNotOfTwosShapeAreLeftOutAndCounted()
    {
        Assert.True(EyeNetwork.TryKind("Feeds", out var kind));
        Assert.Equal(EyeEdgeKind.Feeds, kind);
        foreach (var bad in new[] { "7", "999", "feeds,shows", " feeds", "", "feed", "Feeds2" }) Assert.False(EyeNetwork.TryKind(bad, out _), bad);
        var facts = PlayMapRoom(new[] { Row("out3", "@" + Desk, "7"), Row("out3", "@" + Desk, "feeds,shows"), Row("out3", "air", "999") });
        var g = EyeGraph.Build(facts);
        Assert.DoesNotContain(g.Edges, e => e.From == $"app:{PlayMap}:out3" && e.To != $"app:{PlayMap}:out3");
        Assert.Equal(2, g.Unplaced);                                                             // the two cross-node rows; the inner row is not drawn
        // A peers row whose id another app's item id could be (app:x:t1) is no node: no mention, no card, counted.
        var peers = PlayMapRoom(Array.Empty<ShowContextLink>());
        var said = new EyeFacts
        {
            ShowBusId = Desk,
            ShowApps = new[] { peers.ShowApps[0] with { Context = peers.ShowApps[0].Context! with { Peers = new[] { new ShowContextPeer { Kind = "main", Id = "x:t1", Name = "Odd" } } } } },
        };
        var g2 = EyeGraph.Build(said);
        Assert.DoesNotContain(g2.Nodes, n => n.Id.StartsWith("app:x", StringComparison.Ordinal));
        Assert.Equal(1, g2.Unplaced);
        // And the band says what is left out where it says the rest.
        var p = EyeLayout.Place(g);
        Assert.Contains("2 links the apps name not placed", p.Note, StringComparison.Ordinal);
    }
}
