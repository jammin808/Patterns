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
            var network = Send(router, "EYE LENS network");                                  // a band, not a lens: refused, and the refusal says where its things are
            Assert.StartsWith("ERR", network);
            Assert.Contains("the NETWORK band is not a lens", network);
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
            Assert.Contains(services.Eye.Graph.Find(EyeGraph.DeskId)!.Words, w => w.Contains("held", StringComparison.Ordinal) && w.Contains("GPU cache", StringComparison.Ordinal));   // round 69: the desk node's memory line
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
    /// <summary>
    /// The NETWORK band on the desk — another app a cluster of its own, opened and folded from the side card and its card's
    /// right-click menu alike (the same edit, no wire word); the open set and what is left out on STATE and EYE; the page's canvas
    /// counting the clusters it reads; the rail counting this desk's own, never another app's things.
    /// </summary>
    [AvaloniaFact]
    public void TheNetworkBandOpensAndFoldsFromTheSideCardAndTheMenuAndEveryReaderSaysSo()
    {
        var b = TestApp.Boot();
        var (services, vm, _) = b;
        try
        {
            var context = new ShowContext
            {
                Node = new ShowContextNode { Kind = "playmap", Id = "0a1b2c3d", Name = "Stage left" },
                Light = "red",
                Headline = "Out 7 not open",
                Items = Enumerable.Range(1, 14).Select(i => new ShowContextItem { Id = $"out{i}", Kind = "output", Label = $"Out {i}", Light = i == 7 ? "red" : "green", State = i == 7 ? "not open" : "60 fps" }).ToList(),
            };
            services.Eye.AppsOverride = () => new[]
            {
                new EyeShowApp("0a1b2c3d", "PlayMap", "Stage left", ShowBusState.Live, false, "") { Context = context },
                new EyeShowApp("77aa77aa", "AGMessage", "Messenger", ShowBusState.Live, false, ""),
            };
            Assert.True(services.Eye.Refresh());
            var g = services.Eye.Graph;
            const string Card = "app:0a1b2c3d";
            Assert.Equal(EyePlane.Network, g.Find(Card)!.Band);
            Assert.True(services.Eye.Placement.IsExpanded(Card));                                  // the reference canvas reads it
            Assert.Equal(2, services.Eye.Placement.LeftItems);                                     // fourteen named, twelve drawn
            Assert.DoesNotContain("Out 7", g.Headline, StringComparison.Ordinal);                   // PlayMap's red is PlayMap's to say
            Assert.Contains("app:0a1b2c3d:out7", g.Problems);
            Assert.NotEqual(Card, g.Worst?.Id);
            Assert.NotEqual("app:0a1b2c3d:out7", g.Worst?.Id);

            var router = new CommandRouter(services);
            using (var eye = Payload(Send(router, "EYE")))
            {
                var root = eye.RootElement;
                Assert.Equal(Card, root.GetProperty("expanded").EnumerateArray().Single().GetString());
                Assert.Equal(2, root.GetProperty("left").GetProperty("items").GetInt32());
                Assert.Equal("network", root.GetProperty("nodes").EnumerateArray().Single(n => n.GetProperty("id").GetString() == Card).GetProperty("band").GetString());
                Assert.Equal(g.Things, root.GetProperty("counts").GetProperty("things").GetInt32());   // the headline's count, not every node drawn
                Assert.Equal(g.CountedProblems, root.GetProperty("problemsCounted").GetInt32());
                Assert.Equal(g.CountedProblems + 2, root.GetProperty("problems").GetArrayLength());     // PlayMap's red card and Out 7 after the desk's own
            }

            // Opened by the canvas, the operator keeps it open: the choice is recorded, and a second ask changes nothing and says so.
            var kept = services.Eye.Cluster(Card, open: true);
            Assert.True(kept.Ok);
            Assert.Contains("now kept open", kept.Message, StringComparison.Ordinal);
            var twice = services.Eye.Cluster(Card, open: true);
            Assert.False(twice.Ok);
            Assert.Contains("already open, kept open", twice.Message, StringComparison.Ordinal);

            // The side card folds it: the layout alone moves, the picture is not rebuilt.
            var rev = services.Eye.Rev;
            var graph = services.Eye.Graph;
            vm.EyeSelectCommand.Execute(Card);
            Assert.True(vm.EyeCanCluster);
            Assert.Equal("FOLD CLUSTER", vm.EyeClusterWords);
            Assert.Contains("NETWORK", vm.EyeSelectedKind, StringComparison.Ordinal);
            vm.EyeClusterCommand.Execute(null);
            Assert.Contains("folded to its card", vm.StatusMessage, StringComparison.Ordinal);
            Assert.False(services.Eye.Placement.IsExpanded(Card));
            Assert.Same(graph, services.Eye.Graph);
            Assert.True(services.Eye.Rev > rev);
            Assert.Equal(14, services.Eye.Placement.Hidden[Card]);
            Assert.True(services.Eye.Placement.Of("app:0a1b2c3d:out1").IsEmpty);
            Assert.Equal("OPEN CLUSTER", vm.EyeClusterWords);
            // Folded, it stays folded: the facts read again, a focus on one of its things, a wider canvas — none opens it.
            services.Eye.Refresh();
            var focus = services.Eye.Focus("app:0a1b2c3d:out7");
            Assert.True(focus.Ok);
            Assert.EndsWith("Out 7 is folded in PlayMap Stage left's card — open its cluster to see it.", focus.Message, StringComparison.Ordinal);
            Assert.Equal(services.Eye.Placement.Of(Card), services.Eye.Placement.Around("app:0a1b2c3d:out7", services.Eye.Shown));   // the focus fits its card
            Assert.False(services.Eye.Placement.IsExpanded(Card));
            services.Eye.SetCanvas(3000, 2000);
            Assert.False(services.Eye.Placement.IsExpanded(Card));
            using (var state = Payload(Send(router, "STATUS")))
            {
                var row = state.RootElement.GetProperty("eye");
                Assert.Empty(row.GetProperty("expanded").EnumerateArray());
                Assert.Equal(14, row.GetProperty("left").GetProperty("items").GetInt32());
                Assert.Equal(new[] { "left", "problemsCounted", "worstMark" }, row.EnumerateObject().Select(o => o.Name).TakeLast(3));   // appended at the end of the row
                Assert.Equal(EyeMarks.Glyph(services.Eye.Graph.WorstLight), row.GetProperty("worstMark").GetString());   // the rail's light as its shape
                Assert.Equal(services.Eye.Graph.Things, row.GetProperty("things").GetInt32());
                Assert.Equal(services.Eye.Graph.CountedProblems, row.GetProperty("problemsCounted").GetInt32());
            }
            // A second fold changes nothing and says so.
            var again = services.Eye.Cluster(Card, open: false);
            Assert.False(again.Ok);
            Assert.Contains("already folded", again.Message, StringComparison.Ordinal);

            // The card's right-click menu is the side card's press: the same edit, no wire word.
            var menu = vm.MenuFor("eye", g.Find(Card)!);
            Assert.NotNull(menu);
            var entry = menu!.Find("eye.cluster");
            Assert.NotNull(entry);
            Assert.Equal("Open its cluster", entry!.Entry.Text);
            Assert.Equal("", entry.Entry.Wire);
            entry.ChooseCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.True(services.Eye.Placement.IsExpanded(Card));
            Assert.Equal("FOLD CLUSTER", vm.EyeClusterWords);
            // The assistant reads the same picture: PlayMap's things marked as another app's, and what the band leaves out.
            var brief = services.Eye.BriefLines();
            Assert.Contains(brief, l => l.StartsWith("NETWORK · VIDEO · ShowAppItem: Out 7 (another app's, not counted) — red", StringComparison.Ordinal));
            Assert.Contains(brief, l => l.StartsWith("The Eye's picture: 1 of 2 apps open · 2 things not drawn", StringComparison.Ordinal));

            // An app with nothing to draw, and this desk's own things, have no cluster: refused with the reason, no entry, no button.
            Assert.Contains("names no things", services.Eye.Cluster("app:77aa77aa", open: true).Message, StringComparison.Ordinal);
            Assert.Contains("no other app's card", services.Eye.Cluster(EyeGraph.DeskId, open: true).Message, StringComparison.Ordinal);
            Assert.Null(vm.MenuFor("eye", g.Find("app:77aa77aa")!)!.Find("eye.cluster"));
            vm.EyeSelectCommand.Execute(EyeGraph.DeskId);
            Assert.False(vm.EyeCanCluster);

            // The page's canvas: a canvas too small to read the band opens nothing it was not asked to and says marks only.
            services.Eye.SetCanvas(400, 260);
            Assert.Equal((400.0, 260.0), (services.Eye.CanvasW, services.Eye.CanvasH));
            Assert.True(services.Eye.Placement.IsExpanded(Card));                                  // opened by hand: it stays open
            Assert.True(services.Eye.Placement.MarksOnly);
            Assert.Contains("marks only", services.Eye.Placement.Note, StringComparison.Ordinal);
        }
        finally
        {
            services.Eye.AppsOverride = null;
            b.Dispose();
        }
    }
}
