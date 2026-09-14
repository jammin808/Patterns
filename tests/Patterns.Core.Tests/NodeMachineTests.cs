using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>A node's Machine tab in words: what it is, its ports, the watchdog, where the desk's key comes from, the help per kind.</summary>
public class NodeMachineTests
{
    [Fact]
    public void TheStripSaysWhatTheNodeIsItsPortsAndTheWatchdog()
    {
        Assert.Equal("Caller node · CALLER-PC · build 1.4.0 · folder D:\\Patterns", NodeMachine.Identity(NodeKind.Caller, "CALLER-PC", "1.4.0", "D:\\Patterns"));
        var control = new ControlConfig { HttpPort = 9696, TcpPort = 9697, AudienceEnabled = false };
        var words = NodeMachine.PortsWords(NodeKind.Timer, control);
        Assert.Contains("HTTP 9696", words);
        Assert.Contains("stage display", words);
        Assert.Contains("Companion (TCP) 9697", words);
        Assert.Contains("audience port off", words);
        control.AudienceEnabled = true;
        control.AudiencePort = 9701;
        control.AudienceBind = "10.10.0.1";
        control.AudienceMaxPlayers = 300;
        Assert.Contains("audience 9701 at 10.10.0.1 — the phones' play pages, 300 seats", NodeMachine.PortsWords(NodeKind.Arcade, control));
        Assert.Contains("phone pad", NodeMachine.PortsWords(NodeKind.Arcade, control));
        control.Enabled = false;
        Assert.StartsWith("Remote control off", NodeMachine.PortsWords(NodeKind.Arcade, control));
        Assert.StartsWith("Under the watchdog", NodeMachine.WatchdogWords(true));
        Assert.Contains("refused", NodeMachine.WatchdogWords(false));
    }

    [Fact]
    public void TheKeyWordsSendTheOperatorToTheDeskAndThenToLink()
    {
        Assert.Contains("Machine page (TWIN)", NodeMachine.KeyWords(hasKey: false, linked: false));
        Assert.Contains("LINK on the Nodes tab", NodeMachine.KeyWords(hasKey: true, linked: false));
        Assert.Contains("linked", NodeMachine.KeyWords(hasKey: true, linked: true));
    }

    [Fact]
    public void EveryKindHasItsOwnHelpAndItsMachineTabAndTheDeskNeither()
    {
        Assert.Contains("no outputs, ever", NodeMachine.Help(NodeKind.Caller));
        Assert.Contains("APPLY", NodeMachine.Help(NodeKind.Caller));
        Assert.Contains("/stage", NodeMachine.Help(NodeKind.Timer));
        Assert.Contains("no key", NodeMachine.Help(NodeKind.Arcade));
        Assert.Equal("", NodeMachine.Help(NodeKind.Desk));
        foreach (var kind in new[] { NodeKind.Caller, NodeKind.Timer, NodeKind.Arcade }) Assert.Equal("Machine", NodeKinds.Pages(kind)!.Last());
        Assert.Null(NodeKinds.Pages(NodeKind.Desk));
    }
}
