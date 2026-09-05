using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Patterns;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.Core.Rendering;

/// <summary>
/// Renders a show snapshot to any Skia canvas. The same engine draws the preview, every
/// fullscreen output, preset thumbnails and NDI frames — one implementation, one visual truth.
/// </summary>
public sealed class PatternEngine
{
    private static readonly SKColor LetterboxColor = new(0x0A, 0x0A, 0x0F);

    private readonly IReadOnlyDictionary<PatternKind, IPatternRenderer> _renderers = PatternRegistry.CreateAll();

    public void Render(SKCanvas canvas, ShowSnapshot snap, in RenderContext ctx, SinkState sink)
    {
        // Crossfade on content changes: when this sink's content identity changes, the
        // previous snapshot keeps rendering on top, fading out over the configured time.
        // Thumbnails and fade-source re-renders themselves are excluded.
        if (!ctx.IsFadeSource && ctx.Sink != SinkKind.Thumbnail && snap.FadesEnabled)
        {
            var key = snap.TransitionKeyFor(ctx.ScreenId);
            // A CUT this sink has not shown yet: switch now, and abandon any fade in flight.
            var cut = snap.CutAtVersion > sink.TransitionSeenVersion;
            if (cut)
            {
                sink.TransitionFrom = null;
                sink.TransitionEndClock = 0;
            }
            else if (sink.TransitionKey is { } lastKey && lastKey != key && sink.LastSnapshot is { } prev)
            {
                sink.TransitionFrom = prev;
                sink.TransitionStartClock = ctx.Time;
                sink.TransitionEndClock = ctx.Time + snap.FadeSecondsFor(snap.Version);
            }
            sink.TransitionKey = key;
            sink.LastSnapshot = snap;
            sink.TransitionSeenVersion = snap.Version;

            if (sink.TransitionFrom is { } from)
            {
                var duration = Math.Max(0.05, sink.TransitionEndClock - sink.TransitionStartClock);
                var t = (ctx.Time - sink.TransitionStartClock) / duration;
                if (t >= 1)
                {
                    sink.TransitionFrom = null;
                    sink.TransitionEndClock = 0;
                }
                else
                {
                    RenderContent(canvas, snap, in ctx, sink);

                    // Smoothstep fade-out of the old content on top of the new.
                    var eased = 1 - (t * t * (3 - 2 * t));
                    var alpha = (byte)Math.Clamp(eased * 255, 0, 255);
                    using var fade = new SKPaint { Color = new SKColor(255, 255, 255, alpha) };
                    var bounds = SKRect.Create(0, 0, ctx.ViewportSize.Width, ctx.ViewportSize.Height);
                    canvas.SaveLayer(bounds, fade);
                    var fadeCtx = ctx with { IsFadeSource = true };
                    try
                    {
                        RenderContent(canvas, from, in fadeCtx, sink);
                    }
                    catch (Exception ex)
                    {
                        // A fade must never take the show down — drop it and carry on.
                        Log.Warn("Transition fade-source render failed.", ex);
                        sink.TransitionFrom = null;
                        sink.TransitionEndClock = 0;
                    }
                    canvas.Restore();
                    return;
                }
            }
        }
        else if (!ctx.IsFadeSource && ctx.Sink != SinkKind.Thumbnail)
        {
            // Transitions off: keep tracking identity so enabling them later never fades
            // from long-stale content.
            sink.TransitionKey = snap.TransitionKeyFor(ctx.ScreenId);
            sink.LastSnapshot = snap;
            sink.TransitionFrom = null;
            sink.TransitionEndClock = 0;
            sink.TransitionSeenVersion = snap.Version; // a cut shown with fades off is still seen
        }

        RenderContent(canvas, snap, in ctx, sink);
    }

    private void RenderContent(SKCanvas canvas, ShowSnapshot snap, in RenderContext ctx, SinkState sink)
    {
        if (sink.LastSnapshotVersion != snap.Version)
        {
            // A config change may well have fixed whatever made a renderer throw.
            sink.Failed.Clear();
            sink.LastSnapshotVersion = snap.Version;
        }

        var palette = Palette.Resolve(snap);

        // The frame the desk can take hold of: only the top-level draw records what it drew where.
        var topLevel = !ctx.IsFadeSource && !ctx.InMultiview && !ctx.InLayer;
        if (topLevel) sink.Hits.Clear();

        if (snap.State.Blackout)
        {
            // Checked before any pattern code runs: blackout cannot be broken by a pattern bug.
            canvas.Clear(SKColors.Black);
            OverlayRenderer.RenderViewportOverlays(canvas, snap, ctx, sink, palette, blackout: true);
            return;
        }

        canvas.Clear(LetterboxColor);

        var cfg = snap.PatternFor(ctx.ScreenId);
        var canvasSize = CanvasResolver.Resolve(cfg, ctx.ReferenceSize);
        var (offset, scale) = CanvasResolver.MapToReference(canvasSize, ctx.ReferenceSize, cfg.Canvas.ScaleMode);
        if (topLevel)
        {
            sink.LastCanvasOffset = offset;
            sink.LastCanvasScale = scale;
            sink.LastCanvasSize = canvasSize;
        }

        var frame = new PatternFrame
        {
            Snapshot = snap,
            Config = cfg,
            Ctx = ctx,
            Sink = sink,
            Canvas = canvasSize,
            Palette = palette,
        };

        var save = canvas.Save();
        canvas.Translate(-ctx.ViewportOrigin.X, -ctx.ViewportOrigin.Y);
        canvas.Translate(offset.X, offset.Y);
        canvas.Scale(scale);
        canvas.ClipRect(SKRect.Create(0, 0, canvasSize.Width, canvasSize.Height));

        if (sink.Failed.Contains(cfg.Kind))
        {
            DrawErrorCard(canvas, frame, null);
        }
        else if (cfg.Kind == PatternKind.Multiview)
        {
            try
            {
                RenderMultiview(canvas, in frame, sink, cfg.Multiview);
            }
            catch (Exception ex)
            {
                Log.Error("Multiview renderer threw — disabled until settings change.", ex);
                sink.Failed.Add(cfg.Kind);
                DrawErrorCard(canvas, frame, ex.Message);
            }
        }
        else if (_renderers.TryGetValue(cfg.Kind, out var renderer))
        {
            try
            {
                renderer.Render(canvas, in frame);
            }
            catch (Exception ex)
            {
                // Contain the failure: log once, keep the show running with an unmissable card.
                Log.Error($"Pattern renderer '{cfg.Kind}' threw — disabled until settings change.", ex);
                sink.Failed.Add(cfg.Kind);
                DrawErrorCard(canvas, frame, ex.Message);
            }
        }

        // The two layers: over the pattern, under the overlays; a bad layer never takes the sink down.
        if (!ctx.InLayer && (cfg.Layer1.Enabled || cfg.Layer2.Enabled))
        {
            try
            {
                LayerRenderer.Render(canvas, in frame, DrawLayerScreen);
            }
            catch (Exception ex)
            {
                Log.Error("Layer rendering threw.", ex);
            }
        }

        try
        {
            OverlayRenderer.RenderCanvasOverlays(canvas, in frame);
        }
        catch (Exception ex)
        {
            Log.Error("Overlay rendering threw.", ex);
        }

        canvas.RestoreToCount(save);

        OverlayRenderer.RenderViewportOverlays(canvas, snap, ctx, sink, palette, blackout: false, cfg);

        // The sync check: a white frame on the master clock's grid, on every sink that shows the show.
        if (ctx.Sink != SinkKind.Thumbnail && Effects.SyncMarks.IsFlash(ctx.Time))
        {
            canvas.DrawRect(SKRect.Create(0, 0, ctx.ViewportSize.Width, ctx.ViewportSize.Height), sink.Paints.Fill(SKColors.White));
        }
    }

    // ---- layers -------------------------------------------------------------

    /// <summary>
    /// Another target's picture inside a layer's box: the target drawn at its own pixel size
    /// into a canvas scaled to fit the box (the multiview tile's maths), as a monitor of that
    /// target — never an output, never a layer host, so two screens showing each other stop.
    /// </summary>
    private bool DrawLayerScreen(SKCanvas canvas, SKRect dest, string targetId, in PatternFrame f)
    {
        if (!ContentTargets.IsInRig(f.Snapshot.State, targetId)) return false;
        var v = f.Snapshot.Rig.ViewportForTile(targetId);
        if (v.ViewportSize.Width <= 0 || v.ViewportSize.Height <= 0) return false;
        var scale = Math.Min(dest.Width / v.ViewportSize.Width, dest.Height / v.ViewportSize.Height);
        if (scale <= 0) return false;
        var sub = f.Ctx with
        {
            ViewportSize = v.ViewportSize,
            ReferenceSize = v.ReferenceSize,
            ViewportOrigin = v.Origin,
            ScreenId = v.TargetId,
            InMultiview = true,
            InLayer = true,
            Sink = f.Ctx.Sink == SinkKind.Thumbnail ? SinkKind.Thumbnail : SinkKind.Monitor,
            SinkIndex = 0,
            SinkLabel = f.Snapshot.Rig.LabelFor(f.Snapshot.State, targetId),
        };
        var save = canvas.Save();
        try
        {
            canvas.Translate(dest.Left + (dest.Width - v.ViewportSize.Width * scale) / 2f,
                             dest.Top + (dest.Height - v.ViewportSize.Height * scale) / 2f);
            canvas.Scale(scale);
            canvas.ClipRect(SKRect.Create(0, 0, v.ViewportSize.Width, v.ViewportSize.Height));
            RenderContent(canvas, f.Snapshot, in sub, f.Sink);
        }
        finally
        {
            canvas.RestoreToCount(save);
        }
        return true;
    }

    // ---- multiview ----------------------------------------------------------

    private static readonly SKColor MultiviewBg = new(0x06, 0x07, 0x0A);
    private static readonly SKColor TallyRed = new(0xE0, 0x34, 0x2E);
    private static readonly SKColor TallyIdle = new(0x2A, 0x31, 0x3E);

    /// <summary>
    /// The monitor wall: each tile re-renders program/per-screen content through this same
    /// engine (live inputs and a clock draw directly), with tally borders and labels.
    /// Public so the remote /multiview endpoint can render the same picture standalone.
    /// </summary>
    public void RenderMultiview(SKCanvas canvas, in PatternFrame f, SinkState sink, MultiviewOptions opts)
    {
        if (f.Ctx.InMultiview)
        {
            DrawTileSlate(canvas, f, SKRect.Create(0, 0, f.W, f.H), "MULTIVIEW");
            return;
        }

        if (f.Snapshot.ReviewOnMultiview)
        {
            // A review: the preview fills the whole multiview, with a chip that says so — the
            // caller checks the next look on the monitor wall before the TAKE.
            canvas.Clear(MultiviewBg);
            var full = SKRect.Create(0, 0, f.W, f.H);
            DrawPreview(canvas, in f, sink, full);
            var chipH = Math.Clamp(f.H * 0.06f, 14f, 34f);
            var chip = SKRect.Create(chipH * 0.5f, chipH * 0.5f, chipH * 7.2f, chipH);
            canvas.DrawRoundRect(chip, chipH * 0.25f, chipH * 0.25f, f.Paints.FillAA(new SKColor(0x10, 0x12, 0x18, 0xD8)));
            canvas.DrawRoundRect(chip, chipH * 0.25f, chipH * 0.25f, f.Paints.StrokeAA(TallyIdle, 1.5f));
            var chipFont = f.Paints.FontBold;
            chipFont.Size = chipH * 0.55f;
            DrawUtil.TextCentered(canvas, "REVIEW · PREVIEW", chip.MidX, chip.MidY + chipFont.Size * 0.35f,
                chipFont, f.Paints.Text(new SKColor(0x2E, 0xE6, 0x8A)));
            return;
        }

        var tiles = opts.Tiles.Count > 0 ? opts.Tiles.ToList() : DefaultTiles(f.Snapshot);
        canvas.Clear(MultiviewBg);
        if (tiles.Count == 0)
        {
            DrawTileSlate(canvas, f, SKRect.Create(0, 0, f.W, f.H), "Add multiview tiles in the Pattern tab");
            return;
        }

        var cols = opts.Columns > 0 ? opts.Columns : (int)Math.Ceiling(Math.Sqrt(tiles.Count));
        var rows = (int)Math.Ceiling(tiles.Count / (double)cols);
        var gap = Math.Max(2f, f.W * 0.004f);
        var cellW = (f.W - gap * (cols + 1)) / cols;
        var cellH = (f.H - gap * (rows + 1)) / rows;
        var labelH = opts.ShowLabels ? Math.Clamp(cellH * 0.14f, 13f, 30f) : 0f;

        for (var i = 0; i < tiles.Count; i++)
        {
            var tile = tiles[i];
            var col = i % cols;
            var row = i / cols;
            var cell = SKRect.Create(gap + col * (cellW + gap), gap + row * (cellH + gap), cellW, cellH);
            var content = SKRect.Create(cell.Left, cell.Top, cell.Width, cell.Height - labelH);
            if (content.Width < 1f || content.Height < 1f) continue;   // a grid too dense to draw

            // Each tile takes its target's real shape inside a uniform cell — the same two-step
            // the wall does with AspectBox + RenderFitted, so a 3840×1080 canvas is a wide strip
            // and a portrait screen a tall box, never a re-layout at 16:9. Live inputs and the
            // clock have no target of their own and stay 16:9.
            var vp = TileViewport(f.Snapshot, tile);
            var video = FitRect(content, vp?.Aspect ?? 16f / 9f);
            DrawTileContent(canvas, in f, sink, tile, video, vp);

            if (opts.ShowTally)
            {
                var on = TileOnAir(f.Snapshot, tile);
                canvas.DrawRect(video, f.Paints.StrokeAA(on ? TallyRed : TallyIdle, on ? 3 : 1.5f));
            }

            if (opts.ShowLabels)
            {
                var label = TileLabel(f.Snapshot, tile);
                var bar = SKRect.Create(video.Left, cell.Bottom - labelH, video.Width, labelH);
                canvas.DrawRect(bar, f.Paints.Fill(new SKColor(0x10, 0x12, 0x18)));
                var font = f.Paints.FontBold;
                font.Size = labelH * 0.62f;
                DrawUtil.TextCentered(canvas, label, bar.MidX, bar.MidY + font.Size * 0.35f,
                    font, f.Paints.Text(new SKColor(0xD8, 0xDE, 0xE8)));
            }
        }
    }

    private static SKRect FitRect(SKRect outer, float aspect)
    {
        var w = outer.Width;
        var h = w / aspect;
        if (h > outer.Height)
        {
            h = outer.Height;
            w = h * aspect;
        }
        return SKRect.Create(outer.Left + (outer.Width - w) / 2, outer.Top + (outer.Height - h) / 2, w, h);
    }

    private void DrawTileContent(SKCanvas canvas, in PatternFrame f, SinkState sink, MultiviewTileConfig tile,
        SKRect rect, TargetViewport? vp)
    {
        switch (tile.Source)
        {
            case MultiviewSource.Program:
            case MultiviewSource.Screen:
            {
                if (vp is not { } v)
                {
                    // A Screen tile with nothing picked, or naming a screen or canvas this show
                    // no longer has. Say so: a confidence monitor that quietly shows the program
                    // instead is worse than no monitor.
                    DrawTileSlate(canvas, f, rect,
                        tile.ScreenId.Length == 0 ? "Pick a screen or canvas" : "Not in this rig");
                    break;
                }

                // RenderFitted's maths: draw the target at its own pixel size into a canvas
                // scaled to fit the tile. A FollowOutput grid gets the cell count it has on the
                // wall; a fixed canvas letterboxes against the target's shape, not 16:9.
                var scale = Math.Min(rect.Width / v.ViewportSize.Width, rect.Height / v.ViewportSize.Height);
                var sub = f.Ctx with
                {
                    ViewportSize = v.ViewportSize,     // this screen's own pixels
                    ReferenceSize = v.ReferenceSize,   // the canvas the pattern resolves against
                    ViewportOrigin = v.Origin,         // this member's slice of a joined canvas
                    ScreenId = v.TargetId,             // a screen id, a canvas key, or null = program
                    InMultiview = true,
                    // A tile is a monitor of one target, never an output: no identify badge inside
                    // a tile. Never more overlay than the sink the multiview itself draws on, so
                    // /mv.jpg's thumbnail tiles stay free of PiP, tone and info chips.
                    Sink = f.Ctx.Sink == SinkKind.Thumbnail ? SinkKind.Thumbnail : SinkKind.Monitor,
                    SinkIndex = 0,
                    SinkLabel = TileLabel(f.Snapshot, tile),
                };
                var save = canvas.Save();
                canvas.Translate(rect.Left + (rect.Width - v.ViewportSize.Width * scale) / 2f,
                                 rect.Top + (rect.Height - v.ViewportSize.Height * scale) / 2f);
                canvas.Scale(scale);
                canvas.ClipRect(SKRect.Create(0, 0, v.ViewportSize.Width, v.ViewportSize.Height));
                RenderContent(canvas, f.Snapshot, in sub, sink);
                canvas.RestoreToCount(save);
                break;
            }

            case MultiviewSource.NdiFeed:
            {
                var name = tile.Input.Length > 0 ? tile.Input : Services.MediaLocator.FindActiveNdiSource(f.Snapshot.State);
                if (InputBus.For(InputKeys.Ndi(name)) is { } ndi)
                {
                    canvas.DrawRect(rect, f.Paints.Fill(SKColors.Black));
                    if (!ndi.DrawFrame(canvas, rect, null)) DrawTileSlate(canvas, f, rect, "NDI — waiting for frames");
                }
                else
                {
                    DrawTileSlate(canvas, f, rect, name.Length > 0 ? $"NDI — {name} not received" : "NDI — no feed chosen");
                }
                break;
            }

            case MultiviewSource.Capture:
                if (InputBus.For(InputKeys.Capture(tile.Input)) is { } cap)
                {
                    canvas.DrawRect(rect, f.Paints.Fill(SKColors.Black));
                    if (!cap.DrawFrame(canvas, rect, null)) DrawTileSlate(canvas, f, rect, "Capture — waiting for frames");
                }
                else
                {
                    DrawTileSlate(canvas, f, rect, tile.Input.Length > 0 ? $"Capture — {tile.Input} not open" : "Capture — no device chosen");
                }
                break;

            case MultiviewSource.Preview:
                DrawPreview(canvas, in f, sink, rect);
                break;

            case MultiviewSource.Pip:
            {
                var pipCfg = f.Snapshot.State.Overlays.Pip;
                var key = pipCfg.Source == PipSource.NdiFeed
                    ? InputKeys.Ndi(pipCfg.NdiSourceName)
                    : InputKeys.Capture(pipCfg.CaptureDevice);
                if (pipCfg.Enabled && InputBus.For(key) is { } pip)
                {
                    canvas.DrawRect(rect, f.Paints.Fill(SKColors.Black));
                    var crop = FrameCrop.From(pipCfg); // the tile shows the inset as the room sees it
                    if (!pip.DrawFrame(canvas, rect, null, in crop)) DrawTileSlate(canvas, f, rect, "PiP — waiting for frames");
                }
                else
                {
                    DrawTileSlate(canvas, f, rect, "PiP input off");
                }
                break;
            }

            default:
            {
                canvas.DrawRect(rect, f.Paints.Fill(new SKColor(0x0C, 0x0E, 0x14)));
                var font = f.Paints.FontBold;
                font.Size = rect.Height * 0.3f;
                DrawUtil.TextCentered(canvas, f.Ctx.Now.ToString("HH:mm:ss"), rect.MidX, rect.MidY + font.Size * 0.1f,
                    font, f.Paints.Text(new SKColor(0xE8, 0xEC, 0xF2)));
                var small = f.Paints.FontRegular;
                small.Size = rect.Height * 0.1f;
                DrawUtil.TextCentered(canvas, f.Ctx.Now.ToString("ddd d MMM"), rect.MidX, rect.MidY + rect.Height * 0.28f,
                    small, f.Paints.Text(new SKColor(0x8A, 0x93, 0xA3)));
                break;
            }
        }
    }

    /// <summary>
    /// The sandboxed preview — the program target as the desk is building it — fitted into a
    /// rect, rendered from the preview's own snapshot through the sink's preview sub-sink so the
    /// program's fault gate and caches never see another snapshot's versions. A slate while
    /// EDIT SAFE is off (there is no preview then), and while the preview has no program target.
    /// </summary>
    private void DrawPreview(SKCanvas canvas, in PatternFrame f, SinkState sink, SKRect rect)
    {
        var preview = f.Snapshot.PreviewSource?.Invoke();
        if (preview is null)
        {
            DrawTileSlate(canvas, f, rect, "Preview — EDIT SAFE is off");
            return;
        }
        var v = preview.Rig.ViewportForTarget(null);
        if (v.ViewportSize.Width <= 0 || v.ViewportSize.Height <= 0)
        {
            DrawTileSlate(canvas, f, rect, "Preview — no program target");
            return;
        }
        var video = FitRect(rect, v.Aspect);
        var scale = Math.Min(video.Width / v.ViewportSize.Width, video.Height / v.ViewportSize.Height);
        var sub = f.Ctx with
        {
            ViewportSize = v.ViewportSize,
            ReferenceSize = v.ReferenceSize,
            ViewportOrigin = v.Origin,
            ScreenId = null,
            InMultiview = true,
            Sink = f.Ctx.Sink == SinkKind.Thumbnail ? SinkKind.Thumbnail : SinkKind.Monitor,
            SinkIndex = 0,
            SinkLabel = "PREVIEW",
        };
        var save = canvas.Save();
        try
        {
            canvas.Translate(video.Left + (video.Width - v.ViewportSize.Width * scale) / 2f,
                             video.Top + (video.Height - v.ViewportSize.Height * scale) / 2f);
            canvas.Scale(scale);
            canvas.ClipRect(SKRect.Create(0, 0, v.ViewportSize.Width, v.ViewportSize.Height));
            RenderContent(canvas, preview, in sub, sink.Preview);
        }
        finally
        {
            canvas.RestoreToCount(save);
        }
    }

    private static void DrawTileSlate(SKCanvas canvas, in PatternFrame f, SKRect rect, string text)
    {
        canvas.DrawRect(rect, f.Paints.Fill(new SKColor(0x11, 0x13, 0x1A)));
        var font = f.Paints.FontRegular;
        font.Size = Math.Max(10, rect.Height * 0.09f);
        DrawUtil.TextCentered(canvas, text, rect.MidX, rect.MidY + font.Size * 0.35f,
            font, f.Paints.Text(new SKColor(0x8A, 0x93, 0xA3)));
    }

    private static List<MultiviewTileConfig> DefaultTiles(ShowSnapshot snap)
    {
        // No tiles configured: program + every arranged screen + a clock.
        var tiles = new List<MultiviewTileConfig> { new() { Source = MultiviewSource.Program } };
        foreach (var p in snap.State.Output.Placements.OrderBy(p => p.X).ThenBy(p => p.Y))
        {
            tiles.Add(new MultiviewTileConfig { Source = MultiviewSource.Screen, ScreenId = p.ScreenId });
        }
        tiles.Add(new MultiviewTileConfig { Source = MultiviewSource.Clock });
        return tiles;
    }

    /// <summary>
    /// The target maths for a tile that re-renders show content. Null for a tile that draws a
    /// live input or the clock straight into its rect, and null for a Screen tile whose id
    /// names nothing in this show — that one draws a slate.
    /// </summary>
    private static TargetViewport? TileViewport(ShowSnapshot snap, MultiviewTileConfig tile)
        => tile.Source switch
        {
            MultiviewSource.Program => snap.Rig.ViewportForTarget(null),
            MultiviewSource.Screen when ContentTargets.IsInRig(snap.State, tile.ScreenId)
                => snap.Rig.ViewportForTile(tile.ScreenId),
            MultiviewSource.Preview => snap.PreviewSource?.Invoke()?.Rig.ViewportForTarget(null),
            _ => null,
        };

    private static bool TileOnAir(ShowSnapshot snap, MultiviewTileConfig tile)
    {
        if (snap.State.Blackout || !snap.OutputsLive) return false;
        return tile.Source switch
        {
            MultiviewSource.Program => true,
            MultiviewSource.Screen => ContentTargets.IsTargetEnabled(snap.State, tile.ScreenId),
            _ => false,
        };
    }

    private static string TileLabel(ShowSnapshot snap, MultiviewTileConfig tile)
    {
        if (tile.Label.Length > 0) return tile.Label;
        switch (tile.Source)
        {
            case MultiviewSource.Program:
                return "PROGRAM";
            case MultiviewSource.Screen:
                return tile.ScreenId.Length == 0
                    ? "—"
                    : snap.Rig.LabelFor(snap.State, tile.ScreenId);
            case MultiviewSource.NdiFeed:
            {
                var name = tile.Input.Length > 0 ? tile.Input : Services.MediaLocator.FindActiveNdiSource(snap.State);
                return name.Length > 0 ? snap.State.InputLabel("ndi:" + name, name) : "NDI FEED";
            }
            case MultiviewSource.Capture:
                return tile.Input.Length > 0 ? snap.State.InputLabel("cap:" + tile.Input, tile.Input) : "CAPTURE";
            case MultiviewSource.Pip:
                return "PIP";
            case MultiviewSource.Preview:
                return "PREVIEW";
            default:
                return "CLOCK";
        }
    }

    private static void DrawErrorCard(SKCanvas c, in PatternFrame f, string? message)
    {
        c.Clear(new SKColor(0x14, 0x06, 0x06));
        var pc = f.Paints;
        float w = Math.Min(f.W * 0.8f, 900);
        float h = Math.Min(f.H * 0.4f, 260);
        var rect = SKRect.Create((f.W - w) / 2, (f.H - h) / 2, w, h);
        c.DrawRoundRect(rect, 14, 14, pc.FillAA(new SKColor(0x3A, 0x10, 0x10)));
        c.DrawRoundRect(rect, 14, 14, pc.StrokeAA(new SKColor(0xE0, 0x50, 0x50), 2));

        var title = pc.FontBold;
        title.Size = Math.Max(16, h * 0.16f);
        DrawUtil.TextCentered(c, $"{f.Config.Kind} pattern error", rect.MidX, rect.Top + h * 0.3f, title, pc.Text(new SKColor(0xFF, 0xB0, 0xB0)));

        var body = pc.FontRegular;
        body.Size = Math.Max(12, h * 0.1f);
        var detail = string.IsNullOrEmpty(message) ? "Adjust the pattern settings to retry." : Truncate(message, 90);
        DrawUtil.TextCentered(c, detail, rect.MidX, rect.Top + h * 0.55f, body, pc.Text(new SKColor(0xE8, 0xC0, 0xC0)));
        DrawUtil.TextCentered(c, "The rest of the show keeps running.", rect.MidX, rect.Top + h * 0.78f, body, pc.Text(new SKColor(0xC0, 0x90, 0x90)));
    }

    private static string Truncate(string s, int max) => s.Length <= max ? s : s[..(max - 1)] + "…";

    /// <summary>How often a sink must redraw for this snapshot (drives the idle-efficiency logic).</summary>
    public static RedrawCadence CadenceOf(ShowSnapshot snap, string? screenId, DateTime utcNow) => CadenceOf(snap, screenId, utcNow, 0);

    private static RedrawCadence CadenceOf(ShowSnapshot snap, string? screenId, DateTime utcNow, int depth)
    {
        var s = snap.State;
        if (snap.IdentifyUntilUtc is { } until && until > utcNow) return RedrawCadence.Continuous;
        if (Effects.SyncMarks.Enabled) return RedrawCadence.Continuous; // the flash lands on the frame it is due
        if (s.Blackout)
        {
            return RedrawCadence.Static;
        }

        var p = snap.PatternFor(screenId);
        var continuous = p.Kind is PatternKind.Motion or PatternKind.ColorCycle or PatternKind.Particles or PatternKind.Multiview or PatternKind.Fractal
            || (p.Kind == PatternKind.Checkerboard && p.Checker.Animate)
            || (p.Kind == PatternKind.Media && p.Media.Source is MediaSource.Video or MediaSource.NdiFeed or MediaSource.Capture or MediaSource.Web)
            || (p.Kind == PatternKind.Media && p.Media.Source == MediaSource.Playlist && snap.PlaylistNow?.IsVideo == true)
            || (s.Overlays.Message.Enabled && s.Overlays.Message.Scroll)
            || LowerThirds.LowerThirdClock.IsLive(s.LowerThirds, utcNow)
            || LayerIsLive(snap, p.Layer1, screenId, utcNow, depth)
            || LayerIsLive(snap, p.Layer2, screenId, utcNow, depth);

        if (!continuous && s.Countdown.Enabled && s.Countdown.EndBehavior == CountdownEndBehavior.Flash)
        {
            var status = CountdownService.Evaluate(s.Countdown, DateTime.Now, utcNow);
            if (status.Phase == CountdownPhase.Over) continuous = true;
        }

        if (continuous) return RedrawCadence.Continuous;
        if (s.Overlays.Clock.Enabled || s.Countdown.Enabled) return RedrawCadence.PerSecond;
        return RedrawCadence.Static;
    }

    /// <summary>A layer that moves: a clip or a live feed, or another target whose own picture moves (two hops at most, so a pair of screens showing each other settle).</summary>
    private static bool LayerIsLive(ShowSnapshot snap, LayerConfig l, string? screenId, DateTime utcNow, int depth)
    {
        if (!l.Enabled) return false;
        if (l.Source is LayerSource.Video or LayerSource.NdiFeed or LayerSource.Capture or LayerSource.Web) return true;
        if (l.Source != LayerSource.Screen || l.TargetId.Length == 0 || l.TargetId == screenId || depth >= 2) return false;
        return CadenceOf(snap, l.TargetId, utcNow, depth + 1) == RedrawCadence.Continuous;
    }
}
