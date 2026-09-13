namespace Patterns.Core.Media;

/// <summary>
/// A ring of frame buffers between one writer and any number of readers, with no lock held across
/// a draw, a copy or a send. The writer <see cref="Acquire"/>s a buffer that is neither the newest
/// (a reader may pin it next) nor held by a reader, draws into it and <see cref="Publish"/>es it as
/// the newest; a reader <see cref="Pin"/>s the newest — the writer will not take it while it is
/// pinned — reads it and <see cref="Unpin"/>s. A writer that finds no free buffer skips the frame
/// rather than waiting, so a game loop never blocks on a slow reader (a window's draw, a copy for
/// the input bus, an NDI send), and a slow reader always sees the newest frame there is, never a
/// queue of old ones. Four buffers serve one writer and two readers with one spare. The sequence
/// counts publishes, so a lane can wait for a frame newer than the one it last sent. The buffers
/// themselves are the owner's; the ring only says which index is whose.
/// </summary>
public sealed class FrameRing
{
    private readonly object _gate = new();
    private readonly int[] _pins;
    private int _latest = -1;
    private int _writing = -1;
    private long _sequence;
    private long _skipped;

    public FrameRing(int buffers = 4)
    {
        if (buffers < 2) throw new ArgumentOutOfRangeException(nameof(buffers), "A ring needs two buffers at least.");
        Buffers = buffers;
        _pins = new int[buffers];
    }

    public int Buffers { get; }

    /// <summary>How many frames have been published.</summary>
    public long Sequence
    {
        get { lock (_gate) return _sequence; }
    }

    /// <summary>Frames the writer skipped for want of a free buffer — readers holding too much for too long.</summary>
    public long Skipped
    {
        get { lock (_gate) return _skipped; }
    }

    /// <summary>The newest published buffer's index, or -1 before the first frame.</summary>
    public int Latest
    {
        get { lock (_gate) return _latest; }
    }

    /// <summary>How many readers hold a buffer.</summary>
    public int Pins(int index)
    {
        lock (_gate) return index >= 0 && index < Buffers ? _pins[index] : 0;
    }

    /// <summary>A buffer for the writer — never the newest, never one a reader holds — or -1 to skip this frame.</summary>
    public int Acquire()
    {
        lock (_gate)
        {
            if (_writing >= 0) return -1;   // one writer: a frame is being drawn already
            for (var i = 0; i < Buffers; i++)
            {
                if (i == _latest || _pins[i] > 0) continue;
                _writing = i;
                return i;
            }
            _skipped++;
            return -1;
        }
    }

    /// <summary>The acquired buffer becomes the newest: the sequence moves and a waiting lane wakes.</summary>
    public void Publish(int index)
    {
        lock (_gate)
        {
            if (index != _writing) throw new InvalidOperationException("Publish names a buffer that was not acquired.");
            _writing = -1;
            _latest = index;
            _sequence++;
            Monitor.PulseAll(_gate);
        }
    }

    /// <summary>An acquired buffer given back unpublished — a draw that faulted; the last frame stands.</summary>
    public void Abandon(int index)
    {
        lock (_gate)
        {
            if (index == _writing) _writing = -1;
        }
    }

    /// <summary>The newest buffer, held for the reader until <see cref="Unpin"/>; -1 before the first frame.</summary>
    public int Pin()
    {
        lock (_gate)
        {
            if (_latest < 0) return -1;
            _pins[_latest]++;
            return _latest;
        }
    }

    public void Unpin(int index)
    {
        lock (_gate)
        {
            if (index < 0 || index >= Buffers || _pins[index] == 0) return;
            if (--_pins[index] == 0) Monitor.PulseAll(_gate);
        }
    }

    /// <summary>Waits for a frame past <paramref name="seen"/>: true with the sequence now, false when the wait ran out.</summary>
    public bool WaitNewer(long seen, int timeoutMs, out long sequence)
    {
        lock (_gate)
        {
            var deadline = Environment.TickCount64 + timeoutMs;
            while (_sequence <= seen)
            {
                var left = deadline - Environment.TickCount64;
                if (left <= 0)
                {
                    sequence = _sequence;
                    return false;
                }
                Monitor.Wait(_gate, (int)Math.Min(left, int.MaxValue));
            }
            sequence = _sequence;
            return true;
        }
    }

    /// <summary>Wakes every waiter — for a lane told to stop.</summary>
    public void Wake()
    {
        lock (_gate) Monitor.PulseAll(_gate);
    }

    /// <summary>
    /// Forgets the newest frame so no new reader pins one, then waits for the readers that hold a
    /// buffer to let go: true when none is held, false when one still is after the wait — the
    /// owner then keeps its buffers a little longer rather than freeing them under a draw.
    /// </summary>
    public bool Drain(int timeoutMs)
    {
        lock (_gate)
        {
            _latest = -1;
            _writing = -1;
            var deadline = Environment.TickCount64 + timeoutMs;
            while (Array.Exists(_pins, p => p > 0))
            {
                var left = deadline - Environment.TickCount64;
                if (left <= 0) return false;
                Monitor.Wait(_gate, (int)Math.Min(left, int.MaxValue));
            }
            return true;
        }
    }
}
