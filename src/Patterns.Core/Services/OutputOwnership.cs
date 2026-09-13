namespace Patterns.Core.Services;

/// <summary>
/// Which process has this show folder's screens, written by whoever owns them. A crash or a hang
/// does not take the render windows down with the desk — that is the point, the room keeps its
/// picture — so the next start has to find them again rather than open a second set beside them.
/// The pid alone is not enough (Windows hands the same number out again), so the owner's own
/// start time travels with it, and a beat every second says whether that owner is still answering.
/// </summary>
/// <param name="Pid">The owning process.</param>
/// <param name="StartedAtUtcTicks">That process's start time, UTC ticks — a pid with a different start time is a different process.</param>
/// <param name="HeartbeatUtc">Last refreshed by the owner's own once-a-second poll; a hung desk stops writing it while its windows play on.</param>
/// <param name="ClaimedUtc">When the outputs went live under this owner.</param>
/// <param name="Targets">The content targets whose windows are open — screen ids and canvas keys, for the words the next start says.</param>
/// <param name="Machine">The computer's name, so a show folder on a share never reads as this machine's own orphan.</param>
/// <param name="ExePath">The owner's executable, checked before this desk ever ends that process.</param>
public sealed record OutputOwner(
    int Pid,
    long StartedAtUtcTicks,
    DateTime HeartbeatUtc,
    DateTime ClaimedUtc,
    IReadOnlyList<string> Targets,
    string Machine,
    string ExePath);

/// <summary>A claimant asking the owner to let the screens go. Written by the new desk, read and cleared by the old one.</summary>
/// <param name="Pid">The claiming process.</param>
/// <param name="AskedUtc">When it asked — an ask nobody answered is given up on after <see cref="OutputOwnership.HandoverGrace"/>.</param>
public sealed record HandoverRequest(int Pid, DateTime AskedUtc);

/// <summary>What the record beside the settings means for the desk that just started.</summary>
public enum OutputClaim
{
    /// <summary>No record: nothing else has the screens.</summary>
    Free,

    /// <summary>The record is this very process — a re-read, not an orphan.</summary>
    Ours,

    /// <summary>A record left behind by a process that is gone: the windows went with it. Clear it and carry on.</summary>
    Stale,

    /// <summary>The owner is alive and beating — a second desk on the same folder. Ask it for the screens.</summary>
    HeldByLiveDesk,

    /// <summary>The owner's process is up but its desk stopped answering: the windows are playing with nobody at the controls.</summary>
    HeldByHungDesk,

    /// <summary>Another computer's record (a show folder on a share) — never this desk's to take.</summary>
    AnotherMachine,

    /// <summary>
    /// The record could not be read — a locked file, a torn write, a folder that is not there: a
    /// fence, not an absence. Nothing is opened by itself; OUTPUTS ON by hand still opens the
    /// screens, with the words said first.
    /// </summary>
    Unknown,
}

/// <summary>
/// The rules for taking the screens back, kept pure so every branch is a unit test rather than a
/// second process: what a record means, how long an owner may be silent before it counts as hung,
/// and the words the desk says about it. The doing — asking, waiting, ending a process — is the
/// App's.
/// </summary>
public static class OutputOwnership
{
    /// <summary>The owner refreshes its record this often, from the same once-a-second poll that keeps the desk honest.</summary>
    public static readonly TimeSpan HeartbeatEvery = TimeSpan.FromSeconds(1);

    /// <summary>
    /// Silence past this from a process that is still up means its desk is hung: the watchdog's own
    /// patience minus a little, so a desk the watchdog is about to restart is never called healthy.
    /// </summary>
    public static readonly TimeSpan HeartbeatSilent = TimeSpan.FromSeconds(10);

    /// <summary>How long a polite ask is given before the old process is ended outright.</summary>
    public static readonly TimeSpan HandoverGrace = TimeSpan.FromSeconds(3);

    /// <summary>A record older than this is history — a hard power cut, a folder off a stick — and is never acted on.</summary>
    public static readonly TimeSpan Forgotten = TimeSpan.FromHours(12);

    /// <summary>
    /// What the record means here and now. <paramref name="processStartTicks"/> answers null when
    /// there is no process with that id (UTC ticks otherwise), so pid reuse reads as stale rather
    /// than as an owner. The two-answer read: every process it can see, it can read.
    /// </summary>
    public static OutputClaim Read(
        OutputOwner? owner,
        int myPid,
        string myMachine,
        DateTime utcNow,
        Func<int, long?> processStartTicks)
        => Read(owner, myPid, myMachine, utcNow, ProcessSight.From(processStartTicks));

    /// <summary>
    /// What the record means here and now, from a look that can answer three ways. A process that
    /// is gone, or whose id was handed out again, took its windows with it: stale. One that is up
    /// but cannot be read from here is a fence, not an absence — it may well still have the
    /// screens, and the heartbeat alone says whether its desk still answers — so it reads as an
    /// owner, live or hung, never as free.
    /// </summary>
    public static OutputClaim Read(
        OutputOwner? owner,
        int myPid,
        string myMachine,
        DateTime utcNow,
        Func<int, ProcessSight> look)
    {
        if (owner is null) return OutputClaim.Free;
        if (owner.Pid == myPid) return OutputClaim.Ours;
        if (!string.Equals(owner.Machine, myMachine, StringComparison.OrdinalIgnoreCase)) return OutputClaim.AnotherMachine;
        if (utcNow - owner.HeartbeatUtc > Forgotten) return OutputClaim.Stale;

        var sight = look(owner.Pid);
        if (sight.IsGoneOrReused(owner.StartedAtUtcTicks)) return OutputClaim.Stale;   // gone, and so are its windows; or the id handed out again

        return utcNow - owner.HeartbeatUtc > HeartbeatSilent
            ? OutputClaim.HeldByHungDesk
            : OutputClaim.HeldByLiveDesk;
    }

    /// <summary>
    /// What a sidecar read means: no file is nothing to take, a record is read as above, and a
    /// file that could not be read is <see cref="OutputClaim.Unknown"/> — the fence the rule
    /// "unreadable is a fence, not an absence" puts under the process probe, here under the file.
    /// </summary>
    public static OutputClaim Read(
        SidecarRead<OutputOwner> read,
        int myPid,
        string myMachine,
        DateTime utcNow,
        Func<int, ProcessSight> look)
        => read.IsUnreadable ? OutputClaim.Unknown : Read(read.Value, myPid, myMachine, utcNow, look);

    /// <summary>True when this desk should go and take the screens rather than open a second set beside them.</summary>
    public static bool ShouldTakeOver(OutputClaim claim)
        => claim is OutputClaim.HeldByLiveDesk or OutputClaim.HeldByHungDesk;

    /// <summary>The ask has gone unanswered long enough: the owner is not going to let go on its own.</summary>
    public static bool GraceExpired(DateTime askedUtc, DateTime utcNow) => utcNow - askedUtc >= HandoverGrace;

    /// <summary>
    /// A stand-down ask this desk should obey: another process asked for the screens, recently
    /// enough to still mean it, and it is not this desk asking itself.
    /// </summary>
    public static bool ShouldStandDown(HandoverRequest? request, int myPid, DateTime utcNow)
        => request is not null
           && request.Pid != myPid
           && utcNow - request.AskedUtc >= TimeSpan.Zero
           && utcNow - request.AskedUtc < Forgotten;

    /// <summary>What was on the screens, in words: "3 screens (Main wall, Stage left, Foyer)" — for the status line and the log.</summary>
    public static string TargetWords(IReadOnlyList<string>? targets)
    {
        if (targets is null || targets.Count == 0) return "the screens";
        var names = string.Join(", ", targets.Take(4));
        var more = targets.Count > 4 ? $" and {targets.Count - 4} more" : "";
        return $"{targets.Count} screen{(targets.Count == 1 ? "" : "s")} ({names}{more})";
    }

    /// <summary>The sentence the desk puts on its status line when it finds a record from a previous run.</summary>
    public static string Words(OutputClaim claim, OutputOwner? owner) => claim switch
    {
        OutputClaim.Free => "The screens are free.",
        OutputClaim.Ours => "This desk has the screens.",
        OutputClaim.Stale => "The last run's screens went with it — nothing left playing.",
        OutputClaim.HeldByLiveDesk =>
            $"Another Patterns (pid {owner?.Pid}) has {TargetWords(owner?.Targets)} — asking it for them.",
        OutputClaim.HeldByHungDesk =>
            $"The last run is still playing on {TargetWords(owner?.Targets)} but stopped answering — taking the screens back.",
        OutputClaim.AnotherMachine =>
            $"{owner?.Machine} has these screens — this desk leaves them alone.",
        OutputClaim.Unknown => UnknownWords(""),
        _ => "",
    };

    /// <summary>
    /// The sentence for a record that could not be read: nothing is opened by itself, and the
    /// operator is told how to open the screens once sure. The problem rides along when known.
    /// </summary>
    public static string UnknownWords(string problem)
        => $"The record of who has the screens could not be read{(problem.Length > 0 ? $" ({problem})" : "")} — this desk does not open them by itself. "
           + "OUTPUTS ON here once you are sure nothing else is playing on them.";

    /// <summary>What the desk says once it has the screens back.</summary>
    public static string TakenWords(OutputOwner owner, bool ended)
        => ended
            ? $"Took {TargetWords(owner.Targets)} back from the last run (pid {owner.Pid}, ended — it had stopped answering); the show is back on them."
            : $"Took {TargetWords(owner.Targets)} back from the last run (pid {owner.Pid}, which stood down); the show is back on them.";
}

/// <summary>What a read of a sidecar came to. Three answers, because the third means the opposite of the first to a fence: no file is nothing to take; a file that cannot be read may be a desk still playing.</summary>
public enum SidecarState
{
    Missing,
    Valid,
    Unreadable,
}

/// <summary>A sidecar read: the state, the record when there is one, and the problem when there is one.</summary>
public readonly record struct SidecarRead<T>(SidecarState State, T? Value, string Problem) where T : class
{
    public static readonly SidecarRead<T> Missing = new(SidecarState.Missing, null, "");

    public static SidecarRead<T> Valid(T value) => new(SidecarState.Valid, value, "");

    public static SidecarRead<T> Unreadable(string problem) => new(SidecarState.Unreadable, null, problem);

    public bool IsUnreadable => State == SidecarState.Unreadable;

    public bool IsMissing => State == SidecarState.Missing;

    public bool IsValid => State == SidecarState.Valid;
}

/// <summary>A sidecar write or clear: committed to disk, or not, with the reason. A caller that acts on a write acts on this, never on the call having returned.</summary>
public readonly record struct SidecarWrite(bool Committed, string Problem)
{
    public static readonly SidecarWrite Done = new(true, "");

    public static SidecarWrite Failed(string problem) => new(false, problem);
}

/// <summary>
/// The files under the sidecar store — a seam, so a folder that is read-only, a file that is
/// locked, a rename that fails and a share that has gone are each a test rather than a night.
/// </summary>
public interface ISidecarFiles
{
    bool DirectoryExists(string path);

    bool Exists(string path);

    string ReadAllText(string path);

    void WriteAllText(string path, string text);

    /// <summary>Moves a file over another — the atomic step every write ends with.</summary>
    void Move(string from, string to);

    void Delete(string path);
}

/// <summary>The real files.</summary>
public sealed class RealSidecarFiles : ISidecarFiles
{
    public static readonly RealSidecarFiles Instance = new();

    public bool DirectoryExists(string path) => Directory.Exists(path);

    public bool Exists(string path) => File.Exists(path);

    public string ReadAllText(string path) => File.ReadAllText(path);

    public void WriteAllText(string path, string text) => File.WriteAllText(path, text);

    public void Move(string from, string to) => File.Move(from, to, overwrite: true);

    public void Delete(string path) => File.Delete(path);
}

/// <summary>
/// The two tiny sidecars beside the settings that carry ownership between processes:
/// <c>patterns.outputs.json</c>, written by whoever has the screens, and
/// <c>patterns.handover.json</c>, written by a new desk asking for them. Atomic like the settings
/// store — a torn write must never make a live desk look like an orphan. Every read says whether
/// it could read, every write whether it committed: the record on disk decides who may put a
/// picture on the wall, so a call that returned is never taken for a fact it did not establish.
/// </summary>
public sealed class OutputOwnerStore
{
    private readonly string _directory;
    private readonly string _owner;
    private readonly string _handover;
    private readonly ISidecarFiles _files;

    public OutputOwnerStore(string directory, ISidecarFiles? files = null)
    {
        _directory = directory;
        _owner = Path.Combine(directory, "patterns.outputs.json");
        _handover = Path.Combine(directory, "patterns.handover.json");
        _files = files ?? RealSidecarFiles.Instance;
    }

    public string OwnerPath => _owner;

    public string RequestPath => _handover;

    public SidecarRead<OutputOwner> Read() => ReadFile<OutputOwner>(_owner);

    public SidecarWrite Write(OutputOwner owner) => WriteFile(_owner, owner);

    public SidecarWrite Clear() => Delete(_owner);

    public SidecarRead<HandoverRequest> ReadRequest() => ReadFile<HandoverRequest>(_handover);

    public SidecarWrite Ask(HandoverRequest request) => WriteFile(_handover, request);

    public SidecarWrite ClearRequest() => Delete(_handover);

    private SidecarRead<T> ReadFile<T>(string path) where T : class
    {
        try
        {
            // A folder that is not there is not a folder with no record in it: a share that has
            // gone, or a stick that was pulled, reads as unreadable, never as free.
            if (!_files.DirectoryExists(_directory)) return SidecarRead<T>.Unreadable("the show folder is not there");
            if (!_files.Exists(path)) return SidecarRead<T>.Missing;
            var value = JsonUtil.Deserialize<T>(_files.ReadAllText(path));
            return value is null ? SidecarRead<T>.Unreadable("the file holds no record") : SidecarRead<T>.Valid(value);
        }
        catch (Exception ex)
        {
            Log.Warn($"{Path.GetFileName(path)} unreadable.", ex);
            return SidecarRead<T>.Unreadable(ex.Message);
        }
    }

    private SidecarWrite WriteFile<T>(string path, T value)
    {
        var tmp = path + ".tmp";
        try
        {
            _files.WriteAllText(tmp, JsonUtil.Serialize(value));
            _files.Move(tmp, path);
            return SidecarWrite.Done;
        }
        catch (Exception ex)
        {
            Log.Warn($"{Path.GetFileName(path)} write failed.", ex);
            try { _files.Delete(tmp); } catch { /* the half-written file waits for the next write */ }
            return SidecarWrite.Failed(ex.Message);
        }
    }

    private SidecarWrite Delete(string path)
    {
        try
        {
            if (!_files.Exists(path)) return SidecarWrite.Done;
            _files.Delete(path);
            return SidecarWrite.Done;
        }
        catch (Exception ex)
        {
            // Locked, or a folder that will not let go: said, because a record that would not
            // clear is a record the next start reads.
            Log.Warn($"{Path.GetFileName(path)} could not be cleared.", ex);
            return SidecarWrite.Failed(ex.Message);
        }
    }
}
