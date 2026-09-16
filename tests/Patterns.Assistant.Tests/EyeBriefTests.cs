using Patterns.Assistant;
using Patterns.Core.Model;
using Xunit;

namespace Patterns.Assistant.Tests;

/// <summary>Round 66: the God's Eye's lines in the assistant's brief, under the rule that says how to read them — and nothing when the desk has no picture.</summary>
public class EyeBriefTests
{
    [Fact]
    public void TheBriefCarriesThePictureWorstFirstAndOnlyWhenThereIsOne()
    {
        var facts = new ShowFacts
        {
            Eye = new[]
            {
                "Main LED: MISMATCH — Rate: 50 Hz asked · 59.94 Hz observed",
                "VIDEO · Screen: Main LED — red · MISMATCH — Rate: 50 Hz asked · 59.94 Hz observed",
                "CONTROL · Deck: FOH deck — amber · connected, not paired — its keys do nothing",
                "This desk → Main LED (shows, green: on air)",
            },
        };
        var brief = ShowBrief.Summarise(new ShowState(), facts);
        var rule = brief.IndexOf("The God's Eye — the whole show as one picture", StringComparison.Ordinal);
        Assert.True(rule >= 0, brief);
        Assert.Contains("red is wrong now, amber needs a look, green is right, grey is off or unknown", brief);
        Assert.Contains("Answer 'what is wrong?' from the problems in this order", brief);
        Assert.Contains("EYE FOCUS <thing> on the wire", brief);
        var first = brief.IndexOf("  Main LED: MISMATCH", StringComparison.Ordinal);
        var second = brief.IndexOf("  VIDEO · Screen: Main LED — red", StringComparison.Ordinal);
        var link = brief.IndexOf("  This desk → Main LED (shows, green: on air)", StringComparison.Ordinal);
        Assert.True(rule < first && first < second && second < link, brief);

        Assert.DoesNotContain("God's Eye", ShowBrief.Summarise(new ShowState(), new ShowFacts()));
    }
}
