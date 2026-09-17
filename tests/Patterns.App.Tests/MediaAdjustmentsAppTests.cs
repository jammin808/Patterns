using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 77.5: a mirror ticked on a preset months ago made every web page read backwards on the
/// wall with no trace but a tick three sections down the Media page. Now a recall says it, a tile's
/// take says it, STATE and the Eye carry it, the Media page warns, and RESET ADJUSTMENTS is one press.
/// </summary>
public class MediaAdjustmentsAppTests
{
    [AvaloniaFact]
    public void AMirrorIsSaidOnTheRecallTheTakeStateAndTheEyeAndOnePressPutsItRight()
    {
        var b = TestApp.Boot("patterns-adjust-");
        try
        {
            var (services, vm, _) = b;
            var router = new CommandRouter(services);
            var state = vm.State;

            // A preset saved with Mirror ticked — for a confidence monitor, long ago — recalled onto the programme.
            var preset = new PatternConfig { Kind = PatternKind.Media };
            preset.Media.Source = MediaSource.Web;
            preset.Media.WebUrl = "https://www.youtube.com/watch?v=abc";
            preset.Media.FlipHorizontal = true;
            preset.Media.RotateQuarters = 1;
            services.Store.SavePreset("YouTube", preset);
            var recalled = services.Actions.Execute(new ShowAction(ShowActionKind.PatternPreset, "", "YouTube"), ActionOrigin.Desk);
            Assert.True(recalled.Ok, recalled.Message);
            Assert.EndsWith("The picture is mirrored, turned 90°.", recalled.Message);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("mirrored, turned 90°", state.Pattern.Media.AdjustmentWords());

            // STATE says it of the programme; the Media page says it, and that a mirrored page reads backwards.
            var st = JsonDocument.Parse(router.StateJson()).RootElement;
            Assert.Equal("mirrored, turned 90°", st.GetProperty("adjustments").GetString());
            vm.Media.RefreshCropSummary();
            Assert.Contains("Turned 90°.", vm.Media.CropSummary);
            Assert.Contains("Mirrored.", vm.Media.CropSummary);
            Assert.Contains("reads backwards", vm.Media.CropSummary);

            // One press: the picture as it came, said; STATE follows; a second press has nothing to do.
            vm.Media.ResetAdjustmentsCommand.Execute(null);
            Assert.False(state.Pattern.Media.HasAdjustments);
            Assert.StartsWith("Adjustments reset — the picture was mirrored, turned 90°", vm.StatusMessage);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("", JsonDocument.Parse(router.StateJson()).RootElement.GetProperty("adjustments").GetString());
            vm.Media.RefreshCropSummary();
            Assert.DoesNotContain("Mirrored", vm.Media.CropSummary);
            vm.Media.ResetAdjustmentsCommand.Execute(null);
            Assert.StartsWith("The picture is as it came", vm.StatusMessage);

            // A screen's own picture, upside down with a crop: its STATE row and the Eye's screen node carry the words.
            var target = state.Output.Placements[0].ScreenId;
            ContentTargets.SetOwnPattern(state, target, true);
            var own = ContentTargets.EnsureAssignment(state, target).Pattern;
            own.Kind = PatternKind.Media;
            own.Media.Source = MediaSource.Image;
            own.Media.ImagePath = "/pics/a.png";
            own.Media.FlipVertical = true;
            own.Media.CropLeftPct = 10;
            own.Media.CropRightPct = 30;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("upside down, cropped: keeps 60% × 100% of the picture (from 10% in, 0% down)", own.Media.AdjustmentWords());
            var screen = JsonDocument.Parse(router.StateJson()).RootElement.GetProperty("screens").EnumerateArray().First();
            Assert.StartsWith("upside down, cropped", screen.GetProperty("adjustments").GetString());
            services.Eye.Refresh();
            var node = services.Eye.Graph.Find("screen:" + target);
            Assert.NotNull(node);
            Assert.Contains(node!.Words, w => w.StartsWith("picture upside down, cropped", StringComparison.Ordinal));

            // A tile's CUT of an adjusted preview says it too: EDIT SAFE open, a mirrored picture built, cut to the screen alone.
            ContentTargets.SetOwnPattern(state, target, false);
            Dispatcher.UIThread.RunJobs();
            vm.IsSandboxActive = true;
            Dispatcher.UIThread.RunJobs();
            state.Pattern.Kind = PatternKind.Media;
            state.Pattern.Media.Source = MediaSource.Image;
            state.Pattern.Media.ImagePath = "/pics/b.png";
            state.Pattern.Media.FlipHorizontal = true;
            Dispatcher.UIThread.RunJobs();
            var cut = services.Actions.Execute(ShowActionKind.ScreenCut, ActionOrigin.Desk, target);
            Assert.True(cut.Ok, cut.Message);
            Assert.EndsWith("The picture is mirrored.", cut.Message);
            Assert.True(services.AirState.Independent.Single(a => a.ScreenId == target).Pattern.Media.FlipHorizontal);
        }
        finally
        {
            b.Dispose();
        }
    }
}
