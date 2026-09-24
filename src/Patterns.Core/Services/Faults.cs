namespace Patterns.Core.Services;

/// <summary>
/// A connection handler meets two kinds of exception. An I/O end — the peer went away, the socket closed,
/// the desk is shutting down — is routine and says nothing. A fault is anything else: a bug somewhere behind
/// the line, which must leave a trace and an answer. The wire's handlers kept one catch for both, so a fault
/// closed the connection with nothing logged and no ERR; this is the line between them, and the words a
/// status line or a reply may carry (the type and the first line, never the stack).
/// </summary>
public static class Faults
{
    /// <summary>True for the exceptions a socket's end raises: cancellation, disposal, an I/O error, a socket error.</summary>
    public static bool IsIoEnd(Exception ex) => ex switch
    {
        OperationCanceledException => true,
        ObjectDisposedException => true,
        IOException => true,
        System.Net.Sockets.SocketException => true,
        AggregateException a => a.InnerExceptions.Count > 0 && a.InnerExceptions.All(IsIoEnd),
        _ => false,
    };

    /// <summary>The exception's type and the first line of its message, capped — a 300 KB parser message becomes one line.</summary>
    public static string Brief(Exception ex, int max = 160)
    {
        var message = ex.Message ?? "";
        var cut = message.IndexOfAny(LineBreaks);
        if (cut >= 0) message = message[..cut];
        message = message.Trim();
        if (max > 0 && message.Length > max) message = message[..max].TrimEnd() + "…";
        return message.Length == 0 ? ex.GetType().Name : $"{ex.GetType().Name}: {message}";
    }

    private static readonly char[] LineBreaks = { '\r', '\n' };
}

/// <summary>
/// Counts faults and says which to write: the first with its stack, then one a minute with the count, so a
/// fault that repeats on every line can neither fill patterns.log nor hide in it.
/// </summary>
public sealed class FaultThrottle
{
    private readonly object _gate = new();
    private readonly TimeSpan _gap;
    private long _count;
    private DateTime _lastWrittenUtc = DateTime.MinValue;

    public FaultThrottle(TimeSpan? gap = null) => _gap = gap ?? TimeSpan.FromMinutes(1);

    /// <summary>Every fault noted so far.</summary>
    public long Count { get { lock (_gate) return _count; } }

    /// <summary>Notes one fault at <paramref name="nowUtc"/>; true when this one is the one to write.</summary>
    public bool Note(DateTime nowUtc)
    {
        lock (_gate)
        {
            var n = ++_count;
            if (n != 1 && nowUtc - _lastWrittenUtc < _gap) return false;
            _lastWrittenUtc = nowUtc;
            return true;
        }
    }
}
