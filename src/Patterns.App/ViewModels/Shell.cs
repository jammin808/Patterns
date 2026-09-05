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
    public static readonly IReadOnlyList<ShellGroupInfo> Groups = new[]
    {
        new ShellGroupInfo(ShellGroup.Show, "SHOW", "#2EE68A", "Show time: the panel beside the switcher, and the Run surface for the caller"),
        new ShellGroupInfo(ShellGroup.Plan, "PLAN", "#6E9BFF", "Before the show: the cue stack, looks, and the install's clock — programmes, adverts, announcements"),
        new ShellGroupInfo(ShellGroup.Build, "BUILD", "#3EC1F3", "Making content: patterns, media, overlays, countdown, particles, branding, the library"),
        new ShellGroupInfo(ShellGroup.Setup, "SETUP", "#B18CFF", "At the rig: screens, audio, NDI, streaming, remote control"),
        new ShellGroupInfo(ShellGroup.Admin, "ADMIN", "#B8E356", "The machine: performance, GPU, the watchdog — and Help"),
    };

    public static readonly IReadOnlyList<ShellPage> Pages = Table(
        ("Panel", ShellGroup.Show, "#2EE68A"),
        ("Run", ShellGroup.Show, "#2EE68A"),
        ("Cues", ShellGroup.Plan, "#6E9BFF"),
        ("Looks", ShellGroup.Plan, "#6E9BFF"),
        ("Install", ShellGroup.Plan, "#9AB4FF"),
        ("Pattern", ShellGroup.Build, "#3EC1F3"),
        ("Media", ShellGroup.Build, "#FF6EC7"),
        ("Overlays", ShellGroup.Build, "#FFC24D"),
        ("Lower thirds", ShellGroup.Build, "#FFC24D"),
        ("Countdown", ShellGroup.Build, "#FFC24D"),
        ("Particles", ShellGroup.Build, "#FFC24D"),
        ("Branding", ShellGroup.Build, "#FFC24D"),
        ("Library", ShellGroup.Build, "#C0CBDB"),
        ("Screens", ShellGroup.Setup, "#B18CFF"),
        ("Audio", ShellGroup.Setup, "#FF9E58"),
        ("NDI", ShellGroup.Setup, "#8FA5FF"),
        ("Stream", ShellGroup.Setup, "#FF5C7A"),
        ("Remote", ShellGroup.Setup, "#35E0D0"),
        ("Interactive", ShellGroup.Setup, "#7CF5C8"),
        ("Machine", ShellGroup.Admin, "#B8E356"),
        ("Help", ShellGroup.Admin, "#C0CBDB"));

    /// <summary>The page the app opens on: the show panel, never the Run surface.</summary>
    public const int PanelPage = 0;

    /// <summary>The page that is the Run layout: selecting it takes the whole window.</summary>
    public const int RunPage = 1;

    public static ShellGroup GroupOf(int index) => Pages[index].Group;

    public static int FirstPage(ShellGroup group) => Pages.First(p => p.Group == group).Index;

    public static int IndexOf(string header) => Pages.First(p => p.Header == header).Index;

    public static ShellGroupInfo Info(ShellGroup group) => Groups.First(g => g.Group == group);

    private static IReadOnlyList<ShellPage> Table(params (string Header, ShellGroup Group, string Hue)[] rows)
        => rows.Select((r, i) => new ShellPage(i, r.Header, r.Group, r.Hue)).ToList();
}
