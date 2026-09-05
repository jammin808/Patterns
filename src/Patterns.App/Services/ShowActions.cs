using Patterns.Core.LowerThirds;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// The one way to do something to the show. The desk's buttons, the keyboard, the output
/// windows, the remote protocol, the Companion module, the daily schedule and (later) the
/// cue executor all call <see cref="Execute"/>; every call returns a typed result, is
/// written to the show journal with its origin, and raises <see cref="Performed"/> so the
/// view model can resync its editors. Nothing here needs the window to exist.
/// </summary>
public sealed class ShowActions
{
    private readonly AppServices _s;

    public ShowActions(AppServices services) => _s = services;

    /// <summary>Raised on the UI thread after every action, whatever its outcome.</summary>
    public event Action<ShowAction, ActionOrigin, ActionResult>? Performed;

    private ShowState State => _s.State;

    public ActionResult Execute(ShowAction action, ActionOrigin origin)
    {
        ActionResult result;
        try
        {
            result = Run(action, origin);
        }
        catch (Exception ex)
        {
            Log.Error($"Action {action} failed.", ex);
            result = ActionResult.Failed(ex.Message);
        }

        if (action.Kind is not (ShowActionKind.Note or ShowActionKind.Identify))
        {
            _s.Journal.Record(origin.Label, action.Kind.ToString(), JournalTarget(action), result.Status.ToString(), result.Message);
        }
        Performed?.Invoke(action, origin, result);
        return result;
    }

    public ActionResult Execute(ShowActionKind kind, ActionOrigin origin, string target = "", string value = "")
        => Execute(new ShowAction(kind, target, value), origin);

    /// <summary>The journal names looks and break music, not their ids — a caller reading it back should not need the show file.</summary>
    private string JournalTarget(ShowAction action) => action.Kind switch
    {
        ShowActionKind.ApplyLook or ShowActionKind.ApplyLookToPreview => LookService.Find(State, action.Target)?.Name ?? action.Target,
        ShowActionKind.SpotifyPlay when action.Target.Length > 0 => SpotifyLibrary.Find(State, action.Target)?.DisplayName ?? action.Target,
        _ => action.Target,
    };

    // ---- convenience entry points the desk and the windows use ------------------

    /// <param name="note">Where the recall came from, for the status line ("cue 18:00"); ignored when cutting.</param>
    public ActionResult ApplyLook(LookConfig look, ActionOrigin origin, bool cut = false, string note = "")
        => Execute(new ShowAction(ShowActionKind.ApplyLook, look.Id, cut ? "cut" : note), origin);

    /// <summary>F1–F12. False = no look on that key (the key is left for other handlers).</summary>
    public bool ApplyLookHotkey(int slot, ActionOrigin origin)
    {
        if (State.LooksAndCues.Looks.All(l => l.Hotkey != slot)) return false;
        return Execute(new ShowAction(ShowActionKind.ApplyLookHotkey, slot.ToString()), origin).Ok;
    }

    public bool PresenterAdvance(int delta, ActionOrigin origin)
        => Execute(delta >= 0 ? ShowActionKind.PresenterNext : ShowActionKind.PresenterPrev, origin).Ok;

    /// <summary>Runs a cue now, from any list: the desk's FIRE button, and later the caller's GO.</summary>
    public ActionResult FireCue(RunCueConfig cue, ActionOrigin origin)
        => Execute(new ShowAction(ShowActionKind.CueFire, cue.Id), origin);

    /// <summary>Screen by overview number (1-based) → on / off / toggled (null). False = no such screen.</summary>
    public bool SetScreenEnabled(int number, bool? target, IReadOnlyList<ScreenInfo>? screens = null)
    {
        var ordered = Rig.OrderedLivePlacements(State, screens ?? _s.Screens.All);
        if (number < 1 || number > ordered.Count) return false;
        var placement = ordered[number - 1].Placement;
        placement.Enabled = target ?? !placement.Enabled;
        placement.UserPinned = true;
        return true;
    }

    /// <summary>A screen by overview number (1-based), a placement id, or a canvas key — as a content target the rig has; null when it does not.</summary>
    private string? ResolveScreenTarget(string target)
    {
        if (int.TryParse(target, out var number))
        {
            var ordered = Rig.OrderedLivePlacements(State, _s.Screens.All);
            return number >= 1 && number <= ordered.Count ? ordered[number - 1].Placement.ScreenId : null;
        }
        return ContentTargets.IsInRig(State, target) ? target : null;
    }

    /// <summary>Every screen of canvas 'A'/'B'… on or off at once. False = no such canvas.</summary>
    public bool SetGroupEnabled(string letter, bool enabled, IReadOnlyList<ScreenInfo>? screens = null)
    {
        if (letter.Length != 1) return false;
        var groups = Rig.CanvasGroups(State, screens ?? _s.Screens.All);
        var index = char.ToUpperInvariant(letter[0]) - 'A';
        if (index < 0 || index >= groups.Count) return false;
        foreach (var placement in groups[index])
        {
            placement.Enabled = enabled;
            placement.UserPinned = true;
        }
        return true;
    }

    /// <summary>Screen rows for the remote-state JSON and the phone page.</summary>
    public object[] RemoteScreens(IReadOnlyList<ScreenInfo>? screens = null)
    {
        var known = screens ?? _s.Screens.All;
        var groups = Rig.CanvasGroups(State, known);
        return Rig.OrderedLivePlacements(State, known)
            .Select((x, i) => (object)new
            {
                n = i + 1,
                label = Rig.LabelFor(x.Placement, x.Info),
                enabled = x.Placement.Enabled,
                group = Rig.LetterOf(groups, x.Placement),
                locked = !x.Placement.FollowsCues,
                role = x.Placement.Role.ToString().ToLowerInvariant(),
            })
            .ToArray();
    }

    /// <summary>
    /// The daily schedule: fires every cue whose minute has come (once per day) to air, with
    /// origin "schedule", whether or not the sandbox is open. Called from the 1 s poll.
    /// </summary>
    public void RunSchedule(DateTime localNow)
    {
        if (_s.CueStack.SuspendsAutomation) return; // the caller is armed: only GO moves the picture
        foreach (var cue in State.LooksAndCues.Cues)
        {
            if (!LookService.ShouldFire(cue, localNow)) continue;
            cue.LastFiredDate = localNow.Date;
            var look = LookService.Find(State, cue.LookName);
            if (look is null)
            {
                _s.Journal.Record(ActionOrigin.Schedule.Label, ShowActionKind.ApplyLook.ToString(), cue.LookName,
                    ActionStatus.Refused.ToString(), $"Scheduled cue {cue.Time}: look '{cue.LookName}' not found.");
                continue;
            }
            var result = ApplyLook(look, ActionOrigin.Schedule, note: $"cue {cue.Time}");
            if (result.Ok) Log.Info($"Cue {cue.Time}: look '{look.Name}' applied.");
        }
    }

    /// <summary>"Next cue: 'Walk-in' at 18:00 tomorrow" — the same line on the Show page and in STATE.</summary>
    public static string NextScheduledText(ShowState state, DateTime localNow)
    {
        var next = LookService.NextCue(state.LooksAndCues.Cues, localNow);
        return next is { } n
            ? $"Next cue: '{n.Cue.LookName}' at {n.At:HH:mm}{(n.At.Date != localNow.Date ? " tomorrow" : "")}"
            : "No cues scheduled.";
    }

    // ---- the verbs -----------------------------------------------------------

    private ActionResult Run(ShowAction a, ActionOrigin origin)
    {
        switch (a.Kind)
        {
            case ShowActionKind.Note:
                return ActionResult.Done();
            case ShowActionKind.Unknown:
                return ActionResult.Refused("This action comes from a newer build and cannot run here.");

            case ShowActionKind.OutputsOn:
                if (State.Mode == ShowMode.Prep)
                {
                    return ActionResult.Refused("PREP MODE — outputs are held closed. Switch to SHOW in the header when you are at the venue.");
                }
                _s.Outputs.Apply();
                // Output windows take focus when they open and nothing hands it back: the next
                // keystroke would land on the audience surface. The desk owns the keyboard —
                // when the operator pressed the button here. A remote never raises the desk,
                // and neither does anything when an output sits on the desk's own display (the
                // desk would cover the audience surface); Esc twice hands focus back there.
                if (origin.Kind is OriginKind.Desk or OriginKind.Keyboard && !DeskSharesADisplayWithAnOutput())
                {
                    try { _s.MainWindow?.Activate(); } catch { /* headless or minimised — fine */ }
                }
                return _s.Outputs.IsLive ? ActionResult.Done("Outputs on.") : ActionResult.Failed("No enabled screens to output to.");
            case ShowActionKind.OutputsOff:
                _s.Outputs.CloseAll();
                return ActionResult.Done("Outputs off.");
            case ShowActionKind.Identify:
                _s.Identify();
                return ActionResult.Done();

            case ShowActionKind.BlackoutOn:
                State.Blackout = true;
                return ActionResult.Done("Blackout.");
            case ShowActionKind.BlackoutOff:
                State.Blackout = false;
                return ActionResult.Done("Blackout lifted.");
            case ShowActionKind.BlackoutToggle:
                State.Blackout = !State.Blackout;
                return ActionResult.Done(State.Blackout ? "Blackout." : "Blackout lifted.");

            case ShowActionKind.ApplyLook:
            {
                var look = LookService.Find(State, a.Target);
                return look is null ? ActionResult.Refused($"No look named '{a.Target}'.") : ApplyLookToAir(look, a.Value, origin);
            }
            case ShowActionKind.ApplyLookHotkey:
            {
                if (!int.TryParse(a.Target, out var slot)) return ActionResult.Refused($"'{a.Target}' is not an F-key slot.");
                var look = State.LooksAndCues.Looks.FirstOrDefault(l => l.Hotkey == slot);
                if (look is null) return ActionResult.Refused($"No look on F{slot}.");
                // A stray key cannot fire behind the caller: plain F-keys wait while the stack is
                // armed (the show can opt out). The look buttons and a remote's LOOK stay live.
                if (origin.Kind == OriginKind.Keyboard && _s.CueStack.SuspendsAutomation)
                {
                    return ActionResult.Refused($"F{slot} held — the cue stack is armed (looks from the desk or a remote still work).");
                }
                return ApplyLookToAir(look, a.Value, origin);
            }
            case ShowActionKind.ApplyLookToPreview:
            {
                var look = LookService.Find(State, a.Target);
                if (look is null) return ActionResult.Refused($"No look named '{a.Target}'.");
                var ok = false;
                _s.BulkEdit(() => ok = LookService.Apply(look.Json, State));
                if (!ok) return ActionResult.Failed($"Look '{look.Name}' could not be loaded.");
                if (_s.Sandbox.Active)
                {
                    _s.PreviewLookId = look.Id;
                    return ActionResult.Done($"Look '{look.Name}' loaded into the preview — CUT or TAKE to put it on air.");
                }
                // No sandbox: the live model is the program, so the look went on air.
                _s.AirLookId = look.Id;
                _s.AirLabel = look.Name;
                return ActionResult.Done($"Look '{look.Name}' applied.");
            }

            case ShowActionKind.PresenterNext:
            case ShowActionKind.PresenterPrev:
                return Presenter(a.Kind == ShowActionKind.PresenterNext ? +1 : -1, origin);

            case ShowActionKind.CueFire:
            {
                var found = CueStacks.FindCue(State, a.Target);
                if (found is null) return ActionResult.Refused($"No cue '{a.Target}'.");
                return RunCue(found.Value.Stack, found.Value.Cue, origin);
            }
            case ShowActionKind.CueGo:
                return _s.CueStack.Go(origin, a.Target.Length == 0 ? null : a.Target);
            case ShowActionKind.ListArm:
            case ShowActionKind.ListDisarm:
            case ShowActionKind.ListGo:
            case ShowActionKind.ListBack:
            case ShowActionKind.ListReset:
            {
                var stack = CueStacks.Find(State, a.Target);
                if (stack is null) return ActionResult.Refused($"No cue list '{a.Target}'.");
                var rt = _s.Cues.For(stack);
                switch (a.Kind)
                {
                    case ShowActionKind.ListArm:
                        rt.Armed = true;
                        return ActionResult.Done($"{stack.Name} armed.");
                    case ShowActionKind.ListDisarm:
                        rt.Armed = false;
                        return ActionResult.Done($"{stack.Name} disarmed.");
                    case ShowActionKind.ListReset:
                        rt.CurrentIndex = -1;
                        return ActionResult.Done($"{stack.Name} reset to the start.");
                    case ShowActionKind.ListGo:
                        return RunList(stack, +1, origin);
                    default:
                        return RunList(stack, -1, origin);
                }
            }

            case ShowActionKind.CountdownStart:
            {
                if (!double.TryParse(a.Value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var minutes) || minutes <= 0)
                {
                    return ActionResult.Refused("A countdown needs a number of minutes above zero.");
                }
                _s.EditAir(air =>
                {
                    air.Countdown.TargetKind = CountdownTargetKind.Duration;
                    air.Countdown.DurationMinutes = minutes;
                    air.Countdown.ArmedAtUtc = DateTime.UtcNow;
                    air.Countdown.Enabled = true;
                });
                return ActionResult.Done($"Countdown running: {minutes:0.#} min.");
            }
            case ShowActionKind.CountdownStop:
                _s.EditAir(air => air.Countdown.Enabled = false);
                return ActionResult.Done("Countdown off.");
            case ShowActionKind.MessageOn:
                _s.EditAir(air =>
                {
                    if (a.Value.Length > 0) air.Overlays.Message.Text = a.Value;
                    air.Overlays.Message.Enabled = true;
                });
                return ActionResult.Done(a.Value.Length > 0 ? $"Message on: '{a.Value}'." : "Message on.");
            case ShowActionKind.MessageOff:
                _s.EditAir(air => air.Overlays.Message.Enabled = false);
                return ActionResult.Done("Message off.");
            case ShowActionKind.ClockOn:
                _s.EditAir(air => air.Overlays.Clock.Enabled = true);
                return ActionResult.Done("Clock on.");
            case ShowActionKind.ClockOff:
                _s.EditAir(air => air.Overlays.Clock.Enabled = false);
                return ActionResult.Done("Clock off.");
            case ShowActionKind.LowerThirdShow:
            case ShowActionKind.LowerThirdPreview:
            {
                var toPreview = a.Kind == ShowActionKind.LowerThirdPreview;
                if (toPreview && !_s.Sandbox.Active)
                {
                    return ActionResult.Refused("Switch EDIT SAFE on to preview a lower third — without it the preview is the air (AIR puts it on straight away).");
                }
                // An empty target is the design in the preview (for a preview) or on air, else the show's default (★): a person recalled into whatever is showing.
                var design = a.Target.Length == 0
                    ? DefaultLowerThird(toPreview)
                    : State.LowerThirds.Find(a.Target) ?? _s.AirState.LowerThirds.Find(a.Target);
                if (design is null)
                {
                    return ActionResult.Refused(a.Target.Length == 0 ? "No lower third design in the show." : $"Lower third '{a.Target}' not found.");
                }
                LowerThirdEntry? who = null;
                if (a.Value.Length > 0)
                {
                    // A wrong name must never reach the screen: an entry that is not there refuses the show.
                    who = State.LowerThirds.FindEntry(a.Value) ?? _s.AirState.LowerThirds.FindEntry(a.Value);
                    if (who is null) return ActionResult.Refused($"'{a.Value}' is not in the lower-thirds library.");
                }
                var now = ShowClock.UtcNow;
                if (toPreview)
                {
                    // The edited design is the preview's copy: the PREVIEW pane, the multiview's Preview tile and REVIEW draw it.
                    _s.BulkEdit(() =>
                    {
                        if (who is not null) LowerThirdsConfig.Fill(design, who);
                        State.LowerThirds.Show(design, now);
                    });
                    return ActionResult.Done(who is null
                        ? $"Lower third '{design.Name}' in the preview — TAKE puts it on air."
                        : $"Lower third '{design.Name}' in the preview — {who.Name}. TAKE puts it on air.");
                }
                // The designer follows the person who went on air — unless this design is holding the next name in the
                // preview, which stays as the operator left it (the status line names who is on).
                var designInPreview = _s.LowerThirdInPreview() && State.LowerThirds.ActiveId == design.Id;
                if (who is not null && _s.Sandbox.Active && !designInPreview) _s.BulkEdit(() => LowerThirdsConfig.Fill(design, who));
                _s.EditAir(air =>
                {
                    // To air as the design is now: with EDIT SAFE open the program holds its own copy, refreshed on
                    // every show so an edit made since the last one goes with it (a copy left as it was is exactly
                    // "shown on the desk, not on the output"). Without the sandbox the air's design is this one.
                    var onAir = PutOnAir(air, design);
                    if (who is not null) LowerThirdsConfig.Fill(onAir, who);
                    air.LowerThirds.Show(onAir, now);
                });
                return ActionResult.Done(who is null ? $"Lower third '{design.Name}' on." : $"Lower third '{design.Name}' on — {who.Name}.");
            }
            case ShowActionKind.LowerThirdHide:
                _s.EditAir(air => air.LowerThirds.Hide(ShowClock.UtcNow));
                return ActionResult.Done("Lower third off.");
            case ShowActionKind.LowerThirdPreviewOff:
            {
                if (!_s.Sandbox.Active) return ActionResult.Done("The preview is the air while EDIT SAFE is off — nothing to clear.");
                var preview = State.LowerThirds;
                if (!_s.LowerThirdInPreview()) return ActionResult.Done("The preview is clear.");
                var name = preview.Active?.Name ?? "";
                _s.BulkEdit(() => preview.Hide(ShowClock.UtcNow));
                return ActionResult.Done($"Lower third '{name}' leaving the preview.");
            }
            case ShowActionKind.LowerThirdTake:
            {
                if (!_s.Sandbox.Active) return ActionResult.Refused("Nothing is in the preview — EDIT SAFE is off, so AIR puts a lower third on straight away.");
                var preview = State.LowerThirds;
                var now = ShowClock.UtcNow;
                if (!_s.LowerThirdInPreview() || preview.Active is not { } next || !LowerThirdClock.IsLive(preview, now))
                {
                    return ActionResult.Refused("No lower third in the preview to take — PVW one first.");
                }
                _s.EditAir(air =>
                {
                    var onAir = PutOnAir(air, next);
                    air.LowerThirds.Show(onAir, now);   // afresh: it arrives on air the way it was designed to
                });
                _s.BulkEdit(() => preview.Hide(now));   // the preview clears, ready for the next name
                return ActionResult.Done(next.PersonName.Length > 0
                    ? $"Lower third '{next.Name}' taken to air — {next.PersonName}."
                    : $"Lower third '{next.Name}' taken to air.");
            }
            case ShowActionKind.LowerThirdUpdate:
            {
                if (!_s.Sandbox.Active) return ActionResult.Done("EDIT SAFE is off — the design on air is the design you edit; it is already live.");
                var air = _s.AirState.LowerThirds;
                if (!air.IsShowing || air.Active is not { } onAir) return ActionResult.Refused("No lower third on air to update.");
                var edited = State.LowerThirds.Find(onAir.Id);
                if (edited is null) return ActionResult.Refused($"Lower third '{onAir.Name}' is no longer in the show — HIDE it, or AIR another.");
                if (LowerThirdsConfig.SameDesign(edited, onAir)) return ActionResult.Done($"Lower third '{onAir.Name}' on air is already as edited.");
                // In place: the copy changes under the same id and the instants stay, so it neither leaves nor arrives again.
                _s.EditAir(a2 => a2.LowerThirds.Put(edited.Clone(newId: false)));
                return ActionResult.Done($"Lower third '{edited.Name}' updated on air.");
            }
            case ShowActionKind.AudioVolume:
            {
                // The track is not in the snapshot: the player reads the live model every poll,
                // sandbox or not, so this is the audio's air seam.
                if (!CueActionSpec.TryParsePercent(a.Value, out var percent))
                {
                    return ActionResult.Refused("Audio volume needs a number from 0 to 125.");
                }
                State.AudioPlayer.VolumePct = percent;
                return ActionResult.Done($"Audio volume {percent:0}%.");
            }

            case ShowActionKind.ScreenOn:
            case ShowActionKind.ScreenOff:
            case ShowActionKind.ScreenToggle:
            {
                bool? target = a.Kind switch
                {
                    ShowActionKind.ScreenOn => true,
                    ShowActionKind.ScreenOff => false,
                    _ => null,
                };
                if (int.TryParse(a.Target, out var number))
                {
                    return SetScreenEnabled(number, target) ? ActionResult.Done() : ActionResult.Refused($"No screen {number}.");
                }
                var placement = State.Output.Placements.FirstOrDefault(p => p.ScreenId == a.Target);
                if (placement is null) return ActionResult.Refused($"No screen '{a.Target}'.");
                placement.Enabled = target ?? !placement.Enabled;
                placement.UserPinned = true;
                return ActionResult.Done();
            }
            case ShowActionKind.ScreenLock:
            case ShowActionKind.ScreenUnlock:
            case ShowActionKind.ScreenLockToggle:
            {
                var target = ResolveScreenTarget(a.Target);
                if (target is null) return ActionResult.Refused($"No screen '{a.Target}'.");
                var locked = a.Kind switch
                {
                    ShowActionKind.ScreenLock => true,
                    ShowActionKind.ScreenUnlock => false,
                    _ => !ScreenRoles.IsLocked(State, target),
                };
                // "Keep what you show": the picture on air for the target, whichever state holds
                // it — the live model, or the frozen program while the sandbox is open. Both get
                // the lock, so a look to air and the next TAKE agree.
                var air = _s.AirState;
                var source = ScreenRoles.ResolveMirror(air, target);
                var showing = ContentTargets.UsesOwnPattern(air, source)
                    ? air.Independent.FirstOrDefault(x => x.ScreenId == source)?.Pattern ?? air.Pattern
                    : air.Pattern;
                var picture = JsonUtil.ClonePattern(showing);
                _s.BulkEdit(() => ScreenRoles.SetLocked(State, target, locked, picture));
                if (_s.Sandbox.Active) _s.EditAir(program => ScreenRoles.SetLocked(program, target, locked, picture));
                var label = Rig.Geometry(State, _s.Screens.All).LabelFor(State, target);
                return ActionResult.Done(locked
                    ? $"{label} locked — it keeps its picture through looks, cues, TAKE ALL and stingers."
                    : $"{label} follows looks, cues and TAKE again.");
            }
            case ShowActionKind.CanvasOn:
            case ShowActionKind.CanvasOff:
            {
                var on = a.Kind == ShowActionKind.CanvasOn;
                if (a.Target.Length == 1)
                {
                    return SetGroupEnabled(a.Target, on) ? ActionResult.Done() : ActionResult.Refused($"No canvas {a.Target.ToUpperInvariant()}.");
                }
                var groups = Rig.CanvasGroups(State, _s.Screens.All);
                var group = groups.FirstOrDefault(g => CanvasNameConfig.KeyFor(g.Select(m => m.ScreenId)) == a.Target);
                if (group is null) return ActionResult.Refused($"No canvas '{a.Target}'.");
                foreach (var p in group)
                {
                    p.Enabled = on;
                    p.UserPinned = true;
                }
                return ActionResult.Done();
            }

            case ShowActionKind.AudioPlay:
                State.AudioPlayer.Playing = true;
                return ActionResult.Requested("Audio track playing.");
            case ShowActionKind.AudioStop:
                State.AudioPlayer.Playing = false;
                return ActionResult.Done("Audio track stopped.");

            case ShowActionKind.SpotifyPlay:
            {
                // Break music is not in the snapshot: the service reads the live model every poll,
                // sandbox or not — the same air seam as the audio track. Never Failed and never
                // Refused for a network or setup fact: RunCue aborts a cue at the first non-Ok
                // result and the GO gate skips a cue the validator calls Broken, so a cue must not
                // stop, or be skipped, because Spotify is unhappy.
                // A name that resolves to nothing is a programming error, on or off: refused either
                // way, so the validator's Broken set and the executor's Refused set are one set.
                SpotifyItemConfig? item = null;
                if (a.Target.Length > 0)
                {
                    item = SpotifyLibrary.Find(State, a.Target);
                    if (item is null) return ActionResult.Refused($"No break music '{a.Target}'.");
                    if (!SpotifyUri.IsValid(item.Uri)) return ActionResult.Refused($"'{item.DisplayName}' has no valid Spotify link.");
                }
                if (!State.Spotify.Enabled) return ActionResult.Done("Break music is off — nothing played.");
                if (item is not null)
                {
                    State.Spotify.PlayingId = item.Id;
                    State.Spotify.Playing = true;
                    return ActionResult.Requested($"Break music: {item.DisplayName}.");
                }
                State.Spotify.Playing = true;
                return ActionResult.Requested("Break music playing.");
            }
            case ShowActionKind.SpotifyPause:
                if (!State.Spotify.Enabled) return ActionResult.Done("Break music is off.");
                State.Spotify.Playing = false;
                _s.Spotify.PokeNow();
                return ActionResult.Requested("Break music pausing.");
            case ShowActionKind.SpotifyNext:
                if (!State.Spotify.Enabled) return ActionResult.Done("Break music is off.");
                _s.Spotify.SkipRequested = true;   // consumed by the next poll; never a synchronous socket
                return ActionResult.Requested("Break music: next track.");
            case ShowActionKind.SpotifyVolume:
            {
                if (!CueActionSpec.TryParseLevel(a.Value, out var level))
                {
                    return ActionResult.Refused("Break music level needs a number from 0 to 100.");
                }
                if (!State.Spotify.Enabled) return ActionResult.Done("Break music is off.");
                State.Spotify.LevelPct = level;
                return ActionResult.Done($"Break music level {level:0}%.");
            }
            case ShowActionKind.FreezeOn:
            case ShowActionKind.FreezeOff:
            case ShowActionKind.FreezeToggle:
            {
                // A runtime flag on the bus like the review: every output holds its frame from the
                // next snapshot on; the desk's own views keep moving; the show file never carries it.
                var on = a.Kind switch
                {
                    ShowActionKind.FreezeOn => true,
                    ShowActionKind.FreezeOff => false,
                    _ => !_s.Bus.Frozen,
                };
                if (on == _s.Bus.Frozen) return ActionResult.Done(on ? "Already frozen." : "Not frozen.");
                _s.Bus.Frozen = on;
                _s.PublishRuntime();
                return ActionResult.Done(on ? "FREEZE — every output holds its frame." : "Freeze released.");
            }

            case ShowActionKind.FadeToBlack:
            case ShowActionKind.FadeUp:
            {
                // A blackout with a fade of its own: the value's milliseconds, or the show's time.
                var down = a.Kind == ShowActionKind.FadeToBlack;
                if (State.Blackout == down) return ActionResult.Refused(down ? "Already black." : "Not black.");
                var ms = int.TryParse(a.Value, System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var v) && v > 0
                    ? v
                    : (int)Math.Round(State.Transition.DurationMs);
                _s.Bus.FadeOnNextPublish(ms);
                State.Blackout = down;
                var secs = (ms / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
                return ActionResult.Done(down ? $"Fading to black over {secs} s." : $"Fading up over {secs} s.");
            }

            case ShowActionKind.LookBack:
            {
                var id = _s.PreviousAirLookId;
                var back = id.Length > 0 ? LookService.Find(State, id) : null;
                if (back is null) return ActionResult.Refused("No previous look to go back to.");
                // The recall makes today's look the previous one, so LOOK BACK twice is a swap.
                return ApplyLookToAir(back, a.Value, origin);
            }

            case ShowActionKind.ReviewOn:
            case ShowActionKind.ReviewOff:
            case ShowActionKind.ReviewToggle:
            {
                // A runtime flag on the bus, never in the show file: PublishRuntime carries it to the
                // frozen program's snapshot too, which is the one every multiview renders.
                var on = a.Kind switch
                {
                    ShowActionKind.ReviewOn => true,
                    ShowActionKind.ReviewOff => false,
                    _ => !_s.Bus.ReviewOnMultiview,
                };
                if (on == _s.Bus.ReviewOnMultiview) return ActionResult.Done(on ? "Review is already on." : "Review is already off.");
                _s.Bus.ReviewOnMultiview = on;
                _s.PublishRuntime();
                return ActionResult.Done(on ? "Review: the preview fills every multiview." : "Review off: the multiviews show their tiles.");
            }

            case ShowActionKind.ToneOn:
                State.Tone.Enabled = true;
                return ActionResult.Done("Tone on.");
            case ShowActionKind.ToneOff:
                State.Tone.Enabled = false;
                return ActionResult.Done("Tone off.");

            // The live duck: sound only, never the picture — so never the sandbox, never the label.
            case ShowActionKind.DuckOn:
                _s.Stingers.SetDuck(true);
                return ActionResult.Done($"Ducked to {State.Stingers.DuckToPct:0}% for a live announcement.");
            case ShowActionKind.DuckOff:
                _s.Stingers.SetDuck(false);
                return ActionResult.Done("Duck lifted.");
            case ShowActionKind.DuckToggle:
                _s.Stingers.SetDuck(!State.Stingers.DuckActive);
                return ActionResult.Done(State.Stingers.DuckActive
                    ? $"Ducked to {State.Stingers.DuckToPct:0}% for a live announcement."
                    : "Duck lifted.");

            case ShowActionKind.StingerFire:
            {
                var item = StingerLibrary.Find(State, a.Target);
                if (item is null) return ActionResult.Refused($"No VOG or stinger '{a.Target}'.");
                if (!StingerLibrary.KindMatches(item, a.Value, out var wanted))
                {
                    // A button that says VOG must never fire a stinger: refused, and the item named.
                    return ActionResult.Refused($"'{item.DisplayName}' is a {StingerLibrary.KindWord(item.Kind)}, not a {wanted}.");
                }
                if (!_s.Stingers.Fire(item)) return ActionResult.Failed(_s.Stingers.Status);
                return ActionResult.Requested(_s.Stingers.Status); // the service owns the strip's label while it plays
            }
            case ShowActionKind.StingerStop:
                _s.Stingers.Stop();
                return ActionResult.Done(_s.Stingers.Status);

            case ShowActionKind.PlaylistPart:
            {
                // Parts drive what the audience sees, sandbox open or not.
                var options = MediaLocator.FindActivePlaylist(_s.AirState)?.Playlist ?? _s.AirState.Pattern.Media.Playlist;
                PlaylistSequencer.Normalize(options);
                var index = int.TryParse(a.Target, out var n)
                    ? n - 1
                    : options.Sections.ToList().FindIndex(x => string.Equals(x.Name, a.Target, StringComparison.OrdinalIgnoreCase));
                if (index < 0 || index >= options.Sections.Count) return ActionResult.Refused($"No playlist part '{a.Target}'.");
                _s.EditAir(_ => options.ActiveSection = index);
                _s.AirLabel = $"PART: {options.Sections[index].Name}";
                _s.AirLookId = ""; // a part replaced the look's picture; the tally follows the picture from here
                return ActionResult.Done($"Playlist part '{options.Sections[index].Name}' is on air.");
            }

            case ShowActionKind.StreamStart:
                State.Stream.Active = true;
                return ActionResult.Requested("Stream starting…");
            case ShowActionKind.StreamStop:
                State.Stream.Active = false;
                return ActionResult.Done("Stream stopped.");

            case ShowActionKind.Take:
            case ShowActionKind.Cut:
            {
                if (!_s.Sandbox.Active)
                {
                    return ActionResult.Refused("Open EDIT SAFE (the sandbox) first — build the look, then CUT or TAKE it to air.");
                }
                var cut = a.Kind == ShowActionKind.Cut;
                // Un-armed tiles and locked screens (a confidence monitor, an info screen) keep their picture.
                var unarmed = _s.Arming.Unarmed;
                var locked = ScreenRoles.LockedTargets(State, Rig.Targets(State, _s.Screens.All));
                IReadOnlyCollection<string> held = locked.Count == 0 ? unarmed : unarmed.Concat(locked).ToHashSet(StringComparer.Ordinal);
                _s.Sandbox.SendAll(cut, held);
                var rearmed = _s.Sandbox.Active ? " EDIT SAFE re-armed." : "";
                var scope = held.Count == 0 ? "on every screen" : $"on the armed tiles ({held.Count} kept their picture)";
                return ActionResult.Done((cut
                    ? $"CUT — sandbox is now the program {scope}."
                    : $"TAKE — sandbox faded up {scope}.") + rearmed);
            }

            case ShowActionKind.StopAll:
                _s.Stingers.Stop();              // both kinds; a clip or a held frame reverts; an after is cancelled, never fired
                State.AudioPlayer.Playing = false;
                State.Spotify.Playing = false;   // the service issues the pause and retries until it lands…
                _s.Spotify.PokeNow();            // …starting on this turn, not up to 400 ms later
                State.Tone.Enabled = false;
                return ActionResult.Done("Stopped: audio track, break music, VOGs and stingers (previous content back), tone. Outputs, blackout and the stream are untouched.");

            default:
                return ActionResult.Refused($"Unknown action '{a.Kind}'.");
        }
    }

    /// <summary>
    /// Fires a look to air. EDIT SAFE protects what you are <em>building</em>, not what you
    /// <em>fire</em>: with the sandbox open the audience gets the look and the preview keeps
    /// showing the operator's in-progress edit. "cut" switches without the crossfade.
    /// </summary>
    private ActionResult ApplyLookToAir(LookConfig look, string value, ActionOrigin origin)
    {
        var sandboxed = _s.Sandbox.Active;
        // Value: "cut", a fade in ms (this recall only), or anything else for the show default.
        if (CueActionSpec.TryParseTransition(value, out var cut, out var fadeMs))
        {
            if (cut) _s.Bus.CutOnNextPublish();
            else if (fadeMs >= 0) _s.Bus.FadeOnNextPublish(fadeMs);
        }
        var ok = false;
        _s.EditAir(air =>
        {
            SyncLookLowerThird(air, look.Json);
            ok = LookService.Apply(look.Json, air, rearmCountdown: true);
        });
        if (!ok) return ActionResult.Failed($"Look '{look.Name}' could not be applied.");
        _s.AirLabel = look.Name;
        _s.AirLookId = look.Id;
        // "cue 18:00" from the schedule: the status line says which cue fired, as it used to.
        var prefix = value.StartsWith("cue ", StringComparison.OrdinalIgnoreCase) ? $"Cue {value[4..]}: " : "";
        var text = prefix + (sandboxed
            ? $"Look '{look.Name}' on air — your preview edit is untouched."
            : $"Look '{look.Name}' applied.");
        if (RunLookMusic(look, origin) is not { } music) return ActionResult.Done(text);
        // The music is asynchronous like every break-music verb: a Requested look settles on it.
        var line = $"{text} {music.Message}";
        return music.Status == ActionStatus.Requested ? ActionResult.Requested(line) : ActionResult.Done(line);
    }

    /// <summary>
    /// A look can start or pause break music: the same verb a cue or the remote would use, run
    /// after the picture has landed, journaled on its own with the look's origin — and never
    /// able to stop the look, whatever Spotify or the library says. Null when the look leaves
    /// the music alone.
    /// </summary>
    private ActionResult? RunLookMusic(LookConfig look, ActionOrigin origin)
    {
        if (look.MusicItemId.Length == 0) return null;
        var action = look.MusicItemId == LookConfig.PauseMusic
            ? new ShowAction(ShowActionKind.SpotifyPause)
            : new ShowAction(ShowActionKind.SpotifyPlay, look.MusicItemId);
        ActionResult result;
        try
        {
            result = Run(action, origin);
        }
        catch (Exception ex)
        {
            Log.Error($"Look '{look.Name}': music step {action} failed.", ex);
            result = ActionResult.Failed(ex.Message);
        }
        _s.Journal.Record(origin.Label, action.Kind.ToString(), JournalTarget(action), result.Status.ToString(),
            $"Look '{look.Name}': {result.Message}");
        return result;
    }

    /// <summary>The clicker: Page Down / Up, NEXT / PREV and the Show page drive the clicker list.</summary>
    private ActionResult Presenter(int delta, ActionOrigin origin)
        => RunList(CueStacks.Clicker(State), delta, origin);

    /// <summary>
    /// Steps a list: the next (or previous) cue that can run is fired; a disabled or broken cue
    /// is skipped in the direction of travel — the list never sticks on one mid-show — and the
    /// status line says what was skipped.
    /// </summary>
    private ActionResult RunList(CueStackConfig stack, int delta, ActionOrigin origin)
    {
        var rt = _s.Cues.For(stack);
        var cues = stack.Cues;
        var skipped = new List<string>();
        var current = rt.CurrentIndex;
        for (var hops = 0; hops < cues.Count; hops++)
        {
            if (PresenterLogic.Advance(current, cues.Count, delta, stack.LoopAtEnd) is not { } idx)
            {
                return ActionResult.Refused(skipped.Count == 0
                    ? $"No cue in {stack.Name}."
                    : $"No cue left to run in {stack.Name} — skipped {string.Join(", ", skipped)}.");
            }
            var cue = cues[idx];
            if (!cue.Enabled)
            {
                skipped.Add($"{cue.Number} (disabled)");
                current = idx;
                continue;
            }
            var check = CueValidator.ValidateOne(State, cue, _s.ValidationContext);
            if (check.BrokenCount > 0)
            {
                skipped.Add($"{cue.Number} ({check.ReasonFor(cue.Id)})");
                current = idx;
                continue;
            }
            rt.CurrentIndex = idx;
            var result = RunCue(stack, cue, origin);
            if (!result.Ok) return result;
            var prefix = stack.IsClicker ? "Presenter" : stack.Name;
            return ActionResult.Done(skipped.Count == 0
                ? $"{prefix} {idx + 1}/{cues.Count}: {cue.Name}"
                : $"{prefix} {idx + 1}/{cues.Count}: {cue.Name} — skipped {string.Join(", ", skipped)}");
        }
        return ActionResult.Failed($"No cue in {stack.Name} can run — {string.Join(", ", skipped)}.");
    }

    /// <summary>
    /// Runs one cue: re-checked against the live state first (a hard issue refuses it, program
    /// untouched), then its actions in order inside one bulk edit so the screens change once,
    /// stopping at the first failure ("failed at action k of n"; earlier actions stand). Blackout
    /// is transport: it is put back afterwards unless the cue says otherwise.
    /// </summary>
    internal ActionResult RunCue(CueStackConfig stack, RunCueConfig cue, ActionOrigin origin)
    {
        var label = $"{cue.Number} {cue.Name}";
        var rt = _s.Cues.For(stack);
        if (!cue.Enabled)
        {
            rt.LastOutcome = "Refused";
            return ActionResult.Refused($"{label} is disabled.");
        }
        var check = CueValidator.ValidateOne(State, cue, _s.ValidationContext);
        if (check.BrokenCount > 0)
        {
            rt.LastOutcome = "Refused";
            return ActionResult.Refused($"{label}: {check.ReasonFor(cue.Id)}");
        }

        var blackoutBefore = _s.State.Blackout;
        var explicitBlackout = cue.Actions.Any(x => x.Kind is CueActionKind.BlackoutOn or CueActionKind.BlackoutOff);
        var total = cue.Actions.Count;
        var done = 0;
        var requested = false;
        ActionResult? failure = null;
        _s.BulkEdit(() =>
        {
            foreach (var action in cue.Actions)
            {
                if (action.Kind == CueActionKind.Note)
                {
                    done++;
                    continue;
                }
                var mapped = ToShowAction(action);
                ActionResult r;
                try
                {
                    r = Run(mapped, origin);
                }
                catch (Exception ex)
                {
                    Log.Error($"Cue {label}: action {mapped} failed.", ex);
                    r = ActionResult.Failed(ex.Message);
                }
                Performed?.Invoke(mapped, origin, r); // editors resync; the cue itself is what gets journaled
                if (!r.Ok)
                {
                    failure = r;
                    break;
                }
                if (r.Status == ActionStatus.Requested) requested = true;
                done++;
            }
            // Blackout is transport, put back after the cue — unless a clip took the screens in
            // this cue (or a stinger is holding them): it lifted blackout on purpose so the
            // audience sees it, and it puts the previous state back itself when it ends.
            if (!explicitBlackout && !_s.Stingers.OwnsScreens) _s.State.Blackout = blackoutBefore;
        });

        rt.LastCueId = cue.Id;
        if (failure is not null)
        {
            rt.LastOutcome = "Failed";
            return ActionResult.Failed($"{label}: failed at action {done + 1} of {total} — {failure.Message}");
        }
        rt.LastOutcome = requested ? "Requested" : "Done";
        return requested
            ? ActionResult.Requested($"{label} — {CueSummary.Describe(State, cue)} (still settling).")
            : ActionResult.Done($"{label} — {CueSummary.Describe(State, cue)}");
    }

    /// <summary>A typed cue action as the action layer runs it.</summary>
    public static ShowAction ToShowAction(CueActionConfig a) => a.Kind switch
    {
        CueActionKind.ApplyLook => new ShowAction(ShowActionKind.ApplyLook, a.Target, a.Value),
        CueActionKind.AudioPlay => new ShowAction(ShowActionKind.AudioPlay),
        CueActionKind.AudioStop => new ShowAction(ShowActionKind.AudioStop),
        CueActionKind.AudioVolume => new ShowAction(ShowActionKind.AudioVolume, "", a.Value),
        CueActionKind.SpotifyPlay => new ShowAction(ShowActionKind.SpotifyPlay, a.Target),
        CueActionKind.SpotifyPause => new ShowAction(ShowActionKind.SpotifyPause),
        CueActionKind.SpotifyNext => new ShowAction(ShowActionKind.SpotifyNext),
        CueActionKind.SpotifyVolume => new ShowAction(ShowActionKind.SpotifyVolume, "", a.Value),
        CueActionKind.StingerFire => new ShowAction(ShowActionKind.StingerFire, a.Target),
        CueActionKind.StingerStop => new ShowAction(ShowActionKind.StingerStop),
        CueActionKind.PlaylistPart => new ShowAction(ShowActionKind.PlaylistPart, a.Target),
        CueActionKind.StreamStart => new ShowAction(ShowActionKind.StreamStart),
        CueActionKind.StreamStop => new ShowAction(ShowActionKind.StreamStop),
        CueActionKind.BlackoutOn => new ShowAction(ShowActionKind.BlackoutOn),
        CueActionKind.BlackoutOff => new ShowAction(ShowActionKind.BlackoutOff),
        CueActionKind.ScreenOn => new ShowAction(ShowActionKind.ScreenOn, a.Target),
        CueActionKind.ScreenOff => new ShowAction(ShowActionKind.ScreenOff, a.Target),
        CueActionKind.ScreenLock => new ShowAction(ShowActionKind.ScreenLock, a.Target),
        CueActionKind.ScreenUnlock => new ShowAction(ShowActionKind.ScreenUnlock, a.Target),
        CueActionKind.CanvasOn => new ShowAction(ShowActionKind.CanvasOn, a.Target),
        CueActionKind.CanvasOff => new ShowAction(ShowActionKind.CanvasOff, a.Target),
        CueActionKind.CountdownStart => new ShowAction(ShowActionKind.CountdownStart, "", a.Value),
        CueActionKind.CountdownStop => new ShowAction(ShowActionKind.CountdownStop),
        CueActionKind.MessageOn => new ShowAction(ShowActionKind.MessageOn, "", a.Value),
        CueActionKind.MessageOff => new ShowAction(ShowActionKind.MessageOff),
        CueActionKind.ClockOn => new ShowAction(ShowActionKind.ClockOn),
        CueActionKind.ClockOff => new ShowAction(ShowActionKind.ClockOff),
        CueActionKind.LowerThirdShow => new ShowAction(ShowActionKind.LowerThirdShow, a.Target, a.Value),
        CueActionKind.LowerThirdHide => new ShowAction(ShowActionKind.LowerThirdHide),
        CueActionKind.LowerThirdPreview => new ShowAction(ShowActionKind.LowerThirdPreview, a.Target, a.Value),
        CueActionKind.LowerThirdTake => new ShowAction(ShowActionKind.LowerThirdTake),
        CueActionKind.DuckOn => new ShowAction(ShowActionKind.DuckOn),
        CueActionKind.DuckOff => new ShowAction(ShowActionKind.DuckOff),
        CueActionKind.ListArm => new ShowAction(ShowActionKind.ListArm, a.Target),
        CueActionKind.ListDisarm => new ShowAction(ShowActionKind.ListDisarm, a.Target),
        CueActionKind.ListGo => new ShowAction(ShowActionKind.ListGo, a.Target),
        CueActionKind.ListBack => new ShowAction(ShowActionKind.ListBack, a.Target),
        CueActionKind.ListReset => new ShowAction(ShowActionKind.ListReset, a.Target),
        CueActionKind.Note => new ShowAction(ShowActionKind.Note),
        _ => new ShowAction(ShowActionKind.Unknown),
    };

    /// <summary>
    /// The lower third a person goes into when none is named: for a preview the one already in the
    /// preview; then the one on air; then the show's default (★, else the first); then whatever was
    /// last shown. The edited design is preferred (it is the one the desk edits and the program copies).
    /// </summary>
    private LowerThirdDesign? DefaultLowerThird(bool forPreview = false)
    {
        var air = _s.AirState.LowerThirds;
        var edited = State.LowerThirds;
        if (forPreview && _s.LowerThirdInPreview() && edited.Active is { } inPreview) return inPreview;
        if (air.IsShowing && air.ActiveId.Length > 0 && (edited.Find(air.ActiveId) ?? air.Find(air.ActiveId)) is { } onAir) return onAir;
        if (edited.DefaultDesign is { } chosen) return chosen;
        var id = air.ActiveId.Length > 0 ? air.ActiveId : edited.ActiveId;
        return (id.Length > 0 ? edited.Find(id) ?? air.Find(id) : null) ?? air.DefaultDesign;
    }

    /// <summary>
    /// The design the air draws: the design itself when the air is the live state, else the frozen
    /// program's own copy, refreshed from the edited design now (same id: its row and its instants stay).
    /// </summary>
    private LowerThirdDesign PutOnAir(ShowState air, LowerThirdDesign design)
        => ReferenceEquals(air, State) ? design : air.LowerThirds.Put(design.Clone(newId: false));

    /// <summary>
    /// A look about to land on the frozen program names a lower third by id: the program gets the
    /// edited design's current copy first, so the recall shows the design as it is now, and shows it
    /// at all when it was made while EDIT SAFE was open (the frozen clone never saw it).
    /// </summary>
    private void SyncLookLowerThird(ShowState air, string lookJson)
    {
        if (ReferenceEquals(air, State)) return;
        var id = LookService.LowerThirdIdOf(lookJson);
        if (string.IsNullOrEmpty(id)) return;
        var edited = State.LowerThirds.Find(id);
        if (edited is not null) air.LowerThirds.Put(edited.Clone(newId: false));
    }

    private bool DeskSharesADisplayWithAnOutput()
    {
        try
        {
            var desk = _s.MainWindow;
            if (desk is null) return false;
            var deskScreen = desk.Screens.ScreenFromWindow(desk);
            if (deskScreen is null) return false;
            foreach (var window in _s.Outputs.Windows)
            {
                var info = _s.Screens.All.FirstOrDefault(s => s.Id == window.TargetScreenId);
                if (info is not null && info.Bounds.Intersects(deskScreen.Bounds)) return true;
            }
        }
        catch
        {
            // No screen information (headless) — nothing to protect.
        }
        return false;
    }

}
