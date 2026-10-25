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
    [InlineData("010.0.0.5", "10.0.0.5")]          // a leading zero is decimal — the parser read it as octal, 8.0.0.5
    [InlineData("192.168.001.010", "192.168.1.10")]
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
    [InlineData("0x0A.0.0.5")]          // hex to the parser, 10.0.0.5
    [InlineData("10.0.0.5:80")]
    [InlineData("1.2.3.4.5")]
    [InlineData("10.0.-1.5")]
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

    /// <summary>
    /// One rule for every address field the desk reads: the bind and each OSC ALLOW entry take one whole address, so a
    /// typo is said as a typo in both and read as some other machine in neither.
    /// </summary>
    [Theory]
    [InlineData("10.0.0.5", true)]
    [InlineData("::ffff:10.0.0.5", true)]
    [InlineData("010.0.0.5", true)]
    [InlineData("10.0.0", false)]
    [InlineData("10", false)]
    [InlineData("0x0A.0.0.5", false)]
    [InlineData("studio-mac.local", false)]
    [InlineData("", false)]
    public void TheBindAndTheOscAllowListReadAnAddressOneWay(string words, bool isAddress)
    {
        Assert.Equal(isAddress, BindAddress.TryAddress(words, out _));
        Assert.Equal(isAddress, OscGate.Read(words).Addresses.Count == 1);
        Assert.Equal(isAddress || words.Length == 0, BindAddress.Problem(words).Length == 0);
        Assert.Equal(!isAddress && words.Length > 0, OscGate.Read(words).LeftOut.Count == 1);
    }

    /// <summary>
    /// The Eye says what the Super Check says: on a paired desk OSC's verbs come only from this machine or OSC ALLOW
    /// (a proof grants nothing to an address, ADR-017), bound or not, with what the list names; on a port open on every
    /// interface with someone listed, that a listed address is not a proof; an entry that is not an address, amber.
    /// </summary>
    [Fact]
    public void TheEyesOscNodeOnAPairedDeskSaysOnlyThisMachineAndTheAllowList()
    {
        var paired = EyeGraph.Build(new EyeFacts { Osc = new EyeOsc(9698, "OSC in on port 9698.", "", Paired: true) }).Find("osc");
        Assert.Equal(CheckLight.Green, paired!.Light);
        var words = Assert.Single(paired.Words);
        Assert.StartsWith("OSC cannot pair; " + OscGate.PairedRule, words, StringComparison.Ordinal);
        Assert.Contains(OscGate.Listed(0), words, StringComparison.Ordinal);
        Assert.DoesNotContain("proved the token", words, StringComparison.Ordinal);

        Assert.DoesNotContain("not a proof", words, StringComparison.Ordinal);                  // nobody listed

        var listed = EyeGraph.Build(new EyeFacts { Osc = new EyeOsc(9698, "OSC in on port 9698.", "", Paired: true, OscGate.Read("10.0.0.9")) }).Find("osc");
        Assert.Equal(CheckLight.Green, listed!.Light);
        words = Assert.Single(listed.Words);
        Assert.Contains("OSC ALLOW names 1 address", words, StringComparison.Ordinal);
        Assert.Contains("A listed address is not a proof", words, StringComparison.Ordinal);

        var boundPaired = EyeGraph.Build(new EyeFacts { Osc = new EyeOsc(9698, "OSC in on port 9698 on 10.0.0.5 only.", "10.0.0.5", Paired: true, OscGate.Read("10.0.0.8, 10.0.0.9")) }).Find("osc");
        Assert.Equal(CheckLight.Green, boundPaired!.Light);
        words = Assert.Single(boundPaired.Words);
        Assert.StartsWith("open on 10.0.0.5 only — OSC cannot pair; the bind keeps it to the control network; " + OscGate.PairedRule, words, StringComparison.Ordinal);
        Assert.Contains("OSC ALLOW names 2 addresses", words, StringComparison.Ordinal);
        Assert.DoesNotContain("not a proof", words, StringComparison.Ordinal);                  // the bind keeps it to the control network

        // An entry that is not an address: amber on a paired desk, named; on an unpaired one the list gates nothing.
        var typo = EyeGraph.Build(new EyeFacts { Osc = new EyeOsc(9698, "OSC in on port 9698 on 10.0.0.5 only.", "10.0.0.5", Paired: true, OscGate.Read("192.168.1")) }).Find("osc");
        Assert.Equal(CheckLight.Amber, typo!.Light);
        Assert.Contains("'192.168.1' is not an address — left out", Assert.Single(typo.Words), StringComparison.Ordinal);
        var unpairedTypo = EyeGraph.Build(new EyeFacts { Osc = new EyeOsc(9698, "OSC in on port 9698 on 10.0.0.5 only.", "10.0.0.5", Paired: false, OscGate.Read("192.168.1")) }).Find("osc");
        Assert.Equal(CheckLight.Green, unpairedTypo!.Light);

        var boundOpen = EyeGraph.Build(new EyeFacts { Osc = new EyeOsc(9698, "OSC in on port 9698 on 10.0.0.5 only.", "10.0.0.5") }).Find("osc");
        Assert.Equal("open on 10.0.0.5 only — OSC cannot pair; the bind keeps it to the control network", Assert.Single(boundOpen!.Words));
    }
}
