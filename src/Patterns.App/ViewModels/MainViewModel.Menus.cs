using Patterns.App.Services;
using Patterns.Core.LowerThirds;
using Patterns.Core.Menus;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Patterns.Rendering;

namespace Patterns.App.ViewModels;

/// <summary>
/// The desk as the right-click menus' host (round 60). One rule runs every menu: what changes a
/// picture lands in the preview — EDIT SAFE is opened first, so nothing a menu does reaches the
/// audience until CUT or TAKE — the cue stack's edits land on the stack, a page of the rail
/// opens with its item selected, and a question goes to the assistant with the facts in it.
/// </summary>
public sealed partial class MainViewModel : IDeskMenuHost
{
    /// <summary>The desk's facts as the menus read them, with the Pattern page's own labels for the kinds of picture.</summary>
    internal DeskFacts MenuFacts()
        => DeskMenuFacts.Desk(_services) with { Kinds = Lists.PatternKinds.Select(k => new MenuKind(((PatternKind)k.Value).ToString(), k.Label)).ToList() };

    public DeskMenuVm? MenuFor(string kind, object? subject)
    {
        var facts = MenuFacts();
        switch (kind)
        {
            case "tile":
                return subject is SwitcherTile tile ? TileMenu(tile, facts) : null;
            case "screen":
                // Round 67.7: a screen on the Screens page (its arrangement tile) gets the same menu as its wall tile —
                // the group, the lock, the preview, TAKE — so the two never disagree; the wall tile, when there is
                // one for this screen alone, carries its switches (arm, MON, collapse).
                return subject is ScreenPlacement placement ? ScreenMenu(placement, facts) : null;
            case "program":
                return new DeskMenuVm(DeskMenus.Program(facts), e => RunMenuEntry(e, null));
            case "preview":
                return subject is HitRect hit ? MenuForHit(hit, facts) : new DeskMenuVm(DeskMenus.Preview(facts), e => RunMenuEntry(e, null));
            case "cue":
            {
                var cue = subject switch { RunRow r => r.Cue, CueRow c => c.Cue, RunCueConfig q => q, _ => null };
                return cue is null ? null : CueMenus.Build(_services, Run, Cues, cue, subject as RunRow, subject as CueRow, facts, GoTo, AskAssistant, m => StatusMessage = m);
            }
            case "look":
            {
                if (subject is not LookConfig look) return null;
                var l = facts.Looks.FirstOrDefault(x => x.Id == look.Id) ?? new MenuLook(look.Id, look.Name, look.Hotkey, false, false);
                return new DeskMenuVm(DeskMenus.Look(facts, l), e => RunMenuEntry(e, look));
            }
            case "lowerthird":
            {
                if (subject is not LowerThirdDesign design) return null;
                var d = facts.Designs.FirstOrDefault(x => x.Id == design.Id) ?? new MenuDesign(design.Id, design.Name, design.IsDefault, design.IsOnAir, design.IsInPreview);
                return new DeskMenuVm(DeskMenus.LowerThird(facts, d), e => RunMenuEntry(e, design));
            }
            case "person":
            {
                if (subject is not LowerThirdEntry entry) return null;
                return new DeskMenuVm(DeskMenus.Person(facts, new MenuPerson(entry.Id, entry.Name, entry.Summary)), e => RunMenuEntry(e, entry));
            }
            case "layer":
            {
                var index = subject switch
                {
                    LayerConfig layer => ReferenceEquals(layer, ActivePattern.Layer2) ? 2 : 1,
                    string word when word.EndsWith('2') => 2,
                    _ => 1,
                };
                return new DeskMenuVm(DeskMenus.Layer(facts, DeskMenuFacts.Layer(ActivePattern, index)), e => RunMenuEntry(e, index));
            }
            case "overlay":
            case "countdown":
            {
                var word = kind == "countdown" ? "countdown" : subject as string ?? "clock";
                return new DeskMenuVm(DeskMenus.Overlay(facts, DeskMenuFacts.Overlay(_services, word)), e => RunMenuEntry(e, word));
            }
            case "monitor":
                return new DeskMenuVm(DeskMenus.Monitor(facts, DeskMenuFacts.Monitor(_services)), e => RunMenuEntry(e, null));
            case "transport":
                return new DeskMenuVm(DeskMenus.Transport(facts), e => RunMenuEntry(e, null));   // round 73: GO, HOLD, BLACKOUT, STOP ALL — learnable like everything else
            case "eye":
                return subject is EyeNode node ? EyeMenu(node, facts) : null;
            default:
                return null;
        }
    }

    /// <summary>
    /// The menu for the thing a right-click on the PREVIEW pane landed on (round 63): the box the
    /// frame drew under the pointer names it — a layer, an overlay, the countdown — and that
    /// thing's own menu opens, its settings, inputs and position in it. The bare picture, a media
    /// frame or a web page open the preview's menu, whose SOURCE group changes what the picture is.
    /// </summary>
    private DeskMenuVm? MenuForHit(HitRect hit, DeskFacts facts) => hit.Kind switch
    {
        HitKind.Layer1 => MenuFor("layer", "layer1"),
        HitKind.Layer2 => MenuFor("layer", "layer2"),
        HitKind.Clock => MenuFor("overlay", "clock"),
        HitKind.Logo => MenuFor("overlay", "logo"),
        HitKind.Message => MenuFor("overlay", "message"),
        HitKind.Weather => MenuFor("overlay", "weather"),
        HitKind.Pip => MenuFor("overlay", "pip"),
        HitKind.Badge => MenuFor("overlay", "badge"),
        HitKind.Countdown => MenuFor("countdown", null),
        _ => new DeskMenuVm(DeskMenus.Preview(facts), e => RunMenuEntry(e, null)),
    };

    /// <summary>
    /// A thing on the God's Eye (round 66) opens the menu the desk already has for it — a screen its
    /// wall tile's, the stack its standby cue's — and its own Eye menu otherwise, so the Eye never
    /// keeps a copy of a menu that could go stale.
    /// </summary>
    private DeskMenuVm? EyeMenu(EyeNode node, DeskFacts facts)
    {
        switch (node.MenuKind)
        {
            case "tile":
            {
                var tile = SwitcherTiles.FirstOrDefault(t => t.TargetId == node.MenuSubject);
                if (tile is not null) return TileMenu(tile, facts);
                var s = DeskMenuFacts.Screen(_services, node.MenuSubject);
                return new DeskMenuVm(DeskMenus.Screen(facts, s), e => RunMenuEntry(e, null));
            }
            case "cue":
            {
                var menu = MenuFor("cue", State.Stacks.SelectMany(s => s.Cues).FirstOrDefault(c => c.Id == node.MenuSubject));
                if (menu is not null) return menu;
                break;
            }
        }
        return new DeskMenuVm(EyeMenus.For(facts, _services.Eye.Graph, node), e => RunMenuEntry(e, node));
    }

    private DeskMenuVm ScreenMenu(ScreenPlacement placement, DeskFacts facts)
    {
        var id = placement.ScreenId;
        var tile = SwitcherTiles.FirstOrDefault(t => t.TargetId == id);
        if (tile is not null) return TileMenu(tile, facts);
        // A screen inside a joined canvas has no wall tile of its own: the menu is the screen's, its tile switches read-only.
        var s = DeskMenuFacts.Screen(_services, id);
        return new DeskMenuVm(DeskMenus.Screen(facts, s), e => RunMenuEntry(e, null));
    }

    private DeskMenuVm TileMenu(SwitcherTile tile, DeskFacts facts)
    {
        var s = DeskMenuFacts.Screen(_services, tile.TargetId ?? "", tile.IsMonitored) with { Collapsed = tile.IsCollapsed };
        if (!tile.IsProgramTile) s = s with { Title = tile.Title };
        var menu = tile.IsProgramTile ? DeskMenus.Program(facts, s) : DeskMenus.Screen(facts, s);
        return new DeskMenuVm(menu, e => RunMenuEntry(e, tile));
    }

    /// <summary>
    /// One entry, chosen: the words for the status line come back. A preview entry opens EDIT
    /// SAFE first and runs its action or its edit on the edited state; a live entry runs its
    /// action or flips the tile's own switch; the pages and the assistant are the same for
    /// every menu.
    /// </summary>
    internal string? RunMenuEntry(MenuEntry entry, object? subject)
    {
        string? words = null;
        switch (entry.Scope)
        {
            case MenuScope.Preview:
                EnsureEditSafe();
                if (entry.Action is { } action)
                {
                    var result = Report(_services.Actions.Execute(action, ActionOrigin.Desk));
                    words = result.Message;
                    if (subject is SwitcherTile staged && result.Ok && !staged.IsProgramTile) SelectTarget(staged.TargetId); // a FOCUSED take puts it up there
                }
                else if (entry.Edit.StartsWith("layer.browse:", StringComparison.Ordinal))
                {
                    var layer = entry.Edit.EndsWith('2') ? ActivePattern.Layer2 : ActivePattern.Layer1;
                    if (layer.Source == LayerSource.Video) BrowseLayerVideoCommand.Execute(layer);
                    else BrowseLayerImageCommand.Execute(layer);
                }
                else if (entry.Edit.Length > 0)
                {
                    var picture = ActivePattern;
                    _services.BulkEdit(() => words = PreviewEdits.Apply(State, picture, entry.Edit, DateTime.UtcNow, MenuFacts()));
                    if (words is null) words = $"The desk does not know '{entry.Edit}'.";
                }
                AfterMenuChange();
                break;

            case MenuScope.Live:
                if (entry.Action is { } live)
                {
                    words = Report(_services.Actions.Execute(live, ActionOrigin.Desk)).Message;
                    AfterMenuChange();
                    break;
                }
                switch (entry.Edit)
                {
                    case "tile.arm" when subject is SwitcherTile tile:
                        tile.IsArmed = !tile.IsArmed;
                        words = StatusMessage;
                        break;
                    case "tile.mon" when subject is SwitcherTile tile:
                        tile.IsMonitored = !tile.IsMonitored;
                        words = tile.IsMonitored ? $"{tile.Title}: its miniatures are drawn again." : $"{tile.Title}: its miniatures are off — the GPU is spared; nothing changes on air.";
                        break;
                    case "tile.collapse" when subject is SwitcherTile tile:
                        tile.IsCollapsed = !tile.IsCollapsed;
                        words = StatusMessage;
                        break;
                    case "preview.open":
                        IsSandboxActive = true;
                        words = StatusMessage;
                        break;
                    case "preview.discard":
                        IsSandboxActive = false;
                        words = StatusMessage;
                        break;
                }
                break;

            case MenuScope.Stack:
                switch (subject)
                {
                    case LookConfig look when entry.Edit == "look.update":
                        Show.UpdateLookCommand.Execute(look);
                        words = StatusMessage;
                        break;
                    case LookConfig look when entry.Edit.StartsWith("look.hotkey:", StringComparison.Ordinal) && int.TryParse(entry.Edit[12..], out var slot):
                        foreach (var other in State.LooksAndCues.Looks)
                        {
                            if (slot > 0 && other.Hotkey == slot && !ReferenceEquals(other, look)) other.Hotkey = 0;
                        }
                        look.Hotkey = slot;
                        words = slot > 0 ? $"F{slot} puts '{look.Name}' on air." : $"'{look.Name}' has no F-key.";
                        break;
                    case LowerThirdDesign design when entry.Edit == "lt.default":
                        SetDefaultLowerThird(design);
                        words = StatusMessage;
                        break;
                }
                break;

            case MenuScope.Go:
                words = entry.Route is { } route ? GoTo(route) : null;
                break;

            case MenuScope.Ask:
                words = AskAssistant(entry.Question);
                break;

            case MenuScope.Learn:
                // Round 73: MIDI learn armed (or cancelled, or a binding forgotten) through the action layer — journaled like a key; the air is never touched.
                if (entry.Action is { } learn) words = _services.Actions.Execute(learn, ActionOrigin.Desk).Message;
                break;
        }
        if (words is { Length: > 0 }) StatusMessage = words;
        return words;
    }

    /// <summary>EDIT SAFE open before a menu changes a picture, so the change can never go live by itself.</summary>
    internal void EnsureEditSafe()
    {
        if (!_services.Sandbox.Active) IsSandboxActive = true;
    }

    /// <summary>The desk after a menu moved something: the editors, the wall, the tallies.</summary>
    private void AfterMenuChange()
    {
        Raise(nameof(IsSandboxActive));
        RebuildEditTargets();
        RefreshSwitcherTiles();
        RefreshTallies();
        Raise(nameof(ActivePattern));
    }

    /// <summary>A page of the rail, with the item the menu named selected there.</summary>
    internal string? GoTo(MenuRoute route)
    {
        if (Shell.Pages.All(p => p.Header != route.Page)) return $"No page called '{route.Page}'.";
        var item = route.Item;
        switch (route.Page)
        {
            case "Screens":
            {
                var id = ContentTargets.IsCanvasKey(item) ? ContentTargets.Members(item).FirstOrDefault() ?? "" : item;
                var placement = State.Output.Placements.FirstOrDefault(p => p.ScreenId == id);
                if (placement is not null) Screens.SelectedPlacement = placement;
                break;
            }
            case "Pattern":
            case "Layers":
                if (item.Length > 0) EditTarget = EditTargets.FirstOrDefault(t => t.ScreenId == item) ?? EditTargets[0];
                break;
            case "Cues":
            {
                var cue = CueStacks.AllActions(State).Select(x => x.Cue).FirstOrDefault(c => c.Id == item)
                          ?? State.Stacks.SelectMany(s => s.Cues).FirstOrDefault(c => c.Id == item);
                if (cue is not null)
                {
                    OpenCueInEditor(cue);
                    return $"Cue {cue.Number} in the editor.";
                }
                break;
            }
            case "Lower thirds":
            {
                var design = State.LowerThirds.Find(item);
                if (design is not null) SelectedLowerThird = design;
                var entry = State.LowerThirds.FindEntry(item);
                if (entry is not null) SelectedEntry = entry;
                break;
            }
        }
        SelectPage(Shell.IndexOf(route.Page));
        return $"{route.Page} page.";
    }

    /// <summary>The question, with its facts, to the assistant — asked at once when a key is saved, typed in and waiting otherwise.</summary>
    internal string? AskAssistant(string question)
    {
        if (question.Length == 0) return null;
        SelectPage(Shell.IndexOf("Assistant"));
        if (!Assistant.HasKey)
        {
            Assistant.Input = question;
            return "The question is typed in on the Assistant page — save an Anthropic API key there to ask it.";
        }
        _ = Assistant.AskAsync(question);
        return "Asked — the answer comes to the Assistant page.";
    }
}
