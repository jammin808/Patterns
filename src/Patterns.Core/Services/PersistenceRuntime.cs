using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// The show's files, on one lane (round 65.12 — peeled from the desk's services into the core,
/// behaviour as it was): the autosaves, the recovery record's writes and its clears, the final save, in the order
/// they were asked for on a worker, so an older write never lands over a newer one and a clear never
/// races a write. A save a newer save overtakes before it runs is skipped — five edits in a second
/// are one serialisation and one write, of the latest show; the same for the recovery record. The
/// exit waits a bounded time and says whether the show reached the disk. Nothing here runs on the
/// desk's thread but taking the frozen show the caller hands in.
/// </summary>
public sealed class PersistenceRuntime
{
    private readonly SettingsStore _store;
    private readonly RecoveryStore _recovery;
    private readonly object _gate = new();
    private Task _lane = Task.CompletedTask;
    private long _saveGeneration;
    private long _recoveryGeneration;

    public PersistenceRuntime(SettingsStore store, RecoveryStore recovery, FileBudget? files = null)
    {
        _store = store;
        _recovery = recovery;
        Files = files ?? new FileBudget();
    }

    /// <summary>Off (a safe run, a test) means no file of the show is written by this runtime.</summary>
    public bool Autosave { get; set; } = true;

    /// <summary>What the show's files cost, phase by phase, and the saves a newer one made unnecessary. The Machine page's line and the assistant's brief read it.</summary>
    public FileBudget Files { get; }

    /// <summary>The queued work still on its way to the disk — complete when the file holds the last of it.</summary>
    public Task Pending
    {
        get
        {
            lock (_gate) return _lane;
        }
    }

    /// <summary>One step on the lane, behind everything queued before it; a step that throws is logged and the lane carries on.</summary>
    public void Queue(string what, Action work)
    {
        lock (_gate)
        {
            _lane = _lane.ContinueWith(_ =>
            {
                try
                {
                    work();
                }
                catch (Exception ex)
                {
                    Log.Error($"{what} failed.", ex);
                }
            }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        }
    }

    /// <summary>Waits for the queued work, briefly: a write that is stuck on a dead share must not stop an exit.</summary>
    public void AwaitPending(TimeSpan? wait = null)
    {
        Task pending;
        lock (_gate) pending = _lane;
        if (pending.IsCompleted) return;
        try
        {
            #pragma warning disable VSTHRD002 // the exit's bounded wait for the persistence lane (ADR-008)
            if (!pending.Wait(wait ?? TimeSpan.FromSeconds(10))) Log.Warn("An autosave is still writing after ten seconds — saving over it.");
            #pragma warning restore VSTHRD002
        }
        catch
        {
            // A queued write logs its own failure; the save that follows writes the newest show regardless.
        }
    }

    /// <summary>
    /// The frozen show saved on the lane: serialised and written on the worker, skipped when a newer
    /// save is already behind it. <paramref name="snapshotMs"/> is what taking the frozen show cost on
    /// the desk's thread, recorded with the rest.
    /// </summary>
    public void SaveInBackground(ShowState frozen, double snapshotMs)
    {
        if (!Autosave) return;
        var generation = Interlocked.Increment(ref _saveGeneration);
        Files.Record(FileBudget.SaveSnapshot, snapshotMs, onDeskThread: true);
        Queue("Settings save", () =>
        {
            if (Volatile.Read(ref _saveGeneration) != generation)
            {
                Files.CoalescedOne();                                 // a newer save is behind this one: it writes the latest show, and this one need not
                return;
            }
            var t = System.Diagnostics.Stopwatch.GetTimestamp();
            var json = JsonUtil.Serialize(frozen);
            Files.Record(FileBudget.SaveSerialise, MsSince(t));
            t = System.Diagnostics.Stopwatch.GetTimestamp();
            _store.SaveJsonTo(_store.SettingsPath, json);
            Files.Record(FileBudget.SaveWrite, MsSince(t));
        });
    }

    /// <summary>The show written here and now, after whatever is queued: the desk's SAVE, the start-up's first write.</summary>
    public void SaveNow(ShowState state)
    {
        if (!Autosave) return;
        AwaitPending();
        try
        {
            _store.Save(state);
        }
        catch (Exception ex)
        {
            Log.Error("Settings save failed.", ex);
        }
    }

    /// <summary>
    /// The final save, on the lane behind the autosaves, waited for a bounded time: true when the
    /// show reached the disk, false when it did not — then the recovery record stays for the next start.
    /// </summary>
    public bool SaveAtExit(ShowState state, TimeSpan wait)
    {
        if (!Autosave) return true;
        string json;
        try
        {
            json = JsonUtil.Serialize(state);
        }
        catch (Exception ex)
        {
            Log.Error("The show could not be serialised at exit.", ex);
            return false;
        }
        var done = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Queue("Final save", () =>
        {
            try
            {
                _store.SaveJsonTo(_store.SettingsPath, json);
                done.TrySetResult(true);
            }
            catch (Exception ex)
            {
                Log.Error("The final save failed.", ex);
                done.TrySetResult(false);
            }
        });
        #pragma warning disable VSTHRD002 // the exit's bounded wait for the final save (ADR-008)
        if (done.Task.Wait(wait)) return done.Task.Result;
        #pragma warning restore VSTHRD002
        Log.Warn($"The final save did not reach the disk in {wait.TotalSeconds:0} s — an autosave ahead of it is still writing; the exit goes on and the recovery record is kept.");
        return false;
    }

    /// <summary>
    /// The recovery record made and written on the lane: <paramref name="make"/> runs on the worker
    /// (the clone, the pin, the serialisation), skipped when a newer record is already behind; the
    /// written record is handed to <paramref name="written"/> on the worker, whole and on the disk.
    /// </summary>
    public void WriteRecovery(Func<(RecoverySnapshot Record, string Json)?> make, Action<RecoverySnapshot> written)
    {
        var generation = Interlocked.Increment(ref _recoveryGeneration);
        Queue("Recovery write", () =>
        {
            if (Volatile.Read(ref _recoveryGeneration) != generation)
            {
                Files.CoalescedOne();
                return;
            }
            var made = make();
            if (made is not { } m) return;
            var t = System.Diagnostics.Stopwatch.GetTimestamp();
            _recovery.WriteJson(m.Json);
            Files.Record(FileBudget.RecoveryWrite, MsSince(t));
            written(m.Record);
        });
    }

    /// <summary>The recovery record cleared, behind the writes: a clear never races a write.</summary>
    public void ClearRecovery() => Queue("Recovery clear", _recovery.Clear);

    public static double MsSince(long timestamp) => System.Diagnostics.Stopwatch.GetElapsedTime(timestamp).TotalMilliseconds;
}
