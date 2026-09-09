using System.Collections.ObjectModel;
using Patterns.Core.Model;

namespace Patterns.App.ViewModels;

/// <summary>
/// One scene chip on the Reactive page: the operator's name for it, a line about what it is for,
/// and the settings it applies. A scene is a starting point — the sliders under it stay the
/// operator's, and so do the colours and the sound settings.
/// </summary>
public sealed class ReactiveSceneChip
{
    public required string Name { get; init; }

    public required string Note { get; init; }

    public required ReactiveScene Scene { get; init; }

    public double Speed { get; init; } = 1;

    public double Depth { get; init; } = 0.5;

    public int Symmetry { get; init; } = 6;

    public double Rotation { get; init; } = 0.06;

    public double Brightness { get; init; } = 1;

    /// <summary>Lands the scene on a pattern, leaving the colours and the sound settings alone.</summary>
    public void ApplyTo(ReactiveOptions o)
    {
        o.Scene = Scene;
        o.Preset = Name;
        o.Speed = Speed;
        o.Depth = Depth;
        o.Symmetry = Symmetry;
        o.Rotation = Rotation;
        o.Brightness = Brightness;
    }
}

public sealed partial class MainViewModel
{
    // ---- the reactive scenes ------------------------------------------------------------

    public EnumItem[] ReactiveSceneKinds => Lists.ReactiveScenes;

    public EnumItem[] ReactiveQualities => Lists.ReactiveQualities;

    /// <summary>
    /// The Reactive page's chips. Six, on purpose: every one is a shader on the graphics card and a
    /// twin on the CPU, and the two are held to the same picture by a test — a family that cannot
    /// be drawn both ways is not a scene this desk offers.
    /// </summary>
    public ObservableCollection<ReactiveSceneChip> ReactiveScenes { get; } = new()
    {
        new ReactiveSceneChip
        {
            Name = "Ambient Plasma", Scene = ReactiveScene.Plasma,
            Note = "The quietest of them, and the cheapest on every sink: slow colour drifting through the brand kit. The walk-in that never distracts.",
            Speed = 0.6, Depth = 0.35, Rotation = 0.02, Brightness = 0.95,
        },
        new ReactiveSceneChip
        {
            Name = "Subtle Tunnel", Scene = ReactiveScene.Tunnel,
            Note = "Bands running away down a tunnel — depth without motion sickness. The doors-open look.",
            Speed = 0.8, Depth = 0.45, Symmetry = 8, Rotation = 0.03,
        },
        new ReactiveSceneChip
        {
            Name = "Brand Kaleidoscope", Scene = ReactiveScene.Kaleidoscope,
            Note = "The picture folded into wedges around the middle. Carries a brand kit better than anything else here.",
            Speed = 0.7, Depth = 0.6, Symmetry = 6, Rotation = 0.05,
        },
        new ReactiveSceneChip
        {
            Name = "Corporate Pulse", Scene = ReactiveScene.Pulse,
            Note = "Rings rolling out on the low end. Reads as reactive without ever strobing — the safe choice over music.",
            Speed = 1, Depth = 0.4, Rotation = 0,
        },
        new ReactiveSceneChip
        {
            Name = "Vortex", Scene = ReactiveScene.Vortex,
            Note = "A field swirled around the centre, tighter towards the edge. Motion with a still middle, so a lower third still reads.",
            Speed = 0.9, Depth = 0.5, Symmetry = 5, Rotation = -0.04,
        },
        new ReactiveSceneChip
        {
            Name = "Star Warp", Scene = ReactiveScene.StarWarp,
            Note = "Streaks running out past the viewer. The awards-walk-on look; turn the speed up for the reveal.",
            Speed = 1.2, Depth = 0.5, Symmetry = 4, Rotation = 0.01, Brightness = 1.1,
        },
    };

    private RelayCommand<ReactiveSceneChip>? _applyReactiveScene;

    public RelayCommand<ReactiveSceneChip> ApplyReactiveSceneCommand => _applyReactiveScene ??= new RelayCommand<ReactiveSceneChip>(chip =>
    {
        if (chip is null) return;
        _services.BulkEdit(() =>
        {
            chip.ApplyTo(ActivePattern.Reactive);
            ActivePattern.Kind = PatternKind.Reactive;
        });
        Raise(nameof(ReactiveSceneNote));
        StatusMessage = $"{chip.Name} — {(IsSandboxActive ? "in the preview; CUT or TAKE puts it on air" : "on air")}.";
    });

    /// <summary>What the scene on the page is for, in a line — the chip's own note, so the page reads without a tooltip.</summary>
    public string ReactiveSceneNote
    {
        get
        {
            var scene = ActivePattern.Reactive.Scene;
            var chip = ReactiveScenes.FirstOrDefault(c => c.Scene == scene);
            return chip is null ? "" : $"{chip.Name} — {chip.Note}";
        }
    }
}
