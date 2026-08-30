using Avalonia.Threading;
using NAudio.Wave;
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

    public int Read(float[] buffer, int offset, int count)
    {
        var step = 2 * Math.PI * _frequency / WaveFormat.SampleRate;
        for (var i = 0; i < count; i += 2)
        {
            _ampLeft += (_targetLeft - _ampLeft) * RampPerSample * 32;
            _ampRight += (_targetRight - _ampRight) * RampPerSample * 32;
            var s = (float)Math.Sin(_phase);
            _phase += step;
            if (_phase > 2 * Math.PI) _phase -= 2 * Math.PI;
            buffer[offset + i] = s * _ampLeft;
            buffer[offset + i + 1] = s * _ampRight;
        }
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

    private void Tick()
    {
        var cfg = _services.State.Tone;
        try
        {
            if (!cfg.Enabled)
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
                _device = new WaveOutEvent { DesiredLatency = 90 };
                _device.Init(_provider);
                _device.Play();
                _identStep = -1;
                Log.Info("Tone generator started.");
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
    }

    public void Dispose()
    {
        _timer.Stop();
        StopDevice();
    }
}
