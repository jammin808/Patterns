namespace Patterns.Core.Services;

/// <summary>
/// Show Bus 1.3 §2, 13.4.3: which Stagewatch a typed address names on the show network — the one rule the link's dial (its
/// Resolve) and its evaluator's look at a live connection both read, so the two can never disagree. A typed address is named by
/// a node only through that node's own datagrams: a hello heard live from that address (the datagram's source, never what the
/// hello says of its own host) on the typed port. Pure: the hello table, now and the pins are handed in.
/// </summary>
public static class StagewatchAt
{
    /// <summary>The Stagewatches the link may dial: heard live now, at an address, with a web door.</summary>
    public static IReadOnlyList<ShowBusPeer> Dialable(IEnumerable<ShowBusPeer> peers, DateTime utcNow)
        => peers.Where(p => p.Kind == "main" && p.StateAt(utcNow) == ShowBusState.Live && p.Address.Length > 0 && (p.Ports.Ws > 0 || p.Ports.Http > 0)).ToList();

    /// <summary>Whether <paramref name="p"/>'s hellos came from <paramref name="host"/> and name <paramref name="port"/> among its web doors.</summary>
    public static bool Speaks(ShowBusPeer p, string host, int port)
        => p.HeardAt(host) && (p.Ports.Http == port || p.Ports.Ws == port || p.Ports.Https == port);

    /// <summary>
    /// The node a typed <paramref name="host"/>:<paramref name="port"/> names: the one live Stagewatch heard there, or of several the
    /// one whose id this desk pinned (<paramref name="pinnedId"/>); else null, and the address stays its own record. A node whose id two
    /// running nodes say is no target: null, with <paramref name="conflict"/> saying which — no new link goes to that id while the mark
    /// stands.
    /// </summary>
    public static ShowBusPeer? Named(IReadOnlyList<ShowBusPeer> dialable, string host, int port, Func<string, bool> pinnedId, out ShowBusConflict? conflict)
    {
        conflict = null;
        var heard = dialable.Where(p => Speaks(p, host, port)).ToList();
        var named = heard.Count == 1 ? heard[0] : heard.Where(p => pinnedId(p.Id)).ToList() is { Count: 1 } pinned ? pinned[0] : null;
        if (named?.Conflict is { } held)
        {
            conflict = held;
            return null;
        }
        return named;
    }

    /// <summary>
    /// The evaluator's look again at a live connection: the node to dial again as, or null. Only a connection whose target is the
    /// typed address itself (<paramref name="targetKey"/> is the address's own <c>addr:</c> binding, <paramref name="typedKey"/>) is
    /// looked at — a found, named-by-id or hello-named target never is — and once (<paramref name="already"/>: this connection was
    /// looked at and ended for it before); then only to the node <see cref="Named"/> names now, never one in conflict. A hello that
    /// names nothing there, or names two unpinned nodes, moves nothing.
    /// </summary>
    public static ShowBusPeer? Retarget(string targetKey, string typedKey, bool already, IReadOnlyList<ShowBusPeer> dialable, string host, int port, Func<string, bool> pinnedId)
    {
        if (already || typedKey.Length == 0 || !string.Equals(targetKey, typedKey, StringComparison.Ordinal)) return null;
        return Named(dialable, host, port, pinnedId, out _);
    }

    /// <summary>
    /// Whether the node's own dial would carry this desk's key there and prove it with LINK to the id meant, where this connection does
    /// not (13.4.3, 13.6.5): a key is set (<paramref name="hasKey"/>), it goes to the node (<paramref name="keyFor"/> binds it there, to the
    /// typed address, or nowhere yet), the acceptor at the address was not found older than LINK (<paramref name="level0"/>), and the
    /// dial reaches it over TLS or on this machine (<paramref name="tlsOrLocal"/>). Only then does the look move the link: a keyless or
    /// public-only link stays the address's own record until a verified link names the acceptor's id, so nothing a hello claims names
    /// the address's public view; and a connection that already carries the key there (<paramref name="keyedHere"/>) is never ended for it.
    /// </summary>
    public static bool ProvesThere(bool keyedHere, bool hasKey, string keyFor, string nodeKey, string typedKey, bool level0, bool tlsOrLocal)
        => !keyedHere && hasKey && !level0 && tlsOrLocal && StagewatchKeyFor.Goes(keyFor, nodeKey, typedKey);

    /// <summary>
    /// What the node's own dial would be refused on, known from the live connection without dialling anything — so the link keeps the
    /// address's public view, the room's time on the wall, rather than end it for a dial that is refused: a dial of that node this run
    /// already made from this address and that was refused (<paramref name="refused"/>, the core's word: another id, a floor, a changed
    /// certificate; "" when none), this desk's key held for that node (<paramref name="heldMs"/> more; the key is presented to it only
    /// when the hold ends, so nothing is gained before), or a certificate a person or a proof pinned for that node
    /// (<paramref name="nodePin"/>, sha256:…) that this connection's own TLS did not show (<paramref name="shown"/>, "" over plain):
    /// CERTIFICATE CHANGED, which only a person's ACCEPT lifts. Null when none stands, and the link dials again as the node.
    /// </summary>
    public static string? Stays(string nodePin, string shown, long heldMs, string refused = "")
    {
        // Said without the seconds left: the words are the record's and the journal's, which move only when why moves.
        if (refused.Length > 0) return $"its own dial from here was refused ({refused}) — this address's public view is read until another Stagewatch process is heard here";
        if (heldMs > 0) return "this desk's key is held for it — the link dials it as that Stagewatch once the hold ends, or at LINK AGAIN";
        if (!TlsFingerprint.IsValid(nodePin) || string.Equals(nodePin, shown, StringComparison.Ordinal)) return null;
        return TlsFingerprint.IsValid(shown)
            ? $"it shows {TlsFingerprint.Short(shown)}, not {TlsFingerprint.Short(nodePin)} pinned for it — nothing of this desk's goes to it; compare it on its screen and ACCEPT it on the Nodes page"
            : $"its certificate {TlsFingerprint.Short(nodePin)} is pinned and this address answers without TLS — nothing of this desk's goes to it (another machine at its address?)";
    }
}
