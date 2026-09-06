using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 16: one action vocabulary. A cue's step is a ShowAction — the same kind, target and value
/// the desk, the wire and OSC send — with no cue vocabulary and no map between two. The one table
/// (ActionSpec) classifies every kind; these tests hold the door so a kind added to the vocabulary
/// cannot slip past the cue editor, the summary, the checks or the sheet unclassified.
/// </summary>
public class ActionVocabularyTests
{
    [Fact]
    public void EveryKindIsACueKindOrTheDesksAloneAndEveryCueKindReadsAsWords()
    {
        var state = new ShowState();
        var all = Enum.GetValues<ShowActionKind>();
        var cue = new HashSet<ShowActionKind>(ActionSpec.CueKinds);
        Assert.Equal(cue.Count, ActionSpec.CueKinds.Count);   // no kind twice in the picker
        foreach (var kind in all)
        {
            var deskOnly = ActionSpec.DeskOnly(kind);
            if (kind == ShowActionKind.Unknown)
            {
                Assert.DoesNotContain(kind, cue);
                Assert.Null(deskOnly);
                continue;
            }
            // classified, exactly one way
            Assert.True(cue.Contains(kind) ^ deskOnly is not null, $"{kind}: a cue kind or the desk's alone, one of the two");
            // named for a person, not by its enum name
            Assert.NotEqual(kind.ToString(), ActionSpec.Label(kind));
            if (!cue.Contains(kind))
            {
                Assert.False(string.IsNullOrWhiteSpace(deskOnly), $"{kind}: the desk-only reason is said");
                continue;
            }
            // a cue's step reads as words in the summary (never the raw kind — a Note is the one word that is its own), with or without a target and a value
            foreach (var (target, value) in new[] { ("", ""), ("x", "1"), ("SCREEN 2", "on") })
            {
                var words = CueSummary.DescribeAction(state, new CueActionConfig { Kind = kind, Target = target, Value = value });
                Assert.False(string.IsNullOrWhiteSpace(words), $"{kind} has words");
                if (kind != ShowActionKind.Note) Assert.NotEqual(kind.ToString(), words);
            }
            // the sheet reads the picker's label and the enum name back to the kind
            Assert.Equal(kind, CueSheet.ParseKind(ActionSpec.Label(kind)));
            Assert.Equal(kind, CueSheet.ParseKind(kind.ToString()));
        }
        // the desk's own never parse from a sheet or the assistant's kind words
        Assert.Null(CueSheet.ParseKind("Take"));
        Assert.Null(CueSheet.ParseKind("CUT"));
        Assert.Null(CueSheet.ParseKind("Restart Patterns"));
        Assert.Null(CueSheet.ParseKind("CueGo"));
        // the kinds the round opened to cues are there, by their words
        Assert.Equal(ShowActionKind.LogoOn, CueSheet.ParseKind("logo"));
        Assert.Equal(ShowActionKind.PipOn, CueSheet.ParseKind("PiP"));
        Assert.Equal(ShowActionKind.OverlaysOff, CueSheet.ParseKind("clean picture"));
        Assert.Equal(ShowActionKind.ClockFormat, CueSheet.ParseKind("clock hours"));
        Assert.Equal(ShowActionKind.CountdownTo, CueSheet.ParseKind("back at"));
        Assert.Equal(ShowActionKind.PatternKind, CueSheet.ParseKind("Pattern — change its kind"));
        Assert.Equal(ShowActionKind.StopAll, CueSheet.ParseKind("stop all"));
        Assert.Equal(ShowActionKind.FreezeOn, CueSheet.ParseKind("freeze"));
        Assert.Equal(ShowActionKind.LookBack, CueSheet.ParseKind("previous look"));
        Assert.Equal(ShowActionKind.ApplyLookToPreview, CueSheet.ParseKind("preload"));
        Assert.Equal(ShowActionKind.WebOpen, CueSheet.ParseKind("open page"));
    }

    [Fact]
    public void ACueStepIsAShowActionWithNoTranslationAndTheShowFileReadsAsBefore()
    {
        var step = new CueActionConfig { Kind = ShowActionKind.ClockFormat, Target = "", Value = "24" };
        Assert.Equal(new ShowAction(ShowActionKind.ClockFormat, "", "24"), step.ToAction());
        var fade = new CueActionConfig { Kind = ShowActionKind.FadeToBlack, Target = "SCREEN 2", Value = "1.5" };
        Assert.Equal(new ShowAction(ShowActionKind.FadeToBlack, "SCREEN 2", "1.5"), fade.ToAction());   // seconds, as the wire's line and the desk's key carry them

        // The kind is kept by name — a show file written before the two vocabularies became one loads unchanged.
        var clone = JsonUtil.Clone(new RunCueConfig { Actions = { fade, new CueActionConfig { Kind = ShowActionKind.BlackoutOn } } });
        Assert.Equal(ShowActionKind.FadeToBlack, clone.Actions[0].Kind);
        Assert.Equal("1.5", clone.Actions[0].Value);
        Assert.Equal(ShowActionKind.BlackoutOn, clone.Actions[1].Kind);
        var old = JsonUtil.Deserialize<CueActionConfig>("""{"Kind":"LowerThirdShow","Target":"lt1","Value":"Ada"}""");
        Assert.NotNull(old);
        Assert.Equal(ShowActionKind.LowerThirdShow, old!.Kind);
        Assert.Equal("Ada", old.Value);
        // A kind from a newer build is Unknown here — the checks say so and it never runs.
        var newer = JsonUtil.Deserialize<CueActionConfig>("""{"Kind":"TeleportAudience","Target":"","Value":""}""");
        Assert.Equal(ShowActionKind.Unknown, newer!.Kind);
    }

    [Fact]
    public void TheChecksRefuseTheDesksOwnKindsAndReadTheNewValues()
    {
        var state = new ShowState();
        var stack = CueStacks.Caller(state);

        string? HardFor(ShowActionKind kind, string target = "", string value = "")
        {
            stack.Cues.Clear();
            var cue = new RunCueConfig { Name = kind.ToString() };
            cue.Actions.Add(new CueActionConfig { Kind = kind, Target = target, Value = value });
            stack.Cues.Add(cue);
            return CueValidator.Validate(state, stack, new CueValidationContext { FileExists = _ => true }).ReasonFor(cue.Id);
        }

        // The desk's own kinds are refused with the reason, whatever their target.
        Assert.Contains("desk key", HardFor(ShowActionKind.Take));
        Assert.Contains("desk key", HardFor(ShowActionKind.Cut, "TICKED"));
        Assert.Contains("loop", HardFor(ShowActionKind.CueGo));
        Assert.Contains("passcode", HardFor(ShowActionKind.Restart, "1234"));
        Assert.Contains("newer build", HardFor(ShowActionKind.Unknown));

        // The values the round opened to cues: the clock's hours, a switch, a time of day, a kind of picture, an address, a transition.
        Assert.Null(HardFor(ShowActionKind.ClockFormat, value: "12"));
        Assert.Null(HardFor(ShowActionKind.ClockFormat, value: "24"));
        Assert.Contains("12 or 24", HardFor(ShowActionKind.ClockFormat, value: "13"));
        Assert.Null(HardFor(ShowActionKind.ClockSeconds, value: "on"));
        Assert.Null(HardFor(ShowActionKind.MessageScroll, value: "Toggle"));
        Assert.Null(HardFor(ShowActionKind.ClockDate, value: "hide"));
        Assert.Contains("on, off or toggle", HardFor(ShowActionKind.ClockSeconds, value: "maybe"));
        Assert.Contains("on, off or toggle", HardFor(ShowActionKind.MessageScroll, value: ""));
        Assert.Null(HardFor(ShowActionKind.CountdownTo, value: "14:00"));
        Assert.Contains("HH:mm", HardFor(ShowActionKind.CountdownTo, value: "after lunch"));
        Assert.Null(HardFor(ShowActionKind.PatternKind, value: "grid"));
        Assert.Null(HardFor(ShowActionKind.PatternKind, value: "Color bars"));
        Assert.Contains("kind of picture", HardFor(ShowActionKind.PatternKind, value: "cube"));
        Assert.Null(HardFor(ShowActionKind.WebOpen, value: "https://example.org/board"));
        Assert.Contains("which address", HardFor(ShowActionKind.WebOpen, value: " "));
        Assert.Null(HardFor(ShowActionKind.LookBack, value: "cut"));
        Assert.Contains("transition", HardFor(ShowActionKind.LookBack, value: "slowly"));
        Assert.Null(HardFor(ShowActionKind.OverlaysOff));
        Assert.Null(HardFor(ShowActionKind.StopAll));
        Assert.Null(HardFor(ShowActionKind.FreezeOn));
        Assert.Null(HardFor(ShowActionKind.OutputsOn));
        // A screen toggle names a screen of the rig, like on / off.
        Assert.Contains("not in the rig", HardFor(ShowActionKind.ScreenToggle, "nowhere"));
        // A look into the preview must exist, like a look to air.
        Assert.Contains("not found", HardFor(ShowActionKind.ApplyLookToPreview, "Keynote"));
        // The fade's seconds are checked as seconds.
        Assert.Null(HardFor(ShowActionKind.FadeToBlack, value: "1.5"));
        Assert.Contains("seconds", HardFor(ShowActionKind.FadeToBlack, value: "slow"));

        // The spec knows the new value kinds and the summary reads them.
        Assert.Equal((TargetKind.None, ValueKind.Hours), ActionSpec.For(ShowActionKind.ClockFormat));
        Assert.Equal((TargetKind.None, ValueKind.Switch), ActionSpec.For(ShowActionKind.MessageScroll));
        Assert.Equal((TargetKind.None, ValueKind.ClockTime), ActionSpec.For(ShowActionKind.CountdownTo));
        Assert.Equal((TargetKind.None, ValueKind.PatternKind), ActionSpec.For(ShowActionKind.PatternKind));
        Assert.Equal((TargetKind.Page, ValueKind.Address), ActionSpec.For(ShowActionKind.WebOpen));
        Assert.Equal((TargetKind.Look, ValueKind.None), ActionSpec.For(ShowActionKind.ApplyLookToPreview));
        Assert.Equal("Clock 24-hour", CueSummary.DescribeAction(state, new CueActionConfig { Kind = ShowActionKind.ClockFormat, Value = "24" }));
        Assert.Equal("Pattern: ColorBars", CueSummary.DescribeAction(state, new CueActionConfig { Kind = ShowActionKind.PatternKind, Value = "color bars" }));
        Assert.Equal("Countdown to 14:00", CueSummary.DescribeAction(state, new CueActionConfig { Kind = ShowActionKind.CountdownTo, Value = "14:00" }));
        Assert.Equal("Message scroll on", CueSummary.DescribeAction(state, new CueActionConfig { Kind = ShowActionKind.MessageScroll, Value = "1" }));
        Assert.Equal(PatternKind.LedWall, ActionSpec.ParsePatternKind("led wall"));
        Assert.Null(ActionSpec.ParsePatternKind("cube"));
    }
}
