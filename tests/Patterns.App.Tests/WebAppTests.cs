using Avalonia;
using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.Rendering;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>A web page that paints one colour and remembers everything the desk did to it — the app's stand-in for WebView2.</summary>
public sealed class FakeWebSource : IWebSource, IDisposable
{
    private readonly SKColor _color;

    public FakeWebSource(string url, (int Width, int Height) size, SKColor color)
    {
        CurrentUrl = url;
        Size = size;
        _color = color;
    }

    public (int Width, int Height) Size { get; }
    public List<(string Kind, float X, float Y)> Events { get; } = new();
    public List<string> Typed { get; } = new();
    public List<string> Keys { get; } = new();
    public List<string> Scripts { get; } = new();
    public int Backs, Forwards, Reloads;
    public bool Disposed { get; private set; }

    public bool DrawFrame(SKCanvas canvas, SKRect dest, SKPaint? paint)
    {
        canvas.DrawRect(dest, new SKPaint { Color = _color });
        return true;
    }

    public bool DrawFrame(SKCanvas canvas, SKRect dest, SKPaint? paint, in FrameCrop crop) => DrawFrame(canvas, dest, paint);

    public SKSizeI? FrameSize => new SKSizeI(640, 360);
    public bool IsPlaying => true;
    public bool IsEnded => false;
    public double DurationSeconds => 0;
    public string StatusText => "Showing";
    public SKSizeI PageSize => new(Size.Width, Size.Height);
    public SKPoint? PointerNorm { get; private set; }
    public DateTime? LastClickUtc { get; private set; }
    public string CurrentUrl { get; private set; }
    public string Title => "A page";
    public double ZoomPct { get; set; } = 100;
    public bool IsMuted { get; set; }

    public void PointerMove(float nx, float ny)
    {
        PointerNorm = new SKPoint(nx, ny);
        Events.Add(("move", nx, ny));
    }

    public void PointerDown(float nx, float ny)
    {
        PointerNorm = new SKPoint(nx, ny);
        LastClickUtc = DateTime.UtcNow;
        Events.Add(("down", nx, ny));
    }

    public void PointerUp(float nx, float ny) => Events.Add(("up", nx, ny));

    public void PointerLeave()
    {
        PointerNorm = null;
        Events.Add(("leave", 0, 0));
    }

    public void Wheel(float nx, float ny, float deltaLines, bool horizontal) => Events.Add((horizontal ? "hwheel" : "wheel", deltaLines, 0));
    public void TypeText(string text) => Typed.Add(text);
    public void PressKey(string key) => Keys.Add(key);
    public void RunScript(string script) => Scripts.Add(script);
    public void Navigate(string url) => CurrentUrl = url;
    public void GoBack() => Backs++;
    public void GoForward() => Forwards++;
    public void Reload() => Reloads++;
    public void Dispose() => Disposed = true;
}

/// <summary>
/// Web pages inside the engine, from the desk: the engine mounting what the show wants, a click
/// on the PREVIEW pane reaching the page, a web layer clicked into or dragged, the page controls
/// typing into it, and the pages carrying the blocks.
/// </summary>
public class WebAppTests
{
    private const string Key = "web:https://example.com";

    private static void Settle(Window window)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static void RenderPane(RenderPipeline pipeline, int width, int height)
    {
        using var surface = SKSurface.Create(new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul));
        pipeline.Render(surface.Canvas, width, height, 1.0);
    }

    private static Point OnPane(in PaneMap map, float canvasX, float canvasY)
    {
        var tx = canvasX * map.CanvasScale + map.CanvasOffset.X;
        var ty = canvasY * map.CanvasScale + map.CanvasOffset.Y;
        return new Point(tx * map.Scale + map.Dx, ty * map.Scale + map.Dy);
    }

    /// <summary>Every page the engine opens is a fake — remembered so the test can ask it what happened.</summary>
    private static List<FakeWebSource> FakePages(AppServices services)
    {
        var made = new List<FakeWebSource>();
        services.WebIn.SourceFactory = w =>
        {
            var page = new FakeWebSource(w.Target, WebEngine.ParseSize(w.Format), w.Target.Contains("other") ? SKColors.Red : SKColors.Blue);
            made.Add(page);
            return page;
        };
        return made;
    }

    private static void ShowPage(MainViewModel vm, string url = "example.com")
    {
        vm.State.Pattern.Kind = PatternKind.Media;
        vm.State.Pattern.Media.Source = MediaSource.Web;
        vm.State.Pattern.Media.WebUrl = url;
    }

    [AvaloniaFact]
    public void TheEngineMountsWhatTheShowWantsAndAppliesZoomLive()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            var made = FakePages(services);
            vm.IsSandboxActive = false;
            ShowPage(vm);
            vm.State.Pattern.Media.WebZoomPct = 150;
            Settle(window);
            var page = Assert.IsType<FakeWebSource>(InputBus.For(Key));
            Assert.Equal(150, page.ZoomPct);
            Assert.Equal((1920, 1080), page.Size);
            Assert.Equal("https://example.com", page.CurrentUrl);

            // Zoom and sound apply to the running page; a new viewport reopens it and the old one fades out.
            vm.State.Pattern.Media.WebZoomPct = 200;
            vm.State.Pattern.Media.Mute = true;
            Settle(window);
            Assert.Same(page, InputBus.For(Key));
            Assert.Equal(200, page.ZoomPct);
            Assert.True(page.IsMuted);
            services.BulkEdit(() =>
            {
                vm.State.Pattern.Media.WebWidth = 1280;
                vm.State.Pattern.Media.WebHeight = 720;
            });
            Settle(window);
            var reopened = Assert.IsType<FakeWebSource>(InputBus.For(Key));
            Assert.NotSame(page, reopened);
            Assert.Equal((1280, 720), reopened.Size);
            Assert.Same(page, InputBus.PreviousFor(Key));

            // A layer wanting the same address shares the browser; another address is another page.
            vm.State.Pattern.Layer1.Enabled = true;
            vm.State.Pattern.Layer1.Source = LayerSource.Web;
            vm.State.Pattern.Layer1.WebUrl = "https://example.com";
            vm.State.Pattern.Layer2.Enabled = true;
            vm.State.Pattern.Layer2.Source = LayerSource.Web;
            vm.State.Pattern.Layer2.WebUrl = "other.org";
            Settle(window);
            Assert.Equal(2, services.WebIn.PageCount);
            Assert.True(((FakeWebSource)InputBus.For("web:https://other.org")!).IsMuted);
            Assert.Equal(3, made.Count);

            // Nothing wanting a page any more retires it.
            vm.State.Pattern.Media.Source = MediaSource.Image;
            vm.State.Pattern.Layer1.Enabled = false;
            vm.State.Pattern.Layer2.Enabled = false;
            Settle(window);
            Assert.Null(InputBus.For(Key));
            Assert.Null(InputBus.For("web:https://other.org"));
            Assert.Equal(0, services.WebIn.PageCount);
            Assert.DoesNotContain(MediaLocator.FindWantedInputs(services.Bus.Current), w => w.Kind == MediaLocator.WantedKind.Web);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ClicksOnThePreviewPaneReachThePageAndAWebLayerTakesBothAClickAndADrag()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            var made = FakePages(services);
            vm.IsSandboxActive = false;
            ShowPage(vm);
            Settle(window);
            var page = made.Single();

            var pipeline = window.PreviewPipeline!;
            RenderPane(pipeline, 800, 450);
            var map = pipeline.LastMap!.Value;
            var hit = pipeline.LastHits.Single(h => h.Kind == HitKind.WebPage);
            var centre = OnPane(in map, hit.Rect.MidX, hit.Rect.MidY);

            // A press lands on the page as a click at that spot, a move drags on the page, the release ends it.
            Assert.True(window.PreviewWebPress(centre));
            var down = page.Events.Last();
            Assert.Equal("down", down.Kind);
            Assert.Equal(0.5f, down.X, 2);
            Assert.Equal(0.5f, down.Y, 2);
            window.PreviewWebMove(new Point(centre.X + 40, centre.Y));
            Assert.Equal("move", page.Events.Last().Kind);
            Assert.True(page.Events.Last().X > 0.5f);
            window.PreviewWebRelease(new Point(centre.X + 40, centre.Y));
            Assert.Equal("up", page.Events.Last().Kind);
            Assert.True(vm.HasWebPage);
            Assert.Contains("example.com", vm.WebControlsTarget);

            // Hovering tells the page where the pointer is; leaving the page tells it that too; the wheel scrolls.
            Assert.True(window.PreviewWebHover(centre));
            Assert.Equal("move", page.Events.Last().Kind);
            Assert.False(window.PreviewWebHover(new Point(-20, -20)));
            Assert.Equal("leave", page.Events.Last().Kind);
            Assert.Null(page.PointerNorm);
            Assert.True(window.PreviewWebWheel(centre, -1, 0));
            Assert.Equal(("wheel", -1f, 0f), page.Events.Last());
            Assert.False(window.PreviewWebWheel(new Point(-20, -20), -1, 0));

            // The page itself is not a handle: a drag finds nothing there.
            Assert.False(window.BeginPreviewDrag(centre));

            // A web layer over a plain pattern: its page takes the click; the box still drags (Alt on the real pane).
            vm.State.Pattern.Kind = PatternKind.FlatField;
            vm.State.Pattern.Layer1.Enabled = true;
            vm.State.Pattern.Layer1.Source = LayerSource.Web;
            vm.State.Pattern.Layer1.WebUrl = "other.org";
            vm.State.Pattern.Layer1.XPct = 30;
            vm.State.Pattern.Layer1.YPct = 30;
            vm.State.Pattern.Layer1.WPct = 40;
            vm.State.Pattern.Layer1.HPct = 40;
            Settle(window);
            var other = made.Single(p => p.CurrentUrl.Contains("other"));
            RenderPane(pipeline, 800, 450);
            var box = pipeline.LastHits.First(h => h.Kind == HitKind.Layer1);
            var at = OnPane(in map, box.Rect.MidX, box.Rect.MidY);
            Assert.True(window.PreviewWebPress(at));
            Assert.Equal("down", other.Events.Last().Kind);
            window.PreviewWebRelease(at);
            Assert.Contains("other.org", vm.WebControlsTarget);

            Assert.True(window.BeginPreviewDrag(at));
            window.MovePreviewDrag(new Point(at.X + 40, at.Y));
            window.EndPreviewDrag();
            Assert.True(vm.State.Pattern.Layer1.XPct > 30);
            Assert.Contains("Layer 1 placed", vm.StatusMessage);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ThePageControlsTypeIntoThePageAndThePagesCarryTheBlocks()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            var made = FakePages(services);
            vm.IsSandboxActive = false;

            // From the Remote & web page: the typed address becomes the pattern and is remembered.
            vm.State.Web.Url = "schedule.example.org";
            vm.PutWebPageOnPatternCommand.Execute(null);
            Settle(window);
            Assert.Equal(PatternKind.Media, vm.State.Pattern.Kind);
            Assert.Equal(MediaSource.Web, vm.State.Pattern.Media.Source);
            Assert.Equal("https://schedule.example.org", vm.State.Pattern.Media.WebUrl);
            Assert.Contains("https://schedule.example.org", vm.State.Web.SavedUrls);
            var page = made.Single();
            Assert.True(vm.HasWebPage);

            // The controls drive the pattern's page until the desk points at another.
            vm.WebTypedText = "hello";
            vm.SendWebTextCommand.Execute(null);
            Assert.Equal("hello", Assert.Single(page.Typed));
            Assert.Equal("", vm.WebTypedText);
            vm.WebKeyCommand.Execute("Enter");
            Assert.Equal("Enter", Assert.Single(page.Keys));
            vm.WebBackCommand.Execute(null);
            vm.WebForwardCommand.Execute(null);
            vm.WebReloadCommand.Execute(null);
            Assert.Equal((1, 1, 1), (page.Backs, page.Forwards, page.Reloads));

            // A saved page picked on the Media page becomes the pattern's address; Remember keeps a typed one.
            vm.SavedWebPick = "https://other.org";
            Assert.Equal("https://other.org", vm.State.Pattern.Media.WebUrl);
            vm.State.Pattern.Media.WebUrl = "third.example";
            vm.RememberWebUrlCommand.Execute(null);
            Assert.Contains("https://third.example", vm.State.Web.SavedUrls);

            // The Media page carries the page block and the controls; the Remote & web page its button.
            vm.SelectPage(Shell.IndexOf("Media"));
            Settle(window);
            var texts = window.GetVisualDescendants().OfType<TextBlock>().Select(t => t.Text).ToList();
            Assert.Contains("WEB PAGE", texts);
            Assert.Contains("PAGE CONTROLS", texts);
            vm.SelectPage(Shell.IndexOf("Remote"));
            Settle(window);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "A WEB PAGE ON THE PATTERN");

            // Without a page anywhere, the controls say so instead of throwing.
            vm.State.Pattern.Media.Source = MediaSource.Image;
            Settle(window);
            vm.WebTypedText = "x";
            vm.SendWebTextCommand.Execute(null);
            Assert.Contains("No web page", vm.StatusMessage);
        }
        finally
        {
            b.Dispose();
        }
    }
}
