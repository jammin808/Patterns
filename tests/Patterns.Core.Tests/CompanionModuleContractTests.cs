using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The contract between the Companion module and the desk: every line the module can put on the
/// wire is a line the desk parses. The module's own tests write test/lines.txt from every action
/// pressed with every choice of its dropdowns; this side reads the file and parses each line, so a
/// verb spelt wrongly on either side fails a build, not a show. The palette is held the same way
/// in CompanionPaletteTests.
/// </summary>
public class CompanionModuleContractTests
{
    internal static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (Directory.Exists(Path.Combine(dir.FullName, "integrations", "companion-module-patterns"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }

    internal static string ModuleDir => Path.Combine(RepoRoot() ?? throw new InvalidOperationException("The repository root was not found above the test binary."), "integrations", "companion-module-patterns");

    [Fact]
    public void EveryLineTheModuleCanSendIsALineTheDeskParses()
    {
        var path = Path.Combine(ModuleDir, "test", "lines.txt");
        Assert.True(File.Exists(path), $"{path} is missing — run `npm run lines` in the module folder");
        var lines = File.ReadAllLines(path).Where(l => l.Trim().Length > 0).ToList();
        Assert.True(lines.Count > 150, $"only {lines.Count} lines");
        var unknown = new List<string>();
        foreach (var line in lines)
        {
            var cmd = ControlProtocol.Parse(line);
            if (cmd.Kind == RemoteCommandKind.Unknown) unknown.Add(line);
        }
        Assert.True(unknown.Count == 0, "The desk does not understand:\n" + string.Join("\n", unknown));
    }

    [Fact]
    public void TheModuleIsOneCompanion5Loads()
    {
        var manifest = File.ReadAllText(Path.Combine(ModuleDir, "companion", "manifest.json"));
        Assert.Contains("\"type\": \"connection\"", manifest);
        Assert.Contains("\"type\": \"node22\"", manifest);
        Assert.Contains("\"bonjourQueries\"", manifest);
        Assert.Contains("\"type\": \"patterns\"", manifest);
        var package = File.ReadAllText(Path.Combine(ModuleDir, "package.json"));
        Assert.Contains("\"@companion-module/base\": \"~2.", package);
        Assert.False(File.Exists(Path.Combine(ModuleDir, "main.js")), "the module lives in src/ now");
        Assert.Contains("export default PatternsInstance", File.ReadAllText(Path.Combine(ModuleDir, "src", "main.js")));
        Assert.Contains("export const UpgradeScripts", File.ReadAllText(Path.Combine(ModuleDir, "src", "main.js")));
    }
}
