using Patterns.Core.Model;
using Patterns.Core.Play;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

public class PlaylistSequencerTests
{
    private static PlaylistOptions Options(params string[] itemPaths)
    {
        var o = new PlaylistOptions();
        foreach (var p in itemPaths)
        {
            o.Items.Add(new PlaylistItemConfig { Path = p });
        }
        return o;
    }

    private static readonly DateTime Utc0 = new(2026, 8, 30, 10, 0, 0, DateTimeKind.Utc);
    private static readonly DateTime Local0 = new(2026, 8, 30, 12, 0, 0);

    [Fact]
    public void OrderKeepsCustomItemOrderThenSortedFolders()
    {
        var o = Options(@"C:\media\zeta.png", @"C:\media\alpha.mp4");
        var folder = new[] { @"C:\scan\b.png", @"C:\scan\A.png", @"C:\media\zeta.png", @"C:\scan\readme.txt" };

        var order = PlaylistSequencer.BuildOrder(o, o.Items, folder, videoPlaybackAvailable: true);

        Assert.Equal(new[]
        {
            @"C:\media\zeta.png",   // explicit items first, in list order
            @"C:\media\alpha.mp4",
            @"C:\scan\A.png",       // folder files name-sorted (case-insensitive)
            @"C:\scan\b.png",       // duplicate zeta dropped, readme.txt is not media
        }, order.Select(e => e.Path));
        Assert.True(order[1].IsVideo);
        Assert.False(order[0].IsVideo);
    }

    [Fact]
    public void OrderFiltersByKindAndVideoAvailability()
    {
        var o = Options(@"a.png", @"b.mp4");

        o.IncludeVideos = false;
        Assert.Equal(new[] { "a.png" }, PlaylistSequencer.BuildOrder(o, o.Items, Array.Empty<string>(), true).Select(e => e.Path));

        o.IncludeVideos = true;
        Assert.Equal(new[] { "a.png" }, PlaylistSequencer.BuildOrder(o, o.Items, Array.Empty<string>(), videoPlaybackAvailable: false).Select(e => e.Path));

        o.IncludeImages = false;
        Assert.Equal(new[] { "b.mp4" }, PlaylistSequencer.BuildOrder(o, o.Items, Array.Empty<string>(), true).Select(e => e.Path));
    }

    [Fact]
    public void ShuffleIsDeterministicPerSeed()
    {
        var o = Options("a.png", "b.png", "c.png", "d.png", "e.png", "f.png", "g.png", "h.png");
        o.Shuffle = true;
        o.ShuffleSeed = 7;

        var one = PlaylistSequencer.BuildOrder(o, o.Items, Array.Empty<string>(), true).Select(e => e.Path).ToList();
        var two = PlaylistSequencer.BuildOrder(o, o.Items, Array.Empty<string>(), true).Select(e => e.Path).ToList();
        Assert.Equal(one, two);

        o.ShuffleSeed = 8;
        var other = PlaylistSequencer.BuildOrder(o, o.Items, Array.Empty<string>(), true).Select(e => e.Path).ToList();
        Assert.Equal(one.OrderBy(x => x), other.OrderBy(x => x)); // same set…
        Assert.True(one.Count == 8 && other.Count == 8);
    }

    [Fact]
    public void ImagesAdvanceAfterDwell()
    {
        var o = Options("a.png", "b.png");
        o.ImageDwellSeconds = 8;
        var seq = new PlaylistSequencer();
        seq.SetOrder(PlaylistSequencer.BuildOrder(o, o.Items, Array.Empty<string>(), true), Utc0);
        Assert.Equal("a.png", seq.Current!.Path); // first item comes up on SetOrder

        Assert.False(seq.Tick(o, Local0, Utc0.AddSeconds(7.5), false, 0));
        Assert.Equal("a.png", seq.Current!.Path);

        Assert.True(seq.Tick(o, Local0, Utc0.AddSeconds(8.2), false, 0));
        Assert.Equal("b.png", seq.Current!.Path);

        Assert.True(seq.Tick(o, Local0, Utc0.AddSeconds(16.5), false, 0)); // wraps — looped
        Assert.Equal("a.png", seq.Current!.Path);
    }

    [Fact]
    public void FullLengthVideosAdvanceOnlyWhenEnded()
    {
        var o = Options("v.mp4", "a.png");
        o.VideoFullLength = true;
        var seq = new PlaylistSequencer();
        seq.SetOrder(PlaylistSequencer.BuildOrder(o, o.Items, Array.Empty<string>(), true), Utc0);
        seq.Tick(o, Local0, Utc0, false, 0);
        Assert.Equal("v.mp4", seq.Current!.Path);

        // A minute passes but the video has not ended — stay put.
        Assert.False(seq.Tick(o, Local0, Utc0.AddSeconds(60), videoEnded: false, 0));
        Assert.True(seq.Tick(o, Local0, Utc0.AddSeconds(61), videoEnded: true, 0));
        Assert.Equal("a.png", seq.Current!.Path);
    }

    [Fact]
    public void ExplicitDurationOverridesVideoLength()
    {
        var o = Options("v.mp4");
        o.Items[0].DurationSeconds = 5;
        var seq = new PlaylistSequencer();
        seq.SetOrder(PlaylistSequencer.BuildOrder(o, o.Items, Array.Empty<string>(), true), Utc0);
        seq.Tick(o, Local0, Utc0, false, 0);

        Assert.False(seq.Tick(o, Local0, Utc0.AddSeconds(4), false, 0));
        Assert.True(seq.Tick(o, Local0, Utc0.AddSeconds(5.1), false, 0)); // advanced (wraps to itself)
    }

    [Fact]
    public void ScheduledItemInterruptsOncePerDayThenResumes()
    {
        var o = Options("a.png", "b.png", "promo.png");
        o.ImageDwellSeconds = 1000; // cycle would never advance by itself in this test
        o.Items[2].ScheduledTime = "12:30";
        o.Items[2].ScheduledDurationSeconds = 10;

        var seq = new PlaylistSequencer();
        seq.SetOrder(PlaylistSequencer.BuildOrder(o, o.Items, Array.Empty<string>(), true), Utc0);
        seq.Tick(o, Local0, Utc0, false, 0);
        Assert.Equal("a.png", seq.Current!.Path);

        // 12:30 → the scheduled item takes over.
        var at = new DateTime(2026, 8, 30, 12, 30, 0);
        Assert.True(seq.Tick(o, at, Utc0.AddSeconds(30), false, 0));
        Assert.Equal("promo.png", seq.Current!.Path);
        Assert.Equal(-1, seq.CurrentIndex);

        // Same minute again — the once-per-day guard holds, override still showing.
        Assert.False(seq.Tick(o, at.AddSeconds(5), Utc0.AddSeconds(35), false, 0));
        Assert.Equal("promo.png", seq.Current!.Path);

        // After its duration the cycle resumes.
        Assert.True(seq.Tick(o, at.AddSeconds(11), Utc0.AddSeconds(41), false, 0));
        Assert.Equal("a.png", seq.Current!.Path);

        // Next day, same minute — fires again.
        Assert.True(seq.Tick(o, at.AddDays(1), Utc0.AddDays(1), false, 0));
        Assert.Equal("promo.png", seq.Current!.Path);
    }

    [Fact]
    public void RebuildKeepsTheCurrentItemPlaying()
    {
        var o = Options("a.png", "b.png", "c.png");
        o.ImageDwellSeconds = 8;
        var seq = new PlaylistSequencer();
        seq.SetOrder(PlaylistSequencer.BuildOrder(o, o.Items, Array.Empty<string>(), true), Utc0);
        seq.Tick(o, Local0, Utc0, false, 0);
        seq.Tick(o, Local0, Utc0.AddSeconds(9), false, 0);
        Assert.Equal("b.png", seq.Current!.Path);

        // A folder rescan reorders the list — the on-screen item must not jump.
        var rebuilt = PlaylistSequencer.BuildOrder(o, o.Items, new[] { @"C:\x\new.png" }, true);
        seq.SetOrder(rebuilt, Utc0.AddSeconds(10));
        Assert.Equal("b.png", seq.Current!.Path);
    }

    // ---- the hold: a playlist item paused with its clip (VIDEO PAUSE / STOP on a playlist video) ----

    [Fact]
    public void AHeldItemDoesNotAdvanceAtItsEndAndTheReleaseMovesItsStartByTheHeldSpan()
    {
        var o = Options("v.mp4", "b.png");
        o.Items[0].DurationSeconds = 30;
        var seq = new PlaylistSequencer();
        seq.SetOrder(PlaylistSequencer.BuildOrder(o, o.Items, Array.Empty<string>(), true), Utc0);
        seq.Tick(o, Local0, Utc0, false, 0);
        Assert.Equal("v.mp4", seq.Current!.Path);
        Assert.False(seq.Held);

        // Held ten seconds in: a second hold changes nothing, and its end time passes with nothing moved.
        Assert.True(seq.Hold(Utc0.AddSeconds(10)));
        Assert.True(seq.Held);
        Assert.False(seq.Hold(Utc0.AddSeconds(12)));
        Assert.False(seq.Tick(o, Local0, Utc0.AddSeconds(31), false, 0));
        Assert.False(seq.Tick(o, Local0, Utc0.AddSeconds(300), videoEnded: true, 0));   // not even an end the decoder claims
        Assert.Equal("v.mp4", seq.Current!.Path);
        Assert.Equal(Utc0, seq.ItemStartedUtc);                                          // the start stands while held…
        Assert.Equal(10, seq.ItemElapsedSeconds(Utc0.AddSeconds(300)), 3);               // …and the item's own clock reads where it was held

        // Released 290 s later: the start moved on by exactly the held span, so twenty seconds are left, as they were.
        Assert.True(seq.Release(Utc0.AddSeconds(300)));
        Assert.False(seq.Held);
        Assert.False(seq.Release(Utc0.AddSeconds(301)));
        Assert.Equal(Utc0.AddSeconds(290), seq.ItemStartedUtc);
        Assert.False(seq.Tick(o, Local0, Utc0.AddSeconds(319), false, 0));
        Assert.True(seq.Tick(o, Local0, Utc0.AddSeconds(320.1), false, 0));
        Assert.Equal("b.png", seq.Current!.Path);
    }

    [Fact]
    public void NothingIsHeldWithNoItemOnAndAClockSetUnderAHoldCountsFromThere()
    {
        var seq = new PlaylistSequencer();
        Assert.False(seq.Hold(Utc0));                       // no item on: nothing to hold
        Assert.False(seq.Release(Utc0));

        var o = Options("v.mp4", "b.png");
        o.Items[0].DurationSeconds = 30;
        seq.SetOrder(PlaylistSequencer.BuildOrder(o, o.Items, Array.Empty<string>(), true), Utc0);
        seq.Tick(o, Local0, Utc0, false, 0);

        // STOP on a playlist item: its clock restarted under the hold, at the top — the release adds only what follows.
        Assert.True(seq.Hold(Utc0.AddSeconds(10)));
        Assert.True(seq.RestartItem(Utc0.AddSeconds(40)));
        Assert.Equal(Utc0.AddSeconds(40), seq.HeldSinceUtc);
        Assert.Equal(0, seq.ItemElapsedSeconds(Utc0.AddSeconds(100)), 3);
        Assert.True(seq.Release(Utc0.AddSeconds(100)));
        Assert.Equal(Utc0.AddSeconds(100), seq.ItemStartedUtc);
        Assert.False(seq.Tick(o, Local0, Utc0.AddSeconds(129), false, 0));
        Assert.True(seq.Tick(o, Local0, Utc0.AddSeconds(130.1), false, 0));

        // A seek under a hold (VIDEO SEEK while paused): five seconds left as of the seek, and still five at the release.
        seq = new PlaylistSequencer();
        seq.SetOrder(PlaylistSequencer.BuildOrder(o, o.Items, Array.Empty<string>(), true), Utc0);
        seq.Tick(o, Local0, Utc0, false, 0);
        seq.Tick(o, Local0, Utc0.AddSeconds(1), false, 0);  // the item's length is known to the sequencer from a tick
        Assert.True(seq.Hold(Utc0.AddSeconds(10)));
        Assert.True(seq.EndItemIn(5, Utc0.AddSeconds(50)));
        Assert.True(seq.Release(Utc0.AddSeconds(200)));
        Assert.False(seq.Tick(o, Local0, Utc0.AddSeconds(204), false, 0));
        Assert.True(seq.Tick(o, Local0, Utc0.AddSeconds(205.1), false, 0));
    }

    [Fact]
    public void AnItemThatReplacesAHeldOneRuns()
    {
        var o = Options("v.mp4", "b.png");
        var seq = new PlaylistSequencer();
        seq.SetOrder(PlaylistSequencer.BuildOrder(o, o.Items, Array.Empty<string>(), true), Utc0);
        seq.Tick(o, Local0, Utc0, false, 0);
        Assert.True(seq.Hold(Utc0.AddSeconds(5)));

        // A rebuild that keeps the held item keeps the hold; one that drops it drops the hold with it.
        seq.SetOrder(PlaylistSequencer.BuildOrder(o, o.Items, new[] { "c.png" }, true), Utc0.AddSeconds(6));
        Assert.True(seq.Held);
        var other = Options("x.png", "y.png");
        seq.SetOrder(PlaylistSequencer.BuildOrder(other, other.Items, Array.Empty<string>(), true), Utc0.AddSeconds(7));
        Assert.Equal("x.png", seq.Current!.Path);
        Assert.False(seq.Held);
    }

    [Fact]
    public void AScheduledEntryDueDuringAHoldFiresAtTheReleaseInsideItsWindowAndIsMissedPastIt()
    {
        var o = Options("v.mp4", "b.png", "promo.png", "late.png");
        o.ImageDwellSeconds = 100000;
        o.Items[0].DurationSeconds = 100000;
        o.Items[2].ScheduledTime = "12:30";
        o.Items[2].ScheduledDurationSeconds = 300;          // its window: five minutes from 12:30
        o.Items[3].ScheduledTime = "12:31";
        o.Items[3].ScheduledDurationSeconds = 20;           // its window: its own minute, never less
        Assert.Equal(300, PlaylistSequencer.ScheduleWindowSeconds(o.Items[2]));
        Assert.Equal(PlaylistSequencer.ScheduleMinuteSeconds, PlaylistSequencer.ScheduleWindowSeconds(o.Items[3]));

        var seq = new PlaylistSequencer();
        seq.SetOrder(PlaylistSequencer.BuildOrder(o, o.Items, Array.Empty<string>(), true), Utc0);
        seq.Tick(o, Local0, Utc0, false, 0);
        var day = new DateTime(2026, 8, 30);
        DateTime At(int h, int m, int s) => day.AddHours(h).AddMinutes(m).AddSeconds(s);
        DateTime UtcAt(int h, int m, int s) => Utc0.Add(At(h, m, s) - Local0);

        // Held from 12:29: both minutes pass under the hold and neither entry takes the picture.
        Assert.True(seq.Hold(UtcAt(12, 29, 0)));
        Assert.False(seq.Tick(o, At(12, 30, 10), UtcAt(12, 30, 10), false, 0));
        Assert.False(seq.Tick(o, At(12, 31, 10), UtcAt(12, 31, 10), false, 0));
        Assert.Equal("v.mp4", seq.Current!.Path);
        Assert.Empty(seq.TakeMissed());                     // nothing is missed while the hold may still end in time

        // Released at 12:33: the promo is inside its five minutes and fires, whole; the 12:31 entry is past its minute — missed, not played late.
        Assert.True(seq.Release(UtcAt(12, 33, 0)));
        Assert.True(seq.Tick(o, At(12, 33, 0), UtcAt(12, 33, 0), false, 0));
        Assert.Equal("promo.png", seq.Current!.Path);
        Assert.Equal(300, seq.ItemDurationSeconds, 3);
        Assert.False(seq.Tick(o, At(12, 33, 1), UtcAt(12, 33, 1), false, 0));
        var missed = Assert.Single(seq.TakeMissed());
        Assert.Equal("late.png", missed.Path);
        Assert.Equal("12:31", missed.ScheduledTime);
        Assert.Equal(At(12, 31, 0), missed.DueLocal);
        Assert.Equal(At(12, 33, 1), missed.ReleasedLocal);
        Assert.Empty(seq.TakeMissed());                     // said once
        Assert.Equal("promo.png", seq.Current!.Path);       // and it never took the picture

        // Neither fires again that day; both fire the next day at their minutes.
        Assert.True(seq.Tick(o, At(12, 38, 5), UtcAt(12, 38, 5), false, 0));
        Assert.Equal("v.mp4", seq.Current!.Path);
        Assert.False(seq.Tick(o, At(12, 38, 6), UtcAt(12, 38, 6), false, 0));
        Assert.True(seq.Tick(o, At(12, 30, 0).AddDays(1), UtcAt(12, 30, 0).AddDays(1), false, 0));
        Assert.Equal("promo.png", seq.Current!.Path);
        Assert.True(seq.Tick(o, At(12, 31, 0).AddDays(1), UtcAt(12, 31, 0).AddDays(1), false, 0));
        Assert.Equal("late.png", seq.Current!.Path);
    }

    [Fact]
    public void AHoldReleasedInsideTheEntrysOwnMinuteFiresItAsEver()
    {
        var o = Options("v.mp4", "promo.png");
        o.Items[0].DurationSeconds = 100000;
        o.Items[1].ScheduledTime = "12:30";
        o.Items[1].ScheduledDurationSeconds = 10;
        var seq = new PlaylistSequencer();
        seq.SetOrder(PlaylistSequencer.BuildOrder(o, o.Items, Array.Empty<string>(), true), Utc0);
        seq.Tick(o, Local0, Utc0, false, 0);
        var at = new DateTime(2026, 8, 30, 12, 30, 0);

        Assert.True(seq.Hold(Utc0.AddMinutes(29)));
        Assert.False(seq.Tick(o, at.AddSeconds(5), Utc0.AddMinutes(30).AddSeconds(5), false, 0));
        Assert.True(seq.Release(Utc0.AddMinutes(30).AddSeconds(20)));
        Assert.True(seq.Tick(o, at.AddSeconds(20), Utc0.AddMinutes(30).AddSeconds(20), false, 0));
        Assert.Equal("promo.png", seq.Current!.Path);
        Assert.Empty(seq.TakeMissed());
    }

    [Fact]
    public void AHeldScheduledInterruptionKeepsWhatWasLeftOfIt()
    {
        var o = Options("a.png", "promo.mp4");
        o.ImageDwellSeconds = 100000;
        o.Items[1].ScheduledTime = "12:30";
        o.Items[1].ScheduledDurationSeconds = 60;
        var seq = new PlaylistSequencer();
        seq.SetOrder(PlaylistSequencer.BuildOrder(o, o.Items, Array.Empty<string>(), true), Utc0);
        seq.Tick(o, Local0, Utc0, false, 0);
        var at = new DateTime(2026, 8, 30, 12, 30, 0);
        var fired = Utc0.AddMinutes(30);
        Assert.True(seq.Tick(o, at, fired, false, 0));
        Assert.Equal("promo.mp4", seq.Current!.Path);

        // Held twenty seconds in for ten minutes: its end passes with the interruption still on; forty seconds are left at the release.
        Assert.True(seq.Hold(fired.AddSeconds(20)));
        Assert.False(seq.Tick(o, at.AddMinutes(5), fired.AddMinutes(5), false, 0));
        Assert.Equal("promo.mp4", seq.Current!.Path);
        Assert.True(seq.Release(fired.AddSeconds(620)));
        Assert.Equal(60, seq.ItemDurationSeconds, 3);
        Assert.False(seq.Tick(o, at.AddSeconds(659), fired.AddSeconds(659), false, 0));
        Assert.True(seq.Tick(o, at.AddSeconds(661), fired.AddSeconds(661), false, 0));
        Assert.Equal("a.png", seq.Current!.Path);
    }

    [Fact]
    public void ASectionWhoseStartComesDuringAHoldTakesOverAtTheRelease()
    {
        var o = new PlaylistOptions();
        o.Sections.Add(new PlaylistSectionConfig { Name = "Walk-in" });
        o.Sections.Add(new PlaylistSectionConfig { Name = "Break", StartTime = "12:15" });
        o.Sections[0].Items.Add(new PlaylistItemConfig { Path = "v.mp4" });
        var seq = new PlaylistSequencer();
        seq.SetOrder(PlaylistSequencer.BuildOrder(o, o.Sections[0].Items, Array.Empty<string>(), true), Utc0);
        seq.Tick(o, Local0, Utc0, false, 0);

        Assert.True(seq.Hold(Utc0.AddMinutes(14)));
        Assert.Null(seq.SectionDue(o, new DateTime(2026, 8, 30, 12, 15, 10)));   // its minute, under the hold: the held picture stays
        Assert.Null(seq.SectionDue(o, new DateTime(2026, 8, 30, 12, 20, 0)));
        Assert.True(seq.Release(Utc0.AddMinutes(25)));
        Assert.Equal(1, seq.SectionDue(o, new DateTime(2026, 8, 30, 12, 25, 0))); // a part is a state from its start on: late, not never
        Assert.Null(seq.SectionDue(o, new DateTime(2026, 8, 30, 12, 25, 1)));    // once
    }

    [Fact]
    public void WhatWaitedUnderAHoldIsForgottenWhenItsPlaylistLeavesTheAir()
    {
        var o = new PlaylistOptions();
        o.Sections.Add(new PlaylistSectionConfig { Name = "Walk-in" });
        o.Sections.Add(new PlaylistSectionConfig { Name = "Break", StartTime = "12:15" });
        o.Sections[0].Items.Add(new PlaylistItemConfig { Path = "v.mp4", DurationSeconds = 100000 });
        o.Sections[0].Items.Add(new PlaylistItemConfig { Path = "promo.png", ScheduledTime = "12:16", ScheduledDurationSeconds = 300 });
        o.Sections[0].Items.Add(new PlaylistItemConfig { Path = "late.png", ScheduledTime = "12:17", ScheduledDurationSeconds = 20 });
        var seq = new PlaylistSequencer();
        seq.SetOrder(PlaylistSequencer.BuildOrder(o, o.Sections[0].Items.Take(1).ToList(), Array.Empty<string>(), true), Utc0);
        seq.Tick(o, Local0, Utc0, false, 0);
        var day = new DateTime(2026, 8, 30);
        DateTime At(int h, int m, int s) => day.AddHours(h).AddMinutes(m).AddSeconds(s);
        DateTime UtcAt(int h, int m, int s) => Utc0.Add(At(h, m, s) - Local0);

        // Held from 12:14: a section's start and two entries' minutes come under the hold.
        Assert.True(seq.Hold(UtcAt(12, 14, 0)));
        Assert.Null(seq.SectionDue(o, At(12, 15, 10)));
        Assert.False(seq.Tick(o, At(12, 16, 10), UtcAt(12, 16, 10), false, 0));
        Assert.False(seq.Tick(o, At(12, 17, 10), UtcAt(12, 17, 10), false, 0));

        // The playlist leaves the air at 12:18 and its hold ends there: what waited was its own.
        Assert.True(seq.Release(UtcAt(12, 18, 0)));
        seq.ForgetWaiting();

        // The next playlist on air, a minute or a day later: no stale part takes over, the entry still inside its
        // window does not fire late, and the one past it is not reported with a release time that was never its own.
        foreach (var later in new[] { At(12, 19, 0), At(12, 19, 0).AddDays(1) })
        {
            Assert.Null(seq.SectionDue(o, later));
            seq.Tick(o, later, Utc0.Add(later - Local0), false, 0);
            Assert.Equal("v.mp4", seq.Current!.Path);   // the cycle's own item, never an entry fired late
            Assert.Equal(0, seq.CurrentIndex);
            Assert.Empty(seq.TakeMissed());
        }

        // Nothing is spent for good: the next day each still comes at its own minute.
        Assert.Equal(1, seq.SectionDue(o, At(12, 15, 0).AddDays(2)));
        Assert.True(seq.Tick(o, At(12, 16, 0).AddDays(2), UtcAt(12, 16, 0).AddDays(2), false, 0));
        Assert.Equal("promo.png", seq.Current!.Path);
    }

    [Fact]
    public void ASectionForgottenInsideItsOwnMinuteMayStillTakeOverInIt()
    {
        var o = new PlaylistOptions();
        o.Sections.Add(new PlaylistSectionConfig { Name = "Walk-in" });
        o.Sections.Add(new PlaylistSectionConfig { Name = "Break", StartTime = "12:15" });
        o.Sections[0].Items.Add(new PlaylistItemConfig { Path = "v.mp4" });
        var seq = new PlaylistSequencer();
        seq.SetOrder(PlaylistSequencer.BuildOrder(o, o.Sections[0].Items, Array.Empty<string>(), true), Utc0);
        seq.Tick(o, Local0, Utc0, false, 0);

        // Off the air and back on inside the section's minute: the same as a playlist that came on air in that minute.
        Assert.True(seq.Hold(Utc0.AddMinutes(14)));
        Assert.Null(seq.SectionDue(o, new DateTime(2026, 8, 30, 12, 15, 10)));
        Assert.True(seq.Release(Utc0.AddMinutes(15).AddSeconds(20)));
        seq.ForgetWaiting();
        Assert.Equal(1, seq.SectionDue(o, new DateTime(2026, 8, 30, 12, 15, 40)));
        Assert.Null(seq.SectionDue(o, new DateTime(2026, 8, 30, 12, 15, 50)));
    }

    [Theory]
    [InlineData("show.mp4", true)]
    [InlineData("SHOW.MOV", true)]
    [InlineData("still.png", false)]
    [InlineData("still.jpeg", false)]
    public void ClassifiesVideoPaths(string path, bool video)
        => Assert.Equal(video, PlaylistSequencer.IsVideoPath(path));
}

public class FeedParserTests
{
    private static readonly DateTime Noon = new(2026, 8, 30, 12, 0, 0);

    [Fact]
    public void ParsesRssTitles()
    {
        const string rss = """
            <?xml version="1.0"?>
            <rss version="2.0"><channel><title>Site</title>
            <item><title>First headline</title><link>x</link></item>
            <item><title>Second headline</title></item>
            <item><title>Third headline</title></item>
            </channel></rss>
            """;
        var items = FeedParser.Parse(rss, FeedKind.Rss, "", Noon, 10);
        Assert.Equal(new[] { "First headline", "Second headline", "Third headline" }, items);

        Assert.Equal(2, FeedParser.Parse(rss, FeedKind.Rss, "", Noon, 2).Count);
    }

    [Fact]
    public void ParsesAtomEntries()
    {
        const string atom = """
            <?xml version="1.0"?>
            <feed xmlns="http://www.w3.org/2005/Atom">
            <title>Feed</title>
            <entry><title>Entry one</title></entry>
            <entry><title>Entry two</title></entry>
            </feed>
            """;
        Assert.Equal(new[] { "Entry one", "Entry two" }, FeedParser.Parse(atom, FeedKind.Rss, "", Noon, 10));
    }

    [Fact]
    public void ParsesCsvLines()
    {
        const string csv = "# schedule\n09:00,Doors\n\n10:30,Keynote — Main Hall\n";
        var items = FeedParser.Parse(csv, FeedKind.Csv, "", Noon, 10);
        Assert.Equal(new[] { "09:00  ·  Doors", "10:30  ·  Keynote — Main Hall" }, items);
    }

    [Fact]
    public void ParsesUpcomingIcsEvents()
    {
        const string ics = "BEGIN:VCALENDAR\r\n" +
                           "BEGIN:VEVENT\r\nDTSTART:20260830T190000\r\nSUMMARY:Doors open\r\nEND:VEVENT\r\n" +
                           "BEGIN:VEVENT\r\nDTSTART:20260830T140000\r\nSUMMARY:Sound\r\n  check\r\nEND:VEVENT\r\n" + // folded line (RFC 5545)
                           "BEGIN:VEVENT\r\nDTSTART:20260905T190000\r\nSUMMARY:Next week\r\nEND:VEVENT\r\n" +
                           "BEGIN:VEVENT\r\nDTSTART:20260830T080000\r\nSUMMARY:Already done\r\nEND:VEVENT\r\n" +
                           "END:VCALENDAR\r\n";
        var items = FeedParser.Parse(ics, FeedKind.Ics, "", Noon, 10);
        // Soonest first; only the next 24 h; the folded SUMMARY is unfolded.
        Assert.Equal(new[] { "14:00  Sound check", "19:00  Doors open" }, items);
    }

    [Theory]
    [InlineData("20260830T183000", true, 18, 30)]
    [InlineData("20260830T1830", true, 18, 30)]
    [InlineData("20260830", true, 0, 0)]
    [InlineData("not-a-date", false, 0, 0)]
    public void ParsesIcsDates(string value, bool ok, int hour, int minute)
    {
        Assert.Equal(ok, FeedParser.TryParseIcsDate(value, out var dt));
        if (ok)
        {
            Assert.Equal(new DateTime(2026, 8, 30, hour, minute, 0), dt);
        }
    }

    [Fact]
    public void UtcIcsDatesParseWithoutThrowing()
        => Assert.True(FeedParser.TryParseIcsDate("20260830T183000Z", out _));

    [Theory]
    [InlineData("whatever.ics", "", FeedKind.Ics)]
    [InlineData("lines.txt", "", FeedKind.Csv)]
    [InlineData("feed.xml", "", FeedKind.Rss)]
    [InlineData("", "<rss version=\"2.0\"/>", FeedKind.Rss)]
    [InlineData("", "BEGIN:VCALENDAR", FeedKind.Ics)]
    [InlineData("", "hello world", FeedKind.Csv)]
    public void DetectsKind(string hint, string content, FeedKind expected)
        => Assert.Equal(expected, FeedParser.Detect(content, hint));

    [Fact]
    public void MalformedXmlYieldsEmptyNotThrow()
        => Assert.Empty(FeedParser.Parse("<rss><broken", FeedKind.Rss, "", Noon, 10));

    [Fact]
    public void JoinUsesSeparatorWithDefault()
    {
        Assert.Equal("a — b", FeedParser.Join(new[] { "a", "b" }, " — "));
        Assert.Equal("a   •   b", FeedParser.Join(new[] { "a", "b" }, ""));
    }
}

/// <summary>Round 83 (L39): the ticker meets the word list, the feed has a size, and a feed that will not parse is one line on the status and in the log, never the document.</summary>
public class FeedModerationTests
{
    [Fact]
    public void TheTickerMeetsTheWordListAndAFeedThatWillNotParseIsOneLineNotADocument()
    {
        var (kept, held) = FeedParser.Moderate(new[] { "Doors open 7pm", "What a load of bullshit", "Scunthorpe win 2-1" }, WordList.Default);
        Assert.Equal(new[] { "Doors open 7pm", "Scunthorpe win 2-1" }, kept);
        Assert.Equal(1, held);
        Assert.Equal(2L * 1024 * 1024, FeedParser.MaxBytes);

        var torn = "<rss><channel><item><title>" + new string('x', 20000) + "</item>";      // unclosed tags: a parser's message names the document
        var items = FeedParser.Parse(torn, FeedKind.Rss, "feed.xml", new DateTime(2026, 9, 24, 12, 0, 0), 10, out var problem);
        Assert.Empty(items);
        Assert.StartsWith("XmlException", problem, StringComparison.Ordinal);
        Assert.True(problem.Length <= 200, problem);
        Assert.DoesNotContain("\n", problem);

        var fine = FeedParser.Parse("one\ntwo", FeedKind.Csv, "notes.txt", new DateTime(2026, 9, 24, 12, 0, 0), 10, out var none);
        Assert.Equal(2, fine.Count);
        Assert.Equal("", none);
    }
}
