using System.Collections.Concurrent;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Round 88 (Show Bus 1.2 §11): what this desk has sent to a linked approval desk (a Stagewatch with the "approvals" cap)
/// and what it is waiting to hear. The assistant's proposals stay here — the desk only ever sees their words — and come
/// back approved by their local id; this desk then applies them through its own action layer, fences and all, and says
/// what happened. Thread-safe: the page adds on the UI thread, the link reads and updates from its task.
/// </summary>
public sealed class DeskApprovals
{
    public sealed class Entry
    {
        public required string LocalId { get; init; }
        public required string Line { get; init; }
        public required string Reason { get; init; }
        public required string Risk { get; init; }
        /// <summary>Applies the proposal on the UI thread: (ok, words).</summary>
        public required Func<(bool Ok, string Words)> Apply { get; init; }
        /// <summary>Told (on the UI thread) each time the proposal's state moves: the chip's line.</summary>
        public required Action<string> Said { get; init; }
        public string DeskId { get; set; } = "";
        public string State { get; set; } = "";
        public bool Ran { get; set; }

        /// <summary>
        /// The link's monotonic ms at a post the approval desk's answer said nothing of — no answer, a 5xx, a 408 (13.12.3) — or
        /// <see cref="long.MinValue"/>: while set the proposal is not posted again blind (13.12.7); a read of this desk's proposals
        /// settles it.
        /// </summary>
        public long UnconfirmedAt { get; set; } = long.MinValue;

        /// <summary>
        /// Show Bus 1.3 §12.4: a line for the Stagewatch's own desk (a window asked for this desk), posted to its desk rather than to
        /// this one; it never comes back here to apply — the Stagewatch runs it once a person approves it there.
        /// </summary>
        public bool ToDesk { get; init; }
    }

    private readonly ConcurrentDictionary<string, Entry> _byLocal = new(StringComparer.Ordinal);

    /// <summary>Sent to the desk, waiting to go out: the link posts these.</summary>
    public IReadOnlyList<Entry> Unsent => _byLocal.Values.Where(e => e.DeskId.Length == 0).ToList();

    public IReadOnlyList<Entry> All => _byLocal.Values.ToList();

    public Entry? ByLocal(string localId) => _byLocal.TryGetValue(localId, out var e) ? e : null;

    public Entry? ByDesk(string deskId) => _byLocal.Values.FirstOrDefault(e => e.DeskId == deskId);

    /// <summary>
    /// An approved APPLY naming <paramref name="local"/>: the entry it names and why it may not run, or a null refusal when
    /// it may (<see cref="DeskProposals.ApplyRefusal"/>). Read against that entry alone — its own approval-desk id and
    /// whether it ran — never against another entry that shares the id (an approval desk's ids can restart with its run).
    /// </summary>
    public (Entry? Entry, string? Refusal) ForApply(DeskProposal p, string local, string selfId)
    {
        var e = ByLocal(local);
        return (e, DeskProposals.ApplyRefusal(p, local, selfId, e is not null, e?.DeskId ?? "", e?.Ran ?? false));
    }

    /// <summary>
    /// An ask for this very line is still open on the Stagewatch's desk (being sent, or pending there): a press again asks nothing
    /// new — one ask per window waits for its person.
    /// </summary>
    public bool AskOpen(string line)
        => _byLocal.Values.Any(e => e.ToDesk && string.Equals(e.Line, line, StringComparison.Ordinal) && e.State is "" or "sending" or "pending");

    /// <summary>A line for the Stagewatch's own desk (<see cref="Entry.ToDesk"/>): a window asked for this desk. Returns its local id.</summary>
    public string AddAsk(string line, string reason, Action<string> said)
    {
        var localId = "w" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        _byLocal[localId] = new Entry
        {
            LocalId = localId, Line = line, Reason = reason, Risk = "high", ToDesk = true, Said = said, State = "sending",
            Apply = static () => (false, "a window grant is the Stagewatch's to run, once a person there approves it"),
        };
        Trim();
        return localId;
    }

    /// <summary>A proposal for the desk; returns its local id.</summary>
    public string Add(string kindLabel, string title, string reason, string risk, Func<(bool, string)> apply, Action<string> said)
    {
        var localId = "a" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        _byLocal[localId] = new Entry
        {
            LocalId = localId, Line = DeskProposals.Line(localId, kindLabel, title), Reason = reason, Risk = risk, Apply = apply, Said = said, State = "sending",
        };
        Trim();
        return localId;
    }

    /// <summary>At most 200 entries are held: past that, the settled ones (ran, rejected, undelivered) go first.</summary>
    private void Trim()
    {
        if (_byLocal.Count > 200) foreach (var old in _byLocal.Values.Where(e => e.Ran || e.State is "rejected" or "undelivered").Take(_byLocal.Count - 200).ToList()) _byLocal.TryRemove(old.LocalId, out _);
    }
}
