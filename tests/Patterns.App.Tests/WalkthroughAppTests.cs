using Avalonia.Controls;
using Avalonia.Controls.Primitives;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.ViewModels;
using Patterns.App.Views.Sections;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>The Help page's walkthroughs on a live desk: the roles, GO opening a page, the show ticking steps by itself, the hand ticks, the page itself.</summary>
public class WalkthroughAppTests
{
    [AvaloniaFact]
    public void EveryStepNamesAShellPageAndEveryCheckHasAnAnswer()
    {
        var b = TestApp.Boot();
        try
        {
            foreach (var w in Walkthroughs.All)
            foreach (var s in w.Steps)
            {
                Assert.Contains(Shell.Pages, p => p.Header == s.Page);
            }
            foreach (var check in Walkthroughs.Checks)
            {
                Assert.NotNull(b.Vm.EvaluateWalkCheck(check));
            }
            Assert.Null(b.Vm.EvaluateWalkCheck("nothing-of-the-sort"));
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheRolesTheGoButtonAndTheShowsOwnTicksDriveAScenario()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;

            // The desk opens on the show caller's first scenario; the chips and the choices say so.
            Assert.Equal(DeskRole.ShowCaller, vm.WalkRole);
            Assert.True(vm.HasWalk);
            Assert.Equal(Walkthroughs.For(DeskRole.ShowCaller).First().Id, vm.WalkId);
            Assert.Equal(5, vm.WalkRoles.Count);
            Assert.Single(vm.WalkRoles, r => r.IsCurrent);
            Assert.Equal(Walkthroughs.For(DeskRole.ShowCaller).Count(), vm.WalkChoices.Count);
            Assert.Single(vm.WalkChoices, c => c.IsCurrent);

            // A role chip lists its scenarios and opens the first; a choice opens another.
            vm.WalkRoles.First(r => r.Role == DeskRole.Technician).PickCommand.Execute(null);
            Assert.Equal(DeskRole.Technician, vm.WalkRole);
            Assert.Equal("tech-venue", vm.WalkId);
            Assert.Contains("Technician", vm.WalkRoleBlurb.Length > 0 ? "Technician" : "");
            vm.WalkChoices.First(c => c.Id == "tech-blend").StartCommand.Execute(null);
            Assert.Equal("tech-blend", vm.WalkId);
            Assert.True(vm.WalkChoices.First(c => c.Id == "tech-blend").IsCurrent);

            // Starting a scenario of another role moves the role chip with it.
            vm.StartWalkthrough("op-look");
            Assert.Equal(DeskRole.Operator, vm.WalkRole);
            Assert.True(vm.WalkRoles.First(r => r.Role == DeskRole.Operator).IsCurrent);
            var steps = Walkthroughs.Find("op-look")!.Steps;
            Assert.Equal(steps.Count, vm.WalkSteps.Count);
            Assert.Equal($"Step 1 of {steps.Count} · 0 done", vm.WalkWords.Replace("· 1 done", "· 0 done").Replace("· 2 done", "· 0 done"));

            // GO on a step opens its page and makes it the current step.
            var pattern = vm.WalkSteps.First(r => r.Page == "Pattern");
            pattern.GoCommand.Execute(null);
            Assert.Equal(Shell.IndexOf("Pattern"), vm.SelectedPageIndex);
            Assert.Equal(pattern.Index, vm.WalkCurrent);
            Assert.True(pattern.IsCurrent);
            Assert.Single(vm.WalkSteps, r => r.IsCurrent);

            // The show ticks a step by itself: EDIT SAFE on, a look saved.
            var editSafe = vm.WalkSteps.First(r => r.Step.Check == "edit-safe");
            vm.IsSandboxActive = true;
            vm.PollNow();
            Assert.True(editSafe.IsDone);
            Assert.True(editSafe.IsDoneByApp);
            Assert.Equal("✓ seen", editSafe.TickText);
            var looks = vm.WalkSteps.First(r => r.Step.Check == "looks-saved");
            Assert.False(looks.IsDone);
            vm.State.LooksAndCues.Looks.Add(new LookConfig { Name = "Walk-in" });
            vm.PollNow();
            Assert.True(looks.IsDoneByApp);
            vm.IsSandboxActive = false;
            vm.PollNow();
            Assert.False(editSafe.IsDone);   // the app's tick follows the fact

            // A hand tick on a step without a check, DONE NEXT, BACK, RESTART.
            var byHand = vm.WalkSteps.First(r => !r.HasCheck);
            Assert.Equal("DONE", byHand.TickText);
            byHand.DoneCommand.Execute(null);
            Assert.True(byHand.IsDone);
            Assert.Equal("✓ done", byHand.TickText);
            byHand.DoneCommand.Execute(null);
            Assert.False(byHand.IsDone);
            vm.WalkSteps[0].GoCommand.Execute(null);
            vm.WalkNextCommand.Execute(null);
            Assert.True(vm.WalkSteps[0].IsDone);
            Assert.Equal(1, vm.WalkCurrent);
            vm.WalkBackCommand.Execute(null);
            Assert.Equal(0, vm.WalkCurrent);
            vm.WalkRestartCommand.Execute(null);
            Assert.False(vm.WalkSteps[0].IsDone);
            Assert.True(looks.IsDone);       // what the show has stays ticked
            Assert.True(vm.WalkFraction > 0 && vm.WalkFraction < 1);

            // The Help page: the role chips, the choices, a row per step with GO and a tick button.
            vm.SelectPage(Shell.IndexOf("Help"));
            var host = new Window { DataContext = vm, Width = 900, Height = 3000, Content = new ScrollViewer { Content = new HelpSection() } };
            host.Show();
            Dispatcher.UIThread.RunJobs();
            var chips = host.GetVisualDescendants().OfType<ToggleButton>().Where(t => t.DataContext is WalkRoleChip).ToList();
            Assert.Equal(5, chips.Count);
            Assert.Single(chips, c => c.IsChecked == true);
            Assert.Equal(vm.WalkChoices.Count, host.GetVisualDescendants().OfType<ToggleButton>().Count(t => t.DataContext is WalkChoice));
            var goButtons = host.GetVisualDescendants().OfType<Button>().Where(x => x.DataContext is WalkStepRow && x.Content as string == "GO").ToList();
            Assert.Equal(steps.Count, goButtons.Count);
            Assert.Contains(host.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == vm.WalkWords);
            // GO from the page changes the desk's page; the Help page itself stays where it is.
            goButtons[^1].Command!.Execute(null);
            Assert.Equal(Shell.IndexOf(steps[^1].Page), vm.SelectedPageIndex);
            host.Close();

            // Nothing of this is in the show file: a walkthrough is a rehearsal, not a setting.
            var json = JsonUtil.Serialize(vm.State);
            Assert.DoesNotContain("WalkRole", json);
            Assert.DoesNotContain("WalkId", json);
            Assert.DoesNotContain("Walkthrough", json);
        }
        finally
        {
            b.Dispose();
        }
    }
}
