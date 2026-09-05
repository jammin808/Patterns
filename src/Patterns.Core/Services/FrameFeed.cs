namespace Patterns.Core.Services;

/// <summary>
/// The hand-off between a renderer and an encoder that pulls bytes: one frame at a time, read
/// in whatever chunks the reader asks for, a frame never torn (a new frame waits until the
/// current one has been read out), the newest frame winning when the reader is slow. Pure and
/// thread-safe; the App wraps it in libVLC's memory input.
/// </summary>
public sealed class FrameFeed
{
    private readonly object _gate = new();
    private byte[]? _current;
    private int _offset;
    private byte[]? _next;
    private bool _closed;

    public FrameFeed(int frameBytes) => FrameBytes = Math.Max(1, frameBytes);

    /// <summary>The size of one frame; a published frame must be exactly this long.</summary>
    public int FrameBytes { get; }

    /// <summary>Frames published; frames the reader never saw are <see cref="Dropped"/>.</summary>
    public long Published { get; private set; }

    public long Dropped { get; private set; }

    public bool IsClosed
    {
        get
        {
            lock (_gate) return _closed;
        }
    }

    /// <summary>Offers a frame; the reader takes it after the one it is on. A frame it never started is replaced.</summary>
    public bool Publish(byte[] frame)
    {
        if (frame.Length != FrameBytes) return false;
        lock (_gate)
        {
            if (_closed) return false;
            Published++;
            if (_next is not null) Dropped++;
            _next = frame;
            Monitor.PulseAll(_gate);
        }
        return true;
    }

    /// <summary>
    /// Copies the next bytes into <paramref name="dest"/>: the rest of the current frame, then
    /// the next. Blocks up to <paramref name="timeoutMs"/> for a frame; 0 when closed, or when
    /// nothing arrived in time (the reader may ask again).
    /// </summary>
    public int Read(Span<byte> dest, int timeoutMs = 1000)
    {
        if (dest.Length == 0) return 0;
        lock (_gate)
        {
            if (_current is null || _offset >= _current.Length)
            {
                var deadline = Environment.TickCount64 + timeoutMs;
                while (!_closed && _next is null)
                {
                    var wait = deadline - Environment.TickCount64;
                    if (wait <= 0) return 0;
                    Monitor.Wait(_gate, (int)Math.Min(wait, int.MaxValue));
                }
                if (_closed) return 0;
                _current = _next;
                _next = null;
                _offset = 0;
            }
            var n = Math.Min(dest.Length, _current!.Length - _offset);
            _current.AsSpan(_offset, n).CopyTo(dest);
            _offset += n;
            return n;
        }
    }

    /// <summary>Ends the feed: readers get 0, publishers are refused.</summary>
    public void Close()
    {
        lock (_gate)
        {
            _closed = true;
            Monitor.PulseAll(_gate);
        }
    }
}
