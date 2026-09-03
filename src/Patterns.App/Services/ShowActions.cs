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

    /// <summary>The journal names looks, not their ids — a caller reading it back should not need the show file.</summary>
    private string JournalTarget(ShowAction action)
        => action.Kind is ShowActionKind.ApplyLook or ShowActionKind.ApplyLookToPreview
            ? LookService.Find(State, action.Target)?.Name ?? action.Target
            : action.Target;

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
                    return ActionResult.Refused("PREP MODE — outputs are held closed. Switch to SHOW (Outputs tab) when you are at the venue.");
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
                return look is null ? ActionResult.Refused($"No look named '{a.Target}'.") : ApplyLookToAir(look, a.Value);
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
                return ApplyLookToAir(look, a.Value);
            }
            case ShowActionKind.ApplyLookToPreview:
            {
                var look = LookService.Find(State, a.Target);
                if (look is null) return ActionResult.Refused($"No look named '{a.Target}'.");
                var ok = false;
                _s.BulkEdit(() => ok = LookService.Apply(look.Json, State));
                if (!ok) return ActionResult.Failed($"Look '{look.Name}' could not be loaded.");
                return ActionResult.Done(_s.Sandbox.Active
                    ? $"Look '{look.Name}' loaded into the preview — CUT or TAKE to put it on air."
                    : $"Look '{look.Name}' applied.");
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
            case ShowActionKind.ToneOn:
                State.Tone.Enabled = true;
                return ActionResult.Done("Tone on.");
            case ShowActionKind.ToneOff:
                State.Tone.Enabled = false;
                return ActionResult.Done("Tone off.");

            case ShowActionKind.StingerFire:
            {
                var item = FindStinger(a.Target);
                if (item is null) return ActionResult.Refused($"No stinger '{a.Target}'.");
                if (!_s.Stingers.Fire(item)) return ActionResult.Failed(_s.Stingers.Status);
                _s.AirLabel = $"STING: {item.DisplayName}";
                return ActionResult.Requested(_s.Stingers.Status);
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
                var unarmed = _s.Arming.Unarmed;
                _s.Sandbox.SendAll(cut, unarmed);
                var rearmed = _s.Sandbox.Active ? " EDIT SAFE re-armed." : "";
                var scope = unarmed.Count == 0 ? "on every screen" : $"on the armed tiles ({unarmed.Count} kept their picture)";
                return ActionResult.Done((cut
                    ? $"CUT — sandbox is now the program {scope}."
                    : $"TAKE — sandbox faded up {scope}.") + rearmed);
            }

            case ShowActionKind.StopAll:
                _s.Stingers.Stop();
                State.AudioPlayer.Playing = false;
                State.Tone.Enabled = false;
                return ActionResult.Done("Stopped: audio track, stingers, tone. Outputs, blackout and the stream are untouched.");

            default:
                return ActionResult.Refused($"Unknown action '{a.Kind}'.");
        }
    }

    /// <summary>
    /// Fires a look to air. EDIT SAFE protects what you are <em>building</em>, not what you
    /// <em>fire</em>: with the sandbox open the audience gets the look and the preview keeps
    /// showing the operator's in-progress edit. "cut" switches without the crossfade.
    /// </summary>
    private ActionResult ApplyLookToAir(LookConfig look, string value)
    {
        var sandboxed = _s.Sandbox.Active;
        // Value: "cut", a fade in ms (this recall only), or anything else for the show default.
        if (CueActionSpec.TryParseTransition(value, out var cut, out var fadeMs))
        {
            if (cut) _s.Bus.CutOnNextPublish();
            else if (fadeMs >= 0) _s.Bus.FadeOnNextPublish(fadeMs);
        }
        var ok = false;
        _s.EditAir(air => ok = LookService.Apply(look.Json, air, rearmCountdown: true));
        if (!ok) return ActionResult.Failed($"Look '{look.Name}' could not be applied.");
        _s.AirLabel = look.Name;
        // "cue 18:00" from the schedule: the status line says which cue fired, as it used to.
        var prefix = value.StartsWith("cue ", StringComparison.OrdinalIgnoreCase) ? $"Cue {value[4..]}: " : "";
        return ActionResult.Done(prefix + (sandboxed
            ? $"Look '{look.Name}' on air — your preview edit is untouched."
            : $"Look '{look.Name}' applied."));
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
            if (!explicitBlackout) _s.State.Blackout = blackoutBefore;
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
        CueActionKind.StingerFire => new ShowAction(ShowActionKind.StingerFire, a.Target),
        CueActionKind.StingerStop => new ShowAction(ShowActionKind.StingerStop),
        CueActionKind.PlaylistPart => new ShowAction(ShowActionKind.PlaylistPart, a.Target),
        CueActionKind.StreamStart => new ShowAction(ShowActionKind.StreamStart),
        CueActionKind.StreamStop => new ShowAction(ShowActionKind.StreamStop),
        CueActionKind.BlackoutOn => new ShowAction(ShowActionKind.BlackoutOn),
        CueActionKind.BlackoutOff => new ShowAction(ShowActionKind.BlackoutOff),
        CueActionKind.ScreenOn => new ShowAction(ShowActionKind.ScreenOn, a.Target),
        CueActionKind.ScreenOff => new ShowAction(ShowActionKind.ScreenOff, a.Target),
        CueActionKind.CanvasOn => new ShowAction(ShowActionKind.CanvasOn, a.Target),
        CueActionKind.CanvasOff => new ShowAction(ShowActionKind.CanvasOff, a.Target),
        CueActionKind.CountdownStart => new ShowAction(ShowActionKind.CountdownStart, "", a.Value),
        CueActionKind.CountdownStop => new ShowAction(ShowActionKind.CountdownStop),
        CueActionKind.MessageOn => new ShowAction(ShowActionKind.MessageOn, "", a.Value),
        CueActionKind.MessageOff => new ShowAction(ShowActionKind.MessageOff),
        CueActionKind.ClockOn => new ShowAction(ShowActionKind.ClockOn),
        CueActionKind.ClockOff => new ShowAction(ShowActionKind.ClockOff),
        CueActionKind.ListArm => new ShowAction(ShowActionKind.ListArm, a.Target),
        CueActionKind.ListDisarm => new ShowAction(ShowActionKind.ListDisarm, a.Target),
        CueActionKind.ListGo => new ShowAction(ShowActionKind.ListGo, a.Target),
        CueActionKind.ListBack => new ShowAction(ShowActionKind.ListBack, a.Target),
        CueActionKind.ListReset => new ShowAction(ShowActionKind.ListReset, a.Target),
        CueActionKind.Note => new ShowAction(ShowActionKind.Note),
        _ => new ShowAction(ShowActionKind.Unknown),
    };

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

    private StingerItemConfig? FindStinger(string target)
    {
        var items = State.Stingers.Items;
        if (int.TryParse(target, out var n)) return n >= 1 && n <= items.Count ? items[n - 1] : null;
        return items.FirstOrDefault(i => i.Id == target)
               ?? items.FirstOrDefault(i => string.Equals(i.DisplayName, target, StringComparison.OrdinalIgnoreCase));
    }
}
