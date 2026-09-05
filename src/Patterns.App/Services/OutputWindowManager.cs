using Patterns.App.Rendering;
using Patterns.App.Views;
using Patterns.Core.Model;
using Patterns.Core.Rendering;
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

    public event Action? LiveChanged;

    /// <summary>Open (or retarget) output windows for the current arrangement.</summary>
    public void Apply()
    {
        var targets = BuildViewports(_services.State.Output.Placements, _services.Screens.All,
            masterFps: _services.State.Output.MasterFps, canvases: _services.State.Output.CanvasNames);
        if (targets.Count == 0)
        {
            Log.Warn("No enabled screens to output to.");
            LiveChanged?.Invoke();
            return;
        }

        var wanted = new HashSet<string>();
        foreach (var (screen, viewport) in targets)
        {
            wanted.Add(screen.Id);
            if (_windows.TryGetValue(screen.Id, out var existing))
            {
                existing.Pipeline.Viewport = viewport;
                existing.ApplyOptions();
                existing.NotifySnapshot();
            }
            else
            {
                var window = new OutputWindow(_services, screen, viewport);
                window.Closed += (_, _) =>
                {
                    _windows.Remove(screen.Id);
                    LiveChanged?.Invoke();
                };
                _windows[screen.Id] = window;
                window.Show();
            }
        }

        foreach (var id in _windows.Keys.Where(id => !wanted.Contains(id)).ToList())
        {
            CloseWindow(id);
        }

        LiveChanged?.Invoke();
        Log.Info($"Outputs live: {_windows.Count}.");
    }

    /// <summary>
    /// Pure mapping from arrangement to per-screen viewports — grouped screens get a span
    /// viewport over the group union; singles reference their own size (with per-screen
    /// pattern lookup enabled). Unit tested.
    /// </summary>
    public static List<(ScreenInfo Screen, PipelineViewport Viewport)> BuildViewports(
        IEnumerable<ScreenPlacement> placements, IReadOnlyList<ScreenInfo> screens, bool includePlanned = false, int masterFps = 0,
        IEnumerable<CanvasNameConfig>? canvases = null)
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
                    SKRectI.Create(x.Placement.X, x.Placement.Y, size.Width, size.Height),
                    x.Placement.BlendAuto);
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
                    new SKSizeI(union.Width, union.Height),
                    group.Select(m => (
                        SKRectI.Create(m.Rect.Left - union.Left, m.Rect.Top - union.Top, m.Rect.Width, m.Rect.Height),
                        (IEnumerable<WallGap>)byPlacement[m.Id].Gaps)),
                    canvasCfg?.SeamGapX ?? 0, canvasCfg?.SeamGapY ?? 0)
                : GapMap.ForScreen(new SKSizeI(group[0].Rect.Width, group[0].Rect.Height), byPlacement[group[0].Id].Gaps);
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
                        gaps.IsEmpty ? new SKSizeI(union.Width, union.Height) : gaps.Virtual,
                        gaps.VirtualOrigin(new SKPointI(region.Left, region.Top)),
                        canvasKey,
                        indexOf[member.Id],
                        info.Label)
                    : new PipelineViewport(
                        SinkKind.Output, gaps.IsEmpty ? SKSizeI.Empty : gaps.Virtual, default, member.Id, indexOf[member.Id], info.Label);
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
                    BlendLeftPx = blend.Left, BlendTopPx = blend.Top,
                    BlendRightPx = blend.Right, BlendBottomPx = blend.Bottom,
                    BlendCurve = placement.BlendCurve,
                    BlendGamma = placement.BlendGamma,
                    // The screen's own rate wins; else the master; 0 leaves the display's refresh.
                    TargetFps = placement.FpsOverride > 0 ? placement.FpsOverride : masterFps,
                };
                result.Add((info, viewport));
            }
        }
        return result;
    }

    /// <summary>The size a screen occupies in arrangement space (swapped for portrait rotations).</summary>
    public static SKSizeI EffectiveSize(ScreenPlacement placement, ScreenInfo info)
        => RigGeometry.EffectiveSize(placement, new SKSizeI(info.Bounds.Width, info.Bounds.Height));

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
