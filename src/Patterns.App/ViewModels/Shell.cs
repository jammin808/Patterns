using Avalonia.Media;

namespace Patterns.App.ViewModels;

/// <summary>The five groups of the shell, in the order a show happens.</summary>
public enum ShellGroup
{
    Show,
    Plan,
    Build,
    Setup,
    Admin,
}

/// <summary>One page of the shell: its index in the window's TabControl, its header, its group and its neon hue.</summary>
public sealed record ShellPage(int Index, string Header, ShellGroup Group, string Hue);

/// <summary>A group as the rail shows it.</summary>
public sealed record ShellGroupInfo(ShellGroup Group, string Label, string Hue, string Hint);

/// <summary>A chip on the page strip.</summary>
public sealed record PageChip(int Index, string Header, string Hue, bool IsCurrent)
{
    public IBrush HueBrush { get; } = Brush.Parse(Hue);
}

/// <summary>A group button on the rail.</summary>
public sealed record GroupChip(ShellGroup Group, string Label, string Hue, string Hint, bool IsCurrent)
{
    public IBrush HueBrush { get; } = Brush.Parse(Hue);
}

/// <summary>
/// The shell's one table: which page sits in which group, in the exact order of the TabItems in
/// MainWindow.axaml (a test pins the two together). Grouped by who is at the desk and when:
/// SHOW at show time, PLAN before it, BUILD for whoever makes content, SETUP at the rig, ADMIN
/// for whoever owns the machine.
/// </summary>
public static class Shell
{
    /// <summary>The rails, from the desk's one table (round 74: <see cref="Patterns.Core.Services.DeskPages"/> — the wire's NAV and a deck's navigator read the same rows).</summary>
    public static readonly IReadOnlyList<ShellGroupInfo> Groups = Patterns.Core.Services.DeskPages.Rails
        .Select(r => new ShellGroupInfo(Enum.Parse<ShellGroup>(r.Id), r.Label, r.Hue, r.Hint)).ToList();

    /// <summary>The pages, from the same table, in the exact order of the TabItems in MainWindow.axaml (a test pins the two together).</summary>
    public static readonly IReadOnlyList<ShellPage> Pages = Patterns.Core.Services.DeskPages.All
        .Select((p, i) => new ShellPage(i, p.Header, Enum.Parse<ShellGroup>(p.Rail), p.Hue)).ToList();

    /// <summary>The page the app opens on: the show panel, never the Run surface.</summary>
    public const int PanelPage = 0;

    /// <summary>The page that is the Run layout: selecting it takes the whole window.</summary>
    public const int RunPage = 1;

    public static int FirstPage(ShellGroup group) => Pages.First(p => p.Group == group).Index;

    /// <summary>Whether a kind of process shows a page on its rail: the desk shows every page, a node the few it is for.</summary>
    public static bool IsVisible(Patterns.Core.Model.NodeKind kind, string header)
        => Patterns.Core.Services.NodeKinds.Pages(kind) is not { } pages || pages.Contains(header);

    /// <summary>The pages a kind shows, in the table's order.</summary>
    public static IReadOnlyList<ShellPage> PagesFor(Patterns.Core.Model.NodeKind kind) => Pages.Where(p => IsVisible(kind, p.Header)).ToList();

    /// <summary>The groups with at least one page for the kind, in rail order.</summary>
    public static IReadOnlyList<ShellGroupInfo> GroupsFor(Patterns.Core.Model.NodeKind kind) => Groups.Where(g => PagesFor(kind).Any(p => p.Group == g.Group)).ToList();

    /// <summary>The page a kind opens on: the desk's panel; a node's first page.</summary>
    public static int HomePage(Patterns.Core.Model.NodeKind kind) => kind == Patterns.Core.Model.NodeKind.Desk ? PanelPage : PagesFor(kind)[0].Index;

    public static int IndexOf(string header) => Pages.First(p => p.Header == header).Index;

    public static ShellGroupInfo Info(ShellGroup group) => Groups.First(g => g.Group == group);

    /// <summary>The style class a page's view wears for its neon ("hue-cues", "hue-lower-thirds"): what the settings column beside it wears too.</summary>
    public static string HueClass(string header) => "hue-" + header.ToLowerInvariant().Replace(' ', '-');
}
