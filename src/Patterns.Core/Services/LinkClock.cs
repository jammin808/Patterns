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
/// The peer's clock against ours, by Show Bus 1.3 §13.11 — the one clock the twin's beats and the Stagewatch link's pongs
/// both measure. Every exchange is our send (t1, our clock), the peer's receive (t2, its clock), its send (t3, its clock)
/// and our receive (t4, our clock), in ticks of each side's own clock; NTP's arithmetic gives the offset, peer minus us, as
/// ((t2 − t1) + (t3 − t4)) / 2, and the path's round trip as (t4 − t1) − (t3 − t2): the hold the peer kept the beat for is
/// measured on its own clock and taken out, so a beat once a second serves as well as a request answered at once.
/// <para>
/// An exchange is admitted only by 13.11.2: no stamp of 0 or less, t4 not before t1, t3 not before t2, and a round trip
/// from 0 to <see cref="RoundTripMost"/> — a stamp lost, a reply that came before its ask or a peer whose clock stepped
/// between its two stamps measures nothing, and is counted (<see cref="Refused"/>), never clamped into a sample. The last
/// <see cref="Kept"/> admitted exchanges are kept and the one with the shortest round trip is believed (the newer of two
/// equal ones) — a beat that sat in a switch's queue implies an offset it never had, and its long round trip says so.
/// The believed sample publishes its error (13.11.3, ⌊(rtt + 1) / 2⌋ + 1 ms), its age since its t4 and the count kept;
/// its quality is fresh while the age is within <see cref="FreshFor"/>, and fit for a tolerance while fresh and the
/// error within it (13.11.4); the clocks are APART only when the offset less that error is past
/// <see cref="ApartAfter"/> (13.11.5). The window is emptied when the peer it measures is another process (13.11.7,
/// <see cref="Measuring"/>): a peer that restarted is not the clock measured. One lock: the twin's read loops and the
/// desk's readers share an instance.
/// </para>
/// </summary>
public sealed class LinkClock
{
    /// <summary>Exchanges kept; the shortest round trip among them is the one believed (§14 <c>clock.window</c>).</summary>
    public const int Kept = 8;

    /// <summary>Past this, less the believed sample's error, the clocks are apart (§14 <c>clock.apart.ms</c>, 13.11.5).</summary>
    public static readonly TimeSpan ApartAfter = TimeSpan.FromSeconds(2);

    /// <summary>The longest round trip an exchange may take and still be a sample (§14 <c>clock.rtt.max.ms</c>, 13.11.2).</summary>
    public static readonly TimeSpan RoundTripMost = TimeSpan.FromSeconds(60);

    /// <summary>How long after its t4 the believed sample is fresh (§14 <c>clock.fresh.ms</c>, 13.11.4).</summary>
    public static readonly TimeSpan FreshFor = TimeSpan.FromSeconds(10);

    /// <summary>Under this an offset is not worth a word on the line.</summary>
    public static readonly TimeSpan SaidFrom = TimeSpan.FromMilliseconds(500);

    private readonly object _gate = new();
    private readonly Queue<(long Offset, long Delay, long T4)> _samples = new();
    private (long Offset, long Delay, long T4) _best;
    private bool _known;
    private int _refused;
    private string _peer = "";

    /// <summary>The round trip of one exchange: (t4 − t1) − (t3 − t2).</summary>
    public static long RoundTrip(long t1, long t2, long t3, long t4) => (t4 - t1) - (t3 - t2);

    /// <summary>13.11.2: whether an exchange is a sample — every stamp above 0, t4 ≥ t1, t3 ≥ t2, and a round trip within 0 to <see cref="RoundTripMost"/>.</summary>
    public static bool Admits(long t1, long t2, long t3, long t4)
        => t1 > 0 && t2 > 0 && t3 > 0 && t4 > 0 && t4 >= t1 && t3 >= t2 && RoundTrip(t1, t2, t3, t4) is var rtt && rtt >= 0 && rtt <= RoundTripMost.Ticks;

    /// <summary>13.11.3: a sample's error in whole ms from its round trip in whole ms, ⌊(rtt + 1) / 2⌋ + 1.</summary>
    public static long ErrMs(long rttMs) => (rttMs + 1) / 2 + 1;

    /// <summary>A round trip in ticks as the whole ms 13.11.3 counts, rounded up: an error is never said smaller than it is.</summary>
    public static long WholeMs(long ticks) => (ticks + TimeSpan.TicksPerMillisecond - 1) / TimeSpan.TicksPerMillisecond;

    /// <summary>13.11.5: CLOCKS APART when the offset less the sample's error is past <see cref="ApartAfter"/>.</summary>
    public static bool Apart(TimeSpan offset, TimeSpan error) => offset.Duration() - error > ApartAfter;

    /// <summary>13.11.4: fresh while the believed sample's age is within <see cref="FreshFor"/>.</summary>
    public static bool Fresh(TimeSpan age) => age <= FreshFor;

    /// <summary>13.11.4: fit for a tolerance while fresh and its error within the tolerance.</summary>
    public static bool FitFor(TimeSpan age, TimeSpan error, TimeSpan tolerance) => Fresh(age) && error <= tolerance;

    /// <summary>
    /// One exchange, in ticks of each side's own clock: kept when 13.11.2 admits it and true; false, counted and nothing kept
    /// otherwise.
    /// </summary>
    public bool Sample(long t1, long t2, long t3, long t4)
    {
        lock (_gate)
        {
            if (!Admits(t1, t2, t3, t4))
            {
                _refused++;
                return false;
            }
            _samples.Enqueue((((t2 - t1) + (t3 - t4)) / 2, RoundTrip(t1, t2, t3, t4), t4));
            while (_samples.Count > Kept) _samples.Dequeue();
            var best = _samples.Peek();
            foreach (var s in _samples)
                if (s.Delay <= best.Delay) best = s;
            _best = best;
            _known = true;
            return true;
        }
    }

    /// <summary>
    /// 13.11.7: the peer process this clock measures — its node and its hello's inst, as the reader knows them. Another than
    /// the last one named empties the window (true): the clock of a peer that restarted, or of another node, is not the one
    /// measured. The first name keeps what the window holds.
    /// </summary>
    public bool Measuring(string peer)
    {
        lock (_gate)
        {
            var changed = _peer.Length > 0 && !string.Equals(_peer, peer, StringComparison.Ordinal);
            _peer = peer;
            if (changed) EmptyLocked();
            return changed;
        }
    }

    /// <summary>13.11.7: the window emptied — the peer restarted, or this node's own wall clock stepped.</summary>
    public void Clear()
    {
        lock (_gate) EmptyLocked();
    }

    private void EmptyLocked()
    {
        _samples.Clear();
        _best = default;
        _known = false;
    }

    public bool Known
    {
        get { lock (_gate) return _known; }
    }

    /// <summary>The peer's clock minus ours, from the exchange with the shortest round trip.</summary>
    public TimeSpan Offset
    {
        get { lock (_gate) return TimeSpan.FromTicks(_best.Offset); }
    }

    /// <summary>The round trip of the exchange believed.</summary>
    public TimeSpan Delay
    {
        get { lock (_gate) return TimeSpan.FromTicks(_best.Delay); }
    }

    /// <summary>13.11.3: the believed sample's error, ⌊(rtt + 1) / 2⌋ + 1 ms; zero before any.</summary>
    public TimeSpan Error
    {
        get { lock (_gate) return _known ? TimeSpan.FromMilliseconds(ErrMs(WholeMs(_best.Delay))) : TimeSpan.Zero; }
    }

    /// <summary>The samples kept.</summary>
    public int Samples
    {
        get { lock (_gate) return _samples.Count; }
    }

    /// <summary>The exchanges 13.11.2 refused since start: a reader says them, none is a sample.</summary>
    public int Refused
    {
        get { lock (_gate) return _refused; }
    }

    /// <summary>13.11.3: the believed sample's age at <paramref name="nowTicks"/> on this side's clock, since its t4; null before any.</summary>
    public TimeSpan? Age(long nowTicks)
    {
        lock (_gate) return _known ? TimeSpan.FromTicks(nowTicks - _best.T4) : null;
    }

    /// <summary>13.11.5 on the believed sample: CLOCKS APART.</summary>
    public bool IsApart
    {
        get { lock (_gate) return _known && Apart(TimeSpan.FromTicks(_best.Offset), TimeSpan.FromMilliseconds(ErrMs(WholeMs(_best.Delay)))); }
    }

    /// <summary>13.11.4: a sample believed and fresh at <paramref name="nowTicks"/>.</summary>
    public bool IsFresh(long nowTicks) => Age(nowTicks) is { } age && Fresh(age);

    /// <summary>13.11.4: fresh at <paramref name="nowTicks"/> and its error within <paramref name="tolerance"/>.</summary>
    public bool IsFitFor(TimeSpan tolerance, long nowTicks) => Age(nowTicks) is { } age && FitFor(age, Error, tolerance);

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
