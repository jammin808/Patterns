using Patterns.Core.Model;

namespace Patterns.App.Services;

/// <summary>The arcade's verbs: run here on the arcade node (or a desk with no arcade heard), sent to the arcade nodes the beacon hears otherwise.</summary>
public sealed partial class ShowActions
{
    private ActionResult? RunArcade(ShowAction a, ActionOrigin origin)
    {
        if (!ArcadeService.IsArcadeKind(a.Kind)) return null;
        // The arcade node runs them; a desk sends them to the arcade nodes it hears, and with none
        // heard runs the game itself — its own Arcade page, a rig day's toy on the show machine.
        if (_s.Profile == NodeKind.Arcade || _s.Nodes.Arcades().Count == 0) return _s.Arcade.Run(a);
        return _s.Nodes.SendToArcades(ArcadeService.Line(a));
    }
}
