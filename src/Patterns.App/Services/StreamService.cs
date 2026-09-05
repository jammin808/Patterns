using Avalonia.Threading;
using LibVLCSharp.Shared;
using Patterns.Core.Model;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.App.Services;

/// <summary>
/// Streaming output: captures the chosen screen through libVLC's screen input, encodes
/// h264 once at the configured resolution/frame rate, and duplicates the same encode to
/// up to two destinations (RTMP/SRT/UDP). Fully isolated — an encoder or network failure
/// changes a status line, never the show. Windows + libVLC (full build) only.
/// </summary>
public sealed class StreamService : IDisposable
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _timer;
    private MediaPlayer? _player;
    private Media? _media;
    private string _activeKey = "";
    private DateTime _startedUtc;
    private int _destinations;
    private string _status = "Not streaming.";

    public StreamService(AppServices services)
    {
        _services = services;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(1000) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public string Status => _status;

    /// <summary>The timer body, callable directly (tests drive it without waiting on the clock).</summary>
    public void Poll() => Tick();

    private void Tick()
    {
        var cfg = _services.State.Stream;
        try
        {
            var urls = cfg.Destinations.Where(d => d.Enabled && !string.IsNullOrWhiteSpace(d.Url))
                .Select(d => d.Url.Trim()).Take(2).ToList();

            if (!cfg.Active || urls.Count == 0)
            {
                Stop();
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

            if (!OperatingSystem.IsWindows())
            {
                _status = "Streaming runs on Windows.";
                return;
            }

            if (!_services.Video.EnsureAvailable() || _services.Video.SharedVlc is not { } vlc)
            {
                _status = "Streaming needs libVLC — use the full build (or install VLC).";
                return;
            }

            var rendered = IsRendered(cfg.SourceScreenId);
            var rect = rendered ? SKRectI.Empty : SourceRect(cfg.SourceScreenId);
            var fps = StreamMrl.EffectiveFps(cfg, _services.State.Output.MasterFps);
            var key = $"{(rendered ? "render:" + cfg.SourceScreenId : rect.ToString())}|{cfg.Width}x{cfg.Height}@{fps}|{cfg.VideoKbps}|{cfg.AudioDevice}|{cfg.AudioKbps}|{string.Join(";", urls)}";
            if (key != _activeKey)
            {
                Stop();
                if (rendered)
                {
                    // The engine draws the target into raw frames; libVLC pulls them through the memory input.
                    if (StreamMrl.BuildRendered(cfg, urls, _services.State.Output.MasterFps) is not { } renderedPlan) return;
                    _renderer = new StreamRenderer(_services.Bus, cfg.SourceScreenId, cfg.Width, cfg.Height, fps);
                    _renderer.Start();
                    _input = new FeedMediaInput(_renderer.Feed);
                    _media = new Media(vlc, _input, renderedPlan.Options);
                }
                else
                {
                    if (StreamMrl.Build(cfg, rect, urls, _services.State.Output.MasterFps) is not { } plan) return;
                    _media = new Media(vlc, plan.Mrl, FromType.FromLocation, plan.Options);
                }
                _player = new MediaPlayer(_media);
                if (!_player.Play())
                {
                    _status = "Encoder failed to start — check the destination URLs.";
                    Stop();
                    return;
                }
                _activeKey = key;
                _startedUtc = DateTime.UtcNow;
                _destinations = urls.Count;
                Log.Info($"Streaming started: {cfg.Width}x{cfg.Height}@{fps}, {cfg.VideoKbps} kbps, {urls.Count} destination(s).");
            }

            if (_player is { } player)
            {
                if (player.State == VLCState.Error)
                {
                    _status = "Stream error — check URL/key and bandwidth, then start again.";
                    cfg.Active = false;
                    Stop();
                    return;
                }
                var up = DateTime.UtcNow - _startedUtc;
                _status = $"LIVE · {_destinations} destination{(_destinations == 1 ? "" : "s")} · " +
                          $"{cfg.Width}×{cfg.Height}@{fps} · {cfg.VideoKbps / 1000.0:0.#} Mbps · {up:hh\\:mm\\:ss}" +
                          (_renderer is not null ? " · rendered" : " · desktop capture");
            }
        }
        catch (Exception ex)
        {
            Log.Error("Streaming failed.", ex);
            _status = $"Stream error: {ex.Message}";
            cfg.Active = false;
            Stop();
        }
    }

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

    /// <summary>The engine-fed source while one runs (tests and the super-check read it).</summary>
    public StreamRenderer? Renderer => _renderer;

    private StreamRenderer? _renderer;
    private FeedMediaInput? _input;

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

    private void Stop()
    {
        if (_player is null && _media is null && _renderer is null) return;
        try
        {
            _player?.Stop();
            _player?.Dispose();
            _media?.Dispose();
            _renderer?.Dispose();
            _input?.Dispose();
        }
        catch (Exception ex)
        {
            Log.Warn("Stream stop issue.", ex);
        }
        _player = null;
        _media = null;
        _renderer = null;
        _input = null;
        _activeKey = "";
    }

    public void Dispose()
    {
        _timer.Stop();
        Stop();
    }
}
