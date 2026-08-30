using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

public class PlaylistSectionTests
{
    private static PlaylistOptions Legacy(params string[] paths)
    {
        var o = new PlaylistOptions();
        foreach (var p in paths) o.Items.Add(new PlaylistItemConfig { Path = p });
        o.Folders.Add(@"C:\media\walkin");
        return o;
    }

    [Fact]
    public void NormalizeLiftsLegacyListsIntoAFirstSection()
    {
        var o = Legacy(@"C:\a.png", @"C:\b.mp4");
        PlaylistSequencer.Normalize(o);

        var section = Assert.Single(o.Sections);
        Assert.Equal("Main", section.Name);
        Assert.Equal(2, section.Items.Count);
        Assert.Single(section.Folders);
        Assert.Empty(o.Items);
        Assert.Empty(o.Folders);

        // Idempotent — a second pass changes nothing.
        PlaylistSequencer.Normalize(o);
        Assert.Single(o.Sections);
    }

    [Fact]
    public void ActiveSectionClampsAndResolves()
    {
        var o = new PlaylistOptions();
        o.Sections.Add(new PlaylistSectionConfig { Name = "Walk-in" });
        o.Sections.Add(new PlaylistSectionConfig { Name = "Break" });
        o.ActiveSection = 99;
        Assert.Equal("Break", PlaylistSequencer.ActiveSectionOf(o).Name);
        o.ActiveSection = 0;
        Assert.Equal("Walk-in", PlaylistSequencer.ActiveSectionOf(o).Name);
    }

    [Fact]
    public void OrderComesFromTheActiveSectionOnly()
    {
        var o = new PlaylistOptions();
        var a = new PlaylistSectionConfig { Name = "A" };
        a.Items.Add(new PlaylistItemConfig { Path = @"C:\a1.png" });
        var b = new PlaylistSectionConfig { Name = "B" };
        b.Items.Add(new PlaylistItemConfig { Path = @"C:\b1.png" });
        b.Items.Add(new PlaylistItemConfig { Path = @"C:\b2.png" });
        o.Sections.Add(a);
        o.Sections.Add(b);

        o.ActiveSection = 1;
        var order = PlaylistSequencer.BuildOrder(o, PlaylistSequencer.ActiveSectionOf(o).Items,
            Array.Empty<string>(), videoPlaybackAvailable: true);
        Assert.Equal(new[] { @"C:\b1.png", @"C:\b2.png" }, order.Select(e => e.Path));
    }

    [Fact]
    public void ScheduledInterruptionsFireFromAnySection()
    {
        var o = new PlaylistOptions();
        var idle = new PlaylistSectionConfig { Name = "Idle" };
        idle.Items.Add(new PlaylistItemConfig { Path = @"C:\loop.png" });
        var other = new PlaylistSectionConfig { Name = "Other" };
        other.Items.Add(new PlaylistItemConfig { Path = @"C:\promo.mp4", ScheduledTime = "18:30", ScheduledDurationSeconds = 30 });
        o.Sections.Add(idle);
        o.Sections.Add(other);
        o.ActiveSection = 0;

        var seq = new PlaylistSequencer();
        seq.SetOrder(PlaylistSequencer.BuildOrder(o, idle.Items, Array.Empty<string>(), true), DateTime.UtcNow);
        var localNow = new DateTime(2026, 8, 30, 18, 30, 5);
        seq.Tick(o, localNow, DateTime.UtcNow, videoEnded: false, videoLengthSeconds: 0);
        Assert.Equal(@"C:\promo.mp4", seq.Current?.Path); // the other part's schedule interrupted
    }

    [Fact]
    public void SectionsTakeOverAtTheirDailyStartTimeOnce()
    {
        var o = new PlaylistOptions();
        o.Sections.Add(new PlaylistSectionConfig { Name = "Walk-in" });
        o.Sections.Add(new PlaylistSectionConfig { Name = "Break", StartTime = "12:15" });
        var seq = new PlaylistSequencer();

        Assert.Null(seq.SectionDue(o, new DateTime(2026, 8, 30, 12, 14, 0)));
        Assert.Equal(1, seq.SectionDue(o, new DateTime(2026, 8, 30, 12, 15, 10)));
        Assert.Null(seq.SectionDue(o, new DateTime(2026, 8, 30, 12, 15, 40))); // once per day
        Assert.Equal(1, seq.SectionDue(o, new DateTime(2026, 8, 31, 12, 15, 0))); // next day again
    }

    [Theory]
    [InlineData("SECTION 2", 2, "")]
    [InlineData("section Break", 0, "Break")]
    public void SectionCommandParses(string line, int intArg, string textArg)
    {
        var cmd = ControlProtocol.Parse(line);
        Assert.Equal(RemoteCommandKind.PlaylistSection, cmd.Kind);
        Assert.Equal(intArg, cmd.IntArg);
        Assert.Equal(textArg, cmd.TextArg);
    }

    [Fact]
    public void MigrationNormalizesEveryPlaylistInTheFile()
    {
        var state = new ShowState { SchemaVersion = 2 };
        state.Pattern.Media.Playlist.Items.Add(new PlaylistItemConfig { Path = @"C:\x.png" });
        var independent = new OutputAssignment { ScreenId = "s" };
        independent.Pattern.Media.Playlist.Folders.Add(@"C:\media");
        state.Independent.Add(independent);

        SettingsStore.Migrate(state);

        Assert.Equal(ShowState.CurrentSchemaVersion, state.SchemaVersion);
        Assert.Single(state.Pattern.Media.Playlist.Sections);
        Assert.Single(state.Pattern.Media.Playlist.Sections[0].Items);
        Assert.Single(independent.Pattern.Media.Playlist.Sections);
        Assert.Single(independent.Pattern.Media.Playlist.Sections[0].Folders);
    }
}
