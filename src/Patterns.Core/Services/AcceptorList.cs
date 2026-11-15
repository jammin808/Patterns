using System.Globalization;

namespace Patterns.Core.Services;

/// <summary>
/// Show Bus 1.3 §13.16.5: one link this desk accepted on its wire that LINK verified — its lid, the connector's <c>t:id</c> its proof
/// covered, the name its hello gave, where it dialled from, whether it rides TLS, what proved it (<c>key</c>, <c>link</c> or
/// <c>code</c>) and the kid, the scope it stands at now, since when, whether its key (or the token, for a code) was seen in clear, and,
/// once it has ended, when and why. Never a key, a proof, a nonce or a session credential. A deck that proved the token by the legacy
/// proof is no row here: it is a deck (13.18), and a row here is never a deck.
/// </summary>
public sealed record AppLinkRow(string Lid, string Client, string Name, string Address, bool Local, bool Tls, string Verified, string Kid, string Scope, DateTime SinceUtc)
{
    /// <summary>13.6.9: the key this link stands on (the token, for a code's link) arrived in clear from another machine.</summary>
    public bool ClearSeen { get; init; }

    /// <summary>When the link ended; null while it is open.</summary>
    public DateTime? EndedUtc { get; init; }

    /// <summary>Why it ended, in words ("closed by the operator", "key revoked"); "" while it is open.</summary>
    public string EndWords { get; init; } = "";

    /// <summary>Where its navigator said it is (NAV DECK), "" when it said nothing — the same fact a deck's row carries.</summary>
    public string Where { get; init; } = "";

    /// <summary>It asked for the desk's ACTION feed (RECORD ON) and hears what the operator does.</summary>
    public bool Recording { get; init; }

    /// <summary>
    /// Show Bus 1.3 §6 P2's K, named by the acceptor from what the connector has done on the link: it ran one of this desk's verbs
    /// (<c>controls</c>); otherwise it reads this desk's state (<c>follows</c>), which every link that stands hears.
    /// </summary>
    public bool Drove { get; init; }

    /// <summary>The connector's kind (§2): <c>main</c> for a Stagewatch.</summary>
    public string T => Client.IndexOf(':', StringComparison.Ordinal) is var at and > 0 ? Client[..at] : Client;

    /// <summary>The connector's node id.</summary>
    public string Id => Client.IndexOf(':', StringComparison.Ordinal) is var at and > 0 ? Client[(at + 1)..] : "";

    /// <summary>13.1: a row here ran LINK.</summary>
    public int Level => 1;

    public bool Ended => EndedUtc is not null;

    /// <summary>13.16.2's <c>plain</c>: a line from another machine with no TLS.</summary>
    public bool Plain => !Tls && !Local;

    /// <summary>The kid a revoke reaches: an issued or enrolled key's; "" for a code's link, which only a new token ends.</summary>
    public string KeyKid => Verified is "key" or "link" ? Kid : "";

    /// <summary>
    /// 13.16.5's light: ○ grey once ended (never red: an acceptor does not redial); △ amber over green while it is <c>plain</c> from
    /// another machine or its key was seen in clear; ✓ green otherwise (a plain link from this machine is an i note beside it).
    /// </summary>
    public CheckLight Light => Ended ? CheckLight.Grey : Plain || ClearSeen ? CheckLight.Amber : CheckLight.Green;

    /// <summary>The light's shape beside its colour.</summary>
    public string Mark => LinkRecord.MarkOf(Light);

    /// <summary>The flags beside the light (13.16.2's words): <c>plain</c>, <c>clear</c>, and <c>local</c> (i) for a plain line from this machine.</summary>
    public IReadOnlyList<string> Flags
    {
        get
        {
            var flags = new List<string>(2);
            if (Plain) flags.Add("plain");
            else if (!Tls) flags.Add("local");
            if (ClearSeen) flags.Add("clear");
            return flags;
        }
    }

    /// <summary>What the row is called: the name its hello gave, else its kind and id.</summary>
    public string Label => Name.Length > 0 ? Name : AppKeys.ConnectorWords(Client);

    /// <summary>What proved it, in words: "key e63c317763b1454f", "enrolled key …", "the pairing token".</summary>
    public string ProofWords => Verified switch
    {
        "code" => "the pairing token",
        "link" => "enrolled key " + Kid,
        _ => "key " + Kid,
    };

    /// <summary>
    /// The row in words, as the Nodes page, the Eye and the assistant say it: "Stagewatch (main 3f9c2a1b7d0e, 10.0.0.5) · key e63c… ·
    /// scope drive · line over TLS · since 20:14:03 UTC", with what makes it amber after it, or when and why it ended.
    /// </summary>
    public string Words
    {
        get
        {
            var who = Name.Length > 0 ? $"{Name} ({AppKeys.ConnectorWords(Client)}, {Address})" : $"{AppKeys.ConnectorWords(Client)} ({Address})";
            var line = Tls ? "line over TLS" : Local ? "line, plain on this machine" : "line, plain";
            var words = $"{who} · {ProofWords} · scope {Scope} · {line} · since {Stamp(SinceUtc)}";
            if (Plain) words += " · △ proved, not private — from another machine with no TLS";
            if (Recording) words += " · recording the desk's actions";
            if (Where.Length > 0) words += " · navigator: " + Where;
            if (ClearSeen) words += " · △ " + (Verified == "code" ? "the pairing token" : "its key") + " was seen in clear — replace it";
            if (EndedUtc is { } ended) words += $" · ended {Stamp(ended)}" + (EndWords.Length > 0 ? " — " + EndWords : "");
            return words;
        }
    }

    private static string Stamp(DateTime utc) => utc.ToString("HH:mm:ss", CultureInfo.InvariantCulture) + " UTC";
}

/// <summary>
/// Show Bus 1.3 §13.16.5: one refusal this desk's wire gave a LINK, counted this minute per address, claimed connector, kid and class:
/// "Stagewatch 3f9c2a1b7d0e (10.0.0.12): key e63c… refused (binding) — … ×3 this minute". The connector is what the ask claimed — a
/// refusal proved nothing — and the words are the ones the line heard, so nothing here is a secret.
/// </summary>
public sealed record AppLinkRefusal(string Address, string Client, string Kid, string Class, string Words, int Count, DateTime LastUtc)
{
    /// <summary>The refusal in words, its class in brackets unless it is the plain <c>refused</c>, its count this minute beside it.</summary>
    public string Line
        => $"{AppKeys.ConnectorWords(Client)} ({Address}): {(Kid == "code" ? "the pairing token" : "key " + Kid)} refused{(Class == "refused" ? "" : " (" + Class + ")")} — {Words} ×{Count.ToString(CultureInfo.InvariantCulture)} this minute";
}

/// <summary>
/// Show Bus 1.3 §13.16.5's memory beside the links that stand: the links that ended, each kept ○ grey for <c>peer.forget.ms</c> and
/// then left out, and the refusals of the last minute, counted. Thread-safe; time is handed in, so a test steps it. Both lists are
/// bounded (<see cref="MaxEnded"/>, <see cref="MaxRefusals"/>): list bounds, not governors — no measurement stands behind them, and a
/// refusal past the bound is counted in <see cref="RefusalsUncounted"/> rather than dropped silently.
/// </summary>
public sealed class AcceptorList
{
    /// <summary>The ended links kept at most (the oldest gives way).</summary>
    public const int MaxEnded = 32;

    /// <summary>The refusal rows kept at most in a minute.</summary>
    public const int MaxRefusals = 32;

    /// <summary>Show Bus §14 <c>peer.forget.ms</c>: how long an ended link stays on the list, grey.</summary>
    public static TimeSpan ForgetAfter => ShowBusLiveness.ForgetAfter;

    /// <summary>The window a refusal is counted in.</summary>
    public static readonly TimeSpan RefusalWindow = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private readonly List<AppLinkRow> _ended = new();
    private readonly List<AppLinkRefusal> _refusals = new();
    private long _uncounted;

    /// <summary>Refusals the bound left uncounted in a row of their own since start.</summary>
    public long RefusalsUncounted => Interlocked.Read(ref _uncounted);

    /// <summary>A link that stood has ended: kept grey with when and why.</summary>
    public void Ended(AppLinkRow row, DateTime utcNow, string why)
    {
        lock (_gate)
        {
            Sweep(utcNow);
            _ended.RemoveAll(r => r.Lid == row.Lid);
            _ended.Add(row with { EndedUtc = utcNow, EndWords = why ?? "" });
            while (_ended.Count > MaxEnded) _ended.RemoveAt(0);
        }
    }

    /// <summary>A LINK refused: counted on its row for this minute.</summary>
    public void Refused(string address, string client, string kid, string cls, string words, DateTime utcNow)
    {
        lock (_gate)
        {
            Sweep(utcNow);
            var at = _refusals.FindIndex(r => r.Address == address && r.Client == client && r.Kid == kid && r.Class == cls);
            if (at >= 0)
            {
                var r = _refusals[at];
                _refusals[at] = r with { Words = words, Count = r.Count + 1, LastUtc = utcNow };
            }
            else if (_refusals.Count < MaxRefusals)
            {
                _refusals.Add(new AppLinkRefusal(address, client, kid, cls, words, 1, utcNow));
            }
            else
            {
                Interlocked.Increment(ref _uncounted);
            }
        }
    }

    /// <summary>The ended links within <see cref="ForgetAfter"/>, oldest first.</summary>
    public IReadOnlyList<AppLinkRow> EndedRows(DateTime utcNow)
    {
        lock (_gate)
        {
            Sweep(utcNow);
            return _ended.ToList();
        }
    }

    /// <summary>The refusals of the last minute, newest first.</summary>
    public IReadOnlyList<AppLinkRefusal> Refusals(DateTime utcNow)
    {
        lock (_gate)
        {
            Sweep(utcNow);
            return _refusals.OrderByDescending(r => r.LastUtc).ToList();
        }
    }

    private void Sweep(DateTime utcNow)
    {
        // Within a window includes its end (13's rule): an ended link at exactly peer.forget.ms is still listed.
        _ended.RemoveAll(r => r.EndedUtc is { } ended && utcNow - ended > ForgetAfter);
        _refusals.RemoveAll(r => utcNow - r.LastUtc > RefusalWindow);
    }
}

/// <summary>
/// The acceptor's list in the words every reader shares (13.16.4: no reader derives a link fact of its own) — the Remote page's strip,
/// the Run strip's glance, the Super Check's row, the Health dashboard's tile, the assistant's facts. Pure.
/// </summary>
public static class AcceptorWords
{
    /// <summary>"2 app links: Stagewatch ✓ key drive TLS · PlayMap Stage left △ plain" — "" with none open or ended.</summary>
    public static string Line(IReadOnlyList<AppLinkRow> rows)
    {
        var open = rows.Where(r => !r.Ended).ToList();
        if (rows.Count == 0) return "";
        var head = open.Count == 1 ? "1 app link by LINK" : $"{open.Count.ToString(CultureInfo.InvariantCulture)} app links by LINK";
        var ended = rows.Count - open.Count;
        var tail = ended > 0 ? $" ({ended.ToString(CultureInfo.InvariantCulture)} ended in the last minute)" : "";
        if (open.Count == 0) return head + tail + " — none open";
        return head + tail + ": " + string.Join(" · ", open.Select(Short));
    }

    /// <summary>One row, short: its label, mark, what proved it, scope and carrier.</summary>
    public static string Short(AppLinkRow r)
        => $"{r.Label} {r.Mark} {(r.Verified == "code" ? "token" : "key " + r.Kid)} {r.Scope} {(r.Tls ? "TLS" : r.Local ? "plain, this machine" : "plain")}"
           + (r.ClearSeen ? " seen in clear" : "");

    /// <summary>
    /// The Run strip's word: "APP LINKS 2 ✓", "APP LINK △ PLAIN", "APP LINKS 3 △ (1 plain, 1 in clear)" — "" with none open. Ended links
    /// are grey and say nothing here: the Nodes page lists them.
    /// </summary>
    public static string Glance(IReadOnlyList<AppLinkRow> rows)
    {
        var open = rows.Where(r => !r.Ended).ToList();
        if (open.Count == 0) return "";
        var head = open.Count == 1 ? "APP LINK" : "APP LINKS " + open.Count.ToString(CultureInfo.InvariantCulture);
        var plain = open.Count(r => r.Plain);
        var clear = open.Count(r => r.ClearSeen);
        if (plain + clear == 0) return head + " ✓";
        var why = new List<string>(2);
        if (plain > 0) why.Add(open.Count == 1 ? "PLAIN" : plain.ToString(CultureInfo.InvariantCulture) + " plain");
        if (clear > 0) why.Add(open.Count == 1 ? "SEEN IN CLEAR" : clear.ToString(CultureInfo.InvariantCulture) + " in clear");
        return open.Count == 1 ? head + " △ " + string.Join(", ", why) : head + " △ (" + string.Join(", ", why) + ")";
    }

    /// <summary>The worst light among the open rows (grey with none open): what a tile and a row are lit by.</summary>
    public static CheckLight Worst(IReadOnlyList<AppLinkRow> rows)
    {
        var open = rows.Where(r => !r.Ended).ToList();
        if (open.Count == 0) return CheckLight.Grey;
        return open.Any(r => r.Light == CheckLight.Amber) ? CheckLight.Amber : CheckLight.Green;
    }

    /// <summary>A signature of the list for a reader's change test: a row added, ended, rescoped, marked, recording or moved moves it.</summary>
    public static string Signature(IReadOnlyList<AppLinkRow> rows)
        => string.Join("|", rows.Select(r => r.Lid + ":" + r.Scope + ":" + r.Mark + (r.Ended ? ":e" : "") + (r.Recording ? ":r" : "") + ":" + r.Where));

    /// <summary>What the operator's CLOSE says to the link as its last line: the operator's act, refused class so the connector holds.</summary>
    public static string ClosedWords(string desk) => $"{desk}'s operator closed this link — the key stays in force; link again after the hold";

    /// <summary>
    /// What the other desk shows after a CLOSE (docs/OPEN.md L115): the line carrier has no disconnect word, so the close rides the
    /// <c>refused</c> class, which a Stagewatch reads as its key refused and holds for <c>peerlink.hold.refused.ms</c> (a minute), not
    /// 13.15.3's 30 s disconnect hold.
    /// </summary>
    public const string ClosedSeenAs = "a Stagewatch shows its key refused and waits a minute before it links again — Show Bus 1.3's line has no word for an operator's close";

    /// <summary>
    /// The refusals past the list's bound, said beside the listed ones: "+3 more LINK refusals this run were counted and not listed" —
    /// "" with none.
    /// </summary>
    public static string Uncounted(long uncounted)
        => uncounted <= 0 ? "" : $"+{uncounted.ToString(CultureInfo.InvariantCulture)} more LINK refusal{(uncounted == 1 ? " this run was" : "s this run were")} counted and not listed (the list keeps {AcceptorList.MaxRefusals.ToString(CultureInfo.InvariantCulture)} a minute)";
}
