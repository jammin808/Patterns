using System.Diagnostics;
using Patterns.Core.Media;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.Rendering.Media;

/// <summary>What a page's frame path is doing: the words for the status line, STATE, the Eye and the deck.</summary>
public readonly record struct WebFrameReport(
    string Smoothing, int Depth, double LatencyMs, double JitterMs, double DecodeMs, double DeliveredFps, double PresentedFps,
    long Underruns, long Dropped, int Held, int PoolStarved, long PoolBytes, long Duplicates = 0)
{
    public static readonly WebFrameReport None = new("", 0, 0, 0, 0, 0, 0, 0, 0, 0, 0, 0);

    /// <summary>"smooth 3 (100 ms) · decode 6.2 ms · 30 → 30 fps".</summary>
    public string Words
    {
        get
        {
            if (Smoothing.Length == 0) return "";
            var rates = DeliveredFps > 0 || PresentedFps > 0 ? $" · {DeliveredFps:0} → {PresentedFps:0} fps" : "";
            var decode = DecodeMs > 0 ? $" · decode {DecodeMs:0.#} ms" : "";
            return Smoothing + decode + rates;
        }
    }
}

/// <summary>
/// A web page's frames from the browser to the glass (round 68): each encoded frame is decoded by
/// Skia's codec straight into a pooled BGRA buffer — the clips' <see cref="FramePool"/>, fixed
/// buffers behind the render fence, nothing allocated per frame — and queued in the
/// <see cref="FrameSmoother"/> for its time; a sink's draw picks the frame whose time has come,
/// publishes it (a pointer swap) and leases it as it does a clip's. Every sink reads the show
/// clock, so every output shows the same frame in the same slot. Leaving the programme drains the
/// queue through the fade and cuts it; nothing of the page outlives the cut. Thread-safe: the
/// decoder offers on a worker (one decode at a time), the sinks draw on their own threads, the
/// words are read anywhere.
/// </summary>
public sealed class WebFramePipeline : IDisposable
{
    private readonly object _gate = new();                              // the smoother, the arrivals, the publish
    private readonly object _decodeGate = new();                        // one decode at a time; never during a dispose or a pool remake
    private readonly FrameSmoother _smoother;
    private readonly long _poolBudget;
    private readonly FrameRateMeter _delivered = new();
    private readonly FrameRateMeter _presented = new();
    private readonly List<long> _dropped = new();
    private FramePool? _pool;
    private double[] _arrival = Array.Empty<double>();                  // per slot: the show clock the frame arrived at
    private double _decodeMsAvg;
    private long _decoded;
    private long _refusedNoBuffer;
    private long _duplicates;
    private int _lastHash;
    private int _lastLength = -1;
    private bool _disposed;

    public WebFramePipeline(FrameSmoother.Bounds bounds, WebSmoothing mode, long poolBudgetBytes)
    {
        _smoother = new FrameSmoother(bounds, mode);
        _poolBudget = Math.Max(1, poolBudgetBytes);
    }

    public FrameSmoother Smoother => _smoother;

    public WebSmoothing Mode
    {
        get => _smoother.Mode;
        set
        {
            lock (_gate)
            {
                _smoother.Mode = value;
            }
        }
    }

    /// <summary>The pool's buffers (the words and the ledger); 0 before the first frame.</summary>
    public long MemoryBytes => _pool?.Bytes ?? 0;

    public FramePool? Pool => _pool;

    public bool HasFrame => _pool?.Latest is not null;

    public SKSizeI? Size
    {
        get
        {
            var pool = _pool;
            return pool is null || pool.Latest is null ? null : new SKSizeI(pool.Width, pool.Height);
        }
    }

    /// <summary>The show clock the frame on show arrived at (-1 before one).</summary>
    public double PublishedClock
    {
        get
        {
            var pool = _pool;
            if (pool is null) return -1;
            return pool.TryLease(out var lease) ? lease.ArrivalClock : -1;
        }
    }

    public double DeliveredFps => _delivered.Rate(DateTime.UtcNow.Ticks);
    public double PresentedFps => _presented.Rate(DateTime.UtcNow.Ticks);

    /// <summary>The decode into the pool, ms, smoothed over the last frames.</summary>
    public double DecodeMs => _decodeMsAvg;

    /// <summary>Frames decoded into the pool so far.</summary>
    public long Decoded => Interlocked.Read(ref _decoded);

    /// <summary>Frames that found no buffer even after the oldest waiting one made room.</summary>
    public long RefusedNoBuffer => Interlocked.Read(ref _refusedNoBuffer);

    /// <summary>Frames whose bytes were the previous frame's — the browser re-sent an unchanged picture — skipped before any decode.</summary>
    public long Duplicates => Interlocked.Read(ref _duplicates);

    /// <summary>
    /// An encoded frame (JPEG, PNG) that arrived at this show clock: decoded into a pooled buffer
    /// and queued for its time — or, when the pool is starved, the oldest waiting frame goes to make
    /// room. False for a frame that did not decode, one refused while draining, or one with no room.
    /// </summary>
    public bool Offer(ReadOnlySpan<byte> encoded, double arrivalClock)
    {
        if (encoded.Length == 0) return false;
        lock (_decodeGate)
        {
            if (_disposed) return false;
            if (_smoother.Draining)
            {
                lock (_gate)
                {
                    _smoother.Refuse();
                }
                return false;
            }
            // The same bytes as the last frame are the same picture: a still page the browser paints again
            // costs a hash, not a decode; the frame on show simply stands (a video's real repeat is a repeat).
            var hash = new HashCode();
            hash.AddBytes(encoded);
            var code = hash.ToHashCode();
            if (encoded.Length == _lastLength && code == _lastHash)
            {
                Interlocked.Increment(ref _duplicates);
                return false;
            }
            _lastHash = code;
            _lastLength = encoded.Length;
            var started = Stopwatch.GetTimestamp();
            unsafe
            {
                fixed (byte* p = encoded)
                {
                    using var data = SKData.Create((IntPtr)p, encoded.Length);
                    using var codec = SKCodec.Create(data);
                    if (codec is null) return false;
                    var info = codec.Info;
                    if (info.Width <= 0 || info.Height <= 0) return false;
                    var pool = PoolFor(info.Width, info.Height);
                    if (pool is null) return false;
                    int slot;
                    lock (_gate)
                    {
                        slot = pool.Acquire();
                        if (slot < 0)
                        {
                            // Every buffer is spoken for: the oldest waiting frame makes room — the room gets the fresher picture.
                            var oldest = -1L;
                            foreach (var id in _smoother.WaitingIds())
                            {
                                oldest = id;
                                break;
                            }
                            if (oldest >= 0 && _smoother.Remove(oldest))
                            {
                                pool.Release((int)oldest);
                                slot = pool.Acquire();
                            }
                        }
                        if (slot < 0)
                        {
                            Interlocked.Increment(ref _refusedNoBuffer);
                            return false;
                        }
                    }
                    var target = new SKImageInfo(info.Width, info.Height, SKColorType.Bgra8888, SKAlphaType.Opaque);
                    var result = codec.GetPixels(target, pool.Pointer(slot), pool.RowBytes, SKCodecOptions.Default);
                    if (result is not (SKCodecResult.Success or SKCodecResult.IncompleteInput))
                    {
                        lock (_gate)
                        {
                            pool.Release(slot);
                        }
                        return false;
                    }
                    lock (_gate)
                    {
                        pool.Queue(slot);
                        _arrival[slot] = arrivalClock;
                        var dropped = _smoother.Offer(slot, arrivalClock);
                        if (dropped >= 0 && dropped != slot) pool.Release((int)dropped);
                    }
                }
            }
            var ms = (Stopwatch.GetTimestamp() - started) * 1000.0 / Stopwatch.Frequency;
            _decodeMsAvg = _decodeMsAvg <= 0 ? ms : _decodeMsAvg * 0.9 + ms * 0.1;
            Interlocked.Increment(ref _decoded);
            _delivered.Tick(DateTime.UtcNow.Ticks);
            return true;
        }
    }

    /// <summary>The pool for frames of this size — made on the first frame, remade when the browser changes the size it sends (under the decode gate).</summary>
    private FramePool? PoolFor(int width, int height)
    {
        var pool = _pool;
        if (pool is not null && pool.Width == width && pool.Height == height) return pool;
        try
        {
            var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Opaque);
            var rowBytes = ((width * 4) + 31) & ~31;
            var fresh = new FramePool(info, rowBytes, FramePool.BuffersFor((long)rowBytes * height, _poolBudget));
            lock (_gate)
            {
                // The browser changed the size it sends: what waited at the old size goes with its pool — the fresh frames are the picture now.
                if (pool is not null)
                {
                    _smoother.Clear();                                                          // the old pool's slots go with it
                    pool.Dispose();
                    _lastLength = -1;
                }
                _pool = fresh;
                _arrival = new double[fresh.Count];
                _smoother.Cap = fresh.Count - 3;                                            // the frame on show, the one retiring and the one decoding are not the buffer's
            }
            return fresh;
        }
        catch (Exception ex)
        {
            Log.Warn("The page's frame pool could not be made.", ex);
            return null;
        }
    }

    /// <summary>
    /// The frame for this draw: the smoother's pick for the show clock now is published (a pointer
    /// swap under the pool's lock) and the newest published frame is drawn through a lease.
    /// <see cref="DrawnFrame.Nothing"/> with nothing to show yet.
    /// </summary>
    public DrawnFrame Draw(SKCanvas canvas, SKRect dest, SKPaint? paint, in FrameCrop crop)
    {
        Present(ShowClock.Seconds);
        var pool = _pool;
        if (pool is null || !pool.TryLease(out var lease)) return DrawnFrame.Nothing;
        if (crop.Any)
        {
            canvas.DrawImage(lease.Image, crop.SourceRect(new SKSizeI(lease.Image.Width, lease.Image.Height)), dest, DrawUtil.Smooth, paint);
        }
        else
        {
            canvas.DrawImage(lease.Image, dest, DrawUtil.Smooth, paint);
        }
        return new DrawnFrame(true, lease.ArrivalClock, true, lease.Generation);
    }

    /// <summary>The smoother's pick for a clock, published — called by a draw, or by a test that has no canvas. True when a frame went on show.</summary>
    public bool Present(double now)
    {
        lock (_gate)
        {
            var pool = _pool;
            if (_disposed || pool is null) return false;
            _dropped.Clear();
            var id = _smoother.Pick(now, _dropped);
            foreach (var d in _dropped)
            {
                pool.Release((int)d);                                                       // due together with the pick and never shown: their buffers go back
            }
            if (id < 0) return false;
            var slot = (int)id;
            if (pool.Publish(slot, _arrival[slot]) is null) return false;
            _presented.Tick(DateTime.UtcNow.Ticks);
            return true;
        }
    }

    /// <summary>No new frames: what waits is presented through the fade; <see cref="Cut"/> ends it.</summary>
    public void BeginLeaving()
    {
        lock (_gate)
        {
            _smoother.Drain();
        }
    }

    /// <summary>The queue cleared and every waiting buffer released: nothing of the page waits to be shown.</summary>
    public void Cut()
    {
        lock (_gate)
        {
            var pool = _pool;
            foreach (var id in _smoother.Cut())
            {
                pool?.Release((int)id);
            }
            _lastLength = -1;                                                                   // a page back after a cut shows its picture even if it is the one it left with
        }
    }

    /// <summary>Frames taken again (a page wanted back while it drained).</summary>
    public void Resume()
    {
        lock (_gate)
        {
            _smoother.Resume();
        }
    }

    public WebFrameReport Report
    {
        get
        {
            lock (_gate)
            {
                var s = _smoother;
                return new WebFrameReport(s.Words, s.Smoothing ? s.Depth : 0, s.LatencySeconds * 1000, s.JitterSeconds * 1000, _decodeMsAvg,
                    DeliveredFps, PresentedFps, s.Underruns, s.Dropped, s.Held, _pool?.Starved ?? 0, MemoryBytes, Duplicates);
            }
        }
    }

    public void Dispose()
    {
        lock (_decodeGate)
        {
            lock (_gate)
            {
                if (_disposed) return;
                _disposed = true;
                _smoother.Cut();
                _pool?.Dispose();                                                           // its buffers go once every frame that drew them has closed
                _pool = null;
            }
        }
    }
}
