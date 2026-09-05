using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The caller's home from the desk: a sheet imported and appended, the quick look and actions,
/// the plan fields, an export, the Run surface reading the clock, the caller's edits, a follow
/// that fires by itself through the gate and stops when the caller steps in, and the remote seeing it all.
/// </summary>
public class CallerHomeTests
{
    private static LookConfig SaveLook(MainViewModel vm, string name, PatternKind kind)
    {
        vm.ActivePattern.Kind = kind;
        vm.NewLookName = name;
        vm.SaveLookCommand.Execute(null);
        return LookService.Find(vm.State, name)!;
    }

    private static string TempCsv(string text)
    {
        var path = Path.Combine(Path.GetTempPath(), "patterns-sheet-" + Guid.NewGuid().ToString("N") + ".csv");
        File.WriteAllText(path, text);
        return path;
    }

    [AvaloniaFact]
    public void ASheetBecomesTheStackAndTheCuesPageEditsThePlan()
    {
        var b = TestApp.Boot();
        var sheet = TempCsv("Number,Name,Start,Duration,Look,Mark,Follow\n01.010,Welcome,09:00,10:00,Keynote,,\n01.020,Talk,09:10,20:00,,,\n01.030,Coffee,09:30,15:00,,break,\n");
        var more = TempCsv("Name,Duration\nWrap,5 min\n");
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var keynote = SaveLook(vm, "Keynote", PatternKind.ColorBars);
            var stack = CueStacks.Caller(vm.State);
            stack.Cues.Add(new RunCueConfig { Number = "00.010", Name = "Old" });
            vm.Cues.OnShowLoaded();

            var status = vm.ImportCueSheetFrom(sheet, append: false);
            Assert.StartsWith("Imported 3 cues from 3 rows", status);
            Assert.Equal(3, stack.Cues.Count);                                  // the old cue is gone
            Assert.Equal(keynote.Id, stack.Cues[0].Actions.Single().Target);
            Assert.Equal(("09:00", 600), (stack.Cues[0].PlannedStart, stack.Cues[0].PlannedSeconds));
            Assert.Equal(CueMark.Break, stack.Cues[2].Mark);
            Assert.StartsWith("Imported 3 cues", vm.Cues.LastImportReport);
            Assert.Same(stack.Cues[0], vm.Cues.SelectedCue);

            Assert.StartsWith("Appended 1 cue from 1 row", vm.ImportCueSheetFrom(more, append: true));
            Assert.Equal(4, stack.Cues.Count);
            Assert.Equal("01.040", stack.Cues[3].Number);                       // numbering continued
            Assert.Equal(300, stack.Cues[3].PlannedSeconds);
            Assert.Contains("Could not read", vm.ImportCueSheetFrom(Path.Combine(Path.GetTempPath(), "nope-" + Guid.NewGuid().ToString("N") + ".csv"), append: true));

            // The selected cue: a look in one pick, an action in one press, the plan fields.
            vm.Cues.SelectedCue = stack.Cues[1];
            vm.Cues.QuickLook = vm.Cues.QuickLooks.Single(l => l.Id == keynote.Id);
            Assert.Equal(CueActionKind.ApplyLook, stack.Cues[1].Actions[0].Kind);
            Assert.Equal(keynote.Id, stack.Cues[1].Actions[0].Target);
            vm.Cues.QuickActionCommand.Execute("CountdownStart");
            Assert.Equal(CueActionKind.CountdownStart, stack.Cues[1].Actions[^1].Kind);
            Assert.Contains("Start countdown added", vm.StatusMessage);
            vm.Cues.QuickActionCommand.Execute("LowerThirdShow");
            Assert.Equal(CueActionKind.LowerThirdShow, stack.Cues[1].Actions[^1].Kind);
            Assert.Contains("pick its target", vm.StatusMessage);
            vm.Cues.SelectedDurationText = "5 min";
            Assert.Equal(300, stack.Cues[1].PlannedSeconds);
            Assert.Equal("5:00", vm.Cues.SelectedDurationText);
            vm.Cues.SelectedStartText = "9.45";
            Assert.Equal("09:45", stack.Cues[1].PlannedStart);
            vm.Cues.SelectedFollowText = "0";
            Assert.Equal(0, stack.Cues[1].FollowSeconds);
            vm.Cues.SelectedFollowText = "";
            Assert.Null(stack.Cues[1].FollowSeconds);
            vm.Cues.SelectedDurationText = "later";                                  // refused, kept
            Assert.Equal(300, stack.Cues[1].PlannedSeconds);
            Assert.Contains("not a length", vm.StatusMessage);
            vm.Cues.SelectedMark = vm.Cues.Marks.Single(m => m.Id == nameof(CueMark.Lunch));
            Assert.Equal(CueMark.Lunch, stack.Cues[1].Mark);

            // The export carries it all back out.
            var back = CsvTable.Parse(vm.Cues.ExportCsv());
            Assert.Equal(4, back.Rows.Count);
            Assert.Equal("Keynote", back.Get(1, "Look"));
            Assert.Equal("Start countdown", back.Get(1, "Action"));
            Assert.Equal("lunch", back.Get(1, "Mark"));
            Assert.Equal("5:00", back.Get(1, "Duration"));

            // The remote's list carries the plan and changes its revision for it.
            var router = new CommandRouter(services);
            var json = router.CueListJson();
            Assert.Contains("\"plannedStart\":\"09:45\"", json);
            Assert.Contains("\"plannedSeconds\":300", json);
            Assert.Contains("\"mark\":\"lunch\"", json);
            Assert.Contains("\"timing\":{", router.StateJson());
        }
        finally
        {
            File.Delete(sheet);
            File.Delete(more);
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheRunSurfaceReadsTheClockAndTheCallersEditsMoveTheDay()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var a = SaveLook(vm, "A", PatternKind.ColorBars);
            vm.ActivePattern.Kind = PatternKind.Grid;
            var stack = CueStacks.Caller(vm.State);
            var wall = DateTime.Now;
            var now = new DateTime(wall.Year, wall.Month, wall.Day, wall.Hour, wall.Minute, 0);   // whole minutes, like a running order
            // A was planned five minutes ago; B and C follow with lengths; Coffee is the next mark.
            RunCueConfig Cue(string number, string name, TimeSpan startFromNow, int? seconds, CueMark mark = CueMark.None)
            {
                var cue = new RunCueConfig { Number = number, Name = name, PlannedStart = CueTiming.FormatClock((now + startFromNow).TimeOfDay), PlannedSeconds = seconds, Mark = mark };
                cue.Actions.Add(new CueActionConfig { Kind = CueActionKind.ApplyLook, Target = a.Id });
                stack.Cues.Add(cue);
                return cue;
            }
            Cue("01.010", "A", TimeSpan.FromMinutes(-5), 600);
            var cueB = Cue("01.020", "B", TimeSpan.FromMinutes(5), 600);
            Cue("01.030", "C", TimeSpan.FromMinutes(15), 600);
            Cue("01.040", "Coffee", TimeSpan.FromMinutes(25), 900, CueMark.Break);
            Dispatcher.UIThread.RunJobs();
            var cues = services.CueStack;
            vm.Run.Refresh();
            Assert.True(vm.Run.HasPlan);
            Assert.Contains("Next break", vm.Run.ScheduleSummary);
            Assert.Matches("^[56] MIN LATE$", vm.Run.OffsetText);   // nothing has run and A was due five minutes ago (the clock's seconds round)
            Assert.True(vm.Run.Rows[0].HasPlan);
            Assert.Equal("Not behind the plan — nothing to catch up.", cues.CatchUp(ActionOrigin.Desk, now - TimeSpan.FromMinutes(6)));

            // GO on A now: it started five minutes after its plan — late — and the estimates carry that.
            cues.SetArmed(true, ActionOrigin.Desk);
            Assert.True(cues.Go(ActionOrigin.Desk).Ok);
            vm.Run.Tick();
            Assert.Matches("^[56] MIN LATE$", vm.Run.OffsetText);
            Assert.True(vm.Run.IsLate);
            Assert.True(cues.Timing().IsLate);
            Assert.Matches(@"\+[56] min", vm.Run.StandbyPlanText);
            Assert.Contains("expected", vm.Run.StandbyPlanText);
            Assert.Contains("10:00", vm.Run.StandbyPlanText);      // B's planned length

            // +1 / −1 minute move every planned start from the standby cue on; A stays.
            var bBefore = cueB.PlannedStart;
            Assert.Contains("3 planned starts moved +1 min", cues.ShiftPlan(TimeSpan.FromMinutes(1), ActionOrigin.Desk));
            Assert.Equal(CueTiming.FormatClock(CueTiming.ParseClock(bBefore)!.Value + TimeSpan.FromMinutes(1)), cueB.PlannedStart);
            Assert.Equal(CueTiming.FormatClock((now - TimeSpan.FromMinutes(5)).TimeOfDay), stack.Cues[0].PlannedStart);
            cues.ShiftPlan(TimeSpan.FromMinutes(-1), ActionOrigin.Desk);
            Assert.Equal(bBefore, cueB.PlannedStart);

            // Catch up: the lateness comes off B and C, in proportion, before the coffee break.
            var behind = (int)Math.Round(cues.Timing().Offset!.Value.TotalSeconds);
            Assert.InRange(behind, 290, 370);
            var caught = cues.CatchUp(ActionOrigin.Desk);
            Assert.StartsWith("Caught up", caught);
            Assert.Equal(1200 - behind, cueB.PlannedSeconds + stack.Cues[2].PlannedSeconds);
            Assert.Equal(900, stack.Cues[3].PlannedSeconds);

            // Resume now: B is planned for the clock and the rest of the day moves with it.
            var resumed = cues.ResumeNow(ActionOrigin.Desk, now + TimeSpan.FromMinutes(9));
            Assert.Contains($"{cueB.Number} now planned for {CueTiming.FormatClock((now + TimeSpan.FromMinutes(9)).TimeOfDay)}", resumed);
            Assert.Equal(CueTiming.FormatClock((now + TimeSpan.FromMinutes(9)).TimeOfDay), cueB.PlannedStart);
            Assert.Equal(CueTiming.FormatClock((now + TimeSpan.FromMinutes(29)).TimeOfDay), stack.Cues[3].PlannedStart);   // moved by the same four minutes
            Assert.Contains(services.Journal.Tail(8), r => r.Kind == "PlanCatchUp");
            Assert.Contains(services.Journal.Tail(8), r => r.Kind == "PlanResume");
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AutoFollowFiresTheNextCueThroughTheGateAndTheCallerCanStopIt()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var a = SaveLook(vm, "A", PatternKind.ColorBars);
            var bb = SaveLook(vm, "B", PatternKind.Focus);
            var c = SaveLook(vm, "C", PatternKind.Grid);
            vm.ActivePattern.Kind = PatternKind.FlatField;
            var stack = CueStacks.Caller(vm.State);
            RunCueConfig Cue(string number, string name, LookConfig look, int? follow)
            {
                var cue = new RunCueConfig { Number = number, Name = name, FollowSeconds = follow };
                cue.Actions.Add(new CueActionConfig { Kind = CueActionKind.ApplyLook, Target = look.Id });
                stack.Cues.Add(cue);
                return cue;
            }
            var cueA = Cue("01.010", "A", a, 0);        // fires B at once
            var cueB = Cue("01.020", "B", bb, 2);       // fires C two seconds later
            var cueC = Cue("01.030", "C", c, null);
            var cueD = Cue("01.040", "D", a, 5);
            var cueE = Cue("01.050", "E", bb, null);
            Dispatcher.UIThread.RunJobs();

            var cues = services.CueStack;
            var t = new DateTime(2026, 9, 5, 19, 0, 0, DateTimeKind.Utc);
            cues.SetArmed(true, ActionOrigin.Desk);
            Assert.True(cues.Go(ActionOrigin.Desk, nowUtc: t).Ok);
            Dispatcher.UIThread.RunJobs();

            // A fired, and B with it (follow 0): the picture is B's, standby is C, the follow to C is pending.
            Assert.Equal(PatternKind.Focus, services.Bus.Current.State.Pattern.Kind);
            Assert.Equal(cueB.Id, cues.Runtime.LastCueId);
            Assert.Equal(cueC.Id, cues.Runtime.StandbyCueId);
            Assert.Equal("follow", cues.History[0].Origin);
            Assert.Equal("01.020 B", cues.History[0].Label);
            Assert.Equal("01.010 A", cues.History[1].Label);
            Assert.Equal(cueC.Id, cues.Runtime.FollowCueId);
            Assert.Equal("AUTO 01.030 in 0:02", cues.FollowText(t));
            vm.Run.Tick();
            Assert.True(vm.Run.HasFollow);

            // Not yet due: nothing; due: C fires by itself and leaves D on standby with no follow pending.
            cues.Poll(t + TimeSpan.FromSeconds(1));
            Assert.Equal(cueB.Id, cues.Runtime.LastCueId);
            cues.Poll(t + TimeSpan.FromSeconds(3));
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(cueC.Id, cues.Runtime.LastCueId);
            Assert.Equal(PatternKind.Grid, services.Bus.Current.State.Pattern.Kind);
            Assert.Equal(cueD.Id, cues.Runtime.StandbyCueId);
            Assert.Null(cues.Runtime.FollowDueUtc);
            Assert.Equal("", cues.FollowText(t));

            // D's follow: the caller moving standby cancels it; so does HOLD; so does disarming.
            var t2 = t + TimeSpan.FromSeconds(10);
            Assert.True(cues.Go(ActionOrigin.Desk, nowUtc: t2).Ok);
            Assert.Equal(cueE.Id, cues.Runtime.FollowCueId);
            cues.Standby(cueA.Id);
            Assert.Null(cues.Runtime.FollowDueUtc);
            cues.Poll(t2 + TimeSpan.FromSeconds(6));
            Assert.Equal(cueD.Id, cues.Runtime.LastCueId);           // nothing fired by itself

            cues.Standby(cueD.Id);
            var t3 = t2 + TimeSpan.FromSeconds(20);
            Assert.True(cues.Go(ActionOrigin.Desk, nowUtc: t3).Ok);
            Assert.NotNull(cues.Runtime.FollowDueUtc);
            cues.SetHold(true, ActionOrigin.Desk);
            Assert.Null(cues.Runtime.FollowDueUtc);
            cues.SetHold(false, ActionOrigin.Desk);

            cues.Standby(cueD.Id);
            var t4 = t3 + TimeSpan.FromSeconds(20);
            Assert.True(cues.Go(ActionOrigin.Desk, nowUtc: t4).Ok);
            Assert.NotNull(cues.Runtime.FollowDueUtc);
            cues.SetArmed(false, ActionOrigin.Desk);
            Assert.Null(cues.Runtime.FollowDueUtc);

            // The Run surface's STOP FOLLOW does the same.
            cues.SetArmed(true, ActionOrigin.Desk);
            cues.Standby(cueD.Id);
            Assert.True(cues.Go(ActionOrigin.Desk, nowUtc: t4 + TimeSpan.FromSeconds(20)).Ok);
            Assert.NotNull(cues.Runtime.FollowDueUtc);
            vm.Run.CancelFollowCommand.Execute(null);
            Assert.Null(cues.Runtime.FollowDueUtc);
            Assert.Contains("cancelled", vm.StatusMessage);
        }
        finally
        {
            b.Dispose();
        }
    }
}
