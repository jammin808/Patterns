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

    /// <summary>
    /// The whole look at once, with the answer the two reads above cannot give: a process that is
    /// up but that this one may not read, which every fence treats as a fence and never as an
    /// absence. The default builds it from the two reads — a world where every process that can
    /// be seen can be read, which is the tests' — so a probe that knows better overrides it.
    /// </summary>
    ProcessSight Look(int pid) => StartTicks(pid) is { } started ? ProcessSight.Alive(started, ExePath(pid)) : ProcessSight.Gone;
}

/// <summary>
/// The real thing: <see cref="Process"/>. "No such process" and "a process this one may not read"
/// are told apart, because they mean opposite things to a fence: the first has let go of the
/// screens, the second may well still have them.
/// </summary>
public sealed class SystemProcessProbe : IProcessProbe
{
    public long? StartTicks(int pid) => Look(pid).StartTicks;

    public string ExePath(int pid) => Look(pid).ExePath;

    public ProcessSight Look(int pid)
    {
        if (pid <= 0) return ProcessSight.Gone;
        Process p;
        try
        {
            p = Process.GetProcessById(pid);
        }
        catch (ArgumentException)
        {
            return ProcessSight.Gone;                       // no process with that id
        }
        catch (Exception)
        {
            return ProcessSight.Unreadable();               // there is one, and it is not this process's to open
        }
        using (p)
        {
            long started;
            try
            {
                if (p.HasExited) return ProcessSight.Gone;
                started = p.StartTime.ToUniversalTime().Ticks;
            }
            catch (InvalidOperationException)
            {
                return ProcessSight.Gone;                   // it ended between the two calls
            }
            catch (Exception)
            {
                return ProcessSight.Unreadable(Module(p));  // up, and its times refused: another user's, elevated, protected
            }
            return ProcessSight.Alive(started, Module(p));
        }
    }

    /// <summary>The main module's file, "" when it cannot be read — a process that can be timed can still refuse its modules.</summary>
    private static string Module(Process p)
    {
        try
        {
            return p.MainModule?.FileName ?? "";
        }
        catch (Exception)
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
            // "Ended" means gone: a process still on its way out after five seconds is not ended,
            // and whoever asked must not open the screens on the strength of it.
            return p.WaitForExit(5000) || p.HasExited;
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

    /// <summary>The record could not be read: a fence. Nothing opens the screens by itself this run; the operator can.</summary>
    public bool Uncertain => Claim == OutputClaim.Unknown;

    /// <summary>What a start that could not read the record comes to — said, and never acted on as if the screens were free.</summary>
    public static TakeoverResult Unknown(string problem) => new(OutputClaim.Unknown, null, false, false, OutputOwnership.UnknownWords(problem));
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
        Action<TimeSpan>? wait = null,
        ISidecarFiles? files = null)
    {
        var result = Claim(baseDirectory, enabled, probe, clock, wait, files);
        Last = result;
        if (result.Words.Length > 0) Log.Info(result.Words);
        return result;
    }

    private static TakeoverResult Claim(
        string baseDirectory,
        bool enabled,
        IProcessProbe? probe,
        Func<DateTime>? clock,
        Action<TimeSpan>? wait,
        ISidecarFiles? files)
    {
        try
        {
            probe ??= new SystemProcessProbe();
            clock ??= () => DateTime.UtcNow;
#pragma warning disable RS0030 // the owner store is another process's file: the poll between reads is the wait, on the boot thread before the desk exists
            wait ??= Thread.Sleep;
#pragma warning restore RS0030
            var store = new OutputOwnerStore(baseDirectory, files);
            var read = store.Read();
            // A record that cannot be read is a fence, not an absence: it may name a desk that is
            // playing to the room right now. Nothing is asked, ended or opened; the words say how
            // the operator opens the screens once sure.
            if (read.IsUnreadable) return TakeoverResult.Unknown(read.Problem);
            var owner = read.Value;
            var claim = OutputOwnership.Read(owner, Environment.ProcessId, Environment.MachineName, clock(), probe.Look);

            switch (claim)
            {
                case OutputClaim.Free:
                case OutputClaim.Ours:
                    return TakeoverResult.None;

                case OutputClaim.Stale:
                    // The owner died and took its windows with it: the record is litter, not a rival.
                    // A clear that fails leaves litter the next start reads as stale again — said, harmless.
                    if (!store.Clear().Committed) Log.Warn("The last run's ownership record could not be cleared; the next start will read it as stale again.");
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
            // is the tidy way — nothing is ended, and its operator is told what happened. An ask
            // that never reached the disk was never asked: a silence after it says nothing about
            // the owner, so nothing is ended on the strength of it.
            var asked = store.Ask(new HandoverRequest(Environment.ProcessId, clock()));
            if (!asked.Committed)
            {
                return new TakeoverResult(claim, owner, false, false,
                    $"The last run still has {OutputOwnership.TargetWords(owner.Targets)} (pid {owner.Pid}) and could not be asked for them ({asked.Problem}) — " +
                    "close it by hand, then OUTPUTS ON here.");
            }
            var askedAt = clock();
            var handed = false;
            var unsure = "";
            while (!OutputOwnership.GraceExpired(askedAt, clock()))
            {
                wait(PollEvery);
                var now = store.Read();
                if (now.IsUnreadable)
                {
                    unsure = now.Problem;                   // a look that could not read is not a look that saw it let go
                    continue;
                }
                unsure = "";
                if (now.IsMissing || now.Value!.Pid != owner.Pid)
                {
                    handed = true;
                    break;
                }
            }

            var ended = false;
            var why = "would not let go";
            if (!handed && unsure.Length > 0)
            {
                // The last looks could not read the record: whether it let go is not known, and a
                // desk that may have let go is not ended.
                why = $"its record could not be read while waiting ({unsure}), so it is not ended";
            }
            else if (!handed)
            {
                // It never answered — the hung case. End it, but only once it is provably the same
                // process and provably Patterns: a pid is never enough to end something by. A
                // process this desk cannot read is a fence, not an absence: it may well still have
                // the screens, so nothing is taken and the record stays for the next start to read.
                var sight = probe.Look(owner.Pid);
                if (sight.IsGoneOrReused(owner.StartedAtUtcTicks))
                {
                    handed = true;                          // it let go between the last look and this one, or its id was handed out again
                }
                else if (sight.IsUnreadable)
                {
                    why = "cannot be read from here (another user's, or elevated?), so it is not ended";
                }
                else if (!IsPatterns(sight.ExePath, owner.ExePath))
                {
                    why = $"is not Patterns ({sight.ExePath}), so it is not ended";
                }
                else
                {
                    ended = probe.Kill(owner.Pid);
                }
            }
            if (handed || ended)
            {
                if (!store.Clear().Committed) Log.Warn("The last run's ownership record could not be cleared after the screens were taken; the next start will read it as stale.");
            }

            if (!store.ClearRequest().Committed) Log.Warn("This desk's ask for the screens could not be cleared; it goes stale by itself.");
            var took = handed || ended;
            return new TakeoverResult(claim, owner, took, ended,
                took
                    ? OutputOwnership.TakenWords(owner, ended)
                    : $"The last run still has {OutputOwnership.TargetWords(owner.Targets)} (pid {owner.Pid}) and {why} — " +
                      "close it by hand, then OUTPUTS ON here.");
        }
        catch (Exception ex)
        {
            // Not "free": a start that could not find out who has the screens does not open them by itself.
            Log.Warn("Reading who has the screens failed — nothing is opened by itself this run.", ex);
            return TakeoverResult.Unknown(ex.Message);
        }
    }

    /// <summary>
    /// The record's exe against this one: the same file, or failing that the same file name (a
    /// process whose module cannot be read still answers with its own recorded path). Never ends
    /// anything that is not Patterns.
    /// </summary>
    internal static bool IsPatterns(string livePath, string recordedPath)
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
    /// <summary>The files under the store — the tests' seam for a folder that will not take a write.</summary>
    public static Func<ISidecarFiles>? SidecarFiles { get; set; }

    private readonly AppServices _services;
    private readonly OutputOwnerStore _store;
    private readonly Func<DateTime> _clock;
    private readonly long _startedTicks;
    private DateTime _lastWrite = DateTime.MinValue;
    private DateTime? _troubleSinceUtc;
    private string _troubleProblem = "";
    private HandoverRequest? _answered;
    private string _unreadableAsk = "";
    private volatile string _trouble = "";
    private volatile bool _held;
    private volatile bool _closed;

    public OutputOwnershipService(AppServices services, Func<DateTime>? clock = null, ISidecarFiles? files = null)
    {
        _services = services;
        _store = new OutputOwnerStore(services.Store.BaseDirectory, files ?? SidecarFiles?.Invoke());
        _clock = clock ?? (() => DateTime.UtcNow);
        _startedTicks = OwnStartTicks();
    }

    /// <summary>Raised when another desk asked for the screens and this one let them go.</summary>
    public event Action<string>? StoodDown;

    /// <summary>Raised when the record could not be kept, and again with "" once it could.</summary>
    public event Action<string>? TroubleChanged;

    /// <summary>
    /// This desk holds the ownership record — on disk, committed. False while the outputs are
    /// live but the record could not be written: the windows are this desk's, the folder does not
    /// say so, and <see cref="Trouble"/> says why.
    /// </summary>
    public bool Held => _held;

    /// <summary>What is wrong with the record on disk, or "" — the Machine page's screens line and the status line carry it.</summary>
    public string Trouble => _trouble;

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
        if (!_store.Clear().Committed) Log.Warn("The screens' ownership record could not be cleared at exit; the next start reads it as stale.");
        _store.ClearRequest();
    }

    private void Beat(IReadOnlyList<string> targets, bool force)
    {
        if (_closed) return;
        var now = _clock();
        if (!force && _held && now - _lastWrite < OutputOwnership.HeartbeatEvery) return;
        if (!_held) _claimedUtc = now;
        _lastWrite = now;
        var wrote = _store.Write(new OutputOwner(
            Environment.ProcessId,
            _startedTicks,
            now,
            _claimedUtc,
            targets,
            Environment.MachineName,
            Environment.ProcessPath ?? ""));
        // Held means committed: a record the disk did not take is a record another start will not
        // read, so this desk does not believe it holds what the folder does not say it holds.
        if (wrote.Committed)
        {
            _held = true;
            SetTrouble("", now);
        }
        else
        {
            SetTrouble(wrote.Problem, now);
        }
    }

    /// <summary>The words for a record that cannot be kept, said once when it starts and once more when it clears; the seconds count up meanwhile.</summary>
    private void SetTrouble(string problem, DateTime now)
    {
        if (problem.Length == 0)
        {
            if (_troubleSinceUtc is null) return;
            _troubleSinceUtc = null;
            _troubleProblem = "";
            _trouble = "";
            Log.Info("The screens' ownership record is being kept again.");
            var handler = TroubleChanged;
            if (handler is not null) UiThread.Post(() => handler(""));
            return;
        }
        var first = _troubleSinceUtc is null;
        _troubleSinceUtc ??= now;
        var seconds = (int)(now - _troubleSinceUtc.Value).TotalSeconds;
        _trouble = $"The screens' ownership record could not be written for {seconds} s ({problem}) — another Patterns starting on this folder would not see this desk playing, and the watchdog's restart would not know these screens are taken.";
        if (first || problem != _troubleProblem)
        {
            _troubleProblem = problem;
            Log.Warn(_trouble);
            var handler = TroubleChanged;
            var words = _trouble;
            if (handler is not null) UiThread.Post(() => handler(words));
        }
    }

    private DateTime _claimedUtc = DateTime.MinValue;

    private void Release()
    {
        if (!_held) return;
        _held = false;
        _claimedUtc = DateTime.MinValue;
        var cleared = _store.Clear();
        // The windows are gone whatever the disk says; a record that would not clear is said, because
        // the next start reads it — as stale once this process is gone, as this desk's while it lives.
        if (!cleared.Committed) Log.Warn($"The screens' ownership record could not be cleared ({cleared.Problem}); it goes stale with this process.");
    }

    /// <summary>
    /// Another desk is starting and wants these screens. Close the outputs and let the record go —
    /// its start then opens them itself with the show put back, so the room sees the picture change
    /// hands rather than go dark. True when this tick was spent standing down.
    /// </summary>
    private bool AnswerAsk(bool live)
    {
        var read = _store.ReadRequest();
        if (read.IsUnreadable)
        {
            // Junk where an ask would be is not an ask: nobody stands down on a file they cannot
            // read. Said once per problem, because it is read every second.
            if (_unreadableAsk != read.Problem)
            {
                _unreadableAsk = read.Problem;
                Log.Warn($"The handover request beside the settings could not be read ({read.Problem}) — not an ask, so the screens stay this desk's.");
            }
            return false;
        }
        _unreadableAsk = "";
        var request = read.Value;
        if (request is null) return false;
        var now = _clock();
        if (!OutputOwnership.ShouldStandDown(request, Environment.ProcessId, now)
            || now - request.AskedUtc > OutputTakeover.RequestFresh)
        {
            return false;
        }
        // Answered once: an ask whose clear failed is still on disk next second, and standing
        // down twice would tell the operator twice about one desk.
        if (_answered is { } done && done.Pid == request.Pid && done.AskedUtc == request.AskedUtc) return false;
        _answered = request;

        // Before we let go of anything: the recovery record on disk is the incoming desk's way
        // back to the room's picture, and closing our outputs below would clear it. Freeze it
        // here, on this thread, because the incoming desk starts reading the moment the
        // ownership record goes.
        if (live) _services.HandOverRecovery();
        if (!_store.ClearRequest().Committed) Log.Warn("The ask for the screens could not be cleared after it was answered; it goes stale by itself.");
        Release();
        var words = live
            ? $"Another Patterns (pid {request.Pid}) started and took the screens over — this desk's outputs are closed."
            : $"Another Patterns (pid {request.Pid}) started and has the screens.";
        Log.Info(words);
        // The windows are the UI thread's to close, and so is the line that says so.
        UiThread.Post(() =>
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
