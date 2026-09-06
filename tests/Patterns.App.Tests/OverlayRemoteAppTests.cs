using System.Net;
using System.Net.Sockets;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The overlays driven from a remote on a live desk: the verbs onto the air, STATE and the
/// feedback reading them, the wire through EDIT SAFE, and the phone's OVERLAYS tab.
/// </summary>
public class OverlayRemoteAppTests
{
    private static string Run(CommandRouter router, string line) => TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse(line)));

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

    [AvaloniaFact]
    public void TheVerbsDriveTheOverlaysAndStateReadsThem()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            vm.IsSandboxActive = false;
            var router = new CommandRouter(services);
            var air = services.AirState;

            // The clock: on, its hours, the seconds and the date; a bare CLOCK toggles.
            Assert.Equal("OK", Run(router, "CLOCK ON"));
            Assert.True(air.Overlays.Clock.Enabled);
            Assert.Equal("OK", Run(router, "CLOCK 12"));
            Assert.False(air.Overlays.Clock.TwentyFourHour);
            Assert.Equal("OK", Run(router, "CLOCK 24"));
            Assert.True(air.Overlays.Clock.TwentyFourHour);
            Assert.StartsWith("ERR", Run(router, "CLOCK 13"));
            Assert.Equal("OK", Run(router, "CLOCK SECONDS OFF"));
            Assert.False(air.Overlays.Clock.ShowSeconds);
            Assert.Equal("OK", Run(router, "CLOCK SECONDS"));
            Assert.True(air.Overlays.Clock.ShowSeconds);
            Assert.Equal("OK", Run(router, "CLOCK DATE OFF"));
            Assert.False(air.Overlays.Clock.ShowDate);
            Assert.Equal("OK", Run(router, "CLOCK"));
            Assert.False(air.Overlays.Clock.Enabled);
            Assert.Equal("OK", Run(router, "CLOCK"));
            Assert.True(air.Overlays.Clock.Enabled);

            // The message: the words and on, off with the words kept, a ticker.
            Assert.Equal("OK", Run(router, "MESSAGE Doors open at 7"));
            Assert.True(air.Overlays.Message.Enabled);
            Assert.Equal("Doors open at 7", air.Overlays.Message.Text);
            Assert.Equal("OK", Run(router, "MESSAGE OFF"));
            Assert.False(air.Overlays.Message.Enabled);
            Assert.Equal("Doors open at 7", air.Overlays.Message.Text);
            Assert.Equal("OK", Run(router, "MESSAGE"));
            Assert.True(air.Overlays.Message.Enabled);
            Assert.Equal("OK", Run(router, "TICKER ON"));
            Assert.True(air.Overlays.Message.Scroll);
            Assert.Equal("OK", Run(router, "MESSAGE SCROLL OFF"));
            Assert.False(air.Overlays.Message.Scroll);

            // The countdown: minutes, minutes:seconds, the label, a time of day, STOP, a bare START as set up.
            Assert.Equal("OK", Run(router, "COUNTDOWN 5"));
            Assert.True(air.Countdown.Enabled);
            Assert.Equal(CountdownTargetKind.Duration, air.Countdown.TargetKind);
            Assert.Equal(5, air.Countdown.DurationMinutes);
            Assert.NotNull(air.Countdown.ArmedAtUtc);
            Assert.Equal("OK", Run(router, "COUNTDOWN START 2:30"));
            Assert.Equal(2.5, air.Countdown.DurationMinutes);
            Assert.Equal("OK", Run(router, "COUNTDOWN LABEL BACK FROM LUNCH IN"));
            Assert.Equal("BACK FROM LUNCH IN", air.Countdown.Label);
            var target = DateTime.Now.AddHours(2).ToString("HH:mm");   // always ahead: past midnight it rolls to tomorrow
            Assert.Equal("OK", Run(router, "COUNTDOWN TO " + target));
            Assert.Equal(CountdownTargetKind.TimeOfDay, air.Countdown.TargetKind);
            Assert.Equal(target, air.Countdown.TargetTime);
            Assert.StartsWith("ERR", Run(router, "COUNTDOWN TO half past"));
            Assert.Equal("OK", Run(router, "COUNTDOWN STOP"));
            Assert.False(air.Countdown.Enabled);
            Assert.Equal("OK", Run(router, "COUNTDOWN START"));   // as set up: to the time
            Assert.True(air.Countdown.Enabled);
            Assert.Equal(CountdownTargetKind.TimeOfDay, air.Countdown.TargetKind);
            Assert.StartsWith("ERR", Run(router, "COUNTDOWN START soon"));

            // The logo, the PiP; STATE and the feedback read every overlay.
            Assert.Equal("OK", Run(router, "LOGO ON"));
            Assert.True(air.Overlays.Logo.Enabled);
            Assert.Equal("OK", Run(router, "PIP"));
            Assert.True(air.Overlays.Pip.Enabled);
            Assert.Equal("OK", Run(router, "WEATHER ON"));
            var json = router.StateJson();
            Assert.Contains("\"overlays\":{\"clock\":{\"on\":true,\"hours\":24,\"seconds\":true,\"date\":false,", json);
            Assert.Contains("\"message\":{\"on\":true,\"text\":\"Doors open at 7\",\"scroll\":false}", json);
            Assert.Contains($"\"countdown\":{{\"on\":true,\"phase\":\"running\",\"label\":\"BACK FROM LUNCH IN\",\"target\":\"{target}\"", json);
            Assert.Contains("\"logo\":{\"on\":true,\"file\":false}", json);
            Assert.Contains("\"pip\":{\"on\":true}", json);
            var fed = OscFeedback.FromState(json);
            Assert.StartsWith("Clock 24 h with seconds · Message: Doors open at 7 · Countdown ", (string)Assert.Single(fed, m => m.Address == "/patterns/state/overlays/text").Args[0]!);
            Assert.Equal(1, Assert.Single(fed, m => m.Address == "/patterns/state/clock").Args[0]);
            Assert.Equal("Doors open at 7", Assert.Single(fed, m => m.Address == "/patterns/state/message/text").Args[0]);
            Assert.Equal("running", Assert.Single(fed, m => m.Address == "/patterns/state/countdown/phase").Args[0]);
            Assert.True(OverlayControl.CountsEverySecond(air.Countdown, DateTime.Now, DateTime.UtcNow));   // the remotes get a push every second

            // Every overlay off in one press; nothing on is still OK.
            Assert.Equal("OK", Run(router, "OVERLAYS OFF"));
            Assert.False(air.Overlays.Clock.Enabled || air.Overlays.Message.Enabled || air.Countdown.Enabled || air.Overlays.Logo.Enabled || air.Overlays.Pip.Enabled || air.Overlays.Weather.Enabled);
            Assert.Contains("\"text\":\"No overlays on.\"", router.StateJson());
            Assert.Equal("OK", Run(router, "OVERLAYS OFF"));

            // Through EDIT SAFE the verbs drive the air, never the edit.
            vm.IsSandboxActive = true;
            Assert.Equal("OK", Run(router, "CLOCK ON"));
            Assert.True(services.AirState.Overlays.Clock.Enabled);
            Assert.False(vm.State.Overlays.Clock.Enabled);
            vm.IsSandboxActive = false;
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ThePhonePageHasTheOverlaysTab()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            vm.State.Control.HttpPort = FreePort();
            vm.State.Control.TcpPort = FreePort();
            Dispatcher.UIThread.RunJobs();
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{vm.State.Control.HttpPort}/") };
            var page = Pump(http.GetStringAsync("/"));
            Assert.Contains("data-tab=\"overlays\"", page);
            Assert.Contains("id=\"tab-overlays\"", page);
            foreach (var line in new[]
                     {
                         "'CLOCK TOGGLE'", "'CLOCK SECONDS TOGGLE'", "'CLOCK DATE TOGGLE'", "'CLOCK ' + ", "'MESSAGE OFF'", "'MESSAGE SCROLL TOGGLE'", "'MESSAGE ' + t",
                         "'COUNTDOWN START 5'", "'COUNTDOWN STOP'", "'COUNTDOWN START ' + v", "'COUNTDOWN TO ' + v", "'COUNTDOWN LABEL ' + v",
                         "'LOGO TOGGLE'", "'PIP TOGGLE'", "'OVERLAYS OFF'",
                     })
            {
                Assert.Contains(line, page);
            }
            foreach (var box in new[] { "msgtext", "cdmins", "cdtime", "cdlabel" }) Assert.Contains($"id=\"{box}\"", page);

            // The page's own command endpoint drives the overlays without a client header, and the state it long-polls carries them.
            Assert.True(Pump(http.PostAsync("/api/cmd", new StringContent("MESSAGE Welcome to the show"))).IsSuccessStatusCode);
            Assert.True(Pump(http.PostAsync("/api/cmd", new StringContent("COUNTDOWN START 5"))).IsSuccessStatusCode);
            Dispatcher.UIThread.RunJobs();
            var state = Pump(http.GetStringAsync("/api/state"));
            Assert.Contains("\"message\":{\"on\":true,\"text\":\"Welcome to the show\"", state);
            Assert.Contains("\"countdown\":{\"on\":true,\"phase\":\"running\"", state);
            Assert.True(Pump(http.PostAsync("/api/cmd", new StringContent("OVERLAYS OFF"))).IsSuccessStatusCode);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("\"text\":\"No overlays on.\"", Pump(http.GetStringAsync("/api/state")));
        }
        finally
        {
            b.Dispose();
        }
    }
}
