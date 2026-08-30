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
        if (!ctx.IsFadeSource && ctx.Sink != SinkKind.Thumbnail && snap.State.Transition.Enabled)
        {
            var key = snap.TransitionKeyFor(ctx.ScreenId);
            if (sink.TransitionKey is { } lastKey && lastKey != key && sink.LastSnapshot is { } prev)
            {
                sink.TransitionFrom = prev;
                sink.TransitionStartClock = ctx.Time;
                sink.TransitionEndClock = ctx.Time + snap.State.Transition.DurationMs / 1000.0;
            }
            sink.TransitionKey = key;
            sink.LastSnapshot = snap;

            if (sink.TransitionFrom is { } from)
            {
                var duration = Math.Max(0.05, snap.State.Transition.DurationMs / 1000.0);
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

            // 16:9 letterbox inside the content area keeps every source undistorted.
            var video = FitRect(content, 16f / 9f);
            DrawTileContent(canvas, in f, sink, tile, video);

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

    private void DrawTileContent(SKCanvas canvas, in PatternFrame f, SinkState sink, MultiviewTileConfig tile, SKRect rect)
    {
        switch (tile.Source)
        {
            case MultiviewSource.Program:
            case MultiviewSource.Screen:
            {
                var size = new SKSizeI(Math.Max(8, (int)rect.Width), Math.Max(8, (int)rect.Height));
                var sub = f.Ctx with
                {
                    ViewportSize = size,
                    ReferenceSize = size,
                    ViewportOrigin = default,
                    ScreenId = tile.Source == MultiviewSource.Screen ? tile.ScreenId : null,
                    InMultiview = true,
                };
                var save = canvas.Save();
                canvas.Translate(rect.Left, rect.Top);
                canvas.ClipRect(SKRect.Create(0, 0, size.Width, size.Height));
                RenderContent(canvas, f.Snapshot, in sub, sink);
                canvas.RestoreToCount(save);
                break;
            }

            case MultiviewSource.NdiFeed:
                if (NdiInput.Current is { } ndi)
                {
                    canvas.DrawRect(rect, f.Paints.Fill(SKColors.Black));
                    if (!ndi.DrawFrame(canvas, rect, null)) DrawTileSlate(canvas, f, rect, "NDI — waiting for frames");
                }
                else
                {
                    DrawTileSlate(canvas, f, rect, "NDI — no feed received");
                }
                break;

            case MultiviewSource.Pip:
                if (PipInput.Current is { } pip)
                {
                    canvas.DrawRect(rect, f.Paints.Fill(SKColors.Black));
                    if (!pip.DrawFrame(canvas, rect, null)) DrawTileSlate(canvas, f, rect, "PiP — waiting for frames");
                }
                else
                {
                    DrawTileSlate(canvas, f, rect, "PiP input off");
                }
                break;

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

    private static bool TileOnAir(ShowSnapshot snap, MultiviewTileConfig tile)
    {
        if (snap.State.Blackout || !snap.OutputsLive) return false;
        return tile.Source switch
        {
            MultiviewSource.Program => true,
            MultiviewSource.Screen => snap.State.Output.Placements.FirstOrDefault(p => p.ScreenId == tile.ScreenId)?.Enabled == true,
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
            {
                var ordered = snap.State.Output.Placements.OrderBy(p => p.X).ThenBy(p => p.Y).ToList();
                var placement = ordered.FirstOrDefault(p => p.ScreenId == tile.ScreenId);
                if (placement is null) return "SCREEN";
                var n = ordered.IndexOf(placement) + 1;
                return placement.CustomLabel.Length > 0 ? $"{n} · {placement.CustomLabel}" : $"SCREEN {n}";
            }
            case MultiviewSource.NdiFeed:
            {
                var name = snap.State.Pattern.Media.NdiSourceName;
                return name.Length > 0 ? snap.State.InputLabel("ndi:" + name, name) : "NDI FEED";
            }
            case MultiviewSource.Pip:
                return "PIP";
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
    public static RedrawCadence CadenceOf(ShowSnapshot snap, string? screenId, DateTime utcNow)
    {
        var s = snap.State;
        if (snap.IdentifyUntilUtc is { } until && until > utcNow) return RedrawCadence.Continuous;
        if (s.Blackout)
        {
            return RedrawCadence.Static;
        }

        var p = snap.PatternFor(screenId);
        var continuous = p.Kind is PatternKind.Motion or PatternKind.ColorCycle or PatternKind.Particles or PatternKind.Multiview
            || (p.Kind == PatternKind.Checkerboard && p.Checker.Animate)
            || (p.Kind == PatternKind.Media && p.Media.Source is MediaSource.Video or MediaSource.NdiFeed or MediaSource.Capture)
            || (p.Kind == PatternKind.Media && p.Media.Source == MediaSource.Playlist && snap.PlaylistNow?.IsVideo == true)
            || (s.Overlays.Message.Enabled && s.Overlays.Message.Scroll);

        if (!continuous && s.Countdown.Enabled && s.Countdown.EndBehavior == CountdownEndBehavior.Flash)
        {
            var status = CountdownService.Evaluate(s.Countdown, DateTime.Now, utcNow);
            if (status.Phase == CountdownPhase.Over) continuous = true;
        }

        if (continuous) return RedrawCadence.Continuous;
        if (s.Overlays.Clock.Enabled || s.Countdown.Enabled) return RedrawCadence.PerSecond;
        return RedrawCadence.Static;
    }
}
