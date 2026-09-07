using Patterns.Core.Model;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.Core.Particles;

/// <summary>
/// A sink's particle sims, one per field it draws. One sink draws more than one field in a
/// frame more often than it looks: a crossfade draws the old snapshot under the new, a multiview
/// draws a tile per screen at the tile's size, a layer draws another target — and one sim
/// serving them all was re-seeded, settled and caught up on every draw, twice a frame at 60 Hz.
/// Every distinct field (the scene, its colours, the canvas — <see cref="ParticleSim.KeyFor"/>)
/// has a sim of its own here, found by the snapshot version and canvas it was last drawn with
/// (no allocation on the hot path) or by its key when the version moved on; a new one joins the
/// snapshot's leader for that field so it shows the same particles as the sinks already drawing it.
/// </summary>
public sealed class ParticleSimCache : IDisposable
{
    /// <summary>Fields a sink keeps at once: a crossfade needs two, a monitor wall a few; the least recently drawn goes.</summary>
    public const int Capacity = 6;

    private sealed class Entry
    {
        public ParticleSim Sim = new();
        public string Key = "";
        // The last two draws of this field — the snapshot version, the options object that
        // snapshot holds for the target (two targets of one snapshot are two objects, so two
        // scenes at one size are told apart without a key) and the canvas; a crossfade alternates.
        public long Version0 = -1, Version1 = -1;
        public ParticleOptions? Options0, Options1;
        public SKSizeI Canvas0, Canvas1;
        public long Used;
    }

    private readonly List<Entry> _entries = new();
    private long _tick;

    /// <summary>The sim for this field, configured and in step with the snapshot's leader.</summary>
    public ParticleSim Get(ParticleOptions o, ShowSnapshot snap, SKSizeI canvas)
    {
        _tick++;
        var version = snap.Version;
        foreach (var e in _entries)
        {
            if ((e.Version0 == version && ReferenceEquals(e.Options0, o) && e.Canvas0 == canvas) ||
                (e.Version1 == version && ReferenceEquals(e.Options1, o) && e.Canvas1 == canvas))
            {
                e.Used = _tick;
                return e.Sim;
            }
        }

        // The version moved on, or a field this sink has not drawn: find it by what it is.
        var key = ParticleSim.KeyFor(o, snap, canvas);
        foreach (var e in _entries)
        {
            if (e.Key != key) continue;
            e.Sim.Configure(o, snap, canvas, key);   // a no-op that takes the live options
            Stamp(e, version, o, canvas);
            e.Used = _tick;
            snap.ParticleLeaders.Offer(key, e.Sim);
            return e.Sim;
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
            entry.Sim.Dispose();
            entry.Sim = new ParticleSim();
            entry.Version0 = entry.Version1 = -1;
            entry.Options0 = entry.Options1 = null;
        }
        entry.Key = key;
        entry.Sim.Configure(o, snap, canvas, key);
        Stamp(entry, version, o, canvas);
        entry.Used = _tick;
        var leader = snap.ParticleLeaders.Offer(key, entry.Sim);
        if (!ReferenceEquals(leader, entry.Sim)) entry.Sim.JoinTimeline(leader);
        return entry.Sim;
    }

    private static void Stamp(Entry e, long version, ParticleOptions o, SKSizeI canvas)
    {
        if (e.Version0 == version && ReferenceEquals(e.Options0, o) && e.Canvas0 == canvas) return;
        e.Version1 = e.Version0;
        e.Options1 = e.Options0;
        e.Canvas1 = e.Canvas0;
        e.Version0 = version;
        e.Options0 = o;
        e.Canvas0 = canvas;
    }

    /// <summary>The sim drawn most recently (tests, and the desk's readouts).</summary>
    public ParticleSim? Latest
    {
        get
        {
            Entry? best = null;
            foreach (var e in _entries) if (best is null || e.Used > best.Used) best = e;
            return best?.Sim;
        }
    }

    /// <summary>How many fields this sink holds.</summary>
    public int Count => _entries.Count;

    public void Dispose()
    {
        foreach (var e in _entries) e.Sim.Dispose();
        _entries.Clear();
    }
}

/// <summary>
/// The leaders of a snapshot's particle fields: the first sim to draw a field under a snapshot
/// leads it, and every sim that starts the same field later copies the leader's timeline
/// (<see cref="ParticleSim.JoinTimeline"/>) instead of anchoring on its own — so the PGM pane, an
/// output opened at OUTPUTS ON, an NDI send started mid-show and a display plugged in late all
/// show one field. Runtime-only, on the snapshot: a new version starts with no leaders and the
/// sims already running the field lead it from there. Weak, so a closed sink's sim goes with it.
/// </summary>
public sealed class ParticleLeaders
{
    private readonly object _gate = new();
    private readonly Dictionary<string, WeakReference<ParticleSim>> _leaders = new();

    /// <summary>Registers <paramref name="sim"/> as the field's leader unless a live one leads already; returns the one to follow (itself when it leads).</summary>
    public ParticleSim Offer(string key, ParticleSim sim)
    {
        lock (_gate)
        {
            if (_leaders.TryGetValue(key, out var weak) && weak.TryGetTarget(out var leader) && !leader.IsDisposed && leader.StepsDone >= 0)
            {
                return leader;
            }
            _leaders[key] = new WeakReference<ParticleSim>(sim);
            return sim;
        }
    }

    /// <summary>The live leader of a field, if any (tests).</summary>
    public ParticleSim? Leader(string key)
    {
        lock (_gate)
        {
            return _leaders.TryGetValue(key, out var weak) && weak.TryGetTarget(out var leader) && !leader.IsDisposed ? leader : null;
        }
    }
}
