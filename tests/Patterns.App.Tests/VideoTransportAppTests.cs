using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 86: the clip's transport on a live desk, as another app drives it over the wire — PAUSE,
/// PLAY, STOP and SEEK moving the decoder, the refusals that would change nothing, and STATE's video
/// row with the precise clock (positionMs, lengthMs, remainingMs, paused, rate, seq) and videos[].
/// </summary>
public class VideoTransportAppTests
{
    private static string Send(CommandRouter router, string line) => TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse(line)));

    private static FakeSource PutClipOnAir(TestApp.Booted b, AudioFakes fakes, string name, bool loop = false, [System.Runtime.CompilerServices.CallerFilePath] string file = "", [System.Runtime.CompilerServices.CallerMemberName] string member = "")
    {
        var (services, vm, _) = b;
        vm.ActivePattern.Kind = PatternKind.Media;
        vm.ActivePattern.Media.Source = MediaSource.Video;
        vm.ActivePattern.Media.Loop = loop;
        vm.ActivePattern.Media.VideoPath = AudioFakes.TempFile(b, name, file, member);
        Dispatcher.UIThread.RunJobs();
        services.ReconcileInputs();
        var fake = fakes.Sources.Single();
        fake.Length = 210;
        fake.Position = 62;
        return fake;
    }

    private static long Seq(string json)
    {
        using var doc = System.Text.Json.JsonDocument.Parse(json);
        return doc.RootElement.GetProperty("video").GetProperty("seq").GetInt64();
    }

    [AvaloniaFact]
    public void AnotherAppPausesSeeksAndStopsTheClipAndReadsItBack()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var fakes = AudioFakes.Install(b);
            var router = new CommandRouter(services);
            var fake = PutClipOnAir(b, fakes, "sponsor.mp4");

            // The precise clock: milliseconds, the rate, a seq — at the end of the row; videos[] lists the mounted clip.
            var json = router.StateJson();
            Assert.Contains("\"positionMs\":62000", json);
            Assert.Contains("\"lengthMs\":210000", json);
            Assert.Contains("\"remainingMs\":148000", json);
            Assert.Contains("\"paused\":false", json);
            Assert.Contains("\"rate\":1", json);
            Assert.Contains("\"seekable\":true", json);
            Assert.Contains("\"videos\":[{\"key\":", json);
            Assert.Contains("\"onAir\":true", json);
            var seq0 = Seq(json);
            Assert.Equal(seq0, Seq(router.StateJson()));   // nothing moved, nothing to resync

            // PAUSE holds it; a second PAUSE changes nothing and is refused; STATE says paused and the seq moved.
            Assert.StartsWith("OK", Send(router, "VIDEO PAUSE"));
            Assert.True(fake.Paused);
            Assert.Contains("already paused", Send(router, "VIDEO PAUSE"));
            var paused = router.StateJson();
            Assert.Contains("\"paused\":true", paused);
            Assert.Contains("\"chip\":\"VT PAUSED 2:28\"", paused);
            var seq1 = Seq(paused);
            Assert.True(seq1 > seq0);

            // PLAY plays on; a PLAY while playing is refused.
            Assert.StartsWith("OK", Send(router, "VIDEO PLAY"));
            Assert.False(fake.Paused);
            Assert.Contains("already playing", Send(router, "VIDEO PLAY"));

            // SEEK: from the top, a clock time, from the end; each moves the decoder.
            Assert.StartsWith("OK", Send(router, "VIDEO SEEK 1:23"));
            Assert.Equal(83, fake.SeekedTo);
            Assert.StartsWith("OK", Send(router, "VIDEO SEEK END 5"));
            Assert.Equal(205, fake.SeekedTo);
            Assert.StartsWith("OK", Send(router, "VT SEEK 30"));
            Assert.Equal(30, fake.SeekedTo);
            Assert.True(Seq(router.StateJson()) > seq1);
            Assert.StartsWith("ERR", Send(router, "VIDEO SEEK soon"));

            // STOP: back to the top and held — the outputs untouched.
            var outputsBefore = services.Bus.OutputsLive;
            Assert.StartsWith("OK", Send(router, "VIDEO STOP"));
            Assert.Equal(0, fake.SeekedTo);
            Assert.True(fake.Paused);
            Assert.Equal(outputsBefore, services.Bus.OutputsLive);
            Assert.Contains("already stopped", Send(router, "VIDEO STOP"));
            Assert.StartsWith("OK", Send(router, "VIDEO PLAY"));
            Assert.Contains(services.Journal.Tail(20), e => e.Kind == nameof(ShowActionKind.VideoStop));
            Assert.Contains(services.Journal.Tail(20), e => e.Kind == nameof(ShowActionKind.VideoSeek));

            // A length not known yet: from the top still works, from the end is refused rather than guessed; no lengthMs.
            fake.Length = 0;
            Assert.Contains("not said how long", Send(router, "VIDEO SEEK END 5"));
            Assert.StartsWith("OK", Send(router, "VIDEO SEEK 12"));
            var unknown = router.StateJson();
            Assert.Contains("\"lengthMs\":null", unknown);
            Assert.Contains("\"remainingMs\":null", unknown);

            // A clip shorter than the gap starts again from its top.
            fake.Length = 6;
            Assert.StartsWith("OK", Send(router, "VIDEO SEEK END 10"));
            Assert.Equal(0, fake.SeekedTo);

            // An ended clip: PLAY and PAUSE refuse with the way back; RESTART is the verb.
            fake.Length = 210;
            fake.Ended = true;
            Assert.Contains("VIDEO RESTART", Send(router, "VIDEO PLAY"));
            Assert.Contains("VIDEO RESTART", Send(router, "VIDEO PAUSE"));
            fake.Ended = false;

            // The panel's keys: one play/pause key and a stop.
            vm.VideoPlayPauseCommand.Execute(null);
            Assert.True(fake.Paused);
            vm.VideoPlayPauseCommand.Execute(null);
            Assert.False(fake.Paused);
            vm.VideoStopCommand.Execute(null);
            Assert.True(fake.Paused);
            Assert.Equal(0, fake.SeekedTo);

            // No clip on air: every transport verb says so.
            vm.ActivePattern.Kind = PatternKind.Grid;
            Dispatcher.UIThread.RunJobs();
            services.ReconcileInputs();
            foreach (var line in new[] { "VIDEO PLAY", "VIDEO PAUSE", "VIDEO STOP", "VIDEO SEEK 10" })
            {
                Assert.Contains("No video is on air", Send(router, line));
            }
        }
        finally
        {
            b.Window.Close();
            b.Services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void ALoopHasNoEndToCountDownTo()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var fakes = AudioFakes.Install(b);
            var router = new CommandRouter(services);
            PutClipOnAir(b, fakes, "walk-in.mp4", loop: true);
            var json = router.StateJson();
            Assert.Contains("\"loops\":true", json);
            Assert.Contains("\"lengthMs\":210000", json);
            Assert.Contains("\"remainingMs\":null", json);
            Assert.Contains("\"remainingMs\":null,\"playing\"", json);   // videos[] too
        }
        finally
        {
            b.Window.Close();
            b.Services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void TheHelloNamesTheWireAndNothingOfTheShow()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, _, _) = b;
            services.State.Name = "Secret AGM";
            var hello = services.Beacon.BuildHello();
            var json = hello.ToJson();
            Assert.Equal(services.State.Control.TcpPort, hello.Wire);
            Assert.Equal(services.State.Control.HttpPort, hello.Web);
            Assert.Equal(services.Kernel.ShowBusId, hello.Id);                       // round 87: the install's id; the process rides as inst
            Assert.Equal(services.Beacon.Instance, hello.Instance);
            Assert.DoesNotContain("Secret AGM", json);
            if (services.State.Control.Token.Length > 0) Assert.DoesNotContain(services.State.Control.Token, json);
        }
        finally
        {
            b.Window.Close();
            b.Services.Shutdown();
        }
    }
}
