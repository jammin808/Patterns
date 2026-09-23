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
        // Round 79: one pair — no frame reads the new programme beside the old sandbox.
        _services.Bus.PublishBoth(_program, _services.AirWatch, _services.State, _services.StateWatch);
    }

    // ---- what a tile's PVW holds (round 78, round 81) ----------------------------------------------

    /// <summary>
    /// The picture a tile's PVW holds, and so what its own CUT / TAKE and a scoped wall take land — one rule,
    /// read by the snapshot the miniatures draw (<see cref="ShowSnapshot.PatternFor"/>), by the take
    /// (<see cref="PvwPicture"/>), by the menus, STATE and the Eye (<see cref="IsStaged"/>):
    ///
    ///   its own   — a target on its own picture (OWN) holds its own edited picture, whether the audience has
    ///               seen it or not and whether the editors are on it or not. The programme's preview never
    ///               reaches it: SEND on its tile copies the programme's preview onto its PVW; an edit with the
    ///               tile selected changes it in place; PROGRAM puts it back on the programme.
    ///   programme — a target that follows the programme holds the programme's preview; its first edit with
    ///               the tile selected makes it its own (round 67.5).
    ///   pending   — a light, not a picture: an own picture the audience has not seen (SEND staged it, an
    ///               edit changed it) — the tile's PVW badge, STATE's pending row, the Eye's "not taken yet".
    ///
    /// Round 78 had a settled OWN tile's PVW follow the programme's preview, so a take there put the
    /// programme's preview up; the maintainer's rule (round 81) is the mixer's — an OWN tile is its own until
    /// SEND or PROGRAM says otherwise. A take lands what the PVW shows, and a take that would change nothing
    /// is refused with the reason and the way out (<see cref="EffectOf"/>).
    /// </summary>
    public bool IsStaged(string targetId)
        => Active && _program is not null && Pending(_services.State, _program, ScreenRoles.ResolveMirror(_services.State, targetId));

    /// <summary>The picture this target's PVW shows — what a CUT / TAKE on its tile or a scoped wall take lands: its own picture while it is OWN, else the programme's preview (<see cref="LookService.Shown"/> on the edited state, the rule the miniatures draw too).</summary>
    public PatternConfig PvwPicture(string targetId)
    {
        var state = _services.State;
        return LookService.Shown(state, ScreenRoles.ResolveMirror(state, targetId));
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
        if (!Same(PvwPicture(resolved), LookService.Shown(_program, resolved))) return TakeEffect.Picture;
        return ContentTargets.UsesOwnPattern(_program, resolved) ? TakeEffect.Nothing : TakeEffect.OwnOnly;
    }

    /// <summary>Of the targets a wall TAKE changes, how many the audience already sees with the picture it would land — the picture the take lands on them (<see cref="Landing"/>) is the air's.</summary>
    public int AlreadyOnAir(IReadOnlyList<string> taken)
    {
        if (!Active || _program is null) return 0;
        var n = 0;
        foreach (var t in taken)
        {
            if (Same(Landing(_services.State, t), LookService.Shown(_program, t))) n++;
        }
        return n;
    }

    /// <summary>
    /// Round 79: whether a wall TAKE with this plan would change what the audience sees. Every screen of the rig is
    /// read through the edited state's mirror map (a take carries the rig too: a screen made a repeater shows its
    /// source's picture the moment the take lands), and the picture the take puts there — a taken target's landing
    /// picture, a kept target's pin of the air's (<see cref="MergeScope"/>) — is compared with the air's; then the
    /// rig-wide layers a take carries (the overlays, the countdown). The look tally and the pins themselves are not
    /// what the audience sees, and do not count: the field pressed TAKE eleven times over an air that already was
    /// the preview and read "fades up" each time.
    /// </summary>
    public bool WouldChange(IReadOnlyList<string> taken, IReadOnlyList<string> kept)
    {
        if (!Active || _program is null) return false;
        var state = _services.State;
        var program = _program;
        var keptSet = new HashSet<string>(kept, StringComparer.Ordinal);
        var takenSet = new HashSet<string>(taken, StringComparer.Ordinal);
        foreach (var t in Rig.Targets(state, _services.Screens.All).Concat(state.Output.Placements.Select(p => p.ScreenId)).Distinct(StringComparer.Ordinal))
        {
            var resolved = ScreenRoles.ResolveMirror(state, t);
            var after = keptSet.Contains(resolved) && !takenSet.Contains(resolved) ? PinSource(program, resolved) : Landing(state, resolved);
            if (!Same(after, LookService.Shown(program, t))) return true;
        }
        return JsonUtil.SerializeCompact(state.Overlays) != JsonUtil.SerializeCompact(program.Overlays)
            || JsonUtil.SerializeCompact(state.Countdown) != JsonUtil.SerializeCompact(program.Countdown);
    }

    /// <summary>The picture a scoped take pins on a kept target: the air's own picture for it, else the air's programme — <see cref="MergeScope"/>'s rule.</summary>
    private static PatternConfig PinSource(ShowState program, string target)
        => ContentTargets.UsesOwnPattern(program, target)
            ? program.Independent.FirstOrDefault(a => a.ScreenId == target)?.Pattern ?? program.Pattern
            : program.Pattern;

    /// <summary>
    /// The picture a wall take lands on a taken target: the edited state's, except that a pin the take lifts — an armed
    /// target whose own picture is only the copy a scoped take kept for it (<see cref="MergeScope"/>) — follows the
    /// programme's preview, which is what the send makes it show.
    /// </summary>
    private static PatternConfig Landing(ShowState state, string targetId)
    {
        var resolved = ScreenRoles.ResolveMirror(state, targetId);
        if (ContentTargets.UsesOwnPattern(state, resolved))
        {
            foreach (var a in state.Independent)
            {
                if (a.ScreenId == resolved && a.PinnedByTake) return state.Pattern;
            }
        }
        return LookService.Shown(state, resolved);
    }

    private bool Pending(ShowState state, ShowState program, string targetId)
    {
        if (!ContentTargets.UsesOwnPattern(state, targetId)) return false;
        if (!ContentTargets.UsesOwnPattern(program, targetId)) return true;
        var mine = OwnPicture(state, targetId);
        var air = OwnPicture(program, targetId);
        return mine is null || air is null || !Same(mine, air);
    }

    private static PatternConfig? OwnPicture(ShowState s, string targetId)
    {
        foreach (var a in s.Independent)
        {
            if (a.ScreenId == targetId) return a.Pattern;
        }
        return null;
    }

    /// <summary>The same picture in every property the show file keeps — the identity a take compares, as the recovery record would see it. The service's own asks go through <see cref="Same"/>, which memoises it.</summary>
    public static bool SamePicture(PatternConfig a, PatternConfig b)
        => ReferenceEquals(a, b) || JsonUtil.SerializeCompact(a) == JsonUtil.SerializeCompact(b);

    // ---- picture identity, memoised on the change counters (round 79) ------------------------------

    private readonly Dictionary<PatternConfig, string> _pictureKeys = new(ReferenceEqualityComparer.Instance);
    private long _keysState = -1;
    private long _keysAir = -1;
    private ShowState? _keysProgram;

    /// <summary>
    /// <see cref="SamePicture"/> for the service's own asks. Every wall refresh asks each tile (<see cref="IsStaged"/>,
    /// <see cref="PvwPicture"/>), the menus and the take ask again (<see cref="EffectOf"/>, <see cref="WouldChange"/>):
    /// each picture is serialised once per change of either root, not once per ask. The two trackers' change counters
    /// (<see cref="ChangeTracker.Version"/>) say when an identity can have moved — a frozen programme replaced counts
    /// too — and the memo starts afresh then, so a picture edited a moment ago reads as changed before any publish.
    /// </summary>
    private bool Same(PatternConfig a, PatternConfig b)
    {
        if (ReferenceEquals(a, b)) return true;
        var stateVersion = _services.StateWatch.Version;
        var airVersion = _services.AirWatch?.Version ?? -1;
        if (stateVersion != _keysState || airVersion != _keysAir || !ReferenceEquals(_program, _keysProgram))
        {
            _pictureKeys.Clear();
            _keysState = stateVersion;
            _keysAir = airVersion;
            _keysProgram = _program;
        }
        return Key(a) == Key(b);
    }

    private string Key(PatternConfig picture)
    {
        if (!_pictureKeys.TryGetValue(picture, out var key)) _pictureKeys[picture] = key = JsonUtil.SerializeCompact(picture);
        return key;
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
    /// Round 67: take what the tile's own PVW shows — <see cref="PvwPicture"/>, the one rule: its own picture
    /// while the target is OWN, else the programme's preview — rather than the programme's preview whatever the
    /// tile holds. The tile's own CUT / TAKE and a scoped wall take (round 81) go this way; SEND and SEND TO
    /// TICKED carry the programme's preview.
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
