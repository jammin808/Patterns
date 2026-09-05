using System.IO.Compression;
using System.Text;
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
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// A PowerPoint on a live desk: mounted as a pending deck while LibreOffice (a stand-in here)
/// converts it, the PDF taking its place when the conversion lands, the cache reused on the next
/// mount, RELOAD converting afresh, the state row, and the honest card when LibreOffice is
/// nowhere. The real LibreOffice runs the real command line when this machine has one.
/// </summary>
public class DeckConversionAppTests
{
    private static void Settle(Window window)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    private static SKBitmap RenderPane(RenderPipeline pipeline, int width, int height)
    {
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        pipeline.Render(surface.Canvas, width, height, 1.0);
        surface.Canvas.Flush();
        var bmp = new SKBitmap(info);
        surface.ReadPixels(info, bmp.GetPixels(), info.RowBytes, 0, 0);
        return bmp;
    }

    private static void AssertNear(SKColor expected, SKColor actual)
        => Assert.True(Math.Abs(expected.Red - actual.Red) < 24 && Math.Abs(expected.Green - actual.Green) < 24 && Math.Abs(expected.Blue - actual.Blue) < 24,
            $"expected about {expected}, got {actual}");

    /// <summary>Two landscape pages, red then blue — what the stand-in LibreOffice "converts" a PowerPoint into.</summary>
    private static void WriteTwoPagePdf(string path)
    {
        using var stream = File.Create(path);
        using var doc = SKDocument.CreatePdf(stream);
        foreach (var color in new[] { SKColors.Red, SKColors.Blue })
        {
            var canvas = doc.BeginPage(800, 450);
            canvas.Clear(color);
            doc.EndPage();
        }
        doc.Close();
    }

    /// <summary>
    /// A stand-in for LibreOffice: waits at the gate the test holds at that moment (a real
    /// conversion takes seconds, and the test must see the deck pending), then writes the PDF
    /// where soffice would, named as soffice names it.
    /// </summary>
    private static DeckConverter.Runner FakeLibreOffice(List<string> sources, Func<Task> gate)
        => async (exe, args, ct) =>
        {
            var list = args.ToList();
            var outDir = list[list.IndexOf("--outdir") + 1];
            var source = list[^1];
            sources.Add(source);
            await gate().WaitAsync(ct);
            WriteTwoPagePdf(Path.Combine(outDir, DeckConversion.ProducedName(source)));
            return (true, "");
        };

    private static void WaitForConversions(AppServices services, Window window)
    {
        TestApp.Pump(services.DeckIn.WhenConversionsSettled().ContinueWith(_ => true));
        Settle(window);
    }

    [AvaloniaFact]
    public void APowerPointIsConvertedOnceAndBecomesTheDeckOnAir()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            var router = new CommandRouter(services);
            vm.IsSandboxActive = false;
            vm.State.Transition.Enabled = false;
            var sources = new List<string>();
            var gate = new TaskCompletionSource();
            services.DeckIn.Converter.Locator = () => "/fake/soffice";
            services.DeckIn.Converter.RunnerOverride = FakeLibreOffice(sources, () => gate.Task);

            var pptx = Path.Combine(b.Dir, "keynote talk.pptx");
            File.WriteAllText(pptx, "not really a PowerPoint — the stand-in never reads it");
            vm.State.Pattern.Kind = PatternKind.Media;
            vm.State.Pattern.Media.Source = MediaSource.Deck;
            vm.State.Pattern.Media.DeckPath = pptx;
            Settle(window);

            // Pending while LibreOffice works: no pages, an honest status on the desk, the phone and the wire.
            var key = InputKeys.Deck(pptx);
            var pending = Assert.IsType<PendingDeckSource>(services.DeckIn.For(key));
            Assert.Contains("Converting keynote talk.pptx", pending.StatusText);
            Assert.Equal(0, pending.PageCount);
            vm.PollNow();
            Assert.Contains("Converting", vm.DeckPageText);
            Assert.Contains("LibreOffice found", vm.DeckToolText);
            Assert.False(vm.DeckToolMissing);
            Assert.False(vm.DeckOnAir);
            Assert.StartsWith("ERR", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("DECK NEXT"))));
            var pendingState = System.Text.Json.JsonDocument.Parse(router.StateJson()).RootElement.GetProperty("deck");
            Assert.True(pendingState.GetProperty("converting").GetBoolean());
            Assert.True(services.DeckIn.Converting);
            Assert.Single(sources);                      // LibreOffice was started once, and is at work

            // The conversion lands: the PDF takes the pending deck's place — the same key, the pages on the PREVIEW pane.
            gate.SetResult();
            WaitForConversions(services, window);
            var deck = Assert.IsType<PdfDeckSource>(services.DeckIn.For(key));
            Assert.Equal(pptx, deck.Path);
            Assert.Equal(2, deck.PageCount);
            Assert.Single(sources);
            Assert.Equal(pptx, sources[0]);
            Assert.NotNull(services.DeckIn.Converter.Cached(pptx));
            Assert.StartsWith(services.DeckIn.Converter.CacheDirectory, services.DeckIn.Converter.Cached(pptx)!);
            var pipeline = window.PreviewPipeline!;
            using (var page1 = RenderPane(pipeline, 800, 450)) AssertNear(SKColors.Red, page1.GetPixel(400, 225));
            Assert.True(services.Actions.PresenterAdvance(+1, ActionOrigin.Clicker));
            Assert.Equal(2, deck.Page);
            using (var page2 = RenderPane(pipeline, 800, 450)) AssertNear(SKColors.Blue, page2.GetPixel(400, 225));
            vm.PollNow();
            Assert.True(vm.DeckOnAir);
            Assert.StartsWith("Page 2 / 2", vm.DeckPageText);
            var state = System.Text.Json.JsonDocument.Parse(router.StateJson()).RootElement.GetProperty("deck");
            Assert.Equal("PowerPoint", state.GetProperty("kind").GetString());
            Assert.False(state.GetProperty("converting").GetBoolean());
            Assert.Equal(2, state.GetProperty("count").GetInt32());

            // Off and on again: the cache serves it — LibreOffice is not run twice for the same file.
            vm.State.Pattern.Media.Source = MediaSource.Image;
            Settle(window);
            Assert.Null(services.DeckIn.For(key));
            vm.State.Pattern.Media.Source = MediaSource.Deck;
            Settle(window);
            var again = Assert.IsType<PdfDeckSource>(services.DeckIn.For(key));
            Assert.NotSame(deck, again);
            Assert.Equal(2, again.PageCount);
            Assert.Single(sources);

            // RELOAD drops the cached PDF and converts afresh — held at a new gate, so the drop and the
            // pending deck are seen before the stand-in lands; an edited file (a new size) converts by itself.
            gate = new TaskCompletionSource();
            vm.ReloadDeckCommand.Execute(null);
            Assert.Null(services.DeckIn.Converter.Cached(pptx));
            Settle(window);
            Assert.IsType<PendingDeckSource>(services.DeckIn.For(key));
            Assert.Equal(2, sources.Count);
            gate.SetResult();
            WaitForConversions(services, window);
            var reloaded = Assert.IsType<PdfDeckSource>(services.DeckIn.For(key));
            Assert.NotSame(again, reloaded);
            Assert.Equal(2, sources.Count);
            vm.State.Pattern.Media.Source = MediaSource.Image;
            Settle(window);
            File.WriteAllText(pptx, "the same deck, edited — a few more bytes than before");
            vm.State.Pattern.Media.Source = MediaSource.Deck;
            Settle(window);
            WaitForConversions(services, window);
            Assert.Equal(3, sources.Count);
            Assert.IsType<PdfDeckSource>(services.DeckIn.For(key));

            // The Media page names the block and carries RELOAD; the LibreOffice box stays away while it is found.
            vm.SelectPage(Shell.IndexOf("Media"));
            Settle(window);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "DECK — A PDF OR POWERPOINT PRESENTATION");
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), x => x.Content as string == "RELOAD");
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void WithoutLibreOfficeThePowerPointIsAnHonestCardAndThePathBoxShows()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            var router = new CommandRouter(services);
            vm.IsSandboxActive = false;
            vm.State.Transition.Enabled = false;
            services.DeckIn.Converter.Locator = () => null;
            services.DeckIn.Converter.RunnerOverride = (_, _, _) => throw new InvalidOperationException("never run without LibreOffice");

            var pptx = Path.Combine(b.Dir, "talk.pptx");
            File.WriteAllText(pptx, "a deck");
            vm.State.Pattern.Kind = PatternKind.Media;
            vm.State.Pattern.Media.Source = MediaSource.Deck;
            vm.State.Pattern.Media.DeckPath = pptx;
            Settle(window);
            WaitForConversions(services, window);

            var key = InputKeys.Deck(pptx);
            var pending = Assert.IsType<PendingDeckSource>(services.DeckIn.For(key));
            Assert.True(pending.Failed);
            Assert.Contains("LibreOffice not found", pending.StatusText);
            vm.PollNow();
            Assert.Contains("LibreOffice not found", vm.DeckPageText);
            Assert.True(vm.DeckToolMissing);
            Assert.StartsWith("ERR", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("DECK NEXT"))));
            var state = System.Text.Json.JsonDocument.Parse(router.StateJson()).RootElement.GetProperty("deck");
            Assert.False(state.GetProperty("converting").GetBoolean());
            Assert.Contains("LibreOffice not found", state.GetProperty("status").GetString());
            Assert.Equal(0, services.DeckIn.Converter.Conversions);

            // The card says so on the PREVIEW pane — never a blank, never a crash — and the page offers the path box.
            using (var card = RenderPane(window.PreviewPipeline!, 800, 450)) Assert.NotEqual(SKColors.Red, card.GetPixel(400, 225));
            vm.SelectPage(Shell.IndexOf("Media"));
            Settle(window);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "LibreOffice path" && t.IsEffectivelyVisible);

            // A missing file is its own card.
            vm.State.Pattern.Media.DeckPath = Path.Combine(b.Dir, "gone.pptx");
            Settle(window);
            var missing = Assert.IsType<PendingDeckSource>(services.DeckIn.For(InputKeys.Deck(vm.State.Pattern.Media.DeckPath)));
            Assert.Contains("not found", missing.StatusText);
        }
        finally
        {
            b.Dispose();
        }
    }

    /// <summary>A two-slide Impress deck, red and green, written as the OpenDocument package LibreOffice reads.</summary>
    private static string WriteTwoSlideImpressDeck(string dir)
    {
        const string content = """
            <?xml version="1.0" encoding="UTF-8"?>
            <office:document-content xmlns:office="urn:oasis:names:tc:opendocument:xmlns:office:1.0" xmlns:style="urn:oasis:names:tc:opendocument:xmlns:style:1.0" xmlns:text="urn:oasis:names:tc:opendocument:xmlns:text:1.0" xmlns:draw="urn:oasis:names:tc:opendocument:xmlns:drawing:1.0" xmlns:fo="urn:oasis:names:tc:opendocument:xmlns:xsl-fo-compatible:1.0" xmlns:svg="urn:oasis:names:tc:opendocument:xmlns:svg-compatible:1.0" xmlns:presentation="urn:oasis:names:tc:opendocument:xmlns:presentation:1.0" office:version="1.2">
            <office:automatic-styles>
            <style:style style:name="dp1" style:family="drawing-page"><style:drawing-page-properties draw:fill="solid" draw:fill-color="#ff0000"/></style:style>
            <style:style style:name="dp2" style:family="drawing-page"><style:drawing-page-properties draw:fill="solid" draw:fill-color="#00ff00"/></style:style>
            </office:automatic-styles>
            <office:body><office:presentation>
            <draw:page draw:name="page1" draw:style-name="dp1"><draw:frame svg:width="10cm" svg:height="2cm" svg:x="2cm" svg:y="2cm"><draw:text-box><text:p>One</text:p></draw:text-box></draw:frame></draw:page>
            <draw:page draw:name="page2" draw:style-name="dp2"><draw:frame svg:width="10cm" svg:height="2cm" svg:x="2cm" svg:y="2cm"><draw:text-box><text:p>Two</text:p></draw:text-box></draw:frame></draw:page>
            </office:presentation></office:body></office:document-content>
            """;
        const string manifest = """
            <?xml version="1.0" encoding="UTF-8"?>
            <manifest:manifest xmlns:manifest="urn:oasis:names:tc:opendocument:xmlns:manifest:1.0" manifest:version="1.2">
            <manifest:file-entry manifest:full-path="/" manifest:media-type="application/vnd.oasis.opendocument.presentation"/>
            <manifest:file-entry manifest:full-path="content.xml" manifest:media-type="text/xml"/>
            </manifest:manifest>
            """;
        var path = Path.Combine(dir, "real talk.odp");
        using var zip = new ZipArchive(File.Create(path), ZipArchiveMode.Create);
        var mime = zip.CreateEntry("mimetype", CompressionLevel.NoCompression);   // first and stored, as the format asks
        using (var s = mime.Open()) s.Write(Encoding.ASCII.GetBytes("application/vnd.oasis.opendocument.presentation"));
        using (var s = zip.CreateEntry("content.xml").Open()) s.Write(Encoding.UTF8.GetBytes(content));
        using (var s = zip.CreateEntry("META-INF/manifest.xml").Open()) s.Write(Encoding.UTF8.GetBytes(manifest));
        return path;
    }

    [AvaloniaFact]
    public void TheRealLibreOfficeConvertsADeckWhenThisMachineHasOne()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, _, _) = b;
            var converter = services.DeckIn.Converter;
            if (converter.LibreOffice is null) return;   // nothing to prove here: the stand-in tests cover the pipeline

            var odp = WriteTwoSlideImpressDeck(b.Dir);
            var result = TestApp.Pump(converter.ConvertAsync(odp));
            if (!result.Ok && result.Message.Contains("could not be loaded", StringComparison.OrdinalIgnoreCase))
            {
                return;   // a LibreOffice without its Impress component (a bare core install) cannot open a deck
            }
            Assert.True(result.Ok, result.Message);
            Assert.True(File.Exists(result.PdfPath));
            Assert.Equal(1, converter.Conversions);
            var deck = PdfDeckSource.Open(odp, result.PdfPath, 1, new SKSizeI(1920, 1080));
            Assert.Equal(2, deck.PageCount);
            Assert.True(deck.PageShape.Width > deck.PageShape.Height);
            deck.Dispose();

            // The same file again is the cache, not another run of LibreOffice.
            var cached = TestApp.Pump(converter.ConvertAsync(odp));
            Assert.Equal(result.PdfPath, cached.PdfPath);
            Assert.Equal(1, converter.Conversions);
        }
        finally
        {
            b.Dispose();
        }
    }
}
