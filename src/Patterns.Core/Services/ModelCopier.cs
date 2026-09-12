using System.Collections;
using System.Reflection;
using System.Text.Json.Serialization;
using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// Copies one model graph onto another in place, so UI bindings (which hold object
/// references) survive loading shows, presets and brand kits. Runtime-only
/// ([JsonIgnore]) properties are left untouched.
/// </summary>
public static class ModelCopier
{
    public static void Copy(object source, object target)
    {
        if (source.GetType() != target.GetType())
        {
            throw new ArgumentException($"Type mismatch: {source.GetType().Name} → {target.GetType().Name}");
        }

        foreach (var pi in source.GetType().GetProperties(BindingFlags.Public | BindingFlags.Instance))
        {
            if (pi.GetIndexParameters().Length != 0) continue;
            if (pi.GetCustomAttribute<JsonIgnoreAttribute>() is not null) continue;
            CopyValue(pi, pi.GetValue(source), target);
        }
    }

    /// <summary>
    /// One property's value onto <paramref name="target"/>, the way <see cref="Copy"/> lands each of
    /// them: a value or a string is set, a list is cleared and refilled with detached clones, an
    /// observable is copied into in place. The twin mirror lands a section with it.
    /// </summary>
    public static void CopyValue(PropertyInfo pi, object? value, object target)
    {
        var type = pi.PropertyType;
        if (type.IsValueType || type == typeof(string))
        {
            if (pi.CanWrite) pi.SetValue(target, value);
            return;
        }

        var targetValue = pi.GetValue(target);
        if (value is null || targetValue is null) return;

        if (typeof(IList).IsAssignableFrom(type) && value is IList srcList && targetValue is IList dstList)
        {
            dstList.Clear();
            foreach (var item in srcList)
            {
                // Collection items are detached clones so later edits don't alias.
                dstList.Add(item is Observable ? CloneItem(item) : item);
            }
            return;
        }

        if (value is Observable && targetValue is Observable)
        {
            Copy(value, targetValue);
        }
    }

    private static object CloneItem(object item)
    {
        var json = System.Text.Json.JsonSerializer.Serialize(item, item.GetType(), JsonUtil.Options);
        return System.Text.Json.JsonSerializer.Deserialize(json, item.GetType(), JsonUtil.Options)!;
    }
}
