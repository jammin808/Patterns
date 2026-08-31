using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// A screen the show can address: a detected display, or — while pre-programming — a planned
/// one with no hardware behind it yet (<see cref="IsPlanned"/>).
/// </summary>
public sealed record ScreenInfo(string Id, string Label, PixelRect Bounds, double Scaling, bool IsPrimary, int Index,
    bool IsPlanned = false)
{
    public string Description => IsPlanned
        ? $"{Bounds.Width}×{Bounds.Height} · planned (no display yet)"
        : $"{Bounds.Width}×{Bounds.Height} @ {Bounds.X},{Bounds.Y}{(IsPrimary ? " · primary" : "")}";
}

/// <summary>Enumerates screens off the main window and tracks hot-plug changes.</summary>
public sealed class ScreenService
{
    private Screens? _screens;

    /// <summary>Detected displays plus any planned (pre-programmed) screens.</summary>
    public ObservableCollection<ScreenInfo> All { get; } = new();

    /// <summary>Only the displays that physically exist — the ones outputs can open on.</summary>
    public IReadOnlyList<ScreenInfo> Real => All.Where(s => !s.IsPlanned).ToList();

    /// <summary>
    /// Supplies the planned screens to merge in after each refresh. Set by the composition
    /// root so this service stays free of the show model.
    /// </summary>
    public Func<IEnumerable<ScreenInfo>>? PlannedProvider { get; set; }

    /// <summary>Raised on the UI thread after the screen list was rebuilt.</summary>
    public event Action? Changed;

    public void Attach(Window window)
    {
        _screens = window.Screens;
        if (_screens is not null)
        {
            _screens.Changed += (_, _) =>
            {
                Log.Info("Screen topology changed.");
                Refresh();
            };
        }
        Refresh();
    }

    public void Refresh()
    {
        All.Clear();
        if (_screens is null)
        {
            MergePlanned();
            Changed?.Invoke();
            return;
        }

        // Stable ordering: left-to-right, then top-to-bottom.
        var ordered = _screens.All
            .OrderBy(s => s.Bounds.X)
            .ThenBy(s => s.Bounds.Y)
            .ToList();

        for (var i = 0; i < ordered.Count; i++)
        {
            var s = ordered[i];
            var name = string.IsNullOrWhiteSpace(s.DisplayName) ? $"Display {i + 1}" : s.DisplayName!;
            var id = $"{i}:{s.Bounds.Width}x{s.Bounds.Height}@{s.Bounds.X},{s.Bounds.Y}";
            All.Add(new ScreenInfo(id, name, s.Bounds, s.Scaling, s.IsPrimary, i));
        }
        MergePlanned();
        Changed?.Invoke();
    }

    /// <summary>Appends the planned screens after the real ones, skipping any id already present.</summary>
    private void MergePlanned()
    {
        if (PlannedProvider is null) return;
        try
        {
            var index = All.Count;
            foreach (var planned in PlannedProvider())
            {
                if (All.Any(s => s.Id == planned.Id)) continue;
                All.Add(planned with { Index = index++ });
            }
        }
        catch (Exception ex)
        {
            Log.Warn("Planned screens could not be merged.", ex);
        }
    }
}
