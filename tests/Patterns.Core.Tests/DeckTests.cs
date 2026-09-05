using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>A deck that paints one colour at a 4:3 page shape and remembers every turn.</summary>
public sealed class FakeDeckSource : IDeckSource
{
    private readonly SKColor _color;

    public FakeDeckSource(string path, int pages, SKColor color, SKSize? shape = null)
    {
        Path = path;
        PageCount = pages;
        Page = pages > 0 ? 1 : 0;
        PageShape = shape ?? new SKSize(1024, 768);
        _color = color;
    }

    public List<int> Turns { get; } = new();

    public bool DrawFrame(SKCanvas canvas, SKRect dest, SKPaint? paint)
    {
        canvas.DrawRect(dest, new SKPaint { Color = _color });
        return true;
    }

    public bool DrawFrame(SKCanvas canvas, SKRect dest, SKPaint? paint, in FrameCrop crop) => DrawFrame(canvas, dest, paint);

    public SKSizeI? FrameSize => new SKSizeI((int)PageShape.Width, (int)PageShape.Height);
    public bool IsPlaying => true;
    public bool IsEnded => false;
    public double DurationSeconds => 0;
    public string StatusText => $"Page {Page} / {PageCount}";
    public string Path { get; }
    public int PageCount { get; }
    public int Page { get; private set; }
    public SKSize PageShape { get; }

    public bool GoTo(int page)
    {
        var target = Math.Clamp(page, 1, Math.Max(1, PageCount));
        if (PageCount == 0 || target == Page) return false;
        Page = target;
        Turns.Add(target);
        return true;
    }
}

/// <summary>
/// Decks — PDF presentations — in the engine and the workflow: the page words, the raster for a
/// rig, the show wanting a deck, the page drawn at its own shape, the verbs on the wire and over
/// OSC, and the cue actions through the spec, the checks, the summary and the sheet.
/// </summary>
[Collection("InputBus")]
public class DeckTests
{
    [Fact]
    public void PagesAreNamedByNumberOrWordAndRenderedAtTheRigsRaster()
    {
        Assert.True(Decks.TryParsePage("5", out var page, out var word));
        Assert.Equal((5, ""), (page, word));
        Assert.True(Decks.TryParsePage("page 12", out page, out _));
        Assert.Equal(12, page);
        Assert.True(Decks.TryParsePage("p3", out page, out _));
        Assert.Equal(3, page);
        Assert.True(Decks.TryParsePage("FIRST", out _, out word));
        Assert.Equal("first", word);
        Assert.True(Decks.TryParsePage("end", out _, out word));
        Assert.Equal("last", word);
        Assert.True(Decks.TryParsePage("back", out _, out word));
        Assert.Equal("prev", word);
        Assert.False(Decks.TryParsePage("0", out _, out _));
        Assert.False(Decks.TryParsePage("dance", out _, out _));
        Assert.False(Decks.TryParsePage("", out _, out _));
        Assert.Equal("page 5", Decks.DescribePage("5"));
        Assert.Equal("the last page", Decks.DescribePage("last"));
        Assert.Equal("dance", Decks.DescribePage("dance"));
        Assert.Equal(1, Decks.Resolve("first", 7, 12));
        Assert.Equal(12, Decks.Resolve("last", 7, 12));
        Assert.Equal(8, Decks.Resolve("next", 7, 12));
        Assert.Equal(12, Decks.Resolve("next", 12, 12));   // clamped at the end
        Assert.Equal(6, Decks.Resolve("prev", 7, 12));
        Assert.Equal(12, Decks.Resolve("99", 7, 12));
        Assert.Equal(0, Decks.Resolve("dance", 7, 12));
        Assert.Equal(0, Decks.Resolve("next", 1, 0));

        Assert.Equal(new SKSizeI(1920, 1080), Decks.RasterCeiling(null));
        Assert.Equal(new SKSizeI(1440, 1080), Decks.FitInto(new SKSize(1024, 768), new SKSizeI(1920, 1080)));
        Assert.Equal(new SKSizeI(3840, 2160), Decks.FitInto(new SKSize(800, 450), new SKSizeI(3840, 2160)));
        Assert.Equal(new SKSizeI(607, 1080), Decks.FitInto(new SKSize(595, 1058), new SKSizeI(1920, 1080)));   // a portrait page
        Assert.Equal(new SKSizeI(1080, 1080), Decks.FitInto(new SKSize(0, 0), new SKSizeI(1920, 1080)));   // a broken page: a square at the ceiling

        Assert.Equal("deck:C:\\show\\deck.pdf", InputKeys.Deck("C:\\show\\deck.pdf"));
        Assert.Equal("", InputKeys.Deck(" "));
        Assert.True(PlaylistSequencer.IsDeckPath("talk.PDF"));
        Assert.True(PlaylistSequencer.IsDeckPath("talk.pptx"));    // a PowerPoint is a deck too — through LibreOffice
        Assert.False(PlaylistSequencer.IsDeckPath("talk.docx"));
        Assert.Equal(LibraryMediaKind.Deck, MediaLibraryEntry.KindOf("talk.pdf", false));
    }

    [Fact]
    public void TheShowWantsItsDeckAndThePageIsDrawnAtItsOwnShape()
    {
        var state = RenderTestHarness.State(s =>
        {
            s.Pattern.Kind = PatternKind.Media;
            s.Pattern.Media.Source = MediaSource.Deck;
            s.Pattern.Media.DeckPath = "C:\\show\\deck.pdf";
            s.Pattern.Media.DeckStartPage = 2;
            s.Pattern.Media.BackgroundColor = "#000000";
        });
        var wanted = Assert.Single(MediaLocator.FindWantedInputs(RenderTestHarness.Snap(state)));
        Assert.Equal((MediaLocator.WantedKind.Deck, "deck:C:\\show\\deck.pdf", "C:\\show\\deck.pdf", "2"), (wanted.Kind, wanted.Key, wanted.Target, wanted.Format));
        Assert.Equal(RedrawCadence.Continuous, PatternEngine.CadenceOf(RenderTestHarness.Snap(state), null, RenderTestHarness.FixedUtcNow));

        InputBus.Clear();
        try
        {
            // Nothing mounted: a card that names the file, never a crash.
            using var card = RenderTestHarness.Render(state, 1280, 720);
            Assert.NotEqual(SKColors.Blue, card.GetPixel(640, 360));

            // A 4:3 page on a 16:9 canvas: fitted, bars either side, never stretched (Tile reads as Fit).
            var deck = new FakeDeckSource("C:\\show\\deck.pdf", 3, SKColors.Blue);
            InputBus.Mount(wanted.Key, deck);
            state.Pattern.Media.Fit = FitMode.Tile;
            using var bmp = RenderTestHarness.Render(state, 1280, 720);
            Assert.Equal(SKColors.Blue, bmp.GetPixel(640, 360));
            Assert.Equal(SKColors.Blue, bmp.GetPixel(170, 360));
            Assert.Equal(SKColors.Black, bmp.GetPixel(150, 360));
            Assert.Equal(SKColors.Black, bmp.GetPixel(1130, 360));

            // Fill covers the canvas; the area of interest applies to a page like any picture.
            state.Pattern.Media.Fit = FitMode.Fill;
            using var filled = RenderTestHarness.Render(state, 1280, 720);
            Assert.Equal(SKColors.Blue, filled.GetPixel(10, 360));
        }
        finally
        {
            InputBus.Clear();
        }
    }

    [Fact]
    public void TheWireAndOscTurnTheDeck()
    {
        Assert.Equal(RemoteCommandKind.DeckNext, ControlProtocol.Parse("DECK NEXT").Kind);
        Assert.Equal(RemoteCommandKind.DeckPrev, ControlProtocol.Parse("DECK PREV").Kind);
        Assert.Equal(RemoteCommandKind.DeckPrev, ControlProtocol.Parse("pdf back").Kind);
        var page = ControlProtocol.Parse("DECK PAGE 5");
        Assert.Equal((RemoteCommandKind.DeckPage, 5, ""), (page.Kind, page.IntArg, page.TextArg));
        var bare = ControlProtocol.Parse("SLIDES 7");
        Assert.Equal((RemoteCommandKind.DeckPage, 7), (bare.Kind, bare.IntArg));
        var first = ControlProtocol.Parse("DECK FIRST");
        Assert.Equal((RemoteCommandKind.DeckPage, 0, "first"), (first.Kind, first.IntArg, first.TextArg));
        Assert.Equal("last", ControlProtocol.Parse("DECK LAST").TextArg);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("DECK").Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("DECK dance").Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("DECK PAGE 0").Kind);

        Assert.Equal("DECK NEXT", OscMap.ToLine(OscMessage.Of("/patterns/deck/next")));
        Assert.Equal("DECK PREV", OscMap.ToLine(OscMessage.Of("/patterns/deck/prev")));
        Assert.Equal("DECK FIRST", OscMap.ToLine(OscMessage.Of("/patterns/deck/first")));
        Assert.Equal("DECK PAGE 5", OscMap.ToLine(OscMessage.Of("/patterns/deck/page/5")));
        Assert.Equal("DECK PAGE 5", OscMap.ToLine(OscMessage.Of("/patterns/deck/page", 5)));
        Assert.Equal("DECK 3", OscMap.ToLine(OscMessage.Of("/patterns/deck", 3)));
        Assert.Equal("DECK LAST", OscMap.ToLine(OscMessage.Of("/patterns/pdf", "last")));
        Assert.Null(OscMap.ToLine(OscMessage.Of("/patterns/deck")));
        Assert.Contains(OscMap.Reference, r => r.Address.StartsWith("/patterns/deck/page"));
    }

    [Fact]
    public void ACueTurnsTheDeckThroughTheSpecTheChecksTheSummaryAndTheSheet()
    {
        Assert.Equal((TargetKind.None, ValueKind.DeckPage), CueActionSpec.For(CueActionKind.DeckPage));
        Assert.Equal((TargetKind.None, ValueKind.None), CueActionSpec.For(CueActionKind.DeckNext));
        Assert.Contains(CueActionKind.DeckPage, CueActionSpec.Editable);
        Assert.Equal(CueActionKind.DeckNext, CueSheet.ParseKind("next page"));
        Assert.Equal(CueActionKind.DeckNext, CueSheet.ParseKind("Deck — next page"));
        Assert.Equal(CueActionKind.DeckPrev, CueSheet.ParseKind("previous slide"));
        Assert.Equal(CueActionKind.DeckPage, CueSheet.ParseKind("deck"));

        var state = new ShowState();
        state.Pattern.Kind = PatternKind.Media;
        state.Pattern.Media.Source = MediaSource.Deck;
        state.Pattern.Media.DeckPath = "C:\\show\\deck.pdf";
        var stack = new CueStackConfig();
        var next = new RunCueConfig { Number = "01.010", Name = "Next" };
        next.Actions.Add(new CueActionConfig { Kind = CueActionKind.DeckNext });
        var goTo = new RunCueConfig { Number = "01.020", Name = "Page 5" };
        goTo.Actions.Add(new CueActionConfig { Kind = CueActionKind.DeckPage, Value = "5" });
        var bad = new RunCueConfig { Number = "01.030", Name = "Bad" };
        bad.Actions.Add(new CueActionConfig { Kind = CueActionKind.DeckPage, Value = "dance" });
        foreach (var cue in new[] { next, goTo, bad }) stack.Cues.Add(cue);
        var report = CueValidator.Validate(state, stack);
        Assert.False(report.IsBroken(next.Id));
        Assert.False(report.Warnings.ContainsKey(next.Id));
        Assert.False(report.IsBroken(goTo.Id));
        Assert.Contains("a number", report.ReasonFor(bad.Id));

        // No deck in the show: a soft note — the deck may come on by hand before the cue.
        var bare = new ShowState();
        var bareStack = new CueStackConfig();
        var bareCue = new RunCueConfig { Number = "01.010", Name = "Next" };
        bareCue.Actions.Add(new CueActionConfig { Kind = CueActionKind.DeckNext });
        bareStack.Cues.Add(bareCue);
        var bareReport = CueValidator.Validate(bare, bareStack);
        Assert.False(bareReport.IsBroken(bareCue.Id));
        Assert.Contains("no deck is on air", bareReport.Warnings[bareCue.Id]);

        Assert.Equal("Deck: the next page", CueSummary.DescribeAction(state, next.Actions[0]));
        Assert.Equal("Deck: page 5", CueSummary.DescribeAction(state, goTo.Actions[0]));
        Assert.Equal("Deck: the last page", CueSummary.DescribeAction(state, new CueActionConfig { Kind = CueActionKind.DeckPage, Value = "last" }));
        Assert.Equal("Deck: the previous page", CueSummary.DescribeAction(state, new CueActionConfig { Kind = CueActionKind.DeckPrev }));
    }
}
