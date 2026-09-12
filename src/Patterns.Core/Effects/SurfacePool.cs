using SkiaSharp;

namespace Patterns.Core.Effects;

/// <summary>
/// A sink's CPU frames of one kind, one per size it draws. One sink draws more than one size in
/// a frame more often than it looks — a multiview draws a tile per screen at the tile's shape, a
/// screen layer draws another target inside a box, a dissolve draws the outgoing look under the
/// incoming one, a lower third rides every tile of a wall — and one frame serving them all was
/// disposed and allocated afresh on every draw, twice a frame at 60 Hz, for as long as the two
/// sizes were on. The least recently drawn goes when the sink holds <see cref="Capacity"/>.
/// Owned by one sink and touched on its render thread only.
/// </summary>
public sealed class SurfacePool<T> : IDisposable where T : class, IDisposable
{
    /// <summary>Sizes a sink keeps at once: a dissolve needs two, a monitor wall a few.</summary>
    public const int Capacity = 4;

    private sealed class Entry
    {
        public SKSizeI Size;
        public T Surface = null!;
        public long Used;
    }

    private readonly List<Entry> _entries = new();
    private long _tick;

    /// <summary>The frame of this size, made by <paramref name="make"/> the first time it is asked for.</summary>
    public T Get(SKSizeI size, Func<SKSizeI, T> make)
    {
        _tick++;
        foreach (var e in _entries)
        {
            if (e.Size != size) continue;
            e.Used = _tick;
            return e.Surface;
        }
        Entry entry;
        if (_entries.Count < Capacity)
        {
            entry = new Entry();
            _entries.Add(entry);
        }
        else
        {
            entry = _entries[0];
            foreach (var e in _entries) if (e.Used < entry.Used) entry = e;
            entry.Surface.Dispose();
        }
        entry.Size = size;
        entry.Surface = make(size);
        entry.Used = _tick;
        return entry.Surface;
    }

    /// <summary>The frame drawn most recently; null before the first.</summary>
    public T? Latest
    {
        get
        {
            Entry? best = null;
            foreach (var e in _entries) if (best is null || e.Used > best.Used) best = e;
            return best?.Surface;
        }
    }

    /// <summary>Every frame held, for a sweep that touches them all.</summary>
    public IEnumerable<T> All => _entries.Select(e => e.Surface);

    /// <summary>How many sizes this sink holds.</summary>
    public int Count => _entries.Count;

    public void Dispose()
    {
        foreach (var e in _entries) e.Surface.Dispose();
        _entries.Clear();
    }
}
