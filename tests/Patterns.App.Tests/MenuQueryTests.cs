using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 60: MENU on the wire. The same right-click menu the desk shows, answered as JSON with
/// each entry's wire line — so a tablet or a script offers the desk's own choices and sends the
/// desk's own words back, and every line it could send parses on the desk.
/// </summary>
public class MenuQueryTests
{
    private static List<ScreenInfo> ThreeScreens() => new()
    {
        new("a", "Left", new Avalonia.PixelRect(0, 0, 1920, 1080), 1.0, true, 0),
        new("b", "Right", new Avalonia.PixelRect(3000, 0, 1920, 1080), 1.0, false, 1),
        new("c", "Lobby", new Avalonia.PixelRect(6000, 0, 1920, 1080), 1.0, false, 2),
    };

    private static void Rig(TestApp.Booted b)
    {
        var fakes = ThreeScreens();
        b.Services.Screens.All.Clear();
        foreach (var s in fakes) b.Services.Screens.All.Add(s);
        b.Vm.State.Output.Placements.Clear();
        b.Vm.ReconcilePlacements(fakes);
        foreach (var p in b.Vm.State.Output.Placements) p.Enabled = true;
        b.Vm.RebuildSwitcherTiles(fakes);
        Dispatcher.UIThread.RunJobs();
    }

    private static string Send(CommandRouter router, string line) => TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse(line)));

    private static JsonElement Menu(CommandRouter router, string line)
    {
        var reply = Send(router, line);
        Assert.StartsWith("OK ", reply);
        return JsonDocument.Parse(reply[3..]).RootElement;
    }

    private static IEnumerable<JsonElement> Entries(JsonElement menu)
    {
        foreach (var group in menu.GetProperty("groups").EnumerateArray())
        {
            foreach (var entry in group.GetProperty("entries").EnumerateArray())
            {
                yield return entry;
                foreach (var child in entry.GetProperty("children").EnumerateArray()) yield return child;
            }
        }
    }

    [AvaloniaFact]
    public void MenuAnswersTheDesksOwnMenuAndEveryLineItOffersParses()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            Rig(b);
            vm.ActivePattern.Kind = PatternKind.Grid;
            vm.Show.NewLookName = "Walk-in";
            vm.Show.SaveLookCommand.Execute(null);
            var design = vm.NewLowerThird("Clean");
            var cue = vm.Cues.AddCue();
            cue.Name = "Keynote";
            Dispatcher.UIThread.RunJobs();
            var router = new CommandRouter(services);

            var screen = Menu(router, "MENU SCREEN 2");
            Assert.Equal("screen", screen.GetProperty("kind").GetString());
            Assert.Equal("b", screen.GetProperty("subject").GetString());
            Assert.StartsWith("2 · ", screen.GetProperty("title").GetString());
            var wires = Entries(screen).Select(e => e.GetProperty("wire").GetString()!).Where(w => w.Length > 0).ToList();
            Assert.Contains("SCREEN 2 PVW LOOK Walk-in", wires);
            Assert.Contains("SCREEN 2 PVW RESET", wires);
            Assert.Contains("LOCK 2 ON", wires);
            foreach (var wire in wires) Assert.True(ControlProtocol.Parse(wire).IsAction, wire);
            // A disabled entry says why, in the JSON too: the look just saved is the picture on air, so there is nothing to reset.
            var reset = Entries(screen).First(e => e.GetProperty("id").GetString() == "stage.reset");
            Assert.False(reset.GetProperty("enabled").GetBoolean());
            Assert.Contains("exactly as the look asked", reset.GetProperty("because").GetString());

            Assert.Equal("program", Menu(router, "MENU").GetProperty("kind").GetString());
            Assert.Equal("program", Menu(router, "MENU PGM").GetProperty("kind").GetString());
            Assert.Equal("preview", Menu(router, "MENU PREVIEW").GetProperty("kind").GetString());
            var cueMenu = Menu(router, "MENU CUE Keynote");
            Assert.Equal("cue", cueMenu.GetProperty("kind").GetString());
            Assert.Equal(cue.Id, cueMenu.GetProperty("subject").GetString());
            Assert.Contains(Entries(cueMenu), e => e.GetProperty("id").GetString() == "cue.transition");
            Assert.Equal("cue", Menu(router, $"MENU CUE {cue.Number}").GetProperty("kind").GetString());
            Assert.Equal("look", Menu(router, "MENU LOOK Walk-in").GetProperty("kind").GetString());
            Assert.Equal("lowerthird", Menu(router, "MENU LT 1").GetProperty("kind").GetString());
            Assert.Equal(design.Id, Menu(router, $"MENU LT {design.Name}").GetProperty("subject").GetString());
            Assert.Equal("layer", Menu(router, "MENU LAYER 2").GetProperty("kind").GetString());
            Assert.Equal("layer2", Menu(router, "MENU LAYER 2").GetProperty("subject").GetString());
            Assert.Equal("overlay", Menu(router, "MENU CLOCK").GetProperty("kind").GetString());
            Assert.Equal("countdown", Menu(router, "MENU COUNTDOWN").GetProperty("subject").GetString());
            Assert.Equal("pip", Menu(router, "MENU OVERLAY pip").GetProperty("subject").GetString());

            // A stranger is an ERR that says what MENU takes.
            Assert.StartsWith("ERR", Send(router, "MENU DANCE"));
            Assert.Contains("SCREEN n", Send(router, "MENU DANCE"));
            Assert.StartsWith("ERR", Send(router, "MENU SCREEN 9"));
            Assert.StartsWith("ERR", Send(router, "MENU LOOK Nope"));
            Assert.StartsWith("ERR", Send(router, "MENU LAYER 3"));
        }
        finally
        {
            b.Window.Close();
        }
    }
}
