using Patterns.Core.Model;
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

        var order = PlaylistSequencer.BuildOrder(o, folder, videoPlaybackAvailable: true);

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
        Assert.Equal(new[] { "a.png" }, PlaylistSequencer.BuildOrder(o, Array.Empty<string>(), true).Select(e => e.Path));

        o.IncludeVideos = true;
        Assert.Equal(new[] { "a.png" }, PlaylistSequencer.BuildOrder(o, Array.Empty<string>(), videoPlaybackAvailable: false).Select(e => e.Path));

        o.IncludeImages = false;
        Assert.Equal(new[] { "b.mp4" }, PlaylistSequencer.BuildOrder(o, Array.Empty<string>(), true).Select(e => e.Path));
    }

    [Fact]
    public void ShuffleIsDeterministicPerSeed()
    {
        var o = Options("a.png", "b.png", "c.png", "d.png", "e.png", "f.png", "g.png", "h.png");
        o.Shuffle = true;
        o.ShuffleSeed = 7;

        var one = PlaylistSequencer.BuildOrder(o, Array.Empty<string>(), true).Select(e => e.Path).ToList();
        var two = PlaylistSequencer.BuildOrder(o, Array.Empty<string>(), true).Select(e => e.Path).ToList();
        Assert.Equal(one, two);

        o.ShuffleSeed = 8;
        var other = PlaylistSequencer.BuildOrder(o, Array.Empty<string>(), true).Select(e => e.Path).ToList();
        Assert.Equal(one.OrderBy(x => x), other.OrderBy(x => x)); // same set…
        Assert.True(one.Count == 8 && other.Count == 8);
    }

    [Fact]
    public void ImagesAdvanceAfterDwell()
    {
        var o = Options("a.png", "b.png");
        o.ImageDwellSeconds = 8;
        var seq = new PlaylistSequencer();
        seq.SetOrder(PlaylistSequencer.BuildOrder(o, Array.Empty<string>(), true), Utc0);
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
        seq.SetOrder(PlaylistSequencer.BuildOrder(o, Array.Empty<string>(), true), Utc0);
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
        seq.SetOrder(PlaylistSequencer.BuildOrder(o, Array.Empty<string>(), true), Utc0);
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
        seq.SetOrder(PlaylistSequencer.BuildOrder(o, Array.Empty<string>(), true), Utc0);
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
        seq.SetOrder(PlaylistSequencer.BuildOrder(o, Array.Empty<string>(), true), Utc0);
        seq.Tick(o, Local0, Utc0, false, 0);
        seq.Tick(o, Local0, Utc0.AddSeconds(9), false, 0);
        Assert.Equal("b.png", seq.Current!.Path);

        // A folder rescan reorders the list — the on-screen item must not jump.
        var rebuilt = PlaylistSequencer.BuildOrder(o, new[] { @"C:\x\new.png" }, true);
        seq.SetOrder(rebuilt, Utc0.AddSeconds(10));
        Assert.Equal("b.png", seq.Current!.Path);
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
