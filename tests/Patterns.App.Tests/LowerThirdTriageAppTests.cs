using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.Services;
using Patterns.App.Views.Sections;
using Patterns.Core.LowerThirds;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The lower-thirds triage on a live desk: with EDIT SAFE open an edit reaches the air copy on
/// AIR again and on UPDATE, a picture TAKE keeps the lower third on air where it is in its life,
/// the preview / take sign-off flow, the show's default design, and the pages' controls.
/// </summary>
public class LowerThirdTriageAppTests
{
    [AvaloniaFact]
    public void AnAirAgainAnUpdateAndAPictureTakeAllCarryTheDesignOnAirWithEditSafeOpen()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            var router = new CommandRouter(services);
            vm.IsSandboxActive = true;
            Dispatcher.UIThread.RunJobs();
            var neon = vm.NewLowerThird("Neon");
            neon.InMs = 0;
            neon.OutMs = 0;
            vm.ShowLowerThird(neon);
            var air = services.AirState.LowerThirds;
            Assert.NotSame(vm.State.LowerThirds, air);
            var copy = air.Find(neon.Id)!;
            Assert.NotSame(neon, copy);
            var shownAt = air.ShownAtUtc;
            Assert.NotNull(shownAt);
            var before = copy.PersonName;

            // An edit on the desk is not on air until it is pushed — and the desk, the remotes say so.
            neon.PersonName = "Alice Fixed";
            vm.RefreshTallies();
            Assert.True(vm.LowerThirdAirEdited);
            Assert.Contains("EDITED", vm.LowerThirdStatus);
            Assert.Contains("\"lowerThirdEdited\":true", router.StateJson());
            Assert.Equal(before, services.AirState.LowerThirds.Active!.PersonName);

            // UPDATE ON AIR: the copy changes in place — the same row, the same instants, so no leaving and arriving again.
            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("LT UPDATE"))));
            Dispatcher.UIThread.RunJobs();
            air = services.AirState.LowerThirds;
            Assert.Equal("Alice Fixed", air.Active!.PersonName);
            Assert.Equal(shownAt, air.ShownAtUtc);
            Assert.Equal(neon.Id, air.ActiveId);
            Assert.Same(air.Active, Assert.Single(air.Designs, d => d.Id == neon.Id));
            Assert.Equal("Alice Fixed", services.Bus.Current.State.LowerThirds.Active!.PersonName);   // the outputs' snapshot
            vm.RefreshTallies();
            Assert.False(vm.LowerThirdAirEdited);
            Assert.Contains("\"lowerThirdEdited\":false", router.StateJson());
            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("LT UPDATE"))));   // nothing to push: still OK

            // AIR again: the copy is refreshed from the edited design (the old desk kept the stale copy: shown on the desk, not on the output).
            neon.PersonRole = "Chief Fixer";
            vm.ShowLowerThird(neon);
            Assert.Equal("Chief Fixer", services.AirState.LowerThirds.Active!.PersonRole);
            Assert.True(services.AirState.LowerThirds.IsShowing);
            Assert.Equal("Chief Fixer", services.Bus.Current.State.LowerThirds.Active!.PersonRole);

            // A picture TAKE keeps it on air, where it is in its life — the edited state was never told, and used to drop it.
            var taken = services.AirState.LowerThirds.ShownAtUtc;
            Assert.False(vm.State.LowerThirds.IsShowing);
            vm.ActivePattern.Kind = PatternKind.Grid;
            Dispatcher.UIThread.RunJobs();
            vm.TakeCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var afterTake = services.AirState.LowerThirds;
            Assert.True(afterTake.IsShowing);
            Assert.Equal(neon.Id, afterTake.ActiveId);
            Assert.Equal(taken, afterTake.ShownAtUtc);
            Assert.Equal(neon.Id, services.Bus.Current.State.LowerThirds.ActiveId);
            Assert.True(services.Bus.Current.State.LowerThirds.IsShowing);
            Assert.Equal(PatternKind.Grid, services.Bus.Current.State.Pattern.Kind);

            // Switching EDIT SAFE off keeps it too (the discard restores the air, instants and all), and an update then has nothing to do.
            vm.IsSandboxActive = false;
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.State.LowerThirds.IsShowing);
            Assert.Equal(taken, vm.State.LowerThirds.ShownAtUtc);
            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("LT UPDATE"))));
            vm.RefreshTallies();
            Assert.False(vm.LowerThirdAirEdited);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void PreviewThenTakeIsTheSignOffFlowAndNeedsEditSafe()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            var router = new CommandRouter(services);
            vm.IsSandboxActive = false;
            var neon = vm.NewLowerThird("Neon");
            neon.InMs = 0;
            neon.OutMs = 0;
            var bob = vm.NewEntry("Bob Builder");
            bob.Role = "Site manager";

            // Without EDIT SAFE there is no preview to put it in: refused, and the desk says why.
            Assert.StartsWith("ERR", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("LT PREVIEW 1"))));
            Assert.StartsWith("ERR", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("LT TAKE"))));
            Assert.False(vm.State.LowerThirds.IsShowing);
            vm.RefreshTallies();
            Assert.Contains("EDIT SAFE is off", vm.LowerThirdPreviewText);
            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("LT PREVIEW OFF"))));   // nothing to clear is not an error

            vm.IsSandboxActive = true;
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("LT PREVIEW 1 WITH Bob Builder"))));
            Dispatcher.UIThread.RunJobs();
            // The edited state is the preview: it shows there, the frozen program does not have it, the sandbox snapshot carries it.
            Assert.True(vm.State.LowerThirds.IsShowing);
            Assert.Equal("Bob Builder", neon.PersonName);
            Assert.False(services.AirState.LowerThirds.IsShowing);
            Assert.Equal(neon.Id, services.Bus.Sandbox!.State.LowerThirds.ActiveId);
            Assert.NotEqual(neon.Id, services.Bus.Current.State.LowerThirds.ActiveId);
            var json = router.StateJson();
            Assert.Contains("\"lowerThirdPreview\":\"Neon\"", json);
            Assert.Contains("\"lowerThirdPreviewPerson\":\"Bob Builder\"", json);
            Assert.Contains("\"lowerThird\":\"\"", json);
            vm.RefreshTallies();
            Assert.True(neon.IsInPreview);
            Assert.False(neon.IsOnAir);
            Assert.Equal("IN PREVIEW", neon.PreviewText);
            Assert.True(vm.HasLowerThirdInPreview);
            Assert.Contains("In preview: Neon — Bob Builder", vm.LowerThirdPreviewText);

            // TAKE: to air afresh, the preview clears.
            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("LT TAKE"))));
            Dispatcher.UIThread.RunJobs();
            var air = services.AirState.LowerThirds;
            Assert.True(air.IsShowing);
            Assert.Equal(neon.Id, air.ActiveId);
            Assert.Equal("Bob Builder", air.Active!.PersonName);
            Assert.False(vm.State.LowerThirds.IsShowing);
            json = router.StateJson();
            Assert.Contains("\"lowerThird\":\"Neon\"", json);
            Assert.Contains("\"lowerThirdPerson\":\"Bob Builder\"", json);
            Assert.Contains("\"lowerThirdPreview\":\"\"", json);
            vm.RefreshTallies();
            Assert.True(neon.IsOnAir);
            Assert.False(neon.IsInPreview);
            Assert.False(vm.HasLowerThirdInPreview);
            Assert.StartsWith("ERR", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("LT TAKE"))));   // nothing left to take

            // The desk's own buttons: the next person into the same design in the preview while the first is on air; cleared; previewed; taken.
            var cat = vm.NewEntry("Cat Reader");
            vm.PreviewEntry(cat, neon);
            Assert.True(vm.State.LowerThirds.IsShowing);
            Assert.Equal("Cat Reader", neon.PersonName);
            Assert.Equal("Bob Builder", services.AirState.LowerThirds.Active!.PersonName);   // the air copy keeps its person
            vm.RefreshTallies();
            Assert.True(neon.IsInPreview && neon.IsOnAir);
            Assert.True(vm.LowerThirdAirEdited);   // the design now differs from the copy on air — the desk says so
            vm.ClearLowerThirdPreviewCommand.Execute(null);
            Assert.False(vm.State.LowerThirds.IsShowing);
            Assert.True(services.AirState.LowerThirds.IsShowing);   // the air was never touched
            vm.PreviewLowerThirdCommand.Execute(neon);
            Assert.True(vm.State.LowerThirds.IsShowing);
            vm.TakeLowerThirdCommand.Execute(null);
            Assert.Equal("Cat Reader", services.AirState.LowerThirds.Active!.PersonName);
            Assert.False(vm.State.LowerThirds.IsShowing);

            // A cue's two steps: a person to preview with no design named goes into the one on air, then the take.
            Assert.True(services.Actions.Execute(ShowActionKind.LowerThirdPreview, ActionOrigin.Desk, "", bob.Id).Ok);
            Assert.Equal("Bob Builder", neon.PersonName);
            Assert.True(vm.State.LowerThirds.IsShowing);
            Assert.True(services.Actions.Execute(ShowActionKind.LowerThirdTake, ActionOrigin.Desk).Ok);
            Assert.Equal("Bob Builder", services.AirState.LowerThirds.Active!.PersonName);
            Assert.StartsWith("ERR", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("LT PREVIEW 1 WITH Nobody Here"))));   // a stranger never reaches the preview either
            vm.IsSandboxActive = false;
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheShowsDefaultDesignTakesThePeopleWhenNoneIsOnAir()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            var router = new CommandRouter(services);
            vm.IsSandboxActive = false;
            var clean = vm.NewLowerThird("Clean");
            Assert.Equal(clean.Id, vm.State.LowerThirds.DefaultDesignId);   // the first design is the default
            var neon = vm.NewLowerThird("Neon");
            foreach (var d in new[] { clean, neon })
            {
                d.InMs = 0;
                d.OutMs = 0;
            }
            Assert.Equal(clean.Id, vm.State.LowerThirds.DefaultDesignId);
            vm.SetDefaultLowerThirdCommand.Execute(neon);
            Assert.Equal(neon.Id, vm.State.LowerThirds.DefaultDesignId);
            Assert.True(neon.IsDefault);
            Assert.False(clean.IsDefault);
            Assert.Contains("\"lowerThirdDefault\":\"Neon\"", router.StateJson());
            var bob = vm.NewEntry("Bob Builder");

            // PERSON with nothing on air: into the default.
            Assert.Equal("OK", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("PERSON 1"))));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(neon.Id, vm.State.LowerThirds.ActiveId);
            Assert.Equal("Bob Builder", neon.PersonName);

            // With Clean on air the person goes into Clean — the one on air wins over the default.
            vm.ShowLowerThird(clean);
            var ann = vm.NewEntry("Ann Other");
            Assert.True(services.Actions.Execute(ShowActionKind.LowerThirdShow, ActionOrigin.Desk, "", ann.Id).Ok);
            Assert.Equal(clean.Id, vm.State.LowerThirds.ActiveId);
            Assert.Equal("Ann Other", clean.PersonName);
            vm.HideLowerThird();

            // Hidden again: back to the default, from the Show panel's PEOPLE chips too.
            vm.ShowEntryOnAirCommand.Execute(bob);
            Assert.Equal(neon.Id, vm.State.LowerThirds.ActiveId);
            Assert.True(vm.State.LowerThirds.IsShowing);

            // The show file keeps the choice and none of the tallies; a deleted default moves to the first design left, and a deleted design on air leaves.
            var json = JsonUtil.Serialize(vm.State);
            Assert.Contains($"\"DefaultDesignId\": \"{neon.Id}\"", json);
            Assert.DoesNotContain("IsDefault", json);
            Assert.DoesNotContain("IsInPreview", json);
            vm.DeleteLowerThirdCommand.Execute(neon);
            Assert.Equal(clean.Id, vm.State.LowerThirds.DefaultDesignId);
            Assert.False(vm.State.LowerThirds.IsShowing);
            Assert.True(clean.IsDefault);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ThePagesCarryTheSignOffControls()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            var services = b.Services;
            vm.IsSandboxActive = true;
            Dispatcher.UIThread.RunJobs();
            var neon = vm.NewLowerThird("Neon");
            vm.NewLowerThird("Clean");
            neon.InMs = 0;
            vm.PreviewLowerThird(neon);
            vm.RefreshTallies();

            var page = new Window { DataContext = vm, Width = 900, Height = 3000, Content = new ScrollViewer { Content = new LowerThirdsSection() } };
            page.Show();
            Dispatcher.UIThread.RunJobs();
            var buttons = page.GetVisualDescendants().OfType<Button>().ToList();
            Assert.Equal(2, buttons.Count(x => x.Content as string == "PVW" && x.DataContext is LowerThirdDesign));
            Assert.Equal(2, buttons.Count(x => x.Content as string == "AIR" && x.DataContext is LowerThirdDesign));
            var stars = page.GetVisualDescendants().OfType<ToggleButton>().Where(x => x.Content as string == "★").ToList();
            Assert.Equal(2, stars.Count);
            Assert.Single(stars, x => x.IsChecked == true);   // the first design made is the default
            Assert.Contains(buttons, x => x.Content as string == "TAKE TO AIR" && x.IsVisible);
            Assert.Contains(buttons, x => x.Content as string == "CLEAR PREVIEW" && x.IsVisible);
            Assert.Contains(buttons, x => x.Content as string == "UPDATE ON AIR" && !x.IsVisible);   // nothing on air to update
            Assert.Contains(page.GetVisualDescendants().OfType<Border>(), x => x.Classes.Contains("tallyChip") && x.Classes.Contains("pvw") && x.IsVisible);
            Assert.Contains(page.GetVisualDescendants().OfType<TextBlock>(), x => (x.Text ?? "").StartsWith("In preview: Neon"));
            page.Close();

            var panel = new Window { DataContext = vm, Width = 900, Height = 2400, Content = new ScrollViewer { Content = new ShowSection() } };
            panel.Show();
            Dispatcher.UIThread.RunJobs();
            var chips = panel.GetVisualDescendants().OfType<Button>().Where(x => x.DataContext is LowerThirdDesign).ToList();
            Assert.Equal(2, chips.Count);
            Assert.Single(chips, x => x.Classes.Contains("pvw"));
            Assert.Contains(panel.GetVisualDescendants().OfType<Button>(), x => x.Content as string == "TAKE TO AIR");
            Assert.Contains(panel.GetVisualDescendants().OfType<ToggleButton>(), x => x.Content as string == "PVW FIRST");
            panel.Close();

            // PVW FIRST: the panel's chips go to the preview instead of air; off, they go to air.
            vm.ClearLowerThirdPreview();
            Assert.False(vm.State.LowerThirds.IsShowing);
            vm.LowerThirdChipsToPreview = true;
            vm.ChipLowerThirdCommand.Execute(neon);
            Assert.True(vm.State.LowerThirds.IsShowing);
            Assert.False(services.AirState.LowerThirds.IsShowing);
            vm.LowerThirdChipsToPreview = false;
            vm.ChipLowerThirdCommand.Execute(neon);
            Assert.True(services.AirState.LowerThirds.IsShowing);
            vm.IsSandboxActive = false;
        }
        finally
        {
            b.Dispose();
        }
    }
}
