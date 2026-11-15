using System.Buffers;
using System.Globalization;
using System.Text;
using System.Text.Json;

namespace Patterns.Core.Services;

/// <summary>
/// Show Bus 1.3 §13.12.4–13.12.6 at this desk's wire as an acceptor (features <c>final</c> and <c>receipts</c>): per verified connector
/// (<c>t:id</c> its LINK proved), the final receipts of its last <see cref="PeerLink.ReceiptsKept"/> ids for
/// <see cref="PeerLink.ReceiptsKeptFor"/>, kept by one <see cref="ReceiptBook"/> under one lock, so an id two connections of one
/// connector send at once runs once. Only an id of 13.12.1's shape is kept — 1–4 letters, a counter from 1, a dash and its connector's
/// inst — so a sender whose ids restart with its process is never answered from an older process's record. It keeps the receipts of at
/// most <see cref="Connectors"/> connectors, as many as the wire takes connections at once, the one heard from longest ago forgotten
/// first. A receipt is kept as its fields (<c>{"ok":…,"stage":…,"effect":…}</c> or <c>{"ok":false,"error":…}</c>, then <c>lateMs</c>
/// where it ran late) and answered byte for byte as kept, <c>"repeat":true</c> placed by <see cref="ReceiptBook.Repeat"/>. Pure but for
/// its lock; the clock is the caller's, in milliseconds.
/// </summary>
public sealed class AcceptorReceipts
{
    /// <summary>The most connectors whose receipts are kept at once: the wire's own ceiling on connections (<see cref="WireLimits.MaxClients"/>).</summary>
    public const int Connectors = 64;

    /// <summary>What a RECEIPTS line with no request id hears.</summary>
    public const string NeedIdWords = "RECEIPTS asks with a request id: @rq1 RECEIPTS <id> ...";

    /// <summary>What a RECEIPTS ask hears on a connection LINK did not verify: only a PeerLink's own ids are kept.</summary>
    public const string NotLinkedWords = "RECEIPTS is a PeerLink's ask — this connection did not prove itself with LINK, and this desk keeps receipts only for a link's own ids";

    /// <summary>The words of an ask this record does not answer: no id, more than <see cref="PeerLink.ReceiptsAskMost"/>, or one no request id can be.</summary>
    public static readonly string AskWords =
        "RECEIPTS names 1 to " + PeerLink.ReceiptsAskMost.ToString(CultureInfo.InvariantCulture) + " request ids, each 1-32 letters, digits, - or _";

    /// <summary>The longest effect or error a kept receipt carries: a receipt says what came of a command, never a state's whole text.</summary>
    public const int WordsMost = 512;

    private readonly object _gate = new();
    private readonly ReceiptBook _book = new();
    private readonly Dictionary<string, long> _heard = new(StringComparer.Ordinal);

    /// <summary>How many connectors' receipts are held now.</summary>
    public int ConnectorsHeld
    {
        get
        {
            lock (_gate) return _heard.Count;
        }
    }

    /// <summary>
    /// Whether <paramref name="id"/> is one this record keeps: 13.12.1's shape — 1–4 ASCII letters, a counter from 1 (no leading 0),
    /// a dash and the connector's inst of letters and digits — at most <c>request.id.chars</c> long.
    /// </summary>
    public static bool Keeps(string? id)
    {
        if (id is not { Length: > 0 and <= WireReceipt.MaxId }) return false;
        var i = 0;
        while (i < id.Length && char.IsAsciiLetter(id[i])) i++;
        if (i is < 1 or > 4 || i >= id.Length || id[i] is < '1' or > '9') return false;
        while (i < id.Length && char.IsAsciiDigit(id[i])) i++;
        if (i >= id.Length || id[i] != '-' || i + 1 >= id.Length) return false;
        for (var j = i + 1; j < id.Length; j++)
            if (!char.IsAsciiLetterOrDigit(id[j])) return false;
        return true;
    }

    /// <summary>
    /// Before running <paramref name="id"/> from <paramref name="connector"/>: the record's answer when it holds the id — the receipt it
    /// kept with <c>"repeat":true</c>, or accepted while it still runs — and then the command does not run; or null, and the id is held as
    /// running until <see cref="Keep"/>. Null too for an id this record does not keep (<see cref="Keeps"/>), which runs as it always did.
    /// </summary>
    public string? Begin(string connector, string id, long nowMs)
    {
        if (!Keeps(id)) return null;
        lock (_gate)
        {
            Heard(connector, nowMs);
            return _book.Begin(connector, id, nowMs);
        }
    }

    /// <summary>The final receipt of <paramref name="id"/> (<see cref="Applied"/>, <see cref="Refused"/>), kept for its repeats and RECEIPTS.</summary>
    public void Keep(string connector, string id, string receipt, long nowMs)
    {
        if (!Keeps(id)) return;
        lock (_gate)
        {
            Heard(connector, nowMs);
            _book.Keep(connector, id, receipt, nowMs);
        }
    }

    /// <summary>
    /// 13.12.6: a RECEIPTS answer for <paramref name="connector"/>'s own ids (at most <see cref="PeerLink.ReceiptsAskMost"/>), with this
    /// node's <paramref name="inst"/>: <c>{"inst":…,"receipts":{…}}</c>, each id its kept receipt, accepted while it runs, or "unknown".
    /// </summary>
    public string Answer(string connector, IEnumerable<string> ids, string inst, long nowMs)
    {
        lock (_gate) return _book.Answer(connector, ids, inst, nowMs);
    }

    /// <summary>Whether a line (without its id) is a RECEIPTS ask: its first word, in any case.</summary>
    public static bool IsAsk(string body)
    {
        var b = (body ?? "").TrimStart();
        return b.StartsWith("RECEIPTS", StringComparison.OrdinalIgnoreCase) && (b.Length == 8 || b[8] == ' ');
    }

    /// <summary>The ids an ask names, each once, at most <see cref="PeerLink.ReceiptsAskMost"/>; null when it names none, more, or one that is no id.</summary>
    public static IReadOnlyList<string>? AskOf(IEnumerable<string> ids)
    {
        var list = ids.Where(i => i.Length > 0).Distinct(StringComparer.Ordinal).ToList();
        return list.Count is > 0 and <= PeerLink.ReceiptsAskMost && list.All(WireReceipt.IsId) ? list : null;
    }

    private void Heard(string connector, long nowMs)
    {
        if (!_heard.ContainsKey(connector) && _heard.Count >= Connectors)
        {
            var oldest = _heard.MinBy(kv => kv.Value).Key;
            _heard.Remove(oldest);
            _book.Forget(oldest);
        }
        _heard[connector] = nowMs;
    }

    /// <summary>A command that ran: <c>{"ok":true,"stage":"applied","effect":…}</c>, and <c>lateMs</c> after it when it ran late.</summary>
    public static string Applied(string effect, long? lateMs = null) => Receipt(true, Cut(effect), lateMs);

    /// <summary>A command that did not run: <c>{"ok":false,"error":…}</c>, and <c>lateMs</c> after it.</summary>
    public static string Refused(string error, long? lateMs = null) => Receipt(false, Cut(error), lateMs);

    /// <summary>
    /// A command taken whose outcome this desk will never say: <c>{"ok":true,"stage":"accepted","effect":…}</c>, and <c>lateMs</c> after it —
    /// as its first answer read, never more than was observed (13.12.3).
    /// </summary>
    public static string Accepted(string effect, long? lateMs = null) => Receipt(true, Cut(effect), lateMs, "accepted");

    /// <summary>What the record keeps for a CUE GO whose run no reader settles any more: it may have run, and the desk's state says.</summary>
    public const string GivenUpWords = "unconfirmed — the cue history no longer says what came of this run; it may have run — look at the desk's state";

    private static string Receipt(bool ok, string words, long? lateMs, string stage = "applied")
    {
        var buffer = new ArrayBufferWriter<byte>(128);
        using (var w = new Utf8JsonWriter(buffer, Words))
        {
            w.WriteStartObject();
            w.WriteBoolean("ok", ok);
            if (ok)
            {
                w.WriteString("stage", stage);
                w.WriteString("effect", words);
            }
            else
            {
                w.WriteString("error", words);
            }
            if (lateMs is { } late) w.WriteNumber("lateMs", late);
            w.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    // A receipt's words as they read (a dash, a quote mark, an accent): JSON escapes only what it must, as the link's record does.
    private static readonly JsonWriterOptions Words = new() { Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    private static string Cut(string words)
    {
        var flat = (words ?? "").Replace('\n', ' ').Replace('\r', ' ').Trim();
        return flat.Length <= WordsMost ? flat : flat[..WordsMost] + "…";
    }

    /// <summary>13.12.5: how late a command ran — when it ran more than <see cref="PeerLink.ReceiptLate"/> after it was read — or null when in time.</summary>
    public static long? Late(long readMs, long ranMs)
    {
        var late = ranMs - readMs;
        return late > (long)PeerLink.ReceiptLate.TotalMilliseconds ? late : null;
    }

    /// <summary>A line answer's words for <paramref name="lateMs"/> (a line receipt that is not JSON carries words, not fields).</summary>
    public static string LateWords(long lateMs) => "ran " + lateMs.ToString(CultureInfo.InvariantCulture) + " ms after it was read (lateMs)";

    /// <summary>
    /// 13.12.5 on this wire's answer line: an <c>OK {…}</c> takes <c>"lateMs":N</c> as its object's last field; any other <c>OK</c> or
    /// <c>ERR</c> takes the words after its own. Unchanged when it ran in time, and for a line that is no answer.
    /// </summary>
    public static string WithLate(string reply, long? lateMs)
    {
        if (lateMs is not { } late) return reply;
        if (reply.StartsWith("OK {", StringComparison.Ordinal) && reply.EndsWith('}'))
            return reply[..^1] + (reply.Length > 5 ? "," : "") + "\"lateMs\":" + late.ToString(CultureInfo.InvariantCulture) + "}";
        if (reply == "OK" || reply == "ERR") return reply + " " + LateWords(late);
        if (reply.StartsWith("OK ", StringComparison.Ordinal) || reply.StartsWith("ERR ", StringComparison.Ordinal)) return reply + " — " + LateWords(late);
        return reply;
    }

    /// <summary>
    /// The receipt this record keeps for an answer line of this wire (before its id was put in): <c>OK</c> and <c>OK &lt;words&gt;</c>
    /// applied with the words, <c>OK {…}</c> applied with the words its fields name (<see cref="WordsOf"/>), <c>ERR &lt;words&gt;</c> refused.
    /// </summary>
    public static string FromAnswer(string reply, long? lateMs = null)
    {
        if (reply == "ERR" || reply.StartsWith("ERR ", StringComparison.Ordinal)) return Refused(reply.Length > 4 ? reply[4..] : "refused", lateMs);
        var words = reply.Length > 3 ? reply[3..] : "";
        if (words.StartsWith('{')) words = WordsOf(words);
        return Applied(words, lateMs);
    }

    /// <summary>
    /// The words a JSON answer of this wire carries for a receipt: its <c>effect</c>, <c>confirm</c>, <c>words</c> or <c>msg</c>; for a cue's
    /// answer its last row's number, name and outcome; for a standby's, the cue on standby; else none.
    /// </summary>
    public static string WordsOf(string json)
    {
        try
        {
            using var doc = JsonDocument.Parse(json);
            var o = doc.RootElement;
            if (o.ValueKind != JsonValueKind.Object) return "";
            foreach (var name in new[] { "effect", "confirm", "words", "msg" })
                if (o.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String) return v.GetString() ?? "";
            if (o.TryGetProperty("last", out var last) && last.ValueKind == JsonValueKind.Object) return RowWords(last);
            if (o.TryGetProperty("standby", out var sb) && sb.ValueKind == JsonValueKind.Object) return "standby " + Label(sb);
            return "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    private static string RowWords(JsonElement row)
    {
        var outcome = row.TryGetProperty("outcome", out var o) && o.ValueKind == JsonValueKind.String ? o.GetString() ?? "" : "";
        return Label(row) + (outcome.Length > 0 ? " — " + outcome : "");
    }

    private static string Label(JsonElement row)
    {
        string Str(string name) => row.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";
        return (Str("number") + " " + Str("name")).Trim();
    }
}

/// <summary>
/// Show Bus 1.3 §13.12.4 for <c>CUE GO</c> on this desk's wire (feature <c>final</c>): a GO answered <c>Requested</c> — accepted, still
/// settling — is followed by one final line once its row in the cue history says what came of it. Refused (13.12.2: it did not run)
/// only when nothing ran — a cue refused, skipped, or failed before any action was done. A cue that ran is applied, with the row's
/// words: Done, done with warnings, and a cue that failed late or failed part-way — its actions are on the air, and a box that said no
/// or said nothing is named in the words, so a connector never reads a cue that ran as one to fire again. The answer is the row's,
/// which device receipts, the speaker's receipts and the late failures of the services the cue started settle — never the send. Pure.
/// </summary>
public static class CueFinal
{
    /// <summary>The row's final outcome: (true, words) it ran, (false, words) nothing ran, or null while it still settles.</summary>
    public static (bool Ok, string Words)? Of(CueExecutionRecord row)
    {
        var label = row.Label.Trim();
        var said = label + " — " + row.OutcomeWords + (row.Detail.Length > 0 ? ": " + row.Detail : "");
        return row.Outcome switch
        {
            CueOutcome.Requested => null,
            CueOutcome.Done or CueOutcome.DoneWithWarnings => (true, label + " — " + row.OutcomeWords),
            CueOutcome.FailedLate => (true, said),
            CueOutcome.Failed when row.ActionsDone > 0 => (true, said),
            _ => (false, said),
        };
    }

    /// <summary>
    /// The final line for <paramref name="id"/>: <c>OK @id final {"ok":true,"stage":"applied","effect":…}</c> or
    /// <c>ERR @id final &lt;words&gt;</c> — each carrying <paramref name="lateMs"/> when the GO ran late — and the receipt the record keeps.
    /// </summary>
    public static (string Line, string Receipt) Line(string id, bool ok, string words, long? lateMs)
    {
        var receipt = ok ? AcceptorReceipts.Applied(words, lateMs) : AcceptorReceipts.Refused(words, lateMs);
        if (ok) return ("OK @" + id + " final " + receipt, receipt);
        var flat = words.Replace('\n', ' ').Replace('\r', ' ').Trim();
        if (flat.Length > AcceptorReceipts.WordsMost) flat = flat[..AcceptorReceipts.WordsMost] + "…";
        return ("ERR @" + id + " final " + (lateMs is { } late ? flat + " — " + AcceptorReceipts.LateWords(late) : flat), receipt);
    }
}
