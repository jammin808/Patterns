using System.Globalization;
namespace Patterns.Core.Services;

/// <summary>
/// A beat's stamps: <c>BEAT seq sent peerSent peerReceived</c> — the sender's own wall clock as it
/// wrote the line, and the echo of the last beat it heard from the peer: that beat's stamp and the
/// sender's clock when it arrived. Ticks (UTC, 100 ns) of each side's own clock; 0 for a stamp not
/// known. A beat before this round is <c>BEAT seq</c>, and still reads.
/// </summary>
public readonly record struct TwinBeat(long Seq, long SentTicks, long PeerSentTicks, long PeerReceivedTicks)
{
    public static TwinBeat Parse(string? payload)
    {
        var p = (payload ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        long At(int i) => i < p.Length && long.TryParse(p[i], out var v) && v >= 0 ? v : 0;
        return new TwinBeat(At(0), At(1), At(2), At(3));
    }

    /// <summary>"12 1000 900 950"; "1 1000" before any beat was heard; "7" with no stamps at all.</summary>
    public string Format() => PeerSentTicks > 0 && PeerReceivedTicks > 0
        ? $"{Seq} {SentTicks} {PeerSentTicks} {PeerReceivedTicks}"
        : SentTicks > 0 ? $"{Seq} {SentTicks}" : Seq.ToString(CultureInfo.InvariantCulture);

    public bool HasStamps => SentTicks > 0;

    /// <summary>It echoes one of ours: an exchange closes, and the clocks can be compared.</summary>
    public bool HasEcho => PeerSentTicks > 0 && PeerReceivedTicks > 0;
}

/// <summary>
/// The peer's clock against ours, from the beats. Every beat that echoes one of ours closes an
/// exchange — our send (t1, our clock), the peer's receive (t2, its clock), its send (t3, its
/// clock), our receive (t4, our clock) — and NTP's arithmetic gives the offset, peer minus us, as
/// ((t2 − t1) + (t3 − t4)) / 2, and the path's round trip as (t4 − t1) − (t3 − t2): the hold the
/// peer kept the beat for is measured on its own clock and taken out, so a beat once a second
/// serves as well as a request answered at once. The last eight exchanges are kept and the one
/// with the shortest round trip is believed — a beat that sat in a switch's queue on the way out
/// implies an offset it never had, and its long round trip says so. Pure: the arithmetic and the
/// words are tests.
/// </summary>
public sealed class LinkClock
{
    /// <summary>Exchanges kept; the shortest round trip among them is the one believed.</summary>
    public const int Kept = 8;

    /// <summary>Past this the clocks are apart: the line says so and names the fix.</summary>
    public static readonly TimeSpan ApartAfter = TimeSpan.FromSeconds(2);

    /// <summary>Under this an offset is not worth a word on the line.</summary>
    public static readonly TimeSpan SaidFrom = TimeSpan.FromMilliseconds(500);

    private readonly object _gate = new();
    private readonly Queue<(long Offset, long Delay)> _samples = new();
    private long _bestOffset;
    private long _bestDelay;
    private bool _known;

    /// <summary>One exchange, in ticks of each side's own clock; a stamp of 0 is no exchange.</summary>
    public void Sample(long t1, long t2, long t3, long t4)
    {
        if (t1 <= 0 || t2 <= 0 || t3 <= 0 || t4 <= 0) return;
        var offset = ((t2 - t1) + (t3 - t4)) / 2;
        var delay = Math.Max(0, (t4 - t1) - (t3 - t2));
        lock (_gate)
        {
            _samples.Enqueue((offset, delay));
            while (_samples.Count > Kept) _samples.Dequeue();
            var best = _samples.MinBy(s => s.Delay);
            _bestOffset = best.Offset;
            _bestDelay = best.Delay;
            _known = true;
        }
    }

    public bool Known
    {
        get { lock (_gate) return _known; }
    }

    /// <summary>The peer's clock minus ours, from the exchange with the shortest round trip.</summary>
    public TimeSpan Offset
    {
        get { lock (_gate) return TimeSpan.FromTicks(_bestOffset); }
    }

    /// <summary>The round trip of the exchange believed.</summary>
    public TimeSpan Delay
    {
        get { lock (_gate) return TimeSpan.FromTicks(_bestDelay); }
    }

    public int Samples
    {
        get { lock (_gate) return _samples.Count; }
    }

    public static bool Apart(TimeSpan offset) => offset.Duration() >= ApartAfter;

    /// <summary>"its clock 0.8 s ahead", "the desk's clock 1.5 s behind" — "" under half a second, or unknown.</summary>
    public static string Note(TimeSpan? offset, string whose)
    {
        if (offset is not { } o || o.Duration() < SaidFrom) return "";
        return $"{whose} clock {o.Duration().TotalSeconds:0.0} s {(o > TimeSpan.Zero ? "ahead" : "behind")}";
    }

    /// <summary>The warning: "CLOCKS 3.2 s APART — Backup's clock is ahead; set both machines to one time server".</summary>
    public static string ApartWords(string peer, TimeSpan offset)
        => $"CLOCKS {offset.Duration().TotalSeconds:0.0} s APART — {peer}'s clock is {(offset > TimeSpan.Zero ? "ahead" : "behind")}; set both machines to one time server";
}

/// <summary>
/// The rig's one clock as this process reads it: this machine's wall clock moved by the offset
/// the link measured to the desk's. A caller or a stage timer node reads every absolute time the
/// desk mirrors to it — a countdown's target, a cue's planned start, a message's flash — on the
/// desk's clock, not its own, so the speaker's timer and the desk's agree to the second whatever
/// the two machines' clocks say. The desk's own offset is zero: it is the frame. The offset moves
/// only past a deadband, so a jitter of milliseconds never flickers a second on a display, and it
/// is kept when the link drops — the last known frame is better than a jump — and reset when the
/// node is alone by choice.
/// </summary>
public sealed class RoomClock
{
    /// <summary>A measured offset within this of the one applied changes nothing.</summary>
    public static readonly TimeSpan Deadband = TimeSpan.FromMilliseconds(50);

    private long _offsetTicks;

    /// <summary>This machine's own clock; the tests move it.</summary>
    public Func<DateTime> Machine { get; set; } = () => DateTime.UtcNow;

    /// <summary>The desk's clock minus this machine's, as applied.</summary>
    public TimeSpan Offset
    {
        get => TimeSpan.FromTicks(Interlocked.Read(ref _offsetTicks));
        set => Interlocked.Exchange(ref _offsetTicks, value.Ticks);
    }

    public DateTime UtcNow => Machine() + Offset;

    public DateTime Now => UtcNow.ToLocalTime();

    /// <summary>Applies a measured offset when it differs from the applied one by more than the deadband; true when the clock moved.</summary>
    public bool Follow(TimeSpan measured)
    {
        if ((measured - Offset).Duration() <= Deadband) return false;
        Offset = measured;
        return true;
    }

    public void Reset() => Offset = TimeSpan.Zero;

    /// <summary>"the show clock runs 0.8 s ahead of this machine's, on the desk's" — "" at zero.</summary>
    public string Words => Offset == TimeSpan.Zero
        ? ""
        : $"the show clock runs {Offset.Duration().TotalSeconds:0.0} s {(Offset > TimeSpan.Zero ? "ahead of" : "behind")} this machine's, on the desk's";
}
