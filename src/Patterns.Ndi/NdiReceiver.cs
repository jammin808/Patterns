using System.Runtime.InteropServices;
using Patterns.Core.Media;
using Patterns.Rendering.Media;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.Ndi;

/// <summary>
/// Discovers NDI® sources on the network. One finder runs for the app's lifetime once the
/// user first looks — NDI discovery is push-based, so an old instance keeps its list fresh.
/// UI-thread use only.
/// </summary>
public sealed class NdiFinder : IDisposable
{
    private IntPtr _instance;

    /// <summary>Current source names ("MACHINE (Sender)"). Empty when NDI is unavailable.</summary>
    public IReadOnlyList<string> CurrentSources()
    {
        if (!NdiInterop.Available) return Array.Empty<string>();
        try
        {
            if (_instance == IntPtr.Zero)
            {
                var create = new NdiInterop.FindCreate { ShowLocalSources = true };
                _instance = NdiInterop.NDIlib_find_create_v2(ref create);
                if (_instance == IntPtr.Zero) return Array.Empty<string>();
            }

            var array = NdiInterop.NDIlib_find_get_current_sources(_instance, out var count);
            if (array == IntPtr.Zero || count == 0) return Array.Empty<string>();

            var size = Marshal.SizeOf<NdiInterop.Source>();
            var names = new List<string>((int)count);
            for (var i = 0; i < count; i++)
            {
                var source = Marshal.PtrToStructure<NdiInterop.Source>(array + i * size);
                var name = Marshal.PtrToStringUTF8(source.NdiName);
                if (!string.IsNullOrWhiteSpace(name)) names.Add(name);
            }
            names.Sort(StringComparer.OrdinalIgnoreCase);
            return names;
        }
        catch (Exception ex)
        {
            Log.Warn("NDI source discovery failed.", ex);
            return Array.Empty<string>();
        }
    }

    public void Dispose()
    {
        if (_instance != IntPtr.Zero)
        {
            try { NdiInterop.NDIlib_find_destroy(_instance); }
            catch (Exception ex) { Log.Warn("NDI finder dispose issue.", ex); }
            _instance = IntPtr.Zero;
        }
    }
}

/// <summary>
/// Receives one NDI® source as BGRA frames on a background thread; renderers composite the
/// newest frame like any video. Same immutable-image + retire-hold lifecycle as file video,
/// so GPU-deferred draws never read freed pixels.
/// </summary>
public sealed class NdiReceiver : IVideoFrameSource, IDisposable
{
    private static readonly int FourCcBgra = 'B' | ('G' << 8) | ('R' << 16) | ('A' << 24);

    private readonly object _gate = new();
    private readonly string _sourceName;
    private readonly Thread _thread;
    private volatile bool _stop;
    private IntPtr _recv;
    [System.Diagnostics.CodeAnalysis.SuppressMessage("Usage", "CA2213:Disposable fields should be disposed", Justification = "A frame is retired behind the render fence (RetireLatest), never disposed while a sink may still draw it.")]
    private SKImage? _latest;
    private double _latestClock = -1;
    private Patterns.Rendering.Media.FramePool? _pool;
    private volatile int _framesReceived;
    private volatile int _framesRefused;
    private volatile string _lastRefusal = "";
    private long _lastFrameUtcTicks;
    private long _frameClockBits = BitConverter.DoubleToInt64Bits(-1);
    private volatile bool _createFailed;

    /// <summary>The frames that drew the scratch frame on show: its own table, retired with it (a pooled frame on show has the pool's).</summary>
    private long[]? _latestDrewAt;

    private void RetireLatest()
    {
        if (_latest is null || (_pool?.Owns(_latest) ?? false)) return;   // a pooled frame is the pool's to reuse
        Patterns.Rendering.Media.RetiredFrames.Retire(_latest, _latestDrewAt ?? new long[Patterns.Rendering.Media.RenderFence.MaxSinks]);
        _latestDrewAt = null;
    }

    public NdiReceiver(string sourceName)
    {
        _sourceName = sourceName;
        _thread = new Thread(ReceiveLoop) { IsBackground = true, Name = "ndi-recv" };
        _thread.Start();
    }

    private void ReceiveLoop()
    {
        var namePtr = NdiInterop.Utf8(_sourceName);
        var recvNamePtr = NdiInterop.Utf8("Patterns receive");
        try
        {
            var create = new NdiInterop.RecvCreateV3
            {
                SourceToConnectTo = new NdiInterop.Source { NdiName = namePtr },
                ColorFormat = NdiInterop.RecvColorFormatBgrxBgra,
                Bandwidth = NdiInterop.RecvBandwidthHighest,
                AllowVideoFields = false,
                RecvName = recvNamePtr,
            };
            _recv = NdiInterop.NDIlib_recv_create_v3(ref create);
            if (_recv == IntPtr.Zero)
            {
                _createFailed = true;
                Log.Warn($"NDI receive create failed for '{_sourceName}'.");
                return;
            }
            Log.Info($"NDI receiving '{_sourceName}'.");

            while (!_stop)
            {
                var frame = default(NdiInterop.VideoFrameV2);
                var type = NdiInterop.NDIlib_recv_capture_v2(_recv, ref frame, IntPtr.Zero, IntPtr.Zero, 250);
                if (type != NdiInterop.FrameTypeVideo) continue;
                try
                {
                    PublishFrame(in frame);
                }
                finally
                {
                    NdiInterop.NDIlib_recv_free_video_v2(_recv, ref frame);
                }
            }
        }
        catch (Exception ex)
        {
            _createFailed = true;
            Log.Error($"NDI receive loop failed for '{_sourceName}'.", ex);
        }
        finally
        {
            if (_recv != IntPtr.Zero)
            {
                try { NdiInterop.NDIlib_recv_destroy(_recv); }
                catch (Exception ex) { Log.Warn("NDI receiver destroy issue.", ex); }
                _recv = IntPtr.Zero;
            }
            Marshal.FreeHGlobal(namePtr);
            Marshal.FreeHGlobal(recvNamePtr);
        }
    }

    /// <summary>
    /// Round 83: why a frame the runtime described cannot be copied — an empty raster, a colour format the receiver did
    /// not ask for, a stride below a row — or null. The copy reads by the runtime's stride and never past a row, so a
    /// frame described wrongly is refused here rather than read past its buffer. Pure, so the guard is a unit test.
    /// </summary>
    public static string? FrameShapeProblem(int xres, int yres, int strideBytes, int fourCc)
    {
        if (xres <= 0 || yres <= 0) return "an empty raster";
        if (fourCc != FourCcBgra && fourCc != NdiInterop.FourCcBgrx) return "a colour format the receiver did not ask for";
        var row = (long)xres * 4;
        if (strideBytes < row) return $"a stride of {strideBytes} bytes below the row's {row}";
        return null;
    }

    /// <summary>How many frames the runtime described wrongly and the receiver refused (round 83), and the last reason.</summary>
    public int FramesRefused => _framesRefused;

    private unsafe void PublishFrame(in NdiInterop.VideoFrameV2 frame)
    {
        if (frame.Data == IntPtr.Zero) return;
        var problem = FrameShapeProblem(frame.Xres, frame.Yres, frame.LineStrideInBytes, frame.FourCc);
        if (problem is not null)
        {
            _framesRefused++;
            _lastRefusal = problem;
            return;
        }

        // BGRX_BGRA colour format delivers either fourCC; both are BGRA-layout bytes.
        var alpha = frame.FourCc == FourCcBgra ? SKAlphaType.Unpremul : SKAlphaType.Opaque;
        var info = new SKImageInfo(frame.Xres, frame.Yres, SKColorType.Bgra8888, alpha);
        var rowBytes = frame.Xres * 4;
        var arrival = ShowClock.Seconds;                                                              // the frame's arrival: stamped on the frame, never read back later
        var image = PublishInto(ref _pool, info, rowBytes, frame.Data, frame.LineStrideInBytes, out var pooled, arrival);
        if (image is null) return;
        lock (_gate)
        {
            RetireLatest();
            _latest = image;
            _latestDrewAt = pooled ? null : new long[Patterns.Rendering.Media.RenderFence.MaxSinks];
            _latestClock = arrival;
        }
        _framesReceived++;
        Interlocked.Exchange(ref _lastFrameUtcTicks, DateTime.UtcNow.Ticks);
        Interlocked.Exchange(ref _frameClockBits, BitConverter.DoubleToInt64Bits(arrival));
    }

    /// <summary>The show clock the newest frame arrived at: a sink says how old the picture it drew is.</summary>
    public double FrameClock => BitConverter.Int64BitsToDouble(Interlocked.Read(ref _frameClockBits));

    /// <summary>A feed: its age on the glass is latency the room feels.</summary>
    public bool IsLive => true;

    /// <summary>
    /// One received frame into a pooled buffer — one copy, no allocation — or, with every buffer
    /// still under a draw, into an image of its own the old way. The pool is made on the first
    /// frame and again when the picture changes size; <paramref name="pooled"/> says which path
    /// the frame took. Shared with the tests, which hand in pixels of their own.
    /// </summary>
    public static unsafe SKImage? PublishInto(ref Patterns.Rendering.Media.FramePool? pool, SKImageInfo info, int rowBytes, IntPtr data, int sourceStride, out bool pooled, double arrivalClock = -1)
    {
        pooled = false;
        if (data == IntPtr.Zero || info.Width <= 0 || info.Height <= 0) return null;
        if (pool is null || pool.Width != info.Width || pool.Height != info.Height || pool.Info.AlphaType != info.AlphaType)
        {
            pool?.Dispose();
            pool = new Patterns.Rendering.Media.FramePool(info, rowBytes, Patterns.Rendering.Media.FramePool.BuffersFor((long)rowBytes * info.Height, MemoryBudget.FramePoolBytesPerSource(MemoryBudget.MachineMB)));
        }
        var src = (byte*)data;
        var slot = pool.Acquire();
        if (slot >= 0)
        {
            var dst = (byte*)pool.Pointer(slot);
            for (var y = 0; y < info.Height; y++)
            {
                Buffer.MemoryCopy(src + (long)y * sourceStride, dst + (long)y * rowBytes, rowBytes, rowBytes);
            }
            var published = pool.Publish(slot, arrivalClock);
            if (published is not null)
            {
                pooled = true;
                return published;
            }
            pool.Release(slot);
        }
        var bmp = new SKBitmap(info);
        var bytes = (byte*)bmp.GetPixels();
        var pitch = bmp.RowBytes;
        for (var y = 0; y < info.Height; y++)
        {
            Buffer.MemoryCopy(src + (long)y * sourceStride, bytes + (long)y * pitch, rowBytes, rowBytes);
        }
        bmp.SetImmutable();
        var image = SKImage.FromBitmap(bmp);
        bmp.Dispose();
        return image;
    }

    public bool DrawFrame(SKCanvas canvas, SKRect dest, SKPaint? paint)
        => DrawFrame(canvas, dest, paint, FrameCrop.None);

    public bool DrawFrame(SKCanvas canvas, SKRect dest, SKPaint? paint, in FrameCrop crop)
        => Draw(canvas, dest, paint, in crop).Drew;

    /// <summary>
    /// The newest frame through a lease — the pool records this sink as drawing it under the
    /// pool's own lock, so the buffer stays until this frame's next — or, for a frame that went
    /// the old way, the image and the clock taken together under the receiver's lock. What
    /// comes back is the drawn frame's own clock.
    /// </summary>
    public DrawnFrame Draw(SKCanvas canvas, SKRect dest, SKPaint? paint, in FrameCrop crop)
    {
        if (_pool is { } pool && pool.TryLease(out var lease))
        {
            DrawImage(canvas, lease.Image, dest, paint, in crop);
            return new DrawnFrame(true, lease.ArrivalClock, true, lease.Generation);
        }
        SKImage? image;
        double clock;
        lock (_gate)
        {
            image = _latest;
            clock = _latestClock;
            if (image is not null && _latestDrewAt is { } table) Patterns.Rendering.Media.RenderFence.Touch(table);   // this frame draws the scratch frame: noted with the fetch
        }
        if (image is null || (_pool is { } p && p.Owns(image))) return DrawnFrame.Nothing;   // a pooled image with no lease: the pool is gone under it
        DrawImage(canvas, image, dest, paint, in crop);
        return new DrawnFrame(true, clock, true);
    }

    private static void DrawImage(SKCanvas canvas, SKImage image, SKRect dest, SKPaint? paint, in FrameCrop crop)
    {
        if (crop.Any)
        {
            canvas.DrawImage(image, crop.SourceRect(new SKSizeI(image.Width, image.Height)), dest, Rendering.DrawUtil.Smooth, paint);
        }
        else
        {
            canvas.DrawImage(image, dest, Rendering.DrawUtil.Smooth, paint);
        }
    }

    public SKSizeI? FrameSize
    {
        get
        {
            lock (_gate)
            {
                return _latest is { } img ? new SKSizeI(img.Width, img.Height) : null;
            }
        }
    }

    public bool IsPlaying
    {
        get
        {
            var last = Interlocked.Read(ref _lastFrameUtcTicks);
            return last != 0 && DateTime.UtcNow.Ticks - last < TimeSpan.TicksPerSecond * 3;
        }
    }

    public bool IsEnded => false; // live feeds have no natural end

    public double DurationSeconds => 0;

    public string StatusText => (_createFailed
        ? "NDI receive failed — is the runtime installed?"
        : _framesReceived == 0
            ? $"Connecting to {_sourceName}…"
            : IsPlaying
                ? "Receiving"
                : "No frames — sender offline?") + Refusals;

    /// <summary>The source's card carries the refusals (round 83): a sender whose frames are described wrongly is seen, not silently dropped.</summary>
    private string Refusals => _framesRefused > 0 ? $" · {_framesRefused} frame{(_framesRefused == 1 ? "" : "s")} refused ({_lastRefusal})" : "";

    public void Dispose()
    {
        _stop = true;
        if (!_thread.Join(TimeSpan.FromSeconds(2)))
        {
            Log.Warn("NDI receive thread did not stop in time.");
        }
        lock (_gate)
        {
            RetireLatest();
            _latest = null;
            _latestClock = -1;
        }
        _pool?.Dispose();   // its buffers go once every frame that drew them has closed
        _pool = null;
    }
}
