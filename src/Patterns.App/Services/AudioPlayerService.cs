using Avalonia.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Independent audio track player: plays a file regardless of the visual source, to the
/// default device or any set of outputs at once (HDMI screens are audio devices too — so a
/// track can follow all screens, one screen, or a group). One reader+output per device;
/// starts together, drift over very long tracks is accepted for v1. Windows-only playback.
/// </summary>
public sealed class AudioPlayerService : IDisposable
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _timer;
    private readonly List<(IWavePlayer Output, AudioFileReader Reader, MMDevice Device)> _players = new();
    private readonly List<(IWavePlayer Output, AudioFileReader Reader, MMDevice Device)> _sting = new();
    private string _activeKey = "";
    private string _status = "Stopped.";

    public AudioPlayerService(AppServices services)
    {
        _services = services;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(400) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public string Status => _status;

    /// <summary>
    /// Stored key for "the computer's audio output" (the default render device — typically
    /// the jack/interface feeding the venue sound system). A key, not a display name, so it
    /// survives the default device changing.
    /// </summary>
    public const string DefaultDeviceKey = "(computer output)";

    /// <summary>Active output device friendly names (WASAPI). Empty off-Windows.</summary>
    public static IReadOnlyList<string> OutputDevices()
    {
        if (!OperatingSystem.IsWindows()) return Array.Empty<string>();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            var list = new List<string>();
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                using (device)
                {
                    if (!string.IsNullOrWhiteSpace(device.FriendlyName)) list.Add(device.FriendlyName);
                }
            }
            return list;
        }
        catch (Exception ex)
        {
            Log.Warn("Audio device enumeration failed.", ex);
            return Array.Empty<string>();
        }
    }

    /// <summary>The timer body, callable directly (tests drive it without waiting on the clock).</summary>
    public void Poll() => Tick();

    private void Tick()
    {
        var now = DateTime.UtcNow;
        // A 400 ms poll renders a 400 ms fade as one step — a chop, not a fade. While a ramp moves,
        // poll at 50 ms (about eight steps over the default fade) and drop straight back to the
        // idle rate. Stingers is constructed after this service in the composition root; a
        // DispatcherTimer cannot tick before that constructor returns, so the read is safe.
        var want = _services.Stingers is { } stingers && stingers.MusicRamping(now) ? 50d : 400d;
        if (Math.Abs(_timer.Interval.TotalMilliseconds - want) > 0.5) _timer.Interval = TimeSpan.FromMilliseconds(want);

        var cfg = _services.State.AudioPlayer;
        SweepStinger();
        try
        {
            if (!cfg.Playing || string.IsNullOrWhiteSpace(cfg.Path))
            {
                StopAll();
                _status = OperatingSystem.IsWindows()
                    ? string.IsNullOrWhiteSpace(cfg.Path) ? "Choose a track." : "Stopped."
                    : "Audio output is Windows-only.";
                return;
            }

            if (!OperatingSystem.IsWindows())
            {
                _status = "Audio output is Windows-only.";
                return;
            }

            if (!File.Exists(cfg.Path))
            {
                StopAll();
                cfg.Playing = false;
                _status = $"Track not found: {Path.GetFileName(cfg.Path)}";
                return;
            }

            var key = $"{cfg.Path}|{cfg.Loop}|{string.Join(";", cfg.Devices)}";
            if (key != _activeKey)
            {
                StopAll();
                _activeKey = key;
                StartAll(cfg.Path, cfg.Loop, cfg.Devices);
            }

            // Volume applies live (AudioFileReader.Volume is a linear gain; 1.25 ≈ +2 dB). A VOG's
            // sound ducks the track underneath it and a stinger fades it — one rule, shared with
            // break music, so the two music sources move together.
            var volume = (float)(cfg.VolumePct / 100.0 * _services.Stingers.MusicGainAt(now));
            foreach (var (_, reader, _) in _players)
            {
                reader.Volume = volume;
            }

            if (_players.Count > 0)
            {
                var pos = _players[0].Reader.CurrentTime;
                var total = _players[0].Reader.TotalTime;
                var where = cfg.Devices.Count == 0 ? "default output" : $"{_players.Count} output{(_players.Count == 1 ? "" : "s")}";
                _status = $"Playing {Path.GetFileName(cfg.Path)} — {pos:mm\\:ss} / {total:mm\\:ss} on {where}{(cfg.Loop ? " · loop" : "")}";
            }
        }
        catch (Exception ex)
        {
            Log.Error("Audio player failed.", ex);
            _status = $"Audio error: {ex.Message}";
            StopAll();
            cfg.Playing = false;
        }
    }

    private void StartAll(string path, bool loop, IReadOnlyList<string> deviceNames)
    {
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in ResolveDevices(enumerator, deviceNames))
        {
            try
            {
                var reader = new AudioFileReader(path);
                IWaveProvider source = loop ? new LoopingWaveStream(reader) : reader;
                var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 200);
                output.Init(source);
                output.PlaybackStopped += (_, _) => OnPlaybackStopped();
                output.Play();
                _players.Add((output, reader, device)); // device stays alive until StopAll
            }
            catch (Exception ex)
            {
                Log.Warn($"Audio start failed on '{device.FriendlyName}'.", ex);
                device.Dispose();
            }
        }
        Log.Info($"Audio track started on {_players.Count} output(s): {Path.GetFileName(path)}");
    }

    /// <summary>
    /// Stored names → devices. The <see cref="DefaultDeviceKey"/> entry adds the computer's
    /// default output (the venue-PA feed) and can combine with named HDMI screens; the same
    /// physical endpoint never plays twice. Empty selection (or nothing matching) = default.
    /// </summary>
    private static List<MMDevice> ResolveDevices(MMDeviceEnumerator enumerator, IReadOnlyList<string> names)
    {
        var result = new List<MMDevice>();
        var taken = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        void AddDefault()
        {
            var device = enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia);
            if (taken.Add(device.ID)) result.Add(device);
            else device.Dispose();
        }

        if (names.Contains(DefaultDeviceKey)) AddDefault();
        if (names.Count > 0)
        {
            foreach (var device in enumerator.EnumerateAudioEndPoints(DataFlow.Render, DeviceState.Active))
            {
                if (names.Any(n => string.Equals(n, device.FriendlyName, StringComparison.OrdinalIgnoreCase)) &&
                    taken.Add(device.ID))
                {
                    result.Add(device);
                }
                else
                {
                    device.Dispose();
                }
            }
        }
        if (result.Count == 0) AddDefault();
        return result;
    }

    private void OnPlaybackStopped()
    {
        // Natural end without loop: flip the model off so the UI reflects it.
        Dispatcher.UIThread.Post(() =>
        {
            var cfg = _services.State.AudioPlayer;
            if (!cfg.Loop && cfg.Playing && _players.All(p => p.Output.PlaybackState == PlaybackState.Stopped))
            {
                cfg.Playing = false;
            }
        });
    }

    // ---- stingers -----------------------------------------------------------

    /// <summary>An audio stinger is on air (the music track ducks while this is true).</summary>
    public bool StingerPlaying { get; private set; }

    /// <summary>
    /// Fires a one-shot sound on the track's device selection, over whatever else plays.
    /// Independent of the track players — the track keeps rolling (ducked) underneath.
    /// </summary>
    public bool PlayStinger(string path, double volumePct)
    {
        if (!OperatingSystem.IsWindows()) return false;
        StopStinger();
        try
        {
            using var enumerator = new MMDeviceEnumerator();
            foreach (var device in ResolveDevices(enumerator, _services.State.AudioPlayer.Devices))
            {
                try
                {
                    var reader = new AudioFileReader(path) { Volume = (float)(volumePct / 100.0) };
                    var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 200);
                    output.Init(reader);
                    output.Play();
                    _sting.Add((output, reader, device));
                }
                catch (Exception ex)
                {
                    Log.Warn($"Stinger start failed on '{device.FriendlyName}'.", ex);
                    device.Dispose();
                }
            }
        }
        catch (Exception ex)
        {
            Log.Error("Stinger could not start.", ex);
        }
        StingerPlaying = _sting.Count > 0;
        return StingerPlaying;
    }

    public void StopStinger()
    {
        foreach (var (output, reader, device) in _sting)
        {
            try
            {
                output.Stop();
                output.Dispose();
                reader.Dispose();
                device.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn("Stinger stop issue.", ex);
            }
        }
        _sting.Clear();
        StingerPlaying = false;
    }

    /// <summary>Reaps finished stinger players so the duck lifts at the natural end.</summary>
    private void SweepStinger()
    {
        if (_sting.Count > 0 && _sting.All(p => p.Output.PlaybackState == PlaybackState.Stopped))
        {
            StopStinger();
        }
    }

    private void StopAll()
    {
        if (_players.Count == 0) return;
        foreach (var (output, reader, device) in _players)
        {
            try
            {
                output.Stop();
                output.Dispose();
                reader.Dispose();
                device.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn("Audio player stop issue.", ex);
            }
        }
        _players.Clear();
        _activeKey = "";
    }

    public void Dispose()
    {
        _timer.Stop();
        StopStinger();
        StopAll();
    }
}

/// <summary>Loops a wave stream forever by rewinding at the end (NAudio classic pattern).</summary>
public sealed class LoopingWaveStream : WaveStream
{
    private readonly WaveStream _source;

    public LoopingWaveStream(WaveStream source) => _source = source;

    public override WaveFormat WaveFormat => _source.WaveFormat;

    public override long Length => _source.Length;

    public override long Position
    {
        get => _source.Position;
        set => _source.Position = value;
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        var total = 0;
        while (total < count)
        {
            var read = _source.Read(buffer, offset + total, count - total);
            if (read == 0)
            {
                if (_source.Position == 0) break; // empty source — avoid spinning
                _source.Position = 0;
                continue;
            }
            total += read;
        }
        return total;
    }
}
