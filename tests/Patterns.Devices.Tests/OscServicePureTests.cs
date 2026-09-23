using System.Net;
using System.Net.Sockets;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Patterns.Devices;
using Xunit;

namespace Patterns.Devices.Tests;

/// <summary>A router whose BLACKOUT throws — a handler fault — and whose every other line is answered.</summary>
public sealed class FaultingRouter : IRouter
{
    public List<string> Executed { get; } = new();
    public Func<long>? Rev { get; set; }

    public Task<string> ExecuteAsync(RemoteCommand cmd, ActionOrigin? origin = null)
    {
        if (cmd.Action.Kind is ShowActionKind.BlackoutOn or ShowActionKind.BlackoutOff or ShowActionKind.BlackoutToggle) throw new InvalidOperationException("a handler fault");
        Executed.Add(cmd.Kind == RemoteCommandKind.Action ? cmd.Action.Kind.ToString() : cmd.Kind.ToString());
        return Task.FromResult("OK");
    }

    public string StateJson() => "{}";
    public Task<string> StateJsonAsync() => Task.FromResult("{}");
    public Task<string> CueListJsonAsync() => Task.FromResult("[]");
}

/// <summary>The desk as the OSC port sees it: a show and a router, nothing else built.</summary>
public sealed class FakeOscHost : IOscHost
{
    public ShowState State { get; } = new();
    public FaultingRouter Wire { get; } = new();
    public IRouter Router => Wire;
    public ICueStackEvents? CueStackEvents => null;
#pragma warning disable CS0067 // the port subscribes; nothing in these tests publishes
    public event Action? SnapshotPublished;
    public event Action? RuntimeChanged;
#pragma warning restore CS0067
}

/// <summary>
/// The OSC port on the core's contracts alone — a fake host, a real socket on loopback, the inline dispatch. A fault in
/// one message's handling used to end the receive loop for good: Reconcile reopens the port only when its settings
/// change, so one datagram on a port no pairing token covers closed OSC for the rest of the show.
/// </summary>
public class OscServicePureTests
{
    private static int FreeUdpPort()
    {
        using var probe = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        return ((IPEndPoint)probe.Client.LocalEndPoint!).Port;
    }

    private static async Task<OscMessage> NextAsync(UdpClient socket)
    {
        using var wait = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var got = await socket.ReceiveAsync(wait.Token);
        return Assert.Single(OscCodec.Decode(got.Buffer));
    }

    [Fact]
    public async Task AMessageWhoseHandlingThrowsIsAnsweredWithAnErrorAndTheNextMessageIsHeard()
    {
        var host = new FakeOscHost();
        host.State.Control.Enabled = true;
        host.State.Control.OscEnabled = true;
        host.State.Control.OscPort = FreeUdpPort();
        host.State.Control.Bind = "127.0.0.1";
        using var osc = new OscService(host);
        osc.Reconcile();
        Assert.Contains("127.0.0.1", osc.Status, StringComparison.Ordinal);

        using var controller = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var desk = new IPEndPoint(IPAddress.Loopback, host.State.Control.OscPort);

        await controller.SendAsync(OscCodec.Encode(OscMessage.Of("/patterns/blackout", 1)), desk);
        var error = await NextAsync(controller);
        Assert.Equal("/patterns/error", error.Address);
        Assert.Contains("InvalidOperationException", error.Text() ?? "", StringComparison.Ordinal);
        Assert.Equal(1L, osc.MessageFaults);

        await controller.SendAsync(OscCodec.Encode(OscMessage.Of("/patterns/ping")), desk);
        var pong = await NextAsync(controller);
        Assert.Equal("/patterns/pong", pong.Address);
        Assert.Equal(new[] { "Ping" }, host.Wire.Executed);

        // A second fault is a second message's, and the port still answers after it.
        await controller.SendAsync(OscCodec.Encode(OscMessage.Of("/patterns/blackout", 0)), desk);
        Assert.Equal("/patterns/error", (await NextAsync(controller)).Address);
        await controller.SendAsync(OscCodec.Encode(OscMessage.Of("/patterns/ping")), desk);
        Assert.Equal("/patterns/pong", (await NextAsync(controller)).Address);
        Assert.Equal(2L, osc.MessageFaults);
        Assert.Equal(4L, osc.Received);
    }
}
