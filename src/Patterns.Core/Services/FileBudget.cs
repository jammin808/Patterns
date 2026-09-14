namespace Patterns.Core.Services;

/// <summary>
/// What the show's files cost, phase by phase: the autosave (the frozen show taken on the desk's
/// thread in no time, then serialised and written on a worker), the recovery record (the same
/// way), a show loaded (read and parsed on a worker, applied once on the desk), a show saved by
/// hand, a cue sheet imported (read and parsed on a worker). Each phase keeps its count, its
/// last and its worst; the saves a newer save made unnecessary are counted as coalesced. The
/// workers record from their threads — one short lock — and the Machine page reads the words.
/// </summary>
public sealed class FileBudget
{
    public const string SaveSnapshot = "autosave snapshot";
    public const string SaveSerialise = "autosave serialise";
    public const string SaveWrite = "autosave write";
    public const string RecoverySerialise = "recovery serialise";
    public const string RecoveryWrite = "recovery write";
    public const string ShowLoadParse = "show load parse";
    public const string ShowSaveSerialise = "show save serialise";
    public const string ShowSaveWrite = "show save write";
    public const string CueSheetParse = "cue sheet parse";

    /// <summary>A phase on the desk's thread past this is a hitch the operator can feel: what the whole design keeps under.</summary>
    public const double SlowMs = TickBudget.SlowMs;

    /// <summary>One phase's tally.</summary>
    public sealed record Phase(string Name, long Count, double LastMs, double WorstMs, double TotalMs)
    {
        /// <summary>"autosave serialise 12, worst 4.1 ms".</summary>
        public string Words => $"{Name} {Count}, worst {WorstMs:0.0} ms";
    }

    private sealed class Entry
    {
        public long Count;
        public double LastMs = -1;
        public double WorstMs = -1;
        public double TotalMs;
    }

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly List<string> _order = new();
    private long _coalesced;
    private long _onDeskThread;

    /// <summary>Saves and records a newer one made unnecessary before they ran: skipped, never written.</summary>
    public long Coalesced
    {
        get { lock (_gate) return _coalesced; }
    }

    /// <summary>Phases that ran on the desk's thread past the slow line — the hitches this budget exists to make zero.</summary>
    public long SlowOnDeskThread
    {
        get { lock (_gate) return _onDeskThread; }
    }

    /// <summary>A phase ran and took this long; <paramref name="onDeskThread"/> says whether it held the desk while it did.</summary>
    public void Record(string phase, double ms, bool onDeskThread = false)
    {
        if (ms < 0) ms = 0;
        lock (_gate)
        {
            if (!_entries.TryGetValue(phase, out var e))
            {
                e = new Entry();
                _entries[phase] = e;
                _order.Add(phase);
            }
            e.Count++;
            e.LastMs = ms;
            e.TotalMs += ms;
            if (ms > e.WorstMs) e.WorstMs = ms;
            if (onDeskThread && ms > SlowMs) _onDeskThread++;
        }
    }

    /// <summary>A save a newer save overtook before it ran: nothing serialised, nothing written.</summary>
    public void CoalescedOne()
    {
        lock (_gate) _coalesced++;
    }

    public IReadOnlyList<Phase> Phases()
    {
        lock (_gate)
        {
            var list = new List<Phase>(_order.Count);
            foreach (var name in _order)
            {
                var e = _entries[name];
                list.Add(new Phase(name, e.Count, e.LastMs, e.WorstMs, e.TotalMs));
            }
            return list;
        }
    }

    public Phase? Of(string phase)
    {
        lock (_gate)
        {
            return _entries.TryGetValue(phase, out var e) ? new Phase(phase, e.Count, e.LastMs, e.WorstMs, e.TotalMs) : null;
        }
    }

    /// <summary>
    /// The STABILITY line: "Files: autosave 12 (2 coalesced) — snapshot 0.0 ms, serialise 4.1 ms,
    /// write 2.3 ms worst · recovery 3 — serialise 6.0 ms, write 1.1 ms worst · show load parse 18
    /// ms · cue sheet parse 3 ms · nothing on the desk's thread past 16 ms".
    /// </summary>
    public string Describe()
    {
        var phases = Phases();
        if (phases.Count == 0) return "Files: nothing saved, loaded or imported yet.";
        var parts = new List<string>();
        var save = Of(SaveSerialise);
        if (save is not null)
        {
            var coalesced = Coalesced;
            parts.Add($"Files: autosave {save.Count}{(coalesced > 0 ? $" ({coalesced} coalesced)" : "")} — snapshot {Worst(SaveSnapshot)}, serialise {Worst(SaveSerialise)}, write {Worst(SaveWrite)} worst");
        }
        else
        {
            parts.Add("Files:");
        }
        if (Of(RecoverySerialise) is { } recovery) parts.Add($"recovery {recovery.Count} — serialise {Worst(RecoverySerialise)}, write {Worst(RecoveryWrite)} worst");
        if (Of(ShowLoadParse) is { } load) parts.Add($"show load parse {load.WorstMs:0} ms");
        if (Of(ShowSaveSerialise) is { } saved) parts.Add($"show save {saved.Count} — serialise {Worst(ShowSaveSerialise)}, write {Worst(ShowSaveWrite)} worst");
        if (Of(CueSheetParse) is { } sheet) parts.Add($"cue sheet parse {sheet.WorstMs:0} ms");
        var slow = SlowOnDeskThread;
        parts.Add(slow == 0 ? $"nothing on the desk's thread past {SlowMs:0} ms" : $"{slow} on the desk's thread past {SlowMs:0} ms");
        var line = string.Join(" · ", parts);
        return line.StartsWith("Files: ·", StringComparison.Ordinal) ? "Files: " + line["Files: · ".Length..] : line;
    }

    private string Worst(string phase) => Of(phase) is { } p ? $"{p.WorstMs:0.0} ms" : "—";

    public void Reset()
    {
        lock (_gate)
        {
            _entries.Clear();
            _order.Clear();
            _coalesced = 0;
            _onDeskThread = 0;
        }
    }
}
