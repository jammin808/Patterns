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
        _services.RepublishNow();
        Log.Info("Sandbox open — outputs hold program, edits stay in the preview.");
    }

    /// <summary>Called from the publish path on every change while active.</summary>
    public void PublishBoth()
    {
        if (_program is null) return;
        _program.Blackout = _services.State.Blackout; // transport is never sandboxed
        _services.Bus.Publish(_program);
        _services.Bus.PublishSandbox(_services.State);
    }

    /// <summary>
    /// The sandbox becomes the program on every screen. TAKE uses the configured crossfade;
    /// CUT switches instantly whatever the transition setting says.
    /// </summary>
    public void SendAll(bool cut = false, IReadOnlyCollection<string>? unarmedTargets = null)
    {
        if (!Active) return;
        // Runs on every send: un-armed targets get pinned, and pins on armed targets lift —
        // a fully armed TAKE after a scoped one is exactly when the lifting matters.
        var kept = MergeScope(unarmedTargets ?? Array.Empty<string>());
        // A cut is a property of the snapshot, not of the transition setting: toggling the
        // setting around the publish stopped working inside a bulk edit (the intermediate
        // publish is suppressed, so the only snapshot the outputs saw carried "fade").
        if (cut) _services.Bus.CutOnNextPublish();
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
                    // A pattern the operator chose for the target stays theirs; only a program
                    // copy is marked as a pin the next armed send may lift.
                    if (!ContentTargets.UsesOwnPattern(state, target)) assignment.PinnedByTake = true;
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
    /// key) as their own pattern; the program (and every other target) goes back to what it
    /// was showing.
    /// </summary>
    public void SendToTargets(IReadOnlyList<string> targetIds)
    {
        if (!Active || targetIds.Count == 0) return;
        var state = _services.State;
        var pattern = JsonUtil.ClonePattern(state.Pattern);
        RestoreContent();
        _services.BulkEdit(() =>
        {
            foreach (var id in targetIds)
            {
                var assignment = ContentTargets.EnsureAssignment(state, id);
                ModelCopier.Copy(pattern, assignment.Pattern);
                assignment.PinnedByTake = false; // the operator chose this picture — it stays
                ContentTargets.SetOwnPattern(state, id, true);
            }
        });
        Exit(reenterIfDefault: true);
        _services.AirLabel = Modified(_services.AirLabel);
        Log.Info($"Sandbox sent to {targetIds.Count} target(s).");
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
        var blackout = state.Blackout; // keep whatever the operator set during the sandbox
        _services.BulkEdit(() =>
        {
            LookService.Apply(json, state);
            state.Blackout = blackout;
        });
    }

    private void Exit(bool reenterIfDefault)
    {
        Active = false;
        _program = null;
        _contentBefore = null;
        _services.Bus.ClearSandbox();
        _services.RepublishNow(); // outputs pick up the live state again (side effects included)
        if (reenterIfDefault)
        {
            ReArmIfDefault(); // edit-safe stays armed: the next look builds in safety too
        }
    }
}
