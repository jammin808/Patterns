using System.Text.Json;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 67.4: a library tile clicked is the editing target's picture at once — EDIT SAFE opens first so
/// the air never moves, the target tile's PVW shows it on the same publish, a target that followed the
/// programme is its own picture from that press, and the tile stays lit on the page.
/// </summary>
public class LibraryFlowAppTests
{
    private static List<ScreenInfo> TwoScreens() => new()
    {
        new("a", "Left", new PixelRect(0, 0, 1920, 1080), 1.0, true, 0),
        new("b", "Right", new PixelRect(3000, 0, 1920, 1080), 1.0, false, 1),
    };

    private static void Rig(TestApp.Booted b)
    {
        var fakes = TwoScreens();
        b.Services.Screens.All.Clear();
        foreach (var s in fakes) b.Services.Screens.All.Add(s);
        b.Vm.State.Output.Placements.Clear();
        b.Vm.ReconcilePlacements(fakes);
        foreach (var p in b.Vm.State.Output.Placements) p.Enabled = true;
        b.Vm.RebuildSwitcherTiles(fakes);
        Dispatcher.UIThread.RunJobs();
    }

    [AvaloniaFact]
    public void ALibraryTileLandsInTheEditingTargetsPreviewUnderEditSafeAndLightsUp()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            Rig(b);
            vm.IsSandboxActive = false;
            vm.State.Pattern.Kind = PatternKind.Grid;
            Dispatcher.UIThread.RunJobs();
            var air = services.Bus.Current;
            Assert.Equal(PatternKind.Grid, air.PatternFor("b").Kind);

            // The right tile selected, EDIT SAFE off: a built-in pattern preset from the library.
            vm.SelectTileCommand.Execute(vm.SwitcherTiles.Single(t => t.TargetId == "b"));
            var preset = vm.LibraryAll.First(i => i.Section == "Patterns" && i.ThumbConfig?.Invoke(vm.State)?.Kind is { } k && k != PatternKind.Grid);
            vm.ApplyPresetCommand.Execute(preset);
            Dispatcher.UIThread.RunJobs();

            var why = $"sandbox {services.Sandbox.Active} · own {ContentTargets.UsesOwnPattern(vm.State, "b")} · target {vm.EditTarget} · status '{vm.StatusMessage}' · item {preset.Section}/{preset.Name} · active kind {vm.ActivePattern.Kind}";
            Assert.True(vm.IsSandboxActive, "EDIT SAFE opened first: " + why);
            Assert.True(ContentTargets.UsesOwnPattern(vm.State, "b"), why);                    // its own from this press
            Assert.NotEqual(PatternKind.Grid, services.Bus.Sandbox!.PatternFor("b").Kind);      // the tile's PVW shows it
            Assert.Equal(PatternKind.Grid, services.Bus.Sandbox!.PatternFor("a").Kind);         // the other tile does not
            Assert.Equal(PatternKind.Grid, vm.State.Pattern.Kind);                              // the programme's preview untouched
            Assert.Equal(PatternKind.Grid, services.Bus.Current.PatternFor("b").Kind);          // the air never moved
            Assert.Same(preset, vm.SelectedLibraryItem);
            Assert.True(preset.IsSelected);
            Assert.Contains(preset.Name, vm.StatusMessage);
            Assert.Contains("Right", vm.StatusMessage);
            Assert.Contains("CUT or TAKE", vm.StatusMessage);

            // Another tile chosen: the light moves.
            var other = vm.LibraryAll.First(i => i.Section == "Patterns" && !ReferenceEquals(i, preset) && i.ThumbConfig?.Invoke(vm.State)?.Kind is { } k2 && k2 != PatternKind.Grid);
            vm.ApplyPresetCommand.Execute(other);
            Dispatcher.UIThread.RunJobs();
            Assert.False(preset.IsSelected);
            Assert.True(other.IsSelected);

            // With the PGM tile selected the library lands on the programme's preview, and says so.
            vm.SelectTileCommand.Execute(vm.SwitcherTiles.Single(t => t.IsProgramTile));
            vm.ApplyPresetCommand.Execute(preset);
            Dispatcher.UIThread.RunJobs();
            Assert.Contains("programme", vm.StatusMessage);
            Assert.NotEqual(PatternKind.Grid, vm.State.Pattern.Kind);
            Assert.Equal(PatternKind.Grid, services.Bus.Current.State.Pattern.Kind);           // still not on air
        }
        finally
        {
            b.Dispose();
        }
    }

    private static string Send(CommandRouter router, string line) => TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse(line)));

    private static PatternKind KindOf(PresetItem tile, ShowState state) => tile.ThumbConfig?.Invoke(state)?.Kind ?? PatternKind.Grid;

    /// <summary>
    /// Round 73: the tile says which page edits what it made, OPEN goes there, STATE and the Eye say what the
    /// editors are on, and a deck's LIBRARY key is the same press — on the desk's editing target, the
    /// programme, or a screen by number — journaled through the one action layer.
    /// </summary>
    [AvaloniaFact]
    public void AFractalTileNamesItsEditorOpenGoesThereAndTheWireDoesTheSamePress()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            Rig(b);
            vm.IsSandboxActive = false;
            vm.State.Pattern.Kind = PatternKind.Grid;
            Dispatcher.UIThread.RunJobs();
            var router = new CommandRouter(services);

            var fractal = vm.LibraryAll.FirstOrDefault(i => i.Section == "Fractals" && KindOf(i, vm.State) == PatternKind.Fractal);
            Assert.NotNull(fractal);
            var particles = vm.LibraryAll.FirstOrDefault(i => i.Section == "Particles" && KindOf(i, vm.State) == PatternKind.Particles);
            Assert.NotNull(particles);
            Assert.False(vm.HasLibrarySelection);

            // The right tile selected: a fractal scene lands there, and the strip names its studio.
            vm.SelectTileCommand.Execute(vm.SwitcherTiles.Single(t => t.TargetId == "b"));
            vm.ApplyPresetCommand.Execute(fractal);
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.HasLibrarySelection, vm.StatusMessage);
            Assert.Equal(PatternKind.Fractal, services.Bus.Sandbox!.PatternFor("b").Kind);
            Assert.Equal(PatternKind.Grid, services.Bus.Current.PatternFor("b").Kind);              // the air never moved
            Assert.Equal("Fractals", vm.LibraryEditorPage);
            Assert.Equal("OPEN FRACTALS", vm.LibraryOpenEditorText);
            Assert.Contains("Right", vm.LibrarySelectionWhere);
            Assert.Contains("Fractal", vm.LibrarySelectionWhere);
            Assert.StartsWith(fractal!.Name, vm.LibrarySelectionTitle);
            Assert.Contains("OPEN FRACTALS", vm.StatusMessage);

            // STATE's editing row and the Eye's desk node say what the editors are on.
            var editing = JsonDocument.Parse(router.StateJson()).RootElement.GetProperty("editing");
            Assert.Equal("b", editing.GetProperty("target").GetString());
            Assert.True(editing.GetProperty("own").GetBoolean());
            Assert.Equal("Fractal", editing.GetProperty("kind").GetString());
            Assert.Equal("Fractals", editing.GetProperty("editor").GetString());
            Assert.Equal(fractal.Name, editing.GetProperty("library").GetString());
            Assert.Equal("Fractals", editing.GetProperty("librarySection").GetString());
            Assert.Contains("Right", editing.GetProperty("words").GetString());
            services.Eye.Refresh();
            // The label is the wall's own ("2 · Right"), so a deck and the desk name the same tile.
            Assert.Contains(services.Eye.Graph.Find("desk")!.Words, w => w.StartsWith("Editing ", StringComparison.Ordinal) && w.Contains("Right's preview (its own picture)", StringComparison.Ordinal) && w.Contains("Fractals page", StringComparison.Ordinal) && w.Contains(fractal.Name, StringComparison.Ordinal));

            // OPEN FRACTALS: the studio, with the same target under its editors.
            vm.OpenLibraryEditorCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Shell.IndexOf("Fractals"), vm.SelectedPageIndex);
            Assert.Equal("b", vm.EditTarget.ScreenId);

            // The wire: LIBRARY <name> is the same press on the desk's editing target — the left tile now.
            vm.SelectTileCommand.Execute(vm.SwitcherTiles.Single(t => t.TargetId == "a"));
            Dispatcher.UIThread.RunJobs();
            var reply = Send(router, "LIBRARY " + particles!.Name);
            Dispatcher.UIThread.RunJobs();
            Assert.StartsWith("OK", reply);
            Assert.Equal(PatternKind.Particles, services.Bus.Sandbox!.PatternFor("a").Kind);
            Assert.Equal(PatternKind.Fractal, services.Bus.Sandbox!.PatternFor("b").Kind);          // the other tile keeps its picture
            Assert.Equal(PatternKind.Grid, vm.State.Pattern.Kind);                                   // the programme's preview untouched

            // A screen by number, and the programme by name; the air still never moved.
            Send(router, "SCREEN 2 PVW LIBRARY " + particles.Name.ToUpperInvariant());              // names are case-blind
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.Particles, services.Bus.Sandbox!.PatternFor("b").Kind);
            Send(router, "PVW LIBRARY " + fractal.Name);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(PatternKind.Fractal, vm.State.Pattern.Kind);
            Assert.Equal(PatternKind.Grid, services.Bus.Current.State.Pattern.Kind);
            Assert.Equal(PatternKind.Grid, services.Bus.Current.PatternFor("a").Kind);
            Assert.Equal(PatternKind.Grid, services.Bus.Current.PatternFor("b").Kind);

            // A tile the library does not have is refused with the words, and nothing moves.
            var refused = Send(router, "LIBRARY No such tile anywhere");
            Assert.StartsWith("ERR", refused);
            Assert.Contains("Library", refused);
            Assert.Equal(PatternKind.Fractal, vm.State.Pattern.Kind);
        }
        finally
        {
            b.Dispose();
        }
    }

    /// <summary>The page's tiles and the action layer's catalogue are one list, found by id, by name, by section/name.</summary>
    [AvaloniaFact]
    public void ThePagesTilesAndTheWiresCatalogueAreOneList()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.State.MediaLibrary.Add(new MediaLibraryEntry { Path = "C:/show/opener.mp4", IsVideo = true, Name = "Opener" });
            vm.State.Web.SavedUrls.Add("https://www.youtube.com/watch?v=abc123");
            vm.RefreshLibrary();
            Dispatcher.UIThread.RunJobs();

            var tiles = LibraryItems.Build(vm.State, services.Store);
            Assert.Equal(vm.LibraryAll.Select(t => t.Id), tiles.Select(t => t.Id));
            Assert.Equal(vm.LibraryAll.Select(t => (t.Section, t.Category, t.Name)), tiles.Select(t => (t.Section, t.Category, t.Name)));
            Assert.Contains(tiles, t => t.Id.StartsWith("media:", StringComparison.Ordinal) && t.Name == "Opener" && t.Section == "Videos" && t.IsPicture && t.Remove is not null);
            Assert.Contains(tiles, t => t.Id.StartsWith("web:", StringComparison.Ordinal) && t.Category == "YouTube" && t.Swatch is not null);

            var opener = tiles.First(t => t.Name == "Opener");
            Assert.Same(opener, LibraryItems.Find(tiles, opener.Id));
            Assert.Same(opener, LibraryItems.Find(tiles, "opener"));
            Assert.Same(opener, LibraryItems.Find(tiles, "Videos/Opener"));
            Assert.Same(opener, LibraryItems.Find(tiles, "videos:OPENER"));
            Assert.Null(LibraryItems.Find(tiles, "nothing of the kind"));
            Assert.Null(LibraryItems.Find(tiles, ""));

            var picture = new PatternConfig();
            opener.Picture!(picture);
            Assert.Equal(PatternKind.Media, picture.Kind);
            Assert.Equal(MediaSource.Video, picture.Media.Source);
            Assert.Equal("C:/show/opener.mp4", picture.Media.VideoPath);
        }
        finally
        {
            b.Dispose();
        }
    }
}
