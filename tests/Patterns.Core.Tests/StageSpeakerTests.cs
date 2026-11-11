using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The pure parts of the speaker's message through a followed Stagewatch: the route as one percent-encoded segment, the receipt
/// read by Show Bus 1.3 §13.12.3 over the bytes Stagewatch's contract pins (<see cref="StagewatchMessageBytes"/>), the
/// Stagewatch's own message folded from its frames with an answer kept after the frame's fields reset, the later-wins choice
/// either way, and the words STATE and the journal carry.
/// </summary>
public sealed class StageSpeakerTests
{
    // ---- the route -----------------------------------------------------------------------------------------------------

    [Fact]
    public void TheMessageIsOnePercentEncodedSegmentWithInfoItsFlashAndAck()
    {
        Assert.Equal("/api/message/Wrap%20up/info/slow/ack", StageMessagePath.For("Wrap up", flash: true));
        Assert.Equal("/api/message/Wrap%20up/info/off/ack", StageMessagePath.For("Wrap up", flash: false));
        Assert.Equal(StagewatchMessageBytes.MessageRoute("Wrap up"), StageMessagePath.For("  Wrap up  ", flash: true));     // trimmed, as the stage keeps it
        // A slash, a percent sign, a question mark and a hash stay inside the one segment: none can end the path or start a query.
        var path = StageMessagePath.For("50% done / 2 left? #3", flash: true)!;
        Assert.Equal("/api/message/50%25%20done%20%2F%202%20left%3F%20%233/info/slow/ack", path);
        Assert.Equal(7, path.Split('/').Length);                                         // "", api, message, the words, info, slow, ack
        Assert.Equal("50% done / 2 left? #3", Uri.UnescapeDataString(path.Split('/')[3]));
        Assert.Equal("Größe — 5 ✓", Uri.UnescapeDataString(StageMessagePath.For("Größe — 5 ✓", true)!.Split('/')[3]));
    }

    [Fact]
    public void WordsTheRouteCannotCarryGiveNoRouteAndSayWhy()
    {
        Assert.NotNull(StageMessagePath.For(new string('x', 500), true));
        Assert.Null(StageMessagePath.For(new string('x', 501), true));
        Assert.Equal("it is longer than the 500 characters Stagewatch takes", StageMessagePath.WhyNot(new string('x', 501)));
        Assert.Equal("", StageMessagePath.WhyNot(new string('x', 500)));
        Assert.Null(StageMessagePath.For("", true));
        Assert.Null(StageMessagePath.For("   ", true));
        // A segment of dots alone would be folded into the path by a client; a lone surrogate is no text.
        Assert.Null(StageMessagePath.For("..", true));
        Assert.Null(StageMessagePath.For(".", true));
        Assert.NotNull(StageMessagePath.For("...and go", true));
        Assert.Null(StageMessagePath.For("bad \ud800 text", true));
        Assert.NotNull(StageMessagePath.For("good 🎤 text", true));
        Assert.Equal("Stagewatch's route cannot carry these words", StageMessagePath.WhyNot(".."));
    }

    [Fact]
    public void AnAckIsSignedWithThisDesksNameCutToFortyAndNeverAnEmptySegment()
    {
        Assert.Equal(StagewatchMessageBytes.AckRoute(41, "Patterns FOH"), StageMessagePath.Ack(41, "Patterns FOH"));
        Assert.Equal("/api/ack/41/seen/Patterns%20FOH", StageMessagePath.Ack(41, "Patterns FOH"));
        Assert.Null(StageMessagePath.Ack(0, "Patterns FOH"));
        Assert.Null(StageMessagePath.Ack(-3, "Patterns FOH"));
        var longName = "Patterns Front of House 2nd Main Hall ABCDE";
        Assert.Equal(40, StageMessagePath.Name(longName).Length);
        Assert.Equal("Patterns", StageMessagePath.Name(""));
        Assert.Equal("Patterns", StageMessagePath.Name(".."));
        Assert.Equal("FOH desk", StageMessagePath.Name(" FOH\r\n desk "));
        Assert.Equal("/api/ack/7/seen/FOH%2F2", StageMessagePath.Ack(7, "FOH/2"));
        Assert.Equal(StageMessagePath.Clear, StagewatchMessageBytes.ClearRoute);
    }

    // ---- the receipt, by 13.12.3 ---------------------------------------------------------------------------------------

    [Fact]
    public void AnAppliedReceiptNamesItsSignalAndItsEffectIsNeverKept()
    {
        var r = StageMessagePath.Receipt(200, StagewatchMessageBytes.Applied("p12-a1b2c3", 41, "the rig's own words"));
        Assert.Equal(LinkOutcome.Applied, r.Outcome);
        Assert.Equal(41, r.MessageId);
        Assert.Equal("", r.Words);
        // Applied with no signal named: on the screen, and no acknowledgement to follow.
        var none = StageMessagePath.Receipt(200, StagewatchMessageBytes.AppliedNoRefs("p13-a1b2c3", "✓ shown"));
        Assert.Equal((LinkOutcome.Applied, 0L, ""), (none.Outcome, none.MessageId, none.Words));
    }

    [Fact]
    public void ASlowShowsReceiptIsAcceptedWithNoSignal()
    {
        var r = StageMessagePath.Receipt(200, StagewatchMessageBytes.Accepted("p20-a1b2c3"));
        Assert.Equal("{\"type\":\"receipt\",\"id\":\"p20-a1b2c3\",\"ok\":true,\"stage\":\"accepted\",\"effect\":\"\",\"error\":null,\"refs\":{}}", StagewatchMessageBytes.Accepted("p20-a1b2c3"));
        Assert.Equal((LinkOutcome.Accepted, 0L, ""), (r.Outcome, r.MessageId, r.Words));
        // A 2xx this desk cannot read as a receipt says only that it was taken.
        Assert.Equal(LinkOutcome.Accepted, StageMessagePath.Receipt(200, "not json").Outcome);
        Assert.Equal(LinkOutcome.Accepted, StageMessagePath.Receipt(200, null).Outcome);
    }

    [Fact]
    public void ARefusalIsSaidInStagewatchsOwnWordsWhateverItsDoor()
    {
        var conflict = StageMessagePath.Receipt(409, StagewatchMessageBytes.Refused("p3-a1b2c3"));
        Assert.Equal((LinkOutcome.Refused, StagewatchMessageBytes.Gone), (conflict.Outcome, conflict.Words));
        var door = StageMessagePath.Receipt(403, StagewatchMessageBytes.Door(StagewatchMessageBytes.CrewLinks));
        Assert.Equal((LinkOutcome.Refused, StagewatchMessageBytes.CrewLinks), (door.Outcome, door.Words));
        var key = StageMessagePath.Receipt(401, StagewatchMessageBytes.Door("unknown or revoked API key"));
        Assert.Equal((LinkOutcome.Refused, "unknown or revoked API key"), (key.Outcome, key.Words));
        // A refusal that says nothing says its status; this desk's key echoed in the words is masked.
        Assert.Equal("HTTP 403", StageMessagePath.Receipt(403, "").Words);
        Assert.Equal("HTTP 404", StageMessagePath.Receipt(404, "{}").Words);
        var echoed = StageMessagePath.Receipt(403, StagewatchMessageBytes.Door("key swk_abcdef0123456789 may only read"), 0, "swk_abcdef0123456789");
        Assert.DoesNotContain("swk_abcdef0123456789", echoed.Words, StringComparison.Ordinal);
        Assert.Contains("may only read", echoed.Words, StringComparison.Ordinal);
    }

    [Fact]
    public void ARateLimitIsRefusedWithItsWaitAndNoAnswerIsUnconfirmedNeverRefused()
    {
        var busy = StageMessagePath.Receipt(429, StagewatchMessageBytes.Door("slow down"), 7);
        Assert.Equal((LinkOutcome.Refused, "rate limited — again in 7 s", 7), (busy.Outcome, busy.Words, busy.RetryAfter));
        Assert.Equal(LinkOutcome.Unconfirmed, StageMessagePath.Receipt(null, null).Outcome);
        Assert.Equal(LinkOutcome.Unconfirmed, StageMessagePath.Receipt(503, StagewatchMessageBytes.Door("busy")).Outcome);
        Assert.Equal(LinkOutcome.Unconfirmed, StageMessagePath.Receipt(408, "").Outcome);
        Assert.Equal(LinkOutcome.Unconfirmed, StageMessagePath.Receipt(500, "<html>").Outcome);
    }

    [Fact]
    public void EveryOutcomeHasItsJournalRowAndOnlyAppliedWithASignalSaysOnTheScreen()
    {
        Assert.Equal("stage message → Stagewatch Hall A: on the speaker's screen (message 41)", StageSpeakerWords.ReceiptRow("Hall A", new StageReceipt(LinkOutcome.Applied, 41, "")));
        Assert.Equal("stage message → Stagewatch Hall A: accepted, not yet applied — its show thread is slow", StageSpeakerWords.ReceiptRow("Hall A", new StageReceipt(LinkOutcome.Accepted, 0, "")));
        Assert.Equal($"stage message → Stagewatch Hall A refused it ({StagewatchMessageBytes.Gone}) — shown on this desk's displays only",
            StageSpeakerWords.ReceiptRow("Hall A", new StageReceipt(LinkOutcome.Refused, 0, StagewatchMessageBytes.Gone)));
        var unconfirmed = StageSpeakerWords.ReceiptRow("Hall A", new StageReceipt(LinkOutcome.Unconfirmed, 0, "no answer in 4 s"));
        Assert.Contains("unconfirmed (no answer in 4 s)", unconfirmed, StringComparison.Ordinal);
        Assert.Contains("it may be on the speaker's screen", unconfirmed, StringComparison.Ordinal);
        Assert.DoesNotContain("refused", unconfirmed, StringComparison.Ordinal);
        Assert.DoesNotContain("failed", unconfirmed, StringComparison.Ordinal);
        var notSent = StageSpeakerWords.ReceiptRow("Hall A", new StageReceipt(LinkOutcome.NotSent, 0, "the link to Stagewatch Hall A dropped before it was sent"));
        Assert.StartsWith("stage message → Stagewatch Hall A: not sent (the link to Stagewatch Hall A dropped before it was sent)", notSent, StringComparison.Ordinal);
        // Applied with no signal named: on the screen, and said to have no acknowledgement to follow.
        Assert.Contains("no acknowledgement follows", StageSpeakerWords.ReceiptRow("Hall A", new StageReceipt(LinkOutcome.Applied, 0, "")), StringComparison.Ordinal);
        Assert.Equal("stage message 41 seen by Ana (10.0.0.30) at 10:42:07", StageSpeakerWords.SeenRow(41, "Ana (10.0.0.30)", new DateTime(2026, 11, 11, 10, 42, 7)));
    }

    // ---- the Stagewatch's own message, frame by frame ------------------------------------------------------------------

    private static StagewatchFrame Frame(string message, long id = 0, string ack = "", string by = "", string flash = "slow", long seq = 1)
        => StagewatchFrame.Parse(StagewatchMessageBytes.Frame(seq, message, flash, id, ack, by))!;

    [Fact]
    public void AFrameCarriesWhoAnsweredAndTheFlash()
    {
        var f = Frame("Wrap up", 41, "done", "Patterns FOH (127.0.0.1)");
        Assert.Equal(("Wrap up", 41L, "done", "Patterns FOH (127.0.0.1)", "slow"), (f.Message, f.MessageId, f.MessageAck, f.MessageAckBy, f.Flash));
        // The far end's words: one line, bounded.
        var odd = StagewatchFrame.Parse(StagewatchMessageBytes.Frame(2, "x", "sideways", 1, "sent", new string('n', 200) + "\nline"))!;
        Assert.Equal(StagewatchFrame.MaxAckBy, odd.MessageAckBy.Length);
        Assert.Equal("", odd.Flash);
        // The relay to a timer node carries both, so its display agrees with the desk's.
        var sight = new StagewatchSight("Hall A", "10.0.0.5:9924", ShowBusState.Live, f, 10, null, "");
        var relayed = StagewatchRelay.Parse(StagewatchRelay.Of(sight, 10.5, 1).ToJson())!;
        Assert.Equal(("Patterns FOH (127.0.0.1)", "slow", 41L), (relayed.Frame!.MessageAckBy, relayed.Frame.Flash, relayed.Frame.MessageId));
    }

    [Fact]
    public void AFramesAcknowledgementIsOneOfThreeWordsOrNothing()
    {
        // The readers promise a closed set — STATE's stage.speakerAck, /api/stage's follows.messageAck, the deck's variable:
        // any other word, however long, is kept as none, and a signal with no word asks for no ACK.
        foreach (var word in new[] { "sent", "seen", "done" }) Assert.Equal(word, Frame("Wrap up", 41, word).MessageAck);
        Assert.Equal("done", Frame("Wrap up", 41, "DONE").MessageAck);
        foreach (var odd in new[] { "applied", "ok", "sent ", new string('s', 100_000), "do\\u0007ne" })
        {
            var f = Frame("Wrap up", 41, odd);
            Assert.Equal("", f.MessageAck);
            var seen = StageMessageSeen.Next(StageMessageSeen.None, f, 10);
            Assert.Equal((41L, "", false, false), (seen.Id, seen.Ack, seen.Asks, seen.Answered));
        }
    }

    [Fact]
    public void ASignalGoesSentToDoneAndItsAnswerIsKeptWhenTheFramesFieldsGoBackToNothing()
    {
        var seen = StageMessageSeen.Next(StageMessageSeen.None, Frame("Wrap up", 41, "sent"), 100);
        Assert.Equal((41L, "Wrap up", 100.0, "sent", "", true), (seen.Id, seen.Text, seen.AtSeconds, seen.Ack, seen.By, seen.Flash));
        Assert.True(seen.Asks);
        // The same signal again: nothing moves, and its arrival is still the first frame's.
        seen = StageMessageSeen.Next(seen, Frame("Wrap up", 41, "sent"), 102);
        Assert.Equal(100.0, seen.AtSeconds);
        // Answered: done, by whom the frame says.
        seen = StageMessageSeen.Next(seen, Frame("Wrap up", 41, "done", "Ana (10.0.0.30)"), 104);
        Assert.Equal(("done", "Ana (10.0.0.30)", 100.0), (seen.Ack, seen.By, seen.AtSeconds));
        Assert.True(seen.Answered);
        Assert.False(seen.Asks);
        // 30 s on the frame's three fields are 0, "", "" and the message still on stage: done is kept for the signal it had.
        seen = StageMessageSeen.Next(seen, Frame("Wrap up"), 134);
        Assert.Equal((41L, "done", "Ana (10.0.0.30)", 100.0), (seen.Id, seen.Ack, seen.By, seen.AtSeconds));
        // A frame that carries no time says nothing of the message.
        Assert.Same(seen, StageMessageSeen.Next(seen, null, 135));
        // Cleared there: none.
        Assert.Same(StageMessageSeen.None, StageMessageSeen.Next(seen, Frame(""), 140));
    }

    [Fact]
    public void AnotherTextOrAnotherSignalIsAnotherMessageArrivedNow()
    {
        var first = StageMessageSeen.Next(StageMessageSeen.None, Frame("Two minutes", 5, "sent"), 10);
        var second = StageMessageSeen.Next(first, Frame("One minute", 6, "sent", flash: "fast"), 20);
        Assert.Equal((6L, "One minute", 20.0, "sent"), (second.Id, second.Text, second.AtSeconds, second.Ack));
        // The same words under a new signal are a new message too.
        var again = StageMessageSeen.Next(second, Frame("One minute", 7, "sent"), 30);
        Assert.Equal((7L, 30.0), (again.Id, again.AtSeconds));
        // An unanswered signal cancelled, its words still on stage with none: no signal to answer any more.
        var cancelled = StageMessageSeen.Next(again, Frame("One minute"), 40);
        Assert.Equal((0L, "", 40.0), (cancelled.Id, cancelled.Ack, cancelled.AtSeconds));
        Assert.False(cancelled.Asks);
        // A message that asks for no acknowledgement carries none, whatever the frame's fields say.
        var plain = StageMessageSeen.Next(StageMessageSeen.None, Frame("Thank you", 0, "sent", "x", flash: "off"), 50);
        Assert.Equal((0L, "", "", false), (plain.Id, plain.Ack, plain.By, plain.Flash));
    }

    // ---- one message on the page ---------------------------------------------------------------------------------------

    private static StageMessage Local(string text, bool flash = true) => new() { Id = "a1b2c3d4", Text = text, Flash = flash };

    [Fact]
    public void TheLaterMessageWinsEitherWayAndAnAnsweredOneLeaves()
    {
        var theirs = new StageMessageSeen(41, "From Stagewatch", 100, "sent", "", true);
        // The Stagewatch's is the later: drawn as sw:41, answerable there.
        var shown = StageSpeakerChoice.Of(Local("From this desk"), 90, theirs, following: true)!;
        Assert.Equal(("sw:41", "From Stagewatch", true, "stagewatch", true), (shown.Id, shown.Text, shown.Flash, shown.From, shown.Ack));
        // This desk's is the later.
        shown = StageSpeakerChoice.Of(Local("From this desk", flash: false), 110, theirs, following: true)!;
        Assert.Equal(("a1b2c3d4", "From this desk", false, "desk", true), (shown.Id, shown.Text, shown.Flash, shown.From, shown.Ack));
        // At one instant, the room's.
        Assert.Equal("sw:41", StageSpeakerChoice.Of(Local("From this desk"), 100, theirs, following: true)!.Id);
        // Answered, the Stagewatch's leaves the page — and what waits on this desk's displays shows again.
        var answered = theirs with { Ack = "done", By = "Ana (10.0.0.30)" };
        Assert.Null(StageSpeakerChoice.Of(null, 0, answered, following: true));
        Assert.Equal("a1b2c3d4", StageSpeakerChoice.Of(Local("From this desk"), 90, answered, following: true)!.Id);
        // A message with no signal is drawn and offers no ACK.
        var plain = new StageMessageSeen(0, "Thank you", 100, "", "", false);
        Assert.Equal(("sw:0", false), (StageSpeakerChoice.Of(null, 0, plain, true)!.Id, StageSpeakerChoice.Of(null, 0, plain, true)!.Ack));
        // Not following: only this desk's own.
        Assert.Null(StageSpeakerChoice.Of(null, 0, theirs, following: false));
        Assert.Equal("a1b2c3d4", StageSpeakerChoice.Of(Local("From this desk"), 90, theirs, following: false)!.Id);
        Assert.Null(StageSpeakerChoice.Of(null, 0, StageMessageSeen.None, following: true));
    }

    [Fact]
    public void APagesAckNamesTheStagewatchsSignalOnlyBySwAndItsNumber()
    {
        Assert.Equal(41, StageSpeakerChoice.StagewatchSignal("sw:41"));
        Assert.Equal(0, StageSpeakerChoice.StagewatchSignal("sw:0"));
        Assert.Null(StageSpeakerChoice.StagewatchSignal("a1b2c3d4"));
        Assert.Null(StageSpeakerChoice.StagewatchSignal("sw:"));
        Assert.Null(StageSpeakerChoice.StagewatchSignal("sw:-1"));
        Assert.Null(StageSpeakerChoice.StagewatchSignal("sw:4 1"));
        Assert.Null(StageSpeakerChoice.StagewatchSignal(null));
        Assert.Equal("sw:41", StageSpeakerChoice.StagewatchId(41));
    }

    // ---- the words STATE and the readers carry -------------------------------------------------------------------------

    [Fact]
    public void AMessagesReceiptWordFollowsItsRouteAndNeverReadsSeenBeforeAnAnswer()
    {
        var m = new StageMessage { Text = "Wrap up" };
        Assert.Equal("sent", StageSpeakerWords.AckWord(m));                                // on this desk's displays, waiting
        Assert.True(StageSpeakerWords.OnDesk(m));
        m.AckUtc = DateTime.UtcNow;
        Assert.Equal("seen", StageSpeakerWords.AckWord(m));

        var via = new StageMessage { Text = "Wrap up", Via = StageSpeakerWords.Via, SwAck = StageSpeakerWords.Requested, SwName = "Hall A" };
        Assert.Equal("requested", StageSpeakerWords.AckWord(via));
        Assert.True(StageSpeakerWords.OnStagewatch(via));
        Assert.False(StageSpeakerWords.OnDesk(via));
        foreach (var step in new[] { StageSpeakerWords.Accepted, StageSpeakerWords.Sent })
        {
            via.SwAck = step;
            Assert.Equal(step, StageSpeakerWords.AckWord(via));
            Assert.False(StageSpeakerWords.OnDesk(via));
        }
        foreach (var ended in new[] { StageSpeakerWords.Refused, StageSpeakerWords.Unconfirmed, StageSpeakerWords.NotSent })
        {
            via.SwAck = ended;
            Assert.Equal(ended, StageSpeakerWords.AckWord(via));
            Assert.True(StageSpeakerWords.OnDesk(via));                                    // it is on this desk's displays
            Assert.False(StageSpeakerWords.OnStagewatch(via));
            Assert.NotEqual("", StageSpeakerWords.Glance(via));
            Assert.Contains("this desk's displays", StageSpeakerWords.Line(via, "Hall A"), StringComparison.Ordinal);
        }
        via.SwAck = StageSpeakerWords.Unconfirmed;
        Assert.Equal("SPEAKER MESSAGE △ UNCONFIRMED", StageSpeakerWords.Glance(via));
        via.SwAck = StageSpeakerWords.Done;
        via.SwAckBy = "Ana (10.0.0.30)";
        via.SwMessageId = 41;
        via.AckUtc = DateTime.UtcNow;
        Assert.Equal("done", StageSpeakerWords.AckWord(via));
        Assert.Equal("the speaker's message 'Wrap up' (message 41) was seen by Ana (10.0.0.30) through Stagewatch Hall A", StageSpeakerWords.Line(via, "Hall A"));
        Assert.Equal("", StageSpeakerWords.Glance(via));
        via.SwAck = StageSpeakerWords.Cleared;
        Assert.Equal("", StageSpeakerWords.AckWord(via));
        Assert.Equal("", StageSpeakerWords.Line(via, "Hall A"));
    }

    [Fact]
    public void TheReceiptsListSaysSeenOnlyForAMessageSomebodySaw()
    {
        var at = new DateTime(2026, 11, 11, 10, 42, 7, DateTimeKind.Utc);
        var time = at.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);

        // On this desk's displays alone: waiting, then seen with the time of the ACK — the list's words as ever.
        var local = new StageMessage { Text = "Mic 2 is live", Channel = "crew" };
        Assert.Equal(("", "waiting…"), (StageSpeakerWords.Receipt(local), local.Receipt));
        local.AckUtc = at;
        Assert.Equal(("", "seen " + time), (StageSpeakerWords.Receipt(local), local.Receipt));
        // Cleared by the desk: off the displays, and nobody saw it.
        var cleared = new StageMessage { Text = "Wrap up", AckUtc = at, SwAck = StageSpeakerWords.Cleared };
        Assert.Equal("cleared", cleared.Receipt);

        // Through a Stagewatch: each step in its own words, and "seen" in none of them before an answer.
        var via = new StageMessage { Text = "Wrap up", Via = StageSpeakerWords.Via, SwAck = StageSpeakerWords.Requested, SwName = "Hall A" };
        var raised = new List<string?>();
        via.PropertyChanged += (_, e) => raised.Add(e.PropertyName);
        Assert.Equal("sent to Stagewatch Hall A…", via.Receipt);
        via.SwAck = StageSpeakerWords.Accepted;
        Assert.Contains(nameof(StageMessage.Receipt), raised);                             // the panel's line follows the step
        Assert.Equal("accepted by Stagewatch Hall A, not yet on its screen", via.Receipt);
        via.SwAck = StageSpeakerWords.Sent;
        Assert.Equal("on Stagewatch Hall A's screen, waiting…", via.Receipt);
        foreach (var (step, words) in new[] { (StageSpeakerWords.Refused, "refused by"), (StageSpeakerWords.Unconfirmed, "unconfirmed by"), (StageSpeakerWords.NotSent, "not sent to") })
        {
            via.SwAck = step;
            Assert.Equal($"{words} Stagewatch Hall A — on this desk's displays, waiting…", via.Receipt);
            Assert.DoesNotContain("seen", via.Receipt, StringComparison.Ordinal);
        }
        // Answered on this desk's display after its route ended here: seen, here, with the time.
        via.AckUtc = at;
        Assert.Equal($"not sent to Stagewatch Hall A — on this desk's displays, seen {time}", via.Receipt);
        // Answered on the Stagewatch: seen, by whom its frame says.
        raised.Clear();
        (via.SwAck, via.SwAckBy) = (StageSpeakerWords.Done, "Ana (10.0.0.30)");
        Assert.Contains(nameof(StageMessage.Receipt), raised);
        Assert.Equal($"seen by Ana (10.0.0.30) {time}", via.Receipt);

        // A message a newer one replaced before it was sent, and one that left the Stagewatch's stage unanswered, both carry
        // the time they went off the displays — and neither was seen by anyone.
        var superseded = new StageMessage { Text = "Second", Via = StageSpeakerWords.Via, SwAck = StageSpeakerWords.Cleared, SwWords = StageSpeakerWords.SupersededWhy, AckUtc = at };
        Assert.Equal("superseded — never sent", superseded.Receipt);
        var left = new StageMessage { Text = "First", Via = StageSpeakerWords.Via, SwAck = StageSpeakerWords.Cleared, SwMessageId = 41, AckUtc = at };
        Assert.Equal("cleared", left.Receipt);
        foreach (var unseen in new[] { superseded, left, cleared }) Assert.DoesNotContain("seen", unseen.Receipt, StringComparison.Ordinal);
    }

    [Fact]
    public void AClearsRowSaysWhatCameOfItAndAnAcceptedOneIsNotYetCleared()
    {
        Assert.Equal("stage clear → Stagewatch Hall A: this desk's message cleared from the speaker's screen", StageSpeakerWords.ClearRow("Hall A", new StageReceipt(LinkOutcome.Applied, 0, "")));
        Assert.DoesNotContain("cleared from", StageSpeakerWords.ClearRow("Hall A", new StageReceipt(LinkOutcome.Accepted, 0, "")), StringComparison.Ordinal);
        Assert.Contains("accepted, not yet applied", StageSpeakerWords.ClearRow("Hall A", new StageReceipt(LinkOutcome.Accepted, 0, "")), StringComparison.Ordinal);
        // A clear that followed a message which never reached the Stagewatch was never asked: nothing there is this desk's to clear.
        var unasked = StageSpeakerWords.ClearRow("Hall A", new StageReceipt(LinkOutcome.NotSent, 0, StageSpeakerWords.NothingToClearWhy));
        Assert.Equal("stage clear → Stagewatch Hall A: not asked — the message it would clear never reached the speaker's screen", unasked);
        Assert.DoesNotContain("clear it there", unasked, StringComparison.Ordinal);
        Assert.EndsWith("clear it there", StageSpeakerWords.ClearRow("Hall A", new StageReceipt(LinkOutcome.NotSent, 0, "the link dropped")), StringComparison.Ordinal);
    }

    [Fact]
    public void ACuesRowSaysAReceiptThatSettlesNothingInItsOwnWords()
    {
        var row = new CueExecutionRecord(DateTime.UtcNow, "c1", "01", "Wrap", CueOutcome.Requested, "desk", 1, 1, "", "e1e2e3e4", Pending: 1);
        Assert.Equal("Awaiting 1 receipt", row.OutcomeWords);
        var accepted = row with { Waiting = StageSpeakerWords.CueWaiting("Hall A", LinkOutcome.Accepted) };
        Assert.Equal("Stagewatch Hall A accepted the message, not yet applied", accepted.OutcomeWords);
        var unconfirmed = row with { Waiting = StageSpeakerWords.CueWaiting("Hall A", LinkOutcome.Unconfirmed) };
        Assert.Contains("unconfirmed", unconfirmed.OutcomeWords, StringComparison.Ordinal);
        Assert.False(unconfirmed.IsFailure);
        // Settled, the words go with the wait.
        Assert.Equal("Done", (accepted with { Outcome = CueOutcome.Done, Pending = 0 }).OutcomeWords);
        Assert.Equal("Failed late", (accepted with { Outcome = CueOutcome.FailedLate, Pending = 0 }).OutcomeWords);
    }

    [Fact]
    public void TheContextSaysWhereAStageMessageGoes()
    {
        var row = Assert.Single(ShowContextBuilder.Commands, c => c.Verb == "STAGE MESSAGE <words>");
        Assert.Equal("review", row.Risk);
        Assert.Contains("through a linked Stagewatch while the stage follows it and the link holds a node key", row.Words, StringComparison.Ordinal);
        Assert.True(row.Words.Length <= 200, "a reader keeps 200 characters of a row's words");
    }
}
