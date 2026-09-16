namespace Patterns.Core.Services;

/// <summary>
/// Round 70: every regular expression the desk runs over text it did not write — the wire, a deck, a
/// page, a feed, a file name — carries this match timeout (S6444). A pattern that backtracks
/// catastrophically on one hostile line is otherwise a hang for the length of the input; a quarter of a
/// second is longer than any of the desk's patterns needs on any of its inputs and shorter than an
/// operator notices.
/// </summary>
public static class SafeRegex
{
    /// <summary>The match timeout every Regex call names.</summary>
    public static readonly TimeSpan Timeout = TimeSpan.FromMilliseconds(250);
}
