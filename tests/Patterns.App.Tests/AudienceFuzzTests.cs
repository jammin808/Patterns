using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The audience port sent what a room of untrusted phones might send: garbage, heads without
/// end, a thousand headers, bodies that never come, bodies that lie about their length, JSON
/// that is not, names in every alphabet — and after all of it, a phone that joins and is seated.
/// </summary>
public class AudienceFuzzTests
{
    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static void PumpUntil(Func<bool> condition, int timeoutMs = 20000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
            if (Environment.TickCount64 > deadline) throw new TimeoutException("the condition never held");
        }
    }

    /// <summary>Bytes on the wire, exactly as given, and whatever came back before the port closed or went quiet — off the UI thread, so the desk can answer.</summary>
    private static string Raw(int port, byte[] request, int waitMs = 4000)
        => TestApp.Pump(Task.Run(() =>
        {
            using var tcp = new TcpClient();
            tcp.Connect(IPAddress.Loopback, port);
            tcp.ReceiveTimeout = waitMs;
            using var s = tcp.GetStream();
            s.Write(request);
            var buf = new byte[65536];
            var total = 0;
            try
            {
                int n;
                while (total < buf.Length && (n = s.Read(buf, total, buf.Length - total)) > 0) total += n;
            }
            catch (IOException)
            {
                // quiet past the wait: what came is the answer
            }
            return Encoding.UTF8.GetString(buf, 0, total);
        }));

    private static string Raw(int port, string request, int waitMs = 4000) => Raw(port, Encoding.UTF8.GetBytes(request), waitMs);

    private static string Post(HttpClient http, string path, string body)
        => TestApp.Pump(http.PostAsync(path, new StringContent(body)).ContinueWith(t => t.Result.Content.ReadAsStringAsync().Result));

    [AvaloniaFact]
    public void TheAudiencePortAnswersEveryShapeItIsSentWithAStatusOrAClosedDoorAndSeatsThePhoneAfter()
    {
        var audiencePort = FreePort();
        var b = TestApp.Boot("patterns-tests-fuzz-", dir =>
        {
            var s = SettingsStore.Fresh();
            s.Name = "Gala";
            s.Control.Enabled = true;
            s.Control.HttpPort = FreePort();
            s.Control.TcpPort = FreePort();
            s.Control.AudienceEnabled = true;
            s.Control.AudiencePort = audiencePort;
            s.Control.AudienceMaxPlayers = 60;
            s.Twin.AcceptCallers = false;
            s.Watchdog.BeaconListenPort = FreePort();
            s.Watchdog.BeaconPort = s.Watchdog.BeaconListenPort;
            File.WriteAllText(Path.Combine(dir, "patterns.settings.json"), JsonUtil.Serialize(s));
        }, NodeKind.Arcade);
        try
        {
            var (services, _, _) = b;
            var play = services.Play;
            // Nobody here waits five seconds for a phone that stopped talking: one is plenty.
            services.Control.AudienceLimits = HttpLimits.Audience with { HeadSeconds = 1, BodySeconds = 1 };
            PumpUntil(() => services.Control.AudienceListening);
            using var phone = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{audiencePort}/"), Timeout = TimeSpan.FromSeconds(8) };

            // A name with an accent and an emoji: the body is bytes, not characters, and the join lands with the name whole.
            var joined = Post(phone, "api/play/join", $"{{\"nick\":\"Zoë 🎉\",\"room\":\"{play.Code}\"}}");
            Assert.Contains("\"ok\":true", joined);
            using (var doc = JsonDocument.Parse(joined))
            {
                var token = doc.RootElement.GetProperty("token").GetString();
                Assert.Equal("Zoë 🎉", play.Room.Find(token)?.Nick);
            }

            // Garbage and half-requests: a status each, never a wait.
            Assert.StartsWith("HTTP/1.1 400", Raw(audiencePort, new byte[] { 0, 0xFF, 0xFE, 0x20, 0x67, 0x0D, 0x0A, 0x0D, 0x0A }));
            Assert.StartsWith("HTTP/1.1 400", Raw(audiencePort, "GET\r\n\r\n"));
            Assert.StartsWith("HTTP/1.1 400", Raw(audiencePort, "GET /play HTTP/1.1\r\nno colon\r\n\r\n"));
            Assert.StartsWith("HTTP/1.1 400", Raw(audiencePort, "POST /api/play/join HTTP/1.1\r\nContent-Length: abc\r\n\r\n{}"));
            Assert.StartsWith("HTTP/1.1 400", Raw(audiencePort, "POST /api/play/join HTTP/1.1\r\nContent-Length: -5\r\n\r\n{}"));
            var big = Raw(audiencePort, "POST /api/play/join HTTP/1.1\r\nContent-Length: 999999999\r\n\r\n{}");
            Assert.StartsWith("HTTP/1.1 413", big);
            Assert.Contains("999999999", big);
            // A request line the size of a novel, and a head of a thousand headers: refused at the limit, not buffered to it.
            Assert.StartsWith("HTTP/1.1 431", Raw(audiencePort, "GET /" + new string('a', 65536) + " HTTP/1.1\r\n\r\n"));
            var crowded = new StringBuilder("GET /play HTTP/1.1\r\n");
            for (var i = 0; i < 1000; i++) crowded.Append($"H{i}: v\r\n");
            crowded.Append("\r\n");
            Assert.StartsWith("HTTP/1.1 431", Raw(audiencePort, crowded.ToString()));
            // The other port never answers the room, whatever the shape.
            Assert.Equal(0, services.Control.AudienceConnections);

            // Slowloris: three phones that send half a head and nothing more hold three seats — for one second.
            var slow = Enumerable.Range(0, 3).Select(_ =>
            {
                var tcp = new TcpClient();
                tcp.Connect(IPAddress.Loopback, audiencePort);
                tcp.GetStream().Write(Encoding.ASCII.GetBytes("GET /play HTTP/1.1\r\nHost: x\r\n"));
                return tcp;
            }).ToList();
            try
            {
                PumpUntil(() => services.Control.AudienceConnections == 3, 5000);
                PumpUntil(() => services.Control.AudienceConnections == 0, 5000);
            }
            finally
            {
                foreach (var tcp in slow) tcp.Dispose();
            }

            // A body that stops short of its own content length: answered with what came, after the body's second, the seat freed.
            var stopped = Raw(audiencePort, "POST /api/play/join HTTP/1.1\r\nContent-Length: 400\r\n\r\n{\"nick\":\"Half\"", waitMs: 4000);
            Assert.StartsWith("HTTP/1.1 200", stopped);
            Assert.Equal(0, services.Control.AudienceConnections);

            // JSON that is not, to every route: a plain answer each, never a fault.
            foreach (var path in new[] { "api/play/join", "api/play/answer", "api/play/say", "api/play/vote", "api/play/draughts" })
            {
                foreach (var body in new[] { "not json", "[1,2,3]", "{\"nick\":123,\"token\":[]}", "{\"token\":null}", "{\"a\":" + new string('[', 200) + new string(']', 200) + "}", "\"\"", "{" })
                {
                    var answer = Post(phone, path, body);
                    Assert.StartsWith("{", answer);
                    Assert.Contains("\"ok\":", answer);
                }
            }

            // Three hundred random requests — methods, paths, headers and bodies from a seeded die — and every one is answered or the door closed.
            var rng = new Random(1234);
            var methods = new[] { "GET", "POST", "PUT", "DELETE", "BREW", "get", "" };
            var paths = new[] { "/", "/play", "/api/play/state?rev=0", "/api/play/join", "/api/play/answer", "/api/cmd", "/api/state", "/host", "/../../etc/passwd", "/%00", "/play?" + new string('x', 3000), "" };
            var bodies = new[] { "", "{}", "{\"nick\":\"A\"}", "\0\0\0", new string('{', 500), "{\"token\":\"" + new string('t', 2000) + "\"}" };
            for (var i = 0; i < 300; i++)
            {
                var body = bodies[rng.Next(bodies.Length)];
                var req = new StringBuilder();
                req.Append(methods[rng.Next(methods.Length)]).Append(' ').Append(paths[rng.Next(paths.Length)]).Append(rng.Next(3) == 0 ? "\r\n" : " HTTP/1.1\r\n");
                var headers = rng.Next(0, 6);
                for (var h = 0; h < headers; h++) req.Append(rng.Next(4) switch { 0 => "Host: x\r\n", 1 => "Content-Length: " + rng.Next(-2, 40) + "\r\n", 2 => "X-Patterns-Client: 1\r\n", _ => new string('h', rng.Next(1, 300)) + ": v\r\n" });
                if (rng.Next(5) != 0) req.Append("Content-Length: ").Append(Encoding.UTF8.GetByteCount(body)).Append("\r\n");
                req.Append("\r\n").Append(body);
                var answer = Raw(audiencePort, req.ToString(), waitMs: 3000);
                Assert.True(answer.Length == 0 || answer.StartsWith("HTTP/1.1 "), $"request {i}: {answer[..Math.Min(answer.Length, 60)]}");
            }
            PumpUntil(() => services.Control.AudienceConnections == 0, 5000);

            // And after all of it, a phone joins and is seated, and the room still counts its own.
            var late = Post(phone, "api/play/join", $"{{\"nick\":\"Late arrival\",\"room\":\"{play.Code}\"}}");
            Assert.Contains("\"ok\":true", late);
            Assert.Contains(play.Room.Players, p => p.Nick == "Late arrival");
            Assert.Contains(play.Room.Players, p => p.Nick == "Zoë 🎉");
            Assert.True(play.Room.Players.Count <= 60);
            Assert.StartsWith("{", TestApp.Pump(phone.GetStringAsync("api/play/state?rev=0")));
            Assert.StartsWith("HTTP/1.1 404", Raw(audiencePort, "POST /api/play/state?rev=0 HTTP/1.1\r\nContent-Length: 0\r\n\r\n"));   // the room's state is read, never posted
        }
        finally
        {
            b.Dispose();
        }
    }

    /// <summary>A stream that hands out at most a few bytes per read: every line boundary lands mid-read somewhere.</summary>
    private sealed class Dribble : Stream
    {
        private readonly byte[] _bytes;
        private readonly int _most;
        private int _at;
        public Dribble(byte[] bytes, int most) { _bytes = bytes; _most = most; }
        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = Math.Min(Math.Min(count, _most), _bytes.Length - _at);
            Array.Copy(_bytes, _at, buffer, offset, n);
            _at += n;
            return n;
        }
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => _bytes.Length;
        public override long Position { get => _at; set => throw new NotSupportedException(); }
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }

    private static string? Line(BoundedLineReader reader) => reader.ReadLineAsync(CancellationToken.None).GetAwaiter().GetResult();

    [Fact]
    public void TheBoundedLineReaderSplitsLinesAcrossReadsAndRefusesOnePastTheCeiling()
    {
        var reader = new BoundedLineReader(new Dribble(Encoding.UTF8.GetBytes("first\r\nsecond\nZoë 🎉\n\nlast without newline"), 3), 32);
        Assert.Equal("first", Line(reader));
        Assert.Equal("second", Line(reader));
        Assert.Equal("Zoë 🎉", Line(reader));
        Assert.Equal("", Line(reader));
        Assert.Equal("last without newline", Line(reader));
        Assert.Null(Line(reader));
        Assert.Null(Line(reader));

        var exact = new BoundedLineReader(new MemoryStream(Encoding.ASCII.GetBytes(new string('a', 32) + "\n")), 32);
        Assert.Equal(32, Line(exact)!.Length);                                                  // at the ceiling is a line
        var over = new BoundedLineReader(new MemoryStream(Encoding.ASCII.GetBytes(new string('x', 33) + "\nok\n")), 32);
        Assert.Throws<InvalidDataException>(() => Line(over));                                 // one past it is not
        var neverEnds = new BoundedLineReader(new Dribble(Encoding.ASCII.GetBytes(new string('y', 100)), 7), 32);
        Assert.Throws<InvalidDataException>(() => Line(neverEnds));                            // nor one that never ends
        var raised = new BoundedLineReader(new MemoryStream(Encoding.ASCII.GetBytes("hi\n" + new string('z', 100) + "\n")), 8);
        Assert.Equal("hi", Line(raised));
        raised.MaxLineBytes = 1000;                                                             // the peer proved itself: the ceiling rises
        Assert.Equal(new string('z', 100), Line(raised));
    }

    private static bool CanConnect(int port)
    {
        try
        {
            using var probe = new TcpClient();
            probe.Connect(IPAddress.Loopback, port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    /// <summary>Connects, sends the bytes, and returns the lines that came back until the port closed or went quiet — off the UI thread, so the desk can answer.</summary>
    private static List<string> Wire(int port, byte[] bytes, int waitMs = 6000)
        => TestApp.Pump(Task.Run(() =>
        {
            var lines = new List<string>();
            using var tcp = new TcpClient();
            tcp.Connect(IPAddress.Loopback, port);
            tcp.ReceiveTimeout = waitMs;
            using var s = tcp.GetStream();
            var reader = new StreamReader(s, Encoding.UTF8);
            lines.Add(reader.ReadLine() ?? "");                                                 // the greeting
            s.Write(bytes);
            try
            {
                while (reader.ReadLine() is { } line) lines.Add(line);
            }
            catch (IOException)
            {
                // quiet past the wait
            }
            return lines;
        }));

    [AvaloniaFact]
    public void TheControlWiresLinesAndTheTwinsFirstLineAreBoundedToo()
    {
        var wire = FreePort();
        var b = TestApp.Boot("patterns-tests-wire-", dir =>
        {
            var s = SettingsStore.Fresh();
            s.Control.Enabled = true;
            s.Control.HttpPort = FreePort();
            s.Control.TcpPort = wire;
            s.Twin.AcceptCallers = false;
            s.Watchdog.BeaconListenPort = FreePort();
            s.Watchdog.BeaconPort = s.Watchdog.BeaconListenPort;
            File.WriteAllText(Path.Combine(dir, "patterns.settings.json"), JsonUtil.Serialize(s));
        });
        try
        {
            var (services, vm, _) = b;
            PumpUntil(() => CanConnect(wire));

            // A line that never ends is not a command: said once, the door closed, the wire still there for the next.
            // A STATE push may land in between — the desk goes on changing while the line is read — and is no answer to the line.
            var answer = Wire(wire, Encoding.ASCII.GetBytes(new string('x', 200_000)));
            Assert.StartsWith("STATE ", answer[0]);
            var replies = answer.Where(l => !l.StartsWith("STATE ", StringComparison.Ordinal)).ToList();
            var refusal = Assert.Single(replies);
            Assert.StartsWith("ERR", refusal);
            Assert.Contains($"past {ControlService.WireLineBytes} bytes", refusal);
            Assert.Equal(refusal, answer[^1]);                                          // the door closed on the refusal, nothing after it
            var next = Wire(wire, Encoding.ASCII.GetBytes("STATUS\n"), waitMs: 1500);
            Assert.StartsWith("STATE ", next[0]);
            Assert.StartsWith("OK", next.First(l => !l.StartsWith("STATE ", StringComparison.Ordinal)));

            // The twin's door: a JOIN past the ceiling is closed on before the key is even read, and the main listens on.
            vm.State.Twin.Port = FreePort();
            vm.State.Twin.Key = "hunter2";
            vm.State.Twin.Role = TwinRole.Main;
            Dispatcher.UIThread.RunJobs();
            PumpUntil(() => services.Twin.Phase == TwinPhase.Listening && CanConnect(vm.State.Twin.Port));
            var closed = TestApp.Pump(Task.Run(() =>
            {
                using var tcp = new TcpClient();
                tcp.Connect(IPAddress.Loopback, vm.State.Twin.Port);
                tcp.ReceiveTimeout = 6000;
                using var s = tcp.GetStream();
                s.Write(Encoding.ASCII.GetBytes(new string('j', 70_000)));
                try
                {
                    var buf = new byte[256];
                    return s.Read(buf, 0, buf.Length) == 0;                                     // closed, with no welcome
                }
                catch (IOException)
                {
                    return false;                                                                // quiet: still open past the wait
                }
            }));
            Assert.True(closed, "the main kept a join line of 70,000 bytes open");
            Assert.Equal(TwinPhase.Listening, services.Twin.Phase);
            Assert.StartsWith("MAIN — listening", services.Twin.Status);
        }
        finally
        {
            b.Dispose();
        }
    }
}
