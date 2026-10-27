using Patterns.Core.Menus;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>The show network's link verbs a person on this desk presses (Show Bus 1.3 §13.16.6): Trust, beside the short fingerprint.</summary>
public sealed partial class ShowActions
{
    private ActionResult? RunNodes(ShowAction a, ActionOrigin origin) => a.Kind switch
    {
        ShowActionKind.StagewatchTrust => StagewatchLinkActions.Trust(_s.Kernel.Stagewatch, a.Value, origin),
        _ => null,
    };
}

/// <summary>
/// The Stagewatch link's Trust (Show Bus 1.3 §13.6.8) as both desks run it — the main desk's action layer and a node's, which runs
/// it on its own link and never forwards it — and as their Nodes cards and right-click menus offer it: one action, so the button and
/// the menus' entries are the same press, journalled as one row.
/// </summary>
public static class StagewatchLinkActions
{
    /// <summary>What every surface but the desk hears: Trust is a person's choice beside the fingerprint this desk shows.</summary>
    public const string DeskOnlyWords = "TRUST is a person's choice on the desk, beside the fingerprint";

    /// <summary>
    /// The press for the certificate a person was shown (sha256:…) — the one the Nodes card's words drew, or the menu entry's — so a
    /// press trusts that certificate and no other.
    /// </summary>
    public static ShowAction TrustAction(string shownFingerprint) => new(ShowActionKind.StagewatchTrust, "", shownFingerprint);

    /// <summary>
    /// Runs a Trust press: from the desk alone (a button or a right-click entry), for the certificate the press was shown beside, and
    /// only while it is the one waiting — compared and taken in one step by the link, so one that changed since the words were drawn
    /// is refused and nothing is pinned. Refused, too, when nothing waits.
    /// </summary>
    public static ActionResult Trust(StagewatchLinkService link, string fingerprint, ActionOrigin origin)
    {
        if (origin.Kind != OriginKind.Desk) return ActionResult.Refused(DeskOnlyWords + " — refused from " + origin.Label + ".");
        var (pinned, words) = link.TrustCertificate(fingerprint);
        return pinned ? ActionResult.Done(words) : ActionResult.Refused(words);
    }

    /// <summary>
    /// "Stagewatch Hall A shows 03e9 b152 ca42 518c — compare it on its screen; Trust sends it this desk's key", for the certificate
    /// <paramref name="fingerprint"/> the words show; "" when none.
    /// </summary>
    public static string Words(string name, string fingerprint)
        => fingerprint.Length > 0
            ? $"Stagewatch {name} shows {TlsFingerprint.Short(fingerprint)} — compare it on its own screen; Trust sends it this desk's key. Until then its public view is read without the key."
            : "";

    /// <summary>The menus' fact: the linked Stagewatch card whose certificate waits for Trust, or null.</summary>
    public static MenuTrust? MenuFact(StagewatchLinkService link, IEnumerable<ShowAppCard> cards)
    {
        var fp = link.TrustFingerprint;
        if (fp.Length == 0) return null;
        var card = cards.FirstOrDefault(c => c.Linked && c.KindLabel == "Stagewatch");
        return card is null ? null : new MenuTrust(card.Id, link.TrustName, fp);
    }
}
