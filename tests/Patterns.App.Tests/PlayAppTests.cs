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

public class PlayAppTests
{
    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static void PumpUntil(Func<bool> condition, int timeoutMs = 15000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
            if (Environment.TickCount64 > deadline) throw new TimeoutException("the condition never held");
        }
    }

    private static void Settings(string dir, Action<ShowState> edit)
    {
        var s = SettingsStore.Fresh();
        edit(s);
        File.WriteAllText(Path.Combine(dir, "patterns.settings.json"), JsonUtil.Serialize(s));
    }

    private static string Post(HttpClient http, string path, string body)
        => TestApp.Pump(http.PostAsync(path, new StringContent(body)).ContinueWith(t => t.Result.Content.ReadAsStringAsync().Result));

    private static string Get(HttpClient http, string path) => TestApp.Pump(http.GetStringAsync(path));

    [AvaloniaFact]
    public void TheHubRunsARoomForThePhonesTheHostAndTheWall()
    {
        var httpPort = FreePort();
        var audiencePort = FreePort();
        var b = TestApp.Boot("patterns-tests-hub-", dir => Settings(dir, s =>
        {
            s.Name = "Gala";
            s.Control.Enabled = true;
            s.Control.HttpPort = httpPort;
            s.Control.TcpPort = FreePort();
            s.Control.AudienceEnabled = true;
            s.Control.AudiencePort = audiencePort;
            s.Install.AdminPasscode = "1234";
            s.Twin.AcceptCallers = false;
            s.Watchdog.BeaconListenPort = FreePort();
            s.Watchdog.BeaconPort = s.Watchdog.BeaconListenPort;
        }), NodeKind.Arcade);
        try
        {
            var (services, vm, _) = b;
            var play = services.Play;
            Assert.Matches("^[A-Z]{4}$", play.Code);
            Assert.Contains($":{audiencePort}/play?room={play.Code}", play.JoinUrl);         // the door is on the audience port
            Assert.True(File.Exists(Path.Combine(services.Store.BaseDirectory, "play-story.json")));   // the sample, there to be edited
            var router = new CommandRouter(services);
            string Wire(string line) => TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse(line)));
            using var http = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{httpPort}/") };
            using var phone = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{audiencePort}/") };
            PumpUntil(() => services.Control.AudienceListening);

            // The boundary: the audience port answers the play pages and nothing else; the control port never the room.
            Assert.Contains("Patterns Play", Get(phone, "play?room=" + play.Code));
            Assert.Contains("Patterns Play", Get(phone, "/"));
            Assert.Equal(HttpStatusCode.NotFound, TestApp.Pump(phone.PostAsync("api/cmd", new StringContent("BLACKOUT"))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, TestApp.Pump(phone.GetAsync("api/state")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, TestApp.Pump(phone.GetAsync("pgm.jpg")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, TestApp.Pump(phone.GetAsync("host")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, TestApp.Pump(phone.PostAsync("api/play/host", new StringContent("1234"))).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, TestApp.Pump(phone.GetAsync("api/play/feed.csv")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, TestApp.Pump(http.GetAsync("play")).StatusCode);
            Assert.Equal(HttpStatusCode.NotFound, TestApp.Pump(http.PostAsync("api/play/join", new StringContent("{}"))).StatusCode);
            Assert.Contains("Patterns Play — host", Get(http, "host"));
            Assert.Contains("/host", Get(http, "/"));
            Assert.Contains("\"listening\":true", Wire("AUDIENCE STATUS"));

            // Two phones at the door — one with the wrong code.
            Assert.Contains("not this room", Post(phone, "api/play/join", "{\"nick\":\"Sam\",\"room\":\"ZZZZ\"}"));
            var sam = JsonDocument.Parse(Post(phone, "api/play/join", $"{{\"nick\":\"Sam\",\"group\":\"Table 4\",\"room\":\"{play.Code}\"}}")).RootElement;
            Assert.True(sam.GetProperty("ok").GetBoolean());
            var samToken = sam.GetProperty("token").GetString()!;
            var kim = JsonDocument.Parse(Post(phone, "api/play/join", "{\"nick\":\"Kim\"}")).RootElement;
            var kimToken = kim.GetProperty("token").GetString()!;
            Assert.Equal(2, play.Room.PlayerCount);
            Assert.Contains("2 joined", vm.PlayWords);

            // A quiz from the wire: opened, answered from the phones, scored, closed, revealed; the wall shows it on the arcade's lane.
            Assert.StartsWith("OK", Wire("PLAY ADD quiz 2 + 2? | 3 | 4 | 5 | correct=2 time=20"));
            Assert.StartsWith("ERR", Wire("PLAY ADD quiz no answer | A | B"));
            Assert.StartsWith("OK", Wire("PLAY OPEN"));
            Assert.Equal(PlayBoardMode.Results, play.Wall);
            Assert.NotNull(services.Arcade.Board);
            Assert.True(services.Arcade.IsRunning);
            var frames = services.Arcade.Frames;
            PumpUntil(() => services.Arcade.Frames > frames + 3);
            var q = play.Room.Current!;
            Assert.Contains("\"ok\":true", Post(phone, "api/play/answer", $"{{\"token\":\"{samToken}\",\"question\":\"{q.Id}\",\"choices\":[1]}}"));
            Assert.Contains("\"ok\":true", Post(phone, "api/play/answer", $"{{\"token\":\"{kimToken}\",\"question\":\"{q.Id}\",\"choices\":[0]}}"));
            Assert.Contains("already answered", Post(phone, "api/play/answer", $"{{\"token\":\"{samToken}\",\"question\":\"{q.Id}\",\"choices\":[1]}}"));
            var state = JsonDocument.Parse(Get(phone, $"api/play/state?token={samToken}&since=0")).RootElement;
            Assert.Equal("Sam", state.GetProperty("nick").GetString());
            Assert.True(state.GetProperty("question").GetProperty("mine").GetProperty("correct").GetBoolean());
            Assert.True(state.GetProperty("score").GetInt32() >= 500);
            Assert.Contains("\"players\":2", Wire("PLAY STATUS"));
            Assert.Contains("\"counts\":[1,1,0]", Wire("PLAY RESULTS"));
            Assert.StartsWith("OK", Wire("PLAY CLOSE"));
            Assert.StartsWith("ERR", Wire("PLAY CLOSE"));
            Assert.StartsWith("OK", Wire("PLAY REVEAL"));
            state = JsonDocument.Parse(Get(phone, $"api/play/state?token={kimToken}&since=0")).RootElement;
            Assert.Equal(1, state.GetProperty("question").GetProperty("correct").GetInt32());
            Assert.Equal("revealed", state.GetProperty("question").GetProperty("state").GetString());
            Assert.Equal("Sam", state.GetProperty("leaderboard")[0].GetProperty("nick").GetString());

            // Messages back: the room, a group, one phone by its name.
            Assert.StartsWith("OK", Wire("PLAY MESSAGE The next round starts soon"));
            Assert.StartsWith("OK", Wire("PLAY MESSAGE group:Table 4 you won the round"));
            Assert.StartsWith("OK", Wire("PLAY MESSAGE phone:Sam your answer was right"));
            Assert.StartsWith("ERR", Wire("PLAY MESSAGE phone:Nobody hello"));
            var samState = Get(phone, $"api/play/state?token={samToken}&since=0");
            Assert.Contains("you won the round", samState);
            Assert.Contains("your answer was right", samState);
            var kimState = Get(phone, $"api/play/state?token={kimToken}&since=0");
            Assert.Contains("The next round starts soon", kimState);
            Assert.DoesNotContain("you won the round", kimState);
            var feed = Get(http, "api/play/feed.csv");
            Assert.Contains("2 + 2?", feed);
            Assert.Contains("1. Sam", feed);

            // Words go through the queue; the host's page behind the passcode; the wall's modes.
            Assert.StartsWith("OK", Wire("PLAY ADD words One word for today"));
            Assert.StartsWith("OK", Wire("PLAY NEXT"));
            Assert.Contains("queued", Post(phone, "api/play/answer", $"{{\"token\":\"{samToken}\",\"words\":\"Bright\"}}"));
            Assert.Contains("\"state\":\"waiting\"", Wire("PLAY QUEUE"));
            services.Play.Tick();                                                     // no desk heard: the assistant is not asked, the item waits for the host
            Assert.Single(play.Room.Waiting());
            Assert.Contains("1 waiting for you", vm.PlayWords);
            Assert.StartsWith("OK", Wire("PLAY APPROVE all"));
            Assert.Contains("\"word\":\"Bright\"", Wire("PLAY RESULTS"));
            Assert.Contains("with the host", Post(phone, "api/play/say", $"{{\"token\":\"{kimToken}\",\"text\":\"Great talk!\"}}"));
            var host = TestApp.Pump(http.PostAsync("api/play/host", new StringContent("1234")));
            Assert.Equal(HttpStatusCode.OK, host.StatusCode);
            Assert.Contains("\"room\":\"" + play.Code + "\"", TestApp.Pump(host.Content.ReadAsStringAsync()));
            Assert.Equal(HttpStatusCode.Forbidden, TestApp.Pump(http.PostAsync("api/play/host", new StringContent("wrong"))).StatusCode);
            Assert.StartsWith("OK", Wire("PLAY SHOW join"));
            Assert.Equal(PlayBoardMode.Join, play.Wall);
            Assert.StartsWith("OK", Wire("PLAY SHOW leaderboard"));
            Assert.StartsWith("ERR", Wire("PLAY SHOW nothing"));
            Assert.StartsWith("OK", Wire("PLAY HIDE"));
            Assert.Equal(PlayBoardMode.Off, play.Wall);

            // The path and the board from the phones.
            Assert.StartsWith("OK", Wire("PLAY PATH OPEN"));
            Assert.Contains("\"ok\":true", Post(phone, "api/play/vote", $"{{\"token\":\"{samToken}\",\"option\":1}}"));
            Assert.StartsWith("OK", Wire("PLAY PATH CLOSE"));
            Assert.Equal("Up to the gallery", play.Room.Path.LastChoice);
            Assert.Equal("gallery", play.Room.Path.CurrentId);
            Assert.StartsWith("OK", Wire("PLAY DRAUGHTS"));
            Assert.Contains("\"ok\":true", Post(phone, "api/play/draughts", $"{{\"token\":\"{samToken}\",\"action\":\"seat\",\"side\":\"black\"}}"));
            Assert.Contains("that side is taken", Post(phone, "api/play/draughts", $"{{\"token\":\"{kimToken}\",\"action\":\"seat\",\"side\":\"black\"}}"));
            Assert.Contains("\"ok\":true", Post(phone, "api/play/draughts", $"{{\"token\":\"{samToken}\",\"action\":\"move\",\"from\":17,\"to\":26}}"));
            state = JsonDocument.Parse(Get(phone, $"api/play/state?token={samToken}&since=0")).RootElement;
            Assert.Equal("black", state.GetProperty("draughts").GetProperty("mine").GetString());
            Assert.Equal("white", state.GetProperty("draughts").GetProperty("turn").GetString());

            // The export lands in the hub's folder; a new code sends every phone back to the door.
            Assert.StartsWith("OK", Wire("PLAY EXPORT"));
            Assert.NotEmpty(Directory.GetFiles(services.Store.BaseDirectory, "play-export-*.json"));
            var oldCode = play.Code;
            Assert.StartsWith("OK", Wire("PLAY NEW"));
            Assert.NotEqual(oldCode, play.Code);
            Assert.Equal(0, play.Room.PlayerCount);
            Assert.Contains("\"known\":false", Get(phone, $"api/play/state?token={samToken}&since=0"));

            // AUDIENCE OFF closes the socket; ON opens it again on a port of the wire's choosing.
            Assert.StartsWith("OK", Wire("AUDIENCE OFF"));
            PumpUntil(() => !services.Control.AudienceListening);
            Assert.Equal("", play.JoinUrl);
            var again = FreePort();
            Assert.StartsWith("OK", Wire($"AUDIENCE ON {again}"));
            PumpUntil(() => services.Control.AudienceListening);
            Assert.Equal(again, vm.State.Control.AudiencePort);
            Assert.Contains($":{again}/", play.JoinUrl);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ADeskSendsPlayVerbsToTheHubItHearsAndIsTheRoomItselfWithNoneHeard()
    {
        var desk = TestApp.Boot("patterns-tests-desk-play-", dir => Settings(dir, s =>
        {
            s.Name = "Gala";
            s.Control.Enabled = false;
            s.Twin.AcceptCallers = false;
            s.Watchdog.BeaconListenPort = FreePort();
            s.Watchdog.BeaconPort = s.Watchdog.BeaconListenPort;
        }));
        TestApp.Booted? hub = null;
        try
        {
            var d = desk.Services;
            // No hub: the desk is the room.
            Assert.True(d.Actions.Execute(new ShowAction(ShowActionKind.PlayAdd, "", "choice Lunch? | Pizza | Salad"), ActionOrigin.Desk).Ok);
            Assert.True(d.Actions.Execute(new ShowAction(ShowActionKind.PlayOpen, "", "next"), ActionOrigin.Desk).Ok);
            Assert.Equal("Lunch?", d.Play.Room.Current!.Text);
            Assert.StartsWith("OK {", TestApp.Pump(new CommandRouter(d).ExecuteAsync(ControlProtocol.Parse("PLAY STATUS"))));

            var wire = FreePort();
            hub = TestApp.Boot("patterns-tests-hub2-", dir => Settings(dir, s =>
            {
                s.Name = "Gala";
                s.Control.Enabled = true;
                s.Control.HttpPort = FreePort();
                s.Control.TcpPort = wire;
                s.Twin.AcceptCallers = false;
                s.Watchdog.BeaconListenPort = FreePort();
                s.Watchdog.BeaconPort = s.Watchdog.BeaconListenPort;
            }), NodeKind.Arcade);
            var h = hub.Services;
            PumpUntil(() =>
            {
                try { using var probe = new TcpClient(); probe.Connect(IPAddress.Loopback, wire); return true; }
                catch (SocketException) { return false; }
            });
            d.Beacon.Hear(h.Beacon.Build() with { Machine = "HUB-PC" }, new IPEndPoint(IPAddress.Loopback, 9700));
            d.Nodes.Poll();
            Assert.Single(d.Nodes.Arcades());

            var sent = d.Actions.Execute(new ShowAction(ShowActionKind.PlayShow, "", "join"), ActionOrigin.Desk);
            Assert.True(sent.Ok, sent.Message);
            Assert.Contains("HUB-PC", sent.Message);
            PumpUntil(() => h.Play.Wall == PlayBoardMode.Join);
            var status = TestApp.Pump(new CommandRouter(d).ExecuteAsync(ControlProtocol.Parse("PLAY STATUS")));
            Assert.StartsWith("OK [", status);
            Assert.Contains("\"node\":\"HUB-PC\"", status);
            Assert.Contains("\"wall\":\"join\"", status);
            // A cue on the desk adds a question to the hub's room.
            var stack = CueStacks.Caller(desk.Vm.State);
            var cue = new RunCueConfig { Number = "01", Name = "Poll" };
            cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.PlayAdd, Value = "scale How was the morning? | scale=1-10" });
            stack.Cues.Add(cue);
            Assert.True(d.Actions.FireCue(cue, ActionOrigin.Desk).Ok);
            PumpUntil(() => h.Play.Room.Questions.Count == 1);
            Assert.Equal(PlayQuestionKind.Scale, h.Play.Room.Questions[0].Kind);
        }
        finally
        {
            hub?.Dispose();
            desk.Dispose();
        }
    }
}
