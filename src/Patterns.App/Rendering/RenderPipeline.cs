using Patterns.Core.Model;
using Patterns.Core.Patterns;
using Patterns.Core.Media;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.App.Rendering;

/// <summary>Static description of what a sink shows (recomputed by the owner when screens change).</summary>
public sealed record PipelineViewport(
    SinkKind Kind,
    SKSizeI ReferenceSize,
    SKPointI ViewportOrigin,
    string? ScreenId,
    int SinkIndex,
    string Label)
{
    public static PipelineViewport Preview { get; } = new(SinkKind.Preview, SKSizeI.Empty, default, null, 0, "Preview");

    /// <summary>
    /// Monitor mode: render the whole target (<see cref="ReferenceSize"/>) and scale it to fit
    /// the control, letterboxed — a true miniature, so a grid has the cell count it has on the
    /// wall. Rotation, warp and trims are the output's business and are not applied.
    /// </summary>
    public bool FitReference { get; init; }

    /// <summary>Read the sandbox snapshot while one is open (the PVW side of a monitor).</summary>
    public bool UsePreviewSnapshot { get; init; }

    /// <summary>A monitor of one content target: PGM or PVW side, at its true size, scaled to fit.</summary>
    public static PipelineViewport Monitor(string? targetId, SKSizeI targetSize, string label, bool previewSide)
        => new(SinkKind.Monitor, targetSize, default, targetId, 0, label)
        {
            FitReference = true,
            UsePreviewSnapshot = previewSide,
        };

    /// <summary>Physical rotation applied when blitting to the window (content stays upright).</summary>
    public OutputRotation Rotation { get; init; } = OutputRotation.None;

    /// <summary>4-corner warp offsets in physical pixels (all zero = no warp).</summary>
    public int WarpTlx { get; init; }
    public int WarpTly { get; init; }
    public int WarpTrx { get; init; }
    public int WarpTry { get; init; }
    public int WarpBlx { get; init; }
    public int WarpBly { get; init; }
    public int WarpBrx { get; init; }
    public int WarpBry { get; init; }

    public bool HasWarp =>
        WarpTlx != 0 || WarpTly != 0 || WarpTrx != 0 || WarpTry != 0 ||
        WarpBlx != 0 || WarpBly != 0 || WarpBrx != 0 || WarpBry != 0;

    /// <summary>The edge bends, in physical pixels: how far each edge bows outward at its middle (all zero = straight edges).</summary>
    public int WarpTopBow { get; init; }
    public int WarpRightBow { get; init; }
    public int WarpBottomBow { get; init; }
    public int WarpLeftBow { get; init; }

    public bool HasBend => WarpTopBow != 0 || WarpRightBow != 0 || WarpBottomBow != 0 || WarpLeftBow != 0;

    /// <summary>The mesh warp: the lattice's density and each point's pull ("" = at rest).</summary>
    public int WarpMeshColumns { get; init; } = 5;
    public int WarpMeshRows { get; init; } = 5;
    public string WarpMesh { get; init; } = "";

    public bool HasMesh => WarpMesh.Length > 0;

    /// <summary>The lattice drawn over the picture on the output while the Screens page edits it; the selected point is marked.</summary>
    /// <summary>The output's own screen id — a member of a joined canvas keeps its own here while ScreenId names the canvas (the calibration lights one projector, not a canvas).</summary>
    public string OutputId { get; init; } = "";

    public bool ShowLattice { get; init; }
    public int LatticePoint { get; init; } = -1;

    /// <summary>The alignment game's targets — the solver's node positions in the output's pixels — ringed on the lattice; null when no game runs.</summary>
    public IReadOnlyList<SKPoint>? LatticeTargets { get; init; }

    /// <summary>Rig day's moment, swept over the lattice while it lasts (a node locked, the projector aligned, a level cleared, the bar full); null when none.</summary>
    public Patterns.Core.RigDay.Celebration? Celebration { get; init; }

    /// <summary>Per-output colour trims (100/1.0/100/100/100 = neutral).</summary>
    public double BrightnessPct { get; init; } = 100;
    public double Gamma { get; init; } = 1.0;
    public double TrimRPct { get; init; } = 100;
    public double TrimGPct { get; init; } = 100;
    public double TrimBPct { get; init; } = 100;

    public bool HasTrims =>
        Math.Abs(BrightnessPct - 100) > 0.01 || Math.Abs(Gamma - 1.0) > 0.001 ||
        Math.Abs(TrimRPct - 100) > 0.01 || Math.Abs(TrimGPct - 100) > 0.01 || Math.Abs(TrimBPct - 100) > 0.01;

    /// <summary>The same trims as another viewport's — read every frame, so a comparison and never a key string.</summary>
    public bool SameTrimsAs(PipelineViewport other)
        => BrightnessPct.Equals(other.BrightnessPct) && Gamma.Equals(other.Gamma)
           && TrimRPct.Equals(other.TrimRPct) && TrimGPct.Equals(other.TrimGPct) && TrimBPct.Equals(other.TrimBPct);

    /// <summary>Edge-blend zones in this output's own pixels (arrangement space: before rotation and warp).</summary>
    public int BlendLeftPx { get; init; }
    public int BlendTopPx { get; init; }
    public int BlendRightPx { get; init; }
    public int BlendBottomPx { get; init; }
    public BlendCurve BlendCurve { get; init; } = BlendCurve.SCurve;
    public double BlendGamma { get; init; } = 1.0;

    /// <summary>Black-level matching: the pedestal added outside the zones, as a percentage of white (0 = off).</summary>
    public double BlendBlackPct { get; init; }

    /// <summary>A blend mask from a camera calibration — a grey picture over this output's raster that multiplies its light — or "" for the zones alone.</summary>
    public string BlendMaskPath { get; init; } = "";

    /// <summary>The rate this sink presents at (0 = every vsync). Outputs only; the preview and monitors stay unpaced.</summary>
    public int TargetFps { get; init; }

    /// <summary>
    /// The dead strips of the target this output renders through (bezels, the air between LED
    /// pillars): <see cref="ReferenceSize"/> is then the surface with them put back, and the
    /// engine cuts them out of this output's picture. Empty for a plain screen.
    /// </summary>
    public GapMap Gaps { get; init; } = GapMap.Empty;

    /// <summary>This output's real pixels inside its target's raster (only read when <see cref="Gaps"/> has strips).</summary>
    public SKRectI RasterRegion { get; init; }

    /// <summary>How many runs of real pixels this output is cut into — 1 when no strip runs through it.</summary>
    public int WallSlices => Gaps.IsEmpty ? 1 : Gaps.Slices(RasterRegion).Count;

    /// <summary>Only a real output fades its edges — never a monitor, a preview, NDI or a thumbnail.</summary>
    public bool HasBlend => Kind == SinkKind.Output &&
        (BlendLeftPx > 0 || BlendTopPx > 0 || BlendRightPx > 0 || BlendBottomPx > 0 || BlendMaskPath.Length > 0);

    /// <summary>The same blend zones as another viewport's — read every frame of a blended output, so a comparison and never a key string.</summary>
    public bool SameBlendAs(PipelineViewport other)
        => BlendLeftPx == other.BlendLeftPx && BlendTopPx == other.BlendTopPx
           && BlendRightPx == other.BlendRightPx && BlendBottomPx == other.BlendBottomPx
           && BlendCurve == other.BlendCurve && BlendGamma.Equals(other.BlendGamma) && BlendBlackPct.Equals(other.BlendBlackPct)
           && BlendMaskPath == other.BlendMaskPath;

    /// <summary>
    /// The output's geometry as numbers, for the frame's one comparison against the geometry it
    /// built last: the lattice, the bends, the keystone, the rotation, the two sizes and the
    /// pedestal's inputs — and not the lattice's picked point or the game's targets, which change
    /// the overlay and never the picture.
    /// </summary>
    public WarpGeometrySpec GeometrySpec(SKSizeI effectivePx, SKSizeI physicalPx)
        => new(WarpMeshColumns, WarpMeshRows, WarpMesh,
            WarpTopBow, WarpRightBow, WarpBottomBow, WarpLeftBow,
            WarpTlx, WarpTly, WarpTrx, WarpTry, WarpBlx, WarpBly, WarpBrx, WarpBry,
            Rotation, effectivePx, physicalPx,
            new BlendWidths(BlendLeftPx, BlendTopPx, BlendRightPx, BlendBottomPx), BlendBlackPct, BlendGamma);
}

/// <summary>
/// Glues one on-screen sink (preview or output window) to the engine: owns the sink state,
/// builds the per-frame context, measures FPS and renders 1:1 device pixels.
/// </summary>
public sealed class RenderPipeline : IDisposable
{
    private readonly PatternEngine _engine = new();
    private readonly SinkState _sink = new();
    private readonly SnapshotBus _bus;
    private readonly FrameBudget _budget;
    private long _frame;
    private volatile PipelineViewport _viewport;
    private bool _firstFrameTold;

    /// <summary>Told once per pipeline when its first preview frame lands — the start-up budget's last mark.</summary>
    public static Action? FirstPreviewFrame { get; set; }

    // A draw op queued for the compositor's render thread can run after the control that
    // owns this pipeline has gone (a wall tile rebuilt, a window closed). Render and Dispose
    // therefore share one gate: a disposed pipeline draws nothing instead of touching freed
    // Skia handles.
    private readonly object _gate = new();
    private bool _disposed;

    public RenderPipeline(SnapshotBus bus, PipelineViewport viewport)
    {
        _bus = bus;
        _viewport = viewport;
        _budget = new FrameBudget(viewport.Kind, viewport.SinkIndex, viewport.Label) { Scope = bus, TargetFps = viewport.TargetFps };
        FrameBudgets.Attach(_budget);
        _fence = RenderFence.Register();
    }

    /// <summary>This sink on the render fence: advanced at every frame's start, so a pooled frame it drew last frame can be written again.</summary>
    private readonly int _fence;

    /// <summary>This sink's frame budget: the last minute's frames, the worst and the stage that took it.</summary>
    public FrameBudget Budget => _budget;

    /// <summary>
    /// One frame done: into the per-second smoothness counters and this sink's budget, with the
    /// slowest stage the engine noted — and the version this frame drew, from the frame's own
    /// capture, never read back from the bus (a publish that landed while the frame was in
    /// flight is the next frame's to show and to report). A calibration frame drew the
    /// structured light, not the show: it counts as a frame and never as a version shown.
    /// </summary>
    private void FrameDone(PipelineViewport vp, long frameStart, in FrameInput input, bool faulted = false)
    {
        var ms = System.Diagnostics.Stopwatch.GetElapsedTime(frameStart).TotalMilliseconds;
        RenderStats.Record(vp.Kind, vp.SinkIndex, ms);
        if (_budget.Kind != vp.Kind || _budget.SinkIndex != vp.SinkIndex || _budget.Label != vp.Label) _budget.Relabel(vp.Kind, vp.SinkIndex, vp.Label);
        var clock = ShowClock.Seconds;
        _budget.Record(ms, _sink.Stages.SlowestStage, clock);
        if (!faulted && input.IsShow)
        {
            _budget.RecordShown(input.Program.Version, input.Program.PublishedClock, clock);   // a publish reached this sink: its lag, and the version for the GO's clock
            _budget.RecordGood(input.Program.Version);
            var live = _sink.Stages.LiveFrameClock;
            if (live >= 0) _budget.RecordLiveAge((clock - live) * 1000.0, clock, _sink.Stages.LiveFrameGeneration, live);   // the oldest camera or feed picture this frame drew: its age now, decoder to frame
        }
        if (!_firstFrameTold && vp.Kind == SinkKind.Preview)
        {
            _firstFrameTold = true;
            try
            {
                FirstPreviewFrame?.Invoke();
            }
            catch
            {
                // A start-up mark must never touch the frame.
            }
        }
    }

    public PipelineViewport Viewport
    {
        get => _viewport;
        set => _viewport = value;
    }

    /// <summary>When the preview edits an independent screen, it mirrors that screen's pattern.</summary>
    public Func<string?>? ScreenIdOverride { get; set; }

    public RedrawCadence Cadence
    {
        get
        {
            // A running crossfade needs vsync redraw whatever the content would need.
            if (_sink.TransitionEndClock > ShowClock.Seconds) return RedrawCadence.Continuous;
            var vp = _viewport;
            var screenId = ScreenIdOverride?.Invoke() ?? vp.ScreenId;
            var snap = SnapshotFor(vp);
            // …and so does one the next frame is about to start. This decision is made before
            // that frame is drawn, so a fade armed by the draw itself would otherwise never get
            // a second frame and the sink would sit on the outgoing picture for good.
            if (PatternEngine.WillStartFade(snap, screenId, _sink, vp.Kind)) return RedrawCadence.Continuous;
            return PatternEngine.CadenceOf(snap, screenId, DateTime.UtcNow);
        }
    }

    /// <summary>The preview (and a monitor's PVW side) follows the sandbox while look programming is sandboxed; outputs, NDI and thumbnails always show program.</summary>
    private ShowSnapshot SnapshotFor(PipelineViewport vp)
        => vp.Kind == SinkKind.Preview || vp.UsePreviewSnapshot ? _bus.Sandbox ?? _bus.Current : _bus.Current;

    /// <summary>Whether this sink draws the sandbox while one is open: the preview window and a monitor's PVW pane.</summary>
    private static bool PreviewSide(PipelineViewport vp) => vp.Kind == SinkKind.Preview || vp.UsePreviewSnapshot;

    /// <summary>The number of frames this pipeline captured from the bus (tests read it).</summary>
    public long Frames => _frame;

    /// <summary>The snapshot this pipeline is showing right now (tally and pending checks).</summary>
    public ShowSnapshot CurrentSnapshot => SnapshotFor(_viewport);

    private volatile HitRect[] _lastHits = Array.Empty<HitRect>();
    private PaneMap? _lastMap;

    /// <summary>The boxes the last fitted frame drew that the desk can drag (monitor panes only; empty elsewhere).</summary>
    public IReadOnlyList<HitRect> LastHits => _lastHits;

    /// <summary>The maths from the pane's device pixels to the picture it showed last (null until a fitted frame drew).</summary>
    public PaneMap? LastMap => _lastMap;

    private SKColorFilter? _trimFilter;
    private PipelineViewport? _trimFilterFor;
    private readonly SKPaint _trimPaint = new();

    /// <summary>The output's geometry — nodes, patches, lines, the pedestal, the matrices — built once per change and read every frame.</summary>
    private readonly WarpGeometryCache _geometry = new();

    /// <summary>How many times this sink built its geometry: once per change of it, never per frame (the allocation test reads it).</summary>
    public int GeometryBuilds => _geometry.Builds;
    private static readonly byte[] IdentityTable = BuildIdentity();

    private static byte[] BuildIdentity()
    {
        var t = new byte[256];
        for (var i = 0; i < 256; i++) t[i] = (byte)i;
        return t;
    }

    /// <summary>Renders into a leased Skia canvas whose current transform maps DIPs → device px.</summary>
    public void Render(SKCanvas canvas, double widthDips, double heightDips, double renderScaling)
    {
        lock (_gate)
        {
            if (_disposed) return;
            RenderLocked(canvas, widthDips, heightDips, renderScaling);
        }
    }

    private void RenderLocked(SKCanvas canvas, double widthDips, double heightDips, double renderScaling)
    {
        var frameStart = System.Diagnostics.Stopwatch.GetTimestamp();
        RenderFence.Advance(_fence);   // a new frame: the last one flushed, and the pooled frames it drew are free to overwrite
        var vp = _viewport;
        var physicalPx = new SKSizeI(
            Math.Max(1, (int)Math.Round(widthDips * renderScaling)),
            Math.Max(1, (int)Math.Round(heightDips * renderScaling)));

        // The frame's world, captured once: the program it draws, the preview its tile draws, the
        // clock, and whether it is the show or the calibration's light. Nothing below reads the
        // bus again — a publish landing mid-frame is drawn and reported by the next frame.
        var kind = vp.Kind == SinkKind.Output && CalibrationOverlay.Active ? FrameKind.Calibration : FrameKind.Show;
        var input = FrameInput.Capture(_bus, PreviewSide(vp), ShowClock.Seconds, kind);

        var fitted = vp.FitReference && vp.ReferenceSize != SKSizeI.Empty;
        var save = canvas.Save();
        var faulted = false;
        try
        {
            if (fitted) DrawFitted(canvas, vp, physicalPx, renderScaling, in input);
            else DrawOutput(canvas, vp, physicalPx, renderScaling, in input);
        }
        catch (Exception ex)
        {
            // Never let a render fault propagate into the compositor — and never leave the room
            // a half-drawn frame: the fault is counted on this sink, noted in the log once per
            // while, and the last world that drew whole is drawn again in the frame's place. If
            // that throws too the fault is systemic, and what was drawn stays.
            faulted = true;
            Fault(ex, vp, in input);
            if (_lastGood is { } good && !ReferenceEquals(good.Program, input.Program))
            {
                canvas.RestoreToCount(save);
                save = canvas.Save();
                try
                {
                    if (fitted) DrawFitted(canvas, vp, physicalPx, renderScaling, in good);
                    else DrawOutput(canvas, vp, physicalPx, renderScaling, in good);
                }
                catch (Exception again)
                {
                    Fault(again, vp, in good);
                }
            }
        }
        finally
        {
            canvas.RestoreToCount(save);
            if (!faulted && input.IsShow) _lastGood = input;
            FrameDone(vp, frameStart, in input, faulted);
        }
    }

    private FrameInput? _lastGood;
    private double _faultNotedClock = double.NegativeInfinity;
    private int _faultsSinceNote;
    private long _faultNotes;

    /// <summary>Between notes in the log while a sink keeps faulting: the first fault is noted at once, the rest counted and noted together every so many seconds.</summary>
    public const double FaultNoteEverySeconds = 10;

    /// <summary>The notes the log got for faults so far — one per while, never one per frame (tests read it).</summary>
    public long FaultNotesLogged => _faultNotes;

    /// <summary>A frame's draw threw: onto the budget, and into the log — rate-limited, with the count since the last note.</summary>
    private void Fault(Exception ex, PipelineViewport vp, in FrameInput input)
    {
        var clock = ShowClock.Seconds;
        _faultsSinceNote++;
        _budget.RecordFault(FaultWords(ex), DateTime.UtcNow, clock);
        if (clock - _faultNotedClock < FaultNoteEverySeconds) return;
        var more = _faultsSinceNote > 1 ? $" ({_faultsSinceNote} since the last note)" : "";
        Log.Error($"{vp.Label}: a frame's draw threw{more} — the last good picture is drawn in its place (version {input.Program.Version}).", ex);
        _faultNotedClock = clock;
        _faultsSinceNote = 0;
        _faultNotes++;
    }

    /// <summary>The fault's words for the budget and the lines: the exception's kind and its message on one line, kept short.</summary>
    public static string FaultWords(Exception ex)
    {
        var message = ex.Message.Replace('\r', ' ').Replace('\n', ' ').Trim();
        var words = $"{ex.GetType().Name}: {message}";
        return words.Length > 160 ? words[..160] + "…" : words;
    }

    /// <summary>An output's, the preview's or a thumbnail's frame: the content through this sink's geometry, trims and blend — or the calibration's light in its place.</summary>
    private void DrawOutput(SKCanvas canvas, PipelineViewport vp, SKSizeI physicalPx, double renderScaling, in FrameInput input)
    {
        // For 90/270 rotations the engine renders portrait content, blitted rotated below.
        var rotated = vp.Rotation is OutputRotation.Rot90 or OutputRotation.Rot270;
        var effectivePx = rotated ? new SKSizeI(physicalPx.Height, physicalPx.Width) : physicalPx;
        var reference = vp.ReferenceSize == SKSizeI.Empty ? effectivePx : vp.ReferenceSize;

        var ctx = new RenderContext
        {
            ViewportSize = effectivePx,
            ReferenceSize = reference,
            ViewportOrigin = vp.ViewportOrigin,
            Time = input.Clock,
            Now = DateTime.Now,
            UtcNow = DateTime.UtcNow,
            Frame = _frame++,
            Sink = vp.Kind,
            SinkIndex = vp.SinkIndex,
            SinkLabel = vp.Label,
            ScreenId = ScreenIdOverride?.Invoke() ?? vp.ScreenId,
            MeasuredFps = _sink.Fps.Fps,
            Preview = input.Preview,         // the sink composes two snapshots: the program it draws, the preview its PREVIEW tile draws — both from the frame's capture
        };
        _sink.Fps.Tick(ctx.Time);

        // Undo DPI scaling so the engine draws in device pixels — pixel-exact output.
        canvas.Scale((float)(1.0 / renderScaling));
        canvas.ClipRect(SKRect.Create(0, 0, physicalPx.Width, physicalPx.Height));

        if (input.Kind == FrameKind.Calibration)
        {
            // The structured light: this output's pattern, or black while another is read —
            // raw white on the raw raster, before the trims, the warp and the blend, because
            // the camera must see the pixels the code names. A member of a joined canvas is
            // its own output here, not the canvas.
            canvas.Clear(SKColors.Black);
            var outputId = vp.OutputId.Length > 0 ? vp.OutputId : vp.ScreenId;
            if (CalibrationOverlay.PatternFor(outputId) is { } pattern) CalibrationOverlay.Draw(canvas, pattern, physicalPx.Width, physicalPx.Height, _patternPaint);
            return;
        }

        var layered = false;
        if (vp.HasTrims)
        {
            if (_trimFilter is null || _trimFilterFor is not { } trimmed || !trimmed.SameTrimsAs(vp))
            {
                _trimFilter?.Dispose();
                _trimFilter = SKColorFilter.CreateTable(
                    IdentityTable,
                    TrimTable.Build(vp.BrightnessPct, vp.Gamma, vp.TrimRPct),
                    TrimTable.Build(vp.BrightnessPct, vp.Gamma, vp.TrimGPct),
                    TrimTable.Build(vp.BrightnessPct, vp.Gamma, vp.TrimBPct));
                _trimFilterFor = vp;
            }
            _trimPaint.ColorFilter = _trimFilter;
            canvas.SaveLayer(_trimPaint);
            layered = true;
        }

        var meshed = vp.HasMesh && vp.Kind == SinkKind.Output;
        var bent = !meshed && vp.HasBend && vp.Kind == SinkKind.Output;
        // The geometry the frame draws with — the lattice's nodes and patches, the bends'
        // patch, the pedestal's cells, the keystone — built once and kept until a number
        // changes: a frame compares, and never parses or allocates it. A plain output needs none.
        var geo = meshed || bent || vp.HasWarp || vp.ShowLattice || vp.HasBlend
            ? _geometry.For(vp.GeometrySpec(effectivePx, physicalPx))
            : null;
        if (meshed)
        {
            // The mesh: the finished picture — content, zones, pedestal — drawn through a grid
            // of Coons patches with Catmull-Rom tangents (the bends folded into the edge
            // points), under the keystone's perspective and the rotation.
            var surface = EnsureOffscreen(effectivePx);
            DrawContent(surface.Canvas, vp, in input, in ctx);
            if (vp.HasBlend) DrawBlendMask(surface.Canvas, vp, effectivePx, geo!);
            surface.Canvas.Flush();
            using var image = surface.Snapshot();
            canvas.Clear(SKColors.Black);
            var patched = canvas.Save();
            if (vp.HasWarp) canvas.Concat(geo!.Keystone);
            canvas.Concat(geo!.Rotation);
            using var shader = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
            _patchPaint.Shader = shader;
            var cubics = geo.PatchCubics;
            var textures = geo.PatchTextures;
            for (var k = 0; k < cubics.Length; k++)
            {
                canvas.DrawPatch(cubics[k], null, textures[k], _patchPaint);
            }
            _patchPaint.Shader = null;
            if (vp.ShowLattice) DrawLattice(canvas, geo, vp);
            canvas.RestoreToCount(patched);
        }
        else if (bent)
        {
            // The edge bends: the finished picture — content, its blend zones and its black
            // pedestal, all in the picture's own space — drawn through one Coons patch whose
            // edges bow as the operator set them, under the keystone (a perspective, so the
            // inside stays straight) and the rotation.
            var surface = EnsureOffscreen(effectivePx);
            DrawContent(surface.Canvas, vp, in input, in ctx);
            if (vp.HasBlend) DrawBlendMask(surface.Canvas, vp, effectivePx, geo!);
            surface.Canvas.Flush();
            using var image = surface.Snapshot();
            canvas.Clear(SKColors.Black);
            var patched = canvas.Save();
            if (vp.HasWarp) canvas.Concat(geo!.Keystone);
            canvas.Concat(geo!.Rotation);
            using var shader = image.ToShader(SKShaderTileMode.Clamp, SKShaderTileMode.Clamp);
            _patchPaint.Shader = shader;
            canvas.DrawPatch(geo.BendCubics, null, geo.BendTexture, _patchPaint);
            _patchPaint.Shader = null;
            canvas.RestoreToCount(patched);
        }
        else if (vp.HasWarp)
        {
            // Keystone path: content renders to an offscreen surface at the effective
            // size, then blits through warp ∘ rotation as one perspective image draw.
            var surface = EnsureOffscreen(effectivePx);
            DrawContent(surface.Canvas, vp, in input, in ctx);
            surface.Canvas.Flush();
            using var image = surface.Snapshot();

            var warp = geo!.Keystone;
            canvas.Clear(SKColors.Black);
            var warped = canvas.Save();
            canvas.Concat(in warp);
            canvas.Concat(geo.Rotation);
            canvas.DrawImage(image, 0, 0, Patterns.Core.Rendering.DrawUtil.Smooth, _warpPaint);
            canvas.RestoreToCount(warped); // the blend mask below applies the same transform itself
        }
        else
        {
            var turned = canvas.Save();
            canvas.Concat(RotationMatrix(vp.Rotation, physicalPx));
            DrawContent(canvas, vp, in input, in ctx);
            canvas.RestoreToCount(turned);
        }

        if (layered)
        {
            canvas.Restore();
        }

        if (vp.ShowLattice && !meshed)
        {
            // The lattice at rest (or with the bends alone), so the first pull has something to grab.
            var latticeSave = canvas.Save();
            if (vp.HasWarp) canvas.Concat(geo!.Keystone);
            canvas.Concat(geo!.Rotation);
            DrawLattice(canvas, geo, vp);
            canvas.RestoreToCount(latticeSave);
        }

        if (vp.HasBlend && !bent && !meshed)
        {
            // Last, over the trimmed picture, through the same warp and rotation the picture
            // took: the zones sit on the picture's own edges, so a keystoned projector's
            // fade follows its keystone. Black with alpha, so each band multiplies the light.
            // (A bent picture took its zones inside the patch above.)
            canvas.Save();
            if (vp.HasWarp) canvas.Concat(geo!.Keystone);
            canvas.Concat(geo!.Rotation);
            DrawBlendMask(canvas, vp, effectivePx, geo);
            canvas.Restore();
        }
    }

    private static readonly SKColor LetterboxColor = new(0x0A, 0x0A, 0x0F);

    /// <summary>The output's picture in arrangement space: straight, or with the wall's dead strips cut out.</summary>
    private void DrawContent(SKCanvas target, PipelineViewport vp, in FrameInput input, in RenderContext ctx)
    {
        if (vp.Gaps.IsEmpty)
        {
            _engine.Render(target, input.Program, in ctx, _sink);
        }
        else
        {
            _engine.RenderWall(target, input.Program, in ctx, _sink, vp.Gaps, vp.RasterRegion);
        }
    }

    /// <summary>
    /// Monitor rendering: the engine draws the target at its real size into a canvas scaled
    /// to fit the control, so the miniature is the output's picture, not a re-layout at the
    /// control's aspect. The same approach the screen overview uses.
    /// </summary>
    private void DrawFitted(SKCanvas canvas, PipelineViewport vp, SKSizeI physicalPx, double renderScaling, in FrameInput input)
    {
        var target = vp.ReferenceSize;
        var scale = Math.Min(physicalPx.Width / (float)target.Width, physicalPx.Height / (float)target.Height);
        var dx = (physicalPx.Width - target.Width * scale) / 2f;
        var dy = (physicalPx.Height - target.Height * scale) / 2f;

        var ctx = new RenderContext
        {
            ViewportSize = target,
            ReferenceSize = target,
            ViewportOrigin = default,
            Time = input.Clock,
            Now = DateTime.Now,
            UtcNow = DateTime.UtcNow,
            Frame = _frame++,
            Sink = vp.Kind,
            SinkIndex = vp.SinkIndex,
            SinkLabel = vp.Label,
            ScreenId = ScreenIdOverride?.Invoke() ?? vp.ScreenId,
            MeasuredFps = _sink.Fps.Fps,
            Preview = input.Preview,         // the sink composes two snapshots: the program it draws, the preview its PREVIEW tile draws — both from the frame's capture
            // A miniature: the patterns widen their hairlines to this pane's own pixels.
            DeviceScale = scale,
        };
        _sink.Fps.Tick(ctx.Time);

        canvas.Scale((float)(1.0 / renderScaling));
        canvas.ClipRect(SKRect.Create(0, 0, physicalPx.Width, physicalPx.Height));
        canvas.Clear(LetterboxColor);
        canvas.Translate(dx, dy);
        canvas.Scale(scale);
        canvas.ClipRect(SKRect.Create(0, 0, target.Width, target.Height));
        _engine.Render(canvas, input.Program, in ctx, _sink);
        // What the desk can take hold of on this pane, and how its pixels map to the picture.
        _lastMap = new PaneMap(target, dx, dy, scale, _sink.LastCanvasOffset, _sink.LastCanvasScale, _sink.LastCanvasSize);
        _lastHits = _sink.Hits.ToArray();
    }

    // ---- edge blend ---------------------------------------------------------

    private readonly Dictionary<string, SKShader> _blendShaders = new();
    private PipelineViewport? _blendShadersFor;
    private SKSizeI _blendShadersSize;
    private readonly SKPaint _blendPaint = new();
    private readonly SKPaint _maskPaint = new() { BlendMode = SKBlendMode.Modulate };
    private readonly SKPaint _patternPaint = new() { Color = SKColors.White, IsAntialias = false };
    private SKImage? _maskImage;
    private string _maskImageFor = "";

    private static SKImage? LoadMask(string path)
    {
        try
        {
            using var bitmap = SKBitmap.Decode(path);
            return bitmap is null ? null : SKImage.FromBitmap(bitmap);
        }
        catch (Exception ex)
        {
            Log.Warn($"The blend mask '{path}' could not be read.", ex);
            return null;
        }
    }
    private SKColor[] _blendStops = Array.Empty<SKColor>();
    private (BlendCurve Curve, double Gamma) _blendStopsFor = ((BlendCurve)(-1), double.NaN);

    /// <summary>The gradient stops of one zone: black at the outer edge, clear where the full picture begins. Kept until the curve or the gamma changes — this ran on every frame of a blended output.</summary>
    private SKColor[] BlendStops(BlendCurve curve, double gamma)
    {
        if (_blendStopsFor.Curve == curve && _blendStopsFor.Gamma.Equals(gamma) && _blendStops.Length > 0) return _blendStops;
        const int n = 32;
        var stops = new SKColor[n + 1];
        for (var i = 0; i <= n; i++)
        {
            var weight = BlendMath.Weight(curve, i / (double)n, gamma);
            stops[i] = new SKColor(0, 0, 0, (byte)Math.Clamp(Math.Round(255 * (1 - weight)), 0, 255));
        }
        _blendStops = stops;
        _blendStopsFor = (curve, gamma);
        return stops;
    }

    /// <summary>
    /// Four bands, one per blended edge, each a linear gradient across its zone; a corner where
    /// two zones meet multiplies both, which is exactly the product two overlapping projectors
    /// need. Cached per zone geometry and rebuilt only when the viewport's blend changes.
    /// </summary>
    private void DrawBlendMask(SKCanvas canvas, PipelineViewport vp, SKSizeI size, WarpGeometry geo)
    {
        if (_blendShadersFor is not { } blended || !blended.SameBlendAs(vp) || _blendShadersSize != size)
        {
            foreach (var s in _blendShaders.Values) s.Dispose();
            _blendShaders.Clear();
            _blendShadersFor = vp;
            _blendShadersSize = size;
        }
        var stops = BlendStops(vp.BlendCurve, vp.BlendGamma);
        int w = size.Width, h = size.Height;
        if (vp.BlendMaskPath.Length > 0)
        {
            // A camera's mask: every pixel's share of the light, multiplied over the raster.
            if (_maskImage is null || _maskImageFor != vp.BlendMaskPath)
            {
                _maskImage?.Dispose();
                _maskImage = LoadMask(vp.BlendMaskPath);
                _maskImageFor = vp.BlendMaskPath;
            }
            if (_maskImage is not null) canvas.DrawImage(_maskImage, SKRect.Create(0, 0, w, h), Patterns.Core.Rendering.DrawUtil.Smooth, _maskPaint);
        }
        if (vp.BlendLeftPx > 0)
        {
            Band(canvas, "L", SKRect.Create(0, 0, Math.Min(vp.BlendLeftPx, w), h),
                new SKPoint(0, 0), new SKPoint(Math.Min(vp.BlendLeftPx, w), 0), stops);
        }
        if (vp.BlendRightPx > 0)
        {
            var zone = Math.Min(vp.BlendRightPx, w);
            Band(canvas, "R", SKRect.Create(w - zone, 0, zone, h),
                new SKPoint(w, 0), new SKPoint(w - zone, 0), stops);
        }
        if (vp.BlendTopPx > 0)
        {
            Band(canvas, "T", SKRect.Create(0, 0, w, Math.Min(vp.BlendTopPx, h)),
                new SKPoint(0, 0), new SKPoint(0, Math.Min(vp.BlendTopPx, h)), stops);
        }
        if (vp.BlendBottomPx > 0)
        {
            var zone = Math.Min(vp.BlendBottomPx, h);
            Band(canvas, "B", SKRect.Create(0, h - zone, w, zone),
                new SKPoint(0, h), new SKPoint(0, h - zone), stops);
        }
        if (vp.BlendBlackPct > 0)
        {
            // Black-level matching: the regions the zones do not cover, and the bands where only
            // two projectors meet, are lifted to the floor of the deepest overlap — added light,
            // so a dark scene shows one black across the canvas instead of bright seams and a
            // brighter square where four projectors share a corner. The cells and their levels
            // are the geometry's, found once.
            var cells = geo.Pedestal;
            for (var k = 0; k < cells.Length; k++)
            {
                var level = cells[k].Level;
                _pedestalPaint.Color = new SKColor(level, level, level);
                canvas.DrawRect(cells[k].Rect, _pedestalPaint);
            }
        }
    }

    private void Band(SKCanvas canvas, string edge, SKRect rect, SKPoint outer, SKPoint inner, SKColor[] stops)
    {
        if (!_blendShaders.TryGetValue(edge, out var shader))
        {
            shader = SKShader.CreateLinearGradient(outer, inner, stops, SKShaderTileMode.Clamp);
            _blendShaders[edge] = shader;
        }
        _blendPaint.Shader = shader;
        canvas.DrawRect(rect, _blendPaint);
    }

    private readonly SKPaint _latticeLine = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 2, Color = new SKColor(0x3E, 0xC1, 0xF3) };
    private readonly SKPaint _latticeDot = new() { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(0x3E, 0xC1, 0xF3) };
    private readonly SKPaint _latticePick = new() { IsAntialias = true, Style = SKPaintStyle.Fill, Color = new SKColor(0xFF, 0xB0, 0x2E) };
    private readonly SKPaint _latticeRing = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3, Color = SKColors.White };
    private readonly SKPaint _latticeTarget = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3, Color = new SKColor(0xFF, 0xB0, 0x2E) };
    private readonly SKPaint _latticeLocked = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, StrokeWidth = 3, Color = new SKColor(0x7C, 0xF5, 0xC8) };

    /// <summary>The lattice over the projector's picture while the Screens page pulls it: lines, dots, and the picked point ringed — what the walk-up sees.</summary>
    private void DrawLattice(SKCanvas canvas, WarpGeometry geo, PipelineViewport vp)
    {
        var nodes = geo.Nodes;
        var lines = geo.Lines;
        for (var k = 0; k < lines.Length; k++)
        {
            canvas.DrawLine(lines[k].A, lines[k].B, _latticeLine);
        }
        // The alignment game: the solver's target at each node as a ring — green once the node is within a pixel, amber with a line to walk while it is not.
        if (vp.LatticeTargets is { } targets)
        {
            for (var k = 0; k < nodes.Length && k < targets.Count; k++)
            {
                var d = SKPoint.Distance(nodes[k], targets[k]);
                var locked = d <= 1f;
                canvas.DrawCircle(targets[k], k == vp.LatticePoint ? 22 : 12, locked ? _latticeLocked : _latticeTarget);
                if (!locked) canvas.DrawLine(nodes[k], targets[k], _latticeTarget);
            }
        }
        for (var k = 0; k < nodes.Length; k++)
        {
            if (k == vp.LatticePoint)
            {
                canvas.DrawCircle(nodes[k], 14, _latticeRing);
                canvas.DrawCircle(nodes[k], 10, _latticePick);
            }
            else
            {
                canvas.DrawCircle(nodes[k], 6, _latticeDot);
            }
        }
        if (vp.Celebration is { } moment) DrawCelebration(canvas, vp, moment);
    }

    private readonly SKPaint _sweep = new() { IsAntialias = true, Style = SKPaintStyle.Stroke, Color = new SKColor(0x7C, 0xF5, 0xC8) };
    private readonly SKPaint _sweepText = new() { IsAntialias = true, Style = SKPaintStyle.Fill, Color = SKColors.White };
    private readonly SKFont _sweepFont = new() { Size = 64, Embolden = true };

    /// <summary>
    /// The sweep: a ring from the picture's centre out past its corners, fading as it goes, and
    /// the chip's words in the middle — over the lattice only, which the room is never watching.
    /// A node locked is a small quick ring; the projector aligned, a level cleared or the bar
    /// full is the whole picture's worth. Nothing once the moment is over.
    /// </summary>
    private void DrawCelebration(SKCanvas canvas, PipelineViewport vp, Patterns.Core.RigDay.Celebration moment)
    {
        var now = DateTime.UtcNow;
        if (moment.IsOver(now)) return;
        var phase = moment.Phase(now);
        var (radius, alpha) = Patterns.Core.RigDay.Celebration.Ring(phase);
        var bounds = canvas.LocalClipBounds;                 // the picture as this pass draws it: the lattice's own space
        var w = bounds.Width;
        var h = bounds.Height;
        var half = (float)(Math.Sqrt((double)w * w + (double)h * h) / 2);
        var reach = moment.Kind == Patterns.Core.RigDay.CelebrationKind.NodeLocked ? half * 0.25f : half * 1.05f;
        _sweep.Color = _sweep.Color.WithAlpha(alpha);
        _sweep.StrokeWidth = (float)(8 - 6 * phase);
        canvas.DrawCircle(bounds.MidX, bounds.MidY, (float)(radius * reach), _sweep);
        if (moment.Kind != Patterns.Core.RigDay.CelebrationKind.NodeLocked)
        {
            _sweepText.Color = SKColors.White.WithAlpha(alpha);
            _sweepFont.Size = Math.Max(24, Math.Min(w, h) * 0.08f);
            var text = moment.Chip;
            var width = _sweepFont.MeasureText(text);
            canvas.DrawText(text, bounds.MidX - width / 2f, bounds.MidY + _sweepFont.Size * 0.35f, SKTextAlign.Left, _sweepFont, _sweepText);
        }
    }

    private SKSurface? _offscreen;
    private SKSizeI _offscreenSize;
    private readonly SKPaint _warpPaint = new() { IsAntialias = true };
    private readonly SKPaint _patchPaint = new() { IsAntialias = true };
    private readonly SKPaint _pedestalPaint = new() { BlendMode = SKBlendMode.Plus };

    private SKSurface EnsureOffscreen(SKSizeI size)
    {
        if (_offscreen is null || _offscreenSize != size)
        {
            _offscreen?.Dispose();
            _offscreen = SKSurface.Create(new SKImageInfo(size.Width, size.Height, SKColorType.Bgra8888, SKAlphaType.Premul));
            _offscreenSize = size;
        }
        return _offscreen!;
    }

    private static SKMatrix RotationMatrix(OutputRotation rotation, SKSizeI physicalPx) => WarpGeometry.RotationOf(rotation, physicalPx);

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            FrameBudgets.Detach(_budget);
            RenderFence.Unregister(_fence);
            _trimFilter?.Dispose();
            _trimPaint.Dispose();
            _warpPaint.Dispose();
            foreach (var s in _blendShaders.Values) s.Dispose();
            _blendShaders.Clear();
            _blendPaint.Dispose();
            _offscreen?.Dispose();
            _sink.Dispose();
        }
    }
}
