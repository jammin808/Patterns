using Patterns.Core.LowerThirds;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// Round 74: the desk as a deck turns it — the pages, the page before, the settings column,
/// where the desk is, what its editors hold, a design made by name. The desk's view model is
/// the one implementation; a node, which has no pages to turn, sets none and the NAV verbs are
/// refused there in words. The action layer runs every NAV verb through this, so a deck, the
/// wire, a MIDI pad and a menu line all turn the same pages by the same path.
/// </summary>
public interface IDeskNavigator
{
    /// <summary>A page by its header, or a rail by its name (its last page); with an item, the thing to select there — a cue, a look, a screen, a design, a person.</summary>
    ActionResult Go(string pageOrRail, string item);

    /// <summary>The page before this one.</summary>
    ActionResult Back();

    /// <summary>The show panel.</summary>
    ActionResult Home();

    /// <summary>The settings column beside the page: ON, OFF or TOGGLE.</summary>
    ActionResult Settings(string mode);

    /// <summary>Where the desk is now.</summary>
    NavFacts Facts();

    /// <summary>The picture under the editors — the editing target's — for PRESET SAVE.</summary>
    PatternConfig EditingPicture { get; }

    /// <summary>A new lower third from a preset, named, selected on its page.</summary>
    LowerThirdDesign NewLowerThird(string preset, string name);
}
