namespace Patterns.Core.Services;

/// <summary>
/// Where the app's memory is, by owner, in bytes — the pictures cached, the frame pools, the
/// frames held for fades, the decks' pages, the thumbnails, the managed heap and what the runtime
/// has committed — as a game engine's memory screen reads it. Every owner registers a reader
/// once; the ledger asks them when the Machine page, STATE, the CSV or the assistant asks. The
/// working set the ceiling is judged against is the sum of these and what the runtime, Skia,
/// the browser and the drivers hold besides; a climb the ledger cannot place is a leak in what
/// it does not see. Pure; the readers are the owners'.
/// </summary>
public static class MemoryLedger
{
    public sealed record Line(string Owner, long Bytes, string Detail = "")
    {
        /// <summary>"pictures 84 MB (3 cached)".</summary>
        public string Words => $"{Owner} {MemoryBudget.Mb(Bytes / (1024.0 * 1024.0))}{(Detail.Length > 0 ? $" ({Detail})" : "")}";
    }

    private static readonly object Gate = new();
    private static readonly List<(string Owner, Func<(long Bytes, string Detail)> Read)> Owners = new();

    /// <summary>An owner joins (or replaces its reader): the reader answers in bytes, with a detail for the words.</summary>
    public static void Register(string owner, Func<(long Bytes, string Detail)> read)
    {
        lock (Gate)
        {
            Owners.RemoveAll(o => o.Owner == owner);
            Owners.Add((owner, read));
        }
    }

    public static void Unregister(string owner)
    {
        lock (Gate)
        {
            Owners.RemoveAll(o => o.Owner == owner);
        }
    }

    /// <summary>Every owner's line, in the order they registered; a reader that throws reads as nought.</summary>
    public static IReadOnlyList<Line> Read()
    {
        List<(string Owner, Func<(long, string)> Read)> owners;
        lock (Gate)
        {
            owners = new List<(string, Func<(long, string)>)>(Owners);
        }
        var lines = new List<Line>(owners.Count);
        foreach (var (owner, read) in owners)
        {
            try
            {
                var (bytes, detail) = read();
                lines.Add(new Line(owner, Math.Max(0, bytes), detail));
            }
            catch
            {
                lines.Add(new Line(owner, 0, "no reading"));
            }
        }
        return lines;
    }

    /// <summary>The owners' bytes together.</summary>
    public static long Total()
    {
        long sum = 0;
        foreach (var l in Read()) sum += l.Bytes;
        return sum;
    }

    /// <summary>"pictures 84 MB (3 cached) · frame pools 116 MB (2 sources) · frames held 8 MB · managed heap 61 MB" — the owners that hold anything, largest first.</summary>
    public static string Describe(IReadOnlyList<Line>? lines = null)
    {
        lines ??= Read();
        var parts = new List<string>();
        foreach (var l in lines.OrderByDescending(l => l.Bytes))
        {
            if (l.Bytes <= 0) continue;
            parts.Add(l.Words);
        }
        return parts.Count == 0 ? "nothing placed yet" : string.Join(" · ", parts);
    }

    /// <summary>Tests: no owners.</summary>
    public static void ResetForTests()
    {
        lock (Gate)
        {
            Owners.Clear();
        }
    }
}
