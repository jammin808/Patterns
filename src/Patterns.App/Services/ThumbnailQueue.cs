using Avalonia.Media.Imaging;
using Avalonia.Threading;
using Patterns.Core.Services;

namespace Patterns.App.Services;

/// <summary>
/// The one worker that draws the Library's thumbnails. A build of the page hands it every tile;
/// it draws the ones whose picture would differ from the one it drew last (the key names the
/// picture: the pattern the tile would put up, on the show's brand), skips the rest, and posts
/// each finished bitmap to the UI thread. A newer build supersedes the one in hand — the tiles
/// not yet drawn are dropped and the new list is taken up — so a folder of files picked in one go
/// is one pass over the tiles, not one pass per file racing the others. Every build used to start
/// its own pass over every tile, and none of them was ever cancelled.
/// </summary>
public sealed class ThumbnailQueue : IDisposable
{
    /// <summary>What a tile would draw: the key names the picture; the render makes it, off the UI thread.</summary>
    public readonly record struct Work(string Key, Func<Bitmap?> Render);

    /// <summary>One tile: prepare says what to draw (null: nothing), apply takes the bitmap on the UI thread.</summary>
    public sealed record Job(string Id, Func<Work?> Prepare, Action<Bitmap> Apply);

    private readonly object _gate = new();
    // The desk's dispatcher, taken here on the UI thread: a worker that asked for Dispatcher.UIThread
    // itself could be the first to ask after a headless test session reset it, and would mint one
    // with no run loop for the next test to trip over.
    private readonly Dispatcher _dispatcher = Dispatcher.UIThread;
    private readonly ManualResetEventSlim _stopped = new(true);
    private readonly Dictionary<string, string> _drawn = new(StringComparer.Ordinal);
    private List<Job> _queue = new();
    private bool _running;
    private bool _disposed;
    private TaskCompletionSource _idle = Done();
    private int _rendered;
    private int _skipped;
    private int _dropped;

    private static TaskCompletionSource Done()
    {
        var tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        tcs.TrySetResult();
        return tcs;
    }

    /// <summary>Thumbnails drawn this session.</summary>
    public int Rendered { get { lock (_gate) return _rendered; } }

    /// <summary>Tiles handed in whose picture was already drawn.</summary>
    public int Skipped { get { lock (_gate) return _skipped; } }

    /// <summary>Tiles of a superseded build that were never drawn.</summary>
    public int Dropped { get { lock (_gate) return _dropped; } }

    /// <summary>Tiles whose picture the queue remembers drawing.</summary>
    public int Remembered { get { lock (_gate) return _drawn.Count; } }

    /// <summary>Tiles waiting for the worker.</summary>
    public int Pending { get { lock (_gate) return _queue.Count; } }

    /// <summary>Completes, on the UI thread, once every bitmap of the builds so far has landed.</summary>
    public Task Idle { get { lock (_gate) return _idle.Task; } }

    /// <summary>
    /// The tiles of a build, in the order they should be drawn. A tile drawn before with the same
    /// key is skipped; the tiles of the previous build not yet drawn are dropped, and the keys of
    /// tiles no longer in the page are forgotten. Returns <see cref="Idle"/>.
    /// </summary>
    public Task Submit(IReadOnlyList<Job> jobs)
    {
        lock (_gate)
        {
            if (_disposed) return Task.CompletedTask;
            _dropped += _queue.Count;
            _queue = new List<Job>(jobs);
            var ids = new HashSet<string>(jobs.Select(j => j.Id), StringComparer.Ordinal);
            foreach (var gone in _drawn.Keys.Where(k => !ids.Contains(k)).ToList()) _drawn.Remove(gone);
            if (!_running)
            {
                _running = true;
                _stopped.Reset();
                _idle = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
                _ = Task.Run(Worker);
            }
            return _idle.Task;
        }
    }

    private void Worker()
    {
        try
        {
            Drain();
        }
        finally
        {
            _stopped.Set();
        }
    }

    private void Drain()
    {
        while (true)
        {
            Job job;
            lock (_gate)
            {
                if (_disposed || _queue.Count == 0)
                {
                    _running = false;
                    var idle = _idle;
                    // Completed on the UI thread, behind the bitmaps posted before it: whoever awaits
                    // the pass sees every tile filled in.
                    if (!_disposed) _dispatcher.Post(() => idle.TrySetResult(), DispatcherPriority.Background);
                    return;
                }
                job = _queue[0];
                _queue.RemoveAt(0);
            }
            try
            {
                if (job.Prepare() is not { } work) continue;
                lock (_gate)
                {
                    if (_drawn.TryGetValue(job.Id, out var key) && key == work.Key)
                    {
                        _skipped++;
                        continue;
                    }
                }
                var bitmap = work.Render();
                if (bitmap is null) continue;
                lock (_gate)
                {
                    if (_disposed) continue;
                    _drawn[job.Id] = work.Key;
                    _rendered++;
                }
                _dispatcher.Post(() => job.Apply(bitmap), DispatcherPriority.Background);
            }
            catch (Exception ex)
            {
                Log.Warn($"Thumbnail '{job.Id}' failed.", ex);
            }
        }
    }

    /// <summary>Stops the queue: the tiles waiting are dropped, and the worker's tile in hand is finished and forgotten before this returns.</summary>
    public void Dispose()
    {
        lock (_gate)
        {
            _disposed = true;
            _queue.Clear();
        }
        _stopped.Wait(TimeSpan.FromSeconds(5));
    }
}
