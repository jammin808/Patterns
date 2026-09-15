using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Media;
using Patterns.Rendering.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The armed web VT on the desk: a page set up in the preview and armed by the wire, the take that
/// plays it from its mark, the look that arms a page by itself, and the clicker's next step opened
/// early — pre-rolled, held at its mark — so NEXT lands on the frame.
/// </summary>
public class WebVtAppTests
{
    private const string Tube = "https://www.youtube-nocookie.com/embed/abc?autoplay=1&controls=0";
    private const string TubeKey = "web:" + Tube;

    private static void Settle(Window window)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static List<FakeWebSource> FakePages(AppServices services)
    {
        var made = new List<FakeWebSource>();
        services.WebIn.SourceFactory = w =>
        {
            var page = new FakeWebSource(w.Target, WebEngine.ParseSize(w.Format), SKColors.Blue);
            made.Add(page);
            return page;
        };
        return made;
    }

    private static string Send(CommandRouter router, string line) => TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse(line)));

    /// <summary>The engine's poll, pumped: the fake answers its script at once, but the read is asynchronous.</summary>
    private static void Poll(AppServices services)
    {
        services.WebIn.Poll();
        for (var i = 0; i < 4; i++) Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void APageSetUpInThePreviewIsArmedByTheWireAndPlaysFromItsMarkOnTheTake()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            var made = FakePages(services);
            var router = new CommandRouter(services);

            // EDIT SAFE on: the YouTube page goes onto the preview's pattern, and the engine opens it there.
            vm.IsSandboxActive = true;
            vm.State.Pattern.Kind = PatternKind.Media;
            vm.State.Pattern.Media.Source = MediaSource.Web;
            vm.State.Pattern.Media.WebUrl = Tube;
            Settle(window);
            var page = Assert.Single(made);
            Assert.False(services.WebIn.VtFor(TubeKey).OnAir);

            // No player has answered yet: an ARM with no time is refused with the reason, an ARM with a time takes.
            Assert.StartsWith("ERR", Send(router, "WEB ARM"));
            Assert.Contains("no video player answering", Send(router, "WEB ARM"));
            Assert.Equal("OK", Send(router, "WEB ARM 1:23"));
            var arm = services.WebIn.ArmOf(TubeKey);
            Assert.True(arm.Armed);
            Assert.Equal(83, arm.StartSeconds);
            Assert.False(arm.ByLook);

            // The player answers on the next poll: the page is put at the mark and paused — the armed picture.
            page.AnswerAsPlayer(12, 300);
            Poll(services);
            var prepare = page.Scripts.Last(s => s.Contains("pauseVideo"));
            Assert.Contains("seekTo(83,true)", prepare);
            Assert.Equal(WebVtPhase.PrepareRequested, services.WebIn.PhaseOf(TubeKey));                 // sent — not yet seen on the player
            Assert.False(services.WebIn.VtFor(TubeKey).Prepared);
            page.AnswerAsPlayer(83.3, 300, paused: true);                                                // the player reports paused at the mark
            Poll(services);
            Assert.True(services.WebIn.VtFor(TubeKey).Prepared);                                         // observed, and only now
            Assert.Equal(WebVtPhase.PreparedObserved, services.WebIn.PhaseOf(TubeKey));
            vm.Media.PollStatus();
            Assert.Contains("at its mark (observed)", vm.Media.WebVtWords);
            var state = System.Text.Json.JsonDocument.Parse(router.StateJson()).RootElement;
            Assert.Equal("1:23", state.GetProperty("webArmed").GetProperty("atText").GetString());
            Assert.Contains("VT armed at 1:23", vm.ProgressionText);
            Assert.Equal(System.Text.Json.JsonValueKind.Null, state.GetProperty("web").ValueKind);   // nothing on air yet

            // MARK moves the point; the mark's own word says it is still armed.
            page.AnswerAsPlayer(95, 300, paused: true);
            Poll(services);
            var mark = Send(router, "WEB MARK");
            Assert.Equal("OK", mark);
            Assert.Equal(95, services.WebIn.ArmOf(TubeKey).StartSeconds);

            // TAKE: the page reaches the programme and the arm fires — playVideo from the mark, the arm spent.
            var before = page.Scripts.Count;
            services.Sandbox.SendAll(cut: true);
            Settle(window);
            var fired = page.Scripts.Skip(before).Single(s => s.Contains("playVideo"));
            Assert.Contains("seekTo(95,true)", fired);
            var after = services.WebIn.ArmOf(TubeKey);
            Assert.False(after.Armed);
            Assert.NotNull(after.PlayedUtc);
            Assert.Equal(95, after.PlayedFrom);
            Assert.True(services.WebIn.VtFor(TubeKey).OnAir);
            Assert.Single(made);   // the same browser carried over the take

            // On air now: the play is a request until the player reports playing from the mark; then the
            // STATE row carries the player and the arm's story, observed; a fresh ARM is refused.
            Assert.Equal(WebVtPhase.FireRequested, services.WebIn.PhaseOf(TubeKey));
            page.AnswerAsPlayer(100, 300);
            Poll(services);
            Assert.Equal(WebVtPhase.PlayingObserved, services.WebIn.PhaseOf(TubeKey));
            state = System.Text.Json.JsonDocument.Parse(router.StateJson()).RootElement;
            var web = state.GetProperty("web");
            Assert.Equal("1:40 / 5:00", web.GetProperty("player").GetProperty("text").GetString());
            Assert.Contains("played from 1:35", web.GetProperty("arm").GetProperty("words").GetString());
            Assert.Contains("PLAYING (observed)", web.GetProperty("arm").GetProperty("words").GetString());
            Assert.Equal("PlayingObserved", web.GetProperty("arm").GetProperty("phase").GetString());
            Assert.Equal(System.Text.Json.JsonValueKind.Null, state.GetProperty("webArmed").ValueKind);
            Assert.Contains("on air", Send(router, "WEB ARM 2:00"));
            Assert.StartsWith("ERR", Send(router, "WEB ARM 2:00"));
            Assert.Equal("OK", Send(router, "WEB DISARM"));
        }
        finally
        {
            InputBus.Clear();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheLookArmsItsPageByItselfAndTheAdvertIsSkippedWhileItShows()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            var made = FakePages(services);

            vm.IsSandboxActive = true;
            vm.State.Pattern.Kind = PatternKind.Media;
            vm.State.Pattern.Media.Source = MediaSource.Web;
            vm.State.Pattern.Media.WebUrl = Tube;
            vm.State.Pattern.Media.WebAutoPlay = true;
            vm.State.Pattern.Media.WebStartSeconds = 30;
            Settle(window);
            var page = Assert.Single(made);
            var arm = services.WebIn.ArmOf(TubeKey);
            Assert.True(arm.Armed);
            Assert.True(arm.ByLook);
            Assert.Equal(30, arm.StartSeconds);

            // An advert is showing: the skip is pressed on every read, and the mark waits for the advert to go.
            page.AnswerAsPlayer(0, 300, ad: true);
            Poll(services);
            Assert.Contains(page.Scripts, s => s.Contains("ytp-skip-ad-button"));
            Assert.True(services.WebIn.VtFor(TubeKey).PreparedUnderAd);
            var prepares = page.Scripts.Count(s => s.Contains("pauseVideo"));
            page.AnswerAsPlayer(0, 300, ad: false);
            Poll(services);
            Assert.Equal(prepares + 1, page.Scripts.Count(s => s.Contains("pauseVideo")));   // put at the mark again, clean

            // The look's mark moves: the arm follows it.
            vm.State.Pattern.Media.WebStartSeconds = 45;
            Settle(window);
            Assert.Equal(45, services.WebIn.ArmOf(TubeKey).StartSeconds);

            // The take plays from 45; the page leaving the air is armed by the look again for its next time.
            var before = page.Scripts.Count;
            services.Sandbox.SendAll(cut: true);
            Settle(window);
            Assert.Contains(page.Scripts.Skip(before), s => s.Contains("seekTo(45,true)") && s.Contains("playVideo"));
            Assert.False(services.WebIn.ArmOf(TubeKey).Armed);

            vm.State.Pattern.Kind = PatternKind.Grid;   // the preview moves on; the programme still shows the page
            Settle(window);
            Assert.False(services.WebIn.ArmOf(TubeKey).Armed);   // still on air: nothing to re-arm
            services.Sandbox.SendAll(cut: true);                 // the grid goes to air, the page comes off it
            Settle(window);
            Assert.Equal(0, services.WebIn.PageCount);   // retired: nothing wants it any more
        }
        finally
        {
            InputBus.Clear();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheClickersNextStepIsOpenedEarlyAndNextLandsOnTheMark()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            var made = FakePages(services);
            var router = new CommandRouter(services);
            vm.IsSandboxActive = false;
            var state = vm.State;

            // Two looks: a grid, and the sponsor's YouTube page asking to play from 1:23 — built off the
            // air (a look is JSON), so nothing is on the screens but the grid while they are made.
            state.Pattern.Kind = PatternKind.Grid;
            state.LooksAndCues.Looks.Add(new LookConfig { Name = "Grid", Json = LookService.Capture(state) });
            var sponsor = new ShowState();
            sponsor.Pattern.Kind = PatternKind.Media;
            sponsor.Pattern.Media.Source = MediaSource.Web;
            sponsor.Pattern.Media.WebUrl = Tube;
            sponsor.Pattern.Media.WebAutoPlay = true;
            sponsor.Pattern.Media.WebStartSeconds = 83;
            state.LooksAndCues.Looks.Add(new LookConfig { Name = "Sponsor", Json = LookService.Capture(sponsor) });
            var clicker = CueStacks.Clicker(state);
            foreach (var name in new[] { "Grid", "Sponsor" })
            {
                var cue = new RunCueConfig { Name = name, Number = CueNumber.Next(clicker.Cues.Count > 0 ? clicker.Cues[^1].Number : null) };
                cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.ApplyLook, Target = name });
                clicker.Cues.Add(cue);
            }
            Settle(window);

            // The first step is next: nothing to open. NEXT runs it; now the sponsor's page is the next step, and it opens early.
            Assert.Empty(made);
            Assert.True(services.Actions.Execute(ShowActionKind.PresenterNext, ActionOrigin.Clicker).Ok);
            Settle(window);
            var page = Assert.Single(made);
            Assert.True(services.WebIn.IsPreRolled(TubeKey));
            Assert.True(page.IsMuted);
            var arm = services.WebIn.ArmOf(TubeKey);
            Assert.True(arm.Armed && arm.ByLook);
            Assert.Equal(83, arm.StartSeconds);
            page.AnswerAsPlayer(0, 300);
            Poll(services);
            Assert.Contains(page.Scripts, s => s.Contains("seekTo(83,true)") && s.Contains("pauseVideo"));
            var st = System.Text.Json.JsonDocument.Parse(router.StateJson()).RootElement.GetProperty("webArmed");
            Assert.True(st.GetProperty("preRolled").GetBoolean());
            Assert.Contains("VT armed at 1:23", vm.ProgressionText);

            // The speaker presses NEXT: the look lands, the same browser goes to air and plays from the mark.
            var before = page.Scripts.Count;
            Assert.True(services.Actions.Execute(ShowActionKind.PresenterNext, ActionOrigin.Clicker).Ok);
            Settle(window);
            Assert.Single(made);   // the browser opened early is the one on air: no reopen, no top-of-the-video
            Assert.False(services.WebIn.IsPreRolled(TubeKey));
            Assert.Contains(page.Scripts.Skip(before), s => s.Contains("seekTo(83,true)") && s.Contains("playVideo"));
            Assert.False(services.WebIn.ArmOf(TubeKey).Armed);
            Assert.Equal(MediaSource.Web, services.Bus.Current.State.Pattern.Media.Source);
        }
        finally
        {
            InputBus.Clear();
            b.Dispose();
        }
    }
}
