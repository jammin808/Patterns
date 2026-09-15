using Patterns.Core.Services;
using Avalonia.Threading;

namespace Patterns.App.Services;

/// <summary>
/// The desk's UI thread, taken once where it is known to be that thread (the kernel's build) and
/// used by every worker that has a line for it. A worker that asked for <c>Dispatcher.UIThread</c>
/// itself, between two headless tests — after the session had reset the dispatcher and before it
/// had built the platform again — minted a dispatcher with no run loop for the next test to trip
/// over (<c>PlatformNotSupportedException</c> from <c>PushFrame</c>, in a test that had nothing to
/// do with the worker: once in a suite, then once on the build machine, from the audience port's
/// long-polls waking on shutdown). A captured dispatcher that has been reset takes the post and
/// runs nothing, which is what a worker of a desk that is gone deserves; in the running app the
/// captured dispatcher is the one dispatcher there is.
/// </summary>
public static class UiThread
{
    private static Dispatcher? _captured;

    /// <summary>Called on the UI thread, once per desk: the kernel's build is the first thing every role does there.</summary>
    public static void Capture()
    {
        _captured = Dispatcher.UIThread;
        Dispatch.Provider = AvaloniaDispatch.Instance;                                        // the edges reach this thread through the core's seam
    }

    /// <summary>The captured dispatcher; the static one only until something captured it.</summary>
    public static Dispatcher Current => _captured ?? Dispatcher.UIThread;

    public static bool CheckAccess() => Current.CheckAccess();

    public static void Post(Action action, DispatcherPriority priority = default) => Current.Post(action, priority);

    public static DispatcherOperation InvokeAsync(Action action, DispatcherPriority priority = default) => Current.InvokeAsync(action, priority);

    public static DispatcherOperation<TResult> InvokeAsync<TResult>(Func<TResult> function, DispatcherPriority priority = default) => Current.InvokeAsync(function, priority);

    public static Task InvokeAsync(Func<Task> function, DispatcherPriority priority = default) => Current.InvokeAsync(function, priority);

    public static Task<TResult> InvokeAsync<TResult>(Func<Task<TResult>> function, DispatcherPriority priority = default) => Current.InvokeAsync(function, priority);
}

/// <summary>The core's dispatch seam on Avalonia's dispatcher: the one the desk captured.</summary>
public sealed class AvaloniaDispatch : IDispatchProvider
{
    public static readonly AvaloniaDispatch Instance = new();

    public bool CheckAccess() => UiThread.CheckAccess();

    public void Post(Action action) => UiThread.Post(action);

    public IDispatchTimer Timer(TimeSpan interval) => new AvaloniaTimer(interval);

    private sealed class AvaloniaTimer : IDispatchTimer
    {
        private readonly DispatcherTimer _timer;

        public AvaloniaTimer(TimeSpan interval)
        {
            _timer = new DispatcherTimer { Interval = interval };
            _timer.Tick += (_, _) => Tick?.Invoke();
        }

        public TimeSpan Interval { get => _timer.Interval; set => _timer.Interval = value; }

        public bool IsEnabled => _timer.IsEnabled;

        public event Action? Tick;

        public void Start() => _timer.Start();

        public void Stop() => _timer.Stop();

        public void Dispose() => _timer.Stop();
    }
}
