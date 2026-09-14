using System.Runtime.InteropServices;
using Avalonia.Threading;
using LibVLCSharp.Shared;
using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.App.Services;

/// <summary>
/// Optional video decode via libVLC with callback rendering: frames land in BGRA buffers
/// that the engine composites like any layer — so video reaches outputs, spans and NDI.
/// Hosts one mount per referenced source (files, playlist items, DirectShow capture
/// devices), published on the <see cref="InputBus"/>, so different screens, PiP and
/// multiview tiles carry different inputs at the same time — and the same input on three
/// screens still costs one decoder. When libVLC is absent nothing crashes.
/// </summary>
public sealed class VideoEngine : IDisposable
{
    /// <summary>Simultaneous decoders — capture cards and files are real CPU/GPU cost.</summary>
    public const int MaxMounts = 4;

    /// <summary>Sources retired and fading at once, at most: past it the oldest is let go at once — rapid switching cannot grow the retired decoders without bound.</summary>
    public const int MaxRetired = 2;

    private LibVLC? _vlc;
    private bool _vlcInitFailed;

    private sealed record Mount(IMountedSource Source, bool Loop, bool Mute, double VolumePct, string Format = "", bool PreRoll = false, bool Tap = false, bool LowLatency = false)
    {
        /// <summary>Every picture this mount plays on — what the routing matrix reads its source from.</summary>
        public IReadOnlyList<MediaBus> Buses { get; init; } = Array.Empty<MediaBus>();
    }

    private readonly Dictionary<string, Mount> _mounts = new();
    private readonly List<(string Key, IMountedSource Source, DateTime RetiredUtc, int HoldMs)> _retired = new();
    private DispatcherTimer? _pump;
    private double _clipGain = 1;

    /// <summary>The gain every mounted clip's soundtrack currently carries on top of its own volume (the VOG duck).</summary>
    public double ClipGain => _clipGain;

    /// <summary>
    /// Ducks — or restores — the soundtrack of every clip on the screens: a VOG announcement
    /// over a playing sting or a VOG clip steps the clip down and lets it carry on, never stops it.
    /// Retired sources are not touched: they are already leaving.
    /// </summary>
    public void ApplyClipGain(double gain)
    {
        gain = Math.Clamp(gain, 0, 1);
        if (Math.Abs(gain - _clipGain) < 0.0005) return;
        _clipGain = gain;
        foreach (var mount in _mounts.Values)
        {
            mount.Source.SetAudio(mount.Mute, mount.VolumePct * gain);
        }
    }

    private int _audioDelayMs;

    /// <summary>The lip-sync offset every clip's soundtrack carries; every source is told, and a source mounted later is told on mount.</summary>
    public int AudioDelayMs => _audioDelayMs;

    public void ApplyAudioDelay(int ms)
    {
        ms = Math.Clamp(ms, -1000, 2000);
        if (_audioDelayMs == ms) return;
        _audioDelayMs = ms;
        foreach (var mount in _mounts.Values) mount.Source.SetAudioDelay(ms);
    }

    /// <summary>
    /// Opens a source for a wanted input. Null = the libVLC path (the default); tests inject a
    /// fake so the retirement bookkeeping — the fade, the hold, the sweep, a re-fire inside the
    /// hold — runs without a decoder.
    /// </summary>
    public Func<MediaLocator.WantedInput, IMountedSource?>? SourceFactory { get; set; }

    /// <summary>Non-empty when more sources are wanted than the decoder cap allows.</summary>
    public string LimitNote { get; private set; } = "";

    /// <summary>
    /// Whether a decoder opened now uses the graphics card: the show's choice against the run
    /// (software in the run after a native fault). Asked per open, so a change on the Machine page
    /// reaches the next clip and never restarts one mid-play.
    /// </summary>
    public Func<bool> HardwareDecoding { get; set; } = () => true;

    /// <summary>Mounted keys with a short status each — the Media tab's active-inputs line.</summary>
    public IReadOnlyList<(string Key, string Status)> MountStatuses
        => _mounts.Select(kv => (kv.Key, kv.Value.PreRoll
            ? (kv.Value.Source.IsHeld ? "pre-rolled" : "pre-rolling")
            : kv.Value.Source.IsPlaying ? "playing" : kv.Value.Source.StatusText)).ToList();

    /// <summary>Pre-roll clips that could not be mounted because the decoder limit was reached by live sources.</summary>
    public int PreRollWaiting { get; private set; }

    /// <summary>The memory pressure ladder's step at high: the standby cue's clips are not opened ahead; what would have been is counted.</summary>
    public bool PreRollSuppressed { get; set; }

    /// <summary>Pre-rolls the ladder held back on the last reconcile.</summary>
    public int PreRollHeldBack { get; private set; }

    /// <summary>The ladder's step at critical: a source the preview alone wants is not opened; the source on air always is.</summary>
    public bool RefuseNonCriticalOpens { get; set; }

    /// <summary>Preview-only sources the ladder refused on the last reconcile.</summary>
    public int PressureRefused { get; private set; }

    /// <summary>Retired sources let go before their fade was over because more than <see cref="MaxRetired"/> were fading at once, this session.</summary>
    public int RetiredCutShort { get; private set; }

    /// <summary>A reopen staged because its source is on air: the key, the source's name, the edit and the words.</summary>
    public sealed record PendingChange(string Key, string Target, TopologyEdit Edit, string Words);

    private readonly List<PendingChange> _pending = new();

    /// <summary>The reopens staged on the last reconcile — a mode, a profile, a loop or the routing mode changed under a source on air; applied when it leaves the air or the outputs go off.</summary>
    public IReadOnlyList<PendingChange> PendingChanges => _pending;

    /// <summary>The staged reopens as one line for the Media page, STATE and Companion; "" with none.</summary>
    public string PendingNote => string.Join("  ", _pending.Select(p => p.Words));

    /// <summary>Bytes the retired, still-fading sources hold (their frame pools).</summary>
    public long RetiredBytes
    {
        get
        {
            long b = 0;
            foreach (var (_, source, _, _) in _retired) b += source.MemoryBytes;
            return b;
        }
    }

    /// <summary>Decoders open right now — live and pre-rolled — against <see cref="MaxMounts"/>: the memory ceilings' number.</summary>
    public int MountCount => _mounts.Count;

    /// <summary>Where one wanted clip stands: not mounted, opening, held and ready, or already on the screens.</summary>
    public PreRoll.State PreRollStateOf(string key)
    {
        if (!_mounts.TryGetValue(key, out var mount)) return PreRoll.State.Missing;
        if (!mount.PreRoll) return PreRoll.State.OnAir;
        return mount.Source.IsHeld ? PreRoll.State.Ready : PreRoll.State.Opening;
    }

    public IReadOnlyList<PreRoll.State> PreRollStates(IReadOnlyList<MediaLocator.WantedInput> wants)
        => wants.Select(w => PreRollStateOf(w.Key)).ToList();

    /// <summary>
    /// Reconciles the decoder pool with everything the program — and, while the operator is
    /// programming, the sandbox — references (UI thread). Highest-priority reference wins a
    /// shared mount's loop/audio settings.
    /// </summary>
    public void Reconcile(ShowSnapshot snap, ShowSnapshot? sandbox = null, DateTime? nowUtc = null, IReadOnlyList<MediaLocator.WantedInput>? preRoll = null)
    {
        var now = nowUtc ?? ShowClock.UtcNow;
        SweepRetired(now);

        var wanted = WantedVideoInputs(snap, sandbox);
        var wantedKeys = wanted.Select(w => w.Key).ToHashSet();

        // The standby cue's clips ride behind the live wants: opened and held on their first frame,
        // silent, never at a live source's expense, retired like any other when standby moves on.
        var held = new List<MediaLocator.WantedInput>();
        PreRollHeldBack = 0;
        if (preRoll is not null)
        {
            foreach (var p in preRoll)
            {
                if (p.Kind != MediaLocator.WantedKind.VideoFile || wantedKeys.Contains(p.Key)) continue;
                if (PreRollSuppressed)
                {
                    PreRollHeldBack++;                                                                  // the ladder's step: not opened ahead, and said so
                    continue;
                }
                wantedKeys.Add(p.Key);
                held.Add(p);
            }
        }

        // A source that leaves fades its sound out over the stop fade and is kept, silenced, only
        // as long as the longest fade in flight needs its frames.
        var fadeMs = snap.State.Stingers.StopFadeMs;
        var transitionMs = snap.FadesEnabled ? (int)Math.Round(snap.FadeSecondsFor(snap.Version) * 1000) : 0;
        var holdMs = AudioFade.RetireHoldMs(transitionMs, fadeMs);

        foreach (var key in _mounts.Keys.Where(k => !wantedKeys.Contains(k)).ToList())
        {
            RetireMount(key, now, holdMs, fadeMs);
        }

        // With the routing matrix in charge every clip's soundtrack comes to the desk's mixer
        // instead of an output of the decoder's own; switching the matrix reopens the clips on
        // air (a decoder's audio path is chosen when it opens), so it is a setup-time switch.
        var tap = snap.State.AudioRouting.Enabled && SourceFactory is null;

        var over = 0;
        var refused = 0;
        _pending.Clear();
        foreach (var w in wanted)
        {
            if (RefuseNonCriticalOpens && !_mounts.ContainsKey(w.Key) && !AudioMonitorRule.OnProgram(w.Buses))
            {
                refused++;                                                                              // critical pressure: the preview alone wants it, and it is not opened
                continue;
            }
            if (_mounts.TryGetValue(w.Key, out var existing))
            {
                if (existing.PreRoll)
                {
                    // GO: the held clip runs from its first frame with the look's sound.
                    existing = existing with { PreRoll = false };
                    _mounts[w.Key] = existing;
                    existing.Source.Release();
                }
                var edit = existing.LowLatency != w.LowLatency ? TopologyEdit.CaptureLowLatency
                    : existing.Format != w.Format ? TopologyEdit.CaptureFormat
                    : existing.Tap != tap ? TopologyEdit.AudioRoutingMode
                    : existing.Loop != w.Loop ? TopologyEdit.ClipLoop
                    : (TopologyEdit?)null;
                if (edit is null || TopologyPolicy.OnAir(snap.OutputsLive, AudioMonitorRule.OnProgram(w.Buses)))
                {
                    // Mute/volume/route apply live to the running player — never restart the media. A
                    // reopen (the mode, the profile, the loop, the routing mode) landing under a source
                    // on air is staged: the room keeps its picture, the words say so, and the reopen
                    // happens when the source leaves the air or the outputs go off (TopologyPolicy).
                    existing.Source.SetAudio(w.Mute, w.VolumePct * _clipGain);
                    if (!tap) Route(existing.Source, w);
                    _mounts[w.Key] = existing with { Mute = w.Mute, VolumePct = w.VolumePct, Buses = w.Buses };
                    if (edit is { } staged)
                    {
                        var target = w.Kind == MediaLocator.WantedKind.VideoFile ? Path.GetFileName(w.Target) : w.Target;
                        _pending.Add(new PendingChange(w.Key, target, staged, TopologyPolicy.PendingWords(staged, target)));
                    }
                    continue;
                }
                RetireMount(w.Key, now, holdMs, fadeMs); // a loop, capture-mode, latency-profile or routing-mode change needs a reopen: the source is not on air
            }

            if (_mounts.Count >= MaxMounts)
            {
                over++;
                continue;
            }
            if (SourceFactory is null && !EnsureVlc()) return;
            TryOpen(w, preRoll: false, tap);
        }

        var waiting = 0;
        foreach (var p in held)
        {
            if (_mounts.TryGetValue(p.Key, out var existing))
            {
                if (existing.PreRoll) continue;
                if (existing.Loop == p.Loop && existing.Format == p.Format)
                {
                    // The clip just left the screens and the standby wants it again: wound back and held.
                    _mounts[p.Key] = existing with { PreRoll = true, Mute = p.Mute, VolumePct = p.VolumePct };
                    existing.Source.HoldAtStart();
                    continue;
                }
                RetireMount(p.Key, now, holdMs, fadeMs);
            }
            if (_mounts.Count >= MaxMounts)
            {
                waiting++;
                continue;
            }
            if (SourceFactory is null && !EnsureVlc()) break;
            TryOpen(p, preRoll: true, tap);
        }
        PreRollWaiting = waiting;
        PressureRefused = refused;

        LimitNote = over > 0
            ? $"Input limit: {MaxMounts} simultaneous decoders — {over} source{(over == 1 ? "" : "s")} waiting."
            : refused > 0
                ? $"Memory pressure: {refused} preview-only source{(refused == 1 ? "" : "s")} not opened until it eases."
                : "";
    }

    /// <summary>Opens one wanted input and mounts it on the bus; a pre-roll opens held on its first frame.</summary>
    private void TryOpen(MediaLocator.WantedInput w, bool preRoll, bool tap = false)
    {
        try
        {
            var source = SourceFactory is { } open
                ? open(w)
                : new VlcFrameSource(_vlc!, w.Target, w.Loop,
                    w.Kind == MediaLocator.WantedKind.Capture, w.Mute, w.VolumePct * _clipGain, w.Format, HardwareDecoding(), startHeld: preRoll, audioTap: tap, lowLatency: w.LowLatency);
            if (source is null) return;
            if (SourceFactory is not null) source.SetAudio(w.Mute, w.VolumePct * _clipGain);
            if (!tap) Route(source, w);
            if (preRoll) source.HoldAtStart();
            _mounts[w.Key] = new Mount(source, w.Loop, w.Mute, w.VolumePct, w.Format, preRoll, tap, w.LowLatency) { Buses = w.Buses };
            InputBus.Mount(w.Key, source);
        }
        catch (Exception ex)
        {
            Log.Error($"Video open failed for '{w.Target}'.", ex);
            VideoService.AvailabilityNote = $"Could not open video: {ex.Message}";
        }
    }

    /// <summary>
    /// A clip fired onto a mount that already exists — the same file pressed again while it plays,
    /// or a leftover the preview still references that ended long ago — plays from the top: an ended
    /// player is started again and a playing one is wound back. Without this a stinger fired over
    /// its own ended decoder read "ended" on its first tick and put the show straight back, which is
    /// the "tries to play and immediately fades back off" of the round-14 report. False when the key
    /// is not mounted or the source cannot move.
    /// </summary>
    public bool RestartIfMounted(string key)
    {
        if (!_mounts.TryGetValue(key, out var mount)) return false;
        try
        {
            return mount.Source.Seek(0);
        }
        catch (Exception ex)
        {
            Log.Warn("Restarting a mounted clip failed.", ex);
            return false;
        }
    }

    /// <summary>The mounted clips whose soundtracks are tapped for the mixer, with the pictures each plays on; a held pre-roll is silent already.</summary>
    public IEnumerable<(string Key, IReadOnlyList<MediaBus> Buses, Patterns.Core.Audio.AudioRing Tap, bool PreRoll)> Taps()
    {
        foreach (var (key, mount) in _mounts)
        {
            if (mount.Source.AudioTap is { } tap) yield return (key, mount.Buses, tap, mount.PreRoll);
        }
    }

    /// <summary>
    /// Where this mount's sound comes out: the show's programme outputs, or the operator's own.
    /// The names come off the show; the ids come off Windows; a device that is not there leaves
    /// the clip on the default output, and the decoder says so through RoutedTo.
    /// </summary>
    public Func<AudioDestination, string?>? DeviceFor { get; set; }

    private void Route(IMountedSource source, MediaLocator.WantedInput w)
    {
        if (DeviceFor is not { } lookup) return;
        try
        {
            source.SetOutputDevice(lookup(w.Destination));
        }
        catch (Exception ex)
        {
            Log.Warn("Routing a clip's sound failed.", ex);
        }
    }

    /// <summary>
    /// Program + sandbox wants, deduped by key, the program's settings winning a shared mount —
    /// and then routed: the programme's sound to the programme's outputs, the picture the operator
    /// asked to audition to their own, and anything else nowhere. A mount the programme wants is
    /// never silenced by a monitor pick: the room's sound is not the desk's to take away, and on a
    /// rig with one interface taking it away at the desk takes it away in the room.
    /// </summary>
    public static List<MediaLocator.WantedInput> WantedVideoInputs(ShowSnapshot snap, ShowSnapshot? sandbox)
    {
        var list = MergeWithSandbox(MediaLocator.FindWantedInputs(snap), sandbox);
        list.RemoveAll(w => w.Kind is MediaLocator.WantedKind.Ndi or MediaLocator.WantedKind.Web);
        return AudioMonitorRule.Apply(snap.State, list);
    }

    /// <summary>
    /// The preview's wants folded into the programme's. A mount both of them want is one decoder
    /// on two buses, so the preview is added to the buses it is already on rather than dropped —
    /// otherwise listening to the preview would silence a clip that is in the preview.
    /// </summary>
    public static List<MediaLocator.WantedInput> MergeWithSandbox(List<MediaLocator.WantedInput> list, ShowSnapshot? sandbox)
    {
        if (sandbox is null) return list;
        var at = new Dictionary<string, int>(StringComparer.Ordinal);
        for (var i = 0; i < list.Count; i++) at[list[i].Key] = i;
        foreach (var w in MediaLocator.FindWantedInputs(sandbox))
        {
            if (at.TryGetValue(w.Key, out var already))
            {
                var buses = list[already].Buses.ToList();
                if (!buses.Contains(MediaBus.Sandbox)) buses.Add(MediaBus.Sandbox);
                list[already] = list[already] with { Buses = buses };
                continue;
            }
            at[w.Key] = list.Count;
            list.Add(w with { Buses = new[] { MediaBus.Sandbox } });
        }
        return list;
    }

    /// <summary>
    /// The old source keeps decoding briefly on the bus's previous map so a crossfade fades
    /// out real frames instead of a placeholder. Its sound fades to silence over the stop fade
    /// and is then held silent — re-asserted on every pump, because a mute written before the
    /// decoder's audio output exists is lost — and the whole source is disposed once the longest
    /// fade in flight is over. A retired source is never brought back: a re-fire of the same
    /// file inside the hold opens a fresh decoder.
    /// </summary>
    private void RetireMount(string key, DateTime now, int holdMs, int fadeMs)
    {
        if (!_mounts.TryGetValue(key, out var mount)) return;
        _mounts.Remove(key);
        InputBus.Unmount(key);
        mount.Source.BeginFadeOut(now, fadeMs);
        InputBus.SetPrevious(key, mount.Source);
        _retired.Add((key, mount.Source, now, holdMs));
        while (_retired.Count > MaxRetired)
        {
            // Rapid switching: the oldest fade is cut short rather than a third decoder kept — the retired memory has a bound.
            var oldest = _retired[0];
            InputBus.ClearPreviousIf(oldest.Key, oldest.Source);
            oldest.Source.Dispose();
            _retired.RemoveAt(0);
            RetiredCutShort++;
        }
        StartPump();
    }

    /// <summary>Fades and silences every retired source, then sweeps. Runs at 50 ms only while something is retired.</summary>
    public void Pump(DateTime? nowUtc = null)
    {
        var now = nowUtc ?? ShowClock.UtcNow;
        foreach (var (_, source, _, _) in _retired)
        {
            try
            {
                source.Pump(now);
            }
            catch (Exception ex)
            {
                Log.Warn("Retired video source pump failed.", ex);
            }
        }
        SweepRetired(now);
    }

    /// <summary>Also called from the app's 1 s poll so a retired decoder never lingers.</summary>
    public void SweepRetired(DateTime? nowUtc = null)
    {
        var now = nowUtc ?? ShowClock.UtcNow;
        for (var i = _retired.Count - 1; i >= 0; i--)
        {
            if ((now - _retired[i].RetiredUtc).TotalMilliseconds <= _retired[i].HoldMs) continue;
            InputBus.ClearPreviousIf(_retired[i].Key, _retired[i].Source);
            _retired[i].Source.Dispose();
            _retired.RemoveAt(i);
        }
        if (_retired.Count == 0) _pump?.Stop();
    }

    /// <summary>Retired sources are kept alive for a few hundred milliseconds only.</summary>
    public int RetiredCount => _retired.Count;

    private void StartPump()
    {
        if (_pump is null)
        {
            try
            {
                _pump = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(50) };
                _pump.Tick += (_, _) => Pump();
            }
            catch (Exception ex)
            {
                // No dispatcher (a bare test host): the 1 s poll and the next Reconcile still sweep.
                Log.Warn("Video retire pump could not start.", ex);
                return;
            }
        }
        if (!_pump.IsEnabled) _pump.Start();
    }

    /// <summary>Whether video decode is available (initialises libVLC on first ask).</summary>
    public bool EnsureAvailable() => EnsureVlc();

    /// <summary>The shared libVLC instance for secondary players (PiP); null when unavailable.</summary>
    public LibVLC? SharedVlc => EnsureVlc() ? _vlc : null;

    private bool EnsureVlc()
    {
        if (_vlc is not null) return true;
        if (_vlcInitFailed) return false;

        // The same table of folders the encoder host reads (VlcRuntime), so the desk and its host agree on where libVLC is.
        _vlc = VlcRuntime.Create(out var failure, "--no-video-title-show", "--quiet");
        if (_vlc is not null)
        {
            Log.Info("libVLC initialised.");
            VideoService.AvailabilityNote = "";
            return true;
        }

        if (failure.Length > 0) Log.Warn($"libVLC init failed: {failure}");
        _vlcInitFailed = true;
        VideoService.AvailabilityNote =
            "Video needs libVLC: install 64-bit VLC, or put a 'libvlc' folder (libvlc.dll + plugins) beside Patterns.exe.";
        Log.Warn(VideoService.AvailabilityNote);
        return false;
    }

    public void Dispose()
    {
        _pump?.Stop();
        foreach (var (key, mount) in _mounts)
        {
            InputBus.Unmount(key);
            mount.Source.Dispose();
        }
        _mounts.Clear();
        foreach (var (key, source, _, _) in _retired)
        {
            InputBus.SetPrevious(key, null);
            source.Dispose();
        }
        _retired.Clear();
        _vlc?.Dispose();
        _vlc = null;
    }
}

/// <summary>A source the engine owns: live audio settings while mounted, a fade-out and a silence hold once retired.</summary>
public interface IMountedSource : IVideoFrameSource, IDisposable
{
    /// <summary>Live mute/volume — never restarts the media.</summary>
    void SetAudio(bool mute, double volumePct);

    /// <summary>Told to leave: the sound ramps to silence over <paramref name="ms"/> from <paramref name="nowUtc"/>.</summary>
    void BeginFadeOut(DateTime nowUtc, int ms);

    /// <summary>Advances the fade and, once silent, keeps asserting silence — a dropped write must not become a sound.</summary>
    void Pump(DateTime nowUtc);

    /// <summary>The lip-sync offset of the soundtrack, ms (negative = earlier). A source with no sound ignores it.</summary>
    void SetAudioDelay(int ms)
    {
    }

    /// <summary>
    /// Which output this source's sound plays on: a device id, or null for the system's default.
    /// A source that cannot be routed ignores it and says so through <see cref="RoutedTo"/>.
    /// </summary>
    void SetOutputDevice(string? deviceId)
    {
    }

    /// <summary>The output the source is actually on, read back from the decoder; empty when it could not be moved or was never asked.</summary>
    string RoutedTo => "";

    /// <summary>Bytes the source holds for its frames (its pool); 0 for one that holds none of its own.</summary>
    long MemoryBytes => 0;

    /// <summary>The standby cue's clip: wound back and held on its first frame, silent, so GO lands on a picture.</summary>
    void HoldAtStart()
    {
    }

    /// <summary>GO: the held clip runs from its first frame with its sound.</summary>
    void Release()
    {
    }

    /// <summary>Held on the first frame with that frame decoded — pre-rolled and ready.</summary>
    bool IsHeld => false;

    /// <summary>
    /// The decoded soundtrack as the mixer reads it (48 kHz, stereo, float, interleaved) when the
    /// source was opened with its audio tapped; null when the decoder plays its own sound.
    /// </summary>
    Patterns.Core.Audio.AudioRing? AudioTap => null;
}

/// <summary>One playing video: libVLC decodes into our BGRA buffer; renderers draw the newest frame.</summary>
public sealed class VlcFrameSource : IMountedSource
{
    private readonly object _gate = new();
    private readonly Media _media;
    private readonly MediaPlayer _player;
    private DateTime _fadeStartUtc;
    private int _fadeMs = -1;        // -1 = not retiring
    private float _fadeFrom;
    private bool _silenced;

    // Keep delegate instances alive for the lifetime of the callbacks.
    private readonly MediaPlayer.LibVLCVideoFormatCb _formatCb;
    private readonly MediaPlayer.LibVLCVideoCleanupCb _cleanupCb;
    private readonly MediaPlayer.LibVLCVideoLockCb _lockCb;
    private readonly MediaPlayer.LibVLCVideoUnlockCb _unlockCb;
    private readonly MediaPlayer.LibVLCVideoDisplayCb _displayCb;

    // The audio tap: with the routing matrix on, libVLC hands the decoded soundtrack to the desk
    // instead of an output of its own, and the mixer's lanes carry it wherever the matrix says.
    private readonly MediaPlayer.LibVLCAudioPlayCb? _audioPlayCb;
    private readonly MediaPlayer.LibVLCAudioFlushCb? _audioFlushCb;
    private readonly Patterns.Core.Audio.AudioRing? _tap;
    private volatile float _tapGain = 1f;
    private float[] _tapScratch = Array.Empty<float>();

    /// <summary>The tap's rate and channels: the mixer's own, so no lane resamples a clip.</summary>
    public const int TapRate = 48000;
    public const int TapChannels = 2;

    // The decoder writes each frame into a pooled buffer whose image the sinks draw (no copy, no
    // allocation); the scratch buffer is for a frame that finds every pooled buffer still under a
    // draw — that frame goes the old way, copied into an image of its own.
    private FramePool? _pool;
    private IntPtr _native;
    private int _nativePitch;
    private SKImage? _latest;
    private double _latestClock = -1;
    private int _width;
    private int _height;
    private bool _disposed;
    private readonly bool _isCapture;
    private long _frameClockBits = BitConverter.DoubleToInt64Bits(-1);
    private volatile bool _mute;
    private volatile float _volumePct;
    private volatile bool _held;

    // Frames handed to render sinks may be recorded into GPU-deferred canvases that read the
    // pixels at flush time — so each decoded frame becomes its own immutable SKImage, and
    // superseded frames are retired for a grace period instead of disposed immediately, in the
    // one pool every live source shares (RetiredFrames: a short hold and a cap on the count).

    /// <summary>Frames held for a fade right now, across every live source: the memory ceilings' number.</summary>
    public static int RetiredImageCount => RetiredFrames.Count;

    /// <summary>This source's frame pool, for the words and the tests; null before the format is known.</summary>
    public FramePool? Pool => _pool;

    /// <summary>The pool's bytes: what this decoder holds for its frames.</summary>
    public long MemoryBytes => _pool?.Bytes ?? 0;

    private static void RetireImage(SKImage? image) => RetiredFrames.Retire(image);

    /// <summary>
    /// Media options for a DirectShow capture device. A chosen mode ("1920x1080@60") asks the
    /// driver for that size and rate; an empty or unreadable one leaves the device's default.
    /// The low-latency profile (IMAG) takes the decoder's input buffer out and stops its clock
    /// smoothing: the frame is shown as soon as it is decoded, and a hitch drops one rather than
    /// shows it late; the default keeps an 80 ms buffer, which a confidence feed prefers to a
    /// skip. Pure — unit tested.
    /// </summary>
    public static string[] CaptureOptions(string deviceName, string format = "", bool lowLatency = false)
    {
        var options = new List<string>
        {
            $":dshow-vdev={deviceName}",
            ":dshow-adev=none",     // programme audio routing stays with the desk, not the display PC
            ":dshow-aspect-ratio=", // native
        };
        if (lowLatency)
        {
            options.Add(":live-caching=0");   // no input buffer: the frame goes to the sinks as it is decoded
            options.Add(":clock-jitter=0");   // and the clock does not smooth arrivals into a delay
            options.Add(":clock-synchro=0");
        }
        else
        {
            options.Add(":live-caching=80");  // a short buffer: low-latency for confidence monitoring, a hitch absorbed
        }
        if (CaptureFormat.TryParse(format, out var f))
        {
            options.Add($":dshow-size={f.Width}x{f.Height}");
            options.Add($":dshow-fps={f.Fps.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture)}");
        }
        return options.ToArray();
    }

    public VlcFrameSource(LibVLC vlc, string target, bool loop, bool isCapture, bool mute, double volumePct, string format = "", bool hardwareDecoding = true, bool startHeld = false, bool audioTap = false, bool lowLatency = false)
    {
        _isCapture = isCapture;
        if (isCapture)
        {
            _media = new Media(vlc, "dshow://", FromType.FromLocation);
            foreach (var opt in CaptureOptions(target, format, lowLatency))
            {
                _media.AddOption(opt);
            }
        }
        else
        {
            _media = new Media(vlc, new Uri(Path.GetFullPath(target)));
            if (loop) _media.AddOption("input-repeat=65535");
            // A pre-rolled clip opens, decodes its first frame and waits there: libVLC's own
            // start-paused, so the file, the codec and the card's decoder are all up before GO.
            if (startHeld)
            {
                _media.AddOption(":start-paused");
                _held = true;
            }
        }

        _mute = mute;
        _volumePct = (float)volumePct;

        _formatCb = OnFormat;
        _cleanupCb = OnCleanup;
        _lockCb = OnLock;
        _unlockCb = OnUnlock;
        _displayCb = OnDisplay;

        _player = new MediaPlayer(_media)
        {
            // The card's decoder when the run allows it; the CPU in a safe run (see VideoDecodingChoice).
            EnableHardwareDecoding = hardwareDecoding,
        };
        _player.SetVideoFormatCallbacks(_formatCb, _cleanupCb);
        _player.SetVideoCallbacks(_lockCb, _unlockCb, _displayCb);

        if (audioTap)
        {
            // The decoded sound comes to the desk in the mixer's own format; the mute, the volume,
            // the hold and the fade are applied here, in the tap, so nothing depends on a write
            // libVLC might drop before its output exists — there is no output of its own now.
            _tap = new Patterns.Core.Audio.AudioRing(TapChannels, TapRate);
            _audioPlayCb = OnAudioPlay;
            _audioFlushCb = OnAudioFlush;
            _player.SetAudioFormat("FL32", TapRate, TapChannels);
            _player.SetAudioCallbacks(_audioPlayCb, null, null, _audioFlushCb, null);
        }

        // Audio state set before the audio output exists can be lost — (re)apply once
        // playback has actually started, and again on every later change.
        _player.Playing += (_, _) => ApplyAudio();
        _player.Play();
        ApplyAudio();
    }

    public Patterns.Core.Audio.AudioRing? AudioTap => _tap;

    /// <summary>libVLC's decoded samples (its audio thread): scaled by the tap's gain and written to the ring; the decoder never waits.</summary>
    private void OnAudioPlay(IntPtr data, IntPtr samples, uint count, long pts)
    {
        if (_tap is null || _disposed || count == 0) return;
        var n = (int)Math.Min(count, 1 << 20) * TapChannels;
        try
        {
            if (_tapScratch.Length < n) _tapScratch = new float[n];
            Marshal.Copy(samples, _tapScratch, 0, n);
            var gain = _tapGain;
            if (gain != 1f)
            {
                var span = _tapScratch.AsSpan(0, n);
                for (var i = 0; i < span.Length; i++) span[i] *= gain;
            }
            _tap.Write(_tapScratch.AsSpan(0, n));
        }
        catch (Exception ex)
        {
            Log.Warn("The clip's audio tap dropped a block.", ex);
        }
    }

    private void OnAudioFlush(IntPtr data, long pts)
    {
        // A seek or a stop: nothing to do in the ring — the readers simply hear what comes next.
    }

    /// <summary>The tap's gain: the mute, the hold, the volume and a fade in one number, applied in the decoder's own thread.</summary>
    private void RefreshTapGain()
    {
        if (_tap is null) return;
        var gain = _silenced || _mute || _held ? 0f : Math.Clamp(_volumePct / 100f, 0f, 1.25f);
        if (_fadeMs >= 0 && !_silenced) gain = 0f;   // the fade sets its own value in Pump
        _tapGain = gain;
    }

    /// <summary>Live mute/volume — never restarts the media. Ignored once the source is leaving: it only gets quieter.</summary>
    public void SetAudio(bool mute, double volumePct)
    {
        if (_fadeMs >= 0) return;
        if (_mute == mute && Math.Abs(_volumePct - volumePct) < 0.5) return;
        _mute = mute;
        _volumePct = (float)volumePct;
        ApplyAudio();
    }

    /// <summary>Held on the first frame with that frame decoded: the pre-roll is ready for GO.</summary>
    public bool IsHeld => _held && !_disposed && _latest is not null;

    /// <summary>
    /// The standby cue wants this clip: wound back to its start and paused there, silent. A player
    /// that ended plays again first (the callbacks are still wired), so the held frame is the
    /// clip's first, not its last.
    /// </summary>
    public void HoldAtStart()
    {
        if (_disposed || _isCapture) return;
        _held = true;
        try
        {
            if (_player.State is VLCState.Ended or VLCState.Stopped) _player.Play();
            _player.Time = 0;
            _player.SetPause(true);
            ApplyAudio();
        }
        catch (Exception ex)
        {
            Log.Warn("Holding a clip at its start failed.", ex);
        }
    }

    /// <summary>GO: the held clip runs from where it is held with its sound as the look wants it.</summary>
    public void Release()
    {
        if (!_held) return;
        _held = false;
        if (_disposed) return;
        try
        {
            if (_player.State is VLCState.Ended or VLCState.Stopped) _player.Play();
            else _player.SetPause(false);
            ApplyAudio();
        }
        catch (Exception ex)
        {
            Log.Warn("Releasing a held clip failed.", ex);
        }
    }

    private int _audioDelayMs;

    /// <summary>The soundtrack's lip-sync offset: libVLC shifts its audio clock against the picture, live.</summary>
    public void SetAudioDelay(int ms)
    {
        if (_audioDelayMs == ms) return;
        _audioDelayMs = ms;
        ApplyAudio();
    }

    public void BeginFadeOut(DateTime nowUtc, int ms)
    {
        if (_fadeMs >= 0) return;
        _fadeStartUtc = nowUtc;
        _fadeMs = Math.Max(0, ms);
        _fadeFrom = _mute ? 0 : _volumePct;
        Pump(nowUtc);
    }

    /// <summary>
    /// The fade is a pure function of the clock (a missed pump never stalls it). Once it reaches
    /// silence the source is silenced for good and the silence is re-asserted on every pump:
    /// libVLC drops audio writes made before its audio output exists, so a clip retired in its
    /// first moments would otherwise come up at full volume a beat later — the sound a room
    /// hears "again" under the next stinger.
    /// </summary>
    public void Pump(DateTime nowUtc)
    {
        if (_fadeMs < 0 || _disposed) return;
        if (_silenced || AudioFade.Done(_fadeStartUtc, nowUtc, _fadeMs) || _fadeFrom <= 0)
        {
            Silence();
            return;
        }
        var volume = _fadeFrom * (float)AudioFade.GainAt(_fadeStartUtc, nowUtc, _fadeMs);
        if (_tap is not null)
        {
            _tapGain = Math.Clamp(volume / 100f, 0f, 1.25f);
            return;
        }
        try
        {
            _player.Volume = (int)Math.Clamp(volume, 0, 125);
        }
        catch (Exception ex)
        {
            Log.Warn("Fading a retired source failed.", ex);
        }
    }

    /// <summary>Mute, zero volume and no audio track at all — three ways to be silent, kept until disposal.</summary>
    private void Silence()
    {
        _silenced = true;
        _mute = true;
        _volumePct = 0;
        _tapGain = 0f;
        if (_disposed) return;
        if (_tap is not null) return;   // the tap is silent already; nothing of libVLC's to touch
        try
        {
            _player.Mute = true;
            _player.Volume = 0;
            if (_player.AudioTrack != -1) _player.SetAudioTrack(-1);
        }
        catch (Exception ex)
        {
            Log.Warn("Silencing a retired source failed.", ex);
        }
    }

    private string? _wantedDevice;
    private string _routedTo = "";

    /// <summary>
    /// Where this clip's sound goes. libVLC's device id space belongs to the output module and a
    /// write made before the aout exists is dropped — the same hazard the mute and the volume
    /// already work around — so it is asserted here, on every audio apply, and read back rather
    /// than assumed. An operator who believes a route they do not have is worse off than one who
    /// is told it did not take.
    /// </summary>
    public void SetOutputDevice(string? deviceId)
    {
        var wanted = string.IsNullOrWhiteSpace(deviceId) ? null : deviceId;
        if (_wantedDevice == wanted) return;
        _wantedDevice = wanted;
        ApplyAudio();
    }

    public string RoutedTo => _routedTo;

    private void ApplyAudio()
    {
        if (_disposed) return;
        try
        {
            if (_tap is not null)
            {
                // Tapped: the desk owns the level; libVLC's own stays at unity and its delay still applies.
                RefreshTapGain();
                _player.Mute = false;
                _player.Volume = 100;
                _player.SetAudioDelay(_audioDelayMs * 1000L);
                _routedTo = "mixer";
                return;
            }
            _player.Mute = _mute || _held;   // a held clip is silent whatever the look wants, until GO
            _player.Volume = (int)Math.Clamp(_volumePct, 0, 125);
            _player.SetAudioDelay(_audioDelayMs * 1000L); // microseconds
            ApplyOutputDevice();
        }
        catch (Exception ex)
        {
            Log.Warn("Applying audio state failed.", ex);
        }
    }

    private void ApplyOutputDevice()
    {
        if (_wantedDevice is null)
        {
            _routedTo = "";
            return;
        }
        try
        {
            _player.SetOutputDevice(string.Empty, _wantedDevice);
            _routedTo = _player.OutputDevice ?? "";
        }
        catch (Exception ex)
        {
            _routedTo = "";
            Log.Warn($"Sending a clip's sound to '{_wantedDevice}' failed — it stays on the default output.", ex);
        }
    }

    public SKSizeI? FrameSize
    {
        get
        {
            lock (_gate)
            {
                return _latest is { } img ? new SKSizeI(img.Width, img.Height) : null;
            }
        }
    }

    // Every reading below is taken by the desk's tick, the stinger service and the VT clock while
    // the player may be leaving (a retire, a dispose on the sweep): a disposed player answers with
    // a safe value rather than an exception that a caller would have to survive.
    public bool IsPlaying
    {
        get
        {
            if (_disposed) return false;
            try
            {
                return _player.IsPlaying;
            }
            catch
            {
                return false;
            }
        }
    }

    public bool IsEnded
    {
        get
        {
            if (_disposed) return false;
            try
            {
                return _player.State == VLCState.Ended;
            }
            catch
            {
                return false;
            }
        }
    }

    public double DurationSeconds
    {
        get
        {
            if (_disposed) return 0;
            try
            {
                var ms = _player.Length;
                return ms > 0 ? ms / 1000.0 : 0;
            }
            catch
            {
                return 0;
            }
        }
    }

    /// <summary>Where the decoder is, seconds from the start; 0 for a capture device, or before the first frame.</summary>
    public double PositionSeconds
    {
        get
        {
            if (_disposed || _isCapture) return 0;
            try
            {
                var ms = _player.Time;
                return ms > 0 ? ms / 1000.0 : 0;
            }
            catch
            {
                return 0;
            }
        }
    }

    /// <summary>A file can be moved along its timeline; a capture device has none.</summary>
    public bool CanSeek
    {
        get
        {
            if (_disposed || _isCapture) return false;
            try
            {
                return _player.IsSeekable || _player.State is VLCState.Ended or VLCState.Stopped;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>
    /// Moves the clip: a playing clip jumps, an ended one plays again (the callbacks are still
    /// wired) and jumps once it rolls. libVLC clamps the time to the file, so a time past the end
    /// ends the clip — which is what "the last ten seconds" of a shorter clip means.
    /// </summary>
    public bool Seek(double seconds)
    {
        if (_disposed || _isCapture) return false;
        try
        {
            var ms = (long)Math.Round(Math.Max(0, seconds) * 1000);
            if (_player.State is VLCState.Ended or VLCState.Stopped)
            {
                _player.Play();
                if (ms > 0) _player.Time = ms;
                return true;
            }
            _player.Time = ms;
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn("Moving a clip failed.", ex);
            return false;
        }
    }

    public string StatusText
    {
        get
        {
            if (_disposed) return "Closed.";
            VLCState state;
            try
            {
                state = _player.State;
            }
            catch
            {
                return "Closed.";
            }
            return state switch
            {
                VLCState.Opening => "Opening…",
                VLCState.Buffering => "Buffering…",
                VLCState.Error => "Playback error — check the file or device.",
                VLCState.Ended => "Ended.",
                VLCState.Stopped => "Stopped.",
                VLCState.Paused => _held ? (_latest is null ? "Pre-rolling…" : "Pre-rolled — holding the first frame.") : "Paused.",
                VLCState.Playing => "Playing (no picture yet)…",
                _ => "Waiting for first frame…",
            };
        }
    }

    public bool DrawFrame(SKCanvas canvas, SKRect dest, SKPaint? paint)
        => DrawFrame(canvas, dest, paint, FrameCrop.None);

    public bool DrawFrame(SKCanvas canvas, SKRect dest, SKPaint? paint, in FrameCrop crop)
        => Draw(canvas, dest, paint, in crop).Drew;

    /// <summary>
    /// The newest frame through a lease — the pool records this sink as drawing it under the
    /// pool's own lock, so the buffer stays until this frame's next — or, for a frame that went
    /// the old way, the image and its clock taken together under the source's lock. What comes
    /// back is the drawn frame's own clock: the image is immutable and outlives any deferred
    /// flush, a pooled one behind the render fence, any other via the retire hold.
    /// </summary>
    public DrawnFrame Draw(SKCanvas canvas, SKRect dest, SKPaint? paint, in FrameCrop crop)
    {
        if (_pool is { } pool && pool.TryLease(out var lease))
        {
            DrawImage(canvas, lease.Image, dest, paint, in crop);
            return new DrawnFrame(true, lease.ArrivalClock, _isCapture, lease.Generation);
        }
        SKImage? image;
        double clock;
        lock (_gate)
        {
            image = _latest;
            clock = _latestClock;
        }
        if (image is null || (_pool is { } p && p.Owns(image))) return DrawnFrame.Nothing;   // a pooled image with no lease: the pool has gone under it
        DrawImage(canvas, image, dest, paint, in crop);
        return new DrawnFrame(true, clock, _isCapture);
    }

    private static void DrawImage(SKCanvas canvas, SKImage image, SKRect dest, SKPaint? paint, in FrameCrop crop)
    {
        if (crop.Any)
        {
            canvas.DrawImage(image, crop.SourceRect(new SKSizeI(image.Width, image.Height)), dest, Patterns.Core.Rendering.DrawUtil.Smooth, paint);
        }
        else
        {
            canvas.DrawImage(image, dest, Patterns.Core.Rendering.DrawUtil.Smooth, paint);
        }
    }

    private uint OnFormat(ref IntPtr opaque, IntPtr chroma, ref uint width, ref uint height, ref uint pitches, ref uint lines)
    {
        // Ask VLC for BGRA at the native size.
        Marshal.Copy(new[] { (byte)'B', (byte)'G', (byte)'R', (byte)'A' }, 0, chroma, 4);
        var pitch = ((width * 4) + 31) & ~31u;
        pitches = pitch;
        lines = height;

        lock (_gate)
        {
            FreeBuffers();
            _width = (int)width;
            _height = (int)height;
            _nativePitch = (int)pitch;
            _native = Marshal.AllocHGlobal(_nativePitch * _height);
            try
            {
                var info = new SKImageInfo(_width, _height, SKColorType.Bgra8888, SKAlphaType.Opaque);
                _pool = new FramePool(info, _nativePitch, FramePool.BuffersFor((long)_nativePitch * _height, MemoryBudget.FramePoolBytesPerSource(MemoryBudget.MachineMB)));
            }
            catch (Exception ex)
            {
                Log.Warn("Frame pool could not be made; frames go the old way.", ex);
                _pool = null;
            }
        }
        return 1;
    }

    private void OnCleanup(ref IntPtr opaque)
    {
        lock (_gate)
        {
            FreeBuffers();
        }
    }

    /// <summary>
    /// The decoder asks where to write the next picture: a pooled buffer when one is free (the
    /// picture's id is its slot, one-based), else the scratch buffer (id nought) and the old way.
    /// </summary>
    private IntPtr OnLock(IntPtr opaque, IntPtr planes)
    {
        var pool = _pool;
        var slot = pool?.Acquire() ?? -1;
        if (slot >= 0)
        {
            Marshal.WriteIntPtr(planes, pool!.Pointer(slot));
            return (IntPtr)(slot + 1);
        }
        Marshal.WriteIntPtr(planes, _native);
        return IntPtr.Zero;
    }

    /// <summary>The decoder finished writing a picture: a pooled one waits, decoded, to be shown or skipped.</summary>
    private void OnUnlock(IntPtr opaque, IntPtr picture, IntPtr planes)
    {
        var slot = (int)picture - 1;
        if (slot >= 0) _pool?.Decoded(slot);
    }

    /// <summary>The show clock the newest frame was handed over at (-1 before one): a sink says how old the picture it drew is.</summary>
    public double FrameClock => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _frameClockBits));

    /// <summary>A capture device's frames are a camera's: their age on the glass is latency the room feels. A file's are not.</summary>
    public bool IsLive => _isCapture;

    private unsafe void OnDisplay(IntPtr opaque, IntPtr picture)
    {
        var slot = (int)picture - 1;
        var arrival = ShowClock.Seconds;                                                              // the frame's arrival, on the show clock: stamped on the frame
        Interlocked.Exchange(ref _frameClockBits, BitConverter.DoubleToInt64Bits(arrival));
        lock (_gate)
        {
            if (slot >= 0 && _pool is { } pool)
            {
                // The pooled picture goes on show as it is: no copy, nothing allocated; the frame it
                // replaced waits on the render fence and is decoded into again.
                var published = pool.Publish(slot, arrival);
                if (published is null) return;
                if (_latest is not null && !pool.Owns(_latest)) RetireImage(_latest);
                _latest = published;
                _latestClock = arrival;
                return;
            }
            if (_native == IntPtr.Zero || _width <= 0) return;

            // The scratch buffer: every pooled buffer was still under a draw, so this frame becomes
            // an immutable image of its own (native-heap copy, no GC pressure) and retires the old way.
            var bmp = new SKBitmap(new SKImageInfo(_width, _height, SKColorType.Bgra8888, SKAlphaType.Opaque));
            var dst = (byte*)bmp.GetPixels();
            var src = (byte*)_native;
            var rowBytes = _width * 4;
            var dstPitch = bmp.RowBytes;
            for (var y = 0; y < _height; y++)
            {
                Buffer.MemoryCopy(src + (long)y * _nativePitch, dst + (long)y * dstPitch, rowBytes, rowBytes);
            }
            bmp.SetImmutable();
            var image = SKImage.FromBitmap(bmp);
            bmp.Dispose(); // the image keeps the (immutable) pixel ref alive

            if (_latest is not null && !(_pool?.Owns(_latest) ?? false)) RetireImage(_latest);
            _latest = image;
            _latestClock = arrival;
        }
    }

    private void FreeBuffers()
    {
        if (_native != IntPtr.Zero)
        {
            Marshal.FreeHGlobal(_native);
            _native = IntPtr.Zero;
        }
        if (_latest is not null && !(_pool?.Owns(_latest) ?? false)) RetireImage(_latest);
        _latest = null;
        _latestClock = -1;
        _pool?.Dispose();   // its buffers go once every sink has drawn past them
        _pool = null;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            _player.Stop();
            _player.Dispose();
            _media.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Video source dispose issue.", ex);
        }
        lock (_gate)
        {
            FreeBuffers();
        }
    }
}
