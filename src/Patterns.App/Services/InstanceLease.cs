namespace Patterns.App.Services;

/// <summary>
/// Round 78: the one-owner lease on a settings folder. The first desk on a folder owns it — it saves the
/// show, keeps or clears the recovery record and runs the break music; a second desk on the same folder
/// runs with saving left to the first. The lease can be asked for again later: a replacement desk that
/// started beside the desk it replaces (a handover restart) owns the folder the moment the old desk has
/// gone, and a second window becomes the first when the first is closed. The field's replacement ran a
/// whole show as the second desk — autosave off, the break music gated, its exit clearing the record that
/// was not its own — because the lease was asked for once, at the start, and never again.
/// </summary>
public interface IInstanceLease : IDisposable
{
    /// <summary>This process holds the lease.</summary>
    bool Owned { get; }

    /// <summary>Asks for the lease without waiting: true when it is this process's now, or already was.</summary>
    bool TryAcquire();
}

/// <summary>
/// The lease as a named mutex — one per folder, machine-wide. An owner that died holding it leaves it
/// abandoned, and the next ask takes it: the show file is then whatever that owner last wrote.
/// </summary>
public sealed class MutexInstanceLease : IInstanceLease
{
    private readonly Mutex _mutex;

    public MutexInstanceLease(string name)
    {
        _mutex = new Mutex(true, name, out var first);
        Owned = first;
    }

    public bool Owned { get; private set; }

    public bool TryAcquire()
    {
        if (Owned) return true;
        try
        {
            Owned = _mutex.WaitOne(0);
        }
        catch (AbandonedMutexException)
        {
            Owned = true;
        }
        return Owned;
    }

    public void Dispose()
    {
        try
        {
            if (Owned) _mutex.ReleaseMutex();
        }
        catch (ApplicationException)
        {
            // Not this thread's to release: the dispose below lets the next asker find it abandoned, which it takes.
        }
        Owned = false;
        _mutex.Dispose();
    }
}
