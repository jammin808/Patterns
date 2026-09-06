using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>A host the desk started, as its supervision sees it: lines in and out, alive or not, and a way to end it.</summary>
public interface IChildHandle : IDisposable
{
    int Pid { get; }

    bool HasExited { get; }

    int ExitCode { get; }

    /// <summary>A line to the host's stdin, flushed; false when the host is gone.</summary>
    bool WriteLine(string line);

    /// <summary>The next line from the host's stdout, blocking; null at the end — the host closed it or died.</summary>
    string? ReadLine();

    void Kill();
}

/// <summary>Starts a host: the same exe with <c>--host</c> and a role (a scripted fake in the tests).</summary>
public interface IChildLauncher
{
    IChildHandle Launch(string role, Action<string> log);
}

public enum ChildPhase
{
    Idle,
    Starting,    // launched, START sent, no STARTED yet
    Running,     // the host said STARTED and beats
    Restarting,  // it died or went silent: a restart is due after the backoff
    GaveUp,      // a crash loop: not started again until the operator asks
    Stopped,     // the desk ended it
}

/// <summary>
/// The desk's side of a host process (the stream encoder first; the decoders when they follow).
/// Starts it, hands it the START payload, reads its lines on a thread, and — on the owner's
/// once-a-second poll — ends a host whose beat went silent, restarts one that died with the
/// watchdog's own backoff (<see cref="SupervisorPolicy"/>), and stands down with words after a
/// crash loop. What the host says (BEAT, STATUS, ERROR, LOG) is kept for the owner to read; an
/// ERROR is the host's own report, not a crash, and the owner decides what it means for the show.
/// </summary>
public sealed class ChildProcess : IDisposable
{
    private readonly object _gate = new();
    private readonly string _role;
    private readonly IChildLauncher _launcher;
    private readonly Action<string> _log;
    private readonly Func<DateTime> _clock;
    private SupervisorPolicy _policy = new(maxCrashesInWindow: 5, crashWindowMinutes: 10, stableRunMinutes: 2);
    private IChildHandle? _handle;
    private string _payload = "";
    private DateTime _startedUtc;
    private DateTime _lastBeatUtc;
    private DateTime _restartDueUtc;
    private bool _heard;   // the host has said anything at all: from then on the beat's own timeout applies
    private int _releasing;   // hosts ended but not yet out — a moment to leave on QUIT, then killed

    /// <param name="clock">UTC now — the tests turn it by hand so a backoff is a step, not a wait.</param>
    public ChildProcess(string role, IChildLauncher launcher, Action<string> log, Func<DateTime>? clock = null)
    {
        _role = role;
        _launcher = launcher;
        _log = log;
        _clock = clock ?? (() => DateTime.UtcNow);
    }

    public ChildPhase Phase { get; private set; }

    public int Pid { get; private set; }

    /// <summary>How many times the host was started again since <see cref="Start"/>.</summary>
    public int Restarts { get; private set; }

    public bool SaidHello { get; private set; }

    /// <summary>The host's last HELLO: whether it found libVLC.</summary>
    public bool HostHasLibVlc { get; private set; }

    /// <summary>Frames the host has taken from the ring, from its last BEAT.</summary>
    public long Frames { get; private set; }

    /// <summary>The host's own state word from its last BEAT ("Playing", "counting"…).</summary>
    public string HostState { get; private set; } = "";

    /// <summary>The host's last STATUS line.</summary>
    public string StatusText { get; private set; } = "";

    /// <summary>The host's last ERROR (its code word first), or the stand-down words; "" while all is well.</summary>
    public string LastError { get; private set; } = "";

    public bool Failed => LastError.Length > 0;

    /// <summary>The phase in words for a status line: starting, running, the restart due, the stand-down.</summary>
    public string Words { get; private set; } = "";

    /// <summary>Every line the host said, after it was read: the word and the rest. Raised on the reader thread.</summary>
    public event Action<string, string>? Said;

    /// <summary>
    /// No host of this side is alive: none running, and every one it ended has left its process —
    /// the moment whatever the old host held (a stream key takes one publisher) is free for a new one.
    /// </summary>
    public bool HostsGone => Volatile.Read(ref _handle) is null && Volatile.Read(ref _releasing) == 0;

    /// <summary>PING to the host, which answers with a BEAT at once; false when there is no host to ask.</summary>
    public bool Ping()
    {
        lock (_gate)
        {
            return _handle?.WriteLine(HostProtocol.Line(HostProtocol.Ping)) ?? false;
        }
    }

    /// <summary>Starts the host with this START payload; a later restart sends the same payload again. A host already running is ended first — never two on one plan.</summary>
    public void Start(string payload)
    {
        IChildHandle? old;
        lock (_gate)
        {
            old = _handle;
            _handle = null;
            _payload = payload;
            _policy = new SupervisorPolicy(maxCrashesInWindow: 5, crashWindowMinutes: 10, stableRunMinutes: 2);
            Restarts = 0;
            LastError = "";
        }
        if (old is not null) End(old);
        lock (_gate)
        {
            Launch(_clock());
        }
    }

    /// <summary>The owner's tick: a dead or silent host is started again after the backoff; a crash loop is stood down from.</summary>
    public void Poll()
    {
        lock (_gate)
        {
            var utcNow = _clock();
            switch (Phase)
            {
                case ChildPhase.Idle:
                case ChildPhase.Stopped:
                case ChildPhase.GaveUp:
                    return;
                case ChildPhase.Restarting:
                    if (utcNow >= _restartDueUtc) Launch(utcNow);
                    return;
            }
            var handle = _handle;
            if (handle is null) return;
            var exited = handle.HasExited;
            // A host that has spoken gets the beat's timeout; one that has said nothing yet gets the longer
            // patience a cold start of the exe needs — neither is a crash until it is.
            var silent = !exited && (_heard
                ? HostProtocol.IsSilent(_lastBeatUtc, utcNow)
                : utcNow - _startedUtc > HostProtocol.HelloTimeout);
            // A host that beats but never says STARTED is stuck in its bring-up (libVLC, a capture device, a
            // destination that never answers): alive by the beat, hung by any other measure — ended like a silent one.
            var stuck = !exited && !silent && Phase == ChildPhase.Starting && _heard && utcNow - _startedUtc > HostProtocol.StartTimeout;
            if (!exited && !silent && !stuck) return;

            var code = 0;
            if (silent || stuck)
            {
                _log(stuck
                    ? $"The {_role} host did not start within {HostProtocol.StartTimeout.TotalSeconds:0} s — ending it."
                    : _heard
                        ? $"The {_role} host's beat went silent for {HostProtocol.BeatTimeout.TotalSeconds:0} s — ending it."
                        : $"The {_role} host said nothing for {HostProtocol.HelloTimeout.TotalSeconds:0} s — ending it.");
                handle.Kill();
            }
            else
            {
                code = handle.ExitCode;
            }
            var ranFor = utcNow - _startedUtc;
            var why = stuck
                ? $"the {_role} did not start within {HostProtocol.StartTimeout.TotalSeconds:0} s"
                : silent
                    ? (_heard ? $"the {_role}'s beat went silent" : $"the {_role} never came up")
                    : $"the {_role} ended in {ExitCodes.Describe(code)}";
            // An exit nobody asked for is a host that is gone, whatever its code: the show wants it back.
            var hung = silent || stuck;
            var verdict = _policy.OnExit(hung || code == 0 ? 1 : code, hung, ranFor, utcNow);
            Release(handle);
            _handle = null;
            if (verdict.Action == SupervisorAction.GiveUp)
            {
                Phase = ChildPhase.GaveUp;
                Words = $"the {_role} failed {Restarts + 1} times in a short window and was not started again — the last time {why}";
                LastError = Words;
                _log(Words);
                return;
            }
            Restarts++;
            _restartDueUtc = utcNow + verdict.Delay;
            Phase = ChildPhase.Restarting;
            Words = $"{why} after {ranFor.TotalSeconds:0} s — restart #{Restarts} in {verdict.Delay.TotalSeconds:0} s";
            _log(Words);
        }
    }

    /// <summary>Ends the host: STOP and QUIT, a moment to leave on its own, then it is killed. Never blocks the caller.</summary>
    public void Stop()
    {
        IChildHandle? handle;
        lock (_gate)
        {
            handle = _handle;
            _handle = null;
            Phase = ChildPhase.Stopped;
            Words = $"{_role} stopped";
        }
        if (handle is not null) End(handle);
    }

    /// <summary>STOP, QUIT, and the handle let go — off the caller's thread.</summary>
    private void End(IChildHandle handle)
    {
        handle.WriteLine(HostProtocol.Line(HostProtocol.Stop));
        handle.WriteLine(HostProtocol.Line(HostProtocol.Quit));
        Release(handle);
    }

    /// <summary>Under the gate: a fresh host, the payload sent, the reader on its way.</summary>
    private void Launch(DateTime utcNow)
    {
        IChildHandle handle;
        try
        {
            handle = _launcher.Launch(_role, _log);
        }
        catch (Exception ex)
        {
            Phase = ChildPhase.GaveUp;
            Words = $"the {_role} could not be started: {ex.Message}";
            LastError = Words;
            _log(Words);
            return;
        }
        _handle = handle;
        Pid = handle.Pid;
        _startedUtc = utcNow;
        _lastBeatUtc = utcNow;
        _heard = false;
        SaidHello = false;
        HostState = "";
        StatusText = "";
        Frames = 0;
        Phase = ChildPhase.Starting;
        Words = Restarts == 0 ? $"starting the {_role} (pid {Pid})" : $"{_role} started again (#{Restarts}, pid {Pid})";
        handle.WriteLine(HostProtocol.Line(HostProtocol.Start, _payload));
        var reader = new Thread(() => Pump(handle)) { IsBackground = true, Name = $"{_role}-host-reader" };
        reader.Start();
    }

    private void Pump(IChildHandle handle)
    {
        try
        {
            string? line;
            while ((line = handle.ReadLine()) is not null)
            {
                OnLine(handle, line);
            }
        }
        catch (Exception ex)
        {
            _log($"The {_role} host's reader ended: {ex.Message}");
        }
    }

    private void OnLine(IChildHandle handle, string line)
    {
        var (word, rest) = HostProtocol.Parse(line);
        if (word.Length == 0) return;
        lock (_gate)
        {
            if (!ReferenceEquals(handle, _handle)) return;   // an old host's last words
            var now = _clock();
            _heard = true;
            switch (word)
            {
                case HostProtocol.Hello:
                    SaidHello = true;
                    _lastBeatUtc = now;
                    HostHasLibVlc = Read<HostHello>(rest)?.LibVlc ?? false;
                    break;
                case HostProtocol.Beat:
                    _lastBeatUtc = now;
                    if (Read<HostBeat>(rest) is { } beat)
                    {
                        Frames = beat.Frames;
                        HostState = beat.State;
                    }
                    break;
                case HostProtocol.Started:
                    _lastBeatUtc = now;
                    Phase = ChildPhase.Running;
                    Words = $"{_role} running (pid {Pid})";
                    break;
                case HostProtocol.Stopped:
                    break;
                case HostProtocol.Status:
                    StatusText = rest;
                    break;
                case HostProtocol.Error:
                    LastError = rest;
                    _log($"The {_role} host reports: {rest}");
                    break;
                case HostProtocol.Log:
                    _log($"{_role}: {rest}");
                    break;
                default:
                    _log($"The {_role} host said: {line}");
                    break;
            }
        }
        Said?.Invoke(word, rest);
    }

    /// <summary>A line's JSON payload, or null when it is missing or will not read — a host's words never throw here.</summary>
    private static T? Read<T>(string json) where T : class
    {
        try
        {
            return json.Length == 0 ? null : JsonUtil.Deserialize<T>(json);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>Lets a host go: three seconds to leave by itself after QUIT, then it is killed; on a thread of its own, never the desk's. <see cref="HostsGone"/> says when it is out.</summary>
    private void Release(IChildHandle handle)
    {
        Interlocked.Increment(ref _releasing);
        ThreadPool.QueueUserWorkItem(_ =>
        {
            try
            {
                var deadline = Environment.TickCount64 + 3000;
                while (!handle.HasExited && Environment.TickCount64 < deadline) Thread.Sleep(25);
                if (!handle.HasExited) handle.Kill();
            }
            catch
            {
                // Racing a dying process is fine.
            }
            finally
            {
                handle.Dispose();
                Interlocked.Decrement(ref _releasing);
            }
        });
    }

    public void Dispose() => Stop();
}
