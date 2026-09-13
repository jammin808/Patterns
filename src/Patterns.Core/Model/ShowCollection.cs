using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace Patterns.Core.Model;

/// <summary>Anything the walk that marks a published snapshot can freeze: from then on, every write throws.</summary>
public interface IFreezable
{
    bool IsFrozen { get; }

    void Freeze();
}

/// <summary>
/// The show's list: an <see cref="ObservableCollection{T}"/> a publish freezes. A published
/// snapshot's scalars have thrown on a write since the snapshot bus (<see cref="Observable.Set{T}"/>);
/// its lists did not — a sink could add to, take from or reorder a published look's overlays,
/// and every other sink, and the next snapshot sharing that section, would have seen it, silently.
/// Now the walk that marks a snapshot published freezes each list on the way, and Add, Insert,
/// Remove, Clear, Move and the indexer throw the exception the scalars throw — contained by the
/// engine to an error card on the one sink, and a test failure before that. The live show's
/// lists are never frozen: a copy made by JSON is a new, open list.
/// </summary>
public class ShowCollection<T> : ObservableCollection<T>, IFreezable
{
    private bool _frozen;

    public ShowCollection()
    {
    }

    public ShowCollection(IEnumerable<T> items) : base(items)
    {
    }

    /// <summary>True once a publish walked over it: this list belongs to a published snapshot and takes no write.</summary>
    [JsonIgnore]
    public bool IsFrozen => _frozen;

    /// <summary>One way, on purpose — a snapshot is immutable by contract.</summary>
    public void Freeze() => _frozen = true;

    protected override void InsertItem(int index, T item)
    {
        Guard("added to");
        base.InsertItem(index, item);
    }

    protected override void RemoveItem(int index)
    {
        Guard("taken from");
        base.RemoveItem(index);
    }

    protected override void SetItem(int index, T item)
    {
        Guard("written at an index on");
        base.SetItem(index, item);
    }

    protected override void ClearItems()
    {
        Guard("cleared on");
        base.ClearItems();
    }

    protected override void MoveItem(int oldIndex, int newIndex)
    {
        Guard("reordered on");
        base.MoveItem(oldIndex, newIndex);
    }

    private void Guard(string what)
    {
        if (_frozen)
        {
            throw new InvalidOperationException(
                $"A list of {typeof(T).Name} was {what} a published snapshot. Snapshots are immutable: edit the live show state, never the copy a sink draws.");
        }
    }
}
