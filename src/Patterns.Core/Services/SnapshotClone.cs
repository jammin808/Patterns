using System.Collections;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Serialization;
using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// The copy of the show a snapshot carries, and how it is made.
///
/// A snapshot used to be a JSON round trip of the whole show on every publish — every keystroke,
/// every slider tick, every pixel of a drag — and twice that while EDIT SAFE held a frozen
/// program beside the edits. The show being copied is mostly the parts that did not move: twelve
/// looks with their payloads, forty cues, the stinger library, the people list. Here a publish
/// copies only the sections of <see cref="ShowState"/> that were written since the last one and
/// shares the rest with the snapshot before it — safe because a published section is marked
/// (<see cref="Observable.MarkPublished"/>) and read by every sink and never written by any. A drag of
/// a layer then copies the pattern; a drag of the clock copies the overlays; the looks, the cues
/// and the rest are the very same objects the last snapshot carried.
/// </summary>
public static class SnapshotClone
{
    /// <summary>The root's public, serialized, non-indexer properties: the sections and the scalars.</summary>
    private static readonly PropertyInfo[] Sections = typeof(ShowState)
        .GetProperties(BindingFlags.Public | BindingFlags.Instance)
        .Where(p => p.GetIndexParameters().Length == 0 && p.CanRead && p.CanWrite)
        .Where(p => p.GetCustomAttribute<JsonIgnoreAttribute>() is null)
        .ToArray();

    /// <summary>A whole copy, marked published — the first snapshot from a root, or one with no change record to go by.</summary>
    public static ShowState Clone(ShowState live)
    {
        var copy = JsonUtil.Clone(live);
        MarkPublished(copy);
        return copy;
    }

    /// <summary>
    /// A copy that shares with <paramref name="previous"/> every section not in <paramref name="dirty"/>:
    /// the named sections are copied afresh from <paramref name="live"/> and marked published, the scalars
    /// (the show's name, its mode, the blackout) are always read fresh, and everything else is the
    /// previous snapshot's own object.
    /// </summary>
    public static ShowState Compose(ShowState live, ShowState previous, IReadOnlySet<string> dirty)
    {
        // Nothing moved at all — the frozen program republished beside every edit of the preview
        // — and the whole previous copy stands: not one object is made.
        if (dirty.Count == 0 && ScalarsAgree(live, previous)) return previous;
        var result = new ShowState();
        foreach (var p in Sections)
        {
            var type = p.PropertyType;
            if (type.IsValueType || type == typeof(string))
            {
                p.SetValue(result, p.GetValue(live));
                continue;
            }
            var section = dirty.Contains(p.Name) ? CloneSection(p.GetValue(live), type) : p.GetValue(previous);
            p.SetValue(result, section);
        }
        result.MarkPublished();
        return result;
    }

    private static bool ScalarsAgree(ShowState live, ShowState previous)
    {
        foreach (var p in Sections)
        {
            var type = p.PropertyType;
            if (!type.IsValueType && type != typeof(string)) continue;
            if (!Equals(p.GetValue(live), p.GetValue(previous))) return false;
        }
        return true;
    }

    /// <summary>Which of a root's sections a snapshot shares with the one before it — what the tests read.</summary>
    public static IReadOnlyList<string> SharedSections(ShowState a, ShowState b)
    {
        var shared = new List<string>();
        foreach (var p in Sections)
        {
            var type = p.PropertyType;
            if (type.IsValueType || type == typeof(string)) continue;
            if (ReferenceEquals(p.GetValue(a), p.GetValue(b))) shared.Add(p.Name);
        }
        return shared;
    }

    private static object? CloneSection(object? value, Type type)
    {
        if (value is null) return null;
        var json = JsonSerializer.Serialize(value, type, JsonUtil.CloneOptions);
        var copy = JsonSerializer.Deserialize(json, type, JsonUtil.CloneOptions)
                   ?? throw new InvalidOperationException($"Clone of {type.Name} produced null.");
        MarkPublished(copy);
        return copy;
    }

    private static readonly System.Collections.Concurrent.ConcurrentDictionary<Type, PropertyInfo[]> Walkable = new();

    /// <summary>
    /// Marks every <see cref="Observable"/> in a graph published — the object, what its properties
    /// hold, what its collections hold. A JSON-shaped tree has no cycles, and marking twice is
    /// nothing, so the walk needs no visited set.
    /// </summary>
    public static void MarkPublished(object? node)
    {
        if (node is null || node is string) return;
        if (node is Observable obs) obs.MarkPublished();
        if (node is IEnumerable items)
        {
            foreach (var item in items) MarkPublished(item);
        }
        foreach (var p in Walkable.GetOrAdd(node.GetType(), static t => t.GetProperties(BindingFlags.Public | BindingFlags.Instance)
                     .Where(p => p.GetIndexParameters().Length == 0 && p.CanRead)
                     .Where(p => !p.PropertyType.IsValueType && p.PropertyType != typeof(string))
                     .Where(p => typeof(Observable).IsAssignableFrom(p.PropertyType) || typeof(IEnumerable).IsAssignableFrom(p.PropertyType))
                     .ToArray()))
        {
            object? child;
            try { child = p.GetValue(node); }
            catch { continue; }
            MarkPublished(child);
        }
    }
}
