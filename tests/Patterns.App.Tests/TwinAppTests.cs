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
/// The twin end to end, one side at a time against a fake peer on a socket: the main welcomes a
/// standby, mirrors an edit as the section it landed in and beats; the standby follows a main,
/// holds its outputs, hears the silence, takes the show over and stands by again.
/// </summary>
public class TwinAppTests
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

    /// <summary>The next line on a socket, pumping the UI thread meanwhile (the desk's timers and its replies live there).</summary>
    private static string ReadLine(StreamReader reader, int timeoutMs = 10000)
    {
        var task = reader.ReadLineAsync();
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!task.IsCompleted)
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
            if (Environment.TickCount64 > deadline) throw new TimeoutException("the line never came");
        }
        return task.Result ?? throw new EndOfStreamException("the peer closed the link");
    }

    private static TwinMessage ReadWord(StreamReader reader, TwinWord wanted, int timeoutMs = 10000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var msg = TwinMessage.Parse(ReadLine(reader, timeoutMs));
            if (msg.Word == wanted) return msg;
        }
        throw new TimeoutException($"no {wanted} came");
    }

    private static void Write(NetworkStream stream, string line) => stream.Write(Encoding.UTF8.GetBytes(line + "\n"));

    /// <summary>The key every test's twin shares.</summary>
    private const string Key = "hunter2";

    /// <summary>
    /// A fake standby joining a real main: the JOIN with a nonce and no key, the main's proof over
    /// that nonce checked, this side's proof over the main's nonce written — then the main's
    /// WELCOME or REFUSED is the caller's to read. False when the main could not prove the key
    /// with <paramref name="key"/> (a stranger's key against a real main, in the tests that try one).
    /// </summary>
    private static bool Join(NetworkStream stream, StreamReader reader, TwinJoin join, string key)
    {
        var nonce = TwinAuth.NewNonce();
        Write(stream, TwinMessage.Format(TwinWord.Join, (join with { Key = "", Nonce = nonce }).ToJson()));
        var first = TwinMessage.Parse(ReadLine(reader));
        if (first.Word != TwinWord.Challenge) return false;                     // refused before any proof: the caller reads the reason
        var challenge = TwinChallenge.Parse(first.Payload)!;
        var mainProved = TwinAuth.Verify(key, nonce, challenge.Nonce, challenge.Proof);
        Write(stream, TwinMessage.Format(TwinWord.Proof, TwinAuth.Proof(key, challenge.Nonce, join.Instance)));
        return mainProved;
    }

    /// <summary>
    /// Whether a word arrives within the time, reading only what is there — never a read left in
    /// flight on the reader, which the next read would trip over. The state assertions beside it
    /// are the real check; this one says the wire agreed.
    /// </summary>
    private static bool Arrives(TcpClient client, StreamReader reader, TwinWord wanted, int timeoutMs)
    {
        var stream = client.GetStream();
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            while (stream.DataAvailable)
            {
                var line = reader.ReadLine();
                if (line is not null && TwinMessage.Parse(line).Word == wanted) return true;
            }
            Thread.Sleep(10);
        }
        return false;
    }

    /// <summary>
    /// The next standby that joins: connections are accepted until one says JOIN. A dial the standby
    /// cut short itself — a takeover or a goodbye landing while a connect was in flight — sits in the
    /// listener's backlog with nothing said and is simply closed.
    /// </summary>
    private static (TcpClient Peer, StreamReader Reader, TwinJoin Join) AcceptJoin(TcpListener main, int timeoutMs = 15000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (Environment.TickCount64 < deadline)
        {
            var accept = main.AcceptTcpClientAsync();
            PumpUntil(() => accept.IsCompleted, (int)Math.Max(1000, deadline - Environment.TickCount64));
            var peer = accept.Result;
            var reader = new StreamReader(peer.GetStream(), Encoding.UTF8, false, 4096, leaveOpen: true);
            try
            {
                var join = TwinJoin.Parse(ReadWord(reader, TwinWord.Join, 3000).Payload);
                if (join is not null)
                {
                    // The key is proved, never read off the wire: this fake main answers the joiner's
                    // nonce with its own proof, then checks the joiner's over its nonce.
                    Assert.Equal("", join.Key);
                    Assert.Equal(48, join.Nonce.Length);
                    var nonce = TwinAuth.NewNonce();
                    Write(peer.GetStream(), TwinMessage.Format(TwinWord.Challenge, new TwinChallenge(nonce, TwinAuth.Proof(Key, join.Nonce, nonce)).ToJson()));
                    var proof = ReadWord(reader, TwinWord.Proof, 3000);
                    Assert.True(TwinAuth.Verify(Key, nonce, join.Instance, proof.Payload), "the standby proved the key");
                    return (peer, reader, join);
                }
            }
            catch (IOException)
            {
                // closed before it said anything: a dial cut short
            }
            catch (TimeoutException)
            {
                // nothing more came: what was read is the answer
            }
            reader.Dispose();
            peer.Dispose();
        }
        throw new TimeoutException("no standby joined");
    }

    [AvaloniaFact]
    public void TheMainWelcomesAStandbyMirrorsAnEditAsItsSectionAndBeats()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            vm.State.Name = "Gala";
            vm.State.Twin.Port = FreePort();
            vm.State.Twin.Key = "hunter2";
            vm.State.Twin.Role = TwinRole.Main;
            Dispatcher.UIThread.RunJobs(); // publish → the listener opens
            Assert.Equal(TwinPhase.Listening, services.Twin.Phase);
            Assert.Contains($"listening for a standby on port {vm.State.Twin.Port}", services.Twin.Status);
            Assert.Equal(vm.State.Twin.Port, services.Beacon.Build().Twin);           // the beacon names the port a standby may dial

            // A wrong key is refused, with the reason.
            using (var stranger = new TcpClient())
            {
                TestApp.Pump(stranger.ConnectAsync(IPAddress.Loopback, vm.State.Twin.Port).ContinueWith(_ => true));
                using var strangerStream = stranger.GetStream();
                using var strangerReader = new StreamReader(strangerStream, Encoding.UTF8, false, 4096, leaveOpen: true);
                Join(strangerStream, strangerReader, new TwinJoin("Stranger", "OTHER-PC", "zzzz", "wrong"), "wrong");
                var refused = TwinMessage.Parse(ReadLine(strangerReader));
                Assert.Equal(TwinWord.Refused, refused.Word);
                Assert.Equal("wrong key", refused.Payload);
            }

            using var client = new TcpClient();
            TestApp.Pump(client.ConnectAsync(IPAddress.Loopback, vm.State.Twin.Port).ContinueWith(_ => true));
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 1 << 16, leaveOpen: true);
            Join(stream, reader, new TwinJoin("Backup desk", "BACKUP-PC", "abcd1234", "hunter2"), "hunter2");

            var welcome = TwinWelcome.Parse(ReadWord(reader, TwinWord.Welcome).Payload);
            Assert.NotNull(welcome);
            Assert.Equal("Gala", welcome!.Show);
            Assert.Equal(Environment.ProcessId, welcome.Pid);
            Assert.Equal(services.Twin.Instance, welcome.Instance);
            var showJson = ReadWord(reader, TwinWord.Show).Payload;
            var show = JsonUtil.Deserialize<ShowState>(showJson);
            Assert.Equal("Gala", show!.Name);
            Assert.DoesNotContain("\"Twin\":", showJson);       // the machine's own sections never travel: the wire carries the mirrored ones only
            Assert.DoesNotContain("hunter2", showJson);
            Assert.Equal(TwinRole.Off, show.Twin.Role);
            Assert.Equal("null", ReadWord(reader, TwinWord.Air).Payload);   // nothing is live
            PumpUntil(() => services.Twin.StandbyNames.Count == 1);
            Assert.Equal("Backup desk", services.Twin.StandbyNames[0]);
            Assert.Contains("standby Backup desk in step", services.Twin.Status);

            // An edit on the desk arrives as the section it landed in — nothing else travels.
            vm.State.LooksAndCues.Looks.Add(new LookConfig { Name = "Walk-in" });
            Dispatcher.UIThread.RunJobs();
            var section = ReadWord(reader, TwinWord.Section);
            Assert.Equal(nameof(ShowState.LooksAndCues), section.Name);
            var looks = JsonUtil.Deserialize<LooksConfig>(section.Payload);
            Assert.Equal("Walk-in", Assert.Single(looks!.Looks).Name);

            // The caller's place moves: the air record follows it.
            services.WriteRunPlace();
            var air = ReadWord(reader, TwinWord.Air);
            Assert.NotEqual("null", air.Payload);
            Assert.NotNull(JsonUtil.Deserialize<RecoverySnapshot>(air.Payload));

            // A beat: one rode the welcome, and every tick sends another; the standby's own beat is counted.
            services.Twin.Tick();
            var beat = ReadWord(reader, TwinWord.Beat, 4000);
            var stamped = TwinBeat.Parse(beat.Payload);                                   // "seq sent [peerSent peerReceived]": the beat carries the main's clock
            Assert.True(stamped.Seq >= 1, beat.Payload);
            Assert.True(stamped.HasStamps, beat.Payload);
            Write(stream, TwinMessage.Format(TwinWord.Beat, "1"));                        // a beat with no stamps, as a build before the clocks sent it, still counts
            Assert.Contains("\"role\":\"main\"", services.Twin.StatusJson());
            Assert.Contains("\"standbys\":[\"Backup desk\"]", services.Twin.StatusJson());

            // Off: the standby is told goodbye and the port closes.
            vm.State.Twin.Role = TwinRole.Off;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(TwinWord.Bye, ReadWord(reader, TwinWord.Bye).Word);
            Assert.Equal(TwinPhase.Off, services.Twin.Phase);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AStandbyFollowsHoldsItsOutputsHearsTheSilenceTakesOverAndStandsByAgain()
    {
        var b = TestApp.Boot();
        using var main = new TcpListener(IPAddress.Loopback, 0);
        main.Start();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            var twin = services.Twin;
            var clockOffset = TimeSpan.Zero;
            twin.Clock = () => DateTime.UtcNow + clockOffset;
            vm.State.Name = "Mine";
            vm.State.Twin.Port = ((IPEndPoint)main.LocalEndpoint).Port;
            vm.State.Twin.MainHost = "127.0.0.1";
            vm.State.Twin.Key = "hunter2";
            vm.State.Twin.Role = TwinRole.Standby;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(TwinPhase.Connecting, twin.Phase);
            Assert.Equal("this desk is the standby twin", services.OutputsHeldBy);

            // The fake main accepts the join and hands over a show.
            var (peer, reader, join) = AcceptJoin(main);
            using var _peer = peer;
            using var _reader = reader;
            var stream = peer.GetStream();
            Assert.Equal("", join.Key);                                     // proved over the nonces, never on the wire
            Assert.Equal(48, join.Nonce.Length);
            Assert.Equal(twin.Instance, join.Instance);

            var theirs = new ShowState { Name = "From the main" };
            theirs.Twin.Role = TwinRole.Main;                 // never lands here
            theirs.Watchdog.BeaconEnabled = true;             // nor this
            theirs.LooksAndCues.Looks.Add(new LookConfig { Name = "Walk-in" });
            Write(stream, TwinMessage.Format(TwinWord.Welcome, new TwinWelcome("MAIN-DESK", "SOME-OTHER-PC", "ef01", 1, 0, "", "From the main").ToJson()));
            Write(stream, TwinMessage.Format(TwinWord.Show, TwinSync.ShowJson(theirs)));
            Write(stream, TwinMessage.Format(TwinWord.Air, "null"));
            PumpUntil(() => twin.Phase == TwinPhase.InStep);
            Assert.Equal("From the main", vm.State.Name);
            Assert.Equal("Walk-in", Assert.Single(vm.State.LooksAndCues.Looks).Name);
            Assert.Contains("Walk-in", vm.Show.LookNames);                         // the desk's lists followed
            Assert.Equal(TwinRole.Standby, vm.State.Twin.Role);               // its own settings stayed
            Assert.False(vm.State.Watchdog.BeaconEnabled);
            Assert.Equal("MAIN-DESK", twin.MainName);
            Assert.StartsWith("STANDBY for MAIN-DESK — in step", twin.Status);
            Assert.Contains("in step with MAIN-DESK", vm.StatusMessage);

            // Outputs are held however asked; a beat from the standby reaches the main.
            var on = services.Actions.Execute(ShowActionKind.OutputsOn, ActionOrigin.Desk);
            Assert.False(on.Ok);
            Assert.Contains("held closed", on.Message);
            Assert.False(services.Outputs.IsLive);
            twin.Tick();
            Assert.Equal(TwinWord.Beat, ReadWord(reader, TwinWord.Beat, 4000).Word);

            // A section lands in place; a name change on the main is a name change here.
            Write(stream, TwinMessage.Format(TwinWord.Section, "\"Renamed at the main\"", nameof(ShowState.Name)));
            PumpUntil(() => vm.State.Name == "Renamed at the main");
            // …and a section that does not travel is refused without harm.
            Write(stream, TwinMessage.Format(TwinWord.Section, "{\"Role\":\"Main\"}", nameof(ShowState.Twin)));
            Write(stream, TwinMessage.Format(TwinWord.Air, JsonUtil.SerializeCompact(new RecoverySnapshot(false, false, DateTime.UtcNow, AirLabel: "Walk-in", AirLookId: vm.State.LooksAndCues.Looks[0].Id))));
            Write(stream, TwinMessage.Format(TwinWord.Beat, "7"));
            PumpUntil(() => twin.StatusJson().Contains("\"sectionsMirrored\":"));
            Assert.Equal(TwinRole.Standby, vm.State.Twin.Role);
            Assert.Contains("\"role\":\"standby\"", twin.StatusJson());
            Assert.Contains("\"main\":\"MAIN-DESK\"", twin.StatusJson());

            // Before anything went quiet TAKE OVER is the operator's to press — and the wire's.
            Assert.Contains("\"phase\":\"inStep\"", TestApp.Pump(new CommandRouter(services).ExecuteAsync(ControlProtocol.Parse("TWIN STATUS"))));

            // The main dies: the link drops, the silence is counted from the last beat, nothing is taken by itself.
            peer.Close();
            PumpUntil(() => twin.Phase == TwinPhase.MainSilent);
            clockOffset = TimeSpan.FromSeconds(8);
            twin.Tick();
            Assert.Equal(TwinPhase.MainSilent, twin.Phase);
            Assert.StartsWith("MAIN MAIN-DESK SILENT for", twin.Status);
            Assert.Contains("TAKE OVER?", twin.Status);
            Assert.StartsWith("MAIN MAIN-DESK SILENT", twin.HealthWords);

            // TAKE OVER: the hold lifts, the air record goes back on, the desk says so.
            var took = services.Actions.Execute(ShowActionKind.TwinTakeOver, ActionOrigin.Desk);
            Assert.True(took.Ok, took.Message);
            Assert.Equal(TwinPhase.TookOver, twin.Phase);
            Assert.Equal("", services.OutputsHeldBy);
            Assert.StartsWith("TOOK OVER from MAIN-DESK at", took.Message);
            Assert.Equal("Walk-in", services.AirLabel);                          // what the desk calls the picture came with it
            Assert.Contains("TOOK OVER from MAIN-DESK", twin.Status);
            Assert.Equal(TwinPhase.TookOver, twin.Phase);
            Assert.True(services.Actions.Execute(ShowActionKind.TwinTakeOver, ActionOrigin.Desk).Ok); // twice is not an error
            Assert.Contains("\"phase\":\"tookOver\"", twin.StatusJson());

            // STAND BY AGAIN: held once more, dialling once more — the fake main accepts the new join.
            var again = services.Actions.Execute(ShowActionKind.TwinStandBy, ActionOrigin.Desk);
            Assert.True(again.Ok, again.Message);
            Assert.Equal(TwinPhase.Connecting, twin.Phase);
            Assert.Equal("this desk is the standby twin", services.OutputsHeldBy);
            var (peer2, reader2, join2) = AcceptJoin(main);
            using var _peer2 = peer2;
            using var _reader2 = reader2;
            Assert.Equal("", join2.Key);

            // Off: the hold lifts and the outputs are this desk's again.
            vm.State.Twin.Role = TwinRole.Off;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(TwinPhase.Off, twin.Phase);
            Assert.Equal("", services.OutputsHeldBy);
        }
        finally
        {
            main.Stop();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AStandbyToldItMayTakesTheShowByItselfAndAMainThatSaysGoodbyeIsNotTakenFrom()
    {
        var b = TestApp.Boot();
        using var main = new TcpListener(IPAddress.Loopback, 0);
        main.Start();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            var twin = services.Twin;
            var clockOffset = TimeSpan.Zero;
            twin.Clock = () => DateTime.UtcNow + clockOffset;
            vm.State.Twin.Port = ((IPEndPoint)main.LocalEndpoint).Port;
            vm.State.Twin.MainHost = "127.0.0.1";
            vm.State.Twin.Key = "hunter2";
            vm.State.Twin.AutoTakeOver = true;
            vm.State.Twin.Role = TwinRole.Standby;
            Dispatcher.UIThread.RunJobs();

            var (peer, reader, _) = AcceptJoin(main);
            using (peer)
            using (reader)
            {
                var stream = peer.GetStream();
                Write(stream, TwinMessage.Format(TwinWord.Welcome, new TwinWelcome("MAIN-DESK", Environment.MachineName, "ef01", 0, 0, "", "Gala").ToJson()));   // this machine: by itself is allowed
                Write(stream, TwinMessage.Format(TwinWord.Show, TwinSync.ShowJson(new ShowState { Name = "Gala" })));
                Write(stream, TwinMessage.Format(TwinWord.Air, "null"));
                PumpUntil(() => twin.Phase == TwinPhase.InStep);
                Assert.EndsWith("takes over on silence.", twin.Status);

                // A goodbye is a main leaving on purpose: the standby dials again and takes nothing.
                Write(stream, TwinMessage.Format(TwinWord.Bye));
                PumpUntil(() => twin.Phase == TwinPhase.Connecting);
                clockOffset = TimeSpan.FromSeconds(30);
                twin.Tick();
                Assert.NotEqual(TwinPhase.TookOver, twin.Phase);
            }

            // Joined again, then the main dies: five silent seconds and the standby takes the show by itself.
            var (peer2, reader2, _) = AcceptJoin(main);
            using (peer2)
            using (reader2)
            {
                var stream2 = peer2.GetStream();
                Write(stream2, TwinMessage.Format(TwinWord.Welcome, new TwinWelcome("MAIN-DESK", Environment.MachineName, "ef01", 0, 0, "", "Gala").ToJson()));
                Write(stream2, TwinMessage.Format(TwinWord.Show, TwinSync.ShowJson(new ShowState { Name = "Gala again" })));
                Write(stream2, TwinMessage.Format(TwinWord.Air, "null"));
                PumpUntil(() => twin.Phase == TwinPhase.InStep && vm.State.Name == "Gala again");
            }
            PumpUntil(() => twin.Phase == TwinPhase.MainSilent);
            clockOffset = TimeSpan.FromSeconds(40);
            twin.Tick();
            Assert.Equal(TwinPhase.TookOver, twin.Phase);
            Assert.Equal("", services.OutputsHeldBy);
            Assert.Contains("TOOK OVER from MAIN-DESK", vm.StatusMessage);
            Assert.Contains("Nothing was on air at the main", vm.StatusMessage);
        }
        finally
        {
            main.Stop();
            b.Dispose();
        }
    }

    private sealed class TicksProbe : IProcessProbe
    {
        public Dictionary<int, long> Alive { get; } = new();
        public HashSet<int> Unreadable { get; } = new();     // up, and not this process's to read
        public bool KillFails { get; set; }
        public int Kills { get; private set; }
        public long? StartTicks(int pid) => Alive.TryGetValue(pid, out var ticks) ? ticks : null;
        public string ExePath(int pid) => "";
        public ProcessSight Look(int pid)
            => Unreadable.Contains(pid) ? ProcessSight.Unreadable()
                : Alive.TryGetValue(pid, out var ticks) ? ProcessSight.Alive(ticks)
                : ProcessSight.Gone;
        public bool Kill(int pid)
        {
            Kills++;
            return !KillFails && Alive.Remove(pid);
        }
    }

    /// <summary>A fake main's welcome: on this very machine when the pid is given, elsewhere otherwise.</summary>
    private static string WelcomeFrom(string machine, int pid = 0, long startedTicks = 0)
        => new TwinWelcome("MAIN-DESK", machine, "ef01", pid, startedTicks, pid > 0 ? Environment.ProcessPath ?? "Patterns.exe" : "", "From the main").ToJson();

    /// <summary>The fake main hands over a show with one look and that look on air.</summary>
    private static void HandOverTheShow(NetworkStream stream, string welcome)
    {
        var theirs = new ShowState { Name = "From the main" };
        theirs.LooksAndCues.Looks.Add(new LookConfig { Name = "Walk-in" });
        Write(stream, TwinMessage.Format(TwinWord.Welcome, welcome));
        Write(stream, TwinMessage.Format(TwinWord.Show, TwinSync.ShowJson(theirs)));
        Write(stream, TwinMessage.Format(TwinWord.Air, JsonUtil.SerializeCompact(new RecoverySnapshot(false, false, DateTime.UtcNow, AirLabel: "Walk-in", AirLookId: theirs.LooksAndCues.Looks[0].Id))));
    }

    private static RunCueConfig CallerCue(ShowState state, string name, ShowActionKind kind, string value)
    {
        var cue = new RunCueConfig { Name = name };
        cue.Actions.Add(new CueActionConfig { Kind = kind, Value = value });
        CueStacks.Caller(state).Cues.Add(cue);
        return cue;
    }

    /// <summary>The next join that says the standby has the show; a dial from before the takeover still in the backlog is closed.</summary>
    private static (TcpClient Peer, StreamReader Reader, TwinJoin Join) AcceptTookOverJoin(TcpListener main)
    {
        var deadline = Environment.TickCount64 + 15000;
        while (Environment.TickCount64 < deadline)
        {
            var (peer, reader, join) = AcceptJoin(main);
            if (join.TookOver) return (peer, reader, join);
            reader.Dispose();
            peer.Dispose();
        }
        throw new TimeoutException("no standby that has the show joined");
    }

    [AvaloniaFact]
    public void TheMainHoldsItsOutputsForAStandbyThatHasTheShowAndTakesItBack()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            var twin = services.Twin;
            vm.IsSandboxActive = false;
            vm.State.Name = "Gala";
            vm.State.Twin.Port = FreePort();
            vm.State.Twin.Key = "hunter2";
            vm.State.Twin.TakeBackCue = "Wall to main";
            CallerCue(vm.State, "Wall to main", ShowActionKind.MessageOn, "Wall: main");
            vm.State.Twin.Role = TwinRole.Main;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(TwinPhase.Listening, twin.Phase);
            var nothing = services.Actions.Execute(ShowActionKind.TwinTakeBack, ActionOrigin.Desk);
            Assert.False(nothing.Ok);
            Assert.Contains("nothing to take back", nothing.Message);

            // A standby that ran the show while this desk was away joins and says so: it is welcomed,
            // nothing is mirrored to it (its show is the newer one), and this desk's outputs are held.
            using var client = new TcpClient();
            TestApp.Pump(client.ConnectAsync(IPAddress.Loopback, vm.State.Twin.Port).ContinueWith(_ => true));
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 1 << 16, leaveOpen: true);
            Join(stream, reader, new TwinJoin("Backup desk", "BACKUP-PC", "abcd1234", "hunter2", TookOver: true), "hunter2");
            Assert.NotNull(TwinWelcome.Parse(ReadWord(reader, TwinWord.Welcome).Payload));
            Assert.Equal(TwinWord.Beat, TwinMessage.Parse(ReadLine(reader)).Word);   // no SHOW, no AIR
            PumpUntil(() => services.OutputsHeldBy.Length > 0);
            Assert.StartsWith("the standby twin Backup desk has the show", services.OutputsHeldBy);
            Assert.Equal("Backup desk", twin.Holder);
            var on = services.Actions.Execute(ShowActionKind.OutputsOn, ActionOrigin.Desk);
            Assert.False(on.Ok);
            Assert.Contains("held closed", on.Message);
            PumpUntil(() => twin.StandbyNames.Count == 1);
            Assert.StartsWith("MAIN — the standby Backup desk HAS THE SHOW; this desk's outputs are held closed. TAKE BACK puts the show back here.", twin.Status);
            Assert.Equal(twin.Status, twin.HealthWords);
            Assert.Contains("\"holder\":\"Backup desk\"", twin.StatusJson());
            Assert.Contains("has the show", vm.StatusMessage);

            // What it has — its show, edited while it ran, and its air — is kept here, not applied.
            var theirs = new ShowState { Name = "Edited at the standby" };
            theirs.LooksAndCues.Looks.Add(new LookConfig { Name = "Standby look" });
            CallerCue(theirs, "Wall to main", ShowActionKind.MessageOn, "Wall: main");     // the show travels; the cue is in it on both machines
            Write(stream, TwinMessage.Format(TwinWord.Show, TwinSync.ShowJson(theirs)));
            Write(stream, TwinMessage.Format(TwinWord.Air, JsonUtil.SerializeCompact(new RecoverySnapshot(false, false, DateTime.UtcNow, AirLabel: "Standby look", AirLookId: theirs.LooksAndCues.Looks[0].Id))));
            PumpUntil(() => twin.HeldLines >= 2);
            Assert.Equal("Gala", vm.State.Name);

            // TAKE BACK: its show lands here, its air goes on here, the hold lifts, the cue fires — and the
            // cue sends nothing a box confirms, so nothing says the room moved: the standby stays up on
            // its own input until the operator has switched the room and pressed again.
            var back = services.Actions.Execute(ShowActionKind.TwinTakeBack, ActionOrigin.Desk);
            Assert.Equal(ActionStatus.Requested, back.Status);
            Assert.StartsWith("TAKE BACK: the show is on here, on displays the room is not yet looking at. The take-back cue 'Wall to main' fired, but it sends nothing a box confirms — switch the room to this desk by hand, then TAKE BACK again releases the standby Backup desk", back.Message);
            Assert.Equal("Wall: main", vm.State.Overlays.Message.Text);            // the cue did fire
            Assert.Equal("Edited at the standby", vm.State.Name);                  // its show landed here
            Assert.Equal("", services.OutputsHeldBy);                             // the picture is up here
            Assert.Equal("Standby look", services.AirLabel);
            Assert.Equal("Backup desk", twin.Holder);                             // and it is still the holder
            Assert.False(Arrives(client, reader, TwinWord.HandBack, 800));        // not told to let go
            Assert.StartsWith("MAIN — TAKE BACK: the show is on here", twin.Status);
            Assert.Contains("\"awaiting\":\"the operator", twin.StatusJson());
            Assert.True(twin.LastHandover!.Stopped);

            // The room switched by hand, the second press: told to let go, and released once it says it has — then the whole show goes back over the link.
            var again = services.Actions.Execute(ShowActionKind.TwinTakeBack, ActionOrigin.Desk);
            Assert.Equal(ActionStatus.Requested, again.Status);
            Assert.Contains("the standby Backup desk was told to let go and its answer is awaited", again.Message);
            Assert.Equal("", twin.Holder);
            var id = AnswerHandBack(stream, reader);
            PumpUntil(() => twin.LastHandover is { IsComplete: true });
            Assert.Equal(id, twin.LastHandover!.Id);
            Assert.Contains("TOOK BACK from Backup desk at", vm.StatusMessage);
            Assert.Contains("Switched by hand.", vm.StatusMessage);
            Assert.Equal("take back across machines: target ready → route requested (stopped: the route is the operator's own → resumed: switched by hand — the operator's word) → route confirmed → authority committed (Backup desk said it let go) → old owner released → complete", twin.LastHandover.Trail);
            var show = JsonUtil.Deserialize<ShowState>(ReadWord(reader, TwinWord.Show).Payload);
            Assert.Equal("Edited at the standby", show!.Name);
            Assert.Equal(TwinWord.Air, ReadWord(reader, TwinWord.Air).Word);   // the air as it stands here (nothing is live headless: "null")
            Write(stream, TwinMessage.Format(TwinWord.Beat, "1"));
            PumpUntil(() => twin.Status.Contains("standby Backup desk in step"));
            Assert.Equal("", twin.HealthWords);

            vm.State.Twin.Role = TwinRole.Off;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(TwinPhase.Off, twin.Phase);
        }
        finally
        {
            b.Dispose();
        }
    }

    /// <summary>A standby that ran the show joins the main from a machine and says so; the main holds its outputs for it.</summary>
    /// <summary>The fake standby reads HANDBACK — which names the hand-back — and answers RELEASED for it, as a real one does after closing its outputs and clearing its marker.</summary>
    private static string AnswerHandBack(NetworkStream stream, StreamReader reader, int timeoutMs = 10000)
    {
        var handBack = ReadWord(reader, TwinWord.HandBack, timeoutMs);
        Assert.Equal(12, handBack.Payload.Length);
        Write(stream, TwinMessage.Format(TwinWord.Released, handBack.Payload));
        return handBack.Payload;
    }

    private static (TcpClient Client, StreamReader Reader) JoinAsHolder(TestApp.Booted b, string machine, bool withAir, Action<ShowState>? also = null, string handover = "")
    {
        var vm = b.Vm;
        var services = b.Services;
        var twin = services.Twin;
        var client = new TcpClient();
        TestApp.Pump(client.ConnectAsync(IPAddress.Loopback, vm.State.Twin.Port).ContinueWith(_ => true));
        var stream = client.GetStream();
        var reader = new StreamReader(stream, Encoding.UTF8, false, 1 << 16, leaveOpen: true);
        Join(stream, reader, new TwinJoin("Backup desk", machine, "abcd1234", "hunter2", TookOver: true, Handover: handover), "hunter2");
        Assert.NotNull(TwinWelcome.Parse(ReadWord(reader, TwinWord.Welcome).Payload));
        PumpUntil(() => services.OutputsHeldBy.Length > 0);
        Assert.Equal("Backup desk", twin.Holder);
        var theirs = new ShowState { Name = "Edited at the standby" };
        theirs.LooksAndCues.Looks.Add(new LookConfig { Name = "Standby look" });
        also?.Invoke(theirs);                                                   // the show travels: a cue and a box the take-back needs are in it on both machines
        Write(stream, TwinMessage.Format(TwinWord.Show, TwinSync.ShowJson(theirs)));
        if (withAir) Write(stream, TwinMessage.Format(TwinWord.Air, JsonUtil.SerializeCompact(new RecoverySnapshot(false, false, DateTime.UtcNow, AirLabel: "Standby look", AirLookId: theirs.LooksAndCues.Looks[0].Id))));
        PumpUntil(() => twin.HeldLines >= (withAir ? 2 : 1));
        return (client, reader);
    }

    [AvaloniaFact]
    public void ATakeBackWhoseWallSwitchCannotFireKeepsTheStandbyUpAndTheNextPressFinishesIt()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            var twin = services.Twin;
            vm.IsSandboxActive = false;
            vm.State.Name = "Gala";
            vm.State.Twin.Port = FreePort();
            vm.State.Twin.Key = "hunter2";
            vm.State.Twin.TakeBackCue = "Wall to main";      // named, but no such cue yet: the switcher cannot be told
            vm.State.Twin.Role = TwinRole.Main;
            Dispatcher.UIThread.RunJobs();
            var (client, reader) = JoinAsHolder(b, "BACKUP-PC", withAir: true);
            using var _client = client;
            using var _reader = reader;

            // TAKE BACK across machines: the show lands and the picture goes up here first, the room is
            // asked to look here — and the switch cannot be told. The standby is NOT released: the room
            // still shows it, and its picture stays up. Said, failed, and the next press finishes it.
            var stopped = services.Actions.Execute(ShowActionKind.TwinTakeBack, ActionOrigin.Desk);
            Assert.False(stopped.Ok);
            Assert.Contains("TAKE BACK stopped at the wall switch: cue 'Wall to main' could not fire", stopped.Message);
            Assert.Contains("The room still shows the standby Backup desk, whose picture stays up", stopped.Message);
            Assert.Contains("then TAKE BACK again", stopped.Message);
            Assert.Equal("Edited at the standby", vm.State.Name);          // the show landed here
            Assert.Equal("Standby look", services.AirLabel);              // and its air went on here: target ready
            Assert.Equal("", services.OutputsHeldBy);
            Assert.Equal("Backup desk", twin.Holder);                     // still the holder
            Assert.False(Arrives(client, reader, TwinWord.HandBack, 1500));   // no HANDBACK went
            Assert.StartsWith("MAIN — TAKE BACK stopped at the wall switch", twin.Status);
            Assert.Equal(twin.Status, twin.HealthWords);
            var tx = twin.LastHandover;
            Assert.NotNull(tx);
            Assert.Equal(HandoverKind.TakeBack, tx!.Kind);
            Assert.Equal(HandoverShape.AcrossMachines, tx.Shape);
            Assert.True(tx.Stopped);
            Assert.Equal(HandoverStage.RouteRequested, tx.Stage);
            Assert.Equal("take back across machines: target ready → route requested → stopped: could not fire (No cue 'Wall to main'.)", tx.Trail);
            Assert.Contains("\"stage\":\"route requested\"", twin.StatusJson());
            Assert.Contains("\"stopped\":\"could not fire", twin.StatusJson());
            Assert.Contains("TAKE BACK stopped", vm.StatusMessage);

            // The cue exists now (the operator built it, or the switcher is back): TAKE BACK again
            // fires it — and it sends nothing a box confirms, so the room is the operator's to switch:
            // said, and the press after that commits and releases the standby — HANDBACK, its answer, then the show.
            CallerCue(vm.State, "Wall to main", ShowActionKind.MessageOn, "Wall: main");
            Dispatcher.UIThread.RunJobs();
            var fired = services.Actions.Execute(ShowActionKind.TwinTakeBack, ActionOrigin.Desk);
            Assert.Equal(ActionStatus.Requested, fired.Status);
            Assert.Contains("The take-back cue 'Wall to main' fired, but it sends nothing a box confirms", fired.Message);
            Assert.Equal("Wall: main", vm.State.Overlays.Message.Text);
            Assert.Equal("Backup desk", twin.Holder);
            Assert.False(Arrives(client, reader, TwinWord.HandBack, 800));
            var done = services.Actions.Execute(ShowActionKind.TwinTakeBack, ActionOrigin.Desk);
            Assert.Equal(ActionStatus.Requested, done.Status);
            Assert.Equal("", twin.Holder);
            AnswerHandBack(stream: client.GetStream(), reader);
            PumpUntil(() => twin.LastHandover is { IsComplete: true });
            Assert.Contains("TOOK BACK from Backup desk at", vm.StatusMessage);
            Assert.DoesNotContain("its show landed here", vm.StatusMessage);   // landed on the first press, not twice
            Assert.Equal(TwinWord.Show, ReadWord(reader, TwinWord.Show).Word);
            Assert.False(twin.Status.Contains("stopped", StringComparison.Ordinal));
            Assert.Equal("take back across machines: target ready → route requested (stopped: the route is the operator's own → resumed: switched by hand — the operator's word) → route confirmed → authority committed (Backup desk said it let go) → old owner released → complete", twin.LastHandover!.Trail);
            Assert.Contains("\"complete\":true", twin.StatusJson());
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ATakeBackThroughASwitcherReleasesTheStandbyOnlyWhenTheSwitcherSaysYesAndStopsWhenItSaysNo()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            var twin = services.Twin;
            vm.IsSandboxActive = false;
            vm.State.Twin.Port = FreePort();
            vm.State.Twin.Key = "hunter2";
            vm.State.Twin.TakeBackCue = "Wall to main";
            vm.State.Twin.Role = TwinRole.Main;
            // The wall switch is a projector input over PJLink — a box that answers. The box and the
            // cue are in the show, so the standby's show carries them too and the take-back finds them.
            var fake = new FakeDeviceLink();
            services.Devices.LinkFactory = _ => fake;
            static void Rig(ShowState s)
            {
                s.Interactive.Enabled = true;
                s.Interactive.Devices.Add(new DeviceConfig { Id = "proj", Name = "Proj", Link = DeviceLink.Tcp, Profile = DeviceProfile.PjLink, Port = "10.0.0.7", Confirm = ConfirmLevel.Accepted, ConfirmTimeoutMs = 600, HearsShow = false });
                var cue = new RunCueConfig { Name = "Wall to main" };
                cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.DeviceSend, Target = "Proj", Value = "INPUT HDMI 1" });
                CueStacks.Caller(s).Cues.Add(cue);
            }
            Rig(vm.State);
            services.Devices.Reconcile();
            Dispatcher.UIThread.RunJobs();
            var (client, reader) = JoinAsHolder(b, "BACKUP-PC", withAir: false, also: Rig);
            using var _client = client;
            using var _reader = reader;

            // The switch is asked and the standby is not yet released: fired is dispatched, not done.
            var asked = services.Actions.Execute(ShowActionKind.TwinTakeBack, ActionOrigin.Desk);
            Assert.True(asked.Ok, asked.Message);
            Assert.Equal(ActionStatus.Requested, asked.Status);
            Assert.Contains("released once the switch answers", asked.Message);
            Assert.Contains("%1INPT 31\r", fake.Written);
            Assert.Equal("Backup desk", twin.Holder);
            Assert.False(services.Actions.Execute(ShowActionKind.TwinTakeBack, ActionOrigin.Desk).Ok);   // one at a time
            // The projector says no: stopped, the standby still up and the holder, the words say what finishes it.
            fake.Say("%1INPT=ERR3");
            PumpUntil(() => twin.LastHandover is { Stopped: true });
            Assert.False(Arrives(client, reader, TwinWord.HandBack, 800));
            Assert.Equal("Backup desk", twin.Holder);
            Assert.Contains("was not confirmed — Proj: INPUT HDMI 1 — rejected: INPT: unavailable now", twin.Status);
            Assert.Contains("TAKE BACK again", twin.Status);

            // Again: the projector says yes, and only then is the standby told to let go — and released once it says it has.
            var again = services.Actions.Execute(ShowActionKind.TwinTakeBack, ActionOrigin.Desk);
            Assert.Equal(ActionStatus.Requested, again.Status);
            fake.Say("%1INPT=OK");
            var id = AnswerHandBack(client.GetStream(), reader);
            PumpUntil(() => twin.LastHandover is { IsComplete: true });
            Assert.Equal(id, twin.LastHandover!.Id);
            Assert.Equal("", twin.Holder);
            Assert.Equal("take back across machines: target ready → route requested → route confirmed → authority committed (Backup desk said it let go) → old owner released → complete", twin.LastHandover.Trail);
            Assert.Contains("Wall switch confirmed: Proj: INPUT HDMI 1 — accepted (INPT: OK)", vm.StatusMessage);

        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AStandbyTakingOverByItselfWaitsForTheSwitcherAndRefusesWhenItIsSilentOrCannotAnswer()
    {
        var b = TestApp.Boot();
        using var main = new TcpListener(IPAddress.Loopback, 0);
        main.Start();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            var twin = services.Twin;
            var clockOffset = TimeSpan.Zero;
            twin.Clock = () => DateTime.UtcNow + clockOffset;
            vm.IsSandboxActive = false;
            vm.State.Twin.Port = ((IPEndPoint)main.LocalEndpoint).Port;
            vm.State.Twin.MainHost = "127.0.0.1";
            vm.State.Twin.Key = "hunter2";
            vm.State.Twin.AutoTakeOver = true;
            vm.State.Twin.TakeOverCue = "Wall to standby";
            var fake = new FakeDeviceLink();
            services.Devices.LinkFactory = _ => fake;
            vm.State.Twin.Role = TwinRole.Standby;
            Dispatcher.UIThread.RunJobs();
            var (peer, reader, _) = AcceptJoin(main);
            using var _peer = peer;
            using var _reader = reader;
            HandOverTheShow(peer.GetStream(), WelcomeFrom("SOME-OTHER-PC"));
            PumpUntil(() => twin.Phase == TwinPhase.InStep);
            // The main's show landed; the cue and its box go in after it. First a box that cannot
            // answer: an OSC datagram. Not a fence — the standby will not take over by itself on it.
            var lights = new DeviceConfig { Name = "Lights", Link = DeviceLink.Udp, Profile = DeviceProfile.Osc, Port = "127.0.0.1", NetPort = 1, HearsShow = false };
            vm.State.Interactive.Devices.Add(lights);
            vm.State.Interactive.Enabled = true;
            var cue = new RunCueConfig { Name = "Wall to standby" };
            cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.DeviceSend, Target = "Lights", Value = "/wall/standby 1" });
            CueStacks.Caller(vm.State).Cues.Add(cue);
            Dispatcher.UIThread.RunJobs();
            Assert.EndsWith("· TAKE OVER is yours.", twin.Status);
            peer.Close();
            PumpUntil(() => twin.Phase == TwinPhase.MainSilent);
            clockOffset = TimeSpan.FromSeconds(8);
            twin.Tick();
            Assert.Equal(TwinPhase.MainSilent, twin.Phase);
            Assert.Contains("the wall-switch cue's device 'Lights' is sent only", twin.Status);
            Assert.Contains("TAKE OVER is yours", twin.Status);

            // Now a box that answers, and is silent: asked, waited for, refused — the hold kept, the next try after the pause.
            cue.Actions[0].Target = "Proj";
            cue.Actions[0].Value = "INPUT HDMI 1";
            var proj = new DeviceConfig { Name = "Proj", Link = DeviceLink.Tcp, Profile = DeviceProfile.PjLink, Port = "10.0.0.7", Confirm = ConfirmLevel.Accepted, ConfirmTimeoutMs = 300, HearsShow = false };
            vm.State.Interactive.Devices.Add(proj);
            services.Devices.Reconcile();
            Dispatcher.UIThread.RunJobs();
            clockOffset = TimeSpan.FromSeconds(9);
            twin.Tick();
            PumpUntil(() => fake.Written.Contains("%1INPT 31\r") || fake.Written.Any(w => w.StartsWith("%1", StringComparison.Ordinal)));
            Assert.NotEqual(TwinPhase.TookOver, twin.Phase);                                   // asked, not taken: the answer is awaited
            PumpUntil(() => twin.Status.Contains("not taken over"), 3000);
            Assert.Contains("was not confirmed", twin.Status);
            Assert.Contains("no answer in 0.3 s", twin.Status);
            Assert.Equal("this desk is the standby twin", services.OutputsHeldBy);
            Assert.Null(TwinHandover.Read(services.Store.BaseDirectory));

            // The box answers this time: taken over, with the switch confirmed in the words.
            fake.Written.Clear();
            clockOffset = TimeSpan.FromSeconds(20);
            twin.Tick();
            PumpUntil(() => fake.Written.Any(w => w.StartsWith("%1", StringComparison.Ordinal)));
            fake.Say("%1INPT=OK");
            PumpUntil(() => twin.Phase == TwinPhase.TookOver);
            Assert.Equal("", services.OutputsHeldBy);
            Assert.Contains("Wall switch confirmed: Proj:", vm.StatusMessage);
            Assert.True(twin.LastHandover!.IsComplete);
            Assert.Equal("take over across machines: route requested → route confirmed → authority committed → target ready → complete", twin.LastHandover.Trail);
        }
        finally
        {
            main.Stop();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AStandbyProvesTheKeyOnlyToAMainThatProvedItFirstAndTheKeyIsNeverOnTheWire()
    {
        var b = TestApp.Boot();
        using var main = new TcpListener(IPAddress.Loopback, 0);
        main.Start();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            var twin = services.Twin;
            vm.State.Twin.Port = ((IPEndPoint)main.LocalEndpoint).Port;
            vm.State.Twin.MainHost = "127.0.0.1";
            vm.State.Twin.Key = "hunter2";
            vm.State.Twin.Role = TwinRole.Standby;
            Dispatcher.UIThread.RunJobs();

            // A stranger listening on the port: it reads a JOIN with no key in it, answers the nonce
            // with a proof it cannot make — and gets no proof back, no show landed, no hold taken.
            var accept = main.AcceptTcpClientAsync();
            PumpUntil(() => accept.IsCompleted);
            using var stranger = accept.Result;
            using var reader = new StreamReader(stranger.GetStream(), Encoding.UTF8, false, 4096, leaveOpen: true);
            var join = TwinJoin.Parse(ReadWord(reader, TwinWord.Join, 3000).Payload)!;
            Assert.Equal("", join.Key);
            Assert.Equal(48, join.Nonce.Length);
            Assert.Equal(TwinMessage.Proto, join.Proto);
            Write(stranger.GetStream(), TwinMessage.Format(TwinWord.Challenge, new TwinChallenge(TwinAuth.NewNonce(), TwinAuth.Proof("guess", join.Nonce, "x")).ToJson()));
            PumpUntil(() => twin.Phase == TwinPhase.Refused);
            Assert.Contains("did not prove the key", twin.Status);
            Assert.False(Arrives(stranger, reader, TwinWord.Proof, 800));
            Assert.Equal("this desk is the standby twin", services.OutputsHeldBy);
            Assert.NotEqual("From the main", vm.State.Name);
        }
        finally
        {
            main.Stop();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AStandbyWithNoKeySaysSoInItsOwnWordsAndProvesNothing()
    {
        var b = TestApp.Boot();
        using var main = new TcpListener(IPAddress.Loopback, 0);
        main.Start();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            var twin = services.Twin;
            vm.State.Twin.Port = ((IPEndPoint)main.LocalEndpoint).Port;
            vm.State.Twin.MainHost = "127.0.0.1";
            vm.State.Twin.Key = "";
            vm.State.Twin.Role = TwinRole.Standby;
            Dispatcher.UIThread.RunJobs();

            // The main's challenge arrives and this desk has nothing to prove with: the words are its
            // own — where the key goes — not a "wrong key" from the main; no PROOF leaves, the hold stays.
            var accept = main.AcceptTcpClientAsync();
            PumpUntil(() => accept.IsCompleted);
            using var client = accept.Result;
            using var reader = new StreamReader(client.GetStream(), Encoding.UTF8, false, 4096, leaveOpen: true);
            var join = TwinJoin.Parse(ReadWord(reader, TwinWord.Join, 3000).Payload)!;
            Assert.Equal("", join.Key);
            Write(client.GetStream(), TwinMessage.Format(TwinWord.Challenge, new TwinChallenge(TwinAuth.NewNonce(), TwinAuth.Proof("hunter2", join.Nonce, "x")).ToJson()));
            PumpUntil(() => twin.Phase == TwinPhase.Refused);
            Assert.Contains("this desk has no key", twin.Status);
            Assert.False(Arrives(client, reader, TwinWord.Proof, 800));
            Assert.Equal("this desk is the standby twin", services.OutputsHeldBy);
        }
        finally
        {
            main.Stop();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheWireCarriesNoMachineSectionsAndTheShowsCredentialsOnlyWhenTold()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            vm.IsSandboxActive = false;
            vm.State.Twin.Port = FreePort();
            vm.State.Twin.Key = "hunter2";
            vm.State.Twin.SendSecrets = false;
            vm.State.Install.AdminPasscode = "zq9876";                            // letters outside a-f: no hex id can carry it by chance
            vm.State.Weather.ApiKey = "wx-key";
            vm.State.Interactive.Devices.Add(new DeviceConfig { Id = "proj", Name = "Proj", Profile = DeviceProfile.PjLink, Link = DeviceLink.Tcp, Port = "10.0.0.7", Secret = "pjpass", Enabled = false });
            vm.State.Twin.Role = TwinRole.Main;
            Dispatcher.UIThread.RunJobs();

            using var client = new TcpClient();
            TestApp.Pump(client.ConnectAsync(IPAddress.Loopback, vm.State.Twin.Port).ContinueWith(_ => true));
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 1 << 16, leaveOpen: true);
            Assert.True(Join(stream, reader, new TwinJoin("Backup desk", "BACKUP-PC", "abcd1234", "hunter2"), "hunter2"), "the main proved the key first");
            Assert.NotNull(TwinWelcome.Parse(ReadWord(reader, TwinWord.Welcome).Payload));
            var show = ReadWord(reader, TwinWord.Show).Payload;
            // The secrets never travel. Each is a word a generated id cannot spell (cue and stack ids are hex),
            // so a bare substring check is safe; and the machine's own sections are absent as JSON properties,
            // not merely as text.
            Assert.DoesNotContain("hunter2", show);                                 // the key
            Assert.DoesNotContain("zq9876", show);                                  // the admin passcode
            using (var doc = System.Text.Json.JsonDocument.Parse(show))
                foreach (var local in TwinSync.LocalSections)                       // nor the sections they live in
                    Assert.False(doc.RootElement.TryGetProperty(local, out _), $"the wire carried the machine's own {local} section");
            Assert.DoesNotContain("pjpass", show);                                  // the credentials, not sent
            Assert.DoesNotContain("wx-key", show);
            Assert.Contains("\"Name\":\"Proj\"", show);                               // the box itself, yes
            Assert.Equal("pjpass", vm.State.Interactive.Devices[0].Secret);        // and the main keeps its own

            // Told to send them, the next edit's section carries them.
            services.BulkEdit(() => vm.State.Twin.SendSecrets = true);
            services.BulkEdit(() => vm.State.Interactive.Devices[0].Name = "Projector");
            var section = ReadWord(reader, TwinWord.Section);
            Assert.Equal("Interactive", section.Name);
            Assert.Contains("pjpass", section.Payload);
            Assert.Contains("\"Name\":\"Projector\"", section.Payload);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ATakeBackOnThisMachineReleasesTheStandbyFirstBecauseItsWindowsAreTheseDisplays()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            var twin = services.Twin;
            vm.IsSandboxActive = false;
            vm.State.Twin.Port = FreePort();
            vm.State.Twin.Key = "hunter2";
            vm.State.Twin.TakeBackCue = "Wall to main";      // set, and not a fence on one machine: fired after, never waited on
            vm.State.Twin.Role = TwinRole.Main;
            Dispatcher.UIThread.RunJobs();
            var (client, reader) = JoinAsHolder(b, Environment.MachineName, withAir: false);
            using var _client = client;
            using var _reader = reader;

            var done = services.Actions.Execute(ShowActionKind.TwinTakeBack, ActionOrigin.Desk);
            Assert.Equal(ActionStatus.Requested, done.Status);
            Assert.Contains("the picture goes up here the moment it says it has", done.Message);
            Assert.Equal("", twin.Holder);
            Assert.Contains("was told to let go", services.OutputsHeldBy);          // held until it says so: its windows are these displays
            var tx = twin.LastHandover!;
            Assert.Equal(HandoverShape.SameMachine, tx.Shape);
            Assert.Equal(HandoverStage.AuthorityCommitted, tx.Stage);
            Assert.Contains("\"awaiting\":\"the standby", twin.StatusJson());
            var id = AnswerHandBack(client.GetStream(), reader);
            PumpUntil(() => tx.IsComplete);
            Assert.Equal(id, tx.Id);
            Assert.Equal("", services.OutputsHeldBy);
            Assert.Contains("could not fire", vm.StatusMessage);           // the cue, fired after — said, in the words
            Assert.Equal("take back on this machine: authority committed (Backup desk said it let go) → old owner released → target ready → complete", tx.Trail);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AStandbyThatTookOverMarksItOnDiskKeepsDiallingAndFollowsAgainWhenTheMainTakesBack()
    {
        var b = TestApp.Boot();
        using var main = new TcpListener(IPAddress.Loopback, 0);
        main.Start();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            var twin = services.Twin;
            var clockOffset = TimeSpan.Zero;
            twin.Clock = () => DateTime.UtcNow + clockOffset;
            vm.State.Twin.Port = ((IPEndPoint)main.LocalEndpoint).Port;
            vm.State.Twin.MainHost = "127.0.0.1";
            vm.State.Twin.Key = "hunter2";
            vm.State.Twin.Role = TwinRole.Standby;
            Dispatcher.UIThread.RunJobs();

            var theirs = new ShowState { Name = "From the main" };
            theirs.LooksAndCues.Looks.Add(new LookConfig { Name = "Walk-in" });
            var (peer, reader, join) = AcceptJoin(main);
            Assert.False(join.TookOver);
            using (peer)
            using (reader)
            {
                var stream = peer.GetStream();
                Write(stream, TwinMessage.Format(TwinWord.Welcome, new TwinWelcome("MAIN-DESK", "SOME-OTHER-PC", "ef01", 1, 0, "", "From the main").ToJson()));
                Write(stream, TwinMessage.Format(TwinWord.Show, TwinSync.ShowJson(theirs)));
                Write(stream, TwinMessage.Format(TwinWord.Air, JsonUtil.SerializeCompact(new RecoverySnapshot(false, false, DateTime.UtcNow, AirLabel: "Walk-in", AirLookId: theirs.LooksAndCues.Looks[0].Id))));
                PumpUntil(() => twin.Phase == TwinPhase.InStep);
                peer.Close();
            }
            PumpUntil(() => twin.Phase == TwinPhase.MainSilent);

            // TAKE OVER: marked in this desk's own folder — a main on this machine reads it before its first window.
            var took = services.Actions.Execute(ShowActionKind.TwinTakeOver, ActionOrigin.Desk);
            Assert.True(took.Ok, took.Message);
            var marker = TwinHandover.Read(services.Store.BaseDirectory);
            Assert.NotNull(marker);
            Assert.Equal(Environment.ProcessId, marker!.Pid);
            Assert.Equal("MAIN-DESK", marker.MainName);
            Assert.Equal(Environment.MachineName, marker.Machine);
            Assert.Equal(twin.LastHandover!.Id, marker.Handover);                  // the marker names the takeover

            // It keeps dialling; the join says it has the show; the main gets the show it has — edited while it ran — and its air.
            vm.State.Name = "Edited while it ran";
            services.WriteRunPlace();   // the caller's place moved while it ran: the air record it sends is this one
            var (peer2, reader2, join2) = AcceptTookOverJoin(main);
            using var _peer2 = peer2;
            using var _reader2 = reader2;
            Assert.True(join2.TookOver);
            Assert.Equal(marker.Handover, join2.Handover);                          // and so does the join
            var stream2 = peer2.GetStream();
            Write(stream2, TwinMessage.Format(TwinWord.Welcome, new TwinWelcome("MAIN-DESK", "SOME-OTHER-PC", "ef02", 1, 0, "", "From the main").ToJson()));
            var show = JsonUtil.Deserialize<ShowState>(ReadWord(reader2, TwinWord.Show).Payload);
            Assert.Equal("Edited while it ran", show!.Name);
            Assert.NotEqual("null", ReadWord(reader2, TwinWord.Air).Payload);
            PumpUntil(() => twin.Status.Contains("is back on the link"));
            Assert.Equal(TwinPhase.TookOver, twin.Phase);

            // Nothing the main sends lands while this desk has the show.
            Write(stream2, TwinMessage.Format(TwinWord.Section, "\"Renamed at the main\"", nameof(ShowState.Name)));
            Write(stream2, TwinMessage.Format(TwinWord.Beat, "1"));
            twin.Tick();
            Assert.Equal(TwinWord.Beat, ReadWord(reader2, TwinWord.Beat, 4000).Word);
            Assert.Equal("Edited while it ran", vm.State.Name);

            // HANDBACK, by name: the outputs close and are held again, the marker goes, RELEASED answers for that
            // name — and again for a hand-back told twice — and the show that follows puts this desk in step.
            Write(stream2, TwinMessage.Format(TwinWord.HandBack, "0123456789ab"));
            PumpUntil(() => twin.Phase == TwinPhase.Connecting);
            Assert.Equal("this desk is the standby twin", services.OutputsHeldBy);
            Assert.Null(TwinHandover.Read(services.Store.BaseDirectory));
            Assert.Contains("took the show back", vm.StatusMessage);
            Assert.Equal("0123456789ab", ReadWord(reader2, TwinWord.Released).Payload);
            Write(stream2, TwinMessage.Format(TwinWord.HandBack, "0123456789ab"));
            Assert.Equal("0123456789ab", ReadWord(reader2, TwinWord.Released).Payload);
            Write(stream2, TwinMessage.Format(TwinWord.Show, TwinSync.ShowJson(new ShowState { Name = "Back at the main" })));
            PumpUntil(() => twin.Phase == TwinPhase.InStep);
            Assert.Equal("Back at the main", vm.State.Name);
            Assert.False(services.Actions.Execute(ShowActionKind.OutputsOn, ActionOrigin.Desk).Ok);
        }
        finally
        {
            main.Stop();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AMainReadsTheMarkAStandbyOnThisMachineLeftAndHoldsItsOutputsWhileThatProcessLives()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            var twin = services.Twin;
            var probe = new TicksProbe();
            probe.Alive[4242] = 77;
            twin.Probe = probe;
            var clockOffset = TimeSpan.Zero;
            twin.Clock = () => DateTime.UtcNow + clockOffset;
            var home = TwinHandover.StandbyHome(services.Store.BaseDirectory);
            TwinHandover.Write(home, new TwinTookOverMarker("Backup desk", Environment.MachineName, 4242, 77, "", DateTime.UtcNow, "MAIN-DESK"));

            // Whatever the role: the poll reads the mark and this desk's outputs are held while that process lives.
            clockOffset = TimeSpan.FromSeconds(3);
            twin.Poll();
            Assert.Equal("Backup desk", twin.Holder);
            Assert.StartsWith("the standby twin Backup desk has the show", services.OutputsHeldBy);
            Assert.False(services.Actions.Execute(ShowActionKind.OutputsOn, ActionOrigin.Desk).Ok);
            Assert.Contains("the standby Backup desk has the show", twin.Status);
            Assert.Contains("has the show", twin.HealthWords);
            Assert.True(vm.StatusMessage.Contains("TAKE BACK"), vm.StatusMessage);

            // As the main: the same hold, and TAKE BACK waits for the standby to be on the link.
            vm.State.Twin.Port = FreePort();
            vm.State.Twin.Key = "hunter2";
            vm.State.Twin.Role = TwinRole.Main;
            Dispatcher.UIThread.RunJobs();
            Assert.StartsWith("MAIN — the standby Backup desk HAS THE SHOW", twin.Status);
            Assert.Contains("not on the link yet", twin.Status);
            var back = services.Actions.Execute(ShowActionKind.TwinTakeBack, ActionOrigin.Desk);
            Assert.False(back.Ok);
            Assert.Contains("not on the link", back.Message);

            // The process turns unreadable (a session this desk may not look into): the hold stays —
            // a fence, not an absence.
            probe.Unreadable.Add(4242);
            clockOffset = TimeSpan.FromSeconds(5);
            twin.Poll();
            Assert.Equal("Backup desk", twin.Holder);
            Assert.StartsWith("the standby twin Backup desk has the show", services.OutputsHeldBy);

            // The process ends: the hold lifts and the outputs are this desk's again.
            probe.Alive.Clear();
            probe.Unreadable.Clear();
            clockOffset = TimeSpan.FromSeconds(8);
            twin.Poll();
            Assert.Equal("", twin.Holder);
            Assert.Equal("", services.OutputsHeldBy);
            Assert.Contains("died with the show", vm.StatusMessage);
            Assert.StartsWith("MAIN — listening for a standby", twin.Status);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AMainAskedForALocalStandbyStartsTheProcessInTheTwinStandbyFolderAndEndsItWhenSwitchedOff()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            var twin = services.Twin;
            var spawned = new List<IReadOnlyList<string>>();
            var child = new FakeTwinChild { Pid = 4321 };
            twin.Launcher.Spawn = (_, args) =>
            {
                spawned.Add(args);
                return child;
            };
            twin.Launcher.FolderOwned = _ => false;
            vm.State.Twin.Port = FreePort();
            vm.State.Twin.Key = "hunter2";
            vm.State.Twin.LocalStandby = true;
            vm.State.Twin.Role = TwinRole.Main;
            Dispatcher.UIThread.RunJobs();
            twin.Tick();
            var args = Assert.Single(spawned).ToList();
            Assert.Equal(TwinHandover.StandbyHome(services.Store.BaseDirectory), args[args.IndexOf("--home") + 1]);
            Assert.Equal($"127.0.0.1:{vm.State.Twin.Port}", args[args.IndexOf("--standby-of") + 1]);
            Assert.Equal("hunter2", args[args.IndexOf("--key") + 1]);
            Assert.Contains("--no-watchdog", args);
            Assert.Contains("Standby process running (pid 4321).", twin.Status);
            Assert.Contains("\"launcher\":\"Standby process running (pid 4321).\"", twin.StatusJson());
            twin.Tick();
            Assert.Single(spawned);

            vm.State.Twin.LocalStandby = false;
            Dispatcher.UIThread.RunJobs();
            Assert.True(child.Killed);
            Assert.DoesNotContain("Standby process", twin.Status);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AMainWithNoKeyIsGivenOneAndAJoinWithoutItIsRefusedEvenWhenItClaimsTheShow()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            var twin = services.Twin;
            vm.State.Twin.Port = FreePort();
            vm.State.Twin.Key = "";
            vm.State.Twin.Role = TwinRole.Main;
            Dispatcher.UIThread.RunJobs();
            PumpUntil(() => twin.Phase == TwinPhase.Listening);
            var key = vm.State.Twin.Key;
            Assert.True(TwinKeys.LooksMade(key), key);
            Assert.Contains($"was given the key {key}", vm.StatusMessage);

            // No key, a wrong key, and a wrong key that claims the show: refused, and the outputs never held.
            foreach (var (theirs, tookOver) in new[] { ("", false), ("nope", false), ("nope", true) })
            {
                using var client = new TcpClient();
                TestApp.Pump(client.ConnectAsync(IPAddress.Loopback, vm.State.Twin.Port).ContinueWith(_ => true));
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 4096, leaveOpen: true);
                Join(stream, reader, new TwinJoin("Stranger", "SOME-PC", "ffff0000", theirs, TookOver: tookOver), theirs);
                var refused = ReadWord(reader, TwinWord.Refused);
                Assert.Equal("wrong key", refused.Payload);
                Assert.Equal("", services.OutputsHeldBy);
                Assert.Equal("", twin.Holder);
            }

            // The key it was given opens the link.
            using (var client = new TcpClient())
            {
                TestApp.Pump(client.ConnectAsync(IPAddress.Loopback, vm.State.Twin.Port).ContinueWith(_ => true));
                using var stream = client.GetStream();
                using var reader = new StreamReader(stream, Encoding.UTF8, false, 1 << 16, leaveOpen: true);
                Join(stream, reader, new TwinJoin("Backup desk", "BACKUP-PC", "abcd1234", key, TookOver: false), key);
                Assert.NotNull(TwinWelcome.Parse(ReadWord(reader, TwinWord.Welcome).Payload));
            }
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ATakeoverOnThisMachineIsRefusedWhileTheHungMainCannotBeEndedAndForcedByHandOnly()
    {
        var b = TestApp.Boot();
        using var main = new TcpListener(IPAddress.Loopback, 0);
        main.Start();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            var twin = services.Twin;
            var probe = new TicksProbe { KillFails = true };
            probe.Alive[4242] = 77;
            twin.Probe = probe;
            var clockOffset = TimeSpan.Zero;
            twin.Clock = () => DateTime.UtcNow + clockOffset;
            vm.State.Twin.Port = ((IPEndPoint)main.LocalEndpoint).Port;
            vm.State.Twin.MainHost = "127.0.0.1";
            vm.State.Twin.Key = "hunter2";
            vm.State.Twin.AutoTakeOver = true;
            vm.State.Twin.Role = TwinRole.Standby;
            Dispatcher.UIThread.RunJobs();

            // The main is on this very machine: its welcome names its process.
            var (peer, reader, _) = AcceptJoin(main);
            using var _peer = peer;
            using var _reader = reader;
            HandOverTheShow(peer.GetStream(), WelcomeFrom(Environment.MachineName, pid: 4242, startedTicks: 77));
            PumpUntil(() => twin.Phase == TwinPhase.InStep);

            // It hangs: the link drops, the silence is counted, and the takeover by itself is refused —
            // the process is still up and cannot be ended — with the hold kept and the marker not left behind.
            peer.Close();
            PumpUntil(() => twin.Phase == TwinPhase.MainSilent);
            clockOffset = TimeSpan.FromSeconds(8);
            twin.Tick();
            Assert.Equal(TwinPhase.MainSilent, twin.Phase);
            Assert.Equal(1, probe.Kills);
            Assert.Equal("this desk is the standby twin", services.OutputsHeldBy);
            Assert.Null(TwinHandover.Read(services.Store.BaseDirectory));
            Assert.Contains("not taken over: MAIN-DESK's process (pid 4242) is still up and could not be ended", twin.Status);
            Assert.Contains("TAKE OVER?", twin.Status);
            Assert.Contains("TAKE OVER ANYWAY", vm.StatusMessage);
            Assert.False(services.Actions.Execute(ShowActionKind.OutputsOn, ActionOrigin.Desk).Ok);

            // Not every second: the fence is tried again after ten.
            twin.Tick();
            Assert.Equal(1, probe.Kills);
            clockOffset = TimeSpan.FromSeconds(19);
            twin.Tick();
            Assert.Equal(2, probe.Kills);
            Assert.Equal(TwinPhase.MainSilent, twin.Phase);

            // A press is refused the same way; FORCE takes over anyway and says so.
            var pressed = services.Actions.Execute(ShowActionKind.TwinTakeOver, ActionOrigin.Desk);
            Assert.False(pressed.Ok);
            Assert.StartsWith("Not taken over: MAIN-DESK's process (pid 4242) is still up and could not be ended.", pressed.Message);
            Assert.Equal(3, probe.Kills);
            var forced = services.Actions.Execute(new ShowAction(ShowActionKind.TwinTakeOver, "", "force"), ActionOrigin.Desk);
            Assert.True(forced.Ok, forced.Message);
            Assert.Contains("could not be ended — taken over anyway", forced.Message);
            Assert.Equal(TwinPhase.TookOver, twin.Phase);
            Assert.StartsWith("OK", TestApp.Pump(new CommandRouter(services).ExecuteAsync(ControlProtocol.Parse("TWIN TAKEOVER FORCE"))));   // the wire's word for it
            Assert.Equal("", services.OutputsHeldBy);
            Assert.Equal("Walk-in", services.AirLabel);
            var marker = TwinHandover.Read(services.Store.BaseDirectory);
            Assert.NotNull(marker);
            Assert.Equal(Environment.ProcessId, marker!.Pid);
        }
        finally
        {
            main.Stop();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ATakeoverIsRefusedWhenItCannotBeMarkedOnDiskForAMainOnThisMachineButNotForOneElsewhere()
    {
        var b = TestApp.Boot();
        using var main = new TcpListener(IPAddress.Loopback, 0);
        main.Start();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            var twin = services.Twin;
            var probe = new TicksProbe();
            probe.Alive[4242] = 77;
            twin.Probe = probe;
            var clockOffset = TimeSpan.Zero;
            twin.Clock = () => DateTime.UtcNow + clockOffset;
            Directory.CreateDirectory(TwinHandover.PathFor(services.Store.BaseDirectory));   // a folder where the marker file would go: the write fails
            vm.State.Twin.Port = ((IPEndPoint)main.LocalEndpoint).Port;
            vm.State.Twin.MainHost = "127.0.0.1";
            vm.State.Twin.Key = "hunter2";
            vm.State.Twin.Role = TwinRole.Standby;
            Dispatcher.UIThread.RunJobs();

            // A main on another machine never reads the marker: taken over, the failure only logged.
            var (peer, reader, _) = AcceptJoin(main);
            HandOverTheShow(peer.GetStream(), WelcomeFrom("SOME-OTHER-PC"));
            PumpUntil(() => twin.Phase == TwinPhase.InStep);
            peer.Close();
            reader.Dispose();
            PumpUntil(() => twin.Phase == TwinPhase.MainSilent);
            var took = services.Actions.Execute(ShowActionKind.TwinTakeOver, ActionOrigin.Desk);
            Assert.True(took.Ok, took.Message);
            Assert.Equal(TwinPhase.TookOver, twin.Phase);
            Assert.Equal("", services.OutputsHeldBy);
            Assert.True(services.Actions.Execute(ShowActionKind.TwinStandBy, ActionOrigin.Desk).Ok);
            Assert.Equal(TwinPhase.Connecting, twin.Phase);

            // A main on this machine would read it: refused, with the reason, before anything is ended.
            var (peer2, reader2, _) = AcceptJoin(main);
            using var _peer2 = peer2;
            using var _reader2 = reader2;
            HandOverTheShow(peer2.GetStream(), WelcomeFrom(Environment.MachineName, pid: 4242, startedTicks: 77));
            PumpUntil(() => twin.Phase == TwinPhase.InStep);
            peer2.Close();
            PumpUntil(() => twin.Phase == TwinPhase.MainSilent);
            var refused = services.Actions.Execute(ShowActionKind.TwinTakeOver, ActionOrigin.Desk);
            Assert.False(refused.Ok);
            Assert.Contains("the takeover could not be marked on disk", refused.Message);
            Assert.Equal(0, probe.Kills);
            Assert.Equal(TwinPhase.MainSilent, twin.Phase);
            Assert.Equal("this desk is the standby twin", services.OutputsHeldBy);
            Assert.Contains(4242, probe.Alive.Keys);
        }
        finally
        {
            main.Stop();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AMainWhoseStandbyOnThisMachineDiesWithTheShowPutsTheShowBackOnItself()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            var twin = services.Twin;
            var probe = new TicksProbe();
            probe.Alive[4242] = 77;
            twin.Probe = probe;
            var clockOffset = TimeSpan.Zero;
            twin.Clock = () => DateTime.UtcNow + clockOffset;
            vm.State.Name = "Gala";
            vm.State.Twin.Port = FreePort();
            vm.State.Twin.Key = "hunter2";
            vm.State.Twin.Role = TwinRole.Main;
            Dispatcher.UIThread.RunJobs();
            var home = TwinHandover.StandbyHome(services.Store.BaseDirectory);
            TwinHandover.Write(home, new TwinTookOverMarker("Backup desk", Environment.MachineName, 4242, 77, "", DateTime.UtcNow, "MAIN-DESK"));
            clockOffset = TimeSpan.FromSeconds(3);
            twin.Poll();
            Assert.Equal("Backup desk", twin.Holder);

            // On the link too, with the show it edited and what it has on air.
            using var client = new TcpClient();
            TestApp.Pump(client.ConnectAsync(IPAddress.Loopback, vm.State.Twin.Port).ContinueWith(_ => true));
            var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 1 << 16, leaveOpen: true);
            Join(stream, reader, new TwinJoin("Backup desk", Environment.MachineName, "abcd1234", "hunter2", TookOver: true), "hunter2");
            Assert.NotNull(TwinWelcome.Parse(ReadWord(reader, TwinWord.Welcome).Payload));
            var theirs = new ShowState { Name = "Edited at the standby" };
            theirs.LooksAndCues.Looks.Add(new LookConfig { Name = "Standby look" });
            Write(stream, TwinMessage.Format(TwinWord.Show, TwinSync.ShowJson(theirs)));
            Write(stream, TwinMessage.Format(TwinWord.Air, JsonUtil.SerializeCompact(new RecoverySnapshot(false, false, DateTime.UtcNow, AirLabel: "Standby look", AirLookId: theirs.LooksAndCues.Looks[0].Id))));
            PumpUntil(() => twin.HeldLines >= 2);
            Assert.Equal("Gala", vm.State.Name);

            // It crashes: the link drops first — the marker's process still stands, so the hold and what it sent are kept…
            client.Close();
            PumpUntil(() => twin.StandbyNames.Count == 0);
            clockOffset = TimeSpan.FromSeconds(6);
            twin.Poll();
            Assert.Equal("Backup desk", twin.Holder);
            Assert.StartsWith("the standby twin Backup desk has the show", services.OutputsHeldBy);
            Assert.Equal("Gala", vm.State.Name);

            // …then the process is gone: its show lands here, its air goes back on here, the hold lifts, the marker goes.
            probe.Alive.Clear();
            clockOffset = TimeSpan.FromSeconds(9);
            twin.Poll();
            Assert.Equal("", twin.Holder);
            Assert.Equal("", services.OutputsHeldBy);
            Assert.Equal("Edited at the standby", vm.State.Name);
            Assert.Equal("Standby look", services.AirLabel);
            Assert.Contains("died with the show", vm.StatusMessage);
            Assert.Null(TwinHandover.Read(home));
            Assert.DoesNotContain("held closed", services.Actions.Execute(ShowActionKind.OutputsOn, ActionOrigin.Desk).Message);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AStandbyOnAnotherMachineTakesOverByItselfOnlyWithAWallSwitchCueAndFiresIt()
    {
        var b = TestApp.Boot();
        using var main = new TcpListener(IPAddress.Loopback, 0);
        main.Start();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            var twin = services.Twin;
            var clockOffset = TimeSpan.Zero;
            twin.Clock = () => DateTime.UtcNow + clockOffset;
            vm.IsSandboxActive = false;
            vm.State.Twin.Port = ((IPEndPoint)main.LocalEndpoint).Port;
            vm.State.Twin.MainHost = "127.0.0.1";
            vm.State.Twin.Key = "hunter2";
            vm.State.Twin.AutoTakeOver = true;
            vm.State.Twin.Role = TwinRole.Standby;
            Dispatcher.UIThread.RunJobs();
            var (peer, reader, _) = AcceptJoin(main);
            using var _peer = peer;
            using var _reader = reader;
            HandOverTheShow(peer.GetStream(), WelcomeFrom("SOME-OTHER-PC"));
            PumpUntil(() => twin.Phase == TwinPhase.InStep);
            Assert.EndsWith("· TAKE OVER is yours.", twin.Status);              // told it may, but from another machine without a wall switch it will not

            // The main goes silent: nothing is taken by itself, and the line says why.
            peer.Close();
            PumpUntil(() => twin.Phase == TwinPhase.MainSilent);
            clockOffset = TimeSpan.FromSeconds(8);
            twin.Tick();
            Assert.Equal(TwinPhase.MainSilent, twin.Phase);
            Assert.Equal("this desk is the standby twin", services.OutputsHeldBy);
            Assert.Contains("TAKE OVER? · no wall-switch cue for a main on another machine, so not by itself: TAKE OVER is yours", twin.Status);
            Assert.Contains("no wall-switch cue", vm.StatusMessage);

            // A wall switch cue that is words on the message overlay: not a fence — nothing outside this
            // machine answers it — so by itself the standby still does not take over, and says why.
            CallerCue(vm.State, "Wall to standby", ShowActionKind.MessageOn, "Wall: standby");
            vm.State.Twin.TakeOverCue = "Wall to standby";
            Dispatcher.UIThread.RunJobs();
            clockOffset = TimeSpan.FromSeconds(9);
            twin.Tick();
            Assert.Equal(TwinPhase.MainSilent, twin.Phase);
            Assert.Contains("the wall-switch cue 'Wall to standby' sends nothing to a box that answers", twin.Status);
            Assert.Contains("TAKE OVER is yours", twin.Status);
            Assert.False(vm.State.Overlays.Message.Enabled);
            Assert.Contains("\"takeOverCue\":\"Wall to standby\"", twin.StatusJson());

            // The operator's press goes ahead: the cue fires, the words say no box confirmed it, and the trail says by whose word the route stands.
            var pressed = services.Actions.Execute(ShowActionKind.TwinTakeOver, ActionOrigin.Desk);
            Assert.True(pressed.Ok, pressed.Message);
            Assert.Equal(TwinPhase.TookOver, twin.Phase);
            Assert.Equal("", services.OutputsHeldBy);
            Assert.Equal("Walk-in", services.AirLabel);
            Assert.Equal("Wall: standby", vm.State.Overlays.Message.Text);
            Assert.True(vm.State.Overlays.Message.Enabled);
            Assert.Contains("sent nothing a box confirms — switch the room to this desk by hand if it did not", pressed.Message);
            Assert.Equal("take over across machines: route requested → route confirmed (no box answered — the operator's press) → authority committed → target ready → complete", twin.LastHandover!.Trail);
        }
        finally
        {
            main.Stop();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ATakeoverOnThisMachineIsRefusedWhileTheHungMainCannotBeReadAndForcedByHandOnly()
    {
        var b = TestApp.Boot();
        using var main = new TcpListener(IPAddress.Loopback, 0);
        main.Start();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            var twin = services.Twin;
            var probe = new TicksProbe();
            probe.Unreadable.Add(4242);                       // up, and not this process's to read
            twin.Probe = probe;
            var clockOffset = TimeSpan.Zero;
            twin.Clock = () => DateTime.UtcNow + clockOffset;
            vm.State.Twin.Port = ((IPEndPoint)main.LocalEndpoint).Port;
            vm.State.Twin.MainHost = "127.0.0.1";
            vm.State.Twin.Key = "hunter2";
            vm.State.Twin.AutoTakeOver = true;
            vm.State.Twin.Role = TwinRole.Standby;
            Dispatcher.UIThread.RunJobs();
            var (peer, reader, _) = AcceptJoin(main);
            using var _peer = peer;
            using var _reader = reader;
            HandOverTheShow(peer.GetStream(), WelcomeFrom(Environment.MachineName, pid: 4242, startedTicks: 77));
            PumpUntil(() => twin.Phase == TwinPhase.InStep);

            // It hangs. The process cannot be read from here, so it cannot be ended — and "could not
            // see it" is never "it is gone": nothing is ended, nothing is taken, the hold is kept.
            peer.Close();
            PumpUntil(() => twin.Phase == TwinPhase.MainSilent);
            clockOffset = TimeSpan.FromSeconds(8);
            twin.Tick();
            Assert.Equal(TwinPhase.MainSilent, twin.Phase);
            Assert.Equal(0, probe.Kills);
            Assert.Equal("this desk is the standby twin", services.OutputsHeldBy);
            Assert.Null(TwinHandover.Read(services.Store.BaseDirectory));
            Assert.Contains("not taken over: MAIN-DESK's process (pid 4242) is still up but cannot be read from here", twin.Status);
            Assert.Contains("TAKE OVER ANYWAY", vm.StatusMessage);

            // A press is refused the same way; FORCE takes over anyway and says so.
            var pressed = services.Actions.Execute(ShowActionKind.TwinTakeOver, ActionOrigin.Desk);
            Assert.False(pressed.Ok);
            Assert.Contains("cannot be read from here", pressed.Message);
            Assert.Equal(0, probe.Kills);
            var forced = services.Actions.Execute(new ShowAction(ShowActionKind.TwinTakeOver, "", "force"), ActionOrigin.Desk);
            Assert.True(forced.Ok, forced.Message);
            Assert.Contains("cannot be ended — taken over anyway", forced.Message);
            Assert.Equal(TwinPhase.TookOver, twin.Phase);
            Assert.Equal("", services.OutputsHeldBy);
        }
        finally
        {
            main.Stop();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AStandbyOnAnotherMachineWhoseWallSwitchCueCannotFireDoesNotTakeOverByItselfUntilItCan()
    {
        var b = TestApp.Boot();
        using var main = new TcpListener(IPAddress.Loopback, 0);
        main.Start();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            var twin = services.Twin;
            var clockOffset = TimeSpan.Zero;
            twin.Clock = () => DateTime.UtcNow + clockOffset;
            vm.IsSandboxActive = false;
            vm.State.Twin.Port = ((IPEndPoint)main.LocalEndpoint).Port;
            vm.State.Twin.MainHost = "127.0.0.1";
            vm.State.Twin.Key = "hunter2";
            vm.State.Twin.AutoTakeOver = true;
            vm.State.Twin.TakeOverCue = "Wall to standby";    // named, but no such cue yet: the switcher cannot be told
            var fake = new FakeDeviceLink();
            services.Devices.LinkFactory = _ => fake;
            vm.State.Twin.Role = TwinRole.Standby;
            Dispatcher.UIThread.RunJobs();
            var (peer, reader, _) = AcceptJoin(main);
            using var _peer = peer;
            using var _reader = reader;
            HandOverTheShow(peer.GetStream(), WelcomeFrom("SOME-OTHER-PC"));
            PumpUntil(() => twin.Phase == TwinPhase.InStep);
            Assert.EndsWith("· TAKE OVER is yours.", twin.Status);

            // The main goes silent. The wall switch is the fence between two machines, and it fires
            // before a single output opens here: a cue that is not there is no fence, so nothing is
            // asked and nothing is taken — the outputs stay held, no marker is left, and the line says why.
            peer.Close();
            PumpUntil(() => twin.Phase == TwinPhase.MainSilent);
            clockOffset = TimeSpan.FromSeconds(8);
            twin.Tick();
            Assert.Equal(TwinPhase.MainSilent, twin.Phase);
            Assert.Equal("this desk is the standby twin", services.OutputsHeldBy);
            Assert.Null(TwinHandover.Read(services.Store.BaseDirectory));
            Assert.Contains("the wall-switch cue 'Wall to standby' is not in the show, so not by itself: TAKE OVER is yours", twin.Status);
            Assert.False(vm.State.Overlays.Message.Enabled);

            // The cue exists now, and sends to a box that answers: the switch is asked, and the takeover
            // goes ahead once the box says yes — the room is looking at this desk by then.
            var proj = new DeviceConfig { Name = "Proj", Link = DeviceLink.Tcp, Profile = DeviceProfile.PjLink, Port = "10.0.0.7", Confirm = ConfirmLevel.Accepted, ConfirmTimeoutMs = 600, HearsShow = false };
            vm.State.Interactive.Devices.Add(proj);
            vm.State.Interactive.Enabled = true;
            var cue = new RunCueConfig { Name = "Wall to standby" };
            cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.DeviceSend, Target = "Proj", Value = "INPUT HDMI 2" });
            CueStacks.Caller(vm.State).Cues.Add(cue);
            services.Devices.Reconcile();
            Dispatcher.UIThread.RunJobs();
            clockOffset = TimeSpan.FromSeconds(9);
            twin.Tick();
            PumpUntil(() => fake.Written.Any(w => w.StartsWith("%1INPT", StringComparison.Ordinal)));
            Assert.Equal(TwinPhase.MainSilent, twin.Phase);                     // asked, not yet taken
            fake.Say("%1INPT=OK");
            PumpUntil(() => twin.Phase == TwinPhase.TookOver);
            Assert.Equal("", services.OutputsHeldBy);
            Assert.Equal("Walk-in", services.AirLabel);
            Assert.NotNull(TwinHandover.Read(services.Store.BaseDirectory));
            Assert.Equal("take over across machines: route requested → route confirmed → authority committed → target ready → complete", twin.LastHandover!.Trail);
        }
        finally
        {
            main.Stop();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void APressTakesOverEvenWhenTheWallSwitchCueCannotFireAndSaysSo()
    {
        var b = TestApp.Boot();
        using var main = new TcpListener(IPAddress.Loopback, 0);
        main.Start();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            var twin = services.Twin;
            vm.IsSandboxActive = false;
            vm.State.Twin.Port = ((IPEndPoint)main.LocalEndpoint).Port;
            vm.State.Twin.MainHost = "127.0.0.1";
            vm.State.Twin.Key = "hunter2";
            vm.State.Twin.TakeOverCue = "Wall to standby";
            vm.State.Twin.Role = TwinRole.Standby;
            Dispatcher.UIThread.RunJobs();
            var (peer, reader, _) = AcceptJoin(main);
            using var _peer = peer;
            using var _reader = reader;
            HandOverTheShow(peer.GetStream(), WelcomeFrom("SOME-OTHER-PC"));
            PumpUntil(() => twin.Phase == TwinPhase.InStep);

            // The operator decides: the takeover goes ahead, and the words carry the failure so the
            // wall can be switched by hand.
            var pressed = services.Actions.Execute(ShowActionKind.TwinTakeOver, ActionOrigin.Desk);
            Assert.True(pressed.Ok, pressed.Message);
            Assert.StartsWith("TOOK OVER from MAIN-DESK", pressed.Message);
            Assert.Contains("Wall switch: cue 'Wall to standby' could not fire — No cue 'Wall to standby'.", pressed.Message);
            Assert.Equal(TwinPhase.TookOver, twin.Phase);
            Assert.Equal("", services.OutputsHeldBy);
            Assert.Equal("Walk-in", services.AirLabel);
            Assert.Equal("take over across machines: route requested → route confirmed (the cue could not fire — the operator's press) → authority committed → target ready → complete", twin.LastHandover!.Trail);
        }
        finally
        {
            main.Stop();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AHandBackNobodyAnsweredIsSaidTheOutputsStayHeldAndTheNextPressTellsItAgain()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            var twin = services.Twin;
            var clockOffset = TimeSpan.Zero;
            twin.Clock = () => DateTime.UtcNow + clockOffset;
            vm.IsSandboxActive = false;
            vm.State.Twin.Port = FreePort();
            vm.State.Twin.Key = "hunter2";
            vm.State.Twin.Role = TwinRole.Main;
            Dispatcher.UIThread.RunJobs();
            var (client, reader) = JoinAsHolder(b, Environment.MachineName, withAir: true);
            using var _client = client;
            using var _reader = reader;

            // Told to let go — and it says nothing. Its windows may still be up on these displays, so
            // nothing opens here: past the limit the wait is overdue, said, and the outputs stay held.
            var asked = services.Actions.Execute(ShowActionKind.TwinTakeBack, ActionOrigin.Desk);
            Assert.Equal(ActionStatus.Requested, asked.Status);
            var first = ReadWord(reader, TwinWord.HandBack);
            Assert.Equal(12, first.Payload.Length);
            Assert.False(services.Actions.Execute(ShowActionKind.TwinTakeBack, ActionOrigin.Desk).Ok);   // awaited: one at a time
            clockOffset = TimeSpan.FromSeconds(3);
            twin.Poll();
            Assert.False(twin.LastHandover!.Stopped);
            Assert.Contains("was told to let go", services.OutputsHeldBy);
            clockOffset = TimeSpan.FromSeconds(6);
            twin.Poll();
            Assert.True(twin.LastHandover.Stopped);
            Assert.Equal("the standby did not say it let go", twin.LastHandover.Reason);
            Assert.Contains("has not said it has — TAKE BACK again tells it again", services.OutputsHeldBy);
            Assert.Contains("TAKE BACK stopped: the standby Backup desk did not say it let go", twin.Status);
            Assert.Contains("overdue; TAKE BACK again tells it again", twin.StatusJson());
            Assert.Contains("did not say it let go", vm.StatusMessage);
            Assert.NotEqual("Standby look", services.AirLabel);                 // nothing went on air here
            Assert.False(services.Actions.Execute(ShowActionKind.OutputsOn, ActionOrigin.Desk).Ok);

            // TAKE BACK again: told again, by the same name — and this time it answers, so the picture goes up here.
            var again = services.Actions.Execute(ShowActionKind.TwinTakeBack, ActionOrigin.Desk);
            Assert.Equal(ActionStatus.Requested, again.Status);
            var second = ReadWord(reader, TwinWord.HandBack);
            Assert.Equal(first.Payload, second.Payload);
            Write(client.GetStream(), TwinMessage.Format(TwinWord.Released, second.Payload));
            PumpUntil(() => twin.LastHandover is { IsComplete: true });
            Assert.Equal("", services.OutputsHeldBy);
            Assert.Equal("Standby look", services.AirLabel);
            Assert.Equal("take back on this machine: authority committed (stopped: the standby did not say it let go → resumed: told again; Backup desk said it let go) → old owner released → target ready → complete", twin.LastHandover!.Trail);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AClaimMadeUnderATakeoverAlreadyTakenBackIsAnsweredWithTheHandBackNotAHold()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            var twin = services.Twin;
            vm.IsSandboxActive = false;
            vm.State.Twin.Port = FreePort();
            vm.State.Twin.Key = "hunter2";
            vm.State.Twin.Role = TwinRole.Main;
            Dispatcher.UIThread.RunJobs();
            var (client, reader) = JoinAsHolder(b, Environment.MachineName, withAir: true, handover: "aaaaaaaaaaaa");
            var asked = services.Actions.Execute(ShowActionKind.TwinTakeBack, ActionOrigin.Desk);
            Assert.Equal(ActionStatus.Requested, asked.Status);
            var handBack = ReadWord(reader, TwinWord.HandBack);

            // The line is lost: the standby never read the hand-back, and comes back claiming the show
            // under the takeover this desk just took back. Before, that claim closed this desk's live
            // outputs; now it is answered with the hand-back again, and a hold is never taken.
            reader.Dispose();
            client.Dispose();
            PumpUntil(() => twin.StandbyNames.Count == 0);
            Assert.Contains("was told to let go", services.OutputsHeldBy);
            using var back = new TcpClient();
            TestApp.Pump(back.ConnectAsync(IPAddress.Loopback, vm.State.Twin.Port).ContinueWith(_ => true));
            using var stream2 = back.GetStream();
            using var reader2 = new StreamReader(stream2, Encoding.UTF8, false, 1 << 16, leaveOpen: true);
            Join(stream2, reader2, new TwinJoin("Backup desk", Environment.MachineName, "abcd1234", "hunter2", TookOver: true, Handover: "aaaaaaaaaaaa"), "hunter2");
            Assert.NotNull(TwinWelcome.Parse(ReadWord(reader2, TwinWord.Welcome).Payload));
            var told = ReadWord(reader2, TwinWord.HandBack);                        // before the show: the claim answered
            Assert.Equal(handBack.Payload, told.Payload);
            Assert.Equal(TwinWord.Show, ReadWord(reader2, TwinWord.Show).Word);     // then mirrored, as a standby is
            Assert.Equal("", twin.Holder);
            Assert.DoesNotContain("has the show", services.OutputsHeldBy);
            Write(stream2, TwinMessage.Format(TwinWord.Released, told.Payload));
            PumpUntil(() => twin.LastHandover is { IsComplete: true });
            Assert.Equal("", services.OutputsHeldBy);
            Assert.Equal("Standby look", services.AirLabel);
            Assert.Contains("Backup desk said it let go", twin.LastHandover!.Trail);

            // A claim under a takeover this desk never took back — a later one — is a claim, and holds as before.
            Write(stream2, TwinMessage.Format(TwinWord.Bye));
            PumpUntil(() => twin.StandbyNames.Count == 0);
            using var later = new TcpClient();
            TestApp.Pump(later.ConnectAsync(IPAddress.Loopback, vm.State.Twin.Port).ContinueWith(_ => true));
            using var stream3 = later.GetStream();
            using var reader3 = new StreamReader(stream3, Encoding.UTF8, false, 1 << 16, leaveOpen: true);
            Join(stream3, reader3, new TwinJoin("Backup desk", Environment.MachineName, "abcd1234", "hunter2", TookOver: true, Handover: "bbbbbbbbbbbb"), "hunter2");
            Assert.NotNull(TwinWelcome.Parse(ReadWord(reader3, TwinWord.Welcome).Payload));
            PumpUntil(() => twin.Holder == "Backup desk");
            Assert.StartsWith("the standby twin Backup desk has the show", services.OutputsHeldBy);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AStandbyThatStandsByAgainOnAFreshLinkIsTheAnswerToTheHandBack()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            var twin = services.Twin;
            vm.IsSandboxActive = false;
            vm.State.Twin.Port = FreePort();
            vm.State.Twin.Key = "hunter2";
            vm.State.Twin.Role = TwinRole.Main;
            Dispatcher.UIThread.RunJobs();
            var (client, reader) = JoinAsHolder(b, Environment.MachineName, withAir: true, handover: "cccccccccccc");
            Assert.Equal(ActionStatus.Requested, services.Actions.Execute(ShowActionKind.TwinTakeBack, ActionOrigin.Desk).Status);
            Assert.Equal(12, ReadWord(reader, TwinWord.HandBack).Payload.Length);

            // It let go — closed its outputs, cleared its marker — and its RELEASED never reached this desk
            // (the link went as it wrote). It dials again as a standby, claiming nothing: that is the answer.
            reader.Dispose();
            client.Dispose();
            PumpUntil(() => twin.StandbyNames.Count == 0);
            Assert.Contains("was told to let go", services.OutputsHeldBy);
            using var back = new TcpClient();
            TestApp.Pump(back.ConnectAsync(IPAddress.Loopback, vm.State.Twin.Port).ContinueWith(_ => true));
            using var stream2 = back.GetStream();
            using var reader2 = new StreamReader(stream2, Encoding.UTF8, false, 1 << 16, leaveOpen: true);
            Join(stream2, reader2, new TwinJoin("Backup desk", Environment.MachineName, "abcd1234", "hunter2"), "hunter2");
            Assert.NotNull(TwinWelcome.Parse(ReadWord(reader2, TwinWord.Welcome).Payload));
            Assert.Equal(TwinWord.Show, ReadWord(reader2, TwinWord.Show).Word);
            PumpUntil(() => twin.LastHandover is { IsComplete: true });
            Assert.Equal("", services.OutputsHeldBy);
            Assert.Equal("Standby look", services.AirLabel);
            Assert.Equal("take back on this machine: authority committed (it stands by again) → old owner released → target ready → complete", twin.LastHandover!.Trail);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ARouteLessTakeBackAcrossMachinesIsTwoPressesWithTheRoomSwitchedBetweenThem()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            var twin = services.Twin;
            vm.IsSandboxActive = false;
            vm.State.Twin.Port = FreePort();
            vm.State.Twin.Key = "hunter2";
            vm.State.Twin.TakeBackCue = "";                                          // the room's switcher is nobody's box: the operator's hands
            vm.State.Twin.Role = TwinRole.Main;
            Dispatcher.UIThread.RunJobs();
            var (client, reader) = JoinAsHolder(b, "BACKUP-PC", withAir: true);
            using var _client = client;
            using var _reader = reader;

            // The first press: the show lands and goes up here, on displays the room is not looking at
            // — and nothing infers the switch was thrown. The standby stays up and the holder.
            var first = services.Actions.Execute(ShowActionKind.TwinTakeBack, ActionOrigin.Desk);
            Assert.Equal(ActionStatus.Requested, first.Status);
            Assert.StartsWith("TAKE BACK: the show is on here, on displays the room is not yet looking at. No take-back cue is set — switch the room to this desk by hand, then TAKE BACK again releases the standby Backup desk", first.Message);
            Assert.Equal("Edited at the standby", vm.State.Name);
            Assert.Equal("Standby look", services.AirLabel);
            Assert.Equal("", services.OutputsHeldBy);
            Assert.Equal("Backup desk", twin.Holder);
            Assert.False(Arrives(client, reader, TwinWord.HandBack, 800));
            Assert.True(twin.LastHandover!.Stopped);
            Assert.Equal(HandoverStage.TargetReady, twin.LastHandover.Stage);
            Assert.Contains("\"awaiting\":\"the operator", twin.StatusJson());
            Assert.StartsWith("MAIN — TAKE BACK: the show is on here", twin.Status);

            // The room switched by hand, the second press: the standby is told to let go and released once it says it has.
            var second = services.Actions.Execute(ShowActionKind.TwinTakeBack, ActionOrigin.Desk);
            Assert.Equal(ActionStatus.Requested, second.Status);
            Assert.Contains("the room shows this desk; the standby Backup desk was told to let go", second.Message);
            Assert.Equal("", twin.Holder);
            AnswerHandBack(client.GetStream(), reader);
            PumpUntil(() => twin.LastHandover is { IsComplete: true });
            Assert.Equal(TwinWord.Show, ReadWord(reader, TwinWord.Show).Word);
            Assert.Contains("Switched by hand.", vm.StatusMessage);
            Assert.Equal("take back across machines (no wall-switch cue): target ready (stopped: the route is the operator's own → resumed: switched by hand — the operator's word) → authority committed (Backup desk said it let go) → old owner released → complete", twin.LastHandover!.Trail);
            Assert.False(twin.Status.Contains("TAKE BACK", StringComparison.Ordinal));
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ABoxThatWasOnlyHeardDoesNotConfirmTheRoute()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            var twin = services.Twin;
            vm.IsSandboxActive = false;
            vm.State.Twin.Port = FreePort();
            vm.State.Twin.Key = "hunter2";
            vm.State.Twin.TakeBackCue = "Wall to main";
            vm.State.Twin.Role = TwinRole.Main;
            var fake = new FakeDeviceLink();
            services.Devices.LinkFactory = _ => fake;
            // The projector is set to confirm at Delivered: the socket taking the bytes is all it asks — heard, not obeyed.
            static void Rig(ShowState s)
            {
                s.Interactive.Enabled = true;
                s.Interactive.Devices.Add(new DeviceConfig { Id = "proj", Name = "Proj", Link = DeviceLink.Tcp, Profile = DeviceProfile.PjLink, Port = "10.0.0.7", Confirm = ConfirmLevel.Delivered, ConfirmTimeoutMs = 600, HearsShow = false });
                var cue = new RunCueConfig { Name = "Wall to main" };
                cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.DeviceSend, Target = "Proj", Value = "INPUT HDMI 1" });
                CueStacks.Caller(s).Cues.Add(cue);
            }
            Rig(vm.State);
            services.Devices.Reconcile();
            Dispatcher.UIThread.RunJobs();
            var (client, reader) = JoinAsHolder(b, "BACKUP-PC", withAir: false, also: Rig);
            using var _client = client;
            using var _reader = reader;

            var asked = services.Actions.Execute(ShowActionKind.TwinTakeBack, ActionOrigin.Desk);
            Assert.Equal(ActionStatus.Requested, asked.Status);
            PumpUntil(() => twin.LastHandover is { Stopped: true });
            Assert.Contains("was not confirmed — no box said yes", twin.Status);
            Assert.Contains("delivered is not switched", twin.Status);
            Assert.Equal("Backup desk", twin.Holder);
            Assert.False(Arrives(client, reader, TwinWord.HandBack, 800));

            // Set to Accepted, the projector's OK is a fact, and the standby is told to let go. (The
            // standby's show landed on the first press: the box on the page is a new object, and the
            // open link reads the page's current words, not the object it opened with.)
            vm.State.Interactive.Devices[0].Confirm = ConfirmLevel.Accepted;
            services.Devices.Reconcile();
            Dispatcher.UIThread.RunJobs();
            var again = services.Actions.Execute(ShowActionKind.TwinTakeBack, ActionOrigin.Desk);
            Assert.Equal(ActionStatus.Requested, again.Status);
            PumpUntil(() => fake.Written.Count(w => w.StartsWith("%1INPT 31", StringComparison.Ordinal)) >= 2);
            fake.Say("%1INPT=OK");
            AnswerHandBack(client.GetStream(), reader);
            PumpUntil(() => twin.LastHandover is { IsComplete: true });
            Assert.Equal("", twin.Holder);
            Assert.Contains("Wall switch confirmed: Proj: INPUT HDMI 1 — accepted (INPT: OK)", vm.StatusMessage);
        }
        finally
        {
            b.Dispose();
        }
    }
}
