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
/// Round 73: "Right click MIDI/Learn function on any button can be associated from any button,
/// Pattern, Look, Cue, Lower Thirds, action, etc that can be utilised by a MIDI controller."
///
/// On a live desk with a surface open through a fake link: the look's menu arms learn for its
/// line, a release is waited past, the press writes the row into the surface's own table, and
/// every reader — the menu's tick, the status line, the page's map, STATE, the wire's MIDI, the
/// Eye — sees the one binding; a fader learns a level verb with *; the wire arms, cancels and
/// forgets; the refusals say why; the transport menu and the page's CANCEL.
/// </summary>
public class MidiLearnAppTests
{
    private static string Send(CommandRouter router, string line) => TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse(line)));

    private static LookConfig SaveLook(MainViewModel vm, string name)
    {
        vm.ActivePattern.Kind = PatternKind.Grid;
        vm.Show.NewLookName = name;
        vm.Show.SaveLookCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        return LookService.Find(vm.State, name) ?? throw new InvalidOperationException($"look '{name}' was not saved");
    }

    /// <summary>A MIDI surface on the Interactive page, the area on, its link a fake the test speaks through.</summary>
    private static (DeviceConfig Surface, FakeDeviceLink Link) OpenSurface(TestApp.Booted b)
    {
        var fake = new FakeDeviceLink();
        b.Services.Devices.LinkFactory = _ => fake;
        b.Vm.AddMidiDeviceCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        var surface = b.Vm.State.Interactive.Devices.Single();
        surface.Name = "APC40";
        surface.Triggers.Clear();
        b.Vm.State.Interactive.Enabled = true;
        b.Services.Devices.Reconcile();
        Dispatcher.UIThread.RunJobs();
        return (surface, fake);
    }

    private static JsonDocument Json(string reply)
    {
        Assert.StartsWith("OK {", reply, StringComparison.Ordinal);
        return JsonDocument.Parse(reply[3..]);
    }

    [AvaloniaFact]
    public void ARightClickLearnsAControlAndEveryReaderSeesTheBinding()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            var router = new CommandRouter(services);
            var walkIn = SaveLook(vm, "Walk-in");

            // Before a surface: the menu says where to add one, and the wire refuses with the same reason.
            var before = vm.MenuFor("look", walkIn)!;
            Assert.False(before.Find("midi.learn")!.IsEnabled);
            Assert.Contains("Interactive page", before.Find("midi.learn")!.Because);
            var refused = Send(router, "MIDI LEARN LOOK Walk-in");
            Assert.StartsWith("ERR", refused, StringComparison.Ordinal);
            Assert.Contains("Interactive page", refused);
            Assert.False(services.MidiLearn.Armed);

            var (surface, fake) = OpenSurface(b);
            Assert.Equal(1, services.Devices.OpenCount);
            Assert.True(services.MidiLearn.SurfaceOpen);

            // The look's menu: MIDI LEARN ▸ "On air now" arms the desk for LOOK Walk-in — the status line, STATE and the Eye say so.
            var menu = vm.MenuFor("look", walkIn)!;
            Assert.Equal("MIDI", menu.Groups[^1].Heading);
            var drawer = menu.Find("midi.learn")!;
            Assert.True(drawer.IsEnabled, drawer.Because);
            var line = menu.Find("midi.learn:look.air")!;
            Assert.Equal("MIDI LEARN LOOK Walk-in", line.Wire);
            line.ChooseCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.MidiLearn.Armed);
            Assert.Equal("LOOK Walk-in", services.MidiLearn.Wire);
            Assert.Contains("press a control", vm.MidiLearnText);
            Assert.Contains("LOOK Walk-in", vm.StatusMessage);
            Assert.True(vm.IsMidiLearning);
            using (var doc = JsonDocument.Parse(new CommandRouter(services).StateJson()))
            {
                var midi = doc.RootElement.GetProperty("midi");
                Assert.True(midi.GetProperty("learning").GetBoolean());
                Assert.Equal("LOOK Walk-in", midi.GetProperty("wire").GetString());
                Assert.Equal(0, midi.GetProperty("count").GetInt32());
                Assert.Equal("APC40", midi.GetProperty("surfaces")[0].GetProperty("name").GetString());
                Assert.True(midi.GetProperty("surfaces")[0].GetProperty("open").GetBoolean());
            }
            Assert.Contains(services.Eye.Graph.Find("desk")!.Words, w => w.StartsWith("MIDI learning LOOK Walk-in", StringComparison.Ordinal));
            var waiting = vm.MenuFor("look", walkIn)!;
            Assert.True(waiting.Find("midi.learn")!.IsOn);
            Assert.Contains("LEARNING", waiting.Find("midi.learn:look.air")!.Line2);
            Assert.NotNull(waiting.Find("midi.learn.off"));

            // A release is waited past; the press binds the pad at any velocity; nothing runs.
            fake.Say("NOTEOFF 1 53");
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.MidiLearn.Armed);
            fake.Say("NOTE 1 53 127");
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.MidiLearn.Armed);
            Assert.False(services.Devices.LearningAny);
            var row = Assert.Single(surface.Triggers);
            Assert.Equal("NOTE 1 53 *", row.Match);
            Assert.Equal("LOOK Walk-in", row.Command);
            Assert.Equal("⌁ NOTE 1 53 on APC40 → LOOK Walk-in", services.MidiLearn.LastLearned);
            Assert.Equal(services.MidiLearn.LastLearned, vm.StatusMessage);
            Assert.False(vm.IsMidiLearning);
            Assert.True(vm.HasMidiBindings);
            var shown = Assert.Single(vm.MidiBindingRows);
            Assert.Equal("NOTE 1 53 (APC40)", shown.Words);
            Assert.Contains(services.Eye.Graph.Find("desk")!.Words, w => w == "MIDI 1 control bound on APC40");
            Assert.Contains("1 control mapped", services.Eye.Graph.Find("device:" + surface.Id)!.Words);

            // The menu now ticks the line, names the control and offers FORGET; the unbound preview line is plain.
            var after = vm.MenuFor("look", walkIn)!;
            Assert.True(after.Find("midi.learn:look.air")!.IsOn);
            Assert.Contains("NOTE 1 53 (APC40)", after.Find("midi.learn:look.air")!.Line2);
            Assert.NotNull(after.Find("midi.forget:look.air"));
            Assert.False(after.Find("midi.learn:look.preview")!.IsOn);
            Assert.Null(after.Find("midi.learn.off"));

            // A fader learns a level verb with *: the wire arms it, the sweep writes CC 1 7 * → AUDIO LEVEL *.
            Assert.StartsWith("OK", Send(router, "MIDI LEARN AUDIO LEVEL 50"), StringComparison.Ordinal);
            fake.Say("CC 1 7 64");
            Dispatcher.UIThread.RunJobs();
            var level = Assert.Single(surface.Triggers, t => t.Match == "CC 1 7 *");
            Assert.Equal("AUDIO LEVEL *", level.Command);
            Assert.Equal(2, vm.MidiBindingRows.Count);

            // The same pad learned again keeps the last line and the words say what it replaced.
            Assert.StartsWith("OK", Send(router, "MIDI LEARN BLACKOUT"), StringComparison.Ordinal);
            fake.Say("NOTE 1 53 40");
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(2, surface.Triggers.Count);
            Assert.Equal("BLACKOUT", surface.Triggers.Single(t => t.Match == "NOTE 1 53 *").Command);
            Assert.Contains("(was LOOK Walk-in)", services.MidiLearn.LastLearned);

            // MIDI on the wire reads the map; a learn cancelled binds nothing; FORGET clears a line; the page's ✕ one row.
            using (var doc = Json(Send(router, "MIDI")))
            {
                Assert.Equal(2, doc.RootElement.GetProperty("count").GetInt32());
                Assert.False(doc.RootElement.GetProperty("learning").GetBoolean());
                var bindings = doc.RootElement.GetProperty("bindings").EnumerateArray().ToList();
                Assert.Contains(bindings, x => x.GetProperty("control").GetString() == "NOTE 1 53" && x.GetProperty("command").GetString() == "BLACKOUT");
                Assert.Contains(bindings, x => x.GetProperty("match").GetString() == "CC 1 7 *" && x.GetProperty("command").GetString() == "AUDIO LEVEL *");
                Assert.Equal("MIDI 2 controls bound on APC40", "MIDI " + doc.RootElement.GetProperty("words").GetString());
            }
            Assert.StartsWith("OK", Send(router, "MIDI LEARN CUE GO"), StringComparison.Ordinal);
            Assert.True(services.MidiLearn.Armed);
            Assert.StartsWith("OK", Send(router, "MIDI LEARN OFF"), StringComparison.Ordinal);
            Assert.False(services.MidiLearn.Armed);
            Assert.Equal(2, surface.Triggers.Count);
            var forgot = Send(router, "MIDI FORGET AUDIO LEVEL 30");
            Assert.StartsWith("OK", forgot, StringComparison.Ordinal);
            Assert.DoesNotContain(surface.Triggers, t => t.Command == "AUDIO LEVEL *");
            Assert.StartsWith("OK", Send(router, "MIDI FORGET LOOK Walk-in"), StringComparison.Ordinal);   // nothing bound to it any more: OK, says so
            Assert.Single(vm.MidiBindingRows);
            vm.ForgetMidiBindingCommand.Execute(vm.MidiBindingRows[0]);
            Dispatcher.UIThread.RunJobs();
            Assert.Empty(vm.MidiBindingRows);
            Assert.Empty(surface.Triggers);
            Assert.False(vm.HasMidiBindings);

            // Refused, with the reason: a question, a stranger, a bare LEARN.
            Assert.Contains("question", Send(router, "MIDI LEARN STATUS"));
            Assert.Contains("no verb", Send(router, "MIDI LEARN FROBNICATE 12"));
            Assert.StartsWith("ERR", Send(router, "MIDI LEARN"), StringComparison.Ordinal);
            Assert.False(services.MidiLearn.Armed);

            // The transport menu (the RUN surface's GO) arms a learn for CUE GO; the page's CANCEL stops it with nothing bound.
            var transport = vm.MenuFor("transport", null)!;
            Assert.Equal("transport", transport.Menu.Kind);
            transport.Find("midi.learn:go")!.ChooseCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("CUE GO", services.MidiLearn.Wire);
            vm.CancelMidiLearnCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(services.MidiLearn.Armed);
            Assert.False(vm.IsMidiLearning);
            Assert.Contains("cancelled", vm.StatusMessage);
            Assert.Empty(surface.Triggers);

            // The surface's own LEARN button writes the trigger form too (round 73: not the one velocity it was pressed at).
            vm.LearnMidiCommand.Execute(surface);
            Dispatcher.UIThread.RunJobs();
            Assert.True(vm.IsMidiLearning);
            fake.Say("CC 1 14 100");
            Dispatcher.UIThread.RunJobs();
            var learned = Assert.Single(surface.Triggers);
            Assert.Equal("CC 1 14 *", learned.Match);
            Assert.Equal("", learned.Command);
            Assert.False(vm.IsMidiLearning);
        }
        finally
        {
            b.Dispose();
        }
    }
}
