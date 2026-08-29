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
        if (s.IdentifyUntilUtc is { } until && until > utcNow) return RedrawCadence.Continuous;
        if (s.Blackout)
        {
            return RedrawCadence.Static;
        }

        var p = snap.PatternFor(screenId);
        var continuous = p.Kind is PatternKind.Motion or PatternKind.ColorCycle or PatternKind.Particles
            || (p.Kind == PatternKind.Checkerboard && p.Checker.Animate)
            || (p.Kind == PatternKind.Media && p.Media.Source == MediaSource.Video)
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
