using System.Runtime.Versioning;
using Avalonia.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Patterns.Core.Effects;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Listens for the sound-reactive effects: this computer's own sound (WASAPI loopback of the
/// default output) or an input (a microphone, a line, an interface), whichever the show's
/// fractal asks for, on a one-second reconcile like the other services. The capture thread
/// turns each half-window of samples into levels and publishes them on <see cref="AudioLevels"/>;
/// every sink reads them on its next frame. Windows-only, like every WASAPI path: elsewhere it
/// says so and publishes nothing. Never opens a device nobody asked for.
/// </summary>
public sealed class AudioAnalyserService : IDisposable
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _timer;
    private readonly float[] _ring = new float[Spectrum.Window];
    private readonly LevelSmoother _smoother = new();
    private IWaveIn? _capture;
    private int _fill;
    private string _key = "";
    private DateTime _lastUtc;
    private volatile string _status = "Off.";

    public AudioAnalyserService(AppServices services)
    {
        _services = services;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timer.Tick += (_, _) => Poll();
        _timer.Start();
    }

    /// <summary>What the analyser is doing, for the Pattern page: "Off.", "Listening to …", or why it cannot.</summary>
    public string Status => _status;

    /// <summary>True while a capture is open.</summary>
    public bool Listening => _capture is not null;

    /// <summary>The clock the levels are stamped with; a test holds it.</summary>
    public Func<DateTime> NowUtc { get; set; } = () => ShowClock.UtcNow;

    /// <summary>The timer body, callable directly.</summary>
    public void Poll()
    {
        try
        {
            Reconcile();
        }
        catch (Exception ex)
        {
            Log.Warn("Audio analyser poll failed.", ex);
            _status = $"Sound analysis could not run: {ex.Message}";
        }
    }

    /// <summary>The first fractal on the desk or on air that listens, and to what.</summary>
    public (AudioSourceKind Source, string Device) Wanted()
    {
        foreach (var state in new[] { _services.State, _services.Bus.Current?.State })
        {
            if (state is null) continue;
            foreach (var pattern in Patterns(state))
            {
                if (pattern.Kind == PatternKind.Fractal && pattern.Fractal.AudioSource != AudioSourceKind.None)
                {
                    return (pattern.Fractal.AudioSource, pattern.Fractal.AudioDevice);
                }
            }
        }
        return (AudioSourceKind.None, "");
    }

    private static IEnumerable<PatternConfig> Patterns(ShowState state)
    {
        yield return state.Pattern;
        foreach (var a in state.Independent) yield return a.Pattern;
    }

    private void Reconcile()
    {
        var (source, device) = Wanted();
        var key = $"{source}|{device}";
        if (key == _key) return;
        _key = key;
        Stop();
        if (source == AudioSourceKind.None)
        {
            _status = "Off.";
            AudioLevels.Clear();
            return;
        }
        if (!OperatingSystem.IsWindows())
        {
            _status = "Sound-reactive effects listen on Windows only.";
            return;
        }
        try
        {
            Start(source, device);
        }
        catch (Exception ex)
        {
            Log.Warn("Audio capture could not start.", ex);
            _status = $"Could not listen: {ex.Message}";
        }
    }

    [SupportedOSPlatform("windows")]
    private void Start(AudioSourceKind source, string device)
    {
        IWaveIn capture;
        string listening;
        if (source == AudioSourceKind.Internal)
        {
            capture = new WasapiLoopbackCapture();
            listening = "Listening to this computer's sound.";
        }
        else
        {
            using var enumerator = new MMDeviceEnumerator();
            MMDevice? found = null;
            foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
            {
                if (found is null && string.Equals(d.FriendlyName, device, StringComparison.OrdinalIgnoreCase)) found = d;
                else d.Dispose();
            }
            if (found is null)
            {
                _status = device.Length == 0
                    ? "Choose an input to listen to."
                    : $"'{device}' is not an input on this machine right now.";
                return;
            }
            capture = new WasapiCapture(found);
            listening = $"Listening to {device}.";
        }
        var format = capture.WaveFormat;
        capture.DataAvailable += (_, e) => Feed(e.Buffer, e.BytesRecorded, format);
        capture.RecordingStopped += (_, e) =>
        {
            if (e.Exception is not null)
            {
                Log.Warn("Audio capture stopped.", e.Exception);
                _status = $"Listening stopped: {e.Exception.Message}";
            }
        };
        capture.StartRecording();
        _capture = capture;
        _fill = 0;
        _lastUtc = default;
        _status = listening;
    }

    private void Stop()
    {
        var capture = _capture;
        _capture = null;
        if (capture is null) return;
        try
        {
            capture.StopRecording();
            capture.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Audio capture close issue.", ex);
        }
    }

    /// <summary>
    /// The capture callback's body — public so a test can push a buffer through without a device.
    /// Mixes to mono, fills the analysis window, and publishes smoothed levels every half window.
    /// </summary>
    public void Feed(byte[] buffer, int bytes, WaveFormat format)
    {
        var channels = Math.Max(1, format.Channels);
        var bps = format.BitsPerSample / 8;
        if (bps is not (2 or 4)) return;
        var frameBytes = bps * channels;
        var frames = bytes / frameBytes;
        for (var f = 0; f < frames; f++)
        {
            var sum = 0f;
            for (var ch = 0; ch < channels; ch++)
            {
                var i = f * frameBytes + ch * bps;
                sum += bps == 4 ? BitConverter.ToSingle(buffer, i) : BitConverter.ToInt16(buffer, i) / 32768f;
            }
            Push(sum / channels, format.SampleRate);
        }
    }

    private void Push(float sample, int sampleRate)
    {
        _ring[_fill++] = sample;
        if (_fill < Spectrum.Window) return;
        var now = NowUtc();
        // The hop is a known number of samples on the device's clock: the smoother's time comes
        // from the samples, not from the wall, so a stalled callback cannot stretch a level.
        var dt = Math.Clamp((_lastUtc == default ? Spectrum.Window : Spectrum.Window / 2) / (double)Math.Max(1, sampleRate), 0.001, 0.5);
        _lastUtc = now;
        var raw = Spectrum.Analyse(_ring, sampleRate);
        AudioLevels.Publish(_smoother.Follow(raw, dt), now);
        // A half-window hop: the newest half stays for the next analysis.
        Array.Copy(_ring, Spectrum.Window / 2, _ring, 0, Spectrum.Window / 2);
        _fill = Spectrum.Window / 2;
    }

    /// <summary>Active input device names (WASAPI). Empty off Windows.</summary>
    public static IReadOnlyList<string> CaptureDevices()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<string>();
        try
        {
            return ListCaptureDevices();
        }
        catch (Exception ex)
        {
            Log.Warn("Audio input enumeration failed.", ex);
            return Array.Empty<string>();
        }
    }

    [SupportedOSPlatform("windows")]
    private static List<string> ListCaptureDevices()
    {
        using var enumerator = new MMDeviceEnumerator();
        var list = new List<string>();
        foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active))
        {
            using (device)
            {
                if (!string.IsNullOrWhiteSpace(device.FriendlyName)) list.Add(device.FriendlyName);
            }
        }
        return list;
    }

    public void Dispose()
    {
        _timer.Stop();
        Stop();
    }
}
