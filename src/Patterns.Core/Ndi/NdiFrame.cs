using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.Core.Ndi;

/// <summary>Where a target's picture lands in a frame of another shape: scaled to fit, centred, bars around it.</summary>
public readonly record struct FrameFit(float Scale, float OffsetX, float OffsetY, SKSizeI Viewport)
{
    /// <summary>The picture fills the frame exactly — no scaling, no bars.</summary>
    public bool IsExact => Scale == 1 && OffsetX == 0 && OffsetY == 0;

    public static FrameFit Compute(SKSizeI frame, SKSizeI target)
    {
        var fw = Math.Max(1, frame.Width);
        var fh = Math.Max(1, frame.Height);
        var tw = Math.Max(1, target.Width);
        var th = Math.Max(1, target.Height);
        if (fw == tw && fh == th) return new FrameFit(1, 0, 0, new SKSizeI(tw, th));
        var scale = Math.Min(fw / (float)tw, fh / (float)th);
        var drawnW = tw * scale;
        var drawnH = th * scale;
        return new FrameFit(scale, (fw - drawnW) / 2f, (fh - drawnH) / 2f, new SKSizeI(tw, th));
    }
}

/// <summary>
/// One frame of a feed (an NDI send, the stream) drawn by the engine. The program fills the
/// frame — it follows every output's shape anyway. Any other target — a display, a joined
/// canvas, the feed's own screen — is drawn at the target's real shape and fitted into the
/// frame, bars around it, so a mirrored canvas keeps its shape and a feed's own screen (sized
/// to the feed) fills it edge to edge.
/// </summary>
public static class NdiFrame
{
    public static void Render(PatternEngine engine, ShowSnapshot snap, SinkState sink, SKCanvas canvas, SKSizeI frame,
                              string sourceId, SinkKind kind, string label, long frameNumber, double time)
    {
        var target = string.IsNullOrEmpty(sourceId) ? null : sourceId;
        var fit = target is null ? new FrameFit(1, 0, 0, frame) : FrameFit.Compute(frame, snap.Rig.SizeOf(target));
        var ctx = new RenderContext
        {
            ViewportSize = fit.Viewport,
            ReferenceSize = fit.Viewport,
            ViewportOrigin = default,
            Time = time,
            Now = DateTime.Now,
            UtcNow = DateTime.UtcNow,
            Frame = frameNumber,
            Sink = kind,
            SinkIndex = 0,
            SinkLabel = label,
            ScreenId = target,
            MeasuredFps = sink.Fps.Fps,
        };
        if (fit.IsExact)
        {
            engine.Render(canvas, snap, in ctx, sink);
            return;
        }
        canvas.Clear(SKColors.Black);
        canvas.Save();
        canvas.Translate(fit.OffsetX, fit.OffsetY);
        canvas.Scale(fit.Scale);
        canvas.ClipRect(SKRect.Create(0, 0, fit.Viewport.Width, fit.Viewport.Height));
        engine.Render(canvas, snap, in ctx, sink);
        canvas.Restore();
    }
}
