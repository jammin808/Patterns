using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>Who ducks whom: the one table, one row at a time.</summary>
public class GainRulesTests
{
    [Fact]
    public void NothingPlayingLeavesEveryBusAtFull()
    {
        var quiet = new GainInputs(VogSoundPlaying: false, DuckPct: 20, StingRamp: 1);
        foreach (var bus in Enum.GetValues<AudioBus>())
        {
            Assert.Equal(1.0, GainRules.For(bus, quiet));
        }
    }

    [Fact]
    public void AVogSoundDucksTheMusicAStingerSoundAndAClipButNeverItself()
    {
        var vog = new GainInputs(VogSoundPlaying: true, DuckPct: 20, StingRamp: 1);
        Assert.Equal(0.2, GainRules.For(AudioBus.Music, vog), 6);
        Assert.Equal(0.2, GainRules.For(AudioBus.StingSound, vog), 6);
        Assert.Equal(0.2, GainRules.For(AudioBus.ClipAudio, vog), 6);
        Assert.Equal(1.0, GainRules.For(AudioBus.VogSound, vog));
    }

    [Fact]
    public void AStingFadesOnlyTheMusic()
    {
        var fading = new GainInputs(VogSoundPlaying: false, DuckPct: 20, StingRamp: 0.4);
        Assert.Equal(0.4, GainRules.For(AudioBus.Music, fading), 6);
        Assert.Equal(1.0, GainRules.For(AudioBus.StingSound, fading));
        Assert.Equal(1.0, GainRules.For(AudioBus.ClipAudio, fading));
        Assert.Equal(1.0, GainRules.For(AudioBus.VogSound, fading));
    }

    [Fact]
    public void BothAtOnceTheQuieterWinsOnTheMusic()
    {
        Assert.Equal(0.2, GainRules.For(AudioBus.Music, new GainInputs(true, 20, 0.7)), 6);
        Assert.Equal(0.1, GainRules.For(AudioBus.Music, new GainInputs(true, 20, 0.1)), 6);
        Assert.Equal(0.0, GainRules.For(AudioBus.Music, new GainInputs(true, 0, 1)));
    }

    [Fact]
    public void TheLiveDuckIsAFactorOnEverythingButAVog()
    {
        var live = new GainInputs(VogSoundPlaying: false, DuckPct: 20, StingRamp: 1, LiveDuck: 0.1);
        Assert.Equal(0.1, GainRules.For(AudioBus.Music, live), 6);
        Assert.Equal(0.1, GainRules.For(AudioBus.StingSound, live), 6);
        Assert.Equal(0.1, GainRules.For(AudioBus.ClipAudio, live), 6);
        Assert.Equal(1.0, GainRules.For(AudioBus.VogSound, live));

        // With a VOG on top the two ducks multiply; the ramp is clamped like the others.
        var both = new GainInputs(true, 50, 1, 0.5);
        Assert.Equal(0.25, GainRules.For(AudioBus.Music, both), 6);
        Assert.Equal(0.25, GainRules.For(AudioBus.StingSound, both), 6);
        Assert.Equal(0.0, GainRules.For(AudioBus.Music, new GainInputs(false, 20, 1, -1)));
        Assert.Equal(1.0, GainRules.For(AudioBus.Music, new GainInputs(false, 20, 1, 3)));
        Assert.Equal(1.0, GainRules.For(AudioBus.Music, new GainInputs(false, 20, 1)));   // the default is off
    }

    [Fact]
    public void TheRampIsClampedAndTheDuckIsAShare()
    {
        Assert.Equal(1.0, GainRules.For(AudioBus.Music, new GainInputs(false, 20, 7)));
        Assert.Equal(0.0, GainRules.For(AudioBus.Music, new GainInputs(false, 20, -1)));
        Assert.Equal(1.0, GainRules.For(AudioBus.StingSound, new GainInputs(true, 100, 1)));
    }
}
