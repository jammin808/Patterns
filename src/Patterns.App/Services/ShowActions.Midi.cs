using Patterns.Core.Model;

namespace Patterns.App.Services;

/// <summary>MIDI learn's verbs (round 73): armed, cancelled, forgotten — from a menu line, the wire or a deck, one path with one receipt.</summary>
public sealed partial class ShowActions
{
    private ActionResult? RunMidi(ShowAction a) => a.Kind switch
    {
        ShowActionKind.MidiLearn => _s.MidiLearn.Arm(a.Value),
        ShowActionKind.MidiLearnOff => _s.MidiLearn.Cancel(),
        ShowActionKind.MidiForget => _s.MidiLearn.Forget(a.Value),
        _ => null,
    };
}
