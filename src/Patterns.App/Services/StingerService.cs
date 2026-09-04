using Avalonia.Threading;
using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Fires VOGs and stingers — one press, no audio engineer needed. A VOG plays over the show: a
/// sound plays on the audio-track outputs while the music ducks underneath; a clip takes every
/// screen and, when it ends, the exact content that was playing before comes back (captured and
/// restored with the same machinery looks use). A stinger is a transition hit: the music fades
/// out instead of ducking, a clip dissolves in, and when it lands an after-policy runs — the
/// content comes back, the frame holds for the operator's take, the caller's next cue GOes
/// through the real gate, or a named look or cue lands. Any after-policy that cannot run puts
/// the show back and says so; an operator changing the content mid-clip always wins and no
/// revert happens. At most one library item owns the screens at a time — the last one fired —
/// so a superseded stinger never runs its after-policy.
/// </summary>
public sealed class StingerService : IDisposable
{
    /// <summary>The after-policy as it was at the moment of the press — editing the row mid-sting must not change what has already fired.</summary>
    private readonly record struct AfterPlan(string Name, StingerAfter After, string Target, bool MusicReturns);

    private readonly AppServices _services;
    private readonly DispatcherTimer _timer;

    private string? _savedLook;                                   // pre-clip content
    private List<(string ScreenId, bool WasCustom)>? _savedCustom; // per-screen pattern flags
    private string _overrideKey = "";                              // content identity we set
    private string _clipPath = "";
    private DateTime _firedUtc;
    private bool _audioActive;
    private string _status = "Ready.";
    private string _ourLabel = "";                                 // the LIVE strip's label while something plays
    private string? _labelBefore;                                  // what it said before, given back at the end

    private AfterPlan? _after;                                     // non-null while a stinger session is open
    private bool _holding;
    private DateTime? _holdUntilUtc;
    private bool _resolving;                                       // the after-policy is not re-entrant
    private double _gainFrom = 1;
    private double _gainTo = 1;
    private DateTime _gainStartUtc;
    private int _gainMs;

    public StingerService(AppServices services)
    {
        _services = services;
        _timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _timer.Tick += (_, _) => Tick(DateTime.UtcNow);
        _timer.Start();
    }

    /// <summary>
    /// A sentence for the operator. Never carries a user-supplied name: the cue rows scan this
    /// line for failure words, and a look called "Missing person" must not flip a good cue.
    /// </summary>
    public string Status => _status;

    /// <summary>A clip is on the screens right now — including a stinger holding its last frame.</summary>
    public bool ClipActive => _clipPath.Length > 0;

    /// <summary>A stinger landed and is holding the screens until the operator takes them.</summary>
    public bool Holding => _holding;

    /// <summary>The held stinger's name, or "" when nothing is holding.</summary>
    public string HoldName => _holding ? _after?.Name ?? "" : "";

    /// <summary>What RunCue's blackout restore and the settings saver read: something momentary owns the screens.</summary>
    public bool OwnsScreens => ClipActive || _holding;

    /// <summary>The VOG on air by name, or "".</summary>
    public string VogOnAir { get; private set; } = "";

    /// <summary>The stinger on air by name, or "".</summary>
    public string StingOnAir { get; private set; } = "";

    /// <summary>The timer body, callable directly (tests drive it with their own clock).</summary>
    public void Poll(DateTime? nowUtc = null) => Tick(nowUtc ?? DateTime.UtcNow);

    // ---- the music --------------------------------------------------------------------

    /// <summary>
    /// What the background music should be multiplied by right now (0–1) — the file track and
    /// break music both read it. A VOG's sound ducks the music a step; a stinger fades it. Both at
    /// once: the quieter wins, so the music never gets louder under something else.
    /// </summary>
    public double MusicGainAt(DateTime nowUtc)
    {
        var duck = _services.MusicDuckActive ? MusicLevel.Duck(_services.State.Stingers.DuckPct) : 1.0;
        var ramp = MusicLevel.Gain(_gainFrom, _gainTo, MusicLevel.Progress(_gainStartUtc, nowUtc, _gainMs));
        return Math.Min(duck, ramp);
    }

    /// <summary>The fade is moving: the music player polls faster while this is true.</summary>
    public bool MusicRamping(DateTime nowUtc)
        => _gainMs > 0 && Math.Abs(_gainFrom - _gainTo) > 0.0001 && MusicLevel.Progress(_gainStartUtc, nowUtc, _gainMs) < 1;

    /// <summary>Ramps the music to a new level from wherever it is now — a reversal never jumps or clicks.</summary>
    private void StartGain(double to, DateTime now)
    {
        _gainFrom = MusicGainAt(now);   // the anchor is the level actually on air
        _gainTo = to;
        _gainStartUtc = now;
        _gainMs = _services.State.Stingers.FadeMs;
    }

    // ---- firing -----------------------------------------------------------------------

    public bool Fire(StingerItemConfig item, DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        var name = item.DisplayName;
        if (string.IsNullOrWhiteSpace(item.Path) || !File.Exists(item.Path))
        {
            _status = $"File missing: {name}";
            return false;
        }
        var isAudio = PlaylistSequencer.IsAudioPath(item.Path);
        var isVideo = !isAudio && PlaylistSequencer.IsVideoPath(item.Path);
        if (!isAudio && !isVideo)
        {
            _status = "VOGs and stingers are sounds or video clips.";
            return false;
        }
        if (_resolving)
        {
            // A stinger's after-policy fired a cue that fires a stinger: the show would run on by
            // itself. The cue fails at this action and the first stinger falls back to Return.
            _status = "A stinger cannot fire from another stinger's after-policy.";
            return false;
        }

        if (isAudio)
        {
            // A sound replaces whatever owns the screens — a running clip or a held frame — and
            // the previous content comes back (a no-op when nothing is up).
            _holding = false;
            _holdUntilUtc = null;
            StopClipIfAny(restore: true);
            if (!_services.AudioPlayer.PlayStinger(item.Path, item.VolumePct))
            {
                _status = "Audio stingers need Windows audio.";
                return false; // nothing opened: no fade, no label, no name
            }
            _audioActive = true;
            OpenSession(item, now);
            _status = $"On air: {name}";
            return true;
        }

        // A clip takes the air and reverts to the air — it works the same whether the operator is
        // programming in the sandbox or driving the program directly. Chained clips keep the
        // original pre-sting content as the revert target, and a hold is released without
        // restoring because the incoming clip is about to own the screens; StopClipIfAny is not
        // called on this path, because it would drop that saved content.
        _holding = false;
        _holdUntilUtc = null;
        var state = _services.AirState;
        if (_savedLook is null)
        {
            _savedLook = LookService.Capture(state);
            _savedCustom = state.Output.Placements.Select(p => (p.ScreenId, p.UseCustomPattern))
                .Concat(state.Output.CanvasNames.Select(c => (c.MemberKey, c.UseCustomPattern)))
                .ToList();
        }

        // Blackout is transport, never sandboxed: PublishBoth copies the live flag onto the
        // frozen program, so lifting it has to happen on the live state or it is overwritten.
        _services.State.Blackout = false;
        if (item.Kind == StingerKind.Sting)
        {
            // The picture dissolves into the clip over the sting fade; 0 is a hard cut, which is a
            // property of the snapshot, not of the show's transition setting.
            var ms = _services.State.Stingers.FadeMs;
            if (ms > 0) _services.Bus.FadeOnNextPublish(ms);
            else _services.Bus.CutOnNextPublish();
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
            foreach (var c in air.Output.CanvasNames)
            {
                c.UseCustomPattern = false; // and every joined canvas
            }
        });
        _overrideKey = ContentKey(_services.AirState);
        _clipPath = item.Path;
        _firedUtc = now;
        _services.PinAirLook(_savedLook); // a crash must come back to the show, never to the clip
        OpenSession(item, now);
        _status = $"Clip on screens: {name}";
        Log.Info($"{StingerLibrary.KindWord(item.Kind)} fired: {name}");
        return true;
    }

    /// <summary>The fade opens only here, after the fire has committed — a failed press leaves the music alone.</summary>
    private void OpenSession(StingerItemConfig item, DateTime now)
    {
        StartGain(item.Kind == StingerKind.Sting ? 0 : 1, now);
        _after = item.Kind == StingerKind.Sting
            ? new AfterPlan(item.DisplayName, item.After, item.AfterTarget, item.MusicReturns)
            : null;
        _services.State.Stingers.PlayingName = item.DisplayName;
        VogOnAir = item.Kind == StingerKind.Vog ? item.DisplayName : "";
        StingOnAir = item.Kind == StingerKind.Sting ? item.DisplayName : "";
        TakeLabel(item.Kind == StingerKind.Sting ? $"STING: {item.DisplayName}" : $"VOG: {item.DisplayName}");
    }

    /// <summary>
    /// Stops whatever is on air. A clip, or a held frame, reverts to the previous content; an
    /// after-policy is cancelled, never fired — STOP means stop. The music always comes back.
    /// </summary>
    public void Stop(DateTime? nowUtc = null)
    {
        var now = nowUtc ?? DateTime.UtcNow;
        if (_audioActive)
        {
            _services.AudioPlayer.ReleaseStingers(); // a fade to silence over the stop fade, never a cut
            _audioActive = false;
        }
        _holding = false;
        _holdUntilUtc = null;
        _after = null;                   // cancelled before anything can read it
        StopClipIfAny(restore: true);    // a hold kept the saved content, so this is the Return path
        StartGain(1, now);
        CloseSession(giveLabelBack: true);
        _status = "Ready.";
    }

    // ---- the poll ---------------------------------------------------------------------

    private void Tick(DateTime now)
    {
        try
        {
            // 1. A sound reached its natural end: the duck lifts (AudioPlayerService), the session
            //    closes here unless a clip or a hold still owns the screens.
            if (_audioActive && !_services.AudioPlayer.StingerPlaying)
            {
                _audioActive = false;
                if (!ClipActive && !_holding)
                {
                    EndSession(now);
                    return;
                }
            }

            // 2. A held stinger: only the operator, the hold limit or STOP moves it.
            if (_holding)
            {
                if (ContentKey(_services.AirState) != _overrideKey)
                {
                    // Their TAKE / GO / look recall is the release. Their choice stands, no revert.
                    Abandon("Operator took over — the sting hold is released.", now);
                    return;
                }
                if (_holdUntilUtc is { } deadline && now > deadline) TimeOutHold(now);
                return;
            }

            if (!ClipActive) return;

            var state = _services.AirState;
            if (ContentKey(state) != _overrideKey)
            {
                // The operator changed the content mid-clip — their choice stands.
                // (Knob tweaks like fit or volume don't count, only what is on screen.)
                Abandon("Operator took over — no revert.", now);
                return;
            }

            var video = InputBus.For(InputKeys.Video(state.Pattern.Media.VideoPath));
            if (video is { IsEnded: true })
            {
                EndSession(now);
                return;
            }

            // No decode after a while (libVLC missing, unreadable file): put the show back. A
            // stuck stinger never runs its after-policy — the clip never played, so moving the
            // show on would be a lie.
            var stuck = video is null || (!video.IsPlaying && video.DurationSeconds <= 0);
            if (stuck && (now - _firedUtc).TotalSeconds > 12) FailedClip(now);
        }
        catch (Exception ex)
        {
            Log.Error("Stinger tick failed.", ex);
            Abandon("Stinger error.", DateTime.UtcNow);
        }
    }

    // ---- the end of a session ---------------------------------------------------------

    /// <summary>What happens when the item lands: a VOG puts the content back; a stinger runs its after-policy.</summary>
    private void EndSession(DateTime now)
    {
        if (_after is not { } plan)
        {
            var wasClip = ClipActive;
            StopClipIfAny(restore: true);
            _status = wasClip ? "Clip finished — previous content back." : "Ready.";
            if (wasClip) Journal(ActionStatus.Done, _status);
            CloseSession(giveLabelBack: true);
            return;
        }

        switch (plan.After)
        {
            case StingerAfter.Manual when ClipActive:
            {
                // The clip's last frame stays on air. The session, the saved content and the
                // music stay as they are until the operator takes the screens, the hold limit
                // passes, or STOP puts the show back.
                _holding = true;
                _holdUntilUtc = _services.State.Stingers.HoldSeconds > 0
                    ? now.AddSeconds(_services.State.Stingers.HoldSeconds)
                    : null;
                _status = "Holding on the sting — TAKE the preview or GO the next cue.";
                _ourLabel = $"STING HOLD: {plan.Name}";
                _services.AirLabel = _ourLabel;
                _services.Journal.Record(ActionOrigin.Stinger.Label, "StingerHold", plan.Name,
                    ActionStatus.Requested.ToString(), _status);
                return;
            }

            case StingerAfter.Next:
            case StingerAfter.Custom:
            {
                var moved = false;
                var afterLabel = "";
                var detail = "";
                _services.BulkEdit(() =>
                {
                    // The follow-on runs while the saved content is still held, so a follow-on
                    // that cannot run — or that changes nothing on the screens — is a Return.
                    moved = RunAfter(plan, out afterLabel, out detail);
                    var pictureMoved = ContentKey(_services.AirState) != _overrideKey;
                    StopClipIfAny(restore: !moved || !pictureMoved);
                });
                _status = moved
                    ? "Sting done — the show moved on."
                    : "Sting could not move the show on — previous content back.";
                Journal(moved ? ActionStatus.Done : ActionStatus.Failed,
                        detail.Length > 0 ? $"{_status} {detail}" : _status);
                SettleMusic(plan, now);
                CloseSession(giveLabelBack: !moved, afterLabel);
                return;
            }

            default:
            {
                // Return, Manual on a sound (nothing on the screens to hold), and any value this
                // build does not understand: the show comes back.
                var wasClip = ClipActive;
                StopClipIfAny(restore: true);
                _status = wasClip ? "Sting done — previous content back." : "Sting done.";
                Journal(ActionStatus.Done, _status);
                SettleMusic(plan, now);
                CloseSession(giveLabelBack: true);
                return;
            }
        }
    }

    /// <summary>Runs Next / Custom. False, with a reason, rather than ever leaving the clip up.</summary>
    private bool RunAfter(in AfterPlan plan, out string afterLabel, out string detail)
    {
        afterLabel = "";
        detail = "";
        if (_resolving)
        {
            detail = "A sting was already moving the show on.";
            return false;
        }
        _resolving = true;
        try
        {
            if (plan.After == StingerAfter.Next)
            {
                var caller = CueStacks.Caller(_services.State);
                var stack = plan.Target.Length == 0 ? caller : CueStacks.Find(_services.State, plan.Target);
                if (stack is null)
                {
                    detail = $"No cue list '{plan.Target}'.";
                    return false;
                }

                if (ReferenceEquals(stack, caller))
                {
                    // The caller's own next cue, through the one gate a caller's GO uses: not
                    // armed, HOLD, blackout, executing, no standby and the lockout all still
                    // refuse. A sting must never open a confirm window on the caller's behalf, so
                    // a cue that asks for one is refused here rather than half-started.
                    if (_services.CueStack.StandbyCue is { RequireConfirm: true })
                    {
                        detail = "The next cue asks for a confirm — press GO.";
                        return false;
                    }
                    var r = _services.CueStack.Go(ActionOrigin.Stinger);
                    detail = r.Message;
                    if (r.Ok) afterLabel = _services.AirLabel;
                    return r.Ok;
                }

                // Any other list (typically the clicker list) uses the verb a cue already uses to
                // hand the room to a list: it skips disabled and broken cues and says what it skipped.
                var lr = _services.Actions.Execute(ShowActionKind.ListGo, ActionOrigin.Stinger, stack.Id);
                detail = lr.Message;
                if (lr.Ok)
                {
                    var rt = _services.Cues.For(stack);
                    if (rt.CurrentIndex >= 0 && rt.CurrentIndex < stack.Cues.Count)
                    {
                        var c = stack.Cues[rt.CurrentIndex];
                        afterLabel = $"{c.Number} {c.Name}";
                    }
                }
                return lr.Ok;
            }

            var target = StingerLibrary.ResolveAfter(_services.State, plan.Target);
            switch (target.Kind)
            {
                case AfterTargetKind.Look:
                {
                    var r = _services.Actions.Execute(ShowActionKind.ApplyLook, ActionOrigin.Stinger, target.Id);
                    detail = r.Message;
                    return r.Ok; // the look recall already named the air
                }
                case AfterTargetKind.Cue:
                {
                    var r = _services.Actions.Execute(ShowActionKind.CueFire, ActionOrigin.Stinger, target.Id);
                    detail = r.Message;
                    if (r.Ok) afterLabel = target.Label;
                    return r.Ok;
                }
                default:
                    detail = plan.Target.Length == 0 ? "No look or cue is chosen." : $"No look or cue '{plan.Target}'.";
                    return false;
            }
        }
        finally
        {
            _resolving = false;
        }
    }

    private void TimeOutHold(DateTime now)
    {
        var plan = _after;
        _holding = false;
        _holdUntilUtc = null;
        StopClipIfAny(restore: true);
        _status = "Sting hold timed out — previous content back.";
        Journal(ActionStatus.Done, _status);
        if (plan is { } p) SettleMusic(p, now);
        CloseSession(giveLabelBack: true);
    }

    /// <summary>The clip never played: the show comes back, the music comes back, and no after-policy runs.</summary>
    private void FailedClip(DateTime now)
    {
        StopClipIfAny(restore: true);
        _status = "Clip could not play — previous content back.";
        Journal(ActionStatus.Failed, _status);
        StartGain(1, now);
        CloseSession(giveLabelBack: true);
    }

    private void SettleMusic(in AfterPlan plan, DateTime now)
    {
        if (plan.MusicReturns)
        {
            StartGain(1, now);
            return;
        }
        // The track stops for good and the next ▶ Play comes up at full. The live model, never
        // EditAir: the track is not in the snapshot and the player reads the model every poll.
        _gainFrom = _gainTo = 1;
        _gainMs = 0;
        _services.State.AudioPlayer.Playing = false;
    }

    private void CloseSession(bool giveLabelBack, string afterLabel = "")
    {
        _after = null;
        _services.State.Stingers.PlayingName = "";
        VogOnAir = "";
        StingOnAir = "";
        _services.PinAirLook(null);
        if (giveLabelBack)
        {
            if (!_audioActive) GiveLabelBack(); // a sound still playing keeps the label a little longer
            return;
        }
        // A cue the sting moved on to names the air the way a caller's GO does — "01.010 Sponsor",
        // whatever its actions said in between; a look names itself.
        if (afterLabel.Length > 0) _services.AirLabel = afterLabel;
        if (!_audioActive)
        {
            _labelBefore = null;
            _ourLabel = "";
        }
    }

    private void Journal(ActionStatus status, string detail)
        => _services.Journal.Record(ActionOrigin.Stinger.Label, ShowActionKind.StingerStop.ToString(),
            _services.State.Stingers.PlayingName, status.ToString(), detail);

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

        var blackoutNow = _services.State.Blackout; // an operator blackout during the clip stands (live flag)
        _services.EditAir(air =>
        {
            LookService.Apply(saved, air);
            air.Blackout = blackoutNow;
            foreach (var (target, wasCustom) in savedCustom ?? new())
            {
                ContentTargets.SetOwnPattern(air, target, wasCustom);
            }
        });
    }

    /// <summary>The operator took the show: nothing reverts, no after runs, the music comes back — leaving it silent would be a trap.</summary>
    private void Abandon(string status, DateTime now)
    {
        _clipPath = "";
        _savedLook = null;
        _savedCustom = null;
        _overrideKey = "";
        _holding = false;
        _holdUntilUtc = null;
        _after = null;
        _services.State.Stingers.PlayingName = "";
        VogOnAir = "";
        StingOnAir = "";
        _status = status;
        StartGain(1, now);
        _services.PinAirLook(null);
        _labelBefore = null; // the operator's own action named what is on air now
        _ourLabel = "";
    }

    /// <summary>The LIVE strip says what plays; a chain keeps the original label to give back.</summary>
    private void TakeLabel(string label)
    {
        _labelBefore ??= _services.AirLabel;
        _ourLabel = label;
        _services.AirLabel = _ourLabel;
    }

    /// <summary>The strip goes back to what it said — unless a look or a cue claimed it meanwhile: that claim stands.</summary>
    private void GiveLabelBack()
    {
        if (_labelBefore is { } before && _services.AirLabel == _ourLabel) _services.AirLabel = before;
        _labelBefore = null;
        _ourLabel = "";
    }

    /// <summary>Stops first, so a clip or a hold is never left for the settings saver to write as the show.</summary>
    public void Dispose()
    {
        Stop();
        _timer.Stop();
    }
}
