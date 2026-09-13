namespace Patterns.Core.Services;

/// <summary>Which way the show is moving between the twins.</summary>
public enum HandoverKind
{
    /// <summary>The standby takes the show from a main that has gone silent.</summary>
    TakeOver,

    /// <summary>The main takes the show back from a standby that ran it.</summary>
    TakeBack,
}

/// <summary>
/// Where the two desks are. On one machine the old owner's windows are the very displays the new
/// owner's will open on, so the old owner lets go first and the picture goes up after — a dark
/// instant, never two sets. Across machines the room looks at one of them through a switcher, so
/// the new owner's picture goes up first, the room is pointed at it, and only then does the old
/// owner let go — never a dark wall.
/// </summary>
public enum HandoverShape
{
    SameMachine,
    AcrossMachines,
}

/// <summary>The stages of a handover. Each is a fact established on the way, in the order the room needs them.</summary>
public enum HandoverStage
{
    /// <summary>The fences: the role, the peer on the link, the marker on disk, a hung process ended.</summary>
    Preparing,

    /// <summary>The show is on the next owner and its outputs are up (or held for the old owner to let go, on one machine).</summary>
    TargetReady,

    /// <summary>The wall-switch cue fired — the room was asked to look at the next owner.</summary>
    RouteRequested,

    /// <summary>The switch answered at the level its endpoint can — the room is looking at the next owner.</summary>
    RouteConfirmed,

    /// <summary>The next owner is the owner: the holder's state cleared, the marker gone, the phase moved.</summary>
    AuthorityCommitted,

    /// <summary>The old owner said it closed its outputs — RELEASED on the wire for this handover's id, or, on one machine, its marker gone or its process gone. Never the HANDBACK having been written: a line written is not a line read.</summary>
    OldOwnerReleased,

    Complete,
}

/// <summary>
/// One handover as a transaction: the stages its shape runs, in order, the stage it reached, and
/// why it stopped when it stopped short. The doing — landing a show, opening outputs, firing a
/// cue, writing HANDBACK — is the twin service's; this says what order the room needs it in and
/// keeps the trail, so a take-back that stopped at the route says so in words, and the next
/// press finishes the job rather than starting a different one. Its id names it on the wire: the
/// hand-back carries it and the standby's answer carries it back, so an answer is matched to the
/// ask it answers and a claim made under a takeover already taken back is known for what it is.
/// A stopped handover can resume when the fact it waited for arrives — the operator's word that
/// the room was switched, the standby's answer come late — and the trail keeps both. Pure: every
/// shape is a test.
/// </summary>
public sealed class TwinTransaction
{
    private readonly List<HandoverStage> _plan;
    private readonly List<HandoverStage> _reached = new();
    private readonly List<(int After, string Text)> _notes = new();
    private string _reason = "";

    public TwinTransaction(HandoverKind kind, HandoverShape shape, bool hasRoute, DateTime startedUtc, string id = "")
    {
        Kind = kind;
        Shape = shape;
        HasRoute = hasRoute;
        StartedUtc = startedUtc;
        Id = id.Length > 0 ? id : NewId();
        _plan = Steps(kind, shape, hasRoute).ToList();
        _reached.Add(HandoverStage.Preparing);
    }

    /// <summary>Twelve hex characters, drawn at random: what HANDBACK and RELEASED carry, and the marker names.</summary>
    public static string NewId()
    {
        var bytes = new byte[6];
        System.Security.Cryptography.RandomNumberGenerator.Fill(bytes);
        return Convert.ToHexString(bytes).ToLowerInvariant();
    }

    /// <summary>The handover's name on the wire and in the marker.</summary>
    public string Id { get; }

    public HandoverKind Kind { get; }

    public HandoverShape Shape { get; }

    /// <summary>A wall-switch cue is set: the room is asked to look at the next owner, and answers.</summary>
    public bool HasRoute { get; }

    public DateTime StartedUtc { get; }

    /// <summary>The stages this shape runs, in order — the contract the service keeps.</summary>
    public IReadOnlyList<HandoverStage> Plan => _plan;

    /// <summary>The stages established so far, in order.</summary>
    public IReadOnlyList<HandoverStage> ReachedStages => _reached;

    public HandoverStage Stage => _reached[^1];

    /// <summary>Why it stopped short, or "".</summary>
    public string Reason => _reason;

    public bool Stopped => _reason.Length > 0;

    public bool IsComplete => Stage == HandoverStage.Complete;

    /// <summary>The next stage the plan asks for, or null once complete.</summary>
    public HandoverStage? Next
    {
        get
        {
            var i = _plan.IndexOf(Stage);
            return i >= 0 && i + 1 < _plan.Count ? _plan[i + 1] : null;
        }
    }

    /// <summary>
    /// The order each shape needs. Across machines the room is pointed at a picture that is
    /// already up, and the old owner lets go last; on one machine the old owner lets go first,
    /// because its windows are the displays the new ones open on. A takeover has no old owner to
    /// release — the main is silent — and its route fires before its outputs open: the room is
    /// looking at a desk that has stopped, so a dark instant costs nothing, and a route that is
    /// refused leaves this desk exactly as it was.
    /// </summary>
    public static IEnumerable<HandoverStage> Steps(HandoverKind kind, HandoverShape shape, bool hasRoute)
    {
        yield return HandoverStage.Preparing;
        var route = hasRoute && shape == HandoverShape.AcrossMachines;
        switch (kind, shape)
        {
            case (HandoverKind.TakeBack, HandoverShape.AcrossMachines):
                yield return HandoverStage.TargetReady;
                if (route) { yield return HandoverStage.RouteRequested; yield return HandoverStage.RouteConfirmed; }
                yield return HandoverStage.AuthorityCommitted;
                yield return HandoverStage.OldOwnerReleased;
                break;
            case (HandoverKind.TakeBack, HandoverShape.SameMachine):
                yield return HandoverStage.AuthorityCommitted;
                yield return HandoverStage.OldOwnerReleased;
                yield return HandoverStage.TargetReady;
                break;
            case (HandoverKind.TakeOver, HandoverShape.AcrossMachines):
                if (route) { yield return HandoverStage.RouteRequested; yield return HandoverStage.RouteConfirmed; }
                yield return HandoverStage.AuthorityCommitted;
                yield return HandoverStage.TargetReady;
                break;
            default:
                yield return HandoverStage.AuthorityCommitted;
                yield return HandoverStage.TargetReady;
                break;
        }
        yield return HandoverStage.Complete;
    }

    /// <summary>A stage established. It must be the plan's next one: a service that skips a stage has skipped a fact the room needs.</summary>
    public void Reached(HandoverStage stage)
    {
        if (Stopped) throw new InvalidOperationException($"The handover stopped at {Label(Stage)}: {_reason}");
        if (Next != stage) throw new InvalidOperationException($"After {Label(Stage)} the plan asks for {(Next is { } n ? Label(n) : "nothing")}, not {Label(stage)}.");
        _reached.Add(stage);
    }

    /// <summary>Stopped short at the stage reached, for a reason the words carry.</summary>
    public void Stop(string reason)
    {
        if (IsComplete) throw new InvalidOperationException("A complete handover cannot stop.");
        _reason = reason.Length > 0 ? reason : "stopped";
    }

    /// <summary>
    /// The fact a stopped handover waited for arrived — the operator switched the room by hand, the
    /// standby answered late, the standby was told again — so it goes on from the stage it reached.
    /// The trail keeps the stop and the resumption both.
    /// </summary>
    public void Resume(string how)
    {
        if (!Stopped) throw new InvalidOperationException("Only a stopped handover resumes.");
        _notes.Add((_reached.Count - 1, $"stopped: {_reason} → resumed: {(how.Length > 0 ? how : "the fact arrived")}"));
        _reason = "";
    }

    /// <summary>A fact worth the trail at the stage reached, without stopping: "no box answered — the operator's press".</summary>
    public void Note(string text)
    {
        if (text.Length > 0) _notes.Add((_reached.Count - 1, text));
    }

    /// <summary>"take back across machines: target ready → route requested → stopped: …" — for the log and TWIN STATUS.</summary>
    public string Trail
    {
        get
        {
            var head = $"{(Kind == HandoverKind.TakeBack ? "take back" : "take over")} {(Shape == HandoverShape.AcrossMachines ? "across machines" : "on this machine")}{(Shape == HandoverShape.AcrossMachines && !HasRoute ? " (no wall-switch cue)" : "")}: ";
            var parts = new List<string>();
            for (var i = 1; i < _reached.Count; i++)
            {
                var notes = _notes.Where(n => n.After == i).Select(n => n.Text).ToList();
                parts.Add(Label(_reached[i]) + (notes.Count > 0 ? " (" + string.Join("; ", notes) + ")" : ""));
            }
            var steps = parts.Count > 0 ? string.Join(" → ", parts) : Label(HandoverStage.Preparing) + (_notes.Count > 0 ? " (" + string.Join("; ", _notes.Where(n => n.After == 0).Select(n => n.Text)) + ")" : "");
            return head + steps + (Stopped ? $" → stopped: {_reason}" : "");
        }
    }

    public static string Label(HandoverStage stage) => stage switch
    {
        HandoverStage.Preparing => "preparing",
        HandoverStage.TargetReady => "target ready",
        HandoverStage.RouteRequested => "route requested",
        HandoverStage.RouteConfirmed => "route confirmed",
        HandoverStage.AuthorityCommitted => "authority committed",
        HandoverStage.OldOwnerReleased => "old owner released",
        HandoverStage.Complete => "complete",
        _ => stage.ToString(),
    };

    /// <summary>
    /// The sentence for a take-back that stopped at the route: the room still shows the standby,
    /// whose picture is up — switch by hand, then TAKE BACK again finishes it.
    /// </summary>
    public static string RouteStoppedWords(string standby, string cue, string reason)
        => $"TAKE BACK stopped at the wall switch: cue '{cue}' {reason}. The room still shows the standby {standby}, whose picture stays up — switch the wall to this desk by hand, then TAKE BACK again finishes the hand-back.";

    /// <summary>
    /// The sentence for a take-back across machines whose route is the operator's own: the show is
    /// on here, on displays the room is not yet looking at; the room is switched by hand; the
    /// second press releases the standby. Nothing infers that the switch was thrown.
    /// </summary>
    public static string SwitchByHandWords(string standby, string why)
        => $"TAKE BACK: the show is on here, on displays the room is not yet looking at. {why} — switch the room to this desk by hand, then TAKE BACK again releases the standby {standby}, whose picture stays up until then.";

    /// <summary>The words while the standby's answer to the hand-back is awaited.</summary>
    public static string ReleaseAwaitedWords(string standby, bool sameMachine)
        => sameMachine
            ? $"TAKE BACK: the standby {standby} was told to let go — the picture goes up here the moment it says it has."
            : $"TAKE BACK: the room shows this desk; the standby {standby} was told to let go and its answer is awaited.";

    /// <summary>
    /// The words for a hand-back the standby never answered. On one machine this desk's outputs stay
    /// held — the standby's windows may still be up on these very displays; across machines the room
    /// already shows this desk and the standby may still have its picture up on its own input.
    /// </summary>
    public static string ReleaseStoppedWords(string standby, bool sameMachine)
        => sameMachine
            ? $"TAKE BACK stopped: the standby {standby} did not say it let go, and its windows may still be up on these displays, so this desk's outputs stay held. It is told again when it links, or by TAKE BACK again; a standby whose process has gone is seen by its marker and the show is put back here by itself."
            : $"TAKE BACK stopped short: the room shows this desk, but the standby {standby} did not say it let go — its picture may still be up on its own input. It is told again when it links, or by TAKE BACK again.";
}
