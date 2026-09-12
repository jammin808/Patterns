using Patterns.Core.LowerThirds;
using Patterns.Core.Media;
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

            case ShowActionKind.Take:
            case ShowActionKind.Cut:
            {
                if (!_s.Sandbox.Active)
                {
                    return ActionResult.Refused("Open EDIT SAFE (the sandbox) first — build the look, then CUT or TAKE it to air.");
                }
                var cut = a.Kind == ShowActionKind.Cut;
                // Where: every armed screen — the wall's ARM and LOCK decide — or a part of the rig
                // (the focused tile, the ticked tiles, the ticked groups, SCREEN n, GROUP A) with
                // everything outside it keeping its picture exactly as an un-armed tile does: pinned
                // as its own, lifted by the next full send. The same words a fade takes.
                if (FadeScope.Parse(a.Target) is not { } scope)
                {
                    return ActionResult.Refused($"'{a.Target}' is not a place to take to — leave it empty for every armed screen, or SCREEN 2, GROUP A, FOCUSED, TICKED, GROUPS.");
                }
                var all = Rig.Targets(State, _s.Screens.All);
                // Un-armed tiles and locked screens (a confidence monitor, an info screen) keep their picture.
                var held = new HashSet<string>(_s.Arming.Unarmed, StringComparer.Ordinal);
                foreach (var t in ScreenRoles.LockedTargets(State, all)) held.Add(t);
                var where = "on every armed screen";
                if (!(scope.IsEverything || (scope.Kind == FadeScopeKind.Focused && _s.FocusedTarget?.Invoke() is null)))
                {
                    var (targets, problem) = FadeTargets(scope);
                    if (problem is not null) return ActionResult.Refused(problem);
                    var inside = new HashSet<string>(targets, StringComparer.Ordinal);
                    foreach (var t in all)
                    {
                        if (!inside.Contains(t)) held.Add(t);
                    }
                    where = $"on {FadeWords(targets)}";
                }
                _s.Sandbox.SendAll(cut, held);
                var rearmed = _s.Sandbox.Active ? " EDIT SAFE re-armed." : "";
                var kept = held.Count == 0 ? "" : $" ({held.Count} kept their picture)";
                return ActionResult.Done((cut
                    ? $"CUT — sandbox is now the program {where}{kept}."
                    : $"TAKE — sandbox faded up {where}{kept}.") + rearmed);
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
