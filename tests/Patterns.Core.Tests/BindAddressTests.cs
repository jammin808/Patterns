using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 85 (P1-06): the bind setting is read one way by every listener, and a bind that is not an address closes
/// the listener — never opens it on every interface — with the Super Check, the Eye and the status saying why.
/// </summary>
public class BindAddressTests
{
    [Theory]
    [InlineData("", null)]
    [InlineData("   ", null)]
    [InlineData("10.0.0.5", "10.0.0.5")]
    [InlineData(" 10.0.0.5 ", "10.0.0.5")]
    [InlineData("127.0.0.1", "127.0.0.1")]
    [InlineData("0.0.0.0", "0.0.0.0")]
    [InlineData("::1", "::1")]
    [InlineData("fe80::1", "fe80::1")]
    public void AnAddressOrNothingBinds(string words, string? expected)
    {
        Assert.True(BindAddress.TryParse(words, out var address, out var problem));
        Assert.Equal("", problem);
        Assert.Equal(expected, address?.ToString());
        Assert.Equal("", BindAddress.Problem(words));
    }

    [Theory]
    [InlineData("10.0.0")]              // the parser's shorthand for 10.0.0.0 — a typo here
    [InlineData("1")]                   // 0.0.0.1 to the parser
    [InlineData("10.0.0.256")]
    [InlineData("desk.local")]
    [InlineData("every interface")]
    [InlineData("10.0.0.5 only")]
    public void AnythingElseIsAProblemAndNeverEveryInterface(string words)
    {
        Assert.False(BindAddress.TryParse(words, out var address, out var problem));
        Assert.Null(address);
        Assert.StartsWith($"'{words}' is not an address", problem);
        Assert.Contains("every interface", problem);
        Assert.Equal(problem, BindAddress.Problem(words));
    }

    [Fact]
    public void TheCheckRowsSayClosedWithTheFixWhenABindIsNotAnAddress()
    {
        var facts = new CheckFacts
        {
            RemoteEnabled = true, RemoteUrl = "http://10.0.0.5:9696/", RemoteToken = true,
            RemoteBind = "10.0.0", RemoteBindProblem = BindAddress.Problem("10.0.0"),
            OscOpen = true, OscPort = 9698,
            AudienceBindProblem = BindAddress.Problem("phones"),
        };
        var rows = SuperCheck.Run(facts).Rows.Where(r => r.Section == "REMOTE").ToList();
        var remote = Assert.Single(rows, r => r.Item == "Remote control");
        Assert.Equal(CheckLight.Red, remote.Light);
        Assert.Equal("closed — the bind is not an address", remote.Value);
        Assert.StartsWith("FIX: '10.0.0' is not an address", remote.Note);
        Assert.Contains("Bind to", remote.Note);
        var osc = Assert.Single(rows, r => r.Item == "OSC");
        Assert.Equal(CheckLight.Red, osc.Light);
        Assert.Equal("port 9698 · closed — the bind is not an address", osc.Value);
        var audience = Assert.Single(rows, r => r.Item == "Audience");
        Assert.Equal(CheckLight.Red, audience.Light);
        Assert.StartsWith("FIX: 'phones' is not an address", audience.Note);

        // Bound to an address, paired, the audience bind an address: no closed row anywhere, and no audience row at all.
        var open = SuperCheck.Run(new CheckFacts { RemoteEnabled = true, RemoteUrl = "http://10.0.0.5:9696/", RemoteBind = "10.0.0.5", RemoteToken = true, OscOpen = true, OscPort = 9698 })
            .Rows.Where(r => r.Section == "REMOTE").ToList();
        Assert.DoesNotContain(open, r => r.Item == "Audience");
        Assert.All(open, r => Assert.NotEqual(CheckLight.Red, r.Light));
        Assert.Equal("port 9698 · open on 10.0.0.5 only", Assert.Single(open, r => r.Item == "OSC").Value);
    }

    [Fact]
    public void TheEyesOscNodeIsRedWhenTheBindIsNotAnAddress()
    {
        var closed = EyeGraph.Build(new EyeFacts { Osc = new EyeOsc(9698, "OSC closed — '10.0.0' is not an address.", "10.0.0") }).Find("osc");
        Assert.NotNull(closed);
        Assert.Equal(CheckLight.Red, closed!.Light);
        Assert.Contains(closed.Words, w => w.StartsWith("closed — '10.0.0' is not an address", StringComparison.Ordinal));

        var bound = EyeGraph.Build(new EyeFacts { Osc = new EyeOsc(9698, "OSC in on port 9698 on 10.0.0.5 only.", "10.0.0.5") }).Find("osc");
        Assert.Equal(CheckLight.Green, bound!.Light);
        var open = EyeGraph.Build(new EyeFacts { Osc = new EyeOsc(9698, "OSC in on port 9698.") }).Find("osc");
        Assert.Equal(CheckLight.Amber, open!.Light);
    }
}
