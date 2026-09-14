using Patterns.Core.Model;

namespace Patterns.Core.Services;

/// <summary>
/// What a box did lately — runtime alone, never in the show file: the last line sent to it and
/// when, the last reply and when, the highest confirmation it last gave, the last state it was
/// observed in, and the last failure and when. The card reads it with the ages, STATE carries
/// it, the assistant answers from it. Immutable: an event makes the next one.
/// </summary>
public sealed record DeviceRuntime(
    string LastCommand = "", DateTime? LastCommandUtc = null,
    string LastReply = "", DateTime? LastReplyUtc = null,
    ConfirmLevel? LastConfirmed = null, DateTime? LastConfirmedUtc = null,
    string LastObserved = "", DateTime? LastObservedUtc = null,
    string LastFailure = "", DateTime? LastFailureUtc = null)
{
    /// <summary>A box nothing has been said to yet.</summary>
    public static readonly DeviceRuntime None = new();

    /// <summary>A line went to the box — the operator's, a cue's, the wire's; not the show's facts, not the poll.</summary>
    public DeviceRuntime Sent(string line, DateTime utcNow) => this with { LastCommand = line, LastCommandUtc = utcNow };

    /// <summary>The box said something, in its profile's words.</summary>
    public DeviceRuntime Replied(string words, DateTime utcNow) => this with { LastReply = words, LastReplyUtc = utcNow };

    /// <summary>A receipt landed well: the level the box reached, and what it was observed to be when the level was Observed.</summary>
    public DeviceRuntime Confirmed(ConfirmLevel level, string answer, DateTime utcNow) => this with
    {
        LastConfirmed = level,
        LastConfirmedUtc = utcNow,
        LastObserved = level == ConfirmLevel.Observed && answer.Length > 0 ? answer : LastObserved,
        LastObservedUtc = level == ConfirmLevel.Observed && answer.Length > 0 ? utcNow : LastObservedUtc,
    };

    /// <summary>A receipt failed — a no, a silence, a line that could not go, a port that would not open.</summary>
    public DeviceRuntime Failed(string words, DateTime utcNow) => this with { LastFailure = words, LastFailureUtc = utcNow };

    /// <summary>Whether the box's last word was a failure: one more recent than any reply or confirmation since.</summary>
    public bool Failing => LastFailureUtc is { } f
        && (LastReplyUtc is not { } r || f > r)
        && (LastConfirmedUtc is not { } c || f > c);

    /// <summary>Whether anything has happened to this box.</summary>
    public bool Any => LastCommandUtc is not null || LastReplyUtc is not null || LastFailureUtc is not null;

    /// <summary>
    /// "Last sent POWER ON (3 s ago) · reply POWR: OK — accepted (3 s ago) · observed INPT: 31
    /// (1 min ago) · FAILED INPUT HDMI 2 — no answer in 2 s (5 min ago)"; "No line sent yet."
    /// before anything. A failure that is the box's last word is FAILED; one it has answered
    /// since is failed.
    /// </summary>
    public string Words(DateTime utcNow)
    {
        if (!Any) return "No line sent yet.";
        var parts = new List<string>(4);
        if (LastCommandUtc is { } c) parts.Add($"last sent {LastCommand} ({Age(utcNow - c)})");
        if (LastReplyUtc is { } r) parts.Add($"reply {LastReply}{(LastConfirmed is { } level ? " — " + DeviceConfirmation.Label(level) : "")} ({Age(utcNow - r)})");
        else if (LastConfirmed is { } alone && LastConfirmedUtc is { } at) parts.Add($"{DeviceConfirmation.Label(alone)} ({Age(utcNow - at)})");
        if (LastObservedUtc is { } o) parts.Add($"observed {LastObserved} ({Age(utcNow - o)})");
        if (LastFailureUtc is { } f) parts.Add($"{(Failing ? "FAILED" : "failed")} {LastFailure} ({Age(utcNow - f)})");
        var line = string.Join(" · ", parts);
        return char.ToUpperInvariant(line[0]) + line[1..];
    }

    /// <summary>"just now", "3 s ago", "2 min ago", "1 h ago", "2 d ago".</summary>
    public static string Age(TimeSpan since)
    {
        if (since < TimeSpan.FromSeconds(1)) return "just now";
        if (since < TimeSpan.FromMinutes(1)) return $"{(int)since.TotalSeconds} s ago";
        if (since < TimeSpan.FromHours(1)) return $"{(int)since.TotalMinutes} min ago";
        if (since < TimeSpan.FromDays(1)) return $"{(int)since.TotalHours} h ago";
        return $"{(int)since.TotalDays} d ago";
    }
}
