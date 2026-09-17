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
                    return ActionResult.Refused($"'{a.Target}' is not a place to fade — leave it empty for every screen, or SCREEN 2, GROUP A, FOCUSED, TICKED, GROUPS, ID <screen id>.");
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
                    return ActionResult.Refused($"'{a.Target}' is not a place to take to — leave it empty for every armed screen, or SCREEN 2, GROUP A, FOCUSED, TICKED, GROUPS.");
                }
                // One rule (round 67): TakePlan says what this scope changes and what it holds — LOCKED never
                // taken, ARM counting inside every scope, a repeater never, everything outside the scope kept —
                // and a take that would move nothing is refused with the reason, never reported as done.
                var plan = PlanTake(scope);
                if (plan.IsRefused) return ActionResult.Refused(plan.Refusal!);
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
                    return ActionResult.Requested($"TAKE under the sting '{sting.DisplayName}' — the preview lands {plan.Where} when the clip ends; the show's transition is unchanged.");
                }
                if (next is not null) _s.NextTake.Consume();
                ArmNextTransition(next);
                _s.Sandbox.SendAll(cut, plan.Kept);
                var rearmed = _s.Sandbox.Active ? " EDIT SAFE re-armed." : "";
                var kept = plan.Kept.Count == 0 ? "" : $" ({plan.Kept.Count} kept their picture)";
                var arrived = next is null ? "" : $" Arrived by {next.Words} (one shot).";
                return ActionResult.Done((cut
                    ? $"CUT — sandbox is now the program {plan.Where}{kept}."
                    : $"TAKE — sandbox faded up {plan.Where}{kept}.") + arrived + rearmed);
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
                    return ActionResult.Requested($"TAKE under the sting '{sting.DisplayName}' — the preview lands on {where} alone when the clip ends.");
                }
                if (nextOne is not null) _s.NextTake.Consume();
                ArmNextTransition(nextOne);
                // What this tile's PVW shows (round 67): its own picture when one was edited or staged there, else the programme's preview.
                _s.Sandbox.SendToTargets(new[] { target }, toAir: true, cut: cutOne, ownPicture: true);
                var arrivedOne = nextOne is null ? "" : $" Arrived by {nextOne.Words} (one shot).";
                return ActionResult.Done((cutOne
                    ? $"CUT — the preview is on {where} alone, as its own picture; every other screen stays."
                    : $"TAKE — the preview fades up on {where} alone, as its own picture; every other screen stays.") + arrivedOne);
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// The content targets a scope names on this rig, or why it names none: the focused tile, the
    /// ticked tiles, the ticked tiles that are groups, a screen by wall number (the canvas it
    /// renders through when it joined one), a group by wall letter, a target id in the rig. A
    /// fade and a CUT / TAKE read the same words through here.
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
                var groups = (_s.TickedTargets?.Invoke() ?? Array.Empty<string>()).Where(ContentTargets.IsCanvasKey).ToList();
                return groups.Count == 0 ? (Array.Empty<string>(), "Tick a group (a joined canvas) on the wall first.") : (groups, null);
            }
            case FadeScopeKind.Screen:
            {
                var n = int.Parse(scope.Arg, System.Globalization.CultureInfo.InvariantCulture);
                var ordered = Rig.OrderedLivePlacements(State, _s.Screens.All);
                if (n < 1 || n > ordered.Count) return (Array.Empty<string>(), $"No screen {n} — the rig has {ordered.Count}.");
                return (new[] { geometry.TargetOf(ordered[n - 1].Placement.ScreenId) }, null);
            }
            case FadeScopeKind.Group:
            {
                foreach (var key in geometry.Targets)
                {
                    if (ContentTargets.IsCanvasKey(key) && geometry.LetterOf(key) == scope.Arg) return (new[] { key }, null);
                }
                return (Array.Empty<string>(), $"No group {scope.Arg} on the wall.");
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
        _s.Sandbox.SendAll(cut: false, landing.Kept);
        var rearmed = _s.Sandbox.Active ? " EDIT SAFE re-armed." : "";
        var kept = landing.Kept.Count == 0 ? "" : $" ({landing.Kept.Count} kept their picture)";
        return ActionResult.Done($"TAKE — sandbox faded up {ticket.Where}{kept}, as pressed under the sting '{ticket.Cover}'{landing.HeldWords}.{rearmed}");
    }

    public TakePlan PlanTake(FadeScope scope)
    {
        var geometry = Rig.Geometry(State, _s.Screens.All);
        var rig = RigTargets(geometry);
        IReadOnlyList<string>? named = null;
        if (scope.Kind is FadeScopeKind.Screen or FadeScopeKind.Group or FadeScopeKind.Target)
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
            rig.Add(new TakeTarget(target, geometry.LabelFor(State, target), canvas, mirror,
                ScreenRoles.IsLocked(State, target), _s.Arming.IsArmed(target), ticked.Contains(target), shape, key));
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

    /// <summary>STATE's take row (round 67): the wall's scope, the plan it makes, and the next take's one-shot.</summary>
    public object TakeRow()
    {
        var words = _s.TakeScopeWords?.Invoke() ?? "";
        var plan = PlanTake(FadeScope.Parse(words) ?? FadeScope.Everything);
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
            next = _s.NextTake.Row(),
            landing = _s.Stingers.SessionTicket is { } ticket                                          // round 72: the take waiting under a sting — the press's promise
                ? new { sting = ticket.Cover, scope = ticket.Scope, targets = ticket.Taken, where = ticket.Where, words = ticket.Words, pressedUtc = ticket.PressedUtc }
                : null,
        };
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
