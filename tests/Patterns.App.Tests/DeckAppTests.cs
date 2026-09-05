using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Input;
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
/// A PDF deck on a live desk: the real renderer opening a three-page deck at the rig's raster,
/// the pages drawn on the PREVIEW pane, the click-through turning them from the desk, the
/// keyboard, the wire and a cue, the cue stack resuming at the end, the state row, the start
/// page, and the Media page's block.
/// </summary>
public class DeckAppTests
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

    /// <summary>Three landscape pages — red, green, blue — as SkiaSharp writes a PDF.</summary>
    private static string ThreePageDeck(string dir)
    {
        var path = Path.Combine(dir, "deck.pdf");
        using (var stream = File.Create(path))
        using (var doc = SKDocument.CreatePdf(stream))
        {
            foreach (var color in new[] { SKColors.Red, SKColors.Lime, SKColors.Blue })
            {
                var canvas = doc.BeginPage(800, 450);
                canvas.Clear(color);
                doc.EndPage();
            }
            doc.Close();
        }
        return path;
    }

    private static void AssertNear(SKColor expected, SKColor actual)
        => Assert.True(Math.Abs(expected.Red - actual.Red) < 24 && Math.Abs(expected.Green - actual.Green) < 24 && Math.Abs(expected.Blue - actual.Blue) < 24,
            $"expected about {expected}, got {actual}");

    private static string Send(CommandRouter router, string line) => TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse(line)));

    [AvaloniaFact]
    public void ADeckOnAirIsTheClickThroughAndTheCueStackResumesAtItsEnd()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            var router = new CommandRouter(services);
            vm.IsSandboxActive = false;
            vm.State.Transition.Enabled = false;   // the pixels below are the page, not a crossfade from the pattern before it
            Assert.StartsWith("ERR", Send(router, "DECK NEXT"));

            var pdf = ThreePageDeck(b.Dir);
            vm.State.Pattern.Kind = PatternKind.Media;
            vm.State.Pattern.Media.Source = MediaSource.Deck;
            vm.State.Pattern.Media.DeckPath = pdf;
            Settle(window);
            var deck = Assert.IsType<PdfDeckSource>(services.DeckIn.For(InputKeys.Deck(pdf)));
            Assert.Equal(3, deck.PageCount);
            Assert.Equal(1, deck.Page);
            Assert.Equal(1920, deck.Raster.Width);                      // the rig's raster, at the page's 16:9 shape
            Assert.Equal(new SKSize(800, 450), deck.PageShape);
            vm.PollNow();
            Assert.True(vm.DeckOnAir);
            Assert.StartsWith("Page 1 / 3", vm.DeckPageText);
            Assert.Contains("page 1 of 3", vm.PresenterStepText);

            // The page on the PREVIEW pane, full frame: red, then green after a NEXT from the desk.
            var pipeline = window.PreviewPipeline!;
            using (var page1 = RenderPane(pipeline, 800, 450)) AssertNear(SKColors.Red, page1.GetPixel(400, 225));
            Assert.True(services.Actions.PresenterAdvance(+1, ActionOrigin.Desk));
            Assert.Equal(2, deck.Page);
            using (var page2 = RenderPane(pipeline, 800, 450)) AssertNear(SKColors.Lime, page2.GetPixel(400, 225));

            // The wire, a cue and the desk's buttons turn it too; the first page stays put on PREV.
            Assert.Equal("OK", Send(router, "DECK PAGE 3"));
            Assert.Equal(3, deck.Page);
            using (var page3 = RenderPane(pipeline, 800, 450)) AssertNear(SKColors.Blue, page3.GetPixel(400, 225));
            Assert.Equal("OK", Send(router, "DECK PREV"));
            Assert.Equal(2, deck.Page);
            var cue = new CueActionConfig { Kind = CueActionKind.DeckPage, Value = "first" };
            Assert.True(services.Actions.Execute(ShowActions.ToShowAction(cue), new ActionOrigin(OriginKind.Cue, "01.010")).Ok);
            Assert.Equal(1, deck.Page);
            Assert.True(services.Actions.PresenterAdvance(-1, ActionOrigin.Clicker));
            Assert.Equal(1, deck.Page);
            vm.DeckLastCommand.Execute(null);
            Assert.Equal(3, deck.Page);
            vm.DeckFirstCommand.Execute(null);
            Assert.Equal(1, deck.Page);
            vm.DeckNextCommand.Execute(null);
            Assert.Equal(2, deck.Page);
            vm.DeckPrevCommand.Execute(null);
            Assert.Equal(1, deck.Page);
            Assert.StartsWith("ERR", Send(router, "DECK PAGE 0"));

            // The clicker's keys turn the deck with no list armed at all.
            Assert.False(vm.ClickerArmed);
            window.KeyPress(Key.PageDown, RawInputModifiers.None, PhysicalKey.PageDown, null);
            window.KeyRelease(Key.PageDown, RawInputModifiers.None, PhysicalKey.PageDown, null);
            Assert.Equal(2, deck.Page);
            window.KeyPress(Key.PageUp, RawInputModifiers.None, PhysicalKey.PageUp, null);
            window.KeyRelease(Key.PageUp, RawInputModifiers.None, PhysicalKey.PageUp, null);
            Assert.Equal(1, deck.Page);

            // At the last page: with nothing to follow the deck holds; with the caller's stack armed the next click GOes the standby cue.
            Assert.Equal("OK", Send(router, "DECK LAST"));
            Assert.Equal(3, deck.Page);
            var held = services.Actions.Execute(ShowActionKind.PresenterNext, ActionOrigin.Clicker);
            Assert.True(held.Ok);
            Assert.Contains("nothing follows", held.Message);
            Assert.Equal(3, deck.Page);
            vm.State.Pattern.Media.DeckEndsWithGo = false;
            var hold = services.Actions.Execute(ShowActionKind.PresenterNext, ActionOrigin.Clicker);
            Assert.Contains("last page", hold.Message);
            vm.State.Pattern.Media.DeckEndsWithGo = true;

            var lights = new RunCueConfig { Number = "01.010", Name = "Lights down" };
            lights.Actions.Add(new CueActionConfig { Kind = CueActionKind.BlackoutOn });
            services.CueStack.Stack.Cues.Add(lights);
            services.CueStack.SetArmed(true, ActionOrigin.Desk);
            services.CueStack.Standby(lights.Id);
            vm.PollNow();
            Assert.Contains("GOes the standby cue", vm.PresenterStepText);
            var state = System.Text.Json.JsonDocument.Parse(router.StateJson()).RootElement.GetProperty("deck");
            Assert.Equal((3, 3, true), (state.GetProperty("page").GetInt32(), state.GetProperty("count").GetInt32(), state.GetProperty("ended").GetBoolean()));
            Assert.Equal("deck.pdf", state.GetProperty("file").GetString());
            var go = services.Actions.Execute(ShowActionKind.PresenterNext, ActionOrigin.Clicker);
            Assert.True(go.Ok, go.Message);
            Assert.Contains("GO 01.010", go.Message);
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.State.Blackout);

            // A deck that comes on again opens at its start page; away from decks the state row is null.
            vm.State.Blackout = false;
            vm.State.Pattern.Media.Source = MediaSource.Image;
            Settle(window);
            Assert.Null(services.DeckIn.For(InputKeys.Deck(pdf)));
            Assert.Equal(System.Text.Json.JsonValueKind.Null, System.Text.Json.JsonDocument.Parse(router.StateJson()).RootElement.GetProperty("deck").ValueKind);
            vm.State.Pattern.Media.DeckStartPage = 2;
            vm.State.Pattern.Media.Source = MediaSource.Deck;
            Settle(window);
            var again = Assert.IsType<PdfDeckSource>(services.DeckIn.For(InputKeys.Deck(pdf)));
            Assert.NotSame(deck, again);
            Assert.Equal(2, again.Page);

            // A missing file is a card with the reason, never a crash.
            vm.State.Pattern.Media.DeckPath = Path.Combine(b.Dir, "missing.pdf");
            Settle(window);
            var missing = Assert.IsType<PdfDeckSource>(services.DeckIn.For(InputKeys.Deck(vm.State.Pattern.Media.DeckPath)));
            Assert.Equal(0, missing.PageCount);
            Assert.Contains("not found", missing.StatusText);
            Assert.StartsWith("ERR", Send(router, "DECK NEXT"));
            using (var card = RenderPane(pipeline, 800, 450)) Assert.NotEqual(SKColors.Blue, card.GetPixel(400, 225));

            // The Media page carries the block.
            vm.SelectPage(Shell.IndexOf("Media"));
            Settle(window);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "DECK — A PDF OR POWERPOINT PRESENTATION");
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), x => x.Content as string == "NEXT PAGE ▶");
        }
        finally
        {
            b.Dispose();
        }
    }
}
