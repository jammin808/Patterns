namespace Patterns.Core.Play;

/// <summary>
/// The audience listener's routes — the only paths it answers, and the paths the control port
/// never answers. Untrusted phones reach one socket; that socket can reach the room and nothing
/// else. Pure, so the boundary is a table with a test on it.
/// </summary>
public static class AudienceRoutes
{
    private static readonly string[] Gets = { "/", "/index.html", "/play", "/api/play/state" };
    private static readonly string[] Posts = { "/api/play/join", "/api/play/answer", "/api/play/say", "/api/play/vote", "/api/play/draughts" };

    /// <summary>Whether the audience port answers a request at all.</summary>
    public static bool Allows(string method, string path)
    {
        var bare = Bare(path);
        return method switch
        {
            "GET" => Gets.Contains(bare, StringComparer.Ordinal),
            "POST" => Posts.Contains(bare, StringComparer.Ordinal),
            _ => false,
        };
    }

    /// <summary>The audience's own paths, which the control port refuses so a phone has nothing to find there.</summary>
    public static bool AudienceOnly(string path)
    {
        var bare = Bare(path);
        return bare == "/play" || bare == "/api/play/state" || Posts.Contains(bare, StringComparer.Ordinal);
    }

    private static string Bare(string path)
    {
        var q = path.IndexOf('?');
        return q < 0 ? path : path[..q];
    }
}

/// <summary>The budgets an audience server keeps: every one a hard number, none a hope.</summary>
public sealed record AudienceBudget(
    int MaxPlayers = 500,
    int JoinsPerAddressPerMinute = 20,
    int AnswersPerTokenPerMinute = 30,
    int SaysPerTokenPerMinute = 6,
    int MovesPerTokenPerMinute = 60,
    int MaxLongPolls = 1000,
    int MaxConnectionsPerAddress = 64,
    int MaxConnections = 2000,
    int IdleForgetMinutes = 180);

/// <summary>A sliding-window rate limit keyed by a string — an address, a token — with its own clock for the tests.</summary>
public sealed class RateLimiter
{
    private readonly Dictionary<string, Queue<long>> _hits = new();
    private long _lastPruneTicks;

    /// <summary>True and counted when the key has had fewer than <paramref name="limit"/> hits inside the window; false and not counted otherwise.</summary>
    public bool Allow(string key, int limit, TimeSpan window, DateTime utcNow)
    {
        if (limit <= 0) return true;
        var now = utcNow.Ticks;
        lock (_hits)
        {
            if (now - _lastPruneTicks > TimeSpan.TicksPerMinute)
            {
                _lastPruneTicks = now;
                foreach (var stale in _hits.Where(kv => kv.Value.Count == 0 || now - kv.Value.Peek() > window.Ticks * 2).Select(kv => kv.Key).ToList()) _hits.Remove(stale);
            }
            if (!_hits.TryGetValue(key, out var q)) _hits[key] = q = new Queue<long>();
            while (q.Count > 0 && now - q.Peek() > window.Ticks) q.Dequeue();
            if (q.Count >= limit) return false;
            q.Enqueue(now);
            return true;
        }
    }

    public int Keys
    {
        get { lock (_hits) return _hits.Count; }
    }
}
