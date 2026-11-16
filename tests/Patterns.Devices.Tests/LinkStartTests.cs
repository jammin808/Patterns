using System.Collections.Concurrent;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Patterns.Devices;
using Xunit;

namespace Patterns.Devices.Tests;

/// <summary>
/// Every link kind the service makes starts nothing in its constructor: it dials, opens and reads only from <see cref="IDeviceLink.Start"/>,
/// once, so its caller's handlers are on before anything is raised. Each case sets a link that was never started beside one that was,
/// on the same machine and the same address, and holds the unstarted one to having done nothing while the started one did its work.
/// </summary>
public class LinkStartTests
{
    private static readonly TimeSpan Wait = TimeSpan.FromSeconds(5);

    /// <summary>Long enough after the started link's work for an unstarted link that had begun on its own to have shown it.</summary>
    private const int Settle = 200;

    private static bool Until(Func<bool> test)
    {
        var deadline = DateTime.UtcNow + Wait;
        while (DateTime.UtcNow < deadline)
        {
            if (test()) return true;
            Thread.Sleep(10);
        }
        return test();
    }

    [Fact]
    public void ATcpLinkDialsOnlyOnceStartedAndSaysItsConnectionsStartBeforeItsFirstLine()
    {
        using var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        using var unstarted = new TcpDeviceLink("127.0.0.1", port);
        var disposedFirst = new TcpDeviceLink("127.0.0.1", port);
        disposedFirst.Dispose();
        disposedFirst.Start();                                                                       // a link disposed before it starts nothing

        using var link = new TcpDeviceLink("127.0.0.1", port);
        var heard = new ConcurrentQueue<string>();
        link.Moving += () => heard.Enqueue("moving");
        link.Opened += () => heard.Enqueue("opened");
        link.LineReceived += line => heard.Enqueue("line " + line);
        link.Start();
        link.Start();                                                                                // a second start does nothing

        using var accepted = listener.AcceptTcpClient();
        accepted.GetStream().Write(Encoding.UTF8.GetBytes("NONCE 00112233445566778899aabbccddeeff\n"));
        Assert.True(Until(() => heard.Count >= 3), "heard: " + string.Join(", ", heard));
        Assert.Equal(new[] { "moving", "opened", "line NONCE 00112233445566778899aabbccddeeff" }, heard.ToArray());
        Assert.True(link.IsOpen);

        Thread.Sleep(Settle);
        Assert.False(listener.Pending());                                                            // one connection: the started link's, once
        Assert.False(unstarted.IsOpen);
        Assert.Equal("connecting…", unstarted.Status);
    }

    [Fact]
    public void AUdpLinkReadsOnlyOnceStartedAndADatagramThatCameBeforeIsStillRead()
    {
        using var box = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
        var port = ((IPEndPoint)box.Client.LocalEndPoint!).Port;
        using var unstarted = new UdpDeviceLink("127.0.0.1", port);
        using var link = new UdpDeviceLink("127.0.0.1", port);
        var early = new ConcurrentQueue<string>();
        var heard = new ConcurrentQueue<string>();
        unstarted.LineReceived += early.Enqueue;
        link.LineReceived += heard.Enqueue;
        link.Start();
        Thread.Sleep(Settle);
        var open = $"open (127.0.0.1:{port}, UDP)";
        Assert.Equal(open, link.Status);                                                            // read from its start, before any send

        unstarted.Write("PING a");
        link.Write("PING b");
        for (var i = 0; i < 2; i++)
        {
            var from = new IPEndPoint(IPAddress.Any, 0);
            var got = Encoding.UTF8.GetString(box.Receive(ref from));
            var answer = Encoding.UTF8.GetBytes("PONG " + got[^1]);
            box.Send(answer, answer.Length, from);
        }
        Assert.True(Until(() => heard.Contains("PONG b")), "heard: " + string.Join(", ", heard));
        Thread.Sleep(Settle);
        Assert.Empty(early);                                                                         // nothing read before Start
        Assert.Equal(open, link.Status);

        unstarted.Start();                                                                           // the socket kept it: read now, not lost
        Assert.True(Until(() => early.Contains("PONG a")), "heard after the start: " + string.Join(", ", early));
        Assert.Equal(open, unstarted.Status);
    }

    [Fact]
    public void ASerialLinkOpensItsPortOnlyOnceStarted()
    {
        var port = OperatingSystem.IsWindows() ? "COM250" : "/dev/patterns-no-such-port";
        using var unstarted = new SerialDeviceLink(port, 115200);
        using var link = new SerialDeviceLink(port, 115200);
        var said = 0;
        link.Moving += () => Interlocked.Increment(ref said);
        link.Opened += () => Interlocked.Increment(ref said);
        link.Start();
        Assert.True(Until(() => link.Status != "opening…"), "the started link tried its port: " + link.Status);
        Assert.StartsWith("closed: ", link.Status, StringComparison.Ordinal);
        Assert.Equal((0L, 0), (link.Connection, Volatile.Read(ref said)));                               // a port that never opened was no connection
        Assert.Equal(0L, unstarted.Connection);
        Thread.Sleep(Settle);
        Assert.Equal("opening…", unstarted.Status);                                                 // the unstarted link never tried
        Assert.False(unstarted.IsOpen);
    }

    [Fact]
    public void AMidiSurfaceTicksOnlyOnceStarted()
    {
        using var unstarted = new MidiSurfaceLink("APC40");
        using var link = new MidiSurfaceLink("APC40");
        link.Start();
        Assert.True(Until(() => link.Status != "opening…"), "the started surface ticked: " + link.Status);
        Thread.Sleep(Settle);
        Assert.Equal("opening…", unstarted.Status);                                                 // no tick before Start: no port opened, no press read
        Assert.False(unstarted.IsOpen);
    }
}
