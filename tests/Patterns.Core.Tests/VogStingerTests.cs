using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The VOG / stinger split at the model, the migration and the resolver: an older show's items all
/// become VOGs, the music rule is one pure ramp, and a stinger's "after" reads the same everywhere.
/// </summary>
public class VogStingerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "patterns-vog-" + Guid.NewGuid().ToString("N"));

    public VogStingerTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, true); } catch { /* best effort */ }
    }

    // ---- 1. schema 6 and the migration -------------------------------------

    [Fact]
    public void AnOlderShowFilesStingersAllBecomeVogs()
    {
        var store = new SettingsStore(_dir);
        // A file as an older build wrote it: schema 5, two library items, no kind fields at all.
        File.WriteAllText(store.SettingsPath, """
        {
          "SchemaVersion": 5,
          "Stingers": {
            "Items": [
              { "Id": "a", "Name": "Take your seats", "Path": "C:/show/seats.wav", "VolumePct": 100 },
              { "Id": "b", "Name": "Winner", "Path": "C:/show/winner.mp4", "VolumePct": 100 }
            ],
            "DuckPct": 20
          }
        }
        """);

        var loaded = store.Load();

        Assert.True(store.LastLoadMigrated);
        Assert.Equal(ShowState.CurrentSchemaVersion, loaded.SchemaVersion); // 6 then; every later step upgrades it further
        Assert.Equal(2, loaded.Stingers.Items.Count);
        foreach (var item in loaded.Stingers.Items)
        {
            Assert.Equal(StingerKind.Vog, item.Kind);
            Assert.Equal(StingerAfter.Return, item.After);
            Assert.Equal("", item.AfterTarget);
            Assert.True(item.MusicReturns);
        }

        // The upgrade is a one-off: re-running it never re-kinds an item the operator has changed.
        loaded.Stingers.Items[1].Kind = StingerKind.Sting;
        loaded.Stingers.Items[1].After = StingerAfter.Manual;
        SettingsStore.Migrate(loaded);
        SettingsStore.Migrate(loaded);
        Assert.Equal(StingerKind.Sting, loaded.Stingers.Items[1].Kind);
        Assert.Equal(StingerAfter.Manual, loaded.Stingers.Items[1].After);
        Assert.Equal(ShowState.CurrentSchemaVersion, loaded.SchemaVersion);
    }

    [Fact]
    public void AnUnknownKindFromANewerBuildLandsOnVog()
    {
        var item = JsonUtil.Deserialize<StingerItemConfig>(
            """{ "Id": "x", "Path": "C:/x.wav", "Kind": "Fanfare", "After": "Explode" }""");

        Assert.NotNull(item);
        Assert.Equal(StingerKind.Vog, item!.Kind);
        Assert.Equal(StingerAfter.Return, item.After);
    }

    [Fact]
    public void AStingersAfterPolicySurvivesSaveAndLoad()
    {
        var store = new SettingsStore(_dir);
        var state = SettingsStore.Fresh();
        var look = new LookConfig { Name = "Walk-in", Json = LookService.Capture(state) };
        state.LooksAndCues.Looks.Add(look);
        state.Stingers.Items.Add(new StingerItemConfig
        {
            Id = "s1",
            Name = "Whoosh",
            Path = "C:/show/whoosh.mp4",
            Kind = StingerKind.Sting,
            After = StingerAfter.Custom,
            AfterTarget = look.Id,
            MusicReturns = false,
        });
        state.Stingers.FadeMs = 250;
        state.Stingers.HoldSeconds = 45;

        store.Save(state);
        var loaded = store.Load();

        var item = Assert.Single(loaded.Stingers.Items);
        Assert.Equal(StingerKind.Sting, item.Kind);
        Assert.Equal(StingerAfter.Custom, item.After);
        Assert.Equal(look.Id, item.AfterTarget);
        Assert.False(item.MusicReturns);
        Assert.True(item.IsSting);
        Assert.Equal("STING", item.KindLabel);
        Assert.Equal(250, loaded.Stingers.FadeMs);
        Assert.Equal(45, loaded.Stingers.HoldSeconds);
    }

    // ---- 2. the music rule -------------------------------------------------

    [Fact]
    public void AFadeIsTimeBasedAndNeverOvershoots()
    {
        var t0 = new DateTime(2026, 9, 3, 19, 30, 0, DateTimeKind.Utc);

        Assert.Equal(0, MusicLevel.Progress(t0, t0, 400));
        Assert.Equal(0.5, MusicLevel.Progress(t0, t0.AddMilliseconds(200), 400), 6);
        Assert.Equal(1, MusicLevel.Progress(t0, t0.AddSeconds(10), 400));
        Assert.Equal(0, MusicLevel.Progress(t0, t0.AddSeconds(-1), 400));   // a clock that jumps back clamps
        Assert.Equal(1, MusicLevel.Progress(t0, t0, 0));                    // a zero-length fade is instant

        Assert.Equal(0.5, MusicLevel.Gain(1, 0, 0.5), 6);
        Assert.Equal(0.4, MusicLevel.Gain(0.4, 1, 0), 6);                   // a reversal starts where it is
        Assert.Equal(1, MusicLevel.Gain(0.4, 1, 1), 6);
        Assert.Equal(1, MusicLevel.Gain(0.4, 1, 5), 6);                     // progress clamps too
    }

    [Fact]
    public void AVogsDuckIsAStepToTheShowLevel()
    {
        Assert.Equal(0.2, MusicLevel.Duck(20), 6);
        Assert.Equal(0, MusicLevel.Duck(0));
        Assert.Equal(1, MusicLevel.Duck(100));
        Assert.Equal(0, MusicLevel.Duck(-5));
        Assert.Equal(1, MusicLevel.Duck(400));
    }

    // ---- 3. one resolver, one rule set -------------------------------------

    [Fact]
    public void OneResolverCountsBothKindsInLibraryOrder()
    {
        var state = new ShowState();
        var vog = new StingerItemConfig { Id = "a", Name = "Take your seats", Path = "C:/show/seats.wav" };
        var sting = new StingerItemConfig { Id = "b", Name = "Whoosh", Path = "C:/show/whoosh.mp4", Kind = StingerKind.Sting };
        state.Stingers.Items.Add(vog);
        state.Stingers.Items.Add(sting);

        Assert.Same(vog, StingerLibrary.Find(state, "1"));
        Assert.Same(sting, StingerLibrary.Find(state, "2"));     // the number is the library, whatever the kind
        Assert.Same(sting, StingerLibrary.Find(state, "b"));
        Assert.Same(sting, StingerLibrary.Find(state, "WHOOSH"));
        Assert.Same(vog, Assert.Single(StingerLibrary.OfKind(state, StingerKind.Vog)));
        Assert.Same(sting, Assert.Single(StingerLibrary.OfKind(state, StingerKind.Sting)));

        Assert.True(StingerLibrary.KindMatches(vog, "", out _));
        Assert.True(StingerLibrary.KindMatches(vog, null, out _));
        Assert.True(StingerLibrary.KindMatches(vog, "vog", out _));
        Assert.False(StingerLibrary.KindMatches(vog, "sting", out var wantedSting));
        Assert.Equal("stinger", wantedSting);
        Assert.False(StingerLibrary.KindMatches(sting, "VOG", out var wantedVog));
        Assert.Equal("VOG", wantedVog);
        Assert.True(StingerLibrary.KindMatches(vog, "whatever", out _));   // a newer vocabulary never blocks a press

        Assert.Equal("VOG", StingerLibrary.KindWord(StingerKind.Vog));
        Assert.Equal("stinger", StingerLibrary.KindWord(StingerKind.Sting));
    }

    [Fact]
    public void TheAfterTargetResolvesALookThenACue()
    {
        var state = new ShowState();
        var look = new LookConfig { Name = "Walk-in", Json = LookService.Capture(state) };
        state.LooksAndCues.Looks.Add(look);
        var stack = CueStacks.Caller(state);
        var cue = new RunCueConfig { Number = "01.010", Name = "Walk-in" };
        stack.Cues.Add(cue);

        var byId = StingerLibrary.ResolveAfter(state, look.Id);
        Assert.Equal(AfterTargetKind.Look, byId.Kind);
        Assert.Equal(look.Id, byId.Id);
        Assert.Equal("Walk-in", byId.Label);

        var byName = StingerLibrary.ResolveAfter(state, "WALK-IN");
        Assert.Equal(AfterTargetKind.Look, byName.Kind);
        Assert.Equal(look.Id, byName.Id);

        var byCue = StingerLibrary.ResolveAfter(state, cue.Id);
        Assert.Equal(AfterTargetKind.Cue, byCue.Kind);
        Assert.Equal(cue.Id, byCue.Id);
        Assert.Equal("01.010 Walk-in", byCue.Label);

        var blank = StingerLibrary.ResolveAfter(state, "  ");
        Assert.Equal(AfterTargetKind.None, blank.Kind);
        Assert.Equal("", blank.Label);

        var miss = StingerLibrary.ResolveAfter(state, " nowhere ");
        Assert.Equal(AfterTargetKind.None, miss.Kind);
        Assert.Equal("nowhere", miss.Label);   // echoed so a message can name it
    }

    [Fact]
    public void TheAfterPolicyReadsBackInOnePhrase()
    {
        var state = new ShowState();
        var look = new LookConfig { Name = "Walk-in", Json = LookService.Capture(state) };
        state.LooksAndCues.Looks.Add(look);
        var caller = CueStacks.Caller(state);
        var cue = new RunCueConfig { Number = "01.010", Name = "Walk-in" };
        caller.Cues.Add(cue);
        var clicker = CueStacks.Clicker(state);

        var item = new StingerItemConfig { Name = "Whoosh", Path = "C:/show/whoosh.mp4" };
        state.Stingers.Items.Add(item);

        Assert.Equal("", StingerLibrary.AfterSummary(state, item));   // a VOG has no after

        item.Kind = StingerKind.Sting;
        Assert.Equal("content back", StingerLibrary.AfterSummary(state, item));

        item.After = StingerAfter.Manual;
        Assert.Equal("hold for a take", StingerLibrary.AfterSummary(state, item));

        item.After = StingerAfter.Next;
        Assert.Equal("the next cue on the caller's list", StingerLibrary.AfterSummary(state, item));
        item.AfterTarget = clicker.Id;
        Assert.Equal($"the next cue on '{clicker.Name}'", StingerLibrary.AfterSummary(state, item));
        item.AfterTarget = "gone";
        Assert.Equal("a cue list that is not there", StingerLibrary.AfterSummary(state, item));

        item.After = StingerAfter.Custom;
        item.AfterTarget = look.Id;
        Assert.Equal("look 'Walk-in'", StingerLibrary.AfterSummary(state, item));
        item.AfterTarget = cue.Id;
        Assert.Equal("cue 01.010", StingerLibrary.AfterSummary(state, item));
        item.AfterTarget = "";
        Assert.Equal("nothing chosen", StingerLibrary.AfterSummary(state, item));
        item.AfterTarget = "gone";
        Assert.Equal("a look or cue that is not there", StingerLibrary.AfterSummary(state, item));
    }

    [Fact]
    public void AnAfterPolicyThatPointsNowhereIsCaughtAndAVogsIsNever()
    {
        var state = new ShowState();
        var look = new LookConfig { Name = "Walk-in", Json = LookService.Capture(state) };
        state.LooksAndCues.Looks.Add(look);
        var caller = CueStacks.Caller(state);
        var clicker = CueStacks.Clicker(state);

        var item = new StingerItemConfig { Name = "Whoosh", Path = "C:/show/whoosh.mp4", Kind = StingerKind.Sting };
        state.Stingers.Items.Add(item);

        Assert.Null(StingerLibrary.AfterProblem(state, item));                       // Return
        item.After = StingerAfter.Manual;
        Assert.Null(StingerLibrary.AfterProblem(state, item));
        item.After = StingerAfter.Next;
        Assert.Null(StingerLibrary.AfterProblem(state, item));                       // blank = the caller's stack

        item.AfterTarget = "no-such-list";
        Assert.Contains("cue list that is not there", StingerLibrary.AfterProblem(state, item));
        Assert.Contains("no-such-list", StingerLibrary.AfterProblem(state, item));

        item.After = StingerAfter.Custom;
        Assert.Contains("look or cue that is not there", StingerLibrary.AfterProblem(state, item));
        item.AfterTarget = "";
        Assert.Contains("none is chosen", StingerLibrary.AfterProblem(state, item));
        item.AfterTarget = look.Id;
        Assert.Null(StingerLibrary.AfterProblem(state, item));

        // A VOG never reads the field, so a stale target on one must never break a cue.
        item.Kind = StingerKind.Vog;
        item.AfterTarget = "long gone";
        item.After = StingerAfter.Custom;
        Assert.Null(StingerLibrary.AfterProblem(state, item));
        Assert.Null(StingerLibrary.AfterNote(state, item));

        // Note 1: a sound has nothing on the screens to hold.
        var sound = new StingerItemConfig { Name = "Seats", Path = "C:/show/seats.wav", Kind = StingerKind.Sting, After = StingerAfter.Manual };
        state.Stingers.Items.Add(sound);
        Assert.Contains("nothing on the screens to hold", StingerLibrary.AfterNote(state, sound));

        // Note 2: Next with a blank target and an empty caller list.
        item.Kind = StingerKind.Sting;
        item.After = StingerAfter.Next;
        item.AfterTarget = "";
        Assert.Contains("the caller's list is empty", StingerLibrary.AfterNote(state, item));
        caller.Cues.Add(new RunCueConfig { Number = "01.010", Name = "Walk-in" });
        Assert.Null(StingerLibrary.AfterNote(state, item));

        // Note 3: the one-hop cycle — this sting advances a list whose cues fire stings that advance a list.
        item.AfterTarget = clicker.Id;
        Assert.Null(StingerLibrary.AfterNote(state, item));
        var loop = new StingerItemConfig { Id = "loop", Name = "Loop", Path = "C:/show/loop.mp4", Kind = StingerKind.Sting, After = StingerAfter.Next };
        state.Stingers.Items.Add(loop);
        clicker.Cues.Add(new RunCueConfig { Number = "02.010", Name = "Hit", Actions = { new CueActionConfig { Kind = CueActionKind.StingerFire, Target = "loop" } } });
        Assert.Contains("could run on by itself", StingerLibrary.AfterNote(state, item));
    }
}
