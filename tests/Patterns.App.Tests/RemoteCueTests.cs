using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>The cue stack over the wire: HELLO, CUE verbs, STOPALL, the pushed cuestack block, the tablet's endpoints, the pop-out.</summary>
public class RemoteCueTests
{
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

    private sealed record Built(RunCueConfig A, RunCueConfig B, RunCueConfig C);

    private static Built Build(TestApp.Booted b)
    {
        b.Vm.IsSandboxActive = false;
        b.Vm.ActivePattern.Kind = PatternKind.ColorBars;
        b.Vm.NewLookName = "A";
        b.Vm.SaveLookCommand.Execute(null);
        b.Vm.ActivePattern.Kind = PatternKind.Focus;
        b.Vm.NewLookName = "B";
        b.Vm.SaveLookCommand.Execute(null);
        b.Vm.ActivePattern.Kind = PatternKind.Grid;
        var stack = CueStacks.Caller(b.Vm.State);
        RunCueConfig Cue(string number, string name, string look)
        {
            var cue = new RunCueConfig { Number = number, Name = name };
            cue.Actions.Add(new CueActionConfig { Kind = CueActionKind.ApplyLook, Target = LookService.Find(b.Vm.State, look)!.Id });
            stack.Cues.Add(cue);
            return cue;
        }
        var a = Cue("01.010", "Walk-in", "A");
        var bb = Cue("01.020", "Five-minute call", "B");
        var c = Cue("01.030", "Holding", "A");
        Dispatcher.UIThread.RunJobs();
        return new Built(a, bb, c);
    }

    [AvaloniaFact]
    public void TheCueVerbsDriveTheStackOverTcpWithTheFenceAndTheName()
    {
        var b = TestApp.Boot();
        try
        {
            var built = Build(b);
            b.Vm.State.Control.HttpPort = FreePort();
            b.Vm.State.Control.TcpPort = FreePort();
            Dispatcher.UIThread.RunJobs();

            using var client = new TcpClient();
            Pump(client.ConnectAsync(IPAddress.Loopback, b.Vm.State.Control.TcpPort));
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            var pushed = new List<string>();

            var greet = Pump(reader.ReadLineAsync());
            Assert.StartsWith("STATE {", greet);
            Assert.Contains("\"cuestack\":", greet);

            void Send(string line) => stream.Write(Encoding.UTF8.GetBytes(line + "\n"));
            string Response()
            {
                while (true)
                {
                    var line = Pump(reader.ReadLineAsync());
                    Assert.NotNull(line);
                    if (line!.StartsWith("STATE ")) pushed.Add(line);
                    else return line;
                }
            }

            Send("HELLO FOH deck");
            Assert.Equal("OK", Response());

            Send("CUE ARM ON");
            Assert.StartsWith("ERR remotes may not arm", Response()); // the Remote tab decides

            b.Vm.State.Control.RemotesMayArm = true;
            Send("CUE ARM ON");
            Assert.Equal("OK", Response());
            Dispatcher.UIThread.RunJobs();
            Assert.True(b.Services.CueStack.Armed);
            Assert.Equal(built.A.Id, b.Services.CueStack.Runtime.StandbyCueId);
            Assert.Equal("tcp FOH deck", b.Services.Journal.Tail(1).Single().Origin); // the name, not the address

            Send("CUE STANDBY 01.020");
            Assert.StartsWith("OK {", Response());
            Assert.Equal(built.B.Id, b.Services.CueStack.Runtime.StandbyCueId);

            Send("CUE GO " + built.A.Id);
            Assert.StartsWith("ERR", Response()); // the sender saw A on standby; it moved
            Assert.Contains("standby moved", b.Services.CueStack.History.First().Detail);
            Assert.Equal(PatternKind.Grid, b.Services.Bus.Current.State.Pattern.Kind);

            Send("CUE GO " + built.B.Id);
            var ok = Response();
            Assert.StartsWith("OK {", ok);
            Assert.Contains("\"outcome\":\"Done\"", ok);
            Assert.Contains("Five-minute call", ok);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.Focus, b.Services.Bus.Current.State.Pattern.Kind);
            Assert.Equal(built.C.Id, b.Services.CueStack.Runtime.StandbyCueId);
            Assert.Equal("tcp FOH deck", b.Services.CueStack.History.First().Origin);

            Send("CUE HOLD ON");
            Assert.Equal("OK", Response());
            Send("CUE GO " + built.C.Id);
            Assert.Contains("held", Response());
            Send("CUE HOLD OFF");
            Assert.Equal("OK", Response());

            Send("CUE STANDBY PREV");
            Assert.StartsWith("OK {", Response());
            Assert.Equal(built.B.Id, b.Services.CueStack.Runtime.StandbyCueId);

            Send("CUE LIST");
            var list = Response();
            Assert.StartsWith("OK {", list);
            Assert.Contains("\"listRev\"", list);
            Assert.Contains("Holding", list);

            b.Vm.State.AudioPlayer.Playing = true;
            Send("STOPALL");
            Assert.Equal("OK", Response());
            Dispatcher.UIThread.RunJobs();
            Assert.False(b.Vm.State.AudioPlayer.Playing);

            // A standby move is not a snapshot change — the runtime pushes on its own event.
            Send("PING");
            Assert.Equal("OK PONG", Response());
            Thread.Sleep(350);
            Dispatcher.UIThread.RunJobs();
            Send("PING");
            Response();
            var withStandby = pushed.LastOrDefault(p => p.Contains("\"standby\":{") && p.Contains("01.020"));
            Assert.NotNull(withStandby);
            Assert.Contains("\"armed\":true", withStandby);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheTabletEndpointsServeTheRunPageTheListAndALongPollAndGuardCueCommands()
    {
        var b = TestApp.Boot();
        try
        {
            var built = Build(b);
            b.Vm.State.Control.HttpPort = FreePort();
            b.Vm.State.Control.TcpPort = FreePort();
            Dispatcher.UIThread.RunJobs();
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{b.Vm.State.Control.HttpPort}/") };

            var page = Pump(http.GetStringAsync("/run"));
            Assert.Contains("CUE GO", page);
            Assert.Contains("X-Patterns-Client", page);
            Assert.Contains("/api/state?since=", page);

            var cues = Pump(http.GetStringAsync("/api/cues"));
            using (var doc = JsonDocument.Parse(cues))
            {
                Assert.Equal(3, doc.RootElement.GetProperty("cues").GetArrayLength());
                Assert.Equal("Walk-in", doc.RootElement.GetProperty("cues")[0].GetProperty("name").GetString());
            }

            // A cue command without the client header is refused; a plain command still works.
            var bare = Pump(http.PostAsync("/api/cmd", new StringContent("CUE STANDBY 01.020")));
            Assert.Contains("header required", Pump(bare.Content.ReadAsStringAsync()));
            var plain = Pump(http.PostAsync("/api/cmd", new StringContent("BLACKOUT ON")));
            Assert.Contains("\"ok\":true", Pump(plain.Content.ReadAsStringAsync()));
            Dispatcher.UIThread.RunJobs();
            Assert.True(b.Vm.State.Blackout);

            var request = new HttpRequestMessage(HttpMethod.Post, "/api/cmd") { Content = new StringContent("CUE STANDBY 01.020") };
            request.Headers.Add("X-Patterns-Client", "test");
            var guarded = Pump(http.SendAsync(request));
            Assert.Contains("\"ok\":true", Pump(guarded.Content.ReadAsStringAsync()));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(built.B.Id, b.Services.CueStack.Runtime.StandbyCueId);

            // The long-poll returns as soon as the revision moves.
            var state = Pump(http.GetStringAsync("/api/state"));
            long rev;
            using (var doc = JsonDocument.Parse(state)) rev = doc.RootElement.GetProperty("rev").GetInt64();
            var waiting = http.GetStringAsync($"/api/state?since={rev}");
            Thread.Sleep(300);
            Assert.False(waiting.IsCompleted);
            b.Services.CueStack.StandbyMove(+1);
            var next = Pump(waiting, 5000);
            using (var doc = JsonDocument.Parse(next))
            {
                Assert.True(doc.RootElement.GetProperty("rev").GetInt64() > rev);
                Assert.Equal("01.030", doc.RootElement.GetProperty("cuestack").GetProperty("standby").GetProperty("number").GetString());
            }

            var jpeg = Pump(http.GetByteArrayAsync("/pgm.jpg"));
            Assert.True(jpeg.Length > 500);
            Assert.Equal(0xFF, jpeg[0]);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ThePopOutWindowRunsTheStackFromItsOwnKeys()
    {
        var b = TestApp.Boot();
        try
        {
            var built = Build(b);
            b.Vm.PopOutRunCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var popout = b.Vm.RunWindow;
            Assert.NotNull(popout);
            Assert.False(b.Vm.IsRunLayout); // the main window stays as it was

            popout!.PressKey(Key.Return);
            popout.ReleaseKey(Key.Return);
            Assert.Equal(CueOutcome.Refused, b.Services.CueStack.History.First().Outcome); // disarmed: refused with a reason

            b.Services.CueStack.SetArmed(true, ActionOrigin.Desk);
            popout.PressKey(Key.Down);
            popout.ReleaseKey(Key.Down);
            Assert.Equal(built.B.Id, b.Services.CueStack.Runtime.StandbyCueId);
            popout.PressKey(Key.Return);
            popout.PressKey(Key.Return); // held: one GO
            popout.ReleaseKey(Key.Return);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.Focus, b.Services.Bus.Current.State.Pattern.Kind);
            Assert.Equal(built.C.Id, b.Services.CueStack.Runtime.StandbyCueId);
            Assert.Single(b.Services.CueStack.History, r => r.Outcome == CueOutcome.Done);

            popout.Close();
            Dispatcher.UIThread.RunJobs();
            Assert.Null(b.Vm.RunWindow);
        }
        finally
        {
            b.Dispose();
        }
    }
}
