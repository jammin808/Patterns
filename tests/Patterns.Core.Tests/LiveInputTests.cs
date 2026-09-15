using Patterns.Core.Media;
using Patterns.Rendering.Media;
using Patterns.Core.Model;
using Patterns.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The live input age — a camera's or a feed's picture from its arrival in the decoder to the end
/// of the frame that drew it — and the low-latency capture profile stored beside a device's mode.
/// </summary>
public class LiveInputTests
{
    [Fact]
    public void TheFrameKeepsTheOldestLivePictureItDrewAndForgetsItAtTheNextFrame()
    {
        var stages = new FrameStages();
        stages.Begin();
        Assert.Equal(-1, stages.LiveFrameClock);
        stages.NoteLive(new DrawnFrame(true, 12.5, true, 7));
        stages.NoteLive(new DrawnFrame(true, 12.2, true, 6));                                            // older: the one the room waited longest for
        stages.NoteLive(new DrawnFrame(true, 11.0, false));                                              // a file: its age is not latency
        stages.NoteLive(new DrawnFrame(true, -1, true));                                                 // untimed: nothing to say
        stages.NoteLive(new DrawnFrame(false, 9.0, true));                                               // not drawn: nothing on the glass
        Assert.Equal(12.2, stages.LiveFrameClock);
        Assert.Equal(6, stages.LiveFrameGeneration);
        stages.NoteLive(12.9);
        Assert.Equal(12.2, stages.LiveFrameClock);
        stages.Begin();
        Assert.Equal(-1, stages.LiveFrameClock);
        Assert.Equal(0, stages.LiveFrameGeneration);

        // The default draw of a source that says nothing reports a frame that is not live and untimed.
        using var canvas = new SKCanvas(new SKBitmap(4, 4));
        var plain = ((IVideoFrameSource)new NoWords()).Draw(canvas, SKRect.Create(4, 4), null, FrameCrop.None);
        Assert.False(plain.Drew);
        Assert.False(plain.IsLive);
        Assert.Equal(-1, plain.FrameClock);
        var timed = ((IVideoFrameSource)new FakeLive(3.25, live: true)).Draw(canvas, SKRect.Create(4, 4), null, FrameCrop.None);
        Assert.Equal(3.25, timed.FrameClock);                                                            // a fake with a fixed clock: the default reports it

        // A frame slot times what it is handed, on the show clock.
        using var slot = new FrameSlot();
        Assert.Equal(-1, slot.PublishedClock);
        using var bmp = new SKBitmap(2, 2);
        var before = ShowClock.Seconds;
        slot.Publish(SKImage.FromBitmap(bmp));
        Assert.InRange(slot.PublishedClock, before, ShowClock.Seconds);
        var drawnSlot = slot.DrawTimed(canvas, SKRect.Create(4, 4), null, FrameCrop.None);
        Assert.True(drawnSlot.Drew);
        Assert.Equal(slot.PublishedClock, drawnSlot.FrameClock);                                         // the clock travels with the frame drawn
        Assert.False(((IVideoFrameSource)new NoWords()).IsLive);                                        // the contract's defaults: a source that says nothing is not live and untimed
        Assert.Equal(-1, ((IVideoFrameSource)new NoWords()).FrameClock);
    }

    [Fact]
    public void TheBudgetReadsTheWorstLiveAgeOfTheMinuteAndTheWordsSayIt()
    {
        var b = new FrameBudget(SinkKind.Output, 1, "Main");
        b.Record(4, "", 100.2);
        Assert.Equal(-1, b.Read(100.5).LiveAgeMs);
        Assert.DoesNotContain("live", Glance.SinkWords(b.Read(100.5)));
        b.RecordLiveAge(33, 100.2, generation: 184, arrivalClock: 100.167);
        b.RecordLiveAge(21, 100.7);
        var r = b.Read(100.9);
        Assert.Equal(33, r.LiveAgeMs);
        Assert.Equal(33, b.WorstLiveAgeMs);
        Assert.Contains("live input age worst 33 ms", r.Words);
        Assert.NotNull(b.LastLive);
        Assert.Equal(21, b.LastLive!.Value.AgeMs);                                                       // the last one drawn, named for the diagnostics
        Assert.Equal(100.7, b.LastLive!.Value.DrawnClock);
        Assert.Contains("last arrived 100.679 s, drawn 100.700 s: 21 ms", r.LiveLast);
        b.RecordLiveAge(33, 100.8, generation: 184, arrivalClock: 100.767);
        Assert.Contains("last frame 184 arrived 100.767 s, drawn 100.800 s: 33 ms", b.Read(100.9).Words);
        Assert.Contains("· live 33 ms", Glance.SinkWords(r));
        Assert.Contains("live input age worst 33 ms", FrameBudgets.Describe(new[] { r }));
        Assert.Equal(-1, b.Read(200).LiveAgeMs);                                                       // a minute on: gone from the window
        Assert.Equal(33, b.WorstLiveAgeMs);                                                            // the session's worst stays
        b.RecordLiveAge(-5, 200);                                                                       // never negative
        Assert.Equal(0, b.Read(200).LiveAgeMs);
        b.Reset();
        Assert.Equal(-1, b.WorstLiveAgeMs);
        Assert.Null(b.LastLive);
        Assert.Equal(-1, b.Read(200).LiveAgeMs);
    }

    [Fact]
    public void TheSampleTheCsvAndTheCheckCarryTheLiveAge()
    {
        Assert.EndsWith(",managedMB,liveAgeWorstMs,retiringMB,poolStarved", MetricsCsv.Header);
        Assert.EndsWith(",,33,,0", MetricsCsv.Line(new MetricSample { Utc = DateTime.UnixEpoch, LiveAgeWorstMs = 33 }));
        Assert.EndsWith(",,,,0", MetricsCsv.Line(new MetricSample { Utc = DateTime.UnixEpoch }));     // none drawn: the column is empty, never a nought
        var agg = MetricsHistory.Aggregate(new[]
        {
            new MetricSample { Utc = DateTime.UnixEpoch, LiveAgeWorstMs = 21 },
            new MetricSample { Utc = DateTime.UnixEpoch.AddSeconds(1), LiveAgeWorstMs = 47 },
            new MetricSample { Utc = DateTime.UnixEpoch.AddSeconds(2) },
        });
        Assert.Equal(47, agg.LiveAgeWorstMs);                                                          // the worst of the window

        static CheckRow Row(double age) => SuperCheck.Run(new CheckFacts { LiveAgeWorstMs = age }).Rows.Single(r => r.Item == "Live input");
        Assert.Equal(CheckLight.Green, Row(33).Light);
        Assert.Equal(CheckLight.Green, Row(FrameBudget.LiveAgeGoodMs).Light);
        Assert.Equal(CheckLight.Amber, Row(41).Light);
        Assert.Equal(CheckLight.Red, Row(81).Light);
        Assert.Contains("decoder to frame", Row(33).Value);
        Assert.Contains("not in it", Row(33).Note);                                                    // the honest note: the card and the screen are not measured
        Assert.Contains("Low latency (IMAG)", Row(81).Note);
        Assert.DoesNotContain(SuperCheck.Run(new CheckFacts()).Rows, r => r.Item == "Live input");     // no live picture drawn: no row
    }

    [Fact]
    public void TheLowLatencyProfileIsStoredPerDeviceBesideTheModeAndTravelsWithTheWantedInput()
    {
        var s = new ShowState();
        Assert.False(s.CaptureLowLatencyFor("Cam"));
        s.SetCaptureLowLatency("Cam", true);
        Assert.True(s.CaptureLowLatencyFor("Cam"));
        Assert.True(s.CaptureLowLatencyFor("cam"));                                                    // the device name, as everywhere: case does not matter
        Assert.Equal("", s.CaptureFormatFor("Cam"));
        Assert.Single(s.CaptureFormats);
        s.SetCaptureFormat("Cam", "1920x1080@60");
        Assert.Single(s.CaptureFormats);                                                               // the mode and the profile share the entry
        Assert.True(s.CaptureLowLatencyFor("Cam"));
        s.SetCaptureFormat("Cam", "");                                                                 // the mode cleared: the profile keeps the entry
        Assert.True(s.CaptureLowLatencyFor("Cam"));
        Assert.Single(s.CaptureFormats);
        s.SetCaptureLowLatency("Cam", false);                                                          // neither: the entry goes
        Assert.Empty(s.CaptureFormats);
        s.SetCaptureLowLatency("", true);                                                              // no device: nothing
        Assert.Empty(s.CaptureFormats);
        s.SetCaptureFormat("Cam", "1280x720@50");
        s.SetCaptureLowLatency("Cam", false);                                                          // off with a mode kept: the entry stays for the mode
        Assert.Equal("1280x720@50", s.CaptureFormatFor("Cam"));
        Assert.Single(s.CaptureFormats);

        s.Pattern.Kind = PatternKind.Media;
        s.Pattern.Media.Source = MediaSource.Capture;
        s.Pattern.Media.CaptureDevice = "Cam";
        var plain = MediaLocator.FindWantedInputs(new ShowSnapshot { State = s, Version = 1 }).Single(w => w.Kind == MediaLocator.WantedKind.Capture);
        Assert.False(plain.LowLatency);
        Assert.Equal("1280x720@50", plain.Format);
        s.SetCaptureLowLatency("Cam", true);
        var fast = MediaLocator.FindWantedInputs(new ShowSnapshot { State = s, Version = 2 }).Single(w => w.Kind == MediaLocator.WantedKind.Capture);
        Assert.True(fast.LowLatency);
        Assert.Equal(InputKeys.Capture("Cam"), fast.Key);
    }

    private sealed class FakeLive(double clock, bool live) : IVideoFrameSource
    {
        public bool DrawFrame(SKCanvas canvas, SKRect dest, SKPaint? paint) => false;
        public SKSizeI? FrameSize => null;
        public bool IsPlaying => true;
        public bool IsEnded => false;
        public double DurationSeconds => 0;
        public string StatusText => "";
        public double FrameClock => clock;
        public bool IsLive => live;
    }

    private sealed class NoWords : IVideoFrameSource
    {
        public bool DrawFrame(SKCanvas canvas, SKRect dest, SKPaint? paint) => false;
        public SKSizeI? FrameSize => null;
        public bool IsPlaying => false;
        public bool IsEnded => false;
        public double DurationSeconds => 0;
        public string StatusText => "";
    }
}
