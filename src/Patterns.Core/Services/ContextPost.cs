using System.Globalization;
using System.Text;

namespace Patterns.Core.Services;

/// <summary>
/// One context another app posted to this desk (Show Bus 1.3 §6, Posting), as the desk keeps it: the kind and id its key speaks
/// for (13.5.2), the context with its revision (<see cref="KeptContext"/>), when it came on the posted table's monotonic clock, its
/// size as sent and the link's id it came over. Nothing here is a key, a kid's secret or an address.
/// </summary>
public sealed record PostedContext(string T, string Id, KeptContext Kept, long AtMs, int Bytes, string Lid)
{
    /// <summary>How long ago it came, in milliseconds, on the table's clock.</summary>
    public long AgeMs(long nowMs) => Math.Max(0, nowMs - AtMs);
}

/// <summary>A posted context as the Super Check names it: its node, its name, its age and its size — never its text.</summary>
public sealed record PostedContextFact(string T, string Id, string Name, long AgeMs, int Bytes);

/// <summary>What one post did to the table.</summary>
public enum PostOutcome
{
    /// <summary>An id the table did not hold (never, or no longer after <see cref="ContextPost.KeptMs"/>): its first keep.</summary>
    First,

    /// <summary>An id the table held: the latest wins, the revision moving only where what the context says moved.</summary>
    Replaced,

    /// <summary>A new id with <see cref="ContextPost.MaxContexts"/> held: refused in words, nothing kept.</summary>
    Full,
}

/// <summary>
/// The contexts other apps posted to this desk (Show Bus 1.3 §6, Posting): the latest for each node id, kept
/// <see cref="ContextPost.KeptMs"/> after it came and then dropped by the next read or write — never by a timer — at most
/// <see cref="ContextPost.MaxContexts"/> ids, each at most <see cref="ContextPost.MaxBytes"/> as sent. One writer at a time (a lock
/// the reader tasks of the wire's connections take for one copy of at most 128 references); readers take the published array with
/// one volatile read and never wait, so the desk's thread is handed a reference and nothing else. Thread-safe.
/// </summary>
public sealed class PostedContexts
{
    private readonly object _gate = new();
    private PostedContext[] _all = Array.Empty<PostedContext>();

    /// <summary>
    /// Keeps <paramref name="context"/> for <paramref name="t"/>:<paramref name="id"/>, the latest for its id; <c>Moved</c> when what the
    /// table says moved (a first keep, or a revision that moved), so a reader is told only then; <c>Dropped</c> the contexts this write
    /// found past <see cref="ContextPost.KeptMs"/> and let go, which its caller says (each one once, as <see cref="Take"/>'s are).
    /// </summary>
    public (PostOutcome Outcome, PostedContext? Kept, bool Moved, IReadOnlyList<PostedContext> Dropped) Keep(string t, string id, ShowContext context, int bytes, string lid, long nowMs)
    {
        lock (_gate)
        {
            var all = Volatile.Read(ref _all);
            var live = all.Where(p => Fresh(p, nowMs)).ToList();
            IReadOnlyList<PostedContext> dropped = live.Count == all.Length ? Array.Empty<PostedContext>() : all.Where(p => !Fresh(p, nowMs)).ToArray();
            var at = live.FindIndex(p => string.Equals(p.Id, id, StringComparison.Ordinal));
            if (at >= 0)
            {
                var was = live[at];
                var replaced = new PostedContext(t, id, KeptContext.Next(was.Kept, context), nowMs, bytes, lid);
                live[at] = replaced;
                Volatile.Write(ref _all, live.ToArray());
                return (PostOutcome.Replaced, replaced, replaced.Kept.Rev != was.Kept.Rev || !string.Equals(was.T, t, StringComparison.Ordinal) || dropped.Count > 0, dropped);
            }
            if (live.Count >= ContextPost.MaxContexts)
            {
                Volatile.Write(ref _all, live.ToArray());
                return (PostOutcome.Full, null, dropped.Count > 0, dropped);
            }
            var kept = new PostedContext(t, id, KeptContext.Next(null, context), nowMs, bytes, lid);
            live.Add(kept);
            Volatile.Write(ref _all, live.ToArray());
            return (PostOutcome.First, kept, true, dropped);
        }
    }

    /// <summary>
    /// The contexts kept now, and those the table held until now and drops at this read (each one once: the reader that drops it is
    /// the one that says so). Never blocks behind a writer longer than the writer's one copy.
    /// </summary>
    public (IReadOnlyList<PostedContext> Live, IReadOnlyList<PostedContext> Dropped) Take(long nowMs)
    {
        var all = Volatile.Read(ref _all);
        if (all.All(p => Fresh(p, nowMs))) return (all, Array.Empty<PostedContext>());
        lock (_gate)
        {
            all = Volatile.Read(ref _all);
            var live = all.Where(p => Fresh(p, nowMs)).ToArray();
            var dropped = all.Where(p => !Fresh(p, nowMs)).ToArray();
            Volatile.Write(ref _all, live);
            return (live, dropped);
        }
    }

    /// <summary>The contexts kept now (none past <see cref="ContextPost.KeptMs"/>), without dropping anything.</summary>
    public IReadOnlyList<PostedContext> Live(long nowMs) => Volatile.Read(ref _all).Where(p => Fresh(p, nowMs)).ToList();

    /// <summary>The bytes the kept contexts were posted as: at most <see cref="ContextPost.MaxContexts"/> × <see cref="ContextPost.MaxBytes"/>.</summary>
    public long BytesHeld(long nowMs) => Live(nowMs).Sum(p => (long)p.Bytes);

    /// <summary>Kept for <see cref="ContextPost.KeptMs"/> after it came, then dropped: at that age it is gone.</summary>
    private static bool Fresh(PostedContext p, long nowMs) => nowMs - p.AtMs < ContextPost.KeptMs;
}

/// <summary>
/// The refusals of <c>CONTEXT POST</c>, summed by reason, said in one line at most once a minute (13.19.4): the minute opens at the
/// first refusal after the last summary and its summary is said by the first refusal or read past its end. Counts only — no id, no
/// context, no key or kid — and none of them is counted against a link's key (13.5.6). Thread-safe.
/// </summary>
public sealed class ContextPostRefusals
{
    /// <summary>Show Bus §14 <c>log.repeat.summary.ms</c>: one summary of the repeated refusals a minute.</summary>
    public static readonly long SummaryMs = (long)PeerLink.RepeatSummary.TotalMilliseconds;

    private readonly object _gate = new();
    private readonly SortedDictionary<string, int> _counts = new(StringComparer.Ordinal);
    private long _openedMs = -1;

    /// <summary>Counts one refusal; the summary of the minute that has ended before it, when one has, to be said now.</summary>
    public string? Refused(string reason, long nowMs)
    {
        lock (_gate)
        {
            var due = DueLocked(nowMs);
            if (_openedMs < 0) _openedMs = nowMs;
            _counts[reason] = _counts.TryGetValue(reason, out var n) ? n + 1 : 1;
            return due;
        }
    }

    /// <summary>The summary of a minute that has ended, once; null while none is due.</summary>
    public string? Due(long nowMs)
    {
        lock (_gate) return DueLocked(nowMs);
    }

    /// <summary>The refusals counted in the minute now open, by reason.</summary>
    public IReadOnlyDictionary<string, int> Open()
    {
        lock (_gate) return new Dictionary<string, int>(_counts, StringComparer.Ordinal);
    }

    private string? DueLocked(long nowMs)
    {
        if (_openedMs < 0 || nowMs - _openedMs < SummaryMs) return null;
        var total = _counts.Values.Sum();
        var each = string.Join(", ", _counts.Select(c => string.Create(CultureInfo.InvariantCulture, $"{c.Value} {c.Key}")));
        _counts.Clear();
        _openedMs = -1;
        return string.Create(CultureInfo.InvariantCulture, $"{total} CONTEXT POST{(total == 1 ? "" : "s")} refused in the last minute — {each}; nothing of them kept");
    }
}

/// <summary>
/// <c>CONTEXT POST &lt;json&gt;</c> on this desk's wire (Show Bus 1.3 §6, Posting, and §13): the gate that stands ahead of the router,
/// pure. A context is taken only on a connection LINK verified with a key bound to one node — issued or enrolled — of scope
/// <c>post</c> or above, and only for the node that key speaks for (its <c>node.t</c> and <c>node.id</c> the key's). A pairing code
/// speaks for no one node, a connection that did not run LINK (level 0, a deck, this machine trusted) proved no id, and an unpaired
/// one hears the wire's own not-paired words. Each refusal says its reason and none is counted against a key (13.5.6). What is kept is
/// scrubbed of whatever would carry a key, a window's address or a path, and keeps no commands: this desk sends nothing back to a
/// poster, and a post is a node's own act, never one a context lists or an assistant proposes.
/// </summary>
public static class ContextPost
{
    /// <summary>The verb as the wire spells it.</summary>
    public const string Verb = "CONTEXT POST";

    /// <summary>Show Bus §14 <c>context.bytes</c>: the most a posted context runs to, in UTF-8 bytes as sent.</summary>
    public const int MaxBytes = 65_536;

    /// <summary>Show Bus §14 <c>context.kept.ms</c>: how long a posted context is kept after it came.</summary>
    public const long KeptMs = 30_000;

    /// <summary>Show Bus §14 <c>peers.max</c>: the most node ids this desk keeps a posted context for.</summary>
    public const int MaxContexts = ShowBusPeers.MaxPeers;

    /// <summary>The lowest scope that posts (13.5.3).</summary>
    public const string Scope = "post";

    public const string KeptWords = "kept";
    public const string NoPeerLinkWords = "posting a context needs PeerLink — this connection did not prove its id";
    public const string CodeWords = "a pairing code speaks for no one node — posting a context needs a key made or enrolled for this node";
    public const string KeyGoneWords = "this link's key is no longer in force — revoked, or bound to another node — nothing it posts is kept; its link ends";
    public const string NotAContextWords = "not a context — {\"v\",\"node\":{\"t\",\"id\"}} is the least it carries";

    /// <summary>The reasons a post is refused, as the summary counts them.</summary>
    public static class Reasons
    {
        public const string Unpaired = "not paired";
        public const string NoPeerLink = "no PeerLink";
        public const string Code = "a pairing code";
        public const string Scope = "a key below post";
        public const string KeyGone = "a key no longer in force";
        public const string TooBig = "over context.bytes";
        public const string NotAContext = "not a context";
        public const string SpeaksFor = "another node's id or kind";
        public const string Full = "past peers.max";
    }

    /// <summary>13.5.2's words for a context that names a node other than the key's: never counted.</summary>
    public static string SpeaksForWords(string bound, string t, string id)
    {
        var at = bound.IndexOf(':', StringComparison.Ordinal);
        var key = at > 0 ? $"{bound[..at]} {bound[(at + 1)..]}" : bound;
        var said = EyeNetwork.IsId(id) && !Held(id) && t.Length is > 0 and <= 32 && t.All(char.IsAsciiLetterOrDigit) ? $"{t} {id}" : "another node";
        return $"this key speaks for {key} — not for {said}";
    }

    /// <summary>§6's <c>413</c> with the context's size.</summary>
    public static string TooBigWords(int bytes)
        => string.Create(CultureInfo.InvariantCulture, $"413 — this context is {bytes} bytes; context.bytes is {MaxBytes}");

    /// <summary>§6: a context for an id past this desk's own bound, refused with words.</summary>
    public static string FullWords
        => string.Create(CultureInfo.InvariantCulture, $"this desk keeps posted contexts for {MaxContexts} node ids (peers.max) — a new id is refused until one of them is {KeptMs / 1000} s old");

    /// <summary>What the gate made of one post: the context to keep (scrubbed), or the refusal's reason and words; the bytes as sent.</summary>
    public sealed record Decision(ShowContext? Context, string Reason, string Words, int Bytes)
    {
        public bool Ok => Context is not null;
    }

    /// <summary>
    /// The gate, in its order: an unpaired connection the wire's not-paired words; no LINK (level 0) PeerLink's; a code's link the
    /// code's; a key no longer in force (revoked or bound elsewhere, its link not yet closed) its own words, never a scope's; a key
    /// below <c>post</c> the scope's (<paramref name="scopeWords"/>, which name where it is raised); a
    /// body past <see cref="MaxBytes"/> <c>413</c>; one that is not a context, or names no id, the shape's; a context that names
    /// another node than the key's the binding's. <paramref name="scopeNow"/> is the scope the link's key stands at now, null when it is
    /// out of force.
    /// </summary>
    public static Decision Check(bool paired, LinkStanding? link, string? scopeNow, string json, Func<string, string> scopeWords)
    {
        var bytes = Encoding.UTF8.GetByteCount(json ?? "");
        if (link is null)
            return paired ? Refused(Reasons.NoPeerLink, NoPeerLinkWords, bytes) : Refused(Reasons.Unpaired, ControlProtocol.NotPaired, bytes);
        if (link.Verified is not ("key" or "link"))
            return Refused(Reasons.Code, CodeWords, bytes);
        if (scopeNow is null) return Refused(Reasons.KeyGone, KeyGoneWords, bytes);
        if (LinkAcceptor.Rank(scopeNow) < LinkAcceptor.Rank(Scope))
            return Refused(Reasons.Scope, scopeWords(scopeNow), bytes);
        if (bytes > MaxBytes) return Refused(Reasons.TooBig, TooBigWords(bytes), bytes);
        var context = ShowContext.Parse(json);
        if (context is null || !EyeNetwork.IsId(context.Node.Id)) return Refused(Reasons.NotAContext, NotAContextWords, bytes);
        if (!string.Equals(context.Node.Kind + ":" + context.Node.Id, link.Client, StringComparison.Ordinal))
            return Refused(Reasons.SpeaksFor, SpeaksForWords(link.Client, context.Node.Kind, context.Node.Id), bytes);
        return new Decision(Scrub(context), "", KeptWords, bytes);
    }

    private static Decision Refused(string reason, string words, int bytes) => new(null, reason, words, bytes);

    /// <summary>
    /// A posted context as this desk keeps it: every word that would carry a key, a window's address or a path said as held, every item
    /// and link whose id would carry one left out, its commands left out (§6: a post is the node's own act; this desk sends nothing back
    /// on it), its moment kept. The same context in, the same context out.
    /// </summary>
    public static ShowContext Scrub(ShowContext c)
    {
        static bool Bad(string? s) => Held(s);
        return c with
        {
            Node = c.Node with { Name = Clean(c.Node.Name) },
            Headline = Clean(c.Headline),
            Items = c.Items.Where(i => !Bad(i.Id)).Select(i => i with { Kind = Clean(i.Kind), Label = Clean(i.Label), State = Clean(i.State) }).ToList(),
            Links = c.Links.Where(l => !Bad(l.From) && !Bad(l.To)).Select(l => l with { Kind = Clean(l.Kind) }).ToList(),
            Recent = c.Recent.Select(r => r with { Text = Clean(r.Text) }).ToList(),
            Commands = Array.Empty<ShowContextCommand>(),
            CommandsDropped = 0,
            Peers = c.Peers.Where(p => !Bad(p.Id)).Select(p => p with
            {
                Kind = Clean(p.Kind), Name = Clean(p.Name), Headline = Clean(p.Headline), Heard = Clean(p.Heard), Link = Clean(p.Link), Verified = Clean(p.Verified),
            }).ToList(),
        };
    }

    /// <summary>
    /// Words a posted context may not pass on to any reader of this desk: what the Eye holds back from every export
    /// (<see cref="EyeNetwork.Secretish"/>: a key, a window's address, a path), and the capabilities the messenger — the poster this verb
    /// is first for — makes of its own: its context token (<c>agn_</c>) and a screen's address (<c>/d/</c>). A sender is told by §6 P8
    /// never to post them; this desk holds them whether or not it was told.
    /// </summary>
    public static bool Held(string? words)
        => EyeNetwork.Secretish(words) || (words is { Length: > 0 } w && (w.Contains("agn_", StringComparison.Ordinal) || w.Contains("/d/", StringComparison.Ordinal)));

    /// <summary>Words that reach a reader of this desk: whatever would carry a key, a capability address or a path is said as held.</summary>
    public static string Clean(string? words)
    {
        var w = words ?? "";
        if (!Held(w)) return w;
        var redacted = WindowAddresses.RedactText(w);
        return Held(redacted) ? "(held — not kept)" : redacted;
    }

    /// <summary>The journal row's and the log line's words for a first keep: the node, the link's id and the digest's first eight hex — no text of it.</summary>
    public static string FirstKeepWords(PostedContext p)
        => string.Create(CultureInfo.InvariantCulture,
            $"{ShowBusPeers.KindWords(p.T)} {p.Id} posted its context over its link (lid {p.Lid}) — kept {KeptMs / 1000} s after each post, {p.Bytes} bytes, digest {p.Kept.Digest[..8].ToLowerInvariant()}");

    /// <summary>The log line's words for a context the table no longer keeps.</summary>
    public static string DroppedWords(PostedContext p)
        => string.Create(CultureInfo.InvariantCulture, $"{ShowBusPeers.KindWords(p.T)} {p.Id}'s posted context is no longer kept — nothing posted for {KeptMs / 1000} s");
}
