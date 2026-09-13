using System.Diagnostics;
using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Play;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

public class AudienceLoadTests
{
    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static void PumpUntil(Func<bool> condition, int timeoutMs = 20000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
            if (Environment.TickCount64 > deadline) throw new TimeoutException("the condition never held");
        }
    }

    [AvaloniaFact]
    public void TwoHundredPhonesJoinWaitOnTheRoomAndAnswerTogetherWithinTheBudgets()
    {
        const int Phones = 200;
        var audiencePort = FreePort();
        var b = TestApp.Boot("patterns-tests-load-", dir =>
        {
            var s = SettingsStore.Fresh();
            s.Name = "Gala";
            s.Control.Enabled = true;
            s.Control.HttpPort = FreePort();
            s.Control.TcpPort = FreePort();
            s.Control.AudienceEnabled = true;
            s.Control.AudiencePort = audiencePort;
            s.Control.AudienceMaxPlayers = Phones + 50;
            s.Twin.AcceptCallers = false;
            s.Watchdog.BeaconListenPort = FreePort();
            s.Watchdog.BeaconPort = s.Watchdog.BeaconListenPort;
            File.WriteAllText(Path.Combine(dir, "patterns.settings.json"), JsonUtil.Serialize(s));
        }, NodeKind.Arcade);
        try
        {
            var (services, _, _) = b;
            var play = services.Play;
            // Every phone here comes from one address: the per-address budgets are the ones a room of real phones never meets.
            play.Budget = play.Budget with { JoinsPerAddressPerMinute = 100000, MaxConnectionsPerAddress = 100000, AnswersPerTokenPerMinute = 100 };
            PumpUntil(() => services.Control.AudienceListening);
            var router = new CommandRouter(services);
            string Wire(string line) => TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse(line)));
            using var phone = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{audiencePort}/"), Timeout = TimeSpan.FromSeconds(60) };

            // Everyone joins at once.
            var clock = Stopwatch.StartNew();
            var joins = TestApp.Pump(Task.WhenAll(Enumerable.Range(0, Phones).Select(async i =>
            {
                var r = await phone.PostAsync("api/play/join", new StringContent($"{{\"nick\":\"Phone {i}\",\"room\":\"{play.Code}\"}}"));
                var j = JsonDocument.Parse(await r.Content.ReadAsStringAsync()).RootElement;
                return j.GetProperty("ok").GetBoolean() ? j.GetProperty("token").GetString()! : "";
            })));
            var joined = clock.Elapsed;
            Assert.All(joins, t => Assert.NotEmpty(t));
            Assert.Equal(Phones, play.Room.PlayerCount);

            // Everyone waits on the room (the question already written, so the open is the first move); everyone wakes together.
            Assert.StartsWith("OK", Wire("PLAY ADD choice Lunch? | Pizza | Salad | Soup"));
            var rev = play.Rev;
            var waits = Enumerable.Range(0, Phones).Select(i => phone.GetStringAsync($"api/play/state?token={joins[i]}&since=0&rev={rev}")).ToList();
            PumpUntil(() => play.LongPolls >= Phones, 30000);
            Assert.True(play.LongPollsPeak >= Phones, play.LongPollsPeak.ToString());
            var opened = clock.Elapsed;
            Assert.StartsWith("OK", Wire("PLAY OPEN"));
            var states = TestApp.Pump(Task.WhenAll(waits));
            var woke = clock.Elapsed - opened;
            Assert.All(states, st => Assert.Contains("\"state\":\"open\"", st));
            Assert.True(woke < TimeSpan.FromSeconds(10), $"the room took {woke.TotalSeconds:0.0} s to wake {Phones} phones");
            PumpUntil(() => play.LongPolls == 0, 10000);

            // Everyone answers at once; every answer counts.
            var q = play.Room.Current!;
            var answers = TestApp.Pump(Task.WhenAll(Enumerable.Range(0, Phones).Select(async i =>
            {
                var r = await phone.PostAsync("api/play/answer", new StringContent($"{{\"token\":\"{joins[i]}\",\"question\":\"{q.Id}\",\"choices\":[{i % 3}]}}"));
                return await r.Content.ReadAsStringAsync();
            })));
            Assert.All(answers, a => Assert.Contains("\"ok\":true", a));
            var results = play.Room.Results(q);
            Assert.Equal(Phones, results.Answers);
            Assert.Equal(Phones, results.Counts.Sum());
            var total = clock.Elapsed;
            Assert.True(total < TimeSpan.FromSeconds(60), $"{Phones} phones took {total.TotalSeconds:0.0} s (joined in {joined.TotalSeconds:0.0} s)");

            // The budgets bite: a phone past its answers for the minute is told to slow down, the room unmoved.
            play.Budget = play.Budget with { AnswersPerTokenPerMinute = 1 };
            var slow = TestApp.Pump(phone.PostAsync("api/play/answer", new StringContent($"{{\"token\":\"{joins[0]}\",\"question\":\"{q.Id}\",\"choices\":[1]}}")).ContinueWith(t => t.Result.Content.ReadAsStringAsync().Result));
            Assert.Contains("Slow down", slow);
            play.Budget = play.Budget with { JoinsPerAddressPerMinute = 1 };
            var refused = TestApp.Pump(phone.PostAsync("api/play/join", new StringContent("{\"nick\":\"One more\"}")).ContinueWith(t => t.Result.Content.ReadAsStringAsync().Result));
            Assert.Contains("Too many joins", refused);
            // And the seats: a room at its cap turns a new phone away.
            play.Budget = play.Budget with { JoinsPerAddressPerMinute = 100000 };
            b.Vm.State.Control.AudienceMaxPlayers = Phones;
            var full = TestApp.Pump(phone.PostAsync("api/play/join", new StringContent("{\"nick\":\"Late\"}")).ContinueWith(t => t.Result.Content.ReadAsStringAsync().Result));
            Assert.Contains("the room is full", full);
            Assert.Contains($"\"players\":{Phones}", Wire("AUDIENCE STATUS"));
        }
        finally
        {
            b.Dispose();
        }
    }
}
