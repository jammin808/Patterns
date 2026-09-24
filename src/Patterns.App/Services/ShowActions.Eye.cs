using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>The God's Eye's verbs (round 66): the operator's view moved through the action layer — a key, the wire, a menu line, all one path with one receipt.</summary>
public sealed partial class ShowActions
{
    private ActionResult? RunEye(ShowAction a, ActionOrigin origin) => a.Kind switch
    {
        ShowActionKind.EyeFocus => _s.Eye.Focus(a.Value),
        ShowActionKind.EyeNext => _s.Eye.Next(),
        ShowActionKind.EyePrev => _s.Eye.Prev(),
        ShowActionKind.EyeLens => _s.Eye.SetLens(a.Value),
        ShowActionKind.EyeReset => _s.Eye.Reset(),
        ShowActionKind.EyeReplay => _s.Eye.Replay(a.Value),
        _ => null,
    };
}
