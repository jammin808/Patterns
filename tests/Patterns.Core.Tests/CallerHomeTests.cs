using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The caller's home: clock and length spellings, a sheet becoming cues, the template and a
/// round trip, the day estimated from the plan and the clock, and the caller's edits.
/// </summary>
public class CallerHomeTests
{
    private static readonly DateTime Day = new(2026, 9, 5);

    private static RunCueConfig Cue(string number, string name, string start = "", int? seconds = null, CueMark mark = CueMark.None, int? follow = null)
        => new() { Number = number, Name = name, PlannedStart = start, PlannedSeconds = seconds, Mark = mark, FollowSeconds = follow };

    private static List<RunCueConfig> Morning() => new()
    {
        Cue("01.010", "A", "09:00", 600),
        Cue("01.020", "B", "09:10", 1200),
        Cue("01.030", "Coffee", "09:30", 900, CueMark.Break),
        Cue("02.010", "D", "09:45", 3600),
        Cue("02.020", "Lunch", "10:45", null, CueMark.Lunch),
        Cue("03.010", "Close", "12:00", 300, CueMark.End),
    };

    [Fact]
    public void ClockAndLengthSpellingsAreRead()
    {
        Assert.Equal(new TimeSpan(9, 30, 0), CueTiming.ParseClock("9:30"));
        Assert.Equal(new TimeSpan(9, 30, 15), CueTiming.ParseClock("09:30:15"));
        Assert.Equal(new TimeSpan(9, 30, 0), CueTiming.ParseClock("9.30"));
        Assert.Equal(new TimeSpan(9, 30, 0), CueTiming.ParseClock("0930"));
        Assert.Equal(new TimeSpan(14, 15, 0), CueTiming.ParseClock("2:15 pm"));
        Assert.Equal(new TimeSpan(0, 5, 0), CueTiming.ParseClock("12:05 am"));
        Assert.Null(CueTiming.ParseClock("abc"));
        Assert.Null(CueTiming.ParseClock("25:00"));
        Assert.Null(CueTiming.ParseClock(""));

        Assert.Equal(720, CueTiming.ParseDuration("12:00"));
        Assert.Equal(3723, CueTiming.ParseDuration("1:02:03"));
        Assert.Equal(300, CueTiming.ParseDuration("5 min"));
        Assert.Equal(300, CueTiming.ParseDuration("5m"));
        Assert.Equal(90, CueTiming.ParseDuration("90s"));
        Assert.Equal(300, CueTiming.ParseDuration("300"));
        Assert.Equal(3600, CueTiming.ParseDuration("1h"));
        Assert.Equal(5400, CueTiming.ParseDuration("1h30"));
        Assert.Equal(150, CueTiming.ParseDuration("2.5 min"));
        Assert.Equal(0, CueTiming.ParseDuration("0"));
        Assert.Null(CueTiming.ParseDuration("soon"));

        Assert.Equal("1:02:03", CueTiming.FormatDuration(3723));
        Assert.Equal("1:05", CueTiming.FormatDuration(65));
        Assert.Equal("01:30", CueTiming.FormatClock(TimeSpan.FromHours(25.5)));
        Assert.Equal("+7 min", CueTiming.FormatDelta(TimeSpan.FromMinutes(7)));
        Assert.Equal("−40 s", CueTiming.FormatDelta(TimeSpan.FromSeconds(-40)));
        Assert.Equal("ON TIME", CueTiming.OffsetText(TimeSpan.FromSeconds(20)));
        Assert.Equal("3 MIN EARLY", CueTiming.OffsetText(TimeSpan.FromMinutes(-3)));
        Assert.Equal("45 S LATE", CueTiming.OffsetText(TimeSpan.FromSeconds(45)));
    }

    [Fact]
    public void ASheetBecomesCuesWithLooksTimesMarksAndActions()
    {
        var state = new ShowState();
        var walkIn = new LookConfig { Name = "Walk-in" };
        state.LooksAndCues.Looks.Add(walkIn);

        var csv = "Number,Name,Track,Start,Duration,Follow,Mark,Confirm,Look,Action,Target,Value,Notes\n" +
                  "01.010,Walk-in,Video,08:30,30:00,,,,Walk-in,,,,Doors\n" +
                  ",Welcome,,9:00,10 min,,,yes,Keynote,Play audio track,,,\n" +
                  "01.030,Coffee break,,09:10,20:00,0,,,,Start countdown,,20,\n" +
                  "02.010,Lunch,,12:15,1h,5 s,lunch,,,Lower third on,Nobody,,\n";
        var result = CueSheet.Import(CsvTable.Parse(csv), state);
        Assert.Equal(4, result.Cues.Count);
        Assert.Equal(4, result.Rows);

        var c0 = result.Cues[0];
        Assert.Equal(("01.010", "Walk-in", "Video", "08:30", 1800, CueMark.None, "Doors"), (c0.Number, c0.Name, c0.Track, c0.PlannedStart, c0.PlannedSeconds, c0.Mark, c0.Notes));
        var look = Assert.Single(c0.Actions);
        Assert.Equal(CueActionKind.ApplyLook, look.Kind);
        Assert.Equal(walkIn.Id, look.Target);

        var c1 = result.Cues[1];
        Assert.Equal("01.020", c1.Number);              // no number in the sheet: it continues from the row above
        Assert.Equal("09:00", c1.PlannedStart);          // "9:00" tidied
        Assert.Equal(600, c1.PlannedSeconds);            // "10 min"
        Assert.True(c1.RequireConfirm);
        Assert.Equal(2, c1.Actions.Count);
        Assert.Equal("Keynote", c1.Actions[0].Target);   // kept by name: reads as broken until the look exists
        Assert.Equal(CueActionKind.AudioPlay, c1.Actions[1].Kind);
        Assert.Contains(result.Notes, n => n.Contains("Keynote"));

        var c2 = result.Cues[2];
        Assert.Equal(0, c2.FollowSeconds);
        Assert.Equal(CueMark.None, c2.Mark);             // a Mark column exists, so names are not guessed at
        var countdown = Assert.Single(c2.Actions);
        Assert.Equal((CueActionKind.CountdownStart, "20"), (countdown.Kind, countdown.Value));

        var c3 = result.Cues[3];
        Assert.Equal(5, c3.FollowSeconds);
        Assert.Equal(CueMark.Lunch, c3.Mark);
        Assert.Equal(3600, c3.PlannedSeconds);
        Assert.Equal(CueActionKind.LowerThirdShow, Assert.Single(c3.Actions).Kind);
        Assert.Contains(result.Notes, n => n.Contains("Nobody"));
        Assert.StartsWith("4 cues from 4 rows — 2 notes", result.Summary);

        // Without a Mark column the names are read, whole words only, and the report says so.
        var guessed = CueSheet.Import(CsvTable.Parse("Name\nCoffee break\nBreakout session\nLunch\nEnd of the day\n"), state, "05.010");
        Assert.Equal(new[] { CueMark.Break, CueMark.None, CueMark.Lunch, CueMark.End }, guessed.Cues.Select(c => c.Mark));
        Assert.Equal("05.020", guessed.Cues[0].Number);
        Assert.Contains(guessed.Notes, n => n.StartsWith("No Mark column"));

        // A sheet without the columns says why nothing came of it.
        Assert.Empty(CueSheet.Import(CsvTable.Parse("Foo,Bar\n1,2\n"), state).Cues);
        Assert.Empty(CueSheet.Import(TableData.Empty, state).Cues);
        Assert.Equal(CueActionKind.ApplyLook, CueSheet.ParseKind("apply look"));
        Assert.Equal(CueActionKind.StingerFire, CueSheet.ParseKind("VOG"));
        Assert.Equal(CueActionKind.LowerThirdShow, CueSheet.ParseKind("LowerThirdShow"));
        Assert.Null(CueSheet.ParseKind("dance"));
    }

    [Fact]
    public void TheTemplateReadsBackAndAnExportRoundTrips()
    {
        var state = new ShowState();
        var table = CsvTable.Parse(CueSheet.Template());
        Assert.Equal(CueSheet.Headers, table.Headers);
        var imported = CueSheet.Import(table, state);
        Assert.Equal(7, imported.Cues.Count);
        Assert.Equal(("01.010", "Walk-in", "08:30", 1800), (imported.Cues[0].Number, imported.Cues[0].Name, imported.Cues[0].PlannedStart, imported.Cues[0].PlannedSeconds));
        Assert.Equal(CueMark.Break, imported.Cues[2].Mark);
        Assert.Equal(3600, imported.Cues[4].PlannedSeconds);
        Assert.Contains(imported.Cues[4].Actions, a => a.Kind == CueActionKind.CountdownStart && a.Value == "60");
        Assert.Equal(0, imported.Cues[5].FollowSeconds);
        Assert.Equal(CueMark.End, imported.Cues[5].Mark);
        Assert.Contains(imported.Cues[6].Actions, a => a.Kind == CueActionKind.BlackoutOff);

        var keynote = new LookConfig { Name = "Keynote" };
        state.LooksAndCues.Looks.Add(keynote);
        var stack = new CueStackConfig();
        var cue = Cue("01.010", "Welcome, all", "09:00", 720, CueMark.Break, 5);
        cue.Track = "Video";
        cue.RequireConfirm = true;
        cue.Notes = "Take on the \"go\" word";
        cue.Actions.Add(new CueActionConfig { Kind = CueActionKind.ApplyLook, Target = keynote.Id });
        cue.Actions.Add(new CueActionConfig { Kind = CueActionKind.AudioVolume, Value = "80" });
        stack.Cues.Add(cue);
        stack.Cues.Add(Cue("01.020", "Plain"));

        var csv = CueSheet.Export(state, stack);
        var back = CsvTable.Parse(csv);
        Assert.Equal(2, back.Rows.Count);
        Assert.Equal("Keynote", back.Get(0, "Look"));
        Assert.Equal("Audio volume", back.Get(0, "Action"));
        Assert.Equal("80", back.Get(0, "Value"));
        Assert.Equal("12:00", back.Get(0, "Duration"));
        Assert.Equal("5", back.Get(0, "Follow"));
        Assert.Equal("break", back.Get(0, "Mark"));
        Assert.Equal("yes", back.Get(0, "Confirm"));
        Assert.Equal("Welcome, all", back.Get(0, "Name"));

        var again = CueSheet.Import(back, state);
        var c = again.Cues[0];
        Assert.Equal((cue.Number, cue.Name, cue.Track, cue.PlannedStart, cue.PlannedSeconds, cue.FollowSeconds, cue.Mark, true, cue.Notes),
            (c.Number, c.Name, c.Track, c.PlannedStart, c.PlannedSeconds, c.FollowSeconds, c.Mark, c.RequireConfirm, c.Notes));
        Assert.Equal(keynote.Id, c.Actions[0].Target);
        Assert.Equal((CueActionKind.AudioVolume, "80"), (c.Actions[1].Kind, c.Actions[1].Value));
        Assert.Empty(again.Notes);
    }

    [Fact]
    public void TheDayIsEstimatedFromThePlanAndTheClock()
    {
        var cues = Morning();
        // A started three minutes late and has a minute of its plan left; B is on standby.
        var report = CueTiming.Estimate(cues, cues[0].Id, Day + new TimeSpan(9, 3, 0), cues[1].Id, Day + new TimeSpan(9, 12, 0));
        Assert.Equal(TimeSpan.FromMinutes(3), report.Offset);
        Assert.Equal("3 MIN LATE", report.OffsetText);
        Assert.True(report.IsLate);
        Assert.Equal(TimeSpan.FromMinutes(1), report.RunningRemaining);
        Assert.False(report.RunningOverran);
        var b = report.For(cues[1].Id)!;
        Assert.Equal(new TimeSpan(9, 13, 0), b.EstimatedAt);
        Assert.Equal(TimeSpan.FromMinutes(3), b.Delta);
        Assert.False(b.Uncertain);
        Assert.True(report.For(cues[0].Id)!.Past);
        Assert.Equal(new TimeSpan(9, 33, 0), report.NextBreak!.EstimatedAt);
        Assert.Equal("≈ 09:33 (planned 09:30, +3 min)", report.NextBreak.Text);
        Assert.Equal(new TimeSpan(10, 48, 0), report.Lunch!.EstimatedAt);          // 09:13 + 20 + 15 + 60 min
        Assert.Equal(new TimeSpan(12, 3, 0), report.End!.EstimatedAt);             // lunch has no length: the gap to Close's start
        Assert.Equal(CueMark.End, report.End.Mark);
        Assert.False(report.End.Uncertain);
        Assert.StartsWith("Next break ≈ 09:33 (planned 09:30, +3 min)  ·  Lunch ≈ 10:48 (planned 10:45, +3 min)  ·  End ≈ 12:03", report.Summary);

        // A has overrun its ten minutes: what is left is zero and everything after is "at least".
        var over = CueTiming.Estimate(cues, cues[0].Id, Day + new TimeSpan(9, 3, 0), cues[1].Id, Day + new TimeSpan(9, 20, 0));
        Assert.Equal(TimeSpan.Zero, over.RunningRemaining);
        Assert.True(over.RunningOverran);
        Assert.Equal(new TimeSpan(9, 20, 0), over.For(cues[1].Id)!.EstimatedAt);
        Assert.True(over.NextBreak!.Uncertain);
        Assert.StartsWith("≥ 09:40", over.NextBreak.Text);

        // Nothing has run yet, two minutes before the plan: the standby cue's expected start reads early.
        var early = CueTiming.Estimate(cues, null, null, cues[0].Id, Day + new TimeSpan(8, 58, 0));
        Assert.Equal("2 MIN EARLY", early.OffsetText);
        Assert.True(early.IsEarly);
        Assert.Equal(new TimeSpan(8, 58, 0), early.For(cues[0].Id)!.EstimatedAt);
        Assert.Null(early.RunningRemaining);

        // No plan at all: nothing to compare, the end is the last cue and unknown.
        var bare = new List<RunCueConfig> { Cue("01.010", "X"), Cue("01.020", "Y") };
        var none = CueTiming.Estimate(bare, null, null, bare[0].Id, Day + new TimeSpan(9, 12, 0));
        Assert.Null(none.Offset);
        Assert.Equal("", none.OffsetText);
        Assert.Null(none.NextBreak);
        Assert.Equal(CueMark.None, none.End!.Mark);
        Assert.True(none.End.Uncertain);
        Assert.StartsWith("Last cue done ≥ 09:12", none.Summary);
        Assert.Same(TimingReport.Empty, CueTiming.Estimate(new List<RunCueConfig>(), null, null, null, Day));

        // A disabled cue is skipped by the estimates and never a mark.
        cues[2].Enabled = false;
        var skipped = CueTiming.Estimate(cues, null, null, cues[0].Id, Day + new TimeSpan(9, 0, 0));
        Assert.Null(skipped.NextBreak);
        Assert.Equal(new TimeSpan(9, 30, 0), skipped.For(cues[3].Id)!.EstimatedAt);   // A 10 + B 20, no coffee
    }

    [Fact]
    public void TheCallersEditsMoveTheDay()
    {
        var cues = Morning();
        Assert.Equal(5, CueTiming.Shift(cues, 1, TimeSpan.FromMinutes(1)));
        Assert.Equal("09:00", cues[0].PlannedStart);
        Assert.Equal("09:11", cues[1].PlannedStart);
        Assert.Equal("12:01", cues[5].PlannedStart);

        // Resume now: B is planned for 09:20 and everything after moves the same nine minutes.
        Assert.Equal(5, CueTiming.Rebase(cues, 1, new TimeSpan(9, 20, 0)));
        Assert.Equal("09:20", cues[1].PlannedStart);
        Assert.Equal("09:40", cues[2].PlannedStart);
        Assert.Equal("12:10", cues[5].PlannedStart);
        var plain = new List<RunCueConfig> { Cue("01.010", "X"), Cue("01.020", "Y", "10:00") };
        Assert.Equal(1, CueTiming.Rebase(plain, 0, new TimeSpan(9, 1, 30)));
        Assert.Equal("09:02", plain[0].PlannedStart);
        Assert.Equal("10:00", plain[1].PlannedStart);

        // Catch up six minutes before the coffee break: A and B give it up in proportion to their room.
        var day = Morning();
        Assert.Equal(360, CueTiming.CatchUp(day, 0, TimeSpan.FromMinutes(6)));
        Assert.Equal(482, day[0].PlannedSeconds);   // 600 − round(570 × 360 / 1740)
        Assert.Equal(958, day[1].PlannedSeconds);   // 1200 − the rest
        Assert.Equal(900, day[2].PlannedSeconds);   // the break itself is untouched
        Assert.Equal(3600, day[3].PlannedSeconds);  // beyond the mark: untouched

        // More than there is to give: every cue stops at its floor.
        var squeezed = Morning();
        Assert.Equal(1740, CueTiming.CatchUp(squeezed, 0, TimeSpan.FromMinutes(40)));
        Assert.Equal(CueTiming.MinSeconds, squeezed[0].PlannedSeconds);
        Assert.Equal(CueTiming.MinSeconds, squeezed[1].PlannedSeconds);
        Assert.Equal(0, CueTiming.CatchUp(squeezed, 0, TimeSpan.FromMinutes(1)));
        Assert.Equal(0, CueTiming.CatchUp(Morning(), 0, TimeSpan.Zero));

        // Starting on the mark itself: the stretch is the mark and what follows up to the next one.
        var fromBreak = Morning();
        Assert.Equal(120, CueTiming.CatchUp(fromBreak, 2, TimeSpan.FromMinutes(2)));
        Assert.Equal(600 + 3600 + 900 - 120, fromBreak[2].PlannedSeconds + fromBreak[3].PlannedSeconds + fromBreak[0].PlannedSeconds);
    }
}
