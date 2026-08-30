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

    /// <summary>The sandbox becomes the program — every screen takes it (with the crossfade).</summary>
    public void SendAll()
    {
        if (!Active) return;
        Exit();
        Log.Info("Sandbox sent to program (all screens).");
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
        Exit();
        Log.Info($"Sandbox sent to {screenIds.Count} screen(s).");
    }

    /// <summary>Back to exactly what was on before the sandbox opened. Outputs never notice.</summary>
    public void Discard()
    {
        if (!Active) return;
        RestoreContent();
        Exit();
        Log.Info("Sandbox discarded.");
    }

    private void RestoreContent()
    {
        if (_contentBefore is null) return;
        var state = _services.State;
        var blackout = state.Blackout; // keep whatever the operator set during the sandbox
        _services.BulkEdit(() =>
        {
            LookService.Apply(_contentBefore, state);
            state.Blackout = blackout;
        });
    }

    private void Exit()
    {
        Active = false;
        _program = null;
        _contentBefore = null;
        _services.Bus.ClearSandbox();
        _services.RepublishNow(); // outputs pick up the live state again (side effects included)
    }
}
