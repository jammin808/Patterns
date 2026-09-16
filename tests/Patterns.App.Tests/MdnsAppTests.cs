using Patterns.Devices;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The desk on the network by name: with the wire on it announces itself on mDNS and answers a
/// browser's query — a one-shot query from a port of its own gets a unicast answer with the
/// instance, the SRV on the wire's port and the TXT facts — a HELLO with a module token names the
/// deck on the Remote page and in history without the token, STATE carries the version and the
/// decks, and ANNOUNCE off says goodbye.
/// </summary>
public class MdnsAppTests
{
    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

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

    private static (NodeHost Host, string Dir, int Wire) BootNode(bool announce = true)
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-tests-mdns-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var s = SettingsStore.Fresh();
        s.Name = "Gala";
        s.Control.Enabled = true;
        s.Control.Announce = announce;
        s.Control.HttpPort = FreePort();
        s.Control.TcpPort = FreePort();
        s.Watchdog.BeaconName = "HUB-PC";
        s.Watchdog.BeaconListenPort = FreePort();
        s.Watchdog.BeaconPort = s.Watchdog.BeaconListenPort;
        File.WriteAllText(Path.Combine(dir, "patterns.settings.json"), JsonUtil.Serialize(s));
        var host = NodeHost.Build(NodeKind.Arcade, new SettingsStore(dir));
        host.Start();
        return (host, dir, s.Control.TcpPort);
    }

    private static void Clean(NodeHost host, string dir)
    {
        host.Dispose();
        try { Directory.Delete(dir, true); } catch (Exception) { /* a temp folder left behind is not a failed test */ }
    }

    [AvaloniaFact]
    public void TheNodeAnnouncesItselfAndAnswersAOneShotQueryToTheAsker()
    {
        var (host, dir, wire) = BootNode();
        try
        {
            var mdns = host.Kernel.Mdns;
            var advert = mdns.Advert;
            Assert.NotNull(advert);
            Assert.Equal("Patterns arcade HUB-PC", advert!.InstanceLabel);
            Assert.Equal(wire, advert.WirePort);
            Assert.Equal("arcade", advert.TxtStrings.First(t => t.StartsWith("kind=")).Split('=')[1]);
            Assert.Equal(AppVersion.Current, advert.Version);

            // The responder's own path, without a network: a browser's query earns the instance and its facts.
            var reply = mdns.Handle(DnsPacket.Query(MdnsAdvert.ServiceName, DnsSd.TypePtr).ToBytes(), new IPEndPoint(IPAddress.Loopback, 40000));
            Assert.NotNull(reply);
            Assert.Equal("Patterns arcade HUB-PC._patterns._tcp.local", DnsSd.Dotted(Assert.Single(reply!.Answers).Target!));
            Assert.Contains(reply.Additionals, r => r.Type == DnsSd.TypeSrv && r.Port == wire);
            Assert.Null(mdns.Handle(DnsPacket.Query(DnsSd.Labels("_ssh._tcp.local"), DnsSd.TypePtr).ToBytes(), new IPEndPoint(IPAddress.Loopback, 40000)));
            Assert.Equal(2, mdns.Queries);
            Assert.Equal(1, mdns.Answers);

            // A Companion's announcement heard is a Companion on the Remote page.
            var instance = new[] { "Companion (FOH-PC)", "_companion-satellite-tcp", "_tcp", "local" };
            var heard = DnsPacket.Response(new[]
            {
                DnsRecord.Ptr(DnsSd.Labels(CompanionModule.SatelliteServiceType + ".local"), instance, 4500),
                DnsRecord.Srv(instance, new[] { "foh-pc", "local" }, 16622, 120),
                DnsRecord.Txt(instance, new[] { "version=5.0.3" }, 4500),
                DnsRecord.A(new[] { "foh-pc", "local" }, IPAddress.Parse("10.0.0.7"), 120),
            });
            Assert.Null(mdns.Handle(heard.ToBytes(), new IPEndPoint(IPAddress.Parse("10.0.0.7"), 5353)));
            Assert.Equal("Companion (FOH-PC) at 10.0.0.7:16622 · v5.0.3", Assert.Single(mdns.Companions).Line);

            if (mdns.Announcing)
            {
                // The socket is open: a real one-shot query on the loopback gets its answer back to the asker's own port.
                using var asker = new UdpClient(new IPEndPoint(IPAddress.Loopback, 0));
                asker.Client.ReceiveTimeout = 3000;
                asker.Send(DnsPacket.Query(MdnsAdvert.ServiceName, DnsSd.TypePtr).ToBytes(), new IPEndPoint(IPAddress.Loopback, DnsSd.Port));
                var from = new IPEndPoint(IPAddress.Any, 0);
                DnsPacket? answer = null;
                try
                {
                    for (var i = 0; i < 5 && answer is null; i++)
                    {
                        var bytes = asker.Receive(ref from);
                        var p = DnsPacket.Parse(bytes);
                        if (p is { IsResponse: true } && p.Answers.Any(r => r.Type == DnsSd.TypePtr && DnsSd.SameName(r.Name, MdnsAdvert.ServiceName))) answer = p;
                    }
                }
                catch (SocketException)
                {
                    // Another responder on 5353 (the system's own) may take the datagram first; the path above proved the answer.
                }
                if (answer is not null) Assert.Contains(answer.Answers, r => r.Target is not null && DnsSd.Dotted(r.Target).StartsWith("Patterns arcade HUB-PC"));
                Assert.StartsWith("Announced on the network as \"Patterns arcade HUB-PC\"", mdns.Status);
            }
            else
            {
                Assert.StartsWith("Not announced — port 5353 could not be opened", mdns.Status);
            }

            // ANNOUNCE off: the goodbye goes and the words say so.
            host.State.Control.Announce = false;
            PumpUntil(() => !mdns.Announcing);
            Assert.Null(mdns.Advert);
            Assert.StartsWith("Not announced on the network", mdns.Status);
            host.State.Control.Announce = true;
            PumpUntil(() => mdns.Advert is not null);
        }
        finally
        {
            Clean(host, dir);
        }
    }

    [AvaloniaFact]
    public void AHelloWithAModuleTokenNamesTheDeckWithoutTheTokenAndStateCarriesTheVersionAndTheDecks()
    {
        var (host, dir, wire) = BootNode(announce: false);
        try
        {
            PumpUntil(() =>
            {
                try { using var probe = new TcpClient(); probe.Connect(IPAddress.Loopback, wire); return true; }
                catch (SocketException) { return false; }
            });
            using var client = new TcpClient();
            client.Connect(IPAddress.Loopback, wire);
            using var stream = client.GetStream();
            var reader = new StreamReader(stream, Encoding.UTF8);
            // The wire answers on the UI thread, which is this thread: every read pumps the dispatcher while it waits.
            string Reply()
            {
                return TestApp.Pump(ReadAsync());
                async Task<string> ReadAsync()
                {
                    while (true)
                    {
                        var line = await reader.ReadLineAsync() ?? throw new IOException("closed");
                        if (!line.StartsWith("STATE ")) return line;
                    }
                }
            }
            Assert.StartsWith("STATE ", TestApp.Pump(reader.ReadLineAsync()));
            void Send(string line) { var b = Encoding.UTF8.GetBytes(line + "\n"); stream.Write(b, 0, b.Length); stream.Flush(); }
            Send($"HELLO FOH deck module={CompanionModule.Version}");
            Assert.Equal("OK", Reply());
            PumpUntil(() => host.Control.Decks.Count == 1);
            var deck = Assert.Single(host.Control.Decks);
            Assert.Equal("FOH deck", deck.Name);
            Assert.Equal(CompanionModule.Version, deck.Module);
            Assert.Equal($"FOH deck (module {CompanionModule.Version}, 127.0.0.1)", deck.Line);

            // History reads the deck's name, never the token.
            Send("ARCADE START pong 1");
            Assert.StartsWith("OK", Reply());
            PumpUntil(() => host.Kernel.Journal.Tail(5).Any(e => e.Origin.Contains("FOH deck")));
            var entry = host.Kernel.Journal.Tail(5).First(e => e.Origin.Contains("FOH deck"));
            Assert.DoesNotContain("module=", entry.Origin);

            Send("STATUS");
            var status = Reply();
            Assert.StartsWith("OK {", status);
            var json = System.Text.Json.JsonDocument.Parse(status[3..]).RootElement;
            Assert.Equal(AppVersion.Current, json.GetProperty("version").GetString());
            var decks = json.GetProperty("decks").EnumerateArray().ToList();
            Assert.Equal("FOH deck", Assert.Single(decks).GetProperty("name").GetString());
            Assert.Equal(CompanionModule.Version, decks[0].GetProperty("module").GetString());

            // The Remote page's strip reads the deck.
            Assert.Contains($"Connected: FOH deck (module {CompanionModule.Version}, 127.0.0.1).", CompanionWords.DecksLine(host.Control.Decks));

            // A deck of the older module is said to be behind, and a bare HELLO is a name alone.
            Send("HELLO Old deck module=2.8.0");
            Assert.Equal("OK", Reply());
            PumpUntil(() => host.Control.Decks.Single().Module == "2.8.0");
            Assert.Contains($"{CompanionModule.Version} is current", host.Control.Decks.Single().Line);
            Send("HELLO script");
            Assert.Equal("OK", Reply());
            PumpUntil(() => host.Control.Decks.Single().Module == "");
            Assert.Contains("no module", host.Control.Decks.Single().Line);
            client.Close();
            PumpUntil(() => host.Control.Decks.Count == 0);
        }
        finally
        {
            Clean(host, dir);
        }
    }
}
