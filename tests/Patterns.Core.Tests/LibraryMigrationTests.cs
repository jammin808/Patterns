using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>Schema 7: the media library's entries gain an id, a kind and a date, derived and minted once.</summary>
public class LibraryMigrationTests
{
    [Fact]
    public void AVersionSixFileGainsIdsKindsAndDatesOnceAndKeepsWhatItHad()
    {
        var json = """
            {
              "SchemaVersion": 6,
              "MediaLibrary": [
                { "Path": "C:/show/walk-in.png", "IsVideo": false },
                { "Path": "C:/show/opener.mp4", "IsVideo": true },
                { "Path": "C:/show/bed.mp3", "IsVideo": true },
                { "Id": "keep-me", "Path": "C:/show/odd.bin", "IsVideo": true, "Name": "Odd one" }
              ]
            }
            """;
        var state = JsonUtil.Deserialize<ShowState>(json)!;
        SettingsStore.Migrate(state);

        Assert.Equal(ShowState.CurrentSchemaVersion, state.SchemaVersion);
        Assert.Equal(8, ShowState.CurrentSchemaVersion);
        var lib = state.MediaLibrary;
        Assert.Equal(4, lib.Count);
        Assert.All(lib, m => Assert.False(string.IsNullOrWhiteSpace(m.Id)));
        Assert.Equal(4, lib.Select(m => m.Id).Distinct().Count());
        Assert.Equal("keep-me", lib[3].Id);
        Assert.Equal(new[] { LibraryMediaKind.Image, LibraryMediaKind.Video, LibraryMediaKind.Audio, LibraryMediaKind.Video },
            lib.Select(m => m.Kind).ToArray());
        Assert.All(lib, m => Assert.NotEqual(default, m.AddedUtc));
        Assert.Equal("walk-in.png", lib[0].DisplayName);
        Assert.Equal("Odd one", lib[3].DisplayName);
        Assert.True(lib[2].IsVideo); // the older flag is kept as written

        // A second pass changes nothing: ids, kinds and dates stick.
        var before = lib.Select(m => (m.Id, m.Kind, m.AddedUtc)).ToArray();
        SettingsStore.Migrate(state);
        Assert.Equal(before, lib.Select(m => (m.Id, m.Kind, m.AddedUtc)).ToArray());

        // And the upgraded file carries them.
        var back = JsonUtil.Deserialize<ShowState>(JsonUtil.Serialize(state))!;
        Assert.Equal(before, back.MediaLibrary.Select(m => (m.Id, m.Kind, m.AddedUtc)).ToArray());
    }

    [Fact]
    public void TheKindFollowsTheExtensionAndTheDecodedFlagOnlyWhenItSaysNothing()
    {
        Assert.Equal(LibraryMediaKind.Video, MediaLibraryEntry.KindOf("a.MOV", false));
        Assert.Equal(LibraryMediaKind.Audio, MediaLibraryEntry.KindOf("a.wav", false));
        Assert.Equal(LibraryMediaKind.Image, MediaLibraryEntry.KindOf("a.jpg", true));
        Assert.Equal(LibraryMediaKind.Image, MediaLibraryEntry.KindOf("a.unknown", false));
        Assert.Equal(LibraryMediaKind.Video, MediaLibraryEntry.KindOf("a.unknown", true));
        Assert.Equal(LibraryMediaKind.Unknown, new MediaLibraryEntry().Kind);
        Assert.False(string.IsNullOrWhiteSpace(new MediaLibraryEntry().Id));
        Assert.Equal("", new MediaLibraryEntry { Name = null! }.Name);
    }
}
