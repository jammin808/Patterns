using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// The desk's next take (round 67.6): the transition or video sting the next TAKE alone arrives by, held
/// until a TAKE spends it. Runtime only — a show opens with the show's own transition — and never the
/// show's setting: the Screens page's transition is untouched however many one-shots run.
/// </summary>
public sealed class NextTakeService
{
    public NextTransition? Pending { get; private set; }

    /// <summary>Raised on the UI thread when the pending one-shot is set, changed, cleared or spent.</summary>
    public event Action? Changed;

    public void Set(NextTransition? next)
    {
        if (Equals(Pending, next)) return;
        Pending = next;
        Changed?.Invoke();
    }

    /// <summary>The one-shot for the take that is happening now — spent, so the take after it is the show's own again.</summary>
    public NextTransition? Consume()
    {
        var pending = Pending;
        if (pending is null) return null;
        Pending = null;
        Changed?.Invoke();
        return pending;
    }

    /// <summary>The STATE row: set, the words, the wire line that sets it again, the sting's name.</summary>
    public object Row() => new
    {
        set = Pending is not null,
        words = Pending?.Words ?? "",
        wire = Pending?.Wire ?? "",
        sting = Pending?.StingName ?? "",
    };
}
