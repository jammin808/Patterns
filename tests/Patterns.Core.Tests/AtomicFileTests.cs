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
