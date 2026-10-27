namespace Patterns.Core.Services;

/// <summary>
/// Show Bus 1.3 §13.12.9, the reports a connector owes — a §11 result this desk owes an approval desk. Each is recorded before the
/// act it reports (written ahead: owed before the proposal runs, so a run that faults or a link that ends as it runs still leaves a
/// result owed, said as one that may have run), and retried by its sender from <see cref="RetryFirst"/>, doubling to
/// <see cref="RetryMost"/>, ±20 %, for <see cref="RetryFor"/>; a receiver answers a second one 409 "settled", which counts as
/// delivered. Bounded at <see cref="Most"/>: past it the one owed longest is let go and named, so the caller says it. Pure but
/// for its lock: the time is the caller's monotonic ms and the jitter its j.
/// </summary>
public sealed class OwedReports
{
    /// <summary>The first retry's wait (§14 <c>report.retry.first.ms</c>).</summary>
    public static readonly TimeSpan RetryFirst = TimeSpan.FromSeconds(3);

    /// <summary>The longest retry's wait (§14 <c>report.retry.max.ms</c>).</summary>
    public static readonly TimeSpan RetryMost = TimeSpan.FromSeconds(30);

    /// <summary>How long a report is retried before it is let go (§14 <c>report.retry.for.ms</c>).</summary>
    public static readonly TimeSpan RetryFor = TimeSpan.FromMinutes(30);

    /// <summary>The most reports owed at once.</summary>
    public const int Most = 100;

    private sealed class Owed(string to, string id, string body, long firstMs)
    {
        public string To { get; } = to;
        public string Id { get; } = id;
        public string Body { get; set; } = body;
        public long FirstMs { get; } = firstMs;
        public long NextMs { get; set; } = firstMs;
        public int Tries { get; set; }
    }

    private readonly object _gate = new();
    private readonly List<Owed> _owed = new();

    /// <summary>How many are owed now.</summary>
    public int Count
    {
        get
        {
            lock (_gate) return _owed.Count;
        }
    }

    /// <summary>
    /// 13.12.9's wait before retry <paramref name="tries"/> (from 0): <see cref="RetryFirst"/> doubled each try, at most
    /// <see cref="RetryMost"/>, times 0.8 + 0.4 j.
    /// </summary>
    public static TimeSpan Wait(int tries, double j)
    {
        var baseMs = Math.Min(RetryMost.TotalMilliseconds, RetryFirst.TotalMilliseconds * Math.Pow(2, Math.Min(tries, 16)));
        return TimeSpan.FromMilliseconds(Math.Round(baseMs * (0.8 + 0.4 * Math.Clamp(j, 0, 1))));
    }

    /// <summary>
    /// Owes <paramref name="id"/>'s report to <paramref name="to"/> from <paramref name="nowMs"/>, due at once; one already owed
    /// keeps its schedule and takes the new body. Returns the id let go to stay within <see cref="Most"/>, or null.
    /// </summary>
    public string? Owe(string to, string id, string body, long nowMs)
    {
        lock (_gate)
        {
            if (_owed.Find(o => o.To == to && o.Id == id) is { } held)
            {
                held.Body = body;
                return null;
            }
            _owed.Add(new Owed(to, id, body, nowMs));
            if (_owed.Count <= Most) return null;
            var oldest = _owed[0];
            _owed.RemoveAt(0);
            return oldest.Id;
        }
    }

    /// <summary>The reports owed to <paramref name="to"/> whose next try is due at <paramref name="nowMs"/>, oldest first.</summary>
    public IReadOnlyList<(string Id, string Body)> Due(string to, long nowMs)
    {
        lock (_gate) return _owed.Where(o => o.To == to && o.NextMs <= nowMs).Select(o => (o.Id, o.Body)).ToList();
    }

    /// <summary>A try of <paramref name="id"/> left without settling it: its next waits 13.12.9's wait.</summary>
    public void Tried(string to, string id, long nowMs, double j)
    {
        lock (_gate)
        {
            if (_owed.Find(o => o.To == to && o.Id == id) is not { } o) return;
            o.NextMs = nowMs + (long)Wait(o.Tries, j).TotalMilliseconds;
            o.Tries++;
        }
    }

    /// <summary>The report was delivered (a 2xx, or 409 settled) or refused for good: it is owed no longer.</summary>
    public void Settle(string to, string id)
    {
        lock (_gate) _owed.RemoveAll(o => o.To == to && o.Id == id);
    }

    /// <summary>The ids owed past <see cref="RetryFor"/> at <paramref name="nowMs"/>, let go now so the caller says each.</summary>
    public IReadOnlyList<string> Expired(long nowMs)
    {
        lock (_gate)
        {
            var gone = _owed.Where(o => nowMs - o.FirstMs >= RetryFor.TotalMilliseconds).ToList();
            foreach (var o in gone) _owed.Remove(o);
            return gone.Select(o => o.Id).ToList();
        }
    }

    /// <summary>The tries made of <paramref name="id"/> so far; -1 when it is not owed.</summary>
    public int TriesOf(string to, string id)
    {
        lock (_gate) return _owed.Find(o => o.To == to && o.Id == id)?.Tries ?? -1;
    }
}

/// <summary>
/// The ids of approved proposals this desk has run, so none runs twice: the last <see cref="Kept"/>, the oldest let go first —
/// bounded, and never cleared whole, so an id run a moment ago is never run again because the set filled.
/// </summary>
public sealed class RecentIds
{
    /// <summary>The ids kept: far more than an approval desk keeps for one desk at once.</summary>
    public const int Kept = 1024;

    private readonly HashSet<string> _ids = new(StringComparer.Ordinal);
    private readonly Queue<string> _order = new();
    private readonly object _gate = new();

    /// <summary>True the first time <paramref name="id"/> is seen; false for an id already held.</summary>
    public bool Add(string id)
    {
        lock (_gate)
        {
            if (!_ids.Add(id)) return false;
            _order.Enqueue(id);
            while (_order.Count > Kept) _ids.Remove(_order.Dequeue());
            return true;
        }
    }

    public int Count
    {
        get
        {
            lock (_gate) return _ids.Count;
        }
    }
}
