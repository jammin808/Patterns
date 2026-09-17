using System.Net;
using System.Net.Sockets;
using System.Text;
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
/// Round 74: the desk as a deck turns it. NAV turns the pages and selects the thing through the
/// menus' own route, opens and closes the settings column, walks BACK, and every reader — STATE's
/// nav row, the wire's NAV, the Eye's desk node — says where the desk is; the build verbs edit the
/// show file from the wire and the pages follow; MENU PAGE lays a page out for a deck; and over a
/// real socket a deck says where its navigator is and records what the desk does as ACTION lines.
/// </summary>
public class NavAppTests
{
    private static string Send(CommandRouter router, string line) => TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse(line)));

    private static JsonDocument Json(string reply)
    {
        Assert.StartsWith("OK {", reply, StringComparison.Ordinal);
        return JsonDocument.Parse(reply[3..]);
    }

    private static JsonElement NavRow(AppServices services)
    {
        using var doc = JsonDocument.Parse(new CommandRouter(services).StateJson());
        return doc.RootElement.GetProperty("nav").Clone();
    }

    private static LookConfig SaveLook(MainViewModel vm, string name)
    {
        vm.ActivePattern.Kind = PatternKind.Grid;
        vm.Show.NewLookName = name;
        vm.Show.SaveLookCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        return LookService.Find(vm.State, name) ?? throw new InvalidOperationException($"look '{name}' was not saved");
    }

    [AvaloniaFact]
    public void NavTurnsThePagesSelectsTheThingOpensTheColumnAndEveryReaderSaysWhereTheDeskIs()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            var router = new CommandRouter(services);
            Assert.Equal(Shell.PanelPage, vm.SelectedPageIndex);

            // A cue added from the wire is selected on the desk; NAV Cues <name> goes to the page with it in the editor and the column open.
            Assert.StartsWith("OK", Send(router, "CUE ADD Doors open"), StringComparison.Ordinal);
            Assert.Equal("Doors open", vm.Cues.SelectedCue!.Name);
            var reply = Send(router, "NAV Cues Doors open");
            Assert.StartsWith("OK", reply, StringComparison.Ordinal);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(Shell.IndexOf("Cues"), vm.SelectedPageIndex);
            Assert.True(vm.PopOut.IsOpen);
            Assert.Contains("Doors open", vm.PopOut.Title);
            var nav = NavRow(services);
            Assert.Equal("Cues", nav.GetProperty("page").GetString());
            Assert.Equal("PLAN", nav.GetProperty("rail").GetString());
            Assert.False(nav.GetProperty("run").GetBoolean());
            Assert.True(nav.GetProperty("settings").GetProperty("open").GetBoolean());
            Assert.Equal("cue", nav.GetProperty("settings").GetProperty("key").GetString());
            Assert.Equal("Panel", nav.GetProperty("back").GetString());
            Assert.StartsWith("cue ", nav.GetProperty("selection").GetString(), StringComparison.Ordinal);
            Assert.Contains("On the Cues page", nav.GetProperty("words").GetString());
            services.Eye.Refresh();
            Assert.Contains(services.Eye.Graph.Find("desk")!.Words, w => w.StartsWith("On the Cues page", StringComparison.Ordinal));

            // The column closes and opens from the wire; a page with no column says so.
            Assert.StartsWith("OK", Send(router, "NAV SETTINGS OFF"), StringComparison.Ordinal);
            Assert.False(vm.PopOut.IsOpen);
            Assert.StartsWith("OK", Send(router, "NAV SETTINGS ON"), StringComparison.Ordinal);
            Assert.True(vm.PopOut.IsOpen);
            Assert.StartsWith("OK", Send(router, "NAV SETTINGS"), StringComparison.Ordinal);            // toggle
            Assert.False(vm.PopOut.IsOpen);

            // BACK walks the pages left; HOME is the panel; a rail goes to its last page; strangers are refused and the desk stays put.
            Assert.StartsWith("OK", Send(router, "NAV Looks"), StringComparison.Ordinal);
            Assert.Equal(Shell.IndexOf("Looks"), vm.SelectedPageIndex);
            Assert.Equal("Cues", NavRow(services).GetProperty("back").GetString());
            Assert.StartsWith("OK", Send(router, "NAV BACK"), StringComparison.Ordinal);
            Assert.Equal(Shell.IndexOf("Cues"), vm.SelectedPageIndex);
            Assert.StartsWith("OK", Send(router, "NAV HOME"), StringComparison.Ordinal);
            Assert.Equal(Shell.PanelPage, vm.SelectedPageIndex);
            Assert.StartsWith("OK", Send(router, "NAV plan"), StringComparison.Ordinal);
            Assert.Equal(Shell.IndexOf("Cues"), vm.SelectedPageIndex);                                // the rail's last page
            var refusedPage = Send(router, "NAV Nowhere");
            Assert.StartsWith("ERR", refusedPage, StringComparison.Ordinal);
            Assert.Equal(Shell.IndexOf("Cues"), vm.SelectedPageIndex);
            var refusedItem = Send(router, "NAV Looks Nope");
            Assert.StartsWith("ERR", refusedItem, StringComparison.Ordinal);
            Assert.Contains("No look", refusedItem);
            Assert.Equal(Shell.IndexOf("Cues"), vm.SelectedPageIndex);
            var noColumn = Send(router, "NAV Help");
            Assert.StartsWith("OK", noColumn, StringComparison.Ordinal);
            Assert.StartsWith("ERR", Send(router, "NAV SETTINGS ON"), StringComparison.Ordinal);
            Assert.Contains("no settings column", Send(router, "NAV SETTINGS ON"));

            // NAV alone: the rails and pages a deck lays its navigator out from, and where the desk is.
            using (var doc = Json(Send(router, "NAV")))
            {
                Assert.Equal(ControlProtocol.DescriptorVersion, doc.RootElement.GetProperty("protocol").GetInt32());   // round 75
                Assert.Equal(5, doc.RootElement.GetProperty("rails").GetArrayLength());
                Assert.Equal(Shell.Pages.Count, doc.RootElement.GetProperty("pages").GetArrayLength());
                Assert.Equal("Help", doc.RootElement.GetProperty("desk").GetProperty("page").GetString());
                Assert.Equal("ADMIN", doc.RootElement.GetProperty("desk").GetProperty("rail").GetString());
                Assert.Equal("Cues", doc.RootElement.GetProperty("rails")[1].GetProperty("pages")[0].GetString());
                Assert.True(doc.RootElement.GetProperty("pages")[3].GetProperty("settings").GetBoolean());       // Cues has a column
            }
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheBuildVerbsEditTheShowFileFromTheWireAndThePagesFollow()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            var router = new CommandRouter(services);

            // Looks: saved by name, updated in place under the same name, deleted — refused while a cue recalls it.
            Assert.StartsWith("ERR", Send(router, "LOOK UPDATE"), StringComparison.Ordinal);            // no look in the show, none on air
            vm.ActivePattern.Kind = PatternKind.Grid;
            Assert.StartsWith("OK", Send(router, "LOOK SAVE Walk-in"), StringComparison.Ordinal);
            var walkIn = LookService.Find(vm.State, "Walk-in")!;
            Assert.Contains("Walk-in", vm.Show.LookNames);
            vm.ActivePattern.Kind = PatternKind.Fractal;
            Assert.Contains("updated", Send(router, "LOOK SAVE walk-in"));
            Assert.Single(vm.State.LooksAndCues.Looks);
            Assert.Equal(PatternKind.Fractal, LookService.Read(walkIn.Json)!.Pattern.Kind);
            Assert.StartsWith("OK", Send(router, "LOOK UPDATE"), StringComparison.Ordinal);             // the picture matches Walk-in, so it is the look on air
            Assert.StartsWith("ERR", Send(router, "LOOK UPDATE Nope"), StringComparison.Ordinal);
            Assert.StartsWith("OK", Send(router, "LOOK UPDATE Walk-in"), StringComparison.Ordinal);
            Assert.StartsWith("OK", Send(router, "CUE ADD Keynote"), StringComparison.Ordinal);
            var keynote = CueStacks.Caller(vm.State).Cues.Single(c => c.Name == "Keynote");
            keynote.Actions.Add(new CueActionConfig { Kind = ShowActionKind.ApplyLook, Target = walkIn.Id });
            var stillUsed = Send(router, "LOOK DELETE Walk-in");
            Assert.StartsWith("ERR", stillUsed, StringComparison.Ordinal);
            Assert.Contains("Keynote", stillUsed);
            keynote.Actions.Clear();
            Assert.StartsWith("OK", Send(router, "LOOK DELETE Walk-in"), StringComparison.Ordinal);
            Assert.Empty(vm.State.LooksAndCues.Looks);
            Assert.DoesNotContain("Walk-in", vm.Show.LookNames);
            Assert.StartsWith("ERR", Send(router, "LOOK DELETE Walk-in"), StringComparison.Ordinal);

            // Cues: added after the standby (numbered to fit) and selected on the desk; deleted by number; refused while armed.
            var stack = CueStacks.Caller(vm.State);
            Assert.StartsWith("OK", Send(router, "CUE ADD"), StringComparison.Ordinal);
            Assert.Equal("New cue", stack.Cues[^1].Name);
            services.CueStack.Standby(keynote.Id);
            Assert.StartsWith("OK", Send(router, "CUE ADD Doors"), StringComparison.Ordinal);
            Assert.Equal("Doors", stack.Cues[stack.Cues.IndexOf(keynote) + 1].Name);
            Assert.Equal("Doors", vm.Cues.SelectedCue!.Name);
            var doors = stack.Cues.Single(c => c.Name == "Doors");
            Assert.True(string.CompareOrdinal(keynote.Number, doors.Number) < 0);
            services.CueStack.Standby(doors.Id);
            Assert.StartsWith("OK", Send(router, $"CUE DELETE {doors.Number}"), StringComparison.Ordinal);
            Assert.DoesNotContain(stack.Cues, c => c.Name == "Doors");
            Assert.NotNull(services.CueStack.StandbyCue);                                                 // standby moved to a neighbour
            services.CueStack.SetArmed(true, ActionOrigin.Desk);
            Assert.Contains("armed", Send(router, "CUE DELETE Keynote"));
            services.CueStack.SetArmed(false, ActionOrigin.Desk);
            Assert.StartsWith("ERR", Send(router, "CUE DELETE Nope"), StringComparison.Ordinal);

            // A preset: the editing target's picture into the Library by name; the chips and the Library follow.
            vm.ActivePattern.Kind = PatternKind.Grid;
            Assert.StartsWith("OK", Send(router, "PRESET SAVE Deck bars"), StringComparison.Ordinal);
            Assert.Contains("Deck bars", services.Store.PresetNames());
            Assert.StartsWith("OK", Send(router, "PRESET Deck bars"), StringComparison.Ordinal);         // and it recalls

            // A lower third: from a preset, named, selected on its page; a second of the same name is made unique; a stranger preset refused.
            var reply = Send(router, "LT NEW Keynote FROM Neon");
            Assert.StartsWith("OK", reply, StringComparison.Ordinal);
            var design = vm.State.LowerThirds.Designs.Single(d => d.Name == "Keynote");
            Assert.Equal("Neon", design.Preset);
            Assert.Same(design, vm.SelectedLowerThird);
            Assert.Equal(Shell.IndexOf("Lower thirds"), vm.SelectedPageIndex);
            Assert.StartsWith("OK", Send(router, "LT NEW Keynote"), StringComparison.Ordinal);
            Assert.Equal(2, vm.State.LowerThirds.Designs.Count(d => d.Name.StartsWith("Keynote", StringComparison.Ordinal)));
            Assert.Equal(1, vm.State.LowerThirds.Designs.Count(d => d.Name == "Keynote"));
            var strange = Send(router, "LT NEW Sponsor FROM Nope");
            Assert.StartsWith("ERR", strange, StringComparison.Ordinal);
            Assert.Contains("Clean", strange);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void MenuPageLaysAPageOutForADeckWithTheDesksOwnFacts()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            var router = new CommandRouter(services);
            var walkIn = SaveLook(vm, "Walk-in");
            services.Actions.Execute(new ShowAction(ShowActionKind.ApplyLook, walkIn.Id), ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();

            using (var doc = Json(Send(router, "MENU PAGE Looks")))
            {
                Assert.Equal("page", doc.RootElement.GetProperty("kind").GetString());
                Assert.Equal("Looks", doc.RootElement.GetProperty("subject").GetString());
                var groups = doc.RootElement.GetProperty("groups").EnumerateArray().ToList();
                Assert.Equal("LOOKS", groups[0].GetProperty("heading").GetString());
                var look = groups[0].GetProperty("entries")[0];
                Assert.Equal("LOOK Walk-in", look.GetProperty("wire").GetString());
                Assert.Equal("LOOK Walk-in", look.GetProperty("menu").GetString());
                Assert.True(look.GetProperty("on").GetBoolean());                                           // it is on air
                var build = groups.Single(g => g.GetProperty("heading").GetString() == "BUILD");
                var save = build.GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("id").GetString() == "build.look.save");
                Assert.True(save.GetProperty("takesText").GetBoolean());
                Assert.Equal("LOOK SAVE *", save.GetProperty("wire").GetString());
                var update = build.GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("id").GetString() == "build.look.update");
                Assert.True(update.GetProperty("enabled").GetBoolean());
                Assert.Contains("Walk-in", update.GetProperty("text").GetString());
                var go = groups.Single(g => g.GetProperty("heading").GetString() == "GO TO");
                Assert.Equal("NAV Looks", go.GetProperty("entries")[0].GetProperty("wire").GetString());
                Assert.False(go.GetProperty("entries")[0].GetProperty("on").GetBoolean());                   // the desk is on the panel
            }
            // The thing's own menu is one step on, by the words the page gave.
            Assert.StartsWith("OK {\"protocol\":1,\"kind\":\"look\"", Send(router, "MENU LOOK Walk-in"), StringComparison.Ordinal);

            // The Cues page: the caller's cues with the standby ticked; the page on the desk and its column.
            Assert.StartsWith("OK", Send(router, "CUE ADD Doors"), StringComparison.Ordinal);
            var doors = CueStacks.Caller(vm.State).Cues.Single(c => c.Name == "Doors");
            services.CueStack.Standby(doors.Id);
            Assert.StartsWith("OK", Send(router, "NAV Cues Doors"), StringComparison.Ordinal);
            using (var doc = Json(Send(router, "MENU PAGE cues")))
            {
                var stack = doc.RootElement.GetProperty("groups")[0];
                Assert.Equal("THE STACK", stack.GetProperty("heading").GetString());
                var cue = stack.GetProperty("entries").EnumerateArray().Single(e => e.GetProperty("id").GetString() == "cue:" + doors.Id);
                Assert.True(cue.GetProperty("on").GetBoolean());
                Assert.Equal($"CUE {doors.Number}", cue.GetProperty("menu").GetString());
                var go = doc.RootElement.GetProperty("groups").EnumerateArray().Single(g => g.GetProperty("heading").GetString() == "GO TO");
                Assert.True(go.GetProperty("entries")[0].GetProperty("on").GetBoolean());                    // the desk is here
                Assert.True(go.GetProperty("entries")[1].GetProperty("on").GetBoolean());                    // the column is open
                Assert.Contains("the desk is here", doc.RootElement.GetProperty("subtitle").GetString());
            }
            // A rail's word lays out its first page; a stranger is refused with the pages.
            Assert.StartsWith("OK {\"protocol\":1,\"kind\":\"page\",\"subject\":\"Screens\"", Send(router, "MENU PAGE setup"), StringComparison.Ordinal);
            var refused = Send(router, "MENU PAGE Nowhere");
            Assert.StartsWith("ERR", refused, StringComparison.Ordinal);
            Assert.Contains("Lower thirds", refused);
        }
        finally
        {
            b.Dispose();
        }
    }

    // ---- over a real socket: a deck's whereabouts and the recorder's feed ----------------------

    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
    }

    private static bool CanConnect(int port)
    {
        try
        {
            using var probe = new TcpClient();
            probe.Connect(IPAddress.Loopback, port);
            return true;
        }
        catch (SocketException)
        {
            return false;
        }
    }

    private static void PumpUntil(Func<bool> condition, int timeoutMs = 15000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
            if (Environment.TickCount64 > deadline) throw new TimeoutException("the condition never held");
        }
    }

    /// <summary>A deck on the wire: one connection kept open, lines sent, replies read with the dispatcher pumped (the router hops to the UI thread), STATE pushes skipped.</summary>
    /// <summary>
    /// Round 75: the recorder behind pairing, and no secret on any feed. RECORD and NAV DECK are a connection's
    /// standing, so they wait for the token like a verb; an admin verb that succeeded on the desk is never fed to a
    /// recording deck (the passcode rides its target); the journal keeps such a verb's kind and outcome and blanks
    /// its target, so the file on disk carries no passcode; and MIDI learn refuses to bind a line that carries one.
    /// </summary>
    [AvaloniaFact]
    public void AnUnpairedDeckCannotRecordOrSayWhereItIsAndNoFeedOrJournalRowCarriesTheAdminPasscode()
    {
        const string token = "K7QM-3XWD-P9RA";
        const string passcode = "hunter2-9931";
        var wire = FreePort();
        var trusted = ControlService.TrustLoopback;
        ControlService.TrustLoopback = false;   // the test's deck connects from loopback, which a desk trusts by default
        var b = TestApp.Boot("patterns-tests-recorder-", dir =>
        {
            var s = SettingsStore.Fresh();
            s.Control.Enabled = true;
            s.Control.TcpPort = wire;
            s.Control.HttpPort = FreePort();
            s.Twin.AcceptCallers = false;
            s.Watchdog.BeaconListenPort = FreePort();
            s.Watchdog.BeaconPort = s.Watchdog.BeaconListenPort;
            File.WriteAllText(Path.Combine(dir, "patterns.settings.json"), JsonUtil.Serialize(s));
        });
        try
        {
            var (services, vm, _) = b;
            vm.State.Control.Token = token;
            Dispatcher.UIThread.RunJobs();
            PumpUntil(() => CanConnect(wire));
            using var deck = new Deck(wire);
            Assert.Equal("OK", deck.Say("HELLO FOH deck module=3.14.0"));

            // The feed and the deck's whereabouts wait for the token like a verb; a question does not.
            Assert.StartsWith("ERR not paired", deck.Say("RECORD ON"), StringComparison.Ordinal);
            Assert.StartsWith("ERR not paired", deck.Say("NAV DECK PLAN › Looks"), StringComparison.Ordinal);
            Assert.StartsWith("OK {", deck.Say("NAV"), StringComparison.Ordinal);
            Assert.Equal("OK paired", deck.Say("AUTH " + token));
            Assert.Equal("OK at PLAN › Looks", deck.Say("NAV DECK PLAN › Looks"));
            Assert.StartsWith("OK recording", deck.Say("RECORD ON"), StringComparison.Ordinal);

            // An admin verb that succeeded on the desk is never fed; the next action is, which proves the channel.
            services.Control.Feed(new ShowAction(ShowActionKind.UpdateApply, passcode), ActionOrigin.Desk, ActionResult.Requested("Updating"));
            services.Control.Feed(new ShowAction(ShowActionKind.Restart, passcode), ActionOrigin.Desk, ActionResult.Requested("Restarting"));
            Assert.True(services.Actions.Execute(ShowActionKind.CueHoldOn, ActionOrigin.Desk).Ok);
            Assert.Equal("ACTION CUE HOLD ON", deck.Next());

            // The journal keeps an admin verb's kind and outcome, never its target, whatever the outcome.
            var apply = services.Actions.Execute(new ShowAction(ShowActionKind.UpdateApply, passcode), ActionOrigin.Desk);
            var restart = services.Actions.Execute(new ShowAction(ShowActionKind.Restart, passcode), ActionOrigin.Desk);
            Assert.False(apply.Ok);
            Assert.False(restart.Ok);
            Assert.False(Secrets.Carries(apply.Message + restart.Message, passcode));
            var rows = services.Journal.Tail(50).Where(e => e.Kind is nameof(ShowActionKind.UpdateApply) or nameof(ShowActionKind.Restart)).ToList();
            Assert.Equal(2, rows.Count);
            Assert.All(rows, e => Assert.Equal("", e.Target));

            // A control cannot be bound to a line that carries the passcode, and the refusal does not repeat it.
            var learn = deck.Say("MIDI LEARN UPDATE APPLY " + passcode);
            Assert.StartsWith("ERR", learn, StringComparison.Ordinal);
            Assert.Contains("carries the passcode", learn, StringComparison.Ordinal);
            Assert.False(Secrets.Carries(learn, passcode));

            // The file on disk carries no passcode after all of that.
            Assert.False(Secrets.Carries(File.ReadAllText(services.Journal.Path), passcode));
        }
        finally
        {
            ControlService.TrustLoopback = trusted;
            b.Dispose();
        }
    }

    private sealed class Deck : IDisposable
    {
        private readonly TcpClient _tcp = new();
        private readonly StreamReader _reader;
        private readonly NetworkStream _stream;

        public Deck(int port)
        {
            _tcp.Connect(IPAddress.Loopback, port);
            _tcp.ReceiveTimeout = 8000;
            _stream = _tcp.GetStream();
            _reader = new StreamReader(_stream, Encoding.UTF8);
            var greeting = TestApp.Pump(Task.Run(() => _reader.ReadLine())) ?? "";
            Assert.StartsWith("STATE {", greeting, StringComparison.Ordinal);   // the greeting
        }

        /// <summary>The next line that is not a STATE push.</summary>
        public string Next()
        {
            while (true)
            {
                var line = TestApp.Pump(Task.Run(() => _reader.ReadLine())) ?? throw new IOException("the desk closed the connection");
                if (!line.StartsWith("STATE ", StringComparison.Ordinal)) return line;
            }
        }

        public string Say(string line)
        {
            _stream.Write(Encoding.UTF8.GetBytes(line + "\n"));
            return Next();
        }

        public void Dispose()
        {
            _reader.Dispose();
            _tcp.Dispose();
        }
    }

    [AvaloniaFact]
    public void ADeckSaysWhereItsNavigatorIsAndRecordsWhatTheDeskDoesAsActionLines()
    {
        var wire = FreePort();
        var b = TestApp.Boot("patterns-tests-nav-", dir =>
        {
            var s = SettingsStore.Fresh();
            s.Control.Enabled = true;
            s.Control.TcpPort = wire;
            s.Control.HttpPort = FreePort();
            s.Twin.AcceptCallers = false;
            s.Watchdog.BeaconListenPort = FreePort();
            s.Watchdog.BeaconPort = s.Watchdog.BeaconListenPort;
            File.WriteAllText(Path.Combine(dir, "patterns.settings.json"), JsonUtil.Serialize(s));
        });
        try
        {
            var (services, vm, _) = b;
            PumpUntil(() => CanConnect(wire));
            var walkIn = SaveLook(vm, "Walk-in");
            using var deck = new Deck(wire);
            Assert.Equal("OK", deck.Say("HELLO FOH deck module=3.14.0"));

            // Where the navigator is: kept per connection, on STATE's decks row, the Remote page's line and the Eye's deck node.
            Assert.Equal("OK at PLAN › Cues › 03.020", deck.Say("NAV DECK PLAN › Cues › 03.020"));
            PumpUntil(() => services.Control.Decks.Count == 1 && services.Control.Decks[0].Where.Length > 0);
            Assert.Equal("PLAN › Cues › 03.020", services.Control.Decks[0].Where);
            Assert.Contains("navigator at PLAN › Cues › 03.020", services.Control.Decks[0].Line);
            using (var doc = JsonDocument.Parse(new CommandRouter(services).StateJson()))
            {
                Assert.Equal("PLAN › Cues › 03.020", doc.RootElement.GetProperty("decks")[0].GetProperty("where").GetString());
                Assert.False(doc.RootElement.GetProperty("decks")[0].GetProperty("recording").GetBoolean());
                Assert.Equal("FOH deck", doc.RootElement.GetProperty("nav").GetProperty("decks")[0].GetProperty("name").GetString());
            }
            services.Eye.Refresh();
            var node = services.Eye.Graph.Find("deck:FOH deck@127.0.0.1")!;
            Assert.Contains("navigator: PLAN › Cues › 03.020", node.Words);

            // RECORD ON: what the desk does comes back as the line that reproduces it; automation and the deck's own presses do not.
            Assert.StartsWith("OK recording", deck.Say("RECORD ON"), StringComparison.Ordinal);
            PumpUntil(() => services.Control.Decks[0].Recording);
            services.Actions.Execute(new ShowAction(ShowActionKind.ApplyLook, walkIn.Id), ActionOrigin.Desk);
            Assert.Equal("ACTION LOOK Walk-in", deck.Next());
            services.Actions.Execute(new ShowAction(ShowActionKind.ClockOn), new ActionOrigin(OriginKind.Cue, "03.020"));   // a cue's step: not a press
            services.Actions.Execute(new ShowAction(ShowActionKind.CueHoldOn), ActionOrigin.Keyboard);
            Assert.Equal("ACTION CUE HOLD ON", deck.Next());
            Assert.Equal("OK", deck.Say("CUE HOLD OFF"));                                                   // its own press: the reply, and no echo
            services.Actions.Execute(new ShowAction(ShowActionKind.Take), ActionOrigin.Desk);                // a TAKE has no line on the wire
            services.Actions.Execute(new ShowAction(ShowActionKind.ApplyLook, "Nope"), ActionOrigin.Desk);   // refused: nothing happened
            Assert.Equal("OK PONG", deck.Say("PING"));                                                      // nothing else was fed
            Assert.Equal("OK recording off", deck.Say("RECORD OFF"));
            PumpUntil(() => !services.Control.Decks[0].Recording);
            services.Actions.Execute(new ShowAction(ShowActionKind.CueHoldOn), ActionOrigin.Keyboard);
            Assert.Equal("OK PONG", deck.Say("PING"));
        }
        finally
        {
            b.Dispose();
        }
    }
}
