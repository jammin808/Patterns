using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 27: "Different highlights for which Look is live, if a Look is set but one or more screens
/// have changed Patterns within that look, or not live (Also for Patterns on each screen)."
///
/// The Looks page has said PROGRAM · EDITED since round 17 and no remote could see it: the reading
/// was worked out in the view model, for the view model, and STATE carried a plain "air": true that
/// stayed true however much the picture moved afterwards. A Stream Deck at front of house showed
/// solid green over a picture nobody had checked. These pin the fact reaching the wire.
/// </summary>
public class LookTallyWireTests
{
    private static List<ScreenInfo> ThreeScreens() => new()
    {
        new("a", "Left", new Avalonia.PixelRect(0, 0, 1920, 1080), 1.0, true, 0),
        new("b", "Right", new Avalonia.PixelRect(3000, 0, 1920, 1080), 1.0, false, 1),
        new("c", "Lobby", new Avalonia.PixelRect(6000, 0, 1920, 1080), 1.0, false, 2),
    };

    /// <summary>A look of whatever is on screen right now, in the show, the way the desk saves one.</summary>
    private static LookConfig Save(Patterns.App.ViewModels.MainViewModel vm, string name)
    {
        var look = new LookConfig { Name = name, Json = LookService.Capture(vm.State) };
        vm.State.LooksAndCues.Looks.Add(look);
        Dispatcher.UIThread.RunJobs();
        return look;
    }

    private static void Rig(TestApp.Booted b)
    {
        var fakes = ThreeScreens();
        b.Services.Screens.All.Clear();
        foreach (var s in fakes) b.Services.Screens.All.Add(s);
        b.Vm.State.Output.Placements.Clear();
        b.Vm.ReconcilePlacements(fakes);
        foreach (var p in b.Vm.State.Output.Placements) p.Enabled = true;
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void TheWireSaysWhetherTheLookOnAirIsStillWhatIsOnTheScreens()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            Rig(b);
            vm.IsSandboxActive = false;
            vm.State.Pattern.Kind = PatternKind.ColorBars;
            Dispatcher.UIThread.RunJobs();

            var look = Save(vm, "Walk-in");
            services.Actions.Execute(ShowActionKind.ApplyLook, ActionOrigin.Desk, look.Id);
            Dispatcher.UIThread.RunJobs();

            var router = new CommandRouter(services);

            // Up and untouched: on air, not edited, no screen off it.
            var json = router.StateJson();
            Assert.Contains("\"airLook\":\"Walk-in\"", json);
            Assert.Contains("\"lookEdited\":false", json);
            Assert.Contains("\"lookScreensOff\":0", json);
            Assert.False(services.LookTally.AirEdited());

            // Somebody changes the picture. The look is STILL on air — that is the whole point, and
            // why one bit could never say it — but the desk now knows it is not what it saved.
            vm.State.Pattern.Kind = PatternKind.Motion;
            Dispatcher.UIThread.RunJobs();
            json = router.StateJson();
            Assert.Contains("\"airLook\":\"Walk-in\"", json);
            Assert.Contains("\"lookEdited\":true", json);
            Assert.True(services.LookTally.AirEdited());
            // The row itself stays four fields: a row can only be edited if it is the row on air,
            // so the fact rides once rather than on every row four times a second.
            Assert.Contains("\"name\":\"Walk-in\",\"slot\":0,\"air\":true,\"preview\":false}", json);
            Assert.DoesNotContain("\"edited\":true,\"off\"", json);

            // Recall it and the desk is quiet again.
            services.Actions.Execute(ShowActionKind.ApplyLook, ActionOrigin.Desk, look.Id);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("\"lookEdited\":false", router.StateJson());
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void EveryScreenSaysWhatItIsDrawingAndWhetherItHasGoneItsOwnWay()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            Rig(b);
            vm.IsSandboxActive = false;
            vm.State.Pattern.Kind = PatternKind.Grid;
            Dispatcher.UIThread.RunJobs();

            var look = Save(vm, "Rig check");
            services.Actions.Execute(ShowActionKind.ApplyLook, ActionOrigin.Desk, look.Id);
            Dispatcher.UIThread.RunJobs();

            var router = new CommandRouter(services);
            var json = router.StateJson();

            // What each screen is drawing reaches the wire, which it never did: a key could only
            // ever read the programme's kind, so a screen on its own picture was invisible to it.
            Assert.Contains("\"pattern\":\"Grid\"", json);
            Assert.Contains("\"off\":false", json);
            Assert.Empty(services.LookTally.TargetsOffLook());

            // Screen b is given its own picture after the look went up.
            services.EditAir(air =>
            {
                ContentTargets.SetOwnPattern(air, "b", true);
                ContentTargets.EnsureAssignment(air, "b").Pattern.Kind = PatternKind.Focus;
            });
            Dispatcher.UIThread.RunJobs();

            Assert.Equal(new[] { "b" }, services.LookTally.TargetsOffLook());
            Assert.True(services.LookTally.IsOffLook("b"));
            Assert.False(services.LookTally.IsOffLook("a"));

            json = router.StateJson();
            Assert.Contains("\"lookScreensOff\":1", json);
            Assert.Contains("\"pattern\":\"Focus\",\"off\":true", json);
            // And the screens that were left alone say so, so a key can tell one from the other.
            Assert.Contains("\"pattern\":\"Grid\",\"off\":false", json);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheReadingIsOneReadingSoThePageAndTheWireCannotDisagree()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            Rig(b);
            vm.IsSandboxActive = false;
            vm.State.Pattern.Kind = PatternKind.ColorBars;
            Dispatcher.UIThread.RunJobs();
            var look = Save(vm, "Holding");
            services.Actions.Execute(ShowActionKind.ApplyLook, ActionOrigin.Desk, look.Id);
            Dispatcher.UIThread.RunJobs();

            vm.State.Pattern.Kind = PatternKind.Ramp;
            Dispatcher.UIThread.RunJobs();
            vm.RefreshTallies();

            // The page's own words and the wire's flag come off the same reading now. They were two
            // pieces of arithmetic in two places, and two surfaces disagreeing about a fact is
            // worse than neither of them having it.
            Assert.Equal("PROGRAM · EDITED", look.TallyText);
            Assert.True(services.LookTally.AirEdited());
            Assert.Contains("\"lookEdited\":true", new CommandRouter(services).StateJson());
        }
        finally
        {
            b.Dispose();
        }
    }

    /// <summary>
    /// The picker's list and the enum are maintained by hand in two files, and the desk THROWS when
    /// it is asked for the label of a kind the list has not got — MainViewModel names the kind on
    /// the switcher tile through Lists.PatternKinds.First(...), which is an InvalidOperationException
    /// on a miss. Adding a kind and forgetting the list is therefore a crash, not a blank, and this
    /// is the only thing that would catch it.
    /// </summary>
    [AvaloniaFact]
    public void EveryPatternKindIsInThePickerAndNothingElseIs()
    {
        var listed = Patterns.App.ViewModels.Lists.PatternKinds.Select(k => (PatternKind)k.Value!).ToList();
        foreach (var kind in Enum.GetValues<PatternKind>())
        {
            Assert.Contains(kind, listed);
        }
        Assert.Equal(Enum.GetValues<PatternKind>().Length, listed.Count);
        Assert.Equal(listed.Count, listed.Distinct().Count());

        // Every one of them has words an operator reads, and none is left as the enum's own name.
        foreach (var item in Patterns.App.ViewModels.Lists.PatternKinds)
        {
            Assert.False(string.IsNullOrWhiteSpace(item.Label));
        }

        // The first-run default and the tolerant reader's fallback are deliberately different: the
        // desk opens on its own card, while a kind written by a newer build reads as Grid, because
        // the fallback is the FIRST enum member and reordering the enum would change what every
        // unknown value means in every show file ever written.
        Assert.Equal(PatternKind.TestCard, new ShowState().Pattern.Kind);
        Assert.Equal(PatternKind.Grid, Enum.GetValues<PatternKind>()[0]);
        Assert.Equal(PatternKind.Grid, JsonUtil.Deserialize<PatternConfig>("{\"Kind\":\"SomethingFromTheFuture\"}")!.Kind);
    }
}
