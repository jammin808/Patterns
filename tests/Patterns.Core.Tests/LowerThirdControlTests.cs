using Patterns.Core.LowerThirds;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>Lower thirds as the desk drives them: the remote verbs, the cue action, a look that carries one, the files.</summary>
public class LowerThirdControlTests
{
    [Fact]
    public void TheRemoteVerbsParseByNumberNameAndOff()
    {
        Assert.Equal(new ShowAction(ShowActionKind.LowerThirdShow, "2"), ControlProtocol.Parse("LOWERTHIRD 2").Action);
        Assert.Equal(new ShowAction(ShowActionKind.LowerThirdShow, "Keynote speaker"), ControlProtocol.Parse("lt Keynote speaker").Action);
        Assert.Equal(new ShowAction(ShowActionKind.LowerThirdHide), ControlProtocol.Parse("LT OFF").Action);
        Assert.Equal(new ShowAction(ShowActionKind.LowerThirdHide), ControlProtocol.Parse("LOWERTHIRD hide").Action);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("LT").Kind);

        // Round 73: the timed forms.
        Assert.Equal(new ShowAction(ShowActionKind.LowerThirdShowFor, "2", "8"), ControlProtocol.Parse("LT 2 FOR 8").Action);
        Assert.Equal(new ShowAction(ShowActionKind.LowerThirdShowFor, "Keynote speaker", "7.5"), ControlProtocol.Parse("LOWERTHIRD Keynote speaker for 7.5").Action);
        Assert.Equal(new ShowAction(ShowActionKind.LowerThirdShowFor, "2", "STAY"), ControlProtocol.Parse("LT 2 STAY").Action);
        Assert.Equal(new ShowAction(ShowActionKind.LowerThirdShowFor, "2", "STAY"), ControlProtocol.Parse("LT 2 FOR STAY").Action);
        Assert.Equal(new ShowAction(ShowActionKind.LowerThirdHold, "2", "6"), ControlProtocol.Parse("LT 2 HOLD 6").Action);
        Assert.Equal(new ShowAction(ShowActionKind.LowerThirdHold, "Keynote", "STAY"), ControlProtocol.Parse("LT Keynote HOLD STAY").Action);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("LT 2 FOR soon").Kind);
        // A design may be called anything: words that are not the timed form are a name, as they always were.
        Assert.Equal(new ShowAction(ShowActionKind.LowerThirdShow, "2 HOLD"), ControlProtocol.Parse("LT 2 HOLD").Action);
        Assert.Equal(new ShowAction(ShowActionKind.LowerThirdShow, "FOR 8"), ControlProtocol.Parse("LT FOR 8").Action);
    }

    /// <summary>Round 73: the timed verbs have their spec, their cue words, their sheet words, their checks and their OSC addresses.</summary>
    [Fact]
    public void TheTimedVerbsAreCueStepsWithWordsAndChecks()
    {
        Assert.Equal((TargetKind.LowerThird, ValueKind.Seconds), ActionSpec.For(ShowActionKind.LowerThirdShowFor));
        Assert.Equal((TargetKind.LowerThird, ValueKind.Seconds), ActionSpec.For(ShowActionKind.LowerThirdHold));
        Assert.Contains(ShowActionKind.LowerThirdShowFor, ActionSpec.CueKinds);
        Assert.Contains(ShowActionKind.LowerThirdHold, ActionSpec.CueKinds);
        Assert.StartsWith("Lower third on for", ActionSpec.Label(ShowActionKind.LowerThirdShowFor));
        Assert.StartsWith("Lower third hold", ActionSpec.Label(ShowActionKind.LowerThirdHold));
        Assert.Equal(ShowActionKind.LowerThirdShowFor, CueSheet.ParseKind("lt for"));
        Assert.Equal(ShowActionKind.LowerThirdHold, CueSheet.ParseKind("lower third hold"));

        var state = SettingsStore.Fresh();
        var neon = LowerThirdPresets.Create("Neon");
        state.LowerThirds.Designs.Add(neon);
        Assert.Equal("Lower third 'Neon' for 8 s", CueSummary.DescribeAction(state, new CueActionConfig { Kind = ShowActionKind.LowerThirdShowFor, Target = neon.Id, Value = "8" }));
        Assert.Equal("Lower third 'Neon' — until hidden", CueSummary.DescribeAction(state, new CueActionConfig { Kind = ShowActionKind.LowerThirdShowFor, Target = neon.Id, Value = "STAY" }));
        Assert.Equal("Lower third 'Neon' holds 6 s", CueSummary.DescribeAction(state, new CueActionConfig { Kind = ShowActionKind.LowerThirdHold, Target = neon.Id, Value = "6" }));
        Assert.Equal("Lower third 'Neon' stays until hidden", CueSummary.DescribeAction(state, new CueActionConfig { Kind = ShowActionKind.LowerThirdHold, Target = neon.Id, Value = "STAY" }));

        var ctx = new CueValidationContext();
        RunCueConfig Cue(ShowActionKind kind, string target, string value)
        {
            var cue = new RunCueConfig { Name = kind.ToString() };
            cue.Actions.Add(new CueActionConfig { Kind = kind, Target = target, Value = value });
            return cue;
        }
        Assert.Equal(0, CueValidator.ValidateOne(state, Cue(ShowActionKind.LowerThirdShowFor, neon.Id, "8"), ctx).BrokenCount);
        Assert.Equal(0, CueValidator.ValidateOne(state, Cue(ShowActionKind.LowerThirdHold, neon.Id, "STAY"), ctx).BrokenCount);
        Assert.Equal(1, CueValidator.ValidateOne(state, Cue(ShowActionKind.LowerThirdShowFor, neon.Id, "soon"), ctx).BrokenCount);
        Assert.Equal(1, CueValidator.ValidateOne(state, Cue(ShowActionKind.LowerThirdHold, "nope", "8"), ctx).BrokenCount);

        Assert.Equal("LOWERTHIRD 2 FOR 8", OscMap.ToLine(OscMessage.Of("/patterns/lowerthird/2/for", "8")));
        Assert.Equal("LOWERTHIRD 2 FOR 8", OscMap.ToLine(OscMessage.Of("/patterns/lt/2/for/8")));
        Assert.Equal("LOWERTHIRD 2 STAY", OscMap.ToLine(OscMessage.Of("/patterns/lowerthird/2/stay")));
        Assert.Equal("LOWERTHIRD Keynote HOLD STAY", OscMap.ToLine(OscMessage.Of("/patterns/lowerthird/Keynote/hold/STAY")));
        Assert.Equal(ShowActionKind.LowerThirdShowFor, ControlProtocol.Parse(OscMap.ToLine(OscMessage.Of("/patterns/lowerthird/2/for", "8"))!).Action.Kind);
        Assert.Contains(OscMap.Reference, r => r.Address.StartsWith("/patterns/lowerthird/<n|name>/for"));
    }

    [Fact]
    public void TheCueActionHasItsSpecSummaryAndChecks()
    {
        Assert.Equal((TargetKind.LowerThird, ValueKind.Person), ActionSpec.For(ShowActionKind.LowerThirdShow));
        Assert.Equal((TargetKind.None, ValueKind.None), ActionSpec.For(ShowActionKind.LowerThirdHide));
        Assert.Equal("Lower third on", ActionSpec.Label(ShowActionKind.LowerThirdShow));
        Assert.Contains(ShowActionKind.LowerThirdShow, ActionSpec.CueKinds);
        Assert.Contains(ShowActionKind.LowerThirdHide, ActionSpec.CueKinds);
        Assert.False(ActionSpec.ChangesContent(ShowActionKind.LowerThirdShow)); // it rides over the content, so it can share a cue with a look

        var state = SettingsStore.Fresh();
        var neon = LowerThirdPresets.Create("Neon");
        state.LowerThirds.Designs.Add(neon);
        var empty = LowerThirdPresets.Blank();
        state.LowerThirds.Designs.Add(empty);
        var stack = CueStacks.Caller(state);
        var good = new RunCueConfig { Name = "Speaker on" };
        good.Actions.Add(new CueActionConfig { Kind = ShowActionKind.LowerThirdShow, Target = neon.Id });
        var thin = new RunCueConfig { Name = "Nothing in it" };
        thin.Actions.Add(new CueActionConfig { Kind = ShowActionKind.LowerThirdShow, Target = empty.Id });
        var bad = new RunCueConfig { Name = "Gone" };
        bad.Actions.Add(new CueActionConfig { Kind = ShowActionKind.LowerThirdShow, Target = "no-such-design" });
        var off = new RunCueConfig { Name = "Speaker off" };
        off.Actions.Add(new CueActionConfig { Kind = ShowActionKind.LowerThirdHide });
        stack.Cues.Add(good);
        stack.Cues.Add(thin);
        stack.Cues.Add(bad);
        stack.Cues.Add(off);

        Assert.Equal("Lower third 'Neon'", CueSummary.DescribeAction(state, good.Actions[0]));
        Assert.Equal("Lower third off", CueSummary.DescribeAction(state, off.Actions[0]));
        var report = CueValidator.Validate(state, stack, new CueValidationContext { FileExists = _ => true });
        Assert.DoesNotContain(report.Issues, p => p.CueId == good.Id);
        Assert.Contains(report.Issues, p => p.CueId == thin.Id && p.Severity != IssueSeverity.Hard && p.Text.Contains("nothing in it"));
        Assert.Contains(report.Issues, p => p.CueId == bad.Id && p.Severity == IssueSeverity.Hard && p.Text.Contains("not found"));
        Assert.DoesNotContain(report.Issues, p => p.CueId == off.Id);
    }

    [Fact]
    public void ALookCarriesTheLowerThirdOnAirAndARecallShowsItAgain()
    {
        var state = new ShowState();
        var clean = LowerThirdPresets.Create("Clean");
        state.LowerThirds.Designs.Add(clean);

        // Saved with nothing on: recalling it takes a lower third off.
        var plain = LookService.Capture(state);
        state.LowerThirds.Show(clean, ShowClock.UtcAt(-10));   // instants before this process started: "now" is always later
        Assert.True(state.LowerThirds.IsShowing);
        Assert.True(LookService.Apply(plain, state, rearmCountdown: true));
        Assert.False(state.LowerThirds.IsShowing);
        Assert.NotNull(state.LowerThirds.HiddenAtUtc);

        // Saved with it on: the recall shows it afresh (a new start instant), a state transfer leaves a running one alone.
        state.LowerThirds.Show(clean, ShowClock.UtcAt(-8));
        var withLower = LookService.Capture(state);
        Assert.Contains(clean.Id, withLower);
        state.LowerThirds.Hide(ShowClock.UtcAt(-4));
        Assert.True(LookService.Apply(withLower, state, rearmCountdown: true));
        Assert.True(state.LowerThirds.IsShowing);
        Assert.True(state.LowerThirds.ShownAtUtc > ShowClock.UtcAt(-4));
        var shownAt = state.LowerThirds.ShownAtUtc;
        Assert.True(LookService.Apply(withLower, state, rearmCountdown: false));
        Assert.Equal(shownAt, state.LowerThirds.ShownAtUtc); // still the same run

        // The fingerprint tells the two looks apart, and a look from before this field leaves it alone.
        Assert.NotEqual(LookService.Fingerprint(plain), LookService.Fingerprint(withLower));
        Assert.True(LookService.Matches(withLower, state));
        var old = JsonUtil.Serialize(JsonUtil.Deserialize<LookData>(plain)!).Replace("\"LowerThirdId\": \"\",", "").Replace("\"LowerThirdId\": \"\"", "");
        Assert.DoesNotContain("LowerThirdId", old);
        Assert.True(LookService.Apply(old, state, rearmCountdown: true));
        Assert.True(state.LowerThirds.IsShowing);
    }

    [Fact]
    public void ADesignSavesAsAFileAndLoadsBack()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-lt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new SettingsStore(dir);
            Assert.Empty(store.ListLowerThirds());
            var stamp = LowerThirdPresets.Create("Stamp");
            stamp.Name = "Doors: 19/00";
            var path = store.SaveLowerThird(stamp.Name, stamp);
            Assert.True(File.Exists(path));
            Assert.Equal(store.LowerThirdsDirectory, Path.GetDirectoryName(path));
            Assert.EndsWith(".json", path);
            Assert.StartsWith("Doors", Path.GetFileName(path));   // the characters a file name cannot take become underscores
            var listed = Assert.Single(store.ListLowerThirds());
            Assert.Equal(Path.GetFileNameWithoutExtension(path), listed.Name);
            var back = store.LoadLowerThird(listed.Path);
            Assert.NotNull(back);
            Assert.Equal(stamp.Id, back!.Id);
            Assert.Equal(stamp.Elements.Count, back.Elements.Count);
            Assert.Equal(Anchor9.TopRight, back.Anchor);
            Assert.Null(store.LoadLowerThird(Path.Combine(dir, "missing.json")));
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* a temp folder left behind is not a failed test */ }
        }
    }
}
