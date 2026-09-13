using System.Net;
using System.Net.Sockets;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>The nodes on the desk: a caller node's own shape, the beacon onto the Nodes page and the rail, and a caller linked to a desk end to end in one process.</summary>
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

    [AvaloniaFact]
    public void ACallerNodeOpensOnItsRunPageHoldsItsOutputsAndShowsOnlyItsPages()
    {
        var b = TestApp.Boot(profile: NodeKind.Caller, prepare: dir => Settings(dir, s => { s.Control.Enabled = false; s.Watchdog.BeaconListenPort = FreePort(); s.Watchdog.BeaconPort = s.Watchdog.BeaconListenPort; }));
        try
        {
            var (services, vm, _) = b;
            Assert.Equal(NodeKind.Caller, services.Profile);
            Assert.False(services.IsDesk);
            Assert.Contains("caller node", services.OutputsHeldBy);
            Assert.False(services.Actions.Execute(ShowActionKind.OutputsOn, ActionOrigin.Desk).Ok);
            Assert.Equal("Patterns — Caller node", vm.WindowTitle);
            Assert.True(vm.IsCallerNode);

            // The rail: the groups with a page for a caller, and only those pages.
            Assert.Equal(new[] { "SHOW", "PLAN", "BUILD", "SETUP", "ADMIN" }, vm.GroupStrip.Select(g => g.Label));
            vm.SelectPage(Shell.HomePage(NodeKind.Caller));
            Assert.Equal("Run", vm.PageStrip.Single(p => p.IsCurrent).Header);
            Assert.Equal(new[] { "Run" }, vm.PageStrip.Select(p => p.Header));
            vm.SelectGroup(ShellGroup.Setup);
            Assert.Equal(new[] { "Nodes" }, vm.PageStrip.Select(p => p.Header));
            Assert.False(Shell.IsVisible(NodeKind.Caller, "Pattern"));
            Assert.True(Shell.IsVisible(NodeKind.Desk, "Pattern"));

            // The beacon says what it is; alone it plans.
            Assert.Equal("caller", services.Beacon.Build().Kind);
            Assert.Equal(0, services.Beacon.Build().Link);
            Assert.StartsWith("CALLER — planning alone", services.Twin.Status);
            Assert.Contains("Caller", services.Nodes.Identity);
            Assert.False(services.Actions.Execute(ShowActionKind.TwinTakeOver, ActionOrigin.Desk).Ok);
        }
        finally
        {
            b.Dispose();
        }
    }

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
    public void ACallerLinksToADeskFollowsItsShowOffersItsPlanCallsItAndItsNotesLandThere()
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
        TestApp.Booted? caller = null;
        try
        {
            PumpUntil(() => desk.Services.Twin.LinkPort == twinPort);

            // The caller planned at home: two cues of its own.
            caller = TestApp.Boot("patterns-tests-caller-", dir => Settings(dir, s =>
            {
                s.Name = "Planned at home";
                s.Twin.Key = "hunter2";
                s.Control.Enabled = false;
                s.Watchdog.BeaconListenPort = FreePort();
                s.Watchdog.BeaconPort = s.Watchdog.BeaconListenPort;
                var stack = CueStacks.Caller(s);
                stack.Scratchpad = "Doors 18:30";
                stack.Cues.Add(new RunCueConfig { Number = "01", Name = "Walk-in" });
                stack.Cues.Add(new RunCueConfig { Number = "02", Name = "Welcome" });
            }), NodeKind.Caller);
            var c = caller.Services;
            var d = desk.Services;
            Assert.Equal("Planned at home", caller.Vm.State.Name);

            // LINK on the desk's card: the caller dials with the key, the desk's show lands, the plan is offered.
            var card = new NodeCard("d1", NodeKind.Desk, "FOH-PC", IPAddress.Loopback, "Gala", "", twinPort, 0, TimeSpan.Zero, false);
            var words = c.Twin.LinkTo(card);
            Assert.StartsWith("Linking to FOH-PC", words);
            PumpUntil(() => c.Twin.Phase == TwinPhase.InStep);
            Assert.Equal("Gala", caller.Vm.State.Name);
            Assert.Equal(new[] { "Walk-in" }, CueStacks.Caller(caller.Vm.State).Cues.Select(x => x.Name));   // the desk's cues are here now
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
            PumpUntil(() => CueStacks.Caller(caller.Vm.State).Cues.Count == 2 && CueStacks.Caller(caller.Vm.State).Scratchpad == "Doors 18:30");

            // The caller calls: a verb pressed there runs on the desk as the caller's own; the desk's runtime reads back.
            var sent = c.Actions.Execute(new ShowAction(ShowActionKind.MessageOn, "", "From the caller"), ActionOrigin.Desk);
            Assert.True(sent.Ok, sent.Message);
            Assert.Contains("sent to", sent.Message);
            PumpUntil(() => d.AirState.Overlays.Message.Text == "From the caller");
            d.CueStack.SetArmed(true, ActionOrigin.Desk);
            d.CueStack.Standby(CueStacks.Caller(desk.Vm.State).Cues[1].Id);
            PumpUntil(() => c.Twin.Live?.Standby == CueStacks.Caller(desk.Vm.State).Cues[1].Id && c.Twin.Live.Armed);
            Assert.Equal(CueStacks.Caller(desk.Vm.State).Cues[1].Id, c.Cues.For(CueStacks.Caller(caller.Vm.State)).StandbyCueId);
            Assert.True(c.Cues.For(CueStacks.Caller(caller.Vm.State)).Armed);

            // A note typed on the caller lands on the desk's cue, and nothing ping-pongs.
            CueStacks.Caller(caller.Vm.State).Cues[0].Notes = "Lights to half";
            PumpUntil(() => CueStacks.Caller(desk.Vm.State).Cues[0].Notes == "Lights to half");
            var sentBefore = d.Twin.StatusJson();
            Thread.Sleep(600);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal(new[] { "Walk-in", "Welcome" }, CueStacks.Caller(desk.Vm.State).Cues.Select(x => x.Name));
            Assert.Equal(new[] { "Walk-in", "Welcome" }, CueStacks.Caller(caller.Vm.State).Cues.Select(x => x.Name));
            Assert.Equal(TwinPhase.InStep, c.Twin.Phase);
            _ = sentBefore;

            // UNLINK: the caller plans on with the show as it stands; the desk counts none.
            Assert.StartsWith("Unlinked", c.Twin.Unlink());
            PumpUntil(() => d.Twin.CallerCount == 0 && c.Twin.Phase == TwinPhase.Off);
            Assert.Equal(new[] { "Walk-in", "Welcome" }, CueStacks.Caller(caller.Vm.State).Cues.Select(x => x.Name));
            Assert.False(c.Twin.IsLinkedToDesk);
        }
        finally
        {
            caller?.Dispose();
            desk.Dispose();
        }
    }
}
