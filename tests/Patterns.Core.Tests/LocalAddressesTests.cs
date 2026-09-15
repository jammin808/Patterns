using System.Net;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>Round 65: the machine's addresses come from its interfaces, picked without the resolver — IPv4 only, no loopback, no link-local, each once, in the order the interfaces gave them.</summary>
public class LocalAddressesTests
{
    [Fact]
    public void PickKeepsRoutableIPv4AddressesOnceAndInOrder()
    {
        var picked = LocalAddresses.Pick(new[]
        {
            IPAddress.Parse("192.168.1.20"),
            IPAddress.Parse("127.0.0.1"),
            IPAddress.Parse("169.254.10.5"),
            IPAddress.Parse("fe80::1"),
            IPAddress.Parse("10.0.0.7"),
            IPAddress.Parse("192.168.1.20"),
        });
        Assert.Equal(new[] { "192.168.1.20", "10.0.0.7" }, picked.Select(a => a.ToString()));
    }

    [Fact]
    public void LinkLocalIsTheAutomaticRangeAlone()
    {
        Assert.True(LocalAddresses.IsLinkLocal(IPAddress.Parse("169.254.0.1")));
        Assert.False(LocalAddresses.IsLinkLocal(IPAddress.Parse("169.253.0.1")));
        Assert.False(LocalAddresses.IsLinkLocal(IPAddress.Parse("fe80::1")));
    }

    [Fact]
    public void EnumerateAnswersAtOnceWithoutTheResolver()
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        var addresses = LocalAddresses.Enumerate();
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(2), $"the interfaces took {sw.Elapsed}");
        Assert.All(addresses, a => Assert.False(IPAddress.IsLoopback(a)));
    }
}
