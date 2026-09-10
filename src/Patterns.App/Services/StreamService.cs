using Avalonia.Threading;
using LibVLCSharp.Shared;
using Patterns.Core.Model;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.App.Services;

/// <summary>
/// Streaming output: one screen — captured off the desktop by libVLC, or rendered by the engine —
/// encoded once at the configured resolution and rate and duplicated to up to two destinations
/// (RTMP/SRT/UDP). The encoder runs in a process of its own (<see cref="EncoderHost"/>: this same
/// exe with <c>--host encoder</c>), fed through a shared frame ring and supervised here: a fault in
/// libVLC ends that process, not the desk, and it is started again with backoff while the show
/// carries on; a crash loop stands down with words; the status line says what happened. Windows
/// only (libVLC's screen capture and its DirectShow audio); needs the full build or an installed
/// VLC for the encoder to find.
/// </summary>
public sealed class StreamService : IDisposable
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _timer;
    private static readonly TimeSpan LeavingPatience = TimeSpan.FromSeconds(10);   // a host is killed 3 s after QUIT; this is for one that will not die
    private ChildProcess? _encoder;
    private ChildProcess? _leaving;   // the encoder just stopped, until its process is out: a stream key takes one publisher at a time
    private DateTime _leavingSinceUtc;
    private SharedFrameRing? _ring;
    private StreamRenderer? _renderer;
    private string _activeKey = "";
    private string _heldKey = "";     // a set-up the encoder said it cannot run (no libVLC): shown, not retried every second
    private string _heldReason = "";
    private DateTime _startedUtc;
    private int _destinations;
    private bool _rendered;
    private string _status = "Not streaming.";
    private long _framesSeen;
    private DateTime _framesSeenUtc;
    private double _fps;
    private StreamHealth _health = StreamHealth.Read(StreamFacts.None);

    public StreamService(AppServices services)
    {
        _services = services;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    /// <summary>Streaming runs on Windows (libVLC's screen capture, its DirectShow audio); the tests may say otherwise to drive the service here.</summary>
    public static bool RunsHere { get; set; } = OperatingSystem.IsWindows();

    /// <summary>How the encoder process is started — the real launcher, or a scripted one in the tests.</summary>
    public IChildLauncher Launcher { get; set; } = ProcessChildLauncher.Default;

    public string Status => _status;

    /// <summary>
    /// How the stream is doing this second — the light, the word and the line every surface shows.
    /// Read on the desk's poll and on the wire; never computed twice, so the rail, the Stream page,
    /// the Show panel, the phone and a Stream Deck key can never disagree about it.
    /// </summary>
    public StreamHealth Health => _health;

    /// <summary>The timer body, callable directly (tests drive it without waiting on the clock).</summary>
    public void Poll() => Tick();

    /// <summary>The encoder process while one runs (the super-check and the tests read it).</summary>
    public ChildProcess? Encoder => _encoder;

    /// <summary>The engine-fed source while one runs.</summary>
    public StreamRenderer? Renderer => _renderer;

    /// <summary>The ring the engine draws into and the encoder reads, while a rendered stream runs.</summary>
    public SharedFrameRing? Ring => _ring;

    private void Tick()
    {
        var cfg = _services.State.Stream;
        try
        {
            TickCore(cfg);
        }
        finally
        {
            // Whatever the tick did or could not do, the health line follows it — a fault that
            // returned early must still reach the rail.
            _health = ReadHealth(cfg);
        }
    }

    /// <summary>The frames the encoder has taken, and the rate they are going in at over the last second.</summary>
    private void MeasureRate()
    {
        var frames = _encoder?.Frames ?? 0;
        var now = DateTime.UtcNow;
        if (_framesSeenUtc == default || frames < _framesSeen)
        {
            _framesSeen = frames;
            _framesSeenUtc = now;
            _fps = 0;
            return;
        }
        var seconds = (now - _framesSeenUtc).TotalSeconds;
        if (seconds < 0.5) return;
        _fps = (frames - _framesSeen) / seconds;
        _framesSeen = frames;
        _framesSeenUtc = now;
    }

    private StreamHealth ReadHealth(StreamConfig cfg)
    {
        MeasureRate();
        var destinations = cfg.Destinations.Count(d => d.Enabled && !string.IsNullOrWhiteSpace(d.Url));
        var enc = _encoder;
        var up = _startedUtc == default || enc is null ? 0 : (DateTime.UtcNow - _startedUtc).TotalSeconds;
        // A held set-up, a fault and a machine that cannot stream at all are all the same thing to
        // an operator: it was asked for and it is not going out. The line says which.
        var trouble = _heldReason.Length > 0 ? _heldReason
            : enc is { Phase: ChildPhase.GaveUp } ? enc.Words
            : enc is { Failed: true } ? enc.LastError
            : cfg.Active && destinations > 0 && !RunsHere ? "streaming runs on Windows"
            : "";
        return StreamHealth.Read(new StreamFacts(
            Wanted: cfg.Active,
            Configured: destinations > 0,
            Destinations: destinations,
            Encoding: enc is { Phase: ChildPhase.Running },
            Starting: enc is null or { Phase: ChildPhase.Starting or ChildPhase.Restarting },
            Frames: enc?.Frames ?? 0,
            Fps: _fps,
            TargetFps: StreamMrl.EffectiveFps(cfg, _services.State.Output.MasterFps),
            Restarts: enc?.Restarts ?? 0,
            Trouble: trouble,
            UpSeconds: up));
    }

    private void TickCore(StreamConfig cfg)
    {
        try
        {
            var urls = cfg.Destinations.Where(d => d.Enabled && !string.IsNullOrWhiteSpace(d.Url))
                .Select(d => d.Url.Trim()).Take(2).ToList();

            if (!cfg.Active || urls.Count == 0)
            {
                Stop();
                _heldKey = "";
                _status = !cfg.Active
                    ? "Not streaming."
                    : "No destination enabled — add an RTMP/SRT/UDP URL below.";
                return;
            }

            // Prep is pre-programming: nothing leaves the machine — not on a cable, not on the
            // network. The stream stays armed and comes up by itself in SHOW.
            if (_services.State.Mode == ShowMode.Prep)
            {
                Stop();
                _status = "PREP — the stream is held closed; it starts when you switch to SHOW.";
                return;
            }

            if (!RunsHere)
            {
                _status = "Streaming runs on Windows.";
                return;
            }

            var rendered = IsRendered(cfg.SourceScreenId);
            var rect = rendered ? SKRectI.Empty : SourceRect(cfg.SourceScreenId);
            var fps = StreamMrl.EffectiveFps(cfg, _services.State.Output.MasterFps);
            var key = $"{(rendered ? "render:" + cfg.SourceScreenId : rect.ToString())}|{cfg.Width}x{cfg.Height}@{fps}|{cfg.VideoKbps}|{cfg.AudioDevice}|{cfg.AudioKbps}|{cfg.AudioDelayMs}|{string.Join(";", urls)}";
            if (key == _heldKey)
            {
                _status = _heldReason;
                return;
            }
            if (key != _activeKey)
            {
                Stop();
                if (_leaving is { } old)
                {
                    // The old encoder's process still holds the destination — an RTMP key takes one
                    // publisher, and a server that refuses the second would fail the new encoder for what
                    // was a bitrate edit — so the new one waits for it to be out (3 s at most: QUIT, then killed).
                    if (!old.HostsGone && DateTime.UtcNow - _leavingSinceUtc < LeavingPatience)
                    {
                        _status = $"Waiting for the previous encoder (pid {old.Pid}) to let go of the destination before the new one starts…";
                        return;
                    }
                    _leaving = null;
                }
                EncoderPlan plan;
                if (rendered)
                {
                    // The engine draws the target into the ring; the encoder process pulls the frames through libVLC's memory input.
                    if (StreamMrl.BuildRendered(cfg, urls, _services.State.Output.MasterFps) is not { } renderedPlan) return;
                    _ring = SharedFrameRing.Create(SharedFrameRing.NameFor("stream"), cfg.Width, cfg.Height);
                    _renderer = new StreamRenderer(_services.Bus, cfg.SourceScreenId, _ring, fps);
                    _renderer.Start();
                    plan = new EncoderPlan(EncoderPlan.Rendered, renderedPlan.Mrl, renderedPlan.Options, _ring.Address, cfg.Width, cfg.Height, fps);
                }
                else
                {
                    if (StreamMrl.Build(cfg, rect, urls, _services.State.Output.MasterFps) is not { } capturePlan) return;
                    plan = new EncoderPlan(EncoderPlan.Capture, capturePlan.Mrl, capturePlan.Options, "", cfg.Width, cfg.Height, fps);
                }
                _encoder = new ChildProcess(EncoderHost.Role, Launcher, line => Log.Info(line));
                _encoder.Start(JsonUtil.Serialize(plan));
                _activeKey = key;
                _startedUtc = DateTime.UtcNow;
                _destinations = urls.Count;
                _rendered = rendered;
                cfg.LastError = "";
                Log.Info($"Streaming started: {cfg.Width}x{cfg.Height}@{fps}, {cfg.VideoKbps} kbps, {urls.Count} destination(s), " +
                         $"{(rendered ? "rendered" : "desktop capture")} — the encoder in its own process (pid {_encoder.Pid}).");
            }

            if (_encoder is not { } enc) return;
            enc.Poll();

            if (_renderer is { Failure.Length: > 0 } failed)
            {
                // The engine's side stopped drawing: a stream of one frozen frame must not read LIVE.
                _status = $"Stream error — {failed.Failure}.";
                cfg.LastError = failed.Failure;
                cfg.Active = false;
                Stop();
                return;
            }

            if (enc.Failed)
            {
                if (enc.Phase == ChildPhase.GaveUp)
                {
                    _status = $"Encoder failed — {enc.Words}. Press START to try again.";
                    cfg.LastError = enc.Words;
                    cfg.Active = false;
                    Stop();
                    return;
                }
                var (code, text) = HostProtocol.SplitError(enc.LastError);
                if (code == HostProtocol.ErrorLibVlc)
                {
                    // Not a fault: this machine has no libVLC. Said, held, and not tried again every second.
                    Stop();
                    _heldKey = key;
                    _heldReason = text.Length > 0 ? text : "Streaming needs libVLC — use the full build (or install VLC).";
                    _status = _heldReason;
                    return;
                }
                _status = code == HostProtocol.ErrorStart ? text : $"Stream error — {text}; check URL/key and bandwidth, then start again.";
                cfg.LastError = text.Length > 0 ? text : enc.LastError;
                cfg.Active = false;
                Stop();
                return;
            }

            var restarts = enc.Restarts > 0 ? $", started again {enc.Restarts}×" : "";
            var up = DateTime.UtcNow - _startedUtc;
            _status = enc.Phase switch
            {
                ChildPhase.Starting => $"Starting the encoder (pid {enc.Pid}{restarts})…",
                ChildPhase.Restarting => $"Encoder restarting — {enc.Words}; the show is untouched.",
                // STARTED is the process running its plan; the destination is reached when libVLC plays.
                ChildPhase.Running when IsConnecting(enc.HostState) =>
                    $"Connecting to {_destinations} destination{(_destinations == 1 ? "" : "s")} (encoder pid {enc.Pid}{restarts})…",
                ChildPhase.Running => $"LIVE · {_destinations} destination{(_destinations == 1 ? "" : "s")} · " +
                                      $"{cfg.Width}×{cfg.Height}@{fps} · {cfg.VideoKbps / 1000.0:0.#} Mbps · {up:hh\\:mm\\:ss}" +
                                      (_rendered ? " · rendered" : " · desktop capture") +
                                      $" · encoder in its own process (pid {enc.Pid}{restarts})",
                _ => enc.Words,
            };
        }
        catch (Exception ex)
        {
            Log.Error("Streaming failed.", ex);
            _status = $"Stream error: {ex.Message}";
            cfg.LastError = ex.Message;
            cfg.Active = false;
            Stop();
        }
    }

    /// <summary>libVLC's state words from the host's beat before its stream output is up: the process runs, the destination is not reached yet.</summary>
    private static bool IsConnecting(string hostState)
        => hostState is nameof(VLCState.Opening) or nameof(VLCState.Buffering) or nameof(VLCState.NothingSpecial);

    /// <summary>
    /// A real display is captured off the desktop (cheapest, and it shows everything on that
    /// display); anything else — the stream's own screen, a joined canvas, a planned screen — is
    /// rendered by the engine.
    /// </summary>
    public bool IsRendered(string sourceId)
    {
        if (string.IsNullOrEmpty(sourceId)) return false;
        return _services.Screens.Real.All(s => s.Id != sourceId);
    }

    /// <summary>Pixel rect of the streamed screen on the OS desktop (screen:// crops to it).</summary>
    private SKRectI SourceRect(string screenId)
    {
        var screens = _services.Screens.All;
        var chosen = screens.FirstOrDefault(s => s.Id == screenId)
                     ?? screens.FirstOrDefault(s =>
                         _services.State.Output.Placements.FirstOrDefault(p => p.ScreenId == s.Id)?.Enabled == true)
                     ?? screens.FirstOrDefault();
        if (chosen is null) return SKRectI.Create(0, 0, 1920, 1080);
        var b = chosen.Bounds;
        return SKRectI.Create(b.X, b.Y, b.Width, b.Height);
    }

    /// <summary>
    /// The encoder first (STOP and QUIT queued; killed off this thread if it lingers, and remembered
    /// until its process is out), then the renderer, whose thread owns its surfaces and the ring under
    /// them and releases both when it ends; the ring by hand only when no renderer ever had it.
    /// </summary>
    private void Stop()
    {
        if (_encoder is null && _renderer is null && _ring is null) return;
        try
        {
            _encoder?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Encoder stop issue.", ex);
        }
        if (_encoder is { } leaving)
        {
            _leaving = leaving;
            _leavingSinceUtc = DateTime.UtcNow;
        }
        try
        {
            if (_renderer is not null) _renderer.Dispose();
            else _ring?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Stream renderer stop issue.", ex);
        }
        _renderer = null;
        _encoder = null;
        _ring = null;
        _activeKey = "";
    }

    public void Dispose()
    {
        _timer.Stop();
        Stop();
    }
}
