using System.Text.Json;
using Avalonia;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Patterns.Platform.Windows;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 66: the God's Eye on the desk — the picture read from the services, a mismatching
/// screen red and first in the headline, the eye moved from the wire and the page, the JSON and
/// the STATE row, a screen's node opening the wall tile's own menu and a device's the Eye menu,
/// and the picture rebuilt only when the facts moved.
/// </summary>
public class EyeAppTests
{
    private static readonly PixelRect Wall = new(0, 9000, 1920, 1080);

    private const string DevicePath = @"\\?\DISPLAY#PTN0001#5&2a3c8f1&0&UID4353#{e6f07b5f-ee97-4a90-b076-33f57bf4eaa7}";

    private static SignalObservation Observation(SignalRate rate)
        => new(Wall.X, Wall.Y, Wall.Width, Wall.Height, Wall.Width, Wall.Height, rate, PixelEncoding.RGB, 8, false, false, Monitor: "Eye LED", Connector: "HDMI", DevicePath: DevicePath);

    private static string Send(CommandRouter router, string line) => TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse(line)));

    private static JsonDocument Payload(string reply)
    {
        Assert.StartsWith("OK {", reply);
        return JsonDocument.Parse(reply[3..]);
    }

    [AvaloniaFact]
    public void TheEyeSeesTheShowAndMovesFromTheWireThePageAndTheMenus()
    {
        var b = TestApp.Boot();
        var (services, vm, _) = b;
        vm.IsSandboxActive = false;
        var observation = Observation(SignalRate.Of(60000, 1001));                              // 59.94 arriving
        DisplayObservation.Source = () => new[] { observation };
        try
        {
            // One display, placed by the desk; the contract says 50 and the display shows 59.94: MISMATCH.
            services.Screens.Source = () => new[] { new ScreenInfo("eye-a", "Eye LED", Wall, 1.0, false, 0, Hz: 50) };
            services.Screens.Refresh();
            Dispatcher.UIThread.RunJobs();
            var placement = services.State.Output.Placements.Single(p => p.ScreenId == "eye-a");
            placement.Enabled = true;                                                                // a found display starts disabled; the operator turns it on
            var n = Rig.OrderedLivePlacements(services.State, services.Screens.All).FindIndex(x => x.Placement.ScreenId == "eye-a") + 1;
            Assert.True(n >= 1);
            var set = services.Actions.Execute(ControlProtocol.Parse($"SCREEN {n} SIGNAL 1920x1080 50 RGB 8 SDR").Action, ActionOrigin.Desk);
            Assert.True(set.Ok, set.Message);
            Assert.Contains("MISMATCH", set.Message);

            // A device the desk drives, linked through a fake and green.
            var fake = new FakeDeviceLink();
            services.Devices.LinkFactory = _ => fake;
            var device = DeviceProfiles.Preset(DeviceProfile.Companion, 1);
            device.Name = "Deck Companion";
            device.Port = "10.0.0.5";
            vm.State.Interactive.Devices.Add(device);
            vm.State.Interactive.Enabled = true;
            services.Devices.Reconcile();

            // The picture: the screen red with the mismatch, first in the problems, named in the headline.
            Assert.True(services.Eye.Refresh());
            var g = services.Eye.Graph;
            var screen = g.Find("screen:" + placement.ScreenId);
            Assert.NotNull(screen);
            Assert.True(screen!.Light == CheckLight.Red, $"{screen.Light}: {screen.Sub} | {string.Join(", ", screen.Words)}");
            Assert.Contains("MISMATCH", screen.Sub);
            Assert.Equal(screen.Id, g.Problems[0]);
            Assert.Equal(screen.Id, g.Worst?.Id);
            Assert.Contains("Eye LED", g.Headline);
            Assert.Contains(g.Edges, e => e.From == EyeGraph.DeskId && e.To == screen.Id && e.Kind == EyeEdgeKind.Shows);
            Assert.Contains(g.Edges, e => e.From == screen.Id && e.To == "display:eye-a" && e.Kind == EyeEdgeKind.Drives && e.Light == CheckLight.Red);
            var deviceNode = g.Find("device:" + device.Id);
            Assert.NotNull(deviceNode);
            Assert.Equal(CheckLight.Green, deviceNode!.Light);
            Assert.Equal("Deck Companion", deviceNode.Label);
            Assert.Contains(g.Edges, e => e.From == EyeGraph.DeskId && e.To == deviceNode.Id && e.Kind == EyeEdgeKind.Drives);
            Assert.True(services.Eye.Placement.Of(screen.Id).W > 0);

            // Nothing moved: the same facts hash the same, the picture is not rebuilt and the revision holds.
            var rev = services.Eye.Rev;
            Assert.False(services.Eye.Refresh());
            Assert.Equal(rev, services.Eye.Rev);

            // The rail: the worst light's count and its colour.
            vm.PollNow();
            Assert.EndsWith("RED", vm.EyeWord);
            Assert.Equal(CompanionPalette.Hex("red"), vm.EyeHue);
            Assert.Equal(g.Headline, vm.EyeLine);
            Assert.Contains("Eye LED", vm.EyeHeadline);

            // The wire moves the eye: by the screen's number, then the problems queue, a lens, the whole picture.
            var router = new CommandRouter(services);
            var focus = Send(router, $"EYE FOCUS screen {n}");
            Assert.StartsWith("OK", focus);                                                          // a verb answers OK; the view is the answer
            Assert.Equal(screen.Id, services.Eye.FocusId);
            Assert.Contains("Eye LED", vm.EyeFocusWords);
            Assert.StartsWith("Problem 1 of", vm.EyeProblemsWords);
            using (var eye = Payload(Send(router, "EYE")))
            {
                var root = eye.RootElement;
                Assert.Equal(screen.Id, root.GetProperty("focus").GetString());
                Assert.Equal("red", root.GetProperty("worstLight").GetString());
                Assert.Equal(screen.Id, root.GetProperty("worst").GetString());
                Assert.Equal("all", root.GetProperty("lens").GetString());
                Assert.Contains(root.GetProperty("problems").EnumerateArray(), p => p.GetString() == screen.Id);
                var node = root.GetProperty("nodes").EnumerateArray().Single(x => x.GetProperty("id").GetString() == screen.Id);
                Assert.Equal("red", node.GetProperty("light").GetString());
                Assert.Equal("screen", node.GetProperty("kind").GetString());
                Assert.Equal("Screens", node.GetProperty("page").GetString());
                Assert.Equal("EYE FOCUS " + screen.Id, node.GetProperty("wire").GetString());
                Assert.Contains(root.GetProperty("edges").EnumerateArray(), e => e.GetProperty("from").GetString() == screen.Id && e.GetProperty("kind").GetString() == "drives");
                Assert.True(root.GetProperty("counts").GetProperty("red").GetInt32() >= 1);
            }
            var next = Send(router, "EYE NEXT");
            Assert.StartsWith("OK", next);
            Assert.Contains(services.Eye.FocusId!, g.Problems);
            Assert.StartsWith("OK", Send(router, "EYE LENS audio"));
            Assert.Equal(EyeLens.Audio, services.Eye.Lens);
            Assert.Contains(vm.EyeLenses, c => c.Word == "audio" && c.IsCurrent);
            Assert.StartsWith("ERR", Send(router, "EYE LENS bogus"));
            Assert.Equal(EyeLens.Audio, services.Eye.Lens);
            Assert.StartsWith("ERR", Send(router, "EYE FOCUS nothing of that name"));
            Assert.StartsWith("OK", Send(router, "EYE RESET"));
            Assert.Null(services.Eye.FocusId);
            Assert.Equal(EyeLens.All, services.Eye.Lens);
            Assert.Equal("The whole picture", vm.EyeFocusWords);

            // STATUS (the STATE document) carries the Eye's row.
            using (var state = Payload(Send(router, "STATUS")))
            {
                var row = state.RootElement.GetProperty("eye");
                Assert.Equal("red", row.GetProperty("worstLight").GetString());
                Assert.Equal(screen.Id, row.GetProperty("worst").GetString());
                Assert.Contains("Eye LED", row.GetProperty("headline").GetString());
                Assert.True(row.GetProperty("problems").GetInt32() >= 1);
                Assert.Equal("", row.GetProperty("focus").GetString());
            }

            // The menus: a screen's node opens the wall tile's own menu; a device's node the Eye menu.
            var tileMenu = vm.MenuFor("eye", screen);
            Assert.NotNull(tileMenu);
            Assert.Equal("screen", tileMenu!.Menu.Kind);
            var eyeMenu = vm.MenuFor("eye", deviceNode);
            Assert.NotNull(eyeMenu);
            Assert.Equal("eye", eyeMenu!.Menu.Kind);
            Assert.Equal(deviceNode.Label, eyeMenu.Title);
            Assert.NotNull(eyeMenu.Find("eye.focus"));
            Assert.NotNull(eyeMenu.Find("eye.reset"));
            Assert.NotNull(eyeMenu.Find("eye.open"));
            Assert.NotNull(eyeMenu.Find("eye.contact:" + EyeGraph.DeskId));
            var ask = eyeMenu.Find("eye.ask");
            Assert.NotNull(ask);
            Assert.False(ask!.IsEnabled);                                                            // no key saved
            Assert.Contains("API key", ask.Because);
            var focusEntry = eyeMenu.Find("eye.focus")!;
            Assert.True(focusEntry.IsEnabled);
            focusEntry.ChooseCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(deviceNode.Id, services.Eye.FocusId);
            Assert.Null(vm.MenuFor("eye", "not a node"));

            // The page: a click selects, the side card says what it is and what it links to, the verbs run through the action layer.
            vm.EyeSelectCommand.Execute(screen.Id);
            Assert.True(vm.HasEyeSelection);
            Assert.Equal("Eye LED", vm.EyeSelectedLabel);
            Assert.Contains("MISMATCH", vm.EyeSelectedSub);
            Assert.Contains("screen", vm.EyeSelectedKind);
            Assert.Contains("red", vm.EyeSelectedKind);
            Assert.Contains($"Screen {n}", vm.EyeSelectedWords);
            Assert.True(vm.EyeCanOpen);
            Assert.Equal("OPEN SCREENS", vm.EyeOpenWords);
            Assert.Contains(vm.EyeContacts, c => c.Id == EyeGraph.DeskId && c.Verb.Contains("shows"));
            Assert.Contains(vm.EyeContacts, c => c.Id == "display:eye-a" && c.Verb.Contains("drives"));
            vm.EyeFocusSelectedCommand.Execute(null);
            Assert.Equal(screen.Id, services.Eye.FocusId);
            vm.EyeNextCommand.Execute(null);
            Assert.Contains(services.Eye.FocusId!, g.Problems);
            Assert.Equal(services.Eye.FocusId, vm.EyeSelectedId);
            vm.EyePrevCommand.Execute(null);
            Assert.Equal(screen.Id, services.Eye.FocusId);
            vm.EyeLensCommand.Execute("video");
            Assert.Equal(EyeLens.Video, services.Eye.Lens);
            vm.EyeResetCommand.Execute(null);
            Assert.Null(services.Eye.FocusId);
            Assert.Equal(EyeLens.All, services.Eye.Lens);
            Assert.False(vm.HasEyeSelection);
            Assert.Equal("The whole picture.", vm.StatusMessage);

            // The observation comes right: the picture rebuilt on the next read, the screen green, the headline moving on.
            observation = Observation(SignalRate.Of(50, 1));
            services.Eye.Focus(screen.Id);
            Assert.True(services.Eye.Refresh());
            var again = services.Eye.Graph.Find(screen.Id);
            Assert.NotNull(again);
            Assert.Equal(CheckLight.Green, again!.Light);
            Assert.DoesNotContain(screen.Id, services.Eye.Graph.Problems);
            Assert.Equal(screen.Id, services.Eye.FocusId);                                          // the focus stays while the thing is in the picture

            // The thing focused leaves the picture: the focus lets go.
            vm.State.Interactive.Devices.Remove(device);
            services.Devices.Reconcile();
            services.Eye.Focus(deviceNode.Id);
            Assert.Equal(deviceNode.Id, services.Eye.FocusId);
            Assert.True(services.Eye.Refresh());
            Assert.Null(services.Eye.Graph.Find(deviceNode.Id));
            Assert.Null(services.Eye.FocusId);
        }
        finally
        {
            DisplayObservation.Source = null;
            services.Screens.Source = null;
            services.Screens.Refresh();
            Dispatcher.UIThread.RunJobs();
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheEyeVerbsAreTheDesksAloneAndTheBriefCarriesThePicture()
    {
        var b = TestApp.Boot();
        var (services, vm, _) = b;
        try
        {
            Assert.NotNull(ActionSpec.DeskOnly(ShowActionKind.EyeFocus));
            Assert.NotNull(ActionSpec.DeskOnly(ShowActionKind.EyeReset));
            // A wire query before the first tick reads the picture itself.
            var lines = services.Eye.BriefLines();
            Assert.NotEmpty(lines);
            // The assistant's facts carry the same picture.
            var facts = services.GatherFacts();
            Assert.NotEmpty(facts.Eye);
            Assert.Equal(lines[0], facts.Eye[0]);
            Assert.True(services.Eye.Graph.Nodes.Count > 0);
            Assert.NotNull(services.Eye.Graph.Find(EyeGraph.DeskId));
            Assert.Contains(services.Eye.Graph.Nodes, node => node.Kind == EyeKind.Stack);
            Assert.Contains(services.Eye.Graph.Nodes, node => node.Kind == EyeKind.Assistant && node.Light == CheckLight.Grey);
            // The Eye page is in the shell and the rail's word is the picture's.
            Assert.Contains(Shell.Pages, p => p.Header == "Eye" && p.Group == ShellGroup.Show);
            vm.PollNow();
            Assert.NotEqual("—", vm.EyeWord);
            Assert.NotEqual("#4A505E", vm.EyeHue);
        }
        finally
        {
            b.Dispose();
        }
    }
}
