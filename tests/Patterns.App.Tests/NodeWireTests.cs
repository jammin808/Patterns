using Patterns.Devices;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// A desk's line to a node's wire: sent again once when it never left, never sent twice once
/// it did, and the node's silence said as that — a cue's verb to the hub PC across a switch
/// that is relearning is not a verb lost.
/// </summary>
public class NodeWireTests
{
    private static NodeCard Card(int port) => new("hub-1", NodeKind.Arcade, "HUB-PC", IPAddress.Loopback, "Gala", "", 0, 0, TimeSpan.Zero, false, port);

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    [Fact]
    public async Task ALineThatNeverLeftIsSentAgainOnceAndThenTheNodeIsCalledUnreachable()
    {
        var retryAfter = NodesService.RetryAfter;
        NodesService.RetryAfter = TimeSpan.FromMilliseconds(120);
        try
        {
            var clock = Stopwatch.StartNew();
            var reply = await NodesService.AskNodeAsync(Card(FreePort()), "PING");       // nothing listens there
            Assert.StartsWith("ERR", reply);
            Assert.Contains("HUB-PC unreachable", reply);
            Assert.EndsWith("(asked twice)", reply);
            Assert.True(clock.ElapsedMilliseconds >= 100, $"asked again after {clock.ElapsedMilliseconds} ms — the pause before the second ask is a real one");
        }
        finally
        {
            NodesService.RetryAfter = retryAfter;
        }
    }

    [Fact]
    public async Task ALineTheNodeTookIsNeverSentTwiceAndItsSilenceIsSaidAsThat()
    {
        var replyBudget = NodesService.ReplyBudget;
        NodesService.ReplyBudget = TimeSpan.FromMilliseconds(400);
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        var lines = new List<string>();
        var answer = false;
        async Task Serve(TcpClient client)
        {
            using (client)
            using (var stream = client.GetStream())
            {
                var reader = new BoundedLineReader(stream, 4096);
                var line = await reader.ReadLineAsync(CancellationToken.None);
                if (line is null) return;
                lock (lines) lines.Add(line);
                if (answer)
                {
                    // A node greets with its state before it answers; the desk reads past the greeting to the reply.
                    var bytes = Encoding.UTF8.GetBytes("STATE {\"node\":true}\nOK pong\n");
                    await stream.WriteAsync(bytes);
                    await stream.FlushAsync();
                }
                else
                {
                    await Task.Delay(1500);                                            // the node took the line and says nothing
                }
            }
        }
        var serving = Task.Run(async () =>
        {
            var served = new List<Task>();
            for (var i = 0; i < 2; i++) served.Add(Serve(await listener.AcceptTcpClientAsync()));   // each door on its own: a silent one holds no other
            await Task.WhenAll(served);
        });
        try
        {
            var silent = await NodesService.AskNodeAsync(Card(port), "ARCADE KEY 1 UP TAP");
            Assert.StartsWith("ERR", silent);
            Assert.Contains("took the line but did not answer within 0.4 s", silent);
            lock (lines) Assert.Equal(new[] { "ARCADE KEY 1 UP TAP" }, lines);           // once: a tap sent twice is two taps

            answer = true;
            var pong = await NodesService.AskNodeAsync(Card(port), "PING");
            Assert.Equal("OK pong", pong);
            lock (lines) Assert.Equal(2, lines.Count);
        }
        finally
        {
            NodesService.ReplyBudget = replyBudget;
            listener.Stop();
            try { await serving.WaitAsync(TimeSpan.FromSeconds(5)); } catch { /* the listener is stopped */ }
        }
    }
}
