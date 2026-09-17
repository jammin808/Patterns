using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 77.3 on the desk: the room hears what it sees. A programme clip on no live output — the
/// desk's monitor with its outputs off, the one live screen showing a picture of its own — fades
/// out and is muted until the programme is shown again; a screen's own picture is heard while its
/// screen is live; with no output open the desk hears the programme as it always did. The same for
/// a web page, with the fade the transition's length.
/// </summary>
public class LiveOutputsAudioAppTests
{
    private static void GoLive(TestApp.Booted b)
    {
        var on = b.Services.Actions.Execute(ShowActionKind.OutputsOn, ActionOrigin.Desk);
        Assert.True(on.Ok, on.Message);
        Dispatcher.UIThread.RunJobs();
        Assert.True(b.Services.Outputs.IsLive);
    }

    private static void Settle(AppServices services)
    {
        Dispatcher.UIThread.RunJobs();
        services.ReconcileInputs();
        Dispatcher.UIThread.RunJobs();
    }

    private static string LiveTarget(AppServices services)
        => services.Outputs.Windows.First().Pipeline.Viewport.ScreenId ?? "";

    /// <summary>The mounted (not retiring) fake for a clip — a reopen leaves an older fake behind in the list.</summary>
    private static FakeSource Mounted(AudioFakes fakes, string path)
        => fakes.Sources.Last(s => s.Wanted.Target == path && s.FadeMs < 0 && !s.Disposed);

    /// <summary>The screen takes a clip of its own — one edit, one publish, the way the desk's own take lands it (an own picture starts as a copy of the programme's, and a publish between the copy and the clip would show the programme's clip on that screen for a moment).</summary>
    private static void OwnClip(AppServices services, ShowState state, string target, string path)
        => services.BulkEdit(() =>
        {
            ContentTargets.SetOwnPattern(state, target, true);
            var own = ContentTargets.EnsureAssignment(state, target).Pattern;
            own.Kind = PatternKind.Media;
            own.Media.Source = MediaSource.Video;
            own.Media.VideoPath = path;
            own.Media.Mute = false;
            own.Media.Loop = false;
        });

    [AvaloniaFact]
    public void TheProgrammesClipFallsSilentWhenNoLiveOutputShowsItAndAnOwnPictureSoundsOnItsScreen()
    {
        var b = TestApp.Boot("patterns-live-sound-");
        try
        {
            var (services, vm, _) = b;
            var fakes = AudioFakes.Install(b);
            var state = vm.State;
            var pgmPath = AudioFakes.TempFile("pgm.mp4");
            var ownPath = AudioFakes.TempFile("own.mp4");
            state.Pattern.Kind = PatternKind.Media;
            state.Pattern.Media.Source = MediaSource.Video;
            state.Pattern.Media.VideoPath = pgmPath;
            state.Pattern.Media.Mute = false;
            state.Pattern.Media.Loop = false;
            Settle(services);

            // No output open: the desk hears the programme, as it always did.
            var pgm = Mounted(fakes, pgmPath);
            Assert.False(pgm.Mute);
            Assert.False(services.ShownLive().AnyLive);
            Assert.Equal("no output is open", services.ShownLive().Words());

            // Live, following the programme: heard on the room's output, nothing faded.
            GoLive(b);
            Settle(services);
            var target = LiveTarget(services);
            Assert.NotEqual("", target);
            var live = services.ShownLive();
            Assert.True(live.ProgrammeLive);
            Assert.True(live.AnyLive);
            Assert.False(pgm.Mute);
            Assert.Empty(pgm.OffAirFades);

            // The one live screen takes a picture of its own: the programme is on no live output. Its clip
            // fades over the show's transition and is muted; the screen's own clip is heard, on its screen.
            OwnClip(services, state, target, ownPath);
            Settle(services);
            live = services.ShownLive();
            Assert.False(live.ProgrammeLive, "no live output shows the programme now");
            Assert.Contains(target, live.OwnTargets);
            var fade = Assert.Single(pgm.OffAirFades);
            Assert.Equal(AudioRouting.LeaveFadeMs(state), fade.Ms);
            Assert.True(pgm.Mute);
            Assert.False(pgm.FadeMs >= 0, "not retired — the programme still wants it for its frames; only its sound left");
            var own = Mounted(fakes, ownPath);
            Assert.False(own.Mute);
            Assert.Empty(own.OffAirFades);
            var wanted = VideoEngine.WantedVideoInputs(services.Bus.Current, null, live);
            Assert.Equal(AudioDestination.Silent, wanted.Single(w => w.Target == pgmPath).Destination);
            Assert.True(wanted.Single(w => w.Target == pgmPath).RuleMuted);
            Assert.Equal(AudioDestination.Program, wanted.Single(w => w.Target == ownPath).Destination);

            // The screen back on the programme: the clip is heard again at once — no second fade, no reopen.
            ContentTargets.SetOwnPattern(state, target, false);
            Settle(services);
            Assert.True(services.ShownLive().ProgrammeLive);
            Assert.False(pgm.Mute);
            Assert.Single(pgm.OffAirFades);
            Assert.True(own.FadeMs >= 0 || own.Disposed, "the own picture nobody shows any more retires with its fade");

            // The outputs closed: the desk hears the programme, and the rule says so.
            services.Actions.Execute(ShowActionKind.OutputsOff, ActionOrigin.Desk);
            Settle(services);
            Assert.False(services.Outputs.IsLive);
            Assert.False(services.ShownLive().AnyLive);
            Assert.False(pgm.Mute);
            Assert.Single(pgm.OffAirFades);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AProgrammePageIsMutedWithTheTransitionsFadeWhenItsScreenTakesAnotherPictureAndComesBackAtOnce()
    {
        var b = TestApp.Boot("patterns-live-page-");
        try
        {
            var (services, vm, _) = b;
            var fakes = AudioFakes.Install(b);
            var made = new List<FakeWebSource>();
            services.WebIn.SourceFactory = w =>
            {
                var page = new FakeWebSource(w.Target, WebEngine.ParseSize(w.Format), SKColors.Blue);
                made.Add(page);
                return page;
            };
            var state = vm.State;
            state.Pattern.Kind = PatternKind.Media;
            state.Pattern.Media.Source = MediaSource.Web;
            state.Pattern.Media.WebUrl = "https://www.youtube.com/watch?v=BvfUshmqKec";
            state.Pattern.Media.Mute = false;
            state.Transition.Enabled = true;
            state.Transition.DurationMs = 800;
            GoLive(b);
            Settle(services);
            var page = Assert.Single(made);
            Assert.False(page.IsMuted, "the programme's page, on a live screen: heard");
            var target = LiveTarget(services);

            // The live screen takes its own clip: the page is on no live output — muted over the transition, not cut.
            var ownPath = AudioFakes.TempFile("own.mp4");
            OwnClip(services, state, target, ownPath);
            Settle(services);
            Assert.True(page.IsMuted);
            var muted = page.MuteCalls.Last();
            Assert.True(muted.Muted);
            Assert.Equal(800, muted.FadeMs);
            Assert.Equal(AudioRouting.LeaveFadeMs(state), muted.FadeMs);
            Assert.False(Mounted(fakes, ownPath).Mute, "the screen's own clip is heard on its screen");

            // Back to the programme: unmuted at once, the elements' levels restored by the source.
            ContentTargets.SetOwnPattern(state, target, false);
            Settle(services);
            Assert.False(page.IsMuted);
            var back = page.MuteCalls.Last();
            Assert.False(back.Muted);
            Assert.Equal(0, back.FadeMs);

            // The operator's own mute is at once, not a fade — the rule's mark tells them apart.
            state.Pattern.Media.Mute = true;
            Settle(services);
            Assert.True(page.IsMuted);
            Assert.Equal(0, page.MuteCalls.Last().FadeMs);
        }
        finally
        {
            b.Dispose();
        }
    }
}
