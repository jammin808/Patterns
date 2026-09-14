using System.Runtime.InteropServices;
using SkiaSharp;

namespace Patterns.Core.Media;

/// <summary>
/// A live source's frames in a fixed set of buffers. Every decoded frame used to be its own
/// native allocation — a bitmap made, filled, wrapped as an image, and freed a fade later:
/// at 1080p60 that is half a gigabyte of allocator traffic a second per source, and a source's
/// memory a function of its rate. Here a source owns four to eight buffers sized once, each
/// wrapped once as a raster image over its own memory (no copy: the image reads the buffer);
/// a decoder takes a free buffer, writes the frame into it (libVLC decodes straight into it,
/// an NDI frame is copied once), and publishes it; the buffer it replaced is retired with a
/// <see cref="RenderFence"/> mark and is free again once every sink that drew from the pool
/// has drawn past it (a draw says so with <see cref="Touch"/>; the other sinks hold nothing). When
/// every buffer is spoken for, <see cref="Acquire"/> says so rather than waits — the caller
/// keeps a scratch buffer and the old path for that frame, and the count says how often. A
/// source's memory is then a fixed number of frames, whatever it plays, and a steady second of
/// video allocates nothing.
/// </summary>
public sealed class FramePool : IDisposable
{
    public const int MinBuffers = 4;
    public const int MaxBuffers = 8;

    private enum Slot : byte { Free, Locked, Decoded, Latest, Retired }

    private readonly object _gate = new();
    private readonly IntPtr[] _memory;
    private readonly SKImage[] _images;
    private readonly Slot[] _slot;
    private readonly RenderFence.Mark[] _marks;
    private readonly long[] _drewAt = new long[RenderFence.MaxSinks];
    private volatile SKImage? _latestImage;
    private int _latest = -1;
    private bool _disposed;
    private bool _freed;

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

    /// <summary>A sink drew one of the pool's frames on its running frame: it is waited for until its next.</summary>
    public void Touch() => RenderFence.Touch(_drewAt);

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

    /// <summary>The writer dropped the frame: the buffer is free again.</summary>
    public void Release(int slot)
    {
        lock (_gate)
        {
            if (_slot[slot] is Slot.Locked or Slot.Decoded) _slot[slot] = Slot.Free;
        }
    }

    /// <summary>
    /// The frame in the buffer goes on show: the previous frame retires behind a fence mark, and a
    /// frame decoded but never shown is dropped — a decoder shows in order, so an older one it
    /// skipped will not be shown later.
    /// </summary>
    public SKImage? Publish(int slot)
    {
        lock (_gate)
        {
            if (_disposed) return null;
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

    /// <summary>Frees the memory once no buffer is under a fence; true when it did.</summary>
    internal bool TryFree()
    {
        lock (_gate)
        {
            if (_freed) return true;
            if (!_disposed) return false;
            for (var i = 0; i < Count; i++)
            {
                if (_slot[i] == Slot.Retired && !RenderFence.Cleared(in _marks[i], _drewAt)) return false;
                if (_slot[i] is Slot.Locked or Slot.Decoded or Slot.Latest) return false;
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
}
