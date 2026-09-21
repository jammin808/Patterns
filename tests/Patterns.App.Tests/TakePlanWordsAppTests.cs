using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 80.2: on a live desk, a tile whose own picture is on the air and unchanged in the preview is named before
/// the press as keeping its picture; an edit to that picture makes it a taken target again.
/// </summary>
public class TakePlanWordsAppTests
{
    private static List<ScreenInfo> ThreeScreens() => new()
    {
        new("a", "Left", new PixelRect(0, 0, 1920, 1080), 1.0, true, 0),
        new("b", "Right", new PixelRect(3000, 0, 1920, 1080), 1.0, false, 1),
        new("c", "Lobby", new PixelRect(6000, 0, 1920, 1080), 1.0, false, 2),
    };

    [AvaloniaFact]
    public void ThePlanNamesTheOwnTileTheTakeLeavesAloneAndStopsWhenItIsEdited()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            var fakes = ThreeScreens();
            services.Screens.All.Clear();
            foreach (var s in fakes) services.Screens.All.Add(s);
            vm.State.Output.Placements.Clear();
            vm.ReconcilePlacements(fakes);
            foreach (var p in vm.State.Output.Placements) p.Enabled = true;
            vm.RebuildSwitcherTiles(fakes);
            Dispatcher.UIThread.RunJobs();
            vm.IsSandboxActive = false;
            vm.State.Pattern.Kind = PatternKind.Grid;
            Dispatcher.UIThread.RunJobs();
            vm.IsSandboxActive = true;
            Dispatcher.UIThread.RunJobs();

            var before = services.Actions.PlanTake(FadeScope.Everything);
            Assert.Empty(before.KeepsOwnLabels);
            Assert.DoesNotContain("own picture", before.Words);

            // b takes the preview as its own picture: on the air and in the preview alike — settled.
            var take = services.Actions.Execute(ShowActionKind.ScreenTake, ActionOrigin.Desk, "b");
            Assert.True(take.Ok, take.Message);
            Dispatcher.UIThread.RunJobs();
            var settled = services.Actions.PlanTake(FadeScope.Everything);
            Assert.Contains("b", settled.Taken);                                          // carried by the send, as round 30 has it
            var label = settled.KeepsOwnLabels.Single();
            Assert.Contains("Right", label);
            Assert.Contains($"{label} keeps its own picture (OWN)", settled.Words);
            Assert.StartsWith("→ ", settled.Words, StringComparison.Ordinal);
            Assert.DoesNotContain($"→ {label}", settled.Words);                            // not listed as a picture that moves

            // One edit to b's own picture in the preview: pending, so the take lands it — a taken target again.
            var own = vm.State.Independent.Single(a => a.ScreenId == "b").Pattern;
            own.Grid.CellSize += 7;
            var pending = services.Actions.PlanTake(FadeScope.Everything);
            Assert.Empty(pending.KeepsOwnLabels);
            Assert.Contains("b", pending.Taken);
            Assert.DoesNotContain("own picture", pending.Words);
        }
        finally
        {
            b.Dispose();
        }
    }
}
