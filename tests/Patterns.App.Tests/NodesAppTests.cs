using System.Net;
using System.Net.Http;
using System.Net.Sockets;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The nodes and the desk, end to end in one process: the beacon onto the Nodes page and the rail; a
/// caller node built from the kernel alone, rehearsing on paper and then calling the desk it follows;
/// a stage timer node showing the desk's clock and sending its receipts home.
/// </summary>
public class NodesAppTests
{
    private static int FreePort()
    {
        using var probe = new TcpListener(IPAddress.Loopback, 0);
        probe.Start();
        return ((IPEndPoint)probe.LocalEndpoint).Port;
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

    /// <summary>A settings file for a fresh folder: its own ports, so two processes' worth of desks live in one test.</summary>
    private static void Settings(string dir, Action<ShowState> edit)
    {
        var state = SettingsStore.Fresh();
        edit(state);
        File.WriteAllText(Path.Combine(dir, "patterns.settings.json"), JsonUtil.Serialize(state));
    }

    /// <summary>A node from the kernel alone, in a fresh folder with its own ports: the settings the test wants, built, started.</summary>
    private static (NodeHost Host, string Dir) BootNode(NodeKind kind, Action<ShowState> edit)
    {
        var dir = Path.Combine(Path.GetTempPath(), $"patterns-tests-{NodeKinds.Wire(kind)}-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        Settings(dir, s =>
        {
            s.Control.Enabled = true;
            s.Control.HttpPort = FreePort();
            s.Control.TcpPort = FreePort();
            s.Watchdog.BeaconListenPort = FreePort();
            s.Watchdog.BeaconPort = s.Watchdog.BeaconListenPort;
            edit(s);
        });
        var host = NodeHost.Build(kind, new SettingsStore(dir));
        host.Start();
        return (host, dir);
    }

    private static string Post(HttpClient http, string path, string body)
        => TestApp.Pump(http.PostAsync(path, new StringContent(body)).ContinueWith(t => t.Result.Content.ReadAsStringAsync().Result));

    [AvaloniaFact]
    public void TheDeskHearsANodeOnTheBeaconListsItOnThePageAndCountsItOnTheRail()
    {
        var b = TestApp.Boot(prepare: dir => Settings(dir, s => { s.Twin.Port = FreePort(); s.Control.HttpPort = FreePort(); s.Control.TcpPort = FreePort(); s.Watchdog.BeaconListenPort = FreePort(); s.Watchdog.BeaconPort = s.Watchdog.BeaconListenPort; }));
        try
        {
            var (services, vm, _) = b;
            Assert.True(services.IsDesk);
            Assert.Equal("desk", services.Beacon.Build().Kind);
            // A desk accepts callers by default: it gets a key made for it, listens on the twin's port, and its beacon names the port.
            PumpUntil(() => services.Twin.LinkPort > 0);
            Assert.True(TwinKeys.LooksMade(vm.State.Twin.Key), vm.State.Twin.Key);
            Assert.Equal(vm.State.Twin.Port, services.Beacon.Build().Link);
            Assert.Equal(vm.State.Control.HttpPort, services.Beacon.Build().Http);
            Assert.StartsWith("Twin off — callers may link on port", services.Twin.Status);

            services.Nodes.Poll();
            Assert.Equal("NONE", vm.NodesWord);
            Assert.Empty(services.Nodes.Nodes);

            // A caller node's beacon arrives: a card, a count, a hue.
            services.Beacon.Hear(new Beacon { Instance = "c1", Kind = "caller", Machine = "CALLER-PC", Show = "Gala", Http = 9696 }, new IPEndPoint(IPAddress.Parse("10.0.0.20"), 9700));
            services.Beacon.Hear(new Beacon { Instance = "a1", Kind = "arcade", Machine = "HUB-PC" }, new IPEndPoint(IPAddress.Parse("10.0.0.30"), 9700));
            services.Nodes.Poll();
            Assert.Equal(new[] { "Caller", "Arcade" }, services.Nodes.Nodes.Select(c => c.KindLabel));
            Assert.StartsWith("Caller CALLER-PC — Gala · heard just now", services.Nodes.Nodes[0].Line);
            Assert.Equal("http://10.0.0.20:9696/", services.Nodes.Nodes[0].PagesUrl);
            Assert.Equal("2 NEAR", vm.NodesWord);
            Assert.Equal("#5FD0FF", vm.NodesHue);
            Assert.Contains("1 caller", vm.NodesLine);
            Assert.Contains("1 arcade", vm.NodesLine);

            // The wire lists the same cards.
            var json = TestApp.Pump(new CommandRouter(services).ExecuteAsync(ControlProtocol.Parse("NODES")));
            Assert.StartsWith("OK", json);
            Assert.Contains("\"kind\":\"caller\"", json);
            Assert.Contains("\"name\":\"HUB-PC\"", json);
            Assert.Contains("\"linked\":0", json);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void ACallerNodeRehearsesOnPaperAloneThenFollowsADeskOffersItsPlanCallsItAndItsNotesLandThere()
    {
        var twinPort = FreePort();
        var desk = TestApp.Boot("patterns-tests-desk-", dir => Settings(dir, s =>
        {
            s.Name = "Gala";
            s.Twin.Port = twinPort;
            s.Twin.Key = "hunter2";
            s.Control.HttpPort = FreePort();
            s.Control.TcpPort = FreePort();
            s.Watchdog.BeaconListenPort = FreePort();
            s.Watchdog.BeaconPort = s.Watchdog.BeaconListenPort;
            var stack = CueStacks.Caller(s);
            stack.Cues.Add(new RunCueConfig { Number = "01", Name = "Walk-in" });
        }));
        NodeHost? node = null;
        var dir = "";
        try
        {
            PumpUntil(() => desk.Services.Twin.LinkPort == twinPort);

            // The caller planned at home: two cues of its own, a pad — and no desk at all.
            (node, dir) = BootNode(NodeKind.Caller, s =>
            {
                s.Name = "Planned at home";
                s.Twin.Key = "hunter2";
                var stack = CueStacks.Caller(s);
                stack.Scratchpad = "Doors 18:30";
                stack.Cues.Add(new RunCueConfig { Number = "01", Name = "Walk-in" });
                stack.Cues.Add(new RunCueConfig { Number = "02", Name = "Welcome", Actions = { new CueActionConfig { Kind = ShowActionKind.BlackoutOn } } });
            });
            var c = node;
            var d = desk.Services;
            Assert.Equal(NodeKind.Caller, c.Kind);
            Assert.True(c.IsFollower);
            Assert.NotNull(c.Twin);
            Assert.Contains("caller node", c.OutputsHeldBy);
            Assert.StartsWith("CALLER — planning alone", c.Twin!.Status);
            Assert.Equal("caller", c.Kernel.Beacon.Build().Kind);

            // Alone: the stack is rehearsed on paper — ARM, GO moves the stack and runs nothing; the desk's verbs say where they would run.
            var stackId = CueStacks.Caller(c.State).Id;
            Assert.True(c.Actions.Execute(ShowActionKind.ListArm, ActionOrigin.Desk, stackId).Ok);
            Assert.True(c.CueStack.Armed);
            Assert.Equal("Walk-in", c.CueStack.StandbyCue?.Name);
            var go = c.Actions.Execute(ShowActionKind.CueGo, ActionOrigin.Desk);
            Assert.True(go.Ok, go.Message);
            Assert.Contains("rehearsed on paper", go.Message);
            Assert.Equal("Walk-in", c.CueStack.LastCue?.Name);
            Assert.Equal("Welcome", c.CueStack.StandbyCue?.Name);
            Assert.Equal("01 Walk-in", c.AirLabel);
            Assert.Single(c.CueStack.History);
            var fired = c.Actions.Execute(ShowActionKind.CueFire, ActionOrigin.Desk, "02");   // a cue with a step: read, not run
            Assert.True(fired.Ok, fired.Message);
            Assert.Contains("1 step read, none run", fired.Message);
            Assert.False(desk.Vm.State.Blackout);                                       // the paper GO ran no step anywhere
            Assert.False(c.State.Blackout);
            var blackout = c.Actions.Execute(ShowActionKind.BlackoutOn, ActionOrigin.Desk);
            Assert.False(blackout.Ok);
            Assert.Contains("LINK to a desk", blackout.Message);
            Assert.Contains(c.Kernel.Journal.Tail(8), e => e.Kind == "CueGo");
            Assert.Contains(c.Kernel.Journal.Tail(8), e => e.Kind == "BlackoutOn" && e.Outcome == "Refused");

            // The wire on the node: the caller's list, the stage, the link.
            var router = c.NewRouter();
            var list = TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("CUE LIST")));
            Assert.StartsWith("OK {", list);
            Assert.Contains("\"name\":\"Welcome\"", list);
            Assert.StartsWith("OK {\"rev\"", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("STAGE STATUS"))));
            Assert.StartsWith("OK {", TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse("TWIN STATUS"))));
            var state = router.StateJson();
            Assert.Contains("\"kind\":\"caller\"", state);
            Assert.Contains("\"linked\":false", state);
            Assert.Contains("\"airLabel\":\"01 Walk-in\"", state);

            // LINK on the desk's card: the caller dials with the key, the desk's show lands, the plan is offered.
            var card = new NodeCard("d1", NodeKind.Desk, "FOH-PC", IPAddress.Loopback, "Gala", "", twinPort, 0, TimeSpan.Zero, false);
            var words = c.Twin.LinkTo(card);
            Assert.StartsWith("Linking to FOH-PC", words);
            PumpUntil(() => c.Twin.Phase == TwinPhase.InStep);
            Assert.Equal("Gala", c.State.Name);
            Assert.Equal(new[] { "Walk-in" }, CueStacks.Caller(c.State).Cues.Select(x => x.Name));   // the desk's cues are here now
            Assert.StartsWith("CALLER for", c.Twin.Status);
            Assert.True(c.Twin.IsLinkedToDesk);
            PumpUntil(() => d.Twin.CallerCount == 1);
            PumpUntil(() => d.Twin.Plans.Count == 1);
            var offer = d.Twin.Plans[0];
            Assert.Equal("2 cues in 1 stack", offer.Count);
            Assert.Contains("2 cues to add, 1 the desk has that the plan does not", offer.Words);
            d.Nodes.Poll();
            Assert.Equal("1 LINKED", desk.Vm.NodesWord);
            Assert.Contains("\"linked\":1", d.Nodes.StatusJson());

            // APPLY on the desk: the plan lands, a version kept first — and mirrors to the caller.
            desk.Vm.ApplyPlanCommand.Execute(offer);
            Assert.Equal(new[] { "Walk-in", "Welcome" }, CueStacks.Caller(desk.Vm.State).Cues.Select(x => x.Name));
            Assert.Equal("Doors 18:30", CueStacks.Caller(desk.Vm.State).Scratchpad);
            Assert.Empty(d.Twin.Plans);
            Assert.NotEmpty(d.Store.ListBackups());
            PumpUntil(() => CueStacks.Caller(c.State).Cues.Count == 2 && CueStacks.Caller(c.State).Scratchpad == "Doors 18:30");

            // The caller calls: a verb pressed there runs on the desk as the caller's own; the desk's runtime reads back.
            var sent = c.Actions.Execute(new ShowAction(ShowActionKind.MessageOn, "", "From the caller"), ActionOrigin.Desk);
            Assert.True(sent.Ok, sent.Message);
            Assert.Contains("sent to", sent.Message);
            PumpUntil(() => d.AirState.Overlays.Message.Text == "From the caller");
            d.CueStack.SetArmed(true, ActionOrigin.Desk);
            d.CueStack.Standby(CueStacks.Caller(desk.Vm.State).Cues[1].Id);
            PumpUntil(() => c.Twin.Live?.Standby == CueStacks.Caller(desk.Vm.State).Cues[1].Id && c.Twin.Live.Armed);
            Assert.Equal(CueStacks.Caller(desk.Vm.State).Cues[1].Id, c.CueStack.Runtime.StandbyCueId);
            Assert.True(c.CueStack.Armed);
            Assert.Contains("\"linked\":true", router.StateJson());

            // The view model over it: the Run surface's rows, the pages a caller shows, the link's words.
            var vm = new NodeViewModel(c);
            vm.Poll();
            Assert.True(vm.HasRun && vm.HasCues && vm.HasStage && !vm.HasArcade);
            Assert.Equal(NodeViewModel.RunTab, vm.SelectedTab);
            Assert.Equal(new[] { "Walk-in", "Welcome" }, vm.Run.Rows.Select(r => r.Name));
            Assert.True(vm.Run.IsArmed);
            Assert.Equal("Patterns — Caller node", vm.WindowTitle);
            Assert.StartsWith("CALLER for", vm.NodesLinkWords);
            Assert.True(vm.IsCallerNode);
            Assert.Equal(2, vm.Cues.Rows.Count);

            // A note typed on the caller lands on the desk's cue, and nothing ping-pongs.
            CueStacks.Caller(c.State).Cues[0].Notes = "Lights to half";
            PumpUntil(() => CueStacks.Caller(desk.Vm.State).Cues[0].Notes == "Lights to half");
            Thread.Sleep(600);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new[] { "Walk-in", "Welcome" }, CueStacks.Caller(desk.Vm.State).Cues.Select(x => x.Name));
            Assert.Equal(new[] { "Walk-in", "Welcome" }, CueStacks.Caller(c.State).Cues.Select(x => x.Name));
            Assert.Equal(TwinPhase.InStep, c.Twin.Phase);

            // UNLINK: the caller plans on with the show as it stands; the desk counts none.
            Assert.StartsWith("Unlinked", c.Twin.Unlink());
            PumpUntil(() => d.Twin.CallerCount == 0 && c.Twin.Phase == TwinPhase.Off);
            Assert.Equal(new[] { "Walk-in", "Welcome" }, CueStacks.Caller(c.State).Cues.Select(x => x.Name));
            Assert.False(c.Twin.IsLinkedToDesk);
        }
        finally
        {
            node?.Dispose();
            desk.Dispose();
            if (dir.Length > 0) { try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ } }
        }
    }

    [AvaloniaFact]
    public void ACallersPlanAndShapeChangingEditsWaitWhileTheDesksStackIsArmedAndItsNotesLandAtOnce()
    {
        var twinPort = FreePort();
        var desk = TestApp.Boot("patterns-tests-desk-", dir => Settings(dir, s =>
        {
            s.Name = "Gala";
            s.Twin.Port = twinPort;
            s.Twin.Key = "hunter2";
            s.Control.HttpPort = FreePort();
            s.Control.TcpPort = FreePort();
            s.Watchdog.BeaconListenPort = FreePort();
            s.Watchdog.BeaconPort = s.Watchdog.BeaconListenPort;
            var stack = CueStacks.Caller(s);
            stack.Cues.Add(new RunCueConfig { Number = "01", Name = "Walk-in" });
        }));
        NodeHost? node = null;
        var dir = "";
        try
        {
            PumpUntil(() => desk.Services.Twin.LinkPort == twinPort);
            (node, dir) = BootNode(NodeKind.Caller, s =>
            {
                s.Name = "Planned at home";
                s.Twin.Key = "hunter2";
                var stack = CueStacks.Caller(s);
                stack.Scratchpad = "Doors 18:30";
                stack.Cues.Add(new RunCueConfig { Number = "01", Name = "Walk-in" });
                stack.Cues.Add(new RunCueConfig { Number = "02", Name = "Welcome" });
            });
            var c = node;
            var d = desk.Services;
            desk.Vm.IsSandboxActive = false;                                         // the desk's edits are the program's: they mirror as they land
            var card = new NodeCard("d1", NodeKind.Desk, "FOH-PC", IPAddress.Loopback, "Gala", "", twinPort, 0, TimeSpan.Zero, false);
            c.Twin!.LinkTo(card);
            PumpUntil(() => c.Twin!.Phase == TwinPhase.InStep);
            PumpUntil(() => d.Twin.CallerCount == 1 && d.Twin.Plans.Count == 1);

            // The desk's stack is armed: the show is running. APPLY does not replace the stack under the
            // operator's hands — the plan waits on the page, said so. The caller's pad and notes are the
            // show and land as ever; a cue added waits.
            d.CueStack.SetArmed(true, ActionOrigin.Desk);
            Assert.Equal("Walk-in", d.CueStack.StandbyCue?.Name);
            var offer = d.Twin.Plans[0];
            var waits = d.Twin.ApplyPlan(offer);
            Assert.Equal(ActionStatus.Requested, waits.Status);
            Assert.Contains("waits: the stack is armed — it lands on DISARM", waits.Message);
            Assert.Single(d.Twin.Plans);
            Assert.True(d.Twin.Plans[0].Queued);
            Assert.Equal(new[] { "Walk-in" }, CueStacks.Caller(desk.Vm.State).Cues.Select(x => x.Name));
            Assert.Contains(d.Kernel.Journal.Tail(6), e => e.Kind == "PlanQueued");
            CueStacks.Caller(c.State).Scratchpad = "Doors 19:00";
            CueStacks.Caller(c.State).Cues[0].Notes = "Lights to half";
            PumpUntil(() => CueStacks.Caller(desk.Vm.State).Scratchpad == "Doors 19:00" && CueStacks.Caller(desk.Vm.State).Cues[0].Notes == "Lights to half");
            Assert.Equal(0, d.Twin.QueuedEdits);
            CueStacks.Caller(c.State).Cues.Add(new RunCueConfig { Number = "03", Name = "Encore" });
            PumpUntil(() => d.Twin.QueuedEdits == 1);
            Thread.Sleep(400);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new[] { "Walk-in" }, CueStacks.Caller(desk.Vm.State).Cues.Select(x => x.Name));
            Assert.Equal("Walk-in", d.CueStack.StandbyCue?.Name);
            Assert.Contains("waits: the stack is armed", desk.Vm.StatusMessage);
            Assert.Contains(d.Kernel.Journal.Tail(6), e => e.Kind == "SectionQueued" && e.Outcome == "Requested");

            // DISARM: the caller's edit lands, then the plan — the operator's press wins; the page is clear.
            d.CueStack.SetArmed(false, ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();
            PumpUntil(() => CueStacks.Caller(desk.Vm.State).Cues.Count == 2 && d.Twin.Plans.Count == 0);
            Assert.Equal(new[] { "Walk-in", "Welcome" }, CueStacks.Caller(desk.Vm.State).Cues.Select(x => x.Name));
            Assert.Equal(0, d.Twin.QueuedEdits);
            Assert.Contains(d.Kernel.Journal.Tail(8), e => e.Kind == "PlanApply");
            Assert.Contains(d.Kernel.Journal.Tail(8), e => e.Kind == "SectionQueued" && e.Outcome == "Done");
            PumpUntil(() => CueStacks.Caller(c.State).Cues.Count == 2);

            // Armed again, the caller adds a cue, and the desk edits the same cues meanwhile: the desk's is
            // the newer, the waiting one is set aside and said; added again, it lands on the next DISARM.
            d.CueStack.SetArmed(true, ActionOrigin.Desk);
            CueStacks.Caller(c.State).Cues.Add(new RunCueConfig { Number = "03", Name = "Encore" });
            PumpUntil(() => d.Twin.QueuedEdits == 1);
            d.BulkEdit(() => CueStacks.Caller(desk.Vm.State).Cues[1].Notes = "From the desk");   // the desk's edit, as the cue editor makes it
            PumpUntil(() => d.Twin.QueuedEdits == 0);
            Assert.Contains("was set aside", desk.Vm.StatusMessage);
            PumpUntil(() => CueStacks.Caller(c.State).Cues.Count == 2 && CueStacks.Caller(c.State).Cues[1].Notes == "From the desk");   // the desk's edit reached the caller, and its own cue went with it
            CueStacks.Caller(c.State).Cues.Add(new RunCueConfig { Number = "03", Name = "Encore" });
            PumpUntil(() => d.Twin.QueuedEdits == 1);
            d.CueStack.SetArmed(false, ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();
            PumpUntil(() => CueStacks.Caller(desk.Vm.State).Cues.Count == 3);
            Assert.Equal("Encore", CueStacks.Caller(desk.Vm.State).Cues[2].Name);
            Assert.Equal("From the desk", CueStacks.Caller(desk.Vm.State).Cues[1].Notes);
        }
        finally
        {
            node?.Dispose();
            desk.Dispose();
            if (dir.Length > 0) { try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ } }
        }
    }

    [AvaloniaFact]
    public void AStageTimerNodeKeepsItsOwnClockAloneThenShowsTheDesksAndSendsItsReceiptsHome()
    {
        var twinPort = FreePort();
        var desk = TestApp.Boot("patterns-tests-desk-", dir => Settings(dir, s =>
        {
            s.Name = "Gala";
            s.Twin.Port = twinPort;
            s.Twin.Key = "hunter2";
            s.Control.HttpPort = FreePort();
            s.Control.TcpPort = FreePort();
            s.Watchdog.BeaconListenPort = FreePort();
            s.Watchdog.BeaconPort = s.Watchdog.BeaconListenPort;
        }));
        NodeHost? node = null;
        var dir = "";
        try
        {
            PumpUntil(() => desk.Services.Twin.LinkPort == twinPort);
            var d = desk.Services;
            desk.Vm.IsSandboxActive = false;

            (node, dir) = BootNode(NodeKind.Timer, s => s.Twin.Key = "hunter2");
            var t = node;
            Assert.Equal(NodeKind.Timer, t.Kind);
            Assert.StartsWith("STAGE TIMER — its own clock", t.Twin!.Status);
            Assert.Equal("timer", t.Kernel.Beacon.Build().Kind);

            // Alone: a stage timer of its own — started, read on the display, paused; the caller's verbs are not its.
            var started = t.Actions.Execute(new ShowAction(ShowActionKind.CountdownStart, "", "5"), ActionOrigin.Desk);
            Assert.True(started.Ok, started.Message);
            Assert.Equal(StageTimerPhase.Running, t.Stage.Time().Phase);
            Assert.True(t.Stage.Time().RemainingSeconds > 290);
            var vm = new NodeViewModel(t);
            vm.Poll();
            Assert.True(vm.HasStage && !vm.HasRun && !vm.HasCues && !vm.HasArcade);
            Assert.Equal(NodeViewModel.StageTab, vm.SelectedTab);
            Assert.False(vm.StageControlsOpen);
            Assert.Matches("^[45]:", vm.DisplayTime);
            Assert.False(vm.DisplayHasMessage);
            Assert.True(t.Actions.Execute(ShowActionKind.TimerPause, ActionOrigin.Desk).Ok);
            Assert.True(t.State.Stage.Paused);
            var go = t.Actions.Execute(ShowActionKind.CueGo, ActionOrigin.Desk);
            Assert.False(go.Ok);
            Assert.Contains("is the caller's", go.Message);
            using var browser = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{t.State.Control.HttpPort}/"), Timeout = TimeSpan.FromSeconds(8) };
            Assert.Contains("Patterns Stage", TestApp.Pump(browser.GetStringAsync("stage")));
            Assert.Contains("/timer", TestApp.Pump(browser.GetStringAsync("/")));
            Assert.Contains("\"paused\":true", TestApp.Pump(browser.GetStringAsync("api/stage")));

            // LINK: the desk's show lands, its clock and its messages show here; the desk counts the timer as linked.
            var card = new NodeCard("d1", NodeKind.Desk, "FOH-PC", IPAddress.Loopback, "Gala", "", twinPort, 0, TimeSpan.Zero, false);
            Assert.StartsWith("Linking to FOH-PC", t.Twin.LinkTo(card));
            PumpUntil(() => t.Twin.Phase == TwinPhase.InStep);
            Assert.Equal("Gala", t.State.Name);
            Assert.StartsWith("STAGE TIMER for", t.Twin.Status);
            Assert.Contains("the desk's clock and its messages show here", t.Twin.Status);
            PumpUntil(() => d.Twin.CallerCount == 1);
            d.Nodes.Poll();
            Assert.Equal("1 LINKED", desk.Vm.NodesWord);
            Assert.True(d.Actions.Execute(new ShowAction(ShowActionKind.CountdownStart, "", "10"), ActionOrigin.Desk).Ok);
            PumpUntil(() => t.State.Countdown.Enabled && t.State.Countdown.DurationMinutes == 10 && !t.State.Stage.Paused);
            Assert.Equal(StageTimerPhase.Running, t.Stage.Time().Phase);
            Assert.True(t.Stage.Time().RemainingSeconds > 590);
            var said = d.Actions.Execute(new ShowAction(ShowActionKind.StageMessage, "speaker", "Wrap up"), ActionOrigin.Desk);
            Assert.True(said.Ok, said.Message);
            PumpUntil(() => t.Stage.Pending("speaker")?.Text == "Wrap up");
            vm.Poll();
            Assert.True(vm.DisplayHasMessage);
            Assert.Equal("Wrap up", vm.DisplayMessage);

            // The display's ACK goes home: the desk marks the message seen, and the receipt mirrors back.
            vm.DisplayAckCommand.Execute(null);
            PumpUntil(() => d.Stage.Pending("speaker") is null);
            Assert.Contains("seen at", desk.Vm.StatusMessage);
            PumpUntil(() => t.Stage.Pending("speaker") is null);
            vm.Poll();
            Assert.False(vm.DisplayHasMessage);

            // The node's own stage page acks the same way; a timer verb from here runs on the desk.
            Assert.True(d.Actions.Execute(new ShowAction(ShowActionKind.StageMessage, "crew", "Mic 2 live"), ActionOrigin.Desk).Ok);
            PumpUntil(() => t.Stage.Pending("crew") is not null);
            var crew = t.Stage.Pending("crew")!;
            Assert.Equal("{\"ok\":true}", Post(browser, "api/stage/ack", crew.Id));
            PumpUntil(() => d.Stage.Pending("crew") is null);
            var paused = t.Actions.Execute(ShowActionKind.TimerPause, ActionOrigin.Desk);
            Assert.True(paused.Ok, paused.Message);
            Assert.Contains("sent to", paused.Message);
            PumpUntil(() => d.AirState.Stage.Paused);
            PumpUntil(() => t.State.Stage.Paused);
            Assert.Contains("\"phase\":\"paused\"", t.Stage.StatusJson());

            // UNLINK: the clock is this node's own again; the desk counts none.
            Assert.StartsWith("Unlinked — the clock", t.Twin.Unlink());
            PumpUntil(() => d.Twin.CallerCount == 0 && t.Twin.Phase == TwinPhase.Off);
        }
        finally
        {
            node?.Dispose();
            desk.Dispose();
            if (dir.Length > 0) { try { Directory.Delete(dir, recursive: true); } catch { /* best effort */ } }
        }
    }
}
