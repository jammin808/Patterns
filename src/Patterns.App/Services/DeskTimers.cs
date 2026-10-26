using Avalonia.Threading;

namespace Patterns.App.Services;

/// <summary>
/// Every dispatcher timer the desk makes goes through here (round 64). A running
/// <see cref="DispatcherTimer"/> is rooted by the dispatcher itself — its tick handler, its owner,
/// the whole desk behind it — so a timer a closed desk forgot to stop keeps that desk alive for
/// the life of the process: the lifetime census found the view model's one-second status timer
/// doing exactly that. The timers are held weakly, each with the identity it was given as it was
/// made (one serial, under the list's lock, never reused); <see cref="Running"/> counts the ones
/// alive and enabled, the census's "timers" row, and <see cref="RunningIds"/> names them, so a
/// census can judge only the timers that were not running at its mark — a timer made long before
/// and started again since is one of those (<see cref="DeskCensus.Mark"/>).
/// </summary>
public static class DeskTimers
{
    private sealed record Entry(WeakReference<DispatcherTimer> Timer, string Owner, long Id);

    private static readonly List<Entry> All = new();

    private static long _made;

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
            All.Add(new Entry(new WeakReference<DispatcherTimer>(timer), owner, ++_made));
        }
        return timer;
    }

    /// <summary>The owners of the timers running right now, for the census's words: "MainViewModel..ctor (1 s)".</summary>
    public static IReadOnlyList<string> RunningOwners => RunningOwnersNotIn(null);

    /// <summary>The owners of the timers running right now whose identity <paramref name="there"/> does not hold; with none, every one.</summary>
    public static IReadOnlyList<string> RunningOwnersNotIn(IReadOnlySet<long>? there)
    {
        lock (All)
        {
            var names = new List<string>();
            foreach (var e in All)
            {
                if (there?.Contains(e.Id) == true) continue;
                if (e.Timer.TryGetTarget(out var t) && t.IsEnabled) names.Add($"{e.Owner} ({t.Interval.TotalMilliseconds:0} ms)");
            }
            return names;
        }
    }

    /// <summary>Timers alive and enabled right now.</summary>
    public static int Running => RunningNotIn(null);

    /// <summary>The identities of the timers alive and enabled right now: what a census mark records.</summary>
    public static IReadOnlySet<long> RunningIds()
    {
        lock (All)
        {
            All.RemoveAll(e => !e.Timer.TryGetTarget(out _));
            var ids = new HashSet<long>();
            foreach (var e in All) if (e.Timer.TryGetTarget(out var t) && t.IsEnabled) ids.Add(e.Id);
            return ids;
        }
    }

    /// <summary>Timers alive and enabled right now whose identity <paramref name="there"/> does not hold; with none, every one.</summary>
    public static int RunningNotIn(IReadOnlySet<long>? there)
    {
        lock (All)
        {
            All.RemoveAll(e => !e.Timer.TryGetTarget(out _));
            var n = 0;
            foreach (var e in All)
            {
                if (there?.Contains(e.Id) == true) continue;
                if (e.Timer.TryGetTarget(out var t) && t.IsEnabled) n++;
            }
            return n;
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
