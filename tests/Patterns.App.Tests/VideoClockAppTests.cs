using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The caller's VT clock on a live desk: a clip on air read on the panel, the Run strip and STATE
/// with the same seconds; the last ten seconds going red with the caller's word; VIDEO END and
/// VIDEO RESTART from the wire, a cue and the panel moving the decoder; the refusals; the page.
/// </summary>
public class VideoClockAppTests
{
    private static void Settle(Window window)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static string Send(CommandRouter router, string line) => TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse(line)));

    /// <summary>A clip on the program's media pattern, decoded by a fake: the clock has something to read.</summary>
    private static FakeSource PutClipOnAir(TestApp.Booted b, AudioFakes fakes, string name)
    {
        var (services, vm, _) = b;
        vm.ActivePattern.Kind = PatternKind.Media;
        vm.ActivePattern.Media.Source = MediaSource.Video;
        vm.ActivePattern.Media.Loop = false;                    // a VT, not a walk-in loop: it comes out
        vm.ActivePattern.Media.VideoPath = AudioFakes.TempFile(name);
        Dispatcher.UIThread.RunJobs();
        services.ReconcileInputs();
        var fake = fakes.Sources.Single();
        fake.Length = 210;
        fake.Position = 62;
        return fake;
    }

    [AvaloniaFact]
    public void TheClockReadsTheClipEverywhereAndTheSkipAndTheTopMoveIt()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            vm.IsSandboxActive = false;
            var fakes = AudioFakes.Install(b);
            var router = new CommandRouter(services);
            var fake = PutClipOnAir(b, fakes, "sponsor.mp4");

            // The panel, the Run strip and STATE read the same seconds.
            vm.PollNow();
            Assert.True(vm.HasVideoClock);
            Assert.Equal("VT", vm.VideoClockTag);
            Assert.EndsWith("sponsor.mp4", vm.VideoClockName);
            Assert.Equal("1:02 / 3:30 · 2:28 left", vm.VideoClockTimes);
            Assert.False(vm.VideoClockOut);
            Assert.Equal("", vm.VideoClockCall);
            Assert.InRange(vm.VideoClockFraction, 0.29, 0.30);
            Assert.True(vm.Run.HasVideo);
            Assert.Equal("VT 2:28", vm.Run.VideoChip);
            Assert.False(vm.Run.VideoOut);
            var json = router.StateJson();
            Assert.Contains("\"video\":{\"file\":", json);
            Assert.Contains("\"remaining\":148", json);
            Assert.Contains("\"chip\":\"VT 2:28\"", json);
            Assert.Contains("\"out\":false", json);

            // The wire moves the decoder: to the last five seconds, the last ten by default, the top.
            Assert.StartsWith("OK", Send(router, "VIDEO END 5"));
            Assert.Equal(205, fake.SeekedTo);
            Assert.StartsWith("OK", Send(router, "VT END"));
            Assert.Equal(200, fake.SeekedTo);
            Assert.StartsWith("OK", Send(router, "VIDEO RESTART"));
            Assert.Equal(0, fake.SeekedTo);
            Assert.Contains(services.Journal.Tail(8), e => e.Kind == nameof(ShowActionKind.VideoToEnd));
            Assert.Contains(services.Journal.Tail(8), e => e.Kind == nameof(ShowActionKind.VideoRestart));

            // A cue's actions, through the executor, and the wire's words as actions.
            var skip = new CueActionConfig { Kind = CueActionKind.VideoToEnd, Value = "3" };
            Assert.True(services.Actions.Execute(ShowActions.ToShowAction(skip), new ActionOrigin(OriginKind.Cue, "01.010")).Ok);
            Assert.Equal(207, fake.SeekedTo);
            var again = new CueActionConfig { Kind = CueActionKind.VideoRestart };
            Assert.True(services.Actions.Execute(ShowActions.ToShowAction(again), new ActionOrigin(OriginKind.Cue, "01.020")).Ok);
            Assert.Equal(0, fake.SeekedTo);
            var parsed = CommandRouter.ToAction(ControlProtocol.Parse("VIDEO END 2.5"));
            Assert.Equal((ShowActionKind.VideoToEnd, "2.5"), (parsed!.Value.Kind, parsed.Value.Value));
            Assert.Equal(ShowActionKind.VideoRestart, CommandRouter.ToAction(ControlProtocol.Parse("VIDEO RESTART"))!.Value.Kind);

            // The panel's own keys.
            vm.VideoToEndCommand.Execute(null);
            Assert.Equal(200, fake.SeekedTo);
            Assert.Contains("last 10", vm.StatusMessage);
            vm.VideoRestartCommand.Execute(null);
            Assert.Equal(0, fake.SeekedTo);
            Assert.Contains("from the top", vm.StatusMessage);

            // The last ten seconds: red, with the caller's word, on every surface.
            fake.Position = 203;
            vm.PollNow();
            Assert.True(vm.VideoClockOut);
            Assert.Equal("OUT IN 7", vm.VideoClockCall);
            Assert.Equal("3:23 / 3:30 · 0:07 left", vm.VideoClockTimes);
            Assert.True(vm.Run.VideoOut);
            Assert.Equal("VT 0:07", vm.Run.VideoChip);
            var near = router.StateJson();
            Assert.Contains("\"out\":true", near);
            Assert.Contains("\"call\":\"OUT IN 7\"", near);

            // Ended, then the top again brings it back.
            fake.Ended = true;
            vm.PollNow();
            Assert.Equal("ended", vm.VideoClockTimes);
            Assert.Equal("VT ENDED", vm.Run.VideoChip);
            Assert.False(vm.VideoClockOut);
            Assert.StartsWith("OK", Send(router, "VIDEO RESTART"));
            Assert.False(fake.Ended);
            Assert.Equal(0, fake.Position);

            // A decoder that cannot be moved, and a word that is not seconds, are refused with the reason.
            fake.Seekable = false;
            Assert.Contains("cannot be moved", Send(router, "VIDEO END"));
            fake.Seekable = true;
            Assert.StartsWith("ERR", Send(router, "VIDEO END soon"));

            // No clip on air: no clock, and the verbs say so.
            vm.ActivePattern.Kind = PatternKind.Grid;
            Dispatcher.UIThread.RunJobs();
            services.ReconcileInputs();
            vm.PollNow();
            Assert.False(vm.HasVideoClock);
            Assert.Equal("", vm.VideoClockText);
            Assert.False(vm.Run.HasVideo);
            Assert.Contains("No video is on air", Send(router, "VIDEO END"));
            Assert.Contains("\"video\":null", router.StateJson());
        }
        finally
        {
            b.Window.Close();
            b.Services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void ThePanelShowsTheClockWithItsKeysAndTheCueEditorAsksForSeconds()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            vm.IsSandboxActive = false;
            var fakes = AudioFakes.Install(b);
            PutClipOnAir(b, fakes, "walk-in.mp4");
            vm.PollNow();

            vm.SelectPage(Shell.PanelPage);
            Settle(window);
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains("VT", texts);
            Assert.Contains("1:02 / 3:30 · 2:28 left", texts);
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), x => x.Content as string == "⏭ LAST 10 s");
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), x => x.Content as string == "⟲ RESTART");

            // The cue editor: the skip wants seconds and says so; the restart wants nothing.
            var stack = services.CueStack.Stack;
            var cue = new RunCueConfig { Number = "1", Name = "Skip" };
            stack.Cues.Add(cue);
            vm.Cues.SelectedStack = stack;
            vm.Cues.SelectedCue = cue;
            vm.Cues.AddActionCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var row = vm.Cues.ActionRows.Last();
            row.SelectedKind = row.KindChoices.First(k => k.Id == nameof(CueActionKind.VideoToEnd));
            Assert.True(row.HasValue);
            Assert.Contains("seconds", row.ValueHint);
            row.SelectedKind = row.KindChoices.First(k => k.Id == nameof(CueActionKind.VideoRestart));
            Assert.False(row.HasValue);

            // The Help catalogue carries the clock, on the pages it lives on.
            var topic = HelpTopics.Find("video-clock");
            Assert.NotNull(topic);
            Assert.Contains("Panel", topic!.Pages);
            Assert.Contains(HelpTopics.ForPage("Run"), t => t.Id == "video-clock");
        }
        finally
        {
            b.Window.Close();
            b.Services.Shutdown();
        }
    }
}
