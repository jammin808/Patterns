using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>Round 66.4: the help knows the God's Eye — its topic, its wire line, the pages it is filed on.</summary>
public class EyeHelpTests
{
    [Fact]
    public void TheHelpKnowsTheEyeAndFilesItOnItsPages()
    {
        var topic = HelpTopics.Find("gods-eye");
        Assert.NotNull(topic);
        Assert.Equal(HelpGroup.RunningTheShow, topic!.Group);
        Assert.Contains("EYE FOCUS", topic.Wire);
        Assert.Contains("EYE RESET", topic.Wire);
        Assert.Contains(HelpTopics.ForPage("Eye"), t => t.Id == "gods-eye");
        Assert.Contains(HelpTopics.ForPage("Remote"), t => t.Id == "gods-eye");
        Assert.Contains("rebuilt only when something moved", topic.Body);
        Assert.Contains("never guesses", topic.Body);
    }
}
