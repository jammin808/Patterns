using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>The stage's verbs: the timer paused, resumed, nudged and flashed; a message to the speaker or the crew, and cleared.</summary>
public sealed partial class ShowActions
{
    private ActionResult? RunStage(ShowAction a, ActionOrigin origin)
    {
        switch (a.Kind)
        {
            case ShowActionKind.TimerPause:
                return _s.Stage.Pause();
            case ShowActionKind.TimerResume:
                return _s.Stage.Resume();
            case ShowActionKind.TimerAdd:
                return _s.Stage.Add(a.Value);
            case ShowActionKind.TimerFlash:
                return _s.Stage.Flash();
            case ShowActionKind.StageMessage:
                // Sent by a cue, the message says the cue; else the hand that sent it — "desk", "companion FOH deck", "caller CALLER-PC".
                return _s.Stage.Message(a.Target.Length > 0 ? a.Target : "speaker", a.Value, CueInHand.Length > 0 ? $"cue {CueInHand}" : origin.Label);
            case ShowActionKind.StageClear:
                return _s.Stage.Clear();
            default:
                return null;
        }
    }
}
