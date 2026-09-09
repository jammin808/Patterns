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
/// default output) or an input — a microphone, a line, a USB capture card or interface channel —
/// whichever the pattern on screen asks for, on a one-second reconcile like the other services.
/// The capture thread turns each half-window of samples into levels and publishes them on
/// <see cref="AudioLevels"/>; every sink reads them on its next frame. Windows-only, like every
/// WASAPI path: elsewhere it says so and publishes nothing. Never opens a device nobody asked for.
///
/// An input that is not there yet is not a dead end: the reconcile keeps looking, and a device
/// that is unplugged mid-show is picked up again when it comes back, because a rig is patched in
/// the order the crew get to it and the desk is usually running first.
/// </summary>
public sealed class AudioAnalyserService : IDisposable
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _timer;
    private readonly float[] _ring = new float[Spectrum.Window];
    private readonly LevelSmoother _smoother = new();
    private volatile IWaveIn? _capture;
    private IWaveIn? _spent;
    private int _fill;
    private string _key = "";
    private long _retryAt;
    private DateTime _lastUtc;
    private volatile string _status = "Off.";

    /// <summary>How long a missing or dropped input waits before the reconcile tries it again.</summary>
    public static readonly TimeSpan RetryEvery = TimeSpan.FromSeconds(5);

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

    /// <summary>The first pattern on the desk or on air that listens, and to what.</summary>
    public (AudioSourceKind Source, string Device) Wanted()
    {
        foreach (var state in new[] { _services.State, _services.Bus.Current?.State })
        {
            if (state is null) continue;
            foreach (var pattern in Patterns(state))
            {
                var asked = Asked(pattern);
                if (asked.Source != AudioSourceKind.None) return asked;
            }
        }
        return (AudioSourceKind.None, "");
    }

    /// <summary>
    /// What one pattern asks to listen to. Only the kinds that answer to sound, and only while
    /// they are the kind on screen — a fractal's settings left behind on a grid open nothing.
    /// </summary>
    public static (AudioSourceKind Source, string Device) Asked(PatternConfig pattern) => pattern.Kind switch
    {
        PatternKind.Fractal => (pattern.Fractal.AudioSource, pattern.Fractal.AudioDevice),
        PatternKind.Reactive => (pattern.Reactive.AudioSource, pattern.Reactive.AudioDevice),
        _ => (AudioSourceKind.None, ""),
    };

    private static IEnumerable<PatternConfig> Patterns(ShowState state)
    {
        yield return state.Pattern;
        foreach (var a in state.Independent) yield return a.Pattern;
    }

    private void Reconcile()
    {
        var (source, device) = Wanted();
        var key = $"{source}|{device}";
        var changed = key != _key;
        _key = key;
        if (changed) Stop();
        Bury();
        if (source == AudioSourceKind.None)
        {
            if (!changed) return;
            _status = "Off.";
            AudioLevels.Clear();
            return;
        }
        if (!OperatingSystem.IsWindows())
        {
            if (changed) _status = "Sound-reactive effects listen on Windows only.";
            return;
        }
        // Already listening to what the show asked for; nothing to do until the ask changes or the
        // capture drops, which clears the field from the capture thread.
        if (_capture is not null) return;
        // A device that is not here yet, or one that went away: the ask has not changed, so this is
        // a retry rather than a fresh start. Off the wall clock, so a show-clock change cannot
        // stall it or make it hammer the endpoint list.
        var now = Environment.TickCount64;
        if (!changed && now < _retryAt) return;
        _retryAt = now + (long)RetryEvery.TotalMilliseconds;
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
            var found = AudioInput.WantsDefault(device) ? Default(enumerator) : Named(enumerator, device);
            if (found is null) return;
            capture = new WasapiCapture(found);
            listening = $"Listening to {found.FriendlyName}{Note(found.FriendlyName, device)}.";
        }
        var format = capture.WaveFormat;
        capture.DataAvailable += (_, e) => Feed(e.Buffer, e.BytesRecorded, format);
        capture.RecordingStopped += (_, e) =>
        {
            // Our own Stop() already took the capture off the field; anything else is the device
            // going away under us, and the next reconcile is the one that goes looking for it.
            if (!ReferenceEquals(_capture, capture)) return;
            _capture = null;
            Interlocked.Exchange(ref _spent, capture);
            if (e.Exception is not null) Log.Warn("Audio capture stopped.", e.Exception);
            var why = e.Exception is null ? "the input went away" : e.Exception.Message;
            _status = $"Listening stopped ({why}) — looking for the input again.";
        };
        capture.StartRecording();
        _capture = capture;
        _fill = 0;
        _lastUtc = default;
        _status = listening;
    }

    /// <summary>This machine's own input, whatever Windows currently calls it; null (with a reason on the status) when it has none.</summary>
    [SupportedOSPlatform("windows")]
    private MMDevice? Default(MMDeviceEnumerator enumerator)
    {
        if (enumerator.HasDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia))
        {
            return enumerator.GetDefaultAudioEndpoint(DataFlow.Capture, Role.Multimedia);
        }
        _status = "This machine has no input to listen to.";
        return null;
    }

    /// <summary>The input the show named, matched against what is plugged in now; null (with a reason) when it is not.</summary>
    [SupportedOSPlatform("windows")]
    private MMDevice? Named(MMDeviceEnumerator enumerator, string device)
    {
        var endpoints = new List<MMDevice>();
        foreach (var d in enumerator.EnumerateAudioEndPoints(DataFlow.Capture, DeviceState.Active)) endpoints.Add(d);
        var at = AudioInput.IndexOf(endpoints.Select(d => d.FriendlyName).ToList(), device);
        MMDevice? found = null;
        for (var i = 0; i < endpoints.Count; i++)
        {
            if (i == at) found = endpoints[i];
            else endpoints[i].Dispose();
        }
        if (found is null)
        {
            _status = $"'{device}' is not an input on this machine right now — looking again every {(int)RetryEvery.TotalSeconds} seconds.";
        }
        return found;
    }

    /// <summary>Says so when the input that answered is not the one the show wrote down, so a moved USB card is not a mystery.</summary>
    private static string Note(string opened, string device)
    {
        if (AudioInput.WantsDefault(device)) return " — this machine's own input";
        return string.Equals(opened.Trim(), device.Trim(), StringComparison.OrdinalIgnoreCase)
            ? ""
            : $" — the show asked for '{device.Trim()}', which is the same input on another socket";
    }

    private void Stop()
    {
        Bury();
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

    /// <summary>Disposes a capture the device dropped, on the desk's thread rather than the capture's own.</summary>
    private void Bury()
    {
        var spent = Interlocked.Exchange(ref _spent, null);
        if (spent is null) return;
        try
        {
            spent.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Audio capture close issue.", ex);
        }
    }

    /// <summary>
    /// The capture callback's body — public so a test can push a buffer through without a device.
    /// Mixes to mono, fills the analysis window, and publishes smoothed levels every half window.
    /// Three sample widths, because that is what turns up: shared-mode WASAPI hands over 32-bit
    /// float, a plain PCM device 16-bit, and a capture card or interface asked for its own format
    /// 24-bit. Anything else is left alone rather than read as noise.
    /// </summary>
    public void Feed(byte[] buffer, int bytes, WaveFormat format)
    {
        var channels = Math.Max(1, format.Channels);
        var bps = format.BitsPerSample / 8;
        if (bps is not (2 or 3 or 4)) return;
        var frameBytes = bps * channels;
        var frames = Math.Min(bytes, buffer.Length) / frameBytes;
        var inv = 1f / channels;
        // The buffer read as the samples it holds (the machine's own byte order, as the device
        // writes it) rather than a BitConverter call per sample per channel.
        if (bps == 4)
        {
            var floats = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, float>(buffer.AsSpan(0, frames * frameBytes));
            for (var f = 0; f < frames; f++)
            {
                var sum = 0f;
                var at = f * channels;
                for (var ch = 0; ch < channels; ch++) sum += floats[at + ch];
                Push(sum * inv, format.SampleRate);
            }
        }
        else if (bps == 3)
        {
            // Three bytes a sample, little end first, the top byte carrying the sign.
            for (var f = 0; f < frames; f++)
            {
                var sum = 0f;
                var at = f * frameBytes;
                for (var ch = 0; ch < channels; ch++)
                {
                    var o = at + ch * 3;
                    var value = buffer[o] | (buffer[o + 1] << 8) | ((sbyte)buffer[o + 2] << 16);
                    sum += value / 8388608f;
                }
                Push(sum * inv, format.SampleRate);
            }
        }
        else
        {
            var shorts = System.Runtime.InteropServices.MemoryMarshal.Cast<byte, short>(buffer.AsSpan(0, frames * frameBytes));
            for (var f = 0; f < frames; f++)
            {
                var sum = 0f;
                var at = f * channels;
                for (var ch = 0; ch < channels; ch++) sum += shorts[at + ch] / 32768f;
                Push(sum * inv, format.SampleRate);
            }
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
