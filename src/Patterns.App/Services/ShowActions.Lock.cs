using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>The show lock's two verbs: the machine held for the show, and released.</summary>
public sealed partial class ShowActions
{
    private ActionResult? RunLock(ShowAction a, ActionOrigin origin)
    {
        switch (a.Kind)
        {
            case ShowActionKind.ShowLockOn:
                return _s.ShowLock.Lock(origin);
            case ShowActionKind.ShowLockOff:
                return _s.ShowLock.Unlock(origin);
            default:
                return null;
        }
    }
}
