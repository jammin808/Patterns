namespace Patterns.Core.Services;

public enum SupervisorAction
{
    Stop,     // clean exit — the operator closed the app
    Restart,  // crash or hang — bring the show back after the delay
    GiveUp,   // crash loop — restarting again would just flap the screens
}

public readonly record struct SupervisorVerdict(SupervisorAction Action, TimeSpan Delay);

/// <summary>
/// The watchdog's decision rules, kept pure so they are unit tested: restart crashes and
/// hangs with a short backoff, treat a long run as a fresh start, and stop restarting
/// when crashes come so thick that flapping outputs would be worse than staying down.
/// </summary>
public sealed class SupervisorPolicy
{
    private static readonly int[] DelaySeconds = { 2, 4, 8, 15, 30 };

    private readonly int _maxCrashesInWindow;
    private readonly TimeSpan _crashWindow;
    private readonly TimeSpan _stableRun;
    private readonly List<DateTime> _crashes = new();
    private int _consecutive;

    public SupervisorPolicy(int maxCrashesInWindow = 6, double crashWindowMinutes = 10, double stableRunMinutes = 5)
    {
        _maxCrashesInWindow = maxCrashesInWindow;
        _crashWindow = TimeSpan.FromMinutes(crashWindowMinutes);
        _stableRun = TimeSpan.FromMinutes(stableRunMinutes);
    }

    /// <summary>How long silence on the heartbeat counts as a hung UI thread.</summary>
    public static readonly TimeSpan HangTimeout = TimeSpan.FromSeconds(30);

    /// <summary>
    /// Exit code the app uses to ask the supervisor for an immediate relaunch (the Admin
    /// tab's Restart button, applying a GPU change). Restarted with no backoff delay, but
    /// still counted against the crash-loop window so a restart storm can't flap forever.
    /// </summary>
    public const int RestartRequestExitCode = 82;

    /// <summary>
    /// Exit code the app uses to ask the supervisor to apply the staged update before the
    /// relaunch: the files swap between the two starts, and a new build that does not stay up
    /// through its proving period is rolled back. Otherwise a restart request.
    /// </summary>
    public const int UpdateRequestExitCode = 83;

    /// <summary>Hung = the child was beating and then went silent past the timeout.</summary>
    public static bool IsHung(DateTime? lastBeatUtc, DateTime utcNow)
        => lastBeatUtc is { } beat && utcNow - beat > HangTimeout;

    public SupervisorVerdict OnExit(int exitCode, bool killedForHang, TimeSpan ranFor, DateTime utcNow)
    {
        if (exitCode == 0 && !killedForHang)
        {
            return new SupervisorVerdict(SupervisorAction.Stop, TimeSpan.Zero);
        }

        // A session that ran a good while wasn't a crash loop — start the backoff over.
        if (ranFor >= _stableRun) _consecutive = 0;

        _crashes.Add(utcNow);
        _crashes.RemoveAll(t => utcNow - t > _crashWindow);
        if (_crashes.Count > _maxCrashesInWindow)
        {
            return new SupervisorVerdict(SupervisorAction.GiveUp, TimeSpan.Zero);
        }

        if (exitCode is RestartRequestExitCode or UpdateRequestExitCode && !killedForHang)
        {
            return new SupervisorVerdict(SupervisorAction.Restart, TimeSpan.Zero);
        }

        var delay = TimeSpan.FromSeconds(DelaySeconds[Math.Min(_consecutive, DelaySeconds.Length - 1)]);
        _consecutive++;
        return new SupervisorVerdict(SupervisorAction.Restart, delay);
    }
}

/// <summary>
/// What was running when the app last changed state — read back after a watchdog restart.
/// <paramref name="AirLook"/> is the content the audience was seeing, captured only while the
/// operator was programming in the sandbox (the settings file already holds it otherwise);
/// without it a crash mid-programming would reopen outputs on the untaken preview.
/// </summary>
public sealed record RecoverySnapshot(bool Live, bool AudioPlaying, DateTime UpdatedUtc, string? AirLook = null, RunPlace? Run = null);

/// <summary>
/// The caller's place, written atomically on every GO: what was on standby, what ran last and
/// the last twenty history rows. A relaunch reopens the Run surface disarmed, pointing at the
/// next cue, and fires nothing.
/// </summary>
public sealed record RunPlace(string? StandbyCueId, string? LastCueId, DateTime? LastGoUtc, IReadOnlyList<CueExecutionRecord> History)
{
    public const int HistoryRows = 20;
}

/// <summary>
/// Tiny sidecar file beside the settings: whether outputs (and the audio track) were live.
/// A crash leaves it behind; a clean shutdown clears it; a watchdog relaunch reads it and
/// puts the show back. Atomic like the settings store — a torn write must never mislead.
/// </summary>
public sealed class RecoveryStore
{
    private readonly string _path;

    public RecoveryStore(string directory) => _path = Path.Combine(directory, "patterns.recovery.json");

    public RecoverySnapshot? Read()
    {
        try
        {
            if (!File.Exists(_path)) return null;
            return JsonUtil.Deserialize<RecoverySnapshot>(File.ReadAllText(_path));
        }
        catch (Exception ex)
        {
            Log.Warn("Recovery file unreadable.", ex);
            return null;
        }
    }

    public void Write(bool live, bool audioPlaying, string? airLook = null, RunPlace? run = null)
    {
        try
        {
            var tmp = _path + ".tmp";
            File.WriteAllText(tmp, JsonUtil.Serialize(new RecoverySnapshot(live, audioPlaying, DateTime.UtcNow, airLook, run)));
            File.Move(tmp, _path, overwrite: true);
        }
        catch (Exception ex)
        {
            Log.Warn("Recovery file write failed.", ex);
        }
    }

    public void Clear()
    {
        try
        {
            File.Delete(_path);
        }
        catch
        {
            // Nothing to clear (or locked) — harmless either way.
        }
    }

    /// <summary>Stale files (an old hard power cut) are not acted on.</summary>
    public static bool IsFresh(RecoverySnapshot snapshot, DateTime utcNow)
        => utcNow - snapshot.UpdatedUtc < TimeSpan.FromHours(12);
}

/// <summary>
/// Process-wide health counters: every error the app caught and contained, uptime, and how
/// often the watchdog had to step in. Feeds the health line on the Show page and remotes.
/// </summary>
public static class HealthMonitor
{
    private static readonly object Gate = new();
    private static long _faults;

    public static DateTime StartedUtc { get; private set; } = DateTime.UtcNow;
    public static int Restarts { get; set; }
    public static string? LastFault { get; private set; }
    public static DateTime? LastFaultLocal { get; private set; }

    /// <summary>What the last supervisor left behind when it stood down (it gave up, it could not start the app) — shown until the next reset.</summary>
    public static string WatchdogNote { get; set; } = "";

    public static long Faults => Interlocked.Read(ref _faults);

    public static void Record(string message)
    {
        Interlocked.Increment(ref _faults);
        lock (Gate)
        {
            LastFault = message.Length > 80 ? message[..80] + "…" : message;
            LastFaultLocal = DateTime.Now;
        }
    }

    public static string Summary(DateTime utcNow)
    {
        var up = utcNow - StartedUtc;
        var upText = up.TotalHours >= 1 ? $"{(int)up.TotalHours}h {up.Minutes:00}m" : $"{up.Minutes}m {up.Seconds:00}s";
        var parts = new List<string> { $"Up {upText}" };
        if (Restarts > 0) parts.Add($"watchdog restarts: {Restarts}");
        if (WatchdogNote.Length > 0) parts.Add(WatchdogNote);
        if (Faults == 0)
        {
            parts.Add("no faults");
        }
        else
        {
            lock (Gate)
            {
                parts.Add($"{Faults} fault{(Faults == 1 ? "" : "s")} caught, show kept running (last {LastFaultLocal:HH\\:mm} — {LastFault})");
            }
        }
        return string.Join(" · ", parts);
    }

    public static void Reset()
    {
        Interlocked.Exchange(ref _faults, 0);
        lock (Gate)
        {
            LastFault = null;
            LastFaultLocal = null;
        }
        StartedUtc = DateTime.UtcNow;
        Restarts = 0;
        WatchdogNote = "";
    }
}

/// <summary>
/// The note a supervisor leaves beside the settings when it stands down — it gave up on a
/// crash loop, or could not start the app — so the next start says so on the health line
/// instead of the operator finding a silent watchdog log. Read once and cleared.
/// </summary>
public static class WatchdogMarker
{
    public const string FileName = "patterns.watchdog.gaveup";

    public static void Write(string directory, string note)
    {
        try
        {
            File.WriteAllText(Path.Combine(directory, FileName), note);
        }
        catch
        {
            // Best-effort, like the watchdog log.
        }
    }

    /// <summary>The note, and the file is gone so it shows once; "" when there is none.</summary>
    public static string ReadAndClear(string directory)
    {
        var path = Path.Combine(directory, FileName);
        try
        {
            if (!File.Exists(path)) return "";
            var note = File.ReadAllText(path).Trim();
            File.Delete(path);
            return note;
        }
        catch
        {
            return "";
        }
    }
}
