using System.Diagnostics;
using System.Runtime.InteropServices;
using LibVLCSharp.Shared;
using Patterns.Core.Model;
using Patterns.Core.Ndi;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.App.Services;

/// <summary>
/// The engine-fed stream source: a thread renders the stream's target — its own screen, or any
/// rig target — at the stream's size and rate into raw BGRA frames, and libVLC's memory input
/// pulls them through a <see cref="FrameFeed"/>. Paced by the show clock so the encoder sees a
/// steady rate; the newest frame wins when the encoder falls behind.
/// </summary>
public sealed class StreamRenderer : IDisposable
{
    private readonly SnapshotBus _bus;
    private readonly PatternEngine _engine = new();
    private readonly string _sourceId;
    private readonly SKSizeI _size;
    private readonly int _fps;
    private Thread? _thread;
    private volatile bool _run;

    public StreamRenderer(SnapshotBus bus, string sourceId, int width, int height, int fps)
    {
        _bus = bus;
        _sourceId = sourceId;
        _size = new SKSizeI(Math.Max(16, width), Math.Max(16, height));
        _fps = Math.Clamp(fps, 1, 120);
        Feed = new FrameFeed(StreamMrl.FrameBytes(_size.Width, _size.Height));
    }

    public FrameFeed Feed { get; }

    public long FramesRendered { get; private set; }

    public void Start()
    {
        if (_run) return;
        _run = true;
        _thread = new Thread(Loop) { Name = "stream-render", IsBackground = true, Priority = ThreadPriority.AboveNormal };
        _thread.Start();
    }

    public void Stop()
    {
        _run = false;
        Feed.Close();
        var t = _thread;
        _thread = null;
        if (t is not null && t.IsAlive && !t.Join(TimeSpan.FromSeconds(3))) Log.Warn("Stream render thread did not stop in time.");
    }

    /// <summary>One frame, rendered and published; public so a test can drive it without the thread.</summary>
    public bool RenderOnce(SKSurface surface, SinkState sink, long frame)
    {
        var snap = _bus.Current;
        var time = ShowClock.Seconds;
        sink.Fps.Tick(time);
        NdiFrame.Render(_engine, snap, sink, surface.Canvas, _size, _sourceId, SinkKind.Stream, "Stream", frame, time);
        surface.Canvas.Flush();
        using var pixmap = surface.PeekPixels();
        if (pixmap is null) return false;
        var bytes = new byte[Feed.FrameBytes];
        var rowBytes = _size.Width * 4;
        if (pixmap.RowBytes == rowBytes)
        {
            Marshal.Copy(pixmap.GetPixels(), bytes, 0, bytes.Length);
        }
        else
        {
            for (var y = 0; y < _size.Height; y++)
            {
                Marshal.Copy(pixmap.GetPixels() + y * pixmap.RowBytes, bytes, y * rowBytes, rowBytes);
            }
        }
        FramesRendered++;
        return Feed.Publish(bytes);
    }

    private void Loop()
    {
        using var sink = new SinkState();
        var info = new SKImageInfo(_size.Width, _size.Height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        if (surface is null)
        {
            Log.Warn($"Stream renderer could not allocate a {_size.Width}×{_size.Height} surface.");
            return;
        }
        var interval = 1.0 / _fps;
        var started = Stopwatch.GetTimestamp();
        long frame = 0;
        while (_run)
        {
            try
            {
                RenderOnce(surface, sink, frame++);
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

    public void Dispose() => Stop();
}

/// <summary>libVLC's memory input over a <see cref="FrameFeed"/>: the demuxer reads raw BGRA frames as they come.</summary>
public sealed class FeedMediaInput : MediaInput
{
    private readonly FrameFeed _feed;

    public FeedMediaInput(FrameFeed feed) => _feed = feed;

    public override bool Open(out ulong size)
    {
        size = ulong.MaxValue; // a live feed has no length
        return true;
    }

    public override int Read(IntPtr buf, uint len)
    {
        var want = (int)Math.Min(len, int.MaxValue);
        var scratch = new byte[Math.Min(want, 1 << 20)];
        // The encoder asks in its own chunks and expects to block: a live feed answers with the
        // next bytes as they come, and 0 — the end of the stream — only once the feed is closed.
        while (!_feed.IsClosed)
        {
            var n = _feed.Read(scratch, timeoutMs: 500);
            if (n <= 0) continue;
            Marshal.Copy(scratch, 0, buf, n);
            return n;
        }
        return 0;
    }

    public override bool Seek(ulong offset) => false;

    public override void Close() { }
}
