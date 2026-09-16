using System.Globalization;
using Patterns.Core.LowerThirds;
using Patterns.Rendering.LowerThirds;
using Patterns.Core.Media;
using Patterns.Rendering.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// The looks' verbs: a recall to the air or to the preview, a hotkey, LOOK BACK; what a look brings with it (its stream, its music).
/// </summary>
public sealed partial class ShowActions
{
    private ActionResult? RunLooks(ShowAction a, ActionOrigin origin)
    {
        switch (a.Kind)
        {
            case ShowActionKind.ApplyLook:
            {
                var look = ResolveLook(a.Target, out var problem);
                return look is null ? ActionResult.Refused(problem) : ApplyLookToAir(look, a.Value, origin);
            }
            case ShowActionKind.ApplyLookHotkey:
            {
                if (!int.TryParse(a.Target, out var slot)) return ActionResult.Refused($"'{a.Target}' is not an F-key slot.");
                var look = State.LooksAndCues.Looks.FirstOrDefault(l => l.Hotkey == slot);
                if (look is null) return ActionResult.Refused($"No look on F{slot}.");
                // A stray key cannot fire behind the caller: plain F-keys wait while the stack is
                // armed (the show can opt out). The look buttons and a remote's LOOK stay live.
                if (origin.Kind == OriginKind.Keyboard && _s.CueStack.SuspendsAutomation)
                {
                    return ActionResult.Refused($"F{slot} held — the cue stack is armed (looks from the desk or a remote still work).");
                }
                return ApplyLookToAir(look, a.Value, origin);
            }
            case ShowActionKind.ApplyLookToPreview:
            {
                var look = ResolveLook(a.Target, out var problem);
                if (look is null) return ActionResult.Refused(problem);
                var ok = false;
                _s.BulkEdit(() => ok = LookService.Apply(look.Json, State));
                if (!ok) return ActionResult.Failed($"Look '{look.Name}' could not be loaded.");
                if (_s.Sandbox.Active)
                {
                    _s.PreviewLookId = look.Id;
                    return ActionResult.Done($"Look '{look.Name}' loaded into the preview — CUT or TAKE to put it on air.");
                }
                // No sandbox: the live model is the program, so the look went on air.
                _s.AirLookId = look.Id;
                _s.AirLabel = look.Name;
                return ActionResult.Done($"Look '{look.Name}' applied.");
            }

            case ShowActionKind.LookBack:
            {
                var id = _s.PreviousAirLookId;
                var back = id.Length > 0 ? LookService.Find(State, id) : null;
                if (back is null) return ActionResult.Refused("No previous look to go back to.");
                // The recall makes today's look the previous one, so LOOK BACK twice is a swap.
                return ApplyLookToAir(back, a.Value, origin);
            }
            default:
                return null;
        }
    }

    /// <param name="note">Where the recall came from, for the status line ("cue 18:00"); ignored when cutting.</param>
    public ActionResult ApplyLook(LookConfig look, ActionOrigin origin, bool cut = false, string note = "")
        => Execute(new ShowAction(ShowActionKind.ApplyLook, look.Id, cut ? "cut" : note), origin);

    /// <summary>F1–F12. False = no look on that key (the key is left for other handlers).</summary>
    public bool ApplyLookHotkey(int slot, ActionOrigin origin)
    {
        if (State.LooksAndCues.Looks.All(l => l.Hotkey != slot)) return false;
        return Execute(new ShowAction(ShowActionKind.ApplyLookHotkey, slot.ToString(CultureInfo.InvariantCulture)), origin).Ok;
    }

    /// <summary>
    /// Fires a look to air. EDIT SAFE protects what you are <em>building</em>, not what you
    /// <em>fire</em>: with the sandbox open the audience gets the look and the preview keeps
    /// showing the operator's in-progress edit. "cut" switches without the crossfade.
    /// </summary>
    private ActionResult ApplyLookToAir(LookConfig look, string value, ActionOrigin origin)
    {
        var sandboxed = _s.Sandbox.Active;
        // Value: "cut", a fade in ms, a transition to arrive by ("wipe 800", "stinger",
        // "reactive vortex") — this recall only — or anything else for the show default.
        if (ActionSpec.TryParseTransition(value, out var cut, out var fadeMs, out var kind, out var scene, out var way))
        {
            if (cut) _s.Bus.CutOnNextPublish();
            else
            {
                if (fadeMs >= 0) _s.Bus.FadeOnNextPublish(fadeMs);
                if (kind is not null || scene is not null || way is not null) _s.Bus.TransitionOnNextPublish(kind, scene, way);
            }
        }
        var ok = false;
        _s.EditAir(air =>
        {
            SyncLookLowerThird(air, look.Json);
            ok = LookService.Apply(look.Json, air, rearmCountdown: true);
        });
        if (!ok) return ActionResult.Failed($"Look '{look.Name}' could not be applied.");
        _s.AirLabel = look.Name;
        _s.AirLookId = look.Id;
        // "cue 18:00" from the schedule: the status line says which cue fired, as it used to.
        var prefix = value.StartsWith("cue ", StringComparison.OrdinalIgnoreCase) ? $"Cue {value[4..]}: " : "";
        var text = prefix + (sandboxed
            ? $"Look '{look.Name}' on air — your preview edit is untouched."
            : $"Look '{look.Name}' applied.");
        var stream = RunLookStream(look, origin);
        if (stream is { } s2) text = $"{text} {s2.Message}";
        if (RunLookMusic(look, origin) is not { } music) return ActionResult.Done(text);
        // The music is asynchronous like every break-music verb: a Requested look settles on it.
        var line = $"{text} {music.Message}";
        return music.Status == ActionStatus.Requested ? ActionResult.Requested(line) : ActionResult.Done(line);
    }

    /// <summary>
    /// A look can start or stop the stream: the same verb a cue, the phone or a Stream Deck key
    /// would run, after the picture has landed and journaled on its own with the look's origin.
    /// This is how an F-key, the clicker list and an install's schedule reach the stream — they
    /// all recall a look and none of them carries an action list. Never able to stop the look:
    /// a stream that will not start is a line on the status strip, not a lost picture.
    /// </summary>
    private ActionResult? RunLookStream(LookConfig look, ActionOrigin origin)
    {
        if (look.Stream == LookConfig.LookStream.Leave) return null;
        var action = new ShowAction(look.Stream == LookConfig.LookStream.Start
            ? ShowActionKind.StreamStart
            : ShowActionKind.StreamStop);
        ActionResult result;
        try
        {
            result = Run(action, origin);
        }
        catch (Exception ex)
        {
            Log.Error($"Look '{look.Name}': stream step {action} failed.", ex);
            result = ActionResult.Failed(ex.Message);
        }
        _s.Journal.Record(origin.Label, action.Kind.ToString(), JournalTarget(action), result.Status.ToString(),
            $"Look '{look.Name}': {result.Message}");
        return result;
    }

    /// <summary>
    /// A look can start or pause break music: the same verb a cue or the remote would use, run
    /// after the picture has landed, journaled on its own with the look's origin — and never
    /// able to stop the look, whatever Spotify or the library says. Null when the look leaves
    /// the music alone.
    /// </summary>
    private ActionResult? RunLookMusic(LookConfig look, ActionOrigin origin)
    {
        if (look.MusicItemId.Length == 0) return null;
        var action = look.MusicItemId == LookConfig.PauseMusic
            ? new ShowAction(ShowActionKind.SpotifyPause)
            : new ShowAction(ShowActionKind.SpotifyPlay, look.MusicItemId);
        ActionResult result;
        try
        {
            result = Run(action, origin);
        }
        catch (Exception ex)
        {
            Log.Error($"Look '{look.Name}': music step {action} failed.", ex);
            result = ActionResult.Failed(ex.Message);
        }
        _s.Journal.Record(origin.Label, action.Kind.ToString(), JournalTarget(action), result.Status.ToString(),
            $"Look '{look.Name}': {result.Message}");
        return result;
    }

    /// <summary>
    /// A look by id or name — or by its place in the show's order as "#3", a bank key that follows
    /// the list as looks are made ("#0" is no place: a name, refused). The problem says why not.
    /// </summary>
    private LookConfig? ResolveLook(string target, out string problem)
    {
        problem = "";
        var t = target.Trim();
        if (t.StartsWith('#') && int.TryParse(t[1..], System.Globalization.NumberStyles.None, System.Globalization.CultureInfo.InvariantCulture, out var n) && n > 0)
        {
            var looks = State.LooksAndCues.Looks;
            if (n > looks.Count)
            {
                problem = $"no look #{n} — the show has {looks.Count}";
                return null;
            }
            return looks[n - 1];
        }
        var look = LookService.Find(State, t);
        if (look is null) problem = $"No look named '{target}'.";
        return look;
    }
}
