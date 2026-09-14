using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The twin service in parts: one partial file per concern, each on a page — so the next round that
/// touches a handover reads the handover, not two and a half thousand lines. A part that outgrows
/// the page is a part that wants splitting again.
/// </summary>
public class TwinServicePartsTests
{
    private const int PageLines = 900;

    private static readonly string[] Parts = { "TwinService.cs", "TwinService.Main.cs", "TwinService.Handover.cs", "TwinService.Standby.cs", "TwinService.Followers.cs" };

    private static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "src", "Patterns.App", "Services"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    [Fact]
    public void TheTwinServiceIsInPartsAndNoPartOutgrowsThePage()
    {
        var root = RepoRoot();
        Assert.NotNull(root);
        var dir = Path.Combine(root!, "src", "Patterns.App", "Services");
        foreach (var part in Parts)
        {
            var path = Path.Combine(dir, part);
            Assert.True(File.Exists(path), path);
            var lines = File.ReadAllLines(path);
            Assert.True(lines.Length <= PageLines, $"{part} is {lines.Length} lines — past the page of {PageLines}");
            Assert.Contains(lines, l => l.StartsWith("public sealed partial class TwinService", StringComparison.Ordinal));
        }
        // No sixth part grew unnoticed, and nothing of the old single file is left behind.
        Assert.Equal(Parts.Length, Directory.GetFiles(dir, "TwinService*.cs").Length);
    }
}
