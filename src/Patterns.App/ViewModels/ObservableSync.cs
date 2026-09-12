using System.Collections.ObjectModel;

namespace Patterns.App.ViewModels;

/// <summary>
/// Brings a page's collection to a wanted list in place: what stays keeps its container, its
/// selection and the operator's place in the list; what is gone goes; what is new comes in where
/// it sits. A Clear-and-refill made every ItemsControl on the page tear down and remake every
/// container, and lost the scroll position and the selection with it.
/// </summary>
public static class ObservableSync
{
    public static void Sync<T>(ObservableCollection<T> page, IReadOnlyList<T> wanted) where T : class
    {
        var want = new HashSet<T>(wanted, ReferenceEqualityComparer.Instance);
        for (var i = page.Count - 1; i >= 0; i--)
        {
            if (!want.Contains(page[i])) page.RemoveAt(i);
        }
        for (var i = 0; i < wanted.Count; i++)
        {
            var item = wanted[i];
            var at = page.IndexOf(item);
            if (at == i) continue;
            if (at < 0) page.Insert(i, item);
            else page.Move(at, i);
        }
    }
}
