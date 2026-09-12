using System.Globalization;
using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>The round-29 review's findings, each pinned (docs/REVIEW.md, round 29).</summary>
public class Round29Tests
{
    [Fact]
    public void IcsDatesAreGregorianWhateverTheMachinesCalendarIs()
    {
        // A Thai Windows reads "2026" as a Buddhist-era year with a null provider: five centuries out,
        // and every event outside the ticker's 24-hour window.
        var was = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = new CultureInfo("th-TH");
            Assert.True(FeedParser.TryParseIcsDate("20260830T183000", out var dt));
            Assert.Equal(new DateTime(2026, 8, 30, 18, 30, 0), dt);
        }
        finally
        {
            CultureInfo.CurrentCulture = was;
        }
    }

    [Fact]
    public void AOneSecondFollowSurvivesTheSheetRoundTrip()
    {
        var state = new ShowState();
        var stack = CueStacks.Caller(state);
        stack.Cues.Add(new RunCueConfig { Number = "01.010", Name = "One second", FollowSeconds = 1 });
        stack.Cues.Add(new RunCueConfig { Number = "01.020", Name = "At once", FollowSeconds = 0 });
        stack.Cues.Add(new RunCueConfig { Number = "01.030", Name = "Manual", FollowSeconds = null });
        var csv = CueSheet.Export(state, stack);

        var back = CueSheet.Import(CsvTable.Parse(csv), new ShowState());
        Assert.Equal(1, back.Cues[0].FollowSeconds);      // "1" is a second, not a yes
        Assert.Equal(0, back.Cues[1].FollowSeconds);
        Assert.Null(back.Cues[2].FollowSeconds);

        var typed = CueSheet.Import(CsvTable.Parse("Name,Follow\nA,yes\nB,no\nC,90 s\n"), new ShowState());
        Assert.Equal(0, typed.Cues[0].FollowSeconds);
        Assert.Null(typed.Cues[1].FollowSeconds);
        Assert.Equal(90, typed.Cues[2].FollowSeconds);
    }

    [Fact]
    public void AVideoStingerCannotShareACueWithAPresetEither()
    {
        var state = new ShowState();
        state.Stingers.Items.Add(new StingerItemConfig { Name = "Clip", Path = "/show/clip.mp4" });
        var stack = CueStacks.Caller(state);
        var cue = new RunCueConfig { Number = "01.010", Name = "Clip and a preset" };
        cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.StingerFire, Target = "Clip" });
        cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.ScreenPreset, Target = "s1", Value = "Walk-in" });
        stack.Cues.Add(cue);
        var ctx = new CueValidationContext { FileExists = p => p.EndsWith(".mp4"), VideoDecoderAvailable = true, Presets = new[] { "Walk-in" } };

        var report = CueValidator.Validate(state, stack, ctx);
        Assert.Contains("cannot share a cue", report.ReasonFor(cue.Id) ?? "");
    }

    [Fact]
    public void TheBundleRedactsSecretsAndKeepsTheInputLabelsKeys()
    {
        const string json = "{ \"AdminPasscode\": \"4321\", \"ManagementToken\": \"t0k\", \"ApiKey\": \"sk-x\", \"Key\": \"ndi:Cam 1\", \"Label\": \"Stage\", \"Passcode\": \"\" }";
        var redacted = SupportBundle.Redact(json);
        Assert.Contains("\"AdminPasscode\": \"•••\"", redacted);
        Assert.Contains("\"ManagementToken\": \"•••\"", redacted);
        Assert.Contains("\"ApiKey\": \"•••\"", redacted);
        Assert.Contains("\"Key\": \"ndi:Cam 1\"", redacted);   // which input the nickname belongs to
        Assert.Contains("\"Passcode\": \"\"", redacted);        // an empty value stays empty
    }

    [Fact]
    public void AShowFileWithAnOddRowLoadsInsteadOfBeingQuarantined()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-tests-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new SettingsStore(dir);
            var state = new ShowState { SchemaVersion = 6 };
            state.Brand.PrimaryColor = "#ABCDEF";
            var json = JsonUtil.Serialize(state)
                .Replace("\"MediaLibrary\": []", "\"MediaLibrary\": [ { \"Path\": null } ]");
            Assert.Contains("\"Path\": null", json);
            File.WriteAllText(store.SettingsPath, json);

            var loaded = store.Load();
            Assert.Equal("#ABCDEF", loaded.Brand.PrimaryColor);          // the operator's show, not a blank one
            Assert.True(File.Exists(store.SettingsPath));                  // and not quarantined
            Assert.Empty(Directory.GetFiles(dir, "*.corrupt-*"));
            Assert.Equal("", loaded.MediaLibrary[0].Path);
            Assert.Equal(ShowState.CurrentSchemaVersion, loaded.SchemaVersion); // the upgrade ran through
        }
        finally
        {
            try { Directory.Delete(dir, true); } catch { /* best effort */ }
        }
    }

    [Fact]
    public void RetiredFramesAreBoundedByCountAndByTime()
    {
        RetiredFrames.Sweep();
        Thread.Sleep(RetiredFrames.Hold + TimeSpan.FromMilliseconds(50));
        RetiredFrames.Sweep();
        var before = RetiredFrames.Count;
        for (var i = 0; i < RetiredFrames.MaxHeld * 3; i++)
        {
            RetiredFrames.Retire(SKImage.Create(new SKImageInfo(4, 4)));
        }
        Assert.True(RetiredFrames.Count <= RetiredFrames.MaxHeld, $"{RetiredFrames.Count} frames held");
        Assert.True(RetiredFrames.Count >= Math.Min(RetiredFrames.MaxHeld, 1));

        Thread.Sleep(RetiredFrames.Hold + TimeSpan.FromMilliseconds(50));
        RetiredFrames.Sweep();
        Assert.Equal(before, RetiredFrames.Count);
        Assert.Equal(MemoryBudget.HeldFrameMs, (int)RetiredFrames.Hold.TotalMilliseconds); // the number the Machine page prints
    }

    [Fact]
    public void TheStageNameIsMadeOncePerKind()
    {
        Assert.Same(FrameStage.PatternOf(PatternKind.Fractal), FrameStage.PatternOf(PatternKind.Fractal));
        Assert.Equal("pattern:Fractal", FrameStage.PatternOf(PatternKind.Fractal));
        Assert.Equal("the Fractal pattern", FrameStage.Words(FrameStage.PatternOf(PatternKind.Fractal)));
    }

    [Theory]
    [InlineData("on", true)]
    [InlineData("SHOW", true)]
    [InlineData("1", true)]
    [InlineData("hide", false)]
    [InlineData("no", false)]
    [InlineData("toggle", null)]
    [InlineData("flip", null)]
    [InlineData("", null)]
    public void TheChecksAndTheVerbsReadOneTableOfSwitchWords(string word, bool? on)
    {
        var expectedWord = on switch { true => "on", false => "off", _ => "toggle" };
        Assert.Equal(expectedWord, OverlayControl.SwitchWord(word));
        Assert.Equal(expectedWord, ControlProtocol.SwitchWord(word));
        Assert.Equal(on ?? true, OverlayControl.SwitchTo(word, current: false));
        Assert.Equal(on ?? false, OverlayControl.SwitchTo(word, current: true));
        Assert.Equal(word.Length > 0, ActionSpec.IsSwitchWord(word));
    }

    [Fact]
    public void ANullMediaPathIsNotAFileOfAnyKind()
    {
        Assert.False(PlaylistSequencer.IsVideoPath(null));
        Assert.False(PlaylistSequencer.IsAudioPath(null));
        Assert.False(PlaylistSequencer.IsDeckPath(null));
        Assert.False(PlaylistSequencer.IsMediaPath(null));
        Assert.Equal(LibraryMediaKind.Image, MediaLibraryEntry.KindOf("", false));
    }
}
