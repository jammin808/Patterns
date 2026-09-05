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
        Assert.Equal(new RemoteCommand(RemoteCommandKind.LowerThirdShow, 2, ""), ControlProtocol.Parse("LOWERTHIRD 2"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.LowerThirdShow, 0, "Keynote speaker"), ControlProtocol.Parse("lt Keynote speaker"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.LowerThirdHide, 0, ""), ControlProtocol.Parse("LT OFF"));
        Assert.Equal(new RemoteCommand(RemoteCommandKind.LowerThirdHide, 0, ""), ControlProtocol.Parse("LOWERTHIRD hide"));
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("LT").Kind);
    }

    [Fact]
    public void TheCueActionHasItsSpecSummaryAndChecks()
    {
        Assert.Equal((TargetKind.LowerThird, ValueKind.None), CueActionSpec.For(CueActionKind.LowerThirdShow));
        Assert.Equal((TargetKind.None, ValueKind.None), CueActionSpec.For(CueActionKind.LowerThirdHide));
        Assert.Equal("Lower third on", CueActionSpec.Label(CueActionKind.LowerThirdShow));
        Assert.Contains(CueActionKind.LowerThirdShow, CueActionSpec.Editable);
        Assert.Contains(CueActionKind.LowerThirdHide, CueActionSpec.Editable);
        Assert.False(CueActionSpec.ChangesContent(CueActionKind.LowerThirdShow)); // it rides over the content, so it can share a cue with a look

        var state = SettingsStore.Fresh();
        var neon = LowerThirdPresets.Create("Neon");
        state.LowerThirds.Designs.Add(neon);
        var empty = LowerThirdPresets.Blank();
        state.LowerThirds.Designs.Add(empty);
        var stack = CueStacks.Caller(state);
        var good = new RunCueConfig { Name = "Speaker on" };
        good.Actions.Add(new CueActionConfig { Kind = CueActionKind.LowerThirdShow, Target = neon.Id });
        var thin = new RunCueConfig { Name = "Nothing in it" };
        thin.Actions.Add(new CueActionConfig { Kind = CueActionKind.LowerThirdShow, Target = empty.Id });
        var bad = new RunCueConfig { Name = "Gone" };
        bad.Actions.Add(new CueActionConfig { Kind = CueActionKind.LowerThirdShow, Target = "no-such-design" });
        var off = new RunCueConfig { Name = "Speaker off" };
        off.Actions.Add(new CueActionConfig { Kind = CueActionKind.LowerThirdHide });
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
            try { Directory.Delete(dir, true); } catch { }
        }
    }
}
