using Patterns.Core.Model;
using Patterns.Core.Particles;
using Patterns.Core.Services;
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

    /// <summary>Snapshot version/canvas the particle sim was last configured for (hot-path gate).</summary>
    public long ParticlesConfiguredVersion { get; set; } = -1;

    public SKSizeI ParticlesConfiguredCanvas { get; set; }

    /// <summary>Checkerboard shader cache (rebuilt only when colours/cell change).</summary>
    public Patterns.CheckerShaderCache Checker { get; } = new();

    /// <summary>Parsed colour-cycle list cache.</summary>
    public Patterns.CycleColorCache CycleColors { get; } = new();

    /// <summary>Pattern kinds that threw this snapshot version — drawn as an error card instead.</summary>
    public HashSet<PatternKind> Failed { get; } = new();

    public long LastSnapshotVersion { get; set; } = -1;

    // ---- crossfade transition (engine-managed, per sink) --------------------

    /// <summary>Content identity last shown (null until the first frame).</summary>
    public int? TransitionKey { get; set; }

    /// <summary>The most recent snapshot this sink rendered (fade-from candidate).</summary>
    public ShowSnapshot? LastSnapshot { get; set; }

    /// <summary>The snapshot being faded OUT (immutable; safe to hold), null when idle.</summary>
    public ShowSnapshot? TransitionFrom { get; set; }

    /// <summary>Show-clock second the running crossfade started.</summary>
    public double TransitionStartClock { get; set; }

    /// <summary>Show-clock second the running crossfade ends (cadence hook; 0 = idle).</summary>
    public double TransitionEndClock { get; set; }

    /// <summary>Newest snapshot version this sink has passed through the transition logic (cut detection).</summary>
    public long TransitionSeenVersion { get; set; } = -1;

    // Zone-plate runtime shader (compiled once per sink; falls back if unsupported).
    public SKRuntimeEffect? ZonePlateEffect { get; set; }
    public bool ZonePlateUnavailable { get; set; }

    /// <summary>Message ticker caches: the measured text width and the fade-band shader.</summary>
    public TickerCache Ticker { get; } = new();

    public void Dispose()
    {
        Paints.Dispose();
        Particles?.Dispose();
        Checker.Dispose();
        ZonePlateEffect?.Dispose();
        Ticker.Dispose();
    }
}

/// <summary>
/// Per-sink caches for the message overlay: the text is measured once per (text, font, size)
/// and the fade band's gradient is built once per (band, strength) — neither changes per frame.
/// </summary>
public sealed class TickerCache : IDisposable
{
    private string _textKey = "";
    private float _textWidth;
    private readonly Dictionary<string, SKShader> _fades = new();

    public float MeasuredWidth(string text, SKFont font, string family)
    {
        var key = $"{family}|{font.Size}|{text}";
        if (key != _textKey)
        {
            _textKey = key;
            _textWidth = font.MeasureText(text);
        }
        return _textWidth;
    }

    /// <summary>A vertical gradient, opaque-dark at <paramref name="darkY"/> and clear at <paramref name="clearY"/>, at x = <paramref name="x"/>.</summary>
    public SKShader FadeShader(float x, float darkY, float clearY, byte peakAlpha)
    {
        var key = $"{x}|{darkY}|{clearY}|{peakAlpha}";
        if (_fades.TryGetValue(key, out var shader)) return shader;
        // A middle-row band needs two at once; a size change replaces them. Never more than a few.
        if (_fades.Count >= 4) Dispose();
        shader = SKShader.CreateLinearGradient(
            new SKPoint(x, darkY), new SKPoint(x, clearY),
            new[] { new SKColor(0, 0, 0, peakAlpha), SKColors.Transparent },
            SKShaderTileMode.Clamp);
        _fades[key] = shader;
        return shader;
    }

    public void Dispose()
    {
        foreach (var shader in _fades.Values) shader.Dispose();
        _fades.Clear();
    }
}
