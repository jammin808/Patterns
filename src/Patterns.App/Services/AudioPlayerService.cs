using Avalonia.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Patterns.Core.Audio;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// The audio playlist's player: an independent bed that plays whatever the screens show, to the
/// default device or any set of outputs at once (HDMI screens are audio devices too — so a bed can
/// follow all screens, one screen, or a group). The list — the rows, then the folders' files,
/// shuffled when asked — is <see cref="AudioPlaylist"/>'s; this owns the devices: one reader and
/// one output per device, started together, each locked to the master clock; the next track when
/// one ends, the list looping or stopping at its end. Windows-only playback; the list itself, its
/// place and its words work everywhere, so a rehearsal at a desk without a sound card still reads.
/// </summary>
public sealed class AudioPlayerService : IDisposable
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _timer;
    private readonly List<Player> _players = new();
    private string _activeKey = "";
    private string _status = "Add tracks or a folder.";

    // The list as the player runs it: the order, its key, the folders' files and when they were last read, the place.
    private List<string> _order = new();
    private string _orderKey = "";
    private List<string> _folderFiles = new();
    private string _folderKey = "";
    private DateTime _lastScanUtc = DateTime.MinValue;
    private int _index = -1;
    private int _pendingIndex = -1;
    private string _nowPath = "";

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

    /// <summary>Reads a folder's files; the app's file system by default, a list in tests.</summary>
    public Func<string, IEnumerable<string>> EnumerateFiles { get; set; } = folder =>
        Directory.Exists(folder) ? Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories) : Array.Empty<string>();

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

    // ---- the list ----------------------------------------------------------------------------

    /// <summary>The order the player runs right now: the rows, then the folders' files; shuffled when asked.</summary>
    public IReadOnlyList<string> Order => _order;

    public int Count => _order.Count;

    /// <summary>The place in the order that is on (0-based), or -1 with nothing on.</summary>
    public int NowIndex => _index;

    /// <summary>The file on, or "" with nothing on.</summary>
    public string NowPath => _nowPath;

    /// <summary>The name of the track on — or, stopped, of the track ▶ PLAY would start — for a key that labels itself; "" with an empty list.</summary>
    public string CurrentName
    {
        get
        {
            if (_nowPath.Length > 0) return AudioPlaylist.NameOf(_services.State.AudioPlayer, _nowPath);
            var at = _index >= 0 && _index < _order.Count ? _index : 0;
            return at < _order.Count ? AudioPlaylist.NameOf(_services.State.AudioPlayer, _order[at]) : "";
        }
    }

    /// <summary>The file after the one on, by the list's rule (the loop wraps it); "" at the end without loop.</summary>
    public string NextPath
    {
        get
        {
            if (_order.Count == 0) return "";
            var next = AudioPlaylist.Step(_index, _order.Count, +1, _services.State.AudioPlayer.Loop);
            return next is { } n && n < _order.Count ? _order[n] : "";
        }
    }

    public string NextName => AudioPlaylist.NameOf(_services.State.AudioPlayer, NextPath);

    /// <summary>The names of the order by place, for the remotes' banks.</summary>
    public IReadOnlyList<string> Names()
    {
        var cfg = _services.State.AudioPlayer;
        return _order.Select(p => AudioPlaylist.NameOf(cfg, p)).ToList();
    }

    /// <summary>Where the track is and how long it is, from the first output's reader; zeros with nothing on.</summary>
    public double PositionSeconds => _players.Count > 0 ? SafeSeconds(() => _players[0].Reader.CurrentTime) : 0;

    public double LengthSeconds => _players.Count > 0 ? SafeSeconds(() => _players[0].Reader.TotalTime) : 0;

    private static double SafeSeconds(Func<TimeSpan> read)
    {
        try
        {
            return read().TotalSeconds;
        }
        catch
        {
            return 0;
        }
    }

    /// <summary>A track's place by its number, a row's id or name, or a file's name; -1 when none (the folders are read first).</summary>
    public int Resolve(string target)
    {
        RefreshOrder(_services.State.AudioPlayer);
        return AudioPlaylist.Find(_services.State.AudioPlayer, _order, target);
    }

    /// <summary>The track at a place plays now; false past the list.</summary>
    public bool PlayAt(int index)
    {
        var cfg = _services.State.AudioPlayer;
        RefreshOrder(cfg);
        if (index < 0 || index >= _order.Count) return false;
        _pendingIndex = index;
        cfg.Playing = true;
        Tick();
        return true;
    }

    /// <summary>The next track, wrapping — a NEXT key never dead-ends; false with an empty list.</summary>
    public bool Next() => Move(+1);

    /// <summary>The previous track, wrapping; false with an empty list.</summary>
    public bool Previous() => Move(-1);

    private bool Move(int delta)
    {
        var cfg = _services.State.AudioPlayer;
        RefreshOrder(cfg);
        if (_order.Count == 0) return false;
        var next = AudioPlaylist.Step(_index, _order.Count, delta, loop: true);
        if (next is null) return false;
        _pendingIndex = next.Value;
        cfg.Playing = true;
        Tick();
        return true;
    }

    /// <summary>
    /// The track on reached its natural end: the next one plays, or the list stops at its end
    /// without loop. The outputs' stop event lands here; a test calls it directly.
    /// </summary>
    public void TrackEnded()
    {
        var cfg = _services.State.AudioPlayer;
        var next = AudioPlaylist.Step(_index, _order.Count, +1, cfg.Loop);
        StopAll();
        if (next is null)
        {
            cfg.Playing = false;
            _index = -1;
            _nowPath = "";
            MarkNowPlaying(cfg, "");
            _status = "The list ended.";
            return;
        }
        _index = next.Value;
        Tick();
    }

    /// <summary>
    /// The folders read (at most every 30 s, or when they change) and the order rebuilt when
    /// anything in it would differ; the track on keeps its place through an edit of the rows.
    /// </summary>
    private void RefreshOrder(AudioPlayerConfig cfg)
    {
        var folderKey = string.Join('|', cfg.Folders);
        var now = DateTime.UtcNow;
        if (folderKey != _folderKey || (now - _lastScanUtc).TotalSeconds > 30)
        {
            _folderKey = folderKey;
            _lastScanUtc = now;
            _folderFiles = cfg.Folders.Count == 0 ? new List<string>() : AudioPlaylist.AudioFilesIn(cfg.Folders, EnumerateFiles);
        }
        var key = AudioPlaylist.OrderKey(cfg, _folderFiles);
        if (key == _orderKey) return;
        _orderKey = key;
        var current = _nowPath;
        _order = AudioPlaylist.BuildOrder(cfg, _folderFiles);
        if (current.Length > 0)
        {
            var kept = AudioPlaylist.IndexOf(_order, current);
            _index = kept >= 0 ? kept : Math.Min(Math.Max(_index, 0), Math.Max(0, _order.Count - 1)); // the track on was removed: its place plays on
            if (kept < 0) _activeKey = "";
        }
        else if (_index >= _order.Count)
        {
            _index = _order.Count - 1;
        }
    }

    private void MarkNowPlaying(AudioPlayerConfig cfg, string path)
    {
        foreach (var item in cfg.Items)
        {
            var on = path.Length > 0 && string.Equals(item.Path, path, StringComparison.OrdinalIgnoreCase);
            if (item.IsNowPlaying != on) item.IsNowPlaying = on;
        }
    }

    /// <summary>"3/12: walk-in · next: intro · shuffle · loop" — the words every surface shows.</summary>
    private string Words(AudioPlayerConfig cfg)
    {
        var next = NextPath;
        var after = next.Length > 0 ? " · next: " + AudioPlaylist.NameOf(cfg, next) : " · the last";
        var flags = (cfg.Shuffle ? " · shuffle" : "") + (cfg.Loop ? " · loop" : "");
        return $"{_index + 1}/{_order.Count}: {AudioPlaylist.NameOf(cfg, _nowPath)}{after}{flags}";
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
            RefreshOrder(cfg);
            if (!cfg.Playing)
            {
                StopAll();
                _nowPath = "";
                MarkNowPlaying(cfg, "");
                _status = _order.Count == 0 ? "Add tracks or a folder." : OperatingSystem.IsWindows() ? "Stopped." : "Stopped (audio output is Windows-only).";
                return;
            }
            if (_order.Count == 0)
            {
                StopAll();
                cfg.Playing = false;
                _nowPath = "";
                _status = "Add tracks or a folder.";
                return;
            }

            if (_pendingIndex >= 0)
            {
                _index = Math.Clamp(_pendingIndex, 0, _order.Count - 1);
                _pendingIndex = -1;
                _activeKey = ""; // the same file asked for again starts again
            }
            if (_index < 0 || _index >= _order.Count) _index = 0;

            // A file that is not on disk is skipped in the direction of travel; a list with nothing on disk stops and says so.
            var tries = 0;
            while (tries < _order.Count && !File.Exists(_order[_index]))
            {
                _index = AudioPlaylist.Step(_index, _order.Count, +1, loop: true) ?? 0;
                tries++;
            }
            if (tries >= _order.Count)
            {
                StopAll();
                cfg.Playing = false;
                _nowPath = "";
                MarkNowPlaying(cfg, "");
                _status = "No track of the list is on disk.";
                return;
            }

            var path = _order[_index];
            if (!string.Equals(_nowPath, path, StringComparison.OrdinalIgnoreCase))
            {
                _nowPath = path;
                MarkNowPlaying(cfg, path);
                Log.Info($"Audio playlist: {Words(cfg)}");
            }

            if (!OperatingSystem.IsWindows())
            {
                _status = $"Playing {Words(cfg)} — audio output is Windows-only, so no sound here.";
                return;
            }

            var loopSingle = cfg.Loop && _order.Count == 1; // one track on a loop is seamless, as the track always was
            var delays = string.Join(";", cfg.OutputDelays.Select(d => $"{d.Device}={d.DelayMs}"));
            var key = $"{path}|{loopSingle}|{string.Join(";", cfg.Devices)}|{delays}";
            if (key != _activeKey)
            {
                StopAll();
                _activeKey = key;
                StartAll(path, loopSingle, cfg.Devices, cfg.DelayFor);
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
                _status = $"Playing {_index + 1}/{_order.Count}: {AudioPlaylist.NameOf(cfg, path)} — {pos:mm\\:ss} / {total:mm\\:ss} on {where}" +
                          $"{(NextPath.Length > 0 ? " · next: " + NextName : " · the last")}{(cfg.Shuffle ? " · shuffle" : "")}{(cfg.Loop ? " · loop" : "")}";
            }
            else
            {
                _status = $"No output opened for {AudioPlaylist.NameOf(cfg, path)} — check the devices.";
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
        // Natural end: every output stopped — the next track, or the list's end.
        Dispatcher.UIThread.Post(() =>
        {
            var cfg = _services.State.AudioPlayer;
            if (!cfg.Playing || _players.Count == 0) return;
            if (_players.All(p => p.Output.PlaybackState == PlaybackState.Stopped)) TrackEnded();
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
        if (_players.Count == 0)
        {
            _activeKey = "";
            return;
        }
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
