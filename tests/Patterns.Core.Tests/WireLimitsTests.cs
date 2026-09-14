using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>The wire's ceilings and the ledger under them: one rule for the audience's door, the wire's and the web remote's.</summary>
public class WireLimitsTests
{
    [Fact]
    public void TheLedgerAdmitsUnderBothCeilingsAndCountsDownOnRelease()
    {
        var ledger = new ConnectionLedger();
        Assert.True(ledger.TryAdmit("10.0.0.5", 3, 2));
        Assert.True(ledger.TryAdmit("10.0.0.5", 3, 2));
        Assert.False(ledger.TryAdmit("10.0.0.5", 3, 2));              // the address's ceiling
        Assert.True(ledger.TryAdmit("10.0.0.6", 3, 2));
        Assert.False(ledger.TryAdmit("10.0.0.7", 3, 2));              // the port's ceiling
        Assert.Equal(3, ledger.Open);
        Assert.Equal(2, ledger.From("10.0.0.5"));
        ledger.Release("10.0.0.5");
        Assert.Equal(1, ledger.From("10.0.0.5"));
        Assert.True(ledger.TryAdmit("10.0.0.7", 3, 2));
        ledger.Release("10.0.0.9");                                    // never counted: nothing changes, nothing goes below zero
        Assert.Equal(3, ledger.Open);
        ledger.Release("10.0.0.5");
        ledger.Release("10.0.0.6");
        ledger.Release("10.0.0.7");
        Assert.Equal(0, ledger.Open);
        Assert.Equal(0, ledger.From("10.0.0.5"));
        ledger.Release("10.0.0.5");
        Assert.Equal(0, ledger.Open);
    }

    [Fact]
    public void TheWordsNameTheCeilingReachedAndTheDefaultsAreSane()
    {
        var limits = WireLimits.Default;
        Assert.True(limits.MaxClientsPerAddress <= limits.MaxClients);
        Assert.True(limits.MaxHttpClientsPerAddress <= limits.MaxHttpClients);
        Assert.True(limits.LineSeconds > 0);
        Assert.Equal($"busy — {limits.MaxClientsPerAddress} connections already open from this address; close one first", limits.BusyWords(limits.MaxClientsPerAddress));
        Assert.Equal($"busy — {limits.MaxClients} connections already open on this port; close one first", limits.BusyWords(0));
        Assert.Equal("a line that did not end within 10 s", limits.SlowLineWords());
        Assert.Equal("a line that did not end within 0.5 s", (limits with { LineSeconds = 0.5 }).SlowLineWords());
    }
}
