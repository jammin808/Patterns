using Patterns.Core.Model;

namespace Patterns.Core.Services;

public enum CueOutcome
{
    Done,
    /// <summary>Every action accepted; something asynchronous (a stream, audio, a clip) is still settling.</summary>
    Requested,
    DoneWithWarnings,
    Failed,
    /// <summary>Accepted at GO, failed later — the encoder, the audio device or the decoder said so.</summary>
    FailedLate,
    Refused,
    Skipped,
}

/// <summary>
/// One run of a cue, named: the id a later receipt settles, how many device receipts it is
/// waiting for, and whether something else asynchronous (a stream, a clip, break music) is
/// still settling. Made by the runner, carried on the result, kept on the history row.
/// </summary>
public sealed record CueExecution(string Id, int DevicePending, bool Settling)
{
    /// <summary>A fresh execution id: short, unique enough for a show's history, never a cue's own id.</summary>
    public static string NewId() => Guid.NewGuid().ToString("N")[..8];
}

/// <summary>
/// One row of the caller's history — and of the sidecar, so a relaunch keeps the place. The row
/// carries its execution: a device's receipt landing later settles this row and no other, so a
/// box saying no to cue 21 never marks cue 22, and a box saying nothing marks the cue that sent
/// to it as FailedLate when its timeout runs out.
/// </summary>
public sealed record CueExecutionRecord(
    DateTime AtUtc, string CueId, string Number, string Name, CueOutcome Outcome, string Origin,
    int ActionsDone, int ActionsTotal, string Detail,
    string ExecutionId = "", int Pending = 0, bool Settling = false)
{
    public string Label => $"{Number} {Name}";
    public DateTime AtLocal => AtUtc.ToLocalTime();
    public string TimeText => AtLocal.ToString("HH:mm:ss");
    public bool IsFailure => Outcome is CueOutcome.Failed or CueOutcome.FailedLate or CueOutcome.Refused;

    /// <summary>The outcome as the Run page reads it: "Done", "Awaiting 2 receipts", "Settling", "Failed late".</summary>
    public string OutcomeWords => Outcome switch
    {
        CueOutcome.Requested when Pending > 0 => $"Awaiting {Pending} receipt{(Pending == 1 ? "" : "s")}",
        CueOutcome.Requested => "Settling",
        CueOutcome.FailedLate => "Failed late",
        CueOutcome.DoneWithWarnings => "Done, with warnings",
        _ => Outcome.ToString(),
    };
}

/// <summary>What the gate decided for one GO.</summary>
public enum GoDecision
{
    Fire,
    /// <summary>The cue asks for confirmation: the runtime arms a confirm window instead of firing.</summary>
    Confirm,
    Refuse,
}

/// <summary>
/// The one gate every GO passes, in order, whatever its origin: armed; not held; blackout not
/// on; not already executing; a standby cue set; the standby id the sender saw matches; the
/// lockout since the last accepted GO has passed; confirmation satisfied. Pure, so the order
/// and every refusal are unit tested.
/// </summary>
public static class GoGate
{
    public static readonly TimeSpan Lockout = TimeSpan.FromMilliseconds(300);
    public static readonly TimeSpan ConfirmWindow = TimeSpan.FromSeconds(4);

    public sealed record Inputs(
        bool Armed,
        bool Held,
        bool Blackout,
        bool Executing,
        string? StandbyCueId,
        string? SeenStandbyCueId,
        DateTime? LastGoUtc,
        DateTime NowUtc,
        bool RequireConfirm,
        string? ConfirmPendingCueId,
        DateTime? ConfirmDeadlineUtc);

    public static (GoDecision Decision, string Reason) Check(Inputs i)
    {
        if (!i.Armed) return (GoDecision.Refuse, "not armed");
        if (i.Held) return (GoDecision.Refuse, "held");
        if (i.Blackout) return (GoDecision.Refuse, "blackout is on — lift it first");
        if (i.Executing) return (GoDecision.Refuse, "a cue is still executing");
        if (i.StandbyCueId is null) return (GoDecision.Refuse, "no cue on standby");
        if (i.SeenStandbyCueId is not null && i.SeenStandbyCueId != i.StandbyCueId) return (GoDecision.Refuse, "standby moved");
        if (i.LastGoUtc is { } last && i.NowUtc - last < Lockout) return (GoDecision.Refuse, "too soon after the last GO");
        if (i.RequireConfirm)
        {
            var confirmed = i.ConfirmPendingCueId == i.StandbyCueId && i.ConfirmDeadlineUtc is { } deadline && i.NowUtc <= deadline;
            if (!confirmed) return (GoDecision.Confirm, "confirm");
        }
        return (GoDecision.Fire, "");
    }
}
