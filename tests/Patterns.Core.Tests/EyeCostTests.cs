using Patterns.Core.Menus;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The Eye's cost and marks, pure (the plan's P94.2): the facts keyed by a steady projection — other apps' contexts by revision,
/// links by their record's steady face — so a heard-again link or a context sent again unchanged rebuilds nothing; two links
/// between one pair kept apart in the graph, the menu and the drawing's geometry; marks derived from lights at every reader, a
/// replay's relighting included; and the rebuild's cost kept and written to the metrics file, governed by nothing.
/// </summary>
public sealed class EyeCostTests
{
    private const string Id = "a1b2c3d4";
    private const long Wall = 1_760_000_000_000;

    private static LinkSettings Settings()
        => new() { Carrier = LinkCarrier.Ws, Key = "e63c317763b14546", Name = "Hall A", Kind = "main", NodeId = Id, Address = "127.0.0.1:9924", Local = true, ShowNeeds = true, Uses = "post" };

    /// <summary>A live level-0 link's machine, the moment it went live.</summary>
    private static PeerLinkMachine Live()
    {
        var m = new PeerLinkMachine(Settings(), null, new LinkNow(0, Wall), () => 0.5);
        Assert.NotNull(m.Dial(new LinkNow(0, Wall)));
        m.FirstMessage(new LinkNow(10, Wall + 10), frame: false);
        m.Frame(new LinkNow(20, Wall + 20));
        return m;
    }

    private static ShowContext Context(string headline, long at = 0, IReadOnlyList<ShowContextLink>? links = null) => new()
    {
        Node = new ShowContextNode { Kind = "main", Id = Id, Name = "Hall A" },
        At = at,
        Light = "green",
        Headline = headline,
        Items = new[]
        {
            new ShowContextItem { Id = "timer", Kind = "timer", Label = "Keynote", Light = "green", State = "12:30 left" },
            new ShowContextItem { Id = "lobby", Kind = "screen", Label = "Lobby", Light = "amber", State = "not seen" },
        },
        Links = links ?? Array.Empty<ShowContextLink>(),
    };

    private static EyeFacts Facts(EyeShowApp app) => new() { MachineName = "SHOW-PC", OutputsLive = true, ShowApps = new[] { app } };

    private static EyeShowApp App(LinkRecord? link = null, ShowContext? context = null, long rev = 0) => new(Id, "Stagewatch", "Hall A", ShowBusState.Live, true, "127.0.0.1")
    {
        Link = link,
        Context = context,
        ContextRev = rev,
        Heard = "live",
        Needed = true,
    };

    // ---- the steady projection ----------------------------------------------------------------------------------------------

    /// <summary>
    /// The plan's named test. Its live legs are a regression fence (a live record's steady face never held these fields); the
    /// connecting leg is what this round changed — a redial's attempt rode in the words, and so in the key, before it.
    /// </summary>
    [Fact]
    public void ARecordWhoseHeardMsAloneMovesLeavesTheKeyAndSoThePictureAsItWas()
    {
        var m = Live();
        var r = m.Record(new LinkNow(30, Wall + 30));
        var key = Facts(App(r)).Key();
        // Heard again, a redial counted down, another attempt counted, a clock sample taken and its offset moved a few ms, the
        // counts moved: nothing the Eye draws moved, and the key is the same — the desk rebuilds nothing.
        var later = r with
        {
            HeardMs = (r.HeardMs ?? 0) + 4_000,
            NextMs = 9_000,
            Attempt = r.Attempt + 3,
            Since = r.Since + 1,
            Clock = r.Clock with { AgeMs = 12_345, OffsetMs = (r.Clock.OffsetMs ?? 0) + 3.7, ErrMs = 9, Samples = r.Clock.Samples + 5 },
            Counts = r.Counts with { Late = 4, Unconfirmed = 2 },
        };
        Assert.Equal(key, Facts(App(later)).Key());
        Assert.Equal(r.Face, later.Face);
        // What the Eye draws of it moves the key: its state, its light and mark, a flag.
        Assert.NotEqual(key, Facts(App(r with { State = "stale" })).Key());
        Assert.NotEqual(key, Facts(App(r with { Light = "red", Mark = "✕" })).Key());
        Assert.NotEqual(key, Facts(App(r with { Flags = new[] { "legacy", "apart" } })).Key());

        // A link still dialling: heard nothing, counting its redial down and its attempts up — the same key throughout.
        var dialling = new PeerLinkMachine(Settings(), null, new LinkNow(0, Wall), () => 0.5);
        Assert.NotNull(dialling.Dial(new LinkNow(0, Wall)));
        var c = dialling.Record(new LinkNow(5, Wall + 5));
        Assert.Equal("connecting", c.State);
        var redialled = c with
        {
            HeardMs = 4_000,
            NextMs = 2_500,
            Attempt = c.Attempt + 2,
            Words = System.Text.RegularExpressions.Regex.Replace(c.Words, @"\(attempt \d+\)", "(attempt 9)"),
        };
        Assert.NotEqual(c.Words, redialled.Words);
        Assert.Equal(Facts(App(c)).Key(), Facts(App(redialled)).Key());
    }

    [Fact]
    public void AConnectingRecordsAttemptIsNotInItsSteadyWordsSoARedialDoesNotRebuild()
    {
        var m = new PeerLinkMachine(Settings(), null, new LinkNow(0, Wall), () => 0.5);
        Assert.NotNull(m.Dial(new LinkNow(0, Wall)));
        var r = m.Record(new LinkNow(5, Wall + 5));
        Assert.Equal("connecting", r.State);
        Assert.Contains("(attempt 1)", r.Words, StringComparison.Ordinal);
        Assert.DoesNotContain("attempt", r.SteadyWords, StringComparison.Ordinal);
        Assert.StartsWith(r.SteadyWords, r.Words, StringComparison.Ordinal);                // the words, less the count
        var again = r with { Attempt = 4, Words = r.Words.Replace("(attempt 1)", "(attempt 4)", StringComparison.Ordinal) };
        Assert.Equal(r.Face, again.Face);
        Assert.Equal(Facts(App(r)).Key(), Facts(App(again)).Key());
    }

    [Fact]
    public void AContextIsReadByItsRevisionNeverSerialisedAndAnUnrevisedOneStillMovesThePicture()
    {
        var one = Context("Keynote — 12:30 left");
        var two = Context("Keynote — 12:29 left");
        // The same revision: the key reads the revision alone, so contexts that differ are not even looked at.
        Assert.Equal(Facts(App(context: one, rev: 7)).Key(), Facts(App(context: two, rev: 7)).Key());
        Assert.DoesNotContain("12:30", Facts(App(context: one, rev: 7)).Key(), StringComparison.Ordinal);
        // A new revision moves it.
        Assert.NotEqual(Facts(App(context: one, rev: 7)).Key(), Facts(App(context: one, rev: 8)).Key());
        // A context handed in without a revision (an app built by hand) is read whole, so a picture is never stale for want of one.
        Assert.NotEqual(Facts(App(context: one)).Key(), Facts(App(context: two)).Key());
        Assert.Equal("", App(context: one, rev: 7).ContextUnrevised);
    }

    [Fact]
    public void AKeptContextsRevisionMovesWhenWhatItSaysMovesAndNeverForItsMoment()
    {
        var first = KeptContext.Next(null, Context("Keynote — 12:30 left", at: 1_000));
        var resent = KeptContext.Next(first, Context("Keynote — 12:30 left", at: 2_000));
        Assert.Equal(first.Rev, resent.Rev);                                                 // only its moment moved
        Assert.Equal(2_000, resent.Context.At);                                              // the newest is kept
        var moved = KeptContext.Next(resent, Context("Keynote — 12:29 left", at: 3_000));
        Assert.True(moved.Rev > resent.Rev);
        // Two sources — or one dialled again after a held key cleared it — never share a revision, whatever they say.
        var other = KeptContext.Next(null, Context("Keynote — 12:29 left", at: 3_000));
        Assert.NotEqual(moved.Rev, other.Rev);
        // Through the view every reader takes: the sight's revision reaches the app, and the node view carries its own.
        var sight = new StagewatchSight("Hall A", "127.0.0.1:9924", ShowBusState.Live, null, 0, moved.Context, "") { ContextRev = moved.Rev };
        var app = Assert.Single(ShowBusView.Apps(Array.Empty<ShowBusPeer>(), DateTime.UtcNow, sight));
        Assert.Equal(moved.Rev, app.ContextRev);
        var node = Assert.Single(ShowBusView.Nodes(new[] { ("Timer 1", moved, DateTime.UtcNow) }, DateTime.UtcNow));
        Assert.Equal(moved.Rev, node.ContextRev);
        Assert.Equal(0, node.Context!.At);
    }

    // ---- parallel links ------------------------------------------------------------------------------------------------------

    private static EyeGraph TwoLinksBetweenOnePair()
    {
        var links = new[]
        {
            new ShowContextLink { From = "timer", To = "lobby", Kind = "feeds", Light = "green" },
            new ShowContextLink { From = "lobby", To = "timer", Kind = "hears", Light = "amber" },
        };
        return EyeGraph.Build(Facts(App(context: Context("Keynote", links: links), rev: 1)));
    }

    [Fact]
    public void TwoLinksBetweenOnePairAreTwoEdgesOneNeighbourAndTwoMenuEntriesEachItsOwnId()
    {
        var g = TwoLinksBetweenOnePair();
        const string Timer = "app:" + Id + ":timer";
        const string Lobby = "app:" + Id + ":lobby";
        Assert.Equal(2, g.EdgesBetween(Timer, Lobby).Count);
        Assert.Equal(g.EdgesBetween(Timer, Lobby), g.EdgesBetween(Lobby, Timer));
        Assert.Equal(new[] { Lobby }, g.Neighbours(Timer).ToArray());                      // a neighbour once
        Assert.Equal(g.Neighbours(Timer).Count, g.Neighbours(Timer).Distinct().Count());
        Assert.Equal(1, g.Hops(Timer)[Lobby]);
        // Each link's lane among its pair's, kept with the picture: the canvas reads it, never counts the pair per frame.
        var lanes = g.Edges.Select((e, i) => (e, i)).Where(x => (x.e.From, x.e.To) is (Timer, Lobby) or (Lobby, Timer)).Select(x => g.Lane(x.i)).ToArray();
        Assert.Equal(new[] { (0, 2), (1, 2) }, lanes);
        Assert.All(g.Edges.Select((e, i) => (e, i)).Where(x => g.EdgesBetween(x.e.From, x.e.To).Count == 1), x => Assert.Equal((0, 1), g.Lane(x.i)));

        var menu = EyeMenus.For(new DeskFacts(), g, g.Find(Timer)!);
        var contacts = menu.Flatten().Where(e => e.Id.StartsWith("eye.contact:", StringComparison.Ordinal)).ToList();
        Assert.Equal(new[] { "eye.contact:" + Lobby, "eye.contact:" + Lobby + "#2" }, contacts.Select(e => e.Id).ToArray());
        Assert.StartsWith("✓ feeds it", contacts[0].Detail, StringComparison.Ordinal);       // each with its own verb and mark
        Assert.StartsWith("△ is heard by this", contacts[1].Detail, StringComparison.Ordinal);
        Assert.All(contacts, e => Assert.Equal("EYE FOCUS " + Lobby, e.Wire));
        var ids = menu.Flatten().Select(e => e.Id).ToList();
        Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count());
    }

    [Fact]
    public void ParallelSegmentsSitApartOnOneSideEachAndAViewCullsOnlyWhatItMisses()
    {
        Assert.Equal(0, EyeGeometry.Offset(0, 1));
        Assert.Equal(new[] { -EyeGeometry.ParallelGap / 2, EyeGeometry.ParallelGap / 2 }, new[] { EyeGeometry.Offset(0, 2), EyeGeometry.Offset(1, 2) });
        Assert.Equal(new[] { -EyeGeometry.ParallelGap, 0, EyeGeometry.ParallelGap }, Enumerable.Range(0, 3).Select(i => EyeGeometry.Offset(i, 3)).ToArray());
        // A→B and B→A shifted by the same offset land on the same side of the line: their segments are one segment.
        var ab = EyeGeometry.Shift(0, 0, 100, 0, 5);
        var ba = EyeGeometry.Shift(100, 0, 0, 0, 5);
        Assert.Equal((ab.Y1, ab.Y2), (ba.Y1, ba.Y2));
        Assert.NotEqual(0, ab.Y1);
        // Culling: a segment wholly outside misses; one crossing the view with both ends outside meets it; one inside meets it.
        var view = new EyeRect(0, 0, 100, 100);
        Assert.False(EyeGeometry.Meets(200, 0, 300, 50, view));
        Assert.False(EyeGeometry.Meets(-50, 120, 150, 130, view));
        Assert.True(EyeGeometry.Meets(-50, 50, 150, 50, view));
        Assert.True(EyeGeometry.Meets(-10, -10, 110, 110, view));
        Assert.True(EyeGeometry.Meets(10, 10, 20, 20, view));
        Assert.False(EyeGeometry.Meets(-30, 10, 10, -30, view));                            // passes the corner outside
    }

    // ---- marks -------------------------------------------------------------------------------------------------------------

    [Fact]
    public void MarksAreSixShapesDerivedFromLightWithIOnlyForAnInformationGrey()
    {
        Assert.Equal(6, Enum.GetValues<EyeMark>().Select(EyeMarks.Glyph).Distinct().Count());
        Assert.Equal(("✓", "△", "✕", "○"), (EyeMarks.Glyph(CheckLight.Green), EyeMarks.Glyph(CheckLight.Amber), EyeMarks.Glyph(CheckLight.Red), EyeMarks.Glyph(CheckLight.Grey)));
        Assert.Equal("i", EyeMarks.Glyph(CheckLight.Grey, info: true));
        Assert.Equal("✕", EyeMarks.Glyph(CheckLight.Red, info: true));                     // a lit information thing wears its light
        // The same glyphs a link's record writes.
        foreach (var light in new[] { CheckLight.Green, CheckLight.Amber, CheckLight.Red, CheckLight.Grey }) Assert.Equal(LinkRecord.MarkOf(light), EyeMarks.Glyph(light));

        var g = EyeGraph.Build(new EyeFacts { MachineName = "SHOW-PC", Companions = new[] { new EyeCompanion("cmp", "10.0.0.9", true, "4.0") } });
        var companion = g.Find("companion:cmp")!;
        Assert.Equal(EyeMark.Info, companion.Mark);
        Assert.Equal(EyeMark.Fault, (companion with { Light = CheckLight.Red }).Mark);       // derived: relit, its mark follows
        Assert.All(g.Edges, e => Assert.Equal(EyeMarks.Of(e.Light), e.Mark));
        // A Companion gone quiet is a gap, not information: a ring.
        var quiet = EyeGraph.Build(new EyeFacts { MachineName = "SHOW-PC", Companions = new[] { new EyeCompanion("cmp", "10.0.0.9", false, "4.0") } });
        Assert.Equal(EyeMark.Off, quiet.Find("companion:cmp")!.Mark);
        // EYE JSON carries each mark at the end of its row, and the worst's.
        var json = EyeJson.Write(g, EyePlacement.Empty, null, EyeLens.All);
        Assert.Contains("\"mark\":\"i\"", json, StringComparison.Ordinal);
        Assert.Contains("\"worstMark\":", json, StringComparison.Ordinal);
    }

    [Fact]
    public void AReplaysRelightingLeavesNoMarkOfNowOnAPictureOfThen()
    {
        var g = EyeGraph.Build(new EyeFacts { MachineName = "SHOW-PC", Companions = new[] { new EyeCompanion("cmp", "10.0.0.9", true, "4.0") } });
        Assert.Equal(EyeMark.Info, g.Find("companion:cmp")!.Mark);
        var at = new DateTime(2026, 11, 12, 20, 0, 0, DateTimeKind.Utc);
        var rows = new[] { new ShowLogEntry(at.AddSeconds(-5), "Desk", "SCREEN", "companion:cmp", "Failed", "refused") };
        var moment = EyeReplay.At(ReplayRecord.From(rows, Array.Empty<MetricSample>()), at);
        var replayed = EyeReplay.Apply(g, moment);
        Assert.Equal(EyeMark.Fault, replayed.Find("companion:cmp")!.Mark);
        // A thing the record says nothing of is "no record" grey: a ring, not the i it wore now.
        var quiet = EyeReplay.Apply(g, EyeReplay.At(ReplayRecord.From(Array.Empty<ShowLogEntry>(), Array.Empty<MetricSample>()), at));
        Assert.Equal(EyeMark.Off, quiet.Find("companion:cmp")!.Mark);
        Assert.All(replayed.Edges, e => Assert.Equal(EyeMark.Off, e.Mark));                 // the record holds things, not links
    }

    [Fact]
    public void TheNodesCardCarriesTheEyeCardsMark()
    {
        var m = Live();
        var r = m.Record(new LinkNow(30, Wall + 30));
        var app = App(r, Context("Keynote"), rev: 1);
        Assert.Equal(EyeMarks.Glyph(ShowBusView.CardLight(app)), ShowAppCard.Of(app).Mark);
        Assert.Equal(EyeGraph.Build(Facts(app)).Find("app:" + Id)!.Mark, EyeMarks.Of(ShowBusView.CardLight(app)));
        Assert.Equal("○", ShowAppCard.Of(new EyeShowApp("e5f6a7b8", "AGMessage", "Messenger", ShowBusState.Live, false, "")).Mark);
        Assert.Equal("✕", ShowAppCard.Of(new EyeShowApp("e5f6a7b8", "AGMessage", "Messenger", ShowBusState.Gone, false, "")).Mark);

        // A linked, live app whose own context says red over a healthy link (no record of its own: the follow link is lit by its
        // state, live green): both cards red, mark and colour alike.
        var red = App(context: Context("Keynote") with { Light = "red" }, rev: 2);
        Assert.Equal(CheckLight.Green, EyeGraph.Build(Facts(red)).EdgeBetween("app:" + Id, EyeGraph.DeskId)!.Light);
        Assert.Equal(CheckLight.Red, ShowBusView.CardLight(red));
        Assert.Equal(("✕", "#B03030"), (ShowAppCard.Of(red).Mark, ShowAppCard.Of(red).Hue));
        Assert.Equal(EyeMark.Fault, EyeGraph.Build(Facts(red)).Find("app:" + Id)!.Mark);
        Assert.Equal(("△", "#FFB020"), (ShowAppCard.Of(App(context: Context("Keynote") with { Light = "amber" }, rev: 3)).Mark, ShowAppCard.Of(App(context: Context("Keynote") with { Light = "amber" }, rev: 3)).Hue));
        // Green context, green link: the kind's own colour beside ✓.
        var green = App(context: Context("Keynote"), rev: 4);
        Assert.Equal(("✓", "#35E0D0"), (ShowAppCard.Of(green).Mark, ShowAppCard.Of(green).Hue));
        // A live level-0 record darker than its green context: amber on both cards.
        Assert.Equal(CheckLight.Amber, r.LightValue);
        Assert.Equal(("△", "#FFB020"), (ShowAppCard.Of(app).Mark, ShowAppCard.Of(app).Hue));

        // The desk's LINKED TO entry for the link says its mark once, first — never again where the record's words begin.
        var g = EyeGraph.Build(Facts(app));
        var entry = EyeMenus.For(new DeskFacts(), g, g.Find(EyeGraph.DeskId)!).Find("eye.contact:app:" + Id)!;
        Assert.StartsWith("△ follows this · ", entry.Detail, StringComparison.Ordinal);
        Assert.StartsWith(r.Mark + " ", g.EdgeBetween("app:" + Id, EyeGraph.DeskId)!.Words, StringComparison.Ordinal);   // the record's words do begin with it
        Assert.Equal(1, entry.Detail.Split('△').Length - 1);
    }

    // ---- the rebuild's cost --------------------------------------------------------------------------------------------------

    [Fact]
    public void TheRebuildsCostIsKeptBoundedAndReadOverTheLastMinute()
    {
        var cost = new EyeCost();
        var t0 = new DateTime(2026, 11, 12, 20, 0, 0, DateTimeKind.Utc);
        Assert.Equal((0, -1.0, -1), cost.Read(t0));
        cost.Record(new EyeBuild(t0, 0.8, 40, 52));
        cost.Record(new EyeBuild(t0.AddSeconds(30), 2.5, 41, 53));
        cost.Record(new EyeBuild(t0.AddSeconds(50), 1.2, 44, 57));
        Assert.Equal((3, 2.5, 44), cost.Read(t0.AddSeconds(55)));
        Assert.Equal((2, 2.5, 44), cost.Read(t0.AddSeconds(61)));                          // the first has left the minute
        Assert.Equal((0, -1.0, 44), cost.Read(t0.AddMinutes(5)));                          // no rebuild, the picture's size still said
        for (var i = 0; i < EyeCost.Kept + 40; i++) cost.Record(new EyeBuild(t0.AddMinutes(10), 0.1, 44, 57));
        Assert.Equal(EyeCost.Kept, cost.Read(t0.AddMinutes(10)).Builds);                   // bounded
        Assert.Equal(3 + EyeCost.Kept + 40, cost.Total);
    }

    [Fact]
    public void TheMetricsFileCarriesTheRebuildAtTheEndOfItsRowAndReadsItBack()
    {
        Assert.EndsWith(",poolStarved,eyeBuildWorstMs,eyeBuilds,eyeNodes", MetricsCsv.Header, StringComparison.Ordinal);
        var t = new DateTime(2026, 11, 12, 20, 0, 0, DateTimeKind.Utc);
        var line = MetricsCsv.Line(new MetricSample { Utc = t, EyeBuildWorstMs = 1.234, EyeBuilds = 7, EyeNodes = 48 });
        Assert.EndsWith(",1.23,7,48", line, StringComparison.Ordinal);
        var back = Assert.Single(MetricsCsv.Parse(new[] { MetricsCsv.Header, line }));
        Assert.Equal((1.23, 7, 48), (back.EyeBuildWorstMs, back.EyeBuilds, back.EyeNodes));
        // None measured: blank, and read back as not measured.
        var none = MetricsCsv.Line(new MetricSample { Utc = t });
        Assert.EndsWith(",,0,", none, StringComparison.Ordinal);
        var nothing = Assert.Single(MetricsCsv.Parse(new[] { MetricsCsv.Header, none }));
        Assert.Equal((-1.0, 0, -1), (nothing.EyeBuildWorstMs, nothing.EyeBuilds, nothing.EyeNodes));
    }
}
