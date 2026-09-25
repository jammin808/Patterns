using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.RegularExpressions;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 65: explicit trust on the control network. A pairing token set on the Remote page makes
/// every mutating verb wait for it — AUTH on the wire, X-Patterns-Token on the web — while the
/// queries, the pages and this machine's own browsers never need it; NEW TOKEN cuts the paired
/// remotes off; the admin passcode rides in a header and never in a URL; the control ports bind
/// where the page says. Real sockets against the live app, from loopback with the exemption off.
/// </summary>
public class RemoteTrustTests
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

    /// <summary>A connection to the wire with the greeting read: Send a line, ReadResponse the next OK/ERR (STATE pushes skipped).</summary>
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
                if (!line!.StartsWith("STATE ")) return line;
            }
        }

        /// <summary>Null once the desk has closed the connection.</summary>
        public string? ReadLineOrEnd()
        {
            while (true)
            {
                var line = Pump(_reader.ReadLineAsync());
                if (line is null || !line.StartsWith("STATE ")) return line;
            }
        }

        public void Dispose() => _client.Dispose();
    }

    /// <summary>One HTTP request on the control port, as a phone or a curl would send it: the status and the body.</summary>
    private static (string Status, string Body) Http(int port, string method, string path, string body = "", params (string Name, string Value)[] headers)
    {
        using var client = new TcpClient();
        Pump(client.ConnectAsync(IPAddress.Loopback, port));
        using var stream = client.GetStream();
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var head = new StringBuilder($"{method} {path} HTTP/1.1\r\nHost: test\r\nContent-Length: {bodyBytes.Length}\r\n");
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
    public void OnTheWireAMutatingVerbWaitsForTheTokenAQueryDoesNotAndNewTokenCutsThePairedOff()
    {
        var (services, vm) = Boot();
        var trusted = ControlService.TrustLoopback;
        ControlService.TrustLoopback = false;
        try
        {
            vm.State.Control.HttpPort = FreePort();
            vm.State.Control.TcpPort = FreePort();
            vm.State.Control.Token = Token;
            Dispatcher.UIThread.RunJobs();
            Assert.EndsWith("· paired.", services.Control.Status);

            using var wire = new Wire(vm.State.Control.TcpPort);
            wire.Send("PING");
            Assert.Equal("OK PONG", wire.ReadResponse());
            wire.Send("STATUS");
            Assert.StartsWith("OK {", wire.ReadResponse());               // reading never needs the token

            wire.Send("BLACKOUT ON");
            var refused = wire.ReadResponse();
            Assert.StartsWith("ERR not paired", refused);
            Assert.Contains("AUTH <token>", refused);
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.State.Blackout);                          // nothing ran

            wire.Send("AUTH K7QM-3XWD-P9RB");
            Assert.StartsWith("ERR wrong token", wire.ReadResponse());
            wire.Send("auth k7qm3xwd p9ra");                                 // dashes, spaces and case do not matter
            Assert.Equal("OK paired", wire.ReadResponse());
            wire.Send("BLACKOUT ON");
            Assert.Equal("OK", wire.ReadResponse());
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.State.Blackout);

            // NEW TOKEN: the show's token changes, and the connection paired with the old one is cut off from the verbs — not from reading.
            vm.NewPairingTokenCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Matches(new Regex("^[A-Z2-9]{4}-[A-Z2-9]{4}-[A-Z2-9]{4}$"), vm.State.Control.Token);
            Assert.NotEqual(Token, vm.State.Control.Token);
            wire.Send("BLACKOUT OFF");
            Assert.StartsWith("ERR not paired", wire.ReadResponse());
            wire.Send("STATUS");
            Assert.StartsWith("OK {", wire.ReadResponse());
            wire.Send("AUTH " + vm.State.Control.Token);
            Assert.Equal("OK paired", wire.ReadResponse());
            wire.Send("BLACKOUT OFF");
            Assert.Equal("OK", wire.ReadResponse());
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.State.Blackout);

            // An open desk answers AUTH too, so a module with a stale token is told rather than refused.
            vm.State.Control.Token = "";
            Dispatcher.UIThread.RunJobs();
            wire.Send("AUTH anything");
            Assert.StartsWith("OK open", wire.ReadResponse());
            wire.Send("BLACKOUT ON");
            Assert.Equal("OK", wire.ReadResponse());
        }
        finally
        {
            ControlService.TrustLoopback = trusted;
            vm.State.Control.Token = "";
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void FiveWrongTokensCloseTheConnection()
    {
        var (services, vm) = Boot();
        var trusted = ControlService.TrustLoopback;
        ControlService.TrustLoopback = false;
        try
        {
            vm.State.Control.HttpPort = FreePort();
            vm.State.Control.TcpPort = FreePort();
            vm.State.Control.Token = Token;
            Dispatcher.UIThread.RunJobs();
            using var wire = new Wire(vm.State.Control.TcpPort);
            for (var i = 1; i < ControlService.WrongTokensBeforeClose; i++)
            {
                wire.Send("AUTH nope");
                Assert.Equal(ControlProtocol.Err(ControlProtocol.WrongToken), wire.ReadResponse());
            }
            wire.Send("AUTH nope");
            Assert.EndsWith("— closed", wire.ReadResponse());
            Assert.Null(wire.ReadLineOrEnd());                                 // the desk hung up
            var deadline = Environment.TickCount64 + 5000;
            while (services.Control.WireConnections > 0 && Environment.TickCount64 < deadline) { Dispatcher.UIThread.RunJobs(); Thread.Sleep(5); }
            Assert.Equal(0, services.Control.WireConnections);
        }
        finally
        {
            ControlService.TrustLoopback = trusted;
            vm.State.Control.Token = "";
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void OnTheWebAMutatingPostWantsTheTokenInItsHeaderAndThisMachinesOwnBrowserIsExempt()
    {
        var (services, vm) = Boot();
        var trusted = ControlService.TrustLoopback;
        ControlService.TrustLoopback = false;
        try
        {
            vm.State.Control.HttpPort = FreePort();
            vm.State.Control.TcpPort = FreePort();
            vm.State.Control.Token = Token;
            Dispatcher.UIThread.RunJobs();
            var port = vm.State.Control.HttpPort;

            var refused = Http(port, "POST", "/api/cmd", "BLACKOUT ON", ("X-Patterns-Client", "phone"));
            Assert.Equal("403 Forbidden", refused.Status);
            Assert.Contains("\"ok\":false", refused.Body);
            Assert.Contains("not paired", refused.Body);
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.State.Blackout);

            var query = Http(port, "POST", "/api/cmd", "STATUS");
            Assert.Equal("200 OK", query.Status);                              // a query needs no token
            Assert.Contains("\"ok\":true", query.Body);

            var wrong = Http(port, "POST", "/api/cmd", "BLACKOUT ON", ("X-Patterns-Token", "K7QM-3XWD-P9RB"));
            Assert.Equal("403 Forbidden", wrong.Status);

            var paired = Http(port, "POST", "/api/cmd", "BLACKOUT ON", ("X-Patterns-Token", "k7qm-3xwd-p9ra"));
            Assert.Equal("200 OK", paired.Status);
            Assert.Contains("\"ok\":true", paired.Body);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.State.Blackout);

            var ack = Http(port, "POST", "/api/stage/ack", "some-id");
            Assert.Equal("403 Forbidden", ack.Status);
            Assert.Contains("not paired", ack.Body);
            var pad = Http(port, "POST", "/api/arcade/key", "1 A TAP");
            Assert.Equal("403 Forbidden", pad.Status);

            // The pages themselves are open; the state and the pictures are the show's too (round 83): the header, or the pages' cookie for their img tags.
            Assert.Equal("200 OK", Http(port, "GET", "/").Status);
            var stateRefused = Http(port, "GET", "/api/state");
            Assert.Equal("403 Forbidden", stateRefused.Status);
            Assert.Contains("not paired", stateRefused.Body);
            Assert.Equal("200 OK", Http(port, "GET", "/api/state", "", ("X-Patterns-Token", Token)).Status);
            Assert.Equal("200 OK", Http(port, "GET", "/api/state", "", ("Cookie", "patterns-token=" + Token)).Status);
            Assert.Equal("403 Forbidden", Http(port, "GET", "/pgm.jpg").Status);
            Assert.Equal("403 Forbidden", Http(port, "GET", "/mv.jpg?w=320").Status);
            Assert.Equal("200 OK", Http(port, "GET", "/pgm.jpg", "", ("Cookie", "patterns-token=" + Token)).Status);

            // A token in the URL is not a token: nothing reads query strings for credentials, and the route is not even there.
            Assert.NotEqual("200 OK", Http(port, "POST", "/api/cmd?token=" + Token, "BLACKOUT OFF").Status);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.State.Blackout);

            // This machine's own browser — the desk's pages opened on the desk — never needs the token.
            ControlService.TrustLoopback = true;
            Assert.Equal("200 OK", Http(port, "POST", "/api/cmd", "BLACKOUT OFF").Status);
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

    [AvaloniaFact]
    public void APageFromAnotherOriginCannotPostToTheDeskWhileACurlAndTheDesksOwnPageCan()
    {
        var (services, vm) = Boot();
        var trusted = ControlService.TrustLoopback;
        ControlService.TrustLoopback = true;                                        // the show machine's own browser: trusted, and still not a way in for another site's page
        try
        {
            vm.State.Control.HttpPort = FreePort();
            vm.State.Control.TcpPort = FreePort();
            Dispatcher.UIThread.RunJobs();
            var port = vm.State.Control.HttpPort;

            var evil = Http(port, "POST", "/api/cmd", "BLACKOUT ON", ("Origin", "http://evil.example"));
            Assert.Equal("403 Forbidden", evil.Status);
            Assert.Contains("another origin", evil.Body);
            Assert.Equal("403 Forbidden", Http(port, "POST", "/api/cmd", "BLACKOUT ON", ("Sec-Fetch-Site", "same-site")).Status);
            Assert.Equal("403 Forbidden", Http(port, "POST", "/api/cmd", "BLACKOUT ON", ("Origin", "null")).Status);
            Assert.Equal("403 Forbidden", Http(port, "POST", "/api/cmd", "STATUS", ("Origin", "http://evil.example")).Status);
            Assert.Equal("403 Forbidden", Http(port, "POST", "/api/stage/ack", "some-id", ("Origin", "http://evil.example")).Status);
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.State.Blackout);

            var own = Http(port, "POST", "/api/cmd", "BLACKOUT ON", ("Origin", "http://test"), ("Sec-Fetch-Site", "same-origin"));
            Assert.Equal("200 OK", own.Status);                                     // the desk's own page: same origin as the Host it was served from
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.State.Blackout);

            var curl = Http(port, "POST", "/api/cmd", "BLACKOUT OFF");
            Assert.Equal("200 OK", curl.Status);                                    // a curl, a device, a script: no Origin, not touched
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.State.Blackout);

            // A deliberate client sends X-Patterns-Client and passes whatever its origin: a browser cannot send that header cross-origin without a CORS grant the desk never gives.
            var client = Http(port, "POST", "/api/cmd", "BLACKOUT ON", ("Origin", "http://tool.example"), ("X-Patterns-Client", "tool"));
            Assert.Equal("200 OK", client.Status);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.State.Blackout);
            Assert.Equal("200 OK", Http(port, "POST", "/api/cmd", "BLACKOUT OFF").Status);
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.State.Blackout);
        }
        finally
        {
            ControlService.TrustLoopback = trusted;
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void TheAdminPasscodeRidesInAHeaderAndNeverInTheUrl()
    {
        var (services, vm) = Boot();
        try
        {
            vm.State.Control.HttpPort = FreePort();
            vm.State.Control.TcpPort = FreePort();
            vm.State.Install.AdminPasscode = "open-sesame";
            Dispatcher.UIThread.RunJobs();
            var port = vm.State.Control.HttpPort;

            var inUrl = Http(port, "GET", "/api/admin/log?pass=open-sesame");
            Assert.Equal("403 Forbidden", inUrl.Status);                       // the URL is not where a passcode goes
            var inHeader = Http(port, "GET", "/api/admin/log", "", ("X-Patterns-Pass", "open-sesame"));
            Assert.Equal("200 OK", inHeader.Status);
            var bundleInUrl = Http(port, "GET", "/support-bundle.zip?pass=open-sesame");
            Assert.Equal("403 Forbidden", bundleInUrl.Status);
            var bundle = Http(port, "GET", "/support-bundle.zip", "", ("X-Patterns-Pass", "open-sesame"));
            Assert.Equal("200 OK", bundle.Status);
            Assert.StartsWith("PK", bundle.Body);                              // a zip

            // The admin page's own script sends the header and never builds a ?pass= URL.
            var page = Http(port, "GET", "/admin").Body;
            Assert.Contains("'X-Patterns-Pass': pass", page);
            Assert.DoesNotContain("?pass=", page);
        }
        finally
        {
            vm.State.Install.AdminPasscode = "";
            Dispatcher.UIThread.RunJobs();
        }
    }

    [AvaloniaFact]
    public void TheControlPortsBindWhereTheRemotePageSaysAndThePagesCarryTheToken()
    {
        var (services, vm) = Boot();
        try
        {
            vm.State.Control.HttpPort = FreePort();
            vm.State.Control.TcpPort = FreePort();
            vm.State.Control.Bind = "127.0.0.1";
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("at 127.0.0.1 only", services.Control.Status);
            services.Control.ForgetRemoteUrls();
            Assert.Equal(new[] { $"http://127.0.0.1:{vm.State.Control.HttpPort}/" }, services.Control.RemoteUrls());
            using var wire = new Wire(vm.State.Control.TcpPort);
            wire.Send("PING");
            Assert.Equal("OK PONG", wire.ReadResponse());

            // Every page that runs a verb sends the token it keeps and asks for it on a 403.
            foreach (var path in new[] { "/", "/run", "/stage", "/pad" })
            {
                var page = Http(vm.State.Control.HttpPort, "GET", path);
                Assert.Equal("200 OK", page.Status);
                Assert.Contains("X-Patterns-Token", page.Body);
                Assert.Contains("patterns.token", page.Body);
                Assert.Contains("403", page.Body);
            }

            // Round 85 (P1-06): a bind that is not an address opens nothing — never every interface — and says why.
            vm.State.Control.Bind = "10.0.0";
            Dispatcher.UIThread.RunJobs();
            Assert.StartsWith("Remote control closed — '10.0.0' is not an address", services.Control.Status);
            Assert.StartsWith("'10.0.0' is not an address", services.Control.BindProblem);
            Assert.False(services.Control.StartFailed);                                            // not a failure to retry: the setting is wrong until it is changed
            using (var probe = new TcpClient())
            {
                Assert.ThrowsAny<SocketException>(() => Pump(probe.ConnectAsync(IPAddress.Loopback, vm.State.Control.TcpPort)));
            }
            using (var probe = new TcpClient())
            {
                Assert.ThrowsAny<SocketException>(() => Pump(probe.ConnectAsync(IPAddress.Loopback, vm.State.Control.HttpPort)));
            }
            var closedRow = Assert.Single(SuperCheck.Run(services.Metrics.GatherFacts()).Rows, r => r.Section == "REMOTE" && r.Item == "Remote control");
            Assert.Equal(CheckLight.Red, closedRow.Light);
            Assert.Equal("closed — the bind is not an address", closedRow.Value);
            Assert.Contains("'10.0.0' is not an address", closedRow.Note);

            // The audience listener fails closed the same way, on its own bind, with the control ports untouched.
            vm.State.Control.Bind = "";
            vm.State.Control.AudienceEnabled = true;
            vm.State.Control.AudiencePort = FreePort();
            vm.State.Control.AudienceBind = "phones";
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.Control.AudienceListening);
            Assert.Contains("Audience closed — 'phones' is not an address", services.Control.Status);
            Assert.StartsWith("Web remote on port", services.Control.Status);
            Assert.Empty(services.Control.AudienceUrls());
            using (var stillOpen = new Wire(vm.State.Control.TcpPort))
            {
                stillOpen.Send("PING");
                Assert.Equal("OK PONG", stillOpen.ReadResponse());
            }
            var audienceRow = Assert.Single(SuperCheck.Run(services.Metrics.GatherFacts()).Rows, r => r.Section == "REMOTE" && r.Item == "Audience");
            Assert.Equal(CheckLight.Red, audienceRow.Light);
            Assert.Contains("'phones' is not an address", audienceRow.Note);
            vm.State.Control.AudienceBind = "";
            Dispatcher.UIThread.RunJobs();
            for (var i = 0; i < 400 && !services.Control.AudienceListening; i++)
            {
                Dispatcher.UIThread.RunJobs();
                Thread.Sleep(5);
            }
            Assert.True(services.Control.AudienceListening);
            Assert.NotEmpty(services.Control.AudienceUrls());
            Assert.DoesNotContain(SuperCheck.Run(services.Metrics.GatherFacts()).Rows, r => r.Section == "REMOTE" && r.Item == "Audience");
            vm.State.Control.AudienceEnabled = false;

            vm.State.Control.Bind = "";
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain("only", services.Control.Status);
            Assert.DoesNotContain("closed", services.Control.Status);
            services.Control.ForgetRemoteUrls();
            Assert.Contains($"http://localhost:{vm.State.Control.HttpPort}/", services.Control.RemoteUrls());
        }
        finally
        {
            vm.State.Control.Bind = "";
            vm.State.Control.AudienceBind = "";
            vm.State.Control.AudienceEnabled = false;
            Dispatcher.UIThread.RunJobs();
        }
    }
}
