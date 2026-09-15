using Patterns.Assistant;
using Patterns.Core.Media;
using Xunit;

namespace Patterns.Assistant.Tests;

/// <summary>The attachment rules stay honest without a codec: a build with none says so instead of guessing.</summary>
public class AssistantCodecTests
{
    [Fact]
    public void ABuildWithoutAPictureCodecSaysSoInsteadOfGuessing()
    {
        var was = Pictures.Shrinker;
        try
        {
            Pictures.Shrinker = null;
            var result = AssistantAttachments.Read("shot.png", new byte[] { 1, 2, 3 });
            Assert.Null(result.Attachment);
            Assert.Contains("no picture codec", result.Refusal);
        }
        finally
        {
            Pictures.Shrinker = was;
        }
    }
}
