using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Effects;
using Patterns.Core.Model;
using Patterns.Core.Ndi;
using Patterns.Core.Particles;
using Patterns.Core.Media;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Patterns.Core.LowerThirds;

namespace Patterns.App.ViewModels;

public sealed partial class MainViewModel
{
    // ---- presenter click-through -------------------------------------------

    /// <summary>Advances the presenter steps and applies the step's look. False = no move.</summary>
    public bool PresenterAdvance(int delta) => _services.Actions.PresenterAdvance(delta, ActionOrigin.Desk);

    public string PresenterStepText
    {
        get
        {
            // A deck on air is the click-through: its page first, the list after it.
            if (_services.DeckOnAir() is { PageCount: > 0 } deck)
            {
                var ends = MediaLocator.FindActiveMedia(_services.AirState, MediaSource.Deck)?.DeckEndsWithGo ?? true;
                return deck.AtEnd
                    ? $"Deck: the last page ({deck.PageCount}){(ends ? " — the next click GOes the standby cue" : "")}"
                    : $"Deck: page {deck.Page} of {deck.PageCount} — NEXT turns it";
            }
            var clicker = CueStacks.Clicker(State);
            var rt = _services.Cues.For(clicker);
            var count = clicker.Cues.Count;
            if (count == 0) return "No clicker cues yet — add them on the Cues page.";
            if (rt.CurrentIndex < 0) return $"Ready — {count} cue{(count == 1 ? "" : "s")}, click to start.";
            var cue = rt.CurrentIndex < count ? clicker.Cues[rt.CurrentIndex] : null;
            return cue is null ? $"Cue {rt.CurrentIndex + 1} of {count}" : $"Cue {rt.CurrentIndex + 1} of {count}: {cue.Name}";
        }
    }

    private string _progressionSeen = "";

    /// <summary>
    /// The Show panel's PROGRESSION line — where the show goes next by itself or by a click, in one
    /// read: the clicker list's place (or a deck's page), an auto-follow counting down, the playlist's part.
    /// </summary>
    public string ProgressionText
    {
        get
        {
            var parts = new List<string>(3) { PresenterStepText };
            var follow = _services.CueStack.FollowText();
            if (follow.Length > 0) parts.Add(follow);
            var playlist = PlaylistStatus;
            if (playlist.Length > 0 && !playlist.StartsWith("Playlist idle", StringComparison.OrdinalIgnoreCase))
            {
                parts.Add(playlist);                                                 // a part playing: where it is and what is left
            }
            return string.Join("  ·  ", parts);
        }
    }

    // ---- the caller's VT clock: the clip on air, what is left, the rehearsal's skip --------------

    private string _videoClockTag = "";
    private string _videoClockName = "";
    private string _videoClockTimes = "";
    private string _videoClockCall = "";
    private double _videoClockFraction;
    private bool _videoClockOut;
    private bool _hasVideoClock;

    /// <summary>"VT", "AUDIO", "STINGER CLIP", "PLAYLIST" — what kind of clip the clock reads.</summary>
    public string VideoClockTag { get => _videoClockTag; private set => Set(ref _videoClockTag, value); }

    /// <summary>The clip's file name.</summary>
    public string VideoClockName { get => _videoClockName; private set => Set(ref _videoClockName, value); }

    /// <summary>"1:02 / 3:30 · 2:28 left", "1:02 · loop", "ended".</summary>
    public string VideoClockTimes { get => _videoClockTimes; private set => Set(ref _videoClockTimes, value); }

    /// <summary>"OUT IN 7" in the clip's last ten seconds; empty before that.</summary>
    public string VideoClockCall { get => _videoClockCall; private set => Set(ref _videoClockCall, value); }

    /// <summary>How far through the clip is, 0–1 — the bar under the words.</summary>
    public double VideoClockFraction { get => _videoClockFraction; private set => Set(ref _videoClockFraction, value); }

    /// <summary>The clip is in its last ten seconds and will end: the row goes red.</summary>
    public bool VideoClockOut { get => _videoClockOut; private set => Set(ref _videoClockOut, value); }

    /// <summary>A clip is on air: the panel shows the clock.</summary>
    public bool HasVideoClock { get => _hasVideoClock; private set => Set(ref _hasVideoClock, value); }

    /// <summary>The one line: "VT sponsor.mp4 · 1:02 / 3:30 · 2:28 left".</summary>
    public string VideoClockText => HasVideoClock ? $"{VideoClockTag} {VideoClockName} · {VideoClockTimes}" : "";

    /// <summary>Every second: the reading, and only the words that changed are raised — the panel never flinches for a clip that is not moving.</summary>
    private void RefreshVideoClock()
    {
        var r = _services.VideoOnAir();
        if (r is null)
        {
            HasVideoClock = false;
            VideoClockTag = "";
            VideoClockName = "";
            VideoClockTimes = "";
            VideoClockCall = "";
            VideoClockFraction = 0;
            VideoClockOut = false;
            return;
        }
        VideoClockTag = VideoClock.Tag(r);
        VideoClockName = r.Name;
        VideoClockTimes = VideoClock.Times(r);
        VideoClockCall = VideoClock.Call(r);
        VideoClockFraction = r.Fraction;
        VideoClockOut = r.InLast(VideoClock.OutWarningSeconds);
        HasVideoClock = true;
    }

    /// <summary>The clicker list's arm — a runtime chip, never saved: the app always opens disarmed.</summary>
    // ---- the shell: five groups on the rail, a page strip, the PREP · SHOW · RUN selector ----

    private int _page = Shell.PanelPage;
    private ShellGroup _group = ShellGroup.Show;
    private int _lastBuildPage = Shell.PanelPage;
    private readonly Dictionary<ShellGroup, int> _lastPage = new() { [ShellGroup.Show] = Shell.PanelPage };

    /// <summary>The selected page as the window's TabControl index (two-way: a test picking a tab by header lands here too).</summary>
    public int SelectedPageIndex
    {
        get => _page;
        set => SelectPage(value);
    }

    public ShellGroup SelectedGroup => _group;

    /// <summary>The group buttons on the rail.</summary>
    public IReadOnlyList<GroupChip> GroupStrip => Shell.Groups.Select(g => new GroupChip(g.Group, g.Label, g.Hue, g.Hint, g.Group == _group)).ToList();

    /// <summary>The pages of the current group, as the strip shows them.</summary>
    public IReadOnlyList<PageChip> PageStrip => Shell.Pages.Where(p => p.Group == _group).Select(p => new PageChip(p.Index, p.Header, p.Hue, p.Index == _page)).ToList();

    public string GroupHint => Shell.Info(_group).Hint;

    /// <summary>
    /// Select a page. The Run page is the Run layout; leaving it is refused while the caller's
    /// stack is armed (the strip and the tab snap back), so a stray click cannot take the
    /// surface away mid-show.
    /// </summary>
    public void SelectPage(int index)
    {
        if (index < 0 || index >= Shell.Pages.Count) return;
        var run = index == Shell.RunPage;
        if (!run && _isRunLayout && _services.CueStack.Armed)
        {
            StatusMessage = "Disarm the cue stack before leaving the Run surface.";
            RaiseShell();
            // The TabControl that asked is mid-write on its two-way binding, which ignores a
            // source change raised inside that write: snap it back once the write has finished.
            Dispatcher.UIThread.Post(() =>
            {
                Raise(nameof(SelectedPageIndex));
                RaiseShell();
            });
            return;
        }
        var page = Shell.Pages[index];
        _page = index;
        _lastPage[page.Group] = index;
        if (!run) _lastBuildPage = index;
        _group = page.Group;
        // The switch itself is guarded: a page that fails as it comes in (a binding, the desk's
        // layout, the Run surface's refresh) is logged and contained, and the desk stays on the
        // page it moved to rather than the process ending — the round-15 crash between menus.
        Patterns.App.Services.UiFaults.Guard(() =>
        {
            Raise(nameof(SelectedPageIndex));
            SetRunLayout(run);
            RaiseShell();
            Raise(nameof(PageWantsRoom));
            RefreshPopOut();   // the settings column follows the page: open for a selection here, closed elsewhere
            // The Multiview page reads the rig and what every output is doing: level on arrival.
            if (page.Header == "Multiview") RefreshWallDestinations();
        }, $"the switch to {page.Header}");
    }

    /// <summary>A group button: the group's last page, or its first; SHOW pressed while in Run goes to the panel.</summary>
    public void SelectGroup(ShellGroup group)
    {
        var index = _lastPage.TryGetValue(group, out var last) ? last : Shell.FirstPage(group);
        if (group == _group && _isRunLayout) index = Shell.PanelPage;
        SelectPage(index);
    }

    public RelayCommand<ShellGroup> SelectGroupCommand { get; private set; } = null!;
    public RelayCommand<int> SelectPageCommand { get; private set; } = null!;

    /// <summary>Open a page by its name — the rail's foot, and anywhere a page number would go stale.</summary>
    public RelayCommand<string> SelectPageByNameCommand { get; private set; } = null!;

    private RelayCommand<string>? _openPage;

    /// <summary>A page by its header — the pointer buttons on the Pattern page (OPEN FRACTALS, OPEN PARTICLES) use it, so a moved page never breaks them.</summary>
    public RelayCommand<string> OpenPageCommand => _openPage ??= new RelayCommand<string>(header =>
    {
        if (header is null || Shell.Pages.All(p => p.Header != header)) return;
        SelectPage(Shell.IndexOf(header));
    });

    private RelayCommand<PatternKind>? _usePatternKind;

    /// <summary>USE IT on a studio page: makes that kind the editing target's pattern, so the studio shows live in the preview.</summary>
    public RelayCommand<PatternKind> UsePatternKindCommand => _usePatternKind ??= new RelayCommand<PatternKind>(kind =>
    {
        if (ActivePattern.Kind == kind) return;
        _services.BulkEdit(() => ActivePattern.Kind = kind);
        StatusMessage = $"{Lists.PatternKinds.First(k => Equals(k.Value, kind)).Label} is the pattern now — the studio shows live in the preview.";
    });

    /// <summary>PREP · SHOW · RUN in the header: the first two are the show mode, RUN is the layout.</summary>
    public bool IsPrepSelected => !_isRunLayout && IsPrepMode;
    public bool IsShowSelected => !_isRunLayout && !IsPrepMode;
    public bool IsRunSelected => _isRunLayout;

    public RelayCommand SelectPrepCommand { get; private set; } = null!;
    public RelayCommand SelectShowCommand { get; private set; } = null!;
    public RelayCommand SelectRunCommand { get; private set; } = null!;

    /// <summary>Leave the Run layout for the last Build page; false when the armed stack refuses it.</summary>
    private bool LeaveRun()
    {
        if (!_isRunLayout) return true;
        SelectPage(_lastBuildPage);
        return !_isRunLayout;
    }

    private void RaiseShell()
    {
        Raise(nameof(SelectedGroup));
        Raise(nameof(GroupStrip));
        Raise(nameof(PageStrip));
        Raise(nameof(GroupHint));
        Raise(nameof(IsPrepSelected));
        Raise(nameof(IsShowSelected));
        Raise(nameof(IsRunSelected));
    }

    /// <summary>The SHOW CONTROLS drawer beside the switcher.</summary>
    public ShowControls ShowControls { get; private set; } = null!;

    /// <summary>The Format picker for the Media page's capture device.</summary>
    public CaptureFormatPicker CaptureFormat { get; private set; } = null!;

    /// <summary>The Format picker for the PiP inset's capture device.</summary>
    public CaptureFormatPicker PipCaptureFormat { get; private set; } = null!;

    public string RunLayoutButtonText => _isRunLayout ? "EXIT RUN" : "RUN";

    public RelayCommand ToggleRunLayoutCommand { get; private set; } = null!;

    private Views.RunWindow? _runWindow;

    /// <summary>The Run surface as a second window for a caller's own monitor.</summary>
    public RelayCommand PopOutRunCommand { get; private set; } = null!;

    /// <summary>The open pop-out, for tests and the warning about output displays.</summary>
    public Views.RunWindow? RunWindow => _runWindow;

    // ---- switcher (program / preview, the wall, sandbox) --------------------

    public ObservableCollection<SwitcherTile> SwitcherTiles { get; } = new();

    private string? _selectedTargetId;
    private SKSizeI _selectedTargetSize = Rig.DefaultTargetSize;
    private string _selectedTargetLabel = "PGM";

    /// <summary>The content target the big PROGRAM and PREVIEW panes show (null = the program bus).</summary>
    public string? SelectedTargetId => _selectedTargetId;

    /// <summary>Its real pixel size — the panes are true miniatures of it, letterboxed to fit.</summary>
    public SKSizeI SelectedTargetSize
    {
        get => _selectedTargetSize;
        private set
        {
            if (Set(ref _selectedTargetSize, value)) Raise(nameof(SelectedTargetRatio));
        }
    }

    public double SelectedTargetRatio =>
        _selectedTargetSize.Height > 0 ? (double)_selectedTargetSize.Width / _selectedTargetSize.Height : 16.0 / 9.0;

    /// <summary>"PGM", "A · Main wall" or "2 · Stage left" — the pane headers name what they show.</summary>
    public string SelectedTargetLabel { get => _selectedTargetLabel; private set => Set(ref _selectedTargetLabel, value); }

    public string SelectedTargetSizeText => $"{_selectedTargetSize.Width}×{_selectedTargetSize.Height}";

    /// <summary>Points the panes (and the preview pipeline) at a target; the editors are set separately.</summary>
    private void SelectTarget(string? targetId)
    {
        _selectedTargetId = targetId;
        _services.PreviewScreenId = targetId; // the preview resolves own-pattern vs program itself
        var tile = SwitcherTiles.FirstOrDefault(t => t.TargetId == targetId);
        SelectedTargetSize = tile?.Size ?? Rig.TargetSize(State, _services.Screens.All, targetId);
        SelectedTargetLabel = tile is null || tile.IsProgramTile ? "PGM" : tile.Title;
        Raise(nameof(SelectedTargetId));
        Raise(nameof(SelectedTargetSizeText));
        RefreshSwitcherTiles();
    }

    /// <summary>How many targets the next CUT / TAKE leaves alone — shown beside the buttons.</summary>
    /// <summary>Tiles the next CUT / TAKE leaves alone: un-armed, or locked (a confidence or info screen).</summary>
    public int HeldCount => SwitcherTiles.Count(t => !t.IsProgramTile && (!t.IsArmed || t.IsLocked));

    public bool AnyHeld => HeldCount > 0;

    public string TakeScopeText => HeldCount is var n && n > 0 ? $"{n} held" : "";

    private void RefreshTakeScope()
    {
        Raise(nameof(HeldCount));
        Raise(nameof(AnyHeld));
        Raise(nameof(TakeScopeText));
    }

    /// <summary>The desk's BLACKOUT toggles go through the action layer, so they are journaled like every other origin.</summary>
    public bool IsBlackout
    {
        get => State.Blackout;
        set
        {
            if (value == State.Blackout) return;
            _services.Actions.Execute(value ? ShowActionKind.BlackoutOn : ShowActionKind.BlackoutOff, ActionOrigin.Desk);
            Raise(nameof(IsBlackout));
        }
    }

    /// <summary>The Run area's wall tiles as vertical title bars — the tally and the name on their side — so the stack has the room (the show remembers it).</summary>
    public bool IsRunWallCollapsed
    {
        get => State.Desk.RunWallCollapsed;
        set
        {
            if (State.Desk.RunWallCollapsed == value) return;
            State.Desk.RunWallCollapsed = value;
            Raise(nameof(IsRunWallCollapsed));
            Raise(nameof(RunWallToggleText));
        }
    }

    public string RunWallToggleText => IsRunWallCollapsed ? "◂ EXPAND TILES" : "▸ COLLAPSE TILES";

    /// <summary>The page takes the room and the screens reduce to a strip on the right (the show remembers it).</summary>
    public bool WideWorkArea
    {
        get => State.Desk.WideWorkArea;
        set
        {
            if (State.Desk.WideWorkArea == value) return;
            State.Desk.WideWorkArea = value;
            Raise(nameof(WideWorkArea));
        }
    }

    /// <summary>
    /// The pages' explanations inline (the show remembers it). Off, they sit behind ? TIPS on
    /// the page strip and the controls have the room.
    /// </summary>
    public bool ShowHints
    {
        get => State.Desk.ShowHints;
        set
        {
            if (State.Desk.ShowHints == value) return;
            State.Desk.ShowHints = value;
            Raise(nameof(ShowHints));
        }
    }

    /// <summary>The EDIT toggle: on = open the sandbox, off = discard. CUT/TAKE close it too.</summary>

    public bool IsSandboxActive
    {
        get => _services.Sandbox.Active;
        set
        {
            if (value == _services.Sandbox.Active) return;
            if (value)
            {
                _services.Sandbox.Enter();
                ClearSendTargets(); // fresh session, fresh targets
                StatusMessage = "EDIT SAFE — build the look here; outputs keep showing the program.";
            }
            else
            {
                _services.Sandbox.Discard();
                StatusMessage = State.Switcher.EditSafeByDefault
                    ? "EDIT SAFE off — the preview now mirrors what is on air (edits go live)."
                    : "Sandbox discarded — outputs untouched.";
            }
            Raise(nameof(IsSandboxActive));
            RefreshSwitcherTiles(); // HOLD tallies only mean something while a send is being built
        }
    }

    /// <summary>The label a screen shows everywhere: the operator's name, or the OS one.</summary>
    public string LabelFor(ScreenPlacement placement, ScreenInfo? info = null)
        => Rig.LabelFor(placement, info ?? LiveInfo(placement));

    /// <summary>The stored (or automatic) name of the canvas containing a set of members.</summary>
    public string CanvasNameFor(IReadOnlyList<ScreenPlacement> members, string letter)
    {
        var key = CanvasNameConfig.KeyFor(members.Select(m => m.ScreenId));
        var stored = State.Output.CanvasNames.FirstOrDefault(c => c.MemberKey == key)?.Name;
        return string.IsNullOrWhiteSpace(stored) ? $"Canvas {letter}" : stored!;
    }

    /// <summary>Rebuilds the wall: PGM tile, then joined canvases, then single screens.</summary>
    public void RebuildSwitcherTiles(IReadOnlyList<ScreenInfo>? screens = null)
    {
        var known = screens ?? _services.Screens.All;
        var keepTargets = SwitcherTiles.Where(t => t.IsSendTarget && t.TargetId is not null)
            .Select(t => t.TargetId!).ToHashSet();
        var monitorOff = SwitcherTiles.Where(t => !t.IsMonitored).Select(t => t.TargetId ?? "").ToHashSet();
        var collapsed = State.Desk.CollapsedTiles.ToHashSet(StringComparer.Ordinal); // the show's own choice, tile by tile
        SwitcherTiles.Clear();

        var arming = _services.Arming;
        var geo = Rig.Geometry(State, known);
        var groups = CanvasGroups(known);
        var grouped = groups.SelectMany(g => g).Select(p => p.ScreenId).ToHashSet();
        var ordered = OrderedLivePlacements(known);
        var numberOf = ordered.Select((x, i) => (x.Placement.ScreenId, N: i + 1))
            .ToDictionary(x => x.ScreenId, x => x.N);
        var targets = new List<string>();

        SwitcherTiles.Add(new SwitcherTile(this, "PGM", null, Array.Empty<string>(),
            Rig.TargetSize(State, known, null),
            enabled: true, isSelected: _selectedTargetId is null, isOwn: false, isArmed: true,
            isCollapsed: collapsed.Contains(""))
        {
            IsMonitored = !monitorOff.Contains(""),
        });

        for (var i = 0; i < groups.Count; i++)
        {
            var letter = ((char)('A' + i)).ToString();
            var members = groups[i];
            var key = CanvasNameConfig.KeyFor(members.Select(m => m.ScreenId));
            targets.Add(key);
            var oneRole = members.Select(m => m.Role).Distinct().Count() == 1;
            SwitcherTiles.Add(new SwitcherTile(this,
                $"{letter} · {CanvasNameFor(members, letter)}",
                key,
                members.Select(m => m.ScreenId).ToList(),
                Rig.TargetSize(State, known, key),
                members.All(m => m.Enabled),
                isSelected: _selectedTargetId == key,
                isOwn: ContentTargets.UsesOwnPattern(State, key),
                isArmed: arming.IsArmed(key),
                isLocked: ScreenRoles.IsLocked(State, key),
                roleBadge: oneRole ? ScreenRoles.Badge(members[0].Role) : "",
                isCollapsed: collapsed.Contains(key),
                kindWord: oneRole ? GroupWord(members[0]) : "MIXED")
            {
                IsSendTarget = keepTargets.Contains(key),
                IsMonitored = !monitorOff.Contains(key),
            });
        }

        foreach (var (placement, info) in ordered)
        {
            var id = placement.ScreenId;
            if (grouped.Contains(id)) continue;
            targets.Add(id);
            SwitcherTiles.Add(new SwitcherTile(this,
                $"{numberOf[id]} · {LabelFor(placement, info)}",
                id,
                new[] { id },
                // The surface the content lays out on: the raster grown by the wall's dead strips, when it has any.
                geo.GapsOf(id).IsEmpty ? OutputWindowManager.EffectiveSize(placement, info) : geo.SizeOf(id),
                placement.Enabled,
                isSelected: _selectedTargetId == id,
                isOwn: placement.UseCustomPattern,
                isArmed: arming.IsArmed(id),
                isLocked: !placement.FollowsCues,
                roleBadge: ScreenRoles.Badge(placement.Role),
                mirrorNote: placement.MirrorOf.Length > 0 && ContentTargets.IsInRig(State, placement.MirrorOf)
                    ? "↳ " + geo.LabelFor(State, placement.MirrorOf)
                    : "",
                isCollapsed: collapsed.Contains(id),
                kindWord: GroupWord(placement))
            {
                IsSendTarget = keepTargets.Contains(id),
                IsMonitored = !monitorOff.Contains(id),
            });
        }

        arming.Prune(targets); // a screen that left the rig cannot hold a stale un-arm
        // Re-measure the selection (a join, a split or a rotation changes its shape); a
        // selection that left the rig falls back to the program.
        SelectTarget(_selectedTargetId is { } selected && targets.Contains(selected) ? selected : null);
        Raise(nameof(EditTargetBanner));
        RefreshTakeScope();
        // A join creates and destroys canvases, so the tile picker's targets move with the wall.
        RebuildMultiviewTargets();
        RebuildMirrorSources();
    }

    /// <summary>
    /// The group a screen is in, in the desk's words: NDI or STREAM for a feed screen, else its
    /// role — MAIN, CONF, INFO, REP. Every tile's foot line reads it.
    /// </summary>
    private static string GroupWord(ScreenPlacement placement)
        => placement.IsVirtual ? placement.VirtualKind
            : ScreenRoles.Badge(placement.Role) is { Length: > 0 } badge ? badge : "MAIN";

    /// <summary>
    /// The foot line of a tile: SETUP → Screens with this tile's screen selected (a canvas: its
    /// first screen) — where its group is set: the role, whether it follows cues, what it repeats.
    /// </summary>
    internal void OpenScreenSetup(SwitcherTile tile)
    {
        if (tile.TargetId is not { } target) return;
        var id = ContentTargets.IsCanvasKey(target) ? ContentTargets.Members(target).FirstOrDefault() ?? "" : target;
        var placement = State.Output.Placements.FirstOrDefault(p => p.ScreenId == id);
        if (placement is null) return;
        SelectedPlacement = placement;
        SelectPage(Shell.IndexOf("Screens"));
        StatusMessage = $"{tile.Title}: its group is set here — Role, whether it follows cues, and what it repeats.";
    }

    /// <summary>Live refresh without rebuilding (keeps ticks, focus and MON; called each poll and on every change that moves a tally).</summary>
    private void RefreshSwitcherTiles()
    {
        var byId = State.Output.Placements.ToDictionary(p => p.ScreenId);
        var live = _services.Outputs.IsLive && !State.Blackout;
        var building = _services.Sandbox.Active;
        foreach (var tile in SwitcherTiles)
        {
            if (tile.TargetId is not { } target)
            {
                tile.RefreshExternal(true, _selectedTargetId is null, isOwn: false, isArmed: true, onAir: live, held: false, locked: false);
                continue;
            }
            var members = tile.MemberIds.Select(id => byId.GetValueOrDefault(id)).Where(p => p is not null).ToList();
            var enabled = members.Count > 0 && members.All(p => p!.Enabled);
            var armed = _services.Arming.IsArmed(target);
            var locked = ScreenRoles.IsLocked(State, target);
            var black = _services.Bus.BlackTargets.Contains(target); // faded to black on its own: the audience sees black, not the picture
            tile.RefreshExternal(enabled, target == _selectedTargetId,
                ContentTargets.UsesOwnPattern(State, target), armed,
                onAir: live && enabled && !black, held: building && (!armed || locked), locked: locked, black: black,
                canSend: building);
        }
    }

    /// <summary>LOCK on a tile: the target keeps its picture through looks, cues, TAKE ALL and stingers; through the action layer, so it is journaled.</summary>
    internal void SetTileLocked(SwitcherTile tile, bool locked)
    {
        if (tile.TargetId is not { } target) return;
        if (ScreenRoles.IsLocked(State, target) == locked) return;
        SetLocked(target, locked);
    }

    /// <summary>SEND on a tile: the preview lands on this target alone as its own pattern; everything else stays.</summary>
    /// <summary>
    /// SEND on a tile: the preview's picture lands on that tile's PVW alone. The audience sees
    /// nothing — it is staged, the way a mixer holds a source on preview — and the next CUT or
    /// TAKE on that tile is what puts it up. The tile is focused as part of the press, so the
    /// FOCUSED scope beside CUT / TAKE means the tile you just staged.
    /// </summary>
    internal void SendSandboxToTile(SwitcherTile tile)
    {
        if (tile.TargetId is not { } target) return;
        if (!_services.Sandbox.Active)
        {
            StatusMessage = "Open EDIT SAFE and build the picture first — then SEND stages it on this tile.";
            return;
        }
        _services.Sandbox.SendToTargets(new[] { target }, toAir: false);
        ClearSendTargets();
        Raise(nameof(IsSandboxActive));
        RebuildEditTargets(); // the target now shows its own pattern — OWN lights up
        SelectTarget(target); // the hand chose this tile: FOCUSED now means this one
        StatusMessage = $"Staged on {tile.Title} — its PVW shows it and the audience does not. CUT or TAKE (FOCUSED) puts it up.";
    }

    /// <summary>
    /// → PVW on a tile: the picture this target shows on air — its own, its source's when it repeats
    /// one, else the program — into the sandboxed preview, through the action layer (journaled). The
    /// editors work on the program (the preview) and the big panes show PGM and the preview; the
    /// air is untouched, and EDIT SAFE opens first when it was off.
    /// </summary>
    internal void LoadTileIntoPreview(SwitcherTile tile)
    {
        // The PGM tile has no target id of its own: an empty target names the programme, which is
        // the one picture an operator most often wants back — "put what is on air into the preview
        // so I can change it and take it again".
        var target = tile.TargetId ?? "";
        var result = Report(_services.Actions.Execute(ShowActionKind.ScreenToPreview, ActionOrigin.Desk, target));
        if (!result.Ok) return;
        Raise(nameof(IsSandboxActive));
        RebuildEditTargets();
        EditTarget = EditTargets[0]; // the program: the preview is what is edited now, not the screen it came from
        // The tile the picture came from stays focused, so a FOCUSED take puts it back exactly
        // where it came from. Pulling from the PGM tile focuses nothing, which is the programme —
        // and FOCUSED then means every armed screen, as it always has.
        SelectTarget(tile.TargetId);
        RefreshSwitcherTiles();
    }

    /// <summary>
    /// → THIS SCREEN on the Show panel: the chosen look's picture lands on this target alone as its own
    /// pattern, live, through the action layer (journaled, the same path a cue or SCREEN n LOOK takes).
    /// </summary>
    internal void SendLookToTile(SwitcherTile tile, LookConfig? look)
    {
        if (tile.TargetId is not { } target) return;
        if (look is null)
        {
            StatusMessage = $"Pick a look for {tile.Title} first — then → THIS SCREEN puts it there alone.";
            return;
        }
        var result = Report(_services.Actions.Execute(ShowActionKind.ScreenLook, ActionOrigin.Desk, target, look.Id));
        if (result.Ok) RebuildEditTargets(); // OWN lights up on the tile and the editors see the new assignment
    }

    /// <summary>PROGRAM on the Show panel: this target drops its own picture and follows the program again, live.</summary>
    internal void SendProgramToTile(SwitcherTile tile)
    {
        if (tile.TargetId is not { } target) return;
        var result = Report(_services.Actions.Execute(ShowActionKind.ScreenProgram, ActionOrigin.Desk, target));
        if (!result.Ok) return;
        RebuildEditTargets();
        SelectTarget(target);
    }

    /// <summary>A tile's on/off switch: every member screen follows, pinned as a user choice.</summary>
    internal void SetTileEnabled(SwitcherTile tile, bool enabled)
    {
        foreach (var id in tile.MemberIds)
        {
            var placement = State.Output.Placements.FirstOrDefault(p => p.ScreenId == id);
            if (placement is null) continue;
            placement.Enabled = enabled;
            placement.UserPinned = true;
        }
        RefreshSwitcherTiles();
    }

    /// <summary>ARM off keeps the target's picture through the next CUT / TAKE. Runtime only — a show always opens fully armed.</summary>
    internal void SetTileArmed(SwitcherTile tile, bool armed)
    {
        if (tile.TargetId is not { } target) return;
        _services.Arming.Set(target, armed);
        StatusMessage = armed
            ? $"{tile.Title} armed — the next CUT / TAKE changes it."
            : $"{tile.Title} held — it keeps its picture through the next CUT / TAKE.";
    }

    /// <summary>
    /// ▸ / ▾ on a tile: this tile alone as its vertical title bar, or open again — the show remembers
    /// it (DeskLayoutConfig.CollapsedTiles, by target id; "" is PGM). The Run area's COLLAPSE TILES
    /// is the whole wall at once and leaves these choices in place under it.
    /// </summary>
    internal void SetTileCollapsed(SwitcherTile tile, bool collapsed)
    {
        var key = tile.TargetId ?? "";
        var list = State.Desk.CollapsedTiles;
        if (collapsed)
        {
            if (!list.Contains(key)) list.Add(key);
            StatusMessage = $"{tile.Title} collapsed to its title bar — ▾ on the bar opens it again.";
        }
        else
        {
            list.Remove(key);
            StatusMessage = $"{tile.Title} open again.";
        }
    }

    /// <summary>OWN on gives the target its own pattern (a copy of the program, so nothing jumps) and hands it to the editors.</summary>
    internal void SetTileOwn(SwitcherTile tile, bool on)
    {
        if (tile.TargetId is not { } target) return;
        if (ContentTargets.UsesOwnPattern(State, target) == on) return;
        _services.BulkEdit(() =>
        {
            if (on) ContentTargets.EnsureAssignment(State, target);
            ContentTargets.SetOwnPattern(State, target, on);
        });
        RebuildEditTargets();
        if (on)
        {
            EditTarget = EditTargets.FirstOrDefault(t => t.ScreenId == target) ?? EditTargets[0];
            StatusMessage = $"{tile.Title} now shows its own pattern — the editors work on it.";
        }
        else
        {
            SelectTarget(target);
            StatusMessage = $"{tile.Title} follows the program again.";
        }
    }

    /// <summary>Big banner over the editor: what the panels currently change.</summary>
    public string EditTargetBanner
    {
        get
        {
            if (_editTarget.ScreenId is not { } id) return "EDITING: PROGRAM — every target without its own pattern";
            if (ContentTargets.IsCanvasKey(id))
            {
                var groups = CanvasGroups();
                for (var i = 0; i < groups.Count; i++)
                {
                    if (CanvasNameConfig.KeyFor(groups[i].Select(m => m.ScreenId)) != id) continue;
                    var letter = ((char)('A' + i)).ToString();
                    return $"EDITING: CANVAS {letter} · {CanvasNameFor(groups[i], letter)} (its own pattern)";
                }
                return "EDITING: PROGRAM";
            }
            var placement = State.Output.Placements.FirstOrDefault(p => p.ScreenId == id);
            if (placement is null) return "EDITING: PROGRAM";
            var ordered = OrderedLivePlacements();
            var n = ordered.FindIndex(x => x.Placement.ScreenId == placement.ScreenId) + 1;
            return $"EDITING: SCREEN {n} · {LabelFor(placement)} (its own pattern)";
        }
    }

    // ---- freeze, the timed fade, the previous look ------------------------------------

    private bool _frozenSeen;
    private string _previousLookSeen = "";
    private double _fadeSeconds = 2;

    /// <summary>FREEZE: every output holds its frame — a runtime flag on the bus, through the action layer like every switch.</summary>
    public bool IsFrozen
    {
        get => _services.Bus.Frozen;
        set
        {
            if (value == _services.Bus.Frozen) return;
            _services.Actions.Execute(value ? ShowActionKind.FreezeOn : ShowActionKind.FreezeOff, ActionOrigin.Desk);
            _frozenSeen = _services.Bus.Frozen;
            Raise(nameof(IsFrozen));
        }
    }

    /// <summary>The seconds the Show panel's FADE TO BLACK and FADE UP take (a desk setting, not the show's).</summary>
    public double FadeSeconds { get => _fadeSeconds; set => Set(ref _fadeSeconds, Math.Clamp(double.IsFinite(value) ? value : 2, 0.1, 60)); }

    /// <summary>The panel's seconds as the action's value — the same words a cue's step and the wire's line carry.</summary>
    private string FadeSecondsText() => _fadeSeconds.ToString("0.###", System.Globalization.CultureInfo.InvariantCulture);

    /// <summary>Where the Show panel's FADE TO BLACK and FADE UP land, as the picker lists it; the words are what the wire takes.</summary>
    public sealed record FadeScopeChoice(string Label, string Words)
    {
        public override string ToString() => Label;
    }

    /// <summary>Every screen (the blackout with a fade), the focused tile, the ticked tiles, the ticked groups — in that order; the first is the default.</summary>
    public IReadOnlyList<FadeScopeChoice> FadeScopes { get; } = new[]
    {
        new FadeScopeChoice("EVERY SCREEN", ""),
        new FadeScopeChoice("THE FOCUSED SCREEN", FadeScope.Focused.Words),
        new FadeScopeChoice("THE TICKED SCREENS", FadeScope.Ticked.Words),
        new FadeScopeChoice("THE TICKED GROUPS", FadeScope.Groups.Words),
    };

    private FadeScopeChoice? _selectedFadeScope;

    /// <summary>The picker's choice; never null — an emptied picker falls back to every screen.</summary>
    public FadeScopeChoice SelectedFadeScope
    {
        get => _selectedFadeScope ?? FadeScopes[0];
        set => Set(ref _selectedFadeScope, value ?? FadeScopes[0]);
    }

    /// <summary>
    /// Where the wall's CUT and TAKE land: every armed screen (ARM and LOCK on the tiles decide), the
    /// focused tile, the ticked tiles, or the ticked groups — everything outside the choice keeps
    /// its picture like an un-armed tile, and the next full send lifts it. The first is the default.
    /// </summary>
    public IReadOnlyList<FadeScopeChoice> TakeScopes { get; } = new[]
    {
        new FadeScopeChoice("ALL ARMED", ""),
        new FadeScopeChoice("FOCUSED", FadeScope.Focused.Words),
        new FadeScopeChoice("TICKED", FadeScope.Ticked.Words),
        new FadeScopeChoice("TICKED GROUPS", FadeScope.Groups.Words),
    };

    private FadeScopeChoice? _selectedTakeScope;

    /// <summary>The CUT / TAKE picker's choice; never null — an emptied picker falls back to every armed screen.</summary>
    public FadeScopeChoice SelectedTakeScope
    {
        get => _selectedTakeScope ?? TakeScopes[0];
        set => Set(ref _selectedTakeScope, value ?? TakeScopes[0]);
    }

    /// <summary>The look LOOK BACK returns to, by name ("" = none yet).</summary>
    public string PreviousLookName => LookService.Find(State, _services.PreviousAirLookId)?.Name ?? "";

    public string LookBackText => PreviousLookName.Length > 0 ? $"◀ BACK TO '{PreviousLookName}'" : "◀ PREVIOUS LOOK";

    // ---- the show file's earlier versions ---------------------------------------------

    /// <summary>One kept version of the show file, as the Machine page lists it.</summary>
    public sealed record BackupChoice(string Label, string Path)
    {
        public override string ToString() => Label;
    }

    public System.Collections.ObjectModel.ObservableCollection<BackupChoice> BackupChoices { get; } = new();

    private BackupChoice? _selectedBackup;

    public BackupChoice? SelectedBackup { get => _selectedBackup; set => Set(ref _selectedBackup, value); }

    private string _backupsSummary = "";

    /// <summary>"The previous save and 12 earlier versions, the oldest from Tue 14:02, in …\backups".</summary>
    public string BackupsSummary { get => _backupsSummary; private set => Set(ref _backupsSummary, value); }

    /// <summary>Reads the kept versions: the previous save first, then the timed copies, newest first.</summary>
    public void RefreshBackups()
    {
        var store = _services.Store;
        var keep = _selectedBackup?.Path;
        BackupChoices.Clear();
        if (store.PreviousSavePath is { } bak)
        {
            BackupChoices.Add(new BackupChoice($"The previous save — {File.GetLastWriteTime(bak):ddd HH:mm:ss}", bak));
        }
        var kept = store.ListBackups();
        foreach (var (when, path) in kept)
        {
            BackupChoices.Add(new BackupChoice($"{when:ddd d MMM HH:mm:ss}", path));
        }
        SelectedBackup = BackupChoices.FirstOrDefault(c => c.Path == keep) ?? BackupChoices.FirstOrDefault();
        BackupsSummary = BackupChoices.Count == 0
            ? "No earlier version yet — one is kept the first time the show file changes."
            : $"{(store.PreviousSavePath is null ? "" : "The previous save and ")}{kept.Count} earlier version{(kept.Count == 1 ? "" : "s")}"
              + (kept.Count > 0 ? $", the oldest from {kept[^1].When:ddd d MMM HH:mm}" : "")
              + $", in {store.BackupsDirectory}";
    }

    /// <summary>The selected version becomes the show — every list starts over, exactly as Load show does.</summary>
    private void RestoreBackup()
    {
        if (_selectedBackup is not { } choice) return;
        var loaded = _services.Store.LoadFrom(choice.Path);
        if (loaded is null)
        {
            StatusMessage = "That version could not be read.";
            return;
        }
        ApplyLoadedShow(loaded, $"Show restored: {choice.Label}");
        RefreshBackups();
    }

    private void OpenBackupsFolder()
    {
        try
        {
            Directory.CreateDirectory(_services.Store.BackupsDirectory);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo(_services.Store.BackupsDirectory) { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            StatusMessage = $"Could not open the folder: {ex.Message}";
        }
    }

    private string _stingerStatus = "Ready.";
    public string StingerStatus { get => _stingerStatus; private set => Set(ref _stingerStatus, value); }

    // ---- looks & cues -------------------------------------------------------

    private string _newLookName = "";
    public string NewLookName { get => _newLookName; set => Set(ref _newLookName, value); }

    private int _newLookHotkey;
    public int NewLookHotkey { get => _newLookHotkey; set => Set(ref _newLookHotkey, value); }

    public int[] HotkeySlots { get; } = Enumerable.Range(0, 13).ToArray();

    private string _nextCueText = "No cues scheduled.";
    public string NextCueText { get => _nextCueText; private set => Set(ref _nextCueText, value); }

    public List<string> LookNames => State.LooksAndCues.Looks.Select(l => l.Name).ToList();

    private void SaveLook()
    {
        var name = string.IsNullOrWhiteSpace(NewLookName) ? $"Look {State.LooksAndCues.Looks.Count + 1}" : NewLookName.Trim();
        // "Walk-in" and "walk-in" are the same look: the resolver is case-insensitive, so the save must be too.
        var existing = LookService.Find(State, name);
        var json = LookService.Capture(State);
        if (existing is not null)
        {
            existing.Json = json;
            if (NewLookHotkey > 0) existing.Hotkey = NewLookHotkey;
        }
        else
        {
            // A hotkey can only belong to one look.
            if (NewLookHotkey > 0)
            {
                foreach (var l in State.LooksAndCues.Looks.Where(l => l.Hotkey == NewLookHotkey))
                {
                    l.Hotkey = 0;
                }
            }
            State.LooksAndCues.Looks.Add(new LookConfig { Name = name, Hotkey = NewLookHotkey, Json = json });
        }
        NewLookName = "";
        NewLookHotkey = 0;
        StatusMessage = $"Look '{name}' saved.";
        Raise(nameof(LookNames));
    }

    /// <summary>
    /// Fires a look to air — F-keys, look buttons, scheduled cues, presenter steps and
    /// remotes all land here. EDIT SAFE protects what you are <em>building</em>, not what you
    /// <em>fire</em>: with the sandbox open the audience gets the look and the preview keeps
    /// showing the operator's in-progress edit. Use "→ PVW" to load one into the editors.
    /// </summary>
    public void ApplyLook(LookConfig look) => _services.Actions.ApplyLook(look, ActionOrigin.Desk);

    /// <summary>
    /// Every action, from every origin, lands here once it has run: the status line and the
    /// editor resyncs live in one place instead of in each caller.
    /// </summary>
    private void OnActionPerformed(ShowAction action, ActionOrigin origin, ActionResult result)
    {
        if (result.Message.Length > 0) StatusMessage = result.Message;
        RefreshTallies(); // a look from anywhere, a TAKE, a fired stinger: the desk lights up at once
        switch (action.Kind)
        {
            case ShowActionKind.ApplyLook:
            case ShowActionKind.ApplyLookHotkey:
                // Unsandboxed, the look landed in the live model the editors bind to.
                if (result.Ok && !_services.Sandbox.Active)
                {
                    RebuildEditTargets();
                    Raise(nameof(ActivePattern));
                }
                break;
            case ShowActionKind.ApplyLookToPreview:
                if (result.Ok)
                {
                    RebuildEditTargets();
                    Raise(nameof(ActivePattern));
                }
                break;
            case ShowActionKind.DeckNext:
            case ShowActionKind.DeckPrev:
            case ShowActionKind.DeckPage:
                RefreshDeck();
                break;
            case ShowActionKind.PresenterNext:
            case ShowActionKind.PresenterPrev:
            case ShowActionKind.ListGo:
            case ShowActionKind.ListBack:
            case ShowActionKind.CueFire:
                RefreshDeck();
                Raise(nameof(PresenterStepText));
            Raise(nameof(ProgressionText));
                if (result.Ok && !_services.Sandbox.Active)
                {
                    RebuildEditTargets();
                    Raise(nameof(ActivePattern));
                }
                break;
            case ShowActionKind.ListArm:
            case ShowActionKind.ListDisarm:
            case ShowActionKind.ListReset:
                Raise(nameof(ClickerArmed));
                Raise(nameof(PresenterStepText));
            Raise(nameof(ProgressionText));
                break;
            case ShowActionKind.Take:
            case ShowActionKind.Cut:
                if (result.Ok)
                {
                    ClearSendTargets();
                    Raise(nameof(IsSandboxActive));
                    RebuildEditTargets(); // a scoped send pins / lifts own patterns — OWN follows
                }
                break;
            case ShowActionKind.OutputsOn:
            case ShowActionKind.OutputsOff:
                RefreshOutputsStatus();
                break;
            case ShowActionKind.PlaylistPart:
                RaisePlaylistSection();
                break;
        }
    }

    /// <summary>After the watchdog restored the air content into the model, resync the editors.</summary>
    public void RefreshAfterRecovery()
    {
        RebuildEditTargets();
        Raise(nameof(ActivePattern));
    }

    /// <summary>Loads a look into the editors (the sandboxed preview) instead of putting it on air.</summary>
    public void ApplyLookToPreview(LookConfig look)
        => _services.Actions.Execute(ShowActionKind.ApplyLookToPreview, ActionOrigin.Desk, look.Id);

    // ---- the tally: which look is in use, which VOG, stinger or sting is playing ----------------

    private DispatcherTimer? _tallyTimer;

    /// <summary>
    /// Lights the rows and chips: the look on air (exactly, or edited since), the look loaded into
    /// the preview, and every VOG, stinger or effect sting playing right now. Runs on the status
    /// poll, after every action, on the stinger service's changes — and on its own 200 ms timer
    /// while something plays, so a surge's bar and a clip's seconds move.
    /// </summary>
    public void RefreshTallies()
    {
        // Chrome, not content: every field these write is [JsonIgnore] and reaches no sink, so
        // none of it publishes a snapshot — and none of it takes the version a look's own
        // transition is riding on.
        var live = false;
        _services.DeskEdit(() =>
        {
            RefreshLookTallies();
            live = RefreshStingerTallies() | RefreshLowerThirdTallies();
        });
        if (live && _tallyTimer is null)
        {
            var timer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(200) };
            timer.Tick += (_, _) =>
            {
                if (RefreshStingerTallies() | RefreshLowerThirdTallies()) return;
                timer.Stop();
                if (ReferenceEquals(_tallyTimer, timer)) _tallyTimer = null;
            };
            _tallyTimer = timer;
            timer.Start();
        }
        else if (!live && _tallyTimer is { } running)
        {
            running.Stop();
            _tallyTimer = null;
        }
    }

    private void RefreshLookTallies()
    {
        var looks = State.LooksAndCues.Looks;
        if (looks.Count == 0) return;

        // Program: the look last put on air, edited or not; with none recorded, the look whose picture this is.
        // One reading, shared with the wire: OnAir carries the "nothing recorded, so whichever look
        // this picture is" fallback the page has always had, and AirEdited compares it.
        var tally = _services.LookTally;
        var onAir = tally.OnAir();
        var airEdited = tally.AirEdited();

        // Preview: only a look loaded with → PVW, while the sandbox is open.
        LookConfig? inPreview = null;
        var previewEdited = false;
        if (_services.Sandbox.Active && _services.PreviewLookId is { Length: > 0 } previewId)
        {
            inPreview = looks.FirstOrDefault(l => l.Id == previewId);
            if (inPreview is not null) previewEdited = tally.FingerprintOf(inPreview) != tally.PreviewFingerprint();
        }

        foreach (var look in looks)
        {
            var air = ReferenceEquals(look, onAir);
            var pvw = ReferenceEquals(look, inPreview);
            look.IsOnAir = air;
            look.IsInPreview = pvw;
            look.TallyText = (air, pvw) switch
            {
                (true, true) => airEdited || previewEdited ? "PROGRAM · PREVIEW · EDITED" : "PROGRAM · PREVIEW",
                (true, false) => airEdited ? "PROGRAM · EDITED" : "PROGRAM",
                (false, true) => previewEdited ? "PREVIEW · EDITED" : "PREVIEW",
                _ => "",
            };
        }
    }

    /// <summary>Lights the library rows and the panel chips; true while anything is playing.</summary>
    private bool RefreshStingerTallies()
    {
        var stingers = _services.Stingers;
        var now = stingers.NowUtc();
        var clock = ShowClock.Seconds;
        var pulse = EffectImpulses.Current;
        var any = false;
        foreach (var item in State.Stingers.Items)
        {
            var on = false;
            var text = "";
            var progress = -1.0;
            if (item.IsPulse)
            {
                if (stingers.PulseId == item.Id && !pulse.IsNone && clock >= pulse.StartSeconds && clock < pulse.EndSeconds)
                {
                    on = true;
                    progress = Math.Clamp((clock - pulse.StartSeconds) / pulse.LengthSeconds, 0, 1);
                    text = $"SURGING · {pulse.EndSeconds - clock:0.0} s left";
                }
            }
            else if (stingers.SessionId == item.Id)
            {
                on = true;
                text = stingers.Holding ? "HOLDING" : $"ON AIR · {Math.Max(0, (now - stingers.SessionStartUtc).TotalSeconds):0} s";
            }
            else if (stingers.VogSoundId == item.Id)
            {
                on = true;
                text = $"ON AIR · {Math.Max(0, (now - stingers.VogSoundStartUtc).TotalSeconds):0} s";
            }
            item.IsOnAir = on;
            item.OnAirText = text;
            item.OnAirProgress = progress;
            any |= on;
        }
        return any;
    }

    /// <summary>F1–F12 from the main window or an output window. False = no look on that key.</summary>
    public bool ApplyLookHotkey(int slot) => _services.Actions.ApplyLookHotkey(slot, ActionOrigin.Keyboard);

    private void CheckCues()
    {
        // Cues fire to air whether or not the sandbox is open: the action layer targets the
        // program, so the schedule runs the show while the operator programs in safety.
        var now = DateTime.Now;
        _services.Actions.RunSchedule(now);
        NextCueText = ShowActions.NextScheduledText(State, now);
    }
}
