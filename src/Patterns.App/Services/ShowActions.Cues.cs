using Patterns.Core.LowerThirds;
using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// The cues' verbs: the caller's GO and standby, a cue fired from any list, the lists armed and stepped, the presenter's clicker.
/// </summary>
public sealed partial class ShowActions
{
    private ActionResult? RunCues(ShowAction a, ActionOrigin origin)
    {
        switch (a.Kind)
        {
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
            case ShowActionKind.CueStandby:
            {
                // next / prev, or a cue by its number, its name or its id: the standby moves, nothing fires.
                var stack = _s.CueStack;
                var word = a.Target.Trim();
                var next = word.Equals("next", StringComparison.OrdinalIgnoreCase);
                if (next || word.Equals("prev", StringComparison.OrdinalIgnoreCase))
                {
                    if (!stack.StandbyMove(next ? +1 : -1)) return ActionResult.Refused("no cue that way");
                    var at = stack.StandbyCue;
                    return ActionResult.Done(at is null ? "Standby moved." : $"Standby: {at.Number} {at.Name}");
                }
                var cue = stack.Stack.Cues.FirstOrDefault(c => c.Id == word)
                          ?? (CueNumber.Parse(word) is not null ? stack.Stack.Cues.FirstOrDefault(c => CueNumber.Compare(c.Number, word) == 0) : null)
                          ?? stack.Stack.Cues.FirstOrDefault(c => string.Equals(c.Name, word, StringComparison.OrdinalIgnoreCase));
                if (cue is null) return ActionResult.Refused($"no cue '{word}'");
                stack.Standby(cue.Id);
                return ActionResult.Done($"Standby: {cue.Number} {cue.Name}");
            }
            case ShowActionKind.CueHoldOn:
            case ShowActionKind.CueHoldOff:
            {
                var hold = a.Kind == ShowActionKind.CueHoldOn;
                _s.CueStack.SetHold(hold, origin);
                return ActionResult.Done(hold ? "HOLD — GO is refused until released." : "HOLD released.");
            }
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
                    case ShowActionKind.ListDisarm:
                    {
                        var arm = a.Kind == ShowActionKind.ListArm;
                        // A remote arms only while the Remote page allows it; the desk's own keys and a cue always may.
                        if (IsRemote(origin) && !State.Control.RemotesMayArm) return ActionResult.Refused("remotes may not arm — allow it on the Remote page");
                        // Disarming a list is the caller saying "not from here": what it left
                        // waiting goes with it, or a step would land after they stood down.
                        if (!arm) _s.Tail.DropStack(stack.Id);
                        if (ReferenceEquals(stack, _s.CueStack.Stack))
                        {
                            // The caller's stack: the standby, a pending confirm and a follow go with the arming.
                            _s.CueStack.SetArmed(arm, origin);
                            return ActionResult.Done(arm ? "Cue stack armed." : "Cue stack disarmed.");
                        }
                        rt.Armed = arm;
                        return ActionResult.Done(arm ? $"{stack.Name} armed." : $"{stack.Name} disarmed.");
                    }
                    case ShowActionKind.ListReset:
                        rt.CurrentIndex = -1;
                        // Back to the top means back to the top: nothing the last cue left waiting.
                        _s.Tail.DropStack(stack.Id);
                        return ActionResult.Done($"{stack.Name} reset to the start.");
                    case ShowActionKind.ListGo:
                        return RunList(stack, +1, origin);
                    default:
                        return RunList(stack, -1, origin);
                }
            }
            default:
                return null;
        }
    }

    public bool PresenterAdvance(int delta, ActionOrigin origin)
        => Execute(delta >= 0 ? ShowActionKind.PresenterNext : ShowActionKind.PresenterPrev, origin).Ok;

    /// <summary>Runs a cue now, from any list: the desk's FIRE button, and later the caller's GO.</summary>
    public ActionResult FireCue(RunCueConfig cue, ActionOrigin origin)
        => Execute(new ShowAction(ShowActionKind.CueFire, cue.Id), origin);

    /// <summary>The clicker: Page Down / Up, NEXT / PREV and the Show page drive the clicker list.</summary>
    /// <summary>
    /// The click-through. A deck on air comes first: NEXT and PREV turn its pages; past the last
    /// page the caller's stack resumes — GO on the standby cue when the deck asks for it and the
    /// stack is armed — else the clicker list steps as it always did. With no deck on air, the
    /// clicker list alone.
    /// </summary>
    private ActionResult Presenter(int delta, ActionOrigin origin)
    {
        if (_s.DeckOnAir() is { PageCount: > 0 } deck)
        {
            if (delta > 0 ? !deck.AtEnd : !deck.AtStart)
            {
                deck.GoTo(deck.Page + Math.Sign(delta));
                return ActionResult.Done(DeckWords(deck));
            }
            if (delta < 0) return ActionResult.Done($"Deck: the first page of {deck.PageCount} — nothing before it.");
            var ends = MediaLocator.FindActiveMedia(_s.AirState, MediaSource.Deck)?.DeckEndsWithGo ?? true;
            if (ends && _s.CueStack.Armed && _s.CueStack.StandbyCue is { } standby)
            {
                // The deck has ended: the cue stack resumes through the same gate a GO from the desk goes through.
                var go = Execute(new ShowAction(ShowActionKind.CueGo), origin);
                return go.Ok ? ActionResult.Done($"Deck ended — GO {standby.Number} {standby.Name}.") : go;
            }
            var clicker = CueStacks.Clicker(State);
            if (!ends || clicker.Cues.Count == 0 || !_s.Cues.For(clicker).Armed)
            {
                return ActionResult.Done(ends
                    ? $"Deck: the last page of {deck.PageCount} — nothing follows: arm the cue stack with a cue on standby, GO a cue, or recall a look."
                    : $"Deck: the last page of {deck.PageCount}.");
            }
        }
        return RunList(CueStacks.Clicker(State), delta, origin);
    }

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
        // A cue that says black — or fades to it, or up from it — means it; the restore below is for the rest.
        var explicitBlackout = cue.Actions.Any(x => x.Kind is ShowActionKind.BlackoutOn or ShowActionKind.BlackoutOff or ShowActionKind.FadeToBlack or ShowActionKind.FadeUp);
        var total = cue.Actions.Count;
        var done = 0;
        var requested = false;
        ActionResult? failure = null;
        // The steps that go with the GO, and the ones that wait. A cue with no waits plans to
        // exactly what it always did — one list, one edit, one publish — so the delayed path
        // costs a show that never uses it nothing at all.
        var plan = CueSteps.Plan(cue.Actions);
        var now = ShowClock.Seconds;
        var immediate = plan.Where(p => p.IsImmediate).ToList();
        var waiting = plan.Where(p => !p.IsImmediate).ToList();
        // The next GO on a list takes the list over: whatever the last cue on it left waiting goes
        // before this one starts, so a step from two cues ago can never land on the audience.
        _s.Tail.Schedule(stack.Id, cue, label, waiting, now);
        _s.BulkEdit(() =>
        {
            foreach (var step in immediate)
            {
                var action = step.Action;
                if (action.Kind == ShowActionKind.Note)
                {
                    done++;
                    continue;
                }
                var mapped = action.ToAction();
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
            // A cue that failed on its way in does not go on running behind the operator's back.
            _s.Tail.DropStack(stack.Id);
            rt.LastOutcome = "Failed";
            return ActionResult.Failed($"{label}: failed at action {done + 1} of {total} — {failure.Message}");
        }
        rt.LastOutcome = requested ? "Requested" : "Done";
        var tail = waiting.Count == 0
            ? ""
            : $" — {waiting.Count} step{(waiting.Count == 1 ? "" : "s")} to come over {CueSteps.AtWords(waiting[^1].AtSeconds).TrimStart('+')}";
        return requested
            ? ActionResult.Requested($"{label} — {CueSummary.Describe(State, cue)}{tail} (still settling).")
            : ActionResult.Done($"{label} — {CueSummary.Describe(State, cue)}{tail}");
    }
}
