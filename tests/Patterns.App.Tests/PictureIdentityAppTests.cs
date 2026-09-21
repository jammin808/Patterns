using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 79.6: the sandbox's picture identities are memoised on the two change counters — an own picture edited a
/// moment ago reads as changed at once, before any publish — and the desk publishes the programme and the sandbox
/// as one pair, the sandbox's generation right after the programme's.
/// </summary>
public class PictureIdentityAppTests
{
    private static List<ScreenInfo> ThreeScreens() => new()
    {
        new("a", "Left", new PixelRect(0, 0, 1920, 1080), 1.0, true, 0),
        new("b", "Right", new PixelRect(3000, 0, 1920, 1080), 1.0, false, 1),
        new("c", "Lobby", new PixelRect(6000, 0, 1920, 1080), 1.0, false, 2),
    };

    [AvaloniaFact]
    public void AnEditedOwnPictureReadsAsChangedBeforeThePublishAndTheDeskPublishesOnePair()
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

            // b takes the preview as its own picture — on air and in the edited state alike: settled.
            var take = services.Actions.Execute(ShowActionKind.ScreenTake, ActionOrigin.Desk, "b");
            Assert.True(take.Ok, take.Message);
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.Sandbox.IsStaged("b"));
            Assert.Equal(SandboxService.TakeEffect.Nothing, services.Sandbox.EffectOf("b"));
            Assert.Equal(1, services.Sandbox.AlreadyOnAir(new[] { "a" }));            // a follows the programme, whose preview is still the air's picture: counted
            Assert.Equal(1, services.Sandbox.AlreadyOnAir(new[] { "b" }));

            // One property of b's own picture edited in the state, and asked again with nothing run between: the memo
            // keyed on the change counters has moved on, so the edit reads as pending at once.
            var own = vm.State.Independent.Single(a => a.ScreenId == "b").Pattern;
            var was = own.Grid.CellSize;
            own.Grid.CellSize = was + 7;
            Assert.True(services.Sandbox.IsStaged("b"));
            Assert.Equal(SandboxService.TakeEffect.Picture, services.Sandbox.EffectOf("b"));
            Assert.Equal(0, services.Sandbox.AlreadyOnAir(new[] { "b" }));
            own.Grid.CellSize = was;                                                  // put back: settled again, from the same counters
            Assert.False(services.Sandbox.IsStaged("b"));
            Assert.Equal(SandboxService.TakeEffect.Nothing, services.Sandbox.EffectOf("b"));
            own.Grid.CellSize = was + 7;

            // The publish that follows is one pair: the sandbox's generation right after the programme's, both readable together.
            Dispatcher.UIThread.RunJobs();
            var pair = services.Bus.Pair;
            Assert.NotNull(pair.Sandbox);
            Assert.Same(pair.Current, services.Bus.Current);
            Assert.Same(pair.Sandbox, services.Bus.Sandbox);
            Assert.Equal(pair.Current.Version + 1, pair.Sandbox!.Version);
            Assert.Equal(was + 7, pair.Sandbox.PatternFor("b").Grid.CellSize);
            Assert.Equal(was, pair.Current.PatternFor("b").Grid.CellSize);
        }
        finally
        {
            b.Dispose();
        }
    }
}
