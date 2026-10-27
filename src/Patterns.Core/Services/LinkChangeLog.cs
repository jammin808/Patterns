using System.Globalization;

namespace Patterns.Core.Services;

/// <summary>What one link writes to its journal and its log for a change of its record: a change of state, or a repeat's summary.</summary>
public readonly record struct LinkLogLine(bool Summary, string From, LinkRecord Record, string Words)
{
    /// <summary>A line a person should look at: the link is down, held or refused (13.16.1's red-when-needed states).</summary>
    public bool Warn => Record.State is "down" or "held" or "refused";
}

/// <summary>
/// Show Bus 1.3 §13.16.7 and §13.19.4: one journal row and one log line per change of a link's state — from, to, the reason and the
/// attempt — never per beat. A change is a repeat when the same change, from and to, was said within <c>log.repeat.summary.ms</c>: a
/// redial going round, a link flapping live and stale or live and down, its coming live again among them. A repeat is counted, not
/// said, and the repeats are said as one summary — that long after the first of them, or just before the next line, whichever comes
/// first — so the lines stay in the order the changes came and the summary's count is the changes not said since the line before it
/// ("still live — Hall A: 3 attempts, 2 changes since the last line, the last stale → live: …"). Each change a summary folds counts as
/// said when the summary is. One writer: the link's own caller, under its own gate; pure on the ms it is handed.
/// </summary>
public sealed class LinkChangeLog
{
    private readonly long _summaryMs;
    private readonly Dictionary<(string From, string To), long> _saidAt = new();
    private readonly HashSet<(string From, string To)> _repeated = new();
    private string _state;
    private int _repeats;
    private long _repeatSince = long.MinValue;
    private (string From, LinkRecord Record)? _repeatLast;

    /// <param name="startState">The state the link starts in, said by nothing: <c>off</c> for a link not yet configured.</param>
    /// <param name="summaryEvery">13.19.4's <c>log.repeat.summary.ms</c>; <see cref="PeerLink.RepeatSummary"/> unless a test hands another.</param>
    public LinkChangeLog(string startState = "off", TimeSpan? summaryEvery = null)
    {
        _state = startState;
        _summaryMs = (long)(summaryEvery ?? PeerLink.RepeatSummary).TotalMilliseconds;
    }

    /// <summary>The state the link was last seen in, said or counted; the start state before any.</summary>
    public string State => _state;

    /// <summary>Changes counted as repeats and not yet said in a summary.</summary>
    public int Repeats => _repeats;

    /// <summary>
    /// The lines <paramref name="record"/>, read at <paramref name="nowMs"/> on the link's monotonic clock, asks for: none while its
    /// state stands; for a change, its line — after the summary of the repeats still unsaid, when there are any — or, for a repeat,
    /// nothing; and the summary once <c>log.repeat.summary.ms</c> has passed since the first repeat it counts.
    /// </summary>
    public IReadOnlyList<LinkLogLine> Take(LinkRecord record, long nowMs)
    {
        var lines = new List<LinkLogLine>(2);
        if (!string.Equals(record.State, _state, StringComparison.Ordinal))
        {
            var change = (_state, record.State);
            var from = _state;
            _state = record.State;
            if (_saidAt.TryGetValue(change, out var said) && nowMs - said < _summaryMs)
            {
                _repeats++;
                if (_repeatSince == long.MinValue) _repeatSince = nowMs;                     // the first repeat starts the summary's wait
                _repeated.Add(change);
                _repeatLast = (from, record);
            }
            else
            {
                Fold(lines, nowMs, null);                                                    // the repeats before it, said first
                _saidAt[change] = nowMs;
                lines.Add(new LinkLogLine(false, from, record, record.ChangeWords(from)));
            }
        }
        if (_repeats > 0 && nowMs - _repeatSince >= _summaryMs) Fold(lines, nowMs, record);
        return lines;
    }

    /// <summary>
    /// The repeats still unsaid, as one summary naming the last of them: with <paramref name="now"/>, the record read as it is said
    /// (the link still in the state that change left it in); without, as it stood at that change. Each change it folds counts as said.
    /// </summary>
    private void Fold(List<LinkLogLine> lines, long nowMs, LinkRecord? now)
    {
        if (_repeats > 0 && _repeatLast is { } last)
        {
            var record = now ?? last.Record;
            lines.Add(new LinkLogLine(true, last.From, record, SummaryWords(record, _repeats, last.From, last.Record.State)));
            foreach (var change in _repeated) _saidAt[change] = nowMs;
        }
        _repeats = 0;
        _repeatSince = long.MinValue;
        _repeatLast = null;
        _repeated.Clear();
    }

    /// <summary>
    /// 13.19.4's summary: the state the link is still in, the peer, its attempts, the changes not said since the line before, and the
    /// last of them with its reason — the record's refusal class, else its words.
    /// </summary>
    public static string SummaryWords(LinkRecord record, int repeats, string lastFrom, string lastTo)
        => string.Create(CultureInfo.InvariantCulture,
            $"still {record.State} — {record.PeerName}: {record.Attempt} attempt{(record.Attempt == 1 ? "" : "s")}, {repeats} change{(repeats == 1 ? "" : "s")} since the last line, the last {lastFrom} → {lastTo}: {(record.Refusal.Length > 0 ? record.Refusal : record.Words)}");
}
