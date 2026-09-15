using NAudio.Wave;

namespace Patterns.Audio;

/// <summary>
/// Click-free stereo sine generator: per-channel target amplitudes are approached with a
/// short exponential ramp so pips and channel switches never pop. Pure DSP — unit tested.
/// </summary>
public sealed class ToneSampleProvider : ISampleProvider
{
    private const float RampPerSample = 0.0015f; // ~10 ms attack/release at 48 kHz

    private double _phase;
    private volatile float _frequency = 1000;
    private volatile float _targetLeft;
    private volatile float _targetRight;
    private float _ampLeft;
    private float _ampRight;

    public WaveFormat WaveFormat { get; } = WaveFormat.CreateIeeeFloatWaveFormat(48000, 2);

    public float Frequency
    {
        get => _frequency;
        set => _frequency = Math.Clamp(value, 20, 20000);
    }

    public void SetTargets(float left, float right)
    {
        _targetLeft = Math.Clamp(left, 0, 1);
        _targetRight = Math.Clamp(right, 0, 1);
    }

    public static float DbToAmplitude(double db) => (float)Math.Pow(10, Math.Clamp(db, -60, 0) / 20.0);

    // ---- the sync check's clicks: short 1 kHz bursts at scheduled frames of this stream ----------

    /// <summary>A click's length in frames (5 ms at 48 kHz).</summary>
    public const int ClickFrames = 240;

    private const float ClickAmplitude = 0.5f;
    private readonly object _clickGate = new();
    private readonly Queue<long> _clicks = new();
    private long _framesRendered;

    /// <summary>Frames this stream has rendered so far — the timeline clicks are scheduled on.</summary>
    public long FramesRendered => Interlocked.Read(ref _framesRendered);

    /// <summary>Schedules a click starting at a frame of this stream; one already past is dropped.</summary>
    public void ScheduleClick(long atFrame)
    {
        lock (_clickGate)
        {
            if (atFrame < FramesRendered) return;
            if (_clicks.Contains(atFrame)) return;
            _clicks.Enqueue(atFrame);
        }
    }

    public int PendingClicks
    {
        get
        {
            lock (_clickGate) return _clicks.Count;
        }
    }

    /// <summary>The tone as the NDI lanes read it, when a graph asked for it.</summary>
    public AudioRing? Tap { get; set; }

    /// <summary>The matrix's gain on the tone's destination (unity while the matrix is off), landed with the same ramp as the amplitude.</summary>
    public volatile float RouteGain = 1f;

    public int Read(float[] buffer, int offset, int count)
    {
        var step = 2 * Math.PI * _frequency / WaveFormat.SampleRate;
        var clickStep = 2 * Math.PI * 1000 / WaveFormat.SampleRate;
        long clickStart = -1;
        lock (_clickGate)
        {
            while (_clicks.Count > 0 && _clicks.Peek() + ClickFrames < _framesRendered) _clicks.Dequeue(); // missed entirely
            if (_clicks.Count > 0) clickStart = _clicks.Peek();
        }
        var frame = _framesRendered;
        for (var i = 0; i < count; i += 2)
        {
            _ampLeft += (_targetLeft - _ampLeft) * RampPerSample * 32;
            _ampRight += (_targetRight - _ampRight) * RampPerSample * 32;
            var s = (float)Math.Sin(_phase);
            _phase += step;
            if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;
            var left = s * _ampLeft;
            var right = s * _ampRight;
            if (clickStart >= 0 && frame >= clickStart && frame < clickStart + ClickFrames)
            {
                var k = frame - clickStart;
                var env = (float)Math.Sin(Math.PI * k / ClickFrames); // a soft burst, no pop
                var click = (float)Math.Sin(clickStep * k) * ClickAmplitude * env;
                left += click;
                right += click;
                if (k == ClickFrames - 1)
                {
                    lock (_clickGate)
                    {
                        if (_clicks.Count > 0 && _clicks.Peek() == clickStart) _clicks.Dequeue();
                        clickStart = _clicks.Count > 0 ? _clicks.Peek() : -1;
                    }
                }
            }
            buffer[offset + i] = left * RouteGain;
            buffer[offset + i + 1] = right * RouteGain;
            frame++;
        }
        Interlocked.Exchange(ref _framesRendered, frame);
        Tap?.Write(buffer.AsSpan(offset, count));
        return count;
    }
}
