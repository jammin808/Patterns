using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Sandbox look programming: while active, the operator's edits go on building in the real
/// model (so every editor panel just works) and the preview shows them — but outputs and
/// NDI hold a frozen clone of the program, so nothing reaches the audience. Blackout stays
/// live through the freeze (an emergency is an emergency). The sandbox then goes somewhere:
/// sent to program (all screens), sent to chosen screens as their per-screen pattern,
/// saved as a look (the normal save captures the sandbox), or discarded.
/// </summary>
public sealed class SandboxService
{
    private readonly AppServices _services;
    private ShowState? _program;      // frozen full clone the audience keeps seeing
    private string? _contentBefore;   // look-capture for discard / send-to-screens

    public SandboxService(AppServices services) => _services = services;

    public bool Active { get; private set; }

    /// <summary>The frozen program the audience is seeing, or null when not sandboxed.
    /// Air-targeted actions (look recalls, cues, stingers, playlist parts) edit this.</summary>
    public ShowState? ProgramState => Active ? _program : null;

    public void Enter()
    {
        if (Active) return;
        _contentBefore = LookService.Capture(_services.State);
        _program = JsonUtil.Clone(_services.State);
        Active = true;
        // From here the audience's picture is this clone, not the show file: the desk watches it
        // so the recovery record follows every move of it — a cue, a send, a stinger, a lower
        // third — without any of those paths having to know the record exists.
        _services.WatchAir(_program);
        _services.RepublishNow();
        Log.Info("Sandbox open — outputs hold program, edits stay in the preview.");
    }

    /// <summary>Called from the publish path on every change while active.</summary>
    public void PublishBoth()
    {
        if (_program is null) return;
        _program.Blackout = _services.State.Blackout; // transport is never sandboxed
        _services.Bus.Publish(_program, _services.AirWatch);
        _services.Bus.PublishSandbox(_services.State, _services.StateWatch, SettledOwn());
    }

    // ---- what a tile's PVW holds (round 78) --------------------------------------------------------

    /// <summary>
    /// The pictures a tile's PVW can hold, and so what its own CUT / TAKE lands — one rule, read by the
    /// snapshot the miniatures draw (<see cref="SettledOwn"/>), by the take (<see cref="PvwPicture"/>),
    /// by the menus and the Eye (<see cref="IsStaged"/>):
    ///
    ///   pending  — a picture the audience has not seen: an own picture in the edited state that the frozen
    ///              program lacks (SEND staged it) or has differently (the preview edited it). The PVW holds it.
    ///   editing  — an own picture already on air, while the editors are on this target: the PVW shows the
    ///              picture being worked on, exactly as the big PREVIEW pane and the editors do.
    ///   settled  — an own picture already on air and no editor on it: the PVW follows the programme's
    ///              preview, because that is what a TAKE on the tile puts up.
    ///   programme — a target that follows the programme: the programme's preview.
    ///
    /// The field pressed TAKE eleven times on a settled tile: each press copied the tile's own picture over
    /// itself and the desk said "fades up" each time. Attempts are not facts — a take lands what the PVW
    /// shows, and a take that would change nothing is refused with the reason (<see cref="EffectOf"/>).
    /// </summary>
    public bool IsStaged(string targetId)
        => Active && _program is not null && Pending(_services.State, _program, ScreenRoles.ResolveMirror(_services.State, targetId));

    /// <summary>The picture this target's PVW shows while the sandbox is open — what a CUT / TAKE on its tile lands.</summary>
    public PatternConfig PvwPicture(string targetId)
    {
        var state = _services.State;
        var resolved = ScreenRoles.ResolveMirror(state, targetId);
        if (!Active || _program is null) return LookService.Shown(state, resolved);
        if (Pending(state, _program, resolved) || resolved == _services.EditingTargetId) return LookService.Shown(state, resolved);
        return state.Pattern;
    }

    /// <summary>What a CUT / TAKE on a tile would do to what the audience sees on its target.</summary>
    public enum TakeEffect
    {
        /// <summary>The picture changes.</summary>
        Picture,
        /// <summary>The same picture, but the target goes its own way (OWN): it keeps it when the programme changes.</summary>
        OwnOnly,
        /// <summary>Nothing: the target already shows this picture as its own.</summary>
        Nothing,
    }

    public TakeEffect EffectOf(string targetId)
    {
        if (!Active || _program is null) return TakeEffect.Nothing;
        var resolved = ScreenRoles.ResolveMirror(_services.State, targetId);
        if (!SamePicture(PvwPicture(resolved), LookService.Shown(_program, resolved))) return TakeEffect.Picture;
        return ContentTargets.UsesOwnPattern(_program, resolved) ? TakeEffect.Nothing : TakeEffect.OwnOnly;
    }

    /// <summary>Of the targets a wall TAKE changes, how many the audience already sees with the picture it would land — the edited state's picture for them is the air's.</summary>
    public int AlreadyOnAir(IReadOnlyList<string> taken)
    {
        if (!Active || _program is null) return 0;
        var n = 0;
        foreach (var t in taken)
        {
            if (SamePicture(LookService.Shown(_services.State, t), LookService.Shown(_program, t))) n++;
        }
        return n;
    }

    private static bool Pending(ShowState state, ShowState program, string targetId)
    {
        if (!ContentTargets.UsesOwnPattern(state, targetId)) return false;
        if (!ContentTargets.UsesOwnPattern(program, targetId)) return true;
        var mine = OwnPicture(state, targetId);
        var air = OwnPicture(program, targetId);
        return mine is null || air is null || !SamePicture(mine, air);
    }

    private static PatternConfig? OwnPicture(ShowState s, string targetId)
    {
        foreach (var a in s.Independent)
        {
            if (a.ScreenId == targetId) return a.Pattern;
        }
        return null;
    }

    /// <summary>The same picture in every property the show file keeps — the identity a take compares, as the recovery record would see it.</summary>
    public static bool SamePicture(PatternConfig a, PatternConfig b)
        => ReferenceEquals(a, b) || JsonUtil.SerializeCompact(a) == JsonUtil.SerializeCompact(b);

    private HashSet<string>? _settled;

    /// <summary>
    /// The settled targets for the sandbox's snapshot: own on both sides with the same picture, and not the
    /// editing target. The same instance while the set has not changed, so the snapshot shares its
    /// transition keys and no PVW fades for nothing.
    /// </summary>
    private IReadOnlyCollection<string>? SettledOwn()
    {
        var state = _services.State;
        var program = _program!;
        List<string>? found = null;
        foreach (var a in state.Independent)
        {
            var id = a.ScreenId;
            if (id == _services.EditingTargetId) continue;
            if (!ContentTargets.UsesOwnPattern(state, id) || !ContentTargets.UsesOwnPattern(program, id)) continue;
            var air = OwnPicture(program, id);
            if (air is null || !SamePicture(a.Pattern, air)) continue;
            (found ??= new List<string>()).Add(id);
        }
        if (found is null)
        {
            _settled = null;
            return null;
        }
        if (_settled is not null && _settled.Count == found.Count && found.TrueForAll(_settled.Contains)) return _settled;
        _settled = new HashSet<string>(found, StringComparer.Ordinal);
        return _settled;
    }

    /// <summary>
    /// The sandbox becomes the program on every screen. TAKE uses the configured crossfade;
    /// CUT switches instantly whatever the transition setting says.
    /// </summary>
    public void SendAll(bool cut = false, IReadOnlyCollection<string>? unarmedTargets = null)
    {
        if (!Active) return;
        using var take = _services.Bus.Take();
        // Runs on every send: un-armed targets get pinned, and pins on armed targets lift —
        // a fully armed TAKE after a scoped one is exactly when the lifting matters.
        var kept = MergeScope(unarmedTargets ?? Array.Empty<string>());
        // A cut is a property of the snapshot, not of the transition setting: toggling the
        // setting around the publish stopped working inside a bulk edit (the intermediate
        // publish is suppressed, so the only snapshot the outputs saw carried "fade").
        if (cut) _services.Bus.CutOnNextPublish();
        CarryLowerThirdOnSend();
        // The preview's look is now the program's (edited or not — the tally says which).
        _services.AirLookId = _services.PreviewLookId;
        Exit(reenterIfDefault: false);
        ReArmIfDefault();
        _services.AirLabel = Modified(_services.AirLabel);
        Log.Info($"Sandbox {(cut ? "cut" : "taken")} to program ({(kept == 0 ? "all screens" : $"{kept} target(s) kept their picture")}).");
    }

    /// <summary>
    /// A scoped send: un-armed targets keep the picture the audience is seeing — the program's
    /// content for that target is pinned as the target's own pattern in the sandbox before it
    /// becomes the program — and armed targets whose only "own pattern" is such a pin have the
    /// pin lifted so they follow the new program. Overlays, countdown and blackout are rig-wide
    /// and always go with the send. Returns how many targets kept their picture.
    /// </summary>
    private int MergeScope(IReadOnlyCollection<string> unarmed)
    {
        var program = _program;
        if (program is null) return 0;
        var state = _services.State;
        var kept = 0;
        if (unarmed.Count == 0 && state.Independent.All(a => !a.PinnedByTake)) return 0; // nothing to pin or lift
        _services.BulkEdit(() =>
        {
            foreach (var target in Rig.Targets(state, _services.Screens.All))
            {
                if (unarmed.Contains(target))
                {
                    var source = ContentTargets.UsesOwnPattern(program, target)
                        ? program.Independent.FirstOrDefault(a => a.ScreenId == target)?.Pattern ?? program.Pattern
                        : program.Pattern;
                    var assignment = ContentTargets.EnsureAssignment(state, target);
                    ModelCopier.Copy(JsonUtil.ClonePattern(source), assignment.Pattern);
                    // A pattern the operator chose for the target stays theirs; only a copy of the
                    // program's picture is marked as a pin the next armed send may lift. Read that
                    // from the PROGRAM, not from the edited state: a picture merely STAGED on this
                    // tile is not one the audience has, so a take that leaves the tile alone must
                    // put the program's picture back over it — the staging was not taken, and it
                    // must leave no residue that quietly un-follows the show.
                    if (!ContentTargets.UsesOwnPattern(program, target)) assignment.PinnedByTake = true;
                    ContentTargets.SetOwnPattern(state, target, true);
                    kept++;
                }
                else
                {
                    var assignment = state.Independent.FirstOrDefault(a => a.ScreenId == target);
                    if (assignment is { PinnedByTake: true })
                    {
                        state.Independent.Remove(assignment);
                        ContentTargets.SetOwnPattern(state, target, false);
                    }
                }
            }
        });
        return kept;
    }

    /// <summary>
    /// The sandbox pattern lands on the chosen content targets (screens, or joined canvases by
    /// key) as their own pattern — on the frozen program, so the audience sees it now, and in
    /// the edited state, so the next TAKE carries it and the wall's OWN lights. Nothing else
    /// moves: every other target keeps what it was showing, and the preview keeps the picture —
    /// the sandbox stays open with the same pattern, the same preview look and the same editing
    /// target, so the same look goes to the next screen, or on being edited. (It used to restore
    /// the program into the edited state and open a fresh sandbox, which mirrors the program: the
    /// picture just built was on one screen and nowhere the operator could edit it.)
    /// </summary>
    /// <summary>
    /// The preview's picture onto named targets. <paramref name="toAir"/> is the whole difference
    /// between the two gestures an operator needs:
    ///
    ///   toAir: false — STAGED. It lands in the edited state alone, so it is that target's own
    ///                  picture in the preview and nowhere else: its tile's PVW miniature shows it,
    ///                  the audience sees nothing, and a CUT or TAKE on that tile puts it up. This
    ///                  is what a tile's SEND does, and it is not a take — nothing moved on air, so
    ///                  nothing should transition.
    ///   toAir: true  — LIVE. It lands in the frozen program too, so it is on the screens now. SEND
    ///                  TO TICKED and a look sent to one screen both go this way.
    /// </summary>
    /// <param name="ownPicture">
    /// Round 67: take what the tile's own PVW shows — round 78: <see cref="PvwPicture"/>, the one rule: a
    /// picture staged or edited on the tile while the audience has not seen it, its own picture while the
    /// editors are on it, else the programme's preview — rather than the programme's preview whatever the
    /// tile holds. The tile's own CUT / TAKE go this way; SEND and SEND TO TICKED carry the programme's preview.
    /// </param>
    public void SendToTargets(IReadOnlyList<string> targetIds, bool toAir = true, bool cut = false, bool ownPicture = false)
    {
        if (!Active || _program is null || targetIds.Count == 0) return;
        using var take = toAir ? _services.Bus.Take() : default;
        // A tile's CUT (round 63): the target switches instead of fading — a property of the publish,
        // exactly as SendAll marks it, so the sink that draws this target sees a cut and no other does.
        if (toAir && cut) _services.Bus.CutOnNextPublish();
        var state = _services.State;
        var program = _program;
        var pictures = targetIds.ToDictionary(id => id, id => JsonUtil.ClonePattern(ownPicture ? PvwPicture(id) : state.Pattern), StringComparer.Ordinal);
        void Land(ShowState s)
        {
            foreach (var id in targetIds)
            {
                var assignment = ContentTargets.EnsureAssignment(s, id);
                ModelCopier.Copy(JsonUtil.ClonePattern(pictures[id]), assignment.Pattern);
                assignment.PinnedByTake = false; // the operator chose this picture — it stays
                ContentTargets.SetOwnPattern(s, id, true);
            }
        }
        // One publish for both sides: the outputs see the target change once, the preview stays.
        _services.BulkEdit(() =>
        {
            Land(state);
            if (toAir) Land(program);
        });
        if (toAir) _services.AirLabel = Modified(_services.AirLabel);
        Log.Info(toAir
            ? $"Sandbox {(cut ? "cut" : "sent")} live to {targetIds.Count} target(s); the preview keeps the picture."
            : $"Sandbox staged on {targetIds.Count} target(s); the air is untouched until a CUT or TAKE.");
    }

    /// <summary>
    /// The lower third across a TAKE. A design showing in the preview (a PVW for a sign-off) goes
    /// to air afresh — it arrives the way it was designed to. With nothing in the preview the one
    /// on air stays exactly where it is in its life: the edited state was never told about a
    /// design shown to air through the frozen program, and without this the TAKE would drop it.
    /// </summary>
    private void CarryLowerThirdOnSend()
    {
        var program = _program;
        if (program is null) return;
        var state = _services.State;
        var now = ShowClock.UtcNow;
        var preview = state.LowerThirds;
        if (preview.IsShowing && !preview.IsSameRunAs(program.LowerThirds) && preview.Active is { } next
            && Patterns.Core.LowerThirds.LowerThirdClock.IsLive(preview, now))
        {
            _services.BulkEdit(() => preview.Show(next, now));
            return;
        }
        _services.BulkEdit(() => CarryAirLowerThird(program, state));
    }

    /// <summary>
    /// The program's lower third into the state that is about to become the program, instants and
    /// all, so its timeline continues on the outputs instead of starting again. A design deleted from
    /// the show while its copy was on air is not brought back (the copy goes with the program).
    /// </summary>
    private static void CarryAirLowerThird(ShowState program, ShowState state)
    {
        var air = program.LowerThirds;
        var mine = state.LowerThirds;
        if (air.ActiveId.Length == 0 || mine.Find(air.ActiveId) is null)
        {
            if (mine.IsShowing) mine.Hide(ShowClock.UtcNow);
            return;
        }
        mine.ActiveId = air.ActiveId;
        mine.ShownAtUtc = air.ShownAtUtc;
        mine.HiddenAtUtc = air.HiddenAtUtc;
    }

    /// <summary>"MODIFIED — last Walk-in": a send changed the picture; the caller's strip stops naming a look it is not.</summary>
    private static string Modified(string previous)
        => previous.StartsWith("MODIFIED", StringComparison.Ordinal) ? previous : $"MODIFIED — last {previous}";

    /// <summary>Re-arms EDIT SAFE when the show asks for it (after a send).</summary>
    private void ReArmIfDefault()
    {
        if (!Active && _services.State.Switcher.EditSafeByDefault) Enter();
    }

    /// <summary>Back to exactly what is on air right now. Outputs never notice.</summary>
    public void Discard()
    {
        if (!Active) return;
        RestoreContent();
        Exit(reenterIfDefault: false); // an explicit toggle-off goes to the live mirror
        Log.Info("Sandbox discarded.");
    }

    /// <summary>
    /// A restart puts the audience's picture back: the recorded program replaces the frozen
    /// clone wholesale, so everything that decides that picture comes back verbatim — the brand
    /// kit, the lower third on air and where it is in its life, the NDI senders, and every
    /// locked screen's own picture. A look carries none of those, which is why a restart used to
    /// hand the audience a hybrid nobody had ever programmed. False when the desk is not split,
    /// and the caller puts the content back through the ordinary air seam instead.
    /// </summary>
    public bool RestoreProgram(ShowState program)
    {
        if (!Active) return false;
        using var take = _services.Bus.Take();
        _program = program;
        _contentBefore = LookService.Capture(program); // a discard now goes back to this, not to the start
        _services.WatchAir(_program);
        _services.RepublishNow();
        Log.Info("The program the audience was seeing is back on the outputs; the preview keeps the edit that was in progress.");
        return true;
    }

    /// <summary>
    /// Runs an air-targeted edit against the frozen program (a cue, a look recall, a stinger
    /// override, a playlist-part switch) and republishes — the audience sees it, the
    /// operator's sandboxed edits stay untouched. False when not sandboxed.
    /// </summary>
    public bool EditProgram(Action<ShowState> edit)
    {
        if (!Active || _program is null) return false;
        edit(_program);
        _services.RepublishNow(); // sandbox branch republishes both sides + side effects
        return true;
    }

    private void RestoreContent()
    {
        // Air may have moved on mid-sandbox (cues, stingers, remote looks) — restoring the
        // *current* program keeps what the audience is seeing, not what was on at Enter.
        var json = _program is not null ? LookService.Capture(_program) : _contentBefore;
        if (json is null) return;
        var state = _services.State;
        var program = _program;
        var blackout = state.Blackout; // keep whatever the operator set during the sandbox
        _services.BulkEdit(() =>
        {
            LookService.Apply(json, state);
            state.Blackout = blackout;
            // The look's recall would show the air's lower third afresh; the audience must not see it arrive twice.
            if (program is not null) CarryAirLowerThird(program, state);
        });
    }

    private void Exit(bool reenterIfDefault)
    {
        Active = false;
        _program = null;
        _contentBefore = null;
        // The show file is the air again: the watch comes off, and the record is rewritten
        // without a look of its own on the next publish.
        _services.WatchAir(null);
        _services.PreviewLookId = ""; // a fresh sandbox mirrors the program; only → PVW names a preview look
        _services.Bus.ClearSandbox();
        _services.RepublishNow(); // outputs pick up the live state again (side effects included)
        if (reenterIfDefault)
        {
            ReArmIfDefault(); // edit-safe stays armed: the next look builds in safety too
        }
    }
}
