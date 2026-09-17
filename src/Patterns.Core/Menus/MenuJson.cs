using Patterns.Core.Services;

namespace Patterns.Core.Menus;

/// <summary>
/// A menu for the wire: MENU SCREEN 2 answers the same menu the desk would show, entry by entry,
/// each with the wire line that does it — so a tablet, a Companion page or a script can offer
/// the desk's own choices and send the desk's own words back. Compact JSON; the shape is pinned
/// in docs/REMOTE.md.
/// </summary>
public static class MenuJson
{
    public static string Write(DeskMenu menu) => JsonUtil.SerializeCompact(Shape(menu));

    /// <summary>The menu as plain objects — what the JSON is made of, and what a test reads.</summary>
    public static object Shape(DeskMenu menu) => new
    {
        protocol = ControlProtocol.DescriptorVersion,
        kind = menu.Kind,
        subject = menu.Subject,
        title = menu.Title,
        subtitle = menu.Subtitle,
        tone = menu.Tone.ToString().ToLowerInvariant(),
        hue = MenuTones.Hex(menu.Tone),
        groups = menu.Groups.Select(g => new
        {
            heading = g.Heading,
            tone = g.Tone.ToString().ToLowerInvariant(),
            note = g.Note,
            entries = g.Entries.Select(Entry).ToList(),
        }).ToList(),
    };

    private static object Entry(MenuEntry e) => new
    {
        id = e.Id,
        text = e.Text,
        detail = e.Detail,
        scope = e.Scope.ToString().ToLowerInvariant(),
        tone = e.Tone.ToString().ToLowerInvariant(),
        wire = e.Wire,
        menu = e.Menu,
        takesText = e.TakesText,
        because = e.Because,
        on = e.IsOn,
        enabled = e.IsEnabled,
        page = e.Route?.Page ?? "",
        item = e.Route?.Item ?? "",
        question = e.Question,
        children = e.Children.Select(Entry).ToList(),
    };
}
