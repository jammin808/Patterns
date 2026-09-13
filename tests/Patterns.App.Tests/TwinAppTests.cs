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
                if (join is not null) return (peer, reader, join);
            }
            catch (IOException)
            {
                // closed before it said anything: a dial cut short
            }
            catch (TimeoutException)
            {
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
                Write(strangerStream, TwinMessage.Format(TwinWord.Join, new TwinJoin("Stranger", "OTHER-PC", "zzzz", "wrong").ToJson()));
                var refused = TwinMessage.Parse(ReadLine(strangerReader));
                Assert.Equal(TwinWord.Refused, refused.Word);
                Assert.Equal("wrong key", refused.Payload);
            }

            using var client = new TcpClient();
            TestApp.Pump(client.ConnectAsync(IPAddress.Loopback, vm.State.Twin.Port).ContinueWith(_ => true));
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8, false, 1 << 16, leaveOpen: true);
            Write(stream, TwinMessage.Format(TwinWord.Join, new TwinJoin("Backup desk", "BACKUP-PC", "abcd1234", "hunter2").ToJson()));

            var welcome = TwinWelcome.Parse(ReadWord(reader, TwinWord.Welcome).Payload);
            Assert.NotNull(welcome);
            Assert.Equal("Gala", welcome!.Show);
            Assert.Equal(Environment.ProcessId, welcome.Pid);
            Assert.Equal(services.Twin.Instance, welcome.Instance);
            var show = JsonUtil.Deserialize<ShowState>(ReadWord(reader, TwinWord.Show).Payload);
            Assert.Equal("Gala", show!.Name);
            Assert.Equal(TwinRole.Main, show.Twin.Role);  // the file as it is; a standby never lands that section (TwinSync.ApplyShow skips it)
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
            Assert.True(long.Parse(beat.Payload) >= 1);
            Write(stream, TwinMessage.Format(TwinWord.Beat, "1"));
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
            Assert.Equal("hunter2", join.Key);
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
            Assert.Equal("hunter2", join2.Key);

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
        public bool KillFails { get; set; }
        public int Kills { get; private set; }
        public long? StartTicks(int pid) => Alive.TryGetValue(pid, out var ticks) ? ticks : null;
        public string ExePath(int pid) => "";
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
            Write(stream, TwinMessage.Format(TwinWord.Join, new TwinJoin("Backup desk", "BACKUP-PC", "abcd1234", "hunter2", TookOver: true).ToJson()));
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

            // TAKE BACK: its show lands here, its air goes on here, the hold lifts, and it is told to follow again — then the whole show goes back over the link.
            var back = services.Actions.Execute(ShowActionKind.TwinTakeBack, ActionOrigin.Desk);
            Assert.True(back.Ok, back.Message);
            Assert.StartsWith("TOOK BACK from Backup desk at", back.Message);
            Assert.Contains("its show landed here", back.Message);
            Assert.Contains("Wall switch: cue 'Wall to main' fired.", back.Message);   // the wall back to this machine
            Assert.Equal("Wall: main", vm.State.Overlays.Message.Text);
            Assert.Equal("Edited at the standby", vm.State.Name);
            Assert.Equal("", services.OutputsHeldBy);
            Assert.Equal("", twin.Holder);
            Assert.Equal("Standby look", services.AirLabel);
            Assert.Equal(TwinWord.HandBack, ReadWord(reader, TwinWord.HandBack).Word);
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

            // It keeps dialling; the join says it has the show; the main gets the show it has — edited while it ran — and its air.
            vm.State.Name = "Edited while it ran";
            services.WriteRunPlace();   // the caller's place moved while it ran: the air record it sends is this one
            var (peer2, reader2, join2) = AcceptTookOverJoin(main);
            using var _peer2 = peer2;
            using var _reader2 = reader2;
            Assert.True(join2.TookOver);
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

            // HANDBACK: the outputs close and are held again, the marker goes, and the show that follows puts this desk in step.
            Write(stream2, TwinMessage.Format(TwinWord.HandBack));
            PumpUntil(() => twin.Phase == TwinPhase.Connecting);
            Assert.Equal("this desk is the standby twin", services.OutputsHeldBy);
            Assert.Null(TwinHandover.Read(services.Store.BaseDirectory));
            Assert.Contains("took the show back", vm.StatusMessage);
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

            // The process ends: the hold lifts and the outputs are this desk's again.
            probe.Alive.Clear();
            clockOffset = TimeSpan.FromSeconds(6);
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
                Write(stream, TwinMessage.Format(TwinWord.Join, new TwinJoin("Stranger", "SOME-PC", "ffff0000", theirs, TookOver: tookOver).ToJson()));
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
                Write(stream, TwinMessage.Format(TwinWord.Join, new TwinJoin("Backup desk", "BACKUP-PC", "abcd1234", key, TookOver: false).ToJson()));
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
            Write(stream, TwinMessage.Format(TwinWord.Join, new TwinJoin("Backup desk", Environment.MachineName, "abcd1234", "hunter2", TookOver: true).ToJson()));
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

            // A wall switch cue — here, words on the message overlay stand in for the switcher's verb — and it takes over by itself, firing it.
            CallerCue(vm.State, "Wall to standby", ShowActionKind.MessageOn, "Wall: standby");
            vm.State.Twin.TakeOverCue = "Wall to standby";
            Dispatcher.UIThread.RunJobs();
            clockOffset = TimeSpan.FromSeconds(9);
            twin.Tick();
            Assert.Equal(TwinPhase.TookOver, twin.Phase);
            Assert.Equal("", services.OutputsHeldBy);
            Assert.Equal("Walk-in", services.AirLabel);
            Assert.Equal("Wall: standby", vm.State.Overlays.Message.Text);
            Assert.True(vm.State.Overlays.Message.Enabled);
            Assert.Contains("\"takeOverCue\":\"Wall to standby\"", twin.StatusJson());
        }
        finally
        {
            main.Stop();
            b.Dispose();
        }
    }
}
