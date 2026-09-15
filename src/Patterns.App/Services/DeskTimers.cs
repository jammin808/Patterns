using Avalonia.Threading;

namespace Patterns.App.Services;

/// <summary>
/// Every dispatcher timer the desk makes goes through here (round 64). A running
/// <see cref="DispatcherTimer"/> is rooted by the dispatcher itself — its tick handler, its owner,
/// the whole desk behind it — so a timer a closed desk forgot to stop keeps that desk alive for
/// the life of the process: the lifetime census found the view model's one-second status timer
/// doing exactly that. The timers are held weakly; <see cref="Running"/> counts the ones alive
/// and enabled, the census's "timers" row, which must read what it read after the first closed
/// boot.
/// </summary>
public static class DeskTimers
{
    private sealed record Entry(WeakReference<DispatcherTimer> Timer, string Owner);

    private static readonly List<Entry> All = new();

    /// <summary>A timer at an interval, on the dispatcher's normal priority unless another is asked; started by its owner. The owner is the calling file and member, for the census's words.</summary>
    public static DispatcherTimer Make(TimeSpan interval, DispatcherPriority? priority = null,
                                       [System.Runtime.CompilerServices.CallerFilePath] string file = "",
                                       [System.Runtime.CompilerServices.CallerMemberName] string member = "")
    {
        var timer = priority is { } p ? new DispatcherTimer(p) { Interval = interval } : new DispatcherTimer { Interval = interval };
        var owner = $"{Path.GetFileNameWithoutExtension(file)}.{member}";
        lock (All)
        {
            All.RemoveAll(e => !e.Timer.TryGetTarget(out _));
            All.Add(new Entry(new WeakReference<DispatcherTimer>(timer), owner));
        }
        return timer;
    }

    /// <summary>The owners of the timers running right now, for the census's words: "MainViewModel..ctor (1 s)".</summary>
    public static IReadOnlyList<string> RunningOwners
    {
        get
        {
            lock (All)
            {
                var names = new List<string>();
                foreach (var e in All)
                {
                    if (e.Timer.TryGetTarget(out var t) && t.IsEnabled) names.Add($"{e.Owner} ({t.Interval.TotalMilliseconds:0} ms)");
                }
                return names;
            }
        }
    }

    /// <summary>Timers alive and enabled right now.</summary>
    public static int Running
    {
        get
        {
            lock (All)
            {
                All.RemoveAll(e => !e.Timer.TryGetTarget(out _));
                var n = 0;
                foreach (var e in All) if (e.Timer.TryGetTarget(out var t) && t.IsEnabled) n++;
                return n;
            }
        }
    }

    /// <summary>Timers alive, enabled or not (a stopped timer still referenced by its owner).</summary>
    public static int Alive
    {
        get
        {
            lock (All)
            {
                All.RemoveAll(e => !e.Timer.TryGetTarget(out _));
                return All.Count;
            }
        }
    }
}
