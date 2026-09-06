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
    // ---- screen arrangement -------------------------------------------------

    private void OnScreensChanged()
    {
        AdoptRenamedDisplay();
        ReconcilePlacements();
        RefreshOutputsStatus();
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
        if (_selectedPlacement is null || LiveInfo(_selectedPlacement) is not { IsPlanned: false } info)
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
        if (_selectedPlacement is null || LiveInfo(_selectedPlacement) is not { IsPlanned: false } info) return;
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
    private void AdoptRenamedDisplay()
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
        _services.BulkEdit(() => ContentTargets.RenameScreen(State, oldId, replacement.Id));
        if (_services.Sandbox.ProgramState is { } air) ContentTargets.RenameScreen(air, oldId, replacement.Id);
        _services.RepublishNow();
        _modeChange = settled ? null : change with { ScreenId = replacement.Id };
        if (_selectedPlacement == placement) RaiseSelection();
        Log.Info($"Display re-identified after a mode change: {oldId} → {replacement.Id}.");
    }

    public void ReconcilePlacements() => ReconcilePlacements(_services.Screens.All.ToList());

    /// <summary>
    /// Displays that physically exist among the given list. The "primary goes off when there
    /// are other screens" default must count these only — a planned screen has no hardware,
    /// so letting it tip the count would turn off the operator's one real output.
    /// </summary>
    private static int RealCount(IReadOnlyList<ScreenInfo> screens)
    {
        var n = 0;
        foreach (var s in screens)
        {
            if (!s.IsPlanned) n++;
        }
        return n;
    }

    /// <summary>
    /// Keeps placements in sync with detected screens: new screens appear to the right of the
    /// arrangement (disconnected), and — until the operator pins a choice — the primary screen
    /// defaults to disabled whenever other screens exist, so GO never covers the control UI.
    /// </summary>
    public void ReconcilePlacements(IReadOnlyList<ScreenInfo> screens)
    {
        var placements = State.Output.Placements;

        foreach (var screen in screens)
        {
            if (placements.All(p => p.ScreenId != screen.Id))
            {
                var maxRight = 0;
                foreach (var p in placements)
                {
                    var info = screens.FirstOrDefault(s => s.Id == p.ScreenId);
                    if (info is not null) maxRight = Math.Max(maxRight, p.X + info.Bounds.Width);
                }
                placements.Add(new ScreenPlacement
                {
                    ScreenId = screen.Id,
                    X = placements.Count == 0 ? 0 : maxRight + 120,
                    Y = 0,
                    Enabled = !(screen.IsPrimary && RealCount(screens) > 1),
                });
            }
        }

        // Re-evaluate the default for anything the user hasn't pinned.
        foreach (var p in placements)
        {
            if (p.UserPinned) continue;
            var info = screens.FirstOrDefault(s => s.Id == p.ScreenId);
            if (info is not null)
            {
                p.Enabled = !(info.IsPrimary && RealCount(screens) > 1);
            }
        }

        if (_selectedPlacement is null || placements.All(p => p != _selectedPlacement))
        {
            SelectedPlacement = placements.FirstOrDefault(p => LiveInfo(p) is not null);
        }

        EnsureAssignmentsForCustomScreens();
        RebuildEditTargets();
        RebuildNdiSources();
        RebuildStreamSources();
        RebuildMultiviewTargets();
        RaiseArrangement();
        // Loading a show, or plugging a display in, can change the mode and the planned set.
        RefreshOutputsStatus();
    }

    public ScreenInfo? LiveInfo(ScreenPlacement placement)
        => _services.Screens.All.FirstOrDefault(s => s.Id == placement.ScreenId);

    public ScreenPlacement? SelectedPlacement
    {
        get => _selectedPlacement;
        set
        {
            if (Set(ref _selectedPlacement, value))
            {
                RaiseSelection();
            }
        }
    }

    public bool HasSelection => _selectedPlacement is not null && LiveInfo(_selectedPlacement) is not null;

    public string SelectedScreenTitle
    {
        get
        {
            if (_selectedPlacement is null) return "No screen selected";
            var info = LiveInfo(_selectedPlacement);
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
                EnsureAssignment(_selectedPlacement.ScreenId);
            }
            RebuildEditTargets();
            if (value)
            {
                EditTarget = EditTargets.FirstOrDefault(t => t.ScreenId == _selectedPlacement.ScreenId) ?? EditTargets[0];
            }
            RaiseSelection();
        }
    }

    /// <summary>A display with hardware behind it — the only kind an output window opens on, so the only kind direct output applies to.</summary>
    public bool SelectedIsDisplay => _selectedPlacement is { Planned: false } p && LiveInfo(p) is { IsPlanned: false, IsVirtual: false };

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
            Raise(nameof(DirectOutputSummary));
        }
    }

    /// <summary>The selected output's direct-output line: in force, waiting for a restart, or why not.</summary>
    public string DirectOutputStatus => _selectedPlacement is null ? "" : DirectOutputService.Status(State, _selectedPlacement);

    /// <summary>The Machine page's line: how many outputs ask, what is in force, what the next start does.</summary>
    public string DirectOutputSummary => DirectOutputService.Summary(State);

    /// <summary>Custom patterns only make sense on stand-alone screens (groups span the program).</summary>
    public bool SelectedIsGrouped
    {
        get
        {
            if (_selectedPlacement is null) return false;
            var arranged = BuildArranged();
            var mine = arranged.FirstOrDefault(a => a.Id == _selectedPlacement.ScreenId);
            if (mine.Id is null) return false;
            return ScreenLayout.Groups(arranged).First(g => g.Any(a => a.Id == mine.Id)).Count > 1;
        }
    }

    public List<ArrangedScreen> BuildArranged()
    {
        var result = new List<ArrangedScreen>();
        foreach (var p in State.Output.Placements)
        {
            var info = LiveInfo(p);
            if (info is not null && p.Enabled)
            {
                var size = OutputWindowManager.EffectiveSize(p, info);
                result.Add(new ArrangedScreen(p.ScreenId, SKRectI.Create(p.X, p.Y, size.Width, size.Height), p.BlendAuto));
            }
        }
        return result;
    }

    // ---- edge blend ---------------------------------------------------------

    public bool SelectedBlendAuto
    {
        get => _selectedPlacement?.BlendAuto ?? false;
        set
        {
            if (_selectedPlacement is null || _selectedPlacement.BlendAuto == value) return;
            _selectedPlacement.BlendAuto = value;
            ReconcilePlacements(); // an overlap may now join (or leave) a canvas
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
            var arranged = BuildArranged();
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
            var notes = BlendAudit.For(p.ScreenId, arranged, id => State.Output.Placements.FirstOrDefault(x => x.ScreenId == id), NameOfScreen);
            return notes.Count == 0 ? head : head + "\n" + BlendAudit.Summary(notes);
        }
    }

    /// <summary>A screen's name for the blend audit: its label on the wall, else its id.</summary>
    private string NameOfScreen(string screenId)
    {
        var p = State.Output.Placements.FirstOrDefault(x => x.ScreenId == screenId);
        if (p is { CustomLabel.Length: > 0 }) return p.CustomLabel;
        var info = p is null ? null : LiveInfo(p);
        return info?.Label is { Length: > 0 } label ? label : screenId;
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
        Raise(nameof(SelectedBlendAuto));
    }

    // ---- wall gaps: bezels, the air between LED pillars -----------------------

    public EnumItem[] GapAxes => Lists.GapAxes;

    /// <summary>The selected screen's own dead strips — the rows on the page edit the model directly.</summary>
    public System.Collections.ObjectModel.ObservableCollection<WallGap>? SelectedGaps => _selectedPlacement?.Gaps;

    /// <summary>The joined canvas the selected screen is in (its stored entry, made on demand), or null for a stand-alone screen.</summary>
    private CanvasNameConfig? SelectedCanvasConfig(bool create)
    {
        if (_selectedPlacement is not { } p) return null;
        var group = CanvasGroups().FirstOrDefault(g => g.Any(m => m.ScreenId == p.ScreenId));
        if (group is null) return null;
        var key = CanvasNameConfig.KeyFor(group.Select(m => m.ScreenId));
        var entry = State.Output.CanvasNames.FirstOrDefault(c => c.MemberKey == key);
        if (entry is null && create)
        {
            entry = new CanvasNameConfig { MemberKey = key };
            State.Output.CanvasNames.Add(entry);
        }
        return entry;
    }

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

    /// <summary>The selected screen's raster as the room sees it: the display's rotation-aware size, or the planned one.</summary>
    private SKSizeI SelectedRasterSize()
    {
        if (_selectedPlacement is not { } p) return SKSizeI.Empty;
        var info = LiveInfo(p);
        return info is null ? new SKSizeI(p.PlannedWidth, p.PlannedHeight) : OutputWindowManager.EffectiveSize(p, info);
    }

    private void AddGap()
    {
        if (_selectedPlacement is not { } p) return;
        var size = SelectedRasterSize();
        p.Gaps.Add(new WallGap { Axis = GapAxis.Vertical, At = Math.Max(1, size.Width / 2), Size = 100 });
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
        var size = SelectedRasterSize();
        _services.BulkEdit(() =>
        {
            p.Gaps.Clear();
            for (var k = 1; k < _gapGridColumns; k++)
            {
                p.Gaps.Add(new WallGap { Axis = GapAxis.Vertical, At = (int)Math.Round(size.Width * (double)k / _gapGridColumns), Size = _gapGridPx });
            }
            for (var k = 1; k < _gapGridRows; k++)
            {
                p.Gaps.Add(new WallGap { Axis = GapAxis.Horizontal, At = (int)Math.Round(size.Height * (double)k / _gapGridRows), Size = _gapGridPx });
            }
        });
        RaiseGaps();
        StatusMessage = $"{p.Gaps.Count} gap{(p.Gaps.Count == 1 ? "" : "s")} set from a {_gapGridColumns} × {_gapGridRows} grid, {_gapGridPx} px each.";
    }

    private void ClearGaps()
    {
        if (_selectedPlacement is not { } p) return;
        _services.BulkEdit(() =>
        {
            p.Gaps.Clear();
            if (SelectedCanvasConfig(create: false) is { } e)
            {
                e.SeamGapX = 0;
                e.SeamGapY = 0;
            }
        });
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

    private void RaiseGaps()
    {
        Raise(nameof(SelectedGaps));
        Raise(nameof(SelectedSeamGapX));
        Raise(nameof(SelectedSeamGapY));
        Raise(nameof(GapSummary));
        RebuildSwitcherTiles();   // the tiles' shapes follow the surface the content lays out on
    }

    // ---- roles, locks and repeaters ---------------------------------------------

    public EnumItem[] ScreenRoleItems => Lists.ScreenRoles;

    /// <summary>What the selected screen is for; picking a role also picks its follow default.</summary>
    public ScreenRole SelectedRole
    {
        get => _selectedPlacement?.Role ?? ScreenRole.Main;
        set
        {
            if (_selectedPlacement is null || _selectedPlacement.Role == value) return;
            _selectedPlacement.Role = value;
            var follows = ScreenRoles.DefaultFollows(value);
            if (_selectedPlacement.FollowsCues != follows) SetLocked(_selectedPlacement.ScreenId, !follows);
            RaiseSelection();
            RebuildSwitcherTiles();   // the tile's badge and foot line read the group at once
        }
    }

    /// <summary>Off = locked: the screen keeps its picture through looks, cues, TAKE ALL and stingers.</summary>
    public bool SelectedFollowsCues
    {
        get => _selectedPlacement?.FollowsCues ?? true;
        set
        {
            if (_selectedPlacement is null || _selectedPlacement.FollowsCues == value) return;
            SetLocked(_selectedPlacement.ScreenId, !value);
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
            RebuildEditTargets();
            RaiseSelection();
            RebuildSwitcherTiles();   // the tile's foot line names what it repeats at once
        }
    }

    /// <summary>What the selected screen may repeat: nothing, or any other target that does not repeat it back.</summary>
    public ObservableCollection<EditTarget> MirrorSources { get; } = new();

    private void RebuildMirrorSources()
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
        ReplaceIfChanged(MirrorSources, wanted);
    }

    /// <summary>A lock goes through the action layer (journaled, the sandbox and the air agree); OWN lights up when the lock gave the target its picture.</summary>
    private void SetLocked(string targetId, bool locked)
    {
        _services.Actions.Execute(new ShowAction(locked ? ShowActionKind.ScreenLock : ShowActionKind.ScreenUnlock, targetId), ActionOrigin.Desk);
        RebuildEditTargets();
        RefreshTakeScope();
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
                RebuildEditTargets(); // labels ripple into the strip, targets and remotes
            }
        }
    }

    /// <summary>True when the selected screen is part of a joined canvas (shows the name box).</summary>
    public bool SelectedIsInCanvas =>
        _selectedPlacement is { } p && CanvasGroups().Any(g => g.Any(m => m.ScreenId == p.ScreenId));

    /// <summary>The name of the canvas containing the selected screen ("Main wall").</summary>
    public string SelectedCanvasName
    {
        get
        {
            if (_selectedPlacement is not { } p) return "";
            var group = CanvasGroups().FirstOrDefault(g => g.Any(m => m.ScreenId == p.ScreenId));
            if (group is null) return "";
            var key = CanvasNameConfig.KeyFor(group.Select(m => m.ScreenId));
            return State.Output.CanvasNames.FirstOrDefault(c => c.MemberKey == key)?.Name ?? "";
        }
        set
        {
            if (_selectedPlacement is not { } p) return;
            var group = CanvasGroups().FirstOrDefault(g => g.Any(m => m.ScreenId == p.ScreenId));
            if (group is null) return;
            var key = CanvasNameConfig.KeyFor(group.Select(m => m.ScreenId));
            var entry = State.Output.CanvasNames.FirstOrDefault(c => c.MemberKey == key);
            if (entry is null)
            {
                entry = new CanvasNameConfig { MemberKey = key };
                State.Output.CanvasNames.Add(entry);
            }
            if (entry.Name != value)
            {
                entry.Name = value;
                RebuildSwitcherTiles();
            }
        }
    }

    /// <summary>Nickname for the live input currently picked in Media (NDI feed or capture).</summary>
    public string InputNickname
    {
        get => CurrentInputKey() is { } key ? State.InputLabel(key, "") : "";
        set
        {
            if (CurrentInputKey() is not { } key) return;
            var entry = State.InputLabels.FirstOrDefault(l => l.Key == key);
            if (entry is null)
            {
                entry = new InputLabelConfig { Key = key };
                State.InputLabels.Add(entry);
            }
            entry.Label = value;
        }
    }

    private string? CurrentInputKey() => ActivePattern.Media.Source switch
    {
        MediaSource.NdiFeed when ActivePattern.Media.NdiSourceName.Length > 0 => "ndi:" + ActivePattern.Media.NdiSourceName,
        MediaSource.Capture when ActivePattern.Media.CaptureDevice.Length > 0 => "cap:" + ActivePattern.Media.CaptureDevice,
        MediaSource.Web when ActivePattern.Media.WebUrl.Length > 0 => InputKeys.Web(ActivePattern.Media.WebUrl),
        _ => null,
    };

    // ---- remote screen/group switching --------------------------------------

    private List<(ScreenPlacement Placement, ScreenInfo Info)> OrderedLivePlacements(IReadOnlyList<ScreenInfo>? screens = null)
        => Rig.OrderedLivePlacements(State, screens ?? _services.Screens.All);

    /// <summary>Remote: screen by its overview number → enabled/disabled/toggled.</summary>
    public bool SetScreenEnabled(int number, bool? target, IReadOnlyList<ScreenInfo>? screens = null)
        => _services.Actions.SetScreenEnabled(number, target, screens);

    /// <summary>Joined-canvas letters (A, B, …) → their member placements, arrangement order.</summary>
    private List<List<ScreenPlacement>> CanvasGroups(IReadOnlyList<ScreenInfo>? screens = null)
        => Rig.CanvasGroups(State, screens ?? _services.Screens.All);

    /// <summary>Remote: every screen of canvas 'A'/'B'… on or off at once.</summary>
    public bool SetGroupEnabled(string letter, bool enabled, IReadOnlyList<ScreenInfo>? screens = null)
        => _services.Actions.SetGroupEnabled(letter, enabled, screens);

    /// <summary>Screen rows for the remote-state JSON. UI thread.</summary>
    public object[] RemoteScreens(IReadOnlyList<ScreenInfo>? screens = null)
        => _services.Actions.RemoteScreens(screens);

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

    // ---- NDI ----------------------------------------------------------------

    // NDI source entries use "" (not null) for Program, matching NdiSenderConfig.SourceScreenId.
    public ObservableCollection<EditTarget> NdiSources { get; } = new() { new EditTarget("Program", "") };

    public string[] NdiRateKeys => NdiRateTable.Keys;

    private void RebuildNdiSources()
    {
        var wanted = new List<EditTarget> { new("Program", "") };
        // Additive: every entry the picker offered before is still offered, so a stored
        // SourceScreenId can never be blanked by the combo's two-way binding.
        var geo = Rig.Geometry(State, _services.Screens.All);
        foreach (var key in geo.Targets)
        {
            if (ContentTargets.IsCanvasKey(key)) wanted.Add(new EditTarget(geo.LabelFor(State, key), key));
        }
        foreach (var s in _services.Screens.All)
        {
            if (s.IsVirtual) continue; // the feeds' own screens are listed by their feed below
            wanted.Add(new EditTarget($"Screen {s.Index + 1} — {s.Label}", s.Id));
        }
        foreach (var sender in State.Ndi.Senders)
        {
            var name = string.IsNullOrWhiteSpace(sender.Name) ? "Patterns" : sender.Name.Trim();
            wanted.Add(new EditTarget($"Its own screen — NDI · {name} (a look of its own)", sender.OwnScreenId));
        }
        if (State.Stream.UsesOwnScreen) wanted.Add(new EditTarget("The stream's own screen", StreamConfig.OwnScreenId));
        ReplaceIfChanged(NdiSources, wanted);
    }

    private void AddNdiSender()
    {
        var n = State.Ndi.Senders.Count + 1;
        State.Ndi.Senders.Add(new NdiSenderConfig
        {
            Name = n == 1 ? "Patterns" : $"Patterns {n}",
            Enabled = false,
        });
        SyncVirtualScreens(); // every send owns a screen of its own from the moment it exists
        StatusMessage = "NDI sender added — it owns a screen on the rig: mirror any target, or give it a look of its own.";
    }

    // ---- prep mode -----------------------------------------------------------

    /// <summary>Pre-programming at the desk: outputs are held closed, planned screens stand in for the rig.</summary>
    public bool IsPrepMode
    {
        get => State.Mode == ShowMode.Prep;
        set
        {
            var mode = value ? ShowMode.Prep : ShowMode.Show;
            if (State.Mode == mode) return;
            if (value && _services.Outputs.IsLive)
            {
                _services.Outputs.CloseAll(); // prep never leaves something on the screens
            }
            State.Mode = mode;
            RaiseModeChanged();
            StatusMessage = value
                ? "PREP — build the rig, screens, inputs and looks; the outputs stay held until you switch to SHOW."
                : "SHOW — outputs can open. Planned screens still need adopting onto real displays.";
            Log.Info(StatusMessage);
        }
    }

    public string ModeBanner => IsPrepMode
        ? "PREP MODE — pre-programming; outputs are held closed"
        : "SHOW MODE";

    /// <summary>Planned screens that have no display behind them yet (blocks a clean GO). A feed's own screen is not one.</summary>
    public int PlannedScreenCount => State.Output.Placements.Count(p => p.IsPlannedDisplay);

    /// <summary>The feeds' own screens on the rig: one per NDI send, one for the stream while it is set to its own.</summary>
    public int VirtualScreenCount => State.Output.Placements.Count(p => p.IsVirtual);

    /// <summary>Keeps the feeds' screens in step with the senders and the stream; on the poll, and after every add or remove.</summary>
    public void SyncVirtualScreens()
    {
        if (!VirtualScreens.Sync(State)) return;
        _services.Screens.Refresh();
        RebuildEditTargets();
        RebuildNdiSources();
        RebuildStreamSources();
        Raise(nameof(VirtualScreenCount));
        Raise(nameof(PlannedScreenCount));
        RaiseModeChanged();
    }

    /// <summary>What the stream can show: a display captured off the desktop, or a rig target rendered by the engine.</summary>
    public ObservableCollection<EditTarget> StreamSources { get; } = new() { new EditTarget("Primary screen (desktop capture)", "") };

    private void RebuildStreamSources()
    {
        var wanted = new List<EditTarget> { new("Primary screen (desktop capture)", "") };
        foreach (var s in _services.Screens.Real)
        {
            wanted.Add(new EditTarget($"Screen {s.Index + 1} — {s.Label} (desktop capture)", s.Id));
        }
        wanted.Add(new EditTarget("Its own screen — rendered, a look of its own", StreamConfig.OwnScreenId));
        var geo = Rig.Geometry(State, _services.Screens.All);
        foreach (var key in geo.Targets)
        {
            if (ContentTargets.IsCanvasKey(key)) wanted.Add(new EditTarget($"{geo.LabelFor(State, key)} (rendered)", key));
        }
        foreach (var p in State.Output.Placements)
        {
            if (p.IsPlannedDisplay) wanted.Add(new EditTarget($"{LabelFor(p)} (planned, rendered)", p.ScreenId));
        }
        ReplaceIfChanged(StreamSources, wanted);
    }

    public string PrepSummary
    {
        get
        {
            var planned = PlannedScreenCount;
            var real = _services.Screens.Real.Count;
            return planned == 0
                ? $"{real} display{(real == 1 ? "" : "s")} detected · no planned screens"
                : $"{real} display{(real == 1 ? "" : "s")} detected · {planned} planned screen{(planned == 1 ? "" : "s")} waiting to be adopted";
        }
    }

    private void RaiseModeChanged()
    {
        RefreshOutputsStatus();
        RaiseShell();
    }

    private void GoLive() => _services.Actions.Execute(ShowActionKind.OutputsOn, ActionOrigin.Desk);

    /// <summary>Adds a screen that does not exist yet, so the whole rig can be built at the desk.</summary>
    public ScreenPlacement AddPlannedScreen(int width = 1920, int height = 1080, string label = "")
    {
        var placement = new ScreenPlacement
        {
            ScreenId = ScreenPlacement.PlannedIdPrefix + Guid.NewGuid().ToString("N")[..8],
            Planned = true,
            PlannedWidth = width,
            PlannedHeight = height,
            CustomLabel = label,
            Enabled = true,
            UserPinned = true,
            X = NextPlannedX(),
        };
        State.Output.Placements.Add(placement);
        _services.Screens.Refresh();
        RebuildEditTargets();
        RaiseModeChanged();
        StatusMessage = $"Planned screen added ({width}×{height}). Arrange, pattern and label it like any other.";
        return placement;
    }

    /// <summary>Places a new planned screen to the right of everything already arranged.</summary>
    private int NextPlannedX()
    {
        var right = 0;
        foreach (var (placement, info) in OrderedLivePlacements())
        {
            right = Math.Max(right, placement.X + OutputWindowManager.EffectiveSize(placement, info).Width);
        }
        return right;
    }

    public void RemovePlannedScreen(ScreenPlacement placement)
    {
        if (!placement.IsPlannedDisplay) return; // a feed's own screen goes with its feed, never on its own
        State.Output.Placements.Remove(placement);
        var assignment = State.Independent.FirstOrDefault(a => a.ScreenId == placement.ScreenId);
        if (assignment is not null) State.Independent.Remove(assignment);
        _services.Screens.Refresh();
        RebuildEditTargets();
        RaiseModeChanged();
        StatusMessage = "Planned screen removed.";
    }

    /// <summary>
    /// At the venue: bind a planned screen onto a real display. Everything programmed against
    /// it — position, label, per-screen pattern, trims, warp, rotation and any look that
    /// names it — follows onto the hardware, so the desk work is not redone.
    /// </summary>
    public bool AdoptPlannedScreen(ScreenPlacement planned, string realScreenId)
    {
        if (!planned.IsPlannedDisplay || realScreenId.Length == 0) return false; // a feed's own screen is never a display
        if (_services.Screens.Real.All(s => s.Id != realScreenId)) return false;

        var oldId = planned.ScreenId;
        if (State.Output.Placements.FirstOrDefault(p => p.ScreenId == realScreenId) is { } existing)
        {
            // That display already has a placement — retire it and let the planned one take over.
            State.Output.Placements.Remove(existing);
            var stale = State.Independent.FirstOrDefault(a => a.ScreenId == realScreenId);
            if (stale is not null) State.Independent.Remove(stale);
        }

        _services.BulkEdit(() =>
        {
            planned.Planned = false;
            ContentTargets.RenameScreen(State, oldId, realScreenId);
        });

        // The rig lives in the frozen program too while EDIT SAFE is on — adopt there as well,
        // or the audience keeps the planned screen the operator just replaced.
        if (_services.Sandbox.ProgramState is { } air)
        {
            foreach (var p in air.Output.Placements.Where(p => p.ScreenId == oldId))
            {
                p.Planned = false;
            }
            ContentTargets.RenameScreen(air, oldId, realScreenId);
            _services.RepublishNow();
        }

        _services.Screens.Refresh();
        RebuildEditTargets();
        RaiseModeChanged();
        var info = _services.Screens.Real.First(s => s.Id == realScreenId);
        StatusMessage = $"Adopted onto {info.Label} ({info.Bounds.Width}×{info.Bounds.Height}) — everything programmed for it carried over.";
        Log.Info(StatusMessage);
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

    // ---- live-input pool -----------------------------------------------------

    private string _activeInputsText = "No live inputs mounted.";
    public string ActiveInputsText { get => _activeInputsText; private set => Set(ref _activeInputsText, value); }

    private void RefreshActiveInputs()
    {
        var rows = new List<string>();
        foreach (var (key, status) in _services.Video.MountStatuses.Concat(_services.NdiIn.MountStatuses).Concat(_services.WebIn.MountStatuses))
        {
            var bare = key.Length > 4 ? key[4..] : key;
            var label = key.StartsWith("vid:", StringComparison.Ordinal)
                ? Path.GetFileName(bare)
                : key.StartsWith("web:", StringComparison.Ordinal)
                    ? State.InputLabel(key, WebAddress.ShortName(bare))
                    : State.InputLabel(key, bare);
            rows.Add($"{label} — {status}");
        }
        var notes = string.Join("  ",
            new[] { _services.Video.LimitNote, _services.NdiIn.LimitNote, _services.WebIn.LimitNote }.Where(s => s.Length > 0));
        var text = rows.Count == 0
            ? "No live inputs mounted."
            : $"Live inputs ({rows.Count}): {string.Join("  ·  ", rows)}";
        ActiveInputsText = notes.Length > 0 ? $"{text}  {notes}" : text;
    }
}
