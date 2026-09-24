using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 80.4: one atomic write for every sidecar — the text to a temp file beside the target, then moved over it —
/// and a source fence that no hand-written copy of the pattern appears again outside the two named exemptions.
/// </summary>
public class AtomicFileTests
{
    [Fact]
    public void AWriteLandsWholeLeavesNoTempFileAndCanKeepTheOldAsBackup()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-atomic-" + Guid.NewGuid().ToString("N"));
        try
        {
            var path = Path.Combine(dir, "deeper", "record.json");             // the directory is made
            AtomicFile.WriteAllText(path, "one");
            Assert.Equal("one", File.ReadAllText(path));
            Assert.False(File.Exists(AtomicFile.TempPath(path)));
            Assert.False(File.Exists(path + ".bak"));

            AtomicFile.WriteAllText(path, "two");                              // over an existing file
            Assert.Equal("two", File.ReadAllText(path));
            Assert.False(File.Exists(AtomicFile.TempPath(path)));

            AtomicFile.WriteAllTextKeepingBackup(path, "three");               // the ladder's shape: the old one kept
            Assert.Equal("three", File.ReadAllText(path));
            Assert.Equal("two", File.ReadAllText(path + ".bak"));
            Assert.False(File.Exists(AtomicFile.TempPath(path)));
            Assert.Equal(path + ".tmp", AtomicFile.TempPath(path));

            // Round 85: with a predicate the old one is kept only when it is whole — a torn file never goes over the backup.
            AtomicFile.WriteAllTextKeepingBackup(path, "four", worthKeeping: old => old != "three");
            Assert.Equal("four", File.ReadAllText(path));
            Assert.Equal("two", File.ReadAllText(path + ".bak"));
            AtomicFile.WriteAllTextKeepingBackup(path, "five", worthKeeping: old => old == "four");
            Assert.Equal("five", File.ReadAllText(path));
            Assert.Equal("four", File.ReadAllText(path + ".bak"));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TheRecoveryRecordKeepsTheOneBeforeItAndReadsItWhenTheRecordIsTorn()
    {
        var dir = Path.Combine(Path.GetTempPath(), "patterns-recovery-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        try
        {
            var store = new RecoveryStore(dir);
            var main = Path.Combine(dir, "patterns.recovery.json");
            Assert.Null(store.Read());                                          // no record: a clean exit, no problem
            Assert.Equal("", store.Problem);

            store.Write(live: true, audioPlaying: false, airLook: "One");
            store.Write(live: true, audioPlaying: true, airLook: "Two");
            Assert.Equal("Two", store.Read()!.AirLook);
            Assert.Equal("", store.Problem);
            Assert.Equal("One", JsonUtil.Deserialize<RecoverySnapshot>(File.ReadAllText(main + ".bak"))!.AirLook);
            Assert.False(File.Exists(AtomicFile.TempPath(main)));

            File.WriteAllText(main, "{\"Live\":tr");                          // torn: a power cut, or a stick whose move was not atomic
            var put = store.Read();
            Assert.NotNull(put);
            Assert.Equal("One", put!.AirLook);
            Assert.Contains("unreadable at boot", store.Problem);
            Assert.Contains("before it", store.Problem);

            File.WriteAllText(main + ".bak", "not a record either");
            Assert.Null(store.Read());
            Assert.Contains("no readable backup", store.Problem);

            store.Write(live: false, audioPlaying: false, airLook: "Three");    // the next write heals both
            Assert.Equal("Three", store.Read()!.AirLook);
            Assert.Equal("", store.Problem);

            // Round 85 (P1-03): a torn record is never rotated over the whole one — the backup stays the last whole record
            // through the writes after the tear — and a file that parses to no record (the literal null) reads the backup.
            store.Write(live: true, audioPlaying: false, airLook: "Four");      // .bak = Three, whole
            File.WriteAllText(main, "{\"Live\":tr");
            store.Write(live: true, audioPlaying: true, airLook: "Five");       // the torn Four is not kept; Three stays as the backup
            Assert.Equal("Five", store.Read()!.AirLook);
            Assert.Equal("", store.Problem);
            Assert.Equal("Three", JsonUtil.Deserialize<RecoverySnapshot>(File.ReadAllText(main + ".bak"))!.AirLook);
            store.Write(live: true, audioPlaying: true, airLook: "Six");        // a whole Five is kept
            Assert.Equal("Five", JsonUtil.Deserialize<RecoverySnapshot>(File.ReadAllText(main + ".bak"))!.AirLook);
            File.WriteAllText(main, "null");
            var fromNull = store.Read();
            Assert.NotNull(fromNull);
            Assert.Equal("Five", fromNull!.AirLook);
            Assert.Contains("unreadable at boot", store.Problem);
            File.WriteAllText(main + ".bak", "null");
            Assert.Null(store.Read());
            Assert.Contains("no readable backup", store.Problem);
            store.Write(live: false, audioPlaying: false, airLook: "Seven");    // a null main is not kept either: the backup is the whole Seven only after the next write
            Assert.Equal("Seven", store.Read()!.AirLook);
            Assert.Equal("null", File.ReadAllText(main + ".bak"));
            store.Write(live: false, audioPlaying: false, airLook: "Eight");
            Assert.Equal("Seven", JsonUtil.Deserialize<RecoverySnapshot>(File.ReadAllText(main + ".bak"))!.AirLook);

            store.Clear();
            Assert.False(File.Exists(main));
            Assert.False(File.Exists(main + ".bak"));
            Assert.Null(store.Read());
            Assert.Equal("", store.Problem);

            AtomicFile.WriteAllText(main, "quick", durable: false);             // the playhead's way: whole, moved, no flush
            Assert.Equal("quick", File.ReadAllText(main));
            Assert.False(File.Exists(AtomicFile.TempPath(main)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    /// <summary>
    /// The fence: no source under src/ writes a ".tmp" beside a target by hand. Two files may — the output-ownership
    /// seam writes through its own file abstraction (its tests inject a failing one), and the management download
    /// streams bytes into its temp file before the hash is checked; both are named here with their reason.
    /// </summary>
    [Fact]
    public void EveryTempThenMoveWriteGoesThroughTheHelper()
    {
        var root = RepoRoot();
        if (root is null) return;                                              // published tests without the tree
        var exempt = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["AtomicFile.cs"] = "the helper itself",
            ["OutputOwnership.cs"] = "writes through its own file seam, which the tests make fail",
            ["ManagementService.cs"] = "streams a download into the temp file before its hash is checked",
        };
        var offences = new List<string>();
        foreach (var file in Directory.EnumerateFiles(Path.Combine(root, "src"), "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}", StringComparison.Ordinal) || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}", StringComparison.Ordinal)) continue;
            if (exempt.ContainsKey(Path.GetFileName(file))) continue;
            var text = File.ReadAllText(file);
            if (text.Contains("\".tmp\"", StringComparison.Ordinal)) offences.Add(Path.GetRelativePath(root, file));
        }
        Assert.True(offences.Count == 0, "Hand-written temp-then-move writes (use AtomicFile):\n" + string.Join("\n", offences));
        foreach (var name in exempt.Keys) Assert.True(Directory.EnumerateFiles(Path.Combine(root, "src"), name, SearchOption.AllDirectories).Any(), $"the exemption {name} names a file that is gone — drop it");
    }

    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Patterns.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
