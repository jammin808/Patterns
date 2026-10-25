using System.Globalization;
using Patterns.Core.Model;

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
    /// Round 87: how long silence counts as a hang while the outputs are live or the stack is armed — the child says so
    /// with <see cref="LiveBeat"/>. An audience looking at a frozen wall is a minute too long at thirty seconds; ten is
    /// past any honest stall the desk measures (the tick budget, a page switch, a save) and short enough to matter.
    /// </summary>
    public static readonly TimeSpan LiveHangTimeout = TimeSpan.FromSeconds(10);

    /// <summary>The hang timeout for a child whose last beat said it was live, or not.</summary>
    public static TimeSpan HangTimeoutFor(bool live) => live ? LiveHangTimeout : HangTimeout;

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

    /// <summary>
    /// Round 76: the exit of a desk that handed its screens to its replacement. A restart asked for
    /// while the outputs are live is a handover, not a gap: the desk asks the supervisor for a
    /// replacement through its heartbeat (<see cref="HandoverBeat"/>), keeps its windows and its
    /// sound up while the replacement boots, opens nothing new, and leaves with this code once the
    /// replacement has its own windows over the old ones and has asked for the screens. The
    /// supervisor then starts nothing — the replacement is already the child it watches.
    /// </summary>
    public const int ReplacedExitCode = 84;

    /// <summary>The heartbeat byte of a child that is alive.</summary>
    public const byte AliveBeat = 1;

    /// <summary>The heartbeat byte of a child that is alive and asks to be replaced (round 76): start the next desk now, beside this one.</summary>
    public const byte HandoverBeat = 2;

    /// <summary>Round 87: the heartbeat byte of a child that is alive with its outputs live or its stack armed — judged by <see cref="LiveHangTimeout"/>.</summary>
    public const byte LiveBeat = 3;

    /// <summary>Round 87: the byte the child beats — the handover ask wins, then live, then plain alive.</summary>
    public static byte BeatFor(byte asked, bool live) => asked == HandoverBeat ? HandoverBeat : live ? LiveBeat : AliveBeat;

    /// <summary>
    /// How long a desk that asked to be replaced waits for the replacement's ask before it takes
    /// its place back — the supervisor gone, a replacement that never became a desk. The startup
    /// deadline is the replacement's; this is a little less, so the old desk says so first and
    /// carries on rather than sitting frozen behind a restart that is not coming.
    /// </summary>
    public static readonly TimeSpan HandoverPatience = TimeSpan.FromSeconds(90);

    /// <summary>Hung = the child was beating and then went silent past the timeout (its live one when its last beat said live).</summary>
    public static bool IsHung(DateTime? lastBeatUtc, DateTime utcNow, bool live = false)
        => lastBeatUtc is { } beat && utcNow - beat > HangTimeoutFor(live);

    /// <summary>
    /// How long a child may run before its first heartbeat (round 65). The runtime, the
    /// framework, the graphics device, the outputs' takeover, the desk and its window all come
    /// up inside it on the slowest show machine with a cold disk; a child still silent past it
    /// wedged before it ever became a desk — a driver, a takeover, a construction — and is a
    /// startup hang, not a process to wait for forever.
    /// </summary>
    public static readonly TimeSpan StartupDeadline = TimeSpan.FromSeconds(120);

    /// <summary>The child as the watchdog reads it (round 65).</summary>
    public enum ChildPhase
    {
        /// <summary>Started, no heartbeat yet, inside the startup deadline.</summary>
        Starting,
        /// <summary>Beating within the hang timeout.</summary>
        Beating,
        /// <summary>Never beat, and the startup deadline has passed.</summary>
        StartupHang,
        /// <summary>Beat once, then silent past the hang timeout.</summary>
        Hung,
    }

    /// <summary>
    /// Where the child stands: before the first beat the startup deadline judges it, after the
    /// first beat the hang timeout does. Both ends kill the child and go through <see cref="OnExit"/>
    /// as a hang, so the backoff and the crash-loop cap apply to a start that never finishes
    /// exactly as to a desk that froze.
    /// </summary>
    public static ChildPhase Phase(DateTime startedUtc, DateTime? lastBeatUtc, DateTime utcNow, bool live = false)
    {
        if (lastBeatUtc is { } beat) return utcNow - beat > HangTimeoutFor(live) ? ChildPhase.Hung : ChildPhase.Beating;
        return utcNow - startedUtc > StartupDeadline ? ChildPhase.StartupHang : ChildPhase.Starting;
    }

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

        // A desk that left after handing its screens over (84) with no replacement running is a
        // restart the supervisor missed — it comes straight back, like a restart request.
        if (exitCode is RestartRequestExitCode or UpdateRequestExitCode or ReplacedExitCode && !killedForHang)
        {
            return new SupervisorVerdict(SupervisorAction.Restart, TimeSpan.Zero);
        }

        var delay = TimeSpan.FromSeconds(DelaySeconds[Math.Min(_consecutive, DelaySeconds.Length - 1)]);
        _consecutive++;
        return new SupervisorVerdict(SupervisorAction.Restart, delay);
    }
}

/// <summary>
/// What was running when the app last changed state — read back after a restart of any kind.
///
/// <paramref name="Air"/> is the program itself: the picture the audience was seeing, whole,
/// written only while it differs from the settings file (EDIT SAFE open, or a clip covering the
/// show). It is a state and not a look on purpose — a look carries the pattern, the overlays and
/// the countdown but not the brand kit, not the lower third on air or where it is in its life,
/// not the NDI senders and not a locked screen's own picture, so a restart that restored a look
/// handed the audience a hybrid nobody had ever programmed.
/// <paramref name="AirLook"/> is the same thing as written by builds before that, kept so a
/// sidecar left by an older build still puts the show back.
/// <paramref name="Sandboxed"/> says the desk was split when the record was written — EDIT SAFE
/// open, the audience on one picture and the operator building another. A restart needs it to
/// put the two back where they were: without it the desk cannot tell "the show" from "the show
/// being built", and both come back as whichever one the settings file happened to hold. Null is
/// a record from a build that did not know, and the desk acts on no opinion it does not have.
/// <paramref name="BlackTargets"/> are the screens faded to black on their own — runtime-only in
/// the model, and part of the picture: a foyer wall the operator darkened must not come back lit.
/// <paramref name="Streaming"/> only records that the stream was up; a restart never starts one
/// by itself (the stream is not a Program output, and pushing to a public endpoint is the
/// operator's call), it says so on the status line.
/// <paramref name="AirLabel"/>, <paramref name="AirLookId"/>, <paramref name="PreviousAirLookId"/>
/// and <paramref name="PreviewLookId"/> are what the desk calls the picture rather than the
/// picture itself — the LIVE strip, the look tallies, LOOK BACK, the beacon and every remote read
/// them. Restored with the pixels, or the wall is right and the desk claims to know nothing
/// about it at the moment a caller most needs to trust the strip.
/// <paramref name="Deliberate"/> (round 76) says the record was written for a restart the operator
/// asked for — the Machine page's RESTART, the wire's — rather than left behind by a crash. A
/// deliberate restart puts the show back whatever the watchdog's AutoRestore choice says: that
/// choice is about a crash the operator did not ask for, and it must never be the reason a restart
/// they did ask for leaves a dark room.
/// </summary>
public sealed record RecoverySnapshot(
    bool Live,
    bool AudioPlaying,
    DateTime UpdatedUtc,
    string? AirLook = null,
    RunPlace? Run = null,
    bool? Sandboxed = null,
    ShowState? Air = null,
    IReadOnlyList<string>? BlackTargets = null,
    bool Streaming = false,
    string? AirLabel = null,
    string? AirLookId = null,
    string? PreviousAirLookId = null,
    string? PreviewLookId = null,
    bool Deliberate = false);

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
    private readonly string _backup;

    public RecoveryStore(string directory)
    {
        _path = Path.Combine(directory, "patterns.recovery.json");
        _backup = _path + ".bak";
    }

    /// <summary>Round 83: what the last read found wrong, or "" — a record unreadable at boot and read from the one before it, or one with no readable backup either; the Super Check's row.</summary>
    public string Problem { get; private set; } = "";

    /// <summary>
    /// The record, or null when a clean exit cleared it. A record that cannot be read (a power cut mid-move, a stick
    /// whose move is not atomic) is not "no crash": the one written before it is read instead (round 83), and the
    /// problem is kept for the Super Check so a disk that loses writes is seen. A file that parses to no record at
    /// all — the literal null — is unreadable in the same sense (round 85): it holds no record, so the backup is read.
    /// </summary>
    public RecoverySnapshot? Read()
    {
        Problem = "";
        if (!File.Exists(_path)) return null;
        try
        {
            if (JsonUtil.Deserialize<RecoverySnapshot>(File.ReadAllText(_path)) is { } record) return record;
            Log.Warn("Recovery file holds no record.");
        }
        catch (Exception ex)
        {
            Log.Warn("Recovery file unreadable.", ex);
        }
        if (File.Exists(_backup))
        {
            try
            {
                if (JsonUtil.Deserialize<RecoverySnapshot>(File.ReadAllText(_backup)) is { } previous)
                {
                    Problem = "the recovery record was unreadable at boot; the one written before it was put back instead";
                    Log.Warn(Problem);
                    return previous;
                }
                Log.Warn("Recovery backup holds no record either.");
            }
            catch (Exception ex)
            {
                Log.Warn("Recovery backup unreadable too.", ex);
            }
        }
        Problem = "the recovery record was unreadable at boot and had no readable backup — the last air was not put back";
        Log.Warn(Problem);
        return null;
    }

    /// <summary>The plain record: what was live, and nothing about the picture. Used by tests and by an install's own bookkeeping.</summary>
    public void Write(bool live, bool audioPlaying, string? airLook = null, RunPlace? run = null)
        => Write(new RecoverySnapshot(live, audioPlaying, DateTime.UtcNow, airLook, run));

    public void Write(RecoverySnapshot snapshot) => WriteJson(Serialize(snapshot));

    /// <summary>The record as its file holds it — compact, stamped now: the record holds a whole show state, and it is made while a show is running.</summary>
    public static string Serialize(RecoverySnapshot snapshot) => JsonUtil.SerializeCompact(snapshot with { UpdatedUtc = DateTime.UtcNow });

    /// <summary>
    /// A record already serialised, onto the disk whole: flushed, then moved over the old one with the old one kept as
    /// .bak, so a reader never sees half a record and a torn one has a whole predecessor. The old one is kept only when
    /// it is a record (round 85): a torn file rotated into .bak would bury the whole record the ladder exists to keep.
    /// </summary>
    public void WriteJson(string json)
    {
        try
        {
            AtomicFile.WriteAllTextKeepingBackup(_path, json, worthKeeping: IsRecord);
        }
        catch (Exception ex)
        {
            Log.Warn("Recovery file write failed.", ex);
        }
    }

    /// <summary>True when the text is a whole record: it parses, and to something.</summary>
    private static bool IsRecord(string text)
    {
        try
        {
            return JsonUtil.Deserialize<RecoverySnapshot>(text) is not null;
        }
        catch (Exception)
        {
            return false;
        }
    }

    public void Clear()
    {
        try
        {
            File.Delete(_path);
            File.Delete(_backup);                                                         // a clean exit leaves no record to resurrect from the backup
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

/// <summary>
/// What an exit code says, in words — the supervisor's log, the crash note and the health line
/// all read the same table. Windows' status codes for a native fault sit above 0xC0000000 and
/// arrive as negative ints; the .NET runtime's own unhandled-exception code is 0xE0434352.
/// </summary>
public static class ExitCodes
{
    public const int AccessViolation = unchecked((int)0xC0000005);
    public const int IllegalInstruction = unchecked((int)0xC000001D);
    public const int StackOverflow = unchecked((int)0xC00000FD);
    public const int HeapCorruption = unchecked((int)0xC0000374);
    public const int StackBufferOverrun = unchecked((int)0xC0000409);
    public const int ConsoleClosed = unchecked((int)0xC000013A);
    public const int ClrException = unchecked((int)0xE0434352);

    /// <summary>A fault in native code — a decoder, a driver, a library — rather than a managed exception or a request.</summary>
    public static bool IsNativeFault(int code)
        => code is AccessViolation or IllegalInstruction or StackOverflow or HeapCorruption or StackBufferOverrun;

    public static string Hex(int code) => $"0x{(uint)code:X8}";

    /// <summary>"an access violation (a native fault…)", "an unhandled .NET exception", "exit code 3 (0x00000003)".</summary>
    public static string Describe(int code) => code switch
    {
        0 => "a clean close",
        SupervisorPolicy.RestartRequestExitCode => "a restart asked for on the Machine page",
        SupervisorPolicy.UpdateRequestExitCode => "a restart to apply an update",
        SupervisorPolicy.ReplacedExitCode => "a restart handed over to its replacement — the screens never went dark",
        AccessViolation => "an access violation (a native fault: a decoder, a driver or a library wrote where it should not)",
        IllegalInstruction => "an illegal instruction (a native fault)",
        StackOverflow => "a stack overflow",
        HeapCorruption => "a heap corruption (a native fault)",
        StackBufferOverrun => "a fail-fast or a stack buffer overrun (a native fault)",
        ConsoleClosed => "the process being closed from outside",
        ClrException => "an unhandled .NET exception (see patterns.log)",
        _ => $"exit code {code} ({Hex(code)})",
    };
}

/// <summary>
/// The note the supervisor leaves beside the settings on every crash restart: what the last run
/// ended in, when, after how long, the mini-dump if one was written, and how many runs in a row
/// ended in a native fault. The next start reads it onto the health line and into its log — and
/// decides its safe run from it — then clears it, so it applies to that run alone. A managed
/// crash carries the exception's own words (its type, message and the app's frames) in
/// <paramref name="Detail"/>, written by the app itself on the way down, so the health line names
/// what threw and where rather than just the runtime's exit code.
/// </summary>
public sealed record CrashNote(int ExitCode, string Words, bool NativeFault, bool Hung, DateTime AtUtc, double RanForSeconds, string DumpPath, int NativeFaultsInARow, string Detail = "")
{
    /// <summary>One sentence for the health line and the log.</summary>
    public string Sentence
    {
        get
        {
            var when = AtUtc.ToLocalTime().ToString("HH:mm:ss", CultureInfo.InvariantCulture);
            var ran = RanForSeconds >= 3600 ? $"{RanForSeconds / 3600:0.#} h" : RanForSeconds >= 60 ? $"{RanForSeconds / 60:0} min" : $"{RanForSeconds:0} s";
            var why = Hung ? "a hung UI thread (the watchdog ended it)" : Words;
            var detail = Detail.Length > 0 ? $" — {Detail} —" : "";
            var dump = DumpPath.Length > 0 ? $"; a mini-dump is at {DumpPath}" : NativeFault ? "; no mini-dump was written (createdump.exe is not beside Patterns.exe)" : "";
            return $"The last run ended in {why}{detail} at {when} after {ran}{dump}.";
        }
    }
}

/// <summary>
/// An exception in one line for the health line, the crash note and the log's first words: its
/// type, its message, and the app's own frames it passed through — "InvalidOperationException:
/// Sequence contains no elements — in MainWindow.ApplyDeskLayout, MainViewModel.SelectPage". The
/// full stack is in the log beside it; this is what the operator reads without opening it.
/// </summary>
public static class FaultWords
{
    /// <summary>How many of the app's own frames the line names.</summary>
    public const int Frames = 2;

    /// <summary>The message is cut to this many characters so a long one never floods the line.</summary>
    public const int MessageLength = 160;

    public static string Describe(Exception ex)
    {
        try
        {
            var inner = Unwrap(ex);
            var message = OneLine(inner.Message);
            var frames = OwnFrames(inner);
            var words = message.Length > 0 ? $"{inner.GetType().Name}: {message}" : inner.GetType().Name;
            return frames.Count > 0 ? $"{words} — in {string.Join(", ", frames)}" : words;
        }
        catch
        {
            return ex.GetType().Name;
        }
    }

    /// <summary>The exception that did the damage: through the wrappers the runtime and reflection add.</summary>
    public static Exception Unwrap(Exception ex)
    {
        for (var i = 0; i < 8; i++)
        {
            switch (ex)
            {
                case System.Reflection.TargetInvocationException { InnerException: { } t }:
                    ex = t;
                    continue;
                case AggregateException { InnerExceptions.Count: > 0 } a:
                    ex = a.InnerExceptions[0];
                    continue;
                default:
                    return ex;
            }
        }
        return ex;
    }

    /// <summary>"Type.Method" for the first frames of the app's own code (Patterns.*), innermost first; empty when the stack never touched it.</summary>
    public static IReadOnlyList<string> OwnFrames(Exception ex, int max = Frames)
    {
        var list = new List<string>();
        var trace = new System.Diagnostics.StackTrace(ex, false);
        foreach (var frame in trace.GetFrames())
        {
            var method = frame.GetMethod();
            var type = method?.DeclaringType;
            if (method is null || type is null) continue;
            // A lambda or an iterator sits in a compiler-generated nested class: name its owner.
            while (type.IsNested && type.Name.StartsWith('<') && type.DeclaringType is { } outer) type = outer;
            if (!(type.FullName ?? "").StartsWith("Patterns.", StringComparison.Ordinal)) continue;
            var name = method.Name;
            // "<SelectPage>b__12_0" is the compiler's name for a lambda inside SelectPage.
            if (name.StartsWith('<') && name.IndexOf('>') is > 1 and var end) name = name[1..end];
            if (name is ".ctor" or ".cctor") name = "the constructor";
            var words = $"{type.Name}.{name}";
            if (!list.Contains(words)) list.Add(words);
            if (list.Count >= max) break;
        }
        return list;
    }

    private static string OneLine(string message)
    {
        var flat = string.Join(" ", message.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(l => l.Trim())).Trim();
        return flat.Length > MessageLength ? flat[..(MessageLength - 1)] + "…" : flat;
    }
}

public static class CrashMarker
{
    public const string FileName = "patterns.crash.json";

    public static void Write(string directory, CrashNote note)
    {
        try
        {
            AtomicFile.WriteAllText(Path.Combine(directory, FileName), JsonUtil.Serialize(note));
        }
        catch
        {
            // Best-effort, like the watchdog log.
        }
    }

    /// <summary>The note without consuming it (the supervisor counts native faults in a row through it).</summary>
    public static CrashNote? Peek(string directory)
    {
        try
        {
            var path = Path.Combine(directory, FileName);
            return File.Exists(path) ? JsonUtil.Deserialize<CrashNote>(File.ReadAllText(path)) : null;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>The note, and the file is gone so it shapes one run only; null when there is none or it is unreadable.</summary>
    public static CrashNote? ReadAndClear(string directory)
    {
        var note = Peek(directory);
        try
        {
            File.Delete(Path.Combine(directory, FileName));
        }
        catch
        {
            // A marker that will not delete is read again next start — harmless.
        }
        return note;
    }
}

/// <summary>
/// Mini-dumps of a native crash: the runtime writes one when the child is started with these
/// variables and createdump.exe sits beside the exe (the publish script puts it there). They go
/// into a crashes folder beside the settings, the newest few kept, so a fault nobody can reproduce
/// still leaves something a debugger reads.
/// </summary>
public static class CrashDumps
{
    public const string Folder = "crashes";

    /// <summary>How many dumps are kept; the oldest past this go on every supervisor start.</summary>
    public const int Keep = 3;

    /// <summary>A dump larger than this is left out of a support bundle (its name is still listed).</summary>
    public const long BundleMaxBytes = 60L * 1024 * 1024;

    public static string DirectoryFor(string baseDirectory) => Path.Combine(baseDirectory, Folder);

    /// <summary>The environment the child runs with: a mini-dump (type 1, the smallest) on a native crash, named by pid and time.</summary>
    public static IReadOnlyDictionary<string, string> Environment(string baseDirectory) => new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["DOTNET_DbgEnableMiniDump"] = "1",
        ["DOTNET_DbgMiniDumpType"] = "1",
        ["DOTNET_DbgMiniDumpName"] = Path.Combine(DirectoryFor(baseDirectory), "patterns-%p-%t.dmp"),
        ["DOTNET_CreateDumpDiagnostics"] = "0",
    };

    /// <summary>The dumps on disk, newest first — after deleting the oldest past the cap.</summary>
    public static IReadOnlyList<string> Sweep(string baseDirectory)
    {
        try
        {
            var dir = DirectoryFor(baseDirectory);
            System.IO.Directory.CreateDirectory(dir);
            var dumps = new DirectoryInfo(dir).GetFiles("*.dmp").OrderByDescending(f => f.LastWriteTimeUtc).ToList();
            foreach (var old in dumps.Skip(Keep))
            {
                try
                {
                    old.Delete();
                }
                catch
                {
                    // A dump a debugger holds open stays until next time.
                }
            }
            return dumps.Take(Keep).Select(f => f.FullName).ToList();
        }
        catch
        {
            return Array.Empty<string>();
        }
    }

    /// <summary>The newest dump written since a moment (the child's start), or "" when none was.</summary>
    public static string NewestSince(string baseDirectory, DateTime sinceUtc)
    {
        try
        {
            var dir = DirectoryFor(baseDirectory);
            if (!System.IO.Directory.Exists(dir)) return "";
            var newest = new DirectoryInfo(dir).GetFiles("*.dmp").Where(f => f.LastWriteTimeUtc >= sinceUtc.AddSeconds(-5)).OrderByDescending(f => f.LastWriteTimeUtc).FirstOrDefault();
            return newest?.FullName ?? "";
        }
        catch
        {
            return "";
        }
    }
}

/// <summary>
/// Whether the clips decode on the graphics card this run. Auto is hardware — except in the run
/// right after a native fault, the safe run, where the decoder is the first suspect and software
/// decoding costs a few percent of CPU to rule it out; the Machine page says so, and Hardware
/// or Software as the choice always wins.
/// </summary>
public static class VideoDecodingChoice
{
    public static bool UseHardware(VideoDecodingKind kind, bool safeRun) => kind switch
    {
        VideoDecodingKind.Hardware => true,
        VideoDecodingKind.Software => false,
        _ => !safeRun,
    };

    /// <summary>The Machine page's line.</summary>
    public static string Words(VideoDecodingKind kind, bool safeRun)
    {
        var hardware = UseHardware(kind, safeRun);
        var now = hardware ? "clips decode on the graphics card" : "clips decode in software (the CPU)";
        return kind switch
        {
            VideoDecodingKind.Hardware => $"Hardware: {now}, whatever the last run did.",
            VideoDecodingKind.Software => $"Software: {now} — the stable choice on a laptop whose driver has faulted.",
            _ => safeRun
                ? $"Auto, in a safe run: {now} this once, because the last run ended in a native fault. The next clean run goes back to the card."
                : $"Auto: {now}. The run after a native fault decodes in software and says so here.",
        };
    }
}

/// <summary>
/// Who may move the show over OSC. OSC is UDP with no session, so it cannot present a proof: on a desk with no token it
/// is open as before (kept to the control network by the bind address, round 79); on a paired desk its mutating verbs
/// are taken only from this machine or from an address the operator named (Remote page, OSC ALLOW), and its queries
/// (PING, STATUS) stay open, as the wire's do. A proof on the wire or the web proves a key on that one connection and
/// grants nothing to the address it came from (ADR-017): no machine reaches this list but by the operator's hand.
/// Pure: the list is handed in.
/// </summary>
public static class OscGate
{
    public static bool Allows(string token, System.Net.IPAddress from, bool trustLoopback, string allowList)
    {
        if (!PairingToken.Needed(token)) return true;
        if (trustLoopback && System.Net.IPAddress.IsLoopback(from)) return true;
        return AllowList(allowList).Contains(Normal(from));
    }

    /// <summary>The addresses the allow-list names (<see cref="Read"/>).</summary>
    public static IReadOnlySet<string> AllowList(string? words) => Read(words).Addresses;

    /// <summary>
    /// The allow-list as the desk reads it: commas, semicolons or spaces between entries, each entry one whole address
    /// read as the bind address is (<see cref="BindAddress.TryAddress"/>), and every other entry left out and named —
    /// never counted. The parser's shorthand would take '192.168.1' as 192.168.0.1, so a typo would read as a listed
    /// address on the Super Check and the Eye while the controller it meant stayed refused.
    /// </summary>
    public static OscAllowList Read(string? words)
    {
        var addresses = new HashSet<string>(StringComparer.Ordinal);
        var leftOut = new List<string>();
        foreach (var w in (words ?? "").Split(Separators, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            if (BindAddress.TryAddress(w, out var a)) addresses.Add(Normal(a));
            else if (!leftOut.Contains(w, StringComparer.Ordinal)) leftOut.Add(w);
        }
        return new OscAllowList(addresses, leftOut);
    }

    private static readonly char[] Separators = { ',', ';', ' ', '\t' };

    /// <summary>An address as compared: IPv4 written plainly even when it came mapped into IPv6.</summary>
    public static string Normal(System.Net.IPAddress a) => (a.IsIPv4MappedToIPv6 ? a.MapToIPv4() : a).ToString();

    /// <summary>The refusal an unlisted sender hears on /patterns/error; it opens "not paired —", the words a controller's operator searches for.</summary>
    public const string NotListed = "not paired — on a paired desk OSC moves the show only from the desk's own machine or an address on its Remote page's OSC ALLOW list; pairing on the wire or the web grants nothing to an address";

    /// <summary>The paired desk's rule as the Super Check and the Eye say it.</summary>
    public const string PairedRule = "on this paired desk its verbs come only from this machine or an address on the Remote page's OSC ALLOW list — a proof on the wire or the web grants nothing to an address";

    /// <summary>
    /// What a listed address leaves open while the port listens on every interface: a datagram's source is whatever its
    /// sender writes, so the list narrows who is believed, never who can write that address — only the bind does.
    /// </summary>
    public const string ListedNotProof = "a listed address is not a proof — any machine that reaches this port can write it as its source; bind the control ports to keep it to the control network";

    /// <summary>What the allow-list names, in the readers' words: none listed says who is refused until one is; an entry that is not an address is named as left out.</summary>
    public static string Listed(int count, IReadOnlyList<string>? leftOut = null)
    {
        var words = count switch
        {
            <= 0 => "OSC ALLOW names no address, so a controller on another machine is refused until its address is listed",
            1 => "OSC ALLOW names 1 address",
            _ => $"OSC ALLOW names {count} addresses",
        };
        return leftOut is { Count: > 0 } ? words + "; " + LeftOutWords(leftOut) : words;
    }

    /// <summary>The entries the list left out, each named as the typo it is: the first three, then how many more.</summary>
    public static string LeftOutWords(IReadOnlyList<string> leftOut)
    {
        var named = string.Join("; ", leftOut.Take(LeftOutNamedMost).Select(w => $"'{(w.Length > 40 ? w[..40] + "…" : w)}' is not an address — left out"));
        return leftOut.Count > LeftOutNamedMost ? $"{named}; and {leftOut.Count - LeftOutNamedMost} more" : named;
    }

    private const int LeftOutNamedMost = 3;

    /// <summary>
    /// A paired desk's OSC note after the port's own reason, the one sentence the Super Check's row and the Eye's node
    /// share: the rule, that its questions stay open, what the list names and leaves out, and — while the port listens
    /// on every interface and the list names someone — what a listed address does not prove.
    /// </summary>
    public static string PairedWords(int allowed, IReadOnlyList<string>? leftOut, bool bound)
        => $"{PairedRule}; its questions stay open. {Listed(allowed, leftOut)}" + (allowed > 0 && !bound ? ". " + Capital(ListedNotProof) : "");

    private static string Capital(string words) => words.Length == 0 ? words : char.ToUpperInvariant(words[0]) + words[1..];

    /// <summary>How many addresses the log names a refused verb for, each once, before one line says it names no more.</summary>
    public const int RefusedNamedMost = 64;

    /// <summary>
    /// The log line for a refused verb, by what <see cref="SaidOnce"/> answered for its address: the address named the first
    /// time, the one line past the bound that says later addresses are no longer named, and nothing otherwise.
    /// </summary>
    public static string? RefusedLine(SaidOnceAnswer said, string address) => said switch
    {
        SaidOnceAnswer.Say => $"OSC: a verb from {address} refused — this desk is paired and {address} is not on the Remote page's OSC ALLOW list (a proof on the wire or the web grants nothing to an address); list it there if that machine is yours. Said once for this address.",
        SaidOnceAnswer.Full => $"OSC: verbs refused from more than {RefusedNamedMost} addresses — no more addresses are named in this log; the Remote page's OSC line counts every refusal.",
        _ => null,
    };
}

/// <summary>The OSC allow-list as read: the addresses it names, as compared, and the entries it left out because they are not one whole address.</summary>
public sealed record OscAllowList(IReadOnlySet<string> Addresses, IReadOnlyList<string> LeftOut);
