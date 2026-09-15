namespace Patterns.Core.Services;

/// <summary>
/// A timer that ticks on the desk's thread — the shape the UI's timer has, so an edge that
/// throttled its pushes on one keeps the same three lines and never names the UI.
/// </summary>
public interface IDispatchTimer : IDisposable
{
    TimeSpan Interval { get; set; }

    bool IsEnabled { get; }

    void Start();

    void Stop();

    event Action? Tick;
}

/// <summary>Who runs work on the desk's thread: the UI's dispatcher in the app, an inline runner in a test.</summary>
public interface IDispatchProvider
{
    /// <summary>Whether the caller is on the desk's thread.</summary>
    bool CheckAccess();

    /// <summary>Runs the action on the desk's thread, later.</summary>
    void Post(Action action);

    IDispatchTimer Timer(TimeSpan interval);
}

/// <summary>
/// The desk's thread as an edge sees it — a post, a check, a timer — with no UI in sight. The
/// app installs its dispatcher once, on the thread it was built on (the kernel's first act);
/// a process with no UI, and every test of an edge, keeps the inline runner: a post runs where
/// it was made and a timer fires when the test says so. An edge assembly (the devices, the
/// audio) reaches the desk's thread only through here, which is what lets it be an assembly.
/// </summary>
public static class Dispatch
{
    private static IDispatchProvider _provider = InlineDispatch.Instance;

    public static IDispatchProvider Provider
    {
        get => _provider;
        set => _provider = value ?? InlineDispatch.Instance;
    }

    public static bool CheckAccess() => _provider.CheckAccess();

    public static void Post(Action action) => _provider.Post(action);

    public static IDispatchTimer Timer(TimeSpan interval) => _provider.Timer(interval);
}

/// <summary>
/// The runner a process without a UI has: a posted action runs at once on the posting thread,
/// and a timer fires only when something fires it. Tests drive edges with it and fire the
/// timers by hand; a headless node that wants real periodic work installs a provider.
/// </summary>
public sealed class InlineDispatch : IDispatchProvider
{
    public static readonly InlineDispatch Instance = new();

    private readonly List<WeakReference<ManualTimer>> _timers = new();

    public bool CheckAccess() => true;

    public void Post(Action action) => action();

    public IDispatchTimer Timer(TimeSpan interval)
    {
        var timer = new ManualTimer(interval);
        lock (_timers) _timers.Add(new WeakReference<ManualTimer>(timer));
        return timer;
    }

    /// <summary>Fires every timer that is running — the test's clock tick.</summary>
    public int FireAll()
    {
        List<ManualTimer> live = new();
        lock (_timers)
        {
            _timers.RemoveAll(w => !w.TryGetTarget(out _));
            foreach (var w in _timers) if (w.TryGetTarget(out var t)) live.Add(t);
        }
        var fired = 0;
        foreach (var t in live) if (t.Fire()) fired++;
        return fired;
    }
}

/// <summary>A timer fired by hand: running after <see cref="Start"/>, quiet after <see cref="Stop"/>, its tick a call away.</summary>
public sealed class ManualTimer : IDispatchTimer
{
    public ManualTimer(TimeSpan interval) => Interval = interval;

    public TimeSpan Interval { get; set; }

    public bool IsEnabled { get; private set; }

    public int Fired { get; private set; }

    public event Action? Tick;

    public void Start() => IsEnabled = true;

    public void Stop() => IsEnabled = false;

    /// <summary>Ticks if running; false when stopped.</summary>
    public bool Fire()
    {
        if (!IsEnabled) return false;
        Fired++;
        Tick?.Invoke();
        return true;
    }

    public void Dispose() => Stop();
}
