using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// The words of a node's Machine tab: what the node is, its ports, the watchdog, how the desk's
/// key reaches it, an update, a restart. The desk's Machine page is the long one; a node's is the
/// few lines its operator needs, in the same words — and pure, so every kind's strip is a test.
/// The tab exists because the words that sent a node's operator to "the Machine page" were
/// sending them to a page a node never had.
/// </summary>
public static class NodeMachine
{
    /// <summary>"Caller node · CALLER-PC · build 1.4.0 · folder D:\Patterns".</summary>
    public static string Identity(NodeKind kind, string machine, string version, string folder)
        => $"{NodeKinds.Label(kind)} node · {machine} · build {version} · folder {folder}";

    /// <summary>The ports in one line and what each carries on this kind — or that remote control is off, which on a node means the desk cannot reach it.</summary>
    public static string PortsWords(NodeKind kind, ControlConfig control)
    {
        if (!control.Enabled) return "Remote control off: the desk cannot reach this node on its wire, and its pages are closed — the beacon still says it is here.";
        var pages = kind switch
        {
            NodeKind.Arcade => "its front door, the phone pad and the audience host page",
            NodeKind.Timer or NodeKind.Caller => "its front door, the stage display and the timer controller",
            _ => "its pages",
        };
        var parts = new List<string>
        {
            $"HTTP {control.HttpPort} — {pages}",
            $"Companion (TCP) {control.TcpPort} — the desk's calls and this node's own verbs",
            control.AudienceEnabled
                ? $"audience {control.AudiencePort}{(control.AudienceBind.Length > 0 ? " at " + control.AudienceBind : "")} — the phones' play pages, {control.AudienceMaxPlayers} seats"
                : "audience port off",
        };
        return string.Join(" · ", parts) + ".";
    }

    /// <summary>Under the watchdog a crash comes back and RESTART and an update land in place; without it they are refused, and the words say what to do instead.</summary>
    public static string WatchdogWords(bool supervised) => supervised
        ? "Under the watchdog: a crash or a hang is brought back within seconds; RESTART and APPLY UPDATE land in place and the node comes back as it was."
        : "Without the watchdog (started by hand, or with --no-watchdog): RESTART and APPLY UPDATE in place are refused — close and reopen this node, or start it normally so the watchdog runs it.";

    /// <summary>How the desk's key reaches a follower: the desk makes one and shows it on its Machine page; typed here, LINK dials with it.</summary>
    public static string KeyWords(bool hasKey, bool linked)
    {
        if (!hasKey) return "No key yet. The desk shows its key on its Machine page (TWIN) — copy it into the box, then LINK on the Nodes tab dials with it; a caller with the wrong key is refused, in words.";
        return linked
            ? "The key is the desk's — this node is linked with it."
            : "The key is set; LINK on the Nodes tab dials with it, and the link's words on that tab say whether the desk took it.";
    }

    /// <summary>The tab's own paragraph: the few sentences this kind's operator needs.</summary>
    public static string Help(NodeKind kind) => kind switch
    {
        NodeKind.Caller => "A caller node is the show caller's own Patterns: the Run surface, the cues and the stage over the desk's show, with no outputs, ever. The desk finds it on the beacon; LINK on the Nodes tab follows the desk, with the desk's key from this tab. Alone, it plans on paper. Its own verbs answer on its wire; the desk's do not. An update is a patterns-update-<version>.zip dropped into the updates folder, or one the management server delivers; APPLY swaps the files through the watchdog and the node comes back linked. RESTART does the same without the swap.",
        NodeKind.Timer => "A stage timer node is the stage display itself: the time in the colour of what is left, the message with its ACK — the desk's clock while linked, its own alone. Its /stage and /timer pages are served on its HTTP port for a tablet on a stand, and every verb and every ACK from them goes to the desk it follows. LINK on the Nodes tab follows the desk, with the desk's key from this tab. Updates and RESTART land through the watchdog, as on a desk.",
        NodeKind.Arcade => "The arcade node is the games and the room: Pong, Snake and Breakout in its window or on a display of its own, the audience's phones on the audience port, the desk driving it by ARCADE and PLAY verbs on its wire. The desk finds it on the beacon; it holds no link and no key. Updates and RESTART land through the watchdog, as on a desk.",
        _ => "",
    };
}
