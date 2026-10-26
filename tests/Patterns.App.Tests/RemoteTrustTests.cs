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
                if (!line!.StartsWith("STATE ") && !line.StartsWith("NONCE ")) return line;   // round 87: a paired desk's nonce is not an answer
            }
        }

        /// <summary>Null once the desk has closed the connection.</summary>
        public string? ReadLineOrEnd()
        {
            while (true)
            {
                var line = Pump(_reader.ReadLineAsync());
                if (line is null || (!line.StartsWith("STATE ") && !line.StartsWith("NONCE "))) return line;
            }
        }

        public void Dispose() => _client.Dispose();
    }

    /// <summary>
    /// One HTTP request on the control port, as a phone or a curl would send it: the status and the body. Its Host is "test"
    /// unless a header names one; a Host header with no value sends none at all.
    /// </summary>
    private static (string Status, string Body) Http(int port, string method, string path, string body = "", params (string Name, string Value)[] headers)
    {
        using var client = new TcpClient();
        Pump(client.ConnectAsync(IPAddress.Loopback, port));
        using var stream = client.GetStream();
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var ownHost = headers.Any(h => h.Name.Equals("Host", StringComparison.OrdinalIgnoreCase));
        var head = new StringBuilder($"{method} {path} HTTP/1.1\r\n{(ownHost ? "" : "Host: test\r\n")}Content-Length: {bodyBytes.Length}\r\n");
        foreach (var (name, value) in headers.Where(h => h.Value.Length > 0)) head.Append($"{name}: {value}\r\n");
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

            // Round 85 (P1-02): a query wants the token too — STATUS answers the JSON /api/state has asked it for since round 83 — and so does the cue list.
            var query = Http(port, "POST", "/api/cmd", "STATUS");
            Assert.Equal("403 Forbidden", query.Status);
            Assert.Contains("not paired", query.Body);
            var queryPaired = Http(port, "POST", "/api/cmd", "STATUS", ("X-Patterns-Token", Token));
            Assert.Equal("200 OK", queryPaired.Status);
            Assert.Contains("\"ok\":true", queryPaired.Body);
            Assert.Equal("403 Forbidden", Http(port, "GET", "/api/cues").Status);
            Assert.Equal("200 OK", Http(port, "GET", "/api/cues", "", ("Host", $"localhost:{port}"), ("Cookie", "patterns-token=" + Token)).Status);   // the cookie, under the desk's own name (round 89)

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
            Assert.Equal("200 OK", Http(port, "GET", "/api/state", "", ("Host", $"localhost:{port}"), ("Cookie", "patterns-token=" + Token)).Status);
            Assert.Equal("403 Forbidden", Http(port, "GET", "/pgm.jpg").Status);
            Assert.Equal("403 Forbidden", Http(port, "GET", "/mv.jpg?w=320").Status);
            Assert.Equal("200 OK", Http(port, "GET", "/pgm.jpg", "", ("Host", $"localhost:{port}"), ("Cookie", "patterns-token=" + Token)).Status);

            // A token in the URL is not a token: nothing reads query strings for credentials, and the route is not even there.
            Assert.NotEqual("200 OK", Http(port, "POST", "/api/cmd?token=" + Token, "BLACKOUT OFF").Status);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.State.Blackout);

            // This machine's own browser — the desk's pages opened on the desk, by a name that is the desk's — never needs the token.
            ControlService.TrustLoopback = true;
            Assert.Equal("200 OK", Http(port, "POST", "/api/cmd", "BLACKOUT OFF", ("Host", $"localhost:{port}")).Status);
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
    public void ARequestOverLoopbackUnderANameThatIsNotThisDesksIsAnsweredAsAnotherMachinesWithWordsAndOneLogLine()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        var trusted = ControlService.TrustLoopback;
        ControlService.TrustLoopback = true;                                        // the show machine's own browser is trusted — by its own names only
        try
        {
            vm.State.Control.HttpPort = FreePort();
            vm.State.Control.TcpPort = FreePort();
            vm.State.Control.Token = Token;
            Dispatcher.UIThread.RunJobs();
            var port = vm.State.Control.HttpPort;
            var rebound = $"evil.example:{port}";
            var log = Path.Combine(b.Dir, "patterns.log");
            int Said() => File.ReadAllLines(log).Count(l => l.Contains("named this desk", StringComparison.Ordinal));
            var saidBefore = Said();

            // This desk by its own names, over loopback: trusted as before.
            foreach (var host in new[] { $"localhost:{port}", $"127.0.0.1:{port}", $"[::1]:{port}", $"{Environment.MachineName.ToLowerInvariant()}:{port}" })
            {
                Assert.Equal("200 OK", Http(port, "GET", "/api/state", "", ("Host", host)).Status);
            }
            Assert.Equal("200 OK", Http(port, "POST", "/api/cmd", "BLACKOUT ON", ("Host", $"localhost:{port}")).Status);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.State.Blackout);
            Assert.Equal(saidBefore, Said());

            // A page whose name was rebound to 127.0.0.1: same-origin to itself, so round 83's rule lets its POST through — and its
            // Host is still its own name, so it is another machine's request: the token or nothing, and it hears why.
            var post = Http(port, "POST", "/api/cmd", "BLACKOUT OFF", ("Host", rebound), ("Origin", "http://" + rebound), ("Sec-Fetch-Site", "same-origin"));
            Assert.Equal("403 Forbidden", post.Status);
            Assert.Contains("\"ok\":false", post.Body);
            Assert.Contains("not paired", post.Body);
            Assert.Contains("a name that is not its own", post.Body);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.State.Blackout);                                   // nothing ran
            var read = Http(port, "GET", "/api/state", "", ("Host", rebound));
            Assert.Equal("403 Forbidden", read.Status);
            Assert.Contains("a name that is not its own", read.Body);
            Assert.DoesNotContain("\"blackout\"", read.Body);
            Assert.Equal("403 Forbidden", Http(port, "GET", "/api/context", "", ("Host", rebound)).Status);
            Assert.Equal("403 Forbidden", Http(port, "GET", "/pgm.jpg", "", ("Host", rebound)).Status);
            Assert.Contains("a name that is not its own", Http(port, "POST", "/api/stage/ack", "some-id", ("Host", rebound)).Body);
            Assert.Equal("200 OK", Http(port, "GET", "/", "", ("Host", rebound)).Status);     // the pages themselves are open to any machine

            // A forwarder's port, a scheme's port, a trailing dot, no Host at all: none names this desk.
            foreach (var host in new[] { $"localhost:{port + 1}", "localhost", $"localhost.:{port}", "" })
            {
                Assert.Equal("403 Forbidden", Http(port, "GET", "/api/state", "", ("Host", host)).Status);
            }

            // The pages' cookie wants this desk's name too; the token in its header pairs any machine, whatever its name.
            ControlService.TrustLoopback = false;
            Assert.Equal("200 OK", Http(port, "GET", "/api/state", "", ("Host", $"localhost:{port}"), ("Cookie", "patterns-token=" + Token)).Status);
            Assert.Equal("200 OK", Http(port, "GET", "/pgm.jpg", "", ("Host", $"127.0.0.1:{port}"), ("Cookie", "patterns-token=" + Token)).Status);
            var cookie = Http(port, "GET", "/api/state", "", ("Host", rebound), ("Cookie", "patterns-token=" + Token));
            Assert.Equal("403 Forbidden", cookie.Status);
            Assert.Contains("a name that is not its own", cookie.Body);
            Assert.Equal("403 Forbidden", Http(port, "GET", "/pgm.jpg", "", ("Host", rebound), ("Cookie", "patterns-token=" + Token)).Status);
            Assert.Equal("200 OK", Http(port, "GET", "/api/state", "", ("Host", rebound), ("X-Patterns-Token", Token)).Status);
            var paired = Http(port, "POST", "/api/cmd", "BLACKOUT OFF", ("Host", rebound), ("X-Patterns-Token", Token));
            Assert.Equal("200 OK", paired.Status);
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.State.Blackout);
            // A machine with no token, under any name, hears the plain words: its name changed nothing.
            var plain = Http(port, "GET", "/api/state", "", ("Host", rebound));
            Assert.Equal("403 Forbidden", plain.Status);
            Assert.Contains("not paired", plain.Body);
            Assert.DoesNotContain("a name that is not its own", plain.Body);

            // One log line for the address (loopback), naming the Host it used, however many requests it sent.
            Assert.Equal(saidBefore + 1, Said());
            var line = File.ReadAllLines(log).Last(l => l.Contains("named this desk", StringComparison.Ordinal));
            Assert.Contains("from 127.0.0.1", line);
            Assert.Contains($"'{rebound}'", line);
            Assert.DoesNotContain(Token, File.ReadAllText(log));

            // An open desk (no token) answers every machine, under any name, as it always has — a curl, a device, a Companion.
            vm.State.Control.Token = "";
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("200 OK", Http(port, "GET", "/api/state", "", ("Host", rebound)).Status);
            Assert.Equal("200 OK", Http(port, "GET", "/api/state", "", ("Host", rebound), ("X-Patterns-Client", "tool")).Status);
            // A browser's page under a name that is not the desk's is no machine of the network's: it is a page whose name was
            // rebound to this desk, from any site a browser on the network has open. The show's routes refuse it with words that
            // say what to do and never ask for a token (there is none to give); the page itself still loads.
            var openPost = Http(port, "POST", "/api/cmd", "BLACKOUT ON", ("Host", rebound), ("Origin", "http://" + rebound), ("Sec-Fetch-Site", "same-origin"));
            Assert.Equal("403 Forbidden", openPost.Status);
            Assert.Contains("a browser named the desk by a name that is not its own", openPost.Body);   // (the JSON escapes its apostrophes and dashes)
            Assert.DoesNotContain("not paired", openPost.Body);
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.State.Blackout);                                  // nothing ran
            var openRead = Http(port, "GET", "/api/state", "", ("Host", rebound), ("Sec-Fetch-Site", "same-origin"));
            Assert.Equal("403 Forbidden", openRead.Status);
            Assert.DoesNotContain("\"blackout\"", openRead.Body);
            foreach (var path in new[] { "/api/context", "/api/cues", "/pgm.jpg", "/mv.jpg?w=320" })
                Assert.Equal("403 Forbidden", Http(port, "GET", path, "", ("Host", rebound), ("Sec-Fetch-Site", "same-origin")).Status);
            Assert.Contains("a browser named the desk by a name that is not its own", Http(port, "POST", "/api/stage/ack", "some-id", ("Host", rebound), ("Sec-Fetch-Site", "same-origin")).Body);
            Assert.Equal("200 OK", Http(port, "GET", "/", "", ("Host", rebound), ("Sec-Fetch-Site", "none")).Status);
            // Under the desk's own names a browser is answered on an open desk, from this machine or another.
            Assert.Equal("200 OK", Http(port, "GET", "/api/state", "", ("Host", $"localhost:{port}"), ("Sec-Fetch-Site", "same-origin")).Status);
            Assert.Equal("200 OK", Http(port, "GET", "/api/state", "", ("Host", $"{Environment.MachineName.ToLowerInvariant()}:{port}"), ("Sec-Fetch-Site", "same-origin")).Status);
            // One more log line for the address, of its own kind: the browser under another name on an open desk.
            Assert.Equal(saidBefore + 2, Said());
            var open = File.ReadAllLines(log).Last(l => l.Contains("named this desk", StringComparison.Ordinal));
            Assert.Contains("with no pairing token set", open);
            Assert.Contains($"'{rebound}'", open);
        }
        finally
        {
            ControlService.TrustLoopback = trusted;
            vm.State.Control.Token = "";
            Dispatcher.UIThread.RunJobs();
            b.Dispose();
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
            var desk = ("Host", $"localhost:{port}");                               // this machine's browser and curl, by the desk's own name (round 89)

            var evil = Http(port, "POST", "/api/cmd", "BLACKOUT ON", desk, ("Origin", "http://evil.example"));
            Assert.Equal("403 Forbidden", evil.Status);
            Assert.Contains("another origin", evil.Body);
            Assert.Equal("403 Forbidden", Http(port, "POST", "/api/cmd", "BLACKOUT ON", desk, ("Sec-Fetch-Site", "same-site")).Status);
            Assert.Equal("403 Forbidden", Http(port, "POST", "/api/cmd", "BLACKOUT ON", desk, ("Origin", "null")).Status);
            Assert.Equal("403 Forbidden", Http(port, "POST", "/api/cmd", "STATUS", desk, ("Origin", "http://evil.example")).Status);
            Assert.Equal("403 Forbidden", Http(port, "POST", "/api/stage/ack", "some-id", desk, ("Origin", "http://evil.example")).Status);
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.State.Blackout);

            var own = Http(port, "POST", "/api/cmd", "BLACKOUT ON", desk, ("Origin", $"http://localhost:{port}"), ("Sec-Fetch-Site", "same-origin"));
            Assert.Equal("200 OK", own.Status);                                     // the desk's own page: same origin as the Host it was served from
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.State.Blackout);

            var curl = Http(port, "POST", "/api/cmd", "BLACKOUT OFF", desk);
            Assert.Equal("200 OK", curl.Status);                                    // a curl, a device, a script: no Origin, not touched
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.State.Blackout);

            // A deliberate client sends X-Patterns-Client and passes round 83's rule whatever its origin: a browser cannot send that
            // header cross-origin without a CORS grant the desk never gives. An Origin that is not the desk's is no page of this
            // machine's own, though (round 89, PeerLink profile 13.3.4): on this paired desk it presents the token, as any client of yours does.
            var foreign = Http(port, "POST", "/api/cmd", "BLACKOUT ON", desk, ("Origin", "http://tool.example"), ("X-Patterns-Client", "tool"));
            Assert.Equal("403 Forbidden", foreign.Status);
            Assert.Contains("not paired", foreign.Body);
            Assert.Contains("a page from another origin is not this desk", foreign.Body);
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.State.Blackout);
            var client = Http(port, "POST", "/api/cmd", "BLACKOUT ON", desk, ("Origin", "http://tool.example"), ("X-Patterns-Client", "tool"), ("X-Patterns-Token", vm.State.Control.Token));
            Assert.Equal("200 OK", client.Status);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.State.Blackout);
            Assert.Equal("200 OK", Http(port, "POST", "/api/cmd", "BLACKOUT OFF", desk).Status);
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
    public void ASiteOpenInThisMachinesBrowserGetsNoTrustForThisMachineEvenUnderTheDesksOwnName()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        var trusted = ControlService.TrustLoopback;
        ControlService.TrustLoopback = true;                                        // the show machine's own browser is trusted — its own pages only
        try
        {
            vm.State.Control.HttpPort = FreePort();
            vm.State.Control.TcpPort = FreePort();
            vm.State.Control.Token = Token;
            Dispatcher.UIThread.RunJobs();
            var port = vm.State.Control.HttpPort;
            var desk = ("Host", $"localhost:{port}");
            var log = Path.Combine(b.Dir, "patterns.log");
            int Said() => File.ReadAllLines(log).Count(l => l.Contains("a page from another origin", StringComparison.Ordinal));
            var saidBefore = Said();

            // The desk's own page (same-origin) and a typed address (none): this machine's, trusted as before.
            Assert.Equal("200 OK", Http(port, "GET", "/api/state", "", desk, ("Sec-Fetch-Site", "same-origin")).Status);
            Assert.Equal("200 OK", Http(port, "GET", "/pgm.jpg", "", desk, ("Sec-Fetch-Site", "none")).Status);

            // <img src="http://localhost:port/mv.jpg?w=1920"> on any site this machine's browser has open: a page of another
            // origin is no page of this machine's own, so it gets no trust — no picture rendered on its demand, no state long-poll
            // whose timing says when the show moves.
            var img = Http(port, "GET", "/mv.jpg?w=1920", "", desk, ("Sec-Fetch-Site", "cross-site"));
            Assert.Equal("403 Forbidden", img.Status);
            Assert.Contains("a page from another origin", img.Body);
            var poll = Http(port, "GET", "/api/state?since=0", "", desk, ("Sec-Fetch-Site", "cross-site"));
            Assert.Equal("403 Forbidden", poll.Status);
            Assert.DoesNotContain("\"blackout\"", poll.Body);
            // Another app on this host (same-site, another port) carries the pages' cookie with it: the cookie is the desk's own pages' only.
            Assert.Equal("403 Forbidden", Http(port, "GET", "/api/state", "", desk, ("Sec-Fetch-Site", "same-site"), ("Cookie", "patterns-token=" + Token)).Status);
            Assert.Equal("403 Forbidden", Http(port, "GET", "/api/cues", "", desk, ("Origin", "http://localhost:3000")).Status);
            Assert.Equal("403 Forbidden", Http(port, "POST", "/api/stage/ack", "some-id", desk, ("Origin", "http://localhost:3000"), ("X-Patterns-Client", "tool")).Status);
            // The token in its header pairs it — any client of yours, whatever its origin.
            Assert.Equal("200 OK", Http(port, "GET", "/api/state", "", desk, ("Sec-Fetch-Site", "cross-site"), ("X-Patterns-Token", Token)).Status);

            // One log line for the address, naming what the page said it was; never the token.
            Assert.Equal(saidBefore + 1, Said());
            var line = File.ReadAllLines(log).Last(l => l.Contains("a page from another origin", StringComparison.Ordinal));
            Assert.Contains("from 127.0.0.1", line);
            Assert.Contains("'cross-site'", line);
            Assert.DoesNotContain(Token, File.ReadAllText(log));
        }
        finally
        {
            ControlService.TrustLoopback = trusted;
            vm.State.Control.Token = "";
            Dispatcher.UIThread.RunJobs();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TrafficForwardedInOverLoopbackIsNotThisMachinesForRequireHttpsOrForTheTokenSeenInClear()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        var trusted = ControlService.TrustLoopback;
        ControlService.TrustLoopback = true;                                        // this machine may stay plain — by the desk's own names
        try
        {
            vm.State.Control.HttpPort = FreePort();
            vm.State.Control.TcpPort = FreePort();
            vm.State.Control.Token = Token;
            vm.State.Control.RequireHttps = true;
            Dispatcher.UIThread.RunJobs();
            var port = vm.State.Control.HttpPort;

            // This machine's own browser, by the desk's name: plain is allowed, and its token crossed no network.
            Assert.Equal("200 OK", Http(port, "GET", "/api/state", "", ("Host", $"localhost:{port}"), ("X-Patterns-Token", Token)).Status);
            Assert.Equal("200 OK", Http(port, "GET", "/tls", "", ("Host", $"127.0.0.1:{port}")).Status);
            Assert.Empty(services.Kernel.TokenSeenInClear.For(Token));

            // A port proxy or a tunnel carrying another machine's plain traffic in over loopback: its Host is the name and port
            // that machine typed. It is not this machine's — Require HTTPS refuses it, and the token it carried crossed the
            // network in clear, so it is marked (rotate it: NEW TOKEN).
            var forwarded = Http(port, "GET", "/api/state", "", ("Host", "show-gateway:8080"), ("X-Patterns-Token", Token));
            Assert.Equal("403 Forbidden", forwarded.Status);
            Assert.Contains("requires HTTPS", forwarded.Body, StringComparison.Ordinal);
            var seen = Assert.Single(services.Kernel.TokenSeenInClear.For(Token));
            Assert.Equal(("127.0.0.1", "web"), (seen.Address, seen.Via));
            Assert.Contains("requires HTTPS", Http(port, "GET", "/tls", "", ("Host", $"localhost:{port + 1}")).Body, StringComparison.Ordinal);   // another port: a forwarder's
            Assert.Contains("requires HTTPS", Http(port, "GET", "/tls", "", ("Host", $"localhost:{port}"), ("Sec-Fetch-Site", "cross-site")).Body, StringComparison.Ordinal);   // another site's page: no grant
        }
        finally
        {
            ControlService.TrustLoopback = trusted;
            vm.State.Control.RequireHttps = false;
            vm.State.Control.Token = "";
            Dispatcher.UIThread.RunJobs();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ABrowsersPostToTheWirePortIsClosedOnItsRequestLineBeforeItsBodyIsReadAsALine()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        var trusted = ControlService.TrustLoopback;
        ControlService.TrustLoopback = true;                                        // the harm needs this machine's trust: the body would run as its line
        try
        {
            vm.State.Control.HttpPort = FreePort();
            vm.State.Control.TcpPort = FreePort();
            vm.State.Control.Token = Token;
            Dispatcher.UIThread.RunJobs();
            var tcp = vm.State.Control.TcpPort;
            var log = Path.Combine(b.Dir, "patterns.log");
            int Said() => File.ReadAllLines(log).Count(l => l.Contains("sent an HTTP request to the Companion port", StringComparison.Ordinal));
            var saidBefore = Said();

            // A no-cors POST from any page in this machine's browser to http://127.0.0.1:<wire>/ with a text/plain body.
            using (var page = new Wire(tcp))
            {
                page.Send($"POST / HTTP/1.1\r\nHost: 127.0.0.1:{tcp}\r\nOrigin: http://evil.example\r\nSec-Fetch-Site: cross-site\r\nContent-Type: text/plain\r\nContent-Length: 12\r\n\r\nBLACKOUT ON");
                Assert.Equal(ControlProtocol.Err(ControlProtocol.HttpOnWire), page.ReadLineOrEnd());
                string? end;
                try
                {
                    end = page.ReadLineOrEnd();
                }
                catch (IOException)
                {
                    end = null;                                                     // a reset is the door closed too: the page's bytes were left unread
                }
                Assert.Null(end);
            }
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.State.Blackout);                                  // the body never ran
            Assert.Equal(saidBefore + 1, Said());

            // The same words sent as a line by this machine's own client run: the door, not the words, was what the page lacked.
            using var own = new Wire(tcp);
            own.Send("BLACKOUT ON");
            Assert.StartsWith("OK", own.ReadResponse());
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.State.Blackout);
            own.Send("BLACKOUT OFF");
            Assert.StartsWith("OK", own.ReadResponse());
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.State.Blackout);
        }
        finally
        {
            ControlService.TrustLoopback = trusted;
            vm.State.Control.Token = "";
            Dispatcher.UIThread.RunJobs();
            b.Dispose();
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

    /// <summary>
    /// Round 90: the ADMIN page's log tail masks what the support bundle masks. A line an older build wrote with the
    /// twin's key in it — the bench host's first boot logged "this desk was given the key xpbe-…" — leaves the machine
    /// with the mask where the key was, and so does any other secret value of the show; the rest of the line reads as
    /// written.
    /// </summary>
    [AvaloniaFact]
    public void TheAdminPagesLogTailMasksTheShowsSecretsAsTheBundleDoes()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            vm.State.Control.HttpPort = FreePort();
            vm.State.Control.TcpPort = FreePort();
            vm.State.Install.AdminPasscode = "open-sesame";
            vm.State.Twin.Key = "xpbe-4kq2-m7rd-9tzc";
            Dispatcher.UIThread.RunJobs();
            // As an older build wrote them: the made key in the twin's line, the passcode in another.
            Log.Info("Twin: this desk was given the key xpbe-4kq2-m7rd-9tzc — a caller node that links needs the same key (Machine page, TWIN).");
            Log.Info("Remote admin: RESTART open-sesame");

            var tail = Http(vm.State.Control.HttpPort, "GET", "/api/admin/log", "", ("X-Patterns-Pass", "open-sesame"));
            Assert.Equal("200 OK", tail.Status);
            Assert.Contains($"Twin: this desk was given the key {Secrets.Mask} — a caller node that links needs the same key", tail.Body, StringComparison.Ordinal);
            Assert.Contains($"Remote admin: RESTART {Secrets.Mask}", tail.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("xpbe-4kq2-m7rd-9tzc", tail.Body, StringComparison.Ordinal);
            Assert.DoesNotContain("open-sesame", tail.Body, StringComparison.Ordinal);
        }
        finally
        {
            b.Dispose();
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

            // Every page that runs a verb sends the token it keeps and asks for it on a 403; round 85: the pages that read the
            // state (the remote, the admin) send it on those reads too, and the pages with a picture set it as the cookie.
            foreach (var path in new[] { "/", "/run", "/stage", "/pad", "/admin" })
            {
                var page = Http(vm.State.Control.HttpPort, "GET", path);
                Assert.Equal("200 OK", page.Status);
                Assert.Contains("X-Patterns-Token", page.Body);
                Assert.Contains("patterns.token", page.Body);
                Assert.Contains("403", page.Body);
            }
            foreach (var path in new[] { "/", "/admin" })
            {
                Assert.Contains("fetch('/api/state', { headers: hdr() })", Http(vm.State.Control.HttpPort, "GET", path).Body);
            }
            foreach (var path in new[] { "/", "/run", "/multiview" })
            {
                Assert.Contains("patterns-token=", Http(vm.State.Control.HttpPort, "GET", path).Body);
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
