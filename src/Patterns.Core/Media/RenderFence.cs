using System.Diagnostics;

namespace Patterns.Core.Media;

/// <summary>
/// The render fence: the way a pooled frame buffer or a retired picture knows its old pixels are
/// no longer being drawn. A renderer fetches a source's newest image inside a frame and hands it
/// to a canvas whose GPU reads the pixels at the end of that frame — so a buffer replaced now may
/// still be under a draw for a frame. Every sink registers here and advances at the start of each
/// frame (the start of a frame is the proof its previous one flushed); a buffer retired at a mark
/// is free once every sink that drew from it has started a frame after the mark. The same idea
/// as a game engine's frames-in-flight fence, for pictures instead of command lists.
///
/// A resource waits only for the sinks that drew it: a draw notes the resource on the sink whose
/// frame is running (<see cref="Touch"/>, under the resource's own lock, in the same step as the
/// fetch), and a mark is cleared for that resource by every sink whose running frame did not draw
/// it — a preview that drew a static page once and stopped holds nothing of a camera's pool.
///
/// Reuse takes positive evidence only: no amount of time clears a mark for a sink that drew the
/// pixels and has not started another frame. The one timing assumption is the dead sink — a sink
/// that has not started a frame in <see cref="DeadAfter"/> is not mid-frame; a window minimised
/// or closed without saying goodbye stops holding buffers then. Time alone (<see cref="Abandoned"/>)
/// is used by the disposal paths for a resource nobody could be drawing any more, and counted as
/// forced when it happens. The clock is monotonic: the system clock moving cannot clear or extend
/// a fence. Pure bookkeeping; nothing allocated per mark.
/// </summary>
public static class RenderFence
{
    public const int MaxSinks = 256;

    /// <summary>A sink that has not started a frame for this long is not mid-frame: it holds nothing.</summary>
    public static readonly TimeSpan DeadAfter = TimeSpan.FromSeconds(2);

    /// <summary>A retired resource nobody released for this long is freed anyway, and the count says so — a sink hung mid-frame for ten seconds is a hung sink, not a draw.</summary>
    public static readonly TimeSpan AbandonAfter = TimeSpan.FromSeconds(10);

    /// <summary>The tick of a sink that never started a frame: older than any clock, so it is dead from the start (a nought would look recent on a clock that has just begun).</summary>
    private const long Never = long.MinValue;

    private static readonly object Gate = new();
    private static readonly bool[] Registered = new bool[MaxSinks];
    private static readonly long[] AdvancedAt = new long[MaxSinks];
    private static readonly long[] AdvancedTicks = NeverAll();

    private static long[] NeverAll()
    {
        var ticks = new long[MaxSinks];
        Array.Fill(ticks, Never);
        return ticks;
    }
    private static long _generation;
    private static long _forcedFrees;

    /// <summary>The sink whose frame is running on this thread, plus one (0: none) — set by <see cref="Advance"/>.</summary>
    [ThreadStatic] private static int t_currentPlusOne;

    /// <summary>Tests: the monotonic clock the fence reads, in <see cref="Stopwatch"/> ticks.</summary>
    public static Func<long>? Clock { get; set; }

    private static long NowTicks => Clock?.Invoke() ?? Stopwatch.GetTimestamp();

    private static long DeadAfterTicks => (long)(DeadAfter.TotalSeconds * Stopwatch.Frequency);

    private static long AbandonAfterTicks => (long)(AbandonAfter.TotalSeconds * Stopwatch.Frequency);

    /// <summary>
    /// A sink joins: its id for <see cref="Advance"/>. Until its first frame starts it holds no
    /// picture, so nothing waits for it — a preview that is made and never shown, a window that
    /// is minimised before it draws, do not hold every pool. A full table gives the seat of the
    /// sink idle the longest — one that never said goodbye and stopped drawing seconds ago — and
    /// -1 only when every seat is drawing (a sink with no seat notes nothing, and the pools it
    /// draws go round on the other sinks' evidence alone).
    /// </summary>
    public static int Register()
    {
        var now = NowTicks;
        lock (Gate)
        {
            for (var i = 0; i < MaxSinks; i++)
            {
                if (Registered[i]) continue;
                Registered[i] = true;
                AdvancedAt[i] = Interlocked.Read(ref _generation);
                AdvancedTicks[i] = Never;                        // no frame yet: not mid-frame with anything
                return i;
            }
            var oldest = -1;
            var oldestTicks = now - DeadAfterTicks;
            for (var i = 0; i < MaxSinks; i++)
            {
                if (AdvancedTicks[i] < oldestTicks)
                {
                    oldestTicks = AdvancedTicks[i];
                    oldest = i;
                }
            }
            if (oldest >= 0)
            {
                AdvancedAt[oldest] = Interlocked.Read(ref _generation);
                AdvancedTicks[oldest] = Never;
            }
            return oldest;
        }
    }

    public static void Unregister(int id)
    {
        if (id < 0 || id >= MaxSinks) return;
        lock (Gate)
        {
            Registered[id] = false;
        }
    }

    /// <summary>A sink starts a frame: whatever it drew last frame has flushed.</summary>
    public static void Advance(int id)
    {
        if (id < 0 || id >= MaxSinks) return;
        var g = Interlocked.Increment(ref _generation);
        Volatile.Write(ref AdvancedAt[id], g);
        Volatile.Write(ref AdvancedTicks[id], NowTicks);
        t_currentPlusOne = id + 1;
    }

    /// <summary>The sink whose frame is running on this thread; -1 off a frame (tests read it).</summary>
    public static int CurrentSink => t_currentPlusOne - 1;

    /// <summary>
    /// A draw of a resource on the running frame: the resource's table remembers which frame of
    /// this sink drew it. Called under the resource's own lock in the same step as the fetch, so
    /// no publish can slip between. Off a frame (a thumbnail on a worker, a test) nothing is noted
    /// — a draw that completes before it returns needs no fence.
    /// </summary>
    public static void Touch(long[] drewAt)
    {
        var s = t_currentPlusOne - 1;
        if (s < 0 || s >= drewAt.Length) return;
        Volatile.Write(ref drewAt[s], Volatile.Read(ref AdvancedAt[s]));
    }

    /// <summary>Where the sinks are now: what a retired resource remembers, with the monotonic tick it was retired at.</summary>
    public readonly record struct Mark(long Generation, long Ticks);

    public static Mark Take() => new(Interlocked.Read(ref _generation), NowTicks);

    /// <summary>How long ago a mark was taken, in milliseconds.</summary>
    public static double AgeMs(in Mark mark) => Math.Max(0, (NowTicks - mark.Ticks) * 1000.0 / Stopwatch.Frequency);

    /// <summary>True once every live sink has started a frame after the mark: the rule for a resource with no table of its own (every sink may have drawn it).</summary>
    public static bool Cleared(in Mark mark) => Cleared(in mark, null);

    /// <summary>
    /// True once every sink that drew the resource (its <paramref name="drewAt"/> table; null means
    /// any sink) on its running frame has started a frame after the mark, or is dead. Never by
    /// time alone.
    /// </summary>
    public static bool Cleared(in Mark mark, long[]? drewAt)
    {
        var deadBefore = NowTicks - DeadAfterTicks;
        lock (Gate)
        {
            for (var i = 0; i < MaxSinks; i++)
            {
                if (!Registered[i]) continue;
                var at = Volatile.Read(ref AdvancedAt[i]);
                if (at > mark.Generation) continue;                                    // a new frame since: the old one flushed
                if (Volatile.Read(ref AdvancedTicks[i]) < deadBefore) continue;        // dead: not mid-frame with this picture
                if (drewAt is not null && Volatile.Read(ref drewAt[i]) != at) continue;   // its running frame drew nothing of this resource
                return false;
            }
        }
        return true;
    }

    /// <summary>A mark nobody cleared for <see cref="AbandonAfter"/>: the disposal paths free the resource anyway and say so through <see cref="NoteForced"/>.</summary>
    public static bool Abandoned(in Mark mark) => NowTicks - mark.Ticks >= AbandonAfterTicks;

    /// <summary>A resource freed on time alone — a sink held it past <see cref="AbandonAfter"/> without a frame: the health row's red.</summary>
    public static void NoteForced() => Interlocked.Increment(ref _forcedFrees);

    /// <summary>Resources freed on time alone this session.</summary>
    public static long ForcedFrees => Interlocked.Read(ref _forcedFrees);

    /// <summary>Sinks registered and drawing (a frame started within <see cref="DeadAfter"/>).</summary>
    public static int LiveSinks
    {
        get
        {
            var deadBefore = NowTicks - DeadAfterTicks;
            var n = 0;
            lock (Gate)
            {
                for (var i = 0; i < MaxSinks; i++)
                {
                    if (Registered[i] && Volatile.Read(ref AdvancedTicks[i]) >= deadBefore) n++;
                }
            }
            return n;
        }
    }

    /// <summary>Tests: every sink gone, the generation and the forced count back to nought.</summary>
    public static void ResetForTests()
    {
        lock (Gate)
        {
            Array.Clear(Registered);
            Array.Clear(AdvancedAt);
            Array.Fill(AdvancedTicks, Never);
            Interlocked.Exchange(ref _generation, 0);
            Interlocked.Exchange(ref _forcedFrees, 0);
        }
        t_currentPlusOne = 0;
    }
}
