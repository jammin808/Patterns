using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

public class AudioFadeTests
{
    private static readonly DateTime T0 = new(2026, 9, 4, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void TheStopFadeIsOneAtTheStartAndZeroAtTheEnd()
    {
        Assert.Equal(1, AudioFade.GainAt(T0, T0, 200));
        Assert.Equal(0.5, AudioFade.GainAt(T0, T0.AddMilliseconds(100), 200), 6);
        Assert.Equal(0, AudioFade.GainAt(T0, T0.AddMilliseconds(200), 200));
        Assert.Equal(0, AudioFade.GainAt(T0, T0.AddSeconds(5), 200));
        Assert.False(AudioFade.Done(T0, T0.AddMilliseconds(199), 200));
        Assert.True(AudioFade.Done(T0, T0.AddMilliseconds(200), 200));
    }

    [Fact]
    public void AZeroLengthFadeIsSilenceAtOnce()
    {
        Assert.Equal(0, AudioFade.GainAt(T0, T0, 0));
        Assert.True(AudioFade.Done(T0, T0, 0));
    }

    [Fact]
    public void ARetiredSourceLivesForTheLongestFadePlusTheMargin()
    {
        Assert.Equal(800, AudioFade.RetireHoldMs(transitionMs: 500, stopFadeMs: 200));
        Assert.Equal(1300, AudioFade.RetireHoldMs(transitionMs: 0, stopFadeMs: 1000));
        Assert.Equal(AudioFade.RetireMarginMs, AudioFade.RetireHoldMs(0, 0));
        Assert.Equal(AudioFade.RetireMarginMs, AudioFade.RetireHoldMs(-5, -5));
        // Never the flat four seconds a stopped clip used to get.
        Assert.True(AudioFade.RetireHoldMs(2000, 1000) < 4000);
    }
}
