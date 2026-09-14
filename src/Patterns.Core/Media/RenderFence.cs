namespace Patterns.Core.Media;

/// <summary>
/// The render fence: the way a pooled frame buffer knows its old picture is no longer being
/// drawn. A renderer fetches a source's newest image inside a frame and hands it to a canvas
/// whose GPU reads the pixels at the end of that frame — so a buffer replaced now may still be
/// under a draw for a frame. Every sink registers here and advances at the start of each frame
/// (the start of a frame is the proof its previous one flushed); a buffer retired at a mark is
/// free once every live sink has advanced past it, or once half a second has passed — a sink
/// that has not started a frame in two seconds is asleep or hung and holds nothing. The same
/// idea as a game engine's frames-in-flight fence, for pictures instead of command lists.
///
/// A pool waits only for the sinks that drew from it: a source's draw notes the pool on the
/// sink whose frame is running (<see cref="Touch"/>), and a mark is cleared for that pool by
/// every sink whose current frame did not draw from it — a preview that drew a static page
/// once and stopped holds no pooled picture, and does not hold every pool for half a second.
/// Pure bookkeeping; nothing allocated per mark.
/// </summary>
public static class RenderFence
{
    public const int MaxSinks = 256;

    /// <summary>A mark clears by time at the latest: no draw lasts this long.</summary>
    public static readonly TimeSpan Fallback = TimeSpan.FromMilliseconds(500);

    /// <summary>A sink that has not started a frame for this long is not mid-frame: it is not waited for.</summary>
    public static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(2);

    private static readonly object Gate = new();
    private static readonly bool[] Registered = new bool[MaxSinks];
    private static readonly long[] AdvancedAt = new long[MaxSinks];
    private static readonly long[] AdvancedUtcTicks = new long[MaxSinks];
    private static long _generation;

    /// <summary>The sink whose frame is running on this thread, plus one (0: none) — set by <see cref="Advance"/>.</summary>
    [ThreadStatic] private static int t_currentPlusOne;

    /// <summary>Tests: the clock the fence reads.</summary>
    public static Func<DateTime>? Clock { get; set; }

    private static long NowTicks => (Clock?.Invoke() ?? DateTime.UtcNow).Ticks;

    /// <summary>
    /// A sink joins: its id for <see cref="Advance"/>. Until its first frame starts it holds no
    /// picture, so nothing waits for it — a preview that is made and never shown, a window that
    /// is minimised before it draws, do not hold every pool for half a second. A full table gives
    /// the seat of the sink idle the longest — one that never said goodbye and stopped drawing
    /// seconds ago — and -1 only when every seat is drawing (the fence then waits on time alone
    /// for this one).
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
                AdvancedUtcTicks[i] = 0;                       // no frame yet: not mid-frame with anything
                return i;
            }
            var oldest = -1;
            var oldestTicks = now - IdleAfter.Ticks;
            for (var i = 0; i < MaxSinks; i++)
            {
                if (AdvancedUtcTicks[i] < oldestTicks)
                {
                    oldestTicks = AdvancedUtcTicks[i];
                    oldest = i;
                }
            }
            if (oldest >= 0)
            {
                AdvancedAt[oldest] = Interlocked.Read(ref _generation);
                AdvancedUtcTicks[oldest] = now;
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
        Volatile.Write(ref AdvancedUtcTicks[id], NowTicks);
        t_currentPlusOne = id + 1;
    }

    /// <summary>The sink whose frame is running on this thread; -1 off a frame (tests read it).</summary>
    public static int CurrentSink => t_currentPlusOne - 1;

    /// <summary>
    /// A draw from a pool on the running frame: the pool's table remembers which frame of this
    /// sink drew from it. Off a frame (a thumbnail on a worker, a test) nothing is noted — a draw
    /// that completes before it returns needs no fence.
    /// </summary>
    public static void Touch(long[] drewAt)
    {
        var s = t_currentPlusOne - 1;
        if (s < 0 || s >= drewAt.Length) return;
        Volatile.Write(ref drewAt[s], Volatile.Read(ref AdvancedAt[s]));
    }

    /// <summary>Where the sinks are now: what a retired buffer remembers.</summary>
    public readonly record struct Mark(long Generation, long UtcTicks);

    public static Mark Take() => new(Interlocked.Read(ref _generation), NowTicks);

    /// <summary>True once every live sink has started a frame after the mark, or the fallback has passed.</summary>
    public static bool Cleared(in Mark mark) => Cleared(in mark, null);

    /// <summary>
    /// The same for one pool: a sink whose running frame did not draw from the pool (its
    /// <paramref name="drewAt"/> table) holds none of its pictures and is not waited for.
    /// </summary>
    public static bool Cleared(in Mark mark, long[]? drewAt)
    {
        var now = NowTicks;
        if (now - mark.UtcTicks >= Fallback.Ticks) return true;
        var idleBefore = now - IdleAfter.Ticks;
        lock (Gate)
        {
            for (var i = 0; i < MaxSinks; i++)
            {
                if (!Registered[i]) continue;
                var at = Volatile.Read(ref AdvancedAt[i]);
                if (at > mark.Generation) continue;                                    // a new frame since: the old one flushed
                if (Volatile.Read(ref AdvancedUtcTicks[i]) < idleBefore) continue;    // asleep or hung: not mid-frame with this picture
                if (drewAt is not null && Volatile.Read(ref drewAt[i]) != at) continue;   // its running frame drew nothing from this pool
                return false;
            }
        }
        return true;
    }

    /// <summary>Sinks registered and drawing (a frame started in the last two seconds).</summary>
    public static int LiveSinks
    {
        get
        {
            var idleBefore = NowTicks - IdleAfter.Ticks;
            var n = 0;
            lock (Gate)
            {
                for (var i = 0; i < MaxSinks; i++)
                {
                    if (Registered[i] && Volatile.Read(ref AdvancedUtcTicks[i]) >= idleBefore) n++;
                }
            }
            return n;
        }
    }

    /// <summary>Tests: every sink gone, the generation back to nought.</summary>
    public static void ResetForTests()
    {
        lock (Gate)
        {
            Array.Clear(Registered);
            Array.Clear(AdvancedAt);
            Array.Clear(AdvancedUtcTicks);
            Interlocked.Exchange(ref _generation, 0);
        }
        t_currentPlusOne = 0;
    }
}
