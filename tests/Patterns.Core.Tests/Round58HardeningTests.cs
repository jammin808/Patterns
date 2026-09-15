using Patterns.Core.Audio;
using Patterns.Core.Media;
using Patterns.Rendering.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The browser and audio rules hardened in round 58: the web VT's phases observed, never
/// dispatched; a flush on the audio ring as an epoch no reader hears across; the screencast's
/// liveness judged from what arrived; a page's sound route failing closed; the audio graph's
/// domain.
/// </summary>
public class Round58HardeningTests
{
    private static readonly DateTime T0 = new(2026, 9, 14, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void AWebVtPhaseMovesOnWhatThePlayerReportsAndFailsWhenItDoesNot()
    {
        var mark = 83.0;
        var paused = new WebPlayerReading(true, 83.4, 240, Paused: true, AdShowing: false, Muted: false);
        var elsewhere = new WebPlayerReading(true, 12.0, 240, Paused: true, AdShowing: false, Muted: false);
        var playing = new WebPlayerReading(true, 83.9, 240, Paused: false, AdShowing: false, Muted: false);
        var none = WebPlayerReading.None;

        // A prepare is a request until the player reports paused at the mark.
        Assert.Equal(WebVtPhase.PrepareRequested, WebVt.Observe(WebVtPhase.PrepareRequested, none, mark, T0, T0.AddSeconds(1)));
        Assert.Equal(WebVtPhase.PrepareRequested, WebVt.Observe(WebVtPhase.PrepareRequested, elsewhere, mark, T0, T0.AddSeconds(2)));
        Assert.Equal(WebVtPhase.PreparedObserved, WebVt.Observe(WebVtPhase.PrepareRequested, paused, mark, T0, T0.AddSeconds(2)));
        Assert.Equal(WebVtPhase.Failed, WebVt.Observe(WebVtPhase.PrepareRequested, elsewhere, mark, T0, T0.AddSeconds(8.5)));         // unanswered past its timeout
        Assert.Equal(WebVtPhase.PrepareRequested, WebVt.Observe(WebVtPhase.PrepareRequested, playing, mark, T0, T0.AddSeconds(2)));    // playing is not "paused at the mark"

        // A play is a request until the player reports playing from the mark.
        Assert.Equal(WebVtPhase.FireRequested, WebVt.Observe(WebVtPhase.FireRequested, paused, mark, T0, T0.AddSeconds(1)));
        Assert.Equal(WebVtPhase.PlayingObserved, WebVt.Observe(WebVtPhase.FireRequested, playing, mark, T0, T0.AddSeconds(1)));
        Assert.Equal(WebVtPhase.Failed, WebVt.Observe(WebVtPhase.FireRequested, paused, mark, T0, T0.AddSeconds(5.5)));
        Assert.Equal(WebVtPhase.FireRequested, WebVt.Observe(WebVtPhase.FireRequested, new WebPlayerReading(true, 10, 240, false, false, false), mark, T0, T0.AddSeconds(1)));   // playing, but from the top: not the mark

        // Observed and idle phases stand.
        foreach (var stands in new[] { WebVtPhase.Unprepared, WebVtPhase.PreparedObserved, WebVtPhase.PlayingObserved, WebVtPhase.Failed })
        {
            Assert.Equal(stands, WebVt.Observe(stands, none, mark, T0, T0.AddMinutes(5)));
        }

        var arm = WebArm.None.ArmedBy(false, mark, T0);
        Assert.Contains("at its mark (observed)", WebVt.Words(arm, paused, T0.AddSeconds(3), WebVtPhase.PreparedObserved));
        Assert.Contains("prepare requested", WebVt.Words(arm, none, T0.AddSeconds(3), WebVtPhase.PrepareRequested));
        Assert.Contains("FAILED — the player did not answer", WebVt.Words(arm, none, T0.AddSeconds(30), WebVtPhase.Failed));
        Assert.DoesNotContain("requested", WebVt.Words(arm, paused, T0.AddSeconds(3)));                                             // no phase given: the old words
        Assert.Equal("VT armed at 1:23 (at mark)", WebVt.ShortWords(arm, paused, WebVtPhase.PreparedObserved));
        Assert.Equal("VT armed at 1:23 (NOT at mark)", WebVt.ShortWords(arm, elsewhere, WebVtPhase.Failed));
        var fired = arm.Fired(T0.AddSeconds(10));
        Assert.Equal("VT play requested", WebVt.ShortWords(fired, paused, WebVtPhase.FireRequested));
        Assert.Equal("VT did not start", WebVt.ShortWords(fired, paused, WebVtPhase.Failed));
        Assert.Equal("VT playing 1:23 / 4:00", WebVt.ShortWords(fired, playing, WebVtPhase.PlayingObserved));
    }

    [Fact]
    public void AFlushIsAnEpochNoReaderHearsAcross()
    {
        var ring = new AudioRing(channels: 2, capacityFrames: 100);
        var reader = ring.OpenReader(latencyFrames: 10);
        var samples = new float[40];
        for (var i = 0; i < samples.Length; i++) samples[i] = i + 1;                                   // 1..40: the sound before the seek
        ring.Write(samples);
        var got = new float[10];
        Assert.Equal(10, ring.Read(reader, got));
        Assert.Equal(21, got[0]);                                                                        // started 10 frames (20 samples) behind

        ring.Flush();                                                                                    // the seek
        Assert.Equal(1, ring.Epoch);
        Assert.Equal(0, ring.Read(reader, got));                                                         // nothing written since: silence, not the samples left before the flush
        Assert.All(got, v => Assert.Equal(0, v));
        Assert.Equal(1, reader.Flushes);
        var fresh = new float[6];
        for (var i = 0; i < fresh.Length; i++) fresh[i] = 100 + i;                                       // the sound after the seek
        ring.Write(fresh);
        Assert.Equal(6, ring.Read(reader, got));
        Assert.Equal(100, got[0]);                                                                       // the first new sample, never a pre-flush one
        Assert.Equal(105, got[5]);

        var late = ring.OpenReader(latencyFrames: 50);                                                   // a reader opened after the flush cannot reach behind it either
        ring.Write(new float[] { 200, 201 });
        var lateGot = new float[8];
        Assert.Equal(8, ring.Read(late, lateGot));
        Assert.Equal(100, lateGot[0]);                                                                   // its latency would have reached into the old sound: clamped at the flush
        Assert.Equal(0, late.Flushes);                                                                   // it never crossed one
    }

    [Fact]
    public void TheScreencastIsJudgedFromWhatArrivedNeverHealthyForeverAfterOneFrame()
    {
        Assert.Equal(ScreencastLiveness.Off, ScreencastHealth.Judge(on: false, 100, 0, 0, true, 0));
        Assert.Equal(ScreencastLiveness.Starting, ScreencastHealth.Judge(true, 0, 1000, double.MaxValue, false, 0));
        Assert.Equal(ScreencastLiveness.Static, ScreencastHealth.Judge(true, 0, 4000, double.MaxValue, false, 0));         // nothing ever, no media: a still page
        Assert.Equal(ScreencastLiveness.Stalled, ScreencastHealth.Judge(true, 0, 4000, double.MaxValue, true, 0));         // nothing ever while a video plays: stalled
        Assert.Equal(ScreencastLiveness.Delivering, ScreencastHealth.Judge(true, 30, 10_000, 500, true, 0));
        Assert.Equal(ScreencastLiveness.Static, ScreencastHealth.Judge(true, 30, 60_000, 2500, false, 0));                 // quiet, no media: fine
        Assert.Equal(ScreencastLiveness.Static, ScreencastHealth.Judge(true, 30, 60_000, 2500, true, 0));                  // quiet while playing, under the stall line: not yet
        Assert.Equal(ScreencastLiveness.Stalled, ScreencastHealth.Judge(true, 30, 60_000, 3500, true, 0));                 // past it: stalled
        Assert.Equal(ScreencastLiveness.Stalled, ScreencastHealth.Judge(true, 30, 60_000, 100, true, 5));                  // the acks failing: the session is gone whatever arrived
        Assert.True(ScreencastHealth.Carries(ScreencastLiveness.Starting));
        Assert.True(ScreencastHealth.Carries(ScreencastLiveness.Delivering));
        Assert.True(ScreencastHealth.Carries(ScreencastLiveness.Static));
        Assert.False(ScreencastHealth.Carries(ScreencastLiveness.Stalled));
        Assert.False(ScreencastHealth.Carries(ScreencastLiveness.Off));
        Assert.Contains("video,audio", ScreencastHealth.MediaPlayingScript);
        Assert.Equal(3, ScreencastHealth.MaxRestarts);
    }

    [Fact]
    public void APagesSoundRouteFailsClosed()
    {
        Assert.Equal(WebRouteOutcome.NothingAsked, WebAudioRoute.Classify("", ""));
        Assert.Equal(WebRouteOutcome.NothingAsked, WebAudioRoute.Classify("the machine's default output", ""));
        Assert.Equal(WebRouteOutcome.Default, WebAudioRoute.Classify("not routed: something", ""));
        Assert.Equal(WebRouteOutcome.Pending, WebAudioRoute.Classify("", "HDMI 3"));
        Assert.Equal(WebRouteOutcome.Pending, WebAudioRoute.Classify("nothing answered yet", "HDMI 3"));
        Assert.Equal(WebRouteOutcome.Routed, WebAudioRoute.Classify("routed to HDMI 3 (1 player)", "HDMI 3"));
        Assert.Equal(WebRouteOutcome.Failed, WebAudioRoute.Classify("not routed: no output called HDMI 3 among 2", "HDMI 3"));
        Assert.Equal(WebRouteOutcome.Failed, WebAudioRoute.Classify("the page's answer was not understood", "HDMI 3"));
        Assert.Equal(WebRouteOutcome.Failed, WebAudioRoute.Classify("the page could not be asked: gone", "HDMI 3"));

        Assert.True(WebAudioRoute.HoldSound(WebRouteOutcome.Pending));                                    // asked and not yet in force: held
        Assert.True(WebAudioRoute.HoldSound(WebRouteOutcome.Failed));                                     // asked and refused: held, never the default output
        Assert.False(WebAudioRoute.HoldSound(WebRouteOutcome.Routed));
        Assert.False(WebAudioRoute.HoldSound(WebRouteOutcome.NothingAsked));
        Assert.False(WebAudioRoute.HoldSound(WebRouteOutcome.Default));
        Assert.Equal("not routed: no output called HDMI 3 — sound held, not on the default output", WebAudioRoute.HeldWords("not routed: no output called HDMI 3"));
        Assert.Equal("sound held until routed", WebAudioRoute.HeldWords(""));
        Assert.True(WebAudioRoute.NeedsOutputNames("HDMI 3"));
        Assert.False(WebAudioRoute.NeedsOutputNames(""));                                                // no route wanted: the page does not get the outputs' names

        Assert.Equal("https://www.youtube.com", WebAudioRoute.OriginOf("https://www.youtube.com/watch?v=abc"));
        Assert.Equal("http://10.0.0.5:8080", WebAudioRoute.OriginOf("http://10.0.0.5:8080/board"));
        Assert.Equal("", WebAudioRoute.OriginOf("file:///C:/shows/page.html"));                          // no origin to grant anything to
        Assert.Equal("", WebAudioRoute.OriginOf("about:blank"));
        Assert.Equal("", WebAudioRoute.OriginOf("not an address"));
        Assert.Equal("", WebAudioRoute.OriginOf(null));
    }

    [Fact]
    public void TheAudioGraphHasADomainOfItsOwn()
    {
        Assert.True(SideEffectDomains.Touches(new HashSet<string> { nameof(ShowState.AudioRouting) }, SideEffectDomains.AudioGraph));
        Assert.True(SideEffectDomains.Touches(new HashSet<string> { nameof(ShowState.Monitor) }, SideEffectDomains.AudioGraph));
        Assert.False(SideEffectDomains.Touches(new HashSet<string> { nameof(ShowState.Pattern) }, SideEffectDomains.AudioGraph));
        Assert.False(SideEffectDomains.Touches(new HashSet<string> { nameof(ShowState.Countdown) }, SideEffectDomains.AudioGraph));
    }
}
