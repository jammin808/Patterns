using System.Collections.ObjectModel;
using Avalonia.Media.Imaging;
using Avalonia.Platform.Storage;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Rendering.Effects;
using Patterns.Core.Model;
using Patterns.Ndi;
using Patterns.Rendering.Particles;
using Patterns.Core.Media;
using Patterns.Rendering.Media;
using Patterns.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Patterns.Core.LowerThirds;
using Patterns.Rendering.LowerThirds;

namespace Patterns.App.ViewModels;

public sealed partial class MainViewModel
{
    // ---- layers and drags on the PREVIEW pane ---------------------------------------

    public EnumItem[] LayerSources => Lists.LayerSources;

    public RelayCommand<LayerConfig> BrowseLayerImageCommand { get; }
    public RelayCommand<LayerConfig> BrowseLayerVideoCommand { get; }

    /// <summary>The pattern the PREVIEW pane shows, in the live model: the target's own, its source's when it repeats one, else the program.</summary>
    public PatternConfig PreviewPattern
    {
        get
        {
            var id = _services.PreviewScreenId;
            if (id is null) return State.Pattern;
            id = ScreenRoles.ResolveMirror(State, id);
            if (!ContentTargets.UsesOwnPattern(State, id)) return State.Pattern;
            return State.Independent.FirstOrDefault(a => a.ScreenId == id)?.Pattern ?? State.Pattern;
        }
    }

    public static string DragName(HitKind kind) => kind switch
    {
        HitKind.Layer1 => "Layer 1",
        HitKind.Layer2 => "Layer 2",
        HitKind.Logo => "The logo",
        HitKind.Clock => "The clock",
        HitKind.Countdown => "The countdown",
        HitKind.Message => "The message",
        HitKind.Pip => "The PiP inset",
        HitKind.Weather => "The weather",
        HitKind.WebPage => "The web page",
        HitKind.Badge => "The Patterns badge",
        _ => "The element",
    };

    /// <summary>Where a draggable thing sits now: a layer's box (a share of the canvas) or an overlay's nudge from its anchor.</summary>
    public (double X, double Y) DragPlaceOf(HitKind kind)
    {
        var p = PreviewPattern;
        if (AnchoredOf(kind) is { } placed) return (placed.OffsetXPct, placed.OffsetYPct);
        return kind switch
        {
            HitKind.Layer1 => (p.Layer1.XPct, p.Layer1.YPct),
            HitKind.Layer2 => (p.Layer2.XPct, p.Layer2.YPct),
            _ => (0, 0),
        };
    }

    /// <summary>
    /// What the last preview frame drew for a kind, in the space it drew it in — set by the window,
    /// which owns the pipeline. Null before the first frame, and for anything not on the picture:
    /// the pixel fields are then quiet rather than lying about where something is.
    /// </summary>
    public Func<HitKind, PlaceEditor.PlacedBox?>? PreviewBox { get; set; }

    private PlaceEditor PlaceFor(HitKind kind)
        => new(() => AnchoredOf(kind), () => PreviewBox?.Invoke(kind), _services.BulkEdit);

    /// <summary>The place editors the overlay pages' pixel rows bind to — one per overlay, built once.</summary>
    public PlaceEditor ClockPlace => _clockPlace ??= PlaceFor(HitKind.Clock);
    public PlaceEditor LogoPlace => _logoPlace ??= PlaceFor(HitKind.Logo);
    public PlaceEditor MessagePlace => _messagePlace ??= PlaceFor(HitKind.Message);
    public PlaceEditor WeatherPlace => _weatherPlace ??= PlaceFor(HitKind.Weather);
    public PlaceEditor PipPlace => _pipPlace ??= PlaceFor(HitKind.Pip);
    public PlaceEditor CountdownPlace => _countdownPlace ??= PlaceFor(HitKind.Countdown);
    public PlaceEditor BadgePlace => _badgePlace ??= PlaceFor(HitKind.Badge);

    private PlaceEditor? _clockPlace, _logoPlace, _messagePlace, _weatherPlace, _pipPlace, _countdownPlace, _badgePlace;

    /// <summary>Every place editor, for the poll's once-a-second refresh.</summary>
    public IEnumerable<PlaceEditor> Places
    {
        get
        {
            yield return ClockPlace;
            yield return LogoPlace;
            yield return MessagePlace;
            yield return WeatherPlace;
            yield return PipPlace;
            yield return CountdownPlace;
            yield return BadgePlace;
        }
    }

    /// <summary>
    /// The overlay a hit names, or null when the thing dragged has no anchor — a layer's box is
    /// given as the canvas's own share, so it has nowhere to be re-anchored to.
    /// </summary>
    public IAnchored? AnchoredOf(HitKind kind)
    {
        var o = State.Overlays;
        return kind switch
        {
            HitKind.Logo => o.Logo,
            HitKind.Clock => o.Clock,
            HitKind.Countdown => State.Countdown,
            HitKind.Message => o.Message,
            HitKind.Pip => o.Pip,
            HitKind.Weather => o.Weather,
            HitKind.Badge => o.Badge,
            _ => null,
        };
    }

    /// <summary>
    /// A drag has ended: the same pixels, told from the nearest anchor. Nothing moves — the box is
    /// where it was dropped — but the Nudge sliders come back to counting from a corner or an edge
    /// that is still there at another size and on a canvas of another shape, instead of from an
    /// anchor the element has long since left behind. One publish for the pair.
    /// </summary>
    public void DragReanchor(HitKind kind, Anchor9 anchor, double x, double y)
    {
        if (AnchoredOf(kind) is not { } placed) return;
        BulkEdit(() =>
        {
            placed.Anchor = anchor;
            placed.OffsetXPct = x;
            placed.OffsetYPct = y;
        });
        // The page's Position picker, its pixel fields and RESET follow the drop at once rather
        // than at the next second's poll — the operator is looking at them as they let go.
        PlaceOf(kind)?.Refresh();
    }

    /// <summary>The place editor for a draggable overlay, or null for anything without one.</summary>
    private PlaceEditor? PlaceOf(HitKind kind) => kind switch
    {
        HitKind.Clock => ClockPlace,
        HitKind.Logo => LogoPlace,
        HitKind.Message => MessagePlace,
        HitKind.Weather => WeatherPlace,
        HitKind.Pip => PipPlace,
        HitKind.Countdown => CountdownPlace,
        HitKind.Badge => BadgePlace,
        _ => null,
    };

    /// <summary>
    /// Puts a draggable thing at a place (the same units <see cref="DragPlaceOf"/> reads); the
    /// model publishes, the panes follow. The pair lands as one edit: this runs on every pointer
    /// move, and two writes were two publishes — two copies of the section and two rounds of
    /// side effects — for one movement of the hand.
    /// </summary>
    public void DragPlace(HitKind kind, double x, double y)
    {
        if (AnchoredOf(kind) is { } placed)
        {
            BulkEdit(() =>
            {
                placed.OffsetXPct = x;
                placed.OffsetYPct = y;
            });
            return;
        }
        var p = PreviewPattern;
        switch (kind)
        {
            case HitKind.Layer1:
                BulkEdit(() => { p.Layer1.XPct = x; p.Layer1.YPct = y; });
                break;
            case HitKind.Layer2:
                BulkEdit(() => { p.Layer2.XPct = x; p.Layer2.YPct = y; });
                break;
        }
    }

    /// <summary>The Media page: sources, crop, playlist, live inputs, web pages and decks.</summary>
    public MediaPage Media { get; }

    private void BulkEdit(Action edit) => _services.BulkEdit(edit);

    /// <summary>Auto, or the service the operator named — for both page pickers.</summary>
    public EnumItem[] PageServices => Lists.PageServices;

    /// <summary>What a look does to the stream — the Looks page's picker.</summary>
    public EnumItem[] LookStreams => Lists.LookStreams;

    // ---- how one picture becomes the next -------------------------------------------------

    public EnumItem[] TransitionKinds => Lists.TransitionKinds;

    public EnumItem[] TransitionDirections => Lists.TransitionDirections;

    /// <summary>Only a wipe and a push travel, so only they ask which way.</summary>
    public bool TransitionHasDirection => State.Transition.Kind is TransitionKind.Wipe or TransitionKind.Push;

    public bool TransitionIsReactive => State.Transition.Kind == TransitionKind.Reactive;

    public bool TransitionIsDip => State.Transition.Kind == TransitionKind.Dip;

    /// <summary>A wipe and a matte have an edge to soften; the rest have none.</summary>
    public bool TransitionHasSoftness => State.Transition.Kind is TransitionKind.Wipe or TransitionKind.Reactive;

    /// <summary>What the chosen transition does, in a line — read before the show rather than during it.</summary>
    public string TransitionNote => State.Transition.Kind switch
    {
        TransitionKind.Dip => "Out through the colour and back, with the change made underneath it — the broadcast standard, and the safest thing to put between two pictures that have nothing in common.",
        TransitionKind.Wipe => "A soft edge travels across the picture and the change happens behind it. Widen the edge for a slow bleed, narrow it for a hard line.",
        TransitionKind.Push => "The incoming picture comes in from the far side as the outgoing one leaves — the two move together, so it reads as one movement rather than two.",
        TransitionKind.BrandStinger => "The show's own identity as the stinger: two bars in the brand's primary and secondary sweep in, the background fills behind them, the logo lands at the peak, and the picture changes underneath. No clip to prepare and no file to lose.",
        TransitionKind.Reactive => "The reactive scene's own picture is the matte: the change lands where the scene is dark first, so a plasma dissolves in clouds, a vortex spirals in and a star warp opens from the middle. Drawn small and scaled up, so a 4K wall and a thumbnail cost the same.",
        _ => "The outgoing picture fades away over the incoming one — what a desk has always done, and what every show that says nothing else gets.",
    };

    private TransitionConfig? _hookedTransition;

    /// <summary>
    /// The transition pickers follow the kind on the keystroke, not on the next poll: choosing a
    /// wipe must show its direction now. Re-hooked when a show is loaded, because the model under
    /// the desk is a different object then.
    /// </summary>
    public void HookTransition()
    {
        if (!ReferenceEquals(_hookedTransition, State.Transition))
        {
            if (_hookedTransition is not null) _hookedTransition.PropertyChanged -= OnTransitionChanged;
            _hookedTransition = State.Transition;
            _hookedTransition.PropertyChanged += OnTransitionChanged;
        }
        // Always, hooked or not: a show read from a file arrives with its own transition and the
        // page has to be showing that one's rows before the operator looks at it.
        RaiseTransition();
    }

    private void OnTransitionChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(TransitionConfig.Kind)) RaiseTransition();
    }

    /// <summary>The transition pickers follow the kind, at once.</summary>
    private void RaiseTransition()
    {
        Raise(nameof(TransitionHasDirection));
        Raise(nameof(TransitionIsReactive));
        Raise(nameof(TransitionIsDip));
        Raise(nameof(TransitionHasSoftness));
        Raise(nameof(TransitionNote));
    }

    // ---- pattern editing target ---------------------------------------------

    public ObservableCollection<EditTarget> EditTargets { get; } = new() { new EditTarget("Program", null) };

    public EditTarget EditTarget
    {
        get => _editTarget;
        set
        {
            var previous = _editTarget;
            if (value is not null && Set(ref _editTarget, value))
            {
                // Round 67: the target's picture the editors write into. A target that follows the
                // programme gets an inert copy of it (nothing jumps, nothing publishes a change) and a
                // watch on that copy: the first edit makes the target its own. Leaving a target whose
                // copy was never edited takes the copy away again, so the show file never gathers them.
                if (previous?.ScreenId is { } left && left != value.ScreenId) DropUnusedStaging(left);
                if (value.ScreenId is { } id) EnsureStaged(id);
                Raise(nameof(ActivePattern));
                Raise(nameof(EditTargetBanner));
                Raise(nameof(CanvasInfo));
                Raise(nameof(ShowCanvasPanel));
                // The panes follow the editors: a target with its own pattern is selected with
                // it; "Program" keeps the selected tile unless that tile shows its own picture.
                if (value.ScreenId is not null) SelectTarget(value.ScreenId);
                else if (_selectedTargetId is { } selected && ContentTargets.UsesOwnPattern(State, selected)) SelectTarget(null);
                RefreshSwitcherTiles();
                Media.RaisePlaylistSection();
            }
        }
    }

    /// <summary>The pattern the editor panels bind to (program, or a custom screen's).</summary>
    public PatternConfig ActivePattern
    {
        get
        {
            if (_editTarget.ScreenId is { } id)
            {
                var a = State.Independent.FirstOrDefault(x => x.ScreenId == id);
                if (a is not null) return a.Pattern;
            }
            return State.Pattern;
        }
    }

    public bool ShowEditTargets => EditTargets.Count > 1;

    // ---- own on the first edit (round 67.5) ----------------------------------------

    private readonly Dictionary<string, (PatternConfig Pattern, ChangeTracker Watch)> _editWatches = new(StringComparer.Ordinal);

    /// <summary>
    /// The target's own assignment, or an inert copy of the programme when it still follows it — and
    /// a watch on that pattern, so the first edit flips the target to its own picture at once.
    /// </summary>
    private void EnsureStaged(string id)
    {
        if (!ContentTargets.IsInRig(State, id)) return;
        if (State.Independent.All(x => x.ScreenId != id)) _services.BulkEdit(() => ContentTargets.EnsureAssignment(State, id));
        ArmEditWatch(id);
    }

    private void ArmEditWatch(string id)
    {
        var assignment = State.Independent.FirstOrDefault(x => x.ScreenId == id);
        if (assignment is null) return;
        if (_editWatches.TryGetValue(id, out var have) && ReferenceEquals(have.Pattern, assignment.Pattern)) return;
        var pattern = assignment.Pattern;
        // Wired once per pattern object, zero per-frame cost; runtime-only chrome on the pattern never counts as an edit.
        _editWatches[id] = (pattern, new ChangeTracker(pattern, () => OnStagedEdited(id, pattern), onRuntimeOnlyChanged: static () => { }));
    }

    /// <summary>An edit landed on the target's staged copy: it is the target's own picture from this moment.</summary>
    private void OnStagedEdited(string id, PatternConfig pattern)
    {
        if (_editTarget?.ScreenId != id) return;                                        // a stale watch: not the target being edited
        if (ContentTargets.UsesOwnPattern(State, id)) return;
        var assignment = State.Independent.FirstOrDefault(x => x.ScreenId == id);
        if (assignment is null || !ReferenceEquals(assignment.Pattern, pattern)) return;
        _services.BulkEdit(() =>
        {
            assignment.PinnedByTake = false;                                              // the operator chose this picture — it stays
            ContentTargets.SetOwnPattern(State, id, true);
        });
        RefreshSwitcherTiles();                                                           // OWN lights on the tile at once
        Raise(nameof(EditTargetBanner));
        Raise(nameof(CanvasInfo));
        _services.Eye.Refresh();                                                          // the Eye's screen word follows on the same press
        StatusMessage = $"{TargetTitle(id)} is its own picture now — the programme and every other screen are untouched.";
    }

    /// <summary>A target left without an edit: its inert copy goes; a pin or an own picture stays.</summary>
    private void DropUnusedStaging(string id)
    {
        if (ContentTargets.UsesOwnPattern(State, id)) return;
        var assignment = State.Independent.FirstOrDefault(x => x.ScreenId == id);
        if (assignment is null || assignment.PinnedByTake) return;
        _services.BulkEdit(() => State.Independent.Remove(assignment));
        _editWatches.Remove(id);
    }

    /// <summary>"2 · Comfort", "A · Main wall" — the wall's name for a target.</summary>
    private string TargetTitle(string id) => Rig.Geometry(State, _services.Screens.All).LabelFor(State, id);

    private void EnsureAssignmentsForCustomScreens()
    {
        foreach (var p in State.Output.Placements.Where(p => p.UseCustomPattern))
        {
            EnsureAssignment(p.ScreenId);
        }
        foreach (var c in State.Output.CanvasNames.Where(c => c.UseCustomPattern && c.MemberKey.Length > 0))
        {
            EnsureAssignment(c.MemberKey);
        }
    }

    internal void EnsureAssignment(string targetId) => ContentTargets.EnsureAssignment(State, targetId);

    internal void RebuildEditTargets()
    {
        Screens.RefreshAdoptTargets();
        var current = _editTarget?.ScreenId;
        EditTargets.Clear();
        EditTargets.Add(new EditTarget("Program", null));
        // Round 67: every target of the wall is an editing target — a joined canvas and every screen
        // that stands alone — whether it shows its own picture yet or follows the programme (its first
        // edit makes it its own). A repeater draws its source's picture and is never one.
        var groups = CanvasGroups();
        var grouped = groups.SelectMany(g => g).Select(m => m.ScreenId).ToHashSet(StringComparer.Ordinal);
        for (var i = 0; i < groups.Count; i++)
        {
            var key = CanvasNameConfig.KeyFor(groups[i].Select(m => m.ScreenId));
            var letter = ((char)('A' + i)).ToString();
            EditTargets.Add(new EditTarget($"Canvas {letter} — {CanvasNameFor(groups[i], letter)}", key));
        }
        foreach (var p in State.Output.Placements)
        {
            if (grouped.Contains(p.ScreenId) && !p.UseCustomPattern) continue;          // a member's own entry stays while it has a picture of its own
            if (p.MirrorOf.Length > 0 && ContentTargets.IsInRig(State, p.MirrorOf)) continue;
            var info = LiveInfo(p);
            if (info is not null)
            {
                EditTargets.Add(new EditTarget($"Screen {info.Index + 1} — {LabelFor(p, info)}", p.ScreenId));
            }
        }
        // The target after the rebuild: the same one when it is still there, else Program — never
        // nothing. Clearing the list emptied the Pattern page's picker (its two-way binding wrote
        // that empty selection back, refused below), and a same target re-added as an equal record
        // used to raise nothing, so the picker stayed blank until the operator picked it again:
        // the same target is re-published as the list's own instance, and only a different one
        // goes through the full setter (the panes follow that).
        var wanted = EditTargets.FirstOrDefault(t => t.ScreenId == current) ?? EditTargets[0];
        if (wanted.ScreenId == _editTarget?.ScreenId)
        {
            _editTarget = wanted;
            if (wanted.ScreenId is { } kept) EnsureStaged(kept);                        // its copy may have gone with the rig change
            Raise(nameof(EditTarget));
            Raise(nameof(EditTargetBanner));
        }
        else
        {
            EditTarget = wanted;
        }
        Raise(nameof(ShowEditTargets));
        RebuildSwitcherTiles();
    }

    // ---- media library ------------------------------------------------------

    internal void AddToMediaLibrary(string path, bool isVideo)
    {
        if (State.MediaLibrary.Any(m => string.Equals(m.Path, path, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }
        State.MediaLibrary.Add(new MediaLibraryEntry
        {
            Path = path,
            IsVideo = isVideo,
            Kind = MediaLibraryEntry.KindOf(path, isVideo),
            AddedUtc = DateTime.UtcNow,
        });
        BuildLibrary();
    }

    // ---- multiview ----------------------------------------------------------

    /// <summary>Target choices for multiview tiles, in wall order: joined canvases, then every screen.</summary>
    public ObservableCollection<EditTarget> MultiviewTargets { get; } = new();

    private void RebuildMultiviewTargets()
    {
        var geo = Rig.Geometry(State, _services.Screens.All);
        var wanted = new List<EditTarget>();
        foreach (var key in geo.Targets)
        {
            if (ContentTargets.IsCanvasKey(key)) wanted.Add(new EditTarget(geo.LabelFor(State, key), key));
        }
        foreach (var s in geo.Screens)
        {
            wanted.Add(new EditTarget(geo.LabelFor(State, s.Id), s.Id));
        }
        ReplaceIfChanged(MultiviewTargets, wanted);
    }

    /// <summary>
    /// A picker bound two-way to a model id is rebuilt in place only when its entries really
    /// moved: clearing a ComboBox's items drops its selection, and the binding would write that
    /// empty selection back into the show. Same entries, same order = nothing happens.
    /// </summary>
    internal static void ReplaceIfChanged(ObservableCollection<EditTarget> current, List<EditTarget> wanted)
    {
        if (current.Count == wanted.Count && current.SequenceEqual(wanted)) return;
        current.Clear();
        foreach (var t in wanted) current.Add(t);
    }

    public bool ClickerArmed
    {
        get => _services.Cues.For(CueStacks.Clicker(State)).Armed;
        set
        {
            var rt = _services.Cues.For(CueStacks.Clicker(State));
            if (rt.Armed == value) return;
            _services.Actions.Execute(value ? ShowActionKind.ListArm : ShowActionKind.ListDisarm, ActionOrigin.Desk, CueStacks.Clicker(State).Id);
            Raise(nameof(ClickerArmed));
        }
    }

    /// <summary>The Cues page.</summary>
    public CueEditor Cues { get; }

    /// <summary>The Run surface's state and commands.</summary>
    public RunViewModel Run { get; }

    /// <summary>The desk has the wall beside the caller's list.</summary>
    public bool HasRunWall => true;

    // ---- the RUN monitor (round 62): one screen large between the wall and the history ----

    private DeskLayoutConfig? _monitorDesk;
    private Patterns.App.Rendering.PipelineViewport? _runMonitorViewport;
    private string _runMonitorTitle = "";
    private double _runMonitorRatio = 16.0 / 9.0;

    /// <summary>The monitor is drawn: not hidden, and there is a screen (or the programme) to draw.</summary>
    public bool HasRunMonitor => _runMonitorViewport is not null;

    /// <summary>The picture the monitor draws — a target's PGM side at its true size, or the programme.</summary>
    public Patterns.App.Rendering.PipelineViewport? RunMonitorViewport
    {
        get => _runMonitorViewport;
        private set { if (Set(ref _runMonitorViewport, value)) Raise(nameof(HasRunMonitor)); }
    }

    public string RunMonitorTitle { get => _runMonitorTitle; private set => Set(ref _runMonitorTitle, value); }

    public double RunMonitorRatio { get => _runMonitorRatio; private set => Set(ref _runMonitorRatio, value); }

    /// <summary>
    /// The monitor follows the desk layout's choice (RUN MONITOR, the right-click) and the rig: called
    /// when the wall is rebuilt, and by itself when the choice changes from any origin.
    /// </summary>
    internal void RefreshRunMonitor()
    {
        var desk = State.Desk;
        if (!ReferenceEquals(_monitorDesk, desk))
        {
            _monitorDesk = desk;
            desk.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(DeskLayoutConfig.RunMonitor)) RefreshRunMonitor();
            };
        }
        var facts = DeskMenuFacts.Monitor(_services);
        var target = facts.IsProgram ? null : facts.ShownTargetId;
        if (facts.IsOff || (!facts.IsProgram && string.IsNullOrEmpty(target)))
        {
            RunMonitorTitle = facts.IsOff ? "hidden" : "no screen in the rig";
            RunMonitorViewport = null;
            return;
        }
        var size = Rig.TargetSize(State, _services.Screens.All, target);
        var title = facts.IsProgram ? "PGM — the programme" : facts.ShowingWords;
        RunMonitorRatio = size.Height > 0 ? (double)size.Width / size.Height : 16.0 / 9.0;
        RunMonitorTitle = title;
        RunMonitorViewport = Patterns.App.Rendering.PipelineViewport.Monitor(target, size, title, previewSide: false);
    }

    // ---- the caller's lower thirds on the Run surface (round 62) ----

    public Patterns.Core.LowerThirds.LowerThirdsConfig LowerThirds => State.LowerThirds;

    /// <summary>The show has a design to call up; the strip shows.</summary>
    public bool HasLowerThirds => State.LowerThirds.Designs.Count > 0;

    /// <summary>A row's OPEN IN EDITOR: the cue selected on the Cues page, and the page shown.</summary>
    public void OpenCueInEditor(RunCueConfig cue)
    {
        Cues.SelectedCue = cue;
        SelectPage(Shell.IndexOf("Cues"));
    }

    private bool _isRunLayout;

    /// <summary>
    /// The caller's one-column layout instead of the editors and the switcher. It is the Run
    /// page of the SHOW group: setting it selects that page (or the last Build page on the way
    /// out), and leaving is refused while the caller's stack is armed.
    /// </summary>
    public bool IsRunLayout
    {
        get => _isRunLayout;
#pragma warning disable S4275 // the layout follows the page: selecting the page sets the field (SetRunLayout)
        set
        {
            if (value == _isRunLayout) return;
            SelectPage(value ? Shell.RunPage : _lastBuildPage);
        }
#pragma warning restore S4275
    }

    private void SetRunLayout(bool value)
    {
        if (_isRunLayout == value) return;
        _isRunLayout = value;
        Raise(nameof(IsRunLayout));
        Raise(nameof(RunLayoutButtonText));
        if (value)
        {
            Run.Refresh();
            if (_services.RecoveryBanner.Length > 0)
            {
                Run.Banner = _services.RecoveryBanner;
                _services.RecoveryBanner = "";
                // The recovery's own line stays on the strip: a keyboard hint the operator has
                // read a hundred times must not push "the screens are this desk's now" off it.
                return;
            }
            StatusMessage = "RUN — Enter is GO while armed, ↑ ↓ move standby, Esc twice is STOP ALL. Space is still blackout.";
        }
    }

    // ---- audio / fonts / feed / LED map ------------------------------------

    public EnumItem[] FeedKinds => Lists.FeedKinds;
    public EnumItem[] MessageBackgrounds => Lists.MessageBackgrounds;
    public EnumItem[] Rotations => Lists.Rotations;
    public EnumItem[] PipSources => Lists.PipSources;

    private string _feedStatus = "";
    public string FeedStatus { get => _feedStatus; private set => Set(ref _feedStatus, value); }

    public const string BuiltInFontLabel = "Inter (built-in)";

    private List<string>? _fontFamilies;
    public List<string> FontFamilies => _fontFamilies ??= BuildFontFamilies();

    private static List<string> BuildFontFamilies()
    {
        var list = new List<string> { BuiltInFontLabel };
        try
        {
            list.AddRange(SkiaSharp.SKFontManager.Default.FontFamilies
                .Where(f => !string.IsNullOrWhiteSpace(f))
                .Distinct()
                .OrderBy(f => f, StringComparer.OrdinalIgnoreCase));
        }
        catch (Exception ex)
        {
            Log.Warn("System font enumeration failed.", ex);
        }
        return list;
    }

    /// <summary>Maps the empty model value ⇄ the built-in entry for the combo.</summary>
    public string? SelectedFontFamily
    {
        get => string.IsNullOrEmpty(State.Brand.FontFamily) ? BuiltInFontLabel : State.Brand.FontFamily;
        set
        {
            // The combo coerces values missing from its list to null (e.g. a show file made
            // on a machine with fonts this one lacks) — never let that clear the model; the
            // renderer already falls back to the built-in font for missing families.
            if (value is null) return;
            State.Brand.FontFamily = value == BuiltInFontLabel ? "" : value;
            Raise();
        }
    }

    private LedTileConfig? _selectedLedTile;
    public LedTileConfig? SelectedLedTile
    {
        get => _selectedLedTile;
        set
        {
            if (Set(ref _selectedLedTile, value)) Raise(nameof(HasLedTileSelection));
        }
    }

    public bool HasLedTileSelection => _selectedLedTile is not null;

    private void AddLedTile()
    {
        var led = ActivePattern.LedWall;
        var tiles = led.CustomTiles;
        var x = tiles.Count == 0 ? 0 : tiles.Max(t => t.X + t.Width);
        var tile = new LedTileConfig { X = x, Y = 0, Width = led.TileWidth, Height = led.TileHeight };
        tiles.Add(tile);
        led.UseCustomMap = true;
        SelectedLedTile = tile;
    }

    /// <summary>Seeds the custom map from the current regular grid — then edit the exceptions.</summary>
    private void ImportGridToMap()
    {
        var led = ActivePattern.LedWall;
        var layout = CanvasResolver.Led(led);
        _services.BulkEdit(() =>
        {
            led.CustomTiles.Clear();
            for (var r = 0; r < layout.Rows; r++)
            {
                for (var col = 0; col < layout.Columns; col++)
                {
                    led.CustomTiles.Add(new LedTileConfig
                    {
                        X = col * layout.TileWidth,
                        Y = r * layout.TileHeight,
                        Width = layout.TileWidth,
                        Height = layout.TileHeight,
                    });
                }
            }
            led.UseCustomMap = true;
        });
        SelectedLedTile = led.CustomTiles.FirstOrDefault();
        StatusMessage = $"Imported {led.CustomTiles.Count} tiles from the grid — drag or edit the exceptions.";
    }

    // ---- lists for the views ------------------------------------------------

    public EnumItem[] PatternKinds => Lists.PatternKinds;
    public EnumItem[] MultiviewSourceKinds => Lists.MultiviewSources;
    public EnumItem[] Anchors => Lists.Anchors;
    public EnumItem[] AppearKinds => Lists.AppearKinds;
    public EnumItem[] FitModes => Lists.FitModes;
    public EnumItem[] BarsVariants => Lists.BarsVariants;
    public EnumItem[] TestCardVariants => Lists.TestCardVariants;
    public EnumItem[] RampVariants => Lists.RampVariants;
    public EnumItem[] MotionVariants => Lists.MotionVariants;
    public EnumItem[] BlendCurves => Lists.BlendCurves;
    public EnumItem[] BlendOrientations => Lists.BlendOrientations;
    public EnumItem[] TileNumberings => Lists.TileNumberings;
    public EnumItem[] MediaSources => Lists.MediaSources;
    public EnumItem[] ParticleShapes => Lists.ParticleShapes;
    public EnumItem[] ParticleEmitters => Lists.ParticleEmitters;
    public EnumItem[] CountdownKinds => Lists.CountdownKinds;
    public EnumItem[] CountdownEnds => Lists.CountdownEnds;
    public EnumItem[] ScaleModes => Lists.ScaleModes;
    public ResolutionPreset[] Resolutions => Lists.Resolutions;
    public string[] CountdownLabels => Lists.CountdownLabels;

    // ---- fractal ------------------------------------------------------------

    /// <summary>The Fractals page's chips: every family in order, then "Custom" — the operator's saved fractal presets.</summary>
    public ObservableCollection<FractalSceneGroup> FractalSceneGroups { get; } = new();

    private RelayCommand<FractalChip>? _applyFractalChip;

    public RelayCommand<FractalChip> ApplyFractalChipCommand => _applyFractalChip ??= new RelayCommand<FractalChip>(chip => chip?.Apply());

    private RelayCommand? _applyBrandPaletteToFractal;

    /// <summary>The brand kit's five colours as the fractal's palette — the ground first, the text colour last, so a scene reads in the client's colours.</summary>
    public RelayCommand ApplyBrandPaletteToFractalCommand => _applyBrandPaletteToFractal ??= new RelayCommand(() =>
    {
        var b = State.Brand;
        var palette = string.Join(",", b.BackgroundColor, b.PrimaryColor, b.SecondaryColor, b.AccentColor, b.TextColor);
        _services.BulkEdit(() => ActivePattern.Fractal.ColorsCsv = palette);
        StatusMessage = $"The brand kit's colours are the fractal's palette: {palette}.";
    });

    private void RefreshFractalSceneGroups()
    {
        var groups = new List<FractalSceneGroup>();
        foreach (var category in FractalPresets.Categories)
        {
            groups.Add(new FractalSceneGroup(category, FractalPresets.In(category)
                .Select(scene => new FractalChip(scene.Name, () => _services.BulkEdit(() => FractalPresets.Apply(scene.Name, ActivePattern.Fractal))))
                .ToList()));
        }
        var custom = new List<FractalChip>();
        foreach (var (name, path) in _services.Store.ListPresets())
        {
            var p = path;
            if (_services.Store.LoadPreset(p) is not { Kind: PatternKind.Fractal }) continue;
            custom.Add(new FractalChip(name, () =>
            {
                if (_services.Store.LoadPreset(p) is not { } cfg) return;
                _services.BulkEdit(() => ModelCopier.Copy(cfg.Fractal, ActivePattern.Fractal));
            }));
        }
        if (custom.Count > 0) groups.Add(new FractalSceneGroup("Custom", custom));
        if (FractalSceneGroups.Count == groups.Count &&
            FractalSceneGroups.Zip(groups).All(z => z.First.Category == z.Second.Category &&
                                                    z.First.Chips.Select(c => c.Name).SequenceEqual(z.Second.Chips.Select(c => c.Name))))
        {
            return; // the same chips: leave the page alone
        }
        FractalSceneGroups.Clear();
        foreach (var g in groups) FractalSceneGroups.Add(g);
    }

    // ---- library ------------------------------------------------------------

    /// <summary>The section chips, in the order they are shown; "All" first.</summary>
    public string[] LibrarySections => LibraryCatalogue.SectionNames;

    /// <summary>Every tile, whatever the chips and the search say.</summary>
    public List<PresetItem> LibraryAll { get; } = new();

    /// <summary>The tiles the page shows: <see cref="LibraryAll"/> through the section chip and the search box.</summary>
    public ObservableCollection<PresetItem> Library { get; } = new();

    private string _selectedLibrarySection = "All";
    public string SelectedLibrarySection
    {
        get => _selectedLibrarySection;
        set
        {
            if (!Set(ref _selectedLibrarySection, value ?? "All")) return;
            ApplyLibraryFilter();
        }
    }

    private string _librarySearch = "";
    public string LibrarySearch
    {
        get => _librarySearch;
        set
        {
            if (!Set(ref _librarySearch, value ?? "")) return;
            ApplyLibraryFilter();
        }
    }

    private string _librarySummary = "";

    /// <summary>"74 tiles", or "12 of 74 · Images · 'logo'".</summary>
    public string LibrarySummary { get => _librarySummary; private set => Set(ref _librarySummary, value); }

    /// <summary>The thumbnails in flight for the current library — awaited by tests and the screenshot pass.</summary>
    public Task LibraryThumbnails { get; private set; } = Task.CompletedTask;

    private RelayCommand<PresetItem>? _removeLibraryItem;

    public RelayCommand<PresetItem> RemoveLibraryItemCommand => _removeLibraryItem ??= new RelayCommand<PresetItem>(item =>
    {
        if (item?.Remove is null) return;
        item.Remove();
        StatusMessage = $"'{item.Name}' taken out of the library — the file itself stays.";
        RefreshLibrary();
    });

    /// <summary>Brings every tile up to date — the factory table, the show's media, the saved presets, the brand kits — and draws the thumbnails that changed.</summary>
    public void RefreshLibrary() => BuildLibrary();

    // ---- particle scenes, by pack ----------------------------------------------

    /// <summary>The Particles page's chips: every factory pack in order, then "Custom" — the operator's saved particle presets.</summary>
    public ObservableCollection<ParticlePackGroup> ParticlePackGroups { get; } = new();

    private RelayCommand<ParticleChip>? _applyParticleChip;

    public RelayCommand<ParticleChip> ApplyParticleChipCommand => _applyParticleChip ??= new RelayCommand<ParticleChip>(chip => chip?.Apply());

    private void RefreshParticlePackGroups()
    {
        var groups = new List<ParticlePackGroup>();
        foreach (var category in ParticlePresets.Categories)
        {
            groups.Add(new ParticlePackGroup(category, ParticlePresets.In(category)
                .Select(pack => new ParticleChip(pack.Name, () => _services.BulkEdit(() => ParticlePresets.Apply(pack.Name, ActivePattern.Particles))))
                .ToList()));
        }
        var custom = new List<ParticleChip>();
        foreach (var (name, path) in _services.Store.ListPresets())
        {
            var p = path;
            if (_services.Store.LoadPreset(p) is not { Kind: PatternKind.Particles }) continue;
            custom.Add(new ParticleChip(name, () =>
            {
                if (_services.Store.LoadPreset(p) is not { } cfg) return;
                _services.BulkEdit(() => ModelCopier.Copy(cfg.Particles, ActivePattern.Particles));
            }));
        }
        if (custom.Count > 0) groups.Add(new ParticlePackGroup("Custom", custom));
        if (ParticlePackGroups.Count == groups.Count &&
            ParticlePackGroups.Zip(groups).All(z => z.First.Category == z.Second.Category &&
                                                    z.First.Chips.Select(c => c.Name).SequenceEqual(z.Second.Chips.Select(c => c.Name))))
        {
            return; // the same chips: leave the page alone
        }
        ParticlePackGroups.Clear();
        foreach (var g in groups) ParticlePackGroups.Add(g);
    }

    /// <summary>
    /// The tiles, brought up to date in place — a tile that is still the same tile keeps its
    /// instance, its thumbnail and its place in the ItemsControl — and the thumbnails handed to
    /// the one queue, the visible tiles first. One file picked used to rebuild every tile and
    /// start a thumbnail pass over all of them beside the passes already running.
    /// </summary>
    private void BuildLibrary()
    {
        RefreshParticlePackGroups();
        RefreshFractalSceneGroups();
        var desk = new LibraryCatalogue.Desk(() => ActivePattern, _services.BulkEdit, words => StatusMessage = words);
        LibraryCatalogue.Reconcile(LibraryAll, LibraryCatalogue.Build(State, _services.Store, desk));
        ApplyLibraryFilter();
        // The published picture of the show is what the thumbnails draw over: immutable, so the
        // worker reads it while the desk goes on editing.
        var over = _services.Bus.Sandbox?.State ?? _services.Bus.Current.State;
        var brand = JsonUtil.SerializeCompact(over.Brand);
        var jobs = Library.Concat(LibraryAll.Except(Library)).Select(tile => LibraryCatalogue.JobFor(tile, over, brand)).ToList();
        LibraryThumbnails = _services.Thumbnails.Submit(jobs);
    }

    private PresetItem? _selectedLibraryItem;

    /// <summary>The library tile last put in the preview — lit on the page until another is chosen.</summary>
    public PresetItem? SelectedLibraryItem
    {
        get => _selectedLibraryItem;
        private set
        {
            if (ReferenceEquals(_selectedLibraryItem, value)) return;
            if (_selectedLibraryItem is not null) _selectedLibraryItem.IsSelected = false;
            _selectedLibraryItem = value;
            if (value is not null) value.IsSelected = true;
            Raise(nameof(SelectedLibraryItem));
        }
    }

    /// <summary>
    /// A library tile clicked (round 67.4): its picture lands in the editing target's preview at once —
    /// EDIT SAFE opens first when it was off, so the air never moves — and the target tile's PVW shows
    /// it on the same publish; a target that followed the programme is its own picture from this press.
    /// </summary>
    private void ApplyLibraryItem(PresetItem? item)
    {
        if (item is null) return;
        var brandKit = item.Section == "Brand kits";
        if (!brandKit) EnsureEditSafe();
        item.Apply();
        SelectedLibraryItem = item;
        if (brandKit) return;
        Raise(nameof(IsSandboxActive));
        RefreshSwitcherTiles();
        RefreshTallies();
        Raise(nameof(ActivePattern));
        Raise(nameof(EditTargetBanner));
        var where = _editTarget.ScreenId is { } id
            ? $"{TargetTitle(id)}'s preview{(ContentTargets.UsesOwnPattern(State, id) ? " (its own picture)" : "")}"
            : "the programme's preview";
        StatusMessage = $"{item.Name} → {where} — CUT or TAKE puts it up.";
    }

    /// <summary>The chip and the search box together: every search word must appear in the tile's name, category or section.</summary>
    private void ApplyLibraryFilter()
    {
        var shown = LibraryCatalogue.Filter(LibraryAll, SelectedLibrarySection, LibrarySearch);
        LibraryCatalogue.Sync(Library, shown);
        LibrarySummary = LibraryCatalogue.Summary(shown.Count, LibraryAll.Count, SelectedLibrarySection, LibrarySearch);
    }

    /// <summary>A saved pattern, as the Pattern page's own chip: press to recall, ✕ to forget.</summary>
    public ObservableCollection<PresetChip> PresetChips { get; } = new();

    /// <summary>"Three presets. Press one to recall it." — or what to do when there are none.</summary>
    public string PresetHint => PresetChips.Count == 0
        ? "No presets yet. Build a picture, name it above and press Save — then it is one press from here, from each screen's row on the Show panel, and from a cue."
        : $"{PresetChips.Count} preset{(PresetChips.Count == 1 ? "" : "s")}. Press one to recall it into the picture you are editing.";

    /// <summary>
    /// The chips, rebuilt from the folder. Presets are files rather than part of the show, so the
    /// list is read from disk — one saved a moment ago, or one copied into the folder by hand while
    /// the desk is running, both appear without a restart.
    /// </summary>
    public void RefreshPresetChips()
    {
        var names = _services.Store.PresetNames();
        if (PresetChips.Count == names.Count && PresetChips.Select(c => c.Name).SequenceEqual(names)) return;
        PresetChips.Clear();
        foreach (var name in names) PresetChips.Add(new PresetChip(this, name));
        Raise(nameof(PresetHint));
        RefreshSendChoices();
    }

    /// <summary>Press a chip: the saved pattern back into the picture being edited, through the action layer.</summary>
    internal void RecallPreset(string name)
        => Report(_services.Actions.Execute(ShowActionKind.PatternPreset, ActionOrigin.Desk, "", name));

    /// <summary>✕ on a chip: the file is the preset, so forgetting it removes the file.</summary>
    internal void ForgetPreset(string name)
    {
        StatusMessage = _services.Store.DeletePreset(name)
            ? $"Preset '{name}' forgotten."
            : $"Preset '{name}' could not be removed — see the log.";
        RefreshPresetChips();
        BuildLibrary();
    }

    private void SaveUserPreset()
    {
        var name = string.IsNullOrWhiteSpace(NewPresetName) ? $"Preset {DateTime.Now:HHmmss}" : NewPresetName.Trim();
        try
        {
            _services.Store.SavePreset(name, ActivePattern);
            // Say where it went and how to get it back. It used to say only "saved", and the answer
            // to "so how do I recall it?" was a different page that nothing here mentioned.
            StatusMessage = $"Preset '{name}' saved — press it below to recall it, or pick it in a screen's row on the Show panel.";
            NewPresetName = "";
            RefreshPresetChips();
            BuildLibrary();
        }
        catch (Exception ex)
        {
            Log.Error("Preset save failed.", ex);
            StatusMessage = $"Preset save failed: {ex.Message}";
        }
    }

    private void SaveBrandKit()
    {
        var name = string.IsNullOrWhiteSpace(State.Brand.CompanyName) ? "brand" : State.Brand.CompanyName;
        try
        {
            _services.Store.SaveBrandKit(name, State.Brand);
            StatusMessage = $"Brand kit '{name}' saved.";
        }
        catch (Exception ex)
        {
            StatusMessage = $"Brand kit save failed: {ex.Message}";
        }
    }

    private async Task LoadBrandKitAsync()
    {
        var path = await PickOpenPathAsync("Load brand kit", new FilePickerFileType("Brand kit") { Patterns = new[] { "*.json" } },
            _services.Store.BrandKitsDirectory);
        if (path is null) return;
        var kit = _services.Store.LoadBrandKit(path);
        if (kit is not null)
        {
            _services.BulkEdit(() => ModelCopier.Copy(kit, State.Brand));
            StatusMessage = "Brand kit loaded.";
        }
    }
}
