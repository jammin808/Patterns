using Patterns.Core.Model;

namespace Patterns.App.Services;

/// <summary>Rig day's verbs: the games' switch and the alignment game — the desk's own, never a cue's.</summary>
public sealed partial class ShowActions
{
    private ActionResult? RunRigDay(ShowAction a, ActionOrigin origin)
    {
        switch (a.Kind)
        {
            case ShowActionKind.RigDayOn: return _s.RigDay.SetEnabled(true);
            case ShowActionKind.RigDayOff: return _s.RigDay.SetEnabled(false);
            case ShowActionKind.AlignStart: return _s.RigDay.StartAlign(a.Value);
            case ShowActionKind.AlignStop: return _s.RigDay.StopAlign();
            case ShowActionKind.AlignNext: return _s.RigDay.NextNode();
            case ShowActionKind.AlignPrev: return _s.RigDay.PrevNode();
            case ShowActionKind.AlignSnap: return _s.RigDay.Snap();
            case ShowActionKind.AlignNudge:
            {
                var words = (a.Value ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                if (words.Length < 2 || !float.TryParse(words[0], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dx)
                    || !float.TryParse(words[1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var dy))
                {
                    return ActionResult.Refused("ALIGN NUDGE <dx> <dy> in the output's pixels.");
                }
                return _s.RigDay.Nudge(dx, dy);
            }
            default:
                return null;
        }
    }
}
