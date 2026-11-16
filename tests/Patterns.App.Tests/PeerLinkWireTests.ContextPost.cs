using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Menus;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 100 (Show Bus 1.3 §6, Posting): <c>CONTEXT POST</c> on this desk's wire, 9697, from in-process diallers on real sockets that
/// run LINK as a connector would, from loopback with this machine's trust off (restored by the desk's Dispose). A post on a link a
/// <c>post</c> key verified is kept, the latest for its id, and read by every reader; every other door and shape is refused in its own
/// words and none of them counted against a key; canaries in a post reach no log, journal, STATE or this desk's own context; and a
/// flood at the wire's caps keeps the table, the journal and the desk's thread bounded.
/// </summary>
public sealed partial class PeerLinkWireTests
{
    private const string PosterId = "77aa77aa";
    private const string Poster = "messenger:" + PosterId;

    /// <summary>A context of §6's shape for <paramref name="t"/>:<paramref name="id"/>, padded in its headline to <paramref name="pad"/> characters more.</summary>
    private static string PostJson(string t = "messenger", string id = PosterId, string headline = "12 in the room", int pad = 0)
        => "{\"v\":1,\"node\":{\"t\":\"" + t + "\",\"id\":\"" + id + "\",\"name\":\"AGM\"},\"at\":1760101234567,\"light\":\"green\",\"headline\":\"" + headline + new string('h', pad) + "\","
           + "\"items\":[{\"id\":\"hall:a\",\"kind\":\"room\",\"label\":\"Hall A room\",\"light\":\"green\",\"state\":\"12 in the room\"}]}";

    /// <summary>
    /// LINK as a connector runs it on <c>line</c>, from <paramref name="from"/>: the ask with the key's kid, the proof over the desk's
    /// offer, the verdict, the greeting's STATE. The connection, verified at the key's scope.
    /// </summary>
    private static async Task<Lines> LinkAs(int port, LinkCredential cred, string client, IPAddress? from = null)
    {
        var lines = from is null ? await Lines.Dial(port, tls: false) : await Lines.DialFrom(port, from);
        var heard = new List<string>();
        async Task<string> Until(Func<string, bool> match)
        {
            try
            {
                for (;;)
                {
                    var line = await lines.Next();
                    heard.Add(line.Length > 80 ? line[..80] + "…" : line);
                    if (match(line)) return line;
                }
            }
            catch (Exception ex) when (ex is OperationCanceledException or IOException)
            {
                throw new InvalidOperationException($"LINK as {client} from {from?.ToString() ?? "127.0.0.1"} heard only: " + string.Join(" | ", heard), ex);
            }
        }
        var at = client.IndexOf(':', StringComparison.Ordinal);
        var cnonce = Nonce();
        await lines.Send($"@l1 LINK {PeerLink.Version} {client[..at]} {client[(at + 1)..]} {cnonce} {cred.Kid}");
        var asked = await Until(l => l.StartsWith("OK @l1 ", StringComparison.Ordinal) || l.StartsWith("ERR @l1", StringComparison.Ordinal));
        var offer = PeerLink.ReadOfferLine(asked) ?? throw new InvalidOperationException("no offer: " + asked);
        var t = PeerLink.Transcript("line", PeerLink.Version, offer.Speaks, offer.T + ":" + offer.Id, client, cred.Kid, offer.SNonce, cnonce, PeerLink.Bind(null))!;
        await lines.Send("@l2 LINK PROOF " + PeerLink.ClientProof(cred.K, t));
        var verdict = await Until(l => l.StartsWith("OK @l2 ", StringComparison.Ordinal) || l.StartsWith("ERR @l2", StringComparison.Ordinal));
        if (!verdict.StartsWith("OK @l2 paired ", StringComparison.Ordinal)) throw new InvalidOperationException("LINK refused: " + verdict);
        await Until(l => l.StartsWith("STATE {", StringComparison.Ordinal));
        return lines;
    }

    /// <summary>One line and the answer that names its id.</summary>
    private static string Ask(Lines lines, string rid, string line)
    {
        TestApp.Pump(lines.Send("@" + rid + " " + line).ContinueWith(_ => true, TaskScheduler.Default));
        return TestApp.Pump(lines.NextMatching(l => l.StartsWith("OK @" + rid + " ", StringComparison.Ordinal) || l.StartsWith("ERR @" + rid + " ", StringComparison.Ordinal)
            || l == "OK @" + rid || l.StartsWith("ERR ", StringComparison.Ordinal) && l.Contains("closed", StringComparison.Ordinal)));
    }

    private static int CountLines(string file, string text) => File.Exists(file) ? File.ReadAllLines(file).Count(l => l.Contains(text, StringComparison.Ordinal)) : 0;

    [AvaloniaFact]
    public void AVerifiedPostIsKeptLatestWinsForThirtySecondsAndEveryOtherDoorIsRefusedInItsWordsUncounted()
    {
        using var desk = new AcceptorDesk();
        var machine = desk.Services.Kernel.Beacon.MachineName;
        var clock = 1_000_000L;
        desk.Control.PostedNowMs = () => clock;
        var postKey = MakeKey(desk, Poster, "post");
        var followKey = MakeKey(desk, "messenger:66bb66bb", "follow");
        const string keyCanary = "ptk_canary0123456789abcdefghijklmn";
        const string windowCanary = "http://10.0.0.9:9924/w/canarytoken0123456789";

        using var poster = TestApp.Pump(LinkAs(desk.Port, LinkAcceptor.Key(postKey, Poster, "post")!, Poster));
        // Verified at post: kept, one journal row on the first keep, the app's card told by it.
        Assert.Equal("OK @p1 kept", Ask(poster, "p1", "CONTEXT POST " + PostJson()));
        var kept = Assert.Single(desk.Control.Posted);
        Assert.Equal(("messenger", PosterId, "12 in the room"), (kept.T, kept.Id, kept.Kept.Context.Headline));
        var app = Assert.Single(desk.Services.Apps(DateTime.UtcNow), a => a.Id == PosterId);
        Assert.True(app.Posted);
        Assert.Equal(CheckLight.Green, ShowBusView.Light(app).Light);                              // its link LINK verified says it is there
        var row = Assert.Single(Rows(desk.Services, "ContextPost"));
        Assert.Equal(Poster, row.Target);
        Assert.Contains("digest " + kept.Kept.Digest[..8].ToLowerInvariant(), row.Message, StringComparison.Ordinal);
        Assert.DoesNotContain("12 in the room", row.Message, StringComparison.Ordinal);            // no text of it
        // STATE's bus.apps row says what it says of itself came by CONTEXT POST, at the row's end.
        using (var early = JsonDocument.Parse(new CommandRouter(desk.Services).StateJson()))
            Assert.True(early.RootElement.GetProperty("bus").GetProperty("apps").EnumerateArray().Single(a => a.GetProperty("id").GetString() == PosterId).GetProperty("posted").GetBoolean());
        // The Eye draws its things as told, on the card its key proved.
        desk.Services.Eye.Refresh();
        Assert.Contains(desk.Services.Eye.Graph.Nodes, n => n.App == PosterId && n.Kind == EyeKind.ShowAppItem && n.Origin == EyeOrigin.Told);

        // The same words again: kept, no second row, the apps' revision unmoved (the Eye's gather rebuilds nothing); changed words: latest wins.
        var rev = desk.Services.AppsWithRev(DateTime.UtcNow).Rev;
        Assert.Equal("OK @p2 kept", Ask(poster, "p2", "CONTEXT POST " + PostJson().Replace("1760101234567", "1760101299999", StringComparison.Ordinal)));
        Assert.Equal(rev, desk.Services.AppsWithRev(DateTime.UtcNow).Rev);
        Assert.Equal("OK @p3 kept", Ask(poster, "p3", "CONTEXT POST " + PostJson(headline: "13 in the room")));
        Assert.Equal("13 in the room", Assert.Single(desk.Control.Posted).Kept.Context.Headline);
        Assert.Single(Rows(desk.Services, "ContextPost"));

        // Another id or kind than the key's: refused in §6's words, uncounted — the link stays and posts again.
        Assert.Equal("ERR @p4 this key speaks for messenger 77aa77aa — not for messenger 5e6f7a8b", Ask(poster, "p4", "CONTEXT POST " + PostJson(id: "5e6f7a8b")));
        Assert.Equal("ERR @p5 this key speaks for messenger 77aa77aa — not for main 77aa77aa", Ask(poster, "p5", "CONTEXT POST " + PostJson(t: "main")));
        Assert.Equal("ERR @p6 " + ContextPost.NotAContextWords, Ask(poster, "p6", "CONTEXT POST {\"v\":1}"));
        // Explicit nulls: no node kind is not a context; a thing with a null id is left out and the rest kept — nothing of either throws
        // on the reader task, and the link stays up.
        Assert.Equal("ERR @n1 " + ContextPost.NotAContextWords, Ask(poster, "n1", "CONTEXT POST {\"v\":1,\"node\":{\"t\":null,\"id\":\"" + PosterId + "\"}}"));
        Assert.Equal("OK @n2 kept", Ask(poster, "n2", "CONTEXT POST " + PostJson(headline: "13 in the room").Replace("\"id\":\"hall:a\"", "\"id\":null", StringComparison.Ordinal)));
        Assert.Empty(Assert.Single(desk.Control.Posted).Kept.Context.Items);
        Assert.Equal("OK @p7 kept", Ask(poster, "p7", "CONTEXT POST " + PostJson(headline: "13 in the room")));
        Assert.Single(desk.Control.Posted);

        // Canaries in a post — an app key, a window's address — reach no log, journal, STATE or this desk's own context.
        Assert.Equal("OK @p8 kept", Ask(poster, "p8", "CONTEXT POST " + PostJson(headline: "keyed " + keyCanary + " at " + windowCanary)));
        // Swept while its link is open and its card is drawn, so every reader of the card has the post in hand: the log, the journal,
        // STATE (its bus.apps row says the held words), this desk's context (its peers row), the Eye's card and things, the brief.
        TestApp.FlushFiles(desk.Services);
        desk.Services.Eye.Refresh();
        var openApps = desk.Services.Apps(DateTime.UtcNow);
        Assert.Contains(openApps, a => a.Id == PosterId && a.Posted);
        var openState = new CommandRouter(desk.Services).StateJson();
        using (var drawn = JsonDocument.Parse(openState))
            Assert.Equal("(held — not kept)", drawn.RootElement.GetProperty("bus").GetProperty("apps").EnumerateArray().Single(a => a.GetProperty("id").GetString() == PosterId).GetProperty("headline").GetString());
        var openContext = desk.Services.ContextJson();
        Assert.Contains(PosterId, openContext, StringComparison.Ordinal);                                   // its peers row is there to be swept
        var eyeWords = desk.Services.Eye.Graph.Nodes.Where(n => n.App == PosterId || n.Id == "app:" + PosterId).SelectMany(n => new[] { n.Label, n.Sub }.Concat(n.Words)).ToList();
        Assert.NotEmpty(eyeWords);
        var openReaders = new[]
        {
            File.ReadAllText(Path.Combine(desk.Booted.Dir, "patterns.log")), Journal(desk.Services), openState, openContext,
            string.Join("\n", eyeWords), string.Join("\n", ShowBusView.BriefLines(openApps)),
        };
        foreach (var canary in new[] { keyCanary, windowCanary, "canarytoken", "ptk_canary", postKey })
            Assert.All(openReaders, r => Assert.DoesNotContain(canary, r, StringComparison.Ordinal));

        // A follow key: its scope's words. A deck paired by the legacy proof (level 0) and an unpaired connection: the wire's words.
        using (var follower = TestApp.Pump(LinkAs(desk.Port, LinkAcceptor.Key(followKey, "messenger:66bb66bb", "follow")!, "messenger:66bb66bb")))
            Assert.Equal("ERR @f1 " + LinkAcceptor.ScopeWords("follow", machine), Ask(follower, "f1", "CONTEXT POST " + PostJson(id: "66bb66bb")));
        using (var deck = TestApp.Pump(Lines.Dial(desk.Port, tls: false)))
        {
            // The wire tells TLS from plain by the first byte: a deck speaks first, then hears its nonce.
            TestApp.Pump(deck.Send("HELLO AGMessage").ContinueWith(_ => true, TaskScheduler.Default));
            var nonce = TestApp.Pump(deck.NextMatching(l => l.StartsWith("NONCE ", StringComparison.Ordinal)))["NONCE ".Length..];
            Assert.Equal("ERR @u1 " + ControlProtocol.NotPaired, Ask(deck, "u1", "CONTEXT POST " + PostJson()));
            Assert.Equal("ERR @u2 " + ControlProtocol.StateNotPaired, Ask(deck, "u2", "STATUS"));     // STATE's posted fields reach paired connections only
            TestApp.Pump(deck.Send("AUTH-PROOF " + WireProof.Proof(desk.Token, nonce, "AGMessage")).ContinueWith(_ => true, TaskScheduler.Default));
            TestApp.Pump(deck.NextMatching(l => l.StartsWith("OK paired", StringComparison.Ordinal)));
            Assert.Equal("ERR @d1 " + ContextPost.NoPeerLinkWords, Ask(deck, "d1", "CONTEXT POST " + PostJson()));
        }
        // A link the pairing code proved speaks for no one node.
        using (var coded = TestApp.Pump(LinkAs(desk.Port, LinkAcceptor.Code(desk.Token)!, "main:a1b2c3d4")))
            Assert.Equal("ERR @c1 " + ContextPost.CodeWords, Ask(coded, "c1", "CONTEXT POST " + PostJson(t: "main", id: "a1b2c3d4")));
        // Every door but the wire's port refuses it, the JSON never echoed: the router (the web, OSC, a running order, an assistant).
        var routed = TestApp.Pump(new CommandRouter(desk.Services).ExecuteAsync(ControlProtocol.Parse("CONTEXT POST " + PostJson(id: "5e6f7a8b"))));
        Assert.Equal("ERR " + CommandRouter.ContextPostElsewhere, routed);
        Assert.Single(desk.Control.Posted);

        // A line of 64 KiB + 1 is no line of the wire: closed in its words, nothing of it kept.
        var head = "@big CONTEXT POST ";
        var json = PostJson(id: PosterId, headline: "big");
        var oversize = head + PostJson(id: PosterId, headline: "big", pad: WireLimits.CommandBytes + 1 - Encoding.UTF8.GetByteCount(head + json));
        Assert.Equal(WireLimits.CommandBytes + 1, Encoding.UTF8.GetByteCount(oversize));
        TestApp.Pump(poster.Send(oversize).ContinueWith(_ => true, TaskScheduler.Default));
        Assert.EndsWith("closed", TestApp.Pump(poster.NextMatching(l => l.StartsWith("ERR ", StringComparison.Ordinal))), StringComparison.Ordinal);
        Assert.Equal("(held — not kept)", Assert.Single(desk.Control.Posted).Kept.Context.Headline);   // the canary post, said as held

        // The readers, canaries first: none in the log, the journal, STATE or the context; STATE's bus row names the post by id and age.
        TestApp.FlushFiles(desk.Services);
        var state = new CommandRouter(desk.Services).StateJson();
        var readers = new[] { File.ReadAllText(Path.Combine(desk.Booted.Dir, "patterns.log")), Journal(desk.Services), state, desk.Services.ContextJson() };
        foreach (var canary in new[] { keyCanary, windowCanary, "canarytoken", postKey, followKey })
            Assert.All(readers, r => Assert.DoesNotContain(canary, r, StringComparison.Ordinal));
        Assert.DoesNotContain("NullReferenceException", readers[0], StringComparison.Ordinal);            // the null posts faulted nothing
        using (var doc = JsonDocument.Parse(state))
        {
            var bus = doc.RootElement.GetProperty("bus");
            var posted = Assert.Single(bus.GetProperty("posted").EnumerateArray());
            Assert.Equal(("messenger", PosterId), (posted.GetProperty("kind").GetString(), posted.GetProperty("id").GetString()));
            // Its link ended at the oversize line and no hello names it: what it posted is kept, and no card is drawn for it alone.
            Assert.DoesNotContain(bus.GetProperty("apps").EnumerateArray(), a => a.GetProperty("id").GetString() == PosterId);
        }
        Assert.DoesNotContain("Hall A room", desk.Services.ContextJson(), StringComparison.Ordinal);   // §6 P1: another app's thing, never this desk's

        // Kept context.kept.ms after it came: 29 999 ms on, kept; at 30 000 ms the next read drops it, and the card no longer says it.
        clock += ContextPost.KeptMs - 1;
        Assert.Single(desk.Control.Posted);
        clock += 1;
        Assert.Empty(desk.Control.Posted);
        Assert.DoesNotContain(desk.Services.Apps(DateTime.UtcNow), a => a.Posted);
        Assert.Single(Rows(desk.Services, "ContextPost"));
        // Its link closed (the oversize line), the key links again and posts: a first keep after expiry is a row again — one, not one a post.
        using (var again = TestApp.Pump(LinkAs(desk.Port, LinkAcceptor.Key(postKey, Poster, "post")!, Poster)))
        {
            Assert.Equal("OK @a1 kept", Ask(again, "a1", "CONTEXT POST " + PostJson()));
            Assert.Equal("OK @a2 kept", Ask(again, "a2", "CONTEXT POST " + PostJson()));
        }
        Assert.Equal(2, Rows(desk.Services, "ContextPost").Count);
        // None of the refusals counted against a key: the wrong-proof table locked no pair, and the refusals are summed by reason.
        Assert.DoesNotContain(Rows(desk.Services, "LinkLocked"), _ => true);
        Assert.True(desk.Control.PostRefusals.Values.Sum() >= 7, string.Join(", ", desk.Control.PostRefusals.Select(r => r.Key + " " + r.Value)));
    }

    /// <summary>
    /// Round 100.5: a link this desk has told its last word — the oversize line's <c>ERR … closed</c>, the operator's CLOSE — is closing
    /// from that word for every reader, while its connection has not yet ended: its row listed, told and not ended, its app-link slot
    /// held (the connection's), its posted context kept, and nothing reading it as open — no card in STATE's <c>bus.apps</c>, no open
    /// accepted link on any app, no CLOSE on its row or its menu, no app link on the Run strip. Deterministic: the first leg holds the
    /// connection's handler right after its last word is queued (<see cref="ControlService.AfterLastWord"/>, the poster's alone, let go
    /// in <c>finally</c>), the second holds its connection open after the word for <see cref="ControlService.CloseLinger"/> — 3 s here,
    /// the margin its reads after the word are made within on a loaded runner (no hook holds that leg; the linger alone does); every
    /// assertion after the word is read is made with no pump and no wait. Then the connection ends, its slot is given back and the grey
    /// row is written — the close observed by the grey row, as before.
    /// </summary>
    [AvaloniaFact]
    public void ALinkToldItsLastWordIsClosingForEveryReaderUntilItsConnectionEndsAndTheGreyRowObservesTheEnd()
    {
        using var desk = new AcceptorDesk();
        var clock = 1_000_000L;
        desk.Control.PostedNowMs = () => clock;
        var cred = LinkAcceptor.Key(MakeKey(desk, Poster, "post"), Poster, "post")!;
        var machine = desk.Services.Kernel.Beacon.MachineName;

        // What every reader says of a link told its last word and not yet ended, read at once on the test's thread.
        void AssertClosing(string lid, string when)
        {
            var row = Assert.Single(desk.Control.AppLinkList, r => r.Lid == lid);
            Assert.True(row.Told && !row.Ended && !row.Open, $"{when}: the row is told {row.Told}, ended {row.Ended}");
            Assert.Contains(AppLinkRow.ClosingWords, row.Words, StringComparison.Ordinal);
            Assert.Equal(1, desk.Control.AppLinks);                                                  // the slot is the connection's, still held
            using (var doc = JsonDocument.Parse(new CommandRouter(desk.Services).StateJson()))
            {
                var bus = doc.RootElement.GetProperty("bus");
                Assert.DoesNotContain(bus.GetProperty("apps").EnumerateArray(), a => a.GetProperty("id").GetString() == PosterId);
                Assert.Contains(bus.GetProperty("posted").EnumerateArray(), p => p.GetProperty("id").GetString() == PosterId);
                // The lid's row (an earlier link of the same app may be listed ended beside it): told at the row's end, not ended.
                var deckRow = Assert.Single(doc.RootElement.GetProperty("decks").EnumerateArray(), d => d.GetProperty("level").GetInt32() == 1 && !d.GetProperty("ended").GetBoolean());
                Assert.Equal((PosterId, true), (deckRow.GetProperty("id").GetString(), deckRow.GetProperty("told").GetBoolean()));
                Assert.Equal("told", deckRow.EnumerateObject().Last().Name);
            }
            Assert.All(desk.Services.Apps(DateTime.UtcNow), a => Assert.Null(ShowBusView.OpenAccepted(a)));
            Assert.DoesNotContain(desk.Services.Apps(DateTime.UtcNow), a => a.Id == PosterId);
            Assert.Equal("", AcceptorWords.Glance(desk.Control.AppLinkList));
            // The Nodes page's row offers no CLOSE, its menu says why, and the action refuses it in the same words' sense.
            var vm = desk.Booted.Vm;
            vm.AppLinks.Look();
            var tableRow = Assert.Single(vm.AppLinks.Rows, r => r.Lid == lid);
            Assert.False(tableRow.CanClose, when + ": CLOSE is drawn on a closing row");
            Assert.Equal(DeskMenus.ClosingAlready, DeskMenus.AppLink(new DeskFacts(), tableRow.Menu).Groups.SelectMany(g => g.Entries).Single(e => e.Id == "link.close").Because);
            var again = desk.Services.Actions.Execute(new ShowAction(ShowActionKind.AppLinkClose, lid), ActionOrigin.Desk);
            Assert.Equal(ActionStatus.Refused, again.Status);
            Assert.Contains("closing already", again.Message, StringComparison.Ordinal);
        }

        // The oversize line's last word, the connection's handler held right after it is queued: the line is written and read, and the
        // handler has not reached its end, so the row is told and not ended for as long as the hold lasts.
        using var held = new ManualResetEventSlim(false);
        string lid;
        ControlService.AfterLastWord = peer =>
        {
            if (peer.Link?.Client == Poster) held.Wait(TimeSpan.FromSeconds(10));
        };
        try
        {
            using var poster = TestApp.Pump(LinkAs(desk.Port, cred, Poster));
            Assert.Equal("OK @p1 kept", Ask(poster, "p1", "CONTEXT POST " + PostJson()));
            lid = Assert.Single(desk.Control.AppLinkList).Lid;
            Assert.Contains(desk.Services.Apps(DateTime.UtcNow), a => a.Id == PosterId && a.Posted && ShowBusView.OpenAccepted(a)?.Lid == lid);
            var head = "@big CONTEXT POST ";
            var json = PostJson(id: PosterId, headline: "big");
            TestApp.Pump(poster.Send(head + PostJson(id: PosterId, headline: "big", pad: WireLimits.CommandBytes + 1 - Encoding.UTF8.GetByteCount(head + json))).ContinueWith(_ => true, TaskScheduler.Default));
            Assert.EndsWith("closed", TestApp.Pump(poster.NextMatching(l => l.StartsWith("ERR ", StringComparison.Ordinal))), StringComparison.Ordinal);
            AssertClosing(lid, "after the oversize line's last word");
        }
        finally
        {
            held.Set();
            ControlService.AfterLastWord = null;
        }
        // Let go, the connection ends: its slot is given back and its grey row written — the end observed, never claimed at the word.
        PumpWhile(() => desk.Control.AppLinks != 0 || !desk.Control.AppLinkList.Any(r => r.Lid == lid && r.Ended), 5000);
        var oversized = Assert.Single(desk.Control.AppLinkList, r => r.Lid == lid);
        Assert.Equal((CheckLight.Grey, true), (oversized.Light, oversized.Told));                 // told before it ended, grey once it had

        // The operator's CLOSE, its connection held open for three seconds after the word: told and not ended for the whole linger, then
        // the slot's release and the grey row, as the acceptor's list test waits for them.
        desk.Control.CloseLinger = TimeSpan.FromSeconds(3);
        using var closed = TestApp.Pump(LinkAs(desk.Port, cred, Poster));
        Assert.Equal("OK @c1 kept", Ask(closed, "c1", "CONTEXT POST " + PostJson()));
        var second = Assert.Single(desk.Control.AppLinkList, r => r.Open).Lid;
        var pressed = Stopwatch.StartNew();
        Assert.Equal(ActionStatus.Requested, desk.Services.Actions.Execute(new ShowAction(ShowActionKind.AppLinkClose, second), ActionOrigin.Desk).Status);
        TestApp.Pump(closed.NextMatching(l => l == "ERR LINK refused — " + AcceptorWords.ClosedWords(machine)));
        AssertClosing(second, "after CLOSE's last word");
        var reads = 0;
        while (desk.Control.AppLinks != 0)
        {
            // Every reading while the connection lingers: no card, and a row for the lid that has not ended is told.
            reads++;
            using (var doc = JsonDocument.Parse(new CommandRouter(desk.Services).StateJson()))
                Assert.DoesNotContain(doc.RootElement.GetProperty("bus").GetProperty("apps").EnumerateArray(), a => a.GetProperty("id").GetString() == PosterId);
            Assert.All(desk.Control.AppLinkList.Where(r => r.Lid == second && !r.Ended), r => Assert.True(r.Told, "a lingering row read as open"));
            // Bound from the press: the flush (at most 2 s), the 3 s linger, and a margin for a loaded runner.
            Assert.True(pressed.ElapsedMilliseconds < 9000, $"the closed link's slot was still held {pressed.ElapsedMilliseconds} ms after the press ({reads} readings)");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(10);
        }
        PumpWhile(() => !desk.Control.AppLinkList.Any(r => r.Lid == second && r.Ended), 5000);
        var ended = Assert.Single(desk.Control.AppLinkList, r => r.Lid == second);
        Assert.Equal((CheckLight.Grey, "closed by this desk's operator"), (ended.Light, ended.EndWords));
        Assert.Single(Rows(desk.Services, nameof(ShowActionKind.AppLinkClose)), r => r.Target == second && r.Outcome == "Requested");
    }

    /// <summary>
    /// The flood at the wire's own caps (Round 100's opening names it): <c>WireLimits.MaxAppLinks</c> (16) links a <c>post</c> key each
    /// verified, from four loopback addresses (127.0.0.1 to 127.0.0.4, <c>MaxAppLinksPerAddress</c> each), every one posting lines of
    /// exactly <c>WireLimits.CommandBytes</c> beside bodies that are not a context and contexts for another node, on an injected clock.
    /// Measured: the table's count and bytes from the table itself, the journal's rows by kind, the refusal summaries per injected
    /// minute and the count inside them, and a read answered on another link while the flood runs. The 129th id is the Core leg's
    /// (<c>ContextPostTests</c>): sixteen links over sixteen keys never reach <c>peers.max</c>.
    /// </summary>
    [AvaloniaFact]
    public void AFloodAtTheWiresCapsKeepsTheTableTheJournalAndTheRefusalsBoundedAndTheDeskAnswering()
    {
        using var desk = new AcceptorDesk();
        var clock = 5_000_000L;
        desk.Control.PostedNowMs = () => Interlocked.Read(ref clock);
        var limits = desk.Control.WireLimits;
        Assert.Equal((16, 4), (limits.MaxAppLinks, limits.MaxAppLinksPerAddress));
        var ids = Enumerable.Range(0, limits.MaxAppLinks).Select(i => "aa" + i.ToString("x6", CultureInfo.InvariantCulture)).ToArray();
        var creds = ids.Select(id => LinkAcceptor.Key(MakeKey(desk, "messenger:" + id, "post"), "messenger:" + id, "post")!).ToArray();
        var links = new List<Lines>();
        using var deck = TestApp.Pump(Lines.Dial(desk.Port, tls: false));
        try
        {
            TestApp.Pump(deck.Send("HELLO FOH deck").ContinueWith(_ => true, TaskScheduler.Default));
            var nonce = TestApp.Pump(deck.NextMatching(l => l.StartsWith("NONCE ", StringComparison.Ordinal)))["NONCE ".Length..];
            TestApp.Pump(deck.Send("AUTH-PROOF " + WireProof.Proof(desk.Token, nonce, "FOH deck")).ContinueWith(_ => true, TaskScheduler.Default));
            TestApp.Pump(deck.NextMatching(l => l.StartsWith("OK paired", StringComparison.Ordinal)));
            for (var i = 0; i < ids.Length; i++)
                links.Add(TestApp.Pump(LinkAs(desk.Port, creds[i], "messenger:" + ids[i], IPAddress.Parse("127.0.0." + (1 + i / limits.MaxAppLinksPerAddress).ToString(CultureInfo.InvariantCulture)))));
            Assert.Equal(limits.MaxAppLinks, desk.Control.AppLinks);

            const int posts = 24;
            const int refusals = 4;
            var head = "@x00 CONTEXT POST ";
            string Full(string id, int n)
            {
                var json = PostJson(id: id, headline: "n" + n.ToString("D3", CultureInfo.InvariantCulture));
                return head[..1] + "x" + n.ToString("D2", CultureInfo.InvariantCulture) + head[4..]
                    + PostJson(id: id, headline: "n" + n.ToString("D3", CultureInfo.InvariantCulture), pad: WireLimits.CommandBytes - Encoding.UTF8.GetByteCount(head + json));
            }
            Assert.Equal(WireLimits.CommandBytes, Encoding.UTF8.GetByteCount(Full(ids[0], 0)));     // the longest line the wire reads
            var sw = Stopwatch.StartNew();
            var flood = links.Select((l, i) => Task.Run(async () =>
            {
                for (var n = 0; n < posts; n++) await l.Send(Full(ids[i], n));
                for (var n = 0; n < refusals; n++) await l.Send("@r" + n.ToString(CultureInfo.InvariantCulture) + (n % 2 == 0 ? " CONTEXT POST [1,2]" : " CONTEXT POST " + PostJson(id: "5e6f7a8b")));
                var kept = 0;
                var refused = 0;
                while (kept + refused < posts + refusals)
                {
                    var line = await l.Next();
                    if (line.StartsWith("OK @x", StringComparison.Ordinal) && line.EndsWith(" kept", StringComparison.Ordinal)) kept++;
                    else if (line.StartsWith("ERR @r", StringComparison.Ordinal)) refused++;
                }
                return (kept, refused);
            })).ToArray();
            // A read on a deck of its own, answered while the flood is in the desk's readers, timed from its own send: the show thread is
            // never what the posts wait on, and the posts never hold it.
            var status = Task.Run(async () =>
            {
                await deck.Send("@s1 STATUS");
                var probe = Stopwatch.StartNew();
                var answer = await deck.NextMatching(l => l.StartsWith("OK @s1 ", StringComparison.Ordinal) || l.StartsWith("ERR @s1", StringComparison.Ordinal));
                return (answer[..Math.Min(8, answer.Length)], probe.ElapsedMilliseconds);
            });
            var results = TestApp.Pump(Task.WhenAll(flood));
            var (answered, waitedMs) = TestApp.Pump(status);
            Assert.All(results, r => Assert.Equal((posts, refusals), r));
            Assert.Equal("OK @s1 {", answered);
            Assert.InRange(waitedMs, 0, 10_000);

            // Bounded: one context per id, the bytes the table's own sum, one journal row per first keep — never one a post.
            var table = desk.Control.Posted;
            Assert.Equal(ids.Length, table.Count);
            Assert.True(table.Count <= ContextPost.MaxContexts);
            Assert.InRange(desk.Control.PostedBytes, ids.Length * (long)(WireLimits.CommandBytes - head.Length - 4), (long)ids.Length * ContextPost.MaxBytes);
            Assert.All(table, p => Assert.Equal("n" + (posts - 1).ToString("D3", CultureInfo.InvariantCulture), p.Kept.Context.Headline[..4]));   // latest wins, in order per link
            Assert.Equal(ids.Length, Rows(desk.Services, "ContextPost").Count);
            Assert.All(links, l => Assert.Equal("OK @k1 kept", Ask(l, "k1", "CONTEXT POST " + PostJson(id: ids[links.IndexOf(l)]))));   // every link stands: no refusal cut one
            Assert.Equal(limits.MaxAppLinks, desk.Control.AppLinks);

            // One refusal summary a minute, its count the refusals sent; nothing past it before the next minute.
            var log = Path.Combine(desk.Booted.Dir, "patterns.log");
            const string summary = "CONTEXT POSTs refused in the last minute";
            Assert.Equal(0, CountLines(log, summary));
            Interlocked.Add(ref clock, ContextPostRefusals.SummaryMs);
            Assert.Empty(desk.Control.Posted);                                                      // a minute on: every post past context.kept.ms
            Assert.Equal(1, CountLines(log, summary));
            var said = File.ReadAllLines(log).Single(l => l.Contains(summary, StringComparison.Ordinal));
            Assert.Contains(string.Create(CultureInfo.InvariantCulture, $"{ids.Length * refusals} {summary}"), said, StringComparison.Ordinal);
            Assert.DoesNotContain("5e6f7a8b", said, StringComparison.Ordinal);
            _ = desk.Control.Posted;
            Assert.Equal(1, CountLines(log, summary));
            Assert.Equal(ids.Length, CountLines(log, "posted context is no longer kept"));                // one log line per change, not per read
            Assert.InRange(sw.ElapsedMilliseconds, 0, 120_000);
        }
        finally
        {
            foreach (var l in links) l.Dispose();
        }
    }
}
