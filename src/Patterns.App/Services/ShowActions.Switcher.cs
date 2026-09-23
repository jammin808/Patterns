using Patterns.Core.LowerThirds;
using Patterns.Rendering.LowerThirds;
using Patterns.Core.Media;
using Patterns.Rendering.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// The switcher's verbs: TAKE and CUT, the fades to black and up (scoped to a screen, a canvas or the rig), the blackout, the freeze, the review.
/// </summary>
public sealed partial class ShowActions
{
    private ActionResult? RunSwitcher(ShowAction a, ActionOrigin origin)
    {
        switch (a.Kind)
        {
            case ShowActionKind.BlackoutOn:
                State.Blackout = true;
                return ActionResult.Done("Blackout.");
            case ShowActionKind.BlackoutOff:
                State.Blackout = false;
                ReleaseBlackAudio();
                return ActionResult.Done("Blackout lifted.");
            case ShowActionKind.BlackoutToggle:
                State.Blackout = !State.Blackout;
                if (!State.Blackout) ReleaseBlackAudio();
                return ActionResult.Done(State.Blackout ? "Blackout." : "Blackout lifted.");
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
                // Where: the rig — a blackout with a fade of its own — or some of it: the focused
                // tile, the ticked tiles, the ticked groups, SCREEN n, GROUP A, a target id. A
                // target faded on its own goes into the bus's runtime set (never the show file);
                // the engine draws it black and the sink's own crossfade, carrying the seconds
                // asked for, is the fade. How long: the value's seconds ("2", "1.5", "1500ms";
                // empty or 0 = the show's transition time) — the one convention the desk, the
                // wire, OSC and a cue share, so a cue's step is the wire's line is the desk's key.
                var down = a.Kind == ShowActionKind.FadeToBlack;
                if (FadeScope.Parse(a.Target) is not { } scope)
                {
                    return ActionResult.Refused($"'{a.Target}' is not a place to fade — leave it empty for every screen, or SCREEN 2, CANVAS A, GROUP CONFIDENCE, FOCUSED, TICKED, GROUPS, ID <screen id>.");
                }
                if (!ControlProtocol.TryParseSeconds(a.Value, out var asked)) return ActionResult.Refused($"'{a.Value}' is not a number of seconds for the fade.");
                var ms = asked > 0 ? asked : (int)Math.Round(State.Transition.DurationMs);
                var secs = (ms / 1000.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
                if (scope.IsEverything || (scope.Kind == FadeScopeKind.Focused && _s.FocusedTarget?.Invoke() is null))
                {
                    // The rig (the program tile focused is the rig too): the blackout, faded. A fade
                    // up of the rig lifts the blackout and brings back every screen that was black
                    // on its own — with or without a blackout over them.
                    if (down)
                    {
                        if (State.Blackout) return ActionResult.Refused("Already black.");
                        _s.Bus.FadeOnNextPublish(ms);
                        State.Blackout = true;
                    }
                    else
                    {
                        var own = _s.Bus.BlackTargets.Count > 0;
                        if (!State.Blackout && !own) return ActionResult.Refused("Not black.");
                        _s.Bus.FadeOnNextPublish(ms);
                        if (own) _s.Bus.BlackTargets = Array.Empty<string>();
                        if (State.Blackout) State.Blackout = false;
                        else _s.PublishRuntime();
                    }
                    LinkBlackAudio(ms);
                    return ActionResult.Done(down ? $"Fading to black over {secs} s." : $"Fading up over {secs} s.");
                }
                var (targets, problem) = FadeTargets(scope);
                if (problem is not null) return ActionResult.Refused(problem);
                var set = new HashSet<string>(_s.Bus.BlackTargets, StringComparer.Ordinal);
                var changed = 0;
                foreach (var t in targets)
                {
                    if (down ? set.Add(t) : set.Remove(t)) changed++;
                }
                var words = FadeWords(targets);
                if (changed == 0) return ActionResult.Refused(down ? $"{words}: already black." : $"{words}: not black.");
                _s.Bus.BlackTargets = set.ToArray();
                _s.Bus.FadeOnNextPublish(ms);
                _s.PublishRuntime();
                LinkBlackAudio(ms);
                return ActionResult.Done(down ? $"Fading {words} to black over {secs} s." : $"Fading {words} up over {secs} s.");
            }

            case ShowActionKind.RunMonitor:
            case ShowActionKind.RunMonitorOff:
            {
                // The RUN surface's monitor (round 62): the desk's own eye, kept with the show's desk
                // layout — never the air, never the frozen program. The view follows the setting.
                string word;
                if (a.Kind == ShowActionKind.RunMonitorOff) word = "OFF";
                else if (ContentTargets.IsMonitorMain(a.Target)) word = "";
                else if (ContentTargets.IsProgramTarget(a.Target)) word = "PGM";
                else
                {
                    var target = ResolveScreenTarget(a.Target);
                    if (target is null) return ActionResult.Refused($"No screen '{a.Target}'.");
                    word = target;
                }
                var words = word switch
                {
                    "OFF" => "RUN monitor hidden — the history has the room.",
                    "" => "RUN monitor: the main screen.",
                    "PGM" => "RUN monitor: the programme.",
                    _ => $"RUN monitor: {Rig.Geometry(State, _s.Screens.All).LabelFor(State, word)}.",
                };
                if (string.Equals(State.Desk.RunMonitor, word, StringComparison.Ordinal)) return ActionResult.Done(words);
                State.Desk.RunMonitor = word;
                return ActionResult.Done(words);
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

            case ShowActionKind.NextTransition:
            {
                // Round 67.6: the next TAKE alone arrives this way; the show's transition never moves.
                if (!NextTransition.TryParse(a.Value, name => StingerLibrary.Find(State, name) is { Kind: StingerKind.Sting, Source: StingerSource.File } item ? (item.Id, item.DisplayName) : null, out var next, out var problem))
                {
                    return ActionResult.Refused(problem ?? $"'{a.Value}' is not a transition.");
                }
                _s.NextTake.Set(next);
                return ActionResult.Done(next is null
                    ? "Next take: the show's own transition."
                    : $"Next take: {next.Words} — one shot; the show's transition is unchanged.");
            }
            case ShowActionKind.Take:
            case ShowActionKind.Cut:
            {
                if (!_s.Sandbox.Active)
                {
                    return ActionResult.Refused("Open EDIT SAFE (the sandbox) first — build the look, then CUT or TAKE it to air.");
                }
                var cut = a.Kind == ShowActionKind.Cut;
                // Round 72: a sting's landing runs the ticket the press froze, never a fresh plan.
                if (origin == ActionOrigin.Stinger && _landing is { Tile: false } landing) return LandWall(landing);
                // Where: every armed screen — the wall's ARM and LOCK decide — or a part of the rig
                // (the focused tile, the ticked tiles, the ticked groups, SCREEN n, GROUP A) with
                // everything outside it keeping its picture exactly as an un-armed tile does: pinned
                // as its own, lifted by the next full send. The same words a fade takes.
                if (FadeScope.Parse(a.Target) is not { } scope)
                {
                    return ActionResult.Refused($"'{a.Target}' is not a place to take to — leave it empty for every armed screen, or SCREEN 2, CANVAS A, GROUP CONFIDENCE, FOCUSED, TICKED, GROUPS.");
                }
                // One rule (round 67): TakePlan says what this scope changes and what it holds — LOCKED never
                // taken, ARM counting inside every scope, a repeater never, everything outside the scope kept —
                // and a take that would move nothing is refused with the reason, never reported as done.
                var plan = PlanTake(scope);
                if (plan.IsRefused) return ActionResult.Refused(plan.Refusal!);
                // Round 81: every scope but the rig lands on its targets alone, as their own pictures — the tile's own
                // key, once per target — and the programme, its preview and every other screen are untouched.
                if (plan.IsScoped) return TakeScoped(origin, cut, scope, plan);
                // Round 79: attempts are not facts on the wall either. The plan says what the scope would move; whether that
                // would change anything the audience sees is another question (SandboxService.WouldChange: each taken
                // screen's landing picture against the air's, and the layers a take carries). A hand that presses TAKE over
                // an air that already is the preview is told so, spends no one-shot and publishes nothing. The show's own
                // automation asserts an end state and is not refused when it holds — its row says Done with Effect Nothing.
                if (!origin.IsAutomation && !_s.Sandbox.WouldChange(plan.Taken, plan.Kept))
                {
                    return ActionResult.Refused($"Nothing to take {plan.Where} — the air is already the preview. Change the preview, or SEND a picture to a tile first.");
                }
                // The one-shot (round 67.6): a CUT is a cut and leaves it for the TAKE it was given to; a video
                // sting covers the screens first and the take lands when the clip ends — requested now, done then.
                // The take a sting lands at its end is the press's own landing: it never spends a one-shot set meanwhile.
                // Round 72: the one-shot is spent by a take that happens, never by a press that fails — a sting that
                // cannot fire leaves it for the next press and says so; a sting gone from the library is cleared, and says so.
                var next = cut || origin == ActionOrigin.Stinger ? null : _s.NextTake.Pending;
                if (next is { IsSting: true })
                {
                    if (StingerLibrary.Find(State, next.StingId) is not { } sting)
                    {
                        _s.NextTake.Consume();
                        return ActionResult.Refused($"The sting '{next.StingName}' is not in the library any more — the one-shot is cleared; the next TAKE is the show's own.");
                    }
                    var ticket = TakeTicket.From(plan, StingTakeScope(scope, plan), sting.DisplayName, ShowClock.UtcNow);
                    if (!_s.Stingers.Fire(sting, afterOverride: StingerAfter.Take, ticket: ticket)) return ActionResult.Failed($"{_s.Stingers.Status} The one-shot stays for the next TAKE.");
                    _s.NextTake.Consume();
                    return ActionResult.Requested($"TAKE under the sting '{sting.DisplayName}' — the preview lands {plan.Where} when the clip ends; the show's transition is unchanged." + UnseenNote());
                }
                if (next is not null) _s.NextTake.Consume();
                ArmNextTransition(next);
                var already = _s.Sandbox.AlreadyOnAir(plan.Taken);                              // round 78: read before the send settles it
                _s.Sandbox.SendAll(cut, plan.Kept);
                var rearmed = _s.Sandbox.Active ? " EDIT SAFE re-armed." : "";
                var kept = plan.Kept.Count == 0 ? "" : $" ({plan.Kept.Count} kept their picture)";
                var arrived = next is null ? "" : $" Arrived by {next.Words} (one shot).";
                return ActionResult.Done((cut
                    ? $"CUT — sandbox is now the program {plan.Where}{kept}{AlreadyWords(already, plan.Taken.Count)}."
                    : $"TAKE — sandbox faded up {plan.Where}{kept}{AlreadyWords(already, plan.Taken.Count)}.") + arrived + rearmed + UnseenNote());
            }
            case ShowActionKind.ScreenTake:
            case ShowActionKind.ScreenCut:
            {
                // The tile's own CUT / TAKE (round 63): the preview to this one screen, as its own
                // picture — OWN lights up on it, the programme and every other screen stay exactly as
                // they were, and the preview keeps the picture for the next one. A take, so the
                // transition runs on that sink alone; a cut switches it. The look tally reads the
                // result by itself: the screen has gone its own way inside the look on air.
                if (!_s.Sandbox.Active)
                {
                    return ActionResult.Refused("Open EDIT SAFE (the sandbox) first — build the picture, then CUT or TAKE it to this screen.");
                }
                var target = ResolveScreenTarget(a.Target);
                if (target is null)
                {
                    // Round 76: a promised target gone from the rig during the clip — the ticket's words say where it went.
                    if (origin == ActionOrigin.Stinger && _landing is { Tile: true } goneTicket)
                    {
                        var goneGeometry = Rig.Geometry(State, _s.Screens.All);
                        var gone = goneTicket.Land(RigTargets(goneGeometry), id => WhereNow(goneGeometry, id));
                        if (gone.IsRefused) return ActionResult.Failed(gone.Refusal!);
                    }
                    return ActionResult.Refused($"No screen '{a.Target}'.");
                }
                if (!ContentTargets.IsCanvasKey(target) && State.Output.Placements.FirstOrDefault(p => p.ScreenId == target) is { MirrorOf.Length: > 0 } mirror
                    && ContentTargets.IsInRig(State, mirror.MirrorOf))
                {
                    return ActionResult.Refused("A repeater draws its source's picture and has none of its own — take to its source instead.");
                }
                var tileGeometry = Rig.Geometry(State, _s.Screens.All);
                var where = tileGeometry.LabelFor(State, target);
                // Round 76: a tile's landing runs the ticket the press froze against the rig as it is now — the one
                // validator the wall's landing runs — so a canvas that grew or a screen made a repeater during the
                // clip holds the landing with the reason, and the tile path only applies a landing already approved.
                if (origin == ActionOrigin.Stinger && _landing is { Tile: true } tileTicket)
                {
                    var landingNow = tileTicket.Land(RigTargets(tileGeometry), id => WhereNow(tileGeometry, id));
                    if (landingNow.IsRefused) return ActionResult.Failed(landingNow.Refusal!);
                }
                // LOCKED means locked (round 67): the tile's own key is no way round it either — nor is a sting's
                // landing (round 72): a lock that came after the press holds the screen, and the landing says so.
                if (ScreenRoles.IsLocked(State, target))
                {
                    return ActionResult.Refused(origin == ActionOrigin.Stinger && _landing is not null
                        ? $"{where} was locked after the press — it keeps its picture; the take did not land."
                        : $"{where} is locked — it keeps its picture. Unlock it (LOCK on its tile, or LOCK n OFF) to take to it.");
                }
                var cutOne = a.Kind == ShowActionKind.ScreenCut;
                // Round 78: attempts are not facts. The tile's PVW holds one picture (SandboxService.PvwPicture — round 81:
                // its own while it is OWN, else the programme's preview) and the take lands exactly that — so a press that
                // would put up what the screen already shows is refused with the reason and the way out, and spends no
                // one-shot. A landing under a sting was validated at the press.
                var effect = _s.Sandbox.EffectOf(target);
                if (!origin.IsAutomation && effect == SandboxService.TakeEffect.Nothing)
                {
                    return ActionResult.Refused(_s.EditingTargetId == target
                        ? $"{where} already shows its own picture — nothing to take. Edit it here, or SEND the programme's preview to its tile."
                        : $"{where} already shows its own picture — nothing to take. SEND the programme's preview to its tile, or select the tile and edit it; PROGRAM puts it back on the programme.");
                }
                var pendingOne = _s.Sandbox.IsStaged(target);                                     // read before the send settles it
                var nextOne = cutOne || origin == ActionOrigin.Stinger ? null : _s.NextTake.Pending;
                if (nextOne is { IsSting: true })
                {
                    if (StingerLibrary.Find(State, nextOne.StingId) is not { } sting)
                    {
                        _s.NextTake.Consume();
                        return ActionResult.Refused($"The sting '{nextOne.StingName}' is not in the library any more — the one-shot is cleared; the next TAKE is the show's own.");
                    }
                    // Round 76: the tile's ticket carries its target's shape and key like the wall's, so the landing can tell a target that is not what the press saw.
                    var pressed = RigTargets(tileGeometry).FirstOrDefault(t => t.Id == target);
                    var ticket = new TakeTicket
                    {
                        Scope = "TILE " + target, Taken = new[] { target }, Labels = new[] { where }, Where = $"on {where} alone", Tile = true, Cover = sting.DisplayName, PressedUtc = ShowClock.UtcNow,
                        Shapes = new[] { pressed?.Shape ?? "" }, Keys = new[] { pressed?.ShapeKey ?? "" },
                    };
                    if (!_s.Stingers.Fire(sting, afterOverride: StingerAfter.Take, ticket: ticket)) return ActionResult.Failed($"{_s.Stingers.Status} The one-shot stays for the next TAKE.");
                    _s.NextTake.Consume();
                    return ActionResult.Requested($"TAKE under the sting '{sting.DisplayName}' — the preview lands on {where} alone when the clip ends." + UnseenNote());
                }
                if (nextOne is not null) _s.NextTake.Consume();
                ArmNextTransition(nextOne);
                // What this tile's PVW shows (round 67, the rule in SandboxService.PvwPicture since round 78).
                _s.Sandbox.SendToTargets(new[] { target }, toAir: true, cut: cutOne, ownPicture: true);
                var arrivedOne = nextOne is null ? "" : $" Arrived by {nextOne.Words} (one shot).";
                var adjustedOne = AdjustmentNote(_s.AirState.Independent.FirstOrDefault(x => x.ScreenId == target)?.Pattern);   // round 77
                var verb = cutOne ? "CUT" : "TAKE";
                var whatOne = pendingOne ? $"the picture on {where}'s PVW" : "the preview";
                var landedOne = effect switch
                {
                    SandboxService.TakeEffect.OwnOnly => $"{verb} — {where} already showed this picture; it is now its own (OWN), so it keeps it when the programme changes.",
                    SandboxService.TakeEffect.Nothing => $"{verb} — {where} already shows this picture as its own; nothing changed.",     // round 79: automation's no-op, said as one
                    _ => cutOne
                        ? $"CUT — {whatOne} is on {where} alone, as its own picture; every other screen stays."
                        : $"TAKE — {whatOne} fades up on {where} alone, as its own picture; every other screen stays.",
                };
                return ActionResult.Done(landedOne + arrivedOne + UnseenNote() + adjustedOne);   // the picture's own note last, as round 77 pinned it
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// Round 81: a scoped CUT / TAKE — FOCUSED on a tile, TICKED, GROUPS, a group by kind, SCREEN n, CANVAS A,
    /// CANVASES, an id. The maintainer's rule from the rig: FOCUSED sends to the selected screen alone. Round 15's
    /// model moved the programme to the preview under everything and pinned every other screen to a copy of its
    /// old picture as OWN — the room saw one screen change and the wall lit OWN everywhere. Now each taken target
    /// gets what its PVW shows as its own picture, exactly as its own tile's key lands it (SandboxService.PvwPicture,
    /// SendToTargets), the programme's air and preview stay, no pin is set and EDIT SAFE stays open with the preview
    /// kept for the next one. LOCK means lock: the plan's Taken never holds a locked target, a repeater or an
    /// un-armed tile — they are Held, with the reason. A take whose every target already shows its picture as its
    /// own is refused with the way out; one that only lights OWN says so.
    /// </summary>
    private ActionResult TakeScoped(ActionOrigin origin, bool cut, FadeScope scope, TakePlan plan)
    {
        var effects = plan.Taken.ToDictionary(t => t, t => _s.Sandbox.EffectOf(t), StringComparer.Ordinal);
        var moving = effects.Values.Count(e => e != SandboxService.TakeEffect.Nothing);
        if (!origin.IsAutomation && moving == 0)
        {
            return ActionResult.Refused(plan.Taken.Count == 1
                ? $"Nothing to take {plan.Where} — it already shows its own picture. SEND the programme's preview to its tile, edit it there, or PROGRAM puts it back on the programme."
                : $"Nothing to take {plan.Where} — they already show their own pictures. SEND the programme's preview to their tiles, edit them there, or PROGRAM puts them back on the programme.");
        }
        var next = cut || origin == ActionOrigin.Stinger ? null : _s.NextTake.Pending;
        if (next is { IsSting: true })
        {
            if (StingerLibrary.Find(State, next.StingId) is not { } sting)
            {
                _s.NextTake.Consume();
                return ActionResult.Refused($"The sting '{next.StingName}' is not in the library any more — the one-shot is cleared; the next TAKE is the show's own.");
            }
            var ticket = TakeTicket.From(plan, StingTakeScope(scope, plan), sting.DisplayName, ShowClock.UtcNow);
            if (!_s.Stingers.Fire(sting, afterOverride: StingerAfter.Take, ticket: ticket)) return ActionResult.Failed($"{_s.Stingers.Status} The one-shot stays for the next TAKE.");
            _s.NextTake.Consume();
            return ActionResult.Requested($"TAKE under the sting '{sting.DisplayName}' — the preview lands {plan.Where}, as {(plan.Taken.Count == 1 ? "its own picture" : "their own pictures")}, when the clip ends; the programme is unchanged." + UnseenNote());
        }
        if (next is not null) _s.NextTake.Consume();
        ArmNextTransition(next);
        var pending = plan.Taken.Count(t => _s.Sandbox.IsStaged(t));                        // read before the send settles it
        _s.Sandbox.SendToTargets(plan.Taken, toAir: true, cut: cut, ownPicture: true);
        var arrived = next is null ? "" : $" Arrived by {next.Words} (one shot).";
        return ActionResult.Done(ScopedWords(cut, plan, effects, pending) + arrived + UnseenNote());
    }

    /// <summary>
    /// The words of a scoped take that landed: the tiles it landed on (the taken ones, never the held), as whose
    /// picture, the held ones with their reason, what only lit OWN, what was already there, and that the rest is untouched.
    /// </summary>
    private static string ScopedWords(bool cut, TakePlan plan, IReadOnlyDictionary<string, SandboxService.TakeEffect> effects, int pending)
    {
        var verb = cut ? "CUT" : "TAKE";
        var one = plan.Taken.Count == 1;
        var what = pending == 0 ? "the preview" : pending == plan.Taken.Count ? (one ? "the picture on its PVW" : "the pictures on their PVWs") : "the previews";
        var on = $"on {string.Join(", ", plan.TakenLabels)}{(one ? " alone" : "")}";
        var landed = cut
            ? $"{verb} — {what} is {on}, as {(one ? "its own picture" : "their own pictures")}"
            : $"{verb} — {what} fades up {on}, as {(one ? "its own picture" : "their own pictures")}";
        var held = plan.Held.Count == 0 ? "" : $" · held: {string.Join(", ", plan.Held.Select(h => $"{h.Label} ({h.Reason})"))}";
        var ownOnly = new List<string>();
        var already = new List<string>();
        for (var i = 0; i < plan.Taken.Count; i++)
        {
            var effect = effects[plan.Taken[i]];
            if (effect == SandboxService.TakeEffect.OwnOnly) ownOnly.Add(plan.TakenLabels[i]);
            else if (effect == SandboxService.TakeEffect.Nothing) already.Add(plan.TakenLabels[i]);
        }
        var notes = "";
        if (ownOnly.Count > 0) notes += $" {string.Join(", ", ownOnly)} already showed it and {(ownOnly.Count == 1 ? "is" : "are")} now {(ownOnly.Count == 1 ? "its" : "their")} own (OWN).";
        if (already.Count > 0) notes += $" {string.Join(", ", already)} already {(already.Count == 1 ? "shows" : "show")} it as {(already.Count == 1 ? "its" : "their")} own; nothing changed there.";
        return $"{landed}; the programme and every other screen stay{held}.{notes}";
    }

    /// <summary>
    /// The content targets a scope names on this rig, or why it names none: the focused tile, the
    /// ticked tiles, every target in the ticked tiles' groups (round 81: a group is what a screen is
    /// for — main, confidence, info), a group by kind, a screen by wall number (the canvas it renders
    /// through when it joined one), a joined canvas by wall letter, the ticked canvases, a target id in
    /// the rig. A fade and a CUT / TAKE read the same words through here.
    /// </summary>
    private (IReadOnlyList<string> Targets, string? Problem) FadeTargets(FadeScope scope)
    {
        var geometry = Rig.Geometry(State, _s.Screens.All);
        switch (scope.Kind)
        {
            case FadeScopeKind.Focused:
            {
                var focused = _s.FocusedTarget?.Invoke();
                return focused is null ? (Array.Empty<string>(), "No wall tile is focused.") : (new[] { focused }, null);
            }
            case FadeScopeKind.Ticked:
            {
                var ticked = _s.TickedTargets?.Invoke() ?? Array.Empty<string>();
                return ticked.Count == 0 ? (Array.Empty<string>(), "Tick the wall tiles first.") : (ticked, null);
            }
            case FadeScopeKind.Groups:
            {
                var kinds = (_s.TickedTargets?.Invoke() ?? Array.Empty<string>()).Select(t => ScreenRoles.KindOf(State, t)).Where(ScreenRoles.IsTakeKind).Distinct(StringComparer.Ordinal).ToList();
                if (kinds.Count == 0) return (Array.Empty<string>(), "Tick a tile in a group first — GROUPS fades every screen of the ticked tiles' groups (Main, Confidence, Info).");
                return (geometry.Targets.Where(t => kinds.Contains(ScreenRoles.KindOf(State, t))).ToList(), null);
            }
            case FadeScopeKind.Group:
            {
                var ofKind = geometry.Targets.Where(t => ScreenRoles.KindOf(State, t) == scope.Arg).ToList();
                return ofKind.Count == 0 ? (Array.Empty<string>(), $"No {scope.Arg} screen on the wall.") : (ofKind, null);
            }
            case FadeScopeKind.Canvases:
            {
                var canvases = (_s.TickedTargets?.Invoke() ?? Array.Empty<string>()).Where(ContentTargets.IsCanvasKey).ToList();
                return canvases.Count == 0 ? (Array.Empty<string>(), "Tick a joined canvas on the wall first.") : (canvases, null);
            }
            case FadeScopeKind.Screen:
            {
                var n = int.Parse(scope.Arg, System.Globalization.CultureInfo.InvariantCulture);
                var ordered = Rig.OrderedLivePlacements(State, _s.Screens.All);
                if (n < 1 || n > ordered.Count) return (Array.Empty<string>(), $"No screen {n} — the rig has {ordered.Count}.");
                return (new[] { geometry.TargetOf(ordered[n - 1].Placement.ScreenId) }, null);
            }
            case FadeScopeKind.Canvas:
            {
                foreach (var key in geometry.Targets)
                {
                    if (ContentTargets.IsCanvasKey(key) && geometry.LetterOf(key) == scope.Arg) return (new[] { key }, null);
                }
                return (Array.Empty<string>(), $"No canvas {scope.Arg} on the wall.");
            }
            default:
            {
                if (!ContentTargets.IsInRig(State, scope.Arg)) return (Array.Empty<string>(), $"'{scope.Arg}' is not a screen in the rig.");
                return (new[] { ContentTargets.IsCanvasKey(scope.Arg) ? scope.Arg : geometry.TargetOf(scope.Arg) }, null);
            }
        }
    }

    /// <summary>
    /// What the next CUT / TAKE with this scope changes and holds (round 67): the one rule,
    /// <see cref="TakePlan"/>, over this rig — its geometry, its focus, its ticks, its arming and its
    /// locks. The wall's keys, their words, the PGM tile's menu, the multiview's NEXT TAKE line and
    /// STATE all read it here, so none of them can disagree about what a press will do.
    /// </summary>
    /// <summary>
    /// The scope words a sting-driven take runs with when the clip ends (round 67.6). FOCUSED is pinned to
    /// the tile focused at the press — a click on another tile during the clip must not move where the
    /// preview lands, and the words the press answered stay facts; the PGM tile focused means every armed
    /// screen. Every other scope keeps its words: arming and ticks are visible state, read as the take lands.
    /// </summary>
    private string StingTakeScope(FadeScope scope, TakePlan plan)
    {
        if (scope.Kind != FadeScopeKind.Focused) return scope.Words;
        var focused = _s.FocusedTarget?.Invoke();
        return focused is { Length: > 0 } && plan.Taken.Count == 1 && plan.Taken[0] == focused ? new FadeScope(FadeScopeKind.Target, focused).Words : "";
    }

    private TakeTicket? _landing;   // round 72: the ticket a sting's landing runs, while it runs

    /// <summary>
    /// Round 72: a sting's landing. The take runs through the same verb the key runs — the journal, the desk's
    /// hooks and the ticks read it as a TAKE — on the ticket the press froze rather than a fresh plan: what the
    /// press promised lands, less a lock since or a screen gone from the rig, never more.
    /// </summary>
    public ActionResult Land(TakeTicket ticket)
    {
        _landing = ticket;
        try
        {
            return ticket.Tile
                ? Execute(ShowActionKind.ScreenTake, ActionOrigin.Stinger, ticket.Taken.Count == 1 ? ticket.Taken[0] : "")
                : Execute(ShowActionKind.Take, ActionOrigin.Stinger, ticket.Scope);
        }
        finally
        {
            _landing = null;
        }
    }

    /// <summary>The wall's landing: the ticket against the rig as it is now — every target's shape included (round 75) — then the same send a press makes.</summary>
    private ActionResult LandWall(TakeTicket ticket)
    {
        var geometry = Rig.Geometry(State, _s.Screens.All);
        var landing = ticket.Land(RigTargets(geometry), id => WhereNow(geometry, id));
        if (landing.IsRefused) return ActionResult.Failed(landing.Refusal!);
        if (ticket.Scope.Length > 0)
        {
            // Round 81: a scoped press lands per target, as the press would have — the programme untouched.
            var pending = landing.Landed.Count(t => _s.Sandbox.IsStaged(t));
            _s.Sandbox.SendToTargets(landing.Landed, toAir: true, cut: false, ownPicture: true);
            var oneLanded = landing.Landed.Count == 1;
            return ActionResult.Done($"TAKE — {(pending == 0 ? "the preview" : oneLanded ? "the picture on its PVW" : "the pictures on their PVWs")} fades up {ticket.Where}, as {(oneLanded ? "its own picture" : "their own pictures")}, as pressed under the sting '{ticket.Cover}'{landing.HeldWords}; the programme and every other screen stay.{UnseenNote()}");
        }
        var already = _s.Sandbox.AlreadyOnAir(landing.Landed);                                        // round 78
        _s.Sandbox.SendAll(cut: false, landing.Kept);
        var rearmed = _s.Sandbox.Active ? " EDIT SAFE re-armed." : "";
        var kept = landing.Kept.Count == 0 ? "" : $" ({landing.Kept.Count} kept their picture)";
        return ActionResult.Done($"TAKE — sandbox faded up {ticket.Where}{kept}{AlreadyWords(already, landing.Landed.Count)}, as pressed under the sting '{ticket.Cover}'{landing.HeldWords}.{rearmed}{UnseenNote()}");
    }

    /// <summary>
    /// Round 78: attempts are not facts — the words a take carries when the room cannot see it land: the
    /// outputs closed (the field's eleven takes with the outputs off, each reported as a fade-up), or the blackout up.
    /// </summary>
    private string UnseenNote()
    {
        if (!_s.Outputs.IsLive) return " The outputs are off — nothing is on the screens until OUTPUTS ON.";
        if (_s.AirState.Blackout) return " Blackout is on — the screens stay black until it lifts.";
        return "";
    }

    /// <summary>Round 78: of the screens a wall take changed, those the audience already saw with that picture — none, some, or every one.</summary>
    private static string AlreadyWords(int already, int taken)
    {
        if (already <= 0 || taken <= 0) return "";
        return already >= taken ? " — every screen taken already showed its picture" : $" — {already} of {taken} already showed it";
    }

    public TakePlan PlanTake(FadeScope scope)
    {
        var geometry = Rig.Geometry(State, _s.Screens.All);
        var rig = RigTargets(geometry);
        IReadOnlyList<string>? named = null;
        if (scope.Kind is FadeScopeKind.Screen or FadeScopeKind.Canvas or FadeScopeKind.Target)
        {
            var (targets, problem) = FadeTargets(scope);
            if (problem is not null) return new TakePlan { Scope = scope, Refusal = problem };
            named = targets;
        }
        return TakePlan.Resolve(rig, scope, _s.FocusedTarget?.Invoke(), named);
    }

    /// <summary>
    /// The rig's targets as a take sees them now — locked, armed, ticked, a canvas, a repeater — each with its
    /// shape in the wall's words (round 75), so the plan a press makes and the landing a ticket makes read the
    /// same list and a landing can tell a target that is not what the press promised.
    /// </summary>
    private List<TakeTarget> RigTargets(RigGeometry geometry)
    {
        var ticked = new HashSet<string>(_s.TickedTargets?.Invoke() ?? Array.Empty<string>(), StringComparer.Ordinal);
        var byId = State.Output.Placements.ToDictionary(p => p.ScreenId, StringComparer.Ordinal);
        var program = _s.Sandbox.ProgramState ?? State;                 // round 80: the air, for what a take leaves as it is
        var rig = new List<TakeTarget>();
        foreach (var target in geometry.Targets)
        {
            var canvas = ContentTargets.IsCanvasKey(target);
            var mirror = !canvas && byId.TryGetValue(target, out var p) && p.MirrorOf.Length > 0 && ContentTargets.IsInRig(State, p.MirrorOf);
            var shape = canvas
                ? CanvasShape(geometry, target)
                : mirror ? $"a repeater of {geometry.LabelFor(State, byId[target].MirrorOf)}"
                : "a screen of its own";
            // Round 76: the key beside the words — ids alone, so a rename during the clip is no change and a member moved is.
            var key = canvas ? TakeShapes.Canvas(ContentTargets.Members(target)) : mirror ? TakeShapes.Mirror(byId[target].MirrorOf) : TakeShapes.Own;
            // Round 80: an own picture on the air and unchanged in the preview — a wall take carries it and leaves it as it is
            // (round 30's rule); the plan says so before the press instead of listing the target as if its picture would move.
            var keepsOwn = !mirror && ContentTargets.UsesOwnPattern(program, target) && !(_s.Sandbox.Active && _s.Sandbox.IsStaged(target));
            rig.Add(new TakeTarget(target, geometry.LabelFor(State, target), canvas, mirror,
                ScreenRoles.IsLocked(State, target), _s.Arming.IsArmed(target), ticked.Contains(target), shape, key, keepsOwn,
                ScreenRoles.KindOf(State, target)));                                                     // round 81: the group it is in, by kind
        }
        return rig;
    }

    /// <summary>"A · Canvas A of 1 · Left, 2 · Right" — a canvas's shape in the wall's words.</summary>
    private string CanvasShape(RigGeometry geometry, string canvasKey)
        => $"{geometry.LabelFor(State, canvasKey)} of {string.Join(", ", ContentTargets.Members(canvasKey).Select(m => geometry.LabelFor(State, m)))}";

    /// <summary>
    /// Round 75: where a promised target went when it is no longer a target of its own — "in Canvas A" for a
    /// screen that joined a canvas; round 76: for a promised canvas, what its members are now ("A · Canvas A of
    /// 1 · Left, 2 · Right, 3 · Lobby" when the canvas grew or shrank around them, "screens of their own" when it
    /// broke up) — for a landing's words; "" when it is simply gone from the rig.
    /// </summary>
    private string WhereNow(RigGeometry geometry, string id)
    {
        if (ContentTargets.IsCanvasKey(id))
        {
            var members = ContentTargets.Members(id);
            foreach (var target in geometry.Targets)
            {
                if (ContentTargets.IsCanvasKey(target) && ContentTargets.Members(target).Any(m => Array.IndexOf(members, m) >= 0)) return CanvasShape(geometry, target);
            }
            return members.Any(m => geometry.Targets.Contains(m)) ? "screens of their own" : "";
        }
        foreach (var target in geometry.Targets)
        {
            if (ContentTargets.IsCanvasKey(target) && Array.IndexOf(ContentTargets.Members(target), id) >= 0) return $"in {geometry.LabelFor(State, target)}";
        }
        return "";
    }

    /// <summary>The one-shot onto the publish about to happen: a cut, a rate, a kind with its scene and direction — the bus's own overrides, as a look's recall uses them.</summary>
    private void ArmNextTransition(NextTransition? next)
    {
        if (next is null || next.IsSting) return;
        if (next.Cut)
        {
            _s.Bus.CutOnNextPublish();
            return;
        }
        if (next.FadeMs >= 0) _s.Bus.FadeOnNextPublish(next.FadeMs);
        if (next.Kind is not null || next.Scene is not null || next.Direction is not null) _s.Bus.TransitionOnNextPublish(next.Kind, next.Scene, next.Direction);
    }

    /// <summary>STATE's take row (round 67): the wall's scope, the plan it makes, the next take's one-shot; round 81: the tile FOCUSED means and the ticked tiles' groups.</summary>
    public object TakeRow()
    {
        var words = _s.TakeScopeWords?.Invoke() ?? "";
        var plan = PlanTake(FadeScope.Parse(words) ?? FadeScope.Everything);
        var focused = _s.FocusedTarget?.Invoke() ?? "";
        var ticked = _s.TickedTargets?.Invoke() ?? Array.Empty<string>();
        var groups = ticked.Select(t => ScreenRoles.KindOf(State, t)).Where(ScreenRoles.IsTakeKind).Distinct(StringComparer.Ordinal).ToArray();
        return new
        {
            scope = words,
            scopeLabel = plan.Scope.Label,
            words = plan.Words,
            where = plan.Where,
            taken = plan.Taken,
            held = plan.Held.Select(h => new { id = h.Id, label = h.Label, reason = h.Reason }).ToArray(),
            outside = plan.Outside.Count,
            refusal = plan.Refusal ?? "",
            outputsLive = _s.Outputs.IsLive,                                                                  // round 78: a take with the outputs off lands on no screen
            pending = PendingTargets(),                                                                       // round 78: the tiles whose PVW holds a picture the audience has not seen
            next = _s.NextTake.Row(),
            landing = _s.Stingers.SessionTicket is { } ticket                                          // round 72: the take waiting under a sting — the press's promise
                ? new { sting = ticket.Cover, scope = ticket.Scope, targets = ticket.Taken, where = ticket.Where, words = ticket.Words, pressedUtc = ticket.PressedUtc }
                : null,
            last = LastTake is { } lt                                                                  // round 79: the last take as a fact row — what it did, and whether anybody saw it
                ? new { kind = lt.Kind.ToString(), outcome = lt.Result.Status.ToString(), effect = lt.Result.Effect.ToString(), visibility = lt.Result.Visibility.ToString(), atUtc = lt.AtUtc, words = lt.Result.Message }
                : null,
            focused,                                                                                     // round 81: the tile FOCUSED means — the one highlighted on the wall, under the editors; "" is the PGM tile, every armed screen
            focusedLabel = focused.Length == 0 ? "" : Rig.Geometry(State, _s.Screens.All).LabelFor(State, focused),
            groups,                                                                                      // round 81: the ticked tiles' groups by kind — what TICKED GROUPS takes to
        };
    }

    /// <summary>Round 78: the targets whose PVW holds a picture the audience has not seen — staged by SEND or edited in the preview — in wall order.</summary>
    private string[] PendingTargets()
    {
        if (!_s.Sandbox.Active) return Array.Empty<string>();
        var pending = new List<string>();
        foreach (var target in Rig.Geometry(State, _s.Screens.All).Targets)
        {
            if (_s.Sandbox.IsStaged(target)) pending.Add(target);
        }
        return pending.ToArray();
    }

    /// <summary>"Screen 2 · Group A" — the targets as the wall names them.</summary>
    private string FadeWords(IReadOnlyList<string> targets)
    {
        var geometry = Rig.Geometry(State, _s.Screens.All);
        return string.Join(" · ", targets.Select(t => geometry.LabelFor(State, t)));
    }

    /// <summary>The whole rig is dark: the blackout, or every content target black on its own.</summary>
    private bool RigDark()
    {
        if (State.Blackout) return true;
        var targets = Rig.Geometry(State, _s.Screens.All).Targets;
        if (targets.Count == 0) return false;
        var black = _s.Bus.BlackTargets;
        foreach (var t in targets)
        {
            if (!black.Contains(t)) return false;
        }
        return true;
    }

    /// <summary>
    /// The sound follows the picture: a fade that leaves the whole rig dark takes the programme's
    /// sound (the music, a clip's soundtrack) down over the same seconds when the show says so; a
    /// fade that lights any of it brings the sound back the same way. A plain BLACKOUT never
    /// touches the sound on its own.
    /// </summary>
    private void LinkBlackAudio(int ms)
    {
        var dark = RigDark();
        if (dark && !State.Switcher.FadeAudioWithBlack) return;
        _s.Stingers.SetBlack(dark, ms);
    }

    /// <summary>A blackout lifted by hand after a fade to black: the sound comes back over the show's transition, unless the screens are still black on their own.</summary>
    private void ReleaseBlackAudio()
    {
        if (RigDark()) return;
        _s.Stingers.SetBlack(false, (int)Math.Round(State.Transition.DurationMs));
    }
}
