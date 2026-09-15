using System.Text.RegularExpressions;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 61: CHANGELOG.md is the history in rounds, newest first, and this test keeps it. Every
/// round from the newest in the plan down to 15 (the first in the repository's history) has its
/// entry, in order, with no gap, and each names its tag, a section of the plan that is that
/// round's, and the suite's count — so a round that ships without its line fails a build, not a
/// reader. The rounds before the history are listed in prose and not counted here.
/// </summary>
public class ChangelogTests
{
    private static string Root => CompanionModuleContractTests.RepoRoot()
        ?? throw new InvalidOperationException("The repository root was not found above the test binary.");

    private static string Changelog => File.ReadAllText(Path.Combine(Root, "CHANGELOG.md"));
    private static string Plan => File.ReadAllText(Path.Combine(Root, "docs", "PLAN.md"));

    /// <summary>The plan's round sections: "## 79. Round 61 — …" → section 79 is round 61's.</summary>
    private static Dictionary<int, HashSet<int>> PlanSectionsByRound()
    {
        var map = new Dictionary<int, HashSet<int>>();
        foreach (Match m in Regex.Matches(Plan, @"^## (\d+)\. Round (\d+) ", RegexOptions.Multiline))
        {
            var round = int.Parse(m.Groups[2].Value);
            if (!map.TryGetValue(round, out var set)) map[round] = set = new HashSet<int>();
            set.Add(int.Parse(m.Groups[1].Value));
        }
        return map;
    }

    private static List<(int Round, string Entry)> Entries()
    {
        var parts = Regex.Split(Changelog, @"^(?=## Round \d+ )", RegexOptions.Multiline);
        return parts.Where(p => p.StartsWith("## Round ", StringComparison.Ordinal))
            .Select(p =>
            {
                var head = p.Substring(0, p.IndexOf('\n'));
                var body = p.Contains("\n## ") ? p.Substring(0, p.IndexOf("\n## ", StringComparison.Ordinal)) : p;
                return (int.Parse(Regex.Match(head, @"^## Round (\d+) ").Groups[1].Value), body);
            })
            .ToList();
    }

    [Fact]
    public void EveryRoundFromTheNewestDownToTheFirstInHistoryHasItsEntryInOrder()
    {
        var newest = PlanSectionsByRound().Keys.Max();
        var rounds = Entries().Select(e => e.Round).ToList();
        Assert.True(rounds.Count > 0, "CHANGELOG.md has no round entries");
        Assert.True(rounds[0] == newest, $"the plan's newest round is {newest}; the changelog's is {rounds[0]} — add the entry");
        Assert.Equal(15, rounds[^1]);
        for (var i = 1; i < rounds.Count; i++)
            Assert.True(rounds[i] == rounds[i - 1] - 1, $"round {rounds[i - 1] - 1} has no entry (found {rounds[i]} after {rounds[i - 1]})");
    }

    [Fact]
    public void EveryEntryNamesItsDateItsTagOneOfItsPlanSectionsAndTheSuitesCount()
    {
        var sections = PlanSectionsByRound();
        foreach (var (round, entry) in Entries())
        {
            var head = entry.Substring(0, entry.IndexOf('\n'));
            Assert.Matches(@"^## Round \d+ — \d{4}-\d{2}-\d{2}", head);
            Assert.Contains($"`round-{round}`", entry);
            Assert.Matches(@"[\d,]{3,} tests", entry);

            var named = Regex.Matches(entry, @"§(\d+)").Select(m => int.Parse(m.Groups[1].Value)).ToHashSet();
            Assert.True(named.Count > 0, $"round {round}'s entry names no section of the plan");
            Assert.True(sections.TryGetValue(round, out var own), $"the plan has no 'Round {round}' section");
            Assert.True(named.Overlaps(own), $"round {round}'s entry names §{string.Join(", §", named)}; the plan's sections for it are §{string.Join(", §", own)}");
        }
    }
}
