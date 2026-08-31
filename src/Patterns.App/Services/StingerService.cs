using Avalonia.Threading;
using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Fires stingers — one press, no audio engineer needed. A sound plays over everything on
/// the audio-track outputs while the music track ducks underneath; a video clip takes over
/// every screen and, when it ends, the exact content that was playing before comes back
/// (captured and restored with the same machinery looks use). If the operator changes the
/// content mid-clip, their change wins and no revert happens.
/// </summary>
public sealed class StingerService : IDisposable
{
    private readonly AppServices _services;
    private readonly DispatcherTimer _timer;

    private string? _savedLook;                                   // pre-clip content
    private List<(string ScreenId, bool WasCustom)>? _savedCustom; // per-screen pattern flags
    private string _overrideKey = "";                              // content identity we set
    private string _clipPath = "";
    private DateTime _firedUtc;
    private bool _audioActive;
    private string _status = "Ready.";

    public StingerService(AppServices services)
    {
        _services = services;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Tick();
        _timer.Start();
    }

    public string Status => _status;

    /// <summary>A clip is on the screens right now (its end will revert the content).</summary>
    public bool ClipActive => _clipPath.Length > 0;

    /// <summary>The timer body, callable directly (tests drive it without waiting on the clock).</summary>
    public void Poll() => Tick();

    public bool Fire(StingerItemConfig item)
    {
        var name = item.DisplayName;
        if (string.IsNullOrWhiteSpace(item.Path) || !File.Exists(item.Path))
        {
            _status = $"File missing: {name}";
            return false;
        }

        if (PlaylistSequencer.IsAudioPath(item.Path))
        {
            StopClipIfAny(restore: true); // a sound replaces a running clip
            if (!_services.AudioPlayer.PlayStinger(item.Path, item.VolumePct))
            {
                _status = "Audio stingers need Windows audio.";
                return false;
            }
            _audioActive = true;
            _services.State.Stingers.PlayingName = name;
            _status = $"On air: {name}";
            return true;
        }

        if (!PlaylistSequencer.IsVideoPath(item.Path))
        {
            _status = "Stingers are sounds or video clips.";
            return false;
        }

        // A clip takes the air and reverts to the air — it works the same whether the
        // operator is programming in the sandbox or driving the program directly.
        var state = _services.AirState;
        if (_savedLook is null)
        {
            // Chained clips keep the original pre-stinger content as the revert target.
            _savedLook = LookService.Capture(state);
            _savedCustom = state.Output.Placements.Select(p => (p.ScreenId, p.UseCustomPattern)).ToList();
        }

        _services.EditAir(air =>
        {
            air.Blackout = false;
            air.Pattern.Kind = PatternKind.Media;
            var media = air.Pattern.Media;
            media.Source = MediaSource.Video;
            media.VideoPath = item.Path;
            media.Loop = false;
            media.Mute = false;
            media.VolumePct = item.VolumePct;
            foreach (var p in air.Output.Placements)
            {
                p.UseCustomPattern = false; // the clip owns every screen
            }
        });
        _overrideKey = ContentKey(_services.AirState);
        _clipPath = item.Path;
        _firedUtc = DateTime.UtcNow;
        _services.State.Stingers.PlayingName = name;
        _status = $"Clip on screens: {name}";
        Log.Info($"Stinger fired: {name}");
        return true;
    }

    /// <summary>Stops whatever stinger is on air; a clip reverts to the previous content.</summary>
    public void Stop()
    {
        if (_audioActive)
        {
            _services.AudioPlayer.StopStinger();
            _audioActive = false;
        }
        StopClipIfAny(restore: true);
        _services.State.Stingers.PlayingName = "";
        _status = "Ready.";
    }

    private void Tick()
    {
        try
        {
            if (_audioActive && !_services.AudioPlayer.StingerPlaying)
            {
                // Natural end of a sound: duck lifts (AudioPlayerService), name clears here.
                _audioActive = false;
                if (!ClipActive)
                {
                    _services.State.Stingers.PlayingName = "";
                    _status = "Ready.";
                }
            }

            if (!ClipActive) return;

            var state = _services.AirState;
            if (ContentKey(state) != _overrideKey)
            {
                // The operator changed the content mid-clip — their choice stands.
                // (Knob tweaks like fit or volume don't count, only what is on screen.)
                Abandon("Operator took over — no revert.");
                return;
            }

            var video = InputBus.For(InputKeys.Video(state.Pattern.Media.VideoPath));
            if (video is { IsEnded: true })
            {
                StopClipIfAny(restore: true);
                _services.State.Stingers.PlayingName = "";
                _status = "Clip finished — previous content back.";
                return;
            }

            // No decode after a while (libVLC missing, unreadable file): put the show back.
            var stuck = video is null || (!video.IsPlaying && video.DurationSeconds <= 0);
            if (stuck && (DateTime.UtcNow - _firedUtc).TotalSeconds > 12)
            {
                StopClipIfAny(restore: true);
                _services.State.Stingers.PlayingName = "";
                _status = "Clip could not play — previous content back.";
            }
        }
        catch (Exception ex)
        {
            Log.Error("Stinger tick failed.", ex);
            Abandon("Stinger error.");
        }
    }

    /// <summary>What is on screen, ignoring knobs: pattern kind, media source and file.</summary>
    private static string ContentKey(ShowState state)
        => $"{state.Pattern.Kind}|{state.Pattern.Media.Source}|{state.Pattern.Media.VideoPath}";

    private void StopClipIfAny(bool restore)
    {
        if (!ClipActive) return;
        var saved = _savedLook;
        var savedCustom = _savedCustom;
        _clipPath = "";
        _savedLook = null;
        _savedCustom = null;
        _overrideKey = "";
        if (!restore || saved is null) return;

        var blackoutNow = _services.AirState.Blackout; // an operator blackout during the clip stands
        _services.EditAir(air =>
        {
            LookService.Apply(saved, air);
            air.Blackout = blackoutNow;
            foreach (var (screenId, wasCustom) in savedCustom ?? new())
            {
                var placement = air.Output.Placements.FirstOrDefault(p => p.ScreenId == screenId);
                if (placement is not null) placement.UseCustomPattern = wasCustom;
            }
        });
    }

    private void Abandon(string status)
    {
        _clipPath = "";
        _savedLook = null;
        _savedCustom = null;
        _overrideKey = "";
        _services.State.Stingers.PlayingName = "";
        _status = status;
    }

    public void Dispose() => _timer.Stop();
}
