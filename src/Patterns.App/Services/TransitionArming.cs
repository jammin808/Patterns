namespace Patterns.App.Services;

/// <summary>
/// Which content targets the next CUT / TAKE touches. Everything is armed unless the operator
/// un-arms it; monitoring (what the wall shows) never changes this. Runtime only: a show
/// always opens with every target armed, so nothing is silently left out at the venue.
/// </summary>
public sealed class TransitionArming
{
    private readonly HashSet<string> _unarmed = new(StringComparer.Ordinal);

    /// <summary>Raised on the UI thread when the armed set changes.</summary>
    public event Action? Changed;

    public IReadOnlyCollection<string> Unarmed => _unarmed;

    public bool IsArmed(string targetId) => !_unarmed.Contains(targetId);

    public void Set(string targetId, bool armed)
    {
        var changed = armed ? _unarmed.Remove(targetId) : _unarmed.Add(targetId);
        if (changed) Changed?.Invoke();
    }

    /// <summary>Back to "everything": after a rig change, a show load, or on demand.</summary>
    public void ArmAll()
    {
        if (_unarmed.Count == 0) return;
        _unarmed.Clear();
        Changed?.Invoke();
    }

    /// <summary>Drops targets that no longer exist so a stale id cannot hold a tile un-armed.</summary>
    public void Prune(IEnumerable<string> existingTargets)
    {
        var keep = new HashSet<string>(existingTargets, StringComparer.Ordinal);
        if (_unarmed.RemoveWhere(id => !keep.Contains(id)) > 0) Changed?.Invoke();
    }
}
