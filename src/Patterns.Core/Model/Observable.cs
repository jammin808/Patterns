using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Reflection;
using System.Runtime.CompilerServices;

namespace Patterns.Core.Model;

/// <summary>
/// Minimal INotifyPropertyChanged base. No third-party MVVM dependency — fewer moving parts,
/// and the model stays a plain serializable POCO.
/// </summary>
public abstract class Observable : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return false;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }

    protected void Raise([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// Subscribes (once, at wiring time — zero per-frame cost) to every <see cref="Observable"/>
/// and <see cref="ObservableCollection{T}"/> reachable from a root object and funnels all
/// change notifications into a single callback. Used to version the show state so render
/// sinks know when to take a fresh snapshot.
/// </summary>
public sealed class ChangeTracker
{
    private readonly Action _onChanged;
    private readonly HashSet<object> _wired = new(ReferenceEqualityComparer.Instance);

    public ChangeTracker(object root, Action onChanged)
    {
        _onChanged = onChanged;
        Wire(root);
    }

    private void Wire(object? node)
    {
        if (node is null || !_wired.Add(node)) return;

        if (node is Observable obs)
        {
            obs.PropertyChanged += (_, e) =>
            {
                // A reference-typed property may have been swapped for a brand-new object
                // graph (e.g. loading a show file section) — wire the newcomer too.
                if (e.PropertyName is { } prop &&
                    node.GetType().GetProperty(prop, BindingFlags.Public | BindingFlags.Instance) is { } pi &&
                    !pi.PropertyType.IsValueType && pi.PropertyType != typeof(string))
                {
                    Wire(pi.GetValue(node));
                }
                _onChanged();
            };
        }

        if (node is INotifyCollectionChanged col)
        {
            col.CollectionChanged += (_, e) =>
            {
                if (e.NewItems is not null)
                {
                    foreach (var item in e.NewItems) Wire(item);
                }
                _onChanged();
            };
        }

        // Recurse into child objects.
        foreach (var pi in node.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (pi.GetIndexParameters().Length != 0) continue;
            var t = pi.PropertyType;
            if (t.IsValueType || t == typeof(string)) continue;
            if (!typeof(Observable).IsAssignableFrom(t) &&
                !typeof(INotifyCollectionChanged).IsAssignableFrom(t) &&
                !typeof(System.Collections.IEnumerable).IsAssignableFrom(t))
            {
                continue;
            }

            object? value;
            try { value = pi.GetValue(node); }
            catch { continue; }

            if (value is System.Collections.IEnumerable en and not string)
            {
                foreach (var item in en) Wire(item);
            }
            Wire(value);
        }
    }
}
