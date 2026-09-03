using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>Schema 4: tolerant enums, look ids and per-screen capture, the show name.</summary>
public class ActionLayerCoreTests
{
    [Fact]
    public void AnUnknownEnumValueFromANewerBuildLoadsWithTheRestOfTheShowIntact()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-enum-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var state = new ShowState { Name = "Awards 2026" };
        state.Pattern.Kind = PatternKind.ColorBars;
        state.Overlays.Clock.Enabled = true;
        var json = JsonUtil.Serialize(state).Replace("\"Kind\": \"ColorBars\"", "\"Kind\": \"Hologram\"");
        Assert.Contains("Hologram", json);
        var path = Path.Combine(dir, "newer.patshow.json");
        File.WriteAllText(path, json);

        var loaded = new SettingsStore(dir).LoadFrom(path);
        Assert.NotNull(loaded);
        Assert.Equal(PatternKind.Grid, loaded!.Pattern.Kind);   // the enum's first member, not a quarantined file
        Assert.Equal("Awards 2026", loaded.Name);
        Assert.True(loaded.Overlays.Clock.Enabled);
        Assert.Empty(Directory.GetFiles(dir, "*.corrupt-*"));
        Assert.Equal(ShowState.CurrentSchemaVersion, loaded.SchemaVersion);
    }

    [Fact]
    public void EnumsStillRoundTripByName()
    {
        var state = new ShowState();
        state.Pattern.Kind = PatternKind.Multiview;
        state.Mode = ShowMode.Prep;
        var json = JsonUtil.Serialize(state);
        Assert.Contains("\"Kind\": \"Multiview\"", json);
        Assert.Contains("\"Mode\": \"Prep\"", json);
        var back = JsonUtil.Deserialize<ShowState>(json)!;
        Assert.Equal(PatternKind.Multiview, back.Pattern.Kind);
        Assert.Equal(ShowMode.Prep, back.Mode);
    }

    [Fact]
    public void LooksCaptureWhichScreensShowTheirOwnPattern()
    {
        var saved = new ShowState();
        saved.Output.Placements.Add(new ScreenPlacement { ScreenId = "a" });
        saved.Output.Placements.Add(new ScreenPlacement { ScreenId = "b", UseCustomPattern = true });
        var json = LookService.Capture(saved);
        Assert.Contains("CustomScreens", json);

        var target = new ShowState();
        target.Output.Placements.Add(new ScreenPlacement { ScreenId = "a", UseCustomPattern = true });
        target.Output.Placements.Add(new ScreenPlacement { ScreenId = "b" });
        Assert.True(LookService.Apply(json, target));
        Assert.False(target.Output.Placements[0].UseCustomPattern);
        Assert.True(target.Output.Placements[1].UseCustomPattern);

        // A look saved before the field existed leaves the flags alone.
        var old = System.Text.RegularExpressions.Regex.Replace(json, @",?\s*""CustomScreens"":\s*\[[^\]]*\]", "");
        Assert.DoesNotContain("CustomScreens", old);
        var untouched = new ShowState();
        untouched.Output.Placements.Add(new ScreenPlacement { ScreenId = "a", UseCustomPattern = true });
        Assert.True(LookService.Apply(old, untouched));
        Assert.True(untouched.Output.Placements[0].UseCustomPattern);
    }

    [Fact]
    public void LooksAndStingersCarryStableIdsThroughSaveAndLoad()
    {
        var state = new ShowState();
        state.LooksAndCues.Looks.Add(new LookConfig { Name = "Walk-in" });
        state.Stingers.Items.Add(new StingerItemConfig { Path = "sting.wav" });
        var lookId = state.LooksAndCues.Looks[0].Id;
        var stingId = state.Stingers.Items[0].Id;
        Assert.Equal(32, lookId.Length);

        var back = JsonUtil.Deserialize<ShowState>(JsonUtil.Serialize(state))!;
        Assert.Equal(lookId, back.LooksAndCues.Looks[0].Id);
        Assert.Equal(stingId, back.Stingers.Items[0].Id);

        var copy = new ShowState();
        ModelCopier.Copy(back, copy);
        Assert.Equal(lookId, copy.LooksAndCues.Looks[0].Id);
    }

    [Fact]
    public void TheResolverListsWhatStillPointsAtALook()
    {
        var state = new ShowState();
        var look = new LookConfig { Name = "Walk-in" };
        state.LooksAndCues.Looks.Add(look);
        state.LooksAndCues.Cues.Add(new CueConfig { Time = "18:00", LookName = "walk-in" });
        state.Presenter.Steps.Add(new PresenterStepConfig { LookName = "WALK-IN" });
        Assert.Same(look, LookService.Find(state, "Walk-In"));
        Assert.Null(LookService.Find(state, ""));
        var refs = LookService.References(state, look);
        Assert.Equal(new[] { "scheduled cue at 18:00", "presenter step 1" }, refs);
    }

    [Fact]
    public void AShowTakesItsNameFromTheFileItWasLoadedFrom()
    {
        Assert.Equal("awards-2026", SettingsStore.ShowNameFor(@"C:\shows\awards-2026.patshow.json"));
        Assert.Equal("awards", SettingsStore.ShowNameFor("/tmp/awards.json"));
        Assert.Equal("", SettingsStore.ShowNameFor("/tmp/patterns.settings.json"));
    }

    [Fact]
    public void TheShowLogAppendsAndReadsItsTail()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-log-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        var log = new ShowLog(dir);
        for (var i = 0; i < 5; i++)
        {
            log.Record("desk", "ApplyLook", $"look{i}", "Done", "");
        }
        File.AppendAllText(log.Path, "{torn line");
        var tail = log.Tail(3);
        Assert.Equal(new[] { "look2", "look3", "look4" }, tail.Select(e => e.Target));
        Assert.All(tail, e => Assert.Equal("desk", e.Origin));
    }
}
