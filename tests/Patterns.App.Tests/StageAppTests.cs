using System.Net;
using System.Net.Sockets;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>The stage on a live desk: the timer's verbs on the countdown's clock, a message with its receipt through the page's API, the pages themselves, a cue that messages the stage.</summary>
public class StageAppTests
{
    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    [AvaloniaFact]
    public void TheStageTimerRunsPausesAndNudgesAndAMessageComesBackWithAReceipt()
    {
        var b = TestApp.Boot(prepare: dir =>
        {
            var s = SettingsStore.Fresh();
            s.Control.HttpPort = FreePort();
            s.Control.TcpPort = FreePort();
            s.Twin.AcceptCallers = false;
            File.WriteAllText(Path.Combine(dir, "patterns.settings.json"), JsonUtil.Serialize(s));
        });
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var stage = services.Stage;
            var utc = new DateTime(2026, 9, 13, 18, 0, 0, DateTimeKind.Utc);
            stage.UtcNow = () => utc;
            var router = new CommandRouter(services);

            Assert.Equal(StageTimerPhase.Idle, stage.Time().Phase);
            Assert.StartsWith("Stage timer idle", stage.StatusLine);

            // START 10: the countdown is the timer.
            Assert.StartsWith("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("TIMER START 10"))));
            services.AirState.Countdown.ArmedAtUtc = utc;                                  // the verb armed it at the wall clock; the test's clock reads from here
            var t = stage.Time();
            Assert.Equal(StageTimerPhase.Running, t.Phase);
            Assert.Equal("10:00", t.Text);
            Assert.Equal("green", t.Colour);

            // PAUSE at 9:00 keeps 9:00; +60 makes it 10:00; RESUME runs from now; -30 reads 9:30.
            utc = utc.AddSeconds(60);
            Assert.True(services.Actions.Execute(ShowActionKind.TimerPause, ActionOrigin.Desk).Ok);
            Assert.True(services.AirState.Stage.Paused);
            Assert.Equal("9:00", stage.Time().Text);
            utc = utc.AddSeconds(300);
            Assert.Equal("9:00", stage.Time().Text);
            Assert.StartsWith("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("TIMER +60"))));
            Assert.Equal("10:00", stage.Time().Text);
            Assert.True(services.Actions.Execute(ShowActionKind.TimerResume, ActionOrigin.Desk).Ok);
            Assert.Equal(StageTimerPhase.Running, stage.Time().Phase);
            Assert.StartsWith("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("TIMER -30"))));
            Assert.Equal("9:30", stage.Time().Text);
            Assert.False(services.Actions.Execute(ShowActionKind.TimerResume, ActionOrigin.Desk).Ok);   // nothing paused
            Assert.Contains("Stage timer 9:30", stage.StatusLine);

            // A message to the speaker: pending, on the page's API, acknowledged through it, seen.
            var rev = stage.Rev;
            var said = services.Actions.Execute(new ShowAction(ShowActionKind.StageMessage, "speaker", "Wrap up"), ActionOrigin.Desk);
            Assert.True(said.Ok, said.Message);
            Assert.True(stage.Rev > rev);
            var pending = stage.Pending("speaker");
            Assert.NotNull(pending);
            Assert.Equal("Wrap up", pending!.Text);
            Assert.Contains("1 message waiting on a receipt", stage.StatusLine);

            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{vm.State.Control.HttpPort}/") };
            var json = TestApp.Pump(http.GetStringAsync("api/stage"));
            using (var doc = JsonDocument.Parse(json))
            {
                var root = doc.RootElement;
                Assert.Equal("running", root.GetProperty("timer").GetProperty("phase").GetString());
                Assert.Equal("Wrap up", root.GetProperty("pendingSpeaker").GetProperty("text").GetString());
                Assert.Equal(pending.Id, root.GetProperty("pendingSpeaker").GetProperty("id").GetString());
                Assert.True(root.GetProperty("presets").GetArrayLength() > 3);
                Assert.Equal(JsonValueKind.Null, root.GetProperty("pendingCrew").ValueKind);
            }
            var ack = TestApp.Pump(http.PostAsync("api/stage/ack", new StringContent(pending.Id)).ContinueWith(t2 => t2.Result.Content.ReadAsStringAsync().Result));
            Assert.Equal("{\"ok\":true}", ack);
            Assert.Null(stage.Pending("speaker"));
            Assert.NotNull(services.AirState.Stage.Messages.Single().AckUtc);
            Assert.Contains("seen at", vm.StatusMessage);
            Assert.Contains("\"seen\":true", stage.StatusJson());
            var again = TestApp.Pump(http.PostAsync("api/stage/ack", new StringContent(pending.Id)).ContinueWith(t2 => t2.Result.Content.ReadAsStringAsync().Result));
            Assert.Contains("\"ok\":false", again);

            // The pages are served; the remote links to them; STAGE STATUS is the same payload.
            Assert.Contains("Patterns Stage", TestApp.Pump(http.GetStringAsync("stage?view=crew")));
            Assert.Contains("Patterns Timer", TestApp.Pump(http.GetStringAsync("timer")));
            Assert.Contains("/timer", TestApp.Pump(http.GetStringAsync("/")));
            Assert.StartsWith("OK {\"rev\"", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("STAGE STATUS"))));

            // A cue carries a message to the crew; CLEAR marks it seen; FLASH sets the moment.
            var stack = CueStacks.Caller(vm.State);
            var cue = new RunCueConfig { Number = "01", Name = "Mic check" };
            cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.StageMessage, Target = "crew", Value = "Mic 2 is live" });
            stack.Cues.Add(cue);
            Assert.True(services.Actions.FireCue(cue, ActionOrigin.Desk).Ok);
            Assert.Equal("Mic 2 is live", stage.Pending("crew")!.Text);
            Assert.Contains("cue", services.AirState.Stage.Messages.Last().From);
            Assert.StartsWith("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("STAGE CLEAR"))));
            Assert.Null(stage.Pending("crew"));
            Assert.True(services.Actions.Execute(ShowActionKind.TimerFlash, ActionOrigin.Desk).Ok);
            Assert.Equal(utc.AddSeconds(3), services.AirState.Stage.FlashUntilUtc);

            // STOP is the countdown's off.
            Assert.StartsWith("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("TIMER STOP"))));
            Assert.Equal(StageTimerPhase.Idle, stage.Time().Phase);
        }
        finally
        {
            b.Dispose();
        }
    }
}
