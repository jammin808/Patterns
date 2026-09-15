using Patterns.Core.Services;
using Patterns.Rendering.Media;

namespace Patterns.App.Services;

/// <summary>
/// Takes the lifetime census on this process (round 64): every registry a closed desk, node or
/// surface should have left. Read after a full collection it is a proof; read live it is the
/// support ticket's and STATE's line.
/// </summary>
public static class DeskCensus
{
    public static LifetimeCensus Take(AppServices? services = null)
    {
        var video = services?.Video;
        return new LifetimeCensus(new (string, long)[]
        {
            ("desks", AppServices.Alive),                                   // desks a collection could not reclaim
            ("nodes", NodeHost.Live),
            ("timers", DeskTimers.Running),                                 // dispatcher timers alive and enabled: a closed desk's must all have stopped
            ("pipelines", RenderFence.Seats),                               // one seat per pipeline alive
            ("openFrames", RenderFence.OpenFrames),
            ("hungFrames", RenderFence.HungSinks),
            ("frameBudgets", FrameBudgets.Attached.Count),
            ("framePools", FramePools.Count),
            ("retiringPools", FramePools.PendingFree),
            ("retiredFrames", RetiredFrames.Count),
            ("pictures", ImageCache.Count),
            ("ledgerOwners", MemoryLedger.Count),
            ("inputMounts", InputBus.Keys.Count),
            ("videoSources", video?.MountCount ?? 0),
            ("videoRetiring", video?.RetiredCount ?? 0),
        });
    }
}
