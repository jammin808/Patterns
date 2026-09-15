using Patterns.Core.LowerThirds;
using Patterns.Rendering.LowerThirds;
using Patterns.Core.Media;
using Patterns.Rendering.Media;
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
            case ShowActionKind.ScreenSignal:
            {
                // Round 65: the link's contract in words — each word sets its property, the rest stay; CLEAR empties it.
                var target = ResolveScreenTarget(a.Target);
                var placement = target is null ? null : State.Output.Placements.FirstOrDefault(p => p.ScreenId == target);
                if (placement is null) return ActionResult.Refused($"No screen '{a.Target}'.");
                var draft = new SignalContract();
                draft.CopyFrom(placement.Signal);
                var error = SignalWords.Apply(a.Value, draft);
                if (error.Length > 0) return ActionResult.Refused(error);
                var id = placement.ScreenId;
                _s.BulkEdit(() => placement.Signal.CopyFrom(draft));
                if (_s.Sandbox.Active) _s.EditAir(program => { if (program.Output.Placements.FirstOrDefault(p => p.ScreenId == id) is { } air) air.Signal.CopyFrom(draft); });
                var report = SignalReportFor(placement, _s.Screens.All.FirstOrDefault(s => s.Id == id));
                return ActionResult.Done($"{report.Label} signal contract: {report.Design}. {report.Result}.");
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
        var clockHz = FrameBudgets.ClockHz(FrameBudgets.Readings(ShowClock.Seconds));
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
                    signal = SignalSummary(x.Placement, x.Info, clockHz),                   // round 65: the contract against what Windows sends — design, observed, result
                };
            })
            .ToArray();
    }

    /// <summary>The STATE row's signal words for one screen.</summary>
    private object SignalSummary(ScreenPlacement placement, ScreenInfo? info, double clockHz)
    {
        var report = SignalReportFor(placement, info, clockHz);
        return new { design = report.Design, observed = report.Observed, result = report.Result };
    }

    /// <summary>
    /// Round 65: one screen's signal truth — the contract (DESIGN), what Patterns asks Windows for
    /// (REQUESTED), what Windows is observed to send (OBSERVED, unknowns left unknown) and the
    /// verdict, with the lines Super Check shows. The render clock is read once per call unless
    /// the caller hands it in.
    /// </summary>
    public SignalReport SignalReportFor(ScreenPlacement placement, ScreenInfo? info, double? clockHz = null)
    {
        var label = Rig.LabelFor(placement, info);
        var width = info?.Bounds.Width ?? placement.PlannedWidth;
        var height = info?.Bounds.Height ?? placement.PlannedHeight;
        var present = placement.FpsOverride > 0 ? placement.FpsOverride : State.Output.MasterFps;
        var observed = info is { IsPlanned: false, IsVirtual: false, IsMissing: false } ? DisplayObservation.For(info.Bounds) : null;
        var clock = clockHz ?? FrameBudgets.ClockHz(FrameBudgets.Readings(ShowClock.Seconds));
        return SignalTruth.Compare(label, placement.Signal, width, height, present, info?.Hz ?? 0, observed, clock, EdidReader.For(observed));
    }

    /// <summary>The EDID behind a screen's display as Windows keeps it, parsed; null when there is none to read.</summary>
    public EdidInfo? EdidFor(ScreenInfo? info)
        => info is { IsPlanned: false, IsVirtual: false, IsMissing: false } ? EdidReader.For(DisplayObservation.For(info.Bounds)) : null;

    /// <summary>The signal lines for the assistant's brief: one per screen with a contract or an observation, capability apart from signal.</summary>
    public IReadOnlyList<string> SignalBriefLines()
        => SignalReports().Where(r => r.Lines.Count > 0).Select(r => r.BriefLine).ToList();

    /// <summary>Every live screen's signal report, in wall order — the facts for Super Check; a verdict that moved since the last reading goes into the journal.</summary>
    public IReadOnlyList<SignalReport> SignalReports()
    {
        var clock = FrameBudgets.ClockHz(FrameBudgets.Readings(ShowClock.Seconds));
        var reports = new List<SignalReport>();
        foreach (var (placement, info) in Rig.OrderedLivePlacements(State, _s.Screens.All))
        {
            var report = SignalReportFor(placement, info, clock);
            reports.Add(report);
            JournalSignal(placement.ScreenId, report);
        }
        return reports;
    }

    private readonly Dictionary<string, SignalVerdict> _signalVerdicts = new(StringComparer.Ordinal);

    /// <summary>
    /// Round 65: a signal verdict that changed is a show event — "the rate moved at 14:02" is the
    /// line a support engineer wants — so the journal gets one entry per change, never one per
    /// reading. A screen whose contract is empty has no verdict to move.
    /// </summary>
    private void JournalSignal(string screenId, SignalReport report)
    {
        var had = _signalVerdicts.TryGetValue(screenId, out var previous);
        if (had && previous == report.Verdict) return;
        _signalVerdicts[screenId] = report.Verdict;
        if (!placementHasContract(screenId)) return;
        var moved = report.Lines.Where(l => l.Light is CheckLight.Red or CheckLight.Amber).Select(l => $"{l.Item.ToLowerInvariant()}: {l.Value}").ToList();
        var words = report.Verdict == SignalVerdict.Match ? $"{report.Design} — as observed" : moved.Count > 0 ? string.Join("; ", moved) : report.Observed;
        _s.Journal.Record("signal", "SignalVerdict", report.Label, report.Result, (had ? $"{SignalReport.Words(previous)} → {report.Result}: " : "") + words);

        bool placementHasContract(string id) => State.Output.Placements.FirstOrDefault(p => p.ScreenId == id) is { Signal.IsSet: true };
    }

    /// <summary>SCREEN n SIGNAL as JSON: the one screen's report, or every screen's when <paramref name="word"/> is empty.</summary>
    public string SignalJson(string word)
    {
        var clock = FrameBudgets.ClockHz(FrameBudgets.Readings(ShowClock.Seconds));
        var live = Rig.OrderedLivePlacements(State, _s.Screens.All);
        if (word.Trim().Length == 0) return JsonUtil.SerializeCompact(live.Select((x, i) => SignalRow(i + 1, SignalReportFor(x.Placement, x.Info, clock))).ToArray());
        var target = ResolveScreenTarget(word.Trim());
        var index = live.FindIndex(x => x.Placement.ScreenId == target);
        if (index < 0) return JsonUtil.SerializeCompact(new { ok = false, msg = $"No screen '{word.Trim()}'." });
        return JsonUtil.SerializeCompact(SignalRow(index + 1, SignalReportFor(live[index].Placement, live[index].Info, clock)));
    }

    private object SignalRow(int n, SignalReport r)
    {
        var info = _s.Screens.All.FirstOrDefault(s => Rig.LabelFor(State.Output.Placements.FirstOrDefault(p => p.ScreenId == s.Id) ?? new ScreenPlacement { ScreenId = s.Id }, s) == r.Label);
        var edid = EdidFor(info);
        return new
        {
            n,
            label = r.Label,
            design = r.Design,
            advertised = r.Advertised,
            requested = r.Requested,
            observed = r.Observed,
            result = r.Result,
            edid = edid is null ? null : new
            {
                identity = edid.Identity,
                manufacturer = edid.Manufacturer,
                product = edid.ProductCode,
                serial = edid.SerialText.Length > 0 ? edid.SerialText : edid.SerialNumber.ToString(),
                name = edid.Name,
                version = edid.Version,
                preferred = edid.Preferred?.Words ?? "",
                extensions = edid.ExtensionCount,
                blocks = edid.Blocks,
                checksums = edid.ChecksumsValid,
                problems = edid.Problems,
                hash = edid.Hash,
                length = edid.Length,
            },
            lines = r.Lines.Select(l => new { item = l.Item, light = l.Light.ToString().ToLowerInvariant(), value = l.Value, note = l.Note }).ToArray(),
        };
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
