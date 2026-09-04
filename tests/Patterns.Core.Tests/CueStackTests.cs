using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>Schema 5: the cue stack model, numbering, the spec table, the simulating validator, migration.</summary>
public class CueStackTests
{
    private static ShowState ShowWithLook(string lookName, PatternKind kind)
    {
        var state = SettingsStore.Fresh();
        state.Pattern.Kind = kind;
        state.LooksAndCues.Looks.Add(new LookConfig { Name = lookName, Json = LookService.Capture(state) });
        return state;
    }

    private static RunCueConfig Cue(string number, string name, params CueActionConfig[] actions)
    {
        var cue = new RunCueConfig { Number = number, Name = name };
        foreach (var a in actions) cue.Actions.Add(a);
        return cue;
    }

    private static CueActionConfig Act(CueActionKind kind, string target = "", string value = "")
        => new() { Kind = kind, Target = target, Value = value };

    [Fact]
    public void NumbersStepByTenFitBetweenAndNeverResort()
    {
        Assert.Equal("01.010", CueNumber.Next(null));
        Assert.Equal("03.030", CueNumber.Next("03.020"));
        Assert.Equal("03.025", CueNumber.Between("03.020", "03.030"));
        Assert.Equal("03.030", CueNumber.Between("03.020", "03.021")); // no room: step on
        Assert.Equal("01.010", CueNumber.Between(null, null));
        Assert.True(CueNumber.Compare("02.010", "10.005") < 0);
        Assert.True(CueNumber.Compare("banana", "01.010") > 0); // unparseable sorts last
        Assert.Equal((3, 20), CueNumber.Parse("3.20"));
        Assert.Null(CueNumber.Parse("three"));

        var cues = new List<RunCueConfig>
        {
            new() { Number = "01.010" }, new() { Number = "01.010" }, new() { Number = "01.005" }, new() { Number = "02.900" },
        };
        var notes = CueNumber.Warnings(cues);
        Assert.Contains(notes, n => n.Contains("used twice"));
        Assert.Contains(notes, n => n.Contains("out of order"));

        CueNumber.Renumber(cues);
        Assert.Equal(new[] { "01.010", "01.020", "01.030", "02.010" }, cues.Select(c => c.Number)); // a new section keeps its number
        Assert.Empty(CueNumber.Warnings(cues));
    }

    [Fact]
    public void TheSpecTableCoversEveryKindAnOperatorCanPick()
    {
        foreach (var kind in Enum.GetValues<CueActionKind>())
        {
            if (kind == CueActionKind.Unknown)
            {
                Assert.DoesNotContain(kind, CueActionSpec.Editable);
                continue;
            }
            Assert.Contains(kind, CueActionSpec.Editable);
            Assert.False(string.IsNullOrWhiteSpace(CueActionSpec.Label(kind)));
            _ = CueActionSpec.For(kind); // never throws
        }
        Assert.Equal((TargetKind.Look, ValueKind.Transition), CueActionSpec.For(CueActionKind.ApplyLook));
        Assert.Equal((TargetKind.Stack, ValueKind.None), CueActionSpec.For(CueActionKind.ListGo));
        Assert.Equal((TargetKind.None, ValueKind.Minutes), CueActionSpec.For(CueActionKind.CountdownStart));
        Assert.Equal((TargetKind.None, ValueKind.Percent), CueActionSpec.For(CueActionKind.AudioVolume));
        Assert.True(CueActionSpec.TryParsePercent(" 40 ", out var pct) && pct == 40);
        Assert.False(CueActionSpec.TryParsePercent("126", out _));
        Assert.False(CueActionSpec.TryParsePercent("-1", out _));
        Assert.False(CueActionSpec.TryParsePercent("loud", out _));
        Assert.Equal("Audio volume 40%", CueSummary.DescribeAction(new ShowState(), new CueActionConfig { Kind = CueActionKind.AudioVolume, Value = "40" }));
        Assert.True(CueActionSpec.TryParseTransition("", out var cut, out var ms) && !cut && ms < 0);
        Assert.True(CueActionSpec.TryParseTransition("CUT", out cut, out _) && cut);
        Assert.True(CueActionSpec.TryParseTransition("800", out _, out ms) && ms == 800);
        Assert.False(CueActionSpec.TryParseTransition("fast", out _, out _));
    }

    [Fact]
    public void TheValidatorMarksBrokenCuesOneByOneAndSimulatesLooksInOrder()
    {
        var state = ShowWithLook("Walk-in", PatternKind.ColorBars);
        // A second look puts a playlist with parts on air — a later cue may name those parts.
        state.Pattern.Kind = PatternKind.Media;
        state.Pattern.Media.Source = MediaSource.Playlist;
        state.Pattern.Media.Playlist.Sections.Add(new PlaylistSectionConfig { Name = "Main" });
        state.LooksAndCues.Looks.Add(new LookConfig { Name = "Show", Json = LookService.Capture(state) });
        state.Pattern.Kind = PatternKind.Grid;
        state.Pattern.Media.Playlist.Sections.Clear();
        state.Stingers.Items.Add(new StingerItemConfig { Name = "Sting", Path = "/show/sting.wav" });
        state.Stingers.Items.Add(new StingerItemConfig { Name = "Clip", Path = "/show/clip.mp4" });
        var walkIn = LookService.Find(state, "Walk-in")!;
        var show = LookService.Find(state, "Show")!;

        var stack = CueStacks.Caller(state);
        stack.Cues.Add(Cue("01.010", "Walk-in", Act(CueActionKind.ApplyLook, walkIn.Id)));
        stack.Cues.Add(Cue("01.020", "Gone", Act(CueActionKind.ApplyLook, "Deleted look")));
        stack.Cues.Add(Cue("01.030", "Part too early", Act(CueActionKind.PlaylistPart, "Main")));
        stack.Cues.Add(Cue("01.040", "Show", Act(CueActionKind.ApplyLook, show.Id, "800")));
        stack.Cues.Add(Cue("01.050", "Part in time", Act(CueActionKind.PlaylistPart, "Main")));
        stack.Cues.Add(Cue("01.060", "Sting", Act(CueActionKind.StingerFire, "Sting")));
        stack.Cues.Add(Cue("01.070", "Clip with a look", Act(CueActionKind.StingerFire, "Clip"), Act(CueActionKind.ApplyLook, walkIn.Id)));
        stack.Cues.Add(Cue("01.080", "Newer build", Act(CueActionKind.Unknown)));
        stack.Cues.Add(Cue("01.090", "Bad fade", Act(CueActionKind.ApplyLook, walkIn.Id, "fast")));
        stack.Cues.Add(Cue("01.100", "No stream target", Act(CueActionKind.StreamStart)));
        stack.Cues.Add(Cue("01.110", "Countdown", Act(CueActionKind.CountdownStart, "", "5")));
        stack.Cues.Add(Cue("01.120", "Empty message", Act(CueActionKind.MessageOn, "", "")));
        stack.Cues.Add(Cue("01.130", "Volume", Act(CueActionKind.AudioVolume, "", "40")));
        stack.Cues.Add(Cue("01.140", "Bad volume", Act(CueActionKind.AudioVolume, "", "loud")));

        var ctx = new CueValidationContext { FileExists = p => p.EndsWith(".wav") || p.EndsWith(".mp4"), VideoDecoderAvailable = true };
        var report = CueValidator.Validate(state, stack, ctx);

        string Reason(string number) => report.ReasonFor(stack.Cues.First(c => c.Number == number).Id) ?? "";
        Assert.False(report.IsBroken(stack.Cues[0].Id));
        Assert.Contains("not found", Reason("01.020"));
        Assert.Contains("not in the playlist", Reason("01.030"));   // before 'Show' put the parts on air
        Assert.False(report.IsBroken(stack.Cues[3].Id));
        Assert.False(report.IsBroken(stack.Cues[4].Id));            // after it, the part resolves
        Assert.False(report.IsBroken(stack.Cues[5].Id));
        Assert.Contains("cannot share a cue", Reason("01.070"));
        Assert.Contains("newer build", Reason("01.080"));
        Assert.Contains("not 'cut' or a fade", Reason("01.090"));
        Assert.Contains("no enabled stream destination", Reason("01.100"));
        Assert.False(report.IsBroken(stack.Cues[10].Id));
        Assert.False(report.IsBroken(stack.Cues[11].Id));           // soft: the cue still runs
        Assert.Contains("empty", report.Warnings[stack.Cues[11].Id]);
        Assert.False(report.IsBroken(stack.Cues[12].Id));
        Assert.Contains("0 to 125", Reason("01.140"));
        Assert.Equal(7, report.BrokenCount);
        Assert.Empty(report.StackNotes);

        // The environment matters: no video runtime, and a file that vanished.
        var noVideo = CueValidator.Validate(state, stack, new CueValidationContext { FileExists = _ => false, VideoDecoderAvailable = false });
        Assert.Contains("file missing", noVideo.ReasonFor(stack.Cues[5].Id));
        Assert.Contains("libVLC", noVideo.Issues.Single(i => i.CueId == stack.Cues[6].Id && i.Text.Contains("libVLC")).Text);

        // One cue against the live state, as GO re-checks it.
        Assert.Equal(0, CueValidator.ValidateOne(state, stack.Cues[0], ctx).BrokenCount);
        Assert.Equal(1, CueValidator.ValidateOne(state, stack.Cues[1], ctx).BrokenCount);
    }

    [Fact]
    public void SoftIssuesWarnWithoutBreakingACue()
    {
        var state = SettingsStore.Fresh();
        state.Pattern.Kind = PatternKind.Media;
        state.Pattern.Media.Source = MediaSource.Image;
        state.Pattern.Media.ImagePath = "/gone/logo.png";
        state.LooksAndCues.Looks.Add(new LookConfig { Name = "Logo", Json = LookService.Capture(state) });
        var stack = CueStacks.Caller(state);
        stack.Cues.Add(Cue("01.010", "Logo", Act(CueActionKind.ApplyLook, LookService.Find(state, "Logo")!.Id)));

        var report = CueValidator.Validate(state, stack, new CueValidationContext { FileExists = _ => false });
        Assert.Equal(0, report.BrokenCount);
        Assert.Contains("logo.png", report.Warnings[stack.Cues[0].Id]);
    }

    [Fact]
    public void PresenterStepsMigrateIntoTheClickerListOnce()
    {
        var state = new ShowState { SchemaVersion = 4 };
        state.LooksAndCues.Looks.Add(new LookConfig { Name = "Walk-in", Json = LookService.Capture(state) });
        state.Presenter.Steps.Add(new PresenterStepConfig { LookName = "walk-in", Label = "Opening" });
        state.Presenter.Steps.Add(new PresenterStepConfig { LookName = "Missing" });
        state.Presenter.Loop = true;

        SettingsStore.Migrate(state);

        Assert.Equal(ShowState.CurrentSchemaVersion, state.SchemaVersion);
        Assert.Equal(2, state.Stacks.Count);
        var clicker = CueStacks.Clicker(state);
        var caller = CueStacks.Caller(state);
        Assert.NotSame(clicker, caller);
        Assert.Equal(StackRole.Caller, caller.Role);
        Assert.Empty(caller.Cues);
        Assert.Equal(2, clicker.Cues.Count);
        Assert.Equal("Opening", clicker.Cues[0].Name);
        Assert.Equal("Opening", clicker.Cues[0].Notes);
        Assert.Equal("01.010", clicker.Cues[0].Number);
        Assert.Equal("01.020", clicker.Cues[1].Number);
        Assert.Equal(LookService.Find(state, "Walk-in")!.Id, clicker.Cues[0].Actions.Single().Target); // by id now
        Assert.Equal("Missing", clicker.Cues[1].Actions.Single().Target);                            // by name: reads as broken
        Assert.True(clicker.LoopAtEnd);
        Assert.Empty(state.Presenter.Steps);

        // Running the upgrade again (a schema-5 file) leaves the list alone.
        SettingsStore.Migrate(state);
        Assert.Equal(2, clicker.Cues.Count);
        Assert.Equal(2, state.Stacks.Count);

        var fresh = SettingsStore.Fresh();
        Assert.Equal(2, fresh.Stacks.Count);
        Assert.True(CueStacks.Clicker(fresh).IsClicker);
    }

    [Fact]
    public void TheSummaryReadsLikeTheScript()
    {
        var state = ShowWithLook("Awards holding", PatternKind.Grid);
        state.Stingers.Items.Add(new StingerItemConfig { Name = "Applause", Path = "/a.wav" });
        var look = LookService.Find(state, "Awards holding")!;
        var cue = Cue("01.010", "Holding",
            Act(CueActionKind.ApplyLook, look.Id, "cut"),
            Act(CueActionKind.AudioPlay),
            Act(CueActionKind.PlaylistPart, "Main"),
            Act(CueActionKind.StingerFire, "Applause"),
            Act(CueActionKind.MessageOn, "", "Welcome back"));

        Assert.Equal("Apply 'Awards holding' (cut) + Play audio + Part 'Main' + +2 more", CueSummary.Describe(state, cue));
        Assert.Equal("VOG 'Applause'", CueSummary.DescribeAction(state, cue.Actions[3]));   // a default item is a VOG
        Assert.Equal("Message 'Welcome back'", CueSummary.DescribeAction(state, cue.Actions[4]));

        // A stinger says where it leaves the show, in the same phrase the desk and the picker use.
        state.Stingers.Items.Add(new StingerItemConfig
        {
            Name = "Whoosh", Path = "/w.mp4", Kind = StingerKind.Sting, After = StingerAfter.Manual,
        });
        Assert.Equal("Sting 'Whoosh' (hold for a take)",
            CueSummary.DescribeAction(state, Act(CueActionKind.StingerFire, "Whoosh")));
        Assert.Equal("Sting 'gone'", CueSummary.DescribeAction(state, Act(CueActionKind.StingerFire, "gone")));
        Assert.Equal("No actions — notes only.", CueSummary.Describe(state, Cue("01.020", "Note")));
        Assert.Equal("Apply 'nope' (800 ms)", CueSummary.DescribeAction(state, Act(CueActionKind.ApplyLook, "nope", "800")));
    }

    [Fact]
    public void TheValidatorChecksWhereAStingerEnds()
    {
        var state = ShowWithLook("Walk-in", PatternKind.Grid);
        var look = LookService.Find(state, "Walk-in")!;
        var caller = CueStacks.Caller(state);
        var sting = new StingerItemConfig { Id = "s1", Name = "Whoosh", Path = "/show/whoosh.mp4", Kind = StingerKind.Sting };
        state.Stingers.Items.Add(sting);
        var vogClip = new StingerItemConfig { Id = "v1", Name = "Winner", Path = "/show/winner.mp4" };
        state.Stingers.Items.Add(vogClip);
        var ctx = new CueValidationContext { FileExists = _ => true, VideoDecoderAvailable = true };

        // Validated on their own, as GO re-checks a cue — the caller's list stays empty on purpose.
        var cue = Cue("09.010", "Hit", Act(CueActionKind.StingerFire, "s1"));

        // An after-policy that points nowhere is a Hard issue, named where the operator can fix it.
        sting.After = StingerAfter.Custom;
        sting.AfterTarget = "gone";
        Assert.Contains("ends on a look or cue that is not there", CueValidator.ValidateOne(state, cue, ctx).ReasonFor(cue.Id));

        sting.AfterTarget = look.Id;
        Assert.Equal(0, CueValidator.ValidateOne(state, cue, ctx).BrokenCount);

        sting.After = StingerAfter.Next;
        sting.AfterTarget = caller.Id;
        Assert.Equal(0, CueValidator.ValidateOne(state, cue, ctx).BrokenCount);

        // Blank Next on an empty caller list is worth saying, not worth refusing.
        sting.AfterTarget = "";
        var soft = CueValidator.ValidateOne(state, cue, ctx);
        Assert.Equal(0, soft.BrokenCount);
        Assert.Contains("the caller's list is empty", soft.Warnings[cue.Id]);

        // The takeover rule follows the file, not the kind: a video VOG takes every screen too.
        var shared = Cue("09.020", "Clip with a look", Act(CueActionKind.StingerFire, "v1"), Act(CueActionKind.ApplyLook, look.Id));
        Assert.Contains("cannot share a cue", CueValidator.ValidateOne(state, shared, ctx).ReasonFor(shared.Id));
    }

    [Fact]
    public void ARecalledLookRearmsADurationCountdownButATransferDoesNot()
    {
        var state = new ShowState();
        state.Countdown.Enabled = true;
        state.Countdown.TargetKind = CountdownTargetKind.Duration;
        state.Countdown.ArmedAtUtc = new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var json = LookService.Capture(state);

        var transfer = new ShowState();
        Assert.True(LookService.Apply(json, transfer));
        Assert.Equal(new DateTime(2020, 1, 1, 0, 0, 0, DateTimeKind.Utc), transfer.Countdown.ArmedAtUtc);

        var recall = new ShowState();
        Assert.True(LookService.Apply(json, recall, rearmCountdown: true));
        Assert.True(recall.Countdown.ArmedAtUtc > DateTime.UtcNow.AddMinutes(-1));
    }

    [Fact]
    public void AFadeOverrideRidesExactlyOneSnapshot()
    {
        var state = new ShowState();
        state.Transition.Enabled = false;
        state.Transition.DurationMs = 500;
        var bus = new SnapshotBus(state);
        bus.Publish(state);
        var plain = bus.Current;
        Assert.False(plain.FadesEnabled);

        bus.FadeOnNextPublish(800);
        bus.Publish(state);
        var faded = bus.Current;
        Assert.Equal(800, faded.FadeOverrideMs);
        Assert.Equal(faded.Version, faded.FadeOverrideVersion);
        Assert.True(faded.FadesEnabled); // the setting is off, the override is on
        Assert.Equal(0.8, faded.FadeSecondsFor(faded.Version), 3);

        bus.Publish(state);
        var next = bus.Current;
        Assert.False(next.FadesEnabled); // one publish only
        Assert.Equal(0.5, next.FadeSecondsFor(next.Version), 3);
    }

    [Fact]
    public void LookReferencesIncludeCueActions()
    {
        var state = SettingsStore.Fresh();
        var look = new LookConfig { Name = "Walk-in" };
        state.LooksAndCues.Looks.Add(look);
        var caller = CueStacks.Caller(state);
        caller.Cues.Add(Cue("03.020", "Five-minute call", Act(CueActionKind.ApplyLook, look.Id)));
        CueStacks.Clicker(state).Cues.Add(Cue("01.010", "Opening", Act(CueActionKind.ApplyLook, "walk-in")));

        var refs = LookService.References(state, look);
        Assert.Contains("Cue stack cue 03.020 Five-minute call", refs);
        Assert.Contains("Clicker list cue 01.010 Opening", refs);
    }
}
