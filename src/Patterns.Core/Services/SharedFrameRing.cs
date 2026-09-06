using System.IO.MemoryMappedFiles;

namespace Patterns.Core.Services;

/// <summary>
/// A ring of BGRA frames in memory two processes share: the renderer draws a frame straight into a
/// slot (a Skia surface over the mapped bytes — no copy, no allocation) and the encoder in its own
/// process takes the newest complete one. Three slots, so the slot being read is never the one being
/// written; a sequence number per slot, written after the pixels, so a torn frame is never taken; the
/// newest frame wins when the reader is slow. Pagefile-backed and named on Windows (nothing touches
/// the disk), with a named event beside it that wakes a waiting reader the moment a frame is published
/// — a sleep of a millisecond is a 15.6 ms tick on Windows, too coarse for 50 or 60 frames a second;
/// a file under /dev/shm elsewhere, where the reader polls. The parent creates it and passes its
/// <see cref="Address"/> to the child, which opens it. Built for the stream encoder first and for
/// the decoders that follow — a decoder is the same ring with the roles swapped.
/// </summary>
public sealed unsafe class SharedFrameRing : IDisposable
{
    public const int Slots = 3;
    private const int HeaderBytes = 64;
    private const int SlotHeaderBytes = 64;
    private const uint Magic = 0x31524650;   // "PFR1"
    private const int Version = 1;

    // Header: [0] magic u32 · [4] version i32 · [8] width i32 · [12] height i32 · [16] slots i32 · [20] frame bytes i32 · [24] latest seq i64 · [32] closed i32
    // Slot i at HeaderBytes + i × (SlotHeaderBytes + frame bytes): [0] seq i64 (−1 while being written) · [8] UTC ticks i64 · [16] frame index i64 · pixels at +SlotHeaderBytes

    private readonly MemoryMappedFile _map;
    private readonly MemoryMappedViewAccessor _view;
    private readonly EventWaitHandle? _frame;   // Windows: set after every frame and by the owner's close; a reader waits on it instead of polling
    private readonly bool _owner;
    private readonly string? _filePath;
    private byte* _base;
    private bool _disposed;

    private SharedFrameRing(string address, MemoryMappedFile map, EventWaitHandle? frame, bool owner, string? filePath, int width, int height)
    {
        Address = address;
        _map = map;
        _frame = frame;
        _owner = owner;
        _filePath = filePath;
        Width = width;
        Height = height;
        _view = map.CreateViewAccessor(0, CapacityFor(width, height), MemoryMappedFileAccess.ReadWrite);
        _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _base);
        _base += _view.PointerOffset;
    }

    /// <summary>What a child needs to open the ring: the map's name on Windows, the file's path elsewhere.</summary>
    public string Address { get; }

    public int Width { get; }

    public int Height { get; }

    public int Stride => Width * 4;

    public int FrameBytes => Stride * Height;

    /// <summary>The whole map for this geometry: the header and the slots.</summary>
    public static long CapacityFor(int width, int height) => HeaderBytes + (long)Slots * (SlotHeaderBytes + (long)width * 4 * height);

    /// <summary>The parent's side: a fresh ring for this geometry, empty, open.</summary>
    public static SharedFrameRing Create(string name, int width, int height)
    {
        if (width < 1 || height < 1) throw new ArgumentOutOfRangeException(nameof(width), "a frame needs a size");
        var capacity = CapacityFor(width, height);
        SharedFrameRing ring;
        if (OperatingSystem.IsWindows())
        {
            var map = MemoryMappedFile.CreateNew(name, capacity, MemoryMappedFileAccess.ReadWrite);
            ring = new SharedFrameRing(name, map, FrameEvent(name, create: true), owner: true, filePath: null, width, height);
        }
        else
        {
            var path = FilePathFor(name);
            var map = MemoryMappedFile.CreateFromFile(path, FileMode.Create, null, capacity, MemoryMappedFileAccess.ReadWrite);
            ring = new SharedFrameRing(path, map, null, owner: true, filePath: path, width, height);
        }
        var h = ring._base;
        *(uint*)h = Magic;
        *(int*)(h + 4) = Version;
        *(int*)(h + 8) = width;
        *(int*)(h + 12) = height;
        *(int*)(h + 16) = Slots;
        *(int*)(h + 20) = ring.FrameBytes;
        Volatile.Write(ref *(long*)(h + 24), 0);
        Volatile.Write(ref *(int*)(h + 32), 0);
        for (var i = 0; i < Slots; i++) Volatile.Write(ref *(long*)ring.SlotHeader(i), 0);
        return ring;
    }

    /// <summary>The child's side: the ring the parent made, by its address; its geometry is read from the header.</summary>
    public static SharedFrameRing Open(string address)
    {
        MemoryMappedFile map;
        string? filePath = null;
        if (OperatingSystem.IsWindows())
        {
            map = MemoryMappedFile.OpenExisting(address, MemoryMappedFileRights.ReadWrite);
        }
        else
        {
            filePath = address;
            map = MemoryMappedFile.CreateFromFile(address, FileMode.Open, null, 0, MemoryMappedFileAccess.ReadWrite);
        }
        int width, height;
        using (var head = map.CreateViewAccessor(0, HeaderBytes, MemoryMappedFileAccess.Read))
        {
            if (head.ReadUInt32(0) != Magic)
            {
                map.Dispose();
                throw new InvalidDataException($"'{address}' is not a Patterns frame ring");
            }
            width = head.ReadInt32(8);
            height = head.ReadInt32(12);
        }
        return new SharedFrameRing(address, map, FrameEvent(address, create: false), owner: false, filePath, width, height);
    }

    /// <summary>
    /// The event beside a Windows ring — made with the map by the owner, opened by a reader; null
    /// where named events do not exist, or where this one cannot be had (the reader then polls).
    /// </summary>
    private static EventWaitHandle? FrameEvent(string name, bool create)
    {
        if (!OperatingSystem.IsWindows()) return null;
        try
        {
            return create
                ? new EventWaitHandle(false, EventResetMode.AutoReset, name + ".frame")
                : EventWaitHandle.OpenExisting(name + ".frame");
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Where a file-backed ring lives: shared memory when the system has it, the temp folder otherwise.</summary>
    public static string FilePathFor(string name)
        => Path.Combine(Directory.Exists("/dev/shm") ? "/dev/shm" : Path.GetTempPath(), name);

    /// <summary>A name no other ring on this machine has: the role, the process, a counter.</summary>
    public static string NameFor(string role) => $"patterns-{role}-{Environment.ProcessId}-{Interlocked.Increment(ref _counter)}";

    private static int _counter;

    /// <summary>The newest frame's sequence number; 0 before the first frame, and after the ring is gone.</summary>
    public long LatestSeq => _disposed ? 0 : Volatile.Read(ref *(long*)(_base + 24));

    /// <summary>Set by the owner on dispose: a reader stops waiting. A disposed ring reads as closed on either side.</summary>
    public bool IsClosed => _disposed || Volatile.Read(ref *(int*)(_base + 32)) != 0;

    /// <summary>Whether a reader is woken by the writer (Windows) rather than polling for the next frame.</summary>
    public bool Signalled => _frame is not null;

    /// <summary>The pixels of a slot, for a surface over them; the slot's first row starts here, <see cref="Stride"/> bytes per row.</summary>
    public IntPtr PixelsOf(int slot) => _disposed ? throw new ObjectDisposedException(nameof(SharedFrameRing)) : (IntPtr)(SlotHeader(slot) + SlotHeaderBytes);

    /// <summary>
    /// The writer's next slot — the one after the newest frame's, marked as being written so a
    /// reader never takes it half drawn. Draw into <see cref="PixelsOf"/> then <see cref="EndWrite"/>.
    /// </summary>
    public int BeginWrite()
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SharedFrameRing));
        var slot = (int)((LatestSeq + 1) % Slots);
        Volatile.Write(ref *(long*)SlotHeader(slot), -1);
        // A full fence: the pixels drawn next are never seen before the mark, on any CPU — a release
        // store alone orders what came before it, not what follows.
        Interlocked.MemoryBarrier();
        return slot;
    }

    /// <summary>The frame in the slot is whole: it becomes the newest, and a waiting reader is woken. Returns its sequence number.</summary>
    public long EndWrite(int slot, long frameIndex)
    {
        if (_disposed) throw new ObjectDisposedException(nameof(SharedFrameRing));
        var seq = LatestSeq + 1;
        var h = SlotHeader(slot);
        *(long*)(h + 8) = DateTime.UtcNow.Ticks;
        *(long*)(h + 16) = frameIndex;
        Volatile.Write(ref *(long*)h, seq);
        Volatile.Write(ref *(long*)(_base + 24), seq);
        Wake();
        return seq;
    }

    /// <summary>
    /// Copies the newest frame later than <paramref name="afterSeq"/> into <paramref name="dest"/>
    /// (one frame long); false when there is none yet, or the writer lapped it mid-copy and no whole
    /// frame could be taken this time — ask again.
    /// </summary>
    public bool TryRead(long afterSeq, Span<byte> dest, out long seq)
    {
        seq = 0;
        if (dest.Length < FrameBytes) throw new ArgumentException("the destination is smaller than a frame", nameof(dest));
        for (var attempt = 0; attempt < 4 && !_disposed; attempt++)
        {
            var latest = LatestSeq;
            if (latest <= afterSeq) return false;
            var slot = (int)(latest % Slots);
            var h = SlotHeader(slot);
            if (Volatile.Read(ref *(long*)h) != latest) continue;   // being rewritten already: the newer frame is on its way
            new ReadOnlySpan<byte>(h + SlotHeaderBytes, FrameBytes).CopyTo(dest);
            Interlocked.MemoryBarrier();                             // the copy's reads stay before the check, on any CPU
            if (Volatile.Read(ref *(long*)h) != latest) continue;   // torn: the writer lapped us
            seq = latest;
            return true;
        }
        return false;
    }

    /// <summary>
    /// Waits up to <paramref name="timeoutMs"/> for a frame later than <paramref name="afterSeq"/>:
    /// its sequence number, or −1 when none came in time or the ring closed. On Windows the writer's
    /// event wakes the wait the moment a frame is published (the owner's close wakes it too; a wait
    /// is capped at 100 ms for a writer that died without a word); elsewhere a millisecond's poll.
    /// </summary>
    public long WaitForFrame(long afterSeq, int timeoutMs)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (true)
        {
            if (IsClosed) return -1;
            var latest = LatestSeq;
            if (latest > afterSeq) return latest;
            var left = deadline - Environment.TickCount64;
            if (left <= 0) return -1;
            if (_frame is { } frame)
            {
                try
                {
                    frame.WaitOne((int)Math.Min(left, 100));
                }
                catch (ObjectDisposedException)
                {
                    return -1;   // this side closed while waiting
                }
            }
            else
            {
                Thread.Sleep(1);
            }
        }
    }

    /// <summary>A waiting reader is woken: a frame published, or the owner gone.</summary>
    private void Wake()
    {
        try
        {
            _frame?.Set();
        }
        catch (ObjectDisposedException)
        {
            // Closing already.
        }
    }

    private byte* SlotHeader(int slot) => _base + HeaderBytes + (long)slot * (SlotHeaderBytes + FrameBytes);

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try
        {
            if (_owner) Volatile.Write(ref *(int*)(_base + 32), 1);
        }
        catch
        {
            // A map already gone cannot be marked; readers see the missing file instead.
        }
        if (_owner) Wake();
        _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view.Dispose();
        _map.Dispose();
        _frame?.Dispose();
        if (_owner && _filePath is not null)
        {
            try
            {
                File.Delete(_filePath);
            }
            catch
            {
                // A reader still holds it open; the shared-memory folder is cleared on reboot.
            }
        }
    }
}
