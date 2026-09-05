using Avalonia.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Patterns.Core.Audio;
using Patterns.Core.Model;
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
    private readonly List<Player> _players = new();
    private string _activeKey = "";
    private string _status = "Stopped.";

    /// <summary>One output of the track: the device, its chain, and its lock to the master clock.</summary>
    private sealed class Player
    {
        public required IWavePlayer Output { get; init; }
        public required AudioFileReader Reader { get; init; }
        public required MMDevice Device { get; init; }
        public required AsrcSampleProvider Asrc { get; init; }
        public required string Key { get; init; }
        public int DelayMs { get; init; }
        public DriftEstimator Drift { get; } = new(48000);
        public SyncController Lock { get; } = new();
        public double AnchorMaster = double.NaN;
        public double AnchorSource;
        public double LastMaster;
        public double LagMs;
    }

    public AudioPlayerService(AppServices services)
    {
        _services = services;
        VoiceFactory = (path, volumePct) => OperatingSystem.IsWindows()
            ? WasapiStingerVoice.Open(path, volumePct, _services.State.AudioPlayer.Devices, _services.State.AudioPlayer.DelayFor)
            : null;
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
        var now = ShowClock.UtcNow; // the master clock: every ramp and duck reads the same time base as the pictures
        // A 400 ms poll renders a 400 ms fade as one step — a chop, not a fade. While a ramp moves,
        // poll at 50 ms (about eight steps over the default fade) and drop straight back to the
        // idle rate. Stingers is constructed after this service in the composition root; a
        // DispatcherTimer cannot tick before that constructor returns, so the read is safe.
        var want = _services.Stingers is { } stingers && stingers.MusicRamping(now) ? 50d : 400d;
        if (Math.Abs(_timer.Interval.TotalMilliseconds - want) > 0.5) _timer.Interval = TimeSpan.FromMilliseconds(want);

        var cfg = _services.State.AudioPlayer;
        SweepStinger();
        ApplyGains(now);
        _services.Video.ApplyAudioDelay(cfg.VideoAudioDelayMs); // every clip's soundtrack follows the lip-sync offset
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

            var delays = string.Join(";", cfg.OutputDelays.Select(d => $"{d.Device}={d.DelayMs}"));
            var key = $"{cfg.Path}|{cfg.Loop}|{string.Join(";", cfg.Devices)}|{delays}";
            if (key != _activeKey)
            {
                StopAll();
                _activeKey = key;
                StartAll(cfg.Path, cfg.Loop, cfg.Devices, cfg.DelayFor);
            }

            // Volume applies live (AudioFileReader.Volume is a linear gain; 1.25 ≈ +2 dB). A VOG's
            // sound ducks the track underneath it and a stinger fades it — one rule, shared with
            // break music, so the two music sources move together.
            var volume = (float)(cfg.VolumePct / 100.0 * _services.Stingers.MusicGainAt(now));
            foreach (var p in _players)
            {
                p.Reader.Volume = volume;
            }
            ObserveSync(cfg.SyncLock);

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

    private void StartAll(string path, bool loop, IReadOnlyList<string> deviceNames, Func<string, int> delayFor)
    {
        using var enumerator = new MMDeviceEnumerator();
        foreach (var device in ResolveDevices(enumerator, deviceNames))
        {
            try
            {
                var reader = new AudioFileReader(path);
                IWaveProvider source = loop ? new LoopingWaveStream(reader) : reader;
                // The chain: the file → the sample-rate converter that locks this device to the
                // master clock → its lip-sync delay → the device.
                var asrc = new AsrcSampleProvider(source.ToSampleProvider());
                var key = DelayKeyFor(device, deviceNames);
                var delayMs = delayFor(key);
                ISampleProvider tail = delayMs > 0 ? new DelaySampleProvider(asrc, delayMs) : asrc;
                var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 200);
                output.Init(new SampleToWaveProvider(tail));
                output.PlaybackStopped += (_, _) => OnPlaybackStopped();
                output.Play();
                _players.Add(new Player { Output = output, Reader = reader, Device = device, Asrc = asrc, Key = key, DelayMs = delayMs }); // device stays alive until StopAll
            }
            catch (Exception ex)
            {
                Log.Warn($"Audio start failed on '{device.FriendlyName}'.", ex);
                device.Dispose();
            }
        }
        Log.Info($"Audio track started on {_players.Count} output(s): {Path.GetFileName(path)}");
    }

    /// <summary>The delay-table key of a resolved device: its name when it was chosen by name, else the computer-output key.</summary>
    public static string DelayKeyFor(MMDevice device, IReadOnlyList<string> chosenNames)
    {
        try
        {
            var name = device.FriendlyName;
            if (chosenNames.Any(n => string.Equals(n, name, StringComparison.OrdinalIgnoreCase))) return name;
        }
        catch
        {
            // A device that will not say its name is the default one.
        }
        return DefaultDeviceKey;
    }

    // ---- the lock to the master clock ---------------------------------------------------------

    /// <summary>
    /// One reading per poll for every output: what the device has played (its clock) against the
    /// master, into the drift estimator; and how far the source has run against the master, into
    /// the controller, whose ratio the converter then runs at. With the lock off the converters
    /// sit at one and the devices free-run, measured but uncorrected.
    /// </summary>
    private void ObserveSync(bool lockOn)
    {
        var master = ShowClock.Seconds;
        foreach (var p in _players)
        {
            long played;
            int deviceRate;
            try
            {
                if (p.Output is not IWavePosition position) continue;
                var format = p.Output.OutputWaveFormat;
                deviceRate = Math.Max(1, format.SampleRate);
                played = position.GetPosition() / Math.Max(1, format.BlockAlign);
            }
            catch
            {
                continue; // a device that will not say — leave it free-running
            }
            var sourceRate = Math.Max(1, p.Asrc.WaveFormat.SampleRate);
            var playedAtSourceRate = (long)(played * (double)sourceRate / deviceRate);
            p.Drift.Observe(playedAtSourceRate, master);

            if (!lockOn)
            {
                p.Lock.Reset();
                p.Asrc.Ratio = 1;
                p.AnchorMaster = double.NaN;
                p.LagMs = 0;
                continue;
            }

            // The source's played position: what the converter consumed, less what still waits in the device's buffer.
            var buffered = Math.Max(0, p.Asrc.OutputFramesProduced - playedAtSourceRate);
            var sourcePlayed = (p.Asrc.InputFramesConsumed - buffered / p.Asrc.RatioInForce) / sourceRate;
            if (double.IsNaN(p.AnchorMaster))
            {
                p.AnchorMaster = master;
                p.AnchorSource = sourcePlayed;
                p.LastMaster = master;
                continue;
            }
            var lag = (sourcePlayed - p.AnchorSource) - (master - p.AnchorMaster);
            var dt = master - p.LastMaster;
            p.LastMaster = master;
            p.LagMs = lag * 1000;
            p.Asrc.Ratio = p.Lock.Update(lag, dt, p.Drift.Confident ? p.Drift.Ppm : 0);
        }
    }

    /// <summary>One line per playing output: its clock against the master, the correction in force, the lag, its delay.</summary>
    public IReadOnlyList<string> SyncReport()
    {
        var lines = new List<string>();
        foreach (var p in _players)
        {
            string name;
            try
            {
                name = p.Device.FriendlyName;
            }
            catch
            {
                name = p.Key;
            }
            var drift = p.Drift.Confident ? $"{p.Drift.Ppm:+0;-0} ppm" : "measuring";
            var delay = p.DelayMs > 0 ? $" · delay {p.DelayMs} ms" : "";
            lines.Add($"{name}: clock {drift} · correction {p.Lock.CorrectionPpm:+0;-0} ppm · lag {p.LagMs:+0.0;-0.0} ms{delay}");
        }
        return lines;
    }

    /// <summary>The worst lag of any playing output, ms; -1 with nothing playing.</summary>
    public double SyncWorstLagMs => _players.Count == 0 ? -1 : _players.Max(p => Math.Abs(p.LagMs));

    /// <summary>
    /// Stored names → devices. The <see cref="DefaultDeviceKey"/> entry adds the computer's
    /// default output (the venue-PA feed) and can combine with named HDMI screens; the same
    /// physical endpoint never plays twice. Empty selection (or nothing matching) = default.
    /// </summary>
    internal static List<MMDevice> ResolveDevices(MMDeviceEnumerator enumerator, IReadOnlyList<string> names)
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

    private readonly List<(IStingerVoice Voice, StingerKind Kind)> _voices = new();

    /// <summary>
    /// Opens a voice for a file at a volume: by default one WASAPI output per selected device,
    /// null off Windows or when nothing opens. Tests inject a fake so the whole stinger sound
    /// path — fire, release, re-fire, the duck, the sweep — runs headless.
    /// </summary>
    public Func<string, double, IStingerVoice?> VoiceFactory { get; set; }

    /// <summary>A stinger sound of either kind is on air and has not been told to leave.</summary>
    public bool StingerPlaying => _voices.Any(v => v.Voice.IsPlaying && !v.Voice.Releasing);

    /// <summary>A VOG sound is on air and has not been told to leave — what ducks everything else.</summary>
    public bool VogSoundPlaying => _voices.Any(v => v.Kind == StingerKind.Vog && v.Voice.IsPlaying && !v.Voice.Releasing);

    /// <summary>A sting sound is on air and has not been told to leave.</summary>
    public bool StingSoundPlaying => _voices.Any(v => v.Kind == StingerKind.Sting && v.Voice.IsPlaying && !v.Voice.Releasing);

    /// <summary>
    /// Fires a one-shot sound on the track's device selection, over whatever else plays.
    /// Independent of the track players — the track keeps rolling (ducked) underneath. Always a
    /// fresh voice: a released voice is never reused, so a stopped sound cannot come back. Who
    /// leaves when it starts is the stinger service's call (a new VOG releases the old VOG; a
    /// VOG never stops a stinger — it ducks it); this only opens the sound and sets its gain.
    /// </summary>
    public bool PlayStinger(string path, double volumePct, StingerKind kind = StingerKind.Vog)
    {
        IStingerVoice? voice = null;
        try
        {
            voice = VoiceFactory(path, volumePct);
        }
        catch (Exception ex)
        {
            Log.Error("Stinger could not start.", ex);
        }
        if (voice is null) return false;
        _voices.Add((voice, kind));
        ApplyGains(ShowClock.UtcNow);
        return true;
    }

    /// <summary>
    /// The playing sounds of one kind — or of both — leave the air over the show's stop fade. The
    /// duck lifts at once (the music comes back under the tail); the sweep disposes each voice
    /// once it has gone silent.
    /// </summary>
    public void ReleaseStingers(StingerKind? kind = null)
    {
        var ms = _services.State.Stingers.StopFadeMs;
        foreach (var (voice, k) in _voices)
        {
            if (kind is { } only && k != only) continue;
            if (!voice.Releasing) voice.Release(ms);
        }
        ApplyGains(ShowClock.UtcNow);
    }

    /// <summary>
    /// Every sound and every clip on air gets the gain its bus says it should have right now —
    /// a stinger sound and a clip's soundtrack duck under a VOG sound and come back after it.
    /// Called on every poll and the moment a sound starts or leaves, so a duck never waits.
    /// </summary>
    public void ApplyGains(DateTime nowUtc)
    {
        if (_services.Stingers is not { } stingers) return; // constructed after this service
        var sting = stingers.GainAt(AudioBus.StingSound, nowUtc);
        var vog = stingers.GainAt(AudioBus.VogSound, nowUtc);
        foreach (var (voice, kind) in _voices)
        {
            voice.SetGain(kind == StingerKind.Vog ? vog : sting);
        }
        _services.Video.ApplyClipGain(stingers.GainAt(AudioBus.ClipAudio, nowUtc));
    }

    /// <summary>A hard stop — for shutdown, where nothing is listening for a fade.</summary>
    public void StopStinger()
    {
        foreach (var (voice, _) in _voices)
        {
            try
            {
                voice.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn("Stinger stop issue.", ex);
            }
        }
        _voices.Clear();
    }

    /// <summary>Reaps voices that have gone silent — a natural end or a finished release — so the duck lifts and the outputs close.</summary>
    private void SweepStinger()
    {
        for (var i = _voices.Count - 1; i >= 0; i--)
        {
            if (_voices[i].Voice.IsPlaying) continue;
            try
            {
                _voices[i].Voice.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn("Stinger voice dispose issue.", ex);
            }
            _voices.RemoveAt(i);
        }
    }

    private void StopAll()
    {
        if (_players.Count == 0) return;
        foreach (var p in _players)
        {
            try
            {
                p.Output.Stop();
                p.Output.Dispose();
                p.Reader.Dispose();
                p.Device.Dispose();
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
