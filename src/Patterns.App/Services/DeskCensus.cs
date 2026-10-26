using Patterns.Core.Services;
using Patterns.Rendering.Media;

namespace Patterns.App.Services;

/// <summary>
/// What was there at one moment, for a census that judges only what came after it: the desks
/// alive, the desk timers running and the pictures held, each named by the identity it was given
/// as it registered (<see cref="DeskCensus.Mark"/>). It holds numbers only, never the objects, so
/// a mark keeps nothing alive.
/// </summary>
public sealed record CensusMark(IReadOnlySet<long> Desks, IReadOnlySet<long> Timers, IReadOnlySet<long> Pictures);

/// <summary>
/// Takes the lifetime census on this process (round 64): every registry a closed desk, node or
/// surface should have left. Read after a full collection it is a proof; read live it is the
/// support ticket's and STATE's line.
/// </summary>
public static class DeskCensus
{
    /// <summary>The desks alive, the desk timers running and the pictures held right now, by identity.</summary>
    public static CensusMark Mark() => new(AppServices.AliveIds(), DeskTimers.RunningIds(), ImageCache.HeldIds());

    /// <summary>
    /// The census. With no mark every row counts the whole process — what STATE, the support
    /// ticket and the memlog read. With a mark, the "desks", "timers" and "pictures" rows count
    /// only what is alive, running or held now that was not at the mark: what was there then
    /// belongs to whoever made it before, and its leaving is no change of what came after; what
    /// came after is counted whenever its object was made — a timer made before the mark and
    /// started again since is running now and was not then. The other rows count the whole
    /// process either way.
    /// </summary>
    public static LifetimeCensus Take(AppServices? services = null, CensusMark? since = null)
    {
        var video = services?.Video;
        return new LifetimeCensus(new (string, long)[]
        {
            ("desks", AppServices.AliveNotIn(since?.Desks)),                // desks a collection could not reclaim
            ("nodes", NodeHost.Live),
            ("timers", DeskTimers.RunningNotIn(since?.Timers)),            // dispatcher timers alive and enabled: a closed desk's must all have stopped
            ("pipelines", RenderFence.Seats),                               // one seat per pipeline alive
            ("openFrames", RenderFence.OpenFrames),
            ("hungFrames", RenderFence.HungSinks),
            ("frameBudgets", FrameBudgets.Attached.Count),
            ("framePools", FramePools.Count),
            ("retiringPools", FramePools.PendingFree),
            ("retiredFrames", RetiredFrames.Count),
            ("pictures", ImageCache.CountNotIn(since?.Pictures)),
            ("ledgerOwners", MemoryLedger.Count),
            ("inputMounts", InputBus.Keys.Count),
            ("videoSources", video?.MountCount ?? 0),
            ("videoRetiring", video?.RetiredCount ?? 0),
        });
    }
}
