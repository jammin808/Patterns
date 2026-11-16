using System.Text.Json;
using System.Text.Json.Nodes;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 99.5 (the Eye design's §6; Show Bus 1.3 §6 and §13.16): the wire's readers of the show network. EYE JSON appends, at the end
/// of each row, a thing's app, source and folded cluster, a link's source, claim and record — a link this desk holds its 13.16.3
/// record as the last gather read it, a link its wire accepted its 13.16.5 row with no address, any other link and every link in a
/// replay null — and, at the end of the object, the apps known, this desk's links by light, the edges the bounds cut and the ends not
/// placed. STATE's <c>bus.apps</c> rows append the light's shape and the held link's state; the network line names the worst link.
/// </summary>
public class EyeWireTests
{
    private const string Desk = "9f8e7d6c";
    private const string Hall = "3f9c2a1b7d0e";
    private const string PlayMap = "0a1b2c3d";
    private const string Messenger = "77aa77aa";

    private static LinkRecord Record(string peerId, int unconfirmed = 0, string state = "live", string light = "green")
        => new("main", peerId, "Hall A", "ws", 1, "pinned", "03e9b152", "mutual", "52f6aa", "post", state, "", "live",
            Array.Empty<string>(), light, LinkRecord.MarkOf(ShowContext.LightOf(light)), "Hall A live", 0, 120, 0, null,
            new LinkRecordClock(12.5, 3, 900, 6, false), new LinkCounts(unconfirmed, 0, 0, 0, 0, 0), null);

    private static ShowContextItem Item(string id, string kind, string light = "green") => new() { Id = id, Kind = kind, Label = id, Light = light };

    private static ShowContext Context(string kind, string id, string name, IEnumerable<ShowContextItem> items, IEnumerable<ShowContextLink>? links = null) => new()
    {
        Node = new ShowContextNode { Kind = kind, Id = id, Name = name },
        Light = "green",
        Headline = name + " — fine",
        Items = items.ToList(),
        Links = (links ?? Array.Empty<ShowContextLink>()).ToList(),
    };

    /// <summary>A Stagewatch this desk follows by a proved link, a PlayMap heard with its context, an AGMessage linked to this desk's wire in plain from another machine.</summary>
    private static AppLinkRow Lobby() => new("l7", "messenger:" + Messenger, "Lobby", "10.0.0.44", false, false, "key", "e63c3177", "post", new DateTime(2026, 11, 15, 20, 14, 3, DateTimeKind.Utc));

    private static EyeFacts Room(LinkRecord? record = null, AppLinkRow[]? accepted = null) => new()
    {
        ShowBusId = Desk,
        ShowApps = new[]
        {
            new EyeShowApp(Hall, "Stagewatch", "Hall A", ShowBusState.Live, true, "") { Link = record ?? Record(Hall), Needed = true },
            new EyeShowApp(PlayMap, "PlayMap", "Stage left", ShowBusState.Live, false, "") { Context = Context("playmap", PlayMap, "Stage left", new[] { Item("out1", "output"), Item("out2", "output") }) },
        },
        AppLinks = accepted ?? new[] { Lobby() },
    };

    private static JsonDocument Json(EyeGraph g, EyePlacement p, ReplayMoment? moment = null, EyeLinksNow? now = null)
        => JsonDocument.Parse(EyeJson.Write(g, p, null, EyeLens.All, moment, now));

    [Fact]
    public void EyeJsonAppendsEachThingsAppSourceAndFoldAndEachLinksSourceClaimAndRecord()
    {
        var g = EyeGraph.Build(Room());
        var p = EyeLayout.Place(g, new EyeNetworkView { Folded = new[] { "app:" + PlayMap } });
        using var doc = Json(g, p);
        var root = doc.RootElement;
        JsonElement Node(string id) => root.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("id").GetString() == id);

        // A thing's row ends with its band and mark (rounds 95–96), then this round's app, source and whether its cluster is folded.
        Assert.Equal(new[] { "band", "mark", "app", "source", "collapsed" }, Node(EyeGraph.DeskId).EnumerateObject().Select(o => o.Name).TakeLast(5));
        Assert.Equal(("", "own", false), (Node(EyeGraph.DeskId).GetProperty("app").GetString(), Node(EyeGraph.DeskId).GetProperty("source").GetString(), Node(EyeGraph.DeskId).GetProperty("collapsed").GetBoolean()));
        Assert.Equal((Hall, "link"), (Node("app:" + Hall).GetProperty("app").GetString(), Node("app:" + Hall).GetProperty("source").GetString()));
        Assert.False(Node("app:" + Hall).GetProperty("collapsed").GetBoolean());                 // its card names no things: nothing to fold
        Assert.Equal("told", Node("app:" + PlayMap).GetProperty("source").GetString());
        Assert.True(Node("app:" + PlayMap).GetProperty("collapsed").GetBoolean());               // folded by the operator
        Assert.True(Node("app:" + PlayMap + ":out1").GetProperty("collapsed").GetBoolean());     // and its things with it, placed nowhere
        Assert.Equal(0, Node("app:" + PlayMap + ":out1").GetProperty("w").GetDouble());
        Assert.Equal("link", Node("app:" + Messenger).GetProperty("source").GetString());

        // Opened, the same card and its things are not folded.
        using (var open = Json(g, EyeLayout.Place(g, new EyeNetworkView { Opened = new[] { "app:" + PlayMap } })))
        {
            var card = open.RootElement.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("id").GetString() == "app:" + PlayMap);
            Assert.False(card.GetProperty("collapsed").GetBoolean());
        }

        // A link's row ends with its mark, then its source, claim and record.
        var edges = root.GetProperty("edges").EnumerateArray().ToList();
        Assert.All(edges, e => Assert.Equal(new[] { "mark", "source", "claim", "link" }, e.EnumerateObject().Select(o => o.Name).TakeLast(4)));
        var follows = edges.Single(e => e.GetProperty("from").GetString() == "app:" + Hall && e.GetProperty("to").GetString() == EyeGraph.DeskId);
        Assert.Equal(("link", ""), (follows.GetProperty("source").GetString(), follows.GetProperty("claim").GetString()));
        var record = follows.GetProperty("link");
        var verbatim = JsonNode.Parse(Record(Hall).ToJson())!.AsObject();
        Assert.True(JsonNode.DeepEquals(verbatim, JsonNode.Parse(record.GetRawText())), record.GetRawText());   // the 13.16.3 record verbatim
        Assert.Equal(verbatim.Select(p => p.Key), record.EnumerateObject().Select(o => o.Name));                  // its order kept
        Assert.Equal(Hall, record.GetProperty("peer").GetProperty("id").GetString());

        // The accepted link: its 13.16.5 row as the acceptor names it — never the address it dialled from, never its lid.
        var accepted = edges.Single(e => e.GetProperty("from").GetString() == "app:" + Messenger).GetProperty("link");
        Assert.Equal(("acceptor", "messenger", Messenger, "key", "post", "amber", "△"), (accepted.GetProperty("role").GetString(), accepted.GetProperty("t").GetString(),
            accepted.GetProperty("id").GetString(), accepted.GetProperty("verified").GetString(), accepted.GetProperty("scope").GetString(),
            accepted.GetProperty("light").GetString(), accepted.GetProperty("mark").GetString()));
        Assert.Equal("plain", Assert.Single(accepted.GetProperty("flags").EnumerateArray()).GetString());
        Assert.Equal(new DateTimeOffset(2026, 11, 15, 20, 14, 3, TimeSpan.Zero).ToUnixTimeMilliseconds(), accepted.GetProperty("since").GetInt64());
        Assert.DoesNotContain("10.0.0.44", accepted.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("l7", accepted.EnumerateObject().Select(o => o.Value.ToString()));

        // Every other link — a hello's, a context's own row — carries no record.
        Assert.All(edges.Where(e => e.GetProperty("source").GetString() != "link"), e => Assert.Equal(JsonValueKind.Null, e.GetProperty("link").ValueKind));
        var serves = edges.First(e => e.GetProperty("from").GetString() == "app:" + PlayMap && e.GetProperty("kind").GetString() == "serves");
        Assert.Equal("told", serves.GetProperty("source").GetString());                         // its context's own row
        var heard = edges.Single(e => e.GetProperty("from").GetString() == "app:" + PlayMap && e.GetProperty("to").GetString() == EyeGraph.DeskId);
        Assert.Equal("heard", heard.GetProperty("source").GetString());                          // a hello alone

        // The object's end: the apps the Eye knows, this desk's links by light, what the bounds cut, the ends not placed.
        Assert.Equal(new[] { "problemsCounted", "worstMark", "apps", "linkLights", "unplaced" }, root.EnumerateObject().Select(o => o.Name).TakeLast(5));
        Assert.Equal(3, root.GetProperty("apps").GetInt32());
        var lights = root.GetProperty("linkLights");
        Assert.Equal((1, 1, 0, 0), (lights.GetProperty("green").GetInt32(), lights.GetProperty("amber").GetInt32(), lights.GetProperty("red").GetInt32(), lights.GetProperty("grey").GetInt32()));
        Assert.Equal(new[] { "apps", "items", "edges" }, root.GetProperty("left").EnumerateObject().Select(o => o.Name));
        Assert.Equal(0, root.GetProperty("left").GetProperty("edges").GetInt32());
        Assert.Equal(0, root.GetProperty("unplaced").GetInt32());

        // One line for STATE, the deck and the Super Check: the worst link named with what it says.
        Assert.Equal("3 apps · 2 links · 1 amber: AGMessage Lobby — plain", g.NetworkLine);
        Assert.Equal("amber", g.LinkLights.Worst);
        // The assistant's brief reads the same line, after the headline.
        Assert.Equal("The show network: " + g.NetworkLine, g.Lines()[1]);
        Assert.DoesNotContain(EyeGraph.Build(new EyeFacts { ShowBusId = Desk }).Lines(), l => l.StartsWith("The show network", StringComparison.Ordinal));
    }

    [Fact]
    public void ALinksRecordIsReadAsTheLastGatherHadItAndIsNullInAReplay()
    {
        var g = EyeGraph.Build(Room(Record(Hall, unconfirmed: 0)));
        var p = EyeLayout.Place(g);
        // The picture is rebuilt only when a record's face moves; its counts and clock run on. EYE takes the record as it stands now.
        var now = EyeLinksNow.Of(Room(Record(Hall, unconfirmed: 3)));
        Assert.Equal(g.Edges.Single(e => e.Record is not null).Record!.Face, Record(Hall, unconfirmed: 3).Face);   // the same face: no rebuild
        using (var doc = Json(g, p, null, now))
        {
            var link = doc.RootElement.GetProperty("edges").EnumerateArray().Single(e => e.GetProperty("from").GetString() == "app:" + Hall).GetProperty("link");
            Assert.Equal(3, link.GetProperty("counts").GetProperty("unconfirmed").GetInt32());
        }
        // Without a gather, the edge's own record.
        using (var doc = Json(g, p))
        {
            var link = doc.RootElement.GetProperty("edges").EnumerateArray().Single(e => e.GetProperty("from").GetString() == "app:" + Hall).GetProperty("link");
            Assert.Equal(0, link.GetProperty("counts").GetProperty("unconfirmed").GetInt32());
        }
        // A replay draws a moment of the record over the structure of now: no link's record is of then, so none is carried.
        var moment = new ReplayMoment(new DateTime(2026, 11, 15, 20, 0, 0, DateTimeKind.Utc), null, null, Array.Empty<ShowLogEntry>(), null);
        using (var replay = Json(EyeReplay.Apply(g, moment), p, moment, now))
        {
            Assert.True(replay.RootElement.GetProperty("replay").GetBoolean());
            Assert.All(replay.RootElement.GetProperty("edges").EnumerateArray(), e => Assert.Equal(JsonValueKind.Null, e.GetProperty("link").ValueKind));
        }
    }

    [Fact]
    public void TheBoundsCountWhatTheyCutAndTheLineSaysIt()
    {
        // 300 rows from one drawn thing of PlayMap's to this desk: 256 claims drawn, the rest cut by the bound and counted.
        var rows = Enumerable.Range(0, 300).Select(_ => new ShowContextLink { From = "out1", To = "@" + Desk, Kind = "feeds", Light = "green" });
        var f = new EyeFacts
        {
            ShowBusId = Desk,
            ShowApps = new[] { new EyeShowApp(PlayMap, "PlayMap", "Stage left", ShowBusState.Live, false, "") { Context = Context("playmap", PlayMap, "Stage left", new[] { Item("out1", "output") }, rows) } },
        };
        var g = EyeGraph.Build(f);
        Assert.Equal(EyeNetwork.MaxCrossApp, g.Edges.Count(e => e.Origin == EyeOrigin.Told && e.Claim.Length > 0));
        Assert.Equal(300 - EyeNetwork.MaxCrossApp, g.LeftEdges);
        Assert.Equal(300 - EyeNetwork.MaxCrossApp, g.Unplaced);                                   // a cut claim found no place either
        using var doc = Json(g, EyeLayout.Place(g));
        Assert.Equal(44, doc.RootElement.GetProperty("left").GetProperty("edges").GetInt32());
        Assert.Equal(44, doc.RootElement.GetProperty("unplaced").GetInt32());
        Assert.Equal("1 app · 0 links · left out: 44 links past the bounds", g.NetworkLine);    // each cut row said once
        Assert.Equal("", g.LinkLights.Worst);
        // Nothing known, nothing said.
        Assert.Equal("", EyeGraph.Build(new EyeFacts { ShowBusId = Desk }).NetworkLine);
    }

    [Fact]
    public void ARelayedHearingPastItsBoundIsCountedAsCutAndNotAsUnplaced()
    {
        // 20 apps, each saying it hears the same 15 others this desk hears too: 300 relayed hearings, one hop each, 256 drawn.
        static string Id(int i) => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{0x5a000000 + i:x8}");
        var heard = Enumerable.Range(0, 15).Select(i => new EyeShowApp(Id(i), "Stagewatch", "Heard " + i, ShowBusState.Live, false, "") { Heard = "live" });
        var peers = Enumerable.Range(0, 15).Select(i => new ShowContextPeer { Kind = "main", Id = Id(i), Name = "Heard " + i, Light = "green" }).ToList();
        var relaying = Enumerable.Range(100, 20).Select(i => new EyeShowApp(Id(i), "PlayMap", "Relay " + i, ShowBusState.Live, false, "")
        {
            Context = Context("playmap", Id(i), "Relay " + i, Array.Empty<ShowContextItem>()) with { Peers = peers },
        });
        var g = EyeGraph.Build(new EyeFacts { ShowBusId = Desk, ShowApps = heard.Concat(relaying).ToArray() });
        Assert.Equal(EyeNetwork.MaxRelayed, g.Edges.Count(e => e.Origin == EyeOrigin.Relayed));
        Assert.Equal(300 - EyeNetwork.MaxRelayed, g.LeftEdges);
        Assert.Equal(0, g.Unplaced);                                                              // every end found its card; the bound cut the rest
        Assert.Equal(35, g.Apps);
        Assert.Equal("35 apps · 0 links · left out: 44 links past the bounds", g.NetworkLine);
        using var doc = Json(g, EyeLayout.Place(g));
        Assert.Equal((44, 0), (doc.RootElement.GetProperty("left").GetProperty("edges").GetInt32(), doc.RootElement.GetProperty("unplaced").GetInt32()));
    }

    [Fact]
    public void ATwinNodeIsAnAppTheEyeKnowsButItsTwinLinkIsNoPeerLink()
    {
        // A standby Patterns this desk is linked to, gone quiet while the show needs it: its card and twin link red, as the twin says.
        var f = new EyeFacts
        {
            ShowBusId = Desk,
            Nodes = new[] { new EyeNodeHeard("abcdef0123456789", "standby", "STANDBY-PC", false, true, "") { Needed = true } },
        };
        var g = EyeGraph.Build(f);
        var twin = g.Edges.Single(e => e.From == "node:abcdef0123456789" && e.To == EyeGraph.DeskId);
        Assert.Equal((EyeOrigin.Link, CheckLight.Red), (twin.Origin, twin.Light));
        Assert.False(twin.IsLink);                                                                // 13.18: failover's own carrier, never a PeerLink
        Assert.Equal((1, 0), (g.Apps, g.LinkLights.Count));
        Assert.Equal("1 app · 0 links", g.NetworkLine);                                          // never "1 red: Patterns standby"
        Assert.Equal("", g.LinkLights.Worst);
        using var doc = Json(g, EyeLayout.Place(g));
        Assert.Equal(0, doc.RootElement.GetProperty("linkLights").GetProperty("red").GetInt32());
        Assert.Equal(JsonValueKind.Null, doc.RootElement.GetProperty("edges").EnumerateArray()
            .Single(e => e.GetProperty("from").GetString() == "node:abcdef0123456789").GetProperty("link").ValueKind);
    }

    [Fact]
    public void AnAcceptedRowGoneFromTheGatherIsWrittenFromItsEdgeAndAnEndedOneSaysWhenAndWhy()
    {
        var g = EyeGraph.Build(Room());
        // The gather no longer lists the row (the list moved on between rebuilds): the edge's own row is written, never null.
        var gone = EyeLinksNow.Of(Room(accepted: Array.Empty<AppLinkRow>()));
        using (var doc = Json(g, EyeLayout.Place(g), null, gone))
        {
            var row = doc.RootElement.GetProperty("edges").EnumerateArray().Single(e => e.GetProperty("from").GetString() == "app:" + Messenger).GetProperty("link");
            Assert.Equal(("acceptor", Messenger, false, 0L, ""), (row.GetProperty("role").GetString(), row.GetProperty("id").GetString(),
                row.GetProperty("ended").GetBoolean(), row.GetProperty("endedAt").GetInt64(), row.GetProperty("endWords").GetString()));
            Assert.Equal(new[] { "flags", "ended", "endedAt", "endWords" }, row.EnumerateObject().Select(o => o.Name).TakeLast(4));
        }
        // An ended row of an app heard on the bus (an ended link with no card of its own is the Nodes page's to list): grey, with when
        // and why — the why its edge already says — and still no address.
        var at = new DateTime(2026, 11, 15, 21, 0, 0, DateTimeKind.Utc);
        var ended = new EyeFacts
        {
            ShowBusId = Desk,
            ShowApps = Room().ShowApps.Append(new EyeShowApp(Messenger, "AGMessage", "Lobby", ShowBusState.Live, false, "") { Heard = "live" }).ToArray(),
            AppLinks = new[] { Lobby() with { EndedUtc = at, EndWords = "the connection ended" } },
        };
        var eg = EyeGraph.Build(ended);
        using (var doc = Json(eg, EyeLayout.Place(eg)))
        {
            var row = doc.RootElement.GetProperty("edges").EnumerateArray()
                .Single(e => e.GetProperty("from").GetString() == "app:" + Messenger && e.GetProperty("link").ValueKind == JsonValueKind.Object).GetProperty("link");
            Assert.Equal((true, new DateTimeOffset(at).ToUnixTimeMilliseconds(), "the connection ended", "grey"), (row.GetProperty("ended").GetBoolean(),
                row.GetProperty("endedAt").GetInt64(), row.GetProperty("endWords").GetString(), row.GetProperty("light").GetString()));
            Assert.DoesNotContain("10.0.0.44", row.ToString(), StringComparison.Ordinal);
        }
        Assert.Equal(1, eg.LinkLights.Grey);
    }

    [Fact]
    public void StateBusRowsAppendTheMarkAndTheHeldLinksState()
    {
        var apps = new[]
        {
            new EyeShowApp(Hall, "Stagewatch", "Hall A", ShowBusState.Live, true, "") { Link = Record(Hall, state: "stale", light: "amber") },
            new EyeShowApp(PlayMap, "PlayMap", "Stage left", ShowBusState.Live, false, "") { Heard = "live" },
            // Heard with a context that says it is green: the mark is the light's, never the row's default.
            new EyeShowApp(Messenger, "AGMessage", "Lobby", ShowBusState.Live, false, "") { Heard = "live", Context = Context("messenger", Messenger, "Lobby", Array.Empty<ShowContextItem>()) },
        };
        var rows = ShowBusView.StateRows(apps);
        var hall = rows.Single(r => r.Id == Hall);
        Assert.Equal(("stale", "amber", "△"), (hall.Link, hall.Light, hall.Mark));
        var heard = rows.Single(r => r.Id == PlayMap);
        Assert.Equal("", heard.Link);                                                             // a hello alone holds no link
        Assert.Equal(EyeMarks.Glyph(ShowContext.LightOf(heard.Light)), heard.Mark);
        var told = rows.Single(r => r.Id == Messenger);
        Assert.Equal(("", "green", "✓"), (told.Link, told.Light, told.Mark));
    }

    [Fact]
    public void TheSuperCheckSaysTheEyesNetworkReadInGreyBesideTheLinksRows()
    {
        var g = EyeGraph.Build(Room());
        var report = SuperCheck.Run(new CheckFacts { EyeNetwork = g.NetworkLine });
        var row = Assert.Single(report.Rows, r => r.Item == "Eye network");
        Assert.Equal(("SHOW NETWORK", CheckLight.Grey, g.NetworkLine), (row.Section, row.Light, row.Value));
        var section = report.Rows.Where(r => r.Section == "SHOW NETWORK").Select(r => r.Item).ToList();
        Assert.True(section.IndexOf("Show network links") < section.IndexOf("Eye network"));
        Assert.DoesNotContain(SuperCheck.Run(new CheckFacts()).Rows, r => r.Item == "Eye network");
    }
}
