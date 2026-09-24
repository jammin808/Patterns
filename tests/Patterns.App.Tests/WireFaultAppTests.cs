using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 83 (L31): a fault behind the wire is that line's, never the connection's. Over TCP the line is answered
/// ERR naming the exception and the count, the connection stays and the next line is answered as usual; over HTTP
/// the request is answered 500 with the same ERR in the JSON; the count reaches the Super Check facts. The fault
/// is injected at the router's seam, the way a parser or a greeting throws outside the router's own catch — before
/// this round both handlers caught it as a routine disconnect and logged nothing.
/// </summary>
public class WireFaultAppTests
{
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

    private static (string Status, string Body) Http(int port, string method, string path, string body = "")
    {
        using var client = new TcpClient();
        Pump(client.ConnectAsync(IPAddress.Loopback, port));
        using var stream = client.GetStream();
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var head = $"{method} {path} HTTP/1.1\r\nHost: test\r\nContent-Length: {bodyBytes.Length}\r\n\r\n";
        stream.Write(Encoding.UTF8.GetBytes(head));
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

    /// <summary>One TCP connection to the desk's wire: the greeting read, lines sent, replies read past the STATE pushes.</summary>
    private sealed class Wire : IDisposable
    {
        private readonly TcpClient _client = new();
        private readonly NetworkStream _stream;
        private readonly StreamReader _reader;

        public Wire(int port)
        {
            Pump(_client.ConnectAsync(IPAddress.Loopback, port));
            _stream = _client.GetStream();
            _reader = new StreamReader(_stream, Encoding.UTF8);
            var greet = Pump(_reader.ReadLineAsync());
            Assert.StartsWith("STATE {", greet);
        }

        public void Send(string line) => _stream.Write(Encoding.UTF8.GetBytes(line + "\n"));

        public string ReadResponse()
        {
            while (true)
            {
                var line = Pump(_reader.ReadLineAsync());
                Assert.NotNull(line);
                if (!line!.StartsWith("STATE ", StringComparison.Ordinal)) return line;
            }
        }

        public void Dispose() => _client.Dispose();
    }

    [AvaloniaFact]
    public void AFaultBehindTheWireIsAnsweredErrOnTheLineAndTheConnectionCarriesOn()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        vm.State.Control.HttpPort = FreePort();
        vm.State.Control.TcpPort = FreePort();
        Dispatcher.UIThread.RunJobs();
        var before = services.Control.FaultCount;
        try
        {
            CommandRouter.FaultOn = cmd => cmd.Kind == RemoteCommandKind.Ping;
            using var wire = new Wire(vm.State.Control.TcpPort);
            wire.Send("PING");
            var reply = wire.ReadResponse();
            Assert.StartsWith("ERR the desk faulted on this line (InvalidOperationException)", reply);
            Assert.Contains($"fault #{before + 1}, logged", reply);
            Assert.DoesNotContain("   at ", reply);                                  // the type and the count, never the stack
            Assert.Equal(before + 1, services.Control.FaultCount);

            // The connection is still open and the next line is answered as usual — the fault was the line's.
            CommandRouter.FaultOn = null;
            wire.Send("PING");
            Assert.Equal("OK PONG", wire.ReadResponse());

            // Over HTTP the request is answered, not dropped: 500 with the same ERR in the JSON.
            CommandRouter.FaultOn = cmd => cmd.Kind == RemoteCommandKind.Ping;
            var http = Http(vm.State.Control.HttpPort, "POST", "/api/cmd", "PING");
            Assert.Equal("500 Internal Server Error", http.Status);
            Assert.Contains("\"ok\":false", http.Body);
            Assert.Contains("the desk faulted on this line (InvalidOperationException)", http.Body);
            Assert.Equal(before + 2, services.Control.FaultCount);

            CommandRouter.FaultOn = null;
            var ok = Http(vm.State.Control.HttpPort, "POST", "/api/cmd", "PING");
            Assert.Equal("200 OK", ok.Status);
            Assert.Contains("\"ok\":true", ok.Body);

            // The count reaches the Super Check's facts and its REMOTE row.
            var facts = new CheckFacts { RemoteEnabled = true, RemoteUrl = "http://test", WireFaults = services.Control.FaultCount };
            var report = SuperCheck.Run(facts);
            var row = Assert.Single(report.Rows, r => r.Item == "Wire faults");
            Assert.Equal(CheckLight.Amber, row.Light);
            Assert.Contains($"{before + 2} since start", row.Value);
        }
        finally
        {
            CommandRouter.FaultOn = null;
            Dispatcher.UIThread.RunJobs();
        }
    }
}
