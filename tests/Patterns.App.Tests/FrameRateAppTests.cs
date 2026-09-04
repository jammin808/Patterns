using Avalonia;
using Avalonia.Headless.XUnit;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>Frame pacing on the viewports, capture modes through the decoder path, and the Format picker.</summary>
public class FrameRateAppTests
{
    private static readonly DateTime T0 = new(2026, 9, 4, 23, 0, 0, DateTimeKind.Utc);

    private static ScreenInfo Info(string id, int x, int w = 1920, int h = 1080)
        => new(id, id, new PixelRect(x, 0, w, h), 1.0, false, 0);

    [Fact]
    public void OutputsPaceToTheMasterUnlessAScreenOverridesIt()
    {
        var screens = new List<ScreenInfo> { Info("a", 0), Info("b", 2200) };
        var placements = new[]
        {
            new ScreenPlacement { ScreenId = "a", X = 0 },
            new ScreenPlacement { ScreenId = "b", X = 2200, FpsOverride = 60 },
        };

        var paced = OutputWindowManager.BuildViewports(placements, screens, masterFps: 30);
        Assert.Equal(30, paced.First(x => x.Screen.Id == "a").Viewport.TargetFps);
        Assert.Equal(60, paced.First(x => x.Screen.Id == "b").Viewport.TargetFps);

        var free = OutputWindowManager.BuildViewports(placements, screens);
        Assert.Equal(0, free.First(x => x.Screen.Id == "a").Viewport.TargetFps);   // unlimited: the display's refresh
        Assert.Equal(60, free.First(x => x.Screen.Id == "b").Viewport.TargetFps);  // the screen's own rate still wins
    }

    [Fact]
    public void CaptureOptionsAskForAModeOnlyWhenOneIsChosen()
    {
        var plain = VlcFrameSource.CaptureOptions("Cam Link 4K");
        Assert.Contains(":dshow-vdev=Cam Link 4K", plain);
        Assert.DoesNotContain(plain, o => o.StartsWith(":dshow-size"));
        Assert.DoesNotContain(plain, o => o.StartsWith(":dshow-fps"));

        var chosen = VlcFrameSource.CaptureOptions("Cam Link 4K", "1920x1080@59.94");
        Assert.Contains(":dshow-size=1920x1080", chosen);
        Assert.Contains(":dshow-fps=59.94", chosen);

        var junk = VlcFrameSource.CaptureOptions("Cam Link 4K", "wide@fast");
        Assert.Equal(plain, junk); // an unreadable mode leaves the device's default
    }

    private static ShowSnapshot CaptureSnap(string device, string format, long version)
    {
        var s = new ShowState();
        s.Pattern.Kind = PatternKind.Media;
        s.Pattern.Media.Source = MediaSource.Capture;
        s.Pattern.Media.CaptureDevice = device;
        s.SetCaptureFormat(device, format);
        return new ShowSnapshot { State = s, Version = version };
    }

    [AvaloniaFact]
    public void AModeChangeReopensTheDecoderAndNothingElseDoes()
    {
        InputBus.Clear();
        var opened = new List<FakeSource>();
        using var engine = new VideoEngine
        {
            SourceFactory = w =>
            {
                var f = new FakeSource(w);
                opened.Add(f);
                return f;
            },
        };
        try
        {
            engine.Reconcile(CaptureSnap("Cam Link 4K", "", 1), null, T0);
            Assert.Single(opened);
            Assert.Equal("", opened[0].Wanted.Format);

            // The same device, the same default: the decoder stays.
            engine.Reconcile(CaptureSnap("Cam Link 4K", "", 2), null, T0.AddSeconds(1));
            Assert.Single(opened);

            // A chosen mode: the old decoder retires, a fresh one opens with the mode.
            engine.Reconcile(CaptureSnap("Cam Link 4K", "1920x1080@60", 3), null, T0.AddSeconds(2));
            Assert.Equal(2, opened.Count);
            Assert.True(opened[0].FadeMs >= 0, "the old decoder was retired");
            Assert.Equal("1920x1080@60", opened[1].Wanted.Format);
            Assert.Same(opened[1], InputBus.For(InputKeys.Capture("Cam Link 4K")));
        }
        finally
        {
            InputBus.Clear();
        }
    }

    [AvaloniaFact]
    public void TheFormatPickerListsTheCardsModesAndStoresTheChoicePerDevice()
    {
        var state = new ShowState();
        var device = "Cam Link 4K";
        var changes = 0;
        var picker = new CaptureFormatPicker(() => state, () => device, () => changes++)
        {
            Probe = d => d == "Cam Link 4K"
                ? new[] { new CaptureFormat(3840, 2160, 30), new CaptureFormat(1920, 1080, 60), new CaptureFormat(1920, 1080, 59.94) }
                : Array.Empty<CaptureFormat>(),
        };
        picker.Refresh();
        Assert.Equal(new[] { CaptureFormatPicker.DefaultLabel, "3840×2160 @ 30", "1920×1080 @ 60", "1920×1080 @ 59.94" }, picker.Options);
        Assert.Equal(CaptureFormatPicker.DefaultLabel, picker.Selected);

        picker.Selected = "1920×1080 @ 60";
        Assert.Equal("1920x1080@60", state.CaptureFormatFor(device));
        Assert.Equal(1, changes);
        picker.Selected = "1920×1080 @ 60";   // the same again is not a change
        Assert.Equal(1, changes);

        picker.Selected = CaptureFormatPicker.DefaultLabel;
        Assert.Equal("", state.CaptureFormatFor(device));
        Assert.Equal(2, changes);

        // A mode saved on the desk stays offered when the card is not here to list it.
        state.SetCaptureFormat("Podium cam", "1280x720@50");
        device = "Podium cam";
        picker.Refresh();
        Assert.Equal(new[] { CaptureFormatPicker.DefaultLabel, "1280×720 @ 50 (saved)" }, picker.Options);
        Assert.Equal("1280×720 @ 50 (saved)", picker.Selected);

        // No device: only the default, and nothing is stored.
        device = "";
        picker.Refresh();
        Assert.Equal(new[] { CaptureFormatPicker.DefaultLabel }, picker.Options);
        picker.Selected = CaptureFormatPicker.DefaultLabel;
        Assert.Equal(2, changes);
    }

    [AvaloniaFact]
    public void DisplayModesAreHonestOffWindowsAndTheScreensPageSaysSo()
    {
        var b = TestApp.Boot();
        try
        {
            Assert.Equal(OperatingSystem.IsWindows(), DisplayModes.Supported);
            if (!OperatingSystem.IsWindows())
            {
                Assert.Empty(DisplayModes.List("\\\\.\\DISPLAY1"));
                Assert.Null(DisplayModes.Current("\\\\.\\DISPLAY1"));
                Assert.Contains("Windows", DisplayModes.Apply("\\\\.\\DISPLAY1", new DisplayMode(1920, 1080, 60)));
            }
            Assert.Equal("1920x1080@60", new DisplayMode(1920, 1080, 60).Key);
            Assert.Equal("1920×1080 @ 60 Hz", new DisplayMode(1920, 1080, 60).Label);

            b.Vm.MasterFps = 30;
            Assert.Equal(30, b.Vm.State.Output.MasterFps);
            Assert.Equal(30, b.Services.Metrics.BuildContext(new MetricSample()).TargetFps);
        }
        finally
        {
            b.Dispose();
        }
    }
}
