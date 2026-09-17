using Patterns.Core.Geometry;
using Patterns.App.Rendering;
using Patterns.App.Views;
using Patterns.Core.Model;
using Patterns.Rendering;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.App.Services;

/// <summary>
/// Opens/closes/retargets the fullscreen output windows from the screen arrangement:
/// screens dragged flush form one spanned canvas; stand-alone screens are independent
/// outputs; disabled screens get nothing. Reapplying is incremental (no fullscreen flicker).
/// </summary>
public sealed class OutputWindowManager
{
    private readonly AppServices _services;
    private readonly Dictionary<string, OutputWindow> _windows = new();

    public OutputWindowManager(AppServices services)
    {
        _services = services;
    }

    public bool IsLive => _windows.Count > 0;

    /// <summary>The open output windows (tests drive their keys; the wall reads their viewports).</summary>
    public IReadOnlyCollection<OutputWindow> Windows => _windows.Values;

    /// <summary>
    /// What the open windows are playing on, by the operator's own name for each — the words the
    /// ownership record carries, so a start that finds them can say which screens it took back.
    /// </summary>
    public IReadOnlyList<string> LiveTargetNames()
    {
        var labels = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var screen in _services.Screens.All)
        {
            labels[screen.Id] = screen.Label;
        }
        return _windows.Keys
            .Select(id => labels.TryGetValue(id, out var label) && label.Length > 0 ? label : id)
            .ToList();
    }

    public event Action? LiveChanged;

    /// <summary>Open (or retarget) output windows for the current arrangement.</summary>
    public void Apply()
    {
        // A standby twin's screens stay closed however the ask arrives — a hot-plug, a retarget, a remote.
        if (_services.OutputsHeldBy.Length > 0)
        {
            if (IsLive) CloseAll();
            Log.Info($"Outputs held closed: {_services.OutputsHeldBy}.");
            LiveChanged?.Invoke();
            return;
        }
        var targets = BuildViewports(_services.State.Output.Placements, _services.Screens.All,
            masterFps: _services.State.Output.MasterFps, canvases: _services.State.Output.CanvasNames,
            latticeOn: _services.RigEditor.LatticeOn, latticePoint: _services.RigEditor.LatticePoint, latticeTargets: _services.RigEditor.LatticeTargets,
            celebration: _services.RigDay.Celebration);
        if (targets.Count == 0)
        {
            Log.Warn("No enabled screens to output to.");
            LiveChanged?.Invoke();
            return;
        }

        var wanted = new HashSet<string>(targets.Select(t => t.Screen.Id));
        // Round 76: a display Windows re-identified after a hot-plug (a new index, a shifted origin) carries its
        // window over to the new id — the hot-plug pass says which old id became which (the same decision that
        // moved the screen's placement) — so every output the unplugged display had nothing to do with keeps its
        // window, its pipeline and its last frame. Only what no screen claims any more closes.
        var hotPlug = _services.HotPlug;
        var fresh = hotPlug is not null && hotPlug.Pass != _renamePassSeen;
        var renames = fresh ? hotPlug!.RecentRenames : null;
        if (fresh)
        {
            _renamePassSeen = hotPlug!.Pass;
            // A screen that went lost may have carried the very id a re-indexed display now has: its window
            // closes before any screen is matched to an id, or the stale window would answer for the new one —
            // the screen that has the id now gets its own window by carry-over or afresh.
            foreach (var id in hotPlug.RecentLost)
            {
                CloseWindow(id);
            }
        }
        foreach (var (screen, viewport) in targets)
        {
            if (_windows.TryGetValue(screen.Id, out var existing))
            {
                existing.Pipeline.Viewport = viewport;
                existing.ApplyOptions();
                existing.NotifySnapshot();
                continue;
            }
            var was = renames is null ? null : renames.FirstOrDefault(kv => kv.Value == screen.Id && !wanted.Contains(kv.Key) && _windows.ContainsKey(kv.Key)).Key;
            if (was is not null && _windows.Remove(was, out var carried))
            {
                carried.Retarget(screen, viewport);
                _windows[screen.Id] = carried;
                CarriedOver++;
                Log.Info($"Output window carried over to the re-identified display: {was} → {screen.Id} ({screen.Label}).");
                continue;
            }
            var window = new OutputWindow(_services, screen, viewport);
            window.Closed += (_, _) =>
            {
                // By the window's current id: a carry-over may have re-keyed it since it opened.
                if (_windows.TryGetValue(window.TargetScreenId, out var open) && ReferenceEquals(open, window)) _windows.Remove(window.TargetScreenId);
                LiveChanged?.Invoke();
            };
            _windows[screen.Id] = window;
            window.Show();
        }

        foreach (var id in _windows.Keys.Where(id => !wanted.Contains(id)).ToList())
        {
            CloseWindow(id);
        }

        LiveChanged?.Invoke();
        Log.Info($"Outputs live: {_windows.Count}.");
    }

    /// <summary>Round 76: how many windows were carried onto a re-identified display rather than opened afresh — the tests and STATE read it.</summary>
    public int CarriedOver { get; private set; }

    private int _renamePassSeen;

    /// <summary>
    /// Pure mapping from arrangement to per-screen viewports — grouped screens get a span
    /// viewport over the group union; singles reference their own size (with per-screen
    /// pattern lookup enabled). Unit tested.
    /// </summary>
    public static List<(ScreenInfo Screen, PipelineViewport Viewport)> BuildViewports(
        IEnumerable<ScreenPlacement> placements, IReadOnlyList<ScreenInfo> screens, bool includePlanned = false, int masterFps = 0,
        IEnumerable<CanvasNameConfig>? canvases = null, string latticeOn = "", int latticePoint = -1, IReadOnlyList<SKPoint>? latticeTargets = null,
        Patterns.Core.RigDay.Celebration? celebration = null)
    {
        var byId = screens.ToDictionary(s => s.Id);
        var live = new List<(ScreenPlacement Placement, ScreenInfo Info)>();
        foreach (var p in placements)
        {
            // Planned screens take part in every editor (and the wall), but there is no
            // display to open on — the output windows never see them.
            if (p.Enabled && byId.TryGetValue(p.ScreenId, out var info) && (includePlanned || !info.IsPlanned))
            {
                live.Add((p, info));
            }
        }

        var byPlacement = live.ToDictionary(x => x.Placement.ScreenId, x => x.Placement);
        var arranged = live
            .Select(x =>
            {
                var size = EffectiveSize(x.Placement, x.Info);
                return new ArrangedScreen(
                    x.Placement.ScreenId,
                    RasterRect.Create(x.Placement.X, x.Placement.Y, size.Width, size.Height),
                    x.Placement.BlendsOverlaps);
            })
            .ToList();
        var groups = ScreenLayout.Groups(arranged);

        // Stable operator-facing numbering: arrangement order, left-to-right then top-down.
        var ordered = arranged.OrderBy(a => a.Rect.Left).ThenBy(a => a.Rect.Top).ToList();
        var indexOf = ordered.Select((a, i) => (a.Id, Index: i + 1)).ToDictionary(x => x.Id, x => x.Index);

        var result = new List<(ScreenInfo, PipelineViewport)>();
        foreach (var group in groups)
        {
            var union = ScreenLayout.Union(group);
            // A joined canvas is one content target: its members render the canvas's own
            // pattern (or the program) through the span, keyed by the sorted member set.
            var canvasKey = group.Count > 1 ? CanvasNameConfig.KeyFor(group.Select(m => m.Id)) : null;
            // The wall's dead strips — the canvas's seams and every member's own — the same
            // maths the rig carries on the snapshot, so the outputs and the monitors agree.
            var canvasCfg = canvasKey is null ? null : canvases?.FirstOrDefault(c => c.MemberKey == canvasKey);
            var gaps = group.Count > 1
                ? GapMap.ForCanvas(
                    new RasterSize(union.Width, union.Height),
                    group.Select(m => (
                        RasterRect.Create(m.Rect.Left - union.Left, m.Rect.Top - union.Top, m.Rect.Width, m.Rect.Height),
                        (IEnumerable<WallGap>)byPlacement[m.Id].Gaps)),
                    canvasCfg?.SeamGapX ?? 0, canvasCfg?.SeamGapY ?? 0)
                : GapMap.ForScreen(new RasterSize(group[0].Rect.Width, group[0].Rect.Height), byPlacement[group[0].Id].Gaps);
            foreach (var member in group)
            {
                var info = byId[member.Id];
                var placement = byPlacement[member.Id];
                var region = group.Count > 1
                    ? SKRectI.Create(member.Rect.Left - union.Left, member.Rect.Top - union.Top, member.Rect.Width, member.Rect.Height)
                    : SKRectI.Create(0, 0, member.Rect.Width, member.Rect.Height);
                var viewport = group.Count > 1
                    ? new PipelineViewport(
                        SinkKind.Output,
                        gaps.IsEmpty ? new SKSizeI(union.Width, union.Height) : gaps.Virtual.ToSk(),
                        gaps.VirtualOrigin(new RasterPoint(region.Left, region.Top)).ToSk(),
                        canvasKey,
                        indexOf[member.Id],
                        info.Label)
                    : new PipelineViewport(
                        SinkKind.Output, gaps.IsEmpty ? SKSizeI.Empty : gaps.Virtual.ToSk(), default, member.Id, indexOf[member.Id], info.Label);
                viewport = viewport with { Gaps = gaps, RasterRegion = region };
                // The blend zones: the overlaps this screen has with every other live screen
                // (automatic), or the widths the operator typed. Only an output draws them.
                var blend = EdgeBlend.Resolve(placement,
                    EdgeBlend.Derive(member.Rect, arranged.Where(o => o.Id != member.Id).Select(o => o.Rect)));
                viewport = viewport with
                {
                    Rotation = placement.Rotation,
                    BrightnessPct = placement.BrightnessPct,
                    Gamma = placement.Gamma,
                    TrimRPct = placement.TrimRPct,
                    TrimGPct = placement.TrimGPct,
                    TrimBPct = placement.TrimBPct,
                    WarpTlx = placement.WarpTlx, WarpTly = placement.WarpTly,
                    WarpTrx = placement.WarpTrx, WarpTry = placement.WarpTry,
                    WarpBlx = placement.WarpBlx, WarpBly = placement.WarpBly,
                    WarpBrx = placement.WarpBrx, WarpBry = placement.WarpBry,
                    WarpTopBow = placement.WarpTopBow, WarpRightBow = placement.WarpRightBow,
                    WarpBottomBow = placement.WarpBottomBow, WarpLeftBow = placement.WarpLeftBow,
                    WarpMeshColumns = placement.WarpMeshColumns, WarpMeshRows = placement.WarpMeshRows, WarpMesh = placement.WarpMesh,
                    OutputId = placement.ScreenId,
                    ShowLattice = latticeOn.Length > 0 && latticeOn == placement.ScreenId,
                    LatticePoint = latticeOn.Length > 0 && latticeOn == placement.ScreenId ? latticePoint : -1,
                    LatticeTargets = latticeOn.Length > 0 && latticeOn == placement.ScreenId ? latticeTargets : null,
                    Celebration = latticeOn.Length > 0 && latticeOn == placement.ScreenId ? celebration : null,
                    BlendLeftPx = blend.Left, BlendTopPx = blend.Top,
                    BlendRightPx = blend.Right, BlendBottomPx = blend.Bottom,
                    BlendCurve = placement.BlendCurve,
                    BlendGamma = placement.BlendGamma,
                    BlendBlackPct = placement.BlendBlackPct,
                    BlendMaskPath = placement.BlendMaskPath,
                    // The screen's own rate wins; else the master; 0 leaves the display's refresh.
                    TargetFps = placement.FpsOverride > 0 ? placement.FpsOverride : masterFps,
                    DisplayHz = info.Hz,   // the display's own refresh: the rate the output paces to when it is the slower (OutputRate)
                    // Round 73: what Patterns is trying to push down this link — the contract's raster and rate when the
                    // engineer named one, else the output's own pixels at the rate asked — for the tech info chip.
                    PushSize = placement.EffectiveSignal is { Width: > 0, Height: > 0 } contract
                        ? new SKSizeI(contract.Width, contract.Height)
                        : new SKSizeI(member.Rect.Width, member.Rect.Height),
                    PushHz = placement.EffectiveSignal.Rate.IsSet ? placement.EffectiveSignal.Rate.Hz : 0,
                    MasterFps = masterFps,
                };
                result.Add((info, viewport));
            }
        }
        return result;
    }

    /// <summary>The size a screen occupies in arrangement space (swapped for portrait rotations).</summary>
    public static SKSizeI EffectiveSize(ScreenPlacement placement, ScreenInfo info)
        => RigGeometry.EffectiveSize(placement, new RasterSize(info.Bounds.Width, info.Bounds.Height)).ToSk();

    public void CloseAll()
    {
        foreach (var id in _windows.Keys.ToList())
        {
            CloseWindow(id);
        }
        LiveChanged?.Invoke();
        Log.Info("Outputs closed.");
    }

    private void CloseWindow(string id)
    {
        if (_windows.Remove(id, out var window))
        {
            try { window.Close(); }
            catch (Exception ex) { Log.Warn("Output window close failed.", ex); }
        }
    }

    /// <summary>Push a fresh snapshot notification into every live window.</summary>
    public void NotifySnapshot()
    {
        foreach (var w in _windows.Values)
        {
            w.NotifySnapshot();
        }
    }

    /// <summary>Called when screens changed: retarget live windows, drop vanished screens.</summary>
    public void OnScreensChanged()
    {
        if (IsLive) Apply();
    }
}
