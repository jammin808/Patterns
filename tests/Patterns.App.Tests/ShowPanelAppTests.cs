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
/// The Show panel as the control surface on a live desk: a look on one screen alone from the wire,
/// a cue and the panel's own row — every other screen untouched, the program back on request, a
/// whole-look recall sweeping the send and a lock keeping it, the wrong screen and the wrong look
/// refused, the journal; the cue editor's look picker; the cue strip's NEXT; PROGRESSION; the page.
/// </summary>
public class ShowPanelAppTests
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

    private static LookConfig SaveLook(MainViewModel vm, string name, PatternKind kind)
    {
        vm.ActivePattern.Kind = kind;
        vm.NewLookName = name;
        vm.SaveLookCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        return LookService.Find(vm.State, name) ?? throw new InvalidOperationException($"look '{name}' was not saved");
    }

    private static PatternKind? OwnKind(ShowState state, string screenId)
        => state.Independent.FirstOrDefault(a => a.ScreenId == screenId)?.Pattern.Kind;

    [AvaloniaFact]
    public void ALookLandsOnOneScreenAloneFromTheWireACueAndThePanelAndTheProgramComesBack()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            vm.IsSandboxActive = false;
            vm.State.Transition.Enabled = false;
            var router = new CommandRouter(services);

            // A planned side screen standing on its own (a new one lands flush beside the last, which would join it into a canvas).
            var side = vm.AddPlannedScreen(1920, 1080, "Side");
            side.X = 0;
            side.Y = 8000;
            Dispatcher.UIThread.RunJobs();
            vm.RebuildSwitcherTiles();
            var daytime = SaveLook(vm, "Daytime", PatternKind.Grid);
            var sponsor = SaveLook(vm, "Sponsor", PatternKind.ColorBars);
            vm.ApplyLookCommand.Execute(daytime);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.Grid, vm.State.Pattern.Kind);
            Assert.False(side.UseCustomPattern);

            var n = Rig.OrderedLivePlacements(vm.State, services.Screens.All).FindIndex(x => x.Placement.ScreenId == side.ScreenId) + 1;
            Assert.True(n >= 1);

            // The wire: Sponsor on the side screen alone; the program — and so every other screen — keeps Daytime.
            Assert.StartsWith("OK", Send(router, $"SCREEN {n} LOOK Sponsor"));
            Assert.True(side.UseCustomPattern);
            Assert.Equal(PatternKind.ColorBars, OwnKind(vm.State, side.ScreenId));
            Assert.Equal(PatternKind.Grid, vm.State.Pattern.Kind);
            Assert.Contains(services.Journal.Tail(6), e => e.Kind == nameof(ShowActionKind.ScreenLook));

            Assert.StartsWith("OK", Send(router, $"SCREEN {n} PROGRAM"));
            Assert.False(side.UseCustomPattern);
            Assert.Null(OwnKind(vm.State, side.ScreenId));
            Assert.StartsWith("OK", Send(router, $"SCREEN {n} PROGRAM"));                    // already there: said, not an error

            // The wrong screen and the wrong look are refused, and nothing moves.
            Assert.StartsWith("ERR", Send(router, "SCREEN 99 LOOK Sponsor"));
            Assert.StartsWith("ERR", Send(router, $"SCREEN {n} LOOK Nobody"));
            Assert.False(side.UseCustomPattern);

            // A cue's action, by the look's id, through the executor.
            var own = new CueActionConfig { Kind = CueActionKind.ScreenLook, Target = side.ScreenId, Value = sponsor.Id };
            Assert.True(services.Actions.Execute(ShowActions.ToShowAction(own), new ActionOrigin(OriginKind.Cue, "01.010")).Ok);
            Assert.Equal(PatternKind.ColorBars, OwnKind(vm.State, side.ScreenId));
            var back = new CueActionConfig { Kind = CueActionKind.ScreenProgram, Target = side.ScreenId };
            Assert.True(services.Actions.Execute(ShowActions.ToShowAction(back), new ActionOrigin(OriginKind.Cue, "01.020")).Ok);
            Assert.False(side.UseCustomPattern);

            // The panel's row: nothing without a look chosen, then the send, then PROGRAM.
            vm.RebuildSwitcherTiles();
            var tile = vm.SwitcherTiles.Single(t => t.TargetId == side.ScreenId);
            Assert.False(tile.HasPendingLook);
            tile.SendLookCommand.Execute(null);
            Assert.False(side.UseCustomPattern);
            Assert.Contains("Pick a look", vm.StatusMessage);
            tile.PendingLook = sponsor;
            Assert.True(tile.HasPendingLook);
            tile.SendLookCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(side.UseCustomPattern);
            Assert.Equal(PatternKind.ColorBars, OwnKind(vm.State, side.ScreenId));
            Assert.True(vm.SwitcherTiles.Single(t => t.TargetId == side.ScreenId).IsOwn);
            Assert.Contains("alone", vm.StatusMessage);

            // A whole-look recall sweeps the send; a lock keeps it.
            vm.ApplyLookCommand.Execute(daytime);
            Dispatcher.UIThread.RunJobs();
            Assert.False(side.UseCustomPattern);
            tile.SendLookCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.ColorBars, OwnKind(vm.State, side.ScreenId));
            ScreenRoles.SetLocked(vm.State, side.ScreenId, true);
            vm.ApplyLookCommand.Execute(daytime);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.ColorBars, OwnKind(vm.State, side.ScreenId));
            ScreenRoles.SetLocked(vm.State, side.ScreenId, false);

            vm.SwitcherTiles.Single(t => t.TargetId == side.ScreenId).ProgramCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(side.UseCustomPattern);
            Assert.Contains("program", vm.StatusMessage);
        }
        finally
        {
            b.Window.Close();
            b.Services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void TheCueEditorPicksALookTheStripNamesTheNextCueAndThePanelReadsAsOneSurface()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            vm.IsSandboxActive = false;
            var side = vm.AddPlannedScreen(1920, 1080, "Side");
            side.X = 0;
            side.Y = 8000;
            Dispatcher.UIThread.RunJobs();
            vm.RebuildSwitcherTiles();
            Assert.Contains(vm.SwitcherTiles, t => t.TargetId == side.ScreenId);
            var sponsor = SaveLook(vm, "Sponsor", PatternKind.ColorBars);

            // The caller's stack: two cues; the first on standby names the second as NEXT.
            var stack = services.CueStack.Stack;
            var open = new RunCueConfig { Number = "1", Name = "Open" };
            var loop = new RunCueConfig { Number = "2", Name = "Sponsor loop" };
            stack.Cues.Add(open);
            stack.Cues.Add(loop);
            services.CueStack.Standby(open.Id);
            vm.Run.Refresh();
            Assert.Equal("1  Open", vm.Run.StandbyText);
            Assert.True(vm.Run.HasNext);
            Assert.Equal("2  Sponsor loop", vm.Run.NextText);
            services.CueStack.Standby(loop.Id);
            vm.Run.Refresh();
            Assert.False(vm.Run.HasNext);
            Assert.Equal("", vm.Run.NextText);

            // The cue editor: a Screen — its own look action offers the looks (no "as designed" row) and keeps the look's id.
            vm.Cues.SelectedStack = stack;
            vm.Cues.SelectedCue = loop;
            vm.Cues.AddActionCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            var row = vm.Cues.ActionRows.Last();
            row.SelectedKind = row.KindChoices.First(k => k.Id == nameof(CueActionKind.ScreenLook));
            Assert.True(row.HasPersonValue);
            Assert.True(row.HasLookValue);
            Assert.False(row.HasTextValue);
            Assert.Equal("Which look…", row.PickHint);
            Assert.Null(row.SelectedPerson);
            Assert.DoesNotContain(row.PersonChoices, p => p.Id.Length == 0);
            var pick = row.PersonChoices.Single(p => p.Id == sponsor.Id);
            Assert.Equal("Sponsor", pick.Label);
            row.SelectedPerson = pick;
            Assert.Equal(sponsor.Id, row.Action.Value);
            Assert.Equal(CueActionKind.ScreenLook, row.Action.Kind);

            // The people picker is as it was.
            row.SelectedKind = row.KindChoices.First(k => k.Id == nameof(CueActionKind.LowerThirdShow));
            Assert.True(row.HasPersonValue);
            Assert.False(row.HasLookValue);
            Assert.Contains(row.PersonChoices, p => p.Id.Length == 0);
            Assert.Equal("Who… (blank = as designed)", row.PickHint);

            // PROGRESSION carries the clicker's place.
            Assert.Contains(vm.PresenterStepText, vm.ProgressionText);

            // The page reads as one surface.
            vm.SelectPage(Shell.PanelPage);
            Settle(window);
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains("CUES", texts);
            Assert.Contains("LOOKS", texts);
            Assert.Contains("SCREENS — EACH ON ITS OWN", texts);
            Assert.Contains("PROGRESSION", texts);
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), x => x.Content as string == "→ THIS SCREEN");
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), x => x.Content as string == "PVW");
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), x => x.Content as string == "HOLD");
        }
        finally
        {
            b.Window.Close();
            b.Services.Shutdown();
        }
    }
}
