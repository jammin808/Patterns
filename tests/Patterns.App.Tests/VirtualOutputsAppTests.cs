using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 78: an NDI send and the stream are live outputs of the picture they carry — the programme, a
/// screen's own picture through its mirror chain, a canvas — read from each sender's source like a
/// window's, never a blanket "the programme leaves the machine". The rule the sound follows reads them:
/// a sender on a screen's own picture keeps that picture's clip heard and leaves the programme's silent;
/// a sender on the programme keeps the programme heard with no window open.
/// </summary>
public class VirtualOutputsAppTests
{
    private static List<ScreenInfo> ThreeScreens() => new()
    {
        new("a", "Left", new PixelRect(0, 0, 1920, 1080), 1.0, true, 0),
        new("b", "Right", new PixelRect(3000, 0, 1920, 1080), 1.0, false, 1),
        new("c", "Lobby", new PixelRect(6000, 0, 1920, 1080), 1.0, false, 2),
    };

    private static void Rig(TestApp.Booted b)
    {
        var fakes = ThreeScreens();
        b.Services.Screens.All.Clear();
        foreach (var s in fakes) b.Services.Screens.All.Add(s);
        b.Vm.State.Output.Placements.Clear();
        b.Vm.ReconcilePlacements(fakes);
        foreach (var p in b.Vm.State.Output.Placements) p.Enabled = true;
        b.Vm.RebuildSwitcherTiles(fakes);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void AnNdiSenderIsALiveOutputOfThePictureItCarriesNotOfTheProgrammeByDefault()
    {
        var b = TestApp.Boot("patterns-virtual-outputs-");
        try
        {
            var (services, vm, _) = b;
            Rig(b);
            var state = vm.State;
            Assert.False(services.Outputs.IsLive);
            Assert.False(services.ShownLive().AnyLive);

            // Screen b has a picture of its own; a sender carries screen b. The programme is on no live output.
            ContentTargets.EnsureAssignment(state, "b");
            ContentTargets.SetOwnPattern(state, "b", true);
            var sender = new NdiSenderConfig { Id = "s1", Name = "Graphics", Enabled = true, SourceScreenId = "b" };
            state.Ndi.Senders.Add(sender);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("s1", services.Ndi.ActiveIds);
            var live = services.ShownLive();
            Assert.True(live.AnyLive);
            Assert.False(live.ProgrammeLive, "a sender on a screen's own picture is not the programme leaving the machine");
            Assert.Contains("b", live.OwnTargets);

            // The same sender on the programme: the programme is heard with no window open.
            sender.SourceScreenId = "";
            Dispatcher.UIThread.RunJobs();
            live = services.ShownLive();
            Assert.True(live.ProgrammeLive);
            Assert.Empty(live.OwnTargets);

            // On a repeater of b: the chain resolves to b's own picture.
            state.Output.Placements.First(p => p.ScreenId == "c").MirrorOf = "b";
            sender.SourceScreenId = "c";
            Dispatcher.UIThread.RunJobs();
            live = services.ShownLive();
            Assert.False(live.ProgrammeLive);
            Assert.Contains("b", live.OwnTargets);

            // Screen b back on the programme: the sender on c (through b) is the programme again.
            ContentTargets.SetOwnPattern(state, "b", false);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.ShownLive().ProgrammeLive);

            // The sender switched off: nothing leaves the machine.
            sender.Enabled = false;
            Dispatcher.UIThread.RunJobs();
            Assert.DoesNotContain("s1", services.Ndi.ActiveIds);
            Assert.False(services.ShownLive().AnyLive);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheStreamIsALiveOutputOfItsSourceScreen()
    {
        var b = TestApp.Boot("patterns-virtual-stream-");
        try
        {
            var (services, vm, _) = b;
            Rig(b);
            var state = vm.State;
            ContentTargets.EnsureAssignment(state, "b");
            ContentTargets.SetOwnPattern(state, "b", true);

            // The stream on screen b's picture: b's own picture is live, the programme is not.
            state.Stream.SourceScreenId = "b";
            state.Stream.Active = true;
            Dispatcher.UIThread.RunJobs();
            var live = services.ShownLive();
            Assert.True(live.AnyLive);
            Assert.False(live.ProgrammeLive);
            Assert.Contains("b", live.OwnTargets);

            // The stream's default source is the first enabled display's picture — screen a, on the programme.
            state.Stream.SourceScreenId = "";
            Dispatcher.UIThread.RunJobs();
            live = services.ShownLive();
            Assert.True(live.ProgrammeLive);
            Assert.Empty(live.OwnTargets);

            state.Stream.Active = false;
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.ShownLive().AnyLive);
        }
        finally
        {
            b.Dispose();
        }
    }
}
