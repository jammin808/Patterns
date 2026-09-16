using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Patterns.Rendering.Media;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 68.6 on the desk: a YouTube page whose look asks for the native player has its stream found
/// by the tool (a stand-in here), the clip engine opens that stream under the page's own key with the
/// sound as its slave, the browser is never opened, STATE says who plays it; without the tool the page
/// stays in the browser and the words say why.
/// </summary>
public class WebNativeAppTests
{
    private const string Tube = "https://www.youtube.com/watch?v=abc";

    private static (List<FakeWebSource> Pages, List<MediaLocator.WantedInput> Clips) Fakes(AppServices services)
    {
        var pages = new List<FakeWebSource>();
        services.WebIn.SourceFactory = w =>
        {
            var page = new FakeWebSource(w.Target, WebEngine.ParseSize(w.Format), SKColors.Blue);
            pages.Add(page);
            return page;
        };
        var clips = new List<MediaLocator.WantedInput>();
        services.Video.SourceFactory = w =>
        {
            clips.Add(w);
            return new FakeSource(w);
        };
        return (pages, clips);
    }

    private static void WantTube(AppServices services, WebPlayVia via)
    {
        services.State.Pattern.Kind = PatternKind.Media;
        services.State.Pattern.Media.Source = MediaSource.Web;
        services.State.Pattern.Media.WebUrl = Tube;
        services.State.Pattern.Media.WebPlayVia = via;
        Dispatcher.UIThread.RunJobs();
        services.ReconcileInputs();
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void AResolvedPagePlaysThroughTheClipEngineUnderItsOwnKeyAndTheBrowserNeverOpens()
    {
        var b = TestApp.Boot();
        var tool = Path.Combine(Path.GetTempPath(), "patterns-yt-dlp-" + Guid.NewGuid().ToString("N") + ".exe");
        File.WriteAllText(tool, "stand-in");
        try
        {
            var (services, vm, _) = b;
            var (pages, clips) = Fakes(services);
            var asked = new List<string[]>();
            services.WebVideo.ConfiguredPath = () => tool;
            services.WebVideo.PathEnvironment = () => "";
            services.WebVideo.Runner = (exe, args, _) =>
            {
                Assert.Equal(tool, exe);
                asked.Add(args);
                return Task.FromResult((0, "https://cdn.example/v.mp4\nhttps://cdn.example/a.m4a\n", ""));
            };

            WantTube(services, WebPlayVia.NativePlayer);
            services.ReconcileInputs();                                                     // the answer landed on the first pass's request; this pass mounts it
            Dispatcher.UIThread.RunJobs();

            Assert.Single(asked);
            Assert.Equal(Tube, asked[0][^1]);
            Assert.Equal(WebVideoService.Phase.Ready, services.WebVideo.EntryFor(Tube)!.Phase);
            Assert.Single(pages);                                                           // the browser stood in for the first pass, before the answer landed
            Assert.Equal(0, services.WebIn.PageCount);                                      // and left once the stream took over (kept a moment for the crossfade)
            var clip = Assert.Single(clips, c => c.Key == "web:" + Tube);
            Assert.Equal(MediaLocator.WantedKind.VideoFile, clip.Kind);
            Assert.Equal("https://cdn.example/v.mp4", clip.Target);
            Assert.Equal("https://cdn.example/a.m4a", clip.Slave);
            Assert.Equal(Tube, clip.Origin);
            Assert.NotNull(InputBus.For("web:" + Tube));

            var state = System.Text.Json.JsonDocument.Parse(services.Router.StateJson()).RootElement;
            var web = state.GetProperty("web");
            Assert.Equal("native player", web.GetProperty("via").GetString());
            Assert.Equal("ready", web.GetProperty("native").GetProperty("phase").GetString());
            Assert.Equal("cdn.example", web.GetProperty("native").GetProperty("stream").GetString());
            Assert.True(web.GetProperty("native").GetProperty("separateAudio").GetBoolean());
            Assert.Contains("plays through libVLC", services.WebVideo.Note);
            vm.Media.RefreshActiveInputs();
            Assert.Contains("yt-dlp: " + tool, vm.Media.WebNativeText);

            // Back to the browser: a page opens again, the clip mount goes.
            WantTube(services, WebPlayVia.Browser);
            Assert.Equal(2, pages.Count);
            Assert.Equal(1, services.WebIn.PageCount);
            state = System.Text.Json.JsonDocument.Parse(services.Router.StateJson()).RootElement;
            Assert.Equal("browser", state.GetProperty("web").GetProperty("via").GetString());
        }
        finally
        {
            InputBus.Clear();
            b.Dispose();
            File.Delete(tool);
        }
    }

    [AvaloniaFact]
    public void WithoutTheToolThePageStaysInTheBrowserAndTheWordsSayWhy()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            var (pages, clips) = Fakes(services);
            services.WebVideo.ConfiguredPath = () => "";
            services.WebVideo.BaseDirectory = () => Path.Combine(Path.GetTempPath(), "patterns-no-tool-" + Guid.NewGuid().ToString("N"));
            services.WebVideo.PathEnvironment = () => "";
            var ran = false;
            services.WebVideo.Runner = (_, _, _) =>
            {
                ran = true;
                return Task.FromResult((0, "", ""));
            };

            WantTube(services, WebPlayVia.NativePlayer);
            Assert.False(ran);
            Assert.Equal(WebVideoService.Phase.NoTool, services.WebVideo.EntryFor(Tube)!.Phase);
            Assert.Single(pages);                                                           // the browser stands in
            Assert.DoesNotContain(clips, c => c.Key.StartsWith("web:", StringComparison.Ordinal));
            Assert.Contains("not on this machine", services.WebVideo.Note);
            var state = System.Text.Json.JsonDocument.Parse(services.Router.StateJson()).RootElement;
            Assert.Equal("browser", state.GetProperty("web").GetProperty("via").GetString());
            Assert.Equal("notool", state.GetProperty("web").GetProperty("native").GetProperty("phase").GetString());
            vm.Media.RefreshActiveInputs();
            Assert.Contains("yt-dlp is not on this machine", vm.Media.WebNativeText);

            // A tool that fails leaves the browser in place with its complaint, and is not asked again at once.
            var tool = Path.Combine(Path.GetTempPath(), "patterns-yt-dlp-" + Guid.NewGuid().ToString("N") + ".exe");
            File.WriteAllText(tool, "stand-in");
            try
            {
                var asks = 0;
                services.WebVideo.ConfiguredPath = () => tool;
                services.WebVideo.Runner = (_, _, _) =>
                {
                    asks++;
                    return Task.FromResult((1, "", "ERROR: [youtube] abc: Video unavailable\n"));
                };
                services.WebVideo.Forget(Tube);
                services.ReconcileInputs();
                Dispatcher.UIThread.RunJobs();
                services.ReconcileInputs();
                Dispatcher.UIThread.RunJobs();
                Assert.Equal(1, asks);
                var e = services.WebVideo.EntryFor(Tube)!;
                Assert.Equal(WebVideoService.Phase.Failed, e.Phase);
                Assert.Contains("Video unavailable", e.Words);
                Assert.Contains("the browser stands in", e.Words);
                Assert.Single(pages);
            }
            finally
            {
                File.Delete(tool);
            }
        }
        finally
        {
            InputBus.Clear();
            b.Dispose();
        }
    }
}
