using Avalonia.Controls;
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
/// The desk's monitor bus where it actually bites: the preview. EDIT SAFE means the operator is
/// nearly always holding a second picture, and if that picture is a clip its soundtrack used to
/// play over the programme's — the one case of the overlapping mix that happens on every show.
/// </summary>
public class AudioMonitorAppTests
{
    private static ShowState Clip(string path)
    {
        var state = new ShowState();
        state.Pattern.Kind = PatternKind.Media;
        state.Pattern.Media.Source = MediaSource.Video;
        state.Pattern.Media.VideoPath = path;
        state.Pattern.Media.Mute = false;
        return state;
    }

    private static ShowSnapshot Snap(ShowState state) => new() { State = state, Version = 1 };

    [AvaloniaFact]
    public void ThePreviewsClipIsSilentAtTheDeskUntilItIsWhatYouAreListeningTo()
    {
        var program = Clip("/clips/on-air.mp4");
        var preview = Clip("/clips/next.mp4");

        var wanted = VideoEngine.WantedVideoInputs(Snap(program), Snap(preview));
        var onAir = wanted.Single(w => w.Target == "/clips/on-air.mp4");
        var next = wanted.Single(w => w.Target == "/clips/next.mp4");
        Assert.False(onAir.Mute);
        Assert.True(next.Mute, "the clip being built is mounted for its pictures, not for its sound");

        // Listening to the preview: the pair swap over. The programme is still on air; only the
        // desk's own speakers changed.
        program.Monitor.Source = AudioMonitor.Preview;
        wanted = VideoEngine.WantedVideoInputs(Snap(program), Snap(preview));
        Assert.True(wanted.Single(w => w.Target == "/clips/on-air.mp4").Mute);
        Assert.False(wanted.Single(w => w.Target == "/clips/next.mp4").Mute);

        // The same clip in both: one decoder, and it is heard either way round rather than
        // silenced because the programme happened to claim the mount first.
        var same = Clip("/clips/on-air.mp4");
        same.Monitor.Source = AudioMonitor.Preview;
        var one = Assert.Single(VideoEngine.WantedVideoInputs(Snap(same), Snap(Clip("/clips/on-air.mp4"))));
        Assert.False(one.Mute);
    }

    [AvaloniaFact]
    public void TheAudioPageSaysWhatTheDeskIsHearingAndFollowsTheChoiceAtOnce()
    {
        var b = TestApp.Boot();
        try
        {
            var (_, vm, window) = b;
            window.Width = 1500;
            window.Height = 950;
            vm.State.Output.Placements.Add(new ScreenPlacement
            {
                ScreenId = "b", Planned = true, PlannedWidth = 1920, PlannedHeight = 1080, Enabled = true, CustomLabel = "Stage left",
            });
            vm.ReconcilePlacements();
            vm.SelectPage(Shell.IndexOf("Audio"));
            Dispatcher.UIThread.RunJobs();

            // The block is there, and the desk opens listening to the programme.
            var picker = window.GetVisualDescendants().OfType<ComboBox>().FirstOrDefault(x => x.Name == "MonitorSource");
            Assert.NotNull(picker);
            Assert.Equal(AudioMonitor.Program, vm.State.Monitor.Source);
            Assert.Equal("PGM", vm.MonitorWord);
            Assert.Contains("the programme", vm.MonitorWords);
            Assert.False(vm.MonitorIsOutput);

            // Picking an output shows its picker on the click, not on the next poll.
            var raised = new List<string>();
            vm.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");
            vm.State.Monitor.Source = AudioMonitor.Output;
            Assert.True(vm.MonitorIsOutput);
            Assert.Contains(nameof(vm.MonitorIsOutput), raised);
            Assert.Contains("pick which one", vm.MonitorWords);

            vm.State.Monitor.OutputId = "b";
            Assert.Contains("Stage left", vm.MonitorWords);
            Assert.Equal("STAGE LEFT", vm.MonitorWord);
            Assert.Contains(vm.MonitorOutputs, t => t.ScreenId == "b");

            vm.State.Monitor.Source = AudioMonitor.Silent;
            Assert.Equal("SILENT", vm.MonitorWord);
            Assert.False(vm.MonitorIsOutput);
        }
        finally
        {
            b.Dispose();
        }
    }
}
