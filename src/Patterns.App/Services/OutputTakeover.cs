using System.Diagnostics;
using Avalonia.Threading;
using Patterns.Core.Model;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>What a process looks like from outside it. A seam: the tests answer without a process tree.</summary>
public interface IProcessProbe
{
    /// <summary>The process's start time as UTC ticks, or null when there is no process with that id.</summary>
    long? StartTicks(int pid);

    /// <summary>The process's executable, or "" when it cannot be read (a process this one may not open).</summary>
    string ExePath(int pid);

    /// <summary>Ends the process and everything it started. False when it could not be ended.</summary>
    bool Kill(int pid);
}

/// <summary>The real thing: <see cref="Process"/>, with every failure read as "no such process".</summary>
public sealed class SystemProcessProbe : IProcessProbe
{
    public long? StartTicks(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.HasExited ? null : p.StartTime.ToUniversalTime().Ticks;
        }
        catch
        {
            return null;   // gone, or not ours to look at — either way it does not hold our screens
        }
    }

    public string ExePath(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            return p.MainModule?.FileName ?? "";
        }
        catch
        {
            return "";
        }
    }

    public bool Kill(int pid)
    {
        try
        {
            using var p = Process.GetProcessById(pid);
            p.Kill(entireProcessTree: true);
            p.WaitForExit(5000);
            return true;
        }
        catch (Exception ex)
        {
            Log.Warn($"Could not end the process holding the screens (pid {pid}).", ex);
            return false;
        }
    }
}

/// <summary>How a start found the screens, and what it did about it.</summary>
/// <param name="Claim">What the record beside the settings said.</param>
/// <param name="Owner">The record, when there was one.</param>
/// <param name="TookOver">This desk now has the screens the previous run was playing on.</param>
/// <param name="EndedOwner">The previous process had to be ended — it never answered the ask.</param>
/// <param name="Words">One sentence for the status line, the health line and the log.</param>
public sealed record TakeoverResult(
    OutputClaim Claim,
    OutputOwner? Owner,
    bool TookOver,
    bool EndedOwner,
    string Words)
{
    public static readonly TakeoverResult None = new(OutputClaim.Free, null, false, false, "");

    /// <summary>The previous run was playing on real screens when this one started.</summary>
    public bool ScreensWereLive => TookOver;
}

/// <summary>
/// Taking the screens back. A crash or a hang leaves the render windows up — deliberately, so the
/// room keeps its picture — but they then belong to nobody: nothing on the new desk can stop them,
/// retarget them or put the next look on them, and OUTPUTS ON would open a second set behind the
/// first. So every start reads the ownership record beside the settings, and when it names a
/// process that is still up it asks that process for the screens (a request file its own poll
/// answers within a second), waits <see cref="OutputOwnership.HandoverGrace"/>, and ends it if it
/// never answers — which is the hung case, and the whole reason the windows were still playing.
/// The show then goes back on under this desk, from the same recovery sidecar a watchdog restart
/// reads, so the picture returns to what the audience was seeing.
/// </summary>
public static class OutputTakeover
{
    /// <summary>An ask nobody cleared is not acted on past this — a claimant that died must never ambush a later desk.</summary>
    public static readonly TimeSpan RequestFresh = TimeSpan.FromSeconds(30);

    /// <summary>How often the claimant looks to see whether the owner has let go.</summary>
    public static readonly TimeSpan PollEvery = TimeSpan.FromMilliseconds(100);

    /// <summary>What this start found; read by the desk for its status and health lines.</summary>
    public static TakeoverResult Last { get; private set; } = TakeoverResult.None;

    /// <summary>Reset the result — the desk consumes it at construction, and the tests between desks.</summary>
    public static void Reset() => Last = TakeoverResult.None;

    /// <summary>
    /// What this start found, taken once: the desk keeps it for its own lines, and a second desk
    /// built in the same process (the tests) never inherits the first one's story.
    /// </summary>
    public static TakeoverResult Consume()
    {
        var last = Last;
        Last = TakeoverResult.None;
        return last;
    }

    /// <summary>
    /// Run from <c>Program.Main</c> before Avalonia starts, so the screens are this desk's before
    /// a single window opens. Bounded: nothing here waits longer than the grace period, and it
    /// does nothing at all in the ordinary case (no record, or a record from a process that died
    /// with its windows).
    /// </summary>
    public static TakeoverResult ClaimAtStart(
        string baseDirectory,
        bool enabled,
        IProcessProbe? probe = null,
        Func<DateTime>? clock = null,
        Action<TimeSpan>? wait = null)
    {
        var result = Claim(baseDirectory, enabled, probe, clock, wait);
        Last = result;
        if (result.Words.Length > 0) Log.Info(result.Words);
        return result;
    }

    private static TakeoverResult Claim(
        string baseDirectory,
        bool enabled,
        IProcessProbe? probe,
        Func<DateTime>? clock,
        Action<TimeSpan>? wait)
    {
        try
        {
            probe ??= new SystemProcessProbe();
            clock ??= () => DateTime.UtcNow;
            wait ??= Thread.Sleep;
            var store = new OutputOwnerStore(baseDirectory);
            var owner = store.Read();
            var claim = OutputOwnership.Read(owner, Environment.ProcessId, Environment.MachineName, clock(), probe.StartTicks);

            switch (claim)
            {
                case OutputClaim.Free:
                case OutputClaim.Ours:
                    return TakeoverResult.None;

                case OutputClaim.Stale:
                    // The owner died and took its windows with it: the record is litter, not a rival.
                    store.Clear();
                    store.ClearRequest();
                    return new TakeoverResult(claim, owner, false, false, "");

                case OutputClaim.AnotherMachine:
                    return new TakeoverResult(claim, owner, false, false, OutputOwnership.Words(claim, owner));
            }

            if (owner is null) return TakeoverResult.None;
            if (!enabled)
            {
                return new TakeoverResult(claim, owner, false, false,
                    $"The last run is still playing on {OutputOwnership.TargetWords(owner.Targets)} (pid {owner.Pid}) — " +
                    "taking the screens back is off in Machine → Watchdog, so this desk leaves them alone.");
            }

            // Ask first: a desk whose UI still answers closes its own windows within a second, which
            // is the tidy way — nothing is ended, and its operator is told what happened.
            store.Ask(new HandoverRequest(Environment.ProcessId, clock()));
            var askedAt = clock();
            var handed = false;
            while (!OutputOwnership.GraceExpired(askedAt, clock()))
            {
                wait(PollEvery);
                var now = store.Read();
                if (now is null || now.Pid != owner.Pid)
                {
                    handed = true;
                    break;
                }
            }

            var ended = false;
            if (!handed)
            {
                // It never answered — the hung case. End it, but only once it is provably the same
                // process and provably Patterns: a pid is never enough to end something by.
                if (probe.StartTicks(owner.Pid) == owner.StartedAtUtcTicks && IsPatterns(probe.ExePath(owner.Pid), owner.ExePath))
                {
                    ended = probe.Kill(owner.Pid);
                }
                else
                {
                    // It let go between the last look and this one.
                    handed = true;
                }
                store.Clear();
            }

            store.ClearRequest();
            var took = handed || ended;
            return new TakeoverResult(claim, owner, took, ended,
                took
                    ? OutputOwnership.TakenWords(owner, ended)
                    : $"The last run still has {OutputOwnership.TargetWords(owner.Targets)} (pid {owner.Pid}) and would not let go — " +
                      "close it by hand, then OUTPUTS ON here.");
        }
        catch (Exception ex)
        {
            Log.Warn("Reading who has the screens failed — this start carries on as if they were free.", ex);
            return TakeoverResult.None;
        }
    }

    /// <summary>
    /// The record's exe against this one: the same file, or failing that the same file name (a
    /// process whose module cannot be read still answers with its own recorded path). Never ends
    /// anything that is not Patterns.
    /// </summary>
    private static bool IsPatterns(string livePath, string recordedPath)
    {
        var mine = Environment.ProcessPath ?? "";
        var candidate = livePath.Length > 0 ? livePath : recordedPath;
        if (candidate.Length == 0) return false;
        if (mine.Length > 0 && string.Equals(candidate, mine, StringComparison.OrdinalIgnoreCase)) return true;
        var name = Path.GetFileNameWithoutExtension(candidate);
        return name.StartsWith("Patterns", StringComparison.OrdinalIgnoreCase)
            || (mine.Length > 0 && string.Equals(name, Path.GetFileNameWithoutExtension(mine), StringComparison.OrdinalIgnoreCase));
    }
}

/// <summary>
/// The living desk's half of the same story: while the outputs are live this writes the ownership
/// record and beats it once a second from the desk's own poll — so a desk whose UI thread has
/// stopped answering stops beating while its windows play on, which is exactly what the next start
/// reads. It also watches for another desk asking for the screens and stands down when one does.
/// </summary>
public sealed class OutputOwnershipService
{
    private readonly AppServices _services;
    private readonly OutputOwnerStore _store;
    private readonly Func<DateTime> _clock;
    private readonly long _startedTicks;
    private DateTime _lastWrite = DateTime.MinValue;
    private volatile bool _held;
    private volatile bool _closed;

    public OutputOwnershipService(AppServices services, Func<DateTime>? clock = null)
    {
        _services = services;
        _store = new OutputOwnerStore(services.Store.BaseDirectory);
        _clock = clock ?? (() => DateTime.UtcNow);
        _startedTicks = OwnStartTicks();
    }

    /// <summary>Raised when another desk asked for the screens and this one let them go.</summary>
    public event Action<string>? StoodDown;

    /// <summary>This desk holds the ownership record.</summary>
    public bool Held => _held;

    /// <summary>
    /// One tick from the desk's poll: claim or release the record as the outputs come and go, beat
    /// it while they are live, and answer an ask from another desk. What the UI thread does here is
    /// read the outputs and their names; the two sidecars are touched on a worker, because a show
    /// running off a USB stick must never pay for a slow write in the desk's own second. One at a
    /// time — a tick that finds the last one still going simply skips.
    /// </summary>
    public void Tick() => Run(force: false);

    /// <summary>Outputs went live or dark: write or clear the record now rather than at the next tick.</summary>
    public void OnLiveChanged() => Run(force: true);

    private void Run(bool force)
    {
        if (_closed || Interlocked.CompareExchange(ref _working, 1, 0) != 0) return;
        var live = _services.Outputs.IsLive;
        // The names come off the UI thread with the windows they describe; everything after this
        // is file work.
        var targets = live ? _services.Outputs.LiveTargetNames() : Array.Empty<string>();
        _ = Task.Run(() =>
        {
            try
            {
                if (AnswerAsk(live)) return;
                if (live) Beat(targets, force);
                else Release();
            }
            catch (Exception ex)
            {
                Log.Warn("The screens' ownership record could not be kept.", ex);
            }
            finally
            {
                Interlocked.Exchange(ref _working, 0);
            }
        });
    }

    private int _working;

    /// <summary>
    /// A clean exit: the screens are nobody's, and the next start must not go hunting for them.
    /// Straight through rather than through <c>Release</c> — a worker may have written the record a
    /// moment ago, and a record left behind for a process that is going away sends the next start
    /// hunting. The flag shuts the worker up so a beat in flight cannot write it back.
    /// </summary>
    public void Shutdown()
    {
        _closed = true;
        _held = false;
        _claimedUtc = DateTime.MinValue;
        _store.Clear();
        _store.ClearRequest();
    }

    private void Beat(IReadOnlyList<string> targets, bool force)
    {
        if (_closed) return;
        var now = _clock();
        if (!force && _held && now - _lastWrite < OutputOwnership.HeartbeatEvery) return;
        if (!_held) _claimedUtc = now;
        _lastWrite = now;
        _store.Write(new OutputOwner(
            Environment.ProcessId,
            _startedTicks,
            now,
            _claimedUtc,
            targets,
            Environment.MachineName,
            Environment.ProcessPath ?? ""));
        _held = true;
    }

    private DateTime _claimedUtc = DateTime.MinValue;

    private void Release()
    {
        if (!_held) return;
        _held = false;
        _claimedUtc = DateTime.MinValue;
        _store.Clear();
    }

    /// <summary>
    /// Another desk is starting and wants these screens. Close the outputs and let the record go —
    /// its start then opens them itself with the show put back, so the room sees the picture change
    /// hands rather than go dark. True when this tick was spent standing down.
    /// </summary>
    private bool AnswerAsk(bool live)
    {
        var request = _store.ReadRequest();
        if (request is null) return false;
        var now = _clock();
        if (!OutputOwnership.ShouldStandDown(request, Environment.ProcessId, now)
            || now - request.AskedUtc > OutputTakeover.RequestFresh)
        {
            return false;
        }

        // Before we let go of anything: the recovery record on disk is the incoming desk's way
        // back to the room's picture, and closing our outputs below would clear it. Freeze it
        // here, on this thread, because the incoming desk starts reading the moment the
        // ownership record goes.
        if (live) _services.HandOverRecovery();
        _store.ClearRequest();
        Release();
        var words = live
            ? $"Another Patterns (pid {request.Pid}) started and took the screens over — this desk's outputs are closed."
            : $"Another Patterns (pid {request.Pid}) started and has the screens.";
        Log.Info(words);
        // The windows are the UI thread's to close, and so is the line that says so.
        Dispatcher.UIThread.Post(() =>
        {
            if (live) _services.Outputs.CloseAll();
            StoodDown?.Invoke(words);
        });
        return true;
    }

    private static long OwnStartTicks()
    {
        try
        {
            using var me = Process.GetCurrentProcess();
            return me.StartTime.ToUniversalTime().Ticks;
        }
        catch
        {
            return 0;
        }
    }
}
