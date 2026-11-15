using System.Net;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Cryptography.X509Certificates;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Services;
using Patterns.Core.Model;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Show Bus 1.3 §13.8.5 on the wire's port, on real sockets from loopback with this machine's trust off: a connection's first byte
/// says whether it is served TLS with this install's certificate — the web remote's, the one the hello's <c>tls</c> names — or read
/// as lines on the socket, and nothing is said before it. A TLS client pairs by the legacy proof inside TLS and runs a verb; a plain
/// deck beside it on the same listener pairs by the same proof; a byte that opens neither, a connection that says nothing, a
/// handshake that does not complete and a TLS client over the ceiling are closed with nothing written in clear; the listener and its
/// TLS honour the bind address; every reader — STATE's control row, the Super Check, the Nodes page, the assistant's facts — says it.
/// </summary>
public class WireTlsAppTests
{
    private const string Token = "K7QM-3XWD-P9RA";

    private static T Pump<T>(Task<T> task, int timeoutMs = 10000)
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

    private static void Pump(Task task, int timeoutMs = 10000) => Pump(task.ContinueWith(t => { t.GetAwaiter().GetResult(); return true; }, TaskScheduler.Default), timeoutMs);

    private static void PumpUntil(Func<bool> condition, int timeoutMs = 10000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
            if (Environment.TickCount64 > deadline) throw new TimeoutException("the condition never held");
        }
    }

    /// <summary>A wire connection, plain or inside TLS: lines sent and read, the newest NONCE kept, the certificate the handshake showed.</summary>
    private sealed class Line : IDisposable
    {
        private readonly TcpClient _client = new();
        private readonly Stream _stream;
        private readonly StreamReader _reader;

        /// <summary>"sha256:…" of the certificate the TLS handshake showed; "" on a plain line.</summary>
        public string Shown { get; }

        public string Nonce { get; private set; } = "";

        public Line(IPAddress at, int port, bool tls)
        {
            Pump(_client.ConnectAsync(at, port));
            Stream stream = _client.GetStream();
            Shown = "";
            if (tls)
            {
                var shown = "";
                var ssl = new SslStream(stream, leaveInnerStreamOpen: false);
                Pump(ssl.AuthenticateAsClientAsync(new SslClientAuthenticationOptions
                {
                    TargetHost = "localhost",
                    RemoteCertificateValidationCallback = (_, certificate, _, _) =>
                    {
                        if (certificate is not null)
                        {
                            using var der = X509CertificateLoader.LoadCertificate(certificate.GetRawCertData());
                            shown = TlsFingerprint.Of(der);
                        }
                        return true;                                                            // a pin is the connector's to keep; the test reads what was shown
                    },
                }, CancellationToken.None));
                Shown = shown;
                stream = ssl;
            }
            _stream = stream;
            _reader = new StreamReader(_stream, Encoding.UTF8);
        }

        public void Send(string line)
        {
            _stream.Write(Encoding.UTF8.GetBytes(line + "\n"));
            _stream.Flush();
        }

        /// <summary>The next line the desk says, whatever it is.</summary>
        public string Next()
        {
            var line = Pump(_reader.ReadLineAsync()) ?? throw new IOException("the desk closed the connection");
            if (line.StartsWith("NONCE ", StringComparison.Ordinal)) Nonce = line["NONCE ".Length..];
            return line;
        }

        /// <summary>The next line that is an answer — STATE pushes and NONCE lines are not.</summary>
        public string Answer()
        {
            while (true)
            {
                var line = Next();
                if (!line.StartsWith("STATE ", StringComparison.Ordinal) && !line.StartsWith("NONCE ", StringComparison.Ordinal)) return line;
            }
        }

        public void Dispose()
        {
            _stream.Dispose();
            _client.Dispose();
        }
    }

    /// <summary>
    /// The module's order on either carrier: HELLO first (a connector speaks first), the desk's NONCE then — the greeting, said once
    /// the first byte has come, inside TLS on a TLS line — HELLO's answer, AUTH-PROOF, "OK paired" and the STATE greeting, whose
    /// JSON is returned.
    /// </summary>
    private static string Pair(Line line, string name)
    {
        line.Send("HELLO " + name);
        Assert.StartsWith("NONCE ", line.Next());
        Assert.Equal("OK", line.Answer());
        line.Send("AUTH-PROOF " + WireProof.Proof(Token, line.Nonce, name));
        Assert.Equal("OK paired", line.Answer());
        var state = line.Next();
        Assert.StartsWith("STATE {", state);
        return state["STATE ".Length..];
    }

    /// <summary>Raw bytes sent on a fresh connection and everything that comes back until the desk closes it — off the UI thread, pumped.</summary>
    private static byte[] Raw(IPAddress at, int port, byte[] send) => Pump(Task.Run(() =>
    {
        using var client = new TcpClient();
        client.Connect(at, port);
        client.ReceiveTimeout = 8000;
        using var stream = client.GetStream();
        if (send.Length > 0) stream.Write(send);
        using var heard = new MemoryStream();
        var chunk = new byte[4096];
        try
        {
            int n;
            while ((n = stream.Read(chunk, 0, chunk.Length)) > 0) heard.Write(chunk, 0, n);
        }
        catch (IOException)
        {
            // a reset is the door closed too
        }
        return heard.ToArray();
    }), 15000);

    /// <summary>What a TLS client may hear when its handshake is refused: nothing, or a TLS record — never a line in clear.</summary>
    private static void NothingInClear(byte[] heard)
    {
        var text = Encoding.ASCII.GetString(heard);
        foreach (var word in new[] { "NONCE", "STATE", "ERR", "OK" }) Assert.DoesNotContain(word, text, StringComparison.Ordinal);
        if (heard.Length > 0) Assert.Equal(0x15, heard[0]);                                      // a TLS alert, if anything
    }

    [AvaloniaFact]
    public void ATlsClientPairsAndRunsAVerbBesideAPlainDeckOnTheOnePortWithTheDesksCertificate()
    {
        var b = TestApp.Boot();
        var (services, vm, _) = b;
        var trusted = ControlService.TrustLoopback;
        var plainAuth = ControlService.PlainAuthOnLoopback;
        ControlService.TrustLoopback = false;                                                    // loopback plays another machine
        ControlService.PlainAuthOnLoopback = false;
        try
        {
            (vm.State.Control.HttpPort, vm.State.Control.TcpPort) = TestPorts.Two();
            vm.State.Control.Token = Token;
            Dispatcher.UIThread.RunJobs();
            var port = vm.State.Control.TcpPort;
            var fp = services.Kernel.Tls.Fingerprint;
            Assert.True(TlsFingerprint.IsValid(fp));
            var hello = services.Kernel.Beacon.BuildHello();
            Assert.Equal((fp, port), (hello.Tls, hello.Ports.Wire));                             // the hello's tls names the certificate the wire serves
            var before = (services.Kernel.WireDoors.OverTls, services.Kernel.WireDoors.Plain);

            using var tls = new Line(IPAddress.Loopback, port, tls: true);
            Assert.Equal(fp, tls.Shown);                                                         // the certificate served is the desk's
            Pair(tls, "TLS deck");
            tls.Send("BLACKOUT ON");
            Assert.Equal("OK", tls.Answer());
            PumpUntil(() => services.State.Blackout);
            // Plain AUTH is this machine's only, inside TLS too — refused; and the token did not cross in clear, so it is not marked.
            tls.Send("AUTH " + Token);
            Assert.Equal(ControlProtocol.Err(WireProof.UseProof), tls.Answer());
            Assert.Empty(services.Kernel.TokenSeenInClear.For(Token));

            // A deck on the same listener stays plain and pairs by its proof, as the module does today.
            using var deck = new Line(IPAddress.Loopback, port, tls: false);
            Assert.Equal("", deck.Shown);
            var state = Pair(deck, "FOH deck");
            deck.Send("BLACKOUT OFF");
            Assert.Equal("OK", deck.Answer());
            PumpUntil(() => !services.State.Blackout);
            // The token itself, sent plain from another machine, crossed in clear: refused and marked, as before.
            deck.Send("AUTH " + Token);
            Assert.Equal(ControlProtocol.Err(WireProof.UseProof), deck.Answer());
            Assert.Single(services.Kernel.TokenSeenInClear.For(Token));

            // Every reader says the wire serves TLS, with the certificate, and counts each door.
            Assert.Equal(before.OverTls + 1, services.Kernel.WireDoors.OverTls);
            Assert.Equal(before.Plain + 1, services.Kernel.WireDoors.Plain);
            var wire = JsonDocument.Parse(state).RootElement.GetProperty("control").GetProperty("wire");
            Assert.Equal((port, true, fp), (wire.GetProperty("port").GetInt32(), wire.GetProperty("tls").GetBoolean(), wire.GetProperty("fingerprint").GetString()));
            // STATE carries no count that a connection moves, so a probe or a scan never defeats the repeated push's skip.
            Assert.Equal(WireTls.Words(port, fp, ""), wire.GetProperty("words").GetString());
            foreach (var moving in new[] { "overTls", "plain", "unanswered" }) Assert.False(wire.TryGetProperty(moving, out _), moving);
            Assert.StartsWith($"wire {port}: TLS or plain, told by the first byte — certificate {TlsFingerprint.Short(fp)}", services.Kernel.WireTlsLine, StringComparison.Ordinal);
            var row = Assert.Single(SuperCheck.Run(services.Metrics.GatherFacts()).Rows, r => r.Section == "REMOTE" && r.Item == "Wire TLS");
            Assert.Equal((CheckLight.Green, $"port {port} · TLS or plain by the first byte · certificate {TlsFingerprint.Short(fp)}"), (row.Light, row.Value));
            Assert.Contains($"wire {port}: TLS or plain", services.Nodes.Identity, StringComparison.Ordinal);
            Assert.Contains(services.Kernel.Facts().ShowNetwork, l => l.StartsWith($"This desk's wire {port}: TLS or plain", StringComparison.Ordinal));
        }
        finally
        {
            ControlService.TrustLoopback = trusted;
            ControlService.PlainAuthOnLoopback = plainAuth;
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AByteThatOpensNeitherASilentConnectionAndAFailedHandshakeAreClosedWithNothingSaid()
    {
        var b = TestApp.Boot();
        var (services, vm, _) = b;
        var trusted = ControlService.TrustLoopback;
        ControlService.TrustLoopback = false;
        try
        {
            (vm.State.Control.HttpPort, vm.State.Control.TcpPort) = TestPorts.Two();
            vm.State.Control.Token = Token;
            Dispatcher.UIThread.RunJobs();
            services.Control.WireLimits = new WireLimits(FirstByteSeconds: 0.5);
            var port = vm.State.Control.TcpPort;
            var unanswered = services.Kernel.WireDoors.Unanswered;

            // An SSLv2 hello (0x80), a TLS alert record (0x15) and binary: closed, nothing written — never read as a line.
            Assert.Empty(Raw(IPAddress.Loopback, port, new byte[] { 0x80, 0x2e, 0x01, 0x03, 0x01 }));
            Assert.Empty(Raw(IPAddress.Loopback, port, new byte[] { 0x15, 0x03, 0x03, 0x00, 0x02, 0x02, 0x28 }));
            // A connection that says nothing: closed at the seconds, and no NONCE was said to it first.
            var clock = Environment.TickCount64;
            Assert.Empty(Raw(IPAddress.Loopback, port, Array.Empty<byte>()));
            Assert.InRange(Environment.TickCount64 - clock, 300, 8000);
            // A TLS handshake record whose body is not a handshake: never answered in clear.
            NothingInClear(Raw(IPAddress.Loopback, port, new byte[] { 0x16, 0x03, 0x01, 0x00, 0x05, 0x68, 0x65, 0x6c, 0x6c, 0x6f }));
            // A handshake that starts and stops — three bytes of a record header, then nothing: the handshake is bounded by the same
            // seconds, so it is closed with nothing in clear and its place under the ceiling given back.
            clock = Environment.TickCount64;
            NothingInClear(Raw(IPAddress.Loopback, port, new byte[] { 0x16, 0x03, 0x01 }));
            Assert.InRange(Environment.TickCount64 - clock, 300, 8000);
            PumpUntil(() => services.Kernel.WireDoors.Unanswered >= unanswered + 5);

            // The door is unmoved for a line: a deck that speaks first hears its NONCE and its PONG.
            using var deck = new Line(IPAddress.Loopback, port, tls: false);
            deck.Send("PING");
            Assert.StartsWith("NONCE ", deck.Next());
            Assert.Equal("OK PONG", deck.Answer());
            var row = Assert.Single(SuperCheck.Run(services.Metrics.GatherFacts()).Rows, r => r.Section == "REMOTE" && r.Item == "Wire TLS");
            Assert.Contains("closed unanswered", row.Note, StringComparison.Ordinal);
        }
        finally
        {
            ControlService.TrustLoopback = trusted;
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void OverTheCeilingATlsClientHearsNothingInClearAndALineHearsBusy()
    {
        var b = TestApp.Boot();
        var (services, vm, _) = b;
        var trusted = ControlService.TrustLoopback;
        ControlService.TrustLoopback = false;
        try
        {
            (vm.State.Control.HttpPort, vm.State.Control.TcpPort) = TestPorts.Two();
            vm.State.Control.Token = Token;
            Dispatcher.UIThread.RunJobs();
            services.Control.WireLimits = new WireLimits(MaxClients: 1, MaxClientsPerAddress: 1);
            var port = vm.State.Control.TcpPort;
            using var holder = new Line(IPAddress.Loopback, port, tls: false);
            holder.Send("PING");
            Assert.Equal("OK PONG", holder.Answer());

            NothingInClear(Raw(IPAddress.Loopback, port, new byte[] { 0x16, 0x03, 0x01, 0x00, 0x05, 0x01, 0x00, 0x00, 0x01, 0x00 }));
            var busy = Encoding.UTF8.GetString(Raw(IPAddress.Loopback, port, Encoding.UTF8.GetBytes("PING\n")));
            Assert.Equal(ControlProtocol.Err(services.Control.WireLimits.BusyWords(1)) + "\n", busy);

            // A flood of silent connections over the ceiling: at most BusyPeeksMost wait for a first byte, each for at most the busy
            // wait; every one beyond them is closed at once. None hears a word. Timed from the desk's own accept: each close is compared
            // with the others and with its own dial, never with a clock started before the connects, which the test's scheduling moves.
            var flood = ControlService.BusyPeeksMost + 4;
            var closes = Pump(Task.Run(async () =>
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                var clients = new List<(TcpClient Client, long DialMs)>();
                try
                {
                    for (var i = 0; i < flood; i++)
                    {
                        var c = new TcpClient();
                        clients.Add((c, clock.ElapsedMilliseconds));
                        await c.ConnectAsync(IPAddress.Loopback, port);
                    }
                    // Each read awaited on its own, so each close is timed as it happens, not behind the one before it, and no pool
                    // thread is held blocking while the desk's own handlers wait for one.
                    async Task<(int Heard, bool Shut, long DialMs, long EndMs)> Read((TcpClient Client, long DialMs) c)
                    {
                        var heard = 0;
                        var chunk = new byte[256];
                        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(8));
                        try
                        {
                            int n;
                            while ((n = await c.Client.GetStream().ReadAsync(chunk, stop.Token)) > 0) heard += n;
                        }
                        catch (IOException)
                        {
                            // a reset is the door closed too
                        }
                        catch (OperationCanceledException)
                        {
                            return (heard, false, c.DialMs, clock.ElapsedMilliseconds);
                        }
                        return (heard, true, c.DialMs, clock.ElapsedMilliseconds);
                    }
                    var reads = clients.Select(Read).ToArray();
                    return await Task.WhenAll(reads);
                }
                finally
                {
                    foreach (var c in clients) c.Client.Dispose();
                }
            }), 20000);
            var said = string.Join("; ", closes.Select(c => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"dialled {c.DialMs} ms, ended {c.EndMs} ms, heard {c.Heard}{(c.Shut ? "" : ", not shut")}")))
                + string.Create(System.Globalization.CultureInfo.InvariantCulture, $"; the pool {ThreadPool.ThreadCount} threads, {ThreadPool.PendingWorkItemCount} items waiting");
            Assert.True(closes.All(c => c.Shut && c.Heard == 0), "each shut by the desk with nothing written: " + said);
            // The ones within the bound waited the busy wait from their own dial; the ones beyond it were closed at once, at least a
            // second ahead of the earliest that waited — before any wait within it could have ended — and no wait ran past its bound.
            var wait = (long)ControlService.BusyFirstByteWait.TotalMilliseconds;
            var waited = closes.Where(c => c.EndMs - c.DialMs >= wait - 50).ToArray();
            var atOnce = closes.Where(c => c.EndMs - c.DialMs < wait - 50).ToArray();
            Assert.True(waited.Length is >= 1 and <= ControlService.BusyPeeksMost, "at most the bound's number waited, and some did: " + said);
            Assert.True(atOnce.Length >= flood - ControlService.BusyPeeksMost, "the ones beyond the bound are closed at once: " + said);
            Assert.True(atOnce.Max(c => c.EndMs) <= waited.Min(c => c.EndMs) - 1000, "the ones beyond closed at least a second ahead of the earliest that waited: " + said);
            Assert.True(waited.Max(c => c.EndMs) - atOnce.Min(c => c.EndMs) <= wait + 2000, "no wait ran past the busy wait: " + said);
        }
        finally
        {
            ControlService.TrustLoopback = trusted;
            b.Dispose();
        }
    }

    /// <summary>
    /// The four doors' accepts are owed by the doors, not by the desk's thread: with that thread held — the test waits on it without a
    /// pass of the dispatcher — a silent connection to the wire, the web remote, the audience port and the twin's port is still accepted
    /// and closed at its door's own seconds (the twin's at its JOIN's wait), with nothing written. An accept that waited for a pass of the
    /// desk's thread would leave each open until the reads' own bound.
    /// </summary>
    [AvaloniaFact]
    public void EveryDoorClosesASilentConnectionAtItsSecondsWhileTheDesksThreadIsHeld()
    {
        var b = TestApp.Boot();
        var (services, vm, _) = b;
        var trusted = ControlService.TrustLoopback;
        ControlService.TrustLoopback = false;
        try
        {
            var ports = TestPorts.Distinct(4);
            (vm.State.Control.HttpPort, vm.State.Control.TcpPort, vm.State.Control.AudiencePort, vm.State.Twin.Port) = (ports[0], ports[1], ports[2], ports[3]);
            vm.State.Control.AudienceEnabled = true;
            vm.State.Control.Token = Token;
            vm.State.Twin.Key = "hunter2";
            vm.State.Twin.Role = TwinRole.Main;
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.Control.Listening && services.Control.AudienceListening, services.Control.Status);
            Assert.True(services.Twin.Phase == TwinPhase.Listening, services.Twin.Status);
            const double seconds = 1;
            const double joinSeconds = 5;   // the twin's JOIN wait (TwinService.HandleStandby)
            services.Control.WireLimits = new WireLimits(FirstByteSeconds: seconds);
            services.Control.ControlLimits = HttpLimits.Control with { HeadSeconds = seconds };
            services.Control.AudienceLimits = HttpLimits.Audience with { HeadSeconds = seconds };
            var doors = new[]
            {
                (Door: "wire", Port: vm.State.Control.TcpPort, Seconds: seconds), (Door: "web remote", Port: vm.State.Control.HttpPort, Seconds: seconds),
                (Door: "audience", Port: vm.State.Control.AudiencePort, Seconds: seconds), (Door: "twin", Port: vm.State.Twin.Port, Seconds: joinSeconds),
            };

            var reads = Task.Run(async () => await Task.WhenAll(doors.Select(async door =>
            {
                var clock = System.Diagnostics.Stopwatch.StartNew();
                using var client = new TcpClient();
                await client.ConnectAsync(IPAddress.Loopback, door.Port);
                var heard = 0;
                var chunk = new byte[256];
                using var bound = new CancellationTokenSource(TimeSpan.FromSeconds(door.Seconds + 5));
                try
                {
                    int n;
                    while ((n = await client.GetStream().ReadAsync(chunk, bound.Token)) > 0) heard += n;
                }
                catch (IOException)
                {
                    // a reset is the door closed too
                }
                catch (OperationCanceledException)
                {
                    return (door.Door, door.Seconds, Ms: clock.ElapsedMilliseconds, Heard: heard, Shut: false);
                }
                return (door.Door, door.Seconds, Ms: clock.ElapsedMilliseconds, Heard: heard, Shut: true);
            })));
            // The desk's thread held: blocked here on the reads with no pass of the dispatcher, as a long job on it would hold it.
            Assert.True(((IAsyncResult)reads).AsyncWaitHandle.WaitOne(TimeSpan.FromSeconds(joinSeconds + 8)), "the reads end within their own bound");
            var ends = reads.GetAwaiter().GetResult();
            var said = string.Join("; ", ends.Select(e => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"{e.Door}: {(e.Shut ? "shut" : "not shut")} at {e.Ms} ms, heard {e.Heard}")));
            Assert.True(ends.All(e => e.Shut && e.Heard == 0), "every door shut its silent connection with nothing written, the desk's thread held: " + said);
            Assert.True(ends.All(e => e.Ms >= (long)(e.Seconds * 1000) - 50 && e.Ms <= (long)(e.Seconds * 1000) + 2500), "each at its door's own seconds: " + said);
        }
        finally
        {
            ControlService.TrustLoopback = trusted;
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheWireAndItsTlsHonourTheBindAddress()
    {
        var b = TestApp.Boot();
        var (services, vm, _) = b;
        var trusted = ControlService.TrustLoopback;
        ControlService.TrustLoopback = false;
        try
        {
            // 127.0.0.2 stands in for the control network's card: an address of this machine that is not 127.0.0.1.
            var bound = IPAddress.Parse("127.0.0.2");
            vm.State.Control.Bind = "127.0.0.2";
            (vm.State.Control.HttpPort, vm.State.Control.TcpPort) = TestPorts.Two();
            vm.State.Control.Token = Token;
            Dispatcher.UIThread.RunJobs();
            var port = vm.State.Control.TcpPort;
            using (var tls = new Line(bound, port, tls: true))
            {
                Assert.Equal(services.Kernel.Tls.Fingerprint, tls.Shown);
                tls.Send("PING");
                Assert.StartsWith("NONCE ", tls.Next());
                Assert.Equal("OK PONG", tls.Answer());
            }
            using var other = new TcpClient();
            Assert.ThrowsAny<SocketException>(() => Pump(other.ConnectAsync(IPAddress.Loopback, port)));
        }
        finally
        {
            ControlService.TrustLoopback = trusted;
            vm.State.Control.Bind = "";
            Dispatcher.UIThread.RunJobs();
            b.Dispose();
        }
    }
}
