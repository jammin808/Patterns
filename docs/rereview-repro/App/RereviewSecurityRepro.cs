using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>Re-review reproducers (kept under docs/rereview-repro, outside the test projects): SEC-2, a rebound page against the cross-origin gate (SEC-1, the phone page on a paired desk, was fixed by round 85.6 and its test removed).</summary>
public class RereviewSecurityRepro
{
    private const string Token = "K7QM-3XWD-P9RA";

    private static (AppServices Services, MainViewModel Vm) Boot()
    {
        var b = TestApp.Boot();
        return (b.Services, b.Vm);
    }

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static T Pump<T>(Task<T> task, int timeoutMs = 8000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
            if (Environment.TickCount64 > deadline) throw new TimeoutException("pumped task timed out");
        }
        return task.GetAwaiter().GetResult();
    }

    private static void Pump(Task task, int timeoutMs = 8000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
            if (Environment.TickCount64 > deadline) throw new TimeoutException("pumped task timed out");
        }
        task.GetAwaiter().GetResult();
    }

    /// <summary>One HTTP request with the Host header the caller names (a browser sends the name it resolved).</summary>
    private static (string Status, string Body) Http(int port, string method, string path, string host, string body = "", params (string Name, string Value)[] headers)
    {
        using var client = new TcpClient();
        Pump(client.ConnectAsync(IPAddress.Loopback, port));
        using var stream = client.GetStream();
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var head = new StringBuilder($"{method} {path} HTTP/1.1\r\nHost: {host}\r\nContent-Length: {bodyBytes.Length}\r\n");
        foreach (var (name, value) in headers) head.Append($"{name}: {value}\r\n");
        head.Append("\r\n");
        stream.Write(Encoding.UTF8.GetBytes(head.ToString()));
        stream.Write(bodyBytes);
        using var buffer = new MemoryStream();
        var chunk = new byte[8192];
        while (true)
        {
            var n = Pump(stream.ReadAsync(chunk, 0, chunk.Length));
            if (n <= 0) break;
            buffer.Write(chunk, 0, n);
        }
        var text = Encoding.UTF8.GetString(buffer.ToArray());
        var cut = text.IndexOf("\r\n\r\n", StringComparison.Ordinal);
        var statusLine = text[..text.IndexOf("\r\n", StringComparison.Ordinal)];
        return (statusLine["HTTP/1.1 ".Length..], cut < 0 ? "" : text[(cut + 4)..]);
    }

    [AvaloniaFact]
    public void ARequestShapedLikeARebindingPagePassesTheCrossOriginGate()
    {
        var (services, vm) = Boot();
        var trusted = ControlService.TrustLoopback;
        ControlService.TrustLoopback = false;
        try
        {
            vm.State.Control.HttpPort = FreePort();
            vm.State.Control.TcpPort = FreePort();
            vm.State.Control.Token = "";                                           // the shipped default (L42)
            Dispatcher.UIThread.RunJobs();
            var port = vm.State.Control.HttpPort;
            var p = port.ToString(System.Globalization.CultureInfo.InvariantCulture);

            // 83.2 holds against a plain page of another origin.
            var plain = Http(port, "POST", "/api/cmd", "192.168.1.20:" + p, "BLACKOUT ON", ("Origin", "http://evil.example"), ("Sec-Fetch-Site", "cross-site"));
            Assert.Equal("403 Forbidden", plain.Status);
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.State.Blackout);

            // The same page once its name resolves to the desk: the browser sends its own name as Host and calls it same-origin.
            var rebound = Http(port, "POST", "/api/cmd", "evil.example:" + p, "BLACKOUT ON", ("Origin", "http://evil.example:" + p), ("Sec-Fetch-Site", "same-origin"));
            Assert.Equal("200 OK", rebound.Status);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.State.Blackout);

            // A paired desk: the show machine's own browser is loopback, trusted by default, so a page rebound to 127.0.0.1 is paired too.
            vm.State.Control.Token = Token;
            ControlService.TrustLoopback = true;
            Dispatcher.UIThread.RunJobs();
            var plainLoop = Http(port, "POST", "/api/cmd", "127.0.0.1:" + p, "BLACKOUT OFF", ("Origin", "http://evil.example"), ("Sec-Fetch-Site", "cross-site"));
            Assert.Equal("403 Forbidden", plainLoop.Status);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.State.Blackout);
            var reboundLoop = Http(port, "POST", "/api/cmd", "evil.example:" + p, "BLACKOUT OFF", ("Origin", "http://evil.example:" + p), ("Sec-Fetch-Site", "same-origin"));
            Assert.Equal("200 OK", reboundLoop.Status);
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.State.Blackout);
        }
        finally
        {
            ControlService.TrustLoopback = trusted;
            vm.State.Control.Token = "";
            Dispatcher.UIThread.RunJobs();
        }
    }
}
