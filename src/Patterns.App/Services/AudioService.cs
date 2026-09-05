using Avalonia.Threading;
using NAudio.Wave;
using Patterns.Core.Effects;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

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
            buffer[offset + i] = left;
            buffer[offset + i + 1] = right;
            frame++;
        }
        Interlocked.Exchange(ref _framesRendered, frame);
        return count;
    }
}

/// <summary>
/// Soundcheck tone for the audio engineer: continuous sine or channel ident (one pip LEFT,
/// two pips RIGHT, repeating) with a matching on-screen indicator on every sink.
/// Windows audio only; contained everywhere else.
/// </summary>
public sealed class AudioService : IDisposable
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _timer;
    private ToneSampleProvider? _provider;
    private WaveOutEvent? _device;
    private volatile string _status = "Off";
    private int _identStep = -1;
    private DateTime _identNextUtc = DateTime.MinValue;
    private string _indicator = "";

    // Ident pattern: (duration ms, left on, right on, label). One pip L, two pips R, pause.
    private static readonly (int Ms, bool L, bool R, string Label)[] IdentPattern =
    {
        (320, true, false, "LEFT"),
        (280, false, false, "LEFT"),
        (320, false, true, "RIGHT"),
        (200, false, false, "RIGHT"),
        (320, false, true, "RIGHT"),
        (700, false, false, ""),
    };

    public AudioService(AppServices services)
    {
        _services = services;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(60) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public string Status => _status;

    /// <summary>The device's latency, ms: a click scheduled on the stream sounds this much later.</summary>
    private const int DeviceLatencyMs = 90;

    private double _streamStartMaster = double.NaN; // the master instant the first frame of the stream sounds

    /// <summary>
    /// The sync check's clicks: for every mark on the master grid due within the next second, a
    /// click at the stream frame that sounds at that instant — the frame the stream started on,
    /// plus the device's latency. Public for the test; the timer calls it while the check is on.
    /// </summary>
    public void ScheduleSyncClicks(double masterNow)
    {
        if (_provider is null || double.IsNaN(_streamStartMaster)) return;
        var rate = _provider.WaveFormat.SampleRate;
        var mark = SyncMarks.NextMark(masterNow);
        for (var i = 0; i < 2 && mark <= masterNow + 1.5; i++)
        {
            var frame = (long)Math.Round((mark - _streamStartMaster) * rate);
            if (frame >= 0) _provider.ScheduleClick(frame);
            mark += SyncMarks.PeriodSeconds;
        }
    }

    /// <summary>Test seam: pretend the stream started sounding at this master instant.</summary>
    public void SeedForTests(ToneSampleProvider provider, double streamStartMaster)
    {
        _provider = provider;
        _streamStartMaster = streamStartMaster;
    }

    private void Tick()
    {
        var cfg = _services.State.Tone;
        var syncCheck = SyncMarks.Enabled;
        try
        {
            if (!cfg.Enabled && !syncCheck)
            {
                StopDevice();
                SetIndicator("");
                _status = OperatingSystem.IsWindows() ? "Off" : "Audio output is Windows-only.";
                return;
            }

            if (!OperatingSystem.IsWindows())
            {
                _status = "Audio output is Windows-only.";
                SetIndicator("");
                return;
            }

            if (_device is null)
            {
                _provider = new ToneSampleProvider();
                _device = new WaveOutEvent { DesiredLatency = DeviceLatencyMs };
                _device.Init(_provider);
                _device.Play();
                _streamStartMaster = ShowClock.Seconds + DeviceLatencyMs / 1000.0;
                _identStep = -1;
                Log.Info("Tone generator started.");
            }

            if (syncCheck) ScheduleSyncClicks(ShowClock.Seconds);

            if (!cfg.Enabled)
            {
                // Only the sync check wants the device: silent but for the clicks.
                _provider!.SetTargets(0, 0);
                SetIndicator("");
                _status = "Sync check: a click on every flash.";
                return;
            }

            _provider!.Frequency = (float)cfg.FrequencyHz;
            var amp = ToneSampleProvider.DbToAmplitude(cfg.LevelDb);

            if (cfg.Mode == ToneMode.Continuous)
            {
                var left = cfg.Channels is ToneChannels.Both or ToneChannels.Left ? amp : 0;
                var right = cfg.Channels is ToneChannels.Both or ToneChannels.Right ? amp : 0;
                _provider.SetTargets(left, right);
                _identStep = -1;
                SetIndicator(cfg.Channels switch
                {
                    ToneChannels.Left => "LEFT",
                    ToneChannels.Right => "RIGHT",
                    _ => "L+R",
                });
            }
            else
            {
                var utcNow = DateTime.UtcNow;
                if (_identStep < 0 || utcNow >= _identNextUtc)
                {
                    _identStep = (_identStep + 1) % IdentPattern.Length;
                    var step = IdentPattern[_identStep];
                    _identNextUtc = utcNow.AddMilliseconds(step.Ms);
                    _provider.SetTargets(step.L ? amp : 0, step.R ? amp : 0);
                    SetIndicator(step.Label);
                }
            }

            _status = $"Sending {cfg.FrequencyHz:0} Hz @ {cfg.LevelDb:0} dBFS ({(cfg.Mode == ToneMode.Continuous ? "continuous" : "channel ident")})";
        }
        catch (Exception ex)
        {
            Log.Error("Tone generator failed.", ex);
            _status = $"Audio error: {ex.Message}";
            StopDevice();
            SetIndicator("");
        }
    }

    private void SetIndicator(string value)
    {
        if (_indicator == value) return;
        _indicator = value;
        _services.Bus.ToneIndicator = value;
        _services.PublishRuntime();
    }

    private void StopDevice()
    {
        if (_device is null) return;
        try
        {
            _device.Stop();
            _device.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Tone device stop failed.", ex);
        }
        _device = null;
        _provider = null;
        _streamStartMaster = double.NaN;
    }

    public void Dispose()
    {
        _timer.Stop();
        StopDevice();
    }
}
