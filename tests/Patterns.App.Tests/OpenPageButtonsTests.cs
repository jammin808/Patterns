using System.Text.RegularExpressions;
using Avalonia.Headless.XUnit;
using Patterns.App.ViewModels;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 62: every Build section that points the operator at another page carries the button
/// that opens it — "Design the animation in the Particles page" had OPEN PARTICLES, "Configure
/// the file in the Media page" had nothing. The rule, held by this test over the XAML itself:
/// a hint that names a page of the rail (the Screens page, the Arcade page's picture…) sits in a
/// file that also offers OpenPageCommand (or the page-by-name command) with that page as its
/// parameter, so a hint never sends anyone hunting through the rail.
/// </summary>
public class OpenPageButtonsTests
{
    private static readonly string[] BuildSections =
    {
        "PatternSection", "MediaSection", "OverlaysSection", "LowerThirdsSection", "CountdownSection",
        "ParticlesSection", "FractalsSection", "ReactiveSection", "BrandingSection", "LayersSection", "LibrarySection",
    };

    [Fact]
    public void EveryHintThatNamesAPageHasTheButtonThatOpensIt()
    {
        var root = ModuleRulesTests.RepoRoot() ?? throw new InvalidOperationException("The repository root was not found above the test binary.");
        var pages = Shell.Pages.Select(p => p.Header).ToHashSet(StringComparer.Ordinal);
        var missing = new List<string>();
        var found = 0;
        foreach (var section in BuildSections)
        {
            var path = Path.Combine(root, "src", "Patterns.App", "Views", "Sections", section + ".axaml");
            if (!File.Exists(path)) continue;
            var xaml = File.ReadAllText(path);
            foreach (Match hint in Regex.Matches(xaml, @"Text=""([^""]*)"""))
            {
                foreach (Match m in Regex.Matches(hint.Groups[1].Value, @"\b(?<page>[A-Z][a-z]+(?: [a-z]+)?) page(?:'s)?\b"))
                {
                    var page = m.Groups["page"].Value;
                    if (!pages.Contains(page)) continue;
                    found++;
                    var opens = xaml.Contains($"CommandParameter=\"{page}\"", StringComparison.Ordinal)
                                && (xaml.Contains("OpenPageCommand", StringComparison.Ordinal) || xaml.Contains("SelectPageByNameCommand", StringComparison.Ordinal));
                    if (!opens) missing.Add($"{section}: \"{hint.Groups[1].Value[..Math.Min(60, hint.Groups[1].Value.Length)]}…\" names the {page} page and offers no OPEN {page.ToUpperInvariant()}");
                }
            }
        }
        Assert.True(found >= 10, $"only {found} hints name a page — the regex or the sections list has drifted");
        Assert.True(missing.Count == 0, string.Join("\n", missing));
    }

    [AvaloniaFact]
    public void OpenPageOpensAPageOfTheRailAndIgnoresANameItDoesNotHave()
    {
        var b = TestApp.Boot();
        try
        {
            var vm = b.Vm;
            vm.OpenPageCommand.Execute("Media");
            Assert.Equal(Shell.IndexOf("Media"), vm.SelectedPageIndex);
            vm.OpenPageCommand.Execute("Reactive");
            Assert.Equal(Shell.IndexOf("Reactive"), vm.SelectedPageIndex);
            vm.OpenPageCommand.Execute("Nowhere");
            Assert.Equal(Shell.IndexOf("Reactive"), vm.SelectedPageIndex);
        }
        finally
        {
            b.Dispose();
        }
    }
}
