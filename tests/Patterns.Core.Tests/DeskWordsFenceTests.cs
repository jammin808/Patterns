using System.Reflection;
using System.Text.RegularExpressions;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 79.6: the words that send an operator to a page name a page that exists, on the rail it is on. The
/// retrospective found "Admin → Switcher" (never a page: EDIT SAFE BY DEFAULT lives under EDIT SAFE on SETUP →
/// Screens) and "the Outputs page" (the Screens page, since the rail was drawn) in the help, a menu, a tooltip and
/// the README. The fence reads every "RAIL → Page" in the help bodies at runtime and, when the test runs inside the
/// repository, in the menus' and the wall's source, the README and the wire's papers, and holds each against
/// <see cref="DeskPages"/>; the two stale phrases are named so they cannot come back.
/// </summary>
public class DeskWordsFenceTests
{
    private static readonly Regex PageReference = new(@"\b(SHOW|PLAN|BUILD|SETUP|ADMIN|Show|Plan|Build|Setup|Admin) (?:→|›) ([A-Z][A-Za-z]+)(?: ([a-z]+))?", RegexOptions.Compiled);

    private static readonly string[] Papers =
    {
        "README.md", "docs/REMOTE.md", "docs/COMPANION.md",
        "src/Patterns.Core/Menus/DeskMenus.cs", "src/Patterns.App/Views/Controls/WallView.axaml",
    };

    private static IEnumerable<(string Where, string Text)> Surfaces()
    {
        foreach (var f in typeof(HelpBodies).GetFields(BindingFlags.Public | BindingFlags.Static).Where(f => f.IsLiteral && f.FieldType == typeof(string)))
        {
            yield return ("HelpBodies." + f.Name, (string)f.GetRawConstantValue()!);
        }
        var root = RepoRoot();
        if (root is null) yield break;          // a test binary run away from the repository: the runtime surfaces alone
        foreach (var rel in Papers)
        {
            var path = Path.Combine(root, rel);
            if (File.Exists(path)) yield return (rel, File.ReadAllText(path));
        }
    }

    [Fact]
    public void EveryRailArrowPageNamesAPageOnThatRail()
    {
        var wrong = new List<string>();
        var found = 0;
        foreach (var (where, text) in Surfaces())
        {
            foreach (Match m in PageReference.Matches(text))
            {
                found++;
                var rail = m.Groups[1].Value;
                var one = m.Groups[2].Value;
                var two = m.Groups[3].Success ? one + " " + m.Groups[3].Value : null;
                if (!IsPage(rail, one) && (two is null || !IsPage(rail, two))) wrong.Add($"{where}: {m.Value}");
            }
        }
        Assert.True(found >= 10, "the walk found fewer page references than the help alone is known to carry: " + found);
        Assert.True(wrong.Count == 0, "Words that send the operator to a page that is not on that rail:\n" + string.Join("\n", wrong));
    }

    [Theory]
    [InlineData("Admin → Switcher")]
    [InlineData("Outputs page")]
    public void TheStalePhrasesAreGone(string phrase)
    {
        var carrying = Surfaces().Where(s => s.Text.Contains(phrase, StringComparison.Ordinal)).Select(s => s.Where).ToList();
        Assert.True(carrying.Count == 0, $"\"{phrase}\" is still said by: " + string.Join(", ", carrying));
    }

    private static bool IsPage(string rail, string name)
        => DeskPages.All.Any(p => p.Rail.Equals(rail, StringComparison.OrdinalIgnoreCase) && p.Header.Equals(name, StringComparison.OrdinalIgnoreCase));

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
