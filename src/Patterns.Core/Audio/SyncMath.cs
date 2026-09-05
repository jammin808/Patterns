namespace Patterns.Core.Audio;

// ---------------------------------------------------------------------------------------------
// The audio side of the master clock. Every audio device runs on a clock of its own — a sound
// card's crystal, a display's HDMI clock, libVLC's internal clock — and none of them is the
// show's. Over two hours a crystal fifty parts per million off the master is 360 ms out. The
// pieces here are pure: an estimator that measures a device's rate against the master, a
// controller that turns the measurement and the residual lag into a resampling ratio, the
// asynchronous sample-rate converter that applies it, and a delay line for the lip-sync offset.
// ---------------------------------------------------------------------------------------------

/// <summary>
/// A device's clock against the master, in parts per million: from readings of how many frames
/// the device has played and what the master clock said at that moment. Cumulative from the
/// first reading, so it only gets more precise, with a short window before it says anything.
/// </summary>
public sealed class DriftEstimator
{
    public const double MinWindowSeconds = 5;

    private readonly int _rate;
    private double _t0 = double.NaN;
    private long _f0;

    public DriftEstimator(int sampleRate) => _rate = Math.Max(1, sampleRate);

    /// <summary>The device's rate error: +50 means it plays 50 ppm faster than the master.</summary>
    public double Ppm { get; private set; }

    /// <summary>Enough of a window has passed for <see cref="Ppm"/> to mean something.</summary>
    public bool Confident { get; private set; }

    /// <summary>One reading: frames the device has played since it started, and the master's seconds at that instant.</summary>
    public void Observe(long framesPlayed, double masterSeconds)
    {
        if (double.IsNaN(_t0))
        {
            _t0 = masterSeconds;
            _f0 = framesPlayed;
            return;
        }
        var elapsed = masterSeconds - _t0;
        if (elapsed < MinWindowSeconds) return;
        var expected = elapsed * _rate;
        var actual = framesPlayed - _f0;
        var ppm = (actual / expected - 1) * 1e6;
        // Cumulative, so the noise of one reading shrinks with the window; a glitch cannot swing it.
        Ppm = Confident ? Ppm + (ppm - Ppm) * 0.3 : ppm;
        Confident = true;
    }

    public void Reset()
    {
        _t0 = double.NaN;
        _f0 = 0;
        Ppm = 0;
        Confident = false;
    }
}

/// <summary>
/// The lock: turns the measured drift (feed-forward) and the residual lag of the source against
/// the master (a small proportional-integral term) into the resampling ratio the converter
/// runs at — output frames per input frame. Bounded to ±<see cref="MaxCorrectionPpm"/>: far
/// more than any clock needs, far less than anyone can hear.
/// </summary>
public sealed class SyncController
{
    public const double MaxCorrectionPpm = 2000;

    /// <summary>Ratio change per second of lag: one millisecond of lag pulls twenty parts per million.</summary>
    public double Kp { get; init; } = 0.02;

    public double Ki { get; init; } = 0.002;

    private double _integral;

    /// <summary>Output frames per input frame; 1 = untouched.</summary>
    public double Ratio { get; private set; } = 1;

    /// <summary>The correction in force, parts per million (positive = the source is being slowed to hold the master).</summary>
    public double CorrectionPpm => (Ratio - 1) * 1e6;

    /// <summary>
    /// <paramref name="lagSeconds"/> is how far the source has run ahead of the master (positive)
    /// or behind it (negative); <paramref name="feedForwardPpm"/> is the device's measured drift.
    /// </summary>
    public double Update(double lagSeconds, double dtSeconds, double feedForwardPpm = 0)
    {
        _integral = Math.Clamp(_integral + lagSeconds * Math.Max(0, dtSeconds), -1, 1);
        var correction = feedForwardPpm * 1e-6 + Kp * lagSeconds + Ki * _integral;
        correction = Math.Clamp(correction, -MaxCorrectionPpm * 1e-6, MaxCorrectionPpm * 1e-6);
        Ratio = 1 + correction;
        return Ratio;
    }

    public void Reset()
    {
        _integral = 0;
        Ratio = 1;
    }
}

/// <summary>
/// The asynchronous sample-rate converter: pulls interleaved frames from a source and produces
/// output frames at a ratio that can move at any time. Four-point cubic (Catmull-Rom)
/// interpolation, so a ratio of exactly one is the input itself, a hair late; a ratio a few
/// hundred parts per million off is inaudible. Pure, allocation-free once running.
/// </summary>
public sealed class SampleRateConverter
{
    private readonly int _channels;
    private readonly float[] _history;        // the last four input frames, interleaved: h[-2] h[-1] h[0] h[1]
    private readonly float[] _scratch;
    private int _scratchFrames;
    private int _scratchPos;
    private double _frac;                     // the output's position between h[0] and h[1]
    private int _primed;                      // input frames pulled into the history so far
    private double _ratio = 1;
    private bool _dry;

    public SampleRateConverter(int channels, int blockFrames = 1024)
    {
        _channels = Math.Max(1, channels);
        _history = new float[4 * _channels];
        _scratch = new float[Math.Max(16, blockFrames) * _channels];
    }

    /// <summary>Output frames per input frame. Moves at once; the next output frame already runs at the new ratio.</summary>
    public double Ratio
    {
        get => _ratio;
        set => _ratio = Math.Clamp(value, 0.5, 2);
    }

    public long InputFramesConsumed { get; private set; }

    public long OutputFramesProduced { get; private set; }

    /// <summary>The source ran dry: every input frame has been read and the last ones interpolated out.</summary>
    public bool Ended => _dry && _scratchPos >= _scratchFrames;

    /// <summary>
    /// Fills <paramref name="output"/> (interleaved, <paramref name="frames"/> frames) from the
    /// source; returns the frames produced — fewer only when the source has ended.
    /// <paramref name="readInput"/> fills a buffer with up to the requested frames and returns how many.
    /// </summary>
    public int Read(float[] output, int offset, int frames, Func<float[], int, int, int> readInput)
    {
        var produced = 0;
        var ch = _channels;
        var step = 1.0 / _ratio;
        while (produced < frames)
        {
            // Advance the history until the output position sits between h[0] and h[1].
            while (_frac >= 1 || _primed < 4)
            {
                if (!NextInputFrame(readInput)) return produced;
                if (_primed < 4) _primed++;
                if (_primed < 4) continue;
                if (_frac >= 1) _frac -= 1;
                else break;
            }
            var t = (float)_frac;
            var t2 = t * t;
            var t3 = t2 * t;
            for (var c = 0; c < ch; c++)
            {
                var p0 = _history[c];
                var p1 = _history[ch + c];
                var p2 = _history[2 * ch + c];
                var p3 = _history[3 * ch + c];
                output[offset + produced * ch + c] = 0.5f * (2 * p1 + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 + (-p0 + 3 * p1 - 3 * p2 + p3) * t3);
            }
            produced++;
            OutputFramesProduced++;
            _frac += step;
        }
        return produced;
    }

    private bool NextInputFrame(Func<float[], int, int, int> readInput)
    {
        if (_scratchPos >= _scratchFrames)
        {
            if (_dry) return false;
            var want = _scratch.Length / _channels;
            var got = readInput(_scratch, 0, want);
            if (got <= 0)
            {
                _dry = true;
                return false;
            }
            _scratchFrames = got;
            _scratchPos = 0;
        }
        var ch = _channels;
        Array.Copy(_history, ch, _history, 0, 3 * ch);
        Array.Copy(_scratch, _scratchPos * ch, _history, 3 * ch, ch);
        _scratchPos++;
        InputFramesConsumed++;
        return true;
    }
}

/// <summary>A fixed delay on interleaved samples — the lip-sync offset. In place, allocation-free.</summary>
public sealed class DelayBuffer
{
    private readonly float[] _ring;
    private int _index;

    public DelayBuffer(int delayFrames, int channels)
    {
        DelayFrames = Math.Max(0, delayFrames);
        Channels = Math.Max(1, channels);
        _ring = new float[Math.Max(1, DelayFrames * Channels)];
    }

    public int DelayFrames { get; }

    public int Channels { get; }

    /// <summary>Delays the samples in place: what comes out now went in <see cref="DelayFrames"/> frames ago (silence at first).</summary>
    public void Process(float[] buffer, int offset, int count)
    {
        if (DelayFrames == 0) return;
        var n = _ring.Length;
        for (var i = 0; i < count; i++)
        {
            var incoming = buffer[offset + i];
            buffer[offset + i] = _ring[_index];
            _ring[_index] = incoming;
            if (++_index >= n) _index = 0;
        }
    }

    /// <summary>Frames of delay for a time at a rate.</summary>
    public static int FramesFor(int ms, int sampleRate) => (int)Math.Round(Math.Max(0, ms) / 1000.0 * Math.Max(1, sampleRate));
}
