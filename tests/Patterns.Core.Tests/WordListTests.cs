using Patterns.Core.Play;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 83 (L37): the word list matches whole words, so a place name holding a listed word passes, and an entry's
/// own wildcards say when a word that starts, ends or holds it is meant.
/// </summary>
public class WordListTests
{
    [Fact]
    public void AWholeWordMatchesAndAWordInsideALongerOneDoesNot()
    {
        var list = new[] { "cunt" };
        Assert.False(WordList.Matches("Scunthorpe", list));
        Assert.False(WordList.Matches("Scunthorpe United, table 4", list));
        Assert.True(WordList.Matches("cunt", list));
        Assert.True(WordList.Matches("You CUNT!", list));
        Assert.True(WordList.Matches("a (cunt) here", list));
        Assert.False(WordList.Matches("cunts", list));                      // the plural is its own entry
        Assert.True(WordList.Matches("cunts", new[] { "cunts" }));
        Assert.False(WordList.Matches("", list));
        Assert.False(WordList.Matches(null, list));
        Assert.False(WordList.Matches("anything", Array.Empty<string>()));
    }

    [Fact]
    public void AnEntrysWildcardsSayWhenAWordThatStartsEndsOrHoldsItIsMeant()
    {
        Assert.True(WordList.Matches("fucking hell", new[] { "fuck*" }));
        Assert.False(WordList.Matches("motherfucker", new[] { "fuck*" }));
        Assert.True(WordList.Matches("motherfucker", new[] { "*fucker" }));
        Assert.True(WordList.Matches("motherfucker", new[] { "*fuck*" }));
        Assert.True(WordList.Matches("bullshit", new[] { "*shit*" }));
        Assert.False(WordList.Matches("bullshit", new[] { "shit" }));
        Assert.False(WordList.Matches("shitake", new[] { "shit" }));
    }

    [Fact]
    public void AnEntryWithASpaceIsAPhraseOfWholeWordsInOrder()
    {
        var list = new[] { "kill all" };
        Assert.True(WordList.Matches("we will KILL ALL of them", list));
        Assert.True(WordList.Matches("kill, all!", list));
        Assert.False(WordList.Matches("kill them all", list));
        Assert.False(WordList.Matches("killall", list));
    }

    [Fact]
    public void EmptyAndBareWildcardEntriesAreIgnoredAndCaseNeverMatters()
    {
        Assert.False(WordList.Matches("anything at all", new[] { "", "  ", "*", "**" }));
        Assert.True(WordList.Matches("Schöne Grüße", new[] { "SCHÖNE" }));
        Assert.Equal(new[] { "a", "b2", "c" }, WordList.Words("A-b2 ... C"));
    }

    [Fact]
    public void TheDefaultListLetsAPlaceNameThroughAndStopsTheWordsItNames()
    {
        Assert.False(WordList.Matches("Scunthorpe", WordList.Default));
        Assert.False(WordList.Matches("Table 12 — the Penistone crowd", WordList.Default));
        Assert.True(WordList.Matches("bullshit", WordList.Default));
        Assert.True(WordList.Matches("Faggots on parade", WordList.Default));
        Assert.True(WordList.Matches("what a cunt", WordList.Default));
    }
}
