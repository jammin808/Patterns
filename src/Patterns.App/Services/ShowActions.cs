using Patterns.Core.LowerThirds;
using Patterns.Rendering.LowerThirds;
using Patterns.Core.Media;
using Patterns.Rendering.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// The one way to do something to the show. The desk's buttons, the keyboard, the output
/// windows, the remote protocol, the Companion module, the daily schedule and (later) the
/// cue executor all call <see cref="Execute"/>; every call returns a typed result, is
/// written to the show journal with its origin, and raises <see cref="Performed"/> so the
/// view model can resync its editors. Nothing here needs the window to exist.
/// </summary>
public sealed partial class ShowActions : IActionLayer
{
    private readonly AppServices _s;

    public ShowActions(AppServices services) => _s = services;

    /// <summary>Raised on the UI thread after every action, whatever its outcome.</summary>
    public event Action<ShowAction, ActionOrigin, ActionResult>? Performed;

    private ShowState State => _s.State;

    public ActionResult Execute(ShowAction action, ActionOrigin origin)
    {
        ActionResult result;
        // Every verb the show has comes through here — the desk's keys, the wire, OSC, Companion,
        // a cue, the schedule, a device. That makes this the one place that can say "what happens
        // inside this is a take", which is what decides whether the pictures it changes transition
        // or simply arrive. Everything that is not a verb is somebody editing, and an edit is
        // instant.
        using var take = _s.Bus.Take();
        try
        {
            // The live desk's policy, before any area answers: work that is not the show does not
            // start while the stack is armed and the outputs are live, whoever asks — the desk, the
            // wire, a cue, the schedule — and the refusal says what lifts it.
            result = LivePolicy.Refusal(action.Kind, _s.CueStack?.Armed ?? false, _s.OutputsLive) is { } notNow
                ? ActionResult.Refused(notNow)
                : Run(action, origin);
        }
        catch (Exception ex)
        {
            Log.Error($"Action {action} failed.", ex);
            result = ActionResult.Failed(ex.Message);
        }

        if (action.Kind is not (ShowActionKind.Note or ShowActionKind.Identify or ShowActionKind.CueStandby or ShowActionKind.StageAck)
            && !SweptPast(action, origin))
        {
            _s.Journal.Record(origin.Label, action.Kind.ToString(), JournalTarget(action), result.Status.ToString(), result.Message);
        }
        Performed?.Invoke(action, origin, result);
        return result;
    }

    public ActionResult Execute(ShowActionKind kind, ActionOrigin origin, string target = "", string value = "")
        => Execute(new ShowAction(kind, target, value), origin);

    private readonly Dictionary<string, long> _swept = new(StringComparer.Ordinal);

    /// <summary>
    /// A level still moving under somebody's hand, so its row is not written yet.
    ///
    /// The journal writes to disk synchronously, on the thread that draws the desk. That has always
    /// been fine because every verb was a gesture: a GO, a look, a blackout. A fader is not a
    /// gesture, it is a sweep — fifty readings a second while a hand is on it — and fifty file
    /// appends a second on the UI thread would blow the desk's tick budget for as long as somebody
    /// held it.
    ///
    /// So a level from a control surface is recorded about once a second, and the row carries where
    /// the fader actually is rather than every place it passed through. What lands in the journal is
    /// the operator's intent, which is what a journal is read for — nobody has ever wanted to know
    /// that the level went through 47 on its way to 60. The level itself is instant; only the record
    /// is paced. A move that stops writes its last row on the next one, so the settled value is
    /// never the one that got away.
    /// </summary>
    private bool SweptPast(ShowAction action, ActionOrigin origin)
    {
        if (action.Kind is not (ShowActionKind.AudioVolume or ShowActionKind.SpotifyVolume)) return false;
        if (origin.Kind != OriginKind.Device) return false;                 // a typed level is a gesture, and is recorded

        var key = action.Kind + "\u0001" + origin.Label;
        var now = Environment.TickCount64;
        if (_swept.TryGetValue(key, out var next) && now < next) return true;
        _swept[key] = now + 1000;
        if (_swept.Count > 64) _swept.Clear();
        return false;
    }

    private string JournalTarget(ShowAction action) => action.Kind switch
    {
        ShowActionKind.ApplyLook or ShowActionKind.ApplyLookToPreview => ResolveLook(action.Target, out _)?.Name ?? action.Target,
        ShowActionKind.ListArm or ShowActionKind.ListDisarm or ShowActionKind.ListGo or ShowActionKind.ListBack or ShowActionKind.ListReset
            => CueStacks.Find(State, action.Target)?.Name ?? action.Target,
        ShowActionKind.SpotifyPlay when action.Target.Length > 0 => SpotifyLibrary.Find(State, action.Target)?.DisplayName ?? action.Target,
        _ => action.Target,
    };

    // ---- the verbs -----------------------------------------------------------

    private ActionResult Run(ShowAction a, ActionOrigin origin)
    {
        switch (a.Kind)
        {
            case ShowActionKind.Note:
                return ActionResult.Done();
            case ShowActionKind.Unknown:
                return ActionResult.Refused("This action comes from a newer build and cannot run here.");
        }
        // Each area answers its own verbs and null for the rest, in the order of the areas' files.
        return RunRig(a, origin)
            ?? RunSwitcher(a, origin)
            ?? RunLooks(a, origin)
            ?? RunCues(a, origin)
            ?? RunOverlays(a, origin)
            ?? RunLowerThirds(a, origin)
            ?? RunContent(a, origin)
            ?? RunAudio(a, origin)
            ?? RunInstall(a, origin)
            ?? RunTwin(a, origin)
            ?? RunLock(a, origin)
            ?? RunCalibration(a, origin)
            ?? RunStage(a, origin)
            ?? RunArcade(a, origin)
            ?? RunPlay(a, origin)
            ?? RunRigDay(a, origin)
            ?? RunEye(a, origin)
            ?? RunMidi(a)
            ?? ActionResult.Refused($"Unknown action '{a.Kind}'.");
    }

    /// <summary>The origins on the far side of a wire: TCP, HTTP, OSC, Companion, a device, the management server.</summary>
    private static bool IsRemote(ActionOrigin origin)
        => origin.Kind is OriginKind.Tcp or OriginKind.Http or OriginKind.Osc or OriginKind.Companion or OriginKind.Device or OriginKind.Management;
}
