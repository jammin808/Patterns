using Patterns.Audience;
using Patterns.Core.Model;

namespace Patterns.App.Services;

/// <summary>The audience room's verbs: run here on the hub (or a desk with no hub heard), sent to the hub the beacon hears otherwise.</summary>
public sealed partial class ShowActions
{
    private ActionResult? RunPlay(ShowAction a, ActionOrigin origin)
    {
        if (a.Kind is ShowActionKind.AudienceOn or ShowActionKind.AudienceOff) return _s.Play.RunAudience(a);   // this process's own socket, wherever it runs
        if (!PlayService.IsPlayKind(a.Kind)) return null;
        if (_s.Profile == NodeKind.Arcade || _s.Nodes.Arcades().Count == 0) return _s.Play.Run(a);
        return _s.Nodes.SendToArcades(PlayService.Line(a));
    }
}
