using Patterns.Core.LowerThirds;
using Patterns.Rendering.LowerThirds;
using Patterns.Core.Media;
using Patterns.Rendering.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// The content's verbs: a pattern kind or a preset on the program or on one screen, a screen's own look, the deck's pages, a video's jumps, the web page's keys, a playlist's part.
/// </summary>
public sealed partial class ShowActions
{
    private ActionResult? RunContent(ShowAction a, ActionOrigin origin)
    {
        switch (a.Kind)
        {
            case ShowActionKind.PatternKind:
            {
                var word = a.Value.Replace(" ", "").Replace("-", "").Replace("_", "").Trim();
                if (word.Length == 0 || !Enum.TryParse<PatternKind>(word, true, out var kind))
                {
                    return ActionResult.Refused($"'{a.Value}' is not a kind of picture — one of {string.Join(", ", Enum.GetNames<PatternKind>())}.");
                }
                _s.EditAir(air => air.Pattern.Kind = kind);
                return ActionResult.Done($"Pattern: {kind}.");
            }
            case ShowActionKind.WebKey:
            case ShowActionKind.WebClick:
            case ShowActionKind.WebType:
            case ShowActionKind.WebReload:
            case ShowActionKind.WebOpen:
            case ShowActionKind.WebArm:
            case ShowActionKind.WebMark:
                return WebActions.Execute(_s, a);
            case ShowActionKind.DeckNext:
                return DeckTurn("next");
            case ShowActionKind.DeckPrev:
                return DeckTurn("prev");
            case ShowActionKind.DeckPage:
                return DeckTurn(a.Value);
            case ShowActionKind.VideoToEnd:
            case ShowActionKind.VideoRestart:
                return VideoJump(a);
            case ShowActionKind.ScreenLook:
            {
                // The look's picture for this target alone, as its own pattern: every other target stays,
                // and the next TAKE leaves it (an operator's choice, never a pin). A whole-look recall
                // replaces it; a lock keeps it through looks and cues too.
                var target = ResolveScreenTarget(a.Target);
                if (target is null) return ActionResult.Refused($"No screen '{a.Target}'.");
                var look = LookService.Find(State, a.Value);
                if (look is null) return ActionResult.Refused($"No look named '{a.Value}'.");
                var picture = LookService.PictureFor(look.Json, target);
                if (picture is null) return ActionResult.Failed($"Look '{look.Name}' could not be read.");
                void Land(ShowState state)
                {
                    var assignment = ContentTargets.EnsureAssignment(state, target);
                    ModelCopier.Copy(JsonUtil.ClonePattern(picture), assignment.Pattern);
                    assignment.PinnedByTake = false;
                    ContentTargets.SetOwnPattern(state, target, true);
                }
                // Both the edited state and the frozen program, like a lock: the air changes now and the next TAKE carries it.
                _s.BulkEdit(() => Land(State));
                if (_s.Sandbox.Active) _s.EditAir(Land);
                var label = Rig.Geometry(State, _s.Screens.All).LabelFor(State, target);
                return ActionResult.Done($"Look '{look.Name}' on {label} alone — every other screen stays.");
            }
            case ShowActionKind.PatternPreset:
            {
                // A preset is a PATTERN, not a look: it carries no overlays, no countdown and no
                // per-screen arrangement, so recalling one changes what the picture IS and leaves
                // everything the show has dressed it with alone. It lands in the editors like any
                // other change, which is what makes EDIT SAFE hold it for the next TAKE — a recall
                // that jumped straight to air would be a different verb, and a dangerous one.
                var preset = _s.Store.FindPreset(a.Value);
                if (preset is null) return ActionResult.Refused(PresetMissing(a.Value));
                // The programme's picture, in the EDITED state — so with EDIT SAFE open a recall
                // lands in the preview and waits for the TAKE, which is what an operator building a
                // show expects of a recall. Onto one screen is ScreenPreset, and that one is live.
                _s.BulkEdit(() => ModelCopier.Copy(preset, State.Pattern));
                return ActionResult.Done($"Preset '{a.Value.Trim()}' recalled into {(_s.Sandbox.Active ? "the preview" : "the picture")}.");
            }
            case ShowActionKind.ScreenPreset:
            {
                // The twin of ScreenLook, and the reason a preset saved on the Pattern page is
                // usable from the Show panel without building a whole look around it.
                var target = ResolveScreenTarget(a.Target);
                if (target is null) return ActionResult.Refused($"No screen '{a.Target}'.");
                var preset = _s.Store.FindPreset(a.Value);
                if (preset is null) return ActionResult.Refused(PresetMissing(a.Value));
                void LandPreset(ShowState state)
                {
                    var assignment = ContentTargets.EnsureAssignment(state, target);
                    ModelCopier.Copy(JsonUtil.ClonePattern(preset), assignment.Pattern);
                    assignment.PinnedByTake = false;
                    ContentTargets.SetOwnPattern(state, target, true);
                }
                // Both the edited state and the frozen program, exactly as a per-screen look send:
                // the air changes now and the next TAKE carries it.
                _s.BulkEdit(() => LandPreset(State));
                if (_s.Sandbox.Active) _s.EditAir(LandPreset);
                var presetLabel = Rig.Geometry(State, _s.Screens.All).LabelFor(State, target);
                return ActionResult.Done($"Preset '{a.Value.Trim()}' on {presetLabel} alone — every other screen stays.");
            }
            case ShowActionKind.ScreenProgram:
            {
                var target = ResolveScreenTarget(a.Target);
                if (target is null) return ActionResult.Refused($"No screen '{a.Target}'.");
                var label = Rig.Geometry(State, _s.Screens.All).LabelFor(State, target);
                if (!ContentTargets.UsesOwnPattern(_s.AirState, target) && !ContentTargets.UsesOwnPattern(State, target))
                {
                    return ActionResult.Done($"{label} already shows the program.");
                }
                void Follow(ShowState state)
                {
                    ContentTargets.SetOwnPattern(state, target, false);
                    var own = state.Independent.FirstOrDefault(x => x.ScreenId == target);
                    if (own is not null) state.Independent.Remove(own);
                }
                _s.BulkEdit(() => Follow(State));
                if (_s.Sandbox.Active) _s.EditAir(Follow);
                return ActionResult.Done($"{label} shows the program again.");
            }
            case ShowActionKind.ScreenToPreview:
            {
                // → PVW on a tile: the picture the audience sees on the target — its own pattern, its
                // source's when it repeats one, else the program — into the sandboxed preview, to edit
                // and SEND back or TAKE. The air is untouched: EDIT SAFE opens first when it was off,
                // so the load can never go live by itself.
                // An empty target (or PGM) is the programme itself — what the room is watching,
                // back into the preview to change and take again. That is the one picture this
                // could not reach before, and it is the one an operator asks for most.
                var wantsProgram = a.Target.Length == 0 || string.Equals(a.Target, "pgm", StringComparison.OrdinalIgnoreCase);
                var target = wantsProgram ? "" : ResolveScreenTarget(a.Target);
                if (target is null) return ActionResult.Refused($"No screen '{a.Target}'.");
                var air = _s.AirState;
                PatternConfig showing;
                if (wantsProgram)
                {
                    showing = air.Pattern;
                }
                else
                {
                    var source = ScreenRoles.ResolveMirror(air, target);
                    showing = ContentTargets.UsesOwnPattern(air, source)
                        ? air.Independent.FirstOrDefault(x => x.ScreenId == source)?.Pattern ?? air.Pattern
                        : air.Pattern;
                }
                var picture = JsonUtil.ClonePattern(showing);
                var opened = !_s.Sandbox.Active;
                if (opened) _s.Sandbox.Enter();
                _s.BulkEdit(() => ModelCopier.Copy(picture, State.Pattern));
                _s.PreviewLookId = ""; // the preview holds an edit now, not a look
                var label = wantsProgram ? "The program" : Rig.Geometry(State, _s.Screens.All).LabelFor(State, target);
                return ActionResult.Done(opened
                    ? $"{label}'s picture is in the preview (EDIT SAFE opened, the air untouched) — edit it, then SEND it to a screen or TAKE."
                    : $"{label}'s picture is in the preview — edit it, then SEND it to a screen or TAKE.");
            }

            case ShowActionKind.ScreenPattern:
            {
                // PATTERN kind for one screen: the twin of ScreenLook — live, on the air and the edited state both.
                var target = ResolveScreenTarget(a.Target);
                if (target is null) return ActionResult.Refused($"No screen '{a.Target}'.");
                if (ActionSpec.ParsePatternKind(a.Value) is not { } kind)
                {
                    return ActionResult.Refused($"'{a.Value}' is not a kind of picture — one of {string.Join(", ", Enum.GetNames<PatternKind>())}.");
                }
                void LandKind(ShowState state)
                {
                    var assignment = ContentTargets.EnsureAssignment(state, target);
                    assignment.Pattern.Kind = kind;
                    assignment.PinnedByTake = false;
                    ContentTargets.SetOwnPattern(state, target, true);
                }
                _s.BulkEdit(() => LandKind(State));
                if (_s.Sandbox.Active) _s.EditAir(LandKind);
                var kindLabel = Rig.Geometry(State, _s.Screens.All).LabelFor(State, target);
                return ActionResult.Done($"{kind} on {kindLabel} alone — every other screen stays.");
            }
            case ShowActionKind.ScreenStageLook:
            case ShowActionKind.ScreenStagePreset:
            case ShowActionKind.ScreenStagePattern:
            case ShowActionKind.ScreenStageProgram:
            case ShowActionKind.ScreenStageReset:
            case ShowActionKind.ScreenStageLibrary:
                return Stage(a);
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
                _s.AirLookId = ""; // a part replaced the look's picture; the tally follows the picture from here
                return ActionResult.Done($"Playlist part '{options.Sections[index].Name}' is on air.");
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// The staged verbs (round 60): a picture on a target's PVW in the sandboxed preview and nowhere
    /// else. EDIT SAFE opens first when it was off — so a staged verb can never go live by itself,
    /// which is the promise the right-click menus are built on — and the frozen program is never
    /// written: the next CUT or TAKE is what puts the picture up, FOCUSED for that tile alone.
    ///
    /// Target: a screen or canvas (its own picture in the preview: the tile's PVW shows it and
    /// <see cref="SandboxService.IsStaged"/> reads it), or blank / PGM for the programme (the
    /// preview's own picture). What lands: a look's picture for that target, a preset, a kind of
    /// picture, the programme again, or the look on air's own picture back (RESET) — on the
    /// programme target RESET is the whole look back into the preview, and PROGRAM is what is on
    /// air into the preview to edit (the same as → PVW on the PGM tile).
    /// </summary>
    private ActionResult Stage(ShowAction a)
    {
        // Round 73: FOCUSED is the desk's editing target — the tile the operator selected on the wall,
        // the programme when none is (or on a node with no wall) — so a deck's LIBRARY key lands
        // where a click on the Library page would.
        var focused = a.Target.Equals("FOCUSED", StringComparison.OrdinalIgnoreCase);
        var named = focused ? _s.FocusedTarget?.Invoke() ?? "" : a.Target;
        var program = ContentTargets.IsProgramTarget(named);
        var target = program ? "" : ResolveScreenTarget(named);
        if (target is null) return ActionResult.Refused($"No screen '{named}'.");
        var label = program ? "the programme" : Rig.Geometry(State, _s.Screens.All).LabelFor(State, target);

        // Resolve everything before the sandbox opens: a refusal must leave the desk exactly as it was.
        PatternConfig? picture = null;
        string? wholeLookJson = null;
        string lookId = "";
        string what;
        switch (a.Kind)
        {
            case ShowActionKind.ScreenStageLook:
            {
                var look = LookService.Find(State, a.Value);
                if (look is null) return ActionResult.Refused($"No look named '{a.Value}'.");
                if (program)
                {
                    wholeLookJson = look.Json;
                    lookId = look.Id;
                }
                else
                {
                    picture = LookService.PictureFor(look.Json, target);
                    if (picture is null) return ActionResult.Failed($"Look '{look.Name}' could not be read.");
                }
                what = $"look '{look.Name}'";
                break;
            }
            case ShowActionKind.ScreenStagePreset:
            {
                var preset = _s.Store.FindPreset(a.Value);
                if (preset is null) return ActionResult.Refused(PresetMissing(a.Value));
                picture = JsonUtil.ClonePattern(preset);
                what = $"preset '{a.Value.Trim()}'";
                break;
            }
            case ShowActionKind.ScreenStagePattern:
            {
                if (ActionSpec.ParsePatternKind(a.Value) is not { } kind)
                {
                    return ActionResult.Refused($"'{a.Value}' is not a kind of picture — one of {string.Join(", ", Enum.GetNames<PatternKind>())}.");
                }
                // The kind alone: the target's picture as it is in the preview keeps its settings, so a
                // look's grid comes back a grid — the same rule PATTERN kind follows on air.
                var basis = program ? State.Pattern : LookService.Shown(State, target);
                picture = JsonUtil.ClonePattern(basis);
                picture.Kind = kind;
                what = kind.ToString();
                break;
            }
            case ShowActionKind.ScreenStageProgram:
            {
                if (program)
                {
                    // What the room is watching, into the preview to edit: → PVW on the PGM tile.
                    return Execute(new ShowAction(ShowActionKind.ScreenToPreview, ""), ActionOrigin.Desk);
                }
                what = "the programme";
                break;
            }
            case ShowActionKind.ScreenStageLibrary:
            {
                // Round 73: the tile by id (the desk's own press) or by name (the wire), from the same
                // catalogue the Library page draws — so the two are one press, journaled alike.
                var tile = LibraryItems.Find(LibraryItems.Build(State, _s.Store, swatches: false), a.Value);
                if (tile is null) return ActionResult.Refused($"No Library tile '{a.Value.Trim()}' — name one as the Library page lists it (a file by its display name, a page by its title, a preset or a brand kit by name).");
                if (tile.Picture is null)
                {
                    // A brand kit is the show's colours, not a picture: it applies to the show, as its tile does, and no picture is staged.
                    _s.BulkEdit(() => tile.Show?.Invoke(State));
                    return ActionResult.Done(tile.Words.Length > 0 ? tile.Words : $"Brand kit '{tile.Name}' applied.");
                }
                var basis = program ? State.Pattern : LookService.Shown(State, target);
                picture = JsonUtil.ClonePattern(basis);
                tile.Picture(picture);
                what = $"library tile '{tile.Name}'";
                break;
            }
            default: // ScreenStageReset
            {
                var onAir = _s.LookTally.OnAir();
                if (onAir is null) return ActionResult.Refused("No look is on air to reset to — the picture the room is watching was never a look. Load one, or SEND the preview.");
                if (program)
                {
                    wholeLookJson = onAir.Json;
                    lookId = onAir.Id;
                }
                else
                {
                    picture = LookService.PictureFor(onAir.Json, target);
                    if (picture is null) return ActionResult.Failed($"Look '{onAir.Name}' could not be read.");
                }
                what = $"look '{onAir.Name}' as it was";
                break;
            }
        }

        var opened = !_s.Sandbox.Active;
        if (opened) _s.Sandbox.Enter();
        var ok = true;
        _s.BulkEdit(() =>
        {
            if (wholeLookJson is not null)
            {
                ok = LookService.Apply(wholeLookJson, State);
                return;
            }
            if (program)
            {
                ModelCopier.Copy(picture!, State.Pattern);
                return;
            }
            if (a.Kind == ShowActionKind.ScreenStageProgram)
            {
                ContentTargets.SetOwnPattern(State, target, false);
                var own = State.Independent.FirstOrDefault(x => x.ScreenId == target);
                if (own is not null) State.Independent.Remove(own);
                return;
            }
            var assignment = ContentTargets.EnsureAssignment(State, target);
            ModelCopier.Copy(JsonUtil.ClonePattern(picture!), assignment.Pattern);
            assignment.PinnedByTake = false;
            ContentTargets.SetOwnPattern(State, target, true);
        });
        if (!ok) return ActionResult.Failed("The look could not be loaded into the preview.");
        if (program) _s.PreviewLookId = lookId; // a whole look names itself; a picture is an edit
        var opening = opened ? " EDIT SAFE opened; the air is untouched." : "";
        return ActionResult.Done(program
            ? $"{Capitalise(what)} in the preview — the audience sees nothing until CUT or TAKE.{opening}"
            : $"{Capitalise(what)} staged on {label}'s PVW — the audience sees nothing until CUT or TAKE (FOCUSED puts it up there alone).{opening}");
    }

    private static string Capitalise(string words) => words.Length == 0 ? words : char.ToUpperInvariant(words[0]) + words[1..];

    /// <summary>
    /// STATE's editing row (round 73): what the desk's editors are on — the target, whether it is its own
    /// picture, the kind, the page that edits it, and the Library tile last put there; null on a node,
    /// which has no editors.
    /// </summary>
    public object? EditingRow()
    {
        var f = _s.EditingFacts?.Invoke();
        return f is null
            ? null
            : new { target = f.TargetId, label = f.Label, program = f.IsProgram, own = f.Own, kind = f.Kind, editor = f.Editor, library = f.Library, librarySection = f.LibrarySection, words = f.Words };
    }

    /// <summary>The journal names looks and break music, not their ids — a caller reading it back should not need the show file.</summary>
    /// <summary>
    /// Why a preset could not be recalled, in words that say what to do. Presets are files beside
    /// the show rather than part of it, so a show carried to another machine can name one that is
    /// not there — and "no preset 'Walk-in'" alone would leave an operator hunting.
    /// </summary>
    private string PresetMissing(string name)
    {
        var wanted = (name ?? "").Trim();
        if (wanted.Length == 0) return "Which preset? Save one on the Pattern page first.";
        var have = _s.Store.PresetNames();
        return have.Count == 0
            ? $"No preset '{wanted}' — this machine's presets folder is empty. Save one on the Pattern page, or copy the folder across with the show."
            : $"No preset '{wanted}' here. This machine has: {string.Join(", ", have.Take(8))}{(have.Count > 8 ? "…" : "")}";
    }

    /// <summary>"Deck: page 3 / 12", with a word about what the next click does at the end.</summary>
    private string DeckWords(IDeckSource deck)
    {
        if (!deck.AtEnd) return $"Deck: page {deck.Page} / {deck.PageCount}.";
        var ends = MediaLocator.FindActiveMedia(_s.AirState, MediaSource.Deck)?.DeckEndsWithGo ?? true;
        return ends && _s.CueStack.Armed && _s.CueStack.StandbyCue is { } standby
            ? $"Deck: the last page ({deck.PageCount}) — the next click GOes {standby.Number} {standby.Name}."
            : $"Deck: the last page ({deck.PageCount}).";
    }

    /// <summary>DECK NEXT / PREV / PAGE on the deck on air, from a cue, the wire or a phone.</summary>
    private ActionResult DeckTurn(string value)
    {
        if (_s.DeckOnAir() is not { } deck)
        {
            return ActionResult.Refused("No deck is on air — put a PDF or a PowerPoint on the pattern of the look on air first.");
        }
        if (deck.PageCount == 0) return ActionResult.Refused($"The deck is not open: {deck.StatusText}");
        var target = Decks.Resolve(value, deck.Page, deck.PageCount);
        if (target == 0) return ActionResult.Refused($"A deck page is a number (1-based), first, last, next or prev — not '{value}'.");
        deck.GoTo(target);
        return ActionResult.Done(DeckWords(deck));
    }

    /// <summary>
    /// VIDEO END / VIDEO RESTART on the clip on air — from the panel, a cue, the wire or a phone.
    /// The rehearsal's skip: the clip jumps to its last seconds, its end still plays, the out is
    /// still heard, and whatever follows it — a playlist's next item, a stinger's ending — happens
    /// for real, because nothing here fakes the end; the decoder reaches it. A playlist item's own
    /// clock is wound forward with it, so a timed item moves on when the picture does.
    /// </summary>
    private ActionResult VideoJump(ShowAction a)
    {
        var reading = _s.VideoOnAir();
        if (reading is null)
        {
            return ActionResult.Refused("No video is on air — put a clip on the pattern of the look on air, or fire a video stinger, first.");
        }
        var source = Patterns.Rendering.Media.InputBus.For(reading.Key);
        if (source is null || !source.CanSeek)
        {
            return ActionResult.Failed($"'{reading.Name}' cannot be moved — a live source, or its decoder is not open yet.");
        }
        if (a.Kind == ShowActionKind.VideoRestart)
        {
            if (!source.Seek(0)) return ActionResult.Failed($"'{reading.Name}' would not restart.");
            if (reading.Role == VideoRole.Playlist) _s.Playlist.RestartCurrent();
            return ActionResult.Done($"'{reading.Name}' from the top.");
        }
        if (!VideoClock.TryParseBeforeEnd(a.Value, out var before))
        {
            return ActionResult.Refused($"VIDEO END wants a number of seconds before the end (blank = 10) — not '{a.Value}'.");
        }
        if (!reading.HasLength)
        {
            return ActionResult.Failed($"'{reading.Name}' has not said how long it is yet — try again in a moment.");
        }
        var target = Math.Max(0, reading.LengthSeconds - before);
        if (!source.Seek(target)) return ActionResult.Failed($"'{reading.Name}' would not move.");
        if (reading.Role == VideoRole.Playlist) _s.Playlist.EndCurrentIn(Math.Min(before, reading.LengthSeconds));
        return ActionResult.Done($"'{reading.Name}' to its last {before:0.#} s — {VideoClock.Format(target)} of {VideoClock.Format(reading.LengthSeconds)}.");
    }
}
