using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
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
/// Round 97: a PlayMap node known on this desk, paired by proof, driven and drawn — against a fake PlayMap that speaks Mapper's
/// <c>WireSession</c> grammar on TCP: <c>NONCE</c> at connect to a peer it does not trust, <c>OK @id</c> to HELLO, the greeting STATE
/// right after HELLO (an older PlayMap) or only after <c>OK paired</c> (a newer one, which the owner's word of 2026-11-14 asks for),
/// <c>AUTH-PROOF</c> as HMAC-SHA256 of its token over nonce + the HELLO name, <c>SUBSCRIBE OFF|CONTEXT</c>, the show's fact lines taken
/// untagged and never answered, and a verb refused <c>ERR @id not paired — …</c> until the proof. Every reader is read: the device
/// row in STATE, the Nodes card and its menu, the Eye's cluster and its edge, the bus row, the brief, and the DEVICE line's journal row.
/// </summary>
public class NodeDeviceAppTests
{
    private const string Token = "K7QM-3XWD-P9RA";

    private static bool WaitFor(Func<bool> test, int seconds = 8)
    {
        var deadline = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            if (test()) return true;
            Thread.Sleep(20);
        }
        return test();
    }

    private static string Send(CommandRouter router, string line) => TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse(line)));

    /// <summary>
    /// What a wait for the node names when it runs out, so the failure says its class: the connection's start lost (the fake accepted
    /// it, the link is open, nothing went out), the dial never completed (nothing accepted, the link still connecting), or a reader
    /// starved (lines went out, the fake read none) — with the thread pool beside it, which a pool starved for the whole wait shows.
    /// </summary>
    private static string Seen(AppServices services, DeviceConfig device, FakePlayMap playmap)
    {
        var link = services.Devices.LinkFor(device.Id);
        var sight = services.Devices.NodeSights.FirstOrDefault(n => n.DeviceId == device.Id);
        var traffic = services.Devices.Traffic(device.Id);
        var inv = CultureInfo.InvariantCulture;
        return string.Join("; ",
            string.Create(inv, $"the fake read [{string.Join(" | ", playmap.Read)}] on {playmap.Accepted} connection(s) accepted"),
            link is null ? "the link not made" : string.Create(inv, $"the link '{link.Status}', open {link.IsOpen}"),
            sight is null ? "no sight" : string.Create(inv, $"the sight '{sight.Words}', connected {sight.Connected}"),
            traffic is { } t ? string.Create(inv, $"the device in {t.In} (last '{t.LastIn}'), out {t.Out} (last '{t.LastOut}')") : "the device has no link open",
            string.Create(inv, $"the pool {ThreadPool.ThreadCount} threads, {ThreadPool.PendingWorkItemCount} items waiting"));
    }

    [AvaloniaFact]
    public void AnOlderPlayMapThatGreetsBeforeTheProofIsPairedDrivenAndDrawnAndItsEarlyStateIsDiscarded() => Drive(stateAfterProof: false);

    [AvaloniaFact]
    public void ANewerPlayMapThatHoldsItsStateUntilThePairingIsPairedDrivenAndDrawnAndItsStateIsTaken() => Drive(stateAfterProof: true);

    private static void Drive(bool stateAfterProof)
    {
        using var playmap = new FakePlayMap(Token, stateAfterProof);
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var router = new CommandRouter(services);

            // Heard on the show network: the Nodes card offers Add as a device, and the press fills in where its hello came from.
            services.Kernel.Beacon.ShowBus.Hear(Encoding.UTF8.GetBytes($"{{\"t\":\"playmap\",\"v\":1,\"id\":\"pm-hallb1\",\"name\":\"Hall B\",\"ports\":{{\"http\":9940,\"wire\":{playmap.Port}}},\"caps\":[\"context\"]}}"),
                "127.0.0.1", DateTime.UtcNow, "not-this-desk");
            services.Nodes.Poll();
            var heardCard = Assert.Single(services.Nodes.ShowApps, c => c.KindLabel == "PlayMap");
            Assert.True(heardCard.AddAsDevice);
            vm.AddAsDeviceCommand.Execute(heardCard);
            var device = Assert.Single(vm.State.Interactive.Devices, d => d.Profile == DeviceProfile.ShowBusNode);
            Assert.Equal(("127.0.0.1", playmap.Port, "PlayMap-Hall-B"), (device.Port, device.NetPort, device.Name));
            Assert.Contains("type the node's pairing token in its Secret box", vm.StatusMessage);
            device.Secret = Token;
            vm.State.Interactive.Enabled = true;
            services.Devices.Reconcile();

            Assert.True(WaitFor(() => services.Devices.NodeSights.Any(n => n.Up && n.Context is not null)), "paired, and its context taken: " + Seen(services, device, playmap));
            var sight = services.Devices.NodeSights.Single();
            Assert.Equal((stateAfterProof ? 0 : 1, stateAfterProof ? 1 : 0), (sight.StateDiscarded, sight.StateTaken));
            Assert.True(sight.Paired);
            Assert.StartsWith("@dh HELLO Patterns", playmap.Read[0], StringComparison.Ordinal);
            Assert.StartsWith("@dp AUTH-PROOF ", playmap.Read[1], StringComparison.Ordinal);
            Assert.Contains("@do SUBSCRIBE OFF", playmap.Read);
            Assert.Contains("@dc SUBSCRIBE CONTEXT", playmap.Read);
            Assert.True(playmap.Paired);

            // The show's facts follow, untagged and only once paired; none draws an ERR.
            Assert.StartsWith("OK", Send(router, "BLACKOUT ON"));
            Assert.True(WaitFor(() => playmap.Facts.Contains("BLACKOUT 1")), "the desk's blackout reaches the node: " + string.Join(" | ", playmap.Read));
            Assert.DoesNotContain(playmap.Read, l => l.StartsWith("OPEN", StringComparison.Ordinal));
            Assert.Empty(playmap.Errs);
            Assert.All(playmap.Facts, f => Assert.True(playmap.FactsAfterPaired, f));

            // A DEVICE line on the wire: its own id, its own receipt, one journal row (the closing verb's).
            var reply = Send(router, $"DEVICE {device.Name} PLAYLIST GO show #3");
            Assert.StartsWith("OK", reply);
            Assert.True(WaitFor(() => services.Journal.Tail(200).Any(e => e.Kind == "DeviceReceipt" && e.Message.Contains("PLAYLIST GO show #3", StringComparison.Ordinal))), "the receipt is journalled");
            var row = services.Journal.Tail(200).Last(e => e.Kind == "DeviceReceipt");
            Assert.Equal("Done", row.Outcome);
            Assert.Contains("accepted (applied — playing show #3)", row.Message);
            Assert.Single(playmap.Read, l => l.StartsWith("@d1-", StringComparison.Ordinal) && l.EndsWith(" PLAYLIST GO show #3", StringComparison.Ordinal));

            // A node that takes a line and has not run it yet: journalled Accepted, never Done (Show Bus 13.12.3).
            Assert.StartsWith("OK", Send(router, $"DEVICE {device.Name} PLAYLIST LOAD show #4"));
            Assert.True(WaitFor(() => services.Journal.Tail(200).Any(e => e.Kind == "DeviceReceipt" && e.Message.Contains("PLAYLIST LOAD show #4", StringComparison.Ordinal))), "the accepted receipt is journalled");
            var accepted = services.Journal.Tail(200).Last(e => e.Kind == "DeviceReceipt");
            Assert.Equal("Accepted", accepted.Outcome);
            Assert.Contains("accepted, not run yet", accepted.Message);

            // STATE: the device row's node — its pairing, its record and what it discarded — and the bus row naming its device.
            var state = JsonDocument.Parse(router.StateJson()).RootElement;
            var node = state.GetProperty("devices").EnumerateArray().Single(d => d.GetProperty("profile").GetString() == "showbusnode").GetProperty("node");
            Assert.Equal(("auth-proof", 0, "pm-hallb1"), (node.GetProperty("pairing").GetString(), node.GetProperty("level").GetInt32(), node.GetProperty("contextId").GetString()));
            Assert.Equal(stateAfterProof ? 0 : 1, node.GetProperty("stateDiscarded").GetInt32());
            Assert.Equal(("legacy", "amber"), (node.GetProperty("link").GetProperty("proof").GetString(), node.GetProperty("link").GetProperty("light").GetString()));
            var app = state.GetProperty("bus").GetProperty("apps").EnumerateArray().Single(a => a.GetProperty("kind").GetString() == "playmap");
            Assert.Equal(("pm-hallb1", "amber", "PlayMap-Hall-B"), (app.GetProperty("id").GetString(), app.GetProperty("light").GetString(), app.GetProperty("device").GetString()));
            Assert.DoesNotContain(Token, router.StateJson(), StringComparison.Ordinal);
            Assert.DoesNotContain(Token.Replace("-", "", StringComparison.Ordinal), router.StateJson(), StringComparison.Ordinal);

            // The Nodes card folds the hello and the device; its menu opens the device.
            services.Nodes.Poll();
            var card = Assert.Single(services.Nodes.ShowApps, c => c.KindLabel == "PlayMap");
            Assert.Equal(("pm-hallb1", device.Id, false), (card.Id, card.DeviceId, card.AddAsDevice));
            Assert.Contains("driven as the device PlayMap-Hall-B", card.Line);
            Assert.Equal(stateAfterProof, !card.Line.Contains("before the proof discarded", StringComparison.Ordinal));
            Assert.Equal("△", card.Mark);

            // The Eye: a cluster in the NETWORK band, its things on their planes, the device's Controls edge.
            services.Eye.Refresh();
            var cluster = services.Eye.Graph.Nodes.Single(n => n.Id == "app:pm-hallb1");
            Assert.Equal((EyeKind.ShowApp, EyePlane.Network, CheckLight.Amber), (cluster.Kind, cluster.Band, cluster.Light));
            Assert.Equal("output 2 dark", cluster.Sub);
            var output = services.Eye.Graph.Nodes.Single(n => n.Id == "app:pm-hallb1:out2");
            Assert.Equal((EyePlane.Network, EyeLayout.KindPlane("output")), (output.Band, output.Plane));
            var controls = Assert.Single(services.Eye.Graph.Edges, e => e.From == "device:" + device.Id && e.To == "app:pm-hallb1" && e.Kind == EyeEdgeKind.Controls);
            Assert.Equal((CheckLight.Amber, true), (controls.Light, controls.Counted));                // this desk's link to it: the record's amber, counted
            Assert.DoesNotContain(services.Eye.Graph.Edges, e => e.From == "app:pm-hallb1" && e.Kind == EyeEdgeKind.Follows);   // never drawn as the Stagewatch link

            // The brief: the app, its link at level 0, the way to drive it.
            var brief = ShowBusView.BriefLines(services.Apps(DateTime.UtcNow));
            Assert.Contains(brief, l => l.StartsWith("PlayMap Hall B — driven as a device — amber: output 2 dark", StringComparison.Ordinal));
            Assert.Contains(brief, l => l.Contains($"DEVICE {device.Name} <its verb>", StringComparison.Ordinal));
            Assert.DoesNotContain(playmap.Read, l => l.Contains(Token, StringComparison.Ordinal) || l.Contains(Token.Replace("-", "", StringComparison.Ordinal), StringComparison.Ordinal));
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AWrongTokenStopsTheDeviceAndNoFactOrCommandEverReachesTheNode()
    {
        using var playmap = new FakePlayMap(Token, stateAfterProof: true);
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var device = DeviceProfiles.Preset(DeviceProfile.ShowBusNode, 1);
            device.Port = "127.0.0.1";
            device.NetPort = playmap.Port;
            device.Secret = "WRNG-WRNG-WRNG";
            vm.State.Interactive.Devices.Add(device);
            vm.State.Interactive.Enabled = true;
            services.Devices.Reconcile();
            Assert.True(WaitFor(() => services.Devices.NodeSights.Any(n => n.Stopped)), "the wrong token stops it: " + Seen(services, device, playmap));
            var router = new CommandRouter(services);
            var refused = Send(router, $"DEVICE {device.Name} PLAYLIST GO show");
            Assert.StartsWith("ERR", refused);
            Assert.Contains("not-sent — wrong pairing token", refused);
            Send(router, "BLACKOUT ON");
            WaitFor(() => false, 1);
            Assert.Equal(1, playmap.Read.Count(l => l.Contains("AUTH-PROOF", StringComparison.Ordinal)));
            Assert.Empty(playmap.Facts);
            Assert.DoesNotContain(playmap.Read, l => l.Contains("PLAYLIST", StringComparison.Ordinal) || l.Contains("SUBSCRIBE", StringComparison.Ordinal));
            Assert.Equal("refused", services.Devices.NodeSights.Single().Record.State);
        }
        finally
        {
            b.Dispose();
        }
    }

    /// <summary>
    /// A PlayMap's line wire as Mapper's <c>WireSession</c> speaks it, for one connection at a time: a token node greets an untrusted peer
    /// with NONCE; HELLO answers <c>OK @id</c> and, on an older node, the greeting STATE at once; a right proof answers <c>OK @id paired</c>
    /// and, on a newer node, the greeting STATE then; a wrong one <c>ERR @id wrong token — …</c> and a fresh NONCE. Facts are taken from a
    /// paired peer and never answered; a verb before the proof is <c>ERR @id not paired — …</c>.
    /// </summary>
    private sealed class FakePlayMap : IDisposable
    {
        private readonly TcpListener _listener = new(IPAddress.Loopback, 0);
        private readonly string _token;
        private readonly bool _stateAfterProof;
        private readonly object _gate = new();
        private readonly List<string> _read = new();
        private readonly List<string> _facts = new();
        private readonly List<string> _errs = new();
        private readonly CancellationTokenSource _stop = new();
        private volatile bool _paired;
        private int _accepted;
        private bool _factsAfterPaired = true;

        private static readonly HashSet<string> FactKeys = new(StringComparer.Ordinal)
        {
            "BLACKOUT", "LIVE", "DUCK", "FROZEN", "REVIEW", "LOOK", "PROGRAM", "STINGER", "LOWERTHIRD", "VOL", "MUSICVOL", "LOOKEDITED", "ARMED", "HOLD", "CUE", "DECK", "STEP",
        };

        public FakePlayMap(string token, bool stateAfterProof)
        {
            _token = token;
            _stateAfterProof = stateAfterProof;
            _listener.Start();
            _ = Task.Run(AcceptAsync, CancellationToken.None);
        }

        public int Port => ((IPEndPoint)_listener.LocalEndpoint).Port;

        public bool Paired => _paired;

        /// <summary>How many connections the fake has accepted.</summary>
        public int Accepted => Volatile.Read(ref _accepted);

        public IReadOnlyList<string> Read { get { lock (_gate) return _read.ToList(); } }

        public IReadOnlyList<string> Facts { get { lock (_gate) return _facts.ToList(); } }

        public IReadOnlyList<string> Errs { get { lock (_gate) return _errs.ToList(); } }

        public bool FactsAfterPaired { get { lock (_gate) return _factsAfterPaired; } }

        private const string State = "{\"name\":\"Hall B\",\"blackout\":false}";

        private const string Context = "{\"v\":1,\"node\":{\"t\":\"playmap\",\"id\":\"pm-hallb1\",\"name\":\"Hall B\"},\"light\":\"amber\",\"headline\":\"output 2 dark\",\"items\":["
            + "{\"id\":\"out1\",\"kind\":\"output\",\"label\":\"Output 1\",\"light\":\"green\",\"state\":\"live\"},{\"id\":\"out2\",\"kind\":\"output\",\"label\":\"Output 2\",\"light\":\"amber\",\"state\":\"dark\"}]}";

        private async Task AcceptAsync()
        {
            while (!_stop.IsCancellationRequested)
            {
                TcpClient client;
                try
                {
                    client = await _listener.AcceptTcpClientAsync(_stop.Token);
                }
                catch (Exception e) when (e is OperationCanceledException or ObjectDisposedException or SocketException)
                {
                    return;
                }
                Interlocked.Increment(ref _accepted);
                _ = Task.Run(() => ServeAsync(client), CancellationToken.None);
            }
        }

        private async Task ServeAsync(TcpClient client)
        {
            using var _ = client;
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, new UTF8Encoding(false));
            await using var writer = new StreamWriter(stream, new UTF8Encoding(false)) { NewLine = "\n", AutoFlush = true };
            var nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
            var hello = "";
            var paired = false;
            try
            {
                await writer.WriteLineAsync("NONCE " + nonce);
                while (await reader.ReadLineAsync(_stop.Token) is { } raw)
                {
                    lock (_gate) _read.Add(raw);
                    var at = raw.StartsWith('@') ? raw.IndexOf(' ', StringComparison.Ordinal) : -1;
                    var id = at > 0 ? raw[1..at] : null;
                    var line = at > 0 ? raw[(at + 1)..] : raw;
                    var space = line.IndexOf(' ', StringComparison.Ordinal);
                    var verb = (space < 0 ? line : line[..space]).ToUpperInvariant();
                    var rest = space < 0 ? "" : line[(space + 1)..].Trim();
                    string Ok(string words = "") => "OK" + (id is null ? "" : " @" + id) + (words.Length > 0 ? " " + words : "");
                    string Err(string words) => "ERR" + (id is null ? "" : " @" + id) + " " + words;
                    if (id is null && FactKeys.Contains(verb) && (verb != "BLACKOUT" || rest is "0" or "1"))
                    {
                        lock (_gate)
                        {
                            if (!paired) _factsAfterPaired = false;
                            _facts.Add(line);
                        }
                        continue;
                    }
                    switch (verb)
                    {
                        case "HELLO":
                            hello = rest;
                            await writer.WriteLineAsync(Ok());
                            if (!_stateAfterProof) await writer.WriteLineAsync("STATE " + State);
                            break;
                        case "NONCE":
                            nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
                            await writer.WriteLineAsync(Ok(nonce));
                            break;
                        case "AUTH-PROOF":
                            var key = Encoding.UTF8.GetBytes(_token.Replace("-", "", StringComparison.Ordinal).ToUpperInvariant());
                            var right = Convert.ToHexStringLower(HMACSHA256.HashData(key, Encoding.UTF8.GetBytes(nonce + hello))) == rest;
                            nonce = Convert.ToHexStringLower(RandomNumberGenerator.GetBytes(16));
                            if (right)
                            {
                                paired = true;
                                _paired = true;
                                await writer.WriteLineAsync(Ok("paired"));
                                if (_stateAfterProof) await writer.WriteLineAsync("STATE " + State);
                            }
                            else
                            {
                                await writer.WriteLineAsync(Err("wrong token — the pairing token is in PlayMap's Network drawer"));
                                await writer.WriteLineAsync("NONCE " + nonce);
                            }
                            break;
                        case "SUBSCRIBE" when rest.Equals("OFF", StringComparison.OrdinalIgnoreCase):
                            await writer.WriteLineAsync(Ok("pushes off"));
                            break;
                        case "SUBSCRIBE" when rest.Equals("CONTEXT", StringComparison.OrdinalIgnoreCase):
                            await writer.WriteLineAsync(Ok("subscribed context"));
                            await writer.WriteLineAsync("CONTEXT " + Context);
                            break;
                        default:
                            if (!paired)
                            {
                                lock (_gate) _errs.Add(raw);
                                await writer.WriteLineAsync(Err("not paired — this node asks for its pairing token first: AUTH-PROOF on the wire, X-PlayMap-Token on the web"));
                            }
                            else if (id is null)
                            {
                                lock (_gate) _errs.Add(raw);
                                await writer.WriteLineAsync(Err("unknown verb"));
                            }
                            else if (verb == "PLAYLIST" && rest.StartsWith("LOAD ", StringComparison.Ordinal)) await writer.WriteLineAsync(Ok("{\"stage\":\"accepted\",\"effect\":\"loading\"}"));
                            else await writer.WriteLineAsync(Ok(verb == "PLAYLIST" ? "playing " + rest[(rest.IndexOf(' ', StringComparison.Ordinal) + 1)..] : ""));
                            break;
                    }
                }
            }
            catch (Exception e) when (e is IOException or OperationCanceledException or ObjectDisposedException)
            {
                // the desk hung up, or the test is over: this connection's life ends here
            }
        }

        public void Dispose()
        {
            _stop.Cancel();
            _listener.Stop();
            _stop.Dispose();
        }
    }
}
