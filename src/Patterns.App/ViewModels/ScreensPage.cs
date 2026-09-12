using System.Collections.ObjectModel;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.App.ViewModels;

/// <summary>
/// The Screens page: the selected screen and everything set on it — on or off, its own picture,
/// direct output, the frame rates, the display's mode, the edge blend, the wall's gaps, the role
/// and the lock and what it repeats, the label and the canvas name, rotation and trims — and the
/// planned screens adopted onto real displays. Built by the desk and reached from the page and the
/// settings column as <c>Screens.X</c>. The desk keeps the rig itself: reconciling placements to
/// the displays, the edit targets, the tiles and the lists that read the whole arrangement; the
/// page asks it for those and for the words on the status line.
/// </summary>
public sealed class ScreensPage : Observable
{
    private readonly MainViewModel _desk;
    private readonly AppServices _services;
    private ScreenPlacement? _selectedPlacement;

    private ShowState State => _services.State;

    public ScreensPage(MainViewModel desk, AppServices services)
    {
        _desk = desk;
        _services = services;
        ApplyDisplayModeCommand = new RelayCommand(ApplyDisplayMode);
        KeepDisplayModeCommand = new RelayCommand(KeepDisplayMode);
        RevertDisplayModeCommand = new RelayCommand(RevertDisplayMode);
        ResetWarpCommand = new RelayCommand(() =>
        {
            if (_selectedPlacement is not { } placement) return;
            _services.BulkEdit(() =>
            {
                placement.WarpTlx = 0; placement.WarpTly = 0;
                placement.WarpTrx = 0; placement.WarpTry = 0;
                placement.WarpBlx = 0; placement.WarpBly = 0;
                placement.WarpBrx = 0; placement.WarpBry = 0;
                placement.WarpTopBow = 0; placement.WarpRightBow = 0;
                placement.WarpBottomBow = 0; placement.WarpLeftBow = 0;
            });
            RaiseSelection();
        });
        ArrangeBlendGridCommand = new RelayCommand(ArrangeBlendGrid);
        ResetBlendCommand = new RelayCommand(ResetBlend);
        ResetTrimsCommand = new RelayCommand(() =>
        {
            if (_selectedPlacement is not { } placement) return;
            _services.BulkEdit(() =>
            {
                placement.BrightnessPct = 100;
                placement.Gamma = 1.0;
                placement.TrimRPct = 100;
                placement.TrimGPct = 100;
                placement.TrimBPct = 100;
            });
            RaiseSelection();
        });
        AddGapCommand = new RelayCommand(AddGap);
        RemoveGapCommand = new RelayCommand<WallGap>(RemoveGap);
        SetGapsFromGridCommand = new RelayCommand(SetGapsFromGrid);
        ClearGapsCommand = new RelayCommand(ClearGaps);
        AddPlannedScreenCommand = new RelayCommand(() => AddPlannedScreen());
        RemovePlannedScreenCommand = new RelayCommand<ScreenPlacement>(p =>
        {
            if (p is not null) RemovePlannedScreen(p);
        });
        AdoptPlannedScreenCommand = new RelayCommand<ScreenPlacement>(p =>
        {
            if (p is null) return;
            if (!AdoptPlannedScreen(p, p.AdoptTargetId))
            {
                _desk.StatusMessage = "Choose which detected display this planned screen becomes.";
            }
        });
        RefreshAdoptTargetsCommand = new RelayCommand(RefreshAdoptTargets);
    }

    // ---- the selection ---------------------------------------------------------------

    public ScreenPlacement? SelectedPlacement
    {
        get => _selectedPlacement;
        set
        {
            if (Set(ref _selectedPlacement, value))
            {
                RaiseSelection();
                _desk.RefreshPopOut();
            }
        }
    }

    public bool HasSelection => _selectedPlacement is not null && _desk.LiveInfo(_selectedPlacement) is not null;

    public string SelectedScreenTitle
    {
        get
        {
            if (_selectedPlacement is null) return "No screen selected";
            var info = _desk.LiveInfo(_selectedPlacement);
            return info is null
                ? "Offline screen (from a saved show)"
                : $"{info.Label} — {info.Bounds.Width}×{info.Bounds.Height} @ {info.Scaling:0.##}×{(info.IsPrimary ? " · primary" : "")}";
        }
    }

    public bool SelectedEnabled
    {
        get => _selectedPlacement?.Enabled ?? false;
        set
        {
            if (_selectedPlacement is null) return;
            _selectedPlacement.Enabled = value;
            _selectedPlacement.UserPinned = true;
            RaiseSelection();
        }
    }

    public bool SelectedUseCustom
    {
        get => _selectedPlacement?.UseCustomPattern ?? false;
        set
        {
            if (_selectedPlacement is null) return;
            _selectedPlacement.UseCustomPattern = value;
            if (value)
            {
                _desk.EnsureAssignment(_selectedPlacement.ScreenId);
            }
            _desk.RebuildEditTargets();
            if (value)
            {
                _desk.EditTarget = _desk.EditTargets.FirstOrDefault(t => t.ScreenId == _selectedPlacement.ScreenId) ?? _desk.EditTargets[0];
            }
            RaiseSelection();
        }
    }

    /// <summary>A display with hardware behind it — the only kind an output window opens on, so the only kind direct output applies to.</summary>
    public bool SelectedIsDisplay => _selectedPlacement is { Planned: false } p && _desk.LiveInfo(p) is { IsPlanned: false, IsVirtual: false };

    /// <summary>Bypass the desktop compositor on the selected output (the Screens page's tick).</summary>
    public bool SelectedDirectOutput
    {
        get => _selectedPlacement?.DirectOutput ?? false;
        set
        {
            if (_selectedPlacement is null || _selectedPlacement.DirectOutput == value) return;
            _selectedPlacement.DirectOutput = value;
            if (value) DirectOutputService.ClearFuse(); // ticking again after a held-off start is the retry
            _services.Outputs.OnScreensChanged();       // a live window takes its window-side part now
            RaiseSelection();
            _desk.RaiseDirectOutputSummary();
        }
    }

    /// <summary>The selected output's direct-output line: in force, waiting for a restart, or why not.</summary>
    public string DirectOutputStatus => _selectedPlacement is null ? "" : DirectOutputService.Status(State, _selectedPlacement);

    /// <summary>Custom patterns only make sense on stand-alone screens (groups span the program).</summary>
    public bool SelectedIsGrouped
    {
        get
        {
            if (_selectedPlacement is null) return false;
            var arranged = _desk.BuildArranged();
            var mine = arranged.FirstOrDefault(a => a.Id == _selectedPlacement.ScreenId);
            if (mine.Id is null) return false;
            return ScreenLayout.Groups(arranged).First(g => g.Any(a => a.Id == mine.Id)).Count > 1;
        }
    }

    // ---- frame rate ---------------------------------------------------------

    public FpsOption[] MasterFpsOptions => FpsOption.Master;
    public FpsOption[] ScreenFpsOptions => FpsOption.Screen;

    /// <summary>The show's frame rate: outputs pace to it, an NDI sender on "master" sends at it, the stream can follow it.</summary>
    public int MasterFps
    {
        get => State.Output.MasterFps;
        set
        {
            if (State.Output.MasterFps == value) return;
            State.Output.MasterFps = value;
            Raise();
            if (_services.Outputs.IsLive) _services.Outputs.Apply(); // the windows re-read their viewports
        }
    }

    /// <summary>The selected screen's own rate; 0 follows the master.</summary>
    public int SelectedFpsOverride
    {
        get => _selectedPlacement?.FpsOverride ?? 0;
        set
        {
            if (_selectedPlacement is null || _selectedPlacement.FpsOverride == value) return;
            _selectedPlacement.FpsOverride = value;
            Raise();
            if (_services.Outputs.IsLive) _services.Outputs.Apply();
        }
    }

    // ---- display modes ------------------------------------------------------

    private string _displayModeStatus = "";
    private bool _displayModePending;
    private string _selectedDisplayModeLabel = "";
    private readonly List<DisplayMode> _displayModes = new();
    private (string Device, DisplayMode Previous, string ScreenId, int Index)? _modeChange;
    private DispatcherTimer? _modeRevertTimer;

    /// <summary>The modes the selected display offers ("1920×1080 @ 60 Hz"); its current mode first.</summary>
    public ObservableCollection<string> DisplayModeOptions { get; } = new();

    public string SelectedDisplayModeLabel { get => _selectedDisplayModeLabel; set => Set(ref _selectedDisplayModeLabel, value ?? ""); }

    /// <summary>A sentence about the display's mode: what it is in, what a change did, or why none is possible here.</summary>
    public string DisplayModeStatus { get => _displayModeStatus; private set => Set(ref _displayModeStatus, value); }

    /// <summary>A change was applied and waits for KEEP — REVERT, or fifteen seconds, puts the old mode back.</summary>
    public bool DisplayModePending { get => _displayModePending; private set => Set(ref _displayModePending, value); }

    public RelayCommand ApplyDisplayModeCommand { get; }
    public RelayCommand KeepDisplayModeCommand { get; }
    public RelayCommand RevertDisplayModeCommand { get; }

    private void RefreshDisplayModes()
    {
        _displayModes.Clear();
        DisplayModeOptions.Clear();
        if (_selectedPlacement is null || _desk.LiveInfo(_selectedPlacement) is not { IsPlanned: false } info)
        {
            DisplayModeStatus = "";
            return;
        }
        if (!DisplayModes.Supported)
        {
            DisplayModeStatus = "Display modes can only be changed on Windows.";
            return;
        }
        var device = DisplayModes.DeviceFor(info.Bounds);
        var current = device is null ? null : DisplayModes.Current(device);
        foreach (var m in device is null ? Array.Empty<DisplayMode>() : DisplayModes.List(device))
        {
            _displayModes.Add(m);
            DisplayModeOptions.Add(m.Label);
        }
        if (current is { } cur)
        {
            SelectedDisplayModeLabel = cur.Label;
            if (!_displayModePending) DisplayModeStatus = $"Now {cur.Label}.";
        }
        else
        {
            DisplayModeStatus = "This display could not be matched to a Windows display device.";
        }
    }

    private void ApplyDisplayMode()
    {
        if (_selectedPlacement is null || _desk.LiveInfo(_selectedPlacement) is not { IsPlanned: false } info) return;
        var pick = _displayModes.FirstOrDefault(m => m.Label == SelectedDisplayModeLabel);
        if (pick == default)
        {
            DisplayModeStatus = "Pick a mode first.";
            return;
        }
        var device = DisplayModes.DeviceFor(info.Bounds);
        if (device is null || DisplayModes.Current(device) is not { } previous)
        {
            DisplayModeStatus = "This display could not be matched to a Windows display device.";
            return;
        }
        if (previous == pick)
        {
            DisplayModeStatus = $"Already {pick.Label}.";
            return;
        }
        if (_services.Outputs.IsLive)
        {
            DisplayModeStatus = "Close the outputs (OUTPUTS OFF) before changing a display mode.";
            return;
        }
        _modeChange = (device, previous, _selectedPlacement.ScreenId, info.Index);
        var error = DisplayModes.Apply(device, pick);
        if (error.Length > 0)
        {
            _modeChange = null;
            DisplayModeStatus = error;
            return;
        }
        DisplayModePending = true;
        DisplayModeStatus = $"Changed to {pick.Label} — KEEP it, or it reverts to {previous.Label} in 15 s.";
        _modeRevertTimer?.Stop();
        _modeRevertTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(15) };
        _modeRevertTimer.Tick += (_, _) => RevertDisplayMode();
        _modeRevertTimer.Start();
        Log.Info($"Display mode change on {device}: {previous.Label} → {pick.Label}.");
    }

    private void KeepDisplayMode()
    {
        if (!DisplayModePending) return;
        _modeRevertTimer?.Stop();
        _modeRevertTimer = null;
        DisplayModePending = false;
        // The rename has happened once the display is back under the id the change names; if
        // Windows' topology event is still on its way, the hook finishes and forgets the change.
        if (_modeChange is { } change && _services.Screens.Real.Any(s => s.Id == change.ScreenId)) _modeChange = null;
        DisplayModeStatus = $"Kept {SelectedDisplayModeLabel}.";
        Log.Info("Display mode kept.");
    }

    private void RevertDisplayMode()
    {
        _modeRevertTimer?.Stop();
        _modeRevertTimer = null;
        if (!DisplayModePending || _modeChange is not { } change) return;
        DisplayModePending = false;
        var error = DisplayModes.Apply(change.Device, change.Previous);
        DisplayModeStatus = error.Length > 0 ? $"Could not revert: {error}" : $"Reverted to {change.Previous.Label}.";
        // The rename hook below moves the placement back to the display's restored id.
        Log.Info("Display mode reverted.");
    }

    /// <summary>
    /// A display whose mode just changed comes back from Windows with a new id (ids embed the
    /// geometry). Before the arrangement is reconciled — which would add a fresh placement for
    /// the "new" display and orphan the old one — move everything programmed against the old id
    /// onto the new one: the placement, its pattern, its canvases, senders, tiles and looks.
    /// </summary>
    public void AdoptRenamedDisplay()
    {
        if (_modeChange is not { } change) return;
        // After KEEP or REVERT this is the last topology event the change may act on: a later
        // hot-plug must never move a placement onto whichever display took the index.
        var settled = !DisplayModePending;
        var placement = State.Output.Placements.FirstOrDefault(p => p.ScreenId == change.ScreenId);
        var survived = _services.Screens.Real.Any(s => s.Id == change.ScreenId); // the id survived: nothing moved
        var replacement = survived ? null : _services.Screens.Real.FirstOrDefault(s => s.Index == change.Index);
        if (placement is null || survived || replacement is null || State.Output.Placements.Any(p => p.ScreenId == replacement.Id))
        {
            if (settled) _modeChange = null;
            return;
        }

        var oldId = change.ScreenId;
        _services.RigEditor.RenameScreen(oldId, replacement.Id);
        _modeChange = settled ? null : change with { ScreenId = replacement.Id };
        if (_selectedPlacement == placement) RaiseSelection();
        Log.Info($"Display re-identified after a mode change: {oldId} → {replacement.Id}.");
    }

    // ---- edge blend ---------------------------------------------------------

    public bool SelectedBlendAuto
    {
        get => _selectedPlacement?.BlendAuto ?? false;
        set
        {
            if (_selectedPlacement is null || _selectedPlacement.BlendAuto == value) return;
            _selectedPlacement.BlendAuto = value;
            _desk.ReconcilePlacements(); // an overlap may now join (or leave) a canvas
            RaiseSelection();
        }
    }

    public int SelectedBlendLeft
    {
        get => _selectedPlacement?.BlendLeftPx ?? 0;
        set { if (_selectedPlacement is { } p) { p.BlendLeftPx = value; RaiseBlend(); } }
    }

    public int SelectedBlendTop
    {
        get => _selectedPlacement?.BlendTopPx ?? 0;
        set { if (_selectedPlacement is { } p) { p.BlendTopPx = value; RaiseBlend(); } }
    }

    public int SelectedBlendRight
    {
        get => _selectedPlacement?.BlendRightPx ?? 0;
        set { if (_selectedPlacement is { } p) { p.BlendRightPx = value; RaiseBlend(); } }
    }

    public int SelectedBlendBottom
    {
        get => _selectedPlacement?.BlendBottomPx ?? 0;
        set { if (_selectedPlacement is { } p) { p.BlendBottomPx = value; RaiseBlend(); } }
    }

    public BlendCurve SelectedBlendCurve
    {
        get => _selectedPlacement?.BlendCurve ?? BlendCurve.SCurve;
        set { if (_selectedPlacement is { } p) { p.BlendCurve = value; RaiseBlend(); } }
    }

    /// <summary>The black pedestal outside the zones, as a percentage of white — found on a black test picture.</summary>
    public double SelectedBlendBlack
    {
        get => _selectedPlacement?.BlendBlackPct ?? 0;
        set { if (_selectedPlacement is { } p) { p.BlendBlackPct = value; RaiseBlend(); } }
    }

    private int _blendGridColumns = 2;
    private int _blendGridRows = 2;
    private int _blendGridOverlap = 192;

    /// <summary>The grid ARRANGE AS A BLEND GRID lays out: columns, rows, and the overlap every join gets.</summary>
    public int BlendGridColumns { get => _blendGridColumns; set => Set(ref _blendGridColumns, Math.Clamp(value, 1, 8)); }
    public int BlendGridRows { get => _blendGridRows; set => Set(ref _blendGridRows, Math.Clamp(value, 1, 8)); }
    public int BlendGridOverlap { get => _blendGridOverlap; set => Set(ref _blendGridOverlap, Math.Clamp(value, 0, 4096)); }

    /// <summary>Every screen that is on, laid out as the grid with the overlaps, on automatic blend.</summary>
    public void ArrangeBlendGrid()
    {
        _desk.StatusMessage = _services.RigEditor.LayoutBlendGrid(BlendGridColumns, BlendGridRows, BlendGridOverlap);
        _desk.ReconcilePlacements();
        RaiseSelection();
    }

    public double SelectedBlendGamma
    {
        get => _selectedPlacement?.BlendGamma ?? 1.0;
        set { if (_selectedPlacement is { } p) { p.BlendGamma = value; RaiseBlend(); } }
    }

    /// <summary>
    /// The zones this output will actually fade, in its own pixels — derived from the overlaps
    /// when automatic — and the audit of every join it has: whether the neighbour fades the
    /// facing edge by the same width with the same curve, and whether the zones leave a picture.
    /// </summary>
    public string BlendReadback
    {
        get
        {
            if (_selectedPlacement is not { } p) return "";
            var arranged = _desk.BuildArranged();
            var mine = arranged.FirstOrDefault(a => a.Id == p.ScreenId);
            if (mine.Id is null) return "This screen is not in the arrangement.";
            var derived = EdgeBlend.Derive(mine.Rect, arranged.Where(a => a.Id != mine.Id).Select(a => a.Rect));
            var used = EdgeBlend.Resolve(p, derived);
            var words = $"left {used.Left} · top {used.Top} · right {used.Right} · bottom {used.Bottom} px";
            var head = !p.BlendAuto
                ? used.Any ? $"Fading {words}." : "No blend on this output."
                : used.Any
                    ? $"Overlaps found: {words} — every projector that shares them draws them, faded."
                    : "No overlap with another screen yet — drag this screen over its neighbour by the overlap width.";
            if (used.Any)
            {
                var deepest = BlackLevel.MaxCoverage(used);
                head += p.BlendBlackPct > 0
                    ? $" Black level: the picture between the zones is lifted {p.BlendBlackPct:0.#}% of white per missing projector to meet the {(deepest == 4 ? "corner where four meet" : "overlap")}."
                    : deepest == 4
                        ? " Black level: off — on a black scene the overlaps show as brighter bands and the corner where four projectors meet as a brighter square; a pedestal lifts the rest to match."
                        : " Black level: off — on a black scene the overlap shows as a brighter band; a pedestal lifts the rest to match.";
            }
            var notes = BlendAudit.For(p.ScreenId, arranged, id => State.Output.Placements.FirstOrDefault(x => x.ScreenId == id), NameOfScreen);
            return notes.Count == 0 ? head : head + "\n" + BlendAudit.Summary(notes);
        }
    }

    /// <summary>A screen's name for the blend audit: its label on the wall, else its id.</summary>
    private string NameOfScreen(string screenId)
    {
        var p = State.Output.Placements.FirstOrDefault(x => x.ScreenId == screenId);
        if (p is { CustomLabel.Length: > 0 }) return p.CustomLabel;
        var info = p is null ? null : _desk.LiveInfo(p);
        return info?.Label is { Length: > 0 } label ? label : screenId;
    }

    private void ResetBlend()
    {
        if (_selectedPlacement is not { } p) return;
        _services.BulkEdit(() =>
        {
            p.BlendAuto = false;
            p.BlendLeftPx = p.BlendTopPx = p.BlendRightPx = p.BlendBottomPx = 0;
            p.BlendCurve = BlendCurve.SCurve;
            p.BlendGamma = 1.0;
            p.BlendBlackPct = 0;
        });
        _desk.ReconcilePlacements();
        RaiseSelection();
    }

    private void RaiseBlend()
    {
        Raise(nameof(BlendReadback));
        Raise(nameof(SelectedBlendLeft));
        Raise(nameof(SelectedBlendTop));
        Raise(nameof(SelectedBlendRight));
        Raise(nameof(SelectedBlendBottom));
        Raise(nameof(SelectedBlendCurve));
        Raise(nameof(SelectedBlendGamma));
        Raise(nameof(SelectedBlendBlack));
        Raise(nameof(SelectedBlendAuto));
    }

    public RelayCommand ResetWarpCommand { get; }
    public RelayCommand ArrangeBlendGridCommand { get; }
    public RelayCommand ResetBlendCommand { get; }
    public RelayCommand ResetTrimsCommand { get; }

    // ---- wall gaps: bezels, the air between LED pillars -----------------------

    public EnumItem[] GapAxes => Lists.GapAxes;

    /// <summary>The selected screen's own dead strips — the rows on the page edit the model directly.</summary>
    public ObservableCollection<WallGap>? SelectedGaps => _selectedPlacement?.Gaps;

    /// <summary>The joined canvas the selected screen is in (its stored entry, made on demand), or null for a stand-alone screen.</summary>
    private CanvasNameConfig? SelectedCanvasConfig(bool create)
        => _selectedPlacement is { } p ? _services.RigEditor.CanvasConfigFor(p, create) : null;

    /// <summary>Bezel compensation of the canvas the selected screen is in: the dead width between two members side by side.</summary>
    public int SelectedSeamGapX
    {
        get => SelectedCanvasConfig(create: false)?.SeamGapX ?? 0;
        set
        {
            if (SelectedCanvasConfig(create: true) is { } e && e.SeamGapX != value)
            {
                e.SeamGapX = value;
                RaiseGaps();
            }
        }
    }

    /// <summary>…and between two members one above the other.</summary>
    public int SelectedSeamGapY
    {
        get => SelectedCanvasConfig(create: false)?.SeamGapY ?? 0;
        set
        {
            if (SelectedCanvasConfig(create: true) is { } e && e.SeamGapY != value)
            {
                e.SeamGapY = value;
                RaiseGaps();
            }
        }
    }

    private int _gapGridColumns = 2;
    private int _gapGridRows = 2;
    private int _gapGridPx = 40;

    /// <summary>The grid helper: panels packed in the selected screen's raster, and the gap between them.</summary>
    public int GapGridColumns { get => _gapGridColumns; set => Set(ref _gapGridColumns, Math.Clamp(value, 1, 64)); }
    public int GapGridRows { get => _gapGridRows; set => Set(ref _gapGridRows, Math.Clamp(value, 1, 64)); }
    public int GapGridPx { get => _gapGridPx; set => Set(ref _gapGridPx, Math.Clamp(value, 1, 4096)); }

    public RelayCommand AddGapCommand { get; }
    public RelayCommand<WallGap> RemoveGapCommand { get; }
    public RelayCommand SetGapsFromGridCommand { get; }
    public RelayCommand ClearGapsCommand { get; }

    /// <summary>The selected screen's raster as the room sees it: the display's rotation-aware size, or the planned one.</summary>
    private SKSizeI SelectedRasterSize() => _selectedPlacement is { } p ? _services.RigEditor.RasterOf(p) : SKSizeI.Empty;

    private void AddGap()
    {
        if (_selectedPlacement is not { } p) return;
        RigEditor.AddGap(p, SelectedRasterSize());
        RaiseGaps();
    }

    private void RemoveGap(WallGap? gap)
    {
        if (_selectedPlacement is not { } p || gap is null) return;
        p.Gaps.Remove(gap);
        RaiseGaps();
    }

    /// <summary>The strips of an even grid of panels packed in this screen's raster: columns − 1 vertical, rows − 1 horizontal, each the grid's gap wide.</summary>
    private void SetGapsFromGrid()
    {
        if (_selectedPlacement is not { } p) return;
        _services.RigEditor.SetGapsFromGrid(p, SelectedRasterSize(), _gapGridColumns, _gapGridRows, _gapGridPx);
        RaiseGaps();
        _desk.StatusMessage = $"{p.Gaps.Count} gap{(p.Gaps.Count == 1 ? "" : "s")} set from a {_gapGridColumns} × {_gapGridRows} grid, {_gapGridPx} px each.";
    }

    private void ClearGaps()
    {
        if (_selectedPlacement is not { } p) return;
        _services.RigEditor.ClearGaps(p);
        RaiseGaps();
    }

    /// <summary>What the selected screen's target lays out and shows — the same maths the outputs and the monitors use.</summary>
    public string GapSummary
    {
        get
        {
            if (_selectedPlacement is not { } p) return "";
            var geo = Rig.Geometry(State, _services.Screens.All);
            var map = geo.GapsOf(geo.TargetOf(p.ScreenId));
            if (map.IsEmpty) return map.Summary;
            var runs = map.Slices(geo.RasterRectOf(p.ScreenId)).Count;
            return runs > 1 ? $"{map.Summary} This output is cut into {runs} runs of pixels." : map.Summary;
        }
    }

    private string _gapSummarySeen = "";

    /// <summary>On the desk's tick: a gap row edited in place moves the words.</summary>
    public void Poll()
    {
        if (_selectedPlacement is not { Gaps.Count: > 0 }) return;
        var now = GapSummary;
        if (now == _gapSummarySeen) return;
        _gapSummarySeen = now;
        Raise(nameof(GapSummary));
    }

    private void RaiseGaps()
    {
        Raise(nameof(SelectedGaps));
        Raise(nameof(SelectedSeamGapX));
        Raise(nameof(SelectedSeamGapY));
        Raise(nameof(GapSummary));
        _desk.RebuildSwitcherTiles();   // the tiles' shapes follow the surface the content lays out on
    }

    // ---- roles, locks and repeaters ---------------------------------------------

    public EnumItem[] ScreenRoleItems => Lists.ScreenRoles;

    /// <summary>What the selected screen is for; picking a role also picks its follow default. Through the action layer: journaled, the frozen program agrees, a cue and the wire have the same verb.</summary>
    public ScreenRole SelectedRole
    {
        get => _selectedPlacement?.Role ?? ScreenRole.Main;
        set
        {
            if (_selectedPlacement is null || _selectedPlacement.Role == value) return;
            _services.Actions.Execute(new ShowAction(ShowActionKind.ScreenRole, _selectedPlacement.ScreenId, ScreenRoles.Word(value)), ActionOrigin.Desk);
            _desk.RebuildEditTargets();
            _desk.RefreshTakeScope();
            RaiseSelection();
            _desk.RebuildSwitcherTiles();   // the tile's badge and foot line read the group at once
        }
    }

    /// <summary>Off = locked: the screen keeps its picture through looks, cues, TAKE ALL and stingers.</summary>
    public bool SelectedFollowsCues
    {
        get => _selectedPlacement?.FollowsCues ?? true;
        set
        {
            if (_selectedPlacement is null || _selectedPlacement.FollowsCues == value) return;
            _desk.SetLocked(_selectedPlacement.ScreenId, !value);
            RaiseSelection();
        }
    }

    /// <summary>The target the selected screen repeats ("" = its own content); a repeater has no picture of its own.</summary>
    public string SelectedMirrorOf
    {
        get => _selectedPlacement?.MirrorOf ?? "";
        set
        {
            var wanted = value ?? "";
            if (_selectedPlacement is null || _selectedPlacement.MirrorOf == wanted) return;
            var placement = _selectedPlacement;
            _services.BulkEdit(() =>
            {
                placement.MirrorOf = wanted;
                if (wanted.Length > 0) placement.UseCustomPattern = false;
            });
            _desk.RebuildEditTargets();
            RaiseSelection();
            _desk.RebuildSwitcherTiles();   // the tile's foot line names what it repeats at once
        }
    }

    /// <summary>What the selected screen may repeat: nothing, or any other target that does not repeat it back.</summary>
    public ObservableCollection<EditTarget> MirrorSources { get; } = new();

    public void RebuildMirrorSources()
    {
        var wanted = new List<EditTarget> { new("— its own content", "") };
        var me = _selectedPlacement?.ScreenId;
        var geo = Rig.Geometry(State, _services.Screens.All);
        foreach (var key in geo.Targets)
        {
            if (key == me) continue;
            if (ContentTargets.IsCanvasKey(key))
            {
                if (me is not null && ContentTargets.Members(key).Contains(me)) continue;
            }
            else if (State.Output.Placements.FirstOrDefault(p => p.ScreenId == key)?.MirrorOf == me)
            {
                continue;
            }
            wanted.Add(new EditTarget(geo.LabelFor(State, key), key));
        }
        MainViewModel.ReplaceIfChanged(MirrorSources, wanted);
    }

    // ---- custom labels ------------------------------------------------------

    /// <summary>The selected screen's operator label (Outputs page).</summary>
    public string SelectedScreenLabel
    {
        get => _selectedPlacement?.CustomLabel ?? "";
        set
        {
            if (_selectedPlacement is { } p && p.CustomLabel != value)
            {
                p.CustomLabel = value;
                _desk.RefreshTargetNames(); // labels ripple into the strip, targets and remotes — in place, per keystroke
                Raise(nameof(SelectedScreenTitle));
            }
        }
    }

    /// <summary>True when the selected screen is part of a joined canvas (shows the name box).</summary>
    public bool SelectedIsInCanvas =>
        _selectedPlacement is { } p && _desk.CanvasGroups().Any(g => g.Any(m => m.ScreenId == p.ScreenId));

    /// <summary>The name of the canvas containing the selected screen ("Main wall").</summary>
    public string SelectedCanvasName
    {
        get => SelectedCanvasConfig(create: false)?.Name ?? "";
        set
        {
            if (SelectedCanvasConfig(create: true) is not { } entry || entry.Name == value) return;
            entry.Name = value;
            _desk.RefreshTargetNames();
        }
    }

    // ---- rotation & trims for the selected screen ---------------------------

    public OutputRotation SelectedRotation
    {
        get => _selectedPlacement?.Rotation ?? OutputRotation.None;
        set
        {
            if (_selectedPlacement is null) return;
            _selectedPlacement.Rotation = value;
            RaiseSelection();
        }
    }

    public double SelectedBrightness
    {
        get => _selectedPlacement?.BrightnessPct ?? 100;
        set { if (_selectedPlacement is not null) { _selectedPlacement.BrightnessPct = value; Raise(); } }
    }

    public double SelectedGamma
    {
        get => _selectedPlacement?.Gamma ?? 1.0;
        set { if (_selectedPlacement is not null) { _selectedPlacement.Gamma = value; Raise(); } }
    }

    public double SelectedTrimR
    {
        get => _selectedPlacement?.TrimRPct ?? 100;
        set { if (_selectedPlacement is not null) { _selectedPlacement.TrimRPct = value; Raise(); } }
    }

    public double SelectedTrimG
    {
        get => _selectedPlacement?.TrimGPct ?? 100;
        set { if (_selectedPlacement is not null) { _selectedPlacement.TrimGPct = value; Raise(); } }
    }

    public double SelectedTrimB
    {
        get => _selectedPlacement?.TrimBPct ?? 100;
        set { if (_selectedPlacement is not null) { _selectedPlacement.TrimBPct = value; Raise(); } }
    }

    // ---- planned screens and adoption -------------------------------------------

    public RelayCommand AddPlannedScreenCommand { get; }
    public RelayCommand<ScreenPlacement> RemovePlannedScreenCommand { get; }
    public RelayCommand<ScreenPlacement> AdoptPlannedScreenCommand { get; }
    public RelayCommand RefreshAdoptTargetsCommand { get; }

    /// <summary>Adds a screen that does not exist yet, so the whole rig can be built at the desk.</summary>
    public ScreenPlacement AddPlannedScreen(int width = 1920, int height = 1080, string label = "")
    {
        var placement = _services.RigEditor.AddPlannedScreen(width, height, label);
        _desk.RebuildEditTargets();
        _desk.RaiseModeChanged();
        _desk.StatusMessage = $"Planned screen added ({width}×{height}). Arrange, pattern and label it like any other.";
        return placement;
    }

    public void RemovePlannedScreen(ScreenPlacement placement)
    {
        if (!_services.RigEditor.RemovePlannedScreen(placement)) return; // a feed's own screen goes with its feed, never on its own
        _desk.RebuildEditTargets();
        _desk.RaiseModeChanged();
        _desk.StatusMessage = "Planned screen removed.";
    }

    /// <summary>
    /// At the venue: bind a planned screen onto a real display. Everything programmed against
    /// it — position, label, per-screen pattern, trims, warp, rotation and any look that
    /// names it — follows onto the hardware, so the desk work is not redone.
    /// </summary>
    public bool AdoptPlannedScreen(ScreenPlacement planned, string realScreenId)
    {
        if (_services.RigEditor.AdoptPlannedScreen(planned, realScreenId) is not { } info) return false; // not a planned display, or no such display here
        _desk.RebuildEditTargets();
        _desk.RaiseModeChanged();
        _desk.StatusMessage = $"Adopted onto {info.Label} ({info.Bounds.Width}×{info.Bounds.Height}) — everything programmed for it carried over.";
        Log.Info(_desk.StatusMessage);
        return true;
    }

    /// <summary>Real displays not already claimed by a placement — the adopt targets.</summary>
    public ObservableCollection<EditTarget> AdoptTargets { get; } = new();

    public void RefreshAdoptTargets()
    {
        AdoptTargets.Clear();
        foreach (var s in _services.Screens.Real)
        {
            AdoptTargets.Add(new EditTarget($"{s.Label} · {s.Bounds.Width}×{s.Bounds.Height}", s.Id));
        }
    }

    // ---- what a selection change re-reads ---------------------------------------------

    /// <summary>The label or the canvas name was typed elsewhere: the title re-reads.</summary>
    public void RaiseSelectionTitle() => Raise(nameof(SelectedScreenTitle));

    /// <summary>The arrangement moved (a join, a split, a drag): the words that read it.</summary>
    public void RaiseArrangement()
    {
        Raise(nameof(SelectedIsGrouped));
        Raise(nameof(BlendReadback));
    }

    /// <summary>Another screen is selected, or the selected one changed under the page: every line of the settings column re-reads it.</summary>
    public void RaiseSelection()
    {
        Raise(nameof(HasSelection));
        Raise(nameof(SelectedScreenTitle));
        Raise(nameof(SelectedEnabled));
        Raise(nameof(SelectedUseCustom));
        Raise(nameof(SelectedIsGrouped));
        _desk.RaiseGroupSummary();
        Raise(nameof(SelectedRotation));
        Raise(nameof(SelectedBrightness));
        Raise(nameof(SelectedGamma));
        Raise(nameof(SelectedTrimR));
        Raise(nameof(SelectedTrimG));
        Raise(nameof(SelectedTrimB));
        Raise(nameof(SelectedScreenLabel));
        Raise(nameof(SelectedCanvasName));
        Raise(nameof(SelectedIsInCanvas));
        Raise(nameof(SelectedFpsOverride));
        Raise(nameof(SelectedIsDisplay));
        Raise(nameof(SelectedDirectOutput));
        Raise(nameof(DirectOutputStatus));
        Raise(nameof(SelectedRole));
        Raise(nameof(SelectedFollowsCues));
        Raise(nameof(SelectedMirrorOf));
        RebuildMirrorSources();
        RaiseBlend();
        Raise(nameof(SelectedGaps));
        Raise(nameof(SelectedSeamGapX));
        Raise(nameof(SelectedSeamGapY));
        Raise(nameof(GapSummary));
        RefreshDisplayModes();
    }
}
