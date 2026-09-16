using Patterns.Core.Media;
using System.Runtime.InteropServices;
using SkiaSharp;

namespace Patterns.Rendering.Media;

/// <summary>
/// A live source's frames in a fixed set of buffers. Every decoded frame used to be its own
/// native allocation — a bitmap made, filled, wrapped as an image, and freed a fade later:
/// at 1080p60 that is half a gigabyte of allocator traffic a second per source, and a source's
/// memory a function of its rate. Here a source owns four to eight buffers sized once, each
/// wrapped once as a raster image over its own memory (no copy: the image reads the buffer);
/// a decoder takes a free buffer, writes the frame into it (libVLC decodes straight into it,
/// an NDI frame is copied once), and publishes it; the buffer it replaced is retired with a
/// <see cref="RenderFence"/> mark and is free again once every frame that drew it has closed. A sink draws through a lease (<see cref="TryLease"/>): under the pool's own
/// lock it takes the newest frame's image, the show clock the frame arrived at and the slot's
/// generation, and records itself as drawing from the pool — one step, so a publish landing
/// between the fetch and the note cannot retire the leased buffer unnoticed: the frame's pixels,
/// its timestamp and its ownership are one thing. When every buffer is spoken for,
/// <see cref="Acquire"/> says so rather than waits — the caller keeps a scratch buffer and the old
/// path for that frame, and the count says how often. A source's memory is then a fixed number
/// of frames, whatever it plays, and a steady second of video allocates nothing.
/// </summary>
public sealed class FramePool : IDisposable
{
    public const int MinBuffers = 4;
    public const int MaxBuffers = 8;

    private enum Slot : byte { Free, Locked, Decoded, Queued, Latest, Retired }

    private readonly object _gate = new();
    private readonly IntPtr[] _memory;
    private readonly SKImage[] _images;
    private readonly Slot[] _slot;
    private readonly RenderFence.Mark[] _marks;
    private readonly long[] _generation;
    private readonly double[] _arrival;
    private readonly long[] _drewAt = new long[RenderFence.MaxSinks];
    private volatile SKImage? _latestImage;
    private int _latest = -1;
    private long _published;
    private bool _disposed;
    private bool _freed;

    /// <summary>
    /// One frame as a sink holds it: the image, the slot it sits in, the generation of that slot
    /// when it was leased (a later frame decoded into the same slot has a later one — a stale
    /// lease never mistakes newer pixels for its own) and the show clock the frame arrived at.
    /// </summary>
    public readonly record struct FrameLease(SKImage Image, int Slot, long Generation, double ArrivalClock);

    public FramePool(SKImageInfo info, int rowBytes, int buffers)
    {
        if (info.Width <= 0 || info.Height <= 0) throw new ArgumentException("a frame needs a size", nameof(info));
        if (rowBytes < info.Width * info.BytesPerPixel) throw new ArgumentException("the row is shorter than the pixels", nameof(rowBytes));
        Count = Math.Clamp(buffers, MinBuffers, MaxBuffers);
        Info = info;
        RowBytes = rowBytes;
        _memory = new IntPtr[Count];
        _images = new SKImage[Count];
        _slot = new Slot[Count];
        _marks = new RenderFence.Mark[Count];
        _generation = new long[Count];
        _arrival = new double[Count];
        Array.Fill(_arrival, -1);
        var bytes = (nuint)((long)rowBytes * info.Height);
        for (var i = 0; i < Count; i++)
        {
            unsafe
            {
                var p = NativeMemory.AlignedAlloc(bytes, 64);
                NativeMemory.Clear(p, bytes);
                _memory[i] = (IntPtr)p;
            }
            _images[i] = SKImage.FromPixels(info, _memory[i], rowBytes) ?? throw new InvalidOperationException("Skia would not wrap the buffer");
        }
        FramePools.Register(this);
    }

    /// <summary>How many buffers a source gets for a budget: at least four (one being written, one on show, two under a fence), at most eight.</summary>
    public static int BuffersFor(long frameBytes, long budgetBytes) => (int)Math.Clamp(budgetBytes / Math.Max(1, frameBytes), MinBuffers, MaxBuffers);

    /// <summary>
    /// What a pool for these frames really costs against a target: the floor of four buffers can
    /// pass it — a 4K frame is thirty-odd megabytes and four of them a hundred and twenty-six, on a
    /// 64 MB target — and the words say so rather than claim the target was kept.
    /// </summary>
    public static long EffectiveBytes(long frameBytes, long targetBytes) => BuffersFor(frameBytes, targetBytes) * Math.Max(0, frameBytes);

    public int Count { get; }
    public SKImageInfo Info { get; }
    public int RowBytes { get; }
    public int Width => Info.Width;
    public int Height => Info.Height;

    /// <summary>What the pool holds, in bytes: fixed for its life.</summary>
    public long Bytes => (long)RowBytes * Info.Height * Count;

    /// <summary>Times a frame found no free buffer: the caller went its old way for it.</summary>
    public int Starved { get; private set; }

    /// <summary>The newest published frame; read from any render thread.</summary>
    public SKImage? Latest => _latestImage;

    /// <summary>Whether the frame is the pool's own (an image the pool made).</summary>
    public bool Owns(SKImage image) => Array.IndexOf(_images, image) >= 0;

    /// <summary>Frames published into the pool so far: the generation the next publish gets.</summary>
    public long Published
    {
        get
        {
            lock (_gate)
            {
                return _published;
            }
        }
    }

    /// <summary>
    /// The newest frame for a draw, in one step under the pool's lock: the sink whose frame is
    /// running is recorded as drawing from the pool, and the frame's image, slot, generation and
    /// arrival clock come back together. False with nothing published, or a disposed pool.
    /// </summary>
    public bool TryLease(out FrameLease lease)
    {
        lock (_gate)
        {
            if (_disposed || _latest < 0)
            {
                lease = default;
                return false;
            }
            RenderFence.Touch(_drewAt);
            lease = new FrameLease(_images[_latest], _latest, _generation[_latest], _arrival[_latest]);
            return true;
        }
    }

    /// <summary>Whether a lease still names the pixels in its slot — false once the slot was handed to the writer again or a newer frame was published into it (the tests and the diagnostics ask).</summary>
    public bool IsCurrent(in FrameLease lease)
    {
        lock (_gate)
        {
            return lease.Slot >= 0 && lease.Slot < Count && _generation[lease.Slot] == lease.Generation && _slot[lease.Slot] is Slot.Latest or Slot.Retired or Slot.Free;
        }
    }

    /// <summary>The generation of the frame in a slot: 0 before its first publish.</summary>
    public long GenerationOf(int slot)
    {
        lock (_gate)
        {
            return _generation[slot];
        }
    }

    /// <summary>A buffer nobody reads, marked locked for the writer; -1 when every one is spoken for.</summary>
    public int Acquire()
    {
        lock (_gate)
        {
            if (_disposed) return -1;
            for (var i = 0; i < Count; i++)
            {
                if (_slot[i] == Slot.Retired && RenderFence.Cleared(in _marks[i], _drewAt)) _slot[i] = Slot.Free;
            }
            for (var i = 0; i < Count; i++)
            {
                if (_slot[i] != Slot.Free) continue;
                _slot[i] = Slot.Locked;
                return i;
            }
            Starved++;
            return -1;
        }
    }

    /// <summary>The buffer's memory for the writer: <see cref="RowBytes"/> per row.</summary>
    public IntPtr Pointer(int slot) => _memory[slot];

    /// <summary>The writer finished the frame but has not shown it yet (a decoder's unlock): kept until shown or superseded.</summary>
    public void Decoded(int slot)
    {
        lock (_gate)
        {
            if (_slot[slot] == Slot.Locked) _slot[slot] = Slot.Decoded;
        }
    }

    /// <summary>
    /// The writer finished the frame and it waits for its time (round 68's smoothing buffer): kept
    /// until published or released — a publish of another frame never drops it, unlike a decoded
    /// frame a decoder skipped.
    /// </summary>
    public void Queue(int slot)
    {
        lock (_gate)
        {
            if (_slot[slot] is Slot.Locked or Slot.Decoded) _slot[slot] = Slot.Queued;
        }
    }

    /// <summary>Frames decoded and waiting for their time.</summary>
    public int Queued
    {
        get
        {
            lock (_gate)
            {
                var n = 0;
                for (var i = 0; i < Count; i++) if (_slot[i] == Slot.Queued) n++;
                return n;
            }
        }
    }

    /// <summary>The writer dropped the frame — or the buffer let a queued one go: the buffer is free again.</summary>
    public void Release(int slot)
    {
        lock (_gate)
        {
            if (_slot[slot] is Slot.Locked or Slot.Decoded or Slot.Queued) _slot[slot] = Slot.Free;
        }
    }

    /// <summary>
    /// The frame in the buffer goes on show, stamped with the show clock it arrived at (the clock
    /// now when the caller does not say): the previous frame retires behind a fence mark, the
    /// slot's generation moves on, and a frame decoded but never shown is dropped — a decoder
    /// shows in order, so an older one it skipped will not be shown later. A queued frame is not
    /// dropped: it waits for its own time.
    /// </summary>
    public SKImage? Publish(int slot, double arrivalClock = -1)
    {
        lock (_gate)
        {
            if (_disposed) return null;
            if (_slot[slot] is Slot.Free or Slot.Retired) return null;                              // a slot the buffer let go, or the pool's own fence holds: nothing to show
            for (var i = 0; i < Count; i++)
            {
                if (i != slot && _slot[i] == Slot.Decoded) _slot[i] = Slot.Free;
            }
            if (_latest >= 0 && _latest != slot)
            {
                _slot[_latest] = Slot.Retired;
                _marks[_latest] = RenderFence.Take();
            }
            _slot[slot] = Slot.Latest;
            _generation[slot] = ++_published;
            _arrival[slot] = arrivalClock >= 0 ? arrivalClock : Patterns.Core.Services.ShowClock.Seconds;
            _latest = slot;
            _latestImage = _images[slot];
            return _images[slot];
        }
    }

    /// <summary>Buffers retired and waiting on the fence right now.</summary>
    public int Retired
    {
        get
        {
            lock (_gate)
            {
                var n = 0;
                for (var i = 0; i < Count; i++) if (_slot[i] == Slot.Retired) n++;
                return n;
            }
        }
    }

    /// <summary>The pool is done: nothing more is published, every buffer retires, and the memory goes once the fence clears.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _latestImage = null;
            _latest = -1;
            var mark = RenderFence.Take();
            for (var i = 0; i < Count; i++)
            {
                if (_slot[i] == Slot.Free) continue;
                _slot[i] = Slot.Retired;
                _marks[i] = mark;
            }
        }
        FramePools.Retire(this);
    }

    /// <summary>How long the oldest retired buffer has waited on the fence, ms; -1 with none waiting.</summary>
    public double OldestRetiredMs
    {
        get
        {
            lock (_gate)
            {
                var oldest = -1.0;
                for (var i = 0; i < Count; i++)
                {
                    if (_slot[i] != Slot.Retired) continue;
                    var age = RenderFence.AgeMs(in _marks[i]);
                    if (age > oldest) oldest = age;
                }
                return oldest;
            }
        }
    }

    /// <summary>Frees the memory once no buffer is under a fence — on the frames' evidence alone, never on time (a buffer a hung frame holds keeps the pool in quarantine until that frame closes); true when it did.</summary>
    internal bool TryFree()
    {
        lock (_gate)
        {
            if (_freed) return true;
            if (!_disposed) return false;
            for (var i = 0; i < Count; i++)
            {
                if (_slot[i] is Slot.Locked or Slot.Decoded or Slot.Latest) return false;
                if (_slot[i] == Slot.Retired && !RenderFence.Cleared(in _marks[i], _drewAt)) return false;
            }
            _freed = true;
            for (var i = 0; i < Count; i++)
            {
                _images[i].Dispose();
                unsafe { NativeMemory.AlignedFree((void*)_memory[i]); }
                _memory[i] = IntPtr.Zero;
            }
            return true;
        }
    }

    /// <summary>What holds the pool's retired buffers: nothing, a frame still running, or only hung frames — the quarantine.</summary>
    public RenderFence.Hold Hold
    {
        get
        {
            lock (_gate)
            {
                var hold = RenderFence.Hold.Clear;
                for (var i = 0; i < Count; i++)
                {
                    if (_slot[i] != Slot.Retired) continue;
                    var h = RenderFence.Check(in _marks[i], _drewAt);
                    if (h == RenderFence.Hold.Open) return h;
                    if (h == RenderFence.Hold.Hung) hold = h;
                }
                return hold;
            }
        }
    }

    /// <summary>Tests and the words: whether the memory is gone.</summary>
    public bool IsFreed
    {
        get
        {
            lock (_gate)
            {
                return _freed;
            }
        }
    }
}

/// <summary>Every frame pool alive, for the memory ledger, and the retired ones until their fences clear.</summary>
public static class FramePools
{
    private static readonly object Gate = new();
    private static readonly List<FramePool> Live = new();
    private static readonly List<FramePool> Retiring = new();

    internal static void Register(FramePool pool)
    {
        lock (Gate)
        {
            Live.Add(pool);
        }
    }

    internal static void Retire(FramePool pool)
    {
        lock (Gate)
        {
            Live.Remove(pool);
            if (!pool.TryFree()) Retiring.Add(pool);
        }
    }

    /// <summary>Frees the retired pools whose fences cleared: called with every retire and by the engines' sweeps.</summary>
    public static void Sweep()
    {
        lock (Gate)
        {
            for (var i = Retiring.Count - 1; i >= 0; i--)
            {
                if (Retiring[i].TryFree()) Retiring.RemoveAt(i);
            }
        }
    }

    /// <summary>Pools alive: one per live source with a pool.</summary>
    public static int Count
    {
        get
        {
            lock (Gate)
            {
                return Live.Count;
            }
        }
    }

    /// <summary>Bytes the live pools hold, fixed for their lives.</summary>
    public static long Bytes
    {
        get
        {
            lock (Gate)
            {
                long b = 0;
                foreach (var p in Live) b += p.Bytes;
                return b;
            }
        }
    }

    /// <summary>Live pools whose real bytes pass the per-source target: the floor of four buffers on a large frame.</summary>
    public static int OverTarget(long targetBytes)
    {
        lock (Gate)
        {
            var n = 0;
            foreach (var p in Live) if (p.Bytes > targetBytes) n++;
            return n;
        }
    }

    /// <summary>Frames that found no free buffer, across the live pools.</summary>
    public static int Starved
    {
        get
        {
            lock (Gate)
            {
                var n = 0;
                foreach (var p in Live) n += p.Starved;
                return n;
            }
        }
    }

    /// <summary>Pools disposed and still waiting on a fence.</summary>
    public static int PendingFree
    {
        get
        {
            lock (Gate)
            {
                return Retiring.Count;
            }
        }
    }

    /// <summary>Bytes of disposed pools only hung frames still hold — the quarantine.</summary>
    public static long QuarantinedBytes
    {
        get
        {
            lock (Gate)
            {
                long b = 0;
                foreach (var p in Retiring) if (p.Hold == RenderFence.Hold.Hung) b += p.Bytes;
                return b;
            }
        }
    }

    /// <summary>Bytes the disposed pools still hold while they wait.</summary>
    public static long RetiringBytes
    {
        get
        {
            lock (Gate)
            {
                long b = 0;
                foreach (var p in Retiring) b += p.Bytes;
                return b;
            }
        }
    }

    /// <summary>The longest any retired buffer — in a live pool or a disposed one — has waited on the fence, ms; -1 with none waiting.</summary>
    public static double OldestRetiredMs
    {
        get
        {
            lock (Gate)
            {
                var oldest = -1.0;
                foreach (var p in Live) oldest = Math.Max(oldest, p.OldestRetiredMs);
                foreach (var p in Retiring) oldest = Math.Max(oldest, p.OldestRetiredMs);
                return oldest;
            }
        }
    }
}
