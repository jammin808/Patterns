using Patterns.Core.Menus;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The Eye's NETWORK band — every other app a cluster of its own on a band above CONTROL, bounded by the canvas the
/// fit view is read on, the open set the desk's own view state, and the rail counting only this desk's things, its links and
/// the needed apps' cards. The measured cause, computed and asserted below: before the band every other app's thing stacked in
/// one CONTROL column — the reference room's 48 things made a column 3,304 world units tall, so the fit view drew no label on
/// any canvas under 1,237 px, and a 1,080-px laptop page read a column of dots.
/// </summary>
public class EyeLayoutTests
{
    private static ShowContext Context(string id, string name, int things, params string[] kinds) => new()
    {
        Node = new ShowContextNode { Kind = "x", Id = id, Name = name },
        Light = "green",
        Items = Enumerable.Range(0, things).Select(i => new ShowContextItem { Id = $"i{i}", Kind = kinds[i % kinds.Length], Label = $"{name} {i}", Light = "green", State = "fine" }).ToList(),
    };

    private static EyeShowApp App(string id, string kind, string name, int things, bool linked, bool needed, params string[] kinds)
        => new(id, kind, name, ShowBusState.Live, linked, "") { Context = things > 0 ? Context(id, name, things, kinds) : null, Needed = needed };

    /// <summary>
    /// The reference room (PLAN §113.1): Stagewatch, PlayMap and AGMessage at twelve things each, three timer nodes at four each,
    /// and a desk of four screens and six control things.
    /// </summary>
    private static EyeFacts Room(IEnumerable<EyeShowApp>? apps = null) => new()
    {
        MachineName = "FOH",
        Health = CheckLight.Green,
        OutputsLive = true,
        Screens = Enumerable.Range(1, 4).Select(i => new EyeScreen { Id = $"s{i}", Number = $"{i}", Label = $"Screen {i}" }).ToList(),
        Decks = new[] { new EyeDeck("FOH deck", "3.20.0", "10.0.0.5", true), new EyeDeck("Stage deck", "3.20.0", "10.0.0.6", true) },
        WireClients = 1,
        WebClients = 1,
        Osc = new EyeOsc(8000, "mapped", "10.0.0.5"),
        Stack = new EyeStack(true, "03", "c3", "02", false, false, "", CheckLight.Green),
        ShowApps = (apps ?? ReferenceApps()).ToList(),
    };

    private static IEnumerable<EyeShowApp> ReferenceApps() => new[]
    {
        App("a1b2c3d4", "Stagewatch", "Hall A", 12, true, true, "timer", "department", "screen", "message"),
        App("0a1b2c3d", "PlayMap", "Stage left", 12, false, false, "output", "projector", "surface", "media"),
        App("77aa77aa", "AGMessage", "Messenger", 12, false, false, "help", "room", "screen"),
        App("node:t1", "Patterns node", "Timer 1", 4, true, true, "stage", "desk"),
        App("node:t2", "Patterns node", "Timer 2", 4, true, true, "stage", "desk"),
        App("node:t3", "Patterns node", "Timer 3", 4, true, true, "stage", "desk"),
    };

    private static void NoOverlap(EyePlacement p)
    {
        var rects = p.Rects.ToList();
        for (var i = 0; i < rects.Count; i++)
        {
            for (var j = i + 1; j < rects.Count; j++) Assert.False(rects[i].Value.Intersects(rects[j].Value), $"{rects[i].Key} overlaps {rects[j].Key}");
        }
    }

    [Fact]
    public void TheReferenceRoomFitsTheReferenceCanvasWithItsLabelsReadable()
    {
        // The measured cause: one CONTROL column of 48 things, fitted on a 1,080-px canvas, is a column of dots.
        const int Things = 48;
        var column = Things * EyeLayout.NodeH + (Things - 1) * EyeLayout.RowGap + 2 * EyeLayout.BandPad;
        Assert.Equal(3304, column);
        Assert.True((EyeLayout.ReferenceCanvasH - 80) / column < EyeLayout.LabelScale);
        Assert.Equal(1236.4, EyeLayout.LabelScale * column + 80, 6);

        var g = EyeGraph.Build(Room());
        var p = EyeLayout.Place(g, EyeNetworkView.Default with { Slots = EyeLayout.Slots(g, new Dictionary<string, int>()) });
        Assert.Equal(48, g.Nodes.Count(n => n.Kind == EyeKind.ShowAppItem));
        Assert.Equal(6, p.Expanded.Count);                                                    // every cluster open on the reference canvas
        Assert.True(p.FitScale >= EyeLayout.LabelScale, $"fit {p.FitScale:0.000}");
        Assert.Equal(p.FitScale, EyeCamera.FitScale(p.Bounds, EyeLayout.ReferenceCanvasW, EyeLayout.ReferenceCanvasH), 9);   // the camera's own fit, no rendering
        Assert.False(p.MarksOnly);
        NoOverlap(p);

        // The NETWORK band on top, this desk's bands below it in their order; every app's card and thing in it, every own thing in its own band.
        Assert.Equal(new[] { EyePlane.Network, EyePlane.Control, EyePlane.Video }, p.Bands.Select(b => b.Plane));
        var network = p.Bands[0].Rect;
        foreach (var n in g.Nodes)
        {
            var band = p.Bands.Single(b => b.Plane == n.Band).Rect;
            var r = p.Of(n.Id);
            Assert.False(r.IsEmpty, n.Id);
            Assert.True(r.X >= band.X && r.Right <= band.Right && r.Y >= band.Y && r.Bottom <= band.Bottom, n.Id);
            Assert.Equal(n.Kind is EyeKind.ShowApp or EyeKind.ShowAppItem, n.Band == EyePlane.Network);
        }
        Assert.True(p.Bands[1].Rect.Y > network.Bottom);

        // A cluster: the card, then its things three to a row under it, in its kinds' column order and then the context's.
        var card = p.Of("app:0a1b2c3d");
        var things = g.Nodes.Where(n => n.App == "0a1b2c3d" && n.Id != "app:0a1b2c3d").ToList();
        Assert.Equal(12, things.Count);
        Assert.All(things, t => Assert.Equal(EyeKind.ShowAppItem, t.Kind));
        var order = things.OrderBy(t => p.Of(t.Id).Y).ThenBy(t => p.Of(t.Id).X).ToList();
        Assert.Equal(order.Select(t => t.Tier).OrderBy(t => t), order.Select(t => t.Tier));      // the kind table's columns, left to right then down
        Assert.Equal(card.X, p.Of(order[0].Id).X);
        Assert.Equal(card.Bottom + EyeLayout.RowGap, p.Of(order[0].Id).Y);
        Assert.Equal(3, order.Select(t => p.Of(t.Id).X).Distinct().Count());
        // ⌈√6⌉ = 3 clusters to a row: two rows of three cards.
        var cards = g.Nodes.Where(n => n.Kind == EyeKind.ShowApp).Select(n => p.Of(n.Id)).ToList();
        Assert.Equal((2, 3), (cards.Select(r => r.Y).Distinct().Count(), cards.Select(r => r.X).Distinct().Count()));

        // Each thing keeps its kind's plane for the lenses: a PlayMap projector on the VIDEO lens, with its card; the desk's deck off it.
        var projector = g.Nodes.First(n => n.App == "0a1b2c3d" && n.Words.Any(w => w.EndsWith(": projector", StringComparison.Ordinal)));
        Assert.Equal(EyePlane.Video, projector.Plane);
        Assert.Equal(EyePlane.Network, projector.Band);
        Assert.True(g.Visible(projector, EyeLens.Video));
        Assert.True(g.Visible(g.Find("app:0a1b2c3d")!, EyeLens.Video));
        Assert.False(g.Visible(g.Find("app:77aa77aa")!, EyeLens.Audio));
        Assert.Equal("NETWORK · VIDEO", EyeLayout.Where(projector));
        Assert.Equal("VIDEO", EyeLayout.Where(g.Find("screen:s1")!));

        // What the band says, and the JSON's appended fields.
        Assert.Equal("6 of 6 apps open", p.Note);
        Assert.Equal(p.Note, p.Bands[0].Note);
        using var doc = System.Text.Json.JsonDocument.Parse(EyeJson.Write(g, p, null, EyeLens.All));
        var root = doc.RootElement;
        Assert.Equal(new[] { "replay", "replayAt", "moment", "expanded", "left", "fit", "problemsCounted", "worstMark" }, root.EnumerateObject().Select(o => o.Name).TakeLast(8));
        Assert.Equal(6, root.GetProperty("expanded").GetArrayLength());
        Assert.Equal((0, 0), (root.GetProperty("left").GetProperty("apps").GetInt32(), root.GetProperty("left").GetProperty("items").GetInt32()));
        Assert.Equal("", root.GetProperty("fit").GetString());
        var row = root.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("id").GetString() == projector.Id);
        Assert.Equal(("video", "network"), (row.GetProperty("plane").GetString(), row.GetProperty("band").GetString()));
        Assert.Equal(new[] { "band", "mark" }, row.EnumerateObject().Select(o => o.Name).TakeLast(2));   // each appended at the end of its row
    }

    [Fact]
    public void ASmallerCanvasOpensFewerClustersAndAtTheBoundSaysMarksOnly()
    {
        var g = EyeGraph.Build(Room());
        var slots = EyeLayout.Slots(g, new Dictionary<string, int>());
        foreach (var (w, h) in new[] { (1620.0, 1080.0), (1366.0, 768.0), (1024.0, 600.0), (800.0, 480.0), (500.0, 300.0) })
        {
            var p = EyeLayout.Place(g, new EyeNetworkView { Slots = slots, CanvasW = w, CanvasH = h });
            NoOverlap(p);
            Assert.Equal(p.FitScale, EyeCamera.FitScale(p.Bounds, w, h), 9);
            if (p.MarksOnly)
            {
                Assert.Empty(p.Expanded);                                                         // nothing the desk opened by itself is left open
                Assert.Contains("marks only", p.Note, StringComparison.Ordinal);
            }
            else Assert.True(p.FitScale >= EyeLayout.LabelScale, $"{w}×{h}: {p.FitScale:0.000}");
            // The needed apps open first: Stagewatch and the desk's own nodes before PlayMap and AGMessage.
            var needed = new[] { "app:a1b2c3d4", "app:node:t1", "app:node:t2", "app:node:t3" };
            if (p.Expanded.Count is > 0 and < 4) Assert.All(p.Expanded, id => Assert.Contains(id, needed));
            // What is folded is said: its things counted as not drawn.
            Assert.Equal(g.Nodes.Count(n => n.Kind == EyeKind.ShowAppItem && !p.IsExpanded("app:" + n.App)), p.LeftItems);
            Assert.All(g.Nodes.Where(n => n.Kind == EyeKind.ShowAppItem && !p.IsExpanded("app:" + n.App)), n => Assert.True(p.Of(n.Id).IsEmpty));
        }
        var tiny = EyeLayout.Place(g, new EyeNetworkView { Slots = slots, CanvasW = 500, CanvasH = 300 });
        Assert.True(tiny.MarksOnly);
        using var doc = System.Text.Json.JsonDocument.Parse(EyeJson.Write(g, tiny, null, EyeLens.All));
        Assert.Contains("marks only", doc.RootElement.GetProperty("fit").GetString(), StringComparison.Ordinal);
    }

    [Fact]
    public void TheOpenSetIsTheViewsOwnAndNeitherALightNorAFocusMovesIt()
    {
        var g = EyeGraph.Build(Room());
        var slots = EyeLayout.Slots(g, new Dictionary<string, int>());
        var view = new EyeNetworkView { Slots = slots, CanvasW = 1366, CanvasH = 768 };
        var p = EyeLayout.Place(g, view);

        // A folded cluster stays folded though the canvas has room; an opened one stays open though it has none.
        var folded = EyeLayout.Place(g, view with { CanvasW = 4000, CanvasH = 4000, Folded = new[] { "app:a1b2c3d4" } });
        Assert.False(folded.IsExpanded("app:a1b2c3d4"));
        Assert.Equal(5, folded.Expanded.Count);
        Assert.Equal(12, folded.Hidden["app:a1b2c3d4"]);
        Assert.False(folded.Of("app:a1b2c3d4").IsEmpty);                                         // its card stays, in the strip
        var opened = EyeLayout.Place(g, view with { CanvasW = 500, CanvasH = 300, Opened = new[] { "app:77aa77aa" } });
        Assert.Equal(new[] { "app:77aa77aa" }, opened.Expanded);
        Assert.True(opened.MarksOnly);

        // Every light in the room turned: the same clusters open, every thing in the same place.
        var relit = EyeGraph.Build(Room(ReferenceApps().Select(a => a with
        {
            Context = a.Context is null ? null : a.Context with { Light = "red", Items = a.Context.Items.Select((i, n) => i with { Light = n % 2 == 0 ? "red" : "amber" }).ToList() },
        })));
        var q = EyeLayout.Place(relit, view);
        Assert.Equal(p.Expanded, q.Expanded);
        foreach (var n in g.Nodes) Assert.Equal(p.Of(n.Id), q.Of(n.Id));
    }

    [Fact]
    public void AClusterThatFoldsOrAnAppThatLeavesMovesNoOtherClusterOrCard()
    {
        var apps = ReferenceApps().ToList();
        var g = EyeGraph.Build(Room(apps));
        var slots = EyeLayout.Slots(g, new Dictionary<string, int>());
        var view = new EyeNetworkView { Slots = slots };
        var p = EyeLayout.Place(g, view);
        Assert.Equal(6, p.Expanded.Count);
        Assert.Equal(Enumerable.Range(0, 6), p.Cells.Values.OrderBy(c => c));
        var kept = view with { Cells = p.Cells };
        IEnumerable<EyeNode> Others(EyeGraph graph, string app) => graph.Nodes.Where(n => n.Band == EyePlane.Network && n.App != app);
        foreach (var app in apps)
        {
            var card = "app:" + app.Id;
            // Folded by hand: its card goes to the strip, its cell stays a gap, and every other card and thing keeps its rectangle.
            var folded = EyeLayout.Place(g, kept with { Folded = new[] { card } });
            Assert.False(folded.IsExpanded(card));
            Assert.False(folded.Cells.ContainsKey(card));
            foreach (var n in Others(g, app.Id)) Assert.Equal(p.Of(n.Id), folded.Of(n.Id));
            Assert.True(folded.Of(card).Y > g.Nodes.Where(n => n.Band == EyePlane.Network && n.App != app.Id).Max(n => p.Of(n.Id).Bottom), $"{card} sits in the strip below the clusters");
            NoOverlap(folded);
            // Opened again: back into its own cell — the picture is the one before.
            var again = EyeLayout.Place(g, kept with { Cells = folded.Cells });
            Assert.Equal(p.Cells[card], again.Cells[card]);
            foreach (var n in g.Nodes) Assert.Equal(p.Of(n.Id), again.Of(n.Id));
            // The app leaves the room: every other app keeps its slot, its cell and every rectangle.
            var left = EyeGraph.Build(Room(apps.Where(a => a.Id != app.Id)));
            var gone = EyeLayout.Place(left, kept with { Slots = EyeLayout.Slots(left, slots) });
            foreach (var n in Others(left, app.Id)) Assert.Equal(p.Of(n.Id), gone.Of(n.Id));
        }

        // A room with a strip: thirty apps on a laptop's canvas, some open by the canvas, the rest folded. One open cluster folded by
        // hand, or one app leaving, moves no card or thing of an app whose cluster stayed as it was.
        var many = Enumerable.Range(0, 30).Select(i => App($"m{i:00}", "PlayMap", $"Node {i:00}", 6, i % 3 == 0, i % 5 == 0, "output", "projector", "link")).ToList();
        var mg = EyeGraph.Build(Room(many));
        var mslots = EyeLayout.Slots(mg, new Dictionary<string, int>());
        var mview = new EyeNetworkView { Slots = mslots, CanvasW = 1366, CanvasH = 768 };
        var mp = EyeLayout.Place(mg, mview);
        Assert.InRange(mp.Expanded.Count, 1, 29);
        var mkept = mview with { Cells = mp.Cells };
        void Still(EyeGraph graph, EyePlacement after, string changed)
        {
            foreach (var n in graph.Nodes.Where(n => n.Band == EyePlane.Network && n.App.Length > 0 && n.App != changed && mp.IsExpanded("app:" + n.App) == after.IsExpanded("app:" + n.App)))
                Assert.Equal(mp.Of(n.Id), after.Of(n.Id));
            NoOverlap(after);
        }
        var fold = mp.Expanded[0];
        Still(mg, EyeLayout.Place(mg, mkept with { Folded = new[] { fold } }), fold[4..]);
        var leave = many[7].Id;
        var lg = EyeGraph.Build(Room(many.Where(a => a.Id != leave)));
        Still(lg, EyeLayout.Place(lg, mkept with { Slots = EyeLayout.Slots(lg, mslots) }), leave);
    }

    [Fact]
    public void AFocusOnAThingOfAFoldedClusterFitsItsCardNeverTheWorldsCorner()
    {
        // Stagewatch's Lighting is linked only to its timer, so it has no edge to its card: folded, neither end has a place.
        var stagewatch = new ShowContext
        {
            Node = new ShowContextNode { Kind = "main", Id = "a1b2c3d4", Name = "Hall A" },
            Light = "amber",
            Items = new[]
            {
                new ShowContextItem { Id = "timer", Kind = "timer", Label = "Keynote", Light = "green", State = "running" },
                new ShowContextItem { Id = "cue:lighting", Kind = "department", Label = "Lighting", Light = "amber", State = "standing by" },
            },
            Links = new[] { new ShowContextLink { From = "timer", To = "cue:lighting", Kind = "drives", Light = "amber" } },
        };
        var g = EyeGraph.Build(new EyeFacts
        {
            MachineName = "FOH",
            Health = CheckLight.Green,
            ShowApps = new[] { new EyeShowApp("a1b2c3d4", "Stagewatch", "Hall A", ShowBusState.Live, false, "") { Context = stagewatch } },
        });
        Assert.DoesNotContain(g.Neighbours("app:a1b2c3d4:cue:lighting"), id => id == "app:a1b2c3d4");
        Assert.Contains("app:a1b2c3d4:cue:lighting", g.Problems);                                   // NEXT reaches it
        var p = EyeLayout.Place(g, new EyeNetworkView { Folded = new[] { "app:a1b2c3d4" } });
        Assert.True(p.Of("app:a1b2c3d4:cue:lighting").IsEmpty);
        var around = p.Around("app:a1b2c3d4:cue:lighting", g);
        Assert.False(around.IsEmpty);
        Assert.Equal(p.Of("app:a1b2c3d4"), around);
        // Open, it fits its own place and its neighbours as before.
        var open = EyeLayout.Place(g, new EyeNetworkView { Opened = new[] { "app:a1b2c3d4" } });
        Assert.Equal(open.Of("app:a1b2c3d4:cue:lighting").Union(open.Of("app:a1b2c3d4:timer")), open.Around("app:a1b2c3d4:cue:lighting", g));
    }

    [Fact]
    public void WithNoOtherAppAFitViewOfMarksOnlySaysSoToo()
    {
        var big = new EyeFacts
        {
            MachineName = "FOH",
            Health = CheckLight.Green,
            OutputsLive = true,
            Screens = Enumerable.Range(1, 40).Select(i => new EyeScreen { Id = $"s{i}", Number = $"{i}", Label = $"Screen {i}" }).ToList(),
        };
        var g = EyeGraph.Build(big);
        var p = EyeLayout.Place(g, new EyeNetworkView { CanvasW = 800, CanvasH = 480 });
        Assert.DoesNotContain(p.Bands, b => b.Plane == EyePlane.Network);
        Assert.True(p.MarksOnly, $"fit {p.FitScale:0.000}");
        Assert.Equal(EyeLayout.MarksOnlyWords, p.Note);
        Assert.Equal(EyeLayout.MarksOnlyWords, p.Bands[0].Note);                                  // the canvas draws it on the top band
        using var doc = System.Text.Json.JsonDocument.Parse(EyeJson.Write(g, p, null, EyeLens.All));
        Assert.Equal(EyeLayout.MarksOnlyWords, doc.RootElement.GetProperty("fit").GetString());
        var roomy = EyeLayout.Place(g, new EyeNetworkView { CanvasW = 8000, CanvasH = 8000 });
        Assert.False(roomy.MarksOnly);
        Assert.Equal("", roomy.Note);
        Assert.All(roomy.Bands, b => Assert.Equal("", b.Note));
    }

    [Fact]
    public void TheRailCountsThisDesksOwnItsLinksAndTheNeededCardsAndQueuesTheBorrowedAfter()
    {
        var amberThings = new ShowContext
        {
            Node = new ShowContextNode { Kind = "playmap", Id = "0a1b2c3d", Name = "Stage left" },
            Light = "red",
            Headline = "Out 7 not open",
            Items = new[] { new ShowContextItem { Id = "out7", Kind = "output", Label = "Out 7", Light = "red", State = "not open" } },
        };
        var facts = new EyeFacts
        {
            MachineName = "FOH",
            Health = CheckLight.Green,
            ShowApps = new[]
            {
                new EyeShowApp("0a1b2c3d", "PlayMap", "Stage left", ShowBusState.Live, false, "") { Context = amberThings },          // heard, not needed: its red is its own
                new EyeShowApp("a1b2c3d4", "Stagewatch", "Hall A", ShowBusState.Stale, true, "") { Needed = true },                // followed and quiet: the desk's amber
            },
        };
        var g = EyeGraph.Build(facts);
        Assert.Equal(CheckLight.Red, g.Find("app:0a1b2c3d")!.Light);
        Assert.Equal(CheckLight.Red, g.Find("app:0a1b2c3d:out7")!.Light);
        Assert.Equal(2, g.Things);                                                                  // the desk and the needed Stagewatch — its card and its link one thing
        Assert.Equal((0, 1, 1, 0), (g.Red, g.Amber, g.Green, g.Grey));                              // one fault, counted once
        Assert.Equal(g.Things, g.Red + g.Amber + g.Green + g.Grey);
        Assert.Equal(CheckLight.Amber, g.WorstLight);
        Assert.Equal("app:a1b2c3d4", g.Worst!.Id);
        Assert.StartsWith("2 things · 1 amber — Stagewatch Hall A: ", g.Headline, StringComparison.Ordinal);
        Assert.Equal(1, g.CountedProblems);
        Assert.DoesNotContain("Out 7", g.Headline, StringComparison.Ordinal);
        Assert.Equal(new[] { "app:a1b2c3d4", "app:0a1b2c3d:out7", "app:0a1b2c3d" }, g.Problems);    // the borrowed reds after every counted problem
        Assert.Equal("app:0a1b2c3d:out7", g.Next("app:a1b2c3d4"));                                // NEXT still walks to them

        // Every reader says the same count: EYE's counts and its counted problems; the assistant's lines mark the borrowed.
        using (var json = System.Text.Json.JsonDocument.Parse(EyeJson.Write(g, EyeLayout.Place(g), null, EyeLens.All)))
        {
            var counts = json.RootElement.GetProperty("counts");
            Assert.Equal((2, 1, 1), (counts.GetProperty("things").GetInt32(), counts.GetProperty("amber").GetInt32(), json.RootElement.GetProperty("problemsCounted").GetInt32()));
            Assert.Equal(3, json.RootElement.GetProperty("problems").GetArrayLength());
        }
        Assert.Contains(g.Lines(), l => l.StartsWith("NETWORK · VIDEO · ShowAppItem: Out 7 (another app's, not counted) — red", StringComparison.Ordinal));
        Assert.Contains(g.Lines(), l => l.StartsWith("NETWORK · CONTROL · ShowApp: Stagewatch Hall A — amber", StringComparison.Ordinal));

        // Not needed, its link quiet: amber on the rail through the link alone; gone, amber still — red only when the show needs it.
        EyeFacts Only(EyeShowApp app) => new() { MachineName = "FOH", Health = CheckLight.Green, ShowApps = new[] { app } };
        var unneeded = EyeGraph.Build(Only(facts.ShowApps[1] with { Needed = false, State = ShowBusState.Gone }));
        Assert.Equal(2, unneeded.Things);                                                           // the desk and the link this desk holds
        Assert.Equal((0, 1), (unneeded.Red, unneeded.Amber));
        Assert.Equal(unneeded.Things, unneeded.Red + unneeded.Amber + unneeded.Green + unneeded.Grey);
        Assert.Equal(CheckLight.Red, unneeded.Find("app:a1b2c3d4")!.Light);
        var needed = EyeGraph.Build(Only(facts.ShowApps[1] with { State = ShowBusState.Gone }));
        Assert.Equal(CheckLight.Red, needed.WorstLight);

        // The context this desk publishes says the same headline: another app's things are that app's to say.
        var c = ShowContextBuilder.FromEye(g, "ab12cd34", "FOH", new DateTime(2026, 11, 12, 20, 0, 0, DateTimeKind.Utc), Array.Empty<ShowLogEntry>(), Array.Empty<ShowContextPeer>());
        Assert.Equal(g.Headline, c.Headline);

        // The menu: a card with things opens or folds its cluster on the desk; the entry is the side card's press, no wire word.
        var p = EyeLayout.Place(g);
        var menu = EyeMenus.For(new DeskFacts(), g, g.Find("app:0a1b2c3d")!, p);
        var entry = menu.Flatten().Single(e => e.Id == "eye.cluster");
        Assert.Equal((EyeMenuEdits.Fold, true, ""), (entry.Edit, entry.IsOn, entry.Wire));
        Assert.DoesNotContain(EyeMenus.For(new DeskFacts(), g, g.Find("app:a1b2c3d4")!, p).Flatten(), e => e.Id == "eye.cluster");   // nothing to draw
        Assert.DoesNotContain(EyeMenus.For(new DeskFacts(), g, g.Find("app:0a1b2c3d")!).Flatten(), e => e.Id == "eye.cluster");      // no picture, no entry
    }

    [Fact]
    public void PastTheBoundOneGreyCardSaysHowManyMoreAndTheNeededAreDrawnFirst()
    {
        var apps = Enumerable.Range(0, 140).Select(i => App($"p{i:000}", "PlayMap", $"Node {i:000}", 12, false, i == 139, "output", "projector")).ToList();
        var g = EyeGraph.Build(Room(apps));
        Assert.Equal(EyeLayout.MaxApps, g.Nodes.Count(n => n.Kind == EyeKind.ShowApp && n.Id != EyeGraph.MoreId));
        Assert.NotNull(g.Find("app:p139"));                                                        // the needed one, though it sorts last
        Assert.Null(g.Find("app:p128"));
        var more = g.Find(EyeGraph.MoreId)!;
        Assert.Equal(("+12 more apps heard", CheckLight.Grey, false), (more.Label, more.Light, more.Counted));
        Assert.Equal(12, g.MoreApps);

        var p = EyeLayout.Place(g, new EyeNetworkView { Slots = EyeLayout.Slots(g, new Dictionary<string, int>()) });
        NoOverlap(p);
        Assert.True(p.FitScale >= EyeLayout.LabelScale, $"fit {p.FitScale:0.000}");
        Assert.InRange(p.Expanded.Count, 1, EyeLayout.MaxExpanded);
        Assert.Equal(12, p.LeftApps);
        Assert.False(p.Of(EyeGraph.MoreId).IsEmpty);
        Assert.Contains("12 more heard, not drawn", p.Note, StringComparison.Ordinal);
        Assert.True(p.IsExpanded("app:p139"));                                                    // the needed app opens first
    }
}
