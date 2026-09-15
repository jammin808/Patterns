namespace Patterns.Core.Services;

/// <summary>
/// The lifetime census (round 64): a count of everything the process holds that a desk, a node
/// or a surface should have released when it closed — desks alive after a collection, nodes,
/// render seats (one per pipeline), open frames, frame budgets, frame pools live and retiring,
/// retired frames, cached pictures, ledger owners, input mounts, video sources. A green functional
/// suite proves nothing about lifetime; this does: read after every closed boot in the tests,
/// on STATE and in the support ticket on the desk, and compared against a baseline — a count
/// that grows with the number of boots names what stayed rooted (round 62's RUN monitor tile
/// would have shown as one seat and one budget per boot).
/// </summary>
public sealed record LifetimeCensus(IReadOnlyList<(string Name, long Count)> Rows)
{
    /// <summary>A row's count; -1 for a name the census does not carry.</summary>
    public long this[string name]
    {
        get
        {
            foreach (var (n, c) in Rows) if (n == name) return c;
            return -1;
        }
    }

    /// <summary>The rows that differ from a baseline, in words: "pipelines: was 0, now 1".</summary>
    public IReadOnlyList<string> Differences(LifetimeCensus baseline)
    {
        var lines = new List<string>();
        foreach (var (name, count) in Rows)
        {
            var was = baseline[name];
            if (was != count) lines.Add($"{name}: was {was}, now {count}");
        }
        return lines;
    }

    /// <summary>One line: "desks 1 · nodes 0 · pipelines 3 · …".</summary>
    public string Describe() => string.Join(" · ", Rows.Select(r => $"{r.Name} {r.Count}"));

    /// <summary>The rows as a dictionary, for STATE.</summary>
    public Dictionary<string, long> ToDictionary()
    {
        var d = new Dictionary<string, long>();
        foreach (var (name, count) in Rows) d[name] = count;
        return d;
    }
}
