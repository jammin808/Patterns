using System.Collections.ObjectModel;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Platform;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>A physical screen with a stable-ish id (index + geometry) used in saved shows.</summary>
public sealed record ScreenInfo(string Id, string Label, PixelRect Bounds, double Scaling, bool IsPrimary, int Index)
{
    public string Description => $"{Bounds.Width}×{Bounds.Height} @ {Bounds.X},{Bounds.Y}{(IsPrimary ? " · primary" : "")}";
}

/// <summary>Enumerates screens off the main window and tracks hot-plug changes.</summary>
public sealed class ScreenService
{
    private Screens? _screens;

    public ObservableCollection<ScreenInfo> All { get; } = new();

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
        Changed?.Invoke();
    }

    /// <summary>The screens a config addresses: the selected ones that still exist, else all.</summary>
    public IReadOnlyList<ScreenInfo> Resolve(IEnumerable<string> selectedIds)
    {
        var wanted = selectedIds.ToHashSet();
        var hit = All.Where(s => wanted.Contains(s.Id)).ToList();
        return hit.Count > 0 ? hit : All.ToList();
    }
}
