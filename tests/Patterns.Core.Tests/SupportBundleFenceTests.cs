using System.Reflection;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 79.5: the support bundle carries every sidecar the core writes beside the settings, or names why not. The
/// fence walks every public const string file name in the core (patterns.*) — a sidecar added later without a place
/// in <see cref="SupportBundle.Files"/> or a reason in <see cref="SupportBundle.NeverBundled"/> fails here, not on a
/// report that arrives without the file that would have explained it. Two files the retrospective found missing —
/// the playhead record and the quality profile — are in.
/// </summary>
public class SupportBundleFenceTests
{
    [Fact]
    public void EverySidecarTheCoreWritesIsBundledOrNamedAsNeverBundled()
    {
        var constants = typeof(SupportBundle).Assembly.GetTypes()
            .SelectMany(t => t.GetFields(BindingFlags.Public | BindingFlags.Static))
            .Where(f => f.IsLiteral && f.FieldType == typeof(string))
            .Select(f => (Owner: f.DeclaringType!.Name + "." + f.Name, Value: (string)f.GetRawConstantValue()!))
            .Where(c => c.Value.StartsWith("patterns.", StringComparison.Ordinal) && c.Value.Contains('.', StringComparison.Ordinal) && !c.Value.Contains(' ', StringComparison.Ordinal))
            .ToList();
        Assert.True(constants.Count >= 8, "the walk found fewer file-name constants than the core is known to have: " + constants.Count);

        var unplaced = constants
            .Where(c => !SupportBundle.Files.Contains(c.Value, StringComparer.Ordinal) && !SupportBundle.NeverBundled.ContainsKey(c.Value))
            .Select(c => $"{c.Owner} = {c.Value}")
            .ToList();
        Assert.True(unplaced.Count == 0, "Sidecars neither bundled nor named as never bundled:\n" + string.Join("\n", unplaced));

        // The two the field's reports lacked, and the ones that must never travel in a bundle.
        Assert.Contains(PlayheadStore.FileName, SupportBundle.Files);
        Assert.Contains(QualityProfileStore.FileName, SupportBundle.Files);
        Assert.Contains(ShowLog.FileName, SupportBundle.Files);
        Assert.DoesNotContain(SpotifyCredentialStore.FileName, SupportBundle.Files);
        Assert.DoesNotContain("patterns.assistant.json", SupportBundle.Files);                // the assistant's key store lives in its own assembly and never in a bundle
        Assert.Equal(SupportBundle.Files.Length, SupportBundle.Files.Distinct(StringComparer.Ordinal).Count());
        foreach (var never in SupportBundle.NeverBundled.Keys) Assert.DoesNotContain(never, SupportBundle.Files);
    }

    /// <summary>Round 79.5: a settings file a newer build wrote is run as read — its schema never stamped down, its upgrade never run — and the store says so.</summary>
    [Fact]
    public void AFileFromANewerBuildIsReadAsItIsAndNeverMigratedDown()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-newer-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var state = new ShowState { Name = "From tomorrow", SchemaVersion = ShowState.CurrentSchemaVersion + 3 };
            state.Pattern.Kind = PatternKind.ColorBars;
            var path = Path.Combine(dir, "newer.patshow.json");
            File.WriteAllText(path, JsonUtil.Serialize(state));

            var store = new SettingsStore(dir);
            var loaded = store.LoadFrom(path);
            Assert.NotNull(loaded);
            Assert.Equal(ShowState.CurrentSchemaVersion + 3, store.NewerSchema);
            Assert.Equal(ShowState.CurrentSchemaVersion + 3, loaded!.SchemaVersion);          // not stamped down
            Assert.False(store.LastLoadMigrated);
            Assert.Empty(store.LastMigrationNotes);
            Assert.Equal(PatternKind.ColorBars, loaded.Pattern.Kind);
            Assert.Equal("From tomorrow", loaded.Name);

            // This build's own file: no newer schema, the ordinary path.
            var mine = new ShowState { Name = "Mine" };
            var minePath = Path.Combine(dir, "mine.patshow.json");
            File.WriteAllText(minePath, JsonUtil.Serialize(mine));
            var again = new SettingsStore(dir);
            Assert.NotNull(again.LoadFrom(minePath));
            Assert.Equal(0, again.NewerSchema);
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }
}
