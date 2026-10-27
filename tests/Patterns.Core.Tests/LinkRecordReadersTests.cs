using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Show Bus 1.3 §13.16.3–4 and §13.16.7, pure: the link's one record as every reader of this desk takes it — the Eye's card and
/// edge, the Nodes card, the context's peers row and the assistant's facts (<see cref="ShowBusView"/>), the context's
/// <c>link:</c> item and its row (§6 P2), a node's own context, the Super Check's "Show network links" row and the Run strip's word
/// — none deriving a link light of its own; and the journal's and the log's one line per change of state, with a repeat's summary.
/// </summary>
public sealed class LinkRecordReadersTests
{
    private const string Kid = "e63c317763b14546";
    private const string Id = "a1b2c3d4";
    private const long Wall = 1_760_000_000_000;

    private static LinkSettings Settings(bool showNeeds = true, string? nodeId = Id)
        => new() { Carrier = LinkCarrier.Ws, Key = Kid, Name = "Hall A", Kind = "main", NodeId = nodeId, Address = "127.0.0.1:9924", Local = true, ShowNeeds = showNeeds, Uses = "post" };

    /// <summary>A link to a Stagewatch on this machine with its key taken at level 0: live, flagged legacy — amber.</summary>
    private static (PeerLinkMachine Machine, LinkNow Now) LiveLegacy(bool showNeeds = true, string? nodeId = Id)
    {
        var m = new PeerLinkMachine(Settings(showNeeds, nodeId), null, new LinkNow(0, Wall), () => 0.5);
        Assert.NotNull(m.Dial(new LinkNow(0, Wall)));
        m.FirstMessage(new LinkNow(10, Wall + 10), frame: false);
        m.Frame(new LinkNow(20, Wall + 20));
        return (m, new LinkNow(30, Wall + 30));
    }

    private static EyeShowApp Linked(LinkRecord record, ShowContext? context = null)
        => new(Id, "Stagewatch", "Hall A", ShowBusState.Live, true, "127.0.0.1")
        {
            Context = context,
            Link = record,
            Heard = "live",
        };

    private static ShowContext Context(string light) => new()
    {
        Node = new ShowContextNode { Kind = "main", Id = Id, Name = "Hall A" },
        Light = light,
        Headline = "Keynote — 12:30 left",
    };

    // ---- the record's own faces -----------------------------------------------------------------------------------------

    [Fact]
    public void ALiveLevelZeroLinkIsAmberLegacyAndEachFaceOfItsRecordSaysSo()
    {
        var (m, now) = LiveLegacy();
        var r = m.Record(now);
        Assert.Equal(("live", "amber", "△", 0, "legacy", "local"), (r.State, r.Light, r.Mark, r.Level, r.Proof, r.Tls));
        Assert.Equal(new[] { "legacy" }, r.Flags);
        Assert.Equal(new[] { LinkRecord.FlagWords("legacy") }, r.FlagLines);
        Assert.Equal($"△ {r.Words} · {LinkRecord.FlagWords("legacy")}", r.Said);
        Assert.Equal(r.Said, r.Steady);                                                     // live words count nothing
        Assert.Equal("STAGEWATCH △ LIVE (legacy)", r.Glance("Stagewatch"));
        Assert.Equal(("", "none"), (r.PeerRowLink, r.PeerRowVerified));                     // nothing proved who answered
        Assert.Equal("", LinkRecord.FlagWords("a word no row names"));
        // A link LINK proved: the peers row's link and verified only while it is up; down, neither claims a proof that is not running.
        var proved = r with { Level = 1, Proof = "mutual", Kid = Kid, Flags = Array.Empty<string>() };
        Assert.Equal(("ws", "key"), (proved.PeerRowLink, proved.PeerRowVerified));
        var byCode = proved with { Kid = "code" };
        Assert.Equal(("ws", "operator"), (byCode.PeerRowLink, byCode.PeerRowVerified));
        Assert.Equal(("", "none"), ((proved with { State = "down" }).PeerRowLink, (proved with { State = "down" }).PeerRowVerified));
        // 13.16.2's advice where it gives one: an app key made on the node a code proved over plain.
        Assert.EndsWith("make an app key on Hall A", Assert.Single((r with { Flags = new[] { "code-plain" } }).FlagLines), StringComparison.Ordinal);
        Assert.StartsWith("proved, not private", LinkRecord.FlagWords("plain"), StringComparison.Ordinal);

        // Quiet past link.stale.ms: its words count seconds, its steady face does not.
        var quiet = m.Record(new LinkNow(7_000, Wall + 7_000));
        Assert.Equal(("stale", "amber"), (quiet.State, quiet.Light));
        Assert.Contains(" s", quiet.Words, StringComparison.Ordinal);
        Assert.Equal("Hall A stale", quiet.SteadyWords);
        Assert.Equal(quiet.Steady, m.Record(new LinkNow(8_000, Wall + 8_000)).Steady);

        // Off: no word on the Run strip.
        var off = PeerLinkMachine.Unlinked(new LinkSettings { Carrier = LinkCarrier.Ws, Enabled = false, Name = StageFollow.Unnamed }, new LinkNow(0, Wall));
        Assert.Equal(("off", "grey", "○", false), (off.State, off.Light, off.Mark, off.IsOn));
        Assert.Equal("", off.Glance("Stagewatch"));
    }

    [Fact]
    public void ALinkWithNothingToDialSaysOffFindingOrConnectingAndAConflictHoldsItRedOnlyWhenTheShowNeedsIt()
    {
        var t0 = new LinkNow(0, Wall);
        var finding = PeerLinkMachine.Unlinked(new LinkSettings { Carrier = LinkCarrier.Ws, Name = StageFollow.Unnamed, Kind = "main" }, t0);
        Assert.Equal(("finding", "grey", "listening for a Stagewatch on the show network"), (finding.State, finding.Light, finding.Words));
        var typed = PeerLinkMachine.Unlinked(new LinkSettings { Carrier = LinkCarrier.Ws, Name = StageFollow.Unnamed, Kind = "main", Address = "10.0.0.5:9924" }, t0);
        Assert.Equal("connecting", typed.State);
        Assert.StartsWith("dialling a Stagewatch at 10.0.0.5:9924", typed.Words, StringComparison.Ordinal);

        // §2's mark told by the connector's own hello table: held, refusal conflict, its next dial when the mark clears.
        foreach (var needs in new[] { true, false })
        {
            var held = new PeerLinkMachine(new LinkSettings { Carrier = LinkCarrier.Ws, Name = StageFollow.Unnamed, Kind = "main", NodeId = Id, ShowNeeds = needs }, null, t0, () => 0.5);
            held.Conflict(t0, 60_000);
            held.Conflict(new LinkNow(5_000, Wall + 5_000), 60_000);                       // told again: the end moves, the start stays
            var r = held.Record(new LinkNow(6_000, Wall + 6_000));
            Assert.Equal(("held", "conflict", needs ? "red" : "amber"), (r.State, r.Refusal, r.Light));
            Assert.Equal(Wall, r.Since);
            Assert.Equal(59_000, r.NextMs);
            Assert.Contains("two machines say they are " + Id, r.Words, StringComparison.Ordinal);
            Assert.Equal("finding", held.Record(new LinkNow(65_001, Wall + 65_001)).State);  // the mark cleared
        }
    }

    // ---- the desk's readers -----------------------------------------------------------------------------------------------

    [Fact]
    public void EveryReaderOfALinkedAppTakesTheRecordsLightAndNoneDerivesOneOfItsOwn()
    {
        var (m, now) = LiveLegacy();
        var r = m.Record(now);
        var app = Linked(r, Context("green"));

        // The light: a green app no better than its live link's record — amber, in every reader.
        Assert.Equal(CheckLight.Amber, ShowBusView.WithLink(app, CheckLight.Green));
        Assert.Equal(CheckLight.Red, ShowBusView.WithLink(app, CheckLight.Red));            // the app's own red stands
        Assert.Equal(CheckLight.Green, ShowBusView.WithLink(app with { Link = null }, CheckLight.Green));
        Assert.Equal(CheckLight.Green, ShowBusView.WithLink(app with { Linked = false }, CheckLight.Green));

        // The context's peers row: amber, and §6's three fields from the record at the row's end.
        var peer = Assert.Single(ShowBusView.Peers(new[] { app }));
        Assert.Equal(("amber", "live", "", "none"), (peer.Light, peer.Heard, peer.Link, peer.Verified));
        var unlinked = Assert.Single(ShowBusView.Peers(new[] { app with { Linked = false, Link = null } }));
        Assert.Equal(("green", "live", "", ""), (unlinked.Light, unlinked.Heard, unlinked.Link, unlinked.Verified));   // only a hello named it

        // The Nodes card: amber, the record's steady face last.
        var card = ShowAppCard.Of(app);
        Assert.Equal("#FFB020", card.Hue);
        Assert.EndsWith(" · link " + r.Steady, card.Line, StringComparison.Ordinal);

        // The assistant's facts: the app's line amber, then the link's own line.
        var brief = ShowBusView.BriefLines(new[] { app });
        Assert.StartsWith("Stagewatch Hall A — linked — amber: Keynote", brief[0], StringComparison.Ordinal);
        Assert.Contains($"  the link: live, amber — {r.Said}", brief);

        // The Eye: the card amber with the record among its words; the Follows edge the record's light, mark and steady words.
        var graph = EyeGraph.Build(new EyeFacts { MachineName = "FOH", ShowApps = new[] { app } });
        var node = graph.Nodes.Single(n => n.Kind == EyeKind.ShowApp);
        Assert.Equal(CheckLight.Amber, node.Light);
        Assert.Contains("the link: " + r.Steady, node.Words);
        var edge = graph.Edges.Single(e => e.From == node.Id && e.To == EyeGraph.DeskId);
        Assert.Equal((EyeEdgeKind.Follows, CheckLight.Amber, "△ " + r.Words), (edge.Kind, edge.Light, edge.Words));

        // A link held by a refused key while the show needs it: the edge red from the record; the card no worse than amber — the
        // record is not live, so the app's own light stands, and the hold is the link's to say.
        m.KeyRefused(now, "key", 60_000);
        var held = m.Record(new LinkNow(40, Wall + 40));
        Assert.Equal(("held", "red", "✕"), (held.State, held.Light, held.Mark));
        var heldGraph = EyeGraph.Build(new EyeFacts { MachineName = "FOH", ShowApps = new[] { Linked(held, Context("green")) } });
        var heldNode = heldGraph.Nodes.Single(n => n.Kind == EyeKind.ShowApp);
        Assert.Equal(CheckLight.Green, heldNode.Light);
        Assert.Equal((CheckLight.Red, "✕ Hall A held — key"), heldGraph.Edges.Where(e => e.From == heldNode.Id).Select(e => (e.Light, e.Words)).Single());
    }

    [Fact]
    public void TheEyesHashReadsTheRecordsSteadyFaceNeverItsAgesOrCounts()
    {
        var (m, _) = LiveLegacy();
        var early = m.Record(new LinkNow(30, Wall + 30));
        var later = m.Record(new LinkNow(900, Wall + 900));
        Assert.NotEqual(early.HeardMs, later.HeardMs);
        Assert.Equal(JsonUtil.SerializeCompact(Linked(early)), JsonUtil.SerializeCompact(Linked(later)));
        Assert.DoesNotContain("heardMs", JsonUtil.SerializeCompact(Linked(early)), StringComparison.Ordinal);
        var stale = m.Record(new LinkNow(7_000, Wall + 7_000));
        Assert.NotEqual(JsonUtil.SerializeCompact(Linked(early)), JsonUtil.SerializeCompact(Linked(stale)));   // a change of state moves it
    }

    [Fact]
    public void TheContextPublishesEachPeerLinkAsOneLinkItemFirstAndOneRowToItsPeer()
    {
        var (m, now) = LiveLegacy();
        var r = m.Record(now);
        var graph = EyeGraph.Build(new EyeFacts { MachineName = "FOH" });
        var at = new DateTime(2026, 10, 27, 19, 0, 0, DateTimeKind.Utc);
        var link = ShowContextPeerLink.Stagewatch(r)!;
        var c = ShowContextBuilder.FromEye(graph, "ab12cd34", "FOH", at, Array.Empty<ShowLogEntry>(), Array.Empty<ShowContextPeer>(), new[] { link });

        var item = c.Items[0];
        Assert.Equal(("link:stagewatch", "link", "Stagewatch Hall A", "amber"), (item.Id, item.Kind, item.Label, item.Light));
        Assert.Equal($"{r.Words} · scope none · not proved", item.State);
        var row = Assert.Single(c.Links, l => l.From == "link:stagewatch");
        Assert.Equal(("@" + Id, "follows", "amber"), (row.To, row.Kind, row.Light));
        Assert.Equal("amber", c.Light);                                                      // the link is one of the node's own things (P1)
        Assert.Equal(1, c.Items.Count(i => i.Kind == "link"));

        // No peer id (a typed address no hello or answer named): the item and no row a reader could not place.
        var (unnamed, at2) = LiveLegacy(nodeId: null);
        var noId = ShowContextBuilder.FromEye(graph, "ab12cd34", "FOH", at, Array.Empty<ShowLogEntry>(), Array.Empty<ShowContextPeer>(), new[] { ShowContextPeerLink.Stagewatch(unnamed.Record(at2))! });
        Assert.Contains(noId.Items, i => i.Id == "link:stagewatch");
        Assert.DoesNotContain(noId.Links, l => l.From == "link:stagewatch");

        // Off: nothing to publish.
        var off = PeerLinkMachine.Unlinked(new LinkSettings { Carrier = LinkCarrier.Ws, Enabled = false, Name = StageFollow.Unnamed }, new LinkNow(0, Wall));
        Assert.Null(ShowContextPeerLink.Stagewatch(off));

        // Held off an id two machines say (§2's mark): the item says the hold, and no row — §6 gives that id no peers row, so a row
        // would draw an edge to whichever of the two a reader shows under it.
        var marked = new PeerLinkMachine(Settings(), null, new LinkNow(0, Wall), () => 0.5);
        marked.Conflict(new LinkNow(0, Wall), 60_000);
        var held = marked.Record(new LinkNow(10, Wall + 10));
        Assert.Equal(("held", "conflict", Id), (held.State, held.Refusal, held.PeerId));
        var conflicted = ShowContextBuilder.FromEye(graph, "ab12cd34", "FOH", at, Array.Empty<ShowLogEntry>(), Array.Empty<ShowContextPeer>(), new[] { ShowContextPeerLink.Stagewatch(held)! });
        Assert.Contains(conflicted.Items, i => i.Id == "link:stagewatch" && i.Light == "red" && i.State.StartsWith(held.Words, StringComparison.Ordinal));
        Assert.DoesNotContain(conflicted.Links, l => l.From == "link:stagewatch");
        Assert.Null(ShowContextBuilder.LinkRow(ShowContextPeerLink.Stagewatch(held)!));

        // The peers row's three fields go at its end, after headline, as §6 orders them.
        var json = ShowContextBuilder.FromEye(graph, "ab12cd34", "FOH", at, Array.Empty<ShowLogEntry>(), ShowBusView.Peers(new[] { Linked(r) }), new[] { link }).ToJson();
        Assert.Contains("\"headline\":\"linked\",\"heard\":\"live\",\"link\":\"\",\"verified\":\"none\"}", json, StringComparison.Ordinal);
    }

    [Fact]
    public void ANodesOwnLinkIsAnItemAndARowOfItsContextAndTheDesksRelayIsNot()
    {
        var (m, now) = LiveLegacy();
        var r = m.Record(now);
        var sight = new StagewatchSight("Hall A", "127.0.0.1:9924", ShowBusState.Live, null, 0, null, "following Hall A");
        var at = new DateTime(2026, 10, 27, 19, 0, 0, DateTimeKind.Utc);
        var own = NodeContextBuilder.Build("n1n1n1n1", "TIMER-1", at, new NodeContextFacts { Role = "timer", Stagewatch = sight, OwnLink = r }, Array.Empty<ShowLogEntry>());
        Assert.Equal("link:stagewatch", own.Items[0].Id);
        Assert.Equal("amber", own.Items[0].Light);
        Assert.Equal("amber", own.Items.Single(i => i.Id == "stagewatch").Light);             // the live record's amber reaches the item that shows it
        Assert.Contains(own.Links, l => l.From == "link:stagewatch" && l.To == "@" + Id && l.Kind == "follows");

        // Following the desk's relay with no link of its own: the link is the desk's to publish, not this node's.
        var relay = sight with { Address = "via the desk" };
        var relayed = NodeContextBuilder.Build("n1n1n1n1", "TIMER-1", at, new NodeContextFacts { Role = "timer", Stagewatch = relay, StagewatchViaDesk = true }, Array.Empty<ShowLogEntry>());
        Assert.DoesNotContain(relayed.Items, i => i.Kind == "link");
        Assert.DoesNotContain(relayed.Links, l => l.From == "link:stagewatch");

        // Its own link on while its stage follows the relay: its link is published, and lends the relayed Stagewatch's item nothing.
        var both = NodeContextBuilder.Build("n1n1n1n1", "TIMER-1", at, new NodeContextFacts { Role = "timer", Stagewatch = relay, StagewatchViaDesk = true, OwnLink = r }, Array.Empty<ShowLogEntry>());
        Assert.Equal("link:stagewatch", both.Items[0].Id);
        Assert.Equal("green", both.Items.Single(i => i.Id == "stagewatch").Light);
    }

    [Fact]
    public void TheSuperChecksShowNetworkLinksRowIsTheRecordsLightAndWords()
    {
        static CheckRow Row(params LinkRecord[] links) => Assert.Single(SuperCheck.Run(new CheckFacts { ShowNetworkLinks = links }).Rows, r => r.Item == "Show network links");

        var none = Row();
        Assert.Equal(("SHOW NETWORK", CheckLight.Grey, "none on"), (none.Section, none.Light, none.Value));
        Assert.Equal(CheckLight.Grey, Row(PeerLinkMachine.Unlinked(new LinkSettings { Carrier = LinkCarrier.Ws, Enabled = false, Name = StageFollow.Unnamed }, new LinkNow(0, Wall))).Light);

        // An off record that says why it is off — an address that is not one — says so on the grey row.
        var offWhy = PeerLinkMachine.Unlinked(new LinkSettings { Carrier = LinkCarrier.Ws, Enabled = false, Name = StageFollow.Unnamed }, new LinkNow(0, Wall)) with { Words = "link off — the address typed is not one" };
        Assert.Equal((CheckLight.Grey, "link off — the address typed is not one"), (Row(offWhy).Light, Row(offWhy).Note));

        // Level 0: amber, and no press of a person's ends it — no FIX, what ends it instead.
        var (m, now) = LiveLegacy();
        var record = m.Record(now);
        var live = Row(record);
        Assert.Equal((CheckLight.Amber, "Stagewatch Hall A △ live"), (live.Light, live.Value));
        Assert.Equal("", record.Remedy);
        Assert.Equal("it ends once Hall A proves itself with PeerLink — nothing to press on this desk", record.Ends);
        Assert.Equal(record.Said + " — " + record.Ends, live.Note);
        Assert.DoesNotContain("FIX:", live.Note, StringComparison.Ordinal);

        // A certificate pinned at first use and its key held back: a person's Trust ends it — FIX, and the press named.
        var first = record with { Flags = new[] { "first", "keyless", "legacy" } };
        Assert.Equal("FIX: " + first.Said + " — compare its fingerprint and press Trust on the Nodes page", Row(first).Note);

        // Held by a refused key while the show needs it: red, and the key to make named.
        m.KeyRefused(now, "key", 60_000);
        var heldRecord = m.Record(new LinkNow(40, Wall + 40));
        var held = Row(heldRecord);
        Assert.Equal((CheckLight.Red, "Stagewatch Hall A ✕ held"), (held.Light, held.Value));
        Assert.Equal("FIX: " + heldRecord.Said + " — make a node key on Hall A and type it on the Nodes page", held.Note);
        Assert.Equal(CheckLight.Red, SuperCheck.Run(new CheckFacts { ShowNetworkLinks = new[] { heldRecord } }).Overall);

        // Quiet for now: amber, nothing to press — it ends when the Stagewatch is heard again.
        var (q, _) = LiveLegacy();
        var quiet = q.Record(new LinkNow(7_000, Wall + 7_000)) with { Flags = Array.Empty<string>() };
        Assert.Equal(quiet.Said + " — it ends when Hall A is heard again", Row(quiet).Note);

        // An id two machines say: the fix is another id on one of them.
        var conflict = new PeerLinkMachine(Settings(), null, new LinkNow(0, Wall), () => 0.5);
        conflict.Conflict(new LinkNow(0, Wall), 60_000);
        var conflictRecord = conflict.Record(new LinkNow(10, Wall + 10));
        Assert.Equal("give one of the two machines that say " + Id + " another id", conflictRecord.Remedy);
        Assert.StartsWith("FIX: ", Row(conflictRecord).Note, StringComparison.Ordinal);
    }

    // ---- one line per change ----------------------------------------------------------------------------------------------

    [Fact]
    public void EachChangeOfStateIsOneLineAndAChangeSaidWithinAMinuteIsSummedUpInOrder()
    {
        var log = new LinkChangeLog(summaryEvery: TimeSpan.FromSeconds(60));
        var (m, _) = LiveLegacy();
        var live = m.Record(new LinkNow(30, Wall + 30));
        var stale = m.Record(new LinkNow(7_000, Wall + 7_000));
        var down = m.Record(new LinkNow(13_000, Wall + 13_000));
        Assert.Equal("down", down.State);

        // A state that stands says nothing however often it is read; a change is one line, from → to, the reason and the attempt.
        var first = Assert.Single(log.Take(live, 1_000));
        Assert.False(first.Summary);
        Assert.Equal($"off → live: {live.Words} · attempt 0", first.Words);
        Assert.Empty(log.Take(live, 1_500));
        Assert.Empty(log.Take(live, 2_000));
        Assert.Equal($"live → stale: {stale.Words} · attempt 0", Assert.Single(log.Take(stale, 3_000)).Words);
        Assert.Equal($"stale → live: {live.Words} · attempt 0", Assert.Single(log.Take(live, 4_000)).Words);   // a change not said before

        // The same change again within a minute of saying it: a repeat, counted and not said...
        Assert.Empty(log.Take(stale, 5_000));
        Assert.Empty(log.Take(live, 6_000));
        Assert.Equal(2, log.Repeats);
        Assert.Equal("live", log.State);
        Assert.Empty(log.Take(live, 64_999));

        // ...until one summary a minute after the first repeat says how many and the last of them; each folded change counts as
        // said then, so a link that flaps all show writes one summary a minute.
        var summary = Assert.Single(log.Take(live, 65_000));
        Assert.True(summary.Summary);
        Assert.Equal("still live — Hall A: 0 attempts, 2 changes since the last line, the last stale → live: " + live.Words, summary.Words);
        Assert.Equal(0, log.Repeats);
        Assert.Empty(log.Take(stale, 66_000));
        Assert.Equal(1, log.Repeats);

        // A change not said within the minute — the link dropped — is said at once, after the summary of the repeats before it, so
        // the lines keep the changes' order and no line says the link is anywhere it has left.
        var dropped = log.Take(down, 67_000);
        Assert.Equal(2, dropped.Count);
        Assert.True(dropped[0].Summary);
        Assert.Equal(("live", "stale"), (dropped[0].From, dropped[0].Record.State));
        Assert.Equal("still stale — Hall A: 0 attempts, 1 change since the last line, the last live → stale: " + stale.Words, dropped[0].Words);
        Assert.False(dropped[1].Summary);
        Assert.Equal($"stale → down: {down.Words} · attempt 0", dropped[1].Words);
        Assert.True(dropped[1].Warn);
        Assert.Equal(0, log.Repeats);
        Assert.Empty(log.Take(down, 200_000));                                               // nothing counted, nothing to sum up

        // A change said only longer than a minute ago is said again, not counted.
        Assert.Single(log.Take(live, 300_000));
        Assert.Equal($"live → stale: {stale.Words} · attempt 0", Assert.Single(log.Take(stale, 301_000)).Words);
        Assert.Equal(0, log.Repeats);

        // A refusal names its class.
        var (h, hnow) = LiveLegacy();
        h.KeyRefused(hnow, "key", 60_000);
        var held = h.Record(new LinkNow(40, Wall + 40));
        var line = Assert.Single(new LinkChangeLog("live").Take(held, 0));
        Assert.True(line.Warn);
        Assert.Equal($"live → held: {held.Words} (key) · attempt 0", line.Words);
    }
}
