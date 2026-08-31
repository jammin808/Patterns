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

    public event Action? LiveChanged;

    /// <summary>Open (or retarget) output windows for the current arrangement.</summary>
    public void Apply()
    {
        var targets = BuildViewports(_services.State.Output.Placements, _services.Screens.All);
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
        IEnumerable<ScreenPlacement> placements, IReadOnlyList<ScreenInfo> screens)
    {
        var byId = screens.ToDictionary(s => s.Id);
        var live = new List<(ScreenPlacement Placement, ScreenInfo Info)>();
        foreach (var p in placements)
        {
            // Planned screens take part in every editor, but there is no display to open on.
            if (p.Enabled && byId.TryGetValue(p.ScreenId, out var info) && !info.IsPlanned)
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
                    SKRectI.Create(x.Placement.X, x.Placement.Y, size.Width, size.Height));
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
            foreach (var member in group)
            {
                var info = byId[member.Id];
                var placement = byPlacement[member.Id];
                var viewport = group.Count > 1
                    ? new PipelineViewport(
                        SinkKind.Output,
                        new SKSizeI(union.Width, union.Height),
                        new SKPointI(member.Rect.Left - union.Left, member.Rect.Top - union.Top),
                        null,
                        indexOf[member.Id],
                        info.Label)
                    : new PipelineViewport(
                        SinkKind.Output, SKSizeI.Empty, default, member.Id, indexOf[member.Id], info.Label);
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
                };
                result.Add((info, viewport));
            }
        }
        return result;
    }

    /// <summary>The size a screen occupies in arrangement space (swapped for portrait rotations).</summary>
    public static SKSizeI EffectiveSize(ScreenPlacement placement, ScreenInfo info)
        => placement.Rotation is OutputRotation.Rot90 or OutputRotation.Rot270
            ? new SKSizeI(info.Bounds.Height, info.Bounds.Width)
            : new SKSizeI(info.Bounds.Width, info.Bounds.Height);

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
