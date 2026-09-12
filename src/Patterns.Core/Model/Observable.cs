using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Patterns.Core.Model;

/// <summary>
/// Minimal INotifyPropertyChanged base. No third-party MVVM dependency — fewer moving parts,
/// and the model stays a plain serializable POCO.
/// </summary>
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    private bool _published;

    /// <summary>
    /// True once <see cref="MarkPublished"/> ran: this object is a published snapshot's copy, read
    /// by every render thread and shared between one snapshot and the next, and no setter may
    /// touch it again. The live model the desk edits is never marked.
    /// </summary>
    [JsonIgnore]
    public bool IsPublished => _published;

    /// <summary>
    /// Makes every later write through <see cref="Set{T}"/> throw. One way, on purpose: a snapshot
    /// is immutable by contract, and the contract used to be a convention nothing enforced —
    /// a renderer that "tidied" a value on the copy it was handed would have corrupted the
    /// picture on every other sink, silently. Now it is an exception the engine contains to an
    /// error card on that one sink, and a test failure before that.
    /// </summary>
    public void MarkPublished() => _published = true;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        if (_published) ThrowPublished(name);
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    private void ThrowPublished(string? name)
        => throw new InvalidOperationException(
            $"{GetType().Name}.{name} was written on a published snapshot. Snapshots are immutable: edit the live show state, never the copy a sink draws.");
}

/// <summary>
/// Subscribes (once, at wiring time — zero per-frame cost) to every <see cref="Observable"/>
/// and <see cref="ObservableCollection{T}"/> reachable from a root object and funnels all
/// change notifications into a single callback. Used to version the show state so render
/// sinks know when to take a fresh snapshot.
///
/// With <c>trackSections</c> on it also remembers WHICH of the root's own properties each change
/// landed under — its section — so a publish can copy only the sections that moved and share the
/// rest with the snapshot before it (<see cref="Services.SnapshotBus"/>). A write to a
/// <see cref="JsonIgnoreAttribute"/> property never reaches a snapshot, so it dirties nothing and
/// goes to <c>onRuntimeOnlyChanged</c> instead — the desk's chrome (a tally, a status line, a
/// device's counters) must never spend a publish, nor the snapshot version a look's own
/// transition is riding on. Without that callback it raises <c>onChanged</c> as it always did.
///
/// The set of wired objects is held weakly: an item taken out of a list is never held here again,
/// so a show that adds and removes looks, cues and playlist rows for a week does not keep every
/// one of them alive.
/// </summary>
public sealed class ChangeTracker
{
    private static readonly object Marker = new();
    private static readonly ConcurrentPropertyMeta Meta = new();

    private readonly Action _onChanged;
    private readonly Action _onRuntimeOnlyChanged;
    private readonly ConditionalWeakTable<object, object> _wired = new();
    private readonly HashSet<string>? _dirty;
    private bool _allDirty = true;

    /// <param name="trackSections">Remember the root's top-level property under which each change landed, for <see cref="TakeDirty"/>.</param>
    /// <param name="onRuntimeOnlyChanged">Raised instead of <paramref name="onChanged"/> for a write to a <see cref="JsonIgnoreAttribute"/> property; null = <paramref name="onChanged"/>.</param>
    public ChangeTracker(object root, Action onChanged, bool trackSections = false, Action? onRuntimeOnlyChanged = null)
    {
        Root = root;
        _onChanged = onChanged;
        _onRuntimeOnlyChanged = onRuntimeOnlyChanged ?? onChanged;
        if (trackSections) _dirty = new HashSet<string>(StringComparer.Ordinal);
        Wire(root, section: null, isRoot: true);
    }

    /// <summary>The object this tracker watches.</summary>
    public object Root { get; }

    /// <summary>Whether <see cref="TakeDirty"/> can say which sections moved.</summary>
    public bool TracksSections => _dirty is not null;

    /// <summary>
    /// The root's top-level property names written since the last call, then a fresh count. Null
    /// means "everything": the first call (nothing has been published from this root yet), a
    /// change whose section could not be named, or a tracker built without section tracking.
    /// </summary>
    public HashSet<string>? TakeDirty()
    {
        if (_dirty is null) return null;
        if (_allDirty)
        {
            _allDirty = false;
            _dirty.Clear();
            return null;
        }
        var taken = new HashSet<string>(_dirty, StringComparer.Ordinal);
        _dirty.Clear();
        return taken;
    }

    private void MarkDirty(string? section)
    {
        if (_dirty is not null)
        {
            if (section is null) _allDirty = true;
            else _dirty.Add(section);
        }
        _onChanged();
    }

    private void Wire(object? node, string? section, bool isRoot = false)
    {
        if (node is null || !_wired.TryAdd(node, Marker)) return;

        if (node is Observable obs)
        {
            obs.PropertyChanged += (sender, e) =>
            {
                var owner = sender ?? node;
                var name = e.PropertyName;
                // The section a change belongs to: the root's own property, or the branch this
                // object hangs from. A write to the root with no name ("everything changed") is
                // everything.
                var changed = isRoot ? name : section;
                if (name is { Length: > 0 } && Meta.For(owner.GetType(), name) is { } pi)
                {
                    // A reference-typed property may have been swapped for a brand-new object
                    // graph (e.g. loading a show file section) — wire the newcomer too.
                    if (pi.IsReference) Wire(pi.Property.GetValue(owner), changed);
                    // Runtime-only chrome never reaches a snapshot: the copy the sinks hold is right as it is.
                    if (pi.IsJsonIgnored)
                    {
                        _onRuntimeOnlyChanged();
                        return;
                    }
                }
                MarkDirty(changed);
            };
        }

        if (node is INotifyCollectionChanged col)
        {
            col.CollectionChanged += (_, e) =>
            {
                if (e.NewItems is not null)
                {
                    foreach (var item in e.NewItems) Wire(item, section);
                }
                MarkDirty(section);
            };
        }

        // Recurse into child objects.
        foreach (var pi in Meta.References(node.GetType()))
        {
            object? value;
            try { value = pi.GetValue(node); }
            catch { continue; }

            var childSection = isRoot ? pi.Name : section;
            if (value is System.Collections.IEnumerable en and not string)
            {
                foreach (var item in en) Wire(item, childSection);
            }
            Wire(value, childSection);
        }
    }

    /// <summary>What the tracker needs to know about a property, looked up once per type.</summary>
    private sealed class ConcurrentPropertyMeta
    {
        public sealed record Info(PropertyInfo Property, bool IsReference, bool IsJsonIgnored);

        private readonly System.Collections.Concurrent.ConcurrentDictionary<Type, Dictionary<string, Info>> _byType = new();
        private readonly System.Collections.Concurrent.ConcurrentDictionary<Type, PropertyInfo[]> _references = new();

        public Info? For(Type type, string name)
            => _byType.GetOrAdd(type, Build).TryGetValue(name, out var info) ? info : null;

        /// <summary>The properties worth walking into: observables, collections, anything enumerable — never a value or a string.</summary>
        public PropertyInfo[] References(Type type)
            => _references.GetOrAdd(type, static t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                .Where(p => p.GetIndexParameters().Length == 0)
                .Where(p =>
                {
                    var pt = p.PropertyType;
                    if (pt.IsValueType || pt == typeof(string)) return false;
                    return typeof(Observable).IsAssignableFrom(pt)
                        || typeof(INotifyCollectionChanged).IsAssignableFrom(pt)
                        || typeof(System.Collections.IEnumerable).IsAssignableFrom(pt);
                })
                .ToArray());

        private static Dictionary<string, Info> Build(Type type)
        {
            var map = new Dictionary<string, Info>(StringComparer.Ordinal);
            foreach (var p in type.GetProperties(BindingFlags.Public | BindingFlags.Instance))
            {
                if (p.GetIndexParameters().Length != 0) continue;
                var pt = p.PropertyType;
                map[p.Name] = new Info(p, !pt.IsValueType && pt != typeof(string), p.GetCustomAttribute<JsonIgnoreAttribute>() is not null);
            }
            return map;
        }
    }
}
