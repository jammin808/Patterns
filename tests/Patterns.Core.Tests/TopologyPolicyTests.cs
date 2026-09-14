using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>One rule for the edits that reopen a source: the table, the words, and what "on air" means.</summary>
public class TopologyPolicyTests
{
    [Fact]
    public void TheTableNamesWhatEachEditDoesToARunningSource()
    {
        Assert.Equal(TopologyChange.RequiresReopen, TopologyPolicy.Classify(TopologyEdit.CaptureFormat));
        Assert.Equal(TopologyChange.RequiresReopen, TopologyPolicy.Classify(TopologyEdit.CaptureLowLatency));
        Assert.Equal(TopologyChange.RequiresReopen, TopologyPolicy.Classify(TopologyEdit.ClipLoop));
        Assert.Equal(TopologyChange.RequiresReopen, TopologyPolicy.Classify(TopologyEdit.AudioRoutingMode));
        Assert.Equal(TopologyChange.RequiresRestart, TopologyPolicy.Classify(TopologyEdit.DirectOutputMode));
        Assert.Equal(TopologyChange.LiveSafe, TopologyPolicy.Classify(TopologyEdit.ClipSound));
        Assert.Equal(TopologyChange.LiveSafe, TopologyPolicy.Classify(TopologyEdit.InputNickname));
        Assert.Equal(TopologyChange.LiveSafe, TopologyPolicy.Classify(TopologyEdit.InputCrop));
        foreach (var edit in Enum.GetValues<TopologyEdit>()) Assert.NotEmpty(TopologyPolicy.Name(edit));   // every edit has a name for the words
    }

    [Fact]
    public void TheWordsSayWhatIsPendingWhyAndWhenItApplies()
    {
        Assert.Equal("Low latency change pending — Cam Link 4K is on air; applies when it leaves the air or the outputs go off air.",
            TopologyPolicy.PendingWords(TopologyEdit.CaptureLowLatency, "Cam Link 4K"));
        Assert.StartsWith("Format change pending — Podium cam is on air", TopologyPolicy.PendingWords(TopologyEdit.CaptureFormat, "Podium cam"));
        Assert.StartsWith("Loop change pending — walk-in.mp4 is on air", TopologyPolicy.PendingWords(TopologyEdit.ClipLoop, "walk-in.mp4"));
        Assert.StartsWith("Audio routing change pending", TopologyPolicy.PendingWords(TopologyEdit.AudioRoutingMode, "walk-in.mp4"));
    }

    [Fact]
    public void OnAirIsTheProgrammeWhileTheOutputsAreLive()
    {
        Assert.True(TopologyPolicy.OnAir(outputsLive: true, onProgram: true));
        Assert.False(TopologyPolicy.OnAir(outputsLive: false, onProgram: true));                         // the outputs off: a reopen restarts nothing the room sees
        Assert.False(TopologyPolicy.OnAir(outputsLive: true, onProgram: false));                         // the preview alone: not the room's picture
    }
}
