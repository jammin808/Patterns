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

    /// <summary>The fractal shaders compiled for this sink, one per family; a family that failed to compile stays on the CPU path.</summary>
    public Dictionary<FractalKind, SKRuntimeEffect> FractalEffects { get; } = new();

    public HashSet<FractalKind> FractalUnavailable { get; } = new();

    /// <summary>The CPU path's low-resolution frame and its pixel buffer, reused frame to frame.</summary>
    public Effects.FractalSurface? Fractal { get; set; }

    /// <summary>The parsed fractal palette, keyed by the CSV it came from.</summary>
    public string FractalColorsKey { get; set; } = "";

    public SKColor[] FractalColors { get; set; } = Array.Empty<SKColor>();
    public bool ZonePlateUnavailable { get; set; }

    /// <summary>Message ticker caches: the measured text width and the fade-band shader.</summary>
    public TickerCache Ticker { get; } = new();

    /// <summary>The lower third's per-element caches on this sink (sims, rasters, gradients, blurs), by element id.</summary>
    public Dictionary<string, LowerThirds.LowerThirdElementCache> LowerThirds { get; } = new();

    /// <summary>The snapshot version the lower-third caches were last swept for gone elements at.</summary>
    public long LowerThirdsSweptVersion { get; set; } = -1;

    /// <summary>
    /// The boxes the last top-level frame drew that the desk can drag — layers and canvas
    /// overlays in canvas pixels, the PiP in viewport pixels — in draw order. A fade source, a
    /// multiview tile or a screen layer never touches it.
    /// </summary>
    public List<HitRect> Hits { get; } = new();

    /// <summary>Where the last top-level frame put its canvas inside the reference space (the pane's inverse mapping needs it).</summary>
    public SKPoint LastCanvasOffset { get; set; }

    public float LastCanvasScale { get; set; } = 1;

    public SKSizeI LastCanvasSize { get; set; }

    private SinkState? _preview;

    /// <summary>
    /// A sink of its own for the preview a multiview tile (or a review) renders on this sink:
    /// the preview is another snapshot with its own versions, so it must not share this sink's
    /// fault gate or caches with the program. Created on first use, disposed with this one.
    /// </summary>
    public SinkState Preview => _preview ??= new SinkState();

    private SKSurface? _wall;
    private SKSizeI _wallSize;

    /// <summary>
    /// The surface an output of a wall with dead strips draws its whole span on before the runs
    /// of real pixels are placed (<see cref="PatternEngine.RenderWall"/>): kept frame to frame,
    /// remade when the span changes, disposed with this sink.
    /// </summary>
    public SKSurface WallSurface(SKSizeI size)
    {
        size = new SKSizeI(Math.Max(1, size.Width), Math.Max(1, size.Height));
        if (_wall is null || _wallSize != size)
        {
            _wall?.Dispose();
            _wall = SKSurface.Create(new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            _wallSize = size;
        }
        return _wall!;
    }

    private SKSurface? _freeze;
    private SKImage? _frozen;

    /// <summary>The frame this output holds while FREEZE is on; null while it moves.</summary>
    public SKImage? FrozenFrame => _frozen;

    /// <summary>The size the held frame was captured at (a resized sink captures again).</summary>
    public SKSizeI FrozenSize { get; private set; }

    /// <summary>The surface the frame is drawn on once when FREEZE is pressed, before it is held.</summary>
    public SKSurface FreezeSurface(SKSizeI size)
    {
        size = new SKSizeI(Math.Max(1, size.Width), Math.Max(1, size.Height));
        if (_freeze is null || FrozenSize != size)
        {
            _freeze?.Dispose();
            _freeze = SKSurface.Create(new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            FrozenSize = size;
        }
        return _freeze!;
    }

    /// <summary>Holds a captured frame until <see cref="DropFrozen"/>.</summary>
    public void HoldFrozen(SKImage frame, SKSizeI size)
    {
        _frozen?.Dispose();
        _frozen = frame;
        FrozenSize = size;
    }

    /// <summary>FREEZE released (or never on): the held frame and its surface go.</summary>
    public void DropFrozen()
    {
        if (_frozen is null && _freeze is null) return;
        _frozen?.Dispose();
        _frozen = null;
        _freeze?.Dispose();
        _freeze = null;
    }

    public void Dispose()
    {
        _preview?.Dispose();
        _preview = null;
        _wall?.Dispose();
        _wall = null;
        DropFrozen();
        foreach (var cache in LowerThirds.Values) cache.Dispose();
        LowerThirds.Clear();
        foreach (var fx in FractalEffects.Values) fx.Dispose();
        FractalEffects.Clear();
        Fractal?.Dispose();
        Fractal = null;
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
