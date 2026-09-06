using System.Diagnostics;
using Patterns.Core.Model;
using Patterns.Core.Ndi;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.App.Services;

/// <summary>
/// The engine-fed stream source: a thread renders the stream's target — its own screen, or any rig
/// target — at the stream's size and rate straight into the shared frame ring the encoder process
/// reads: a Skia surface over each slot's bytes, so a frame costs no copy and no allocation. Paced
/// by the show clock so the encoder sees a steady rate; the newest frame wins when the encoder
/// falls behind. The thread owns what it draws with — the surfaces and the ring under them — and
/// releases both when it ends: a frame still drawing when <see cref="Stop"/> runs out of patience
/// keeps its memory until it is done, never has it pulled away.
/// </summary>
public sealed class StreamRenderer : IDisposable
{
    private readonly SnapshotBus _bus;
    private readonly PatternEngine _engine = new();
    private readonly string _sourceId;
    private readonly SKSizeI _size;
    private readonly int _fps;
    private readonly SKSurface?[] _surfaces = new SKSurface?[SharedFrameRing.Slots];
    private Thread? _thread;
    private volatile bool _run;

    public StreamRenderer(SnapshotBus bus, string sourceId, SharedFrameRing ring, int fps)
    {
        _bus = bus;
        _sourceId = sourceId;
        Ring = ring;
        _size = new SKSizeI(ring.Width, ring.Height);
        _fps = Math.Clamp(fps, 1, 120);
    }

    /// <summary>The ring the frames go into — the encoder process reads the other end. Released by this renderer, when its thread ends.</summary>
    public SharedFrameRing Ring { get; }

    public long FramesRendered { get; private set; }

    /// <summary>Why the renderer stopped drawing on its own, or "" while it draws — the stream service reads it into the status and stops the stream.</summary>
    public string Failure { get; private set; } = "";

    /// <summary>How long <see cref="Stop"/> waits for the frame being drawn; the tests shorten it.</summary>
    public TimeSpan StopTimeout { get; set; } = TimeSpan.FromSeconds(3);

    /// <summary>Called on the render thread before every frame — the tests hold a frame with it.</summary>
    public Action? BeforeFrame { get; set; }

    public void Start()
    {
        if (_run) return;
        _run = true;
        _thread = new Thread(Loop) { Name = "stream-render", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    /// <summary>
    /// Ends the drawing and waits <see cref="StopTimeout"/> for the frame in progress. The surfaces and
    /// the ring go with the thread — at once when it ends in time, when the late frame ends otherwise.
    /// </summary>
    public void Stop()
    {
        _run = false;
        var t = _thread;
        if (t is null)
        {
            // Never started (frames drawn by hand): nothing else can be drawing, so the release is here.
            Release();
            return;
        }
        if (!t.Join(StopTimeout)) Log.Warn("Stream render thread did not stop in time — its surfaces and frame ring are released when the frame ends.");
    }

    /// <summary>One frame into the ring's next slot, published; public so a test can drive it without the thread.</summary>
    public bool RenderOnce(SinkState sink, long frame)
    {
        if (Ring.IsClosed) return false;
        var snap = _bus.Current;
        var time = ShowClock.Seconds;
        sink.Fps.Tick(time);
        var slot = Ring.BeginWrite();
        var surface = _surfaces[slot] ??= SKSurface.Create(
            new SKImageInfo(_size.Width, _size.Height, SKColorType.Bgra8888, SKAlphaType.Premul), Ring.PixelsOf(slot), Ring.Stride);
        if (surface is null)
        {
            // Said, and read by the stream service — a stream that draws nothing must never read LIVE.
            Failure = $"the engine could not draw a {_size.Width}×{_size.Height} frame into the encoder's ring";
            Log.Warn($"Stream renderer: {Failure}.");
            return false;
        }
        NdiFrame.Render(_engine, snap, sink, surface.Canvas, _size, _sourceId, SinkKind.Stream, "Stream", frame, time);
        surface.Canvas.Flush();
        Ring.EndWrite(slot, frame);
        FramesRendered++;
        return true;
    }

    private void Loop()
    {
        try
        {
            using var sink = new SinkState();
            var interval = 1.0 / _fps;
            var started = Stopwatch.GetTimestamp();
            long frame = 0;
            while (_run)
            {
                try
                {
                    BeforeFrame?.Invoke();
                    if (!RenderOnce(sink, frame++)) break;
                }
                catch (Exception ex)
                {
                    Log.Warn("Stream frame failed.", ex);
                }
                // Pace on the show clock's grid: the next frame's due time, never a drift of sleeps.
                var due = started + (long)(frame * interval * Stopwatch.Frequency);
                var wait = (due - Stopwatch.GetTimestamp()) * 1000.0 / Stopwatch.Frequency;
                if (wait > 1) Thread.Sleep((int)Math.Min(wait, 100));
            }
        }
        finally
        {
            // The thread's own: the surfaces first, then the ring they sit over.
            Release();
        }
    }

    private void Release()
    {
        for (var i = 0; i < _surfaces.Length; i++)
        {
            _surfaces[i]?.Dispose();
            _surfaces[i] = null;
        }
        Ring.Dispose();
    }

    public void Dispose() => Stop();
}
