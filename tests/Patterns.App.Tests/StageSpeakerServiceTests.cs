using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Patterns.Core.Tests;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The stage service's own rules for a speaker's message through a Stagewatch, against a host and a link the test plays: the
/// receipts and the frames are handed in at instants the test chooses, so each rule is read without a socket — a frame that came
/// while the answer was still awaited binds an unconfirmed message; STAGE CLEAR clears this desk's message from the
/// Stagewatch's stage answered or not, follows a message still on its way, and says what it could not clear; a cue's row
/// settles when its message is cleared or seen here, and on the clear's own receipt; a display offers the Stagewatch's ACK
/// only where it can give one; a message a run before this one left reads unconfirmed; and a desk that mirrors another's show
/// moves nothing.
/// </summary>
public sealed class StageSpeakerServiceTests
{
    private const string Name = "Hall A";

    private sealed class Carrier : ISpeakerCarrier
    {
        private int _n;

        public SpeakerRoute Route { get; set; } = new(true, Name, "");

        /// <summary>False: the link takes no call as it is asked.</summary>
        public bool Takes { get; set; } = true;

        public List<SpeakerCall> Asked { get; } = new();

        public SpeakerCall? Ask(string kind, string path, string tag, IReadOnlyList<string>? after = null)
        {
            if (!Takes) return null;
            var call = new SpeakerCall(kind, path, $"p{++_n}-test", tag) { After = after ?? [] };
            Asked.Add(call);
            return call;
        }
    }

    private sealed class Host : IStageHost
    {
        public ShowState AirState { get; } = new();

        public CueStackService CueStack => null!;                                      // the stage reads no stack here

        public RoomClock Clock { get; } = new();

        public void EditAir(Action<ShowState> edit) => edit(AirState);

        public void Notify(string message)
        {
            // the rows carry what a notice would say
        }

        public event Action? SnapshotPublished
        {
            add { /* the stage hears no publish here */ }
            remove { /* nor is one unhooked */ }
        }

        /// <summary>The link's sight; the stage follows it while <see cref="Follows"/>.</summary>
        public StagewatchSight Sight { get; set; } = StagewatchSight.None;

        public bool Follows { get; set; } = true;

        public StagewatchSight FollowedStagewatch => Follows ? Sight : StagewatchSight.None;

        public Carrier? Link { get; set; } = new();

        public ISpeakerCarrier? Speaker => Link;

        public bool AcksThroughDesk { get; set; }

        public bool OwnsTheStage { get; set; } = true;

        public string DeskName => "FOH";

        public List<(string Outcome, string Words, bool Notify)> Rows { get; } = new();

        public void StageJournal(string origin, string outcome, string words, bool notify) => Rows.Add((outcome, words, notify));

        public List<(string Execution, bool? Ok, string Words)> Receipts { get; } = new();

        public void SpeakerReceipt(string execution, bool? ok, string words) => Receipts.Add((execution, ok, words));
    }

    private static readonly StagewatchFrame AnyFrame = StagewatchFrame.Parse(StagewatchMessageBytes.Frame(1, "", "off", 0, "", ""))!;

    /// <summary>The Stagewatch's stage as a frame just told it: its message — arrived when the test says — and the frame's own arrival, now.</summary>
    private static StagewatchSight Stage(StageMessageSeen message)
        => new(Name, "10.0.0.5:9924", ShowBusState.Live, AnyFrame, ShowClock.Seconds, null, "") { Message = message };

    private static StageMessageSeen Signal(long id, string text, double at, string ack = "sent", string by = "") => new(id, text, at, ack, by, true);

    private static (Host Host, StageService Stage) Following()
    {
        var host = new Host { Sight = Stage(StageMessageSeen.None) };
        return (host, new StageService(host));
    }

    /// <summary>A message sent through the link: Requested, and the call the link was asked.</summary>
    private static (StageMessage Message, SpeakerCall Call) Send(Host host, StageService stage, string text, string execution = "")
    {
        var result = stage.Message("speaker", text, "desk", execution: execution);
        Assert.True(result.Status == ActionStatus.Requested, $"'{text}' was {result.Status}: {result.Message}");
        return (host.AirState.Stage.Messages[^1], host.Link!.Asked[^1]);
    }

    private static void Answer(StageService stage, SpeakerCall call, LinkOutcome outcome, long id = 0, string words = "", int retryAfter = 0)
        => stage.OnSpeakerAnswer(new SpeakerAnswer(call, new StageReceipt(outcome, id, words, retryAfter), Name));

    // ---- an answer lost after the message ran --------------------------------------------------------------------------

    [Fact]
    public void AnUnconfirmedMessageIsBoundByAFrameThatCameWhileItsAnswerWasStillAwaited()
    {
        var (host, stage) = Following();
        var (m, call) = Send(host, stage, "Hold for applause", execution: "exec-1");

        // The message ran on the Stagewatch and its frame reached this desk; the HTTP answer never did. The frame is older than
        // the timeout — and newer than the message's leaving, which is what a bind is held to.
        host.Sight = Stage(Signal(41, "Hold for applause", ShowClock.Seconds));
        stage.Follow();
        Assert.Equal(StageSpeakerWords.Requested, m.SwAck);                              // nothing binds before its receipt says what came of it
        Thread.Sleep(15);
        Answer(stage, call, LinkOutcome.Unconfirmed, words: "no answer in 4 s");

        Assert.Equal((StageSpeakerWords.Sent, 41L, ""), (m.SwAck, m.SwMessageId, m.SwWords));
        Assert.Equal(new[] { "unconfirmed", "applied" }, host.Rows.Select(r => r.Outcome));
        Assert.Equal(StageSpeakerWords.BoundRow(Name, 41), host.Rows[^1].Words);
        // The cue's row heard the wait, then the yes — once.
        Assert.Equal(new (string, bool?, string)[] { ("exec-1", null, StageSpeakerWords.CueWaiting(Name, LinkOutcome.Unconfirmed)), ("exec-1", true, "") }, host.Receipts);
        Assert.Equal("", m.Execution);
        Assert.Equal((StageSpeakerChoice.FromStagewatch, "sent", ""), stage.SpeakerNow());
        Assert.Equal("", stage.SpeakerGlance);
        // The speaker's answer on the Stagewatch is read from there on, and nothing was asked of the link again.
        host.Sight = Stage(Signal(41, "Hold for applause", host.Sight.Message.AtSeconds, "done", "Ana (10.0.0.30)"));
        stage.Follow();
        Assert.Equal((StageSpeakerWords.Done, "Ana (10.0.0.30)"), (m.SwAck, m.SwAckBy));
        Assert.Null(stage.Pending("speaker"));
        Assert.Single(host.Link!.Asked);
    }

    [Fact]
    public void ABindIsHeldToWhenTheMessageLeftAndNeverToAMessageOlderThanIt()
    {
        var (host, stage) = Following();
        // The Stagewatch's own operator had these very words on stage before this desk sent them: not this desk's message.
        host.Sight = Stage(Signal(40, "Wrap up", ShowClock.Seconds));
        Thread.Sleep(15);
        var (m, call) = Send(host, stage, "Wrap up");
        Answer(stage, call, LinkOutcome.Unconfirmed, words: "no answer in 4 s");
        Assert.Equal((StageSpeakerWords.Unconfirmed, 0L), (m.SwAck, m.SwMessageId));
        // On this desk's displays from the moment its route ended here — later than the Stagewatch's own, so the page draws it.
        Assert.Equal((m.Id, StageSpeakerChoice.FromDesk), (stage.SpeakerShown()!.Id, stage.SpeakerShown()!.From));
        Assert.Equal((StageSpeakerChoice.FromDesk, "unconfirmed", ""), stage.SpeakerNow());
        // A new signal with its words, arrived after it left, is its own.
        host.Sight = Stage(Signal(42, "Wrap up", ShowClock.Seconds));
        stage.Follow();
        Assert.Equal((StageSpeakerWords.Sent, 42L), (m.SwAck, m.SwMessageId));
    }

    // ---- STAGE CLEAR ---------------------------------------------------------------------------------------------------

    [Fact]
    public void StageClearClearsThisDesksAnsweredMessageFromTheStagewatchsStageAndNeverItsOwn()
    {
        var (host, stage) = Following();
        var (m, call) = Send(host, stage, "Wrap up");
        Answer(stage, call, LinkOutcome.Applied, id: 41);
        var arrived = ShowClock.Seconds;
        host.Sight = Stage(Signal(41, "Wrap up", arrived, "done", "Ana (10.0.0.30)"));
        stage.Follow();
        Assert.Equal(StageSpeakerWords.Done, m.SwAck);

        // Answered, it is off this desk's list — and still on the Stagewatch's stage, where only a clear takes it off.
        var cleared = stage.Clear();
        Assert.Equal(ActionStatus.Requested, cleared.Status);
        Assert.Equal($"Nothing on the displays to clear. Stagewatch {Name}'s speaker screen is asked to clear this desk's message — its receipt comes back here.", cleared.Message);
        var clear = host.Link!.Asked[^1];
        Assert.Equal((SpeakerCall.Clear, StageMessagePath.Clear), (clear.Kind, clear.Path));
        Assert.Empty(clear.After);
        Answer(stage, clear, LinkOutcome.Applied);
        Assert.Equal(StageSpeakerWords.ClearRow(Name, new StageReceipt(LinkOutcome.Applied, 0, "")), host.Rows[^1].Words);

        // 30 s after the answer the frame's three fields are nothing again and the message is still there: still this desk's to clear.
        host.Sight = Stage(StageMessageSeen.Next(Signal(41, "Wrap up", arrived, "done", "Ana (10.0.0.30)"), StagewatchFrame.Parse(StagewatchMessageBytes.Frame(9, "Wrap up", "slow", 0, "", "")), ShowClock.Seconds));
        Assert.Equal(ActionStatus.Requested, stage.Clear().Status);
        Assert.Equal(2, host.Link.Asked.Count(c => c.Kind == SpeakerCall.Clear));

        // The Stagewatch's own message: nothing is asked, and the words say it stays.
        host.Sight = Stage(Signal(43, "Their own", ShowClock.Seconds));
        var kept = stage.Clear();
        Assert.Equal((ActionStatus.Done, $"Nothing on the displays to clear. {StageSpeakerWords.OwnStays(Name)}."), (kept.Status, kept.Message));
        Assert.Equal(2, host.Link.Asked.Count(c => c.Kind == SpeakerCall.Clear));

        // A clear the link cannot take leaves this desk's message there, and says so; asked again, it is asked again.
        host.Sight = Stage(Signal(41, "Wrap up", arrived, "done", "Ana (10.0.0.30)"));
        host.Link.Route = new SpeakerRoute(false, Name, "its rate limit holds this desk's key 3 s");
        var stays = stage.Clear();
        Assert.Equal(ActionStatus.Done, stays.Status);
        Assert.EndsWith($"This desk's message stays on Stagewatch {Name}'s speaker screen (its rate limit holds this desk's key 3 s) — clear it there.", stays.Message, StringComparison.Ordinal);
        host.Link.Route = new SpeakerRoute(true, Name, "");
        Assert.Equal(ActionStatus.Requested, stage.Clear().Status);
    }

    [Fact]
    public void AClearBehindAMessageStillOnItsWayFollowsItAndOneNotConfirmedIsSaid()
    {
        var (host, stage) = Following();
        // The Stagewatch's own message is on its stage, and this desk's is still unanswered: the clear is asked behind it, tied
        // to it — the link writes it only if that message gets there.
        host.Sight = Stage(Signal(40, "Their own", ShowClock.Seconds));
        var (m, call) = Send(host, stage, "On its way");
        var behind = stage.Clear();
        Assert.Equal(ActionStatus.Requested, behind.Status);
        Assert.Contains($"still on its way to Stagewatch {Name}", behind.Message, StringComparison.Ordinal);
        var clear = host.Link!.Asked[^1];
        Assert.Equal(SpeakerCall.Clear, clear.Kind);
        Assert.Equal(new[] { call.Id }, clear.After);
        Assert.Equal(StageSpeakerWords.Cleared, m.SwAck);
        // The message was refused — its row says it is on no display of this desk's, which cleared it — and the link answers
        // the clear unasked: journalled as nothing to clear, and no fault.
        Answer(stage, call, LinkOutcome.Refused, words: "The stage is locked by the showcaller.");
        Assert.Equal($"stage message → Stagewatch {Name} refused it (The stage is locked by the showcaller.) — {StageSpeakerWords.ClearedMeanwhile}", host.Rows[^1].Words);
        Assert.DoesNotContain(StageSpeakerWords.HereOnly, host.Rows[^1].Words, StringComparison.Ordinal);
        Answer(stage, clear, LinkOutcome.NotSent, words: StageSpeakerWords.NothingToClearWhy);
        Assert.Equal(("not-sent", $"stage clear → Stagewatch {Name}: not asked — {StageSpeakerWords.NothingToClearWhy}", false), host.Rows[^1]);

        // A message the Stagewatch has accepted and not yet shown: no clear is asked — it would take off whatever is on that
        // stage now — and the reply says it may still get there.
        var (slow, slowCall) = Send(host, stage, "Slow show");
        Answer(stage, slowCall, LinkOutcome.Accepted);
        var asked = host.Link.Asked.Count;
        var said = stage.Clear();
        Assert.Equal(ActionStatus.Done, said.Status);
        Assert.EndsWith($"A message Stagewatch {Name} has not confirmed may still reach its speaker's screen — clear it there if it does.", said.Message, StringComparison.Ordinal);
        Assert.Equal(asked, host.Link.Asked.Count);
        Assert.Equal(StageSpeakerWords.Cleared, slow.SwAck);
    }

    [Fact]
    public void AClearBetweenAMessagesAppliedReceiptAndTheFrameThatNamesItAsksTheStagewatchTheReceiptBoundItTo()
    {
        var (host, stage) = Following();
        // The Stagewatch's own message is on its stage. This desk's runs there: its receipt says applied, message 41 — and the
        // frame that names it, which travels on the socket while the answer comes on the call, has not come yet.
        host.Sight = Stage(Signal(40, "Their own", ShowClock.Seconds));
        var (m, call) = Send(host, stage, "Wrap up");
        Answer(stage, call, LinkOutcome.Applied, id: 41);
        Assert.Equal((StageSpeakerWords.Sent, 41L), (m.SwAck, m.SwMessageId));

        // A clear in that instant clears this desk's displays and asks that stage at once, tied to nothing: the message is there.
        var cleared = stage.Clear("exec-2");
        Assert.Equal((ActionStatus.Requested, $"1 message cleared from the displays. Stagewatch {Name}'s speaker screen is asked to clear this desk's message — its receipt comes back here."), (cleared.Status, cleared.Message));
        var clear = Assert.Single(host.Link!.Asked, c => c.Kind == SpeakerCall.Clear);
        Assert.Equal(StageMessagePath.Clear, clear.Path);
        Assert.Empty(clear.After);
        Assert.Equal(StageSpeakerWords.Cleared, m.SwAck);
        // Its receipt is the cue row's, and the journal's.
        Answer(stage, clear, LinkOutcome.Applied);
        Assert.Equal(("applied", StageSpeakerWords.ClearRow(Name, new StageReceipt(LinkOutcome.Applied, 0, ""))), (host.Rows[^1].Outcome, host.Rows[^1].Words));
        Assert.Equal(("exec-2", (bool?)true, $"Stagewatch {Name}'s speaker screen is cleared of this desk's message"), host.Receipts[^1]);
        // The frames that follow — the message, then the cleared stage — move nothing more and ask nothing more.
        host.Sight = Stage(Signal(41, "Wrap up", ShowClock.Seconds));
        stage.Follow();
        host.Sight = Stage(StageMessageSeen.None);
        stage.Follow();
        Assert.Single(host.Link.Asked, c => c.Kind == SpeakerCall.Clear);
        Assert.Equal(StageSpeakerWords.Cleared, m.SwAck);

        // A message bound there that a later signal has replaced — the Stagewatch's operator, in the gap — is not on that stage:
        // nothing is asked, and the words say the Stagewatch's own stays.
        var (later, laterCall) = Send(host, stage, "Two minutes");
        Answer(stage, laterCall, LinkOutcome.Applied, id: 43);
        host.Sight = Stage(Signal(44, "Theirs after", ShowClock.Seconds));
        var kept = stage.Clear();
        Assert.Equal((ActionStatus.Done, $"1 message cleared from the displays. {StageSpeakerWords.OwnStays(Name)}."), (kept.Status, kept.Message));
        Assert.Single(host.Link.Asked, c => c.Kind == SpeakerCall.Clear);
        Assert.Equal(StageSpeakerWords.Cleared, later.SwAck);

        // A message with no signal came on that stage after the bind — one its own operator typed, which orders against nothing:
        // it may have replaced this desk's, or be a frame a moment behind the receipt. Nothing is asked, and the words say both.
        var (third, thirdCall) = Send(host, stage, "One minute");
        Answer(stage, thirdCall, LinkOutcome.Applied, id: 45);
        host.Sight = Stage(Signal(0, "Theirs, no ack", ShowClock.Seconds + 0.5));
        var theirs = stage.Clear();
        Assert.Equal((ActionStatus.Done, $"1 message cleared from the displays. {StageSpeakerWords.OwnStays(Name)}. {StageSpeakerWords.MaybeStays(Name)}."), (theirs.Status, theirs.Message));
        Assert.Single(host.Link.Asked, c => c.Kind == SpeakerCall.Clear);
        Assert.Equal(StageSpeakerWords.Cleared, third.SwAck);

        // The same for a message of this desk's bound to no signal (sent without an acknowledgement): the later message is not its own.
        var (plain, plainCall) = Send(host, stage, "Thank you");
        Answer(stage, plainCall, LinkOutcome.Applied);
        Assert.Equal((StageSpeakerWords.Sent, 0L), (plain.SwAck, plain.SwMessageId));
        host.Sight = Stage(Signal(0, "Theirs again", ShowClock.Seconds + 0.5));
        var again = stage.Clear();
        Assert.Equal((ActionStatus.Done, $"1 message cleared from the displays. {StageSpeakerWords.OwnStays(Name)}. {StageSpeakerWords.MaybeStays(Name)}."), (again.Status, again.Message));
        Assert.Single(host.Link.Asked, c => c.Kind == SpeakerCall.Clear);

        // Bound to no signal and shown by its words: it is on that stage, and the clear asks there.
        var (shown, shownCall) = Send(host, stage, "Doors open");
        Answer(stage, shownCall, LinkOutcome.Applied);
        host.Sight = Stage(Signal(0, "Doors open", ShowClock.Seconds + 0.5));
        Assert.Equal(ActionStatus.Requested, stage.Clear().Status);
        Assert.Equal(2, host.Link.Asked.Count(c => c.Kind == SpeakerCall.Clear));
        Assert.Equal(StageSpeakerWords.Cleared, shown.SwAck);
    }

    [Fact]
    public void AClearPastAReceiptsWaitWithNoFrameSinceAsksNothingAndSaysTheMessageMayStillBeThere()
    {
        var (host, stage) = Following();
        stage.BindWithin = TimeSpan.FromMilliseconds(40);
        var (m, call) = Send(host, stage, "Stalled");
        Answer(stage, call, LinkOutcome.Applied, id: 50);
        // The frames have stopped while the link stands: by its own record the desk still holds it as sent there.
        Thread.Sleep(80);
        Assert.Equal(StageSpeakerWords.Sent, m.SwAck);
        var cleared = stage.Clear();
        Assert.Equal((ActionStatus.Done, $"1 message cleared from the displays. {StageSpeakerWords.MaybeStays(Name)}."), (cleared.Status, cleared.Message));
        Assert.DoesNotContain(host.Link!.Asked, c => c.Kind == SpeakerCall.Clear);
        Assert.Equal(StageSpeakerWords.Cleared, m.SwAck);
    }

    // ---- a cue's row ---------------------------------------------------------------------------------------------------

    [Fact]
    public void ACuesRowSettlesWhenItsMessageIsClearedOrSeenHereBeforeTheStagewatchConfirmsIt()
    {
        var (host, stage) = Following();

        // Accepted, then cleared on this desk: the row settles on the clear, in its words, and no later frame moves it.
        var (accepted, call) = Send(host, stage, "Accepted", execution: "exec-a");
        Answer(stage, call, LinkOutcome.Accepted);
        Assert.Equal(("exec-a", (bool?)null), (host.Receipts[^1].Execution, host.Receipts[^1].Ok));
        stage.Clear();
        Assert.Equal(("exec-a", (bool?)true, StageSpeakerWords.ClearedFirst), host.Receipts[^1]);
        Assert.Equal("", accepted.Execution);

        // Cleared before its answer came: the answer — accepted, or unconfirmed — settles the row, never leaves it waiting.
        var (early, earlyCall) = Send(host, stage, "Cleared early", execution: "exec-b");
        stage.Clear();
        var before = host.Receipts.Count;
        Answer(stage, earlyCall, LinkOutcome.Unconfirmed, words: "no answer in 4 s");
        Assert.Equal(new (string, bool?, string)[] { ("exec-b", true, StageSpeakerWords.ClearedFirst) }, host.Receipts.Skip(before));
        Assert.Equal(StageSpeakerWords.Cleared, early.SwAck);

        // Unconfirmed, then its ACK pressed on this desk's display: seen here, and the row waits on nothing more.
        var (seen, seenCall) = Send(host, stage, "Seen here", execution: "exec-c");
        Answer(stage, seenCall, LinkOutcome.Unconfirmed, words: "no answer in 4 s");
        Assert.NotNull(stage.Ack(seen.Id));
        Assert.Equal(("exec-c", (bool?)true, StageSpeakerWords.SeenHereFirst), host.Receipts[^1]);

        // A refusal after the clear is still the row's no.
        var (refused, refusedCall) = Send(host, stage, "Refused", execution: "exec-d");
        stage.Clear();
        Answer(stage, refusedCall, LinkOutcome.Refused, words: "locked");
        Assert.Equal(("exec-d", (bool?)false), (host.Receipts[^1].Execution, host.Receipts[^1].Ok));
        Assert.Equal(StageSpeakerWords.Cleared, refused.SwAck);
    }

    [Fact]
    public void ACuesClearIsOwedItsOwnReceiptAndAFrameSettlesOneNotConfirmed()
    {
        var (host, stage) = Following();
        var (_, call) = Send(host, stage, "Ours");
        Answer(stage, call, LinkOutcome.Applied, id: 41);
        var ours = Signal(41, "Ours", ShowClock.Seconds);
        host.Sight = Stage(ours);
        stage.Follow();

        // Applied: the yes. Refused: the no, with the Stagewatch's words.
        Assert.Equal(ActionStatus.Requested, stage.Clear("exec-1").Status);
        Answer(stage, host.Link!.Asked[^1], LinkOutcome.Applied);
        Assert.Equal(("exec-1", (bool?)true), (host.Receipts[^1].Execution, host.Receipts[^1].Ok));
        Assert.Equal(ActionStatus.Requested, stage.Clear("exec-2").Status);
        Answer(stage, host.Link.Asked[^1], LinkOutcome.Refused, words: "The stage is locked by the showcaller.");
        Assert.Equal(("exec-2", (bool?)false), (host.Receipts[^1].Execution, host.Receipts[^1].Ok));
        Assert.Contains("was not cleared (The stage is locked by the showcaller.)", host.Receipts[^1].Words, StringComparison.Ordinal);

        // Unconfirmed: the row waits with words — and the frame that shows nothing of this desk's on that stage is its yes.
        Assert.Equal(ActionStatus.Requested, stage.Clear("exec-3").Status);
        Answer(stage, host.Link.Asked[^1], LinkOutcome.Unconfirmed, words: "no answer in 4 s");
        Assert.Equal(("exec-3", (bool?)null, StageSpeakerWords.ClearWaiting(Name, LinkOutcome.Unconfirmed)), host.Receipts[^1]);
        stage.Follow();
        Assert.Null(host.Receipts[^1].Ok);                                               // still there: still waiting
        host.Sight = Stage(StageMessageSeen.None);
        stage.Follow();
        Assert.Equal(("exec-3", (bool?)true), (host.Receipts[^1].Execution, host.Receipts[^1].Ok));
        Assert.Equal(("applied", StageSpeakerWords.ClearSeenRow(Name), false), host.Rows[^1]);
        var settled = host.Receipts.Count;
        stage.Follow();
        Assert.Equal(settled, host.Receipts.Count);                                      // once

        // A clear no cue asked owes no row.
        host.Sight = Stage(ours);
        Assert.Equal(ActionStatus.Requested, stage.Clear().Status);
        Answer(stage, host.Link.Asked[^1], LinkOutcome.Applied);
        Assert.Equal(settled, host.Receipts.Count);
    }

    // ---- the display's ACK ---------------------------------------------------------------------------------------------

    [Fact]
    public void ADisplayOffersTheStagewatchsAckOnlyWhereItCanGiveOneAndSaysWhyWhereItCannot()
    {
        var (host, stage) = Following();
        host.Sight = Stage(Signal(7, "Please wrap up", ShowClock.Seconds));
        Assert.Equal(("sw:7", true, ""), (stage.SpeakerShown()!.Id, stage.SpeakerShown()!.Ack, stage.SpeakerShown()!.Why));

        // The link cannot carry it — no key, a hold: no button, and the words say where it is answered.
        host.Link!.Route = new SpeakerRoute(false, Name, "the link holds no node key");
        var keyless = stage.SpeakerShown()!;
        Assert.Equal(("sw:7", false, StageSpeakerWords.NoAck(Name, "the link holds no node key")), (keyless.Id, keyless.Ack, keyless.Why));
        Assert.Equal($"Answer it on Stagewatch {Name}'s own screen — the link holds no node key", keyless.Why);
        Assert.False(stage.AckDisplay("sw:7").Ok);
        host.Link.Route = new SpeakerRoute(true, Name, "");

        // A node: in step with its desk the press goes there; alone it has nowhere to go.
        host.Link = null;
        host.AcksThroughDesk = true;
        Assert.True(stage.SpeakerShown()!.Ack);
        host.AcksThroughDesk = false;
        Assert.Equal((false, StageSpeakerWords.NoAckHere(Name)), (stage.SpeakerShown()!.Ack, stage.SpeakerShown()!.Why));
        host.Link = new Carrier();

        // The Stagewatch refused this desk's ACK at its door: the button gives way to its words for that signal, a second
        // press sends nothing — and the next message's ACK is its own.
        Assert.Equal(ActionStatus.Requested, stage.AckDisplay("sw:7").Status);
        Answer(stage, host.Link.Asked[^1], LinkOutcome.Refused, words: StagewatchMessageBytes.CrewLinks);
        var refused = stage.SpeakerShown()!;
        Assert.Equal((false, StageSpeakerWords.NoAck(Name, StageSpeakerWords.AckRefusedWhy(StagewatchMessageBytes.CrewLinks))), (refused.Ack, refused.Why));
        var again = stage.AckDisplay("sw:7");
        Assert.False(again.Ok);
        Assert.Contains(StagewatchMessageBytes.CrewLinks, again.Message, StringComparison.Ordinal);
        Assert.Single(host.Link.Asked);
        host.Sight = Stage(Signal(8, "Please wrap up now", ShowClock.Seconds));
        stage.Follow();
        Assert.Equal(("sw:8", true, ""), (stage.SpeakerShown()!.Id, stage.SpeakerShown()!.Ack, stage.SpeakerShown()!.Why));

        // A rate limit's refusal is the route's to say, for as long as its hold stands: nothing is held past it.
        Assert.Equal(ActionStatus.Requested, stage.AckDisplay("sw:8").Status);
        Answer(stage, host.Link.Asked[^1], LinkOutcome.Refused, words: "rate limited — again in 3 s", retryAfter: 3);
        Assert.True(stage.SpeakerShown()!.Ack);

        // A message on this desk's displays is answered here, as ever.
        host.Follows = false;
        stage.Message("speaker", "Local", "desk");
        Assert.Equal((true, "", StageSpeakerChoice.FromDesk), (stage.SpeakerShown()!.Ack, stage.SpeakerShown()!.Why, stage.SpeakerShown()!.From));
    }

    // ---- a run that holds no call for a message ------------------------------------------------------------------------

    private static StageMessage Left(Host host, string text, string step, long id = 0, string execution = "")
    {
        var m = new StageMessage { Text = text, From = "desk", SentUtc = host.Clock.UtcNow.AddSeconds(-20), Via = StageSpeakerWords.Via, SwAck = step, SwName = Name, SwRequestId = "p7-old", SwMessageId = id, Execution = execution };
        host.AirState.Stage.Messages.Add(m);
        return m;
    }

    [Fact]
    public void AMessageARunBeforeThisOneLeftReadsUnconfirmedAndADeskThatMirrorsAnotherMovesNothing()
    {
        var (host, stage) = Following();
        var requested = Left(host, "Requested before", StageSpeakerWords.Requested, execution: "exec-old");
        var accepted = Left(host, "Accepted before", StageSpeakerWords.Accepted);
        var bound = Left(host, "Bound before", StageSpeakerWords.Sent, id: 7);

        // A standby mirrors the main's stage: the main moves these, and nothing moves here.
        host.OwnsTheStage = false;
        stage.Follow();
        Assert.Equal((StageSpeakerWords.Requested, StageSpeakerWords.Accepted, StageSpeakerWords.Sent), (requested.SwAck, accepted.SwAck, bound.SwAck));
        Assert.Empty(host.Rows);

        // This desk's own — it restarted, or took the show over: no call of this run will answer them. Requested and accepted
        // read unconfirmed, on this desk's displays, said once; the bound one is not on the Stagewatch's stage, and whether it
        // was answered is not known — never "left unanswered".
        host.OwnsTheStage = true;
        stage.Follow();
        Assert.Equal((StageSpeakerWords.Unconfirmed, StageSpeakerWords.RestartedWhy), (requested.SwAck, requested.SwWords));
        Assert.Equal((StageSpeakerWords.Unconfirmed, StageSpeakerWords.RestartedWhy), (accepted.SwAck, accepted.SwWords));
        Assert.Equal(2, host.Rows.Count(r => r is ("unconfirmed", _, true) && r.Words == StageSpeakerWords.RestartedRow(Name)));
        Assert.Equal((StageSpeakerWords.Cleared, ("cleared", StageSpeakerWords.LostRow(Name, 7), false)), (bound.SwAck, host.Rows[^1]));
        Assert.Equal(("exec-old", (bool?)null, StageSpeakerWords.CueWaiting(Name, LinkOutcome.Unconfirmed)), Assert.Single(host.Receipts));
        Assert.Equal(accepted.Id, stage.SpeakerShown()!.Id);                             // the later of the two, on this desk's displays
        Assert.Equal("SPEAKER MESSAGE △ UNCONFIRMED", stage.SpeakerGlance);
        var rows = host.Rows.Count;
        stage.Follow();
        Assert.Equal(rows, host.Rows.Count);

        // And a frame that names one of them binds it after all.
        host.Sight = Stage(Signal(9, "Requested before", ShowClock.Seconds));
        stage.Follow();
        Assert.Equal((StageSpeakerWords.Sent, 9L), (requested.SwAck, requested.SwMessageId));
        Assert.Equal(("exec-old", (bool?)true), (host.Receipts[^1].Execution, host.Receipts[^1].Ok));
    }

    [Fact]
    public void ABoundMessageNoFrameShowsHasLeftOnceAReceiptsWaitHasPassed()
    {
        var (host, stage) = Following();
        stage.BindWithin = TimeSpan.FromMilliseconds(40);
        var (m, call) = Send(host, stage, "Never shown");
        Answer(stage, call, LinkOutcome.Applied, id: 99);
        // Frames keep coming and none shows it — the Stagewatch restarted, its signals counted from one again.
        host.Sight = Stage(Signal(1, "Their own", ShowClock.Seconds));
        stage.Follow();
        Assert.Equal(StageSpeakerWords.Sent, m.SwAck);                                   // not yet: its frame may be a moment behind its receipt
        Thread.Sleep(80);
        stage.Follow();
        Assert.Equal(StageSpeakerWords.Sent, m.SwAck);                                   // the last frame is older than the wait: nothing new says so
        host.Sight = Stage(host.Sight.Message);
        stage.Follow();
        Assert.Equal(StageSpeakerWords.Cleared, m.SwAck);
        Assert.Equal(("cleared", StageSpeakerWords.LeftRow(Name, 99), false), host.Rows[^1]);
        Assert.Null(stage.Pending("speaker"));
    }

    // ---- a message that stays here says why ----------------------------------------------------------------------------

    [Fact]
    public void AMessageThatStaysHereWhileAStagewatchIsLinkedSaysWhich()
    {
        var (host, stage) = Following();

        // The crew's channel is this desk's own: said while the stage follows the Stagewatch, and never posted.
        var crew = stage.Message("crew", "Mic 2 is live", "desk");
        Assert.Equal((ActionStatus.Done, $"To the crew: 'Mic 2 is live' — on this desk's displays only; Stagewatch {Name}'s displays do not show it (the crew's channel is this desk's own)."), (crew.Status, crew.Message));
        // FOLLOW off with a Stagewatch linked: the speaker's message is this desk's, and the reply says the stage does not follow it.
        host.Follows = false;
        var unfollowed = stage.Message("speaker", "Not following", "desk");
        Assert.Equal((ActionStatus.Done, StageSpeakerWords.DeskOnlyReply(Name, "Not following", StageSpeakerWords.NotFollowedWhy)), (unfollowed.Status, unfollowed.Message));
        var plain = stage.Message("crew", "Stand by", "desk");
        Assert.Equal("To the crew: 'Stand by' — a receipt comes when the display's ACK is pressed.", plain.Message);
        Assert.Empty(host.Link!.Asked);
        Assert.All(host.AirState.Stage.Messages, m => Assert.Equal("", m.Via));

        // No Stagewatch linked: the words every desk has had.
        host.Link.Route = new SpeakerRoute(false, "", "");
        var alone = stage.Message("speaker", "Alone", "desk");
        Assert.Equal("To the speaker: 'Alone' — a receipt comes when the display's ACK is pressed.", alone.Message);

        // The link could not take it as it left: here, with that reason.
        host.Follows = true;
        host.Link.Route = new SpeakerRoute(true, Name, "");
        host.Link.Takes = false;
        var untaken = stage.Message("speaker", "Untaken", "desk");
        Assert.Equal(ActionStatus.Done, untaken.Status);
        Assert.Contains($"the link to Stagewatch {Name} could not take it as it left", untaken.Message, StringComparison.Ordinal);
    }
}
