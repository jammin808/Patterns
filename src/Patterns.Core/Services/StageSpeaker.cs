using System.Globalization;
using System.Text.Json;
using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// The speaker's message through a followed Stagewatch (Show Bus 1.3 §13.7.4): the three routes this desk asks on its link's
/// side calls, each one percent-encoded segment where words go, and the reader of their answers. Pure: the link makes the call.
/// The bytes are the ones Stagewatch's own contract test pins (its <c>tests/e2e/message.e2e.cjs</c>).
/// </summary>
public static class StageMessagePath
{
    /// <summary>The longest message a Stagewatch's route takes; a longer one stays on this desk's displays and says so.</summary>
    public const int MaxChars = 500;

    /// <summary>The longest name this desk signs a forwarded ACK with: Stagewatch cuts the name, never the address it adds.</summary>
    public const int MaxName = 40;

    /// <summary>The route that clears the speaker's message — asked only for a message this desk posted.</summary>
    public const string Clear = "/api/clearmessage";

    /// <summary>
    /// <c>/api/message/&lt;text&gt;/info/&lt;slow|off&gt;/ack</c>: the text as one percent-encoded segment — a space, a slash and a
    /// percent sign all inside it — <c>info</c> because this desk's messages carry no urgency, <c>slow</c> when the message
    /// flashes and <c>off</c> when it does not, and always <c>ack</c>: a message this desk sends waits for its receipt. Null for
    /// words the route cannot carry: none, more than <see cref="MaxChars"/>, a lone surrogate, or a segment of dots alone
    /// (a client would fold it into the path).
    /// </summary>
    public static string? For(string? text, bool flash)
    {
        var t = (text ?? "").Trim();
        if (!Carries(t)) return null;
        return "/api/message/" + Uri.EscapeDataString(t) + "/info/" + (flash ? "slow" : "off") + "/ack";
    }

    /// <summary>Why <see cref="For"/> carries no route for these words; "" when it does.</summary>
    public static string WhyNot(string? text)
    {
        var t = (text ?? "").Trim();
        if (t.Length > MaxChars) return string.Create(CultureInfo.InvariantCulture, $"it is longer than the {MaxChars} characters Stagewatch takes");
        return Carries(t) ? "" : "Stagewatch's route cannot carry these words";
    }

    private static bool Carries(string t)
    {
        if (t.Length is 0 or > MaxChars || t.All(c => c == '.')) return false;
        for (var i = 0; i < t.Length; i++)
        {
            if (char.IsHighSurrogate(t[i]) && i + 1 < t.Length && char.IsLowSurrogate(t[i + 1])) i++;
            else if (char.IsSurrogate(t[i])) return false;
        }
        return true;
    }

    /// <summary>
    /// <c>/api/ack/&lt;messageId&gt;/seen/&lt;this desk's name&gt;</c>: a display's ACK on the Stagewatch's message, signed with this
    /// desk's name (<see cref="Name"/>); Stagewatch adds the address it came from. Null for an id that names no signal.
    /// </summary>
    public static string? Ack(long messageId, string? deskName)
        => messageId <= 0 ? null : "/api/ack/" + messageId.ToString(CultureInfo.InvariantCulture) + "/seen/" + Uri.EscapeDataString(Name(deskName));

    /// <summary>This desk's name as an ACK carries it: one line, at most <see cref="MaxName"/> characters, never empty and never a segment of dots.</summary>
    public static string Name(string? deskName)
    {
        var name = new string((deskName ?? "").Where(c => !char.IsControl(c) && !char.IsSurrogate(c)).ToArray()).Trim();
        if (name.Length > MaxName) name = name[..MaxName].Trim();
        return name.Length == 0 || name.All(c => c == '.') ? "Patterns" : name;
    }

    /// <summary>
    /// The answer to one of the speaker's calls, read by 13.12.3's table (<see cref="PeerLink.ReadHttp"/>): <c>applied</c> with the
    /// signal's id when <c>refs.messageId</c> names one (0 when it names none — a Stagewatch with no message signals), <c>accepted</c>
    /// with no refs (a slow show), <c>refused</c> in the Stagewatch's own words (a 409 receipt, a 403, a 401; a 429 with its wait),
    /// <c>unconfirmed</c> for a timeout, a 408 or a 5xx — <paramref name="status"/> null when no response came after the request
    /// was written. A receipt's <c>effect</c> is the far end's free words and is never kept: the words here are a refusal's, or why
    /// an answer says nothing.
    /// </summary>
    public static StageReceipt Receipt(int? status, string? body, int retryAfterSeconds = 0, string? secret = null)
    {
        var bounded = body is { Length: <= 64 * 1024 } ? body : null;
        var reading = PeerLink.ReadHttp(status, bounded, retryAfterSeconds);
        long id = 0;
        if (reading.Outcome == LinkOutcome.Applied && bounded is not null && PeerLink.ObjectOf(bounded) is { } o
            && o.TryGetProperty("refs", out var refs) && refs.ValueKind == JsonValueKind.Object
            && refs.TryGetProperty("messageId", out var m) && m.ValueKind == JsonValueKind.Number && m.TryGetInt64(out var n) && n > 0) id = n;
        var words = reading.Outcome switch
        {
            LinkOutcome.Applied or LinkOutcome.Accepted => "",
            LinkOutcome.Refused when status == 429 => reading.Words,
            LinkOutcome.Refused => StagewatchFrame.Words(reading.Words, secret) is { Length: > 0 } said && !said.All(char.IsAsciiDigit) ? said
                : string.Create(CultureInfo.InvariantCulture, $"HTTP {status}"),
            _ => reading.Words,
        };
        return new StageReceipt(reading.Outcome, id, words, reading.RetryAfter);
    }
}

/// <summary>What one of the speaker's calls came to: 13.12.2's outcome, the signal's id when the receipt names one, and the words of a refusal or of no answer.</summary>
public readonly record struct StageReceipt(LinkOutcome Outcome, long MessageId, string Words, int RetryAfter = 0);

/// <summary>
/// The Stagewatch's own speaker message as its frames have told it, folded frame by frame (<see cref="Next"/>): the signal's id
/// (0: a message that asks for no acknowledgement), its text, when it arrived on the master clock, its acknowledgement and who
/// gave it. A message signal completes on its first answer, so <see cref="Ack"/> goes <c>sent</c> → <c>done</c> and a reader takes
/// <c>seen</c> or <c>done</c> as the answer; 30 s after the answer the frame's three fields go back to 0, "", "" with the message
/// maybe still on stage, and the answer is kept here for as long as that text stays.
/// </summary>
public sealed record StageMessageSeen(long Id, string Text, double AtSeconds, string Ack, string By, bool Flash)
{
    public static readonly StageMessageSeen None = new(0, "", 0, "", "", false);

    /// <summary>A message is on the Stagewatch's stage.</summary>
    public bool On => Text.Length > 0;

    /// <summary>Its signal was answered — <c>seen</c> or <c>done</c>.</summary>
    public bool Answered => Ack is "seen" or "done";

    /// <summary>A display may answer it now: it has a signal, and nobody has answered it.</summary>
    public bool Asks => On && Id > 0 && Ack == "sent";

    /// <summary>
    /// The message after <paramref name="frame"/>, arrived at <paramref name="nowSeconds"/>. The same signal with the same text
    /// only moves its acknowledgement forward; an answered message whose frame fields went back to nothing keeps its answer; any
    /// other text or signal is another message, arrived now; a frame with no message is none. A null frame (a state frame that
    /// carries no time) says nothing of the message.
    /// </summary>
    public static StageMessageSeen Next(StageMessageSeen was, StagewatchFrame? frame, double nowSeconds)
    {
        if (frame is null) return was;
        if (frame.Message.Length == 0) return None;
        var id = Math.Max(0, frame.MessageId);
        var flash = frame.Flash is "slow" or "fast";
        if (was.On && was.Text == frame.Message && id == was.Id)
        {
            var answered = frame.MessageAck is "seen" or "done";
            return was.Answered && !answered ? was with { Flash = flash } : was with { Ack = id > 0 ? frame.MessageAck : "", By = id > 0 ? frame.MessageAckBy : "", Flash = flash };
        }
        if (was.On && was.Text == frame.Message && id == 0 && was.Answered) return was with { Flash = flash };
        return new StageMessageSeen(id, frame.Message, nowSeconds, id > 0 ? frame.MessageAck : "", id > 0 ? frame.MessageAckBy : "", flash);
    }
}

/// <summary>
/// The one speaker message a stage page draws: its id (<c>sw:&lt;id&gt;</c> for the Stagewatch's), its words, whether it flashes,
/// where it is from, whether an ACK may be pressed on it and — for a message that asks for one this display cannot give — why
/// not, in the words the display shows where the button would be.
/// </summary>
public sealed record StageSpeakerShown(string Id, string Text, bool Flash, string From, bool Ack, string Why = "");

/// <summary>
/// One speaker message in the room: while the stage follows a Stagewatch the page draws whichever arrived later — the
/// Stagewatch's message, or a message on this desk's displays alone. Pure: both arrivals are on the master clock.
/// </summary>
public static class StageSpeakerChoice
{
    public const string FromStagewatch = "stagewatch";
    public const string FromDesk = "desk";

    /// <summary>What an id of the Stagewatch's message starts with: <c>sw:41</c>.</summary>
    public const string StagewatchPrefix = "sw:";

    /// <summary>The page's id for the Stagewatch's message.</summary>
    public static string StagewatchId(long messageId) => StagewatchPrefix + messageId.ToString(CultureInfo.InvariantCulture);

    /// <summary>The signal a page's ACK names, or null when the id is not the Stagewatch's (a local message's).</summary>
    public static long? StagewatchSignal(string? id)
        => id is not null && id.StartsWith(StagewatchPrefix, StringComparison.Ordinal)
           && long.TryParse(id.AsSpan(StagewatchPrefix.Length), NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : null;

    /// <summary>
    /// <paramref name="local"/>: the latest unseen speaker message on this desk's displays (never one that is on the Stagewatch's
    /// stage), sent at <paramref name="localAtSeconds"/>. <paramref name="seen"/>: the Stagewatch's message while the stage follows
    /// it (<paramref name="following"/>); once answered it leaves the page. The later wins; at the same instant the Stagewatch's.
    /// </summary>
    public static StageSpeakerShown? Of(StageMessage? local, double localAtSeconds, StageMessageSeen seen, bool following)
    {
        var theirs = following && seen.On && !seen.Answered ? new StageSpeakerShown(StagewatchId(seen.Id), seen.Text, seen.Flash, FromStagewatch, seen.Asks) : null;
        var ours = local is null ? null : new StageSpeakerShown(local.Id, local.Text, local.Flash, FromDesk, true);
        if (theirs is null) return ours;
        if (ours is null) return theirs;
        return localAtSeconds > seen.AtSeconds ? ours : theirs;
    }
}

/// <summary>
/// The words of the speaker's route — the verb's reply, the journal's rows, the lines the Eye, the Run strip and the assistant
/// read — and the state words STATE's <c>stage.speakerAck</c> carries. One place, so every reader says a step the same way.
/// </summary>
public static class StageSpeakerWords
{
    public const string Via = "stagewatch";

    public const string Requested = "requested";
    public const string Accepted = "accepted";
    public const string Sent = "sent";
    public const string Seen = "seen";
    public const string Done = "done";
    public const string Refused = "refused";
    public const string Unconfirmed = "unconfirmed";
    public const string NotSent = "not-sent";
    public const string Cleared = "cleared";

    /// <summary>The message is on its way to, or on, the Stagewatch's stage: its receipt and its ACK come from there.</summary>
    public static bool OnStagewatch(StageMessage m) => m.Via == Via && m.SwAck is Requested or Accepted or Sent or Seen or Done;

    /// <summary>
    /// The message is one this desk's displays draw while unseen: one never sent through a Stagewatch, or one whose route ended
    /// here — refused, unconfirmed or never sent.
    /// </summary>
    public static bool OnDesk(StageMessage m) => m.Via != Via || m.SwAck is Refused or Unconfirmed or NotSent;

    /// <summary>
    /// STATE's word for a message's receipt: "" once cleared; <c>done</c> or <c>seen</c> once acknowledged; a message through a
    /// Stagewatch by its route's step; a message on this desk's displays <c>sent</c> while it waits.
    /// </summary>
    public static string AckWord(StageMessage m)
    {
        if (m.SwAck == Cleared) return "";
        if (m.AckUtc is not null) return m.SwAck == Done ? Done : Seen;
        return m.Via == Via && m.SwAck.Length > 0 ? m.SwAck : Sent;
    }

    /// <summary>The verb's reply for a message sent through the Stagewatch.</summary>
    public static string ViaReply(string name, string text) => $"To Stagewatch {name}'s speaker: '{text}' — its receipt and the speaker's ACK come back here.";

    /// <summary>The verb's reply for a message that stays on this desk while a Stagewatch is linked, with the reason.</summary>
    public static string DeskOnlyReply(string name, string text, string why, string channel = "speaker")
        => $"To the {channel}: '{text}' — on this desk's displays only; Stagewatch {name}'s displays do not show it ({why}).";

    /// <summary>Why a speaker's message stays here while a Stagewatch is linked and the stage does not follow it.</summary>
    public const string NotFollowedWhy = "the stage does not follow it";

    /// <summary>Why a crew's message stays here while the stage follows a Stagewatch: a node key carries the speaker's messages only.</summary>
    public const string CrewWhy = "the crew's channel is this desk's own";

    /// <summary>What a row says of a message that ended on this desk's displays.</summary>
    public const string HereOnly = "shown on this desk's displays only";

    /// <summary>What a row says of a message this desk cleared before its receipt came: it is on no display of this desk's.</summary>
    public const string ClearedMeanwhile = "cleared on this desk meanwhile";

    /// <summary>
    /// The journal's row for a receipt (13.12.3's outcome) of a message sent to <paramref name="name"/>; <paramref name="cleared"/>:
    /// the desk cleared it before the receipt came, so a route that ended here puts it on no display.
    /// </summary>
    public static string ReceiptRow(string name, StageReceipt receipt, bool cleared = false) => receipt.Outcome switch
    {
        LinkOutcome.Applied when receipt.MessageId > 0 => string.Create(CultureInfo.InvariantCulture, $"stage message → Stagewatch {name}: on the speaker's screen (message {receipt.MessageId})"),
        LinkOutcome.Applied => $"stage message → Stagewatch {name}: on the speaker's screen — it names no signal, so no acknowledgement follows",
        LinkOutcome.Accepted => $"stage message → Stagewatch {name}: accepted, not yet applied — its show thread is slow",
        LinkOutcome.Refused => $"stage message → Stagewatch {name} refused it ({receipt.Words}) — {(cleared ? ClearedMeanwhile : HereOnly)}",
        LinkOutcome.NotSent => $"stage message → Stagewatch {name}: not sent ({receipt.Words}) — {(cleared ? ClearedMeanwhile : HereOnly)}",
        _ => $"stage message → Stagewatch {name}: unconfirmed ({receipt.Words}) — it may be on the speaker's screen; {(cleared ? ClearedMeanwhile : HereOnly + " meanwhile")}",
    };

    /// <summary>The journal's row for a message a newer one replaced before it was sent.</summary>
    public const string SupersededRow = "stage message superseded before it was sent";

    /// <summary>Why a superseded message was not sent.</summary>
    public const string SupersededWhy = "a newer message replaced it before it was sent";

    /// <summary>The journal's row for an accepted or unconfirmed message a frame has now named.</summary>
    public static string BoundRow(string name, long messageId)
        => string.Create(CultureInfo.InvariantCulture, $"stage message → Stagewatch {name}: on the speaker's screen (message {messageId}) — its frame names it");

    /// <summary>The journal's row for an accepted message no frame named within <see cref="PeerLink.ReceiptFinal"/> (13.12.4).</summary>
    public static string NoBindRow(string name)
        => $"stage message → Stagewatch {name}: unconfirmed ({PeerLink.NoFinalWords}) — no frame names it; {HereOnly} meanwhile";

    /// <summary>The journal's row for the speaker's answer.</summary>
    public static string SeenRow(long messageId, string by, DateTime atLocal)
        => string.Create(CultureInfo.InvariantCulture, $"stage message {messageId} seen by {(by.Length > 0 ? by : "the speaker")} at {atLocal:HH:mm:ss}");

    /// <summary>Why a message this run holds no call for reads unconfirmed: the desk restarted, or took the show over, before its receipt came.</summary>
    public const string RestartedWhy = "this desk restarted, or took the show over, before its receipt came";

    /// <summary>The journal's row for a message found requested or accepted with no call of this run behind it.</summary>
    public static string RestartedRow(string name)
        => $"stage message → Stagewatch {name}: unconfirmed ({RestartedWhy}) — it may be on the speaker's screen; {HereOnly} meanwhile";

    /// <summary>The journal's row for a bound message of a run before this one that the Stagewatch's stage does not show now.</summary>
    public static string LostRow(string name, long messageId)
        => string.Create(CultureInfo.InvariantCulture, $"stage message {messageId} is not on Stagewatch {name}'s stage — this desk was not running when it left, and whether it was answered is not known");

    /// <summary>The journal's row for a message that left the Stagewatch's stage before anyone answered it.</summary>
    public static string LeftRow(string name, long messageId)
        => messageId > 0
            ? string.Create(CultureInfo.InvariantCulture, $"stage message {messageId} left Stagewatch {name}'s stage unanswered — cleared or replaced there")
            : $"stage message left Stagewatch {name}'s stage — cleared or replaced there";

    /// <summary>STAGE CLEAR's words when the Stagewatch's own message is on and is not this desk's to clear.</summary>
    public static string OwnStays(string name) => $"Stagewatch {name}'s own message stays — clear it there";

    /// <summary>
    /// STAGE CLEAR's words for a message of this desk's the Stagewatch ran that no frame has shown since, and that the clear did
    /// not ask about: its stage may still hold it.
    /// </summary>
    public static string MaybeStays(string name)
        => $"This desk's message ran on Stagewatch {name} and no frame has shown it there since — it may still be on its speaker's screen; clear it there if it is";

    /// <summary>The journal's row for a clear asked of the Stagewatch.</summary>
    public static string ClearRow(string name, StageReceipt receipt) => receipt.Outcome switch
    {
        LinkOutcome.Applied => $"stage clear → Stagewatch {name}: this desk's message cleared from the speaker's screen",
        LinkOutcome.Accepted => $"stage clear → Stagewatch {name}: accepted, not yet applied — its show thread is slow",
        LinkOutcome.Refused => $"stage clear → Stagewatch {name} refused it ({receipt.Words}) — clear it there",
        LinkOutcome.NotSent when receipt.Words == NothingToClearWhy => $"stage clear → Stagewatch {name}: not asked — {NothingToClearWhy}",
        LinkOutcome.NotSent => $"stage clear → Stagewatch {name}: not sent ({receipt.Words}) — clear it there",
        _ => $"stage clear → Stagewatch {name}: unconfirmed ({receipt.Words}) — look at its speaker's screen",
    };

    /// <summary>
    /// Why a clear that followed a message still on its way was never asked: the message did not reach the Stagewatch — refused,
    /// never sent or replaced first — so what is on its stage is not this desk's to clear.
    /// </summary>
    public const string NothingToClearWhy = "the message it would clear never reached the speaker's screen";

    /// <summary>The journal's row for a clear a frame has shown done: nothing this desk posted is on the Stagewatch's stage.</summary>
    public static string ClearSeenRow(string name) => $"stage clear → Stagewatch {name}: its frame shows nothing of this desk's on the speaker's screen";

    /// <summary>What a cue's row says while the clear it asked of the Stagewatch settles nothing (13.12.3's accepted and unconfirmed).</summary>
    public static string ClearWaiting(string name, LinkOutcome outcome) => outcome == LinkOutcome.Accepted
        ? $"Stagewatch {name} accepted the clear, not yet applied"
        : $"The clear asked of Stagewatch {name} is unconfirmed — look at its speaker's screen";

    /// <summary>What a cue's row says of a message cleared on this desk before its receipt settled the row.</summary>
    public const string ClearedFirst = "the speaker's message was cleared on this desk before Stagewatch confirmed it";

    /// <summary>What a cue's row says of an unconfirmed message whose ACK was pressed on this desk's display.</summary>
    public const string SeenHereFirst = "the speaker's message was seen on this desk's display; Stagewatch never confirmed it";

    /// <summary>What a display shows in place of the ACK on the Stagewatch's message when it cannot give one, with the reason.</summary>
    public static string NoAck(string name, string why) => $"Answer it on Stagewatch {name}'s own screen — {why}";

    /// <summary>The same, on a display with no link of its own to answer through and no desk in step to ask.</summary>
    public static string NoAckHere(string name) => $"Answer it on the desk's stage page, or on Stagewatch {name}'s own screen";

    /// <summary>Why no display offers an ACK the Stagewatch refused, until its frame moves.</summary>
    public static string AckRefusedWhy(string words) => $"it refused this display's ACK ({words})";

    /// <summary>The journal's row for a display's ACK forwarded to the Stagewatch.</summary>
    public static string AckRow(string name, long messageId, StageReceipt receipt) => receipt.Outcome switch
    {
        LinkOutcome.Applied or LinkOutcome.Accepted => string.Create(CultureInfo.InvariantCulture, $"stage message {messageId}: this desk's ACK reached Stagewatch {name}"),
        LinkOutcome.Refused => string.Create(CultureInfo.InvariantCulture, $"stage message {messageId}: Stagewatch {name} refused this desk's ACK ({receipt.Words})"),
        LinkOutcome.NotSent => string.Create(CultureInfo.InvariantCulture, $"stage message {messageId}: this desk's ACK was not sent to Stagewatch {name} ({receipt.Words})"),
        _ => string.Create(CultureInfo.InvariantCulture, $"stage message {messageId}: this desk's ACK to Stagewatch {name} is unconfirmed ({receipt.Words})"),
    };

    /// <summary>What a cue's row says while its message's receipt settles nothing (13.12.3's accepted and unconfirmed).</summary>
    public static string CueWaiting(string name, LinkOutcome outcome) => outcome == LinkOutcome.Accepted
        ? $"Stagewatch {name} accepted the message, not yet applied"
        : $"The message to Stagewatch {name} is unconfirmed — it may be on the speaker's screen";

    /// <summary>
    /// Where the speaker's message went and what came of it, in one line — the Eye's Stagewatch node, the Run page and the
    /// assistant's facts read it. "" for a message with nothing to say (cleared).
    /// </summary>
    public static string Line(StageMessage m, string name)
    {
        var text = m.Text.Length > 60 ? m.Text[..60] + "…" : m.Text;
        var id = m.SwMessageId > 0 ? string.Create(CultureInfo.InvariantCulture, $" (message {m.SwMessageId})") : "";
        var why = m.SwWords.Length > 0 ? $" ({m.SwWords})" : "";
        if (m.Via != Via) return m.SwAck == Cleared ? "" : m.AckUtc is null ? $"the speaker's message '{text}' is on this desk's displays, waiting for its ACK" : $"the speaker's message '{text}' was seen on this desk's displays";
        return m.SwAck switch
        {
            Requested => $"the speaker's message '{text}' is requested of Stagewatch {name} — no receipt yet",
            Accepted => $"the speaker's message '{text}' is accepted by Stagewatch {name}, not yet applied",
            Sent => $"the speaker's message '{text}' is on Stagewatch {name}'s speaker screen{id}, waiting for its ACK",
            Done or Seen => $"the speaker's message '{text}'{id} was seen by {(m.SwAckBy.Length > 0 ? m.SwAckBy : "the speaker")} through Stagewatch {name}",
            Refused => $"Stagewatch {name} refused the speaker's message '{text}'{why} — it is on this desk's displays only",
            Unconfirmed => $"the speaker's message '{text}' to Stagewatch {name} is unconfirmed{why} — it may be on its speaker's screen; it is on this desk's displays",
            NotSent => $"the speaker's message '{text}' was not sent to Stagewatch {name}{why} — it is on this desk's displays only",
            _ => "",
        };
    }

    /// <summary>
    /// The receipts list's words for a message through a Stagewatch, or one cleared: its route's step in a few words — and never
    /// "seen" for a message nobody saw (one a newer message replaced before it was sent, or one cleared unanswered). "" for a
    /// message on this desk's displays alone, whose list says waiting or seen.
    /// </summary>
    public static string Receipt(StageMessage m)
    {
        var to = m.SwName.Length > 0 ? "Stagewatch " + m.SwName : "Stagewatch";
        return m.SwAck switch
        {
            Cleared when m.SwWords == SupersededWhy => "superseded — never sent",
            Cleared => "cleared",
            _ when m.Via != Via => "",
            Requested => $"sent to {to}…",
            Accepted => $"accepted by {to}, not yet on its screen",
            Sent => $"on {to}'s screen, waiting…",
            Done or Seen => $"seen{(m.SwAckBy.Length > 0 ? " by " + m.SwAckBy : "")}",
            Refused => $"refused by {to} — on this desk's displays{(m.AckUtc is null ? ", waiting…" : ", seen")}",
            Unconfirmed => $"unconfirmed by {to} — on this desk's displays{(m.AckUtc is null ? ", waiting…" : ", seen")}",
            NotSent => $"not sent to {to} — on this desk's displays{(m.AckUtc is null ? ", waiting…" : ", seen")}",
            _ => "",
        };
    }

    /// <summary>
    /// One message's receipt as the desk's stage panel says it, in the words the timer page's list has: its route's step
    /// (<see cref="Receipt"/>) or, on this desk's displays alone, waiting or seen — with the time of the ACK only where an ACK
    /// was pressed.
    /// </summary>
    public static string PanelLine(StageMessage m)
    {
        var at = m.AckUtc is { } ack && m.SwAck != Cleared ? " " + ack.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture) : "";
        if (Receipt(m) is { Length: > 0 } words) return words + at;
        return m.AckUtc is null ? "waiting…" : "seen" + at;
    }

    /// <summary>The Run strip's words for a speaker message whose route needs a look while it waits here; "" otherwise.</summary>
    public static string Glance(StageMessage? m) => m is { Via: Via, AckUtc: null } ? m.SwAck switch
    {
        Refused => "SPEAKER MESSAGE △ REFUSED BY STAGEWATCH",
        Unconfirmed => "SPEAKER MESSAGE △ UNCONFIRMED",
        NotSent => "SPEAKER MESSAGE △ NOT SENT TO STAGEWATCH",
        _ => "",
    } : "";
}
