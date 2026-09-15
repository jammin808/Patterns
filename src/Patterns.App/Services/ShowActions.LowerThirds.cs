using Patterns.Core.LowerThirds;
using Patterns.Rendering.LowerThirds;
using Patterns.Core.Media;
using Patterns.Rendering.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// The lower thirds' verbs: shown, previewed, taken, updated, hidden; which design a person goes into.
/// </summary>
public sealed partial class ShowActions
{
    private ActionResult? RunLowerThirds(ShowAction a, ActionOrigin origin)
    {
        switch (a.Kind)
        {
            case ShowActionKind.LowerThirdShow:
            case ShowActionKind.LowerThirdPreview:
            {
                var toPreview = a.Kind == ShowActionKind.LowerThirdPreview;
                if (toPreview && !_s.Sandbox.Active)
                {
                    return ActionResult.Refused("Switch EDIT SAFE on to preview a lower third — without it the preview is the air (AIR puts it on straight away).");
                }
                // An empty target is the design in the preview (for a preview) or on air, else the show's default (★): a person recalled into whatever is showing.
                var design = a.Target.Length == 0
                    ? DefaultLowerThird(toPreview)
                    : State.LowerThirds.Find(a.Target) ?? _s.AirState.LowerThirds.Find(a.Target);
                if (design is null)
                {
                    return ActionResult.Refused(a.Target.Length == 0 ? "No lower third design in the show." : $"Lower third '{a.Target}' not found.");
                }
                LowerThirdEntry? who = null;
                if (a.Value.Length > 0)
                {
                    // A wrong name must never reach the screen: an entry that is not there refuses the show.
                    who = State.LowerThirds.FindEntry(a.Value) ?? _s.AirState.LowerThirds.FindEntry(a.Value);
                    if (who is null) return ActionResult.Refused($"'{a.Value}' is not in the lower-thirds library.");
                }
                var now = ShowClock.UtcNow;
                if (toPreview)
                {
                    // The edited design is the preview's copy: the PREVIEW pane, the multiview's Preview tile and REVIEW draw it.
                    _s.BulkEdit(() =>
                    {
                        if (who is not null) LowerThirdsConfig.Fill(design, who);
                        State.LowerThirds.Show(design, now);
                    });
                    return ActionResult.Done(who is null
                        ? $"Lower third '{design.Name}' in the preview — TAKE puts it on air."
                        : $"Lower third '{design.Name}' in the preview — {who.Name}. TAKE puts it on air.");
                }
                // The designer follows the person who went on air — unless this design is holding the next name in the
                // preview, which stays as the operator left it (the status line names who is on).
                var designInPreview = _s.LowerThirdInPreview() && State.LowerThirds.ActiveId == design.Id;
                if (who is not null && _s.Sandbox.Active && !designInPreview) _s.BulkEdit(() => LowerThirdsConfig.Fill(design, who));
                _s.EditAir(air =>
                {
                    // To air as the design is now: with EDIT SAFE open the program holds its own copy, refreshed on
                    // every show so an edit made since the last one goes with it (a copy left as it was is exactly
                    // "shown on the desk, not on the output"). Without the sandbox the air's design is this one.
                    var onAir = PutOnAir(air, design);
                    if (who is not null) LowerThirdsConfig.Fill(onAir, who);
                    air.LowerThirds.Show(onAir, now);
                });
                return ActionResult.Done(who is null ? $"Lower third '{design.Name}' on." : $"Lower third '{design.Name}' on — {who.Name}.");
            }
            case ShowActionKind.LowerThirdHide:
                _s.EditAir(air => air.LowerThirds.Hide(ShowClock.UtcNow));
                return ActionResult.Done("Lower third off.");
            case ShowActionKind.LowerThirdPreviewOff:
            {
                if (!_s.Sandbox.Active) return ActionResult.Done("The preview is the air while EDIT SAFE is off — nothing to clear.");
                var preview = State.LowerThirds;
                if (!_s.LowerThirdInPreview()) return ActionResult.Done("The preview is clear.");
                var name = preview.Active?.Name ?? "";
                _s.BulkEdit(() => preview.Hide(ShowClock.UtcNow));
                return ActionResult.Done($"Lower third '{name}' leaving the preview.");
            }
            case ShowActionKind.LowerThirdTake:
            {
                if (!_s.Sandbox.Active) return ActionResult.Refused("Nothing is in the preview — EDIT SAFE is off, so AIR puts a lower third on straight away.");
                var preview = State.LowerThirds;
                var now = ShowClock.UtcNow;
                if (!_s.LowerThirdInPreview() || preview.Active is not { } next || !LowerThirdClock.IsLive(preview, now))
                {
                    return ActionResult.Refused("No lower third in the preview to take — PVW one first.");
                }
                _s.EditAir(air =>
                {
                    var onAir = PutOnAir(air, next);
                    air.LowerThirds.Show(onAir, now);   // afresh: it arrives on air the way it was designed to
                });
                _s.BulkEdit(() => preview.Hide(now));   // the preview clears, ready for the next name
                return ActionResult.Done(next.PersonName.Length > 0
                    ? $"Lower third '{next.Name}' taken to air — {next.PersonName}."
                    : $"Lower third '{next.Name}' taken to air.");
            }
            case ShowActionKind.LowerThirdUpdate:
            {
                if (!_s.Sandbox.Active) return ActionResult.Done("EDIT SAFE is off — the design on air is the design you edit; it is already live.");
                var air = _s.AirState.LowerThirds;
                if (!air.IsShowing || air.Active is not { } onAir) return ActionResult.Refused("No lower third on air to update.");
                var edited = State.LowerThirds.Find(onAir.Id);
                if (edited is null) return ActionResult.Refused($"Lower third '{onAir.Name}' is no longer in the show — HIDE it, or AIR another.");
                if (LowerThirdsConfig.SameDesign(edited, onAir)) return ActionResult.Done($"Lower third '{onAir.Name}' on air is already as edited.");
                // In place: the copy changes under the same id and the instants stay, so it neither leaves nor arrives again.
                _s.EditAir(a2 => a2.LowerThirds.Put(edited.Clone(newId: false)));
                return ActionResult.Done($"Lower third '{edited.Name}' updated on air.");
            }
            default:
                return null;
        }
    }

    /// <summary>
    /// The lower third a person goes into when none is named: for a preview the one already in the
    /// preview; then the one on air; then the show's default (★, else the first); then whatever was
    /// last shown. The edited design is preferred (it is the one the desk edits and the program copies).
    /// </summary>
    private LowerThirdDesign? DefaultLowerThird(bool forPreview = false)
    {
        var air = _s.AirState.LowerThirds;
        var edited = State.LowerThirds;
        if (forPreview && _s.LowerThirdInPreview() && edited.Active is { } inPreview) return inPreview;
        if (air.IsShowing && air.ActiveId.Length > 0 && (edited.Find(air.ActiveId) ?? air.Find(air.ActiveId)) is { } onAir) return onAir;
        if (edited.DefaultDesign is { } chosen) return chosen;
        var id = air.ActiveId.Length > 0 ? air.ActiveId : edited.ActiveId;
        return (id.Length > 0 ? edited.Find(id) ?? air.Find(id) : null) ?? air.DefaultDesign;
    }

    /// <summary>
    /// The design the air draws: the design itself when the air is the live state, else the frozen
    /// program's own copy, refreshed from the edited design now (same id: its row and its instants stay).
    /// </summary>
    private LowerThirdDesign PutOnAir(ShowState air, LowerThirdDesign design)
        => ReferenceEquals(air, State) ? design : air.LowerThirds.Put(design.Clone(newId: false));

    /// <summary>
    /// A look about to land on the frozen program names a lower third by id: the program gets the
    /// edited design's current copy first, so the recall shows the design as it is now, and shows it
    /// at all when it was made while EDIT SAFE was open (the frozen clone never saw it).
    /// </summary>
    private void SyncLookLowerThird(ShowState air, string lookJson)
    {
        if (ReferenceEquals(air, State)) return;
        var id = LookService.LowerThirdIdOf(lookJson);
        if (string.IsNullOrEmpty(id)) return;
        var edited = State.LowerThirds.Find(id);
        if (edited is not null) air.LowerThirds.Put(edited.Clone(newId: false));
    }
}
