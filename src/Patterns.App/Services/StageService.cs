using System.Globalization;
using System.Text.Json;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// The stage: a professional stage timer and messages to stage on the countdown's own clock — one
/// clock for the wall, the confidence screen, the stage page and a caller node. The timer's verbs
/// (pause with what is left kept, resume, seconds either way, a flash), the messages to the
/// speaker's display or the crew's with a receipt when the display's ACK is pressed, and the
/// payload the stage and timer pages long-poll.
/// <para>
/// One speaker message in the room. While the stage follows a Stagewatch and its link can carry it
/// (<see cref="IStageHost.Speaker"/>), a message to the speaker goes through the Stagewatch: kept here marked as sent that way,
/// <c>Requested</c>, and moved only by its receipt (Show Bus 1.3 §13.12.3's five outcomes) and then by the Stagewatch's frames —
/// its signal's id, its acknowledgement and who gave it. Otherwise it stays on this desk's displays, <c>Done</c>, with the reason.
/// The pages draw one speaker message, the later of the Stagewatch's and one on this desk's displays alone
/// (<see cref="SpeakerShown"/>); no step is taken from a clock but 13.12.4's, and none sends a message again.
/// </para>
/// </summary>
public sealed class StageService
{
    public const int MessagesKept = 50;

    private readonly IStageHost _s;
    private long _rev;
    private bool _hooked;

    public StageService(IStageHost host)
    {
        _s = host;
        UtcNow = () => host.Clock.UtcNow;      // the desk's frame: on a follower the link's measured offset is in it
        _s.SnapshotPublished += Bump;           // the countdown or the stage section moved: the pages read again
    }

    /// <summary>The clock the pages and the tests read; the tests move it.</summary>
    public Func<DateTime> UtcNow { get; set; } = () => DateTime.UtcNow;

    /// <summary>Bumped on every change the pages should see; the long-poll waits on it.</summary>
    public long Rev => Interlocked.Read(ref _rev);

    public void Bump() => Interlocked.Increment(ref _rev);

    private void Hook()
    {
        if (_hooked || _s.CueStack is null) return;
        _hooked = true;
        _s.CueStack.Changed += Bump;            // the segment follows the stack
    }

    private DateTime LocalNow => UtcNow().ToLocalTime();

    /// <summary>
    /// The timer as it stands: a linked Stagewatch's time while it answers (round 87 — one room, one timer), else this
    /// desk's countdown on its own clock. A Stagewatch gone quiet holds the time back (idle) rather than freezing a
    /// number on the speaker's screen; past the drop it is forgotten and this desk's own timer runs.
    /// </summary>
    public StageTime Time()
    {
        var follow = _s.FollowedStagewatch;
        if (follow.Frame is { } frame && follow.State != ShowBusState.Gone)
            return follow.State == ShowBusState.Live ? StageFollow.Time(frame, follow.FrameAtSeconds, ShowClock.Seconds) : StageTime.Idle;
        return StageTimer.Evaluate(_s.AirState.Countdown, _s.AirState.Stage, LocalNow, UtcNow());
    }

    /// <summary>Round 87: the Stagewatch the stage follows, by name — "" when it runs its own.</summary>
    public string Following => _s.FollowedStagewatch is { Frame: not null, State: not ShowBusState.Gone } f ? f.Name : "";

    /// <summary>Round 87: the refusal for a verb that would move this desk's timer while it follows a Stagewatch's.</summary>
    private ActionResult? FollowingRefusal(string verb) => Following is { Length: > 0 } name
        ? ActionResult.Refused($"The stage timer follows Stagewatch {name} — {verb} it there (Nodes page: the link).")
        : null;

    public ActionResult Pause()
    {
        if (FollowingRefusal("pause") is { } refused) return refused;
        var ok = false;
        _s.EditAir(air => ok = StageTimer.Pause(air.Countdown, air.Stage, LocalNow, UtcNow()));
        Bump();
        return ok ? ActionResult.Done($"Stage timer paused at {Time().Text}.") : ActionResult.Refused("No timer is running to pause.");
    }

    public ActionResult Resume()
    {
        if (FollowingRefusal("resume") is { } refused) return refused;
        var ok = false;
        _s.EditAir(air => ok = StageTimer.Resume(air.Countdown, air.Stage, UtcNow()));
        Bump();
        return ok ? ActionResult.Done($"Stage timer running: {Time().Text}.") : ActionResult.Refused("Nothing is paused.");
    }

    /// <summary>"+60", "-30", "2m": seconds onto what is left.</summary>
    public ActionResult Add(string words)
    {
        if (FollowingRefusal("add time to") is { } refused) return refused;
        var seconds = StageTimer.ParseSeconds(words);
        if (seconds is null) return ActionResult.Refused($"'{words}' is not a number of seconds (+60, -30, 2m).");
        var ok = false;
        _s.EditAir(air => ok = StageTimer.Add(air.Countdown, air.Stage, seconds.Value, LocalNow, UtcNow()));
        Bump();
        return ok ? ActionResult.Done($"{(seconds >= 0 ? "+" : "")}{seconds:0} s — the stage timer reads {Time().Text}.") : ActionResult.Refused("No timer is running to add to.");
    }

    /// <summary>The displays flash for three seconds — "look up" — with no message.</summary>
    public ActionResult Flash()
    {
        _s.EditAir(air => air.Stage.FlashUntilUtc = UtcNow().AddSeconds(3));
        Bump();
        return ActionResult.Done("The stage displays flash.");
    }

    /// <summary>
    /// Words to the speaker's display (or the crew's), with a receipt to come. A speaker's message goes through the Stagewatch the
    /// stage follows when its link can carry it — <c>Requested</c>, its receipt and the speaker's ACK coming back here — and stays
    /// on this desk's displays, <c>Done</c> with the reason, when it cannot: a message always lands somewhere.
    /// <paramref name="execution"/>: the cue execution sending it, whose row waits for the receipt.
    /// </summary>
    public ActionResult Message(string channel, string text, string from, bool flash = true, string execution = "")
    {
        text = (text ?? "").Trim();
        if (text.Length == 0) return ActionResult.Refused("A message to stage needs words.");
        var message = new StageMessage { Text = text, Channel = channel, From = from, SentUtc = UtcNow(), Flash = flash };
        // The link's Stagewatch, followed or not: a message that stays here while one is linked says which, and why.
        var carrier = _s.Speaker;
        var route = carrier?.Route ?? default;
        var sight = _s.FollowedStagewatch;
        var follows = sight.Name.Length > 0 || sight.Address.Length > 0;
        var deskOnly = "";
        SpeakerCall? call = null;
        if (route.Name is { Length: > 0 })
        {
            if (message.Channel != "speaker")
            {
                deskOnly = follows && message.Channel == "crew" ? StageSpeakerWords.CrewWhy : "";
            }
            else if (!follows)
            {
                deskOnly = StageSpeakerWords.NotFollowedWhy;
            }
            else
            {
                var path = StageMessagePath.For(text, flash);
                deskOnly = !route.Can ? route.Why : path is null ? StageMessagePath.WhyNot(text) : "";
                if (deskOnly.Length == 0)
                {
                    call = carrier!.Ask(SpeakerCall.Message, path!, message.Id);
                    if (call is null) deskOnly = $"the link to Stagewatch {route.Name} could not take it as it left";
                }
            }
        }
        if (call is not null)
        {
            message.Via = StageSpeakerWords.Via;
            message.SwAck = StageSpeakerWords.Requested;
            message.SwName = route.Name;
            message.SwRequestId = call.Id;
            message.Execution = execution;
        }
        _leftAt[message.Id] = ShowClock.Seconds;
        _s.EditAir(air =>
        {
            air.Stage.Messages.Add(message);
            while (air.Stage.Messages.Count > MessagesKept) air.Stage.Messages.RemoveAt(0);
        });
        Forget();
        Bump();
        if (call is not null) return ActionResult.Requested(StageSpeakerWords.ViaReply(route.Name, text));
        return ActionResult.Done(deskOnly.Length > 0
            ? StageSpeakerWords.DeskOnlyReply(route.Name, text, deskOnly, message.Channel)
            : $"To the {message.Channel}: '{text}' — a receipt comes when the display's ACK is pressed.");
    }

    /// <summary>
    /// The unseen messages off the displays: marked seen by the desk, so the receipts read "cleared". The Stagewatch's speaker
    /// screen is cleared too only of a message this desk posted: one its frame names now — answered or not, since an answered
    /// message stays on that stage until it is cleared — or one its receipt bound there before a frame named it, while that
    /// receipt is fresh and no message has come on that stage since, asked at once; or one still on its way there, asked behind
    /// it and dropped unasked if that message never reaches the Stagewatch. The Stagewatch's own message is not this desk's to
    /// clear, and the words say it stays; a message it has not confirmed may still reach its screen, and a bound one no frame has
    /// shown and nothing asked about may still be on it, and the words say that.
    /// <paramref name="execution"/>: the cue execution clearing, whose row waits for the clear's receipt.
    /// </summary>
    public ActionResult Clear(string execution = "")
    {
        var n = 0;
        var now = UtcNow();
        var sight = _s.FollowedStagewatch;
        var seen = FollowsNow(sight) ? sight.Message : StageMessageSeen.None;
        var all = _s.AirState.Stage.Messages;
        var there = all.Any(m => IsOnTheirStage(m, seen) || IsBoundThereUnseen(m, seen));
        var maybe = !there && all.Any(m => IsBoundMaybeThere(m, seen));
        var onWay = all.Where(m => m.AckUtc is null && m.Via == StageSpeakerWords.Via && !IsOnTheirStage(m, seen)
                                   && m.SwAck is StageSpeakerWords.Requested or StageSpeakerWords.Accepted or StageSpeakerWords.Unconfirmed).ToList();
        var unanswered = onWay.Where(m => m.SwAck == StageSpeakerWords.Requested).Select(m => m.SwRequestId).ToList();
        var settled = new List<StageMessage>();
        _s.EditAir(air =>
        {
            foreach (var m in air.Stage.Messages.Where(m => m.AckUtc is null))
            {
                // A cue's message a receipt has left waiting is settled by this clear; one still unanswered settles on its answer.
                if (m.Execution.Length > 0 && m.SwAck is StageSpeakerWords.Accepted or StageSpeakerWords.Unconfirmed) settled.Add(m);
                m.AckUtc = now;
                m.SwAck = StageSpeakerWords.Cleared;
                n++;
            }
            air.Stage.FlashUntilUtc = null;
        });
        foreach (var m in settled) Settle(m, true, StageSpeakerWords.ClearedFirst);
        Bump();
        var words = n == 0 ? "Nothing on the displays to clear." : $"{n} message{(n == 1 ? "" : "s")} cleared from the displays.";
        if (sight.Name.Length == 0) return ActionResult.Done(words);
        var name = sight.Name;
        if (there || unanswered.Count > 0)
        {
            var route = _s.Speaker?.Route ?? default;
            var call = route.Can ? _s.Speaker!.Ask(SpeakerCall.Clear, StageMessagePath.Clear, "", there ? null : unanswered) : null;
            if (call is null)
            {
                var why = route.Why.Length > 0 ? route.Why : "its link could not take the clear";
                return ActionResult.Done(there
                    ? $"{words} This desk's message stays on Stagewatch {name}'s speaker screen ({why}) — clear it there."
                    : $"{words} This desk's message is still on its way to Stagewatch {name} and its clear could not be asked ({why}) — clear it there if it gets there.");
            }
            if (execution.Length > 0) _clearOwed[call.Id] = execution;
            return ActionResult.Requested(there
                ? $"{words} Stagewatch {name}'s speaker screen is asked to clear this desk's message — its receipt comes back here."
                : $"{words} This desk's message is still on its way to Stagewatch {name} — its speaker screen is asked to clear it behind it, and is not asked if the message never gets there.");
        }
        if (onWay.Count > 0)
            return ActionResult.Done($"{words} A message Stagewatch {name} has not confirmed may still reach its speaker's screen — clear it there if it does.");
        if (seen.On && !seen.Answered) words = $"{words} {StageSpeakerWords.OwnStays(name)}.";
        return ActionResult.Done(maybe ? $"{words} {StageSpeakerWords.MaybeStays(name)}." : words);
    }

    /// <summary>The display's ACK: the message is seen, and the desk knows when — the receipt's words, or null for a message that is not there to see.</summary>
    public string? Ack(string id)
    {
        StageMessage? found = null;
        _s.EditAir(air => found = air.Stage.Messages.FirstOrDefault(m => m.Id == id && m.AckUtc is null));
        if (found is null) return null;
        var now = UtcNow();
        _s.EditAir(_ => found.AckUtc = now);
        // A cue's message the Stagewatch never confirmed, seen here: its row waits on nothing more.
        if (found.SwAck is StageSpeakerWords.Accepted or StageSpeakerWords.Unconfirmed) Settle(found, true, StageSpeakerWords.SeenHereFirst);
        Bump();
        return $"Stage: '{found.Text}' seen at {now.ToLocalTime():HH:mm:ss}.";
    }

    /// <summary>
    /// A display's ACK as a verb: a message on this desk's displays is seen here; the Stagewatch's message (<c>sw:&lt;id&gt;</c>, the
    /// one the page drew) is answered there, in this desk's name — forwarded once, <c>Requested</c>, and its frame says when it is
    /// answered and by whom. An ACK for a signal that is not on its stage, or already answered, changes nothing and is refused.
    /// </summary>
    public ActionResult AckDisplay(string id)
    {
        if (StageSpeakerChoice.StagewatchSignal(id) is not { } signal)
            return Ack(id) is { } words ? ActionResult.Done(words) : ActionResult.Refused("no such message, or seen already");
        var sight = _s.FollowedStagewatch;
        var seen = sight.Message;
        if (!FollowsNow(sight) || signal <= 0 || seen.Id != signal || !seen.Asks) return ActionResult.Refused("no such message, or seen already");
        if (_ackAsked == signal) return ActionResult.Refused($"The ACK for message {signal} is already on its way to Stagewatch {sight.Name}.");
        if (_ackRefused.Signal == signal) return ActionResult.Refused($"The ACK was not sent — Stagewatch {sight.Name} refused this display's ACK ({_ackRefused.Words}), and takes none for this message from here.");
        if (_s.Speaker is not { } carrier)
            return ActionResult.Refused($"This display cannot answer Stagewatch {sight.Name}'s message — answer it on the desk's stage page, or on Stagewatch's own.");
        var route = carrier.Route;
        var call = route.Can ? carrier.Ask(SpeakerCall.Ack, StageMessagePath.Ack(signal, _s.DeskName)!, id) : null;
        if (call is null)
            return ActionResult.Refused($"The ACK was not sent — Stagewatch {sight.Name} cannot take it now ({(route.Why.Length > 0 ? route.Why : "its link could not take it")}).");
        _ackAsked = signal;
        return ActionResult.Requested($"ACK for message {signal} sent to Stagewatch {sight.Name} — its frame says when it is answered.");
    }

    /// <summary>The latest unseen message on a channel, or null.</summary>
    public StageMessage? Pending(string channel) => _s.AirState.Stage.Messages.LastOrDefault(m => m.Channel == channel && m.AckUtc is null);

    // ---- one speaker message --------------------------------------------------------------------------------------

    /// <summary>
    /// When each message sent this run left, on the master clock — never moved, whatever its route comes to: a frame binds an
    /// accepted or unconfirmed message only when its signal arrived after that, and a frame that came while the answer was
    /// still awaited is as good as one that came after.
    /// </summary>
    private readonly Dictionary<string, double> _leftAt = new(StringComparer.Ordinal);

    /// <summary>
    /// When a message whose route ended here — refused, unconfirmed, never sent — came onto this desk's displays, on the master
    /// clock: what the later-wins choice compares with the arrival of the Stagewatch's own message.
    /// </summary>
    private readonly Dictionary<string, double> _onDeskAt = new(StringComparer.Ordinal);

    /// <summary>When each accepted message's receipt came, on the master clock: 13.12.4's wait for the frame that names it runs from there.</summary>
    private readonly Dictionary<string, double> _acceptedAt = new(StringComparer.Ordinal);

    /// <summary>When each bound message's receipt or bind came, on the master clock: one no frame shows within <see cref="BindWithin"/> of it has left that stage.</summary>
    private readonly Dictionary<string, double> _boundAt = new(StringComparer.Ordinal);

    /// <summary>The messages a frame has shown on the Stagewatch's stage: one seen there and there no longer has left it.</summary>
    private readonly HashSet<string> _shownThere = new(StringComparer.Ordinal);

    /// <summary>The signal this desk last forwarded an ACK for: a second press sends no second ACK.</summary>
    private long _ackAsked;

    /// <summary>The signal whose forwarded ACK the Stagewatch refused, and its words: no display offers that ACK again until its frame moves.</summary>
    private (long Signal, string Words) _ackRefused;

    /// <summary>The cue executions owed the receipt of a clear asked of the Stagewatch, by the clear's request id.</summary>
    private readonly Dictionary<string, string> _clearOwed = new(StringComparer.Ordinal);

    /// <summary>The cue execution whose clear the Stagewatch accepted or did not confirm: a frame with nothing of this desk's on that stage settles it.</summary>
    private (string Execution, string Name)? _clearWaits;

    /// <summary>
    /// How long an accepted message waits for a frame to name it before it reads unconfirmed, and a bound one for a frame to
    /// show it before it is taken to have left (Show Bus 1.3 §14's <c>receipt.final.ms</c>, 13.12.4); a test shortens it.
    /// </summary>
    public TimeSpan BindWithin { get; set; } = PeerLink.ReceiptFinal;

    /// <summary>What this run remembers of messages the history no longer holds is let go.</summary>
    private void Forget()
    {
        var held = _s.AirState.Stage.Messages.Select(m => m.Id).ToHashSet(StringComparer.Ordinal);
        foreach (var book in new[] { _leftAt, _onDeskAt, _acceptedAt, _boundAt })
            foreach (var gone in book.Keys.Where(k => !held.Contains(k)).ToList()) book.Remove(gone);
        _shownThere.RemoveWhere(k => !held.Contains(k));
    }

    /// <summary>When a message left, on the master clock: as this run stamped it, else its own time read back against the stage's clock.</summary>
    private double LeftSeconds(StageMessage m)
        => _leftAt.TryGetValue(m.Id, out var at) ? at : ShowClock.Seconds - Math.Max(0, (UtcNow() - m.SentUtc).TotalSeconds);

    /// <summary>When a message came onto this desk's displays: as it left, or — its route through a Stagewatch ended here — when that ended.</summary>
    private double OnDeskSeconds(StageMessage m) => _onDeskAt.TryGetValue(m.Id, out var at) ? at : LeftSeconds(m);

    /// <summary>The stage follows a Stagewatch now: a frame is held and the link is not gone — its message is the room's.</summary>
    private static bool FollowsNow(StagewatchSight sight) => sight.Frame is not null && sight.State != ShowBusState.Gone;

    /// <summary>A message this desk sent is the one on the Stagewatch's stage now: its signal's id, or — bound to none — its words.</summary>
    private static bool IsOnTheirStage(StageMessage m, StageMessageSeen seen)
        => seen.On && m.Via == StageSpeakerWords.Via
           && (m.SwMessageId > 0 ? seen.Id == m.SwMessageId
               : m.SwAck is StageSpeakerWords.Sent or StageSpeakerWords.Accepted or StageSpeakerWords.Unconfirmed && seen.Text == m.Text);

    /// <summary>
    /// A message this desk sent that its applied receipt (or a bind) put on the Stagewatch's stage, that no frame has shown there
    /// and that <see cref="Follow"/> still holds as sent there — the answer comes on the call and the frame on the socket, either
    /// first. <paramref name="bound"/>: when its receipt or bind came, on the master clock.
    /// </summary>
    private bool IsBoundUnseen(StageMessage m, out double bound)
    {
        bound = 0;
        return m.AckUtc is null && m.Via == StageSpeakerWords.Via && m.SwAck == StageSpeakerWords.Sent
               && _boundAt.TryGetValue(m.Id, out bound) && !_shownThere.Contains(m.Id);
    }

    /// <summary>
    /// The Stagewatch's stage shows a message that is not this one and is surely later: a later signal than the one it was bound to.
    /// A message with no signal (id 0) — any its own operator types, any sent without an acknowledgement — orders against nothing.
    /// </summary>
    private static bool IsSurelyReplaced(StageMessage m, StageMessageSeen seen)
        => !IsOnTheirStage(m, seen) && m.SwMessageId > 0 && seen.Id > m.SwMessageId;

    /// <summary>
    /// A bound, unseen message a clear asks the Stagewatch's stage about at once: its receipt is within a receipt's wait, on the
    /// master clock, and the stage shows nothing that came after its bind but itself — a message that arrived after it, signal or
    /// not, may be the Stagewatch's own that replaced it, which is not this desk's to clear (a frame a moment behind its receipt
    /// may also be showing an older one: then it is unknown, and asked about by nobody).
    /// </summary>
    private bool IsBoundThereUnseen(StageMessage m, StageMessageSeen seen)
        => IsBoundUnseen(m, out var bound) && ShowClock.Seconds - bound <= BindWithin.TotalSeconds && !IsSurelyReplaced(m, seen)
           && !(seen.On && seen.AtSeconds > bound && !IsOnTheirStage(m, seen));

    /// <summary>
    /// A bound, unseen message no clear asks about and no later signal has replaced: it may still be on the Stagewatch's stage —
    /// a frame since its bind showed another message whose order is unknown, or frames have stopped past a receipt's wait while the
    /// link stands — so the clear's words say it may be there.
    /// </summary>
    private bool IsBoundMaybeThere(StageMessage m, StageMessageSeen seen)
        => IsBoundUnseen(m, out _) && !IsOnTheirStage(m, seen) && !IsSurelyReplaced(m, seen) && !IsBoundThereUnseen(m, seen);

    /// <summary>
    /// The one speaker message the pages draw, or null: while the stage follows a Stagewatch, the later of its message and a
    /// message on this desk's displays alone (<see cref="StageSpeakerChoice"/>); else this desk's latest unseen one, as ever.
    /// The Stagewatch's message offers its ACK only where this display can give one: where it cannot — no key, a hold, an ACK
    /// the Stagewatch refused — it says why in the button's place.
    /// </summary>
    public StageSpeakerShown? SpeakerShown()
    {
        var sight = _s.FollowedStagewatch;
        var following = FollowsNow(sight);
        var local = _s.AirState.Stage.Messages.LastOrDefault(m => m.Channel == "speaker" && m.AckUtc is null && (!following || StageSpeakerWords.OnDesk(m)));
        var shown = StageSpeakerChoice.Of(local, local is null ? 0 : OnDeskSeconds(local), sight.Message, following);
        if (shown is not { From: StageSpeakerChoice.FromStagewatch, Ack: true }) return shown;
        return AckWhyNot(sight) is { Length: > 0 } why ? shown with { Ack = false, Why = why } : shown;
    }

    /// <summary>
    /// Why this display cannot answer the Stagewatch's message now, in the words it shows; "" when it can — through this desk's
    /// link, or, on a node in step, through its desk, which answers for it.
    /// </summary>
    private string AckWhyNot(StagewatchSight sight)
    {
        if (_ackRefused.Signal != 0 && _ackRefused.Signal == sight.Message.Id) return StageSpeakerWords.NoAck(sight.Name, StageSpeakerWords.AckRefusedWhy(_ackRefused.Words));
        if (_s.Speaker is not { } carrier) return _s.AcksThroughDesk ? "" : StageSpeakerWords.NoAckHere(sight.Name);
        var route = carrier.Route;
        return route.Can ? "" : StageSpeakerWords.NoAck(sight.Name, route.Why.Length > 0 ? route.Why : "its link cannot take it now");
    }

    /// <summary>
    /// Where the speaker's message stands, for STATE and a deck: <c>From</c> — <c>stagewatch</c>, <c>desk</c> or "" with none;
    /// <c>Ack</c> — its receipt's word (<see cref="StageSpeakerWords.AckWord"/>, or the Stagewatch's own message's); <c>By</c> —
    /// who answered. This desk's latest speaker message, unless the Stagewatch's own arrived after it.
    /// </summary>
    public (string From, string Ack, string By) SpeakerNow()
    {
        var sight = _s.FollowedStagewatch;
        var following = FollowsNow(sight);
        var seen = following ? sight.Message : StageMessageSeen.None;
        var last = _s.AirState.Stage.Messages.LastOrDefault(m => m.Channel == "speaker");
        var theirs = seen.On && (last is null || (!IsOnTheirStage(last, seen) && seen.AtSeconds >= OnDeskSeconds(last)));
        if (theirs) return (StageSpeakerChoice.FromStagewatch, seen.Id > 0 ? seen.Ack : "", seen.By);
        if (last is null || StageSpeakerWords.AckWord(last) is not { Length: > 0 } word) return ("", "", "");
        return (StageSpeakerWords.OnStagewatch(last) ? StageSpeakerChoice.FromStagewatch : StageSpeakerChoice.FromDesk, word, last.SwAckBy);
    }

    /// <summary>
    /// Where this desk's last speaker message went and what came of it, in one line — the Eye's Stagewatch node and the
    /// assistant's facts read it; "" with none, or once it is cleared.
    /// </summary>
    public string SpeakerLine
    {
        get
        {
            var last = _s.AirState.Stage.Messages.LastOrDefault(m => m.Channel == "speaker");
            if (last is null) return "";
            var name = last.SwName.Length > 0 ? last.SwName : _s.FollowedStagewatch.Name;
            return StageSpeakerWords.Line(last, name);
        }
    }

    /// <summary>The Run strip's words for a speaker message whose route ended here and still waits: refused, unconfirmed or never sent; "" otherwise.</summary>
    public string SpeakerGlance => StageSpeakerWords.Glance(Pending("speaker"));

    /// <summary>
    /// The answer to one of the speaker's calls (UI thread), each said once: a message's receipt moves it by 13.12.3 — applied (its
    /// signal bound), accepted, refused, unconfirmed or not-sent — with one journal row, and settles the cue row that owes it; a
    /// clear's is journalled and settles the cue row that asked it; an ACK's is journalled, and one the Stagewatch refused is
    /// offered on no display again until its frame moves. A message cleared here meanwhile keeps its row and moves no further.
    /// </summary>
    public void OnSpeakerAnswer(SpeakerAnswer answer)
    {
        var (call, receipt, name) = (answer.Call, answer.Receipt, answer.Name);
        var fault = receipt.Outcome is LinkOutcome.Refused or LinkOutcome.NotSent or LinkOutcome.Unconfirmed;
        if (call.Kind == SpeakerCall.Clear)
        {
            var nothing = receipt.Outcome == LinkOutcome.NotSent && receipt.Words == StageSpeakerWords.NothingToClearWhy;
            _s.StageJournal("stage", receipt.Outcome.Word(), StageSpeakerWords.ClearRow(name, receipt), fault && !nothing);
            if (!_clearOwed.Remove(call.Id, out var owed)) return;
            switch (receipt.Outcome)
            {
                case LinkOutcome.Applied:
                    _s.SpeakerReceipt(owed, true, $"Stagewatch {name}'s speaker screen is cleared of this desk's message");
                    break;
                case LinkOutcome.NotSent when nothing:
                    _s.SpeakerReceipt(owed, true, $"nothing of this desk's reached Stagewatch {name}'s speaker screen to clear");
                    break;
                case LinkOutcome.Refused or LinkOutcome.NotSent:
                    _s.SpeakerReceipt(owed, false, $"Stagewatch {name}'s speaker screen was not cleared ({receipt.Words}) — this desk's message may still be on it");
                    break;
                default:
                    _s.SpeakerReceipt(owed, null, StageSpeakerWords.ClearWaiting(name, receipt.Outcome));
                    _clearWaits = (owed, name);
                    Follow();
                    break;
            }
            return;
        }
        if (call.Kind == SpeakerCall.Ack)
        {
            var signal = StageSpeakerChoice.StagewatchSignal(call.Tag) ?? 0;
            if (fault && _ackAsked == signal) _ackAsked = 0;                              // it may be pressed again
            _s.StageJournal("stage display", receipt.Outcome.Word(), StageSpeakerWords.AckRow(name, signal, receipt), fault);
            // Refused for the signal still on that stage — and not by a rate limit, whose wait the route says: the button gives way to the words.
            if (receipt.Outcome == LinkOutcome.Refused && receipt.RetryAfter <= 0 && signal > 0 && _s.FollowedStagewatch.Message is { Answered: false } on && on.Id == signal)
            {
                _ackRefused = (signal, receipt.Words);
                Bump();
            }
            return;
        }
        var m = _s.AirState.Stage.Messages.FirstOrDefault(x => x.Id == call.Tag && x.SwRequestId == call.Id);
        if (m is null) return;
        var superseded = receipt.Outcome == LinkOutcome.NotSent && receipt.Words == StageSpeakerWords.SupersededWhy;
        _s.StageJournal(m.From, receipt.Outcome.Word(), superseded ? StageSpeakerWords.SupersededRow : StageSpeakerWords.ReceiptRow(name, receipt, cleared: m.AckUtc is not null), fault && !superseded);
        var now = UtcNow();
        if (m.SwAck == StageSpeakerWords.Requested)
        {
            _s.EditAir(_ =>
            {
                switch (receipt.Outcome)
                {
                    case LinkOutcome.Applied:
                        m.SwMessageId = receipt.MessageId;
                        m.SwAck = StageSpeakerWords.Sent;
                        break;
                    case LinkOutcome.Accepted:
                        m.SwAck = StageSpeakerWords.Accepted;
                        break;
                    case LinkOutcome.Refused:
                        m.SwAck = StageSpeakerWords.Refused;
                        m.SwWords = receipt.Words;
                        break;
                    case LinkOutcome.NotSent when superseded:
                        // It gives way to the message that replaced it: off the displays, never shown as waiting.
                        m.SwAck = StageSpeakerWords.Cleared;
                        m.SwWords = receipt.Words;
                        m.AckUtc = now;
                        break;
                    case LinkOutcome.NotSent:
                        m.SwAck = StageSpeakerWords.NotSent;
                        m.SwWords = receipt.Words;
                        break;
                    default:
                        m.SwAck = StageSpeakerWords.Unconfirmed;
                        m.SwWords = receipt.Words;
                        break;
                }
            });
            if (receipt.Outcome == LinkOutcome.Accepted) _acceptedAt[m.Id] = ShowClock.Seconds;
            if (receipt.Outcome == LinkOutcome.Applied) _boundAt[m.Id] = ShowClock.Seconds;
            if (StageSpeakerWords.OnDesk(m)) _onDeskAt[m.Id] = ShowClock.Seconds;            // on this desk's displays from now; when it left stands
        }
        // The cue that sent it is owed this receipt: a yes, a no, or words while it settles nothing.
        switch (receipt.Outcome)
        {
            case LinkOutcome.Applied:
                Settle(m, true, "");
                break;
            case LinkOutcome.Refused:
                Settle(m, false, $"Stagewatch {name} refused the message ({receipt.Words}) — its displays do not show it");
                break;
            case LinkOutcome.NotSent:
                Settle(m, false, $"the message was not sent to Stagewatch {name} ({receipt.Words}) — its displays do not show it");
                break;
            default:
                // Cleared here before this answer came, nothing is left to bind it: the row settles on the clear, in its words.
                if (m.AckUtc is not null) Settle(m, true, StageSpeakerWords.ClearedFirst);
                else if (m.Execution.Length > 0) _s.SpeakerReceipt(m.Execution, null, StageSpeakerWords.CueWaiting(name, receipt.Outcome));
                break;
        }
        Follow();
        Bump();
    }

    /// <summary>The cue row that owes this message settles on a yes or a no, once; <paramref name="words"/> — what the row says of it beyond the outcome.</summary>
    private void Settle(StageMessage m, bool ok, string words)
    {
        if (m.Execution.Length == 0) return;
        var execution = m.Execution;
        _s.EditAir(_ => m.Execution = "");
        _s.SpeakerReceipt(execution, ok, words);
    }

    /// <summary>
    /// The Stagewatch's frames read against this desk's messages (UI thread; each frame, and each second): an accepted or
    /// unconfirmed message is bound when a frame names a signal that arrived after it left with its words — never by a new id
    /// alone, which a message the Stagewatch's own operator sent in the gap would carry; an accepted one no frame names within
    /// <see cref="BindWithin"/> reads unconfirmed (13.12.4); a bound one is seen when its signal is answered, by whom the frame
    /// says; and one that left the Stagewatch's stage unanswered — cleared or replaced there, or never shown there within
    /// <see cref="BindWithin"/> of its receipt — is off the displays. A message this run holds no call for (the desk restarted,
    /// or took the show over, between the message and its receipt) reads unconfirmed rather than requested for good. One journal
    /// row for each. A desk that mirrors another's show moves none of them: they are the other desk's to move.
    /// </summary>
    public void Follow()
    {
        var sight = _s.FollowedStagewatch;
        var following = FollowsNow(sight);
        var seen = following ? sight.Message : StageMessageSeen.None;
        var name = sight.Name;
        var moved = false;
        if (_ackAsked != 0 && (seen.Id != _ackAsked || seen.Answered)) _ackAsked = 0;   // answered, or gone: nothing waits on that press
        if (_ackRefused.Signal != 0 && (seen.Id != _ackRefused.Signal || seen.Answered))
        {
            _ackRefused = default;                                                       // the frame moved: the next message's ACK is its own
            moved = true;
        }
        if (!_s.OwnsTheStage)
        {
            if (moved) Bump();
            return;
        }
        foreach (var m in _s.AirState.Stage.Messages.Where(m => m.Via == StageSpeakerWords.Via && m.AckUtc is null).ToList())
        {
            var to = m.SwName.Length > 0 ? m.SwName : name;
            var ours = _leftAt.ContainsKey(m.Id);                                        // this run sent it, and answers its call
            if (!ours && m.SwAck is StageSpeakerWords.Requested or StageSpeakerWords.Accepted)
            {
                _s.EditAir(_ =>
                {
                    m.SwAck = StageSpeakerWords.Unconfirmed;
                    m.SwWords = StageSpeakerWords.RestartedWhy;
                });
                _onDeskAt[m.Id] = ShowClock.Seconds;
                _s.StageJournal(m.From, "unconfirmed", StageSpeakerWords.RestartedRow(to), true);
                if (m.Execution.Length > 0) _s.SpeakerReceipt(m.Execution, null, StageSpeakerWords.CueWaiting(to, LinkOutcome.Unconfirmed));
                moved = true;
            }
            if (m.SwAck is StageSpeakerWords.Accepted or StageSpeakerWords.Unconfirmed)
            {
                var named = following && seen.Id > 0 && seen.Text == m.Text && seen.AtSeconds >= LeftSeconds(m)
                            && !_s.AirState.Stage.Messages.Any(o => !ReferenceEquals(o, m) && o.SwMessageId == seen.Id);
                if (named)
                {
                    _s.EditAir(_ =>
                    {
                        m.SwMessageId = seen.Id;
                        m.SwAck = StageSpeakerWords.Sent;
                        m.SwWords = "";
                    });
                    _acceptedAt.Remove(m.Id);
                    _onDeskAt.Remove(m.Id);
                    _boundAt[m.Id] = ShowClock.Seconds;
                    _s.StageJournal(m.From, "applied", StageSpeakerWords.BoundRow(to, seen.Id), false);
                    Settle(m, true, "");
                    moved = true;
                }
                else if (m.SwAck == StageSpeakerWords.Accepted && _acceptedAt.TryGetValue(m.Id, out var at) && ShowClock.Seconds - at > BindWithin.TotalSeconds)
                {
                    _s.EditAir(_ =>
                    {
                        m.SwAck = StageSpeakerWords.Unconfirmed;
                        m.SwWords = "no frame named it";
                    });
                    _acceptedAt.Remove(m.Id);
                    _onDeskAt[m.Id] = ShowClock.Seconds;
                    _s.StageJournal(m.From, "unconfirmed", StageSpeakerWords.NoBindRow(to), true);
                    if (m.Execution.Length > 0) _s.SpeakerReceipt(m.Execution, null, StageSpeakerWords.CueWaiting(to, LinkOutcome.Unconfirmed));
                    moved = true;
                }
            }
            if (m.SwAck != StageSpeakerWords.Sent || !following) continue;
            if (IsOnTheirStage(m, seen))
            {
                _shownThere.Add(m.Id);
                if (m.SwMessageId <= 0 || !seen.Answered) continue;
                var now = UtcNow();
                _s.EditAir(_ =>
                {
                    m.AckUtc = now;
                    m.SwAck = StageSpeakerWords.Done;
                    m.SwAckBy = seen.By;
                });
                _s.StageJournal(m.From, "done", StageSpeakerWords.SeenRow(m.SwMessageId, seen.By, now.ToLocalTime()), false);
                moved = true;
                continue;
            }
            // Not on that stage. It left if a frame once showed it there, or a later signal has replaced it; if no frame has shown
            // it since its receipt for as long as a receipt is waited on; or — a run before this one sent it — as soon as a frame says so.
            var shown = _shownThere.Contains(m.Id) || (m.SwMessageId > 0 && seen.Id > m.SwMessageId);
            var never = ours && _boundAt.TryGetValue(m.Id, out var bound) && sight.FrameAtSeconds - bound > BindWithin.TotalSeconds;
            if (!shown && !never && ours) continue;
            var left = UtcNow();
            _s.EditAir(_ =>
            {
                m.AckUtc = left;
                m.SwAck = StageSpeakerWords.Cleared;
            });
            _s.StageJournal(m.From, "cleared", shown || never ? StageSpeakerWords.LeftRow(to, m.SwMessageId) : StageSpeakerWords.LostRow(to, m.SwMessageId), false);
            moved = true;
        }
        // A cue's clear the Stagewatch accepted or did not confirm: a frame with nothing of this desk's on that stage is its yes.
        if (_clearWaits is { } wait && following && !_s.AirState.Stage.Messages.Any(m => IsOnTheirStage(m, seen)))
        {
            _clearWaits = null;
            _s.StageJournal("stage", "applied", StageSpeakerWords.ClearSeenRow(wait.Name), false);
            _s.SpeakerReceipt(wait.Execution, true, $"Stagewatch {wait.Name}'s speaker screen shows nothing of this desk's");
            moved = true;
        }
        if (moved) Bump();
    }

    /// <summary>
    /// Round 89: the Stagewatch the stage follows says its show is stuck — the stage follows it and shows no time, so the
    /// words say why rather than "idle" or "no timer".
    /// </summary>
    private static bool FollowsAStuckShow(StagewatchSight f) => f is { StuckLive: true, Frame: not null };

    /// <summary>What a speaker's display says under its blank time: no timer, or why a followed Stagewatch's is not shown.</summary>
    public string NoTimeLabel => _s.FollowedStagewatch is var f && FollowsAStuckShow(f)
        ? $"No time shown — Stagewatch {f.Name} says its show is stuck"
        : "No timer running";

    /// <summary>The Countdown page's line: the timer, and what waits on a receipt.</summary>
    public string StatusLine
    {
        get
        {
            var follow = _s.FollowedStagewatch;
            var time = Time();
            string timer;
            if (FollowsAStuckShow(follow))
            {
                timer = $"Stage timer shows no time — following Stagewatch {follow.Name}, which says its show is stuck";
            }
            else
            {
                timer = time.Phase switch
                {
                    StageTimerPhase.Idle => "Stage timer idle",
                    StageTimerPhase.Paused => $"Stage timer PAUSED at {time.Text}",
                    StageTimerPhase.Over => $"Stage timer OVER by {StageTimer.Format(-time.RemainingSeconds)}",
                    _ => $"Stage timer {time.Text} ({time.Colour})",
                };
                if (Following is { Length: > 0 } name) timer += $" — following Stagewatch {name}";
                else if (follow.StuckLive) timer += " — " + StageFollow.Words(follow);               // its stall said before its first frame: this desk's own timer
            }
            var unseen = _s.AirState.Stage.Messages.Count(m => m.AckUtc is null);
            var line = unseen == 0 ? timer + "." : $"{timer} · {unseen} message{(unseen == 1 ? "" : "s")} waiting on a receipt.";
            // A speaker's message sent through a Stagewatch: where it stands, in the words every other reader has.
            var last = _s.AirState.Stage.Messages.LastOrDefault(m => m.Channel == "speaker");
            return last is { Via: StageSpeakerWords.Via } && SpeakerLine is { Length: > 0 } speaker ? $"{line} {char.ToUpperInvariant(speaker[0])}{speaker[1..]}." : line;
        }
    }

    /// <summary>STAGE STATUS, and the pages' payload: the timer, the segment, the clock, the messages with their receipts.</summary>
    /// <summary>The stage as a deck's key reads it: the timer in its own colour, the segment and the next, the messages waiting for their ACK, the flash — the block STATE carries.</summary>
    public object Block()
    {
        Hook();
        var air = _s.AirState;
        var time = Time();
        var stack = _s.CueStack;
        var running = stack?.LastCue;
        var next = stack?.StandbyCue;
        var speaker = SpeakerNow();
        return new
        {
            timer = new
            {
                phase = time.Phase.ToString().ToLowerInvariant(),
                remaining = Math.Round(time.RemainingSeconds, 1),
                text = time.Text,
                colour = time.Colour,
                progress = Math.Round(time.Progress01, 4),
                label = air.Countdown.Label,
                paused = air.Stage.Paused,
            },
            segment = running is null ? "" : $"{running.Number} {running.Name}".Trim(),
            next = next is null ? "" : $"{next.Number} {next.Name}".Trim(),
            pendingSpeaker = Pending("speaker")?.Text ?? "",
            pendingCrew = Pending("crew")?.Text ?? "",
            flash = air.Stage.FlashUntilUtc is { } until && until > UtcNow(),
            follows = Following,                                                     // round 87: "" or the Stagewatch the timer follows
            // The speaker's one message: where it is (through the Stagewatch, or on this desk's displays), its receipt's word and who answered.
            speakerFrom = speaker.From,
            speakerAck = speaker.Ack,
            speakerAckBy = speaker.By,
        };
    }

    /// <summary>What a deck watches for: the timer's phase and colour, the messages waiting, the speaker's message's receipt — a change is a push.</summary>
    public string DeckSignature()
    {
        var time = Time();
        var seen = _s.FollowedStagewatch.Message;
        var speaker = SpeakerNow();
        return $"{time.Phase}|{time.Colour}|{Pending("speaker")?.Id}|{Pending("crew")?.Id}|{Following}|{seen.Id}|{seen.Ack}|{speaker.From}|{speaker.Ack}|{speaker.By}";
    }

    private object? FollowJson()
    {
        var f = _s.FollowedStagewatch;
        if (f.Name.Length == 0 && f.Address.Length == 0) return null;
        return new
        {
            name = f.Name,
            state = f.State.ToString().ToLowerInvariant(),
            words = StageFollow.Words(f),
            title = f.Frame?.Title ?? "",
            next = f.Frame?.Next ?? "",
            message = f.Frame?.Message ?? "",
            stuck = f.StuckLive,                                                     // round 89: it says its show is stuck — no time from it
            // Its speaker message's signal as its frames tell it — an answer kept after the frame's fields go back to nothing.
            messageId = f.Message.Id,
            messageAck = f.Message.Id > 0 ? f.Message.Ack : "",
            messageAckBy = f.Message.By,
        };
    }

    public string StatusJson()
    {
        Hook();
        var air = _s.AirState;
        var now = UtcNow();
        var time = Time();
        var stack = _s.CueStack;
        TimingReport? timing = null;
        try { timing = stack?.Timing(); } catch (Exception) { /* a plan that will not read is no segment */ }
        var running = stack?.LastCue;
        var next = stack?.StandbyCue;
        return JsonSerializer.Serialize(new
        {
            rev = Rev,
            serverUtc = now,
            show = air.Name,
            clock = now.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture),
            timer = new
            {
                phase = time.Phase.ToString().ToLowerInvariant(),
                remaining = Math.Round(time.RemainingSeconds, 1),
                text = time.Text,
                colour = time.Colour,
                progress = Math.Round(time.Progress01, 4),
                label = Following.Length > 0 ? _s.FollowedStagewatch.Frame?.Title ?? "" : air.Countdown.Label,
                paused = Following.Length > 0 ? time.Phase == StageTimerPhase.Paused : air.Stage.Paused,
                amber = air.Stage.AmberSeconds,
                red = air.Stage.RedSeconds,
            },
            flashUntilUtc = air.Stage.FlashUntilUtc,
            segment = new
            {
                name = running?.Name ?? "",
                number = running?.Number ?? "",
                remaining = timing?.RunningRemaining is { } left ? Math.Round(left.TotalSeconds) : (double?)null,
                overran = timing?.RunningOverran ?? false,
                next = next?.Name ?? "",
                nextNumber = next?.Number ?? "",
                nextAt = next?.PlannedStart ?? "",
                offset = timing?.OffsetText ?? "",
                armed = stack?.Armed ?? false,
            },
            sees = new { speakerSegment = air.Stage.SpeakerSeesSegment, crewSegment = air.Stage.CrewSeesSegment, speakerClock = air.Stage.SpeakerSeesClock },
            // Round 87: one room, one timer — when a Stagewatch is linked the timer above is its, and the pages say so.
            follows = FollowJson(),
            presets = air.Stage.Presets.ToArray(),
            messages = air.Stage.Messages.TakeLast(20).Select(m => new
            {
                id = m.Id, text = m.Text, channel = m.Channel, sentUtc = m.SentUtc, ackUtc = m.AckUtc, flash = m.Flash, from = m.From, seen = m.AckUtc is not null,
                // A message through a Stagewatch: its route's step, who answered, and the words the receipts list shows for it.
                via = m.Via, ack = StageSpeakerWords.AckWord(m), by = m.SwAckBy,
                receipt = StageSpeakerWords.Receipt(m),
            }).ToArray(),
            pendingSpeaker = Pending("speaker") is { } sp ? new { id = sp.Id, text = sp.Text, flash = sp.Flash } : null,
            pendingCrew = Pending("crew") is { } cr ? new { id = cr.Id, text = cr.Text, flash = cr.Flash } : null,
            // The one speaker message a page draws: the Stagewatch's (sw:<id>) or one on this desk's displays alone, the later.
            speakerShown = SpeakerShown() is { } shown ? new { id = shown.Id, text = shown.Text, flash = shown.Flash, from = shown.From, ack = shown.Ack, why = shown.Why } : null,
        });
    }
}
