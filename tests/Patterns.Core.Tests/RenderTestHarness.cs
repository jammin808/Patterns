using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.Core.Tests;

/// <summary>Renders show snapshots through the real engine into raster surfaces for pixel asserts.</summary>
public static class RenderTestHarness
{
    public static readonly DateTime FixedUtcNow = new(2026, 8, 29, 10, 0, 0, DateTimeKind.Utc);

    public static ShowSnapshot Snap(ShowState state, long version = 1, DateTime? identifyUntil = null)
        => new() { State = state, Version = version, IdentifyUntilUtc = identifyUntil };

    public static SKBitmap Render(
        ShowState state,
        int width,
        int height,
        double time = 1.0,
        SKSizeI? reference = null,
        SKPointI origin = default,
        string? screenId = null,
        long frame = 0,
        SinkKind sinkKind = SinkKind.Output)
    {
        var snap = Snap(state);
        return Render(snap, width, height, time, reference, origin, screenId, frame, sinkKind);
    }

    public static SKBitmap Render(
        ShowSnapshot snap,
        int width,
        int height,
        double time = 1.0,
        SKSizeI? reference = null,
        SKPointI origin = default,
        string? screenId = null,
        long frame = 0,
        SinkKind sinkKind = SinkKind.Output)
    {
        var engine = new PatternEngine();
        using var sink = new SinkState();
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        var ctx = new RenderContext
        {
            ViewportSize = new SKSizeI(width, height),
            ReferenceSize = reference ?? new SKSizeI(width, height),
            ViewportOrigin = origin,
            Time = time,
            Now = new DateTime(2026, 8, 29, 12, 0, 0),
            UtcNow = FixedUtcNow,
            Frame = frame,
            Sink = sinkKind,
            SinkIndex = 1,
            SinkLabel = "test",
            ScreenId = screenId,
        };
        engine.Render(surface.Canvas, snap, in ctx, sink);
        surface.Canvas.Flush();

        var bmp = new SKBitmap(info);
        surface.ReadPixels(info, bmp.GetPixels(), info.RowBytes, 0, 0);
        return bmp;
    }

    public static ShowState State(Action<ShowState>? mutate = null)
    {
        var s = new ShowState();
        // Quiet defaults for pixel testing.
        s.Overlays.Clock.Enabled = false;
        s.Overlays.Info.Enabled = false;
        s.Countdown.Enabled = false;
        mutate?.Invoke(s);
        return s;
    }
}
