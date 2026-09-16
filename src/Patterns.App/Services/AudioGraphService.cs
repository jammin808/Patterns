using System.Diagnostics;
using Avalonia.Threading;
using NAudio.CoreAudioApi;
using NAudio.Wave;
using NAudio.Wave.SampleProviders;
using Patterns.Audio;
using Patterns.Core.Model;
using Patterns.Ndi;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// The audio graph: the routing matrix (<see cref="AudioRouting"/>) applied to the things that
/// make sound. The players of the show's own sound — the playlist, VOGs, stingers, the tone —
/// open on the destinations routed for them and follow the plan's gains through envelopes of
/// their own; this service runs the mixer lanes: one per destination the matrix names, each a
/// mix of the clip soundtracks tapped from the decoders (a device lane) — plus the show's own
/// sound tapped from its players (an NDI lane, which has no players of its own) — at the plan's
/// gain per source, each gain approached with the destination's attack and release so a VOG
/// lands over the rest without a click and the rest breathe back. A device lane plays through
/// WASAPI; an NDI lane hands 10 ms blocks to its sender on a thread of its own. Reconciled on
/// every change of the matrix and every tick while it is on; a desk with the matrix off runs
/// no lane, no timer, no thread.
/// </summary>
public sealed class AudioGraphService : IDisposable
{
    public const int Rate = AudioMix.Rate;
    public const int Channels = AudioMix.Channels;

    /// <summary>A lane's tick: the envelopes advance and the inputs follow the plan this often.</summary>
    public static readonly TimeSpan Tick = TimeSpan.FromMilliseconds(50);

    /// <summary>How far behind a decoder's writes a lane reads: the decoder's bursts arrive before they are due, and the picture is not left waiting.</summary>
    public const int ClipLatencyFrames = 4800;   // 100 ms

    /// <summary>An NDI lane's block: 10 ms, the runtime's comfortable cadence.</summary>
    public const int NdiBlockFrames = 480;

    private readonly AppServices _services;
    private readonly Dictionary<string, Lane> _lanes = new(StringComparer.OrdinalIgnoreCase);
    private DispatcherTimer? _timer;
    private long _rev;
    private DateTime _lastTickUtc;
    private bool _topologyDirty = true;
    private long _lastSignature;

    /// <summary>Times the plan and the lanes were rebuilt this session: once per change, never per tick — a soak reads it flat while nothing changes.</summary>
    public long TopologyRebuilds { get; private set; }

    /// <summary>Ticks that only advanced the envelopes and the meters.</summary>
    public long QuietTicks { get; private set; }
    private volatile string _status = "Routing off.";
    private IReadOnlyList<AudioDestinationPlan> _plan = Array.Empty<AudioDestinationPlan>();

    private sealed class Input
    {
        public required string Source;
        public required TapSampleProvider Provider;
        public DuckEnvelope Env = new();
        /// <summary>The plan's gain for this input and the row's envelope times, cached at the rebuild so the tick advances without resolving the plan.</summary>
        public double Target;
        public int AttackMs = 40;
        public int ReleaseMs = 600;
    }

    private sealed class Lane : IDisposable
    {
        public required string Key;
        public required AudioDestinationKind Kind;
        public required MixingSampleProvider Mixer;
        public required TeeSampleProvider Meter;
        public ISampleProvider Chain = null!;
        public IWavePlayer? Output;
        public MMDevice? Device;
        public int DelayMs;
        public string Error = "";
        public readonly Dictionary<string, Input> Inputs = new(StringComparer.Ordinal);
        public Thread? Thread;
        public volatile bool Run;
        public readonly ManualResetEventSlim Stop = new(false);   // set on close: the lane's pace wait ends at once
        public NdiSender? Sender;
        public float PeakDb = -60;

        public void Dispose()
        {
            Run = false;
            Stop.Set();
            try
            {
                Output?.Stop();
                Output?.Dispose();
                Device?.Dispose();
            }
            catch (Exception ex)
            {
                Log.Warn("Audio lane close issue.", ex);
            }
            Output = null;
            Device = null;
            Thread = null;
            Stop.Dispose();
        }

        /// <summary>The pace wait of the lane's thread, cut short by the close; closed under it, the loop ends at its next check.</summary>
        public void Pause(int ms)
        {
            try { Stop.Wait(ms); }
            catch (ObjectDisposedException) { Run = false; }
        }
    }

    public AudioGraphService(AppServices services)
    {
        _services = services;
    }

    /// <summary>Bumps on every reconcile — what the players compare their opened outputs against.</summary>
    public long Rev => Interlocked.Read(ref _rev);

    /// <summary>The graph in a line: off, or the lanes and what they carry.</summary>
    public string Status => _status;

    /// <summary>The plan as of the last tick (the VOG's state folded in) — the players read their gains from it.</summary>
    public IReadOnlyList<AudioDestinationPlan> Plan => _plan;

    /// <summary>The plan's gain for a source on a device destination, 0 when it is not routed there.</summary>
    public double PlanGain(string deviceName, string source)
    {
        var key = AudioRouting.DeviceDestination(deviceName);
        foreach (var p in _plan)
        {
            if (string.Equals(p.Key, key, StringComparison.OrdinalIgnoreCase)) return p.GainFor(source);
        }
        return 0;
    }

    /// <summary>The attack and release a destination asks for (the defaults when it has no row).</summary>
    public (int AttackMs, int ReleaseMs) EnvelopeFor(string deviceName)
    {
        var row = AudioRouting.Row(_services.State, AudioRouting.DeviceDestination(deviceName));
        return row is null ? (40, 600) : (row.AttackMs, row.ReleaseMs);
    }

    /// <summary>A destination's meter, in dBFS, from its lane; −60 with no lane (the show's own players are not metered here).</summary>
    public double PeakDb(string key) => _lanes.TryGetValue(key, out var lane) ? lane.PeakDb : -60;

    /// <summary>What a lane could not do — a device that would not open — or "" ; the Audio page's line under the destination.</summary>
    public string LaneError(string key) => _lanes.TryGetValue(key, out var lane) ? lane.Error : "";

    /// <summary>The lane's live gain for a source, in dB, for the matrix's cells; the floor when nothing plays there.</summary>
    public double LiveDb(string key, string source)
    {
        if (!_lanes.TryGetValue(key, out var lane)) return Db.Floor;
        double best = 0;
        var any = false;
        foreach (var input in lane.Inputs.Values)
        {
            if (input.Source != source) continue;
            any = true;
            best = Math.Max(best, input.Env.Value);
        }
        return any ? Db.FromGain(best) : Db.Floor;
    }

    /// <summary>The matrix changed — a crosspoint, a row, the monitor rule, a tap mounted or gone: the plan and the lanes are rebuilt on the UI thread now.</summary>
    public void Reconcile()
    {
        Interlocked.Increment(ref _rev);
        _topologyDirty = true;
        if (!UiThread.CheckAccess())
        {
            UiThread.Post(Reconcile);
            return;
        }
        Run();
    }

    /// <summary>The desk's poll: the timer is up while the matrix is on; nothing is rebuilt unless the topology's signature moved.</summary>
    public void Poll()
    {
        if (!UiThread.CheckAccess())
        {
            UiThread.Post(Poll);
            return;
        }
        Run();
    }

    private void Run()
    {
        try
        {
            Apply(ShowClock.UtcNow);
        }
        catch (Exception ex)
        {
            Log.Error("Audio graph reconcile failed.", ex);
            _status = "Audio graph error: " + ex.Message;
        }
    }

    /// <summary>
    /// What the topology depends on, as one number: the matrix on, the VOG on air, every tap's key,
    /// pre-roll flag and buses, the show's own taps. The tick compares it to the last one and
    /// rebuilds the plan only when it moved; a crosspoint edit reaches the graph through the side
    /// effects (the AudioRouting section) and forces one. Nothing allocated.
    /// </summary>
    private long TopologySignature(bool vog)
    {
        var h = new HashCode();
        h.Add(vog);
        h.Add(AudioRouting.FollowSignature(_services.State, _services.AirState));   // round 72: the picture the audience has, never the preview's     // round 69: a take that moves a screen's picture moves its output's lanes
        foreach (var (key, buses, _, preRoll) in _services.Video.Taps())
        {
            h.Add(key);
            h.Add(preRoll);
            h.Add(AudioRouting.SourceForBuses(buses));
        }
        h.Add(_services.AudioPlayer.MusicTap is not null);
        foreach (var (tag, kind, _) in _services.AudioPlayer.VoiceTaps())
        {
            h.Add(tag);
            h.Add((int)kind);
        }
        h.Add(_services.Audio?.ToneTap is not null);
        return h.ToHashCode();
    }

    private void Apply(DateTime nowUtc)
    {
        var state = _services.State;
        if (!state.AudioRouting.Enabled)
        {
            if (_lanes.Count > 0)
            {
                foreach (var lane in _lanes.Values) lane.Dispose();
                _lanes.Clear();
                Log.Info("Audio graph: the matrix is off — every lane closed.");
            }
            _timer?.Stop();
            _timer = null;
            _plan = Array.Empty<AudioDestinationPlan>();
            _status = "Routing off.";
            return;
        }
        if (_timer is null)
        {
            _timer = global::Patterns.App.Services.DeskTimers.Make(Tick);
            // Round 72: the 50 ms tick is the quiet one — the envelopes and the meters advance, and the plan is
            // resolved only when the topology's signature moved. It used to call Reconcile, which forces a
            // rebuild: twenty plans a second on the desk's thread while the matrix was on, for nothing.
            _timer.Tick += (_, _) => Poll();
            _timer.Start();
            _lastTickUtc = nowUtc;
        }
        var dt = Math.Clamp((nowUtc - _lastTickUtc).TotalSeconds, 0, 0.5);
        _lastTickUtc = nowUtc;

        var vog = _services.AudioPlayer.VogSoundPlaying;
        var signature = TopologySignature(vog);
        if (!_topologyDirty && signature == _lastSignature && _plan.Count > 0)
        {
            // Nothing in the topology moved: the envelopes and the meters advance, and that is all.
            Advance(dt);
            QuietTicks++;
            return;
        }
        _topologyDirty = false;
        _lastSignature = signature;
        TopologyRebuilds++;
        var plan = AudioRouting.Resolve(state, _services.AirState, vog);   // round 72: the rows are the configuration's; the sources are the on-air picture's
        _plan = plan;

        // The taps: every tapped clip by the source its pictures make it, then the show's own sound.
        var clipTaps = new List<(string Tag, string Source, AudioRing Ring)>();
        foreach (var (key, buses, tap, preRoll) in _services.Video.Taps())
        {
            if (preRoll) continue;
            clipTaps.Add(("clip:" + key, AudioRouting.SourceForBuses(buses), tap));
        }
        var showTaps = new List<(string Tag, string Source, AudioRing Ring)>();
        if (_services.AudioPlayer.MusicTap is { } music) showTaps.Add(("music", AudioRouting.Music, music));
        foreach (var (tag, kind, ring) in _services.AudioPlayer.VoiceTaps()) showTaps.Add((tag, kind == StingerKind.Vog ? AudioRouting.Vog : AudioRouting.Sting, ring));
        if (_services.Audio?.ToneTap is { } tone) showTaps.Add(("tone", AudioRouting.Tone, tone));

        // Lanes for the plan's destinations; one the plan no longer names closes.
        var wanted = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in plan)
        {
            wanted.Add(p.Key);
            var row = AudioRouting.Row(state, p.Key);
            var lane = _lanes.TryGetValue(p.Key, out var have) && have.DelayMs == p.DelayMs ? have : Open(p, have);
            if (lane is null) continue;
            // The inputs this lane should carry: clip taps on a device lane; everything routed on an NDI lane.
            var taps = p.Kind == AudioDestinationKind.Ndi ? clipTaps.Concat(showTaps) : clipTaps;
            var keep = new HashSet<string>(StringComparer.Ordinal);
            foreach (var (tag, source, ring) in taps)
            {
                if (!p.Carries(source)) continue;
                keep.Add(tag);
                var target = p.GainFor(source);
                if (!lane.Inputs.TryGetValue(tag, out var input))
                {
                    // A new input starts at its gain — a clip's first words are never faded in; only a change moves the envelope.
                    var provider = new TapSampleProvider(ring, ClipLatencyFrames, gain: (float)target);
                    input = new Input { Source = source, Provider = provider };
                    input.Env.Reset(target);
                    lane.Inputs[tag] = input;
                    lane.Mixer.AddMixerInput(provider);
                }
                input.Target = target;
                input.AttackMs = row?.AttackMs ?? 40;
                input.ReleaseMs = row?.ReleaseMs ?? 600;
                input.Env.Advance(target, dt, input.AttackMs, input.ReleaseMs);
                input.Provider.Target = (float)input.Env.Value;
            }
            foreach (var tag in lane.Inputs.Keys.ToList())
            {
                if (keep.Contains(tag)) continue;
                lane.Mixer.RemoveMixerInput(lane.Inputs[tag].Provider);
                lane.Inputs.Remove(tag);
            }
            var peak = lane.Meter.TakePeak();
            var db = peak <= 0 ? Db.Floor : Math.Max(Db.Floor, 20 * Math.Log10(peak));
            lane.PeakDb = (float)Math.Max(db, lane.PeakDb - 60 * dt);   // the meter falls at 60 dB/s
        }
        foreach (var key in _lanes.Keys.ToList())
        {
            if (wanted.Contains(key)) continue;
            _lanes[key].Dispose();
            _lanes.Remove(key);
        }
        var carrying = _lanes.Values.Sum(l => l.Inputs.Count);
        var errors = _lanes.Values.Count(l => l.Error.Length > 0);
        _status = $"Routing on: {_lanes.Count} lane{(_lanes.Count == 1 ? "" : "s")}, {carrying} input{(carrying == 1 ? "" : "s")} playing{(vog ? " · VOG on air" : "")}{(errors > 0 ? $" · {errors} could not open" : "")}.";
    }

    /// <summary>The quiet tick: every input's envelope towards its cached target, every meter's fall — no plan resolved, nothing allocated.</summary>
    private void Advance(double dt)
    {
        foreach (var lane in _lanes.Values)
        {
            foreach (var input in lane.Inputs.Values)
            {
                input.Env.Advance(input.Target, dt, input.AttackMs, input.ReleaseMs);
                input.Provider.Target = (float)input.Env.Value;
            }
            var peak = lane.Meter.TakePeak();
            var db = peak <= 0 ? Db.Floor : Math.Max(Db.Floor, 20 * Math.Log10(peak));
            lane.PeakDb = (float)Math.Max(db, lane.PeakDb - 60 * dt);
        }
    }

    /// <summary>A lane for a destination: its mixer, its meter, its delay, and the device or the NDI thread behind it. Null when the machine cannot (and the row says so).</summary>
    private Lane? Open(AudioDestinationPlan p, Lane? old)
    {
        old?.Dispose();
        if (old is not null) _lanes.Remove(p.Key);
        var format = WaveFormat.CreateIeeeFloatWaveFormat(Rate, Channels);
        var mixer = new MixingSampleProvider(format) { ReadFully = true };
        var meter = new TeeSampleProvider(mixer, null);
        var lane = new Lane { Key = p.Key, Kind = p.Kind, Mixer = mixer, Meter = meter, DelayMs = p.DelayMs };
        lane.Chain = p.DelayMs > 0 ? new DelaySampleProvider(meter, p.DelayMs) : meter;
        _lanes[p.Key] = lane;
        try
        {
            if (p.Kind == AudioDestinationKind.Ndi)
            {
                lane.Run = true;
                lane.Thread = new Thread(() => NdiLoop(lane)) { IsBackground = true, Name = "audio-ndi-" + p.Label, Priority = ThreadPriority.AboveNormal };
                lane.Thread.Start();
                return lane;
            }
            if (!OperatingSystem.IsWindows())
            {
                lane.Error = "Device lanes need Windows audio.";
                return lane;
            }
            var name = AudioRouting.DeviceName(p.Key);
            using var enumerator = new MMDeviceEnumerator();
            var device = name == AudioRouting.ComputerOutput
                ? enumerator.GetDefaultAudioEndpoint(DataFlow.Render, Role.Multimedia)
                : AudioPlayerService.ResolveDevices(enumerator, new[] { name }, _services.AudioEndpoints.Current.Render).FirstOrDefault();
            if (device is null)
            {
                lane.Error = $"'{name}' is not plugged in — nothing routed here is heard.";
                return lane;
            }
            var output = new WasapiOut(device, AudioClientShareMode.Shared, true, 100);
            output.Init(new SampleToWaveProvider(lane.Chain));
            output.Play();
            lane.Output = output;
            lane.Device = device;
            Log.Info($"Audio lane opened on {name}{(p.DelayMs > 0 ? $" ({p.DelayMs} ms)" : "")}.");
            return lane;
        }
        catch (Exception ex)
        {
            lane.Error = $"Could not open: {ex.Message}";
            Log.Warn($"Audio lane for '{p.Label}' could not open.", ex);
            return lane;
        }
    }

    /// <summary>The NDI lane's clock: a block every 10 ms from the chain to the sender, whether or not anything is routed — silence is a signal too.</summary>
    private void NdiLoop(Lane lane)
    {
        var buffer = new float[NdiBlockFrames * Channels];
        var sw = Stopwatch.StartNew();
        var periodMs = NdiBlockFrames * 1000.0 / Rate;
        var next = periodMs;
        var id = AudioRouting.NdiId(lane.Key);
        var misses = 0;
        while (lane.Run)
        {
            var now = sw.Elapsed.TotalMilliseconds;
            if (now < next)
            {
                lane.Pause(1);
                continue;
            }
            next += periodMs;
            if (now - next > 500) next = now;   // a long stall: start the clock again rather than burst
            try
            {
                lane.Chain.Read(buffer, 0, buffer.Length);
                var sender = lane.Sender ??= _services.Ndi.SenderFor(id);
                if (sender is null || !sender.SendAudio(buffer, Channels, Rate))
                {
                    if (++misses == 1 || misses % 1000 == 0) lane.Error = "The NDI send is not up — its audio waits.";
                    lane.Sender = null;
                }
                else if (misses > 0)
                {
                    misses = 0;
                    lane.Error = "";
                }
            }
            catch (Exception ex)
            {
                Log.Warn("NDI audio lane error.", ex);
                lane.Pause(100);
            }
        }
    }

    public void Dispose()
    {
        _timer?.Stop();
        _timer = null;
        foreach (var lane in _lanes.Values) lane.Dispose();
        _lanes.Clear();
    }
}
