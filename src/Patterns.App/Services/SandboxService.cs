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
    public void SendAll(bool cut = false)
    {
        if (!Active) return;
        var fade = _services.State.Transition.Enabled;
        if (cut) _services.State.Transition.Enabled = false;
        Exit(reenterIfDefault: false);
        // Restore the operator's transition setting *before* re-arming, or the next frozen
        // program would carry CUT's "no fade" for the rest of the show.
        if (cut) _services.State.Transition.Enabled = fade; // republish carries the same content — no late fade
        ReArmIfDefault();
        Log.Info($"Sandbox {(cut ? "cut" : "taken")} to program (all screens).");
    }

    /// <summary>
    /// The sandbox pattern lands on the chosen screens as their per-screen pattern; the
    /// program (and every other screen) goes back to what it was showing.
    /// </summary>
    public void SendToScreens(IReadOnlyList<string> screenIds)
    {
        if (!Active || screenIds.Count == 0) return;
        var state = _services.State;
        var pattern = JsonUtil.ClonePattern(state.Pattern);
        RestoreContent();
        _services.BulkEdit(() =>
        {
            foreach (var id in screenIds)
            {
                var assignment = state.Independent.FirstOrDefault(a => a.ScreenId == id);
                if (assignment is null)
                {
                    assignment = new OutputAssignment { ScreenId = id };
                    state.Independent.Add(assignment);
                }
                ModelCopier.Copy(pattern, assignment.Pattern);
                var placement = state.Output.Placements.FirstOrDefault(p => p.ScreenId == id);
                if (placement is not null) placement.UseCustomPattern = true;
            }
        });
        Exit(reenterIfDefault: true);
        Log.Info($"Sandbox sent to {screenIds.Count} screen(s).");
    }

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
