using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>The twin's two verbs: the standby's own decision to run the show, and to follow again.</summary>
public sealed partial class ShowActions
{
    private ActionResult? RunTwin(ShowAction a, ActionOrigin origin)
    {
        switch (a.Kind)
        {
            case ShowActionKind.TwinTakeOver:
                return _s.Twin.TakeOver(origin);
            case ShowActionKind.TwinStandBy:
                return _s.Twin.StandByAgain(origin);
            default:
                return null;
        }
    }
}
