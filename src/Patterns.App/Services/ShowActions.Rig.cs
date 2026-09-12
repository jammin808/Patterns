using Patterns.Core.LowerThirds;
using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// The rig's verbs: the outputs on and off, a screen or a canvas on, off, locked; the remote screen list.
/// </summary>
public sealed partial class ShowActions
{
    private ActionResult? RunRig(ShowAction a, ActionOrigin origin)
    {
        switch (a.Kind)
        {
            case ShowActionKind.OutputsOn:
                if (State.Mode == ShowMode.Prep)
                {
                    return ActionResult.Refused("PREP MODE — outputs are held closed. Switch to SHOW in the header when you are at the venue.");
                }
                if (_s.OutputsHeldBy.Length > 0)
                {
                    return ActionResult.Refused($"Outputs are held closed — {_s.OutputsHeldBy}. TAKE OVER lifts the hold (Machine page, TWIN).");
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
                return SetLock(target, locked);
            }
            case ShowActionKind.ScreenRole:
            {
                var target = ResolveScreenTarget(a.Target);
                var placement = target is null ? null : State.Output.Placements.FirstOrDefault(p => p.ScreenId == target);
                if (placement is null) return ActionResult.Refused($"No screen '{a.Target}'.");
                if (ScreenRoles.Parse(a.Value) is not { } role) return ActionResult.Refused($"'{a.Value}' is not a role — main, confidence, info or repeater.");
                var id = placement.ScreenId;
                _s.BulkEdit(() => placement.Role = role);
                if (_s.Sandbox.Active) _s.EditAir(program => { if (program.Output.Placements.FirstOrDefault(p => p.ScreenId == id) is { } air) air.Role = role; });
                // The role picks its follow default, the way the Screens page does: a confidence or an info screen keeps its picture.
                var follows = ScreenRoles.DefaultFollows(role);
                var held = placement.FollowsCues != follows ? " " + SetLock(id, !follows).Message : "";
                var label = Rig.Geometry(State, _s.Screens.All).LabelFor(State, id);
                return ActionResult.Done($"{label} is a {ScreenRoles.Word(role)} screen.{held}");
            }
            case ShowActionKind.ScreenLabel:
            {
                var target = ResolveScreenTarget(a.Target);
                if (target is null) return ActionResult.Refused($"No screen or canvas '{a.Target}'.");
                var text = a.Value.Trim();
                var id = target;
                if (ContentTargets.IsCanvasKey(id))
                {
                    _s.BulkEdit(() => RigEditor.CanvasConfigFor(State, id, create: true)!.Name = text);
                    if (_s.Sandbox.Active) _s.EditAir(program => RigEditor.CanvasConfigFor(program, id, create: true)!.Name = text);
                }
                else
                {
                    _s.BulkEdit(() => { if (State.Output.Placements.FirstOrDefault(p => p.ScreenId == id) is { } p) p.CustomLabel = text; });
                    if (_s.Sandbox.Active) _s.EditAir(program => { if (program.Output.Placements.FirstOrDefault(p => p.ScreenId == id) is { } p) p.CustomLabel = text; });
                }
                var label = Rig.Geometry(State, _s.Screens.All).LabelFor(State, id);
                return ActionResult.Done(text.Length > 0 ? $"{label} — named '{text}'." : $"{label} — label cleared.");
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
            default:
                return null;
        }
    }

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

    /// <summary>
    /// A target locked or released. "Keep what you show": the picture on air for the target,
    /// whichever state holds it — the live model, or the frozen program while the sandbox is
    /// open. Both get the lock, so a look to air and the next TAKE agree.
    /// </summary>
    private ActionResult SetLock(string target, bool locked)
    {
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
        var geometry = _s.Bus.Current.Rig;
        return Rig.OrderedLivePlacements(State, known)
            .Select((x, i) =>
            {
                // The target the screen renders through — its canvas, or itself — is what the wall arms and gives a picture of its own.
                var target = geometry.TargetOf(x.Placement.ScreenId);
                return (object)new
                {
                    n = i + 1,
                    label = Rig.LabelFor(x.Placement, x.Info),
                    enabled = x.Placement.Enabled,
                    group = Rig.LetterOf(groups, x.Placement),
                    locked = !x.Placement.FollowsCues,
                    role = x.Placement.Role.ToString().ToLowerInvariant(),
                    armed = _s.Arming.IsArmed(target),                          // the next CUT / TAKE changes it
                    own = ContentTargets.UsesOwnPattern(State, target),           // its own picture, not the program's
                    black = _s.Bus.BlackTargets.Contains(target),                // faded to black on its own (FADE SCREEN n) — the blackout is separate
                    // What this screen is actually drawing, and whether that is still what the look
                    // on air asked of it. "own" was the nearest thing before and it is not the same
                    // question: a look frequently gives a screen its own picture on purpose, so a
                    // key lit from "own" cannot tell an instruction from a divergence.
                    pattern = LookService.Shown(State, target).Kind.ToString(),
                    off = _s.LookTally.IsOffLook(target),
                };
            })
            .ToArray();
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
