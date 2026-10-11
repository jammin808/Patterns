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
    }

    private readonly ConcurrentDictionary<string, Entry> _byLocal = new(StringComparer.Ordinal);

    /// <summary>Sent to the desk, waiting to go out: the link posts these.</summary>
    public IReadOnlyList<Entry> Unsent => _byLocal.Values.Where(e => e.DeskId.Length == 0).ToList();

    public IReadOnlyList<Entry> All => _byLocal.Values.ToList();

    public Entry? ByLocal(string localId) => _byLocal.TryGetValue(localId, out var e) ? e : null;

    public Entry? ByDesk(string deskId) => _byLocal.Values.FirstOrDefault(e => e.DeskId == deskId);

    /// <summary>A proposal for the desk; returns its local id.</summary>
    public string Add(string kindLabel, string title, string reason, string risk, Func<(bool, string)> apply, Action<string> said)
    {
        var localId = "a" + Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(4)).ToLowerInvariant();
        _byLocal[localId] = new Entry
        {
            LocalId = localId, Line = DeskProposals.Line(localId, kindLabel, title), Reason = reason, Risk = risk, Apply = apply, Said = said, State = "sending",
        };
        if (_byLocal.Count > 200) foreach (var old in _byLocal.Values.Where(e => e.Ran || e.State is "rejected" or "undelivered").Take(_byLocal.Count - 200).ToList()) _byLocal.TryRemove(old.LocalId, out _);
        return localId;
    }
}
