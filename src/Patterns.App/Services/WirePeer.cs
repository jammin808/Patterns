using System.Text;

namespace Patterns.App.Services;

/// <summary>
/// One writer per remote client (round 65). Everything the desk says to a Companion — the replies
/// to its lines, in order, and the STATE pushes — goes through one queue and one writer task, so
/// a reply is never interleaved with a push half-written from another thread. Before this the
/// handler wrote its replies from its own task while Broadcast wrote STATE from a worker, and two
/// writers on one socket can put half of one line inside the other; a Companion reads a line that
/// is neither, and drops it or worse. Replies are ordered and never dropped, up to a ceiling;
/// STATE is latest-wins — a slow peer gets the newest state, never a backlog of stale ones; and a
/// peer that cannot take its replies within the ceiling, or one line within the write deadline,
/// is closed, because a wire that cannot hear its answers is not a controller. The socket is the
/// peer's to close: the handler reading it sees the close and lets go.
/// </summary>
public sealed class WirePeer : IDisposable
{
    /// <summary>The most replies a peer may have waiting; past it the peer is closed as too slow.</summary>
    public const int MaxQueuedReplies = 256;

    /// <summary>How long one line may take to be written before the peer is closed as dead. Settable for the tests.</summary>
    public static TimeSpan WriteDeadline { get; set; } = TimeSpan.FromSeconds(10);

    private readonly Stream _stream;
    private readonly IDisposable? _owner;
    private readonly object _gate = new();
    private readonly Queue<string> _replies = new();
    private string? _state;
    private readonly SemaphoreSlim _wake = new(0, 1);
    private int _signalled;
    private readonly CancellationTokenSource _cts = new();
    private long _written;
    private volatile bool _writing;
    private volatile string _closedBecause = "";
    private int _closing;
    private int _disposed;

    /// <param name="stream">The socket's stream; the peer owns it and closes it.</param>
    /// <param name="owner">What else closes with the stream — the TcpClient.</param>
    public WirePeer(Stream stream, IDisposable? owner = null)
    {
        _stream = stream;
        _owner = owner;
        _ = Task.Run(() => WriteLoop(_cts.Token));
    }

    /// <summary>Lines written so far — replies and states.</summary>
    public long Written => Interlocked.Read(ref _written);

    /// <summary>Lines waiting: the replies, and the state if one is pending.</summary>
    public int Pending
    {
        get
        {
            lock (_gate)
            {
                return _replies.Count + (_state is null ? 0 : 1);
            }
        }
    }

    /// <summary>Why the peer closed itself, or "" while it is open or was closed by its owner.</summary>
    public string ClosedBecause => _closedBecause;

    /// <summary>Closed — by itself (see <see cref="ClosedBecause"/>) or by its owner.</summary>
    public bool Closed => Volatile.Read(ref _disposed) != 0;

    /// <summary>A reply to a line the peer sent, or the greeting: in order, never dropped — past the ceiling the peer is closed instead.</summary>
    public void Say(string line)
    {
        var close = false;
        lock (_gate)
        {
            if (Closed) return;
            if (_replies.Count >= MaxQueuedReplies) close = true;
            else _replies.Enqueue(line);
        }
        if (close)
        {
            CloseBecause($"{MaxQueuedReplies} replies waiting and none taken — the peer is not reading its answers");
            return;
        }
        Wake();
    }

    /// <summary>A STATE push: latest-wins — one waiting at most, the newest.</summary>
    public void Push(string line)
    {
        lock (_gate)
        {
            if (Closed) return;
            _state = line;
        }
        Wake();
    }

    /// <summary>Waits until everything queued so far has been written, or the timeout: for a last word before a close.</summary>
    public async Task<bool> FlushAsync(TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (Closed) return false;
            if (Pending == 0 && !_writing) return true;
            await Task.Delay(5);
        }
        return false;
    }

    public void Dispose() => Close();

    private void Wake()
    {
        if (Interlocked.Exchange(ref _signalled, 1) != 0) return;
        try
        {
            _wake.Release();
        }
        catch (SemaphoreFullException)
        {
            // The writer has a wake-up waiting already.
        }
        catch (ObjectDisposedException)
        {
            // Closed under us.
        }
    }

    private bool TryTake(out string line)
    {
        lock (_gate)
        {
            if (_replies.Count > 0)
            {
                line = _replies.Dequeue();
                _writing = true;
                return true;
            }
            if (_state is not null)
            {
                line = _state;
                _state = null;
                _writing = true;
                return true;
            }
            line = "";
            return false;
        }
    }

    private async Task WriteLoop(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                await _wake.WaitAsync(ct);
                Volatile.Write(ref _signalled, 0);
                while (TryTake(out var line))
                {
                    var bytes = Encoding.UTF8.GetBytes(line + "\n");
                    using var deadline = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    deadline.CancelAfter(WriteDeadline);
                    try
                    {
                        await _stream.WriteAsync(bytes, deadline.Token);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        CloseBecause($"a line took longer than {WriteDeadline.TotalSeconds:0} s to write — the peer is not reading");
                        return;
                    }
                    finally
                    {
                        _writing = false;
                    }
                    Interlocked.Increment(ref _written);
                }
            }
        }
        catch (OperationCanceledException)
        {
            // Closed.
        }
        catch (Exception ex)
        {
            CloseBecause($"the socket failed on a write: {ex.GetType().Name}");
        }
    }

    private void CloseBecause(string why)
    {
        if (Interlocked.Exchange(ref _closing, 1) != 0) return;
        _closedBecause = why;
        Close();
    }

    private void Close()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        lock (_gate)
        {
            // A closed peer has nothing pending: what was queued for it goes nowhere now.
            _replies.Clear();
            _state = null;
        }
        try { _cts.Cancel(); } catch (Exception) { /* gone */ }
        try { _stream.Dispose(); } catch (Exception) { /* gone */ }
        try { _owner?.Dispose(); } catch (Exception) { /* gone */ }
    }
}
