using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.Core.Menus;

/// <summary>
/// The show network's verbs for one app (Show Bus 1.3 §13.16.6; the Eye design's D13), built once and read by every surface that
/// shows the app: the Eye's card, a link's edge on the Eye, the Nodes page's card — so the four never disagree. Its groups:
/// <list type="bullet">
/// <item>LINK — the connector's verbs of the link this desk holds: <i>Link again now</i>, <i>Lower floor</i> and <i>Release held
/// fact</i> as the wire's own lines (a deck presses the same verb), <i>Trust</i>, <i>Accept certificate</i> (typed hex) and <i>Accept
/// new id</i> a person's on this desk, with no line (<see cref="DeskMenus.LinkGroup"/>);</item>
/// <item>DEVICE — a Show Bus node driven as a device (<see cref="DeskMenus.DeviceGroup"/>);</item>
/// <item>KEY — the acceptor's <i>Make key for…</i>, a person's on this desk, opening the Nodes page where the key is shown once;</item>
/// <item>ITS LINK TO THIS DESK — each open link this desk accepted from the app: <i>Close</i>, <i>Revoke key</i> and <i>Let this key
/// speak for…</i> (<see cref="DeskMenus.AppLink"/>).</item>
/// </list>
/// None is ever proposed: the trust verbs carry no wire line, so no peer, deck, running order or assistant reaches them, and the
/// link's lines are the operator's (<see cref="StagewatchVerbs.BriefWords"/>). The Eye adds no verb of its own.
/// </summary>
public static class NetworkMenus
{
    /// <summary>The heading of an accepted link's groups beside the link this desk holds to the app.</summary>
    public const string AcceptedHeading = "ITS LINK TO THIS DESK";

    /// <summary>The Nodes page's route item that puts a connector's kind:id in the MAKE KEY box (after this prefix).</summary>
    public const string MakeRoute = "make:";

    /// <summary>
    /// The groups for the app with show network id <paramref name="appId"/> (its card <c>app:&lt;id&gt;</c>): <paramref name="kindLabel"/>
    /// is its kind's words ("Stagewatch"), <paramref name="name"/> its name. Empty for an app none of the groups applies to.
    /// </summary>
    public static IReadOnlyList<MenuGroup> Groups(DeskFacts d, string appId, string kindLabel, string name)
        => Groups(d, d.ShowApps.FirstOrDefault(a => a.Id == appId), appId, kindLabel, name);

    /// <summary>The groups for an app's card, from its card's facts.</summary>
    public static IReadOnlyList<MenuGroup> Groups(DeskFacts d, MenuShowApp app) => Groups(d, app, app.Id, app.KindLabel, app.Name);

    private static IReadOnlyList<MenuGroup> Groups(DeskFacts d, MenuShowApp? app, string appId, string kindLabel, string name)
    {
        var groups = new List<MenuGroup>();
        if (appId.Length == 0) return groups;
        var label = app?.KindLabel is { Length: > 0 } said ? said : kindLabel;
        var stagewatch = label == "Stagewatch" || d.Stagewatch.Heard.Contains(appId) || d.Stagewatch.LinkedId == appId;
        if (DeskMenus.LinkGroup(d, appId, stagewatch, app?.Name ?? name) is { } link) groups.Add(link);
        if (app is not null && DeskMenus.DeviceGroup(d, app) is { } device) groups.Add(device);
        if (KeyGroup(d, app, appId, label, app?.Name ?? name) is { } key) groups.Add(key);
        foreach (var accepted in d.AppLinks.Where(l => !l.Ended && l.Id == appId))
            groups.AddRange(DeskMenus.AppLink(d, accepted).Groups.Select(g => g with { Heading = AcceptedHeading }));
        return groups;
    }

    /// <summary>
    /// KEY: <i>Make key for this app…</i> — the Nodes page's MAKE KEY with its kind:id filled in, the scope picked there and the key
    /// shown there once. Null where the app's kind or id is not one a key may be bound to (a twin node's card, an app no hello names).
    /// </summary>
    private static MenuGroup? KeyGroup(DeskFacts d, MenuShowApp? app, string appId, string kindLabel, string name)
    {
        if (app is null || app.Kind.Length == 0 || AppKeys.Connector(app.Kind + ":" + appId) is not { } connector) return null;
        var who = $"{kindLabel} {name}".Trim();
        var entries = new List<MenuEntry>
        {
            new("key.make", "Make a key for this app…", MenuScope.Go, MenuTone.Go)
            {
                Detail = $"MAKE KEY on the Nodes page with {connector} filled in — pick its scope there; the key is shown there once, for {(who.Length > 0 ? who : connector)} only",
                Route = new MenuRoute("Nodes", MakeRoute + connector),
                Because = d.IsNode ? "App keys are the desk's — make it there" : "",
            },
        };
        return new MenuGroup("KEY", MenuTone.Go, entries)
        {
            Note = "The operator's on this desk: never on the wire or a deck, never proposed by the assistant.",
        };
    }
}
