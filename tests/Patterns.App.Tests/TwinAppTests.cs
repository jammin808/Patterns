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
            Assert.Contains("Walk-in", vm.LookNames);                         // the desk's lists followed
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
                Write(stream, TwinMessage.Format(TwinWord.Welcome, new TwinWelcome("MAIN-DESK", "SOME-OTHER-PC", "ef01", 1, 0, "", "Gala").ToJson()));
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
                Write(stream2, TwinMessage.Format(TwinWord.Welcome, new TwinWelcome("MAIN-DESK", "SOME-OTHER-PC", "ef01", 1, 0, "", "Gala").ToJson()));
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
}
