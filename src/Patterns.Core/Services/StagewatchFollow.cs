using System.Globalization;
using System.Net;
using System.Text.Json;

namespace Patterns.Core.Services;

/// <summary>
/// Round 87: one state frame from a Stagewatch's WebSocket (/ws) — the timer as its speaker sees it. Durations, not
/// wall clocks: <see cref="Seconds"/> is what was left when the frame was made (negative past zero) and the receiver
/// counts on from the moment it arrived, re-syncing on the next frame (Show Bus 1.1 §4).
/// </summary>
public sealed record StagewatchFrame
{
    public int V { get; init; }
    public long Seq { get; init; }
    public string Mode { get; init; } = "";
    public string State { get; init; } = "";
    public double Seconds { get; init; }
    public double Total { get; init; }
    public string Text { get; init; } = "";
    public string Phase { get; init; } = "";
    public string Color { get; init; } = "";
    public string Title { get; init; } = "";
    public string Next { get; init; } = "";
    public string Message { get; init; } = "";
    public long MessageId { get; init; }
    public string MessageAck { get; init; } = "";
    /// <summary>False when the Stagewatch says its own show thread is stuck: the time on it is not to be trusted.</summary>
    public bool Live { get; init; } = true;

    public bool Counting => State is "running";
    public bool Countdown => Mode is "countdown" or "";

    /// <summary>A Stagewatch WebSocket message as a frame; null for a beat, a pong, a reply or anything that is not a state frame.</summary>
    public static StagewatchFrame? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 256 * 1024) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object) return null;
            if (Str(r, "type") is var type && type.Length > 0 && type != "state") return null;
            if (!r.TryGetProperty("seconds", out _) && !r.TryGetProperty("text", out _)) return null;
            return new StagewatchFrame
            {
                V = (int)Num(r, "v"),
                Seq = (long)Num(r, "seq"),
                Mode = Str(r, "mode").ToLowerInvariant(),
                State = Str(r, "state").ToLowerInvariant(),
                Seconds = Num(r, "seconds"),
                Total = Num(r, "total"),
                Text = Str(r, "text"),
                Phase = Str(r, "phase").ToLowerInvariant(),
                Color = Str(r, "color"),
                Title = Str(r, "title"),
                Next = Str(r, "next"),
                Message = Str(r, "message"),
                MessageId = (long)Num(r, "messageId"),
                MessageAck = Str(r, "messageAck"),
                Live = !r.TryGetProperty("live", out var live) || live.ValueKind != JsonValueKind.False,
            };
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>
    /// A Stagewatch's stuck beat, <c>{"type":"beat","live":false}</c>, in the bytes Stagewatch sends: its show thread has
    /// stopped turning over, so its last frame is a frozen time (PeerLink profile 13.10.5). True only for an object whose
    /// type is "beat" and whose live is the literal false — a plain beat, a beat saying true, a state frame or junk is not one.
    /// </summary>
    public static bool IsStuckBeat(string? json)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 4096) return false;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            return r.ValueKind == JsonValueKind.Object && Str(r, "type") == "beat" && r.TryGetProperty("live", out var live) && live.ValueKind == JsonValueKind.False;
        }
        catch (JsonException)
        {
            return false;
        }
    }

    /// <summary>The message kind of a WebSocket message: "state", "beat", "pong", "receipt"… ("" when it names none).</summary>
    public static string TypeOf(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return "";
        try
        {
            using var doc = JsonDocument.Parse(json);
            return doc.RootElement.ValueKind == JsonValueKind.Object ? Str(doc.RootElement, "type") : "";
        }
        catch (JsonException)
        {
            return "";
        }
    }

    /// <summary>A pong's stamps, NTP-style: t1 echoed, the Stagewatch's t2 and t3 (Unix ms); null when the message is not one.</summary>
    public static (long T1, long T2, long T3)? Pong(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || Str(r, "type") != "pong") return null;
            var t1 = (long)Num(r, "t");
            var t2 = (long)Num(r, "t2");
            var t3 = (long)Num(r, "t3");
            return t1 > 0 && t2 > 0 && t3 > 0 ? (t1, t2, t3) : null;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The ping a client sends on a Stagewatch's WebSocket (Show Bus 1.1 §5).</summary>
    public static string Ping(long t1) => string.Create(CultureInfo.InvariantCulture, $"{{\"cmd\":\"ping\",\"t\":{t1}}}");

    /// <summary>The query a desk that names no node dials: a browser screen to every Stagewatch.</summary>
    public const string ScreenQuery = "/ws?cid=patterns&page=/patterns";

    /// <summary>
    /// Round 91: the path and query this desk dials a Stagewatch's /ws with — a node link naming this desk's show network id
    /// and its kind (Stagewatch 2.18, Show Bus 1.3 §7.1): <c>/ws?node=&lt;id&gt;&amp;t=patterns&amp;cid=&lt;id&gt;&amp;page=/patterns</c>.
    /// <c>cid</c> and <c>page</c> are for a Stagewatch older than its node links, which ignores <c>node</c> and lists each
    /// install under its own <c>cid</c> (kept only at 6–32 characters, so a shorter or longer id sends today's). An id that is
    /// no node id dials as today, a screen. The key never rides here: it goes in the Authorization header.
    /// </summary>
    public static string SocketQuery(string? selfId)
    {
        var id = selfId ?? "";
        if (!IsNodeId(id)) return ScreenQuery;
        var cid = id.Length is >= 6 and <= 32 ? id : "patterns";
        return $"/ws?node={id}&t=patterns&cid={cid}&page=/patterns";
    }

    /// <summary>A node id as Show Bus §7.1 takes one: 1–40 letters, digits, <c>-</c> or <c>_</c>.</summary>
    public static bool IsNodeId(string? id)
        => id is { Length: >= 1 and <= 40 } && id.All(c => c is (>= 'a' and <= 'z') or (>= 'A' and <= 'Z') or (>= '0' and <= '9') or '-' or '_');

    /// <summary>
    /// Round 91: a Stagewatch's verdict on a socket — <c>{"type":"auth","ok":…}</c>, the first message a node link hears and
    /// sent again when the key, its binding or the PIN changes (Stagewatch 2.18's <c>NodeLinks.Verdict</c>,
    /// <c>Refusal</c> and <c>ViewRefusal</c>). Null for anything else, or an auth message with no boolean <c>ok</c>. The
    /// words are kept to one line of at most <see cref="MaxWords"/> characters, with <paramref name="secret"/> and anything
    /// shaped as a Stagewatch key masked: they are the far end's, and reach the log, STATE and every reader.
    /// </summary>
    public static StagewatchAuth? Auth(string? json, string? secret = null)
    {
        if (string.IsNullOrWhiteSpace(json) || json.Length > 16 * 1024) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            var r = doc.RootElement;
            if (r.ValueKind != JsonValueKind.Object || Str(r, "type") != "auth") return null;
            if (!r.TryGetProperty("ok", out var ok) || ok.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) return null;
            var node = r.TryGetProperty("node", out var n) && n.ValueKind == JsonValueKind.Object ? n : default;
            var retry = Num(r, "retry");
            return new StagewatchAuth(
                ok.ValueKind == JsonValueKind.True,
                Words(Str(r, "scope"), secret),
                node.ValueKind == JsonValueKind.Object ? Words(Str(node, "t"), secret) : "",
                node.ValueKind == JsonValueKind.Object ? Words(Str(node, "id"), secret) : "",
                node.ValueKind == JsonValueKind.Object ? Words(Str(node, "verified"), secret) : "",
                Words(Str(r, "words"), secret),
                Words(Str(r, "error"), secret),
                retry > 0 ? (long)Math.Min(retry, int.MaxValue) : 0,
                r.TryGetProperty("view", out var view) && view.ValueKind == JsonValueKind.True,
                Words(Str(r, "refused"), secret));
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The most of a Stagewatch's words a reader keeps.</summary>
    public const int MaxWords = 200;

    /// <summary>
    /// A far end's words as every reader shows them: one line, at most <see cref="MaxWords"/> characters, this desk's
    /// <paramref name="secret"/> (the key it presented) and anything shaped as a Stagewatch key (<c>swk_…</c>) masked first —
    /// a far end that echoes the bearer puts no copy of it in the log, STATE, the Eye or the context this desk posts.
    /// </summary>
    public static string Words(string? said, string? secret = null)
    {
        var line = new string((said ?? "").Select(c => char.IsControl(c) || CharUnicodeInfo.GetUnicodeCategory(c) is UnicodeCategory.LineSeparator or UnicodeCategory.ParagraphSeparator ? ' ' : c).ToArray()).Trim();
        line = MaskSecret(line, secret);
        return line.Length <= MaxWords ? line : line[..MaxWords];
    }

    /// <summary>
    /// <paramref name="text"/> — a far end's words, or a whole answer before it is parsed — with <paramref name="secret"/>
    /// (this desk's key; one shorter than <see cref="Secrets.MinScrubLength"/> is left, as free text is) and every
    /// <c>swk_…</c> token replaced by <see cref="Secrets.Mask"/>.
    /// </summary>
    public static string MaskSecret(string text, string? secret)
        => MaskKeys(secret is { Length: >= Secrets.MinScrubLength } ? text.Replace(secret, Secrets.Mask, StringComparison.Ordinal) : text);

    /// <summary>A Stagewatch key's prefix (Stagewatch's <c>ApiKeys.Prefix</c>): a token that starts with it is a key, masked in any words.</summary>
    public const string KeyPrefix = "swk_";

    /// <summary>Every <c>swk_</c> token — the prefix and the letters and digits after it — replaced by <see cref="Secrets.Mask"/>.</summary>
    private static string MaskKeys(string line)
    {
        var at = line.IndexOf(KeyPrefix, StringComparison.Ordinal);
        if (at < 0) return line;
        var sb = new System.Text.StringBuilder(line.Length);
        var from = 0;
        while (at >= 0)
        {
            sb.Append(line, from, at - from).Append(Secrets.Mask);
            var end = at + KeyPrefix.Length;
            while (end < line.Length && char.IsAsciiLetterOrDigit(line[end])) end++;
            from = end;
            at = line.IndexOf(KeyPrefix, from, StringComparison.Ordinal);
        }
        return sb.Append(line, from, line.Length - from).ToString();
    }

    /// <summary>The least a refused key is held (Show Bus 1.3 §14's <c>peerlink.hold.refused.ms</c>, Stagewatch's own <c>retry</c>).</summary>
    public static readonly TimeSpan RefusedHold = TimeSpan.FromSeconds(60);

    /// <summary>The most a far end's <c>retry</c> or <c>Retry-After</c> holds the link: its number is bounded here.</summary>
    public static readonly TimeSpan MostHold = TimeSpan.FromHours(1);

    /// <summary>
    /// The most one dial of the link spends on its connection and its upgrade before it counts as a failed dial (Show Bus 1.3
    /// §14's <c>link.connect.timeout.ms</c>); a TLS dial with no certificate yet is cut shorter.
    /// </summary>
    public static readonly TimeSpan DialWithin = TimeSpan.FromSeconds(5);

    /// <summary>The most one side call of the link — the context's read and post, each approval call — takes (§14's <c>link.sidecall.timeout.ms</c>).</summary>
    public static readonly TimeSpan SideCallWithin = TimeSpan.FromSeconds(4);

    /// <summary>
    /// The longest message the link takes whole from a Stagewatch's socket (§14's <c>link.read.min.bytes</c> is the least a
    /// connector must take): a longer one is counted, said once and skipped, and the link stays up (13.14).
    /// </summary>
    public const int ReadWholeMost = 1024 * 1024;

    /// <summary>
    /// The least time, on one link, between two of its reads of the Stagewatch's context and two posts of this desk's own: they ride
    /// the first beat (<see cref="ShowBusLiveness.Beat"/>) at least this long after the last, so a post comes within this and a
    /// beat (§6's <c>context.post.min.ms</c> to <c>context.post.max.ms</c>).
    /// </summary>
    public static readonly TimeSpan ContextEvery = TimeSpan.FromSeconds(5);

    /// <summary>
    /// Round 91: how long an answer holds what it holds (Show Bus 1.3 §13.15.3; <see cref="StagewatchHoldKind"/> says
    /// what). A refused key — 401 (and a refusal on the socket, which stands for one) — or a node route refused (403) holds
    /// for the larger of <paramref name="atLeast"/> and the far end's <paramref name="retrySeconds"/>; a rate limit (429) its
    /// <c>Retry-After</c>, or <paramref name="atLeast"/> when it names none; anything else holds nothing. Never more than
    /// <see cref="MostHold"/>.
    /// </summary>
    public static TimeSpan Hold(int status, long retrySeconds, TimeSpan atLeast)
    {
        var said = TimeSpan.FromSeconds(Math.Clamp(retrySeconds, 0, (long)MostHold.TotalSeconds));
        var least = atLeast < TimeSpan.Zero ? TimeSpan.Zero : atLeast > MostHold ? MostHold : atLeast;
        return status switch
        {
            401 or 403 => said > least ? said : least,
            429 => said > TimeSpan.Zero ? said : least,
            _ => TimeSpan.Zero,
        };
    }

    /// <summary><see cref="Hold(int, long, TimeSpan)"/> at the contract's least, <see cref="RefusedHold"/>.</summary>
    public static TimeSpan Hold(int status, long retrySeconds) => Hold(status, retrySeconds, RefusedHold);

    private static string Str(JsonElement r, string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() ?? "" : "";

    private static double Num(JsonElement r, string name) => r.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var d) && double.IsFinite(d) ? d : 0;
}

/// <summary>
/// Round 91: a Stagewatch's verdict on a socket, as <see cref="StagewatchFrame.Auth"/> read it. <see cref="Scope"/> is the
/// credential it saw (node, control, read, mcp, pin, none); <see cref="Verified"/> what speaks for the node id it names
/// (key, operator, none); <see cref="Words"/> why an id is verified none; <see cref="Error"/> and <see cref="Retry"/> a
/// refusal's words and the seconds to wait; <see cref="View"/> the viewing code's refusal, after which the socket closes,
/// with <see cref="Refused"/> the key's own refusal when there was one.
/// </summary>
public sealed record StagewatchAuth(bool Ok, string Scope, string Kind, string Node, string Verified, string Words, string Error, long Retry, bool View, string Refused);

/// <summary>
/// What a word from a Stagewatch holds: nothing; every keyed call and the next dial, for a refused key or a rate limit; or
/// only the node routes, for a key the Stagewatch takes that speaks for no node of this desk's.
/// </summary>
public enum StagewatchHoldKind
{
    None,
    /// <summary>The key was refused: every keyed call and the next dial wait, and the next word that takes the key lifts the hold.</summary>
    Refused,
    /// <summary>The key is good and over its rate: every keyed call and the next dial wait out the far end's <c>Retry-After</c>, the key's standing kept.</summary>
    Busy,
    /// <summary>
    /// The key reads but speaks for no node of this desk's (Stagewatch §7.1: "a binding refused keeps the frames: the binding
    /// governs contexts and proposals, not reading"): the node routes — the context this desk posts, its proposals, the
    /// collect and the results — wait, the keyed read of its context and the redial do not; a word that takes the key lifts it.
    /// </summary>
    Node,
}

/// <summary>
/// Round 91: what one word from a Stagewatch does to the link's key — its standing now (null: unchanged), the hold it starts
/// and for how long, whether it takes the key (lifting a refusal's hold), and whether the certificate may be pinned on it
/// (an answer that authenticated the key). <see cref="Words"/> is what the link says of it when the key's standing says
/// nothing (a viewing code asked of a link that carried no key).
/// </summary>
public sealed record StagewatchKeyStep(StagewatchKey? Key, StagewatchHoldKind Hold, TimeSpan For, bool Takes, bool Pins, string Words);

/// <summary>
/// Round 91: what a Stagewatch made of this desk's key, as every reader shows it — the Nodes card, the Eye, the context's
/// peers, the assistant's facts and STATE's <c>bus.stagewatch.key</c>. <see cref="Word"/> is STATE's: <c>key</c> or
/// <c>operator</c> (it speaks for this desk), <c>none</c> (it speaks for no node of this desk's), <c>refused</c>, or <c>""</c>
/// (not said yet, or said by neither). <see cref="Said"/> is the Stagewatch's own words. <see cref="Legacy"/> is P-legacy's
/// fallback: a Stagewatch older than its node links sent no verdict, so a link that carried the key reads its standing from
/// the answers to its keyed calls — said in amber, a row in docs/OPEN.md, removed at Show Bus 1.4's default flip.
/// </summary>
public sealed record StagewatchKey
{
    public static readonly StagewatchKey Unknown = new();

    public string Word { get; init; } = "";

    public string Said { get; init; } = "";

    /// <summary>The key went with the link: a verdict of none is then the key not speaking for this desk.</summary>
    public bool Keyed { get; init; }

    /// <summary>The link heard no verdict (a Stagewatch older than 2.18): its key is judged by the answers to its keyed calls.</summary>
    public bool Legacy { get; init; }

    /// <summary>
    /// Show Bus 1.3 §13.6.8: the key is held back from a Stagewatch off this machine whose certificate no proof and no person
    /// pinned — its short fingerprint, which a person compares on the Stagewatch's screen before pressing Trust; "" otherwise.
    /// The link reads its public view meanwhile.
    /// </summary>
    public string TrustFor { get; init; } = "";

    /// <summary>The key waits for a person's Trust beside <see cref="TrustFor"/>.</summary>
    public bool Withheld => TrustFor.Length > 0;

    public bool Refused => Word == "refused";

    /// <summary>The link carried the key and the Stagewatch lets it speak for no node of this desk's.</summary>
    public bool NotAccepted => Word == "none" && Keyed;

    /// <summary>
    /// The key is refused (every keyed call held) or does not speak for this desk (its node routes held, its reads not):
    /// every reader says so in amber.
    /// </summary>
    public bool Fault => Refused || NotAccepted;

    /// <summary>Amber in every reader: a fault, P-legacy's labelled fallback on a link that carried the key, or a key held back for Trust.</summary>
    public bool Amber => Fault || (Legacy && Keyed) || Withheld;

    /// <summary>What every reader says of a refused key.</summary>
    public const string RefusedWords = "KEY REFUSED";

    /// <summary>What every reader says of a key the Stagewatch takes but lets speak for no node of this desk's.</summary>
    public const string NotAcceptedWords = "key not accepted for this desk";

    /// <summary>P-legacy's label: what every reader says of a link that carried the key and heard no verdict.</summary>
    public const string LegacyWords = "an older Stagewatch: no verdict on its link — the key is judged by its HTTP answers";

    /// <summary>
    /// "KEY REFUSED — unknown or revoked API key", "key not accepted for this desk — …", P-legacy's label, the words of a key held
    /// back for Trust, or "" when there is nothing to say.
    /// </summary>
    public string Line
    {
        get
        {
            var fault = Refused ? Join(RefusedWords, Said) : NotAccepted ? Join(NotAcceptedWords, Said) : "";
            var legacy = Legacy && Keyed ? LegacyWords : "";
            var trust = Withheld ? TrustWords(TrustFor) : "";
            return string.Join(" · ", new[] { fault, legacy, trust }.Where(w => w.Length > 0));
        }
    }

    /// <summary>
    /// Show Bus 1.3 §13.6.8's words for a key held back from a certificate no proof and no person pinned, with its short
    /// fingerprint: what every reader says until someone presses Trust on this desk.
    /// </summary>
    public static string TrustWords(string shortFingerprint)
        => $"it cannot prove itself yet (an older Stagewatch) — compare its fingerprint {shortFingerprint} and press Trust to send the key";

    private static string Join(string head, string said) => said.Length > 0 ? head + " — " + said : head;

    /// <summary>
    /// What a verdict on the socket does (Stagewatch 2.18 §7.1). <c>ok:false</c> refuses the key — held for its <c>retry</c> and
    /// at least <paramref name="atLeast"/>, the socket kept for the public frame (a rate limit, which Stagewatch says on the
    /// socket as a refused key, is held as that); the viewing code asked of a link that carried no key holds the next dial
    /// and says so, the key's standing unchanged. <c>ok:true</c> naming this desk's id: verified key or operator takes the
    /// key; verified none on a link that carried the key is the key not speaking for this desk (Show Bus 1.3 §13.15.3's
    /// "bound to another") — its node routes held, its reads and frames kept; on a link that carried none it is what the
    /// Stagewatch says of a claim, and holds nothing. A verdict naming another node, or none (a control page's), is not
    /// this link's: null.
    /// </summary>
    public static StagewatchKeyStep? OfVerdict(StagewatchAuth a, string selfId, bool keyed, TimeSpan atLeast)
    {
        if (!a.Ok)
        {
            if (a.View && a.Refused.Length == 0 && !keyed)
                return new StagewatchKeyStep(null, StagewatchHoldKind.Refused, StagewatchFrame.Hold(401, a.Retry, atLeast), false, false,
                    "it asks for its viewing code" + (a.Error.Length > 0 ? " — " + a.Error : "") + "; this desk reads it only with a key");
            // A 2.18 that refuses a key at its viewing code names why (refused); a refusal at the code that names nothing, on a
            // link that carried the key, is an older Stagewatch's, and a key it did not let past is one it did not take.
            var why = a.Refused.Length > 0 ? a.Refused
                : a.View ? ViewWords + (a.Error.Length > 0 ? " — " + a.Error : "")
                : a.Error.Length > 0 ? a.Error : "refused";
            return new StagewatchKeyStep(new StagewatchKey { Word = "refused", Said = why, Keyed = keyed }, StagewatchHoldKind.Refused, StagewatchFrame.Hold(401, a.Retry, atLeast), false, false, "");
        }
        if (a.Node.Length == 0 || !string.Equals(a.Node, selfId, StringComparison.Ordinal)) return null;
        var authenticated = a.Scope.Length > 0 && a.Scope != "none";
        if (a.Verified is "key" or "operator")
            return new StagewatchKeyStep(new StagewatchKey { Word = a.Verified, Keyed = keyed }, StagewatchHoldKind.None, TimeSpan.Zero, true, authenticated, "");
        var said = a.Words.Length > 0 ? a.Words : keyed ? $"it lets this key speak for no node {selfId}" : "";
        return keyed
            ? new StagewatchKeyStep(new StagewatchKey { Word = "none", Said = said, Keyed = true }, StagewatchHoldKind.Node, StagewatchFrame.Hold(403, 0, atLeast), false, authenticated, "")
            : new StagewatchKeyStep(new StagewatchKey { Word = "none", Said = said }, StagewatchHoldKind.None, TimeSpan.Zero, false, false, "");
    }

    /// <summary>
    /// What a reader says of a key a Stagewatch did not let past its viewing code without saying why — only a Stagewatch older
    /// than 2.18 does that (a 2.18 lets a key it takes past the code, and says why it refused one).
    /// </summary>
    public const string ViewWords = "its viewing code did not let this key pass";

    /// <summary>
    /// Whether the first message on a link says the Stagewatch is older than its node links: anything but a verdict; or, on
    /// a link that carried the key (<paramref name="keyed"/>), a viewing code's refusal that says nothing of the key (2.16's
    /// bytes — a 2.18 lets a key it takes past the code, and adds the key's own refusal and its <c>retry</c> to one it
    /// refuses). On a link that carried none, that refusal is what both say.
    /// </summary>
    public static bool OlderFirst(string type, StagewatchAuth? auth, bool keyed)
        => type != "auth" || (keyed && auth is { Ok: false, View: true, Refused.Length: 0, Retry: 0 });

    /// <summary>
    /// What the answer to one of this desk's keyed calls does. A 401 refuses the key — every keyed call held; a 403 says it
    /// speaks for no node of this desk's (out of scope, or bound to another) — its node routes held, its reads not; each for
    /// its <c>Retry-After</c> and at least <paramref name="atLeast"/>, in the Stagewatch's words. A 429 is a good key over its
    /// rate — held, its standing kept. A 2xx to this desk's own context (<paramref name="context"/>) is the key's standing as
    /// the answer's <c>verified</c> names it — none is the key not speaking for this desk, its node routes held — and takes
    /// the key. A 2xx that names nothing takes it with no word only on a link that heard no verdict (<paramref
    /// name="legacy"/>, a Stagewatch older than 2.18, P-legacy); on one that heard a verdict a missing or unknown word is
    /// refused as evidence and changes nothing. Any other answer changes nothing.
    /// </summary>
    public static StagewatchKeyStep? OfAnswer(int status, string error, string verified, long retryAfter, bool context, bool legacy, TimeSpan atLeast)
    {
        var said = StagewatchFrame.Words(error);
        switch (status)
        {
            case 401:
                return new StagewatchKeyStep(new StagewatchKey { Word = "refused", Said = said.Length > 0 ? said : "HTTP 401", Keyed = true }, StagewatchHoldKind.Refused, StagewatchFrame.Hold(401, retryAfter, atLeast), false, false, "");
            case 403:
                return new StagewatchKeyStep(new StagewatchKey { Word = "none", Said = said.Length > 0 ? said : "HTTP 403", Keyed = true }, StagewatchHoldKind.Node, StagewatchFrame.Hold(403, retryAfter, atLeast), false, false, "");
            case 429:
                return new StagewatchKeyStep(null, StagewatchHoldKind.Busy, StagewatchFrame.Hold(429, retryAfter, atLeast), false, false, said.Length > 0 ? said : "HTTP 429");
        }
        if (!context || status is < 200 or > 299) return null;
        return verified switch
        {
            "key" or "operator" => new StagewatchKeyStep(new StagewatchKey { Word = verified, Keyed = true }, StagewatchHoldKind.None, TimeSpan.Zero, true, true, ""),
            "none" => new StagewatchKeyStep(new StagewatchKey { Word = "none", Said = UnverifiedWords, Keyed = true }, StagewatchHoldKind.Node, StagewatchFrame.Hold(403, 0, atLeast), false, true, ""),
            "" when legacy => new StagewatchKeyStep(new StagewatchKey { Keyed = true }, StagewatchHoldKind.None, TimeSpan.Zero, true, true, ""),
            _ => null,
        };
    }

    /// <summary>A context taken and kept unverified: the key reads, and speaks for no node of this desk's.</summary>
    public const string UnverifiedWords = "it keeps this desk's context unverified — only a node key made for this desk speaks for it";
}

/// <summary>Round 87: what the Stagewatch link holds at an instant — whom it follows, the last frame and when it came, its context.</summary>
public sealed record StagewatchSight(string Name, string Address, ShowBusState State, StagewatchFrame? Frame, double FrameAtSeconds, ShowContext? Context, string Words)
{
    public static readonly StagewatchSight None = new("", "", ShowBusState.Gone, null, 0, null, "");

    /// <summary>A Stagewatch is linked and answering: its frames are the stage timer's truth.</summary>
    public bool Following => State == ShowBusState.Live && Frame is not null;

    /// <summary>
    /// The Stagewatch says its show is stuck (its stuck beat), and no state frame has come since. Show liveness, apart from
    /// the link's: the link stays live while only the show is stuck (PeerLink profile 13.10.5). A held frame is then
    /// <see cref="StagewatchFrame.Live"/> false, so nothing counts on from it; with no frame held the stage is not following
    /// and this desk's own timer runs.
    /// </summary>
    public bool Stuck { get; init; }

    /// <summary>The link is live and its show is stuck: every reader shows no time from it, the link amber.</summary>
    public bool StuckLive => Stuck && State == ShowBusState.Live;

    /// <summary>Round 91: what the Stagewatch made of this desk's key — its verdict, or P-legacy's HTTP evidence.</summary>
    public StagewatchKey Key { get; init; } = StagewatchKey.Unknown;
}

/// <summary>
/// Round 87: the stage timer when a Stagewatch is linked. One room, one timer: Patterns' stage page, timer page and
/// the stage timer node show the Stagewatch's time instead of running a second countdown beside it. Pure: the frame,
/// the moment it arrived and now on the monotonic clock are handed in.
/// </summary>
public static class StageFollow
{
    /// <summary>
    /// The Stagewatch's time now: a counting countdown counts on from the frame (a second late frame does not freeze the
    /// page); anything else is shown as the frame said. A frame from a stuck Stagewatch (live:false) is shown as idle —
    /// a frozen number on a speaker's screen is worse than none.
    /// </summary>
    public static StageTime Time(StagewatchFrame frame, double frameAtSeconds, double nowSeconds)
    {
        if (!frame.Live || frame.State is "idle" or "") return StageTime.Idle;
        var seconds = frame.Seconds;
        if (frame.Counting && frame.Countdown) seconds -= Math.Max(0, nowSeconds - frameAtSeconds);
        else if (frame.Counting && frame.Mode == "countup") seconds += Math.Max(0, nowSeconds - frameAtSeconds);
        var phase = frame.State switch
        {
            "paused" => StageTimerPhase.Paused,
            "expired" => StageTimerPhase.Over,
            _ => frame.Countdown && seconds < 0 ? StageTimerPhase.Over : StageTimerPhase.Running,
        };
        var progress = frame.Countdown && frame.Total > 0 ? Math.Clamp(1 - Math.Max(0, seconds) / frame.Total, 0, 1) : 0;
        return new StageTime(phase, seconds, Colour(frame, phase), phase == StageTimerPhase.Over ? 1 : progress);
    }

    /// <summary>The Stagewatch's own colour words: overtime and its last stretch red, its warning amber, else green.</summary>
    public static string Colour(StagewatchFrame frame, StageTimerPhase phase)
    {
        if (phase == StageTimerPhase.Over) return "red";
        return frame.Phase switch
        {
            "overtime" or "critical" or "danger" or "red" => "red",
            "warning" or "caution" or "wrap" or "amber" or "yellow" => "amber",
            _ => "green",
        };
    }

    /// <summary>The page's line: "following Stagewatch Hall A", or why not.</summary>
    public static string Words(StagewatchSight sight) => sight.Name.Length == 0 && sight.Address.Length == 0
        ? ""
        : sight.State switch
        {
            ShowBusState.Live when sight.Stuck && sight.Frame is null => $"Stagewatch {sight.Name} says its show is stuck — this desk's own stage timer runs until its first frame",
            ShowBusState.Live when sight.Frame is { Live: false } => $"following Stagewatch {sight.Name} — it says its show is stuck; the time is not shown",
            ShowBusState.Live => $"following Stagewatch {sight.Name}".TrimEnd(),
            ShowBusState.Stale => $"Stagewatch {sight.Name} has gone quiet — the time is held back until it answers".Replace("  ", " "),
            _ => $"Stagewatch {sight.Name} is not answering — this desk's own stage timer runs".Replace("  ", " "),
        };

    /// <summary>A typed "host:port" (or "host", port 9924) as a Stagewatch's address; null when it is not one.</summary>
    public static (string Host, int Port)? ParseAddress(string? typed)
    {
        var t = (typed ?? "").Trim();
        if (t.Length == 0 || t.Length > 255 || t.Contains('/') || t.Contains('@') || t.Contains(' ')) return null;
        if (t.StartsWith('['))
        {
            // "[fe80::1]:9924"
            return IPEndPoint.TryParse(t, out var ep) && ep.Port is > 0 and <= 65535 && t.Contains("]:", StringComparison.Ordinal) ? (ep.Address.ToString(), ep.Port) : null;
        }
        var colons = t.Count(c => c == ':');
        if (colons > 1) return IPAddress.TryParse(t, out var v6) ? (v6.ToString(), DefaultPort) : null;   // a bare IPv6 address
        if (colons == 1)
        {
            var colon = t.IndexOf(':');
            var host = t[..colon];
            return int.TryParse(t[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var port) && port is > 0 and <= 65535 && Uri.CheckHostName(host) != UriHostNameType.Unknown
                ? (host, port) : null;
        }
        return Uri.CheckHostName(t) != UriHostNameType.Unknown ? (t, DefaultPort) : null;
    }

    /// <summary>Stagewatch's web port.</summary>
    public const int DefaultPort = 9924;
}

/// <summary>
/// Round 87: the show network as this desk shows it — one projection for the Eye's CONTROL plane, the Nodes page and the
/// context's peers. Another Patterns is left out (the beacon's node cards already draw it, by its process); a Stagewatch
/// this desk is linked to carries its context. Pure: the peers heard and the link's sight are handed in.
/// </summary>
public static class ShowBusView
{
    public static IReadOnlyList<EyeShowApp> Apps(IReadOnlyList<ShowBusPeer> peers, DateTime utcNow, StagewatchSight sight)
    {
        var apps = new List<EyeShowApp>();
        var linkedSeen = false;
        var linkedId = LinkedId(peers, sight);
        foreach (var p in peers.Where(p => p.Kind != "patterns"))
        {
            var linked = linkedId is not null && p.Id == linkedId;
            linkedSeen |= linked;
            // Show Bus 1.3 §2: every address the id was heard from, the one dialled first.
            var addresses = p.Addresses.Count > 0 ? string.Join(", ", p.Addresses) : p.Address;
            var words = string.Join(" · ", new[] { p.Host.Length > 0 && p.Host != p.Name ? p.Host : "", addresses, p.Caps.Count > 0 ? string.Join(", ", p.Caps.Take(6)) : "" }.Where(w => w.Length > 0));
            var state = linked ? Worse(p.StateAt(utcNow), sight.State) : p.StateAt(utcNow);
            var stuck = linked && sight.Stuck && state == ShowBusState.Live;
            apps.Add(new EyeShowApp(p.Id, p.KindWords, p.Name.Length > 0 ? p.Name : p.Host, state, linked, words)
            {
                Context = linked ? Shown(sight.Context, stuck) : null,
                Stuck = stuck,
                Key = linked ? sight.Key : StagewatchKey.Unknown,                                   // round 91: the linked one's verdict on this desk's key
                Conflict = p.Conflict?.Words ?? "",                                                 // Show Bus 1.3 §2: two running nodes say its id
            });
        }
        // A Stagewatch reached by a typed address that never said hello (another subnet, a loopback-only hello) is still drawn.
        if (!linkedSeen && sight.Address.Length > 0)
        {
            var id = sight.Context?.Node.Id is { Length: > 0 } cid ? cid : "stagewatch";
            var stuck = sight.StuckLive;
            apps.Add(new EyeShowApp(id, "Stagewatch", sight.Context?.Node.Name is { Length: > 0 } n ? n : sight.Name, sight.State, true, sight.Address)
            {
                Context = Shown(sight.Context, stuck),
                Stuck = stuck,
                Key = sight.Key,
            });
        }
        return apps;
    }

    /// <summary>
    /// Round 91: a linked app whose verdict on this desk's key is amber (refused, speaking for no node of this desk's, or
    /// P-legacy's fallback) while its link is live: every reader takes the worse of its own light and amber, never red for
    /// the key alone. A link gone quiet or gone says that instead.
    /// </summary>
    public static bool KeyAmber(EyeShowApp app) => app.Linked && app.State == ShowBusState.Live && app.Key.Amber;

    /// <summary>
    /// Round 91: a linked app whose verdict on this desk's key is a fault (refused, or speaking for no node of this desk's) — or
    /// whose key this desk holds back for a person's Trust (Show Bus 1.3 §13.6.8) — its link live and its show not stuck: the
    /// key's line is what this desk can say of the link, so every reader with one headline — the context's peers, the Eye's card,
    /// STATE's <c>bus.stagewatch.headline</c> and the deck that reads it — says it in place of the app's own, the amber it shows
    /// with its reason. A link gone quiet or gone says that instead; a stuck show says so first.
    /// </summary>
    public static bool KeyLineLeads(EyeShowApp app) => app.Linked && app.State == ShowBusState.Live && !app.Stuck && (app.Key.Fault || app.Key.Withheld);

    /// <summary>
    /// Show Bus 1.3 §2: an app whose id two running nodes say, while it is live — every reader takes the worse of its own
    /// light and amber and names both machines; never red for that alone. Quiet or gone says that instead.
    /// </summary>
    public static bool ConflictAmber(EyeShowApp app) => app.Conflict.Length > 0 && app.State == ShowBusState.Live;

    /// <summary>What every reader adds to a conflict's words: what this desk does about it (Show Bus 1.3 §2, 13.15.3).</summary>
    public const string ConflictHoldWords = "no new link to it until they stop taking turns for a minute";

    /// <summary>A conflict on the Eye's edge.</summary>
    public const string ConflictEdgeWords = "two machines say this id";

    /// <summary>What every reader says of a linked app whose show is stuck (PeerLink profile 13.16.1).</summary>
    public const string StuckWords = "says its show is stuck — no time shown";

    /// <summary>What a counting thing of a stuck app says in place of its time.</summary>
    public const string NoTimeWords = "no time shown — its show is stuck";

    /// <summary>
    /// The things whose words are a count that runs on with the show — a timer, a schedule, a playlist, a chase, a
    /// timecode, a clock. From an app whose show is stuck none is shown as current (PeerLink profile 13.10.6).
    /// </summary>
    public static bool IsCounting(string? kind) => (kind ?? "").Trim().ToLowerInvariant() is "timer" or "schedule" or "playlist" or "chase" or "timecode" or "clock";

    /// <summary>A stuck show's light: the worse of the app's own light and amber — never red for the stall alone (profile 13.16.1).</summary>
    public static string StuckLight(string? light) => ShowContext.Word(ShowContext.Worst(new[] { ShowContext.LightOf(light), CheckLight.Amber }));

    /// <summary>
    /// A linked app's context as every reader shows it while its show is stuck: its light no better than amber, its
    /// headline the stall's words (a headline holds a count), and each counting thing red with no time. Its other things
    /// keep their own words and lights. The same context in, the same context out, so the Eye rebuilds only on a change.
    /// </summary>
    public static ShowContext NoTime(ShowContext c) => c with
    {
        Light = StuckLight(c.Light),
        Headline = StuckWords,
        Items = c.Items.Select(i => IsCounting(i.Kind) ? i with { Light = "red", State = NoTimeWords } : i).ToList(),
    };

    private static ShowContext? Shown(ShowContext? context, bool stuck) => Steady(context) is { } c && stuck ? NoTime(c) : Steady(context);

    /// <summary>
    /// Round 88 (closes L52): this desk's own nodes — timers, callers, the arcade — as the Eye draws them from the context
    /// each sends over the twin: one card per node, its things inside, quiet after 6 s of silence, gone after 12.
    /// </summary>
    public static IReadOnlyList<EyeShowApp> Nodes(IReadOnlyList<(string Name, ShowContext Context, DateTime AtUtc)> nodes, DateTime utcNow)
        => nodes.Select(n => new EyeShowApp("node:" + n.Context.Node.Id, "Patterns node", n.Context.Node.Name.Length > 0 ? n.Context.Node.Name : n.Name,
                ShowBusLiveness.StateOf(TimeSpan.FromSeconds(Math.Max(5, (utcNow - n.AtUtc).TotalSeconds) - 5)), true, n.Context.Headline)
            {
                Context = n.Context with { At = 0 },
            })
            .ToList();

    /// <summary>The context's peers row: each app with its light and headline (its own context's when this desk reads it), never its peers.</summary>
    public static IReadOnlyList<ShowContextPeer> Peers(IReadOnlyList<EyeShowApp> apps) => apps.Select(a => new ShowContextPeer
    {
        Kind = a.KindWords switch { "Stagewatch" => "main", "AGMessage" => "messenger", "Slides agent" => "agent", "Patterns node" => "patterns", _ => a.KindWords.ToLowerInvariant() },
        Id = a.Id,
        Name = a.Name,
        Light = a.State switch
        {
            ShowBusState.Gone => "red",
            ShowBusState.Stale => "amber",
            _ when a.Stuck || KeyAmber(a) || ConflictAmber(a) => StuckLight(a.Context?.Light),
            _ => a.Context?.Light ?? "grey",
        },
        // Round 91: a key refused or not speaking for this desk, on a live link, is what this desk can say of it now: said in place of the headline.
        // Show Bus 1.3 §2: so is an id two running nodes say, while it is live.
        Headline = KeyLineLeads(a) ? a.Key.Line
            : ConflictAmber(a) && !a.Stuck ? a.Conflict
            : a.Context?.Headline is { Length: > 0 } h ? h
            : a.State == ShowBusState.Live ? (a.Stuck ? StuckWords : a.Linked ? "linked" : "heard on the show network")
            : a.State == ShowBusState.Stale ? "quiet" : "gone quiet",
    }).ToList();

    /// <summary>
    /// The id of the heard Stagewatch the link is to, or null: one whose hellos came from the link's address at its port. Two
    /// heard there (an id renewed, a datagram's source forged) are told apart by the id the link's own context names, or
    /// neither is taken — the link is then drawn as an app of its own, never one heard app shown as another.
    /// </summary>
    private static string? LinkedId(IReadOnlyList<ShowBusPeer> peers, StagewatchSight sight)
    {
        if (sight.Address.Length == 0) return null;
        var heard = peers.Where(p => p.Kind == "main" && LinkedTo(sight, p)).Select(p => p.Id).Distinct(StringComparer.Ordinal).ToList();
        if (heard.Count == 1) return heard[0];
        return sight.Context?.Node.Id is { Length: > 0 } verified && heard.Contains(verified, StringComparer.Ordinal) ? verified : null;
    }

    /// <summary>
    /// Whether the link's address names this peer: an address its hellos came from, at one of its doors (Show Bus 1.3 §2) —
    /// never what a hello says of its own host.
    /// </summary>
    private static bool LinkedTo(StagewatchSight sight, ShowBusPeer p)
    {
        var colon = sight.Address.LastIndexOf(':');
        var host = colon > 0 ? sight.Address[..colon].Trim('[', ']') : sight.Address;
        var port = colon > 0 && int.TryParse(sight.Address[(colon + 1)..], NumberStyles.None, CultureInfo.InvariantCulture, out var n) ? n : 0;
        return p.HeardAt(host) && (port == 0 || port == p.Ports.Http || port == p.Ports.Ws || port == p.Ports.Https);
    }

    /// <summary>
    /// The show network for the assistant's brief, in words: each app with its state, light and headline, and for the
    /// one this desk reads, its own things (the reds and ambers first), its last lines, what may be proposed to it and
    /// the apps it hears. The same evidence the Eye draws — unknown stays unknown.
    /// </summary>
    public static IReadOnlyList<string> BriefLines(IReadOnlyList<EyeShowApp> apps)
    {
        var lines = new List<string>();
        foreach (var a in apps)
        {
            var state = a.State switch { ShowBusState.Gone => "gone quiet", ShowBusState.Stale => "quiet", _ => a.Linked ? "linked" : "heard, not linked" };
            var c = a.Context;
            // A stuck show: the link is live and amber, its counting things carry no time (the context says so, NoTime).
            if (a.Stuck && a.State == ShowBusState.Live && c is null) lines.Add($"{a.KindWords} {a.Name} — {state} — {StuckLight(null)}: {StuckWords}");
            else lines.Add($"{a.KindWords} {a.Name} — {state}" + (c is null ? "" : $" — {(KeyAmber(a) || ConflictAmber(a) ? StuckLight(c.Light) : c.Light)}: {c.Headline}"));
            // Round 91: what it made of this desk's key, in its own words (a refusal, a key that speaks for another desk, P-legacy's label).
            if (a.Linked && a.Key.Line.Length > 0) lines.Add($"  this desk's key: {a.Key.Line}");
            // Show Bus 1.3 §2: two running nodes say its id.
            if (a.Conflict.Length > 0) lines.Add($"  {a.Conflict} — {ConflictHoldWords}");
            if (c is null || a.State == ShowBusState.Gone) continue;
            foreach (var i in c.Items.OrderByDescending(i => i.Light == "red").ThenByDescending(i => i.Light == "amber").Take(15))
                lines.Add($"  · {i.Label} ({i.Kind}) — {i.Light}" + (i.State.Length > 0 ? ": " + i.State : ""));
            foreach (var r in c.Recent.TakeLast(5)) lines.Add($"  recent: {r.Text}");
            if (c.Commands.Count > 0 || c.CommandsDropped > 0) lines.Add("  may be proposed to it: " + BriefCommandsLine(c));
            foreach (var p in c.Peers.Take(6)) lines.Add($"  it hears {ShowBusPeers.KindWords(p.Kind)} {p.Name} — {p.Light}" + (p.Headline.Length > 0 ? ": " + p.Headline : ""));
        }
        return lines;
    }

    /// <summary>The most of a peer's verbs the assistant's brief lists (Show Bus 1.3 §6: a view may show fewer, and says how many it left out).</summary>
    public const int BriefCommands = 32;

    /// <summary>
    /// A peer's verbs in the brief: the first <see cref="BriefCommands"/> as the peer listed them (its panic, air and
    /// transport first), each with its risk, then how many were left out — kept but not listed here, and those this
    /// desk's reader did not keep at all (<see cref="ShowContext.CommandsDropped"/>).
    /// </summary>
    public static string BriefCommandsLine(ShowContext c)
    {
        var listed = c.Commands.Take(BriefCommands).Select(x => $"{x.Verb} ({x.Risk})").ToList();
        var notListed = c.Commands.Count - listed.Count;
        var left = notListed + c.CommandsDropped;
        var line = listed.Count > 0 ? string.Join("; ", listed) : "none this desk kept";
        if (left == 0) return line;
        return line + $" — {left} more left out here" + (c.CommandsDropped == 0 ? ""
            : $" ({c.CommandsDropped} not kept by this desk: a verb empty or over {ShowContext.MaxVerbChars} characters, or past the first {ShowContext.MaxCommands})");
    }

    /// <summary>A context without its moment: the Eye rebuilds when what a context says moves, not when it was said.</summary>
    private static ShowContext? Steady(ShowContext? c) => c is null ? null : c with { At = 0 };

    private static ShowBusState Worse(ShowBusState a, ShowBusState b) => (ShowBusState)Math.Max((int)a, (int)b);
}

/// <summary>Round 87: one other show app on the Nodes page — its kind in its colour, its name, and a line of what it is doing.</summary>
public sealed record ShowAppCard(string Id, string KindLabel, string Name, string Line, string Hue, bool Linked)
{
    /// <summary>What the page compares to know the card moved.</summary>
    public string Key => $"{Id}:{KindLabel}:{Name}:{Line}:{Hue}:{Linked}";

    public static ShowAppCard Of(EyeShowApp app)
    {
        var state = app.State switch { ShowBusState.Gone => "gone quiet", ShowBusState.Stale => "quiet", _ => app.Linked ? "linked" : "heard" };
        var line = app.Stuck && app.State == ShowBusState.Live ? $"{state} · {ShowBusView.StuckWords}"
            : app.Context is { Headline.Length: > 0 } c && app.State == ShowBusState.Live ? $"{state} · {c.Headline}" : state + (app.Words.Length > 0 ? " · " + app.Words : "");
        // Round 91: what it made of this desk's key — a refusal, a key that speaks for another desk, P-legacy's label.
        if (app.Linked && app.Key.Line.Length > 0) line += " · " + app.Key.Line;
        // Show Bus 1.3 §2: two running nodes say its id — both named, and what this desk does about it.
        if (app.Conflict.Length > 0) line += " · " + app.Conflict + " — " + ShowBusView.ConflictHoldWords;
        var hue = app.State switch
        {
            ShowBusState.Gone => "#B03030",
            ShowBusState.Stale => "#FFB020",
            // A stuck show, this desk's key refused, or its id said by two machines: the worse of the app's own light and amber — red only when its own light is red.
            _ when app.Stuck || ShowBusView.KeyAmber(app) || ShowBusView.ConflictAmber(app) => ShowBusView.StuckLight(app.Context?.Light) == "red" ? "#B03030" : "#FFB020",
            _ => app.KindWords switch { "Stagewatch" => "#35E0D0", "AGMessage" => "#B98CFF", "Slides agent" => "#5FD0FF", _ => "#8A93A6" },
        };
        return new ShowAppCard(app.Id, app.KindWords, app.Name, line, hue, app.Linked);
    }

    /// <summary>
    /// Round 91: the Nodes page's words for this desk's show network id — the id a Stagewatch's node key speaks for, so the
    /// operator can name it there — and, when its folder could not keep it, that a key bound to it refuses this desk after a
    /// restart. "" with no id.
    /// </summary>
    public static string IdLine(string id, bool kept) => id.Length == 0 ? ""
        : kept ? $" · show network id {id}"
        : $" · show network id {id} — not kept (its folder is not writable): a Stagewatch key bound to it refuses this desk after a restart";

    /// <summary>"2 apps on the show network · Stagewatch link: following Hall A." — or why there are none.</summary>
    public static string NetworkLine(int count, bool hearing, string linkStatus) => NetworkLine(count, hearing, linkStatus, 0);

    /// <summary>
    /// The line, and — once the hello table has had to put an app out or turn one away — how many, since start (Show Bus
    /// 1.3 §2: it keeps <see cref="ShowBusPeers.MaxPeers"/>; something on the network says hello as many apps).
    /// </summary>
    public static string NetworkLine(int count, bool hearing, string linkStatus, long dropped)
    {
        var heard = !hearing ? "Not listening on the show network (remote control is off)." : count == 0 ? "No other show app heard on the show network yet (UDP 9928)." : count == 1 ? "1 app on the show network." : $"{count} apps on the show network.";
        if (dropped > 0) heard += " " + DroppedWords(dropped);
        return linkStatus.Length > 0 ? heard + " " + linkStatus : heard;
    }

    /// <summary>
    /// The words the Nodes page's show network line gives the hello table's drops since start: "" with none. The log says them
    /// in its own bounded line (<see cref="ShowBusTableLog"/>) and STATE counts them in <c>bus.dropped</c>; the brief carries none.
    /// </summary>
    public static string DroppedWords(long dropped) => dropped <= 0 ? ""
        : string.Create(CultureInfo.InvariantCulture, $"{dropped} {(dropped == 1 ? "app" : "apps")} dropped from the hello table since start — it keeps {ShowBusPeers.MaxPeers}, the one heard longest ago that this desk does not need going first: something on the network says hello as many apps.");
}
