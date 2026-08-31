using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>A frame source that paints one solid colour — a stand-in for a live input.</summary>
public sealed class SolidSource : IVideoFrameSource
{
    private readonly SKColor _color;
    public SolidSource(SKColor color) => _color = color;

    public bool DrawFrame(SKCanvas canvas, SKRect dest, SKPaint? paint)
    {
        canvas.DrawRect(dest, new SKPaint { Color = _color });
        return true;
    }

    public SKSizeI? FrameSize => new SKSizeI(320, 180);
    public bool IsPlaying => true;
    public bool IsEnded => false;
    public double DurationSeconds => 0;
    public string StatusText => "solid";
}

[Collection("InputBus")]
public class InputBusTests
{
    [Fact]
    public void MountResolveUnmountRoundTrip()
    {
        InputBus.Clear();
        var a = new SolidSource(SKColors.Red);
        InputBus.Mount("ndi:CamA", a);
        Assert.Same(a, InputBus.For("ndi:CamA"));
        Assert.Null(InputBus.For("ndi:CamB"));
        Assert.Null(InputBus.For(""));

        InputBus.Unmount("ndi:CamA");
        Assert.Null(InputBus.For("ndi:CamA"));
        InputBus.Clear();
    }

    [Fact]
    public void FadeSourcePrefersTheRetiredMountPerKey()
    {
        InputBus.Clear();
        var oldSource = new SolidSource(SKColors.Red);
        var newSource = new SolidSource(SKColors.Blue);
        InputBus.Mount("vid:a.mp4", newSource);
        InputBus.SetPrevious("vid:a.mp4", oldSource);

        Assert.Same(newSource, InputBus.Resolve("vid:a.mp4", isFadeSource: false));
        Assert.Same(oldSource, InputBus.Resolve("vid:a.mp4", isFadeSource: true));

        InputBus.SetPrevious("vid:a.mp4", null);
        Assert.Same(newSource, InputBus.Resolve("vid:a.mp4", isFadeSource: true));
        InputBus.Clear();
    }

    [Fact]
    public void KeysCarryTheOperatorLabelScheme()
    {
        Assert.Equal("ndi:CAM 1", InputKeys.Ndi("CAM 1"));
        Assert.Equal("cap:USB Video", InputKeys.Capture("USB Video"));
        Assert.Equal("vid:C:\\walkin.mp4", InputKeys.Video("C:\\walkin.mp4"));
        Assert.Equal("", InputKeys.Ndi(""));
    }
}

[Collection("InputBus")]
public class WantedInputsTests
{
    private static ShowState State()
    {
        var s = new ShowState();
        s.Pattern.Kind = PatternKind.Media;
        return s;
    }

    private static void AddScreen(ShowState s, string id, Action<PatternConfig> configure)
    {
        s.Output.Placements.Add(new ScreenPlacement { ScreenId = id, Enabled = true, UseCustomPattern = true });
        var a = new OutputAssignment { ScreenId = id };
        configure(a.Pattern);
        s.Independent.Add(a);
    }

    [Fact]
    public void EveryReferencedInputIsWantedProgramFirst()
    {
        var s = State();
        s.Pattern.Media.Source = MediaSource.Video;
        s.Pattern.Media.VideoPath = "/shows/walkin.mp4";
        s.Pattern.Media.Loop = true;

        AddScreen(s, "s2", p =>
        {
            p.Kind = PatternKind.Media;
            p.Media.Source = MediaSource.NdiFeed;
            p.Media.NdiSourceName = "CAM 1";
        });
        AddScreen(s, "s3", p =>
        {
            p.Kind = PatternKind.Media;
            p.Media.Source = MediaSource.Capture;
            p.Media.CaptureDevice = "ATEM out";
        });

        var wanted = MediaLocator.FindWantedInputs(RenderTestHarness.Snap(s));
        Assert.Equal(new[] { "vid:/shows/walkin.mp4", "ndi:CAM 1", "cap:ATEM out" }, wanted.Select(w => w.Key));
        Assert.True(wanted[0].Loop);
        Assert.Equal(MediaLocator.WantedKind.Ndi, wanted[1].Kind);
        Assert.Equal(MediaLocator.WantedKind.Capture, wanted[2].Kind);
    }

    [Fact]
    public void SharedSourcesDedupeWithProgramSettingsWinning()
    {
        var s = State();
        s.Pattern.Media.Source = MediaSource.NdiFeed;
        s.Pattern.Media.NdiSourceName = "CAM 1";
        AddScreen(s, "s2", p =>
        {
            p.Kind = PatternKind.Media;
            p.Media.Source = MediaSource.NdiFeed;
            p.Media.NdiSourceName = "CAM 1"; // same feed on a second surface
        });

        var wanted = MediaLocator.FindWantedInputs(RenderTestHarness.Snap(s));
        Assert.Single(wanted);
        Assert.Equal("ndi:CAM 1", wanted[0].Key);
    }

    [Fact]
    public void PipAndMultiviewTilesJoinTheWantedSet()
    {
        var s = new ShowState();
        s.Pattern.Kind = PatternKind.Multiview;
        s.Pattern.Multiview.Tiles.Add(new MultiviewTileConfig { Source = MultiviewSource.NdiFeed, Input = "CAM 2" });
        s.Pattern.Multiview.Tiles.Add(new MultiviewTileConfig { Source = MultiviewSource.Capture, Input = "USB card" });
        s.Overlays.Pip.Enabled = true;
        s.Overlays.Pip.Source = PipSource.NdiFeed;
        s.Overlays.Pip.NdiSourceName = "CAM 3";

        var keys = MediaLocator.FindWantedInputs(RenderTestHarness.Snap(s)).Select(w => w.Key).ToList();
        Assert.Contains("ndi:CAM 2", keys);
        Assert.Contains("cap:USB card", keys);
        Assert.Contains("ndi:CAM 3", keys);
    }

    [Fact]
    public void OnlyTheActivePlaylistsNowItemMounts()
    {
        var s = State();
        s.Pattern.Media.Source = MediaSource.Playlist; // program playlist = the active one
        AddScreen(s, "s2", p =>
        {
            p.Kind = PatternKind.Media;
            p.Media.Source = MediaSource.Playlist; // a second playlist config — not active
        });

        var snap = new ShowSnapshot
        {
            State = s,
            Version = 1,
            PlaylistNow = new PlaylistNow("/shows/loop.mp4", true, 0, 3, RenderTestHarness.FixedUtcNow, 0),
        };

        var wanted = MediaLocator.FindWantedInputs(snap);
        Assert.Single(wanted);
        Assert.Equal("vid:/shows/loop.mp4", wanted[0].Key);
    }

    [Fact]
    public void DisabledScreensAndEmptyNamesAreIgnored()
    {
        var s = State();
        s.Pattern.Media.Source = MediaSource.NdiFeed;
        s.Pattern.Media.NdiSourceName = ""; // nothing chosen yet
        AddScreen(s, "s2", p =>
        {
            p.Kind = PatternKind.Media;
            p.Media.Source = MediaSource.Capture;
            p.Media.CaptureDevice = "Card";
        });
        s.Output.Placements[0].Enabled = false;

        Assert.Empty(MediaLocator.FindWantedInputs(RenderTestHarness.Snap(s)));
    }
}

[Collection("InputBus")]
public class InputDistributionRenderTests
{
    /// <summary>
    /// The whole point of the pool: two screens carry two different live inputs at the same
    /// time, each rendering its own mounted frames.
    /// </summary>
    [Fact]
    public void TwoScreensRenderTwoDifferentInputsSimultaneously()
    {
        InputBus.Clear();
        try
        {
            InputBus.Mount("ndi:CamA", new SolidSource(new SKColor(255, 0, 0)));
            InputBus.Mount("cap:CardB", new SolidSource(new SKColor(0, 0, 255)));

            var s = new ShowState();
            s.Output.Placements.Add(new ScreenPlacement { ScreenId = "a", Enabled = true, UseCustomPattern = true });
            s.Output.Placements.Add(new ScreenPlacement { ScreenId = "b", Enabled = true, UseCustomPattern = true });
            var pa = new OutputAssignment { ScreenId = "a" };
            pa.Pattern.Kind = PatternKind.Media;
            pa.Pattern.Media.Source = MediaSource.NdiFeed;
            pa.Pattern.Media.NdiSourceName = "CamA";
            pa.Pattern.Media.Fit = FitMode.Stretch;
            var pb = new OutputAssignment { ScreenId = "b" };
            pb.Pattern.Kind = PatternKind.Media;
            pb.Pattern.Media.Source = MediaSource.Capture;
            pb.Pattern.Media.CaptureDevice = "CardB";
            pb.Pattern.Media.Fit = FitMode.Stretch;
            s.Independent.Add(pa);
            s.Independent.Add(pb);

            using var screenA = RenderTestHarness.Render(s, 200, 120, screenId: "a");
            using var screenB = RenderTestHarness.Render(s, 200, 120, screenId: "b");

            Assert.Equal(new SKColor(255, 0, 0, 255), screenA.GetPixel(100, 60));
            Assert.Equal(new SKColor(0, 0, 255, 255), screenB.GetPixel(100, 60));
        }
        finally
        {
            InputBus.Clear();
        }
    }

    [Fact]
    public void MultiviewTilePicksItsOwnNamedInput()
    {
        InputBus.Clear();
        try
        {
            InputBus.Mount("ndi:CamA", new SolidSource(new SKColor(0, 255, 0)));

            var s = new ShowState();
            s.Pattern.Kind = PatternKind.Multiview;
            s.Pattern.Multiview.ShowLabels = false;
            s.Pattern.Multiview.ShowTally = false;
            s.Pattern.Multiview.Columns = 1;
            s.Pattern.Multiview.Tiles.Add(new MultiviewTileConfig { Source = MultiviewSource.NdiFeed, Input = "CamA" });

            using var bmp = RenderTestHarness.Render(s, 320, 180);
            Assert.Equal(new SKColor(0, 255, 0, 255), bmp.GetPixel(160, 90));
        }
        finally
        {
            InputBus.Clear();
        }
    }
}
