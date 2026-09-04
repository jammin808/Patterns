using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using System.Text;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.App.Views.Sections;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Break music against the live app with a fake Spotify: the reconciler, every failure as a
/// sentence, the verbs, the cue rules, STOP ALL, the wire and the UI. No socket is ever opened
/// except by the loopback test, which stays on 127.0.0.1.
/// </summary>
public class SpotifyAppTests
{
    /// <summary>Records every request; answers the token exchange itself and everything else from <see cref="Answer"/> (null = 204).</summary>
    private sealed class FakeSpotify
    {
        public const string TokenJson = "{\"access_token\":\"A\",\"expires_in\":3600}";
        public readonly List<SpotifyRequest> Requests = new();
        public Func<SpotifyRequest, SpotifyReply?> Answer = _ => null;
        public bool Throw;

        public Task<SpotifyReply> Send(SpotifyRequest r, CancellationToken ct)
        {
            Requests.Add(r);
            if (Throw) throw new HttpRequestException("no network");
            if (r.Url == SpotifyEndpoints.TokenUrl) return Task.FromResult(new SpotifyReply(200, TokenJson));
            return Task.FromResult(Answer(r) ?? new SpotifyReply(204, ""));
        }

        public IEnumerable<SpotifyRequest> Of(string fragment) => Requests.Where(r => r.Url.Contains(fragment, StringComparison.Ordinal));
        public int Count(string fragment) => Of(fragment).Count();
    }

    /// <summary>A booted app with the fake transport, a hand-driven clock and (optionally) a sign-in on disk.</summary>
    private sealed class Rig : IDisposable
    {
        public readonly TestApp.Booted B;
        public readonly FakeSpotify Fake = new();
        public DateTime Now = new(2026, 9, 4, 19, 0, 0, DateTimeKind.Utc);

        public Rig(bool enabled, bool connected)
        {
            B = TestApp.Boot();
            B.Services.Spotify.Transport = Fake.Send;
            B.Services.Spotify.NowUtc = () => Now;
            if (connected)
            {
                B.Services.SpotifyCredentials.Write(new SpotifyCredentials("cid", "refresh", "ben", Now));
                B.Services.Spotify.ReloadCredentials();
            }
            B.Vm.State.Spotify.Enabled = enabled;
        }

        public AppServices Services => B.Services;
        public MainViewModel Vm => B.Vm;
        public SpotifyService Spotify => B.Services.Spotify;
        public SpotifyConfig Music => B.Vm.State.Spotify;

        public SpotifyItemConfig Item(string name, string uri, bool shuffle = false)
        {
            var item = new SpotifyItemConfig { Name = name, Uri = uri, Shuffle = shuffle };
            B.Vm.State.Spotify.Items.Add(item);
            return item;
        }

        public ActionResult Execute(ShowActionKind kind, string target = "", string value = "")
            => B.Services.Actions.Execute(kind, ActionOrigin.Desk, target, value);

        public void Poll(int times = 1)
        {
            for (var i = 0; i < times; i++) B.Services.Spotify.Poll();
        }

        public void Advance(double seconds) => Now = Now.AddSeconds(seconds);

        public void Dispose() => B.Dispose();
    }

    private static LookConfig SaveLook(MainViewModel vm, string name, PatternKind kind)
    {
        vm.ActivePattern.Kind = kind;
        vm.NewLookName = name;
        vm.SaveLookCommand.Execute(null);
        return LookService.Find(vm.State, name)!;
    }

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

    // ---- the reconciler ---------------------------------------------------------------

    [AvaloniaFact]
    public void NothingHappensUntilItIsSwitchedOnConnectedAndPrimary()
    {
        using var r = new Rig(enabled: false, connected: false);
        r.Poll(3);
        Assert.Empty(r.Fake.Requests);
        Assert.Equal("Off.", r.Spotify.Status);

        r.Music.Enabled = true;
        r.Poll(3);
        Assert.Empty(r.Fake.Requests);
        Assert.Equal("Add your Spotify Client ID on the Audio page.", r.Spotify.Status);

        r.Spotify.ClientId = "cid";
        r.Poll(3);
        Assert.Empty(r.Fake.Requests);
        Assert.Equal("Not connected — press CONNECT on the Audio page.", r.Spotify.Status);
        Assert.Equal("cid", r.Services.SpotifyCredentials.Read().ClientId); // the sidecar, not the show

        r.Services.SpotifyCredentials.Write(new SpotifyCredentials("cid", "refresh", "ben", r.Now));
        r.Spotify.ReloadCredentials();
        r.Services.PrimaryInstanceOverride = () => false;
        r.Poll(3);
        Assert.Empty(r.Fake.Requests);
        Assert.Equal("Break music is run by the first Patterns window.", r.Spotify.Status);

        r.Services.PrimaryInstanceOverride = null;
        r.Poll();
        Assert.Equal(SpotifyEndpoints.TokenUrl, Assert.Single(r.Fake.Requests).Url); // now, and only now, the network
    }

    [AvaloniaFact]
    public void TheFirstTickNeverPausesAnybodysSpotify()
    {
        using var r = new Rig(enabled: true, connected: true);
        Assert.False(r.Music.Playing);
        r.Poll(10);
        Assert.NotEmpty(r.Fake.Requests);
        Assert.Empty(r.Fake.Of("/me/player/pause"));
        Assert.Empty(r.Fake.Of("/me/player/play"));
        Assert.All(r.Fake.Requests, q => Assert.True(q.Url == SpotifyEndpoints.TokenUrl || q.Method == "GET", q.Url));
        Assert.Equal("Ready — Spotify will use whichever device is active.", r.Spotify.Status);
        Assert.True(r.Spotify.Connected);
    }

    [AvaloniaFact]
    public void PlayIsIssuedOnceAndNotRepeatedWhileTheIntentHolds()
    {
        using var r = new Rig(enabled: true, connected: true);
        r.Item("Interval bed", "spotify:playlist:X");

        var result = r.Execute(ShowActionKind.SpotifyPlay, "1");
        Assert.Equal(ActionStatus.Requested, result.Status);
        Assert.True(r.Music.Playing);
        r.Poll(3);
        var play = Assert.Single(r.Fake.Of("/me/player/play"));
        Assert.Equal("PUT", play.Method);
        Assert.Equal("{\"context_uri\":\"spotify:playlist:X\"}", play.Body);
        Assert.Equal("A", play.Bearer);
        Assert.Single(r.Fake.Of("/me/player/volume")); // the level is asserted once after the play

        r.Execute(ShowActionKind.SpotifyPlay, "1");
        r.Poll(3);
        Assert.Single(r.Fake.Of("/me/player/play"));

        r.Execute(ShowActionKind.SpotifyPause); // PokeNow: the pause goes out on this turn
        Assert.Single(r.Fake.Of("/me/player/pause"));
        Assert.False(r.Music.Playing);

        r.Execute(ShowActionKind.SpotifyPlay, "1");
        r.Poll(2);
        Assert.Equal(2, r.Fake.Count("/me/player/play"));
    }

    [AvaloniaFact]
    public void AShuffleItemShufflesBeforeItPlaysAndAnOrderedOneDoesNot()
    {
        using var r = new Rig(enabled: true, connected: true);
        r.Item("Beds", "spotify:playlist:BEDS", shuffle: true);
        r.Item("Running order", "spotify:album:ORDER");

        r.Execute(ShowActionKind.SpotifyPlay, "Beds");
        r.Poll(2);
        var urls = r.Fake.Requests.Select(q => q.Url).ToList();
        var shuffle = urls.FindIndex(u => u.Contains("/me/player/shuffle?state=true"));
        var play = urls.FindIndex(u => u.Contains("/me/player/play"));
        Assert.True(shuffle >= 0 && play > shuffle, string.Join("\n", urls));

        r.Execute(ShowActionKind.SpotifyPause);
        r.Fake.Requests.Clear();
        r.Execute(ShowActionKind.SpotifyPlay, "Running order");
        r.Poll(2);
        Assert.Empty(r.Fake.Of("/me/player/shuffle"));
        Assert.Contains("spotify:album:ORDER", Assert.Single(r.Fake.Of("/me/player/play")).Body);
    }

    [AvaloniaFact]
    public void AFailedPauseKeepsRetryingUntilItLands()
    {
        using var r = new Rig(enabled: true, connected: true);
        r.Item("Interval bed", "spotify:playlist:X");
        r.Execute(ShowActionKind.SpotifyPlay, "1");
        r.Poll(2);

        var pauses = 0;
        r.Fake.Answer = q => q.Url.Contains("/me/player/pause") ? new SpotifyReply(++pauses <= 2 ? 500 : 204, "") : null;
        r.Execute(ShowActionKind.SpotifyPause);
        Assert.Equal(1, r.Fake.Count("/me/player/pause"));
        Assert.Contains("could not pause", r.Spotify.CommandFailure);
        Assert.False(r.Music.Playing); // the intent stands whatever Spotify said

        r.Poll();                       // inside the 2 s backoff: nothing
        Assert.Equal(1, r.Fake.Count("/me/player/pause"));
        r.Advance(3);
        r.Poll();                       // second attempt → 500 → 4 s backoff
        Assert.Equal(2, r.Fake.Count("/me/player/pause"));
        r.Advance(3);
        r.Poll();
        Assert.Equal(2, r.Fake.Count("/me/player/pause"));
        r.Advance(2);
        r.Poll();                       // third attempt lands
        Assert.Equal(3, r.Fake.Count("/me/player/pause"));
        Assert.Equal("", r.Spotify.CommandFailure);
        r.Poll(3);
        Assert.Equal(3, r.Fake.Count("/me/player/pause")); // applied: no more
    }

    [AvaloniaFact]
    public void EveryFailureIsAStatusLineAndNeverAnException()
    {
        using var r = new Rig(enabled: true, connected: true);
        r.Item("Interval bed", "spotify:playlist:X");
        r.Poll(); // the token
        r.Execute(ShowActionKind.SpotifyPlay, "1");

        // 429: transient, self-healing, never a command failure, and silence until Retry-After.
        r.Fake.Answer = q => q.Url.Contains("/me/player/play") ? new SpotifyReply(429, "", 7) : null;
        r.Poll();
        Assert.Equal("Spotify is busy (rate limited) — retrying in 7s.", r.Spotify.Status);
        Assert.Equal("", r.Spotify.CommandFailure);
        Assert.True(r.Music.Playing);
        var sent = r.Fake.Requests.Count;
        r.Poll(2);
        Assert.Equal(sent, r.Fake.Requests.Count);
        r.Advance(120);

        // 403 Premium: a sentence and a long block, never a retry storm.
        r.Fake.Answer = q => q.Url.Contains("/me/player/play") ? new SpotifyReply(403, "{\"error\":{\"reason\":\"PREMIUM_REQUIRED\"}}") : null;
        r.Poll();
        Assert.Equal("Spotify Premium is required to control playback.", r.Spotify.Status);
        Assert.Contains("could not play", r.Spotify.CommandFailure);
        Assert.True(r.Music.Playing);
        sent = r.Fake.Requests.Count;
        r.Advance(30);
        r.Poll(2);
        Assert.Equal(sent, r.Fake.Requests.Count);
        r.Advance(120);

        // 404 no device: the device is re-resolved next time, after a backoff.
        r.Fake.Answer = q => q.Url.Contains("/me/player/play") ? new SpotifyReply(404, "{\"error\":{\"reason\":\"NO_ACTIVE_DEVICE\"}}") : null;
        r.Poll();
        Assert.Equal("No Spotify device — open Spotify on the desk machine and press play once.", r.Spotify.Status);
        sent = r.Fake.Requests.Count;
        r.Poll();
        Assert.Equal(sent, r.Fake.Requests.Count);
        r.Advance(120);

        // A transport that throws is "never reached Spotify".
        r.Fake.Throw = true;
        r.Poll();
        Assert.Equal("Spotify is unavailable — check the network.", r.Spotify.Status);
        Assert.True(r.Music.Playing);
        r.Fake.Throw = false;
        r.Advance(120);

        // 401 twice: one refresh, then the sign-in is dropped and the reason stays on the line.
        r.Fake.Answer = q => q.Url.Contains("/me/player/play") ? new SpotifyReply(401, "") : null;
        r.Poll();
        Assert.Equal("Spotify sign-in expired — press CONNECT on the Audio page.", r.Spotify.Status);
        Assert.True(r.Services.SpotifyCredentials.Read().IsConnected);
        r.Advance(120);
        r.Poll(); // the refresh (succeeds)
        r.Poll(); // the play → 401 again → signed out
        Assert.False(r.Services.SpotifyCredentials.Read().IsConnected);
        Assert.False(r.Spotify.Connected);
        r.Poll(3);
        Assert.Equal("Spotify sign-in expired — press CONNECT on the Audio page.", r.Spotify.Status);
        Assert.True(r.Music.Playing); // the intent is the operator's; only Spotify's answer changed
    }

    [AvaloniaFact]
    public void AFailedVolumeOrReadBackNeverFlipsACueRow()
    {
        using var r = new Rig(enabled: true, connected: true);
        r.Item("Interval bed", "spotify:playlist:X");
        r.Execute(ShowActionKind.SpotifyPlay, "1");
        r.Poll(2); // token, play

        r.Fake.Answer = q => q.Url.Contains("/me/player/volume") ? new SpotifyReply(500, "") : null;
        r.Poll();
        Assert.Single(r.Fake.Of("/me/player/volume"));
        Assert.Equal("", r.Spotify.CommandFailure);
        Assert.Equal("Spotify service error (500) — retrying.", r.Spotify.Status);

        r.Advance(120);
        r.Fake.Answer = q => q.Method == "GET" && q.Url.EndsWith("/me/player") ? new SpotifyReply(500, "") : null;
        r.Poll(); // the level lands this time
        r.Poll(); // the read-back fails
        Assert.Contains(r.Fake.Requests, q => q.Method == "GET" && q.Url.EndsWith("/me/player"));
        Assert.Equal("", r.Spotify.CommandFailure);
        Assert.Contains("service error (500)", r.Spotify.Status);
    }

    [AvaloniaFact]
    public void TheMusicDucksUnderAnAnnouncementAndComesBack()
    {
        using var r = new Rig(enabled: true, connected: true);
        r.Item("Interval bed", "spotify:playlist:X");
        r.Music.LevelPct = 60;
        r.Vm.State.Stingers.DuckPct = 20;
        r.Execute(ShowActionKind.SpotifyPlay, "1");
        r.Poll(3);
        Assert.Contains(r.Fake.Of("/me/player/volume"), q => q.Url.Contains("volume_percent=60"));

        r.Services.MusicDuckSource = () => true;
        r.Advance(1);
        r.Poll();
        Assert.Contains("volume_percent=12", r.Fake.Of("/me/player/volume").Last().Url);

        r.Services.MusicDuckSource = () => false;
        r.Advance(1);
        r.Poll();
        Assert.Contains("volume_percent=60", r.Fake.Of("/me/player/volume").Last().Url);
        Assert.Equal(3, r.Fake.Count("/me/player/volume"));

        r.Advance(1);
        r.Poll(3);
        Assert.Equal(3, r.Fake.Count("/me/player/volume")); // nothing changed: nothing sent
    }

    [AvaloniaFact]
    public void TheLevelIsRateLimitedSoASliderDragIsNotAStorm()
    {
        using var r = new Rig(enabled: true, connected: true);
        r.Item("Interval bed", "spotify:playlist:X");
        r.Execute(ShowActionKind.SpotifyPlay, "1");
        r.Poll(3);
        Assert.Equal(1, r.Fake.Count("/me/player/volume"));

        r.Music.LevelPct = 61;
        r.Poll();
        r.Music.LevelPct = 62;
        r.Poll();
        r.Music.LevelPct = 63;
        r.Poll();
        Assert.Equal(1, r.Fake.Count("/me/player/volume")); // the drag inside 250 ms sent nothing

        r.Advance(1);
        r.Poll();
        Assert.Equal(2, r.Fake.Count("/me/player/volume"));
        Assert.Contains("volume_percent=63", r.Fake.Of("/me/player/volume").Last().Url); // the final value, never a stale one
    }

    [AvaloniaFact]
    public void SomeoneElsePausingInSpotifyIsAdoptedAndNotFoughtOver()
    {
        using var r = new Rig(enabled: true, connected: true);
        r.Item("Interval bed", "spotify:playlist:X");
        r.Execute(ShowActionKind.SpotifyPlay, "1");
        r.Poll(3);
        Assert.True(r.Music.Playing);

        r.Fake.Answer = q => q.Method == "GET" && q.Url.EndsWith("/me/player")
            ? new SpotifyReply(200, "{\"is_playing\":false,\"item\":{\"name\":\"Kerala\",\"artists\":[{\"name\":\"Bonobo\"}]},\"device\":{\"id\":\"d1\",\"name\":\"Desk\",\"volume_percent\":60}}")
            : null;
        r.Advance(4);
        r.Poll();
        Assert.False(r.Music.Playing);
        Assert.Equal("Paused.", r.Spotify.Status);
        Assert.Equal("Bonobo · Kerala", r.Spotify.NowPlaying);
        Assert.Equal("Desk", r.Spotify.DeviceLabel);

        var plays = r.Fake.Count("/me/player/play");
        r.Advance(1);
        r.Poll(3);
        Assert.Equal(plays, r.Fake.Count("/me/player/play"));
        Assert.Empty(r.Fake.Of("/me/player/pause"));
    }

    [Fact]
    public async Task TheLoopbackCallbackServesOneRequestOnLoopbackOnly()
    {
        using var callback = LoopbackCallback.Start(out var uri);
        Assert.NotNull(callback);
        Assert.StartsWith("http://127.0.0.1:", uri);
        Assert.EndsWith("/callback", uri);
        Assert.Contains(callback!.Port, LoopbackCallback.Ports);
        Assert.Equal(IPAddress.Loopback, ((IPEndPoint)callback.LocalEndpoint!).Address);

        var wait = callback.WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
        using var http = new HttpClient(new HttpClientHandler { UseProxy = false });
        var response = await http.GetAsync(uri + "?code=abc&state=xyz");
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Contains("close this tab", await response.Content.ReadAsStringAsync());
        var query = await wait;
        Assert.Equal("abc", query["code"]);
        Assert.Equal("xyz", query["state"]);

        callback.Dispose();
        using var probe = new TcpClient();
        await Assert.ThrowsAnyAsync<SocketException>(() => probe.ConnectAsync(IPAddress.Loopback, callback.Port));
    }

    // ---- the verbs and the cue rules -------------------------------------------------

    [AvaloniaTheory]
    [InlineData(ShowActionKind.SpotifyPlay, "1", "")]
    [InlineData(ShowActionKind.SpotifyPlay, "", "")]
    [InlineData(ShowActionKind.SpotifyPause, "", "")]
    [InlineData(ShowActionKind.SpotifyNext, "", "")]
    [InlineData(ShowActionKind.SpotifyVolume, "", "40")]
    public void BreakMusicIsANoOpNotARefusalWhenItIsSwitchedOff(ShowActionKind kind, string target, string value)
    {
        using var r = new Rig(enabled: false, connected: true);
        r.Item("Interval bed", "spotify:playlist:X");
        var result = r.Execute(kind, target, value);
        Assert.Equal(ActionStatus.Done, result.Status);
        r.Poll(3);
        Assert.Empty(r.Fake.Requests);
        Assert.False(r.Music.Playing);
        var entry = r.Services.Journal.Tail(1).Single();
        Assert.Equal(kind.ToString(), entry.Kind);
        Assert.Equal(ActionOrigin.Desk.Label, entry.Origin);
        Assert.Equal("Done", entry.Outcome);
    }

    [AvaloniaFact]
    public void ACueThatCannotReachSpotifyStillFinishesItsOtherActions()
    {
        using var r = new Rig(enabled: true, connected: true);
        r.Fake.Throw = true;
        r.Item("Interval bed", "spotify:playlist:X");
        var vm = r.Vm;
        vm.IsSandboxActive = false;
        var look = SaveLook(vm, "A", PatternKind.ColorBars);
        vm.ActivePattern.Kind = PatternKind.Grid;
        var stack = CueStacks.Caller(vm.State);
        var cue = new RunCueConfig { Number = "01.010", Name = "Doors" };
        cue.Actions.Add(new CueActionConfig { Kind = CueActionKind.BlackoutOff });
        cue.Actions.Add(new CueActionConfig { Kind = CueActionKind.SpotifyPlay, Target = "1" });
        cue.Actions.Add(new CueActionConfig { Kind = CueActionKind.ApplyLook, Target = look.Id });
        stack.Cues.Add(cue);
        Dispatcher.UIThread.RunJobs();

        var svc = r.Services.CueStack;
        svc.SetArmed(true, ActionOrigin.Desk);
        svc.Standby(cue.Id);
        var result = svc.Go(ActionOrigin.Desk, nowUtc: r.Now);
        Assert.True(result.Ok, result.Message);
        Assert.Equal(ActionStatus.Requested, result.Status);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(PatternKind.ColorBars, vm.State.Pattern.Kind); // the look landed after the music
        Assert.False(vm.State.Blackout);
        Assert.True(r.Music.Playing);
        var row = svc.History.First();
        Assert.Equal(CueOutcome.Requested, row.Outcome);
        Assert.Equal((3, 3), (row.ActionsDone, row.ActionsTotal));

        r.Poll(2); // the token cannot even be renewed: that counts against the pending play
        Assert.Contains("could not play", r.Services.Spotify.CommandFailure);
        svc.Poll(r.Now.AddSeconds(13));
        Assert.Equal(CueOutcome.FailedLate, svc.History.First().Outcome);
        Assert.Contains("later:", svc.History.First().Detail);
        Assert.Equal(PatternKind.ColorBars, vm.State.Pattern.Kind); // nothing undone
    }

    [AvaloniaFact]
    public void ACueWithBreakMusicStillRunsWhenBreakMusicIsSwitchedOff()
    {
        using var r = new Rig(enabled: false, connected: true);
        r.Item("Interval bed", "spotify:playlist:X");
        var vm = r.Vm;
        vm.IsSandboxActive = false;
        var look = SaveLook(vm, "A", PatternKind.ColorBars);
        vm.ActivePattern.Kind = PatternKind.Grid;
        var stack = CueStacks.Caller(vm.State);
        var cue = new RunCueConfig { Number = "01.010", Name = "Doors" };
        cue.Actions.Add(new CueActionConfig { Kind = CueActionKind.SpotifyPlay, Target = "1" });
        cue.Actions.Add(new CueActionConfig { Kind = CueActionKind.ApplyLook, Target = look.Id });
        stack.Cues.Add(cue);
        Dispatcher.UIThread.RunJobs();

        var report = CueValidator.Validate(vm.State, stack, r.Services.ValidationContext);
        Assert.False(report.IsBroken(cue.Id));
        Assert.True(report.Warnings.ContainsKey(cue.Id));

        var svc = r.Services.CueStack;
        svc.SetArmed(true, ActionOrigin.Desk);
        svc.Standby(cue.Id);
        var result = svc.Go(ActionOrigin.Desk, nowUtc: r.Now);
        Assert.True(result.Ok, result.Message);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(PatternKind.ColorBars, vm.State.Pattern.Kind);
        Assert.False(r.Music.Playing);
        Assert.Empty(r.Fake.Requests);
    }

    [AvaloniaFact]
    public void EveryMusicVerbIsRefusedReadablyAndJournaledWithItsOrigin()
    {
        using var r = new Rig(enabled: true, connected: true);
        r.Item("Bad", "junk");

        var unknown = r.Execute(ShowActionKind.SpotifyPlay, "x");
        Assert.Equal(ActionStatus.Refused, unknown.Status);
        Assert.Equal("No break music 'x'.", unknown.Message);

        var junk = r.Execute(ShowActionKind.SpotifyPlay, "Bad");
        Assert.Equal(ActionStatus.Refused, junk.Status);
        Assert.Equal("'Bad' has no valid Spotify link.", junk.Message);

        var loud = r.Execute(ShowActionKind.SpotifyVolume, "", "loud");
        Assert.Equal(ActionStatus.Refused, loud.Status);
        Assert.Equal("Break music level needs a number from 0 to 100.", loud.Message);

        Assert.False(r.Music.Playing);
        var tail = r.Services.Journal.Tail(3);
        Assert.Equal(3, tail.Count);
        Assert.All(tail, e =>
        {
            Assert.Equal(ActionOrigin.Desk.Label, e.Origin);
            Assert.Equal("Refused", e.Outcome);
        });
    }

    [AvaloniaFact]
    public void TheHardSetIsExactlyTheRefusedSet()
    {
        using var r = new Rig(enabled: true, connected: true);
        var good = r.Item("Interval bed", "spotify:playlist:X");
        var junk = r.Item("Bad", "junk");
        r.Poll(); // connected, so MusicReady is true until the last case disconnects
        var state = r.Vm.State;

        var cases = new (string Name, Action Arrange, CueActionConfig Action)[]
        {
            ("unknown target", () => { }, new CueActionConfig { Kind = CueActionKind.SpotifyPlay, Target = "nope" }),
            ("junk link", () => { }, new CueActionConfig { Kind = CueActionKind.SpotifyPlay, Target = junk.Id }),
            ("level 120", () => { }, new CueActionConfig { Kind = CueActionKind.SpotifyVolume, Value = "120" }),
            ("level words", () => { }, new CueActionConfig { Kind = CueActionKind.SpotifyVolume, Value = "loud" }),
            ("play entry", () => { }, new CueActionConfig { Kind = CueActionKind.SpotifyPlay, Target = good.Id }),
            ("play by number", () => { }, new CueActionConfig { Kind = CueActionKind.SpotifyPlay, Target = "1" }),
            ("resume", () => { }, new CueActionConfig { Kind = CueActionKind.SpotifyPlay }),
            ("pause", () => { }, new CueActionConfig { Kind = CueActionKind.SpotifyPause }),
            ("skip", () => { }, new CueActionConfig { Kind = CueActionKind.SpotifyNext }),
            ("level 40", () => { }, new CueActionConfig { Kind = CueActionKind.SpotifyVolume, Value = "40" }),
            ("no device chosen", () => state.Spotify.DeviceName = "", new CueActionConfig { Kind = CueActionKind.SpotifyPlay, Target = good.Id }),
            ("off, unknown target", () => state.Spotify.Enabled = false, new CueActionConfig { Kind = CueActionKind.SpotifyPlay, Target = "nope" }),
            ("off, level 120", () => state.Spotify.Enabled = false, new CueActionConfig { Kind = CueActionKind.SpotifyVolume, Value = "120" }),
            ("off, good entry", () => state.Spotify.Enabled = false, new CueActionConfig { Kind = CueActionKind.SpotifyPlay, Target = good.Id }),
            ("on, not connected", () => { state.Spotify.Enabled = true; r.Spotify.Disconnect(); }, new CueActionConfig { Kind = CueActionKind.SpotifyPlay, Target = good.Id }),
        };

        foreach (var (name, arrange, action) in cases)
        {
            arrange();
            var cue = new RunCueConfig { Number = "1", Name = name };
            cue.Actions.Add(action);
            var broken = CueValidator.ValidateOne(state, cue, r.Services.ValidationContext).BrokenCount > 0;

            var showAction = action.Kind switch
            {
                CueActionKind.SpotifyPlay => new ShowAction(ShowActionKind.SpotifyPlay, action.Target),
                CueActionKind.SpotifyPause => new ShowAction(ShowActionKind.SpotifyPause),
                CueActionKind.SpotifyNext => new ShowAction(ShowActionKind.SpotifyNext),
                _ => new ShowAction(ShowActionKind.SpotifyVolume, "", action.Value),
            };
            var result = r.Services.Actions.Execute(showAction, ActionOrigin.Desk);
            var refused = result.Status == ActionStatus.Refused;
            Assert.True(broken == refused, $"{name}: broken={broken} refused={refused} ({result.Message})");
            Assert.NotEqual(ActionStatus.Failed, result.Status);
        }
    }

    [AvaloniaFact]
    public void StopAllPausesBreakMusicAndNothingElseChanges()
    {
        using var r = new Rig(enabled: true, connected: true);
        r.Item("Interval bed", "spotify:playlist:X");
        var b = r.B;
        r.Execute(ShowActionKind.SpotifyPlay, "1");
        r.Poll(2);
        b.Services.Actions.Execute(ShowActionKind.OutputsOn, ActionOrigin.Desk);
        Dispatcher.UIThread.RunJobs();
        b.Vm.State.Stream.Active = true;
        b.Services.Actions.Execute(ShowActionKind.BlackoutOn, ActionOrigin.Desk);
        var pauses = r.Fake.Count("/me/player/pause");

        b.Services.Actions.Execute(ShowActionKind.StopAll, ActionOrigin.Desk);
        Assert.False(r.Music.Playing);
        Assert.Equal(pauses + 1, r.Fake.Count("/me/player/pause")); // on this turn, not a poll later
        Assert.True(b.Vm.State.Stream.Active);
        Assert.True(b.Services.Outputs.IsLive);
        Assert.True(b.Vm.State.Blackout);

        // Esc twice on the Run surface is the same thing; once is only a prompt.
        b.Vm.IsRunLayout = true;
        r.Execute(ShowActionKind.SpotifyPlay, "1");
        r.Poll();
        b.Vm.Run.EscapePressed();
        Assert.True(r.Music.Playing);
        Assert.Contains("Esc again", b.Vm.StatusMessage);
        Assert.Contains("break music", b.Vm.StatusMessage);
        b.Vm.Run.EscapePressed();
        Assert.False(r.Music.Playing);
    }

    [AvaloniaFact]
    public void BreakMusicNeverTouchesTheSandboxTheAirLabelOrTheBlackout()
    {
        using var r = new Rig(enabled: true, connected: true);
        r.Item("Interval bed", "spotify:playlist:X");
        var b = r.B;
        b.Vm.IsSandboxActive = true;
        b.Vm.State.Pattern.Kind = PatternKind.Grid;
        b.Services.AirLabel = "Walk-in";
        b.Vm.State.Blackout = true;
        Dispatcher.UIThread.RunJobs();
        var onAir = b.Services.Bus.Current.State.Pattern.Kind;

        Assert.Equal(ActionStatus.Requested, r.Execute(ShowActionKind.SpotifyPlay, "1").Status);
        r.Poll(3);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal("Walk-in", b.Services.AirLabel);
        Assert.Equal(onAir, b.Services.Bus.Current.State.Pattern.Kind);
        Assert.Equal(PatternKind.Grid, b.Vm.State.Pattern.Kind);
        Assert.True(b.Vm.State.Blackout);
        Assert.True(b.Vm.IsSandboxActive);
        Assert.True(r.Music.Playing);
        Assert.Single(r.Fake.Of("/me/player/play"));
    }

    [AvaloniaFact]
    public void AVideoStingerMayShareACueWithBreakMusic()
    {
        using var r = new Rig(enabled: true, connected: true);
        var clip = Path.Combine(Path.GetTempPath(), $"{Guid.NewGuid():N}-doors.mp4");
        File.WriteAllBytes(clip, new byte[] { 0, 0, 0, 1 });
        try
        {
            r.Services.ValidationVideoOverride = () => true; // headless: no libVLC
            r.Item("Interval bed", "spotify:playlist:X");
            r.Poll(); // the token: MusicReady
            var vm = r.Vm;
            vm.State.Pattern.Kind = PatternKind.Grid;
            var sting = new StingerItemConfig { Path = clip, Name = "Doors sting" };
            vm.State.Stingers.Items.Add(sting);
            var stack = CueStacks.Caller(vm.State);
            var cue = new RunCueConfig { Number = "1", Name = "Doors" };
            cue.Actions.Add(new CueActionConfig { Kind = CueActionKind.StingerFire, Target = sting.Id });
            cue.Actions.Add(new CueActionConfig { Kind = CueActionKind.SpotifyPlay, Target = "1" });
            stack.Cues.Add(cue);
            Dispatcher.UIThread.RunJobs();

            var report = CueValidator.Validate(vm.State, stack, r.Services.ValidationContext);
            Assert.Equal(0, report.BrokenCount);

            var result = r.Services.Actions.Execute(new ShowAction(ShowActionKind.CueFire, cue.Id), ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();
            Assert.True(result.Ok, result.Message);
            Assert.True(r.Services.Stingers.ClipActive);
            Assert.True(r.Music.Playing);
            r.Poll(2);
            Assert.Single(r.Fake.Of("/me/player/play"));
        }
        finally
        {
            File.Delete(clip);
        }
    }

    // ---- the wire -------------------------------------------------------------------

    [AvaloniaFact]
    public void RemoteAndCompanionDriveBreakMusicAndTheStateJsonShowsIt()
    {
        var b = TestApp.Boot();
        try
        {
            b.Services.Spotify.Transport = (_, _) => Task.FromResult(new SpotifyReply(204, "")); // never reached: no Client ID
            b.Vm.State.Spotify.Enabled = true;
            var item = new SpotifyItemConfig { Name = "Interval bed", Uri = "spotify:playlist:X" };
            b.Vm.State.Spotify.Items.Add(item);
            b.Vm.State.Control.HttpPort = FreePort();
            b.Vm.State.Control.TcpPort = FreePort();
            Dispatcher.UIThread.RunJobs();

            using var client = new TcpClient();
            Pump(client.ConnectAsync(IPAddress.Loopback, b.Vm.State.Control.TcpPort));
            using var stream = client.GetStream();
            using var reader = new StreamReader(stream, Encoding.UTF8);
            Assert.StartsWith("STATE {", Pump(reader.ReadLineAsync()));

            void Send(string line) => stream.Write(Encoding.UTF8.GetBytes(line + "\n"));
            string Response()
            {
                while (true)
                {
                    var line = Pump(reader.ReadLineAsync());
                    Assert.NotNull(line);
                    if (!line!.StartsWith("STATE ")) return line;
                }
            }

            Send("MUSIC PLAY 9");
            Assert.StartsWith("ERR", Response());
            Send("MUSIC PLAY 1");
            Assert.Equal("OK", Response());
            Dispatcher.UIThread.RunJobs();
            Assert.True(b.Vm.State.Spotify.Playing);
            Assert.Equal(item.Id, b.Vm.State.Spotify.PlayingId);
            Send("MUSIC VOL 40");
            Assert.Equal("OK", Response());
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(40, b.Vm.State.Spotify.LevelPct);
            Send("MUSIC VOL 120");
            Assert.Contains("0 to 100", Response());
            Send("SPOTIFY PAUSE");
            Assert.Equal("OK", Response());
            Dispatcher.UIThread.RunJobs();
            Assert.False(b.Vm.State.Spotify.Playing);
            Send("MUSIC");
            Assert.StartsWith("ERR", Response());

            Send("STATUS");
            var status = Response();
            Assert.StartsWith("OK {", status);
            Assert.Contains("\"music\":{", status);
            Assert.Contains("\"on\":true", status);
            Assert.Contains("\"playing\":false", status);
            Assert.Contains("\"level\":40", status);
            Assert.Contains("\"items\":[{\"n\":1,\"name\":\"Interval bed\"}]", status);

            // Over HTTP a music verb needs no client header; a cue verb still does.
            using var http = new HttpClient(new HttpClientHandler { UseProxy = false }) { BaseAddress = new Uri($"http://127.0.0.1:{b.Vm.State.Control.HttpPort}/") };
            var plain = Pump(http.PostAsync("/api/cmd", new StringContent("MUSIC PLAY 1")));
            Assert.Contains("\"ok\":true", Pump(plain.Content.ReadAsStringAsync()));
            Dispatcher.UIThread.RunJobs();
            Assert.True(b.Vm.State.Spotify.Playing);
            var guarded = Pump(http.PostAsync("/api/cmd", new StringContent("STOPALL")));
            Assert.Contains("header required", Pump(guarded.Content.ReadAsStringAsync()));
            Assert.True(b.Vm.State.Spotify.Playing);
        }
        finally
        {
            b.Dispose();
        }
    }

    // ---- the UI ---------------------------------------------------------------------

    [AvaloniaFact]
    public void TheAudioPageAndTheShowPanelRenderTheBreakMusicBlocks()
    {
        var b = TestApp.Boot();
        try
        {
            b.Vm.State.Spotify.Enabled = true;
            b.Vm.State.Spotify.Items.Add(new SpotifyItemConfig { Name = "Interval bed", Uri = "spotify:playlist:X" });
            b.Vm.State.Spotify.Items.Add(new SpotifyItemConfig { Uri = "spotify:track:Y", Shuffle = true });
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("http://127.0.0.1:8724/callback", b.Vm.SpotifyRedirectUris);
            Assert.Equal(3, b.Vm.SpotifyRedirectUris.Split('\n').Length);
            Assert.Equal("Whichever device is active", Assert.Single(b.Vm.SpotifyDevices).Label);

            var host = new Window { DataContext = b.Vm, Width = 700, Height = 1400 };
            var show = new ShowSection();
            foreach (var section in new UserControl[] { new AudioSection(), show })
            {
                host.Content = new ScrollViewer { Content = section };
                host.Show();
                Dispatcher.UIThread.RunJobs();
                AvaloniaHeadlessPlatform.ForceRenderTimerTick();
                Dispatcher.UIThread.RunJobs();
                using var frame = host.CaptureRenderedFrame();
                Assert.NotNull(frame);
                host.Hide();
            }
            var block = show.FindControl<StackPanel>("BreakMusicBlock")!;
            Assert.True(block.IsVisible);
            b.Vm.State.Spotify.Enabled = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(block.IsVisible);
            host.Close();
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheRunChipAppearsOnlyWhenBreakMusicIsPlaying()
    {
        using var r = new Rig(enabled: false, connected: true);
        r.Item("Interval bed", "spotify:playlist:X");
        var b = r.B;
        b.Vm.State.Spotify.Playing = true;
        Assert.False(b.Vm.Run.IsMusicPlaying); // off is off, whatever the flag says

        b.Vm.State.Spotify.Enabled = true;
        b.Vm.State.Spotify.Playing = false;
        r.Execute(ShowActionKind.SpotifyPlay, "1");
        r.Fake.Answer = q => q.Method == "GET" && q.Url.EndsWith("/me/player")
            ? new SpotifyReply(200, "{\"is_playing\":true,\"item\":{\"name\":\"Kerala\",\"artists\":[{\"name\":\"Bonobo\"}]},\"device\":{\"id\":\"d1\",\"name\":\"Desk\",\"volume_percent\":60}}")
            : null;
        r.Poll(4);
        Assert.True(b.Vm.Run.IsMusicPlaying);
        Assert.Contains("Bonobo · Kerala", b.Vm.Run.MusicTip);
        Assert.Contains("Desk", b.Vm.Run.MusicTip);
        Assert.Contains("STOP ALL", b.Vm.Run.MusicTip);

        b.Vm.IsRunLayout = true;
        b.Vm.Run.Tick();
        Dispatcher.UIThread.RunJobs();
        AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        Dispatcher.UIThread.RunJobs();
        var chip = b.Window.GetVisualDescendants().OfType<Border>().Single(x => x.Classes.Contains("musicChip"));
        Assert.True(chip.IsVisible);
        var strip = Assert.IsType<Grid>(chip.GetVisualParent());
        Assert.Equal(11, strip.ColumnDefinitions.Count);        // …BREAK MUSIC · STING HOLD · DUCK · next-auto · POP OUT + clock
        Assert.Equal(6, Grid.GetColumn(chip));
        var clock = strip.Children.OfType<StackPanel>().Last();  // POP OUT and the clock stay the last column
        Assert.Equal(10, Grid.GetColumn(clock));
        Assert.Equal(strip.ColumnDefinitions.Count - 1, Grid.GetColumn(clock));

        r.Execute(ShowActionKind.SpotifyPause);
        b.Vm.Run.Tick();
        Dispatcher.UIThread.RunJobs();
        Assert.False(b.Vm.Run.IsMusicPlaying);
        Assert.False(chip.IsVisible);
    }

    [AvaloniaFact]
    public void RemovingBreakMusicACueNeedsIsRefused()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var a = new SpotifyItemConfig { Name = "Interval bed", Uri = "spotify:playlist:A" };
            var other = new SpotifyItemConfig { Name = "Walk-out", Uri = "spotify:playlist:B" };
            vm.State.Spotify.Items.Add(a);
            vm.State.Spotify.Items.Add(other);
            var stack = CueStacks.Caller(vm.State);
            stack.Cues.Add(new RunCueConfig { Number = "03.020", Name = "Interval", Actions = { new CueActionConfig { Kind = CueActionKind.SpotifyPlay, Target = a.Id } } });

            vm.RemoveMusicItemCommand.Execute(a);
            Assert.Contains(a, vm.State.Spotify.Items);
            Assert.Contains("03.020 Interval", vm.StatusMessage);
            Assert.Contains("Interval bed", vm.StatusMessage);

            vm.RemoveMusicItemCommand.Execute(other);
            Assert.DoesNotContain(other, vm.State.Spotify.Items);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AddingABreakMusicLinkAcceptsOnlySpotifyLinks()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            vm.MusicLinkDraft = "https://open.spotify.com/playlist/X?si=1";
            vm.AddMusicItemCommand.Execute(null);
            var item = Assert.Single(vm.State.Spotify.Items);
            Assert.Equal("spotify:playlist:X", item.Uri);
            Assert.Equal("LIST", item.KindLabel);
            Assert.Equal("", vm.MusicLinkDraft);

            vm.MusicLinkDraft = "https://youtube.com/x";
            vm.AddMusicItemCommand.Execute(null);
            Assert.Single(vm.State.Spotify.Items);
            Assert.Contains("Share → Copy link", vm.StatusMessage);
            Assert.Equal("https://youtube.com/x", vm.MusicLinkDraft); // left for the operator to fix

            vm.AddSpotifyPlaylistCommand.Execute(null);   // nothing chosen yet: a sentence, not a crash
            Assert.Contains("Refresh my playlists", vm.StatusMessage);
            vm.SpotifyPlaylists.Add(new SpotifyPlaylistRef("spotify:playlist:P", "Walk-in", 40));
            vm.SelectedSpotifyPlaylist = vm.SpotifyPlaylists[0];
            vm.AddSpotifyPlaylistCommand.Execute(null);
            Assert.Equal(2, vm.State.Spotify.Items.Count);
            Assert.Equal(("spotify:playlist:P", "Walk-in"), (vm.State.Spotify.Items[1].Uri, vm.State.Spotify.Items[1].Name));

            // The device picker keeps the show's choice even when Spotify has not listed it.
            vm.State.Spotify.DeviceName = "Lobby speaker";
            vm.RefreshSpotifyDevicesCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("Lobby speaker", vm.SelectedSpotifyDevice!.Name);
            Assert.Contains("not on Spotify right now", vm.SelectedSpotifyDevice.Label);
            vm.SelectedSpotifyDevice = vm.SpotifyDevices[0];
            Assert.Equal("", vm.State.Spotify.DeviceName);
        }
        finally
        {
            b.Dispose();
        }
    }

    // ---- browsing, searching, music on a look ---------------------------------------

    /// <summary>One page of a playlist listing: song i is "Song i"; item 1 is a local file and item 2 a removed song.</summary>
    private static string PlaylistPage(int offset, int total, int pageSize = SpotifyEndpoints.TracksPage)
    {
        var items = new List<string>();
        for (var i = offset; i < Math.Min(total, offset + pageSize); i++)
        {
            items.Add(i switch
            {
                1 => "{\"is_local\":true,\"track\":{\"uri\":\"spotify:local:a:b:c\",\"name\":\"Ripped\"}}",
                2 => "{\"track\":null}",
                _ => $"{{\"track\":{{\"uri\":\"spotify:track:T{i}\",\"name\":\"Song {i}\",\"duration_ms\":{180000 + i},\"artists\":[{{\"name\":\"Artist\"}}]}}}}",
            });
        }
        return $"{{\"total\":{total},\"items\":[{string.Join(",", items)}]}}";
    }

    private static int OffsetOf(string url)
    {
        var i = url.IndexOf("offset=", StringComparison.Ordinal) + 7;
        var end = url.IndexOf('&', i);
        return int.Parse(url[i..(end < 0 ? url.Length : end)]);
    }

    [AvaloniaFact]
    public void BrowsingAPlaylistPagesThroughItsSongsAndStopsAtTheCap()
    {
        using var r = new Rig(enabled: true, connected: true);
        var total = 120;
        r.Fake.Answer = q => q.Url.Contains("/playlists/P/tracks") ? new SpotifyReply(200, PlaylistPage(OffsetOf(q.Url), total)) : null;
        var vm = r.Vm;
        vm.BrowseSpotifyPlaylistCommand.Execute(null);          // nothing chosen yet: a sentence
        Assert.Contains("Choose one of your playlists", vm.StatusMessage);
        vm.SpotifyPlaylists.Add(new SpotifyPlaylistRef("spotify:playlist:P", "Walk-in", total));
        vm.SelectedSpotifyPlaylist = vm.SpotifyPlaylists[0];
        vm.BrowseSpotifyPlaylistCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();

        Assert.Equal(new[] { 0, 50, 100 }, r.Fake.Of("/playlists/P/tracks").Select(q => OffsetOf(q.Url)).ToArray());
        Assert.All(r.Fake.Of("/playlists/P/tracks"), q => Assert.Equal("A", q.Bearer)); // the token came first
        Assert.Equal(118, r.Spotify.Tracks.Count);              // the local file and the removed song are skipped
        Assert.Equal("spotify:playlist:P", r.Spotify.TracksOf);
        Assert.Equal("118 songs.", r.Spotify.BrowseStatus);
        Assert.Equal(118, vm.SpotifyTracks.Count);
        Assert.Equal("118 songs.", vm.SpotifyBrowseStatus);
        Assert.Equal("Artist · Song 0  ·  3:00", vm.SpotifyTracks[0].ToString());

        // A browsed song becomes a one-press entry named like the read-back; the same song twice stays one entry.
        vm.AddSpotifyTrackCommand.Execute(null);
        Assert.Contains("Pick a song", vm.StatusMessage);
        vm.SelectedSpotifyTrack = vm.SpotifyTracks[5];       // songs 0, 3, 4, 5, 6, 7 — the two skipped ones are not there
        vm.AddSpotifyTrackCommand.Execute(null);
        var item = Assert.Single(vm.State.Spotify.Items);
        Assert.Equal(("spotify:track:T7", "Artist · Song 7"), (item.Uri, item.Name));
        vm.AddSpotifyTrackCommand.Execute(null);
        Assert.Single(vm.State.Spotify.Items);
        Assert.Contains("already", vm.StatusMessage);

        // A long listing stops at the cap and says so; a pasted link browses the same way.
        total = 900;
        r.Fake.Requests.Clear();
        vm.MusicLinkDraft = "https://open.spotify.com/playlist/P?si=1";
        vm.BrowseSpotifyLinkCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal(10, r.Fake.Count("/playlists/P/tracks"));
        Assert.Equal(498, r.Spotify.Tracks.Count);
        Assert.Equal("First 498 songs of 900.", vm.SpotifyBrowseStatus);

        // A song has nothing to browse; a link that is not Spotify is a sentence, not a request.
        r.Fake.Requests.Clear();
        vm.MusicLinkDraft = "spotify:track:T1";
        vm.BrowseSpotifyLinkCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("a song has nothing inside it", vm.SpotifyBrowseStatus);
        vm.MusicLinkDraft = "https://youtube.com/x";
        vm.BrowseSpotifyLinkCommand.Execute(null);
        Assert.Contains("Paste a Spotify playlist", vm.StatusMessage);
        Assert.Empty(r.Fake.Requests);
        Assert.Equal(498, vm.SpotifyTracks.Count);              // the last good listing stays
    }

    [AvaloniaFact]
    public void SearchingListsHitsAndAddingOneMakesAnEntryAndAFailureIsASentence()
    {
        using var r = new Rig(enabled: true, connected: true);
        r.Fake.Answer = q => q.Url.Contains("/search?")
            ? new SpotifyReply(200,
                "{\"tracks\":{\"items\":[{\"uri\":\"spotify:track:T\",\"name\":\"Kerala\",\"artists\":[{\"name\":\"Bonobo\"}]}]}," +
                "\"playlists\":{\"items\":[null,{\"uri\":\"spotify:playlist:P\",\"name\":\"Chill\",\"owner\":{\"display_name\":\"Ben\"}}]}}")
            : null;
        var vm = r.Vm;
        vm.SearchSpotifyCommand.Execute(null);                  // nothing typed
        Dispatcher.UIThread.RunJobs();
        Assert.Contains("Type a song", vm.SpotifyBrowseStatus);
        Assert.Empty(r.Fake.Of("/search?"));

        vm.MusicSearchDraft = " bonobo ";
        vm.SearchSpotifyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var search = Assert.Single(r.Fake.Of("/search?"));
        Assert.Contains("q=bonobo&", search.Url);
        Assert.Equal("A", search.Bearer);
        Assert.Equal(2, vm.SpotifySearchHits.Count);
        Assert.Equal("2 results — pick one and ADD.", vm.SpotifyBrowseStatus);
        Assert.Equal("SONG  Kerala — Bonobo", vm.SpotifySearchHits[0].ToString());

        vm.AddSpotifySearchHitCommand.Execute(null);            // nothing picked
        Assert.Contains("Pick a result", vm.StatusMessage);
        vm.SelectedSpotifySearchHit = vm.SpotifySearchHits[1];
        vm.AddSpotifySearchHitCommand.Execute(null);
        vm.SelectedSpotifySearchHit = vm.SpotifySearchHits[0];
        vm.AddSpotifySearchHitCommand.Execute(null);
        Assert.Equal(new[] { ("spotify:playlist:P", "Chill"), ("spotify:track:T", "Bonobo · Kerala") },
            vm.State.Spotify.Items.Select(i => (i.Uri, i.Name)).ToArray());

        r.Fake.Throw = true;
        vm.SearchSpotifyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Spotify is unavailable — check the network.", vm.SpotifyBrowseStatus);
        Assert.Equal(2, vm.SpotifySearchHits.Count);            // the last good answer stays listed

        // Not signed in: a sentence, and no request at all.
        r.Fake.Throw = false;
        r.Fake.Requests.Clear();
        r.Spotify.Disconnect();
        vm.SearchSpotifyCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("Not connected — press CONNECT on the Audio page.", vm.SpotifyBrowseStatus);
        Assert.Empty(r.Fake.Requests);
    }

    [AvaloniaFact]
    public void ALookStartsItsMusicOnAirAndNeverInThePreview()
    {
        using var r = new Rig(enabled: true, connected: true);
        var item = r.Item("Interval bed", "spotify:playlist:X");
        var vm = r.Vm;
        vm.IsSandboxActive = false;
        var look = SaveLook(vm, "Walk-in", PatternKind.ColorBars);
        look.MusicItemId = item.Id;
        vm.ActivePattern.Kind = PatternKind.Grid;

        var result = r.Execute(ShowActionKind.ApplyLook, look.Id);
        Assert.Equal(ActionStatus.Requested, result.Status);    // the look settles on its music, like a cue would
        Assert.Contains("Walk-in", result.Message);
        Assert.Contains("Interval bed", result.Message);
        Assert.Equal(PatternKind.ColorBars, vm.State.Pattern.Kind);
        Assert.True(r.Music.Playing);
        Assert.Equal(item.Id, r.Music.PlayingId);
        var tail = r.Services.Journal.Tail(2);
        Assert.Equal(new[] { "ApplyLook", "SpotifyPlay" }, tail.Select(e => e.Kind).OrderBy(k => k).ToArray());
        Assert.Equal("Interval bed", tail.Single(e => e.Kind == "SpotifyPlay").Target);
        Assert.Contains("Walk-in", tail.Single(e => e.Kind == "SpotifyPlay").Message);
        Assert.Equal("Walk-in", tail.Single(e => e.Kind == "ApplyLook").Target);
        Assert.All(tail, e => Assert.Equal(ActionOrigin.Desk.Label, e.Origin));
        r.Poll(3);
        Assert.Single(r.Fake.Of("/me/player/play"));

        // Loading the same look into the preview changes the picture there and nothing about the music.
        r.Execute(ShowActionKind.SpotifyPause);
        r.Fake.Requests.Clear();
        vm.IsSandboxActive = true;
        Assert.Equal(ActionStatus.Done, r.Execute(ShowActionKind.ApplyLookToPreview, look.Id).Status);
        r.Poll(3);
        Assert.False(r.Music.Playing);
        Assert.Empty(r.Fake.Of("/me/player/play"));

        // A look that pauses: the pause goes out on this turn, as PAUSE does.
        vm.IsSandboxActive = false;
        var speech = SaveLook(vm, "Speech", PatternKind.Focus);
        speech.MusicItemId = LookConfig.PauseMusic;
        r.Execute(ShowActionKind.SpotifyPlay, "1");
        r.Poll(2);
        var pauses = r.Fake.Count("/me/player/pause");
        var onAir = r.Execute(ShowActionKind.ApplyLook, speech.Id);
        Assert.Equal(ActionStatus.Requested, onAir.Status);
        Assert.False(r.Music.Playing);
        Assert.Equal(pauses + 1, r.Fake.Count("/me/player/pause"));
        Assert.Equal(PatternKind.Focus, vm.State.Pattern.Kind);
    }

    [AvaloniaFact]
    public void ALookWhoseMusicIsOffOrGoneStillLandsAndSaysSo()
    {
        using var r = new Rig(enabled: false, connected: true);
        var item = r.Item("Interval bed", "spotify:playlist:X");
        var vm = r.Vm;
        vm.IsSandboxActive = false;
        var look = SaveLook(vm, "Walk-in", PatternKind.ColorBars);
        look.MusicItemId = item.Id;
        vm.ActivePattern.Kind = PatternKind.Grid;

        // Break music off: the look lands, the music step is a no-op, nothing goes out.
        var off = r.Execute(ShowActionKind.ApplyLook, look.Id);
        Assert.Equal(ActionStatus.Done, off.Status);
        Assert.Contains("Break music is off", off.Message);
        Assert.Equal(PatternKind.ColorBars, vm.State.Pattern.Kind);
        Assert.False(r.Music.Playing);
        r.Poll(3);
        Assert.Empty(r.Fake.Requests);

        // The entry is gone: the look still lands; the music step is refused and journaled as such.
        r.Music.Enabled = true;
        look.MusicItemId = "ghost";
        vm.ActivePattern.Kind = PatternKind.Grid;
        var gone = r.Execute(ShowActionKind.ApplyLook, look.Id);
        Assert.Equal(ActionStatus.Done, gone.Status);
        Assert.Contains("No break music 'ghost'", gone.Message);
        Assert.Equal(PatternKind.ColorBars, vm.State.Pattern.Kind);
        Assert.False(r.Music.Playing);
        var music = r.Services.Journal.Tail(2).Single(e => e.Kind == "SpotifyPlay");
        Assert.Equal("Refused", music.Outcome);
        Assert.Contains("Walk-in", music.Message);

        // A cue applying that look validates with a warning, never a broken row, and runs.
        var stack = CueStacks.Caller(vm.State);
        var cue = new RunCueConfig { Number = "01.010", Name = "Doors" };
        cue.Actions.Add(new CueActionConfig { Kind = CueActionKind.ApplyLook, Target = look.Id });
        stack.Cues.Add(cue);
        Dispatcher.UIThread.RunJobs();
        var report = CueValidator.Validate(vm.State, stack, r.Services.ValidationContext);
        Assert.False(report.IsBroken(cue.Id));
        Assert.True(report.Warnings.TryGetValue(cue.Id, out var warning));
        Assert.Contains("no longer in the library", warning);
        vm.ActivePattern.Kind = PatternKind.Grid;
        var fired = r.Services.Actions.Execute(new ShowAction(ShowActionKind.CueFire, cue.Id), ActionOrigin.Desk);
        Assert.True(fired.Ok, fired.Message);
        Assert.Equal(PatternKind.ColorBars, vm.State.Pattern.Kind);

        // Deleting an entry a look starts is refused and names the look.
        look.MusicItemId = item.Id;
        vm.RemoveMusicItemCommand.Execute(item);
        Assert.Contains(item, vm.State.Spotify.Items);
        Assert.Contains("look 'Walk-in'", vm.StatusMessage);
    }

    [AvaloniaFact]
    public void TheLooksPageOffersMusicPerLookAndARenameNeverDropsTheChoice()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            vm.State.Spotify.Enabled = true;
            var bed = new SpotifyItemConfig { Name = "Interval bed", Uri = "spotify:playlist:X" };
            vm.State.Spotify.Items.Add(bed);
            vm.NewLookName = "Walk-in";
            vm.SaveLookCommand.Execute(null);
            var look = LookService.Find(vm.State, "Walk-in")!;
            look.MusicItemId = bed.Id;
            vm.PollNow();
            Assert.Equal(new[] { "", LookConfig.PauseMusic, bed.Id }, vm.LookMusicChoices.Select(c => c.Id).ToArray());
            Assert.Equal("▶ Interval bed", vm.LookMusicChoices[2].Label);

            var host = new Window { DataContext = vm, Width = 900, Height = 700, Content = new ScrollViewer { Content = new LooksSection() } };
            host.Show();
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
            Dispatcher.UIThread.RunJobs();
            var picker = host.GetVisualDescendants().OfType<ComboBox>().Single(c => c.Name == "LookMusic");
            Assert.True(picker.IsVisible);
            Assert.Equal(bed.Id, Assert.IsType<LookMusicChoice>(picker.SelectedItem).Id);

            // A rename relabels in place and an added entry appends: the look keeps its choice through both.
            bed.Name = "Doors bed";
            vm.State.Spotify.Items.Add(new SpotifyItemConfig { Name = "Walk-out", Uri = "spotify:album:Y" });
            vm.PollNow();
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("▶ Doors bed", vm.LookMusicChoices[2].Label);
            Assert.Equal(4, vm.LookMusicChoices.Count);
            Assert.Equal(bed.Id, look.MusicItemId);
            Assert.Same(vm.LookMusicChoices[2], picker.SelectedItem);

            // A look naming an entry that has gone keeps an offered, marked choice rather than losing it.
            look.MusicItemId = "ghost";
            vm.PollNow();
            Assert.Contains(vm.LookMusicChoices, c => c.Id == "ghost" && c.Label.Contains("no longer"));
            look.MusicItemId = bed.Id;
            vm.PollNow();
            Assert.DoesNotContain(vm.LookMusicChoices, c => c.Id == "ghost");

            // Picking "pause" writes through; switching break music off hides the picker.
            picker.SelectedItem = vm.LookMusicChoices[1];
            Assert.Equal(LookConfig.PauseMusic, look.MusicItemId);
            vm.State.Spotify.Enabled = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(picker.IsVisible);
            host.Close();
        }
        finally
        {
            b.Dispose();
        }
    }
}
