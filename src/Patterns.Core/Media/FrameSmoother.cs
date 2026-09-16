using Patterns.Core.Services;

namespace Patterns.Core.Media;

/// <summary>How a page's frames reach the glass: Auto picks by the page's cadence (vMix's rule), Smooth buffers, LowLatency shows the newest at once.</summary>
public enum WebSmoothing
{
    Auto,
    Smooth,
    LowLatency,
}

/// <summary>
/// The smoothing buffer for a live picture whose frames arrive unevenly — a browser's screencast
/// (round 68). A video's frames leave the page every cadence but reach Patterns late, early and
/// in bursts: encoded, carried, decoded. Shown as they arrive they judder. Here each frame gets an
/// <em>ideal</em> time from a phase-locked clock — the previous ideal plus the measured cadence,
/// nudged a little towards the real arrival so the clock tracks the page's rate without following
/// its jitter — and is due at that ideal plus a latency of a few frames at the cadence. A sink
/// that asks <see cref="Pick"/> gets the newest frame whose time has come, never one ahead of its
/// time, never one twice; every sink reads the show clock, so every output shows the same frame
/// in the same slot. The due times are regular even when the arrivals are not: that is the whole
/// point.
///
/// The latency is a depth of frames: it starts from the machine class and follows the measured
/// jitter — the p95 of how far an arrival strays from the clock's prediction — within the class's
/// bounds and the frame pool's room, growing at once and shrinking only after a full quiet window,
/// so a steady source costs the least delay and a ragged one gets the room it needs. A source
/// slower than 24 fps (a dashboard, a clock) is shown at once under Auto: buffering a page that
/// sends a frame when something changes only delays it. A frame that misses its time is an
/// underrun (the last frame stays up; one count per stall); a full ring on arrival is an overrun
/// (the oldest waiting frame goes, counted). <see cref="Drain"/> takes no new frames and presents
/// what is held through the fade; <see cref="Cut"/> clears it — the frames a page buffered are
/// shown or dropped, never leaked past the moment it left the programme. Pure: the ring holds
/// frame ids, the caller holds the pictures; unit tested without a browser.
/// </summary>
public sealed class FrameSmoother
{
    /// <summary>The depth bounds and the starting depth for a machine of a class, in frames.</summary>
    public readonly record struct Bounds(int Min, int Start, int Max)
    {
        public static Bounds For(MachineClass machine) => machine switch
        {
            MachineClass.Small => new Bounds(2, 3, 5),
            MachineClass.Big => new Bounds(1, 2, 3),
            _ => new Bounds(2, 2, 4),
        };

        public int Clamp(int depth) => Math.Clamp(depth, Min, Math.Max(Min, Max));
    }

    /// <summary>One frame waiting: the caller's id for it, the show clock it arrived at and the clock's ideal time for it.</summary>
    public readonly record struct Waiting(long Id, double Arrival, double Ideal);

    /// <summary>The cadence assumed before enough frames have arrived to measure one: a video's 30 fps.</summary>
    public const double DefaultCadence = 1.0 / 30;

    /// <summary>Intervals outside this range are not a cadence (a page that sent nothing for a second, two frames in one burst) and do not move the estimate.</summary>
    public const double MinCadence = 1.0 / 120;
    public const double MaxCadence = 1.0 / 5;

    /// <summary>
    /// The rates a page delivers frames at. A measured cadence within <see cref="SnapTolerance"/> of one
    /// locks to it exactly — the schedule and the latency then stand on a steady number, not on the mean
    /// of jittery intervals (which would wobble the due times by the very jitter the buffer removes); a
    /// page at some other rate keeps its measured mean. The browser composites on its display's vsync,
    /// so a 29.97 fps video reaches the screencast as 30 fps frames with a repeat now and then: the
    /// broadcast fractions are not here on purpose (they would only make the lock flap).
    /// </summary>
    public static readonly double[] KnownRates = { 120, 60, 50, 48, 30, 25, 24, 20, 15, 12, 10 };
    public const double SnapTolerance = 0.04;

    /// <summary>Frames held at most, whatever the depth: the ring's size.</summary>
    public const int Capacity = 8;

    /// <summary>A source at this rate or faster is smoothed under Auto; back to the newest frame at once below <see cref="AutoLowLatencyFps"/> (a band, so a 24 fps page does not flap).</summary>
    public const double AutoSmoothFps = 24;
    public const double AutoLowLatencyFps = 20;

    /// <summary>Intervals measured before the cadence, the jitter and Auto's judgement count.</summary>
    public const int MeasuredIntervals = 8;

    /// <summary>How far the clock's ideal follows a real arrival: a tenth of the error per frame — the rate is tracked, the jitter is not.</summary>
    public const double Lock = 0.1;

    /// <summary>An arrival this far from the clock's prediction is a gap, not jitter: the clock re-locks on it.</summary>
    public const double ReLockSeconds = 0.25;

    /// <summary>A frame on show this many cadences past its time with nothing due is a stall.</summary>
    public const double StallCadences = 2;

    private const int JitterWindow = 32;

    private readonly Waiting[] _ring = new Waiting[Capacity];
    private readonly double[] _jitter = new double[JitterWindow];       // the last latenesses against the clock's prediction, for the p95
    private readonly double[] _arrivals = new double[JitterWindow];     // the last arrivals since the clock locked, for the rate
    private int _jitterNext;
    private int _jitterCount;
    private int _arrivalNext;
    private int _arrivalCount;
    private int _head;                                                  // the oldest waiting frame
    private int _count;
    private int _calm;                                                  // intervals in a row the jitter has asked for less depth than there is
    private int _cap = Capacity;
    private double _raw = DefaultCadence;                               // the rate as measured: the slope of the arrivals over the window
    private double _cadence = DefaultCadence;                           // the measured rate locked to a known one where one is near
    private double _lastArrival = -1;
    private double _ideal = -1;                                         // the clock's time for the last frame offered
    private double _shownDue = -1;
    private long _shownId = -1;
    private bool _draining;
    private bool _autoSmooth;
    private bool _stalled;

    public FrameSmoother(Bounds bounds, WebSmoothing mode = WebSmoothing.Auto)
    {
        BoundsOf = bounds;
        Depth = bounds.Clamp(bounds.Start);
        Mode = mode;
    }

    public Bounds BoundsOf { get; }

    /// <summary>The frames of latency the buffer runs at now.</summary>
    public int Depth { get; private set; }

    /// <summary>The most frames the caller can hold for the buffer (its pool's room less the frame on show, the one retiring and the one decoding); the depth never passes it.</summary>
    public int Cap
    {
        get => _cap;
        set
        {
            _cap = Math.Clamp(value, 1, Capacity);
            Depth = ClampDepth(Depth);
        }
    }

    public WebSmoothing Mode { get; set; }

    /// <summary>The cadence the buffer runs on, seconds between frames: the measured mean locked to a known rate where one is near.</summary>
    public double Cadence => _cadence;

    /// <summary>The cadence as a rate.</summary>
    public double Fps => _cadence > 0 ? 1 / _cadence : 0;

    /// <summary>The rate as measured, before the lock — the diagnostics' number.</summary>
    public double MeasuredFps => _raw > 0 ? 1 / _raw : 0;

    /// <summary>Whether enough intervals have been measured for the cadence and the jitter to mean anything.</summary>
    public bool Measured => _jitterCount >= MeasuredIntervals;

    /// <summary>The p95 of how far an arrival strays from the clock's prediction, seconds; 0 before <see cref="Measured"/>.</summary>
    public double JitterSeconds { get; private set; }

    /// <summary>The latency the buffer adds now, seconds: the depth at the cadence; 0 when the newest frame is shown at once.</summary>
    public double LatencySeconds => Smoothing ? Depth * _cadence : 0;

    /// <summary>Whether frames are buffered right now: the mode, or Auto's judgement of the measured cadence.</summary>
    public bool Smoothing => Mode switch
    {
        WebSmoothing.Smooth => true,
        WebSmoothing.LowLatency => false,
        _ => _autoSmooth,
    };

    /// <summary>Frames waiting for their time.</summary>
    public int Held => _count;

    public long Offered { get; private set; }
    public long Presented { get; private set; }

    /// <summary>Stalls: times the frame on show stood past its time with nothing due while the buffer was smoothing — one count per stall, however long.</summary>
    public long Underruns { get; private set; }

    /// <summary>Frames the room never got: the ring was full when one arrived (the oldest went), a newer one was due at the same pick, or the caller took one out for room.</summary>
    public long Dropped { get; private set; }

    /// <summary>Frames refused while draining.</summary>
    public long RefusedDraining { get; private set; }

    public bool Draining => _draining;

    /// <summary>The id of the frame on show, -1 before the first.</summary>
    public long ShownId => _shownId;

    /// <summary>
    /// A frame arrived at this show clock: measured, given its ideal time and queued. Returns the
    /// id of a frame the caller must free — the oldest waiting one when the ring was full, or the
    /// offered id itself while draining (refused) — or -1.
    /// </summary>
    public long Offer(long id, double arrival)
    {
        if (_draining)
        {
            RefusedDraining++;
            return id;
        }
        Offered++;
        var ideal = Measure(arrival);
        long dropped = -1;
        if (_count == Capacity)
        {
            dropped = _ring[_head].Id;
            _head = (_head + 1) % Capacity;
            _count--;
            Dropped++;
        }
        _ring[(_head + _count) % Capacity] = new Waiting(id, arrival, ideal);
        _count++;
        return dropped;
    }

    /// <summary>The caller refused a frame while draining before it cost anything (a decode not done): counted with the others.</summary>
    public void Refuse() => RefusedDraining++;

    /// <summary>The cadence, the jitter and the clock's ideal for a frame arriving now.</summary>
    private double Measure(double arrival)
    {
        double ideal;
        var interval = _lastArrival >= 0 ? arrival - _lastArrival : 0;
        if (_ideal < 0 || _lastArrival < 0)
        {
            ideal = Relock(arrival);                                                        // the first frame, or the first after a clear: the clock starts here
        }
        else
        {
            var predicted = _ideal + _cadence;
            var error = arrival - predicted;
            if (Math.Abs(error) > ReLockSeconds || interval > MaxCadence)
            {
                ideal = Relock(arrival);                                                    // a gap: the page stopped or jumped; nothing to smooth across
            }
            else
            {
                ideal = predicted + error * Lock;
                _arrivals[_arrivalNext] = arrival;
                _arrivalNext = (_arrivalNext + 1) % JitterWindow;
                if (_arrivalCount < JitterWindow) _arrivalCount++;
                _raw = Slope();
                _cadence = Snap(_raw);
                // Only a late frame needs room in the buffer: an early one waits for its time whatever the depth.
                _jitter[_jitterNext] = Math.Max(0, error);
                _jitterNext = (_jitterNext + 1) % JitterWindow;
                if (_jitterCount < JitterWindow) _jitterCount++;
                if (Measured) Adapt();
            }
        }
        _lastArrival = arrival;
        _ideal = ideal;
        return ideal;
    }

    /// <summary>The clock locks on this arrival: the rate window starts again from it (the cadence and the depth stand as they were).</summary>
    private double Relock(double arrival)
    {
        _arrivalCount = 1;
        _arrivalNext = 1;
        _arrivals[0] = arrival;
        return arrival;
    }

    /// <summary>
    /// The mean interval over the arrival window, as the least-squares slope of the arrivals against
    /// their order: a burst (a late frame and the catch-up after it) leaves it where a running mean
    /// of the intervals would swing, and the first frames set it rather than a default.
    /// </summary>
    private double Slope()
    {
        var n = _arrivalCount;
        if (n < 2) return _raw;
        var first = (_arrivalNext - n + JitterWindow) % JitterWindow;
        var xMean = (n - 1) / 2.0;
        double yMean = 0;
        for (var i = 0; i < n; i++) yMean += _arrivals[(first + i) % JitterWindow];
        yMean /= n;
        double cov = 0, var = 0;
        for (var i = 0; i < n; i++)
        {
            var dx = i - xMean;
            cov += dx * (_arrivals[(first + i) % JitterWindow] - yMean);
            var += dx * dx;
        }
        var slope = var > 0 ? cov / var : _raw;
        return Math.Clamp(slope, MinCadence, MaxCadence);
    }

    /// <summary>The depth from the jitter (enough frames to cover the p95 stray, plus one) and Auto's judgement of the rate — the depth grows at once and shrinks a frame at a time after a quiet window.</summary>
    private void Adapt()
    {
        var sorted = new double[_jitterCount];
        Array.Copy(_jitter, sorted, _jitterCount);
        Array.Sort(sorted);
        JitterSeconds = sorted[Math.Min(sorted.Length - 1, (int)Math.Floor(sorted.Length * 0.95))];
        var needed = ClampDepth((int)Math.Ceiling(JitterSeconds / Math.Max(_cadence, MinCadence)) + 1);
        if (needed > Depth)
        {
            Depth = needed;
            _calm = 0;
        }
        else if (needed < Depth)
        {
            if (++_calm >= JitterWindow)
            {
                Depth = ClampDepth(Depth - 1);
                _calm = 0;
            }
        }
        else
        {
            _calm = 0;
        }
        var fps = Fps;
        if (!_autoSmooth && fps >= AutoSmoothFps) _autoSmooth = true;
        else if (_autoSmooth && fps < AutoLowLatencyFps) _autoSmooth = false;
    }

    private int ClampDepth(int depth) => Math.Min(BoundsOf.Clamp(depth), _cap);

    /// <summary>The nearest known rate within the tolerance, as a cadence; the mean itself when none is near.</summary>
    public static double Snap(double meanCadence)
    {
        if (meanCadence <= 0) return DefaultCadence;
        var fps = 1 / meanCadence;
        var best = meanCadence;
        var bestError = SnapTolerance;
        foreach (var rate in KnownRates)
        {
            var error = Math.Abs(fps - rate) / rate;
            if (error < bestError)
            {
                bestError = error;
                best = 1 / rate;
            }
        }
        return best;
    }

    /// <summary>The show clock a waiting frame is due at: its ideal plus the latency when smoothing, its arrival (at once) when not.</summary>
    public double DueOf(in Waiting w) => Smoothing ? w.Ideal + Depth * _cadence : w.Arrival;

    /// <summary>
    /// The frame a sink should show now: the newest whose due time has passed — older frames due
    /// together with it are dropped into <paramref name="dropped"/> for the caller to free (the room
    /// gets the freshest picture that is on time) — or -1 when the frame on show stands. In low
    /// latency the newest waiting frame is due at once.
    /// </summary>
    public long Pick(double now, List<long>? dropped = null)
    {
        var due = -1;
        // From the oldest: every frame whose time has come is a candidate; the newest of them shows.
        for (var i = 0; i < _count; i++)
        {
            if (DueOf(in _ring[(_head + i) % Capacity]) <= now) due = i;
            else break;
        }
        if (due < 0)
        {
            if (!_stalled && !_draining && Smoothing && _shownDue >= 0 && now - _shownDue > StallCadences * _cadence)
            {
                _stalled = true;
                Underruns++;
            }
            return -1;
        }
        for (var i = 0; i < due; i++)
        {
            dropped?.Add(_ring[(_head + i) % Capacity].Id);
        }
        Dropped += due;
        var chosen = _ring[(_head + due) % Capacity];
        _head = (_head + due + 1) % Capacity;
        _count -= due + 1;
        _shownId = chosen.Id;
        _shownDue = DueOf(in chosen);
        _stalled = false;
        Presented++;
        return chosen.Id;
    }

    /// <summary>The ids waiting, oldest first — for a cut, the caller's room-making and the tests.</summary>
    public IEnumerable<long> WaitingIds()
    {
        for (var i = 0; i < _count; i++) yield return _ring[(_head + i) % Capacity].Id;
    }

    /// <summary>The frames waiting, oldest first, with their times — the tests read the clock's work.</summary>
    public IEnumerable<Waiting> WaitingFrames()
    {
        for (var i = 0; i < _count; i++) yield return _ring[(_head + i) % Capacity];
    }

    /// <summary>One waiting frame taken out (a starved pool's room-making): dropped and counted; false when it was not waiting.</summary>
    public bool Remove(long id)
    {
        for (var i = 0; i < _count; i++)
        {
            if (_ring[(_head + i) % Capacity].Id != id) continue;
            for (var j = i; j < _count - 1; j++)
            {
                _ring[(_head + j) % Capacity] = _ring[(_head + j + 1) % Capacity];
            }
            _count--;
            Dropped++;
            return true;
        }
        return false;
    }

    /// <summary>No new frames: what is held is presented through the fade, then cut.</summary>
    public void Drain() => _draining = true;

    /// <summary>The ring emptied and the clock unlocked (the pictures changed size, the page was reset): the ids that were waiting come back for the caller to free. Draining stands as it was.</summary>
    public long[] Clear()
    {
        var ids = WaitingIds().ToArray();
        _head = 0;
        _count = 0;
        _ideal = -1;
        _lastArrival = -1;
        _shownDue = -1;
        _stalled = false;
        return ids;
    }

    /// <summary>The page left the programme: cleared, and nothing more is taken until <see cref="Resume"/>.</summary>
    public long[] Cut()
    {
        var ids = Clear();
        _draining = true;
        return ids;
    }

    /// <summary>Back to taking frames (a page wanted again while it was draining).</summary>
    public void Resume() => _draining = false;

    /// <summary>The words for the status line: "smooth 3 (100 ms)", "low latency", "auto → smooth 2 (67 ms)", "auto · measuring".</summary>
    public string Words
    {
        get
        {
            var smooth = Smoothing;
            var mode = Mode switch
            {
                WebSmoothing.Smooth => "smooth",
                WebSmoothing.LowLatency => "low latency",
                _ => !Measured ? "auto · measuring" : smooth ? "auto → smooth" : "auto → low latency",
            };
            return smooth ? $"{mode} {Depth} ({LatencySeconds * 1000:0} ms)" : mode;
        }
    }
}
