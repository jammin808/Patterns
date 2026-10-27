using Patterns.Core.Menus;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// The show network's link verbs (G34, Show Bus 1.3 §13.16.6): the Stagewatch link set and followed from the desk, its menus, the wire
/// and a deck; Trust, Accept and the key's binding a person's on this desk, beside the fingerprint.
/// </summary>
public sealed partial class ShowActions
{
    private ActionResult? RunNodes(ShowAction a, ActionOrigin origin) => StagewatchLinkActions.IsLinkVerb(a.Kind)
        ? StagewatchLinkActions.Run(_s.Kernel.Stagewatch, _s.State.Control.Stagewatch, a, origin)
        : null;
}

/// <summary>
/// The Stagewatch link's verbs as both desks run them — the main desk's action layer and a node's, which runs them on its own link and
/// never forwards them — and as their Nodes cards and right-click menus offer them: one action per press, so a button, a menu entry, a
/// wire line and a deck's key are the same verb, journalled as one row.
/// <list type="bullet">
/// <item>LINK, LINK AGAIN, FOLLOW, FLOOR LOWER and RELEASE from any surface but an approval desk; a take that would change nothing is
/// refused with the reason. A link set from anywhere but a person on this desk first turns an unbound key to <c>none</c>
/// (<see cref="StagewatchKeyFor.AfterLine"/>), so the key never goes to a target a line chose.</item>
/// <item>TRUST, ACCEPT and the key's binding from the desk alone: every other origin hears the words and nothing is pinned or bound.</item>
/// </list>
/// </summary>
public static class StagewatchLinkActions
{
    /// <summary>What every surface but the desk hears for TRUST and ACCEPT: a person's choice beside the fingerprint this desk shows.</summary>
    public const string DeskOnlyWords = StagewatchVerbs.DeskOnlyWords;

    /// <summary>The kinds this layer runs.</summary>
    public static bool IsLinkVerb(ShowActionKind kind) => kind is ShowActionKind.StagewatchTrust or ShowActionKind.StagewatchLinkSet
        or ShowActionKind.StagewatchLinkAgain or ShowActionKind.StagewatchFollow or ShowActionKind.StagewatchAccept
        or ShowActionKind.StagewatchFloorLower or ShowActionKind.StagewatchRelease or ShowActionKind.StagewatchKeyBind;

    /// <summary>An approved line from an approval desk (another app's assistant, approved there): the link is never its to change.</summary>
    public static bool FromApprovalDesk(ActionOrigin origin) => origin.Kind == OriginKind.Tcp && origin.Name == StagewatchVerbs.ApprovalDesk;

    /// <summary>
    /// Runs one of the link's verbs on <paramref name="link"/> and its settings (UI thread). The approval desk is refused first; then the
    /// person's own verbs off the desk; then each verb, refused with its reason when it would change nothing.
    /// </summary>
    public static ActionResult Run(StagewatchLinkService link, StagewatchLinkConfig config, ShowAction a, ActionOrigin origin)
    {
        if (FromApprovalDesk(origin)) return ActionResult.Refused(StagewatchVerbs.ApprovalDeskWords + ".");
        switch (a.Kind)
        {
            case ShowActionKind.StagewatchTrust:
                return Trust(link, a.Value, origin);
            case ShowActionKind.StagewatchAccept:
                if (origin.Kind != OriginKind.Desk) return ActionResult.Refused(DeskOnlyWords + " — refused from " + origin.Label + ".");
                var (accepted, acceptWords) = link.AcceptCertificate(a.Value);
                return accepted ? ActionResult.Done(acceptWords) : ActionResult.Refused(acceptWords);
            case ShowActionKind.StagewatchKeyBind:
                if (origin.Kind != OriginKind.Desk) return ActionResult.Refused(StagewatchVerbs.KeyWords + " — refused from " + origin.Label + ".");
                var (bound, bindWords) = link.BindKey(a.Value);
                return bound ? ActionResult.Done(bindWords) : ActionResult.Refused(bindWords);
            case ShowActionKind.StagewatchLinkSet:
                return Link(link, config, a.Value, origin);
            case ShowActionKind.StagewatchFollow:
                return Follow(link, config, a.Value);
            case ShowActionKind.StagewatchLinkAgain:
                var (again, againWords) = link.LinkAgain();
                return again ? ActionResult.Done(againWords) : ActionResult.Refused(againWords);
            case ShowActionKind.StagewatchFloorLower:
                var (lowered, floorWords) = link.LowerFloor();
                return lowered ? ActionResult.Done(floorWords) : ActionResult.Refused(floorWords);
            case ShowActionKind.StagewatchRelease:
                return ActionResult.Refused(StagewatchVerbs.NothingHeldWords + " — nothing to release.");
            default:
                return ActionResult.Refused($"Unknown action '{a.Kind}'.");
        }
    }

    /// <summary>The journal row's target for one of the link's verbs: the value it set (an address, an id, ON or OFF) or the target bound; never typed hex.</summary>
    public static string JournalTarget(ShowAction a) => a.Kind is ShowActionKind.StagewatchLinkSet or ShowActionKind.StagewatchFollow or ShowActionKind.StagewatchKeyBind ? a.Value : "";

    /// <summary>
    /// LINK: ON, OFF, AUTO, an address or a heard id. The settings are the link's one writer's (this thread), so the Nodes page's boxes
    /// show what a line set; the link reconciles at once.
    /// </summary>
    private static ActionResult Link(StagewatchLinkService link, StagewatchLinkConfig config, string value, ActionOrigin origin)
    {
        var v = StagewatchVerbs.LinkValue(value);
        if (v is null) return ActionResult.Refused($"'{value}' is not one of host:port, id:<a heard Stagewatch's id>, AUTO, ON or OFF.");
        if (v == "OFF")
        {
            if (!config.Enabled) return ActionResult.Refused("The Stagewatch link is off already.");
            config.Enabled = false;
            link.Reconcile();
            return ActionResult.Done("Stagewatch link off — this desk's own stage timer runs.");
        }
        string host;
        string where;
        string pinKey;
        string typedKey = "";
        if (v == "ON")
        {
            if (config.Enabled) return ActionResult.Refused("The Stagewatch link is on already.");
            host = config.Host;
            where = host.Length == 0 ? "following the first Stagewatch heard on the show network" : "→ " + host;
            pinKey = StageFollow.ParseId(host) is { } onId ? StagewatchKeyFor.Node(onId) : AddressKey(host);
            typedKey = pinKey.StartsWith("addr:", StringComparison.Ordinal) ? pinKey : "";
        }
        else if (v == "AUTO")
        {
            if (config.Enabled && config.Host.Length == 0) return ActionResult.Refused("The Stagewatch link follows the first Stagewatch heard on the show network already.");
            host = "";
            where = "following the first Stagewatch heard on the show network";
            pinKey = "";
        }
        else if (StageFollow.ParseId(v) is { } id)
        {
            if (link.HeardStagewatch(id) is not { } peer) return ActionResult.Refused($"No Stagewatch with id {id} is heard on the show network.");
            if (peer.Conflict is { } conflict) return ActionResult.Refused($"{conflict.Words} — {ShowBusView.ConflictHoldWords}.");
            var name = peer.Name.Length > 0 ? peer.Name : peer.Host;
            if (config.Enabled && string.Equals(config.Host, v, StringComparison.Ordinal)) return ActionResult.Refused($"The Stagewatch link follows {name} ({id}) already.");
            host = v;
            where = $"→ {name} ({id}) at {peer.Address}";
            pinKey = StagewatchKeyFor.Node(id);
        }
        else
        {
            if (config.Enabled && string.Equals(StagewatchVerbs.LinkValue(config.Host), v, StringComparison.OrdinalIgnoreCase))
                return ActionResult.Refused($"The Stagewatch link follows {v} already.");
            host = v;
            where = "→ " + v;
            pinKey = typedKey = AddressKey(v);
        }
        // G34: a line never sends the key to a target it chose — an unbound key goes nowhere until a person binds it on this desk.
        if (origin.Kind != OriginKind.Desk) config.KeyFor = StagewatchKeyFor.AfterLine(config.KeyFor);
        config.Host = host;
        config.Enabled = true;
        link.Reconcile();
        return ActionResult.Done($"Stagewatch link on {where} — {KeyWords(config, pinKey, typedKey)}.");
    }

    /// <summary>The <c>addr:</c> binding of a typed address ("" for anything else).</summary>
    private static string AddressKey(string host) => StageFollow.ParseAddress(host) is { } t ? StagewatchKeyFor.Address(t.Host, t.Port) : "";

    /// <summary>
    /// What the key's binding allows at the target a LINK named — said, never the key, and never as a delivery: whether the key rides a
    /// dial is the link's to decide at that dial (TLS to a certificate a person pinned, or this machine; no hold standing), and the
    /// link's record says when it goes.
    /// </summary>
    private static string KeyWords(StagewatchLinkConfig config, string pinKey, string typedKey)
    {
        if (config.ApiKey.Length == 0) return "no key is set: its public view is read";
        if (config.KeyFor == StagewatchKeyFor.None) return "dialling without the key: it is bound to nothing — bind it, or type one, on the Nodes page";
        if (config.KeyFor.Length == 0) return "the key is bound to nothing yet: it may go to this Stagewatch and binds to the first that takes it; the link's record says when it goes";
        if (pinKey.Length > 0 && StagewatchKeyFor.Goes(config.KeyFor, pinKey, typedKey)) return "the key is bound to it; the link's record says when it goes";
        return $"the key goes only to {StagewatchKeyFor.Words(config.KeyFor)} — none to another; type its own on the Nodes page, or BIND KEY there";
    }

    /// <summary>FOLLOW ON|OFF: the stage pages and the stage timer follow the linked Stagewatch's time, or this desk's own.</summary>
    private static ActionResult Follow(StagewatchLinkService link, StagewatchLinkConfig config, string value)
    {
        var want = (value ?? "").Trim().ToUpperInvariant() switch { "ON" => true, "OFF" => false, _ => (bool?)null };
        if (want is null) return ActionResult.Refused($"'{value}' is not ON or OFF.");
        var name = link.Sight.Name is { Length: > 0 } n ? "Stagewatch " + n : "the Stagewatch";
        if (config.FollowStage == want.Value)
            return ActionResult.Refused(want.Value ? $"The stage follows {name}'s timer already." : "The stage runs this desk's own timer already.");
        config.FollowStage = want.Value;
        link.Reconcile();                                                                   // the show's need moves the link's light at once (13.1)
        return ActionResult.Done(want.Value
            ? $"The stage follows {name}'s timer" + (config.Enabled ? "." : " once the link is on — it is off.")
            : "The stage runs this desk's own timer.");
    }

    /// <summary>
    /// The press for the certificate a person was shown (sha256:…) — the one the Nodes card's words drew, or the menu entry's — so a
    /// press trusts that certificate and no other.
    /// </summary>
    public static ShowAction TrustAction(string shownFingerprint) => new(ShowActionKind.StagewatchTrust, "", shownFingerprint);

    /// <summary>ACCEPT with the hex a person typed beside the changed certificate; the box is never pre-filled.</summary>
    public static ShowAction AcceptAction(string typedHex) => new(ShowActionKind.StagewatchAccept, "", (typedHex ?? "").Trim());

    /// <summary>BIND KEY for the Stagewatch the press was shown beside (its pin key), so a press binds that one and no other.</summary>
    public static ShowAction BindAction(string shownTarget) => new(ShowActionKind.StagewatchKeyBind, "", shownTarget);

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

    /// <summary>
    /// "CERTIFICATE CHANGED — Stagewatch Hall A now shows 03e9 b152 ca42 518c …; compare it on its own screen and type at least 8 hex of
    /// it to accept it" for the changed certificate the Nodes card shows beside ACCEPT; "" when none waits.
    /// </summary>
    public static string AcceptWords(string name, string fingerprint)
        => TlsFingerprint.IsValid(fingerprint)
            ? $"CERTIFICATE CHANGED — {name} now shows {TlsFingerprint.Short(fingerprint)} …; nothing of this desk's goes to it. Compare it on its own screen, then type at least 8 hex of it here to accept it."
            : "";

    /// <summary>The menus' fact: the linked Stagewatch card whose certificate waits for Trust, or null.</summary>
    public static MenuTrust? MenuFact(StagewatchLinkService link, IEnumerable<ShowAppCard> cards)
    {
        var fp = link.TrustFingerprint;
        if (fp.Length == 0) return null;
        var card = cards.FirstOrDefault(c => c.Linked && c.KindLabel == "Stagewatch");
        return card is null ? null : new MenuTrust(card.Id, link.TrustName, fp);
    }

    /// <summary>The menus' facts of the link (G34): on, the Stagewatch it dials, the stage's follow, the Stagewatches heard, what waits for a person.</summary>
    public static MenuStagewatch MenuStagewatch(StagewatchLinkService link, StagewatchLinkConfig config, IEnumerable<ShowAppCard> cards)
    {
        var list = cards.Where(c => c.KindLabel == "Stagewatch").ToList();
        var linked = list.FirstOrDefault(c => c.Linked);
        return new MenuStagewatch
        {
            On = config.Enabled,
            LinkedId = linked?.Id ?? "",
            LinkedName = linked?.Name ?? "",
            Follow = config.FollowStage,
            Heard = list.Where(c => !c.Linked && c.Line.StartsWith("heard", StringComparison.Ordinal) && StageFollow.ParseId(StageFollow.IdPrefix + c.Id) is not null).Select(c => c.Id).ToList(),
            Pending = link.PendingFingerprint,
            Floor = link.HasFloor,
            BindTarget = link.KeyBindTarget,
            BindName = link.KeyBindName,
        };
    }
}
