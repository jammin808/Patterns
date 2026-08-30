using Patterns.Core.Model;
using SkiaSharp;

namespace Patterns.Core.Rendering;

/// <summary>Everything a sink hands the engine for one frame.</summary>
public readonly record struct RenderContext
{
    /// <summary>This sink's pixel size (the area actually being drawn).</summary>
    public required SKSizeI ViewportSize { get; init; }

    /// <summary>
    /// The size the pattern canvas resolves against: the span union, the single screen,
    /// or the NDI frame size. Equals <see cref="ViewportSize"/> except in span mode.
    /// </summary>
    public required SKSizeI ReferenceSize { get; init; }

    /// <summary>This viewport's offset inside the reference space (span mode; else zero).</summary>
    public SKPointI ViewportOrigin { get; init; }

    /// <summary>Monotonic show time in seconds — identical across sinks, keeps animation phase-locked.</summary>
    public required double Time { get; init; }

    /// <summary>Wall clock, local — for the clock overlay and countdowns.</summary>
    public required DateTime Now { get; init; }

    public required DateTime UtcNow { get; init; }

    /// <summary>This sink's rendered-frame counter (drives per-frame stepped motion).</summary>
    public long Frame { get; init; }

    public SinkKind Sink { get; init; }

    /// <summary>1-based number shown by Identify on outputs.</summary>
    public int SinkIndex { get; init; }

    public string SinkLabel { get; init; }

    /// <summary>Screen id for Independent-mode pattern lookup (null elsewhere).</summary>
    public string? ScreenId { get; init; }

    public double MeasuredFps { get; init; }

    /// <summary>True while re-rendering the previous snapshot as the fading-out half of a crossfade.</summary>
    public bool IsFadeSource { get; init; }

    /// <summary>True while rendering inside a multiview tile — nested multiviews draw a slate instead.</summary>
    public bool InMultiview { get; init; }
}

/// <summary>How often a sink needs to redraw for the current snapshot.</summary>
public enum RedrawCadence
{
    /// <summary>Nothing time-varying — draw once per snapshot.</summary>
    Static,
    /// <summary>Only a seconds-resolution clock/countdown — redraw on second boundaries.</summary>
    PerSecond,
    /// <summary>Animated content — redraw every vsync.</summary>
    Continuous,
}
