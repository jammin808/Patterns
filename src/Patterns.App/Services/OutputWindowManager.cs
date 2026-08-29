using Avalonia;
using Patterns.App.Rendering;
using Patterns.App.Views;
using Patterns.Core.Model;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.App.Services;

/// <summary>
/// Opens/closes/retargets the fullscreen output windows for the current mode. Reapplying is
/// incremental: existing windows are retargeted instead of recreated (no fullscreen flicker).
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

    /// <summary>Open (or retarget) output windows for the current output config.</summary>
    public void Apply()
    {
        var state = _services.State;
        var targets = _services.Screens.Resolve(state.Output.SelectedScreenIds);
        if (targets.Count == 0)
        {
            Log.Warn("No screens available for output.");
            return;
        }

        // Span union in device pixels.
        var union = targets[0].Bounds;
        foreach (var s in targets.Skip(1)) union = union.Union(s.Bounds);

        var wanted = new HashSet<string>();
        for (var i = 0; i < targets.Count; i++)
        {
            var screen = targets[i];
            wanted.Add(screen.Id);
            var viewport = BuildViewport(state.Output.Mode, screen, i, union);

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
        Log.Info($"Outputs live: {_windows.Count} ({state.Output.Mode}).");
    }

    private static PipelineViewport BuildViewport(OutputMode mode, ScreenInfo screen, int index, PixelRect union)
        => mode switch
        {
            OutputMode.Span => new PipelineViewport(
                SinkKind.Output,
                new SKSizeI(union.Width, union.Height),
                new SKPointI(screen.Bounds.X - union.X, screen.Bounds.Y - union.Y),
                null,
                index + 1,
                screen.Label),
            OutputMode.Independent => new PipelineViewport(
                SinkKind.Output, SKSizeI.Empty, default, screen.Id, index + 1, screen.Label),
            _ => new PipelineViewport(
                SinkKind.Output, SKSizeI.Empty, default, null, index + 1, screen.Label),
        };

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
