using System.Globalization;

namespace Patterns.Core.Services;

/// <summary>
/// The desk thread's stall distribution as the wire sees it (the plan's lab line LP-1): for every command the wire ran through the
/// desk's thread, how long after the line was read the thread took it. A measurement and nothing more — no command is refused, held
/// or reordered by it. Show Bus 1.3 §13.10.5 and §13.12.5 offer <c>stuck</c> and <c>expire</c> only once an acceptor's <c>stuckMs</c> and
/// <c>expireMs</c> come from a measured stall of its own show writer; this desk offers neither, and this is the record a later round
/// reads to set them. Counted in fixed buckets, so it costs a few integers whatever the run's length. Any thread may add; any reads.
/// </summary>
public sealed class DeskStall
{
    /// <summary>Each bucket's upper bound in milliseconds; a sample past the last is counted in one more.</summary>
    public static IReadOnlyList<long> Bounds { get; } = new long[] { 16, 50, 100, 250, 1000, 5000 };

    private readonly long[] _counts = new long[Bounds.Count + 1];
    private long _max;
    private long _late;

    /// <summary>One command: <paramref name="waitedMs"/> from its read to the desk thread taking it (a negative wait counts as none).</summary>
    public void Add(long waitedMs)
    {
        var ms = Math.Max(0, waitedMs);
        var i = 0;
        while (i < Bounds.Count && ms > Bounds[i]) i++;
        Interlocked.Increment(ref _counts[i]);
        if (ms > (long)PeerLink.ReceiptLate.TotalMilliseconds) Interlocked.Increment(ref _late);
        long seen;
        while (ms > (seen = Interlocked.Read(ref _max)) && Interlocked.CompareExchange(ref _max, ms, seen) != seen)
        {
            // Another thread raised the longest first: read it again.
        }
    }

    /// <summary>The distribution now.</summary>
    public DeskStallFacts Snapshot()
        => new(Enumerable.Range(0, _counts.Length).Select(i => Interlocked.Read(ref _counts[i])).ToArray(), Interlocked.Read(ref _max), Interlocked.Read(ref _late));
}

/// <summary>The desk thread's stall distribution at one moment: the count per bucket of <see cref="DeskStall.Bounds"/>, the longest wait and how many ran late.</summary>
public sealed record DeskStallFacts(IReadOnlyList<long> Counts, long MaxMs, long Late)
{
    /// <summary>None measured.</summary>
    public static DeskStallFacts None { get; } = new(new long[DeskStall.Bounds.Count + 1], 0, 0);

    /// <summary>How many commands were measured.</summary>
    public long Total => Counts.Sum();

    /// <summary>
    /// The bucket bound within which <paramref name="share"/> of the commands were taken (the longest wait for the last bucket); 0 with
    /// none measured.
    /// </summary>
    public long Within(double share)
    {
        var total = Total;
        if (total == 0) return 0;
        var want = (long)Math.Ceiling(total * share);
        long seen = 0;
        for (var i = 0; i < Counts.Count; i++)
        {
            seen += Counts[i];
            if (seen >= want) return i < DeskStall.Bounds.Count ? Math.Min(DeskStall.Bounds[i], MaxMs) : MaxMs;
        }
        return MaxMs;
    }

    /// <summary>One line: how many, half and 99 in 100 within what, the longest, and how many ran past <c>receipt.late.ms</c>.</summary>
    public string Words => Total == 0
        ? "no command from the wire measured yet"
        : string.Create(CultureInfo.InvariantCulture,
            $"{Total} wire command{(Total == 1 ? "" : "s")} taken: half within {Within(0.5)} ms, 99 in 100 within {Within(0.99)} ms, the longest {MaxMs} ms after its read; {Late} ran more than {(long)PeerLink.ReceiptLate.TotalMilliseconds} ms late (lateMs)");
}
