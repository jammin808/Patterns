using System.Globalization;
using System.Xml.Linq;
using Patterns.Core.Model;
using Patterns.Core.Play;

namespace Patterns.Core.Services;

/// <summary>
/// Turns feed documents into ticker items. Pure string-in/items-out (no IO) — the app
/// service does the fetching/refresh. Supports RSS 2.0 / Atom titles, plain-text or CSV
/// lines, and ICS calendars ("HH:mm Summary" for upcoming events).
/// </summary>
public static class FeedParser
{
    public static FeedKind Detect(string content, string sourceNameHint)
    {
        var hint = sourceNameHint.ToLowerInvariant();
        if (hint.EndsWith(".ics", StringComparison.Ordinal)) return FeedKind.Ics;
        if (hint.EndsWith(".csv", StringComparison.Ordinal) || hint.EndsWith(".txt", StringComparison.Ordinal)) return FeedKind.Csv;
        if (hint.EndsWith(".rss", StringComparison.Ordinal) || hint.EndsWith(".xml", StringComparison.Ordinal) || hint.EndsWith(".atom", StringComparison.Ordinal)) return FeedKind.Rss;

        var head = content.TrimStart();
        if (head.StartsWith("BEGIN:VCALENDAR", StringComparison.OrdinalIgnoreCase)) return FeedKind.Ics;
        if (head.StartsWith('<')) return FeedKind.Rss;
        return FeedKind.Csv;
    }

    /// <summary>The most bytes a feed may be, over the wire or from a file: a document past it is refused before it is read whole (round 83).</summary>
    public const long MaxBytes = 2 * 1024 * 1024;

    public static IReadOnlyList<string> Parse(string content, FeedKind kind, string sourceNameHint, DateTime localNow, int maxItems)
        => Parse(content, kind, sourceNameHint, localNow, maxItems, out _);

    /// <summary>As above, with what went wrong when nothing parsed: the exception's type and first line, never a message the size of the document (round 83).</summary>
    public static IReadOnlyList<string> Parse(string content, FeedKind kind, string sourceNameHint, DateTime localNow, int maxItems, out string problem)
    {
        problem = "";
        if (kind == FeedKind.Auto) kind = Detect(content, sourceNameHint);
        try
        {
            var items = kind switch
            {
                FeedKind.Rss => ParseRssOrAtom(content),
                FeedKind.Ics => ParseIcs(content, localNow),
                _ => ParseLines(content),
            };
            return items.Where(s => !string.IsNullOrWhiteSpace(s)).Take(Math.Max(1, maxItems)).ToList();
        }
        catch (Exception ex)
        {
            problem = Faults.Brief(ex);
            Log.Warn($"Feed parse failed: {problem}");
            return Array.Empty<string>();
        }
    }

    /// <summary>Round 83: the items a listed word keeps off the wall, counted, and the rest — the ticker meets the room's word list as a phone's answer does.</summary>
    public static (IReadOnlyList<string> Kept, int HeldBack) Moderate(IReadOnlyList<string> items, IReadOnlyList<string> words)
    {
        var kept = new List<string>(items.Count);
        var held = 0;
        foreach (var item in items)
        {
            if (WordList.Matches(item, words)) held++;
            else kept.Add(item);
        }
        return (kept, held);
    }

    public static string Join(IReadOnlyList<string> items, string separator)
        => string.Join(string.IsNullOrEmpty(separator) ? "   •   " : separator, items);

    private static List<string> ParseRssOrAtom(string content)
    {
        var doc = XDocument.Parse(content);
        var items = new List<string>();

        // RSS 2.0: rss/channel/item/title
        foreach (var item in doc.Descendants().Where(e => e.Name.LocalName == "item"))
        {
            var title = item.Elements().FirstOrDefault(e => e.Name.LocalName == "title")?.Value;
            if (title is not null) items.Add(Clean(title));
        }

        // Atom: feed/entry/title
        if (items.Count == 0)
        {
            foreach (var entry in doc.Descendants().Where(e => e.Name.LocalName == "entry"))
            {
                var title = entry.Elements().FirstOrDefault(e => e.Name.LocalName == "title")?.Value;
                if (title is not null) items.Add(Clean(title));
            }
        }

        return items;
    }

    private static List<string> ParseLines(string content)
    {
        return content
            .Split('\n')
            .Select(l => Clean(l.Replace(",", "  ·  ")))
            .Where(l => l.Length > 0 && !l.StartsWith('#'))
            .ToList();
    }

    private static List<string> ParseIcs(string content, DateTime localNow)
    {
        // Unfold folded lines (RFC 5545: continuation lines start with space/tab).
        var unfolded = content.Replace("\r\n ", "").Replace("\r\n\t", "").Replace("\n ", "").Replace("\n\t", "");
        var lines = unfolded.Split('\n').Select(l => l.TrimEnd('\r')).ToList();

        var events = new List<(DateTime Start, string Summary)>();
        string? summary = null;
        DateTime? start = null;
        var inEvent = false;

        foreach (var line in lines)
        {
            if (line.StartsWith("BEGIN:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                inEvent = true;
                summary = null;
                start = null;
            }
            else if (line.StartsWith("END:VEVENT", StringComparison.OrdinalIgnoreCase))
            {
                if (inEvent && start is { } s && summary is not null)
                {
                    events.Add((s, summary));
                }
                inEvent = false;
            }
            else if (inEvent)
            {
                if (line.StartsWith("SUMMARY", StringComparison.OrdinalIgnoreCase))
                {
                    var idx = line.IndexOf(':');
                    if (idx >= 0) summary = Clean(line[(idx + 1)..].Replace("\\,", ",").Replace("\\n", " "));
                }
                else if (line.StartsWith("DTSTART", StringComparison.OrdinalIgnoreCase))
                {
                    var idx = line.IndexOf(':');
                    if (idx >= 0 && TryParseIcsDate(line[(idx + 1)..].Trim(), out var dt)) start = dt;
                }
            }
        }

        // Upcoming (or in-progress within the hour) events over the next 24 h, soonest first.
        return events
            .Where(e => e.Start >= localNow.AddHours(-1) && e.Start <= localNow.AddHours(24))
            .OrderBy(e => e.Start)
            .Select(e => $"{e.Start.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture)}  {e.Summary}")
            .ToList();
    }

    public static bool TryParseIcsDate(string value, out DateTime result)
    {
        result = default;
        // 20260830T183000Z / 20260830T183000 / 20260830
        var s = value.Trim();
        var utc = s.EndsWith("Z", StringComparison.OrdinalIgnoreCase);
        if (utc) s = s[..^1];

        string[] formats = { "yyyyMMdd'T'HHmmss", "yyyyMMdd'T'HHmm", "yyyyMMdd" };
        foreach (var f in formats)
        {
            // Invariant on purpose: an ICS date is Gregorian by definition, and a null provider reads
            // it in the machine's own calendar — Buddhist Thai Windows put every event five centuries out.
            if (DateTime.TryParseExact(s, f, System.Globalization.CultureInfo.InvariantCulture, System.Globalization.DateTimeStyles.None, out var dt))
            {
                result = utc ? DateTime.SpecifyKind(dt, DateTimeKind.Utc).ToLocalTime() : dt;
                return true;
            }
        }
        return false;
    }

    private static string Clean(string s) => s.Replace('\r', ' ').Replace('\n', ' ').Trim();
}
