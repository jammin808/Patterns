using Patterns.Core.Menus;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 99.4 (the Eye design's D13, Show Bus 1.3 §13.16.6): one builder of the show network's verbs, read by the Eye's card, a link's
/// edge, the Nodes page's card and — through the wire lines its entries carry — the deck; every entry a person's on this desk or the
/// wire's own line, none ever proposable; the canvas's edge hit-test, the NETWORK lens, the side card's LINKS and the beacon node by
/// the show's need.
/// </summary>
public class NetworkMenusTests
{
    private const string Sw = "a1b2c3d4";
    private const string Pm = "0a1b2c3d";

    private static DeskFacts Facts(string otherId = "") => new()
    {
        AssistantReady = true,
        Stagewatch = new MenuStagewatch { On = true, LinkedId = Sw, LinkedName = "Hall A", Follow = true, Heard = new[] { Sw }, Floor = true, OtherId = otherId },
        ShowApps = new[]
        {
            new MenuShowApp(Sw, "Stagewatch", "Hall A", "linked", true) { Kind = "main" },
            new MenuShowApp(Pm, "PlayMap", "Stage left", "heard", false, AddAsDevice: true) { Kind = "playmap" },
        },
        AppLinks = new[]
        {
            new MenuAppLink("c1bb791379aa366c", "main:" + Sw, "Stagewatch Hall A", "key", "e63c317763b1454f", "post", "✓", false),
            new MenuAppLink("d2cc8a248abb477d", "playmap:" + Pm, "PlayMap Stage left", "code", "", "follow", "△", false),
        },
    };

    private static EyeGraph Graph() => EyeGraph.Build(new EyeFacts
    {
        ShowApps = new[]
        {
            new EyeShowApp(Sw, "Stagewatch", "Hall A", ShowBusState.Live, true, ""),
            new EyeShowApp(Pm, "PlayMap", "Stage left", ShowBusState.Live, false, ""),
        },
    });

    private static IEnumerable<(string Heading, string Id, string Wire, ShowAction? Action, string Route)> Shape(IEnumerable<MenuGroup> groups)
        => groups.SelectMany(g => g.Entries.Select(e => (g.Heading, e.Id, e.Wire, e.Action, e.Route is { } r ? r.Page + "|" + r.Item : "")));

    private static readonly string[] Network = { "LINK", "DEVICE", "KEY", NetworkMenus.AcceptedHeading };

    [Fact]
    public void TheEyesCardItsLinkEdgeAndTheNodesCardCarryOneSetOfNetworkVerbs()
    {
        var d = Facts("5e6f7a8b");
        var g = Graph();
        foreach (var (id, label) in new[] { (Sw, "Stagewatch"), (Pm, "PlayMap") })
        {
            var nodes = DeskMenus.ShowApp(d, d.ShowApps.Single(a => a.Id == id));
            var card = EyeMenus.For(d, g, g.Find("app:" + id)!);
            var edge = g.Edges.First(e => (e.From == "app:" + id && e.To == EyeGraph.DeskId) || (e.To == "app:" + id && e.From == EyeGraph.DeskId));
            var linkMenu = EyeMenus.ForEdge(d, g, edge);
            var expected = Shape(NetworkMenus.Groups(d, d.ShowApps.Single(a => a.Id == id))).ToList();
            Assert.NotEmpty(expected);
            Assert.Equal(expected, Shape(nodes.Groups.Where(x => Network.Contains(x.Heading))));
            Assert.Equal(expected, Shape(card.Groups.Where(x => Network.Contains(x.Heading))));
            Assert.Equal(expected, Shape(linkMenu.Groups.Where(x => Network.Contains(x.Heading))));
        }
        // The Stagewatch's set: the link this desk holds, a key for it, and the link it made to this desk's wire.
        var sw = NetworkMenus.Groups(d, d.ShowApps[0]).Select(x => x.Heading).ToList();
        Assert.Equal(new[] { "LINK", "KEY", NetworkMenus.AcceptedHeading }, sw);
        // The PlayMap's: its device, a key for it, and its link to this desk; it is no Stagewatch, so no LINK group of the Stagewatch link.
        Assert.Equal(new[] { "DEVICE", "KEY", NetworkMenus.AcceptedHeading }, NetworkMenus.Groups(d, d.ShowApps[1]).Select(x => x.Heading));
    }

    [Fact]
    public void EveryNetworkVerbIsTheWiresOwnLineTheDeckSendsOrAPersonsOnThisDeskAndNoneIsProposable()
    {
        var d = Facts("5e6f7a8b");
        var deck = File.ReadAllLines(Path.Combine(CompanionModuleContractTests.ModuleDir, "test", "lines.txt")).Where(l => l.Trim().Length > 0).ToList();
        var entries = d.ShowApps.SelectMany(a => NetworkMenus.Groups(d, a)).SelectMany(g => g.Entries).ToList();
        Assert.Contains(entries, e => e.HasWire);
        Assert.Contains(entries, e => !e.HasWire && e.Action is not null);
        foreach (var e in entries)
        {
            if (e.HasWire)
            {
                // The wire's own line: it parses to the entry's action, and the deck sends that verb (its first two words are a line it writes).
                Assert.Equal(e.Action, ControlProtocol.Parse(e.Wire).Action);
                var verb = string.Join(' ', e.Wire.Split(' ').Take(2));
                Assert.True(deck.Any(l => l.StartsWith(verb, StringComparison.Ordinal)), $"{e.Id}: the deck sends no '{verb}' line");
            }
            else if (e.Action is { } a && e.Route is null)
            {
                // A person's press: no line says it, so no deck, cue, peer or assistant reaches it.
                Assert.Contains(a.Kind, WireWriter.Unsayable);
                Assert.Equal("", WireWriter.Line(a));
            }
            // Never a running order's, a peer's or an assistant's: every action is the desk's alone (the vocabulary's DeskOnly reason).
            if (e.Action is { } act && act.Kind != ShowActionKind.NodesAskWindow) Assert.NotNull(ActionSpec.DeskOnly(act.Kind));
        }
        Assert.Contains("never proposed", NetworkMenus.Groups(d, d.ShowApps[0])[0].Note, StringComparison.Ordinal);
    }

    [Fact]
    public void AcceptNewIdIsAPersonsPressBesideTheIdThatAnsweredAndGreyedWithTheReasonWhileNoneDid()
    {
        var waiting = DeskMenus.ShowApp(Facts("5e6f7a8b"), Facts().ShowApps[0]).Find("link.newid")!;
        Assert.Equal(new ShowAction(ShowActionKind.StagewatchAcceptId, "", "5e6f7a8b"), waiting.Action);
        Assert.Contains("5e6f7a8b", waiting.Text, StringComparison.Ordinal);
        Assert.False(waiting.HasWire);
        Assert.True(waiting.IsEnabled);
        Assert.Contains(ShowActionKind.StagewatchAcceptId, WireWriter.Unsayable);
        Assert.NotNull(ActionSpec.DeskOnly(ShowActionKind.StagewatchAcceptId));

        var none = DeskMenus.ShowApp(Facts(), Facts().ShowApps[0]).Find("link.newid")!;
        Assert.False(none.IsEnabled);
        Assert.Contains("Nothing else answers", none.Because, StringComparison.Ordinal);
        // An app this desk does not link to has none.
        Assert.Null(DeskMenus.ShowApp(Facts(), Facts().ShowApps[1]).Find("link.newid"));
    }

    [Fact]
    public void MakeKeyOpensTheNodesPageWithTheAppsKindAndIdAndLetItSpeakForOpensTheKeysBindBoxes()
    {
        var d = Facts();
        var make = DeskMenus.ShowApp(d, d.ShowApps[0]).Find("key.make")!;
        Assert.Equal(("Nodes", NetworkMenus.MakeRoute + "main:" + Sw), (make.Route!.Page, make.Route.Item));
        Assert.Null(make.Action);                                                                   // the key is made and shown on the page, once
        Assert.Equal(NetworkMenus.MakeRoute + "playmap:" + Pm, DeskMenus.ShowApp(d, d.ShowApps[1]).Find("key.make")!.Route!.Item);
        // A card with no kind word (a twin node's, an app no hello names) offers none: a key binds to a kind:id.
        Assert.Null(DeskMenus.ShowApp(d, new MenuShowApp("node:abcdef", "Patterns node", "STAGE-PC", "live", true)).Find("key.make"));
        // On a node, greyed with the reason: app keys are the desk's.
        Assert.False(DeskMenus.ShowApp(d with { IsNode = true }, d.ShowApps[0]).Find("key.make")!.IsEnabled);

        // The accepted link's own row carries Let its key speak for…, the key row's same route; a code's link has no key of its own.
        var keyed = DeskMenus.AppLink(d, d.AppLinks[0]).Find("link.speakfor")!;
        Assert.Equal(("Nodes", "e63c317763b1454f"), (keyed.Route!.Page, keyed.Route.Item));
        Assert.Null(DeskMenus.AppLink(d, d.AppLinks[1]).Find("link.speakfor"));
    }

    [Fact]
    public void AClaimsEdgeCarriesNoVerbAndSaysWhoseClaimItIs()
    {
        var d = Facts();
        var g = Graph();
        var claim = new EyeEdge("app:" + Pm, "app:" + Sw, EyeEdgeKind.Follows, CheckLight.Green, "PlayMap Stage left says: follows") { Origin = EyeOrigin.Told, Claim = "PlayMap Stage left" };
        var menu = EyeMenus.ForEdge(d, g, claim);
        Assert.DoesNotContain(menu.Groups, x => Network.Contains(x.Heading));
        Assert.Contains("A claim of PlayMap Stage left's", menu.Groups[0].Note, StringComparison.Ordinal);
        Assert.Equal(("EYE FOCUS app:" + Pm, "EYE FOCUS app:" + Sw), (menu.Find("eye.edge.from")!.Wire, menu.Find("eye.edge.to")!.Wire));
        Assert.Equal("PlayMap Stage left → Stagewatch Hall A", menu.Title);
        Assert.Equal($"{EyeMarks.SaysGlyph} PlayMap Stage left says", EyeMarks.SourceWords(claim));
        Assert.Equal("heard only", EyeMarks.SourceWords(new EyeEdge("a", "b", EyeEdgeKind.Hears, CheckLight.Grey) { Origin = EyeOrigin.Heard }));
        Assert.Equal("", EyeMarks.SourceWords(new EyeEdge("a", "b", EyeEdgeKind.Follows, CheckLight.Green) { Origin = EyeOrigin.Link }));
        // A relayed link is a claim by the one rule the canvas, the card and the menu read: dotted with the glyph, said with it, no verb.
        var relayed = new EyeEdge(EyeGraph.DeskId, "app:" + Sw, EyeEdgeKind.Follows, CheckLight.Green, "follows") { Origin = EyeOrigin.Relayed };
        Assert.True(EyeMarks.IsClaim(relayed));
        Assert.True(EyeMarks.IsClaim(claim));
        Assert.False(EyeMarks.IsClaim(new EyeEdge("a", "b", EyeEdgeKind.Follows, CheckLight.Green) { Origin = EyeOrigin.Link }));
        Assert.StartsWith(EyeMarks.SaysGlyph + " ", EyeMarks.SourceWords(relayed), StringComparison.Ordinal);
        var relayedMenu = EyeMenus.ForEdge(d, g, relayed);
        Assert.DoesNotContain(relayedMenu.Groups, x => Network.Contains(x.Heading));               // a desk-to-app link relayed carries no verb
        Assert.Contains("A claim of another app's", relayedMenu.Groups[0].Note, StringComparison.Ordinal);
        // The subject names one link of a pair, so a click and a right-click find the same one after a rebuild.
        var edge = g.Edges.First(e => e.From == "app:" + Sw);
        Assert.Equal($"app:{Sw}>{edge.Verb}>{edge.To}", EyeMenus.EdgeSubject(g, edge));
    }

    [Fact]
    public void TheNetworkLensShowsEveryAppCardEveryNodeAndTheDeskAndTheLinksBetweenThem()
    {
        foreach (var word in new[] { "network", "NETWORK", "apps", "links" }) Assert.Equal(EyeLens.Network, EyeGraph.ParseLens(word));
        var g = EyeGraph.Build(new EyeFacts
        {
            ShowApps = new[] { new EyeShowApp(Sw, "Stagewatch", "Hall A", ShowBusState.Live, true, "") },
            Nodes = new[] { new EyeNodeHeard("i1", "caller", "CALLER-PC", true, true, "linked") },
            Screens = new[] { new EyeScreen { Id = "s1", Number = "1", Label = "Left", OnAir = true } },
        });
        Assert.True(g.Visible(g.Find(EyeGraph.DeskId)!, EyeLens.Network));
        Assert.True(g.Visible(g.Find("app:" + Sw)!, EyeLens.Network));
        Assert.True(g.Visible(g.Find("node:i1")!, EyeLens.Network));
        Assert.False(g.Visible(g.Find("screen:s1")!, EyeLens.Network));
        Assert.All(g.Edges.Where(e => g.Visible(g.Find(e.From)!, EyeLens.Network) && g.Visible(g.Find(e.To)!, EyeLens.Network)),
            e => Assert.True(EyeGraph.IsNetworkCard(g.Find(e.From)!) || EyeGraph.IsNetworkCard(g.Find(e.To)!)));
        Assert.Contains(g.Edges, e => e.From == "app:" + Sw && e.To == EyeGraph.DeskId);
    }

    [Fact]
    public void ABeaconNodeGoneQuietIsRedOnlyWhenTheShowNeedsIt()
    {
        var g = EyeGraph.Build(new EyeFacts
        {
            Nodes = new[] { new EyeNodeHeard("i2", "timer", "STAGE-PC", false, false, ""), new EyeNodeHeard("i3", "timer", "FOH-PC", false, false, "") { Needed = true } },
        });
        Assert.Equal(CheckLight.Grey, g.Find("node:i2")!.Light);
        Assert.Contains("not needed", g.Find("node:i2")!.Sub, StringComparison.Ordinal);
        Assert.False(g.Find("node:i2")!.IsProblem);
        Assert.Equal(CheckLight.Red, g.Find("node:i3")!.Light);
        Assert.Equal("gone quiet", g.Find("node:i3")!.Sub);
        // A node known only by its hello is drawn as heard (the hollow ring); one linked is known by its link.
        Assert.Equal(EyeOrigin.Heard, g.Find("node:i2")!.Origin);
        var linked = EyeGraph.Build(new EyeFacts { Nodes = new[] { new EyeNodeHeard("i4", "timer", "SIDE-PC", true, true, "") } });
        Assert.Equal(EyeOrigin.Link, linked.Find("node:i4")!.Origin);
    }

    [Fact]
    public void ALinkIsHitWithinItsReachOfTheSegmentTheCanvasDrawsAndNeverBeyondIt()
    {
        Assert.Equal(0, EyeGeometry.Distance(5, 0, 0, 0, 10, 0), 9);
        Assert.Equal(3, EyeGeometry.Distance(5, 3, 0, 0, 10, 0), 9);
        Assert.Equal(5, EyeGeometry.Distance(13, 4, 0, 0, 10, 0), 9);                                // past an end: to that end
        Assert.Equal(EyeGeometry.Ends(new EyeRect(0, 0, 10, 10), new EyeRect(20, 0, 10, 10)), EyeGeometry.Segment(new EyeRect(0, 0, 10, 10), new EyeRect(20, 0, 10, 10), 0, 1));

        var g = Graph();
        var p = EyeLayout.Place(g);
        var link = g.Edges.Select((e, i) => (e, i)).First(x => x.e.From == "app:" + Sw && x.e.To == EyeGraph.DeskId);
        var (lane, count) = g.Lane(link.i);
        var (x1, y1, x2, y2) = EyeGeometry.Segment(p.Of("app:" + Sw), p.Of(EyeGraph.DeskId), lane, count);
        var (mx, my) = ((x1 + x2) / 2, (y1 + y2) / 2);
        Assert.Equal(link.i, EyeGeometry.EdgeAt(g, p, EyeLens.All, mx, my, 6));
        Assert.Equal(-1, EyeGeometry.EdgeAt(g, p, EyeLens.All, mx + 500, my + 500, 6));
        Assert.Equal(-1, EyeGeometry.EdgeAt(g, p, EyeLens.Video, mx, my, 6));                       // a lens that hides an end hides the link
        Assert.Equal(link.i, EyeGeometry.EdgeAt(g, p, EyeLens.Network, mx, my, 6));
    }

    [Fact]
    public void TheSideCardsLinksAreEachLinksOwnRecordWithItsFlagsAndClock()
    {
        var record = new LinkRecord("main", Sw, "Hall A", "ws", 1, "pinned", "", "mutual", "52f6", "post", "live", "", "live",
            new[] { "plain" }, "amber", "△", "Stagewatch Hall A · live", 0, 0, 0, null,
            new LinkRecordClock(12, 3, 100, 5, false), new LinkCounts(0, 0, 0, 0, 0, 0), null);
        var app = new EyeShowApp(Sw, "Stagewatch", "Hall A", ShowBusState.Live, true, "") { Link = record };
        var row = Assert.Single(EyeLinks.Of(app));
        Assert.Equal((CheckLight.Amber, "△", "this desk's link to it"), (row.Light, row.Mark, row.Title));
        Assert.Contains("Stagewatch Hall A · live · ws · level 1", row.Words, StringComparison.Ordinal);
        Assert.Equal("clock +12 ms ±3 · 5 samples", row.Clock);
        Assert.NotEqual("", row.Flags);
        Assert.Equal("clock not known", EyeLinks.ClockWords(new LinkRecordClock(null, null, null, 0, false)));
        Assert.EndsWith("· apart", EyeLinks.ClockWords(new LinkRecordClock(-400, 20, 10, 3, true)), StringComparison.Ordinal);
        Assert.Empty(EyeLinks.Of(new EyeShowApp(Pm, "PlayMap", "Stage left", ShowBusState.Live, false, "")));   // heard only: no link of its own
    }
}
