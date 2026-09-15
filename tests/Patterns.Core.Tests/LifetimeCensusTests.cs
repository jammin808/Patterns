using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>Round 64: the census reads, differs and describes; the ledger counts its owners.</summary>
public class LifetimeCensusTests
{
    [Fact]
    public void TheCensusNamesWhatDiffersFromABaselineAndTheLedgerCountsItsOwners()
    {
        var baseline = new LifetimeCensus(new (string, long)[] { ("desks", 0), ("pipelines", 0), ("pictures", 3) });
        var later = new LifetimeCensus(new (string, long)[] { ("desks", 1), ("pipelines", 0), ("pictures", 3) });
        Assert.Empty(baseline.Differences(baseline));
        Assert.Equal("desks: was 0, now 1", Assert.Single(later.Differences(baseline)));
        Assert.Equal("desks 1 · pipelines 0 · pictures 3", later.Describe());
        Assert.Equal(3, later["pictures"]);
        Assert.Equal(-1, later["nobody"]);
        Assert.Equal(1, later.ToDictionary()["desks"]);

        MemoryLedger.ResetForTests();
        Assert.Equal(0, MemoryLedger.Count);
        MemoryLedger.Register("a", () => (1, ""));
        MemoryLedger.Register("b", () => (2, ""));
        MemoryLedger.Register("a", () => (3, ""));                          // replaced, not doubled
        Assert.Equal(2, MemoryLedger.Count);
        MemoryLedger.Unregister("a");
        Assert.Equal(1, MemoryLedger.Count);
        MemoryLedger.ResetForTests();
    }
}
