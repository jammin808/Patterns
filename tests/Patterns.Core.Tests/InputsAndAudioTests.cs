using System.Runtime.InteropServices;
using Patterns.Core.Model;
using Patterns.Core.Ndi;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>NDI receive ABI pins — struct sizes must match the native SDK on x64.</summary>
public class NdiReceiveAbiTests
{
    [Fact]
    public void FindCreateMatchesNativeLayout()
        => Assert.Equal(24, Marshal.SizeOf<NdiInterop.FindCreate>());

    [Fact]
    public void SourceMatchesNativeLayout()
        => Assert.Equal(16, Marshal.SizeOf<NdiInterop.Source>());

    [Fact]
    public void RecvCreateV3MatchesNativeLayout()
        => Assert.Equal(40, Marshal.SizeOf<NdiInterop.RecvCreateV3>());

    [Fact]
    public void FinderIsSafeWithoutTheRuntime()
    {
        // On machines without the NDI runtime discovery must degrade to an empty list.
        using var finder = new NdiFinder();
        if (!NdiInterop.Available)
        {
            Assert.Empty(finder.CurrentSources());
        }
    }
}

public class AudioDefaultsTests
{
    [Fact]
    public void SoundIsOnByDefault()
    {
        var m = new MediaOptions();
        Assert.False(m.Mute); // muted-by-default was the "no audio" field report
        Assert.Equal(100, m.VolumePct);
    }

    [Fact]
    public void VolumeClampsToVlcRange()
    {
        var m = new MediaOptions();
        m.VolumePct = 500;
        Assert.Equal(125, m.VolumePct);
        m.VolumePct = -10;
        Assert.Equal(0, m.VolumePct);
    }

    [Fact]
    public void OldSettingsFilesLoseTheSilentMuteDefault()
    {
        // A v0/v1 file wrote Mute=true from the old default; migration resets it so the
        // fix reaches existing installs. A file already at the current version is left alone.
        var old = new ShowState { SchemaVersion = 0 };
        old.Pattern.Media.Mute = true;
        old.Independent.Add(new OutputAssignment { ScreenId = "a" });
        old.Independent[0].Pattern.Media.Mute = true;

        SettingsStore.Migrate(old);
        Assert.False(old.Pattern.Media.Mute);
        Assert.False(old.Independent[0].Pattern.Media.Mute);
        Assert.Equal(ShowState.CurrentSchemaVersion, old.SchemaVersion);

        var current = new ShowState { SchemaVersion = ShowState.CurrentSchemaVersion };
        current.Pattern.Media.Mute = true; // deliberate operator choice
        SettingsStore.Migrate(current);
        Assert.True(current.Pattern.Media.Mute);
    }

    [Theory]
    [InlineData("track.mp3", true)]
    [InlineData("TRACK.WAV", true)]
    [InlineData("stems.flac", true)]
    [InlineData("movie.mp4", false)]
    [InlineData("still.png", false)]
    public void ClassifiesAudioPaths(string path, bool audio)
        => Assert.Equal(audio, PlaylistSequencer.IsAudioPath(path));

    [Fact]
    public void AudioFilesJoinThePlaylistAsDecodedMedia()
    {
        var o = new PlaylistOptions();
        o.Items.Add(new PlaylistItemConfig { Path = "walkin.mp3" });
        o.Items.Add(new PlaylistItemConfig { Path = "still.png" });

        var order = PlaylistSequencer.BuildOrder(o, Array.Empty<string>(), videoPlaybackAvailable: true);
        Assert.Equal(2, order.Count);
        Assert.True(order[0].IsVideo);  // decoder-bound: plays to its natural end
        Assert.False(order[1].IsVideo);

        // No decoder → audio drops with the videos rather than jamming the cycle.
        var withoutVlc = PlaylistSequencer.BuildOrder(o, Array.Empty<string>(), videoPlaybackAvailable: false);
        Assert.Equal(new[] { "still.png" }, withoutVlc.Select(e => e.Path));
    }
}

public class MediaLocatorInputTests
{
    private static ShowState MediaState(Action<MediaOptions> setup)
    {
        var state = new ShowState();
        state.Pattern.Kind = PatternKind.Media;
        setup(state.Pattern.Media);
        return state;
    }

    private static ShowSnapshot Snap(ShowState s) => new() { State = s, Version = 1 };

    [Fact]
    public void CaptureDeviceFlowsThroughTheDecoderPath()
    {
        var state = MediaState(m =>
        {
            m.Source = MediaSource.Capture;
            m.CaptureDevice = "Elgato HD60 X";
            m.Mute = true;
            m.VolumePct = 80;
        });

        var active = MediaLocator.FindActiveVideo(Snap(state));
        Assert.NotNull(active);
        Assert.True(active!.Value.IsCapture);
        Assert.Equal("Elgato HD60 X", active.Value.Target);
        Assert.True(active.Value.Mute);
        Assert.Equal(80, active.Value.VolumePct);
    }

    [Fact]
    public void FileVideoCarriesLoopAndVolume()
    {
        var state = MediaState(m =>
        {
            m.Source = MediaSource.Video;
            m.VideoPath = @"D:\show\loop.mp4";
            m.Loop = true;
            m.VolumePct = 110;
        });

        var active = MediaLocator.FindActiveVideo(Snap(state));
        Assert.NotNull(active);
        Assert.False(active!.Value.IsCapture);
        Assert.True(active.Value.Loop);
        Assert.Equal(110, active.Value.VolumePct);
    }

    [Fact]
    public void NdiSourceIsLocatedFromTheActivePattern()
    {
        var state = MediaState(m =>
        {
            m.Source = MediaSource.NdiFeed;
            m.NdiSourceName = "STAGE-PC (Cameras)";
        });
        Assert.Equal("STAGE-PC (Cameras)", MediaLocator.FindActiveNdiSource(state));

        state.Pattern.Media.Source = MediaSource.Image;
        Assert.Equal("", MediaLocator.FindActiveNdiSource(state));
    }
}

public class InputRenderSmokeTests
{
    [Fact]
    public void AudioFileRendersACardNotAWait()
    {
        var state = new ShowState();
        state.Pattern.Kind = PatternKind.Media;
        state.Pattern.Media.Source = MediaSource.Video;
        state.Pattern.Media.VideoPath = @"D:\audio\walkin.mp3";

        using var bmp = RenderTestHarness.Render(state, 640, 360);
        // The audio card paints its panel in the middle of the frame.
        var centre = bmp.GetPixel(320, 180);
        Assert.NotEqual(0xFF000000, (uint)centre);
    }

    [Fact]
    public void NdiFeedWithoutReceiverRendersAPlaceholder()
    {
        var state = new ShowState();
        state.Pattern.Kind = PatternKind.Media;
        state.Pattern.Media.Source = MediaSource.NdiFeed;
        state.Pattern.Media.NdiSourceName = "STAGE (Cam 1)";

        using var bmp = RenderTestHarness.Render(state, 640, 360);
        Assert.NotEqual(0xFF000000, (uint)bmp.GetPixel(320, 180));
    }

    [Fact]
    public void CaptureWithoutDeviceRendersAPlaceholder()
    {
        var state = new ShowState();
        state.Pattern.Kind = PatternKind.Media;
        state.Pattern.Media.Source = MediaSource.Capture;

        using var bmp = RenderTestHarness.Render(state, 640, 360);
        Assert.NotEqual(0xFF000000, (uint)bmp.GetPixel(320, 180));
    }
}
