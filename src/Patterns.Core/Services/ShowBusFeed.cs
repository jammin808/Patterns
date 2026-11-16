namespace Patterns.Core.Services;

/// <summary>
/// What this desk holds of the show network, handed to the one composition every reader takes (<see cref="ShowBusView.Compose"/>):
/// the hello table as it stands (bounded by <see cref="ShowBusPeers.MaxPeers"/>), the Stagewatch link's sight with its record, context
/// and revision, the Show Bus nodes driven as devices, the acceptor's list (Show Bus 1.3 §13.16.5), the contexts other apps posted over
/// a verified PeerLink (§6, CONTEXT POST: a source of their own, told) and this desk's own nodes' contexts over the twin. Nothing here
/// is a key, a proof or a session credential.
/// </summary>
public sealed record ShowBusFeed(IReadOnlyList<ShowBusPeer> Peers, StagewatchSight Sight)
{
    public IReadOnlyList<ShowBusNodeSight> Devices { get; init; } = Array.Empty<ShowBusNodeSight>();

    public IReadOnlyList<AppLinkRow> Accepted { get; init; } = Array.Empty<AppLinkRow>();

    /// <summary>The contexts other apps posted over a verified PeerLink and this desk keeps now (<see cref="PostedContexts"/>), each by the id its key proved.</summary>
    public IReadOnlyList<PostedContext> Posted { get; init; } = Array.Empty<PostedContext>();

    public IReadOnlyList<(string Instance, string Name, KeptContext Kept, DateTime AtUtc)> Twin { get; init; }
        = Array.Empty<(string Instance, string Name, KeptContext Kept, DateTime AtUtc)>();
}

/// <summary>
/// The apps' revision (the Eye design's D9, carried from each context to the apps as a whole): it moves when the composition's steady
/// projection moves — what every app's hash fields say (<see cref="EyeShowApp"/>: each context by its revision, each record by its
/// face, never the counts and ages that run on) — and only then, so a reader that keys on it (the Eye's hash) does nothing on a tick
/// that moved nothing. The composition itself is handed back as it came, so a reader of the live record still reads it live.
/// Thread-safe.
/// </summary>
public sealed class ShowBusAppsRevision
{
    private readonly object _gate = new();
    private string _face = "";
    private long _rev;

    /// <summary>The apps' revision now: 1 from the first composition, one more for each that moved what the apps say.</summary>
    public long Rev
    {
        get
        {
            lock (_gate) return _rev;
        }
    }

    /// <summary>The composition kept, and the revision it stands at — one more than before only when its steady projection moved.</summary>
    public (IReadOnlyList<EyeShowApp> Apps, long Rev) Take(IReadOnlyList<EyeShowApp> composed)
    {
        var face = Face(composed);
        lock (_gate)
        {
            if (_rev == 0 || !string.Equals(face, _face, StringComparison.Ordinal))
            {
                _face = face;
                _rev++;
            }
            return (composed, _rev);
        }
    }

    /// <summary>The apps' steady projection: what the Eye's hash would read of them, contexts by revision, records by face.</summary>
    public static string Face(IReadOnlyList<EyeShowApp> apps) => JsonUtil.SerializeCompact(apps);
}
