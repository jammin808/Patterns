using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.App.Views;
using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>Pure command-line/option builders for capture and web windows.</summary>
public class LaunchArgumentTests
{
    [Fact]
    public void CaptureOptionsNameTheDeviceAndStayLowLatency()
    {
        var opts = VlcFrameSource.CaptureOptions("Magewell USB Capture HDMI");
        Assert.Contains(":dshow-vdev=Magewell USB Capture HDMI", opts);
        Assert.Contains(":dshow-adev=none", opts);
        Assert.Contains(opts, o => o.StartsWith(":live-caching="));
    }

    [Fact]
    public void KioskArgsFillTheChosenScreen()
    {
        var args = WebService.BuildArgs("https://example.com/schedule", kiosk: true,
            x: 1920, y: 0, w: 1920, h: 1080, userDataDir: @"C:\tmp\web1", isEdge: true);

        Assert.Contains("--kiosk \"https://example.com/schedule\"", args);
        Assert.Contains("--edge-kiosk-type=fullscreen", args);
        Assert.Contains("--window-position=1920,0", args);
        Assert.Contains("--user-data-dir=\"C:\\tmp\\web1\"", args);
        Assert.DoesNotContain("--window-size", args); // kiosk sizes itself
    }

    [Fact]
    public void WindowedArgsUseAnAppWindowWithASize()
    {
        var args = WebService.BuildArgs("https://example.com", kiosk: false,
            x: 100, y: 50, w: 1280, h: 720, userDataDir: "/tmp/web", isEdge: false);

        Assert.Contains("--app=\"https://example.com\"", args);
        Assert.Contains("--window-size=1280,720", args);
        Assert.DoesNotContain("--kiosk", args);
        Assert.DoesNotContain("--edge-kiosk-type", args);
    }

    [Fact]
    public void UrlQuotesCannotEscapeTheArgument()
    {
        var args = WebService.BuildArgs("https://x/\" --evil", kiosk: true, 0, 0, 1, 1, "d", false);
        Assert.DoesNotContain("\" --evil", args); // no raw quote survives inside the URL argument
        Assert.Contains("%22", args);
    }

    [Theory]
    [InlineData("example.com/schedule", "https://example.com/schedule")]
    [InlineData("  example.com  ", "https://example.com")]
    [InlineData("http://plain.local", "http://plain.local")]
    [InlineData("https://x.y", "https://x.y")]
    [InlineData("", "")]
    public void BareAddressesBecomeHttps(string input, string expected)
        => Assert.Equal(expected, WebService.NormalizeUrl(input));

    [Fact]
    public void CaptureListNeverThrows()
    {
        var list = CaptureDevices.List();
        if (!OperatingSystem.IsWindows())
        {
            Assert.Empty(list); // no DirectShow here — empty, not an error
        }
    }
}

/// <summary>Playlist reorder, input pick lists and web targeting through the live view model.</summary>
public class InputsWebViewModelTests
{
    private static (AppServices Services, MainViewModel Vm, MainWindow Window) Boot()
    {
        var b = TestApp.Boot();
        return (b.Services, b.Vm, b.Window);
    }

    [AvaloniaFact]
    public void DragReorderMovesAndClamps()
    {
        var (services, vm, window) = Boot();
        try
        {
            var items = vm.ActivePlaylistSection.Items;
            items.Add(new PlaylistItemConfig { Path = "a.png" });
            items.Add(new PlaylistItemConfig { Path = "b.png" });
            items.Add(new PlaylistItemConfig { Path = "c.png" });

            vm.MovePlaylistItemTo(items[0], 2);                 // a → end
            Assert.Equal(new[] { "b.png", "c.png", "a.png" }, items.Select(i => i.Path));

            vm.MovePlaylistItemTo(items[2], 0);                 // a → front
            Assert.Equal(new[] { "a.png", "b.png", "c.png" }, items.Select(i => i.Path));

            vm.MovePlaylistItemTo(items[1], 99);                // clamps to last
            Assert.Equal(new[] { "a.png", "c.png", "b.png" }, items.Select(i => i.Path));

            vm.MovePlaylistItemTo(items[0], 0);                 // no-op in place
            Assert.Equal("a.png", items[0].Path);

            vm.SelectedPlaylistItem = items[1];
            Assert.True(vm.HasPlaylistItemSelection);
            vm.SelectedPlaylistItem = null;
            Assert.False(vm.HasPlaylistItemSelection);
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void RefreshingInputListsNeverClearsTheModel()
    {
        var (services, vm, window) = Boot();
        try
        {
            // No NDI runtime / no Windows here — the stored names must survive a refresh
            // (the combo rebuild would otherwise null them, like the font-combo bug).
            vm.ActivePattern.Media.NdiSourceName = "TX1 (Programme)";
            vm.RefreshNdiSourcesCommand.Execute(null);
            Assert.Equal("TX1 (Programme)", vm.ActivePattern.Media.NdiSourceName);
            Assert.Contains("TX1 (Programme)", vm.NdiSourceOptions);

            vm.ActivePattern.Media.CaptureDevice = "DeckLink Mini Recorder";
            vm.RefreshCaptureDevicesCommand.Execute(null);
            Assert.Equal("DeckLink Mini Recorder", vm.ActivePattern.Media.CaptureDevice);
            Assert.Contains("DeckLink Mini Recorder", vm.CaptureDeviceOptions);
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void NdiFeedWithoutRuntimeExplainsItself()
    {
        var (services, vm, window) = Boot();
        try
        {
            vm.ActivePattern.Kind = PatternKind.Media;
            vm.ActivePattern.Media.Source = MediaSource.NdiFeed;
            vm.ActivePattern.Media.NdiSourceName = "TX1 (Programme)";
            Dispatcher.UIThread.RunJobs();

            if (!Patterns.Core.Ndi.NdiInterop.Available)
            {
                Assert.Null(InputBus.For(InputKeys.Ndi("TX1 (Programme)")));
                Assert.Contains("runtime", NdiInput.AvailabilityNote, StringComparison.OrdinalIgnoreCase);
            }
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }

    [AvaloniaFact]
    public void WebScreensListTargetsAndKeepsTheChoice()
    {
        var (services, vm, window) = Boot();
        try
        {
            Assert.Contains(vm.WebScreens, t => t.ScreenId == "");
            vm.State.Web.TargetScreenId = "some-screen";
            vm.ReconcilePlacements(); // triggers RebuildWebScreens
            Assert.Equal("some-screen", vm.State.Web.TargetScreenId);

            vm.State.Web.Url = "example.com";
            vm.State.Web.SavedUrls.Add("https://kept.example");
            vm.RemoveWebUrlCommand.Execute("https://kept.example");
            Assert.Empty(vm.State.Web.SavedUrls);

            vm.State.Web.SavedUrls.Add("https://again.example");
            vm.LoadWebUrlCommand.Execute("https://again.example");
            Assert.Equal("https://again.example", vm.State.Web.Url);
        }
        finally
        {
            window.Close();
            services.Shutdown();
        }
    }
}
