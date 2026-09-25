using System.Net;
using System.Net.Sockets;
using System.Text;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Re-review reproducers (kept under docs/rereview-repro, outside the test projects). A PASS confirms the finding at the head under test.
/// EY-1 (EYE AT to an unpaired wire client), EY-5 (the rotated journal), EY-6 (an open replay never re-reads),
/// RT-1 (the NDI lane's "master" rate under EDIT SAFE).
/// </summary>
public class RereviewEyeRepro
{
    private const string Token = "K7QM-3XWD-P9RA";

    private static string Send(CommandRouter router, string line) => TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse(line)));

    private static string Csv(AppServices services) => Path.Combine(services.Store.BaseDirectory, "patterns.metrics.csv");

    private static void Read(AppServices services)
    {
        if (services.Eye.ReplayRead is { } read) TestApp.Pump(read.ContinueWith(_ => true, TaskScheduler.Default));
        Dispatcher.UIThread.RunJobs();
    }

    private static DateTime WholeSeconds(DateTime utc) => new(utc.Ticks / TimeSpan.TicksPerSecond * TimeSpan.TicksPerSecond, DateTimeKind.Utc);

    private static int FreePort()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        var port = ((IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }

    private static T Pump<T>(Task<T> task, int timeoutMs = 15000)
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

    private static void Pump(Task task, int timeoutMs = 15000) => Pump(task.ContinueWith(_ => true, TaskScheduler.Default), timeoutMs);

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
                if (!line!.StartsWith("STATE ")) return line;
            }
        }

        public void Dispose() => _client.Dispose();
    }

    private static (string Status, string Body) Http(int port, string method, string path, string body = "", params (string Name, string Value)[] headers)
    {
        using var client = new TcpClient();
        Pump(client.ConnectAsync(IPAddress.Loopback, port));
        using var stream = client.GetStream();
        var bodyBytes = Encoding.UTF8.GetBytes(body);
        var head = new StringBuilder($"{method} {path} HTTP/1.1\r\nHost: test\r\nContent-Length: {bodyBytes.Length}\r\n");
        foreach (var (name, value) in headers) head.Append($"{name}: {value}\r\n");
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

    // ---------------------------------------------------------------- EY-1: EYE AT on the wire, unpaired

    [AvaloniaFact]
    public void EY1_AnUnpairedWireClientReadsTheOperatorsJournalRowsThroughEyeAtWhileRecordIsGated()
    {
        var b = TestApp.Boot();
        var (services, vm, _) = b;
        var trusted = ControlService.TrustLoopback;
        ControlService.TrustLoopback = false;
        try
        {
            vm.State.Control.HttpPort = FreePort();
            vm.State.Control.TcpPort = FreePort();
            vm.State.Control.Token = Token;
            Dispatcher.UIThread.RunJobs();
            Assert.EndsWith("· paired.", services.Control.Status);

            var at = WholeSeconds(DateTime.UtcNow.AddMinutes(-5));
            services.Journal.Record(new ShowLogEntry(at, "Desk", "CueGo", "Keynote", "Failed", "the operators private words"));
            var stamp = ReplayTime.Stamp(at.AddSeconds(5));

            using var wire = new Wire(vm.State.Control.TcpPort);
            wire.Send("BLACKOUT ON");
            Assert.StartsWith("ERR not paired", wire.ReadResponse());                           // control: a verb waits for the token
            wire.Send("EYE AT " + stamp);
            var reply = wire.ReadResponse();
            Assert.StartsWith("OK", reply);                                                        // FINDING: answered unpaired
            Assert.Contains("the operators private words", reply);                                // FINDING: the journal row's words
            Assert.False(ControlProtocol.IsQuery(ControlProtocol.Parse("RECORD START")));          // while RECORD is gated on the same wire

            // Round 85.6 closed the web path: the same line over POST /api/cmd wants the token.
            var web = Http(vm.State.Control.HttpPort, "POST", "/api/cmd", "EYE AT " + stamp);
            Assert.StartsWith("403", web.Status);
        }
        finally
        {
            ControlService.TrustLoopback = trusted;
            vm.State.Control.Token = "";
            Dispatcher.UIThread.RunJobs();
            b.Dispose();
        }
    }

    // ---------------------------------------------------------------- EY-5: the rotated journal is never read

    [AvaloniaFact]
    public void EY5_AfterTheJournalRotatesTheReplaySaysNoRowsForAPeriodTheJournalRecorded()
    {
        var b = TestApp.Boot();
        var (services, vm, _) = b;
        try
        {
            var router = new CommandRouter(services);
            var t0 = WholeSeconds(DateTime.UtcNow.AddHours(-2));
            services.Journal.Record(new ShowLogEntry(t0.AddSeconds(10), "Desk", "CueGo", "Keynote", "Failed", "the encoder refused"));
            File.Move(services.Journal.Path, services.Journal.Path + ".1", overwrite: true);        // what ShowLog.RotateIfLarge does at 4 MB
            services.Journal.Record(new ShowLogEntry(t0.AddHours(1), "Desk", "ApplyLook", "Walk-in", "Done", ""));
            File.WriteAllLines(Csv(services), new[] { MetricsCsv.Header, MetricsCsv.Line(new MetricSample { Utc = t0, P95FrameMs = 12, OutputFps = 60 }) });

            var reply = Send(router, "EYE REPLAY " + ReplayTime.Stamp(t0.AddSeconds(15)));
            Assert.StartsWith("OK", reply);
            Read(services);
            Assert.Contains("Keynote", File.ReadAllText(services.Journal.Path + ".1"));           // the row is on disk
            Assert.Contains("no rows", vm.EyeReplayWords);                                          // FINDING: the replay says there were none
            Assert.NotEqual(CheckLight.Red, services.Eye.Shown.Find(EyeGraph.DeskId)!.Light);
        }
        finally { b.Dispose(); }
    }

    // ---------------------------------------------------------------- EY-6: an open replay never re-reads

    [AvaloniaFact]
    public void EY6_AnOpenReplayAnswersALaterInstantFromTheFirstPressesSnapshot()
    {
        var b = TestApp.Boot();
        var (services, vm, _) = b;
        try
        {
            var router = new CommandRouter(services);
            services.Journal.Record(new ShowLogEntry(WholeSeconds(DateTime.UtcNow.AddMinutes(-5)), "Desk", "ApplyLook", "Walk-in", "Done", ""));
            Assert.StartsWith("OK", Send(router, "EYE REPLAY ON"));
            Read(services);
            Assert.True(services.Eye.Replaying);

            Thread.Sleep(3000);
            var failedAt = WholeSeconds(DateTime.UtcNow.AddSeconds(-1));                          // after the first press
            services.Journal.Record(new ShowLogEntry(failedAt, "Desk", "CueGo", "Keynote", "Failed", "the encoder refused"));
            var reply = Send(router, "EYE REPLAY " + ReplayTime.Stamp(failedAt.AddSeconds(1)));
            Read(services);
            Assert.StartsWith("OK Replay", reply);
            Assert.Contains("no rows", reply);                                                      // FINDING: the failure 1 s before is not seen
        }
        finally { b.Dispose(); }
    }

    // ---------------------------------------------------------------- RT-1: the NDI lane's master rate under EDIT SAFE

    [AvaloniaFact]
    public void RT1_UnderEditSafeTheNdiLaneKeepsTheFrozenProgrammesRateAfterTheFollowIsSwitchedOff()
    {
        var b = TestApp.Boot();
        var (services, vm, _) = b;
        try
        {
            var fakes = new List<ScreenInfo>
            {
                new("a", "Left", new PixelRect(0, 0, 1920, 1080), 1.0, true, 0),
                new("c", "Lobby", new PixelRect(3000, 0, 1920, 1080), 1.0, false, 1),
            };
            services.Screens.All.Clear();
            foreach (var s in fakes) services.Screens.All.Add(s);
            vm.State.Output.Placements.Clear();
            vm.ReconcilePlacements(fakes);
            foreach (var p in vm.State.Output.Placements) { p.Enabled = true; p.DisplayHz = 60; }
            vm.State.Output.Placements.First(p => p.ScreenId == "c").DisplayHz = 50;
            vm.State.Output.MasterFps = 60;
            vm.State.Output.FollowDisplays = true;
            vm.RebuildSwitcherTiles(fakes);
            Dispatcher.UIThread.RunJobs();

            vm.IsSandboxActive = false;
            Dispatcher.UIThread.RunJobs();
            vm.IsSandboxActive = true;                                                              // EDIT SAFE opens over the 50 Hz follow
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(50, Patterns.App.Services.Rig.MasterRate(services.State, services.Screens.All).Effective);
            Assert.Equal(50, OutputRate.EffectiveMaster(services.Bus.Pair.Current.State.Output));

            vm.Screens.FollowDisplays = false;                                                      // the operator holds the set rate
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(60, Patterns.App.Services.Rig.MasterRate(services.State, services.Screens.All).Effective);   // the outputs, STATE, the Super Check
            Assert.Equal(50, OutputRate.EffectiveMaster(services.Bus.Pair.Current.State.Output));  // FINDING: what NdiSender reads stays 50
        }
        finally { b.Dispose(); }
    }
}
