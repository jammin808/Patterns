using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 99.5 (the Eye design's §6; ADR-016): the wire's readers of the show network at this desk's own wire. A Stagewatch the scripted
/// peer links by LINK with a key is one link of the Eye's, its edge carrying the acceptor's 13.16.5 row in EYE JSON — read over that
/// link's own connection, which LINK paired — with no address and no lid in it; a connection that has not paired is refused EYE and
/// STATUS in the not-paired words and hears no link field; STATE's <c>eye</c> row says the network in the Eye's own line, and the Super
/// Check's <i>Eye network</i> row says the same beside the links' own rows. From loopback with this machine's trust off.
/// </summary>
public sealed partial class PeerLinkWireTests
{
    [AvaloniaFact]
    public void TheEyesLinkRecordsGoOnlyToAPairedConnectionAndStateAndTheSuperCheckSayTheNetwork()
    {
        using var files = new KitFiles();
        using var desk = new AcceptorDesk();
        TestListeners.Up(desk.Control, desk.Booted.Vm.State.Control, () => desk.Control.Listening, "the desk's listeners");
        var key = NewKey();
        desk.Control.LinkKeys = [LinkAcceptor.Key(key, "main:" + StagewatchId, "drive")!];
        var (conn, linked) = Dialled(desk, DialDesk(desk, tls: true, files.Write("key", key), "key"));
        Assert.Equal(("link", "key"), (Str(linked, "ev"), Str(linked, "verified")));
        ReadOn(desk, conn, l => l.StartsWith("STATE {", StringComparison.Ordinal));
        PumpWhile(() => desk.Control.AppLinkList.Count == 0, 5000);
        desk.Services.Eye.Refresh();
        var graph = desk.Services.Eye.Graph;
        var drawn = Assert.Single(graph.Edges, e => e.Accepted is not null);
        Assert.Equal(("app:" + StagewatchId, EyeGraph.DeskId, EyeOrigin.Link), (drawn.From, drawn.To, drawn.Origin));

        // Paired by LINK, the link's own connection reads EYE: the accepted link's edge carries its 13.16.5 row, never the address.
        Send(desk, conn, "EYE");
        var answer = Str(ReadOn(desk, conn, l => l.StartsWith("OK {", StringComparison.Ordinal)), "line")!;
        using (var eye = JsonDocument.Parse(answer[3..]))
        {
            var edge = eye.RootElement.GetProperty("edges").EnumerateArray()
                .Single(e => e.GetProperty("from").GetString() == "app:" + StagewatchId && e.GetProperty("to").GetString() == EyeGraph.DeskId);
            Assert.Equal("link", edge.GetProperty("source").GetString());
            var row = edge.GetProperty("link");
            Assert.Equal(("acceptor", "main", StagewatchId, "key", "drive", true), (row.GetProperty("role").GetString(), row.GetProperty("t").GetString(),
                row.GetProperty("id").GetString(), row.GetProperty("verified").GetString(), row.GetProperty("scope").GetString(), row.GetProperty("tls").GetBoolean()));
            Assert.Equal(desk.Control.AppLinkList.Single().Kid, row.GetProperty("kid").GetString());
            Assert.DoesNotContain("127.0.0.1", row.ToString(), StringComparison.Ordinal);
            Assert.DoesNotContain(desk.Control.AppLinkList.Single().Lid, row.ToString(), StringComparison.Ordinal);
            Assert.True(eye.RootElement.GetProperty("linkLights").GetProperty("green").GetInt32() >= 1, answer);
            Assert.Equal(graph.Apps, eye.RootElement.GetProperty("apps").GetInt32());
        }

        // A connection that has not paired: NONCE first, then EYE and STATUS refused in the not-paired words — no link field reaches it.
        using (var plain = new TcpClient())
        {
            TestApp.Pump(plain.ConnectAsync("127.0.0.1", desk.Port).ContinueWith(_ => true, TaskScheduler.Default));
            var stream = plain.GetStream();
            stream.Write(Encoding.UTF8.GetBytes("EYE\nSTATUS\n"));
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var heard = new List<string>();
            string? Next()
            {
                var line = TestApp.Pump(reader.ReadLineAsync());
                while (line is not null && line.StartsWith("NONCE ", StringComparison.Ordinal))
                {
                    heard.Add(line);
                    line = TestApp.Pump(reader.ReadLineAsync());
                }
                if (line is not null) heard.Add(line);
                return line;
            }
            Assert.Equal("ERR " + ControlProtocol.StateNotPaired, Next());
            Assert.Equal("ERR " + ControlProtocol.StateNotPaired, Next());
            Assert.All(heard, l => Assert.DoesNotContain("\"link\"", l, StringComparison.Ordinal));
        }

        // STATE's eye row says the network in the Eye's own line, at the end of the row; the Super Check says the same beside the Eye.
        desk.Services.Eye.Refresh();
        using (var state = JsonDocument.Parse(new CommandRouter(desk.Services).StateJson()))
        {
            var row = state.RootElement.GetProperty("eye");
            var now = desk.Services.Eye.Graph;
            Assert.Equal(new[] { "apps", "linksRed", "linksAmber", "network", "linksWorst" }, row.EnumerateObject().Select(o => o.Name).TakeLast(5));
            Assert.Equal((now.Apps, now.LinkLights.Red, now.LinkLights.Amber, now.NetworkLine, now.LinkLights.Worst),
                (row.GetProperty("apps").GetInt32(), row.GetProperty("linksRed").GetInt32(), row.GetProperty("linksAmber").GetInt32(), row.GetProperty("network").GetString(), row.GetProperty("linksWorst").GetString()));
            Assert.Contains("link", now.NetworkLine, StringComparison.Ordinal);
            var check = Assert.Single(SuperCheck.Run(desk.Services.Metrics.GatherFacts()).Rows, r => r.Item == "Eye network");
            Assert.Equal((CheckLight.Grey, now.NetworkLine), (check.Light, check.Value));
        }
    }
}
