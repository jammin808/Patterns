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

/// <summary>One row of the caller's history — and of the sidecar, so a relaunch keeps the place.</summary>
public sealed record CueExecutionRecord(
    DateTime AtUtc, string CueId, string Number, string Name, CueOutcome Outcome, string Origin,
    int ActionsDone, int ActionsTotal, string Detail)
{
    public string Label => $"{Number} {Name}";
    public DateTime AtLocal => AtUtc.ToLocalTime();
    public string TimeText => AtLocal.ToString("HH:mm:ss");
    public bool IsFailure => Outcome is CueOutcome.Failed or CueOutcome.FailedLate or CueOutcome.Refused;
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
