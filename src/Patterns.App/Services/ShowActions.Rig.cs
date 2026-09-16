using System.Globalization;
using Patterns.Core.LowerThirds;
using Patterns.Rendering.LowerThirds;
using Patterns.Core.Media;
using Patterns.Rendering.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Patterns.Platform.Windows;

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
                if (ScreenRoles.Parse(a.Value) is not { } role) return ActionResult.Refused($"'{a.Value}' is not a role — main, confidence, info or repeater.");
                // Round 67.7: a canvas key (the tile's menu speaking for a joined canvas) sets every screen of it.
                if (ContentTargets.IsCanvasKey(a.Target) && ContentTargets.IsInRig(State, a.Target))
                {
                    if (role == ScreenRole.Repeater) return ActionResult.Refused("A canvas cannot repeat — make one screen a repeater on the Screens page.");
                    var members = ContentTargets.Members(a.Target).Where(id => State.Output.Placements.Any(p => p.ScreenId == id)).ToList();
                    if (members.Count == 0) return ActionResult.Refused($"No screen in '{a.Target}'.");
                    var notes = new List<string>();
                    foreach (var member in members)
                    {
                        var each = SetRole(member, role);
                        if (each.Length > 0) notes.Add(each.Trim());
                    }
                    var canvasLabel = Rig.Geometry(State, _s.Screens.All).LabelFor(State, a.Target);
                    return ActionResult.Done($"{canvasLabel}: every screen is {RoleWords(role)}.{(notes.Count > 0 ? " " + string.Join(" ", notes) : "")}");
                }
                var target = ResolveScreenTarget(a.Target);
                var placement = target is null ? null : State.Output.Placements.FirstOrDefault(p => p.ScreenId == target);
                if (placement is null) return ActionResult.Refused($"No screen '{a.Target}'.");
                var held = SetRole(placement.ScreenId, role);
                var label = Rig.Geometry(State, _s.Screens.All).LabelFor(State, placement.ScreenId);
                return ActionResult.Done($"{label} is {RoleWords(role)}.{held}");
            }
            case ShowActionKind.ScreenAudio:
            {
                // Round 69: the output a screen's sound leaves by. The route itself follows the picture — the programme's
                // sound while the screen shows the programme, its own picture's while it shows one of its own, the repeated
                // screen's while it repeats — so this is set once, at the rig. OFF / NONE / CLEAR names no output.
                var word = a.Value.Trim();
                var none = word.Length == 0 || word.Equals("OFF", StringComparison.OrdinalIgnoreCase) || word.Equals("NONE", StringComparison.OrdinalIgnoreCase) || word.Equals("CLEAR", StringComparison.OrdinalIgnoreCase);
                var destination = "";
                if (!none)
                {
                    var devices = _s.AudioEndpoints.RenderNames;
                    destination = AudioRouting.FindDestination(State, devices, word) ?? "";
                    if (destination.Length == 0) return ActionResult.Refused($"'{word}' is not an output this machine has (by its name), NDI <send>, or computer.");
                }
                // A canvas key (the tile's menu speaking for a joined canvas) sets every screen of it.
                var ids = ContentTargets.IsCanvasKey(a.Target) && ContentTargets.IsInRig(State, a.Target)
                    ? ContentTargets.Members(a.Target).Where(id => State.Output.Placements.Any(p => p.ScreenId == id)).ToList()
                    : ResolveScreenTarget(a.Target) is { } one && State.Output.Placements.Any(p => p.ScreenId == one) ? new List<string> { one } : new List<string>();
                if (ids.Count == 0) return ActionResult.Refused($"No screen '{a.Target}'.");
                var switchedOn = false;
                _s.BulkEdit(() =>
                {
                    foreach (var id in ids) State.Output.Placements.First(p => p.ScreenId == id).AudioOutput = destination;
                    if (destination.Length > 0)
                    {
                        if (!State.AudioRouting.Enabled)
                        {
                            // Naming an output is asking for the matrix: on, seeded as AUDIO ROUTING ON would (before the output's
                            // own row is made — the seed only fills an empty table), so the room keeps hearing what it did.
                            AudioRouting.SeedDefaults(State);
                            State.AudioRouting.Enabled = true;
                            switchedOn = true;
                        }
                        AudioRouting.EnsureRow(State, destination);          // the output takes a row: the matrix shows it, a trim, a delay and a VOG mode can be given
                    }
                });
                if (_s.Sandbox.Active) _s.EditAir(program => { foreach (var id in ids) if (program.Output.Placements.FirstOrDefault(p => p.ScreenId == id) is { } air) air.AudioOutput = destination; });
                _s.AudioGraph?.Reconcile();
                var geo = Rig.Geometry(State, _s.Screens.All);
                var names = string.Join(", ", ids.Select(id => geo.LabelFor(State, id)));
                if (destination.Length == 0) return ActionResult.Done($"{names}: no sound output of its own — its sound leaves where the Audio page's rows put it.");
                var what = ids.Count == 1 ? AudioRouting.SourceOfScreenWords(_s.AirState, ids[0]) : "what each shows";
                return ActionResult.Done($"{names} → {AudioRouting.DestinationLabel(State, destination)}: the sound follows the picture ({what} now).{(switchedOn ? " Audio routing switched on." : "")}{(State.AudioRouting.FollowPicture ? "" : " Follow is off — AUDIO FOLLOW ON lets the picture route it.")}");
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
            case ShowActionKind.ScreenReceived:
            {
                // Round 65.11: what the far end says it receives, in the contract's words — the engineer's own reading of the
                // processor's panel; CLEAR forgets it. Said, never inferred: it is held against the contract as the third witness.
                var target = ResolveScreenTarget(a.Target);
                var placement = target is null ? null : State.Output.Placements.FirstOrDefault(p => p.ScreenId == target);
                if (placement is null) return ActionResult.Refused($"No screen '{a.Target}'.");
                var draft = new SignalContract();
                var error = SignalWords.Apply(a.Value, draft);
                if (error.Length > 0) return ActionResult.Refused(error);
                return SetReceived(placement, draft, draft.IsSet ? "engineer" : "", DateTime.UtcNow);
            }
            case ShowActionKind.ScreenTestRoute:
            {
                // Round 65.10: the diagnostic profile stands in for the contract while the path is proven — the contract itself is never touched.
                var target = ResolveScreenTarget(a.Target);
                var placement = target is null ? null : State.Output.Placements.FirstOrDefault(p => p.ScreenId == target);
                if (placement is null) return ActionResult.Refused($"No screen '{a.Target}'.");
                var on = a.Value.Trim().ToUpperInvariant() switch { "ON" => true, "OFF" => false, _ => !placement.TestRoute };
                var id = placement.ScreenId;
                _s.BulkEdit(() => placement.TestRoute = on);
                if (_s.Sandbox.Active) _s.EditAir(program => { if (program.Output.Placements.FirstOrDefault(p => p.ScreenId == id) is { } air) air.TestRoute = on; });
                var report = SignalReportFor(placement, _s.Screens.All.FirstOrDefault(s => s.Id == id));
                _s.Journal.Record("rig", "TestRoute", report.Label, on ? "On" : "Off", on ? "the diagnostic profile stands in for the contract" : $"the contract holds again: {SignalTruth.DesignWords(placement.Signal)}");
                return ActionResult.Done(on
                    ? $"{report.Label} on TEST ROUTE — the diagnostic profile ({report.Design}) stands in for the contract. {report.Result}."
                    : $"{report.Label} off the test route — the contract holds again: {report.Design}. {report.Result}.");
            }
            case ShowActionKind.RigSaveKnownGood:
            {
                // Round 65.9: the engineer's word that the rig is right — the machine as Windows describes it,
                // each screen's contract, the senders, the bindings and the clock saved beside the settings.
                var facts = MachineProbe.Read();
                if (facts.IsEmpty) facts = MachineProbe.ReadNow();
                var snapshot = SnapshotOf(facts, a.Value.Trim());
                try
                {
                    _s.Kernel.KnownGood.Save(snapshot);
                }
                catch (Exception ex)
                {
                    Log.Warn("The known-good rig could not be saved.", ex);
                    return ActionResult.Refused($"The known-good rig could not be saved: {ex.Message}");
                }
                return ActionResult.Done($"Rig saved as known good{(snapshot.Note.Length > 0 ? $" — {snapshot.Note}" : "")}: {Count(snapshot.Gpus.Count, "GPU")}, {Count(snapshot.Displays.Count, "display")}, {Count(snapshot.Contracts.Count, "contract")}, {Count(snapshot.AudioOut.Count, "audio output")}. Every boot now says what moved.");

                static string Count(int n, string word) => $"{n} {word}{(n == 1 || word == "GPU" ? "" : "s")}";
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
    /// <summary>
    /// One screen's role, on the edited state and the frozen programme alike, with the follow default the role
    /// picks — a confidence or an info screen keeps its picture, a main or a repeater follows — the way the
    /// Screens page does. The lock's words when it moved, else "".
    /// </summary>
    /// <summary>"a main screen", "an info screen".</summary>
    private static string RoleWords(ScreenRole role)
    {
        var word = ScreenRoles.Word(role);
        return $"{("aeiou".Contains(word[0]) ? "an" : "a")} {word} screen";
    }

    private string SetRole(string id, ScreenRole role)
    {
        var placement = State.Output.Placements.FirstOrDefault(p => p.ScreenId == id);
        if (placement is null) return "";
        _s.BulkEdit(() => placement.Role = role);
        if (_s.Sandbox.Active) _s.EditAir(program => { if (program.Output.Placements.FirstOrDefault(p => p.ScreenId == id) is { } air) air.Role = role; });
        var follows = ScreenRoles.DefaultFollows(role);
        return placement.FollowsCues != follows ? " " + SetLock(id, !follows).Message : "";
    }

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
        var ticked = new HashSet<string>(_s.TickedTargets?.Invoke() ?? Array.Empty<string>(), StringComparer.Ordinal);
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
                    ticked = ticked.Contains(target),                           // round 67.8: the tick at the top of its tile — a TICKED take, a fade or SEND TO TICKED reads it
                    own = ContentTargets.UsesOwnPattern(State, target),           // its own picture, not the program's
                    black = _s.Bus.BlackTargets.Contains(target),                // faded to black on its own (FADE SCREEN n) — the blackout is separate
                    // What this screen is actually drawing, and whether that is still what the look
                    // on air asked of it. "own" was the nearest thing before and it is not the same
                    // question: a look frequently gives a screen its own picture on purpose, so a
                    // key lit from "own" cannot tell an instruction from a divergence.
                    pattern = LookService.Shown(State, target).Kind.ToString(),
                    off = _s.LookTally.IsOffLook(target),
                    signal = SignalSummary(x.Placement, x.Info, clockHz),                   // round 65: the contract against what Windows sends — design, observed, result
                    audioOut = x.Placement.AudioOutput,                                       // round 69: the output its sound leaves by (a destination key), "" for none
                    audioOutLabel = x.Placement.AudioOutput.Length > 0 ? AudioRouting.DestinationLabel(State, x.Placement.AudioOutput) : "",
                    audioSource = x.Placement.AudioOutput.Length > 0 ? AudioRouting.SourceOfScreenWords(_s.AirState, x.Placement.ScreenId) : "",   // what its picture's sound is now
                };
            })
            .ToArray();
    }

    /// <summary>The STATE row's signal words for one screen.</summary>
    private object SignalSummary(ScreenPlacement placement, ScreenInfo? info, double clockHz)
    {
        var report = SignalReportFor(placement, info, clockHz);
        return new { design = report.Design, observed = report.Observed, received = report.Received, result = report.Result };
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
        var observed = info is { IsPlanned: false, IsVirtual: false, IsMissing: false } ? DisplayObservation.For(info.Bounds.ToRaster()) : null;
        var clock = clockHz ?? FrameBudgets.ClockHz(FrameBudgets.Readings(ShowClock.Seconds));
        // Round 65.8: the EDID Patterns wrote for this screen, so the view can say whether the display presents it.
        var plannedHash = placement.Signal.IsSet && width > 0 && height > 0 ? Edid.Hash(EdidWriter.Build(EdidPlanFor(placement, info))) : "";
        return SignalTruth.Compare(label, placement.EffectiveSignal, width, height, present, info?.Hz ?? 0, observed, clock, EdidReader.For(observed), plannedHash, placement.TestRoute,
            placement.Received.IsSet ? placement.Received : null, placement.ReceivedBy, placement.ReceivedAtUtc);
    }

    /// <summary>The EDID plan a screen makes (round 65.8): its contract's words, its own size where the contract is silent, its identity in the product code.</summary>
    public EdidPlan EdidPlanFor(ScreenPlacement placement, ScreenInfo? info)
    {
        var width = info?.Bounds.Width ?? placement.PlannedWidth;
        var height = info?.Bounds.Height ?? placement.PlannedHeight;
        return EdidPlan.ForContract(placement.Signal, Rig.LabelFor(placement, info), width, height, EdidPlan.ProductCodeFor(placement.ScreenId));
    }

    /// <summary>The EDID a screen is planned to present, built; null when the screen has no size yet.</summary>
    public PlannedEdid? PlannedEdidFor(ScreenPlacement placement, ScreenInfo? info, int number)
    {
        var plan = EdidPlanFor(placement, info);
        if (plan.Width <= 0 || plan.Height <= 0) return null;
        var bytes = EdidWriter.Build(plan);
        return new PlannedEdid(number, Rig.LabelFor(placement, info), plan, bytes, Edid.Hash(bytes), EdidFor(info));
    }

    /// <summary>The planned EDID for a screen named by its number or id; null for no such screen.</summary>
    public PlannedEdid? PlannedEdid(string word)
    {
        var live = Rig.OrderedLivePlacements(State, _s.Screens.All);
        var target = ResolveScreenTarget(word.Trim());
        var index = live.FindIndex(x => x.Placement.ScreenId == target);
        return index < 0 ? null : PlannedEdidFor(live[index].Placement, live[index].Info, index + 1);
    }

    /// <summary>SCREEN n EDID as JSON: the plan, the timing, the bytes (base64 and hex), the hash, the URL, and whether the display presents it.</summary>
    public string EdidJson(string word)
    {
        var planned = PlannedEdid(word);
        if (planned is null) return JsonUtil.SerializeCompact(new { ok = false, msg = $"No screen '{word.Trim()}'." });
        return JsonUtil.SerializeCompact(new
        {
            n = planned.Number,
            label = planned.Label,
            plan = planned.Plan.Words,
            timing = EdidWriter.Timing(planned.Plan.Width, planned.Plan.Height, planned.Plan.Rate).Words,
            length = planned.Bytes.Length,
            hash = planned.Hash,
            bytes = Convert.ToBase64String(planned.Bytes),
            hex = planned.Hex,
            file = planned.FileBase,
            url = $"/api/screens/{planned.Number}/edid.bin",
            presented = planned.Presented is null ? null : new { identity = planned.Presented.Identity, hash = planned.Presented.Hash, matches = string.Equals(planned.Presented.Hash, planned.Hash, StringComparison.OrdinalIgnoreCase) },
            summary = planned.Summary,
        });
    }

    // ---- the machine and the known-good rig (round 65.9) ---------------------------------------

    /// <summary>
    /// The rig of the moment: the machine as Windows describes it (the kept reading — never a probe
    /// on the caller's thread), each live screen's signal contract by its label, the NDI senders
    /// that are on, the control network's bindings in words (never the token) and the render clock.
    /// </summary>
    public RigSnapshot RigSnapshotNow(string note = "") => SnapshotOf(MachineProbe.Read(), note);

    private RigSnapshot SnapshotOf(MachineFacts facts, string note)
    {
        var contracts = Rig.OrderedLivePlacements(State, _s.Screens.All)
            .Where(x => x.Placement.Signal.IsSet)
            .Select(x => new RigContract(Rig.LabelFor(x.Placement, x.Info), SignalWords.Of(x.Placement.Signal)));
        var senders = State.Ndi.Senders.Where(x => x.Enabled).Select(x => x.Name.Trim().Length > 0 ? x.Name.Trim() : x.Id);
        var clock = FrameBudgets.ClockHz(FrameBudgets.Readings(ShowClock.Seconds));
        return RigSnapshot.From(facts, contracts, senders, BindingWords(State.Control), clock > 0 ? $"{clock:0.0} Hz" : "", note, DateTime.UtcNow);
    }

    /// <summary>The control network's bindings as words, never the token itself: "every interface · HTTP 9696 · TCP 9697 · paired".</summary>
    public static string BindingWords(ControlConfig c)
        => !c.Enabled ? "remote off" : $"{(c.Bind.Length > 0 ? c.Bind : "every interface")} · HTTP {c.HttpPort} · TCP {c.TcpPort}{(PairingToken.Needed(c.Token) ? " · paired" : " · open")}";

    /// <summary>
    /// The rig of the day against the commissioned one; null when none is saved, and the last
    /// comparison (or none) while the first reading of the machine is still on its way — a rig
    /// is never judged against an empty reading.
    /// </summary>
    public RigDrift? RigDriftNow()
    {
        var known = _s.Kernel.KnownGood;
        if (known.Known is null) return null;
        var facts = MachineProbe.Read();
        return facts.IsEmpty ? known.LastDrift : known.Compare(SnapshotOf(facts, ""));
    }

    /// <summary>RIG STATUS as JSON: the machine as Windows describes it, the commissioned rig and the drift from it.</summary>
    public string RigJson()
    {
        var facts = MachineProbe.Read();
        var known = _s.Kernel.KnownGood.Known;
        var drift = RigDriftNow();
        return JsonUtil.SerializeCompact(new
        {
            machine = facts.IsEmpty ? null : new
            {
                takenUtc = facts.TakenUtc,
                build = facts.Build,
                machine = facts.Machine,
                windows = facts.Windows,
                dotnet = facts.DotNet,
                cpu = facts.Cpu,
                cores = facts.Cores,
                ramGB = Math.Round(facts.RamGB, 1),
                gpus = facts.Gpus.Select(g => new { name = g.Name, vendor = g.Vendor, vendorId = g.VendorId, deviceId = g.DeviceId, vramMB = g.VramMB, driver = g.DriverVersion, driverFriendly = g.FriendlyDriver, driverDate = g.DriverDate, provider = g.Provider, software = g.Software }).ToArray(),
                displays = facts.Displays.Select(d => new { key = d.Key, x = d.X, y = d.Y, width = d.Width, height = d.Height, rate = d.Rate, connector = d.Connector, encoding = d.Encoding, bits = d.Bits, hdr = d.Hdr, edid = d.EdidIdentity, edidHash = d.EdidHash }).ToArray(),
                audio = facts.Audio.Select(a => new { name = a.Name, flow = a.Flow, isDefault = a.IsDefault, rateHz = a.SampleRateHz, bits = a.Bits, channels = a.Channels }).ToArray(),
                power = facts.PowerPlan,
                onBattery = facts.OnBattery,
                gpuScheduling = facts.HardwareScheduling,
                gameDvr = facts.GameDvr,
                overlayPlanes = facts.MultiplaneOverlay,
                notes = facts.Notes,
                summary = facts.Summary,
                lines = facts.Lines,
            },
            known = known is null ? null : new
            {
                takenUtc = known.TakenUtc,
                note = known.Note,
                build = known.Build,
                windows = known.Windows,
                cpu = known.Cpu,
                gpus = known.Gpus.Select(g => new { name = g.Name, driver = g.DriverVersion, driverDate = g.DriverDate, vramMB = g.VramMB }).ToArray(),
                displays = known.Displays.Select(d => new { key = d.Key, width = d.Width, height = d.Height, rate = d.Rate, connector = d.Connector, edid = d.EdidIdentity, edidHash = d.EdidHash }).ToArray(),
                contracts = known.Contracts.Select(c => new { screen = c.Screen, words = c.Words }).ToArray(),
                audioOut = known.AudioOut,
                audioIn = known.AudioIn,
                ndiSenders = known.NdiSenders,
                bindings = known.Bindings,
                powerPlan = known.PowerPlan,
                gpuScheduling = known.HardwareScheduling,
                renderClock = known.RenderClock,
            },
            drift = drift is null ? null : new
            {
                same = drift.Same,
                changes = drift.Changed,
                headline = drift.Headline,
                lines = drift.Lines.Select(l => new { same = l.Same, item = l.Item, words = l.Words, severe = l.Severe }).ToArray(),
            },
            words = _s.Kernel.KnownGood.Words,
        });
    }

    // ---- what the far end receives (round 65.11) -------------------------------------------------

    /// <summary>The far end's word set on a screen — from the engineer or a device — journaled when it changes, the verdict read back.</summary>
    private ActionResult SetReceived(ScreenPlacement placement, SignalContract said, string by, DateTime atUtc)
    {
        var id = placement.ScreenId;
        var before = SignalWords.Of(placement.Received);
        var after = SignalWords.Of(said);
        _s.BulkEdit(() =>
        {
            placement.Received.CopyFrom(said);
            placement.ReceivedBy = said.IsSet ? by : "";
            placement.ReceivedAtUtc = said.IsSet ? atUtc : null;
        });
        if (_s.Sandbox.Active) _s.EditAir(program =>
        {
            if (program.Output.Placements.FirstOrDefault(p => p.ScreenId == id) is { } air)
            {
                air.Received.CopyFrom(said);
                air.ReceivedBy = said.IsSet ? by : "";
                air.ReceivedAtUtc = said.IsSet ? atUtc : null;
            }
        });
        var report = SignalReportFor(placement, _s.Screens.All.FirstOrDefault(s => s.Id == id));
        if (before != after)
        {
            _s.Journal.Record(by.Length > 0 ? by : "desk", "Received", report.Label, said.IsSet ? report.Result : "Cleared",
                said.IsSet ? $"{(by.Length > 0 ? by : "the far end")} receives {SignalTruth.DesignWords(said)} — {report.Result}" : "what the far end receives is forgotten");
        }
        return ActionResult.Done(said.IsSet
            ? $"{report.Label} — {(by.Length > 0 ? by : "the far end")} receives {SignalTruth.DesignWords(said)}. {report.Result}."
            : $"{report.Label} — what the far end receives is forgotten. {report.Result}.");
    }

    /// <summary>
    /// A device's input-status adapter said what its input receives (round 65.11): the screen it names — a
    /// number in the overview or a label — gets the word, with the device as the witness. Runs on the desk's thread.
    /// </summary>
    public void ReceiveFromDevice(InputStatusReport report)
    {
        var target = ResolveScreenTarget(report.Screen);
        var placement = target is null ? null : State.Output.Placements.FirstOrDefault(p => p.ScreenId == target);
        if (placement is null)
        {
            Log.Warn($"Device '{report.Device}' reports an input for screen '{report.Screen}', which the rig does not have.");
            return;
        }
        if (SignalWords.Of(placement.Received) == SignalWords.Of(report.Received) && placement.ReceivedBy == report.Device) return;   // the same word again: nothing moved
        SetReceived(placement, report.Received, report.Device, report.AtUtc);
    }

    // ---- the commissioning flow (round 65.10) ---------------------------------------------------

    private bool _outputsWereLive;

    /// <summary>
    /// The facts the commissioning flow judges: what Windows shows, what is planned and what is lost,
    /// each live screen's contract, test route, EDID and verdict, the outputs, the known-good rig.
    /// Reads the kept machine reading and the signal reports — nothing here probes on the caller's thread.
    /// </summary>
    public CommissioningFacts CommissioningFactsNow()
    {
        var all = _s.Screens.All;
        var real = all.Where(s => !s.IsPlanned && !s.IsVirtual && !s.IsMissing).ToList();
        var placements = State.Output.Placements.Where(p => !p.IsVirtual).ToList();
        // A display that went is disabled and marked planned by the hot-plug service: it is DISCOVER's to name, never ASSIGN's.
        var lost = placements.Where(HotPlugWatch.IsLost).Select(p => $"{Rig.LabelFor(p, all.FirstOrDefault(s => s.Id == p.ScreenId))} — {HotPlugWatch.LostWords(p)}").ToList();
        var clock = FrameBudgets.ClockHz(FrameBudgets.Readings(ShowClock.Seconds));
        var screens = new List<CommissionScreen>();
        var n = 0;
        foreach (var (placement, info) in Rig.OrderedLivePlacements(State, all))
        {
            n++;
            if (info is null || info.IsVirtual || !placement.Enabled) continue;                        // the screens the show uses; a disabled one is not commissioned
            var report = SignalReportFor(placement, info, clock);
            var edid = EdidFor(info);
            var advertises = edid is not null && !report.Lines.Any(l => l.Item.StartsWith("Advertised", StringComparison.Ordinal) && l.Light == CheckLight.Amber);
            screens.Add(new CommissionScreen(n, report.Label, placement.Signal.IsSet, placement.TestRoute, edid is not null, advertises, report.Verdict, report.Observed, SignalTruth.Unobserved(report)));
        }
        _outputsWereLive |= _s.Outputs.IsLive;
        var known = _s.Kernel.KnownGood;
        var drift = RigDriftNow();
        return new CommissioningFacts
        {
            DisplaysSeen = real.Count,
            PlannedScreens = placements.Count(p => p.IsPlannedDisplay && !HotPlugWatch.IsLost(p)),
            EnabledScreens = placements.Count(p => p.Enabled && !p.IsPlannedDisplay),
            LostScreens = lost,
            Screens = screens,
            OutputsLive = _s.Outputs.IsLive,
            OutputsWereLive = _outputsWereLive,
            KnownGoodSaved = known.Known is not null,
            KnownGoodSame = drift?.Same,
            KnownGoodWords = known.Known is null ? "" : known.Words,
        };
    }

    /// <summary>The flow judged: seven lines, the next step, the headline.</summary>
    public CommissioningReport CommissioningReport() => Commissioning.Build(CommissioningFactsNow());

    /// <summary>COMMISSION STATUS as JSON: every stage with its light, value and next step; the headline; the count.</summary>
    public string CommissionJson()
    {
        var report = CommissioningReport();
        return JsonUtil.SerializeCompact(new
        {
            complete = report.Complete,
            done = report.Done,
            total = report.Total,
            percent = report.Percent,
            headline = report.Headline,
            next = report.Next,
            overall = report.Overall.ToString().ToLowerInvariant(),
            stages = report.Lines.Select(l => new { stage = l.Stage.ToString(), title = l.Title, light = l.Light.ToString().ToLowerInvariant(), value = l.Value, next = l.Next }).ToArray(),
        });
    }

    /// <summary>The STATE row: where the flow is, in a few words.</summary>
    public object CommissioningRow()
    {
        var report = CommissioningReport();
        return new { complete = report.Complete, done = report.Done, total = report.Total, stage = report.Current?.Title ?? "", headline = report.Headline, next = report.Next };
    }

    /// <summary>The flow's lines for the assistant's brief: the headline first, then each stage with its mark.</summary>
    public IReadOnlyList<string> CommissioningBriefLines()
    {
        var report = CommissioningReport();
        var lines = new List<string> { "Commissioning: " + report.Headline };
        lines.AddRange(report.Words);
        return lines;
    }

    /// <summary>The machine's lines for the assistant's brief — without the machine's name — with the known-good rig's verdict last.</summary>
    public IReadOnlyList<string> MachineBriefLines()
    {
        var facts = MachineProbe.Read();
        var lines = new List<string>(facts.IsEmpty ? Array.Empty<string>() : facts.LinesFor(withMachineName: false));
        var known = _s.Kernel.KnownGood;
        if (known.Known is null)
        {
            lines.Add("Known good rig: not saved — SAVE KNOWN GOOD on the Machine page once the rig is right.");
        }
        else
        {
            var drift = RigDriftNow();
            lines.Add(drift is null ? "Known good rig: saved — not compared yet."
                : drift.Same ? $"Known good rig: {drift.Headline}."
                : $"Known good rig: {drift.Headline} — {string.Join("; ", drift.Changes)}.");
        }
        return lines;
    }

    /// <summary>The EDID behind a screen's display as Windows keeps it, parsed; null when there is none to read.</summary>
    public EdidInfo? EdidFor(ScreenInfo? info)
        => info is { IsPlanned: false, IsVirtual: false, IsMissing: false } ? EdidReader.For(DisplayObservation.For(info.Bounds.ToRaster())) : null;

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
        var words = report.Verdict == SignalVerdict.Match ? $"{report.Design} — as observed"
            : report.Verdict == SignalVerdict.Partial ? $"{report.Design} — as observed where the path states it; {SignalTruth.Unobserved(report)} never stated"   // round 72
            : moved.Count > 0 ? string.Join("; ", moved) : report.Observed;
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
            received = r.Received,
            result = r.Result,
            edid = edid is null ? null : new
            {
                identity = edid.Identity,
                manufacturer = edid.Manufacturer,
                product = edid.ProductCode,
                serial = edid.SerialText.Length > 0 ? edid.SerialText : edid.SerialNumber.ToString(CultureInfo.InvariantCulture),
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
