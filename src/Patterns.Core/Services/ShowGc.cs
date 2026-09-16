using System.Runtime;

namespace Patterns.Core.Services;

/// <summary>
/// The garbage collector while the outputs are live: sustained low latency, so a blocking
/// generation-2 collection — the one that stops every thread for tens of milliseconds while
/// the video frames, the NDI buffers and the snapshot clones churn — is avoided for the length
/// of the show and the collector works in the background instead. Off air the default
/// (interactive) mode comes back, so the desk between shows gives memory back as any app does.
/// The runtime keeps the request when it can (workstation concurrent GC, the app's own mode);
/// a runtime that cannot is left as it is.
/// </summary>
public static class ShowGc
{
    private static GCLatencyMode? _restingMode;
    private static bool _wasLive;

    /// <summary>The mode in force before the outputs first went live (null until then).</summary>
    public static GCLatencyMode? RestingMode => _restingMode;

    /// <summary>Round 69: times the large-object heap was asked to compact at the next full collection — once each time the outputs go off air, so a long day's fragments are given back between shows.</summary>
    public static int CompactionsRequested { get; private set; }

    /// <summary>Sustained low latency while <paramref name="live"/>, the resting mode otherwise. Never throws.</summary>
    public static void Apply(bool live)
    {
        try
        {
            if (live)
            {
                _restingMode ??= GCSettings.LatencyMode;
                if (GCSettings.LatencyMode != GCLatencyMode.SustainedLowLatency) GCSettings.LatencyMode = GCLatencyMode.SustainedLowLatency;
            }
            else
            {
                if (_restingMode is { } resting && GCSettings.LatencyMode != resting) GCSettings.LatencyMode = resting;
                if (_wasLive)
                {
                    // Round 69: off air, the large-object heap — the frames' and pictures' arrays, the snapshot clones —
                    // compacts at the runtime's next full collection, so what a show fragmented is given back before the next.
                    GCSettings.LargeObjectHeapCompactionMode = GCLargeObjectHeapCompactionMode.CompactOnce;
                    CompactionsRequested++;
                }
            }
            _wasLive = live;
        }
        catch
        {
            // A runtime that refuses the mode keeps its own; the show runs either way.
        }
    }

    /// <summary>The collector's facts (round 69): its mode, the collections by generation, the large-object and pinned heaps, the last collection's pause and the pause share.</summary>
    public static GcFacts Facts()
    {
        try
        {
            var info = GC.GetGCMemoryInfo();
            double loh = 0, poh = 0;
            var gens = info.GenerationInfo;
            if (gens.Length > 3) loh = gens[3].SizeAfterBytes / (1024.0 * 1024.0);
            if (gens.Length > 4) poh = gens[4].SizeAfterBytes / (1024.0 * 1024.0);
            double pause = 0;
            foreach (var p in info.PauseDurations) pause += p.TotalMilliseconds;
            return new GcFacts(Describe(), GC.CollectionCount(0), GC.CollectionCount(1), GC.CollectionCount(2), Math.Round(loh, 1), Math.Round(poh, 1), Math.Round(pause, 1), Math.Round(info.PauseTimePercentage, 2), CompactionsRequested);
        }
        catch
        {
            return new GcFacts(Describe(), 0, 0, 0, 0, 0, 0, 0, CompactionsRequested);
        }
    }

    /// <summary>"Sustained low latency (outputs live)" / "Interactive (off air)" — the Machine page's line.</summary>
    public static string Describe()
        => GCSettings.LatencyMode switch
        {
            GCLatencyMode.SustainedLowLatency => "sustained low latency (outputs live)",
            GCLatencyMode.LowLatency => "low latency",
            GCLatencyMode.Batch => "batch",
            _ => "interactive (off air)",
        };
}

/// <summary>The collector's facts at one moment (round 69): "gen 2 × 3 · LOH 84 MB · last pause 4.2 ms · 0.3 % paused".</summary>
public sealed record GcFacts(string Mode, int Gen0, int Gen1, int Gen2, double LohMB, double PohMB, double LastPauseMs, double PausePct, int Compactions)
{
    public string Words => $"collector {Mode} · gen 0/1/2 × {Gen0}/{Gen1}/{Gen2} · large objects {MemoryBudget.Mb(LohMB)}{(PohMB > 0 ? $" · pinned {MemoryBudget.Mb(PohMB)}" : "")} · last pause {LastPauseMs:0.0} ms · {PausePct:0.0} % paused{(Compactions > 0 ? $" · LOH compaction asked {Compactions}×" : "")}";
}
