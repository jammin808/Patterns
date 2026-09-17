using System.Text.Json;
using Patterns.Core.Menus;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 60: the right-click menus as a pure model. Built from facts, read back by a test: every
/// entry has words, every entry that cannot be chosen says why, every wire line an entry carries
/// parses back to the action the entry runs, and the cue and preview edits the menus offer land
/// on a show as the steps and the settings the editors would show.
/// </summary>
public class DeskMenuTests
{
    private static DeskFacts Facts(bool sandbox = true, bool lookOnAir = true, bool edited = false, bool assistant = true, bool prep = false) => new()
    {
        SandboxOpen = sandbox,
        PrepMode = prep,
        AssistantReady = assistant,
        LookOnAirId = lookOnAir ? "L1" : "",
        LookOnAirName = lookOnAir ? "Walk-in" : "",
        LookEdited = edited,
        Looks = new[]
        {
            new MenuLook("L1", "Walk-in", 1, lookOnAir, false) { UsedBy = new[] { "01.010", "03.020" } },
            new MenuLook("L2", "Keynote", 3, false, sandbox),
            new MenuLook("L3", "Break", 0, false, false),
        },
        Presets = new[] { "Bars", "Moving grid" },
        Designs = new[] { new MenuDesign("D1", "Neon", true, false, false), new MenuDesign("D2", "Clean", false, true, false) },
        People = new[] { new MenuPerson("P1", "Jane Doe", "CEO — Acme"), new MenuPerson("P2", "Sam", "") },
        Media = new[] { new MenuMedia("M1", "foyer.png", false), new MenuMedia("M2", "sizzle.mp4", true) },
        TransitionDefault = "dissolve · 800 ms",
    };

    private static ScreenFacts Screen(bool offLook = false, bool own = false, bool locked = false, bool mirror = false, bool staged = false, string number = "2") => new()
    {
        TargetId = "b",
        Number = number,
        Title = "2 · Stage left",
        OnAir = true,
        Own = own,
        Locked = locked,
        Armed = true,
        Enabled = true,
        OffLook = offLook,
        Staged = staged,
        IsMirror = mirror,
        ShowingKind = "Grid",
        Group = mirror ? "repeater" : "main",
        MirrorSource = mirror ? "1 · Main wall" : "",
    };

    private static CueFacts Cue(bool hasLook = true, bool standby = false, string problem = "") => new()
    {
        Id = "C1",
        Number = "03.020",
        Name = "Keynote",
        IsStandby = standby,
        Problem = problem,
        Summary = "Apply 'Walk-in' + Clock on",
        LookId = hasLook ? "L1" : "",
        LookName = hasLook ? "Walk-in" : "",
        Transition = "cut",
        FollowSeconds = 5,
        Mark = CueMark.Break,
        Overlays = new HashSet<ShowActionKind> { ShowActionKind.ClockOn },
        LowerThirdDesignId = "D1",
        LowerThirdName = "Neon",
        LowerThirdInAfter = 3,
        LowerThirdOutAfter = 8,
    };

    private static MonitorFacts Monitor(string current = "") => new()
    {
        Current = current,
        MainTargetId = "a",
        MainTitle = "1 · Main wall",
        Choices = new[] { new MonitorChoice("a", "1", "1 · Main wall"), new MonitorChoice("b", "2", "2 · Stage left"), new MonitorChoice("a+b", "", "A · Main wall") },
    };

    private static IEnumerable<DeskMenu> Every(DeskFacts d)
    {
        yield return DeskMenus.Screen(d, Screen());
        yield return DeskMenus.Screen(d, Screen(number: "") with { TargetId = "a+b", IsCanvas = true, Title = "A · Main wall" });
        yield return DeskMenus.Program(d);
        yield return DeskMenus.Preview(d);
        yield return DeskMenus.Cue(d, Cue());
        yield return DeskMenus.Look(d, d.Looks[0]);
        yield return DeskMenus.LowerThird(d, d.Designs[0]);
        yield return DeskMenus.Person(d, d.People[0]);
        yield return DeskMenus.Layer(d, new LayerFacts { Index = 1, Enabled = true, Source = LayerSource.Image, Words = "foyer.png" });
        yield return DeskMenus.Monitor(d, Monitor());
        yield return DeskMenus.Monitor(d, Monitor("OFF"));
        yield return DeskMenus.Transport(d);
        foreach (var (kind, _) in PreviewEdits.OverlayKinds)
        {
            yield return DeskMenus.Overlay(d, new OverlayFacts { Kind = kind, AirOn = kind == "clock", PreviewOn = kind is "clock" or "logo", Anchor = kind == "info" ? null : Anchor9.TopRight, Words = kind == "message" ? "Welcome" : "" });
        }
    }

    [Fact]
    public void EveryEntryHasWordsSaysWhyAndItsWireLineParsesBackToItsAction()
    {
        foreach (var sandbox in new[] { true, false })
        {
            foreach (var menu in Every(Facts(sandbox: sandbox)))
            {
                Assert.False(string.IsNullOrWhiteSpace(menu.Title), menu.Kind);
                Assert.NotEmpty(menu.Groups);
                var ids = menu.Flatten().Select(e => e.Id).ToList();
                Assert.Equal(ids.Count, ids.Distinct(StringComparer.Ordinal).Count()); // Find(id) is unambiguous
                foreach (var group in menu.Groups)
                {
                    Assert.False(string.IsNullOrWhiteSpace(group.Heading));
                    Assert.NotEmpty(group.Entries);
                }
                foreach (var e in menu.Flatten())
                {
                    Assert.False(string.IsNullOrWhiteSpace(e.Text), $"{menu.Kind}/{e.Id} has words");
                    Assert.True(e.HasChildren || e.Action is not null || e.Edit.Length > 0 || e.Route is not null || e.Question.Length > 0, $"{menu.Kind}/{e.Id} does something");
                    if (e.HasChildren) Assert.True(e.Action is null && e.Edit.Length == 0 && e.Route is null, $"{menu.Kind}/{e.Id}: a drawer only opens");
                    Assert.Equal(e.Because.Length == 0, e.IsEnabled);
                    if (e.Scope == MenuScope.Go) Assert.False(string.IsNullOrWhiteSpace(e.Route?.Page), $"{menu.Kind}/{e.Id} names a page");
                    if (e.Scope == MenuScope.Ask) Assert.Contains("?", e.Question);
                    if (e.Scope is MenuScope.Preview or MenuScope.Live && e.Action is { } action && e.HasWire)
                    {
                        var cmd = ControlProtocol.Parse(e.Wire);
                        Assert.True(cmd.IsAction, $"{menu.Kind}/{e.Id}: '{e.Wire}' is a line of the wire");
                        Assert.Equal(action.Kind, cmd.Action.Kind);
                    }
                    if (e.Scope == MenuScope.Preview && e.Action is { } preview)
                    {
                        // A preview entry can never go live: a staged verb, a look into the preview, or → PVW.
                        Assert.True(ActionSpec.IsStaged(preview.Kind) || preview.Kind is ShowActionKind.ApplyLookToPreview or ShowActionKind.ScreenToPreview or ShowActionKind.LowerThirdPreview, $"{menu.Kind}/{e.Id}: {preview.Kind} lands in the preview");
                    }
                }
                // The JSON carries the same entries.
                using var doc = JsonDocument.Parse(MenuJson.Write(menu));
                Assert.Equal(menu.Groups.Count, doc.RootElement.GetProperty("groups").GetArrayLength());
                Assert.Equal(menu.Kind, doc.RootElement.GetProperty("kind").GetString());
            }
        }
    }

    [Fact]
    public void EveryMenuEndsWithAMidiGroupWhoseLinesArmLearnAndTickWhatIsBound()
    {
        // Round 73: a desk with a surface open, one control bound to the look on air, and a learn waiting for CUE GO.
        var d = Facts() with
        {
            HasMidiSurface = true,
            MidiSurfaceOpen = true,
            MidiBindings = new[] { new MidiBinding("APC40", "NOTE 1 53 *", "LOOK Walk-in") },
            MidiLearning = "CUE GO",
        };
        foreach (var menu in Every(d))
        {
            // One line per wire line the menu carries — the drawers' choices included, each once; a menu the wire has no words for has no MIDI group.
            var wires = menu.Flatten().Where(e => e.Scope != MenuScope.Learn && e.HasWire).Select(e => MidiBindings.Normalise(e.Wire)).Distinct(StringComparer.OrdinalIgnoreCase).ToList();
            if (wires.Count == 0)
            {
                Assert.DoesNotContain(menu.Groups, g => g.Heading == "MIDI");
                continue;
            }
            var group = menu.Groups[^1];
            Assert.Equal("MIDI", group.Heading);
            Assert.Equal(MenuTone.Tile, group.Tone);
            Assert.Equal(DeskMenus.MidiNote, group.Note);
            var drawer = menu.Find("midi.learn")!;
            Assert.True(drawer.IsEnabled, $"{menu.Kind}: {drawer.Because}");
            Assert.True(drawer.HasChildren);
            Assert.True(drawer.IsOn);                                                   // a learn waits
            Assert.Contains("CUE GO", drawer.Text);
            Assert.Equal(wires.Count, drawer.Children.Count);
            foreach (var child in drawer.Children)
            {
                Assert.Equal(MenuScope.Learn, child.Scope);
                Assert.StartsWith("MIDI LEARN ", child.Wire, StringComparison.Ordinal);
                var cmd = ControlProtocol.Parse(child.Wire);
                Assert.Equal(child.Action, cmd.Action);
                Assert.Equal(ShowActionKind.MidiLearn, cmd.Action.Kind);
                Assert.Contains(cmd.Action.Value, wires);
                Assert.True(ControlProtocol.Parse(cmd.Action.Value).IsAction, $"{menu.Kind}: '{cmd.Action.Value}' is a line a control can do");
            }
            var off = menu.Find("midi.learn.off")!;
            Assert.Equal(ShowActionKind.MidiLearnOff, ControlProtocol.Parse(off.Wire).Action.Kind);
            Assert.Equal(off.Action, ControlProtocol.Parse(off.Wire).Action);
        }

        // The look's menu: its "on air" line is bound — ticked, its control named, a FORGET beside it; the preview line is not.
        var look = DeskMenus.Look(d, d.Looks[0]);
        var bound = look.Find("midi.learn:look.air")!;
        Assert.True(bound.IsOn);
        Assert.Contains("NOTE 1 53 (APC40)", bound.Detail);
        Assert.False(look.Find("midi.learn:look.preview")!.IsOn);
        Assert.Contains("then press the control", look.Find("midi.learn:look.preview")!.Detail);
        var forget = look.Find("midi.forget:look.air")!;
        Assert.Equal(new ShowAction(ShowActionKind.MidiForget, "", "LOOK Walk-in"), ControlProtocol.Parse(forget.Wire).Action);
        Assert.Equal(forget.Action, ControlProtocol.Parse(forget.Wire).Action);
        Assert.Null(look.Find("midi.forget:look.preview"));
        // The wire's JSON carries the group with scope "learn".
        using var doc = JsonDocument.Parse(MenuJson.Write(look));
        var midi = doc.RootElement.GetProperty("groups").EnumerateArray().Last();
        Assert.Equal("MIDI", midi.GetProperty("heading").GetString());
        Assert.Equal("learn", midi.GetProperty("entries")[0].GetProperty("scope").GetString());
        Assert.Equal("MIDI LEARN LOOK Walk-in", midi.GetProperty("entries")[0].GetProperty("children").EnumerateArray().First(c => c.GetProperty("id").GetString() == "midi.learn:look.air").GetProperty("wire").GetString());

        // The line learn waits for says so in every menu that carries it.
        var transport = DeskMenus.Transport(d);
        Assert.Contains("LEARNING", transport.Find("midi.learn:go")!.Detail);
        Assert.True(transport.Find("midi.learn:go")!.IsOn);

        // No surface: the drawer says where to add one; none open: says so; no learn waiting: no cancel line; a node has no group.
        var bare = DeskMenus.Look(Facts(), Facts().Looks[0]);
        Assert.False(bare.Find("midi.learn")!.IsEnabled);
        Assert.Contains("Interactive page", bare.Find("midi.learn")!.Because);
        Assert.Null(bare.Find("midi.learn.off"));
        var closed = DeskMenus.Look(Facts() with { HasMidiSurface = true }, Facts().Looks[0]);
        Assert.Contains("No surface is open", closed.Find("midi.learn")!.Because);
        var node = DeskMenus.Look(Facts() with { IsNode = true }, Facts().Looks[0]);
        Assert.DoesNotContain(node.Groups, g => g.Heading == "MIDI");
        Assert.Null(node.Find("midi.learn"));
    }

    [Fact]
    public void TheTransportMenuIsTheRunSurfacesButtonsAndEveryLineIsItsOwnWire()
    {
        // Round 73: GO, standby, HOLD, BLACKOUT, STOP ALL, the clicker — learnable like everything else; TAKE and CUT are not on a wire.
        var menu = DeskMenus.Transport(Facts() with { HasMidiSurface = true, MidiSurfaceOpen = true });
        Assert.Equal("transport", menu.Kind);
        var live = menu.Groups[0];
        Assert.Equal("LIVE", live.Heading);
        Assert.Equal(DeskMenus.LiveNote, live.Note);
        foreach (var e in live.Entries)
        {
            Assert.Equal(MenuScope.Live, e.Scope);
            var cmd = ControlProtocol.Parse(e.Wire);
            Assert.True(cmd.IsAction, e.Wire);
            Assert.Equal(cmd.Action, e.Action);
        }
        Assert.Equal(new ShowAction(ShowActionKind.CueGo), menu.Find("go")!.Action);
        Assert.Equal(ShowActionKind.CueStandby, menu.Find("standby.next")!.Action!.Value.Kind);
        Assert.Equal(ShowActionKind.CueHoldOn, menu.Find("hold.on")!.Action!.Value.Kind);
        Assert.Equal(ShowActionKind.BlackoutToggle, menu.Find("blackout")!.Action!.Value.Kind);
        Assert.Equal(ShowActionKind.StopAll, menu.Find("stopall")!.Action!.Value.Kind);
        Assert.Equal(ShowActionKind.PresenterNext, menu.Find("next")!.Action!.Value.Kind);
        Assert.Null(menu.Find("take"));
        Assert.Null(menu.Find("cut"));
        Assert.Equal(live.Entries.Count, menu.Find("midi.learn")!.Children.Count);
    }

    [Fact]
    public void AScreenShowingAStudioPictureHasItsEditorInTheMenu()
    {
        // Round 73: a fractal, particles, a reactive scene or media has a studio of its own — one entry away.
        var fractal = DeskMenus.Screen(Facts(), Screen() with { ShowingKind = "Fractal" });
        var editor = fractal.Find("go.editor")!;
        Assert.Equal(MenuScope.Go, editor.Scope);
        Assert.Equal("Fractals", editor.Route!.Page);
        Assert.Equal("b", editor.Route!.Item);
        Assert.Contains("Fractal", editor.Text);
        Assert.Equal("Media", DeskMenus.Screen(Facts(), Screen() with { ShowingKind = "Media" }).Find("go.editor")!.Route!.Page);
        // A grid is edited on the Pattern page, which the menu already names.
        Assert.Null(DeskMenus.Screen(Facts(), Screen()).Find("go.editor"));
        Assert.Equal("Pattern", DeskMenus.Screen(Facts(), Screen()).Find("go.pattern")!.Route!.Page);
    }

    [Fact]
    public void TheScreenMenuStagesOnThePvwAndSaysWhyWhenItCannot()
    {
        var d = Facts();
        var menu = DeskMenus.Screen(d, Screen(offLook: true, own: true));
        Assert.Contains("off the look 'Walk-in'", menu.Subtitle);
        Assert.Equal(MenuTone.Warn, menu.Tone);

        var reset = menu.Find("stage.reset")!;
        Assert.True(reset.IsEnabled);
        Assert.Equal(new ShowAction(ShowActionKind.ScreenStageReset, "b"), reset.Action);
        Assert.Equal("SCREEN 2 PVW RESET", reset.Wire);
        Assert.Contains("Walk-in", reset.Text);

        var look = menu.Find("stage.look:L2")!;
        Assert.Equal(new ShowAction(ShowActionKind.ScreenStageLook, "b", "L2"), look.Action);
        Assert.Equal("SCREEN 2 PVW LOOK Keynote", look.Wire);
        Assert.Contains("F3", look.Detail);
        Assert.Equal("SCREEN 2 PVW PRESET Bars", menu.Find("stage.preset:Bars")!.Wire);
        var grid = menu.Find("stage.kind:Grid")!;
        Assert.True(grid.IsOn); // what it shows now
        Assert.Equal("SCREEN 2 PVW PATTERN Grid", grid.Wire);
        Assert.Equal("SCREEN 2 PVW PROGRAM", menu.Find("stage.program")!.Wire);
        Assert.Equal("SCREEN 2 PVW", menu.Find("to.preview")!.Wire);
        Assert.Equal("LOCK 2 ON", menu.Find("tile.lock")!.Wire);
        Assert.Equal("SCREEN 2 OFF", menu.Find("tile.out")!.Wire);
        Assert.Equal(MenuScope.Live, menu.Find("tile.lock")!.Scope);
        Assert.Equal("Screens", menu.Find("go.screens")!.Route!.Page);
        Assert.Equal("b", menu.Find("go.screens")!.Route!.Item);
        Assert.Contains("gone its own way", menu.Find("ask.screen")!.Question);

        // On the look, following the programme: reset and follow say why.
        var quiet = DeskMenus.Screen(d, Screen());
        Assert.Contains("exactly as the look asked", quiet.Find("stage.reset")!.Because);
        Assert.Contains("follows the programme already", quiet.Find("stage.program")!.Because);
        Assert.True(quiet.Find("stage.look:L1")!.IsOn); // the look on air is ticked in the drawer

        // No look on air, locked, a repeater, no assistant key.
        var bare = DeskMenus.Screen(Facts(lookOnAir: false, assistant: false) with { Looks = Array.Empty<MenuLook>(), Presets = Array.Empty<string>() }, Screen(locked: true, mirror: true));
        Assert.Contains("No look is on air", bare.Find("stage.reset")!.Because);
        Assert.Contains("No looks yet", bare.Find("stage.look")!.Because);
        Assert.Contains("No presets yet", bare.Find("stage.preset")!.Because);
        Assert.Contains("repeater", bare.Find("stage.program")!.Because);
        Assert.Contains("locked tile", bare.Find("tile.arm")!.Because);
        Assert.Contains("Unlock", bare.Find("tile.lock")!.Text);
        Assert.Contains("API key", bare.Find("ask.screen")!.Because);

        // A canvas rides the action with its key and has no wire line.
        var canvas = DeskMenus.Screen(d, Screen(number: "") with { TargetId = "a+b", IsCanvas = true, Title = "A · Main wall" });
        Assert.Equal("", canvas.Find("stage.reset")!.Wire);
        Assert.Equal("a+b", canvas.Find("stage.reset")!.Action!.Value.Target);
        Assert.Equal("", canvas.Find("tile.lock")!.Wire);
    }

    [Fact]
    public void TheTileMenuShowsTheGroupAndOffersTheOthersOnTheSameVerbAsTheWire()
    {
        var d = Facts();

        // A main screen: the drawer names the group, Main is on, the others carry the wire's line and the desk's verb.
        var menu = DeskMenus.Screen(d, Screen());
        var group = menu.Find("tile.group")!;
        Assert.Equal("Group — Main", group.Text);
        Assert.True(group.HasChildren);
        Assert.Null(group.Action);
        Assert.Equal(new[] { "tile.group:main", "tile.group:confidence", "tile.group:info", "tile.group:repeater" }, group.Children.Select(c => c.Id));
        Assert.True(group.Children[0].IsOn);
        var conf = group.Children[1];
        Assert.False(conf.IsOn);
        Assert.Equal("SCREEN 2 GROUP confidence", conf.Wire);
        Assert.Equal(new ShowAction(ShowActionKind.ScreenRole, "b", "confidence"), conf.Action);
        Assert.Equal(ShowActionKind.ScreenRole, ControlProtocol.Parse(conf.Wire).Action.Kind);
        Assert.Contains("locks", conf.Detail);
        // A repeater needs a source: without one the row says where it is chosen.
        var rep = group.Children[3];
        Assert.False(rep.IsEnabled);
        Assert.Contains("Screens page", rep.Because);
        Assert.Contains("Mirror of", rep.Because);

        // A repeater with its source: Repeater is on and the row repeats it by name; Main is offered to leave the group.
        var repeater = DeskMenus.Screen(d, Screen(mirror: true)).Find("tile.group")!;
        Assert.Equal("Group — Repeater", repeater.Text);
        Assert.True(repeater.Children[3].IsOn);
        Assert.True(repeater.Children[3].IsEnabled);
        Assert.Contains("1 · Main wall", repeater.Children[3].Detail);
        Assert.True(repeater.Children[0].IsEnabled);

        // A canvas: the choice sets every screen; it rides the action (no wire number); a canvas cannot repeat;
        // screens in different groups read as mixed with nothing on.
        var canvas = DeskMenus.Screen(d, Screen(number: "") with { TargetId = "a+b", IsCanvas = true, Title = "A · Main wall", Group = "", GroupsMixed = true }).Find("tile.group")!;
        Assert.Contains("mixed", canvas.Text);
        Assert.Contains("every screen of the canvas", canvas.Detail);
        Assert.All(canvas.Children, c => Assert.False(c.IsOn));
        Assert.Equal("", canvas.Children[1].Wire);
        Assert.Equal(new ShowAction(ShowActionKind.ScreenRole, "a+b", "confidence"), canvas.Children[1].Action);
        Assert.False(canvas.Children[3].IsEnabled);
        Assert.Contains("canvas cannot repeat", canvas.Children[3].Because);

        // The PGM tile has no group — the programme is what the groups follow.
        Assert.Null(DeskMenus.Program(d).Find("tile.group"));
    }

    [Fact]
    public void TheProgramAndPreviewMenusTakeOnlyWithEditSafeOpen()
    {
        var open = DeskMenus.Program(Facts(sandbox: true));
        Assert.True(open.Find("take")!.IsEnabled);
        Assert.Equal(ShowActionKind.Take, open.Find("take")!.Action!.Value.Kind);
        Assert.Equal(new ShowAction(ShowActionKind.ApplyLookToPreview, "L2"), open.Find("stage.look:L2")!.Action);
        Assert.Equal("PVW LOOK Keynote", open.Find("stage.look:L2")!.Wire);
        Assert.Equal("PVW PATTERN LedWall", open.Find("stage.kind:LedWall")!.Wire);
        Assert.Equal("PVW RESET", open.Find("stage.reset")!.Wire);
        Assert.Equal("PVW", open.Find("to.preview")!.Wire);
        Assert.Contains("'Walk-in'", open.Subtitle);

        var closed = DeskMenus.Program(Facts(sandbox: false, edited: true));
        Assert.Contains("EDIT SAFE is off", closed.Find("take")!.Because);
        Assert.Contains("EDIT SAFE is off", closed.Find("cut")!.Because);
        Assert.Contains("changed since", closed.Subtitle);

        var preview = DeskMenus.Preview(Facts(sandbox: false));
        Assert.NotNull(preview.Find("preview.open"));
        Assert.Null(preview.Find("preview.discard"));
        Assert.Contains("LIVE", preview.Subtitle);
        var previewOpen = DeskMenus.Preview(Facts(sandbox: true));
        Assert.NotNull(previewOpen.Find("preview.discard"));
        Assert.Contains("EDIT SAFE", previewOpen.Subtitle);
    }

    [Fact]
    public void TheCueMenuReadsTheCueAndItsEditsLandOnTheStack()
    {
        var d = Facts();
        var menu = DeskMenus.Cue(d, Cue());
        Assert.Contains("Standby here", menu.Find("cue.standby")!.Text);
        Assert.Equal("CUE STANDBY 03.020", menu.Find("cue.standby")!.Wire);
        Assert.True(menu.Find("cue.look:L1")!.IsOn);
        Assert.True(menu.Find("cue.transition:cut")!.IsOn);
        Assert.False(menu.Find("cue.transition:")!.IsOn);
        Assert.Contains("dissolve · 800 ms", menu.Find("cue.transition:")!.Detail);
        Assert.True(menu.Find($"cue.overlay:{ShowActionKind.ClockOn}")!.IsOn);
        Assert.False(menu.Find($"cue.overlay:{ShowActionKind.LogoOn}")!.IsOn);
        Assert.True(menu.Find("cue.lt:D1:3:8")!.IsOn);
        Assert.True(menu.Find("cue.lt.design:D1")!.IsOn);
        Assert.True(menu.Find("cue.follow:5")!.IsOn);
        Assert.True(menu.Find("cue.mark:Break")!.IsOn);
        Assert.Contains("in after 3 s, out 8 s later", menu.Find("cue.lt")!.Text);
        Assert.Equal("Cues", menu.Find("cue.edit")!.Route!.Page);
        Assert.Equal("C1", menu.Find("cue.edit")!.Route!.Item);

        var noLook = DeskMenus.Cue(d, Cue(hasLook: false));
        Assert.Contains("Give the cue a look first", noLook.Find("cue.transition")!.Because);
        var standby = DeskMenus.Cue(d, Cue(standby: true, problem: "look 'X' not found"));
        Assert.Contains("on standby now", standby.Find("cue.standby")!.Because);
        Assert.Contains("not found", standby.Find("cue.go")!.Because);
        Assert.Equal(MenuTone.Live, standby.Tone);

        // Every edit the menu offers lands on a real cue as steps the editor would show.
        var cue = new RunCueConfig { Number = "03.020", Name = "Keynote" };
        Assert.Contains("recalls 'Keynote'", CueMenuEdits.Apply(cue, "cue.look:L2", d));
        Assert.Equal("L2", CueMenuEdits.LookStep(cue)!.Target);
        Assert.Contains("comes in with cut", CueMenuEdits.Apply(cue, "cue.transition:cut", d));
        Assert.Equal("cut", CueMenuEdits.Transition(cue));
        Assert.Contains("brings the clock in", CueMenuEdits.Apply(cue, $"cue.overlay:{ShowActionKind.ClockOn}", d));
        Assert.True(CueMenuEdits.HasStep(cue, ShowActionKind.ClockOn));
        Assert.Contains("no longer touches the clock", CueMenuEdits.Apply(cue, $"cue.overlay:{ShowActionKind.ClockOn}", d));
        Assert.False(CueMenuEdits.HasStep(cue, ShowActionKind.ClockOn));
        Assert.Contains("clears every overlay", CueMenuEdits.Apply(cue, "cue.clean", d));
        Assert.True(CueMenuEdits.HasStep(cue, ShowActionKind.OverlaysOff));
        Assert.Contains("in after 3 s, out 8 s later", CueMenuEdits.Apply(cue, "cue.lt:D1:3:8", d));
        var lt = CueMenuEdits.LowerThird(cue);
        Assert.Equal(("D1", 3d, (double?)8d), lt);
        var show = cue.Actions.Single(a => a.Kind == ShowActionKind.LowerThirdShow);
        var hide = cue.Actions.Single(a => a.Kind == ShowActionKind.LowerThirdHide);
        Assert.Equal(3, show.DelaySeconds);
        Assert.Equal(8, hide.DelaySeconds);
        Assert.True(cue.Actions.IndexOf(show) < cue.Actions.IndexOf(hide));
        Assert.Contains("'Clean'", CueMenuEdits.Apply(cue, "cue.lt.design:D2", d));
        Assert.Equal(("D2", 3d, (double?)8d), CueMenuEdits.LowerThird(cue)); // the timing kept
        Assert.Contains("stays until hidden", CueMenuEdits.Apply(cue, "cue.lt:D1:5:-", d));
        Assert.Equal(("D1", 5d, (double?)null), CueMenuEdits.LowerThird(cue));
        Assert.DoesNotContain(cue.Actions, a => a.Kind == ShowActionKind.LowerThirdHide);
        Assert.Contains("no lower third", CueMenuEdits.Apply(cue, "cue.lt.remove", d));
        Assert.Null(CueMenuEdits.LowerThird(cue));
        Assert.Contains("5 s later", CueMenuEdits.Apply(cue, "cue.follow:5", d));
        Assert.Equal(5, cue.FollowSeconds);
        Assert.Contains("presses GO", CueMenuEdits.Apply(cue, "cue.follow:-", d));
        Assert.Null(cue.FollowSeconds);
        Assert.Contains("the break", CueMenuEdits.Apply(cue, "cue.mark:Break", d));
        Assert.Equal(CueMark.Break, cue.Mark);
        Assert.Contains("second GO", CueMenuEdits.Apply(cue, "cue.confirm", d));
        Assert.True(cue.RequireConfirm);
        Assert.Contains("marked built", CueMenuEdits.Apply(cue, "cue.ready", d));
        Assert.True(cue.Ready);
        Assert.Null(CueMenuEdits.Apply(cue, "cue.dance", d));
        Assert.Null(CueMenuEdits.Apply(cue, "tile.arm", d));
        // A transition with no look says so and changes nothing.
        var bare = new RunCueConfig();
        Assert.Contains("recalls no look yet", CueMenuEdits.Apply(bare, "cue.transition:cut", d));
        Assert.Empty(bare.Actions);
    }

    [Fact]
    public void TheLookLowerThirdAndPersonMenusSpeakTheWire()
    {
        var d = Facts();
        var look = DeskMenus.Look(d, d.Looks[1]);
        Assert.Equal(new ShowAction(ShowActionKind.ApplyLookToPreview, "L2"), look.Find("look.preview")!.Action);
        Assert.Equal(ShowActionKind.ApplyLookToPreview, ControlProtocol.Parse(look.Find("look.preview")!.Wire).Action.Kind);
        Assert.Equal("LOOK Keynote", look.Find("look.air")!.Wire);
        Assert.Equal("cut", look.Find("look.cut")!.Action!.Value.Value);
        Assert.Equal(13, look.Find("look.hotkey")!.Children.Count);
        Assert.True(look.Find("look.hotkey:3")!.IsOn);
        Assert.Contains("now 'Walk-in'", look.Find("look.hotkey:1")!.Detail);
        Assert.True(look.Find("look.update")!.IsEnabled);
        Assert.Contains("Open EDIT SAFE", DeskMenus.Look(Facts(sandbox: false), d.Looks[1]).Find("look.update")!.Because);
        Assert.Contains("PREP", DeskMenus.Look(Facts(prep: true), d.Looks[1]).Find("look.air")!.Because);
        Assert.Contains("used by 01.010, 03.020", DeskMenus.Look(d, d.Looks[0]).Subtitle);

        var design = DeskMenus.LowerThird(d, d.Designs[0]);
        var pvw = ControlProtocol.Parse(design.Find("lt.preview")!.Wire);
        Assert.Equal(new ShowAction(ShowActionKind.LowerThirdPreview, "Neon"), pvw.Action);
        var with = ControlProtocol.Parse(design.Find("lt.preview.with:P1")!.Wire);
        Assert.Equal(new ShowAction(ShowActionKind.LowerThirdPreview, "Neon", "Jane Doe"), with.Action);
        Assert.Equal(new ShowAction(ShowActionKind.LowerThirdPreview, "D1", "P1"), design.Find("lt.preview.with:P1")!.Action);
        Assert.Equal(MenuScope.Live, design.Find("lt.air.with:P1")!.Scope);
        Assert.Contains("not on air", design.Find("lt.off")!.Because);
        Assert.Contains("not in the preview", design.Find("lt.take")!.Because);
        Assert.Contains("It is the default", design.Find("lt.default")!.Because);
        var onAir = DeskMenus.LowerThird(d, d.Designs[1]);
        Assert.True(onAir.Find("lt.off")!.IsEnabled);
        Assert.True(onAir.Find("lt.default")!.IsEnabled);
        Assert.Equal(MenuTone.Live, onAir.Tone);

        var person = DeskMenus.Person(d, d.People[0]);
        Assert.Equal(new ShowAction(ShowActionKind.LowerThirdShow, "", "Jane Doe"), ControlProtocol.Parse(person.Find("person.air")!.Wire).Action);
        Assert.Equal(new ShowAction(ShowActionKind.LowerThirdPreview, "", "Jane Doe"), ControlProtocol.Parse(person.Find("person.preview")!.Wire).Action);
        Assert.Equal(new ShowAction(ShowActionKind.LowerThirdPreview, "D2", "P1"), person.Find("person.preview.with:D2")!.Action);
        Assert.Contains("'Neon'", person.Find("person.preview")!.Text);
    }

    [Fact]
    public void TheLayerAndOverlayMenusEditThePreviewByKey()
    {
        var d = Facts();
        var state = new ShowState();
        state.MediaLibrary.Add(new MediaLibraryEntry { Id = "M2", Name = "sizzle.mp4", Path = "/media/sizzle.mp4", IsVideo = true });
        var picture = state.Pattern;
        var now = new DateTime(2026, 9, 15, 10, 0, 0, DateTimeKind.Utc);

        var layer = DeskMenus.Layer(d, new LayerFacts { Index = 1, Enabled = false, Source = LayerSource.Image, Fit = FitMode.Fill });
        Assert.Equal(Enum.GetValues<LayerSource>().Length, layer.Find("layer.source")!.Children.Count);
        Assert.True(layer.Find("layer.source:1:Image")!.IsOn);
        Assert.True(layer.Find("layer.fit:1:Fill")!.IsOn);
        Assert.Equal("Layers", layer.Find("go.layers")!.Route!.Page);
        Assert.Equal("layer1", layer.Find("go.layers")!.Route!.Item);

        Assert.Contains("on in the preview", PreviewEdits.Apply(state, picture, "layer.on:1", now, d));
        Assert.True(picture.Layer1.Enabled);
        Assert.Contains("a clip", PreviewEdits.Apply(state, picture, "layer.source:1:Video", now, d));
        Assert.Equal(LayerSource.Video, picture.Layer1.Source);
        Assert.Contains("fit", PreviewEdits.Apply(state, picture, "layer.fit:2:Fit", now, d));
        Assert.Equal(FitMode.Fit, picture.Layer2.Fit);
        Assert.Contains("sizzle.mp4", PreviewEdits.Apply(state, picture, "layer.media:2:M2", now, d));
        Assert.Equal(LayerSource.Video, picture.Layer2.Source);
        Assert.Equal("/media/sizzle.mp4", picture.Layer2.VideoPath);
        Assert.True(picture.Layer2.Enabled);
        Assert.Contains("no longer in the library", PreviewEdits.Apply(state, picture, "layer.media:2:zz", now, d));
        Assert.Null(PreviewEdits.Apply(state, picture, "layer.on:3", now, d));
        Assert.Null(PreviewEdits.Apply(state, picture, "cue.ready", now, d));

        var clock = DeskMenus.Overlay(d, new OverlayFacts { Kind = "clock", AirOn = false, PreviewOn = false, Anchor = Anchor9.TopRight });
        Assert.Equal("CLOCK ON", clock.Find("overlay.air:clock")!.Wire);
        Assert.Equal(ShowActionKind.ClockOn, ControlProtocol.Parse("CLOCK ON").Action.Kind);
        Assert.True(clock.Find("overlay.anchor:TopRight")!.IsOn);
        Assert.Contains("Clock on", PreviewEdits.Apply(state, picture, "overlay.on:clock", now, d));
        Assert.True(state.Overlays.Clock.Enabled);
        Assert.Contains("Clock off", PreviewEdits.Apply(state, picture, "overlay.on:clock", now, d));
        Assert.Contains("bottom left", PreviewEdits.Apply(state, picture, "overlay.anchor:logo:BottomLeft", now, d));
        Assert.Equal(Anchor9.BottomLeft, state.Overlays.Logo.Anchor);
        Assert.Null(PreviewEdits.Apply(state, picture, "overlay.on:dance", now, d));
        Assert.Null(PreviewEdits.Apply(state, picture, "overlay.anchor:info:Center", now, d)); // the info chip has no anchor

        var countdown = DeskMenus.Overlay(d, new OverlayFacts { Kind = "countdown", AirOn = true, PreviewOn = true, CountdownMinutes = 5, Words = "BACK IN", CountdownFollow = false, Anchor = Anchor9.Center });
        Assert.Equal("COUNTDOWN 5", countdown.Find("countdown.air.start")!.Wire);
        Assert.Equal(new ShowAction(ShowActionKind.CountdownStart, "", "5"), ControlProtocol.Parse("COUNTDOWN 5").Action);
        Assert.True(countdown.Find("countdown.start:5")!.IsOn);
        Assert.True(countdown.Find("countdown.label:BACK IN")!.IsOn);
        Assert.Equal("Countdown", countdown.Find("go.countdown")!.Route!.Page);
        Assert.Contains("10 min", PreviewEdits.Apply(state, picture, "countdown.start:10", now, d));
        Assert.True(state.Countdown.Enabled);
        Assert.Equal(CountdownTargetKind.Duration, state.Countdown.TargetKind);
        Assert.Equal(10, state.Countdown.DurationMinutes);
        Assert.Equal(now, state.Countdown.ArmedAtUtc);
        Assert.Contains("follows the running order", PreviewEdits.Apply(state, picture, "countdown.follow", now, d));
        Assert.True(state.Countdown.FollowPlan);
        Assert.Contains("'DOORS OPEN IN'", PreviewEdits.Apply(state, picture, "countdown.label:DOORS OPEN IN", now, d));
        Assert.Equal("DOORS OPEN IN", state.Countdown.Label);
        Assert.Contains("bottom centre", PreviewEdits.Apply(state, picture, "countdown.anchor:BottomCenter", now, d));
        Assert.Equal(Anchor9.BottomCenter, state.Countdown.Anchor);
        Assert.Contains("off in the preview", PreviewEdits.Apply(state, picture, "countdown.stop", now, d));
        Assert.False(state.Countdown.Enabled);

        var badge = DeskMenus.Overlay(d, new OverlayFacts { Kind = "badge" });
        Assert.DoesNotContain(badge.Groups, g => g.Heading == "TO AIR"); // no wire verb, no live entry
    }

    [Fact]
    public void TonesAreHexTheWordsAreSaidAndTheJsonCarriesTheWire()
    {
        foreach (var tone in Enum.GetValues<MenuTone>())
        {
            var hex = MenuTones.Hex(tone);
            Assert.Equal(7, hex.Length);
            Assert.StartsWith("#", hex);
        }
        Assert.Equal(CompanionPalette.Hex("amber"), MenuTones.Hex(MenuTone.Preview));
        Assert.Equal("PREVIEW", MenuTones.Word(MenuScope.Preview));
        Assert.Equal("CUE STACK", MenuTones.Word(MenuScope.Stack));
        Assert.Equal(Enum.GetValues<PatternKind>().Length, DeskFacts.DefaultKinds.Count);
        Assert.Contains(DeskFacts.DefaultKinds, k => k.Word == "ColorBars" && k.Label == "Colour Bars");

        var menu = DeskMenus.Screen(Facts(), Screen(offLook: true));
        using var doc = JsonDocument.Parse(MenuJson.Write(menu));
        var root = doc.RootElement;
        Assert.Equal("screen", root.GetProperty("kind").GetString());
        Assert.Equal("b", root.GetProperty("subject").GetString());
        Assert.Equal("#FF8A00", root.GetProperty("hue").GetString());
        var preview = root.GetProperty("groups")[0];
        Assert.Equal("IN THE PREVIEW", preview.GetProperty("heading").GetString());
        Assert.Equal(DeskMenus.PreviewNote, preview.GetProperty("note").GetString());
        var reset = preview.GetProperty("entries")[0];
        Assert.Equal("stage.reset", reset.GetProperty("id").GetString());
        Assert.Equal("SCREEN 2 PVW RESET", reset.GetProperty("wire").GetString());
        Assert.True(reset.GetProperty("enabled").GetBoolean());
        var lookDrawer = preview.GetProperty("entries")[1];
        Assert.Equal(3, lookDrawer.GetProperty("children").GetArrayLength());
        Assert.Equal("SCREEN 2 PVW LOOK Walk-in", lookDrawer.GetProperty("children")[0].GetProperty("wire").GetString());
    }
}
