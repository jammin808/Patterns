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

    /// <summary>The mode in force before the outputs first went live (null until then).</summary>
    public static GCLatencyMode? RestingMode => _restingMode;

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
            else if (_restingMode is { } resting && GCSettings.LatencyMode != resting)
            {
                GCSettings.LatencyMode = resting;
            }
        }
        catch
        {
            // A runtime that refuses the mode keeps its own; the show runs either way.
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
