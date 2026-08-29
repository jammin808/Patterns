using Patterns.Core.Model;
using Patterns.Core.Particles;
using SkiaSharp;

namespace Patterns.Core.Rendering;

/// <summary>
/// Everything mutable a single sink owns. A sink renders on exactly one thread;
/// nothing in here is shared across sinks, so no locks are needed on the render path.
/// </summary>
public sealed class SinkState : IDisposable
{
    public PaintCache Paints { get; } = new();
    public FpsMeter Fps { get; } = new();

    /// <summary>Per-sink particle simulation (created on first use).</summary>
    public ParticleSim? Particles { get; set; }

    /// <summary>Checkerboard shader cache (rebuilt only when colours/cell change).</summary>
    public Patterns.CheckerShaderCache Checker { get; } = new();

    /// <summary>Parsed colour-cycle list cache.</summary>
    public Patterns.CycleColorCache CycleColors { get; } = new();

    /// <summary>Pattern kinds that threw this snapshot version — drawn as an error card instead.</summary>
    public HashSet<PatternKind> Failed { get; } = new();

    public long LastSnapshotVersion { get; set; } = -1;

    // Zone-plate runtime shader (compiled once per sink; falls back if unsupported).
    public SKRuntimeEffect? ZonePlateEffect { get; set; }
    public bool ZonePlateUnavailable { get; set; }

    public void Dispose()
    {
        Paints.Dispose();
        Particles?.Dispose();
        Checker.Dispose();
        ZonePlateEffect?.Dispose();
    }
}
