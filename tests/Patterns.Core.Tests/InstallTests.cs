using System.IO.Compression;
using System.Text;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The install's clock: days and dates as people write them, windows (past midnight too), which
/// programme wins, when adverts and announcements fire, the day's timeline; the runtime's rules —
/// a programme applied once, an advert at its minute and the programme back after it, an
/// announcement beating an advert, a deferred advert firing when the way clears or being missed,
/// the desk owning the screens, idle outside hours, the clock off; the verbs, the OSC addresses,
/// the cue actions; the passcode gate, the support bundle, the update package and its apply and
/// roll-back, the check-in contract.
/// </summary>
public class InstallTests
{
    private static readonly DateTime Monday = new(2026, 9, 7, 0, 0, 0);   // a Monday

    private static DateTime At(int day, int hour, int minute, int second = 0) => Monday.AddDays(day - 1).AddHours(hour).AddMinutes(minute).AddSeconds(second);

    private static ScheduleSlotConfig Programme(string name, string start, string end, string look = "Daytime", string days = "")
        => new() { Name = name, Kind = SlotKind.Programme, Start = start, End = end, Look = look, Days = days };

    [Fact]
    public void DaysReadLikeARota()
    {
        Assert.True(Schedule.TryParseDays("", out var all));
        Assert.Equal(Schedule.EveryDay, all);
        Assert.True(Schedule.TryParseDays("every day", out all));
        Assert.Equal(Schedule.EveryDay, all);
        Assert.True(Schedule.TryParseDays("Mon–Fri", out var weekdays));
        Assert.Equal(Schedule.Weekdays, weekdays);
        Assert.True(Schedule.TryParseDays("weekdays", out weekdays));
        Assert.Equal(Schedule.Weekdays, weekdays);
        Assert.True(Schedule.TryParseDays("mon to fri", out weekdays));
        Assert.Equal(Schedule.Weekdays, weekdays);
        Assert.True(Schedule.TryParseDays("Sat Sun", out var weekend));
        Assert.Equal(Schedule.Weekend, weekend);
        Assert.True(Schedule.TryParseDays("weekends", out weekend));
        Assert.Equal(Schedule.Weekend, weekend);
        Assert.True(Schedule.TryParseDays("Mon, Wed, Fri", out var mwf));
        Assert.Equal(Schedule.Mon | Schedule.Wed | Schedule.Fri, mwf);
        Assert.True(Schedule.TryParseDays("Fri-Sun", out var fss));
        Assert.Equal(Schedule.Fri | Schedule.Sat | Schedule.Sun, fss);
        Assert.True(Schedule.TryParseDays("Thursday", out var thu));
        Assert.Equal(Schedule.Thu, thu);
        Assert.True(Schedule.TryParseDays("Sat-Mon", out var wrap));            // a range past the week's end
        Assert.Equal(Schedule.Sat | Schedule.Sun | Schedule.Mon, wrap);
        Assert.False(Schedule.TryParseDays("someday", out _));
        Assert.False(Schedule.TryParseDays("Mon-Someday", out _));

        Assert.Equal("every day", Schedule.DescribeDays(""));
        Assert.Equal("Mon–Fri", Schedule.DescribeDays("weekdays"));
        Assert.Equal("Sat, Sun", Schedule.DescribeDays("weekend"));
        Assert.Equal("Mon, Wed, Fri", Schedule.DescribeDays("mon wed fri"));
        Assert.Equal("Thu–Sun", Schedule.DescribeDays("thu-sun"));
        Assert.Equal("Mon, Tue", Schedule.DescribeDays("mon tue"));
        Assert.Equal("?", Schedule.DescribeDays("someday"));

        Assert.True(Schedule.TryParseDate("2026-12-24", out var xmas));
        Assert.Equal(new DateOnly(2026, 12, 24), xmas);
        Assert.True(Schedule.TryParseDate("24/12/2026", out xmas));
        Assert.Equal(new DateOnly(2026, 12, 24), xmas);
        Assert.True(Schedule.TryParseDate("24 Dec 2026", out xmas));
        Assert.Equal(new DateOnly(2026, 12, 24), xmas);
        Assert.False(Schedule.TryParseDate("Christmas", out _));
        Assert.False(Schedule.TryParseDate("", out _));
    }

    [Fact]
    public void WindowsFollowDaysDatesAndMidnight()
    {
        var shop = Programme("Daytime", "09:00", "17:00", days: "Mon–Fri");
        Assert.True(Schedule.OnDay(shop, DateOnly.FromDateTime(Monday)));
        Assert.False(Schedule.OnDay(shop, DateOnly.FromDateTime(Monday.AddDays(5))));    // Saturday
        Assert.Equal((At(1, 9, 0), At(1, 17, 0)), Schedule.WindowOn(shop, DateOnly.FromDateTime(Monday)));
        Assert.NotNull(Schedule.WindowAt(shop, At(1, 10, 0)));
        Assert.Null(Schedule.WindowAt(shop, At(1, 17, 0)));                              // the end is exclusive
        Assert.Null(Schedule.WindowAt(shop, At(6, 10, 0)));                              // Saturday

        var bar = Programme("Late bar", "22:00", "02:00");
        Assert.True(Schedule.CrossesMidnight(bar));
        var w = Schedule.WindowOn(bar, DateOnly.FromDateTime(Monday));
        Assert.Equal((At(1, 22, 0), At(2, 2, 0)), w);
        Assert.NotNull(Schedule.WindowAt(bar, At(2, 1, 30)));                            // Tuesday 01:30 belongs to Monday's window
        Assert.Equal(At(1, 22, 0), Schedule.WindowAt(bar, At(2, 1, 30))!.Value.Start);
        Assert.Null(Schedule.WindowAt(bar, At(2, 3, 0)));

        var season = Programme("Christmas", "09:00", "17:00", look: "Xmas");
        season.From = "2026-12-01";
        season.Until = "2026-12-31";
        Assert.True(Schedule.OnDay(season, new DateOnly(2026, 12, 10)));
        Assert.False(Schedule.OnDay(season, new DateOnly(2026, 11, 30)));
        Assert.False(Schedule.OnDay(season, new DateOnly(2027, 1, 1)));
        season.Until = "yesterday";
        Assert.False(Schedule.OnDay(season, new DateOnly(2026, 12, 10)));               // a date that does not read never matches
    }

    [Fact]
    public void TheProgrammeThatWinsIsDatedThenTheLaterStart()
    {
        var cfg = new InstallConfig();
        var daytime = Programme("Daytime", "09:00", "17:00");
        var lunch = Programme("Lunch", "12:00", "14:00", look: "Menu");
        var season = Programme("Christmas", "09:00", "17:00", look: "Xmas");
        season.From = "2026-12-01";
        season.Until = "2026-12-31";
        cfg.Slots.Add(daytime);
        cfg.Slots.Add(lunch);
        cfg.Slots.Add(season);

        Assert.Same(daytime, Schedule.ProgrammeAt(cfg, At(1, 10, 0)));
        Assert.Same(lunch, Schedule.ProgrammeAt(cfg, At(1, 12, 30)));                  // the later start wins inside the day
        Assert.Same(daytime, Schedule.ProgrammeAt(cfg, At(1, 14, 0)));
        Assert.Null(Schedule.ProgrammeAt(cfg, At(1, 18, 0)));
        Assert.Same(season, Schedule.ProgrammeAt(cfg, new DateTime(2026, 12, 10, 10, 0, 0)));   // dated beats undated
        Assert.Same(season, Schedule.ProgrammeAt(cfg, new DateTime(2026, 12, 10, 12, 30, 0)));  // ...whatever the undated row's start
        Assert.Same(daytime, Schedule.ProgrammeAt(cfg, new DateTime(2026, 11, 30, 10, 0, 0)));  // the day before the season
        lunch.Enabled = false;
        Assert.Same(daytime, Schedule.ProgrammeAt(cfg, At(1, 12, 30)));
    }

    [Fact]
    public void FiringsNextChangeAndTheTimeline()
    {
        var advert = new ScheduleSlotConfig { Name = "Offer", Kind = SlotKind.Advert, Start = "10:00", End = "12:00", EveryMinutes = 30, DurationSeconds = 20, Look = "Offer" };
        var firings = Schedule.FiringsOn(advert, DateOnly.FromDateTime(Monday));
        Assert.Equal(new[] { At(1, 10, 0), At(1, 10, 30), At(1, 11, 0), At(1, 11, 30) }, firings);   // the end itself is not a firing
        Assert.Equal(At(1, 10, 30), Schedule.NextFiring(advert, At(1, 10, 0)));
        Assert.Equal(At(2, 10, 0), Schedule.NextFiring(advert, At(1, 11, 30)));
        advert.EveryMinutes = 0;
        Assert.Equal(new[] { At(1, 10, 0) }, Schedule.FiringsOn(advert, DateOnly.FromDateTime(Monday)));
        Assert.Empty(Schedule.FiringsOn(Programme("Daytime", "09:00", "17:00"), DateOnly.FromDateTime(Monday)));

        var cfg = new InstallConfig();
        cfg.Slots.Add(Programme("Daytime", "09:00", "17:00"));
        cfg.Slots.Add(advert);
        cfg.Slots.Add(new ScheduleSlotConfig { Name = "Closing", Kind = SlotKind.Announcement, Start = "16:45", End = "16:46", Text = "Closing soon" });
        Assert.Equal(("Daytime starts", At(1, 9, 0)), Schedule.NextChange(cfg, At(1, 8, 0)));
        Assert.Equal(("advert Offer", At(1, 10, 0)), Schedule.NextChange(cfg, At(1, 9, 30)));
        Assert.Equal(("announcement Closing", At(1, 16, 45)), Schedule.NextChange(cfg, At(1, 10, 1)));
        Assert.Equal(("Daytime ends", At(1, 17, 0)), Schedule.NextChange(cfg, At(1, 16, 50)));
        Assert.Equal(("Daytime starts", At(2, 9, 0)), Schedule.NextChange(cfg, At(1, 17, 0)));

        var rows = Schedule.Timeline(cfg, DateOnly.FromDateTime(Monday));
        Assert.Equal(new[] { "09:00–17:00", "10:00", "16:45" }, rows.Select(r => r.TimeText));
        Assert.Equal("PROGRAMME", rows[0].KindText);
        Assert.Equal("NOW", rows[0].StateAt(At(1, 10, 0)));
        Assert.Equal("done", rows[0].StateAt(At(1, 17, 0)));
        Assert.Equal("", rows[2].StateAt(At(1, 10, 0)));
        Assert.Equal("done", rows[1].StateAt(At(1, 10, 0)));
        Assert.Contains("look Offer", rows[1].Detail);
        Assert.Contains("Closing soon", rows[2].Detail);

        Assert.Equal("Mon–Fri 09:00–17:00 · look Daytime", Schedule.Describe(Programme("Daytime", "09:00", "17:00", days: "weekdays")));
        Assert.Equal("every day at 10:00, 20 s · look Offer", Schedule.Describe(advert));
        advert.EveryMinutes = 30;
        advert.Screens = "1, 3";
        Assert.Equal("every day every 30 min from 10:00 to 12:00, 20 s · look Offer · on screens 1, 3", Schedule.Describe(advert));
        var (numbers, words) = Schedule.ParseScreens("1, 3");
        Assert.Equal(new[] { 1, 3 }, numbers);
        Assert.Empty(words);
        (numbers, words) = Schedule.ParseScreens("Window; 2");
        Assert.Equal(new[] { 2 }, numbers);
        Assert.Equal(new[] { "Window" }, words);

        // The finder: by name, by place among a kind, never a wrong kind when one is asked for.
        Assert.Same(advert, Schedule.Find(cfg, "offer"));
        Assert.Same(advert, Schedule.Find(cfg, "1", SlotKind.Advert));
        Assert.Null(Schedule.Find(cfg, "Offer", SlotKind.Announcement));
        Assert.Same(cfg.Slots[0], Schedule.Find(cfg, "1"));
        Assert.Null(Schedule.Find(cfg, ""));
    }

    [Fact]
    public void ProblemsAreSaidInWords()
    {
        var state = new ShowState();
        state.LooksAndCues.Looks.Add(new LookConfig { Name = "Daytime" });
        var cfg = state.Install;
        cfg.Slots.Add(Programme("Daytime", "09:00", "17:00"));
        Assert.Empty(Schedule.Problems(cfg, state));
        cfg.Slots.Add(Programme("Ghost", "09:00", "17:00", look: "Nowhere"));
        cfg.Slots.Add(new ScheduleSlotConfig { Name = "Bad", Kind = SlotKind.Advert, Days = "someday", Start = "9", End = "17:00", Look = "" });
        cfg.Slots.Add(new ScheduleSlotConfig { Name = "Empty", Kind = SlotKind.Announcement });
        cfg.IdleLook = "Nothing";
        var problems = Schedule.Problems(cfg, state);
        Assert.Contains(problems, p => p.StartsWith("Ghost: look 'Nowhere'"));
        Assert.Contains(problems, p => p.StartsWith("Bad: days 'someday'"));
        Assert.Contains(problems, p => p.StartsWith("Bad: start '9'"));
        Assert.Contains(problems, p => p == "Bad: an advert needs a look.");
        Assert.Contains(problems, p => p == "Empty: an announcement needs words, a VOG or a look.");
        Assert.Contains(problems, p => p.StartsWith("The idle look 'Nothing'"));
    }

    [Fact]
    public void TheRuntimeAppliesTheProgrammeOnceAndPutsItBackAfterAnAdvert()
    {
        var cfg = new InstallConfig { Enabled = true };
        var daytime = Programme("Daytime", "09:00", "17:00");
        var lunch = new ScheduleSlotConfig { Name = "Lunch offer", Kind = SlotKind.Advert, Start = "12:30", End = "12:31", DurationSeconds = 30, Look = "Offer" };
        var closing = new ScheduleSlotConfig { Name = "Closing", Kind = SlotKind.Announcement, Start = "16:45", End = "16:46", DurationSeconds = 20, Text = "Closing soon" };
        cfg.Slots.Add(daytime);
        cfg.Slots.Add(lunch);
        cfg.Slots.Add(closing);
        var rt = new InstallRuntime();

        var steps = rt.Tick(cfg, At(1, 10, 0));
        Assert.Equal(new[] { InstallStepKind.Programme }, steps.Select(s => s.Kind));
        Assert.Same(daytime, steps[0].Slot);
        Assert.Empty(rt.Tick(cfg, At(1, 10, 1)));                                        // applied once, not every second
        Assert.Equal(daytime.Id, rt.ProgrammeId);

        steps = rt.Tick(cfg, At(1, 12, 30));
        Assert.Equal(new[] { InstallStepKind.OverrideStart }, steps.Select(s => s.Kind));
        Assert.Same(lunch, rt.Override);
        Assert.Equal(At(1, 12, 30, 30), rt.OverrideEndsAt);
        Assert.Empty(rt.Tick(cfg, At(1, 12, 30, 10)));
        steps = rt.Tick(cfg, At(1, 12, 30, 31));
        Assert.Equal(new[] { InstallStepKind.OverrideEnd, InstallStepKind.Programme }, steps.Select(s => s.Kind));   // the picture moved: the programme comes back
        Assert.Null(rt.Override);
        Assert.Empty(rt.Tick(cfg, At(1, 12, 31, 0)));                                     // fired once, not again at 12:30 next tick

        // Words only: the announcement ends without touching the programme.
        steps = rt.Tick(cfg, At(1, 16, 45));
        Assert.Equal(new[] { InstallStepKind.OverrideStart }, steps.Select(s => s.Kind));
        steps = rt.Tick(cfg, At(1, 16, 45, 21));
        Assert.Equal(new[] { InstallStepKind.OverrideEnd }, steps.Select(s => s.Kind));

        // Outside every programme: idle once; the next morning the programme again.
        steps = rt.Tick(cfg, At(1, 17, 0));
        Assert.Equal(new[] { InstallStepKind.Idle }, steps.Select(s => s.Kind));
        Assert.True(rt.Idle);
        Assert.Empty(rt.Tick(cfg, At(1, 17, 1)));
        steps = rt.Tick(cfg, At(2, 9, 0));
        Assert.Equal(new[] { InstallStepKind.Programme }, steps.Select(s => s.Kind));
        Assert.False(rt.Idle);
    }

    [Fact]
    public void AnnouncementsBeatAdvertsAndADeferredAdvertFiresWhenTheWayClears()
    {
        var cfg = new InstallConfig { Enabled = true };
        var daytime = Programme("Daytime", "09:00", "17:00");
        var advert = new ScheduleSlotConfig { Name = "Offer", Kind = SlotKind.Advert, Start = "13:00", End = "14:01", EveryMinutes = 30, DurationSeconds = 90, Look = "Offer" };   // fires 13:00, 13:30, 14:00
        var hourly = new ScheduleSlotConfig { Name = "Hourly", Kind = SlotKind.Announcement, Start = "13:00", End = "14:00", EveryMinutes = 31, DurationSeconds = 20, Text = "Hello" };
        cfg.Slots.Add(daytime);
        cfg.Slots.Add(advert);
        cfg.Slots.Add(hourly);
        var rt = new InstallRuntime();
        rt.Tick(cfg, At(1, 12, 59));

        // Both due at 13:00: the announcement first, the advert waits behind it.
        var steps = rt.Tick(cfg, At(1, 13, 0));
        Assert.Equal(new[] { InstallStepKind.OverrideStart }, steps.Select(s => s.Kind));
        Assert.Same(hourly, rt.Override);
        Assert.Equal(1, rt.Waiting);
        steps = rt.Tick(cfg, At(1, 13, 0, 21));
        Assert.Equal(new[] { InstallStepKind.OverrideEnd, InstallStepKind.OverrideStart }, steps.Select(s => s.Kind));
        Assert.Same(advert, rt.Override);
        Assert.Equal(0, rt.Waiting);
        steps = rt.Tick(cfg, At(1, 13, 1, 52));
        Assert.Equal(new[] { InstallStepKind.OverrideEnd, InstallStepKind.Programme }, steps.Select(s => s.Kind));

        // An announcement due while an advert runs cuts it short; the programme is back underneath when the words end.
        steps = rt.Tick(cfg, At(1, 13, 30));
        Assert.Same(advert, rt.Override);
        steps = rt.Tick(cfg, At(1, 13, 31));
        Assert.Equal(new[] { InstallStepKind.OverrideEnd, InstallStepKind.OverrideStart }, steps.Select(s => s.Kind));
        Assert.Contains("cut short", steps[0].Note);
        Assert.Same(hourly, rt.Override);
        steps = rt.Tick(cfg, At(1, 13, 31, 21));
        Assert.Equal(new[] { InstallStepKind.OverrideEnd, InstallStepKind.Programme }, steps.Select(s => s.Kind));   // the advert had moved the picture

        // The desk owns the screens at a firing: skipped and said, never fired late into the wrong moment.
        steps = rt.Tick(cfg, At(1, 14, 0), busy: true);
        Assert.Equal(new[] { InstallStepKind.Note }, steps.Select(s => s.Kind));
        Assert.Contains("skipped", steps[0].Note);
        Assert.Empty(rt.Tick(cfg, At(1, 14, 0, 30)));
    }

    [Fact]
    public void AMissedFiringIsCaughtUpWithinFiveMinutesAndNoLonger()
    {
        var cfg = new InstallConfig { Enabled = true };
        cfg.Slots.Add(Programme("Daytime", "09:00", "17:00"));
        var advert = new ScheduleSlotConfig { Name = "Offer", Kind = SlotKind.Advert, Start = "15:00", End = "15:01", DurationSeconds = 10, Look = "Offer" };
        cfg.Slots.Add(advert);
        var rt = new InstallRuntime();
        rt.Tick(cfg, At(1, 14, 59));
        Assert.Contains(rt.Tick(cfg, At(1, 15, 4)), s => s.Kind == InstallStepKind.OverrideStart);   // the app was busy for four minutes: still fires

        var late = new InstallRuntime();
        late.Tick(cfg, At(1, 14, 59));
        Assert.DoesNotContain(late.Tick(cfg, At(1, 15, 6)), s => s.Kind == InstallStepKind.OverrideStart);   // six minutes: missed

        // A firing before the clock started is not owed: switching the schedule on at 15:03 does not fire 15:00.
        var fresh = new InstallRuntime();
        Assert.DoesNotContain(fresh.Tick(cfg, At(1, 15, 3)), s => s.Kind == InstallStepKind.OverrideStart);
    }

    [Fact]
    public void ByHandOverridesRunWithTheClockOffAndTheClockForgetsWhenSwitchedOff()
    {
        var cfg = new InstallConfig { Enabled = false };
        var daytime = Programme("Daytime", "09:00", "17:00");
        cfg.Slots.Add(daytime);
        var rt = new InstallRuntime();
        Assert.Empty(rt.Tick(cfg, At(1, 10, 0)));                                        // off: the programme is not applied
        Assert.Equal("", rt.ProgrammeId);

        var words = InstallRuntime.AdHoc("Doors close in five minutes", 15);
        Assert.True(words.IsAdHoc);
        var steps = rt.Fire(words, At(1, 10, 0));
        Assert.Equal(new[] { InstallStepKind.OverrideStart }, steps.Select(s => s.Kind));
        Assert.Same(words, rt.Override);
        steps = rt.Tick(cfg, At(1, 10, 0, 16));
        Assert.Equal(new[] { InstallStepKind.OverrideEnd }, steps.Select(s => s.Kind));    // no programme step: the clock is off
        Assert.Null(rt.Override);

        var advert = new ScheduleSlotConfig { Name = "Offer", Kind = SlotKind.Advert, DurationSeconds = 30, Look = "Offer" };
        cfg.Slots.Add(advert);
        rt.Fire(advert, At(1, 10, 1), seconds: 5);
        Assert.Equal(At(1, 10, 1, 5), rt.OverrideEndsAt);
        steps = rt.Fire(words, At(1, 10, 1, 2));                                          // a second fire replaces the first
        Assert.Equal(new[] { InstallStepKind.OverrideEnd, InstallStepKind.OverrideStart }, steps.Select(s => s.Kind));
        steps = rt.EndOverride();
        Assert.Equal(new[] { InstallStepKind.OverrideEnd }, steps.Select(s => s.Kind));
        Assert.Empty(rt.EndOverride());

        // On, applied, then off: the runtime forgets, so switching on again applies the programme afresh.
        cfg.Enabled = true;
        Assert.Contains(rt.Tick(cfg, At(1, 10, 2)), s => s.Kind == InstallStepKind.Programme);
        cfg.Enabled = false;
        Assert.Empty(rt.Reset());
        Assert.Empty(rt.Tick(cfg, At(1, 10, 3)));
        cfg.Enabled = true;
        Assert.Contains(rt.Tick(cfg, At(1, 10, 4)), s => s.Kind == InstallStepKind.Programme);

        // A slot removed while it runs ends on the next tick.
        rt.Fire(advert, At(1, 10, 5));
        cfg.Slots.Remove(advert);
        Assert.Contains(rt.Tick(cfg, At(1, 10, 5, 1)), s => s.Kind == InstallStepKind.OverrideEnd);
    }

    [Fact]
    public void TheVerbsTheAddressesAndTheCueActions()
    {
        Assert.Equal(RemoteCommandKind.Announce, ControlProtocol.Parse("ANNOUNCE The store closes in 15 minutes").Kind);
        Assert.Equal("The store closes in 15 minutes", ControlProtocol.Parse("ANNOUNCE The store closes in 15 minutes").TextArg);
        Assert.Equal(RemoteCommandKind.AnnounceOff, ControlProtocol.Parse("announce off").Kind);
        Assert.Equal(RemoteCommandKind.AnnounceOff, ControlProtocol.Parse("ANNOUNCEMENT STOP").Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("ANNOUNCE").Kind);
        Assert.Equal(RemoteCommandKind.AdvertPlay, ControlProtocol.Parse("ADVERT Lunch offer").Kind);
        Assert.Equal(2, ControlProtocol.Parse("AD 2").IntArg);
        Assert.Equal(RemoteCommandKind.AdvertOff, ControlProtocol.Parse("ADVERT SKIP").Kind);
        Assert.Equal(RemoteCommandKind.ScheduleOn, ControlProtocol.Parse("SCHEDULE ON").Kind);
        Assert.Equal(RemoteCommandKind.ScheduleOff, ControlProtocol.Parse("SCHEDULE OFF").Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("SCHEDULE MAYBE").Kind);
        Assert.Equal(RemoteCommandKind.UpdateApply, ControlProtocol.Parse("UPDATE APPLY secret").Kind);
        Assert.Equal("secret", ControlProtocol.Parse("UPDATE APPLY secret").TextArg);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("UPDATE NOW").Kind);
        Assert.Equal(RemoteCommandKind.Restart, ControlProtocol.Parse("RESTART secret").Kind);
        Assert.Equal("", ControlProtocol.Parse("RESTART").TextArg);

        Assert.Equal("ANNOUNCE Closing time", OscMap.ToLine(OscMessage.Of("/patterns/announce", "Closing time")));
        Assert.Equal("ANNOUNCE Closing time", OscMap.ToLine(OscMessage.Of("/patterns/announce/Closing time")));
        Assert.Equal("ANNOUNCE OFF", OscMap.ToLine(OscMessage.Of("/patterns/announce/off")));
        Assert.Null(OscMap.ToLine(OscMessage.Of("/patterns/announce")));
        Assert.Equal("ADVERT Lunch offer", OscMap.ToLine(OscMessage.Of("/patterns/advert", "Lunch offer")));
        Assert.Equal("ADVERT 2", OscMap.ToLine(OscMessage.Of("/patterns/advert/2")));
        Assert.Equal("ADVERT OFF", OscMap.ToLine(OscMessage.Of("/patterns/ad/off")));
        Assert.Equal("SCHEDULE ON", OscMap.ToLine(OscMessage.Of("/patterns/schedule", 1)));
        Assert.Equal("SCHEDULE OFF", OscMap.ToLine(OscMessage.Of("/patterns/schedule/off")));
        Assert.Contains(OscMap.Reference, r => r.Address.StartsWith("/patterns/announce"));

        Assert.Equal((TargetKind.Slot, ValueKind.Text), CueActionSpec.For(CueActionKind.Announce));
        Assert.Equal((TargetKind.Slot, ValueKind.None), CueActionSpec.For(CueActionKind.AdvertPlay));
        Assert.Equal("Advert — play now", CueActionSpec.Label(CueActionKind.AdvertPlay));
        Assert.Contains(CueActionKind.ScheduleOff, CueActionSpec.Editable);
        Assert.Equal(CueActionKind.Announce, CueSheet.ParseKind("announcement"));
        Assert.Equal(CueActionKind.AdvertPlay, CueSheet.ParseKind("advert"));
        Assert.Equal(CueActionKind.AdvertOff, CueSheet.ParseKind("skip advert"));
        Assert.Equal(CueActionKind.ScheduleOn, CueSheet.ParseKind("schedule on"));

        var state = new ShowState();
        state.LooksAndCues.Looks.Add(new LookConfig { Name = "Offer" });
        state.Install.Slots.Add(new ScheduleSlotConfig { Name = "Closing", Kind = SlotKind.Announcement, Text = "Closing soon" });
        state.Install.Slots.Add(new ScheduleSlotConfig { Name = "Lunch offer", Kind = SlotKind.Advert, Look = "Offer" });
        var cue = new RunCueConfig { Number = "01.010", Name = "Words" };
        cue.Actions.Add(new CueActionConfig { Kind = CueActionKind.Announce, Target = "Closing" });
        cue.Actions.Add(new CueActionConfig { Kind = CueActionKind.AdvertPlay, Target = "Lunch offer" });
        Assert.Equal("Announcement 'Closing'", CueSummary.DescribeAction(state, cue.Actions[0]));
        Assert.Equal("Advert 'Lunch offer' now", CueSummary.DescribeAction(state, cue.Actions[1]));
        var stack = new CueStackConfig();
        stack.Cues.Add(cue);
        Assert.False(CueValidator.Validate(state, stack).IsBroken(cue.Id));

        cue.Actions[0].Target = "";
        cue.Actions[0].Value = "Please take your seats";
        Assert.Equal("Announce: 'Please take your seats'", CueSummary.DescribeAction(state, cue.Actions[0]));
        Assert.False(CueValidator.Validate(state, stack).IsBroken(cue.Id));
        cue.Actions[0].Value = "";
        Assert.True(CueValidator.Validate(state, stack).IsBroken(cue.Id));                 // nothing to announce
        cue.Actions[0].Target = "Lunch offer";                                             // an advert named as an announcement still fires (it is a slot) — a programme would not
        Assert.False(CueValidator.Validate(state, stack).IsBroken(cue.Id));
        cue.Actions[0].Target = "Nobody";
        Assert.True(CueValidator.Validate(state, stack).IsBroken(cue.Id));
        cue.Actions[0].Target = "Closing";
        cue.Actions[1].Target = "Closing";                                                 // an announcement is not an advert
        Assert.True(CueValidator.Validate(state, stack).IsBroken(cue.Id));
        cue.Actions[1].Target = "";
        Assert.True(CueValidator.Validate(state, stack).IsBroken(cue.Id));

        // The OSC feedback carries the install block.
        var messages = OscFeedback.FromState("{\"install\":{\"on\":true,\"programme\":\"Daytime\",\"over\":\"Lunch offer\",\"overUntil\":\"12:30:30\",\"next\":\"16:45 announcement Closing\"}}");
        Assert.Contains(messages, m => m.Address == "/patterns/state/install/on");
        Assert.Contains(messages, m => m.Address == "/patterns/state/install/programme" && Equals(m.Args[0], "Daytime"));
        Assert.Contains(messages, m => m.Address == "/patterns/state/install/over" && Equals(m.Args[0], "Lunch offer"));
        Assert.Contains(messages, m => m.Address == "/patterns/state/install/next");
    }

    [Fact]
    public void ThePasscodeGateIsAFenceWithALock()
    {
        var gate = new AdminGate();
        var now = new DateTime(2026, 9, 7, 12, 0, 0, DateTimeKind.Utc);
        Assert.False(gate.Check("", "anything", now));
        Assert.Contains("no admin passcode", gate.Reason);
        Assert.True(gate.Check("open-sesame", " open-sesame ", now));
        Assert.False(gate.Check("open-sesame", "open-sesam", now));
        Assert.Equal("wrong passcode", gate.Reason);
        for (var i = 0; i < AdminGate.MaxFailures - 2; i++) Assert.False(gate.Check("open-sesame", "nope", now));
        Assert.False(gate.Check("open-sesame", "nope", now));                             // the fifth wrong try locks the gate
        Assert.Contains("locked", gate.Reason);
        Assert.True(gate.IsLocked(now));
        Assert.False(gate.Check("open-sesame", "open-sesame", now.AddSeconds(10)));        // even the right one waits
        Assert.Contains("try again", gate.Reason);
        Assert.True(gate.Check("open-sesame", "open-sesame", now.AddSeconds(61)));
    }

    [Fact]
    public void TheSupportBundleCarriesTheLogsAndBlanksEverySecret()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-bundle-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            File.WriteAllText(Path.Combine(dir, "patterns.log"), "2026-09-07 10:00:00.000 [INFO] hello\n");
            File.WriteAllText(Path.Combine(dir, "patterns.settings.json"), "{ \"Install\": { \"AdminPasscode\": \"open-sesame\", \"ManagementToken\": \"\", \"SiteName\": \"Lobby\" } }");
            Directory.CreateDirectory(Path.Combine(dir, "updates"));
            File.WriteAllText(Path.Combine(dir, "updates", UpdateApply.NoteName), "Updated to 1.2.0");
            var zipPath = Path.Combine(dir, SupportBundle.FileNameFor(new DateTime(2026, 9, 7, 11, 30, 0)));
            Assert.Equal("patterns-support-20260907-1130.zip", Path.GetFileName(zipPath));
            var entries = SupportBundle.Build(dir, zipPath, "Site: Lobby");
            Assert.Equal(new[] { "patterns.log", "patterns.settings.json", "updates/" + UpdateApply.NoteName, "bundle-info.txt" }, entries);
            using var zip = ZipFile.OpenRead(zipPath);
            using var reader = new StreamReader(zip.GetEntry("patterns.settings.json")!.Open());
            var settings = reader.ReadToEnd();
            Assert.Contains("\"AdminPasscode\": \"•••\"", settings);
            Assert.Contains("\"ManagementToken\": \"\"", settings);
            Assert.Contains("\"SiteName\": \"Lobby\"", settings);
            Assert.DoesNotContain("open-sesame", settings);
            using var info = new StreamReader(zip.GetEntry("bundle-info.txt")!.Open());
            Assert.StartsWith("Site: Lobby", info.ReadToEnd());
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    private static string MakePackage(string dir, string name, string version, bool withExe = true, string? extra = null, bool manifest = true)
    {
        var path = Path.Combine(dir, name);
        using var zip = ZipFile.Open(path, ZipArchiveMode.Create);
        if (withExe)
        {
            using var w = new StreamWriter(zip.CreateEntry("Patterns.exe").Open());
            w.Write("new exe " + version);
        }
        if (manifest)
        {
            using var m = new StreamWriter(zip.CreateEntry(UpdatePackage.ManifestName).Open());
            m.Write("{ \"version\": \"" + version + "\", \"notes\": \"the notes\" }");
        }
        if (extra is not null)
        {
            using var e = new StreamWriter(zip.CreateEntry(extra).Open());
            e.Write("extra " + version);
        }
        return path;
    }

    [Fact]
    public void AnUpdatePackageIsReadRefusedOrAppliedAndRolledBack()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-update-" + Guid.NewGuid().ToString("N"));
        var app = Path.Combine(dir, "app");
        var updates = UpdatePackage.Folder(dir);
        Directory.CreateDirectory(app);
        Directory.CreateDirectory(updates);
        try
        {
            File.WriteAllText(Path.Combine(app, "Patterns.exe"), "old exe");
            File.WriteAllText(Path.Combine(app, "patterns.settings.json"), "{}");
            Directory.CreateDirectory(Path.Combine(app, "libvlc"));
            File.WriteAllText(Path.Combine(app, "libvlc", "old.dll"), "old dll");

            var good = MakePackage(updates, "patterns-update-1.2.0.zip", "1.2.0", extra: "libvlc/new.dll");
            var info = UpdatePackage.Inspect(good);
            Assert.True(info.Ok, string.Join("; ", info.Problems));
            Assert.Equal("1.2.0", info.Version);
            Assert.Equal("the notes", info.Notes);
            Assert.Equal(new[] { "Patterns.exe", "libvlc/new.dll" }, info.Files);
            Assert.Contains("1.2.0 (patterns-update-1.2.0.zip, 2 files)", info.Summary);
            Assert.Equal(good, UpdatePackage.Staged(dir));

            Assert.Contains("no Patterns.exe", UpdatePackage.Inspect(MakePackage(updates, "noexe.zip", "1.3.0", withExe: false)).Problems[0]);
            Assert.Contains("no version", UpdatePackage.Inspect(MakePackage(updates, "nomanifest.zip", "1.3.0", manifest: false)).Problems[0]);
            Assert.Contains("unsafe path", UpdatePackage.Inspect(MakePackage(updates, "escape.zip", "1.3.0", extra: "../outside.txt")).Problems[0]);
            File.WriteAllText(Path.Combine(updates, "junk.zip"), "not a zip");
            Assert.Contains("not a package that opens", UpdatePackage.Inspect(Path.Combine(updates, "junk.zip")).Problems[0]);
            Assert.False(UpdatePackage.IsSafePath("C:/x"));
            Assert.False(UpdatePackage.IsSafePath("/etc/passwd"));
            Assert.True(UpdatePackage.IsSafePath("libvlc/win-x64/libvlc.dll"));

            // The request the app leaves, and the swap: the old files into the backup, the new ones in place, the settings untouched.
            UpdateApply.WriteRequest(updates, new UpdateRequest(good, "1.2.0", DateTime.UtcNow));
            var request = UpdateApply.ReadRequest(updates);
            Assert.Equal(good, request!.Package);
            UpdateApply.ClearRequest(updates);
            Assert.Null(UpdateApply.ReadRequest(updates));

            var backup = UpdateApply.BackupFolderFor(updates, new DateTime(2026, 9, 7, 3, 0, 0));
            Assert.EndsWith("backup-20260907-0300", backup);
            var report = UpdateApply.Run(good, app, backup);
            Assert.True(report.Ok, report.Message);
            Assert.Equal(new[] { "Patterns.exe" }, report.Replaced);
            Assert.Equal(new[] { "libvlc/new.dll" }, report.Added);
            Assert.Equal("new exe 1.2.0", File.ReadAllText(Path.Combine(app, "Patterns.exe")));
            Assert.Equal("extra 1.2.0", File.ReadAllText(Path.Combine(app, "libvlc", "new.dll")));
            Assert.Equal("old exe", File.ReadAllText(Path.Combine(backup, "Patterns.exe")));
            Assert.Equal("{}", File.ReadAllText(Path.Combine(app, "patterns.settings.json")));
            Assert.Equal("old dll", File.ReadAllText(Path.Combine(app, "libvlc", "old.dll")));

            // The new build did not stay up: everything back as it was.
            Assert.Equal("rollback", UpdateApply.Verdict(exitCode: 1, killedForHang: false, ranFor: TimeSpan.FromSeconds(20)));
            Assert.Equal("rollback", UpdateApply.Verdict(exitCode: 0, killedForHang: true, ranFor: TimeSpan.FromSeconds(20)));
            Assert.Equal("commit", UpdateApply.Verdict(exitCode: 0, killedForHang: false, ranFor: TimeSpan.FromSeconds(20)));   // closed cleanly: fine
            Assert.Equal("commit", UpdateApply.Verdict(exitCode: 1, killedForHang: false, ranFor: TimeSpan.FromMinutes(3)));    // a crash after the proving period is the watchdog's usual business
            var back = UpdateApply.RollBack(backup, app, report.Added);
            Assert.True(back.Ok, back.Message);
            Assert.Equal("old exe", File.ReadAllText(Path.Combine(app, "Patterns.exe")));
            Assert.False(File.Exists(Path.Combine(app, "libvlc", "new.dll")));

            // A package that refuses to apply changes nothing.
            var bad = UpdatePackage.Inspect(Path.Combine(updates, "noexe.zip"));
            Assert.False(UpdateApply.Run(bad.Path, app, Path.Combine(updates, "backup-x")).Ok);
            Assert.Equal("old exe", File.ReadAllText(Path.Combine(app, "Patterns.exe")));

            UpdateApply.WriteNote(updates, "Updated to 1.2.0");
            Assert.Equal("Updated to 1.2.0", UpdateApply.ReadNote(updates));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TheCheckInContract()
    {
        Assert.Null(CheckIn.ProblemWithUrl("https://signage.example.com/patterns/checkin"));
        Assert.Null(CheckIn.ProblemWithUrl("http://127.0.0.1:5000/checkin"));
        Assert.Null(CheckIn.ProblemWithUrl("http://192.168.1.20/checkin"));
        Assert.Null(CheckIn.ProblemWithUrl("http://10.0.0.5/checkin"));
        Assert.Null(CheckIn.ProblemWithUrl("http://172.20.1.1/checkin"));
        Assert.NotNull(CheckIn.ProblemWithUrl("http://signage.example.com/checkin"));
        Assert.NotNull(CheckIn.ProblemWithUrl("ftp://x"));
        Assert.NotNull(CheckIn.ProblemWithUrl("not a url"));

        var payload = CheckIn.Payload("Lobby", "1.0.0", "SIGN-PC", "Up 1h", "{\"blackout\":false,\"live\":true}", new DateTime(2026, 9, 7, 10, 0, 0, DateTimeKind.Utc));
        Assert.Contains("\"site\":\"Lobby\"", payload);
        Assert.Contains("\"state\":{\"blackout\":false,\"live\":true}", payload);
        Assert.Contains("\"utc\":\"2026-09-07T10:00:00Z\"", payload);
        Assert.Contains("\"state\":null", CheckIn.Payload("Lobby", "1.0.0", "SIGN-PC", "", "nonsense", DateTime.UtcNow));

        var reply = CheckIn.Parse("{\"token\":\"s3cret\",\"commands\":[\"ANNOUNCE Sale on\",\"SCHEDULE ON\",\"\"],\"update\":{\"url\":\"https://x/p.zip\",\"version\":\"1.2.0\",\"sha256\":\"" + new string('a', 64) + "\"},\"applyUpdate\":true,\"note\":\"hi\"}", "s3cret");
        Assert.Equal("", reply.Problem);
        Assert.Equal(new[] { "ANNOUNCE Sale on", "SCHEDULE ON" }, reply.Commands);
        Assert.Equal("1.2.0", reply.Update!.Version);
        Assert.True(reply.ApplyUpdate);
        Assert.False(reply.Restart);
        Assert.Equal("hi", reply.Note);

        var wrong = CheckIn.Parse("{\"token\":\"other\",\"commands\":[\"BLACKOUT ON\"]}", "s3cret");
        Assert.Empty(wrong.Commands);
        Assert.Contains("token", wrong.Problem);
        Assert.Single(CheckIn.Parse("{\"commands\":[\"BLACKOUT ON\"]}", "").Commands);   // no token configured: the reply is taken as it is
        Assert.Null(CheckIn.Parse("{\"update\":{\"url\":\"https://x\",\"version\":\"1\",\"sha256\":\"short\"}}", "").Update);
        Assert.Contains("not JSON", CheckIn.Parse("{oops", "").Problem);
        Assert.Equal(CheckIn.MaxCommands, CheckIn.Parse("{\"commands\":[" + string.Join(",", Enumerable.Repeat("\"PING\"", 30)) + "]}", "").Commands.Count);

        var file = Path.GetTempFileName();
        File.WriteAllText(file, "abc");
        Assert.Equal("ba7816bf8f01cfea414140de5dae2223b00361a396177a9cb410ff61f20015ad", CheckIn.Sha256Of(file));
        File.Delete(file);
    }
}
