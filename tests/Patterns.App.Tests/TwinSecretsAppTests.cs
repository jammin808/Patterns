using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;
using static Patterns.App.Tests.TwinAppTests;

namespace Patterns.App.Tests;

/// <summary>
/// Round 89: the twin keeps the show's secrets home. A main sends a box's password, the weather key and its kept
/// pages only to a standby whose link never leaves this machine, and only with SEND SECRETS on (off by default); to a
/// standby on another machine they are refused with words, whatever the setting; a caller never hears them. A standby
/// without them says so on its line and tells the main, so both desks, STATE, the Super Check and the glance say it.
/// The twin's port listens on the desk's bind address, and a bind that is not an address keeps it closed.
/// </summary>
public class TwinSecretsAppTests
{
    private const string Key = "hunter2";

    private static (TcpClient Client, NetworkStream Stream, StreamReader Reader) JoinMain(int port, TwinJoin join, string key = Key, IPAddress? at = null)
    {
        var client = new TcpClient();
        TestApp.Pump(client.ConnectAsync(at ?? IPAddress.Loopback, port).ContinueWith(_ => true));
        var stream = client.GetStream();
        var reader = new StreamReader(stream, Encoding.UTF8, false, 1 << 16, leaveOpen: true);
        Assert.True(Join(stream, reader, join, key), "the main proved the key first");
        return (client, stream, reader);
    }

    /// <summary>The raw lines a joiner hears until every word wanted (and the section named, where one is) has come once.</summary>
    private static List<string> LinesUntil(StreamReader reader, List<string> heard, params (TwinWord Word, string Name)[] wanted)
    {
        var due = wanted.ToList();
        while (due.Count > 0)
        {
            var line = ReadLine(reader);
            heard.Add(line);
            var msg = TwinMessage.Parse(line);
            due.RemoveAll(w => w.Word == msg.Word && (w.Name.Length == 0 || w.Name == msg.Name));
        }
        return heard;
    }

    /// <summary>
    /// Every line that comes within the time, pumping the desk meanwhile — for a word that must not come. The read still
    /// in flight at the end is the caller's to end with the reader, so this is a link's last read.
    /// </summary>
    private static List<string> LinesFor(StreamReader reader, int ms)
    {
        var heard = new List<string>();
        var deadline = Environment.TickCount64 + ms;
        var next = reader.ReadLineAsync();
        while (Environment.TickCount64 < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            if (!next.IsCompleted)
            {
                Thread.Sleep(5);
                continue;
            }
            if (!next.IsCompletedSuccessfully || next.Result is not { } line) break;
            heard.Add(line);
            next = reader.ReadLineAsync();
        }
        return heard;
    }

    /// <summary>The air record as this desk last wrote it, once it holds the programme whole (EDIT SAFE open).</summary>
    private static RecoverySnapshot AirWritten(AppServices services)
    {
        RecoverySnapshot? moved = null;
        void Moved(RecoverySnapshot? r) => moved = r;
        services.RecoveryMoved += Moved;
        try
        {
            services.WriteRunPlace();
            PumpUntil(() => moved?.Air is not null);
            return moved!;
        }
        finally
        {
            services.RecoveryMoved -= Moved;
        }
    }

    private const string TwinCanary = "twin-canary-1129";
    private static readonly string[] MachinesOwn = { TwinCanary, "token-canary-5521", "swk-canary-8812", "passcode-canary-4410", "mgmt-canary-3390" };
    private static readonly string[] ShowsOwn = { "pj-canary-7731", "wx-canary-6620" };

    /// <summary>Every secret a desk holds, each a value no line may carry by chance: the machine's own and the show's.</summary>
    private static void Canaries(ShowState state)
    {
        state.Twin.Key = TwinCanary;
        state.Control.Token = MachinesOwn[1];
        state.Control.Stagewatch.ApiKey = MachinesOwn[2];
        state.Install.AdminPasscode = MachinesOwn[3];
        state.Install.ManagementToken = MachinesOwn[4];
        state.Interactive.Devices.Add(new DeviceConfig { Id = "proj", Name = "Proj", Profile = DeviceProfile.PjLink, Link = DeviceLink.Tcp, Port = "10.0.0.7", Secret = ShowsOwn[0], Enabled = false });
        state.Weather.ApiKey = ShowsOwn[1];
    }

    private static void CarriesNone(IEnumerable<string> lines, IEnumerable<string> canaries, string who)
    {
        foreach (var line in lines)
        {
            foreach (var canary in canaries) Assert.False(line.Contains(canary, StringComparison.Ordinal), $"{who} heard {canary}: {TwinMessage.Parse(line).Word}");
        }
    }

    /// <summary>The words a joiner hears from the WELCOME up to and including the SHOW.</summary>
    private static List<TwinMessage> UpToTheShow(StreamReader reader)
    {
        var heard = new List<TwinMessage>();
        while (true)
        {
            var msg = TwinMessage.Parse(ReadLine(reader));
            heard.Add(msg);
            if (msg.Word is TwinWord.Show or TwinWord.Refused) return heard;
        }
    }

    /// <summary>The next SECTION line of that name, skipping the beats and the other sections between.</summary>
    private static TwinMessage NextSection(StreamReader reader, string name)
    {
        for (var i = 0; i < 40; i++)
        {
            var section = ReadWord(reader, TwinWord.Section);
            if (section.Name == name) return section;
        }
        throw new TimeoutException($"no {name} section came");
    }

    /// <summary>The LACKS words the standby sends until one says <paramref name="expected"/> — a beat may say an earlier state first.</summary>
    private static void LacksUntil(StreamReader reader, TwinSecretsLack expected)
    {
        TwinSecretsLack? last = null;
        for (var i = 0; i < 10; i++)
        {
            last = TwinSecretsLack.Parse(ReadWord(reader, TwinWord.Lacks).Payload);
            if (last == expected) return;
        }
        Assert.Equal(expected, last);
    }

    private static JsonElement TwinRow(AppServices services) => JsonDocument.Parse(new CommandRouter(services).StateJson()).RootElement.GetProperty("twin");

    private static CheckRow TwinCheck(AppServices services) => SuperCheck.Run(services.Metrics.GatherFacts()).Rows.Single(r => r.Item == "Twin");

    private static void ShowWithSecrets(ShowState state)
    {
        state.Weather.ApiKey = "wx-key";
        state.Interactive.Devices.Add(new DeviceConfig { Id = "proj", Name = "Proj", Profile = DeviceProfile.PjLink, Link = DeviceLink.Tcp, Port = "10.0.0.7", Secret = "pjpass", Enabled = false });
    }

    [AvaloniaFact]
    public void AMainKeepsTheSecretsHomeByDefaultSendsThemOnlyToAStandbyOnThisMachineAndRefusesAnotherMachineWithWords()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            Assert.False(vm.State.Twin.SendSecrets);                                                  // off by default
            vm.State.Twin.Port = FreePort();
            vm.State.Twin.Key = Key;
            ShowWithSecrets(vm.State);
            vm.State.Twin.Role = TwinRole.Main;
            Dispatcher.UIThread.RunJobs();

            // A standby on this machine, SEND SECRETS off: told why and which boxes it is not sent the passwords of — the
            // ids, never a password — and the show it is sent carries none.
            var (client, stream, reader) = JoinMain(vm.State.Twin.Port, new TwinJoin("Backup desk", "BACKUP-PC", "abcd1234", ""));
            using (client)
            using (reader)
            {
                var heard = UpToTheShow(reader);
                Assert.Equal(new[] { TwinWord.Welcome, TwinWord.Secrets, TwinWord.Show }, heard.Select(m => m.Word));
                var offer = TwinSecretsOffer.Parse(heard[1].Payload)!;
                Assert.Equal(TwinSecrets.Off, offer.Why);
                Assert.Equal(new[] { "proj" }, offer.Devices);
                Assert.True(offer.Weather);
                foreach (var m in heard)
                {
                    Assert.DoesNotContain("pjpass", m.Payload, StringComparison.Ordinal);
                    Assert.DoesNotContain("wx-key", m.Payload, StringComparison.Ordinal);
                }
                PumpUntil(() => services.Twin.StandbyNames.Count == 1);
                Assert.Equal("off", TwinRow(services).GetProperty("secrets").GetString());
                Assert.Equal(CheckLight.Green, TwinCheck(services).Light);                          // nothing said missing yet

                // The standby says what it is without: the main's line, its glance, STATE, the Super Check and the health words say it too.
                Write(stream, TwinMessage.Format(TwinWord.Lacks, new TwinSecretsLack(1, true, 0).ToJson()));
                PumpUntil(() => services.Twin.WithoutWords.Length > 0);
                Assert.Contains("Backup desk is WITHOUT 1 device password and the weather key — SEND SECRETS is off: tick it (Machine page, TWIN) to send them to this machine's standby, or type the password and the weather key there.", services.Twin.Status, StringComparison.Ordinal);
                Assert.Contains("Backup desk WITHOUT 1 PASSWORD, WEATHER KEY", services.Twin.GlanceWords, StringComparison.Ordinal);
                Assert.Contains("WITHOUT 1 device password", services.Twin.HealthWords, StringComparison.Ordinal);
                var row = TwinRow(services);
                Assert.Equal("Backup desk: 1 device password and the weather key", row.GetProperty("without").GetString());
                Assert.Contains("WITHOUT 1 device password", row.GetProperty("words").GetString(), StringComparison.Ordinal);   // the deck's twin_words
                var check = TwinCheck(services);
                Assert.Equal(CheckLight.Amber, check.Light);
                Assert.Equal("main · without secrets", check.Value);
                Assert.Contains("\"without\":\"Backup desk: 1 device password and the weather key\"", services.Twin.StatusJson(), StringComparison.Ordinal);

                // SEND SECRETS on: within the beat it is told they ride, and the sections that carry them follow at once.
                services.BulkEdit(() => vm.State.Twin.SendSecrets = true);
                services.Twin.Tick();
                var ride = TwinSecretsOffer.Parse(ReadWord(reader, TwinWord.Secrets).Payload)!;
                Assert.True(ride.Rides);
                Assert.Empty(ride.Devices);
                Assert.Contains("pjpass", NextSection(reader, nameof(ShowState.Interactive)).Payload, StringComparison.Ordinal);
                Assert.Equal("ride", TwinRow(services).GetProperty("secrets").GetString());
                Write(stream, TwinMessage.Format(TwinWord.Lacks, TwinSecretsLack.None.ToJson()));
                PumpUntil(() => services.Twin.WithoutWords.Length == 0);
                Assert.DoesNotContain("WITHOUT", services.Twin.Status, StringComparison.Ordinal);
                Assert.Equal(CheckLight.Green, TwinCheck(services).Light);

                // Another machine: refused with words whatever the setting — told why, sent no password, no key, no pass.
                services.Twin.SameMachineOf = (_, _) => false;
                var (far, farStream, farReader) = JoinMain(vm.State.Twin.Port, new TwinJoin("Hall B", "HALL-B-PC", "abcd5678", ""));
                using (far)
                using (farReader)
                {
                    var farHeard = UpToTheShow(farReader);
                    Assert.Equal(new[] { TwinWord.Welcome, TwinWord.Secrets, TwinWord.Show }, farHeard.Select(m => m.Word));
                    var plain = TwinSecretsOffer.Parse(farHeard[1].Payload)!;
                    Assert.Equal(TwinSecrets.Plain, plain.Why);
                    Assert.Equal(new[] { "proj" }, plain.Devices);
                    Assert.DoesNotContain("pjpass", farHeard[2].Payload, StringComparison.Ordinal);
                    Assert.DoesNotContain("wx-key", farHeard[2].Payload, StringComparison.Ordinal);
                    PumpUntil(() => services.Twin.StandbyNames.Count == 2);
                    Assert.Contains(TwinSecrets.Refused("Hall B"), File.ReadAllText(Path.Combine(b.Dir, "patterns.log")), StringComparison.Ordinal);
                    Assert.Equal("plain", TwinRow(services).GetProperty("secrets").GetString());

                    // An edit: the standby here gets the password, the one on another machine its blank.
                    services.BulkEdit(() => vm.State.Interactive.Devices[0].Name = "Projector");
                    Assert.Contains("pjpass", NextSection(reader, nameof(ShowState.Interactive)).Payload, StringComparison.Ordinal);
                    var farSection = NextSection(farReader, nameof(ShowState.Interactive));
                    Assert.Contains("\"Name\":\"Projector\"", farSection.Payload, StringComparison.Ordinal);
                    Assert.DoesNotContain("pjpass", farSection.Payload, StringComparison.Ordinal);

                    // It says what it is without: the main's words name the plain link and what to type there.
                    Write(farStream, TwinMessage.Format(TwinWord.Lacks, new TwinSecretsLack(1, true, 0).ToJson()));
                    PumpUntil(() => services.Twin.WithoutWords.Contains("Hall B", StringComparison.Ordinal));
                    Assert.Contains("Hall B is WITHOUT 1 device password and the weather key — they stay on this desk: the twin's link to another machine is plain; type the password and the weather key on Hall B.", services.Twin.Status, StringComparison.Ordinal);
                    Assert.Equal(CheckLight.Amber, TwinCheck(services).Light);
                }
                services.Twin.SameMachineOf = TwinSecrets.SameMachine;
            }

            // A caller on this machine, SEND SECRETS on: it never takes the show — no SECRETS word, no password, no key.
            var (caller, callerStream, callerReader) = JoinMain(vm.State.Twin.Port, new TwinJoin("Caller", "CALL-PC", "abcd9999", "", Kind: "caller"));
            using (caller)
            using (callerReader)
            {
                var heard = UpToTheShow(callerReader);
                Assert.DoesNotContain(heard, m => m.Word == TwinWord.Secrets);
                Assert.DoesNotContain("pjpass", heard[^1].Payload, StringComparison.Ordinal);
                Assert.DoesNotContain("wx-key", heard[^1].Payload, StringComparison.Ordinal);
                PumpUntil(() => services.Twin.StandbyNames.Contains("Caller"));

                // A LACKS word from a caller is not a standby's: it never takes the show, so nothing it says it lacks is said.
                Write(callerStream, TwinMessage.Format(TwinWord.Lacks, new TwinSecretsLack(3, true, 2).ToJson()));
                services.Twin.Tick();
                Assert.DoesNotContain(LinesFor(callerReader, 1000), l => TwinMessage.Parse(l).Word == TwinWord.Secrets);
                Assert.Equal("", services.Twin.WithoutWords);
                Assert.Equal("", TwinRow(services).GetProperty("without").GetString());
                Assert.DoesNotContain("WITHOUT", services.Twin.Status, StringComparison.Ordinal);
            }
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AStandbyTheMainKeepsTheSecretsFromSaysWhatItIsWithoutTellsTheMainAndKeepsWhatIsTypedHere()
    {
        var b = TestApp.Boot();
        using var main = new TcpListener(IPAddress.Loopback, 0);
        main.Start();
        try
        {
            var (services, vm, _) = b;
            var twin = services.Twin;
            vm.State.Twin.Port = ((IPEndPoint)main.LocalEndpoint).Port;
            vm.State.Twin.MainHost = "127.0.0.1";
            vm.State.Twin.Key = Key;
            vm.State.Twin.Role = TwinRole.Standby;
            Dispatcher.UIThread.RunJobs();

            var (peer, reader, _) = AcceptJoin(main);
            using var _peer = peer;
            using var _reader = reader;
            var stream = peer.GetStream();

            // The main's show: a box with a password and a weather key — blanked on the wire — and a kept page this machine has no pass for.
            var theirs = new ShowState { Name = "From the main" };
            ShowWithSecrets(theirs);
            theirs.Web.SavedUrls.Add(WindowAddresses.Kept("10.0.0.5", 9924, WindowAddresses.NewId()));
            Write(stream, TwinMessage.Format(TwinWord.Welcome, new TwinWelcome("MAIN-DESK", "MAIN-PC", "ef01", 1, 0, "", "From the main").ToJson()));
            Write(stream, TwinMessage.Format(TwinWord.Secrets, TwinSecretsOffer.For(theirs, TwinSecrets.Plain).ToJson()));
            Write(stream, TwinMessage.Format(TwinWord.Show, TwinSync.WireJson(theirs, sendSecrets: false)));
            Write(stream, TwinMessage.Format(TwinWord.Air, "null"));
            PumpUntil(() => twin.Phase == TwinPhase.InStep);
            Assert.Equal("", vm.State.Interactive.Devices[0].Secret);

            // Read at the beat: its line says what it is without and why; the main is told the counts.
            twin.Tick();
            LacksUntil(reader, new TwinSecretsLack(1, true, 1));
            Assert.Contains("· WITHOUT 1 device password, the weather key and 1 kept page — MAIN-DESK keeps them: the twin's link to another machine is plain; type the password and the weather key here (Interactive and Weather)", twin.Status, StringComparison.Ordinal);
            Assert.Contains("WITHOUT 1 PASSWORD, WEATHER KEY, 1 KEPT PAGE", twin.GlanceWords, StringComparison.Ordinal);
            var row = TwinRow(services);
            Assert.Equal("plain", row.GetProperty("secrets").GetString());
            Assert.Equal("1 device password, the weather key and 1 kept page", row.GetProperty("without").GetString());
            Assert.Equal(CheckLight.Amber, TwinCheck(services).Light);
            Assert.Contains("WITHOUT", twin.HealthWords, StringComparison.Ordinal);

            // Typed here: the main is told, and a section that lands blank keeps what this desk typed.
            services.BulkEdit(() =>
            {
                vm.State.Interactive.Devices[0].Secret = "typed-here";
                vm.State.Weather.ApiKey = "wx-here";
            });
            twin.Tick();
            LacksUntil(reader, new TwinSecretsLack(0, false, 1));
            theirs.Interactive.Devices[0].Name = "Projector";
            Write(stream, TwinMessage.Format(TwinWord.Section, TwinSync.WireSectionJson(theirs, nameof(ShowState.Interactive), sendSecrets: false), nameof(ShowState.Interactive)));
            PumpUntil(() => vm.State.Interactive.Devices[0].Name == "Projector");
            Assert.Equal("typed-here", vm.State.Interactive.Devices[0].Secret);
            twin.Tick();
            Assert.Contains("WITHOUT 1 kept page — MAIN-DESK keeps them: the twin's link to another machine is plain; a kept page opens black here until pasted again once this desk has the show", twin.Status, StringComparison.Ordinal);

            // The main says they ride: nothing is missing on the twin's account, and the main hears so.
            Write(stream, TwinMessage.Format(TwinWord.Secrets, TwinSecretsOffer.For(theirs, TwinSecrets.Ride).ToJson()));
            PumpUntil(() => TwinRow(services).GetProperty("secrets").GetString() == "ride");
            twin.Tick();
            LacksUntil(reader, TwinSecretsLack.None);
            Assert.DoesNotContain("WITHOUT", twin.Status, StringComparison.Ordinal);
            Assert.Equal(CheckLight.Green, TwinCheck(services).Light);

            // The main's air while its EDIT SAFE is open, as a main on another machine sends it — without its own sections
            // and with the secrets blank — then the main goes: the takeover lands that programme with this desk's own
            // sections and the secrets typed here, never the defaults the wire left.
            var programme = new ShowState { Name = "On air at the main" };
            ShowWithSecrets(programme);
            programme.Twin.Key = "their-key";
            Write(stream, TwinMessage.Format(TwinWord.Air, TwinSync.WireAirJson(new RecoverySnapshot(true, false, DateTime.UtcNow, Sandboxed: true, Air: programme), sendSecrets: false)));
            peer.Close();
            PumpUntil(() => twin.Phase == TwinPhase.MainSilent);
            var took = services.Actions.Execute(ShowActionKind.TwinTakeOver, ActionOrigin.Desk);
            Assert.True(took.Ok, took.Message);
            var air = services.AirState;
            Assert.True(services.Sandbox.Active);
            Assert.Equal("On air at the main", air.Name);
            Assert.Equal(TwinRole.Standby, air.Twin.Role);
            Assert.Equal(Key, air.Twin.Key);
            Assert.Equal("typed-here", air.Interactive.Devices.Single().Secret);
            Assert.Equal("wx-here", air.Weather.ApiKey);
        }
        finally
        {
            main.Stop();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheTwinsPortListensOnTheDesksBindAddressAndStaysClosedOnOneThatIsNot()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            var port = FreePort();
            vm.State.Control.Bind = "127.0.0.1";
            vm.State.Twin.Port = port;
            vm.State.Twin.Key = Key;
            vm.State.Twin.Role = TwinRole.Main;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(IPAddress.Loopback, services.Twin.ListeningOn?.Address);
            Assert.Null(services.Twin.AlsoListeningOn);                                                // 127.0.0.1 is the bind itself
            Assert.Contains($"listening for a standby on port {port} at 127.0.0.1 only;", services.Twin.Status, StringComparison.Ordinal);
            Assert.False(services.Twin.PortClosed);

            // Not an address: closed, never every interface — red on the Twin row, the dashboard and the Eye, PORT CLOSED
            // on the glance line, the fix on the Machine page's line.
            services.BulkEdit(() => vm.State.Control.Bind = "10.0.0");
            Dispatcher.UIThread.RunJobs();
            Assert.Null(services.Twin.ListeningOn);
            Assert.True(services.Twin.PortClosed);
            Assert.Contains("the main's port is closed — '10.0.0' is not an address", services.Twin.Status, StringComparison.Ordinal);
            var check = TwinCheck(services);
            Assert.Equal(CheckLight.Red, check.Light);
            Assert.Equal("port closed", check.Value);
            Assert.Equal("TWIN main · PORT CLOSED", services.Twin.GlanceWords);
            services.Eye.Refresh();
            Assert.Equal(CheckLight.Red, services.Eye.Graph.Nodes.Single(n => n.Id == "twin").Light);

            // Empty: every interface, as before; and an address that is every interface is said as every interface.
            services.BulkEdit(() => vm.State.Control.Bind = "");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(IPAddress.Any, services.Twin.ListeningOn?.Address);
            Assert.False(services.Twin.PortClosed);
            Assert.Equal("TWIN main · no standby", services.Twin.GlanceWords);
            Assert.Equal(CheckLight.Green, TwinCheck(services).Light);
            services.BulkEdit(() => vm.State.Control.Bind = "0.0.0.0");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(IPAddress.Any, services.Twin.ListeningOn?.Address);
            Assert.Contains($"listening for a standby on port {port}; none connected", services.Twin.Status, StringComparison.Ordinal);
            Assert.DoesNotContain("0.0.0.0", services.Twin.Status, StringComparison.Ordinal);
            Assert.Null(services.Twin.AlsoListeningOn);

            // A desk that only hosts callers binds the same way.
            services.BulkEdit(() =>
            {
                vm.State.Twin.Role = TwinRole.Off;
                vm.State.Twin.AcceptCallers = true;
                vm.State.Control.Bind = "127.0.0.1";
            });
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(IPAddress.Loopback, services.Twin.ListeningOn?.Address);
            services.BulkEdit(() => vm.State.Control.Bind = "not-an-address");
            Dispatcher.UIThread.RunJobs();
            Assert.Null(services.Twin.ListeningOn);
            Assert.True(services.Twin.PortClosed);
            Assert.Contains("the callers' port is closed", services.Twin.Status, StringComparison.Ordinal);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AMainBoundToOneAddressAlsoListensOnLoopbackForAStandbyOnThisMachineAndTheSecretsRideToIt()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var port = FreePort();
            // 127.0.0.2 stands in for the control network's card: an address of this machine that is not 127.0.0.1. A
            // standby on this machine dials 127.0.0.1 whatever the bind (TwinLaunch), so a pulled cable — the card's
            // address withdrawn — never drops its link, and silence never makes it take over from a healthy main.
            vm.State.Control.Bind = "127.0.0.2";
            vm.State.Twin.Port = port;
            vm.State.Twin.Key = Key;
            vm.State.Twin.SendSecrets = true;
            ShowWithSecrets(vm.State);
            vm.State.Twin.Role = TwinRole.Main;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(IPAddress.Parse("127.0.0.2"), services.Twin.ListeningOn?.Address);
            Assert.Equal(IPAddress.Loopback, services.Twin.AlsoListeningOn?.Address);
            Assert.Equal(port, services.Twin.AlsoListeningOn?.Port);
            Assert.Contains($"listening for a standby on port {port} at 127.0.0.2 only (and 127.0.0.1, for a standby on this machine)", services.Twin.Status, StringComparison.Ordinal);
            Assert.Equal($"127.0.0.1:{port}", TwinLaunch.Arguments("/show/twin-standby", port, Key)[3]);

            // The standby on this machine joins on loopback; the secrets ride to it.
            var (client, _, reader) = JoinMain(port, new TwinJoin("Backup desk", "BACKUP-PC", "abcd1234", ""));
            using (client)
            using (reader)
            {
                var heard = UpToTheShow(reader);
                Assert.Equal(new[] { TwinWord.Welcome, TwinWord.Secrets, TwinWord.Show }, heard.Select(m => m.Word));
                Assert.True(TwinSecretsOffer.Parse(heard[1].Payload)!.Rides);
                Assert.Contains("pjpass", heard[2].Payload, StringComparison.Ordinal);
                PumpUntil(() => services.Twin.StandbyNames.Count == 1);
                Assert.Equal("ride", TwinRow(services).GetProperty("secrets").GetString());
                Assert.Equal(CheckLight.Green, TwinCheck(services).Light);

                // The bound address takes a joiner too.
                var (bound, _, boundReader) = JoinMain(port, new TwinJoin("Hall B", "HALL-B-PC", "abcd5678", ""), at: IPAddress.Parse("127.0.0.2"));
                using (bound)
                using (boundReader)
                {
                    Assert.Equal(TwinWord.Show, UpToTheShow(boundReader)[^1].Word);
                    PumpUntil(() => services.Twin.StandbyNames.Count == 2);
                }
            }

            // Off, hosting callers: the same two listeners; callers off too, both close.
            services.BulkEdit(() => vm.State.Twin.Role = TwinRole.Off);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(IPAddress.Parse("127.0.0.2"), services.Twin.ListeningOn?.Address);
            Assert.Equal(IPAddress.Loopback, services.Twin.AlsoListeningOn?.Address);
            services.BulkEdit(() => vm.State.Twin.AcceptCallers = false);
            Dispatcher.UIThread.RunJobs();
            Assert.Null(services.Twin.ListeningOn);
            Assert.Null(services.Twin.AlsoListeningOn);
            Assert.False(services.Twin.PortClosed);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void NoLineToAStandbyCarriesTheMachinesOwnSecretsAndTheShowsRideOnlyToOneOnThisMachine()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var port = FreePort();
            vm.State.Twin.Port = port;
            Canaries(vm.State);
            vm.State.Twin.SendSecrets = true;                                                         // the worst case: on
            vm.State.Twin.Role = TwinRole.Main;
            Dispatcher.UIThread.RunJobs();

            // EDIT SAFE open: the air record holds the programme whole — and with it every secret the desk has.
            vm.IsSandboxActive = true;
            var written = AirWritten(services);
            Assert.Equal(TwinCanary, written.Air!.Twin.Key);

            // A standby on another machine: from the WELCOME to the AIR, nothing secret.
            services.Twin.SameMachineOf = (_, _) => false;
            var (far, _, farReader) = JoinMain(port, new TwinJoin("Hall B", "HALL-B-PC", "abcd5678", ""), TwinCanary);
            using (far)
            using (farReader)
            {
                var farHeard = LinesUntil(farReader, new List<string>(), (TwinWord.Air, ""));
                var farAir = TwinMessage.Parse(farHeard[^1]).Payload;
                Assert.Contains("\"Air\":{", farAir, StringComparison.Ordinal);                      // the programme rode, by the SHOW line's rule
                Assert.Contains("\"Proj\"", farAir, StringComparison.Ordinal);
                PumpUntil(() => services.Twin.StandbyNames.Count == 1);
                services.Twin.SameMachineOf = TwinSecrets.SameMachine;

                // A standby on this machine, the secrets riding: the boxes' passwords and the weather key, in the air too.
                var (near, _, nearReader) = JoinMain(port, new TwinJoin("Backup desk", "BACKUP-PC", "abcd1234", ""), TwinCanary);
                using (near)
                using (nearReader)
                {
                    var nearHeard = LinesUntil(nearReader, new List<string>(), (TwinWord.Air, ""));
                    foreach (var canary in ShowsOwn) Assert.Contains(canary, nearHeard[^1], StringComparison.Ordinal);
                    PumpUntil(() => services.Twin.StandbyNames.Count == 2);

                    // Later: an edit to each section that carries a show's secret, the air moving again, a beat — each
                    // standby its own form of every line.
                    services.BulkEdit(() =>
                    {
                        vm.State.Interactive.Devices[0].Name = "Projector";
                        vm.State.Weather.Place = "Leeds";
                    });
                    AirWritten(services);
                    services.Twin.Tick();
                    (TwinWord, string)[] later = { (TwinWord.Section, nameof(ShowState.Interactive)), (TwinWord.Section, nameof(ShowState.Weather)), (TwinWord.Air, ""), (TwinWord.Beat, "") };
                    LinesUntil(farReader, farHeard, later);
                    LinesUntil(nearReader, nearHeard, later);
                    Assert.True(farHeard.Count(l => TwinMessage.Parse(l).Word == TwinWord.Air) >= 2, "the air moved again");

                    CarriesNone(farHeard, MachinesOwn.Concat(ShowsOwn), "a standby on another machine");
                    CarriesNone(nearHeard, MachinesOwn, "a standby on this machine");
                    Assert.Contains(nearHeard, l => TwinMessage.Parse(l) is { Word: TwinWord.Section, Name: nameof(ShowState.Interactive) } m && m.Payload.Contains(ShowsOwn[0], StringComparison.Ordinal));
                    Assert.Contains(nearHeard, l => TwinMessage.Parse(l) is { Word: TwinWord.Section, Name: nameof(ShowState.Weather) } m && m.Payload.Contains(ShowsOwn[1], StringComparison.Ordinal));
                    Assert.Contains(farHeard, l => TwinMessage.Parse(l) is { Word: TwinWord.Section, Name: nameof(ShowState.Interactive) } m && m.Payload.Contains("\"Projector\"", StringComparison.Ordinal));

                    // Whatever comes after on either link, the same.
                    CarriesNone(LinesFor(farReader, 1200), MachinesOwn.Concat(ShowsOwn), "a standby on another machine");
                    CarriesNone(LinesFor(nearReader, 200), MachinesOwn, "a standby on this machine");
                }
            }
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AStandbyThatHasTheShowHandsItBackToAMainOnAnotherMachineWithoutItsSecretsWhateverItsSetting()
    {
        var b = TestApp.Boot();
        using var main = new TcpListener(IPAddress.Loopback, 0);
        main.Start();
        try
        {
            var (services, vm, _) = b;
            var twin = services.Twin;
            twin.SameMachineOf = (_, _) => false;                                                     // the main is on another machine, by the socket's word
            vm.State.Twin.Port = ((IPEndPoint)main.LocalEndpoint).Port;
            vm.State.Twin.MainHost = "127.0.0.1";
            vm.State.Twin.Key = Key;
            vm.State.Twin.SendSecrets = true;                                                        // this desk's own tick: refused for another machine all the same
            vm.State.Twin.Role = TwinRole.Standby;
            Dispatcher.UIThread.RunJobs();

            var welcome = new TwinWelcome("MAIN-DESK", "MAIN-PC", "ef01", 1, 0, "", "From the main").ToJson();
            var (peer, reader, _) = AcceptJoin(main);
            using (peer)
            using (reader)
            {
                var stream = peer.GetStream();
                Write(stream, TwinMessage.Format(TwinWord.Welcome, welcome));
                Write(stream, TwinMessage.Format(TwinWord.Show, TwinSync.WireJson(new ShowState { Name = "From the main" }, sendSecrets: false)));
                Write(stream, TwinMessage.Format(TwinWord.Air, "null"));
                PumpUntil(() => twin.Phase == TwinPhase.InStep);
                peer.Close();
            }
            PumpUntil(() => twin.Phase == TwinPhase.MainSilent);
            var took = services.Actions.Execute(ShowActionKind.TwinTakeOver, ActionOrigin.Desk);
            Assert.True(took.Ok, took.Message);

            // It ran the show here: a box's password and the weather key typed, a page pasted (kept), EDIT SAFE open.
            var kept = services.WebPasses.Secure("https://w.example/feed?token=rv7777zz");
            Assert.True(WindowAddresses.IsKept(kept));
            services.BulkEdit(() =>
            {
                vm.State.Interactive.Devices.Add(new DeviceConfig { Id = "proj", Name = "Proj", Profile = DeviceProfile.PjLink, Link = DeviceLink.Tcp, Port = "10.0.0.7", Secret = "pj-here", Enabled = false });
                vm.State.Weather.ApiKey = "wx-here";
                vm.State.Web.SavedUrls.Add(kept);
            });
            vm.IsSandboxActive = true;
            AirWritten(services);

            (TcpClient Peer, StreamReader Reader) Rejoined()
            {
                for (var i = 0; i < 10; i++)
                {
                    var (p, r, join) = AcceptJoin(main);
                    if (join.TookOver) return (p, r);
                    r.Dispose();
                    p.Dispose();
                }
                throw new TimeoutException("no standby that has the show joined");
            }

            // Back to the main on another machine: its show and its air with the passwords and the key blank, no passes.
            var (peer2, reader2) = Rejoined();
            using (peer2)
            using (reader2)
            {
                Write(peer2.GetStream(), TwinMessage.Format(TwinWord.Welcome, welcome));
                var show = ReadWord(reader2, TwinWord.Show).Payload;
                Assert.Contains("\"Proj\"", show, StringComparison.Ordinal);
                var air = ReadWord(reader2, TwinWord.Air).Payload;
                Assert.Contains("\"Air\":{", air, StringComparison.Ordinal);
                var after = LinesFor(reader2, 1500);
                Assert.DoesNotContain(after, l => TwinMessage.Parse(l).Word == TwinWord.Kept);
                CarriesNone(after.Append(show).Append(air), new[] { "pj-here", "wx-here", "rv7777zz", Key }, "a main on another machine");
            }

            // The same desk handing back to a main on this machine: they ride, the page's pass after the show that names it.
            twin.SameMachineOf = TwinSecrets.SameMachine;
            var (peer3, reader3) = Rejoined();
            using (peer3)
            using (reader3)
            {
                Write(peer3.GetStream(), TwinMessage.Format(TwinWord.Welcome, welcome));
                Assert.Contains("pj-here", ReadWord(reader3, TwinWord.Show).Payload, StringComparison.Ordinal);
                Assert.Contains("wx-here", ReadWord(reader3, TwinWord.Air).Payload, StringComparison.Ordinal);
                Assert.True(WebPassBook.TryParse(ReadWord(reader3, TwinWord.Kept).Payload, out var book));
                Assert.Contains("rv7777zz", book.Resolve(kept).Url, StringComparison.Ordinal);
            }
        }
        finally
        {
            main.Stop();
            b.Dispose();
        }
    }
}
