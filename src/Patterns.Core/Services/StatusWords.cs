namespace Patterns.Core.Services;

/// <summary>
/// The words that make a service's status line read as a failure. A Requested cue row settles to
/// FailedLate when a watched service is saying one of these, so an <em>idle</em> status must never
/// contain one — only a status describing an attempt that actually failed.
/// </summary>
public static class StatusWords
{
    public static readonly IReadOnlyList<string> Failure =
        new[] { "fail", "error", "missing", "need", "not found", "unavailable", "could not" };

    public static bool ReadsAsFailure(string? status)
        => status is { Length: > 0 } &&
           Failure.Any(w => status.Contains(w, StringComparison.OrdinalIgnoreCase));
}
