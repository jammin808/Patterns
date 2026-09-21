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
        // Round 79: the take family is measured, not believed — the air's pictures read here before the take and
        // again after it (TakeMeasure), at the one place every take passes whatever asked for it.
        var measure = IsTake(action.Kind) ? TakeMeasure.Before(_s) : null;
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

        // Round 79: attempts are not facts. Two of them the words cannot be trusted for go on the result as fields,
        // stamped here once for every origin: whether anybody could see this land (the outputs and the blackout after
        // it ran), and for a take what it did to the air. The journal row carries both, STATE and the Eye the last take.
        result = result with { Visibility = VisibilityNow(), Effect = measure?.After(_s) ?? ActionEffect.NotMeasured };
        if (measure is not null) LastTake = new TakeRecord(action.Kind, result, ShowClock.UtcNow);
        if (action.Kind is not (ShowActionKind.Note or ShowActionKind.Identify or ShowActionKind.CueStandby or ShowActionKind.StageAck)
            && !SweptPast(action, origin))
        {
            _s.Journal.Record(origin.Label, action.Kind.ToString(), JournalTarget(action), result.Status.ToString(), result.Message, Stamp(result.Visibility), Stamp(result.Effect));
        }
        Performed?.Invoke(action, origin, result);
        return result;
    }

    /// <summary>Round 79: the take family — the verbs whose effect on the air is measured, and whose "Done" must have changed something when a hand pressed them.</summary>
    public static bool IsTake(ShowActionKind kind)
        => kind is ShowActionKind.Take or ShowActionKind.Cut or ShowActionKind.ScreenTake or ShowActionKind.ScreenCut;

    /// <summary>Round 79: the last take this desk ran, with the facts stamped on it — what STATE's take row and the Eye's desk node carry as the last take. Null before the first.</summary>
    public TakeRecord? LastTake { get; private set; }

    /// <summary>Whether the room could see an action land, read after it ran: the outputs closed, the blackout up, or live.</summary>
    private ActionVisibility VisibilityNow()
    {
        if (!_s.OutputsLive) return ActionVisibility.OutputsOff;
        return _s.AirState.Blackout ? ActionVisibility.Blackout : ActionVisibility.OutputsLive;
    }

    private static string? Stamp(ActionVisibility visibility) => visibility == ActionVisibility.Unknown ? null : visibility.ToString();

    private static string? Stamp(ActionEffect effect) => effect == ActionEffect.NotMeasured ? null : effect.ToString();

    /// <summary>
    /// Round 79: the air's pictures at the press — <see cref="LookService.Seen"/> for what the screens show and
    /// <see cref="LookService.Fingerprint"/> for who owns it — read again after the take. Measured on the air's model,
    /// the state the outputs draw from; the frame follows within each sink's lag (round 52). Two small strings per
    /// take, a gesture, never on a publish.
    /// </summary>
    private sealed class TakeMeasure
    {
        private readonly IReadOnlyList<string> _targets;
        private readonly string _seen;
        private readonly string _look;

        private TakeMeasure(IReadOnlyList<string> targets, string seen, string look)
        {
            _targets = targets;
            _seen = seen;
            _look = look;
        }

        public static TakeMeasure Before(AppServices s)
        {
            var targets = Rig.Geometry(s.State, s.Screens.All).Targets;
            var air = s.AirState;
            return new TakeMeasure(targets, LookService.Seen(air, targets), LookService.Fingerprint(air));
        }

        public ActionEffect After(AppServices s)
        {
            var air = s.AirState;
            if (LookService.Seen(air, _targets) != _seen) return ActionEffect.Changed;
            return LookService.Fingerprint(air) != _look ? ActionEffect.OwnOnly : ActionEffect.Nothing;
        }
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
        _ when ActionSpec.CarriesSecret(action.Kind) => "",   // round 75: the admin passcode rides the target — the row keeps the kind and the outcome, never the secret
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
            ?? RunNav(a)
            ?? ActionResult.Refused($"Unknown action '{a.Kind}'.");
    }

    /// <summary>The origins on the far side of a wire: TCP, HTTP, OSC, Companion, a device, the management server.</summary>
    private static bool IsRemote(ActionOrigin origin)
        => origin.Kind is OriginKind.Tcp or OriginKind.Http or OriginKind.Osc or OriginKind.Companion or OriginKind.Device or OriginKind.Management;
}

/// <summary>Round 79: one take as a fact row — the kind, its result with the stamps, and when. STATE's take row and the Eye's desk node carry the last one.</summary>
public sealed record TakeRecord(ShowActionKind Kind, ActionResult Result, DateTime AtUtc)
{
    public string Verb => Kind is ShowActionKind.Cut or ShowActionKind.ScreenCut ? "CUT" : "TAKE";

    /// <summary>"TAKE 20:31:05 — the pictures changed · outputs off — unseen"; "CUT 20:31:09 — refused: Nothing to take …".</summary>
    public string Words
    {
        get
        {
            var at = AtUtc.ToLocalTime().ToString("HH:mm:ss", System.Globalization.CultureInfo.InvariantCulture);
            if (!Result.Ok) return $"{Verb} {at} — {Result.Status.ToString().ToLowerInvariant()}: {Result.Message}";
            var seen = Result.Visibility switch
            {
                ActionVisibility.OutputsLive => "outputs live",
                ActionVisibility.OutputsOff => "outputs off — unseen",
                ActionVisibility.Blackout => "blackout — unseen",
                _ => "",
            };
            if (Result.Status == ActionStatus.Requested) return Join($"{Verb} {at} — requested: lands when the clip ends", seen);
            var effect = Result.Effect switch
            {
                ActionEffect.Changed => "the pictures changed",
                ActionEffect.OwnOnly => "the pictures stayed; a screen is its own now",
                ActionEffect.Nothing => "nothing changed",
                _ => "",
            };
            return Join($"{Verb} {at} — {effect}", seen);
        }
    }

    private static string Join(string head, string tail) => tail.Length == 0 ? head : head + " · " + tail;
}
