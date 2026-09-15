using Avalonia.Media;
using Patterns.Core.Menus;

namespace Patterns.App.ViewModels;

/// <summary>
/// The desk, or a node, as the right-click menus see it: it builds the menu for a thing on the
/// screen (a tile, a cue row, a look, a chip, a layer, an overlay, the panes) and runs what is
/// chosen. The flyout finds the host up the visual tree — the window's view model — so a menu
/// inside a pop-out, a list or a template needs no binding path of its own.
/// </summary>
public interface IDeskMenuHost
{
    /// <summary>The menu for a kind of thing and its subject (the row's or tile's object, or a word the view names), or null when there is none.</summary>
    DeskMenuVm? MenuFor(string kind, object? subject);
}

/// <summary>
/// A menu on the screen: the groups and their entries, the drawer that is open beside them,
/// and the one thing a choice does — run the entry through the host and close. The words the
/// host answers with go to the status line; the flyout hears <see cref="Chosen"/> and hides.
/// </summary>
public sealed class DeskMenuVm : Patterns.Core.Model.Observable
{
    private readonly Func<MenuEntry, string?> _run;
    private MenuEntryVm? _open;

    public DeskMenuVm(DeskMenu menu, Func<MenuEntry, string?> run)
    {
        Menu = menu;
        _run = run;
        Groups = menu.Groups.Select(g => new MenuGroupVm(g, this)).ToList();
        BackCommand = new RelayCommand(() => SetOpen(null));
    }

    public DeskMenu Menu { get; }

    public string Title => Menu.Title;

    public string Subtitle => Menu.Subtitle;

    public bool HasSubtitle => Menu.Subtitle.Length > 0;

    public IBrush HueBrush => MenuBrushes.For(Menu.Tone);

    public IReadOnlyList<MenuGroupVm> Groups { get; }

    /// <summary>The drawer open beside the menu — the entry whose choices show — or null.</summary>
    public MenuEntryVm? Open => _open;

    public bool HasOpen => _open is not null;

    public string OpenHeading => _open?.Text ?? "";

    public IReadOnlyList<MenuEntryVm> OpenChildren => _open?.Children ?? Array.Empty<MenuEntryVm>();

    public RelayCommand BackCommand { get; }

    /// <summary>A leaf ran (or refused): the flyout closes.</summary>
    public event Action? Chosen;

    /// <summary>What the host said about the last choice — the status line's words, for a test.</summary>
    public string LastWords { get; private set; } = "";

    /// <summary>Every entry, the drawers' choices included — Find(id) for a test or the wire.</summary>
    public MenuEntryVm? Find(string id)
    {
        foreach (var g in Groups)
        {
            foreach (var e in g.Entries)
            {
                if (e.Entry.Id == id) return e;
                foreach (var c in e.Children)
                {
                    if (c.Entry.Id == id) return c;
                }
            }
        }
        return null;
    }

    internal void Choose(MenuEntryVm entry)
    {
        if (!entry.IsEnabled) return;
        if (entry.HasChildren)
        {
            SetOpen(ReferenceEquals(_open, entry) ? null : entry);
            return;
        }
        LastWords = _run(entry.Entry) ?? "";
        Chosen?.Invoke();
    }

    private void SetOpen(MenuEntryVm? entry)
    {
        var was = _open;
        if (ReferenceEquals(was, entry)) return;
        _open = entry;
        was?.RaiseOpen();
        entry?.RaiseOpen();
        Raise(nameof(Open));
        Raise(nameof(HasOpen));
        Raise(nameof(OpenHeading));
        Raise(nameof(OpenChildren));
    }

    internal bool IsOpen(MenuEntryVm entry) => ReferenceEquals(_open, entry);
}

/// <summary>A heading in its tone, the one-line rule under it, and its entries.</summary>
public sealed class MenuGroupVm
{
    public MenuGroupVm(MenuGroup group, DeskMenuVm owner)
    {
        Group = group;
        Entries = group.Entries.Select(e => new MenuEntryVm(e, owner)).ToList();
    }

    public MenuGroup Group { get; }
    public string Heading => Group.Heading;
    public string Note => Group.Note;
    public bool HasNote => Group.Note.Length > 0;
    public IBrush HueBrush => MenuBrushes.For(Group.Tone);
    public IReadOnlyList<MenuEntryVm> Entries { get; }
}

/// <summary>One line of the menu as the XAML binds it: the words, the second line (the detail, or why not), the wire line, the tick, the drawer.</summary>
public sealed class MenuEntryVm : Patterns.Core.Model.Observable
{
    private readonly DeskMenuVm _owner;

    public MenuEntryVm(MenuEntry entry, DeskMenuVm owner)
    {
        Entry = entry;
        _owner = owner;
        Children = entry.Children.Select(c => new MenuEntryVm(c, owner)).ToList();
        ChooseCommand = new RelayCommand(() => _owner.Choose(this));
    }

    public MenuEntry Entry { get; }
    public string Text => Entry.Text;
    public string Detail => Entry.Detail;
    public string Because => Entry.Because;
    /// <summary>The line under the text: why it cannot be chosen when it cannot, else what it does.</summary>
    public string Line2 => Entry.Because.Length > 0 ? Entry.Because : Entry.Detail;
    public bool HasLine2 => Line2.Length > 0;
    public string Wire => Entry.Wire;
    public bool HasWire => Entry.HasWire;
    public bool IsEnabled => Entry.IsEnabled;
    public bool IsOn => Entry.IsOn;
    public bool HasChildren => Entry.HasChildren;
    public bool IsOpen => _owner.IsOpen(this);
    public IReadOnlyList<MenuEntryVm> Children { get; }
    public IBrush HueBrush => MenuBrushes.For(Entry.Tone);
    /// <summary>The dot: filled in the tone while the entry is on (the current choice, a switch that is on), hollow otherwise.</summary>
    public IBrush DotBrush => Entry.IsOn ? MenuBrushes.For(Entry.Tone) : Brushes.Transparent;
    public RelayCommand ChooseCommand { get; }

    internal void RaiseOpen() => Raise(nameof(IsOpen));
}

/// <summary>The tones as brushes, parsed once.</summary>
public static class MenuBrushes
{
    private static readonly Dictionary<MenuTone, IBrush> Cache = new();

    public static IBrush For(MenuTone tone)
    {
        lock (Cache)
        {
            if (!Cache.TryGetValue(tone, out var brush))
            {
                brush = new SolidColorBrush(Color.Parse(MenuTones.Hex(tone)));
                Cache[tone] = brush;
            }
            return brush;
        }
    }
}
