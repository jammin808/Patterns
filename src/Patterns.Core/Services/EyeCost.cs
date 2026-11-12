namespace Patterns.Core.Services;

/// <summary>One rebuild of the Eye's picture: when, how long the graph and its layout took, and how big it came out.</summary>
public readonly record struct EyeBuild(DateTime Utc, double Ms, int Nodes, int Edges);

/// <summary>
/// What the Eye's rebuilds cost, measured where they run (the desk's thread) and kept for the metrics file — a measurement, not a
/// governor: nothing here limits a rebuild (the Eye design's D9: measured, then governed by a later round from this record). The
/// newest <see cref="Kept"/> rebuilds are held; a rebuild happens at most once a facts tick, so that is minutes of record.
/// One writer (the desk's thread); a reader on another thread takes a copy under the lock.
/// </summary>
public sealed class EyeCost
{
    /// <summary>The rebuilds held — a bound on memory, far past a minute's worth at one a second.</summary>
    public const int Kept = 256;

    /// <summary>The span the metrics read: the worst rebuild and the count of the last minute.</summary>
    public static readonly TimeSpan Window = TimeSpan.FromMinutes(1);

    private readonly object _gate = new();
    private readonly Queue<EyeBuild> _builds = new();
    private EyeBuild? _last;
    private long _total;

    /// <summary>Rebuilds since start.</summary>
    public long Total
    {
        get
        {
            lock (_gate) return _total;
        }
    }

    /// <summary>The newest rebuild, or null before the first.</summary>
    public EyeBuild? Last
    {
        get
        {
            lock (_gate) return _last;
        }
    }

    public void Record(EyeBuild build)
    {
        lock (_gate)
        {
            _builds.Enqueue(build);
            while (_builds.Count > Kept) _builds.Dequeue();
            _last = build;
            _total++;
        }
    }

    /// <summary>
    /// The window ending at <paramref name="utcNow"/>: how many rebuilds ran in it, the slowest of them (ms; −1 with none) and the
    /// size of the newest rebuild ever (its nodes; −1 before the first) — the picture the desk holds now, whether or not it moved.
    /// </summary>
    public (int Builds, double WorstMs, int Nodes) Read(DateTime utcNow)
    {
        lock (_gate)
        {
            var from = utcNow - Window;
            var builds = 0;
            var worst = -1.0;
            foreach (var b in _builds)
            {
                if (b.Utc <= from || b.Utc > utcNow) continue;
                builds++;
                worst = Math.Max(worst, b.Ms);
            }
            return (builds, worst, _last?.Nodes ?? -1);
        }
    }
}
