using Patterns.Core.Media;
using System.Diagnostics;

namespace Patterns.Rendering.Media;

/// <summary>
/// The render fence: the way a pooled frame buffer or a retired picture knows its old pixels are
/// no longer being drawn. A renderer fetches a source's newest image inside a frame and hands it
/// to a canvas whose GPU reads the pixels when the frame's draws are flushed — so a buffer
/// replaced now may still be under a draw. Every sink takes a seat here; each of its frames is
/// explicit: <see cref="BeginFrame"/> opens it, <see cref="EndFrame"/> closes it once the frame's
/// draws are flushed (the pipeline flushes its canvas first — the upload of every raster picture
/// the frame read is complete then), and a resource is free once every frame that drew it has
/// closed. The same idea as a game engine's frames-in-flight fence, for pictures instead of
/// command lists.
///
/// A resource waits only for the frames that drew it: a draw notes the resource on the seat whose
/// frame is running (<see cref="Touch"/>, under the resource's own lock, in the same step as the
/// fetch), so a frame that never fetched it holds nothing of it.
///
/// Memory takes positive evidence only (round 64). No amount of time clears a resource for a frame
/// that drew it and is still open: a frame open past <see cref="HungAfter"/> is a hung frame — a
/// render thread stalled in a driver, a compositor that stopped — and what it holds stays held,
/// in quarantine, counted and named in the fault record and the health row, until the frame
/// closes. Bounded leakage during a stall is the safe failure; native memory overwritten under a
/// stalled draw is not. Time decides only what time can: a seat that has not begun a frame for
/// <see cref="IdleAfter"/> is not drawing (the live count), and only such a seat may be reseated
/// when the table is full. The clock is monotonic: the system clock moving cannot open or close a
/// fence. Pure bookkeeping; nothing allocated per mark or per frame.
/// </summary>
public static class RenderFence
{
    public const int MaxSinks = 256;

    /// <summary>A seat that has not begun a frame for this long is not drawing: it is not counted live, and its seat may be reseated when the table is full. Never a rule for memory.</summary>
    public static readonly TimeSpan IdleAfter = TimeSpan.FromSeconds(2);

    /// <summary>A frame open longer than this is hung: the health row's red and a fault record. What it holds stays held until it closes.</summary>
    public static readonly TimeSpan HungAfter = TimeSpan.FromSeconds(2);

    /// <summary>The fault records kept: the newest this many.</summary>
    public const int MaxFaults = 32;

    /// <summary>The tick of a seat that never began a frame: older than any clock, so it is idle from the start (a nought would look recent on a clock that has just begun).</summary>
    private const long Never = long.MinValue;

    private static readonly object Gate = new();
    private static readonly bool[] Registered = new bool[MaxSinks];
    private static readonly bool[] Open = new bool[MaxSinks];
    private static readonly bool[] Leaving = new bool[MaxSinks];
    private static readonly bool[] HungNoted = new bool[MaxSinks];
    private static readonly int[] Epoch = new int[MaxSinks];
    private static readonly string[] Labels = new string[MaxSinks];
    private static readonly long[] AdvancedAt = new long[MaxSinks];
    private static readonly long[] BeganTicks = NeverAll();
    private static readonly List<FenceFault> FaultList = new();

    private static long[] NeverAll()
    {
        var ticks = new long[MaxSinks];
        Array.Fill(ticks, Never);
        return ticks;
    }
    private static long _generation;
    private static long _hungFrames;

    /// <summary>The ticket of the seat whose frame is running on this thread, plus one (0: none) — set by <see cref="BeginFrame"/>, cleared by <see cref="EndFrame"/>.</summary>
    [ThreadStatic] private static int t_currentPlusOne;

    /// <summary>Tests: the monotonic clock the fence reads, in <see cref="Stopwatch"/> ticks.</summary>
    public static Func<long>? Clock { get; set; }

    private static long NowTicks => Clock?.Invoke() ?? Stopwatch.GetTimestamp();

    private static long IdleAfterTicks => (long)(IdleAfter.TotalSeconds * Stopwatch.Frequency);

    private static long HungAfterTicks => (long)(HungAfter.TotalSeconds * Stopwatch.Frequency);

    private static double Ms(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;

    /// <summary>A seat's id and the epoch of its tenancy, in one int: id in the low byte, epoch above — so a seat reseated under a quiet sink ignores that sink's later calls.</summary>
    private static int Ticket(int id) => id | (Epoch[id] << 8);

    private static int IdOf(int ticket) => ticket & 0xFF;

    /// <summary>The seat a ticket names, or -1 when the ticket is stale (the seat was reseated) or never a seat.</summary>
    private static int Seat(int ticket)
    {
        if (ticket < 0) return -1;
        var id = IdOf(ticket);
        return Registered[id] && Epoch[id] == ticket >> 8 ? id : -1;
    }

    /// <summary>
    /// A sink takes a seat: its ticket for <see cref="BeginFrame"/>, <see cref="EndFrame"/> and
    /// <see cref="Unregister"/>. Until its first frame begins it holds no picture, so nothing
    /// waits for it. A full table gives the seat idle the longest — one whose sink stopped
    /// drawing seconds ago without going quiet — never one whose frame is open, and -1 only when
    /// no seat is idle (a sink with no seat notes nothing, and the pools it draws go round on the
    /// other sinks' evidence alone). A stale ticket's calls do nothing: the seat's epoch moved on.
    /// </summary>
    public static int Register(string label = "")
    {
        var now = NowTicks;
        lock (Gate)
        {
            for (var i = 0; i < MaxSinks; i++)
            {
                if (Registered[i]) continue;
                return SeatLocked(i, label);
            }
            var oldest = -1;
            var oldestTicks = now - IdleAfterTicks;
            for (var i = 0; i < MaxSinks; i++)
            {
                if (Open[i]) continue;                                   // its frame is open: never reseated under it
                if (BeganTicks[i] < oldestTicks)
                {
                    oldestTicks = BeganTicks[i];
                    oldest = i;
                }
            }
            if (oldest >= 0) return SeatLocked(oldest, label);
            // Every seat is held by an open frame: no seat. The caller draws nothing until it has
            // one (round 65) — the fence fails closed, never open — and the label is on the list
            // the health rows read.
            if (label.Length > 0 && RefusedLabels.Add(label)) NoteFaultLocked(new FenceFault(DateTime.UtcNow, FenceFaultKind.Refused, -1, label, Interlocked.Read(ref _generation), 0));
            return -1;
        }
    }

    /// <summary>Sinks that asked for a seat and were refused, drawing nothing until one frees (round 65).</summary>
    public static IReadOnlyList<string> Refused
    {
        get { lock (Gate) return RefusedLabels.OrderBy(l => l, StringComparer.Ordinal).ToArray(); }
    }

    private static readonly HashSet<string> RefusedLabels = new(StringComparer.Ordinal);

    /// <summary>A refused sink that is gone (disposed) leaves the list without a seat.</summary>
    public static void Withdraw(string label)
    {
        lock (Gate) RefusedLabels.Remove(label);
    }

    private static int SeatLocked(int i, string label)
    {
        if (label.Length > 0 && RefusedLabels.Remove(label)) NoteFaultLocked(new FenceFault(DateTime.UtcNow, FenceFaultKind.Seated, i, label, Interlocked.Read(ref _generation), 0));
        Registered[i] = true;
        Open[i] = false;
        Leaving[i] = false;
        HungNoted[i] = false;
        Epoch[i]++;
        Labels[i] = label;
        AdvancedAt[i] = Interlocked.Read(ref _generation);
        BeganTicks[i] = Never;                                            // no frame yet: holding nothing
        return Ticket(i);
    }

    /// <summary>A sink leaves: an idle seat is free now; a seat whose frame is open stays until that frame ends, holding what it drew until then.</summary>
    public static void Unregister(int ticket)
    {
        lock (Gate)
        {
            var id = Seat(ticket);
            if (id < 0) return;
            if (Open[id]) Leaving[id] = true;
            else FreeLocked(id);
        }
    }

    private static void FreeLocked(int id)
    {
        Registered[id] = false;
        Open[id] = false;
        Leaving[id] = false;
        HungNoted[id] = false;
        Labels[id] = "";
        BeganTicks[id] = Never;
    }

    /// <summary>
    /// A frame opens on this thread: the generation it runs at. A seat whose previous frame was
    /// never closed closes it here — the next frame's start is proof the last one flushed, the
    /// contract before frames were explicit, kept for a caller without an end.
    /// </summary>
    public static long BeginFrame(int ticket)
    {
        var now = NowTicks;
        long g;
        lock (Gate)
        {
            var id = Seat(ticket);
            if (id < 0)
            {
                t_currentPlusOne = 0;
                return 0;
            }
            if (Open[id]) CloseLocked(id, now);
            g = Interlocked.Increment(ref _generation);
            Volatile.Write(ref AdvancedAt[id], g);
            Volatile.Write(ref BeganTicks[id], now);
            Open[id] = true;
            HungNoted[id] = false;
            t_currentPlusOne = Ticket(id) + 1;
        }
        return g;
    }

    /// <summary>
    /// The older name of <see cref="BeginFrame"/>: a sink starts a frame, and whatever its last
    /// frame drew has flushed. The frame it opens stays open until the seat's next frame or its
    /// end — and so does this thread's current seat: a test that advances and moves on must
    /// <see cref="ResetForTests"/> before the next test on the thread fetches a picture, or that
    /// fetch is recorded as a draw of a frame nobody will close (CI on round 64 found a trim test
    /// holding two pictures that way).
    /// </summary>
    public static void Advance(int ticket) => BeginFrame(ticket);

    /// <summary>
    /// The frame on this seat is done and its draws are flushed — the pixels of every picture it
    /// read are consumed: everything it drew is released. Called in the renderer's finally, after
    /// its canvas flushed. A seat that was leaving is free now.
    /// </summary>
    public static void EndFrame(int ticket)
    {
        var now = NowTicks;
        lock (Gate)
        {
            var id = Seat(ticket);
            t_currentPlusOne = 0;
            if (id < 0 || !Open[id]) return;
            CloseLocked(id, now);
            if (Leaving[id]) FreeLocked(id);
        }
    }

    private static void CloseLocked(int id, long now)
    {
        Open[id] = false;
        if (HungNoted[id])
        {
            HungNoted[id] = false;
            NoteFaultLocked(new FenceFault(DateTime.UtcNow, FenceFaultKind.Recovered, id, Labels[id], AdvancedAt[id], Ms(now - BeganTicks[id])));
        }
    }

    /// <summary>The ticket of the seat whose frame is running on this thread; -1 off a frame (tests read it).</summary>
    public static int CurrentSink => t_currentPlusOne - 1;

    /// <summary>
    /// A draw of a resource on the running frame: the resource's table remembers which frame of
    /// this seat drew it. Called under the resource's own lock in the same step as the fetch, so
    /// no publish can slip between. Off a frame (a thumbnail on a worker, a test) nothing is noted
    /// — a draw that completes before it returns needs no fence.
    /// </summary>
    public static void Touch(long[] drewAt)
    {
        var ticket = t_currentPlusOne - 1;
        if (ticket < 0) return;
        var s = IdOf(ticket);
        if (s >= drewAt.Length) return;
        Volatile.Write(ref drewAt[s], Volatile.Read(ref AdvancedAt[s]));
    }

    /// <summary>Where the frames are now: what a retired resource remembers, with the monotonic tick it was retired at.</summary>
    public readonly record struct Mark(long Generation, long Ticks);

    public static Mark Take() => new(Interlocked.Read(ref _generation), NowTicks);

    /// <summary>How long ago a mark was taken, in milliseconds.</summary>
    public static double AgeMs(in Mark mark) => Math.Max(0, (NowTicks - mark.Ticks) * 1000.0 / Stopwatch.Frequency);

    /// <summary>What holds a retired resource: nothing, a frame still open, or only frames hung past <see cref="HungAfter"/> — the quarantine.</summary>
    public enum Hold
    {
        Clear,
        Open,
        Hung,
    }

    /// <summary>True once every frame that drew the resource (its <paramref name="drewAt"/> table) has closed. Never by time alone.</summary>
    public static bool Cleared(in Mark mark, long[] drewAt) => Check(in mark, drewAt) == Hold.Clear;

    /// <summary>
    /// The hold on a resource retired at <paramref name="mark"/>: <see cref="Hold.Clear"/> once
    /// every frame that drew it (its <paramref name="drewAt"/> table, written by <see cref="Touch"/>)
    /// has closed; <see cref="Hold.Open"/> while one is open and running; <see cref="Hold.Hung"/>
    /// when the only frames holding it have been open past <see cref="HungAfter"/>. A frame that
    /// began after the mark could not have fetched the resource — what was retired is no longer
    /// the newest — and a frame that has closed is done with everything it drew.
    /// </summary>
    public static Hold Check(in Mark mark, long[] drewAt)
    {
        var hungBefore = NowTicks - HungAfterTicks;
        var hung = false;
        lock (Gate)
        {
            for (var i = 0; i < MaxSinks; i++)
            {
                if (!Registered[i] || !Open[i]) continue;                          // no frame open: done with whatever it drew
                var at = Volatile.Read(ref AdvancedAt[i]);
                if (at > mark.Generation) continue;                                // began after the mark: never fetched what was retired
                if (i >= drewAt.Length || Volatile.Read(ref drewAt[i]) != at) continue;   // its open frame drew nothing of this resource
                if (Volatile.Read(ref BeganTicks[i]) >= hungBefore) return Hold.Open;
                hung = true;
            }
        }
        return hung ? Hold.Hung : Hold.Clear;
    }

    /// <summary>Seats registered and drawing: a frame begun within <see cref="IdleAfter"/>.</summary>
    public static int LiveSinks
    {
        get
        {
            var idleBefore = NowTicks - IdleAfterTicks;
            var n = 0;
            lock (Gate)
            {
                for (var i = 0; i < MaxSinks; i++)
                {
                    if (Registered[i] && Volatile.Read(ref BeganTicks[i]) >= idleBefore) n++;
                }
            }
            return n;
        }
    }

    /// <summary>Seats taken right now — one per pipeline alive (the lifetime census reads it).</summary>
    public static int Seats
    {
        get
        {
            lock (Gate)
            {
                var n = 0;
                for (var i = 0; i < MaxSinks; i++) if (Registered[i]) n++;
                return n;
            }
        }
    }

    /// <summary>Seats with a frame open right now.</summary>
    public static int OpenFrames
    {
        get
        {
            lock (Gate)
            {
                var n = 0;
                for (var i = 0; i < MaxSinks; i++) if (Registered[i] && Open[i]) n++;
                return n;
            }
        }
    }

    /// <summary>Seats whose frame has been open past <see cref="HungAfter"/> right now: the health row's red. Reading it notes each such frame in the fault record once.</summary>
    public static int HungSinks
    {
        get
        {
            var now = NowTicks;
            var hungBefore = now - HungAfterTicks;
            lock (Gate)
            {
                var n = 0;
                for (var i = 0; i < MaxSinks; i++)
                {
                    if (!Registered[i] || !Open[i] || BeganTicks[i] >= hungBefore) continue;
                    n++;
                    if (HungNoted[i]) continue;
                    HungNoted[i] = true;
                    _hungFrames++;
                    NoteFaultLocked(new FenceFault(DateTime.UtcNow, FenceFaultKind.Hung, i, Labels[i], AdvancedAt[i], Ms(now - BeganTicks[i])));
                }
                return n;
            }
        }
    }

    /// <summary>How long the oldest open frame has been open, ms; -1 with none open.</summary>
    public static double OldestOpenMs
    {
        get
        {
            var now = NowTicks;
            lock (Gate)
            {
                var oldest = -1.0;
                for (var i = 0; i < MaxSinks; i++)
                {
                    if (!Registered[i] || !Open[i]) continue;
                    oldest = Math.Max(oldest, Ms(now - BeganTicks[i]));
                }
                return oldest;
            }
        }
    }

    /// <summary>Frames that went past <see cref="HungAfter"/> this session — the gate a soak reads: it should stay at nought.</summary>
    public static long HungFrames
    {
        get
        {
            _ = HungSinks;                                                      // a frame hung right now counts before anyone asks again
            return Interlocked.Read(ref _hungFrames);
        }
    }

    /// <summary>The fault record: every frame that hung, and its recovery when it closed — the newest <see cref="MaxFaults"/>.</summary>
    public static IReadOnlyList<FenceFault> Faults
    {
        get
        {
            _ = HungSinks;
            lock (Gate)
            {
                return FaultList.ToArray();
            }
        }
    }

    private static void NoteFaultLocked(FenceFault fault)
    {
        FaultList.Add(fault);
        if (FaultList.Count > MaxFaults) FaultList.RemoveAt(0);
    }

    /// <summary>The label a seat was registered with (tests and the words).</summary>
    public static string LabelOf(int ticket)
    {
        lock (Gate)
        {
            var id = Seat(ticket);
            return id < 0 ? "" : Labels[id];
        }
    }

    /// <summary>Whether a ticket still names a seat (tests read it).</summary>
    public static bool IsSeated(int ticket)
    {
        lock (Gate)
        {
            return Seat(ticket) >= 0;
        }
    }

    /// <summary>Whether the seat's frame is open (tests read it).</summary>
    public static bool IsOpen(int ticket)
    {
        lock (Gate)
        {
            var id = Seat(ticket);
            return id >= 0 && Open[id];
        }
    }

    /// <summary>Tests: every seat gone, the generation, the count and the record back to nought.</summary>
    public static void ResetForTests()
    {
        lock (Gate) RefusedLabels.Clear();
        lock (Gate)
        {
            Array.Clear(Registered);
            Array.Clear(Open);
            Array.Clear(Leaving);
            Array.Clear(HungNoted);
            Array.Clear(AdvancedAt);
            Array.Fill(BeganTicks, Never);
            Array.Fill(Labels, "");
            FaultList.Clear();
            Interlocked.Exchange(ref _generation, 0);
            Interlocked.Exchange(ref _hungFrames, 0);
        }
        t_currentPlusOne = 0;
    }
}

public enum FenceFaultKind
{
    /// <summary>A frame open past <see cref="RenderFence.HungAfter"/>: what it drew is in quarantine.</summary>
    Hung,
    /// <summary>The hung frame closed: what it held is released.</summary>
    Recovered,
    /// <summary>A sink asked for a seat and every seat was held by an open frame (round 65): it draws nothing until one frees — black, never a frame outside the fence.</summary>
    Refused,
    /// <summary>A sink that had been refused got its seat.</summary>
    Seated,
}

/// <summary>One line of the fence's fault record: when, what, which seat and its label, the frame's generation, and how long the frame had been open.</summary>
public readonly record struct FenceFault(DateTime AtUtc, FenceFaultKind Kind, int Sink, string Label, long Generation, double OpenMs)
{
    public override string ToString()
        => Kind switch
        {
            FenceFaultKind.Refused => $"{AtUtc:HH:mm:ss} REFUSED a seat{(Label.Length > 0 ? $" ({Label})" : "")} — every seat held by an open frame; drawing nothing",
            FenceFaultKind.Seated => $"{AtUtc:HH:mm:ss} seated{(Label.Length > 0 ? $" ({Label})" : "")} on seat {Sink} after a refusal",
            _ => $"{AtUtc:HH:mm:ss} {(Kind == FenceFaultKind.Hung ? "HUNG" : "recovered")} seat {Sink}{(Label.Length > 0 ? $" ({Label})" : "")} frame {Generation} open {OpenMs:0} ms",
        };
}
