using System.Text;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 100 (Show Bus 1.3 §6, Posting): <c>CONTEXT POST</c>'s gate, the posted table and its readers, without a socket. The gate in
/// its order — unpaired, no PeerLink (level 0), a code's link, a key below <c>post</c>, over <c>context.bytes</c>, not a context, another
/// node's id or kind — then the table: latest wins per id, at most <c>peers.max</c> ids, kept <c>context.kept.ms</c> on an injected
/// clock and dropped by the next read; one refusal summary a minute; canaries scrubbed; and the composition: a posted context is a told
/// source on the card its key proved, never green by itself, never in place of a link this desk dials, never this desk's own thing.
/// </summary>
public class ContextPostTests
{
    private const string Messenger = "77aa77aa";
    private const string Bound = "messenger:" + Messenger;
    private static readonly DateTime Now = new(2026, 11, 16, 20, 0, 0, DateTimeKind.Utc);

    private static LinkStanding KeyLink(string scope = "post", string verified = "key")
        => new(Bound, "52f6a0b1c2d3e4f5", verified, scope, "lid0123456789ab", true) { Name = "AGM", Address = "10.0.0.20", SinceUtc = Now };

    private static string Words(string scope) => LinkAcceptor.ScopeWords(scope, "FOH");

    private static string Json(string t = "messenger", string id = Messenger, string light = "green", string headline = "12 in the room", string pad = "", string extra = "")
        => "{\"v\":1,\"node\":{\"t\":\"" + t + "\",\"id\":\"" + id + "\",\"name\":\"AGM\"},\"at\":1760101234567,\"light\":\"" + light + "\",\"headline\":\"" + headline + pad + "\","
           + "\"items\":[{\"id\":\"hall:a\",\"kind\":\"room\",\"label\":\"Hall A\",\"light\":\"" + light + "\",\"state\":\"12 in the room\"},{\"id\":\"msgs\",\"kind\":\"schedule\",\"label\":\"Messages\",\"light\":\"green\",\"state\":\"3 waiting\"}],"
           + "\"commands\":[{\"verb\":\"MSG <words>\",\"risk\":\"review\"}]" + extra + "}";

    private static ContextPost.Decision Check(string json, LinkStanding? link, string? scopeNow = "post", bool paired = true)
        => ContextPost.Check(paired, link, scopeNow, json, Words);

    [Fact]
    public void TheGateRefusesInItsOrderAndKeepsOnlyAVerifiedPostForTheNodeItsKeySpeaksFor()
    {
        var unpaired = Check(Json(), null, paired: false);
        Assert.Equal((ContextPost.Reasons.Unpaired, ControlProtocol.NotPaired), (unpaired.Reason, unpaired.Words));
        var level0 = Check(Json(), null);                                                          // a deck, a trusted loopback, an open desk
        Assert.Equal((ContextPost.Reasons.NoPeerLink, "posting a context needs PeerLink — this connection did not prove its id"), (level0.Reason, level0.Words));
        var code = Check(Json(), KeyLink("operate", "code"), "operate");
        Assert.Equal((ContextPost.Reasons.Code, ContextPost.CodeWords), (code.Reason, code.Words));
        Assert.StartsWith("a pairing code speaks for no one node", code.Words, StringComparison.Ordinal);
        var follow = Check(Json(), KeyLink("follow"), "follow");
        Assert.Equal((ContextPost.Reasons.Scope, Words("follow")), (follow.Reason, follow.Words));
        var revoked = Check(Json(), KeyLink(), scopeNow: null);                                    // a key gone from force posts nothing
        Assert.Equal((ContextPost.Reasons.KeyGone, ContextPost.KeyGoneWords), (revoked.Reason, revoked.Words));
        Assert.DoesNotContain("raise it", revoked.Words, StringComparison.Ordinal);                // a post key out of force is not told to raise its scope
        var lowered = Check(Json(), KeyLink("drive"), "follow");                                   // the scope the key stands at now
        Assert.Equal(ContextPost.Reasons.Scope, lowered.Reason);

        // §6's words for another node, byte for byte; neither id nor kind is the key's: refused, and never counted against the key.
        var other = Check(Json(id: "5e6f7a8b"), KeyLink());
        Assert.Equal((ContextPost.Reasons.SpeaksFor, "this key speaks for messenger 77aa77aa — not for messenger 5e6f7a8b"), (other.Reason, other.Words));
        var kind = Check(Json(t: "main"), KeyLink());
        Assert.Equal("this key speaks for messenger 77aa77aa — not for main 77aa77aa", kind.Words);
        var forged = Check(Json(id: "ptk_canary0123456789"), KeyLink());
        Assert.Equal("this key speaks for messenger 77aa77aa — not for another node", forged.Words);   // what it wrote is not echoed

        foreach (var bad in new[] { "", "not json", "{\"v\":1}", "{\"v\":1,\"node\":{\"t\":\"messenger\"}}", "[1,2]" })
            Assert.Equal((ContextPost.Reasons.NotAContext, ContextPost.NotAContextWords), (Check(bad, KeyLink()).Reason, Check(bad, KeyLink()).Words));

        var kept = Check(Json(), KeyLink("drive"), "drive");                                       // post and above post
        Assert.True(kept.Ok);
        Assert.Equal(ContextPost.KeptWords, kept.Words);
        Assert.Empty(kept.Context!.Commands);                                                      // a post's commands are not kept: nothing goes back
        Assert.Equal(2, kept.Context.Items.Count);
        Assert.False(AppKeyScopes.Allows("follow", ControlProtocol.Parse("CONTEXT POST " + Json())));
        Assert.True(AppKeyScopes.Allows("post", ControlProtocol.Parse("CONTEXT POST " + Json())));
    }

    [Fact]
    public void AContextOfContextBytesIsKeptAndOneByteMoreIsA413WithItsSize()
    {
        var head = Json(pad: "");
        var fits = Json(pad: new string('h', ContextPost.MaxBytes - Encoding.UTF8.GetByteCount(head)));
        Assert.Equal(ContextPost.MaxBytes, Encoding.UTF8.GetByteCount(fits));
        var taken = Check(fits, KeyLink());
        Assert.True(taken.Ok, taken.Words);
        Assert.Equal(ContextPost.MaxBytes, taken.Bytes);
        var over = Json(pad: new string('h', ContextPost.MaxBytes - Encoding.UTF8.GetByteCount(head) + 1));
        var refused = Check(over, KeyLink());
        Assert.Equal((ContextPost.Reasons.TooBig, "413 — this context is 65537 bytes; context.bytes is 65536"), (refused.Reason, refused.Words));
        // Counted in UTF-8 bytes as sent, never characters: two-byte letters past the bound are refused at half the characters.
        var wide = Json(pad: new string('é', (ContextPost.MaxBytes - Encoding.UTF8.GetByteCount(head)) / 2 + 1));
        Assert.Equal(ContextPost.Reasons.TooBig, Check(wide, KeyLink()).Reason);
    }

    [Fact]
    public void ExplicitNullsInAPostAreReadAsNothingNeverThrown()
    {
        // A sender may write null where a word belongs: no node kind or id is not a context; a null word anywhere else is read as "",
        // a thing with no id is left out, and nothing of it throws on the connection's reader task.
        foreach (var bad in new[] { "{\"v\":1,\"node\":{\"t\":null,\"id\":\"77aa77aa\"}}", "{\"v\":1,\"node\":{\"t\":\"messenger\",\"id\":null}}", "{\"v\":1,\"node\":null}" })
        {
            var d = Check(bad, KeyLink());
            Assert.Equal((ContextPost.Reasons.NotAContext, ContextPost.NotAContextWords), (d.Reason, d.Words));
        }
        var json = "{\"v\":1,\"node\":{\"t\":\"messenger\",\"id\":\"" + Messenger + "\",\"name\":null},\"headline\":null,\"light\":null,"
            + "\"items\":[{\"id\":null,\"label\":\"lost\"},{\"id\":\"msgs\",\"kind\":null,\"label\":null,\"state\":null,\"light\":null}],"
            + "\"links\":[{\"from\":\"msgs\",\"to\":\"msgs\",\"kind\":null},{\"from\":null,\"to\":\"msgs\"}],\"recent\":[{\"at\":1,\"text\":null}],"
            + "\"commands\":[{\"verb\":null},{\"verb\":\"MSG\",\"risk\":null,\"words\":null}],"
            + "\"peers\":[{\"t\":null,\"id\":null,\"name\":null,\"headline\":null,\"heard\":null,\"link\":null,\"verified\":null}]}";
        var kept = Check(json, KeyLink());
        Assert.True(kept.Ok, kept.Words);
        var c = kept.Context!;
        Assert.Equal(("", "", "grey"), (c.Node.Name, c.Headline, c.Light));
        var item = Assert.Single(c.Items);
        Assert.Equal(("msgs", "", "", "", "grey"), (item.Id, item.Kind, item.Label, item.State, item.Light));
        Assert.Equal("", Assert.Single(c.Links).Kind);
        Assert.Equal("", Assert.Single(c.Recent).Text);
        var peer = Assert.Single(c.Peers);
        Assert.Equal(("", "", "", "", "", "", ""), (peer.Kind, peer.Id, peer.Name, peer.Headline, peer.Heard, peer.Link, peer.Verified));
        // What every reader takes of it draws without a fault.
        var app = new EyeShowApp(Messenger, "AGMessage", "AGM", ShowBusState.Live, false, "10.0.0.20") { Kind = "messenger", Heard = "live", Posted = true, Context = c, Accepted = new[] { Accepted() } };
        Assert.Single(ShowBusView.StateRows(new[] { app }));
        Assert.NotEmpty(ShowBusView.BriefLines(new[] { app }));
        Assert.Contains(EyeGraph.Build(new EyeFacts { ShowBusId = "9f8e7d6c", ShowApps = new[] { app }, AppLinks = new[] { Accepted() } }).Nodes, n => n.Id == "app:" + Messenger);
    }

    [Fact]
    public void CanariesInAPostAreHeldOrLeftOutBeforeAnyReaderSeesThem()
    {
        const string key = "ptk_canary0123456789abcdefghijklmn";
        const string window = "http://10.0.0.9:9924/w/canarytoken0123456789";
        // The messenger's own capabilities: its context token and a screen's address.
        const string agn = "agn_canary0123456789abcdefgh";
        const string screen = "https://10.0.0.20:3023/d/canaryscreen0123456789abcdef";
        var json = Json(headline: "keyed " + key, extra: ",\"recent\":[{\"at\":1,\"text\":\"opened " + window + "\"},{\"at\":2,\"text\":\"token " + agn + "\"}],"
            + "\"peers\":[{\"t\":\"main\",\"id\":\"a1b2c3d4\",\"name\":\"Hall " + key + "\",\"headline\":\"" + window + "\",\"heard\":\"" + agn + "\",\"link\":\"" + screen + "\",\"verified\":\"" + key + "\"},"
            + "{\"t\":\"" + screen + "\",\"id\":\"b1b2c3d4\",\"name\":\"Lobby\"}],"
            + "\"links\":[{\"from\":\"hall:a\",\"to\":\"/w/canary\",\"kind\":\"shows\"},{\"from\":\"hall:a\",\"to\":\"msgs\",\"kind\":\"" + screen + "\"}]");
        var d = Check(json.Replace("\"label\":\"Hall A\"", "\"label\":\"Hall A on " + screen + "\"", StringComparison.Ordinal), KeyLink());
        Assert.True(d.Ok, d.Words);
        var kept = d.Context!.ToJson();
        foreach (var raw in new[] { "canary", "ptk_", "/w/", "agn_", "/d/" }) Assert.DoesNotContain(raw, kept, StringComparison.Ordinal);
        Assert.False(ContextPost.Held(kept));
        Assert.Equal(2, d.Context.Peers.Count);                                                     // a peer's words held, the peer kept
        Assert.False(EyeNetwork.Secretish(kept));
        Assert.Equal(("hall:a", "msgs", "(held — not kept)"), (d.Context.Links.Single().From, d.Context.Links.Single().To, d.Context.Links.Single().Kind));   // a link whose end would carry one is left out
    }

    [Fact]
    public void TheTableKeepsTheLatestForEachIdAtMostPeersMaxFor30SecondsAndDropsAtTheNextRead()
    {
        var table = new PostedContexts();
        var c = ShowContext.Parse(Json())!;
        var (first, kept, moved, _) = table.Keep("messenger", Messenger, c, 100, "lid1", 1_000);
        Assert.Equal((PostOutcome.First, true), (first, moved));
        // Latest wins; the same words again do not move the revision (its moment aside), changed words move it once.
        var (again, same, sameMoved, _) = table.Keep("messenger", Messenger, c with { At = 99 }, 100, "lid1", 2_000);
        Assert.Equal((PostOutcome.Replaced, false), (again, sameMoved));
        Assert.Equal(kept!.Kept.Rev, same!.Kept.Rev);
        Assert.Equal(99, Assert.Single(table.Live(2_000)).Kept.Context.At);
        var (_, changed, changedMoved, _) = table.Keep("messenger", Messenger, c with { Headline = "13 in the room" }, 100, "lid1", 3_000);
        Assert.True(changedMoved);
        Assert.NotEqual(kept.Kept.Rev, changed!.Kept.Rev);

        // Kept context.kept.ms after it came: at 29 999 ms it stands; at 30 000 ms the next read drops it, once.
        Assert.Single(table.Live(3_000 + ContextPost.KeptMs - 1));
        var (live, dropped) = table.Take(3_000 + ContextPost.KeptMs);
        Assert.Empty(live);
        Assert.Equal(Messenger, Assert.Single(dropped).Id);
        Assert.Empty(table.Take(3_000 + ContextPost.KeptMs + 1).Dropped);
        // An id dropped after context.kept.ms that posts again is a first keep again (one journal row more, not one a post).
        Assert.Equal(PostOutcome.First, table.Keep("messenger", Messenger, c, 100, "lid2", 40_000).Outcome);

        // An entry past context.kept.ms that a write for another id finds first: that write lets it go and reports it, once — the next
        // read reports nothing more.
        var other = table.Keep("messenger", "5e6f7a8b", c with { Node = c.Node with { Id = "5e6f7a8b" } }, 100, "lid3", 40_000 + ContextPost.KeptMs);
        Assert.Equal((PostOutcome.First, Messenger), (other.Outcome, Assert.Single(other.Dropped).Id));
        Assert.Empty(table.Take(40_000 + ContextPost.KeptMs).Dropped);
        Assert.Equal("5e6f7a8b", Assert.Single(table.Live(40_000 + ContextPost.KeptMs)).Id);

        // peers.max ids, each at context.bytes as sent: the 129th id is refused and nothing of it kept; the bytes held are the table's own sum.
        var flood = new PostedContexts();
        var big = ShowContext.Parse(Json(pad: new string('h', 60_000)))!;
        for (var i = 0; i < ContextPost.MaxContexts; i++)
            Assert.Equal(PostOutcome.First, flood.Keep("messenger", Id(i), big with { Node = big.Node with { Id = Id(i) } }, ContextPost.MaxBytes, "lid", 50_000 + i).Outcome);
        var full = flood.Keep("messenger", Id(ContextPost.MaxContexts), big, ContextPost.MaxBytes, "lid", 50_200);
        Assert.Equal((PostOutcome.Full, (PostedContext?)null, false), (full.Outcome, full.Kept, full.Moved));
        Assert.Empty(full.Dropped);
        Assert.Equal(ContextPost.MaxContexts, flood.Live(50_200).Count);
        Assert.Equal((long)ContextPost.MaxContexts * ContextPost.MaxBytes, flood.BytesHeld(50_200));
        Assert.Equal(PostOutcome.Replaced, flood.Keep("messenger", Id(5), big, 10, "lid", 50_201).Outcome);   // a kept id still posts
        // Once the first is context.kept.ms old a new id takes its place, and the write that lets the old one go says so.
        var after = flood.Keep("messenger", Id(ContextPost.MaxContexts), big, 10, "lid", 50_000 + ContextPost.KeptMs);
        Assert.Equal((PostOutcome.First, Id(0)), (after.Outcome, Assert.Single(after.Dropped).Id));
        Assert.True(flood.Live(50_000 + ContextPost.KeptMs).Count <= ContextPost.MaxContexts);

        static string Id(int i) => "aa" + i.ToString("x6", System.Globalization.CultureInfo.InvariantCulture);
    }

    [Fact]
    public void TheRefusalsAreSummedByReasonInOneLineAMinuteWithNoIdOrContext()
    {
        var r = new ContextPostRefusals();
        var said = new List<string>();
        void Say(string? line)
        {
            if (line is not null) said.Add(line);
        }
        var sent = 0;
        for (var ms = 0L; ms < 5 * ContextPostRefusals.SummaryMs; ms += 250)
        {
            Say(r.Refused(ms % 1000 == 0 ? ContextPost.Reasons.SpeaksFor : ContextPost.Reasons.Scope, ms));
            sent++;
        }
        Say(r.Due(10 * ContextPostRefusals.SummaryMs));
        Assert.InRange(said.Count, 1, 5);                                                          // at most one a minute, over five minutes
        var counted = said.Sum(l => int.Parse(l[..l.IndexOf(' ', StringComparison.Ordinal)], System.Globalization.CultureInfo.InvariantCulture));
        Assert.Equal(sent, counted);                                                               // every refusal summed once
        Assert.All(said, l => Assert.Contains("refused in the last minute", l, StringComparison.Ordinal));
        Assert.All(said, l => Assert.DoesNotContain(Messenger, l, StringComparison.Ordinal));
        Assert.Null(r.Due(11 * ContextPostRefusals.SummaryMs));
    }

    private static ShowBusPeer HeardMessenger(DateTime at)
        => new("messenger", Messenger, "AGM", "agm-host", "10.0.0.20", new ShowBusPorts { Http = 3023 }, Array.Empty<string>(), 3, 0, at) { Addresses = new[] { "10.0.0.20" } };

    private static AppLinkRow Accepted(bool ended = false)
        => new("lid0123456789ab", Bound, "AGM", "10.0.0.20", false, true, "key", "52f6a0b1c2d3e4f5", "post", Now) { EndedUtc = ended ? Now : null };

    private static PostedContext Posted(string light = "green", string t = "messenger")
    {
        var c = ContextPost.Scrub(ShowContext.Parse(Json(light: light))!);
        return new PostedContext(t, Messenger, KeptContext.Next(null, c), 0, 400, "lid0123456789ab");
    }

    [Fact]
    public void APostedContextIsAToldClusterOnTheCardItsKeyProvedAndNeverGreenByItself()
    {
        var feed = new ShowBusFeed(new[] { HeardMessenger(Now) }, StagewatchSight.None) { Accepted = new[] { Accepted() }, Posted = new[] { Posted() } };
        var app = Assert.Single(ShowBusView.Compose(feed, Now));
        Assert.True(app.Posted);
        Assert.Equal("12 in the room", app.Context!.Headline);
        var lit = Assert.Single(ShowBusView.Network(new[] { app }));
        Assert.Equal(CheckLight.Green, lit.Light);                                                 // its link this desk holds says it is there
        Assert.Contains(ShowBusView.PostedNote, lit.Notes);

        // The Eye: one card, its things told, the card the link's; the desk's own context carries none of them (§6 P1).
        var g = EyeGraph.Build(new EyeFacts { ShowBusId = "9f8e7d6c", ShowApps = new[] { app }, AppLinks = new[] { Accepted() } });
        var card = Assert.Single(g.Nodes, n => n.Id == "app:" + Messenger);
        Assert.Equal(EyeOrigin.Link, card.Origin);
        var things = g.Nodes.Where(n => n.Kind == EyeKind.ShowAppItem && n.App == Messenger).ToList();
        Assert.Equal(2, things.Count);
        Assert.All(things, t => Assert.Equal(EyeOrigin.Told, t.Origin));
        var own = ShowContextBuilder.FromEye(g, "9f8e7d6c", "FOH", Now, Array.Empty<ShowLogEntry>(), ShowBusView.Peers(new[] { app }), null, new[] { Accepted() });
        Assert.DoesNotContain(own.Items, i => i.Label is "Hall A" or "Messages");
        Assert.DoesNotContain(own.Commands, c => c.Verb.StartsWith(ContextPost.Verb, StringComparison.Ordinal));   // a post is a node's own act

        // Its link ended, its hello still heard: what it posted is kept 30 s, said as not current, and no better than the hello's grey.
        var ended = Assert.Single(ShowBusView.Compose(feed with { Accepted = new[] { Accepted(ended: true) } }, Now));
        var endedLit = ShowBusView.Light(ended);
        Assert.Equal(CheckLight.Grey, endedLit.Light);
        Assert.Contains(ShowBusView.PostedEndedNote, endedLit.Notes);
        Assert.StartsWith("? — " + ShowBusView.PostedEndedWhy, endedLit.Context!.Items.Single(i => i.Id == "msgs").State, StringComparison.Ordinal);
        // Its headline is not current either: every reader says why in its place, never "12 in the room".
        Assert.Equal((ShowBusView.PostedEndedHeadline, ""), (endedLit.Headline, endedLit.Own));
        Assert.Equal(ShowBusView.PostedEndedHeadline, Assert.Single(ShowBusView.StateRows(new[] { ended })).Headline);
        Assert.Equal(ShowBusView.PostedEndedHeadline, Assert.Single(ShowBusView.Peers(new[] { ended })).Headline);
        // A red it posted is no more current than its green: the hello's grey, and red only where the show needs the app and its hello is gone.
        Assert.Equal(CheckLight.Grey, ShowBusView.Light(Assert.Single(ShowBusView.Compose(feed with { Accepted = new[] { Accepted(ended: true) }, Posted = new[] { Posted("red") } }, Now))).Light);

        // A context alone draws no card: no hello, no open link.
        Assert.Empty(ShowBusView.Compose(new ShowBusFeed(Array.Empty<ShowBusPeer>(), StagewatchSight.None) { Accepted = new[] { Accepted(ended: true) }, Posted = new[] { Posted() } }, Now));
        // Another kind's claim on the id is not folded.
        Assert.False(Assert.Single(ShowBusView.Compose(feed with { Posted = new[] { Posted(t: "main") } }, Now)).Posted);
    }

    [Fact]
    public void APostedContextNeverTakesThePlaceOfALinkThisDeskDialsAndAPostAgainUnchangedMovesNoRevision()
    {
        // A link this desk dials (here a stand-in: an app linked with no context read yet): its record decides, a post is not folded.
        var linked = new EyeShowApp(Messenger, "AGMessage", "AGM", ShowBusState.Live, true, "") { Kind = "messenger" };
        Assert.False(Assert.Single(ShowBusView.Told(new[] { linked }, new[] { Posted() })).Posted);
        var device = new EyeShowApp(Messenger, "AGMessage", "AGM", ShowBusState.Live, false, "") { DeviceId = "d1" };
        Assert.False(Assert.Single(ShowBusView.Told(new[] { device }, new[] { Posted() })).Posted);

        // The apps' revision: the same words posted again leave the composition's face as it was; changed words move it once.
        var revision = new ShowBusAppsRevision();
        var feed = new ShowBusFeed(new[] { HeardMessenger(Now) }, StagewatchSight.None) { Accepted = new[] { Accepted() }, Posted = new[] { Posted() } };
        var r1 = revision.Take(ShowBusView.Compose(feed, Now)).Rev;
        var p = feed.Posted[0];
        var again = p with { Kept = KeptContext.Next(p.Kept, p.Kept.Context with { At = 5 }), AtMs = 9_000 };
        Assert.Equal(r1, revision.Take(ShowBusView.Compose(feed with { Posted = new[] { again } }, Now)).Rev);
        var moved = p with { Kept = KeptContext.Next(p.Kept, p.Kept.Context with { Headline = "14 in the room" }) };
        Assert.Equal(r1 + 1, revision.Take(ShowBusView.Compose(feed with { Posted = new[] { moved } }, Now)).Rev);
    }

    [Fact]
    public void TheSuperCheckNamesEachPostedContextWithItsAgeInGrey()
    {
        var rows = SuperCheck.Run(new CheckFacts
        {
            PostedContexts = new[] { new PostedContextFact("messenger", Messenger, "AGM", 12_400, 900) },
            PostRefusals = new Dictionary<string, int> { [ContextPost.Reasons.SpeaksFor] = 3 },
        }).Rows;
        var row = Assert.Single(rows, r => r.Item == "Posted contexts");
        Assert.Equal(("SHOW NETWORK", CheckLight.Grey, "AGMessage AGM 12 s ago"), (row.Section, row.Light, row.Value));
        Assert.Contains("3 another node's id or kind", row.Note, StringComparison.Ordinal);
        Assert.DoesNotContain(SuperCheck.Run(new CheckFacts()).Rows, r => r.Item == "Posted contexts");
    }
}
