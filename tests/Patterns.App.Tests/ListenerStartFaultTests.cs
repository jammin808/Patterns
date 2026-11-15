using System.Net;
using System.Net.Sockets;
using Avalonia.Headless.XUnit;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// A desk whose control listeners cannot all open says which could not: the start's fault names the listener (the wire, the
/// web remote or the audience listener), its port and the address it asked for, the three are closed together, and every
/// reader of the start — the status, the desk's Remote line, the Super Check's row, the health tile — carries those words.
/// The suite's port helper hands one desk distinct ports, probed on the address the desk binds.
/// </summary>
public class ListenerStartFaultTests
{
    [Fact]
    public void TheHelpersPortsForOneDeskAreDistinctAndEachOpensOnTheAnyAddressWhileTheOthersAreHeld()
    {
        var ports = TestPorts.Distinct(8);
        Assert.Equal(ports.Length, ports.Distinct().Count());
        Assert.All(ports, p => Assert.InRange(p, 1, 65535));
        var held = new List<TcpListener>();
        try
        {
            foreach (var port in ports)
            {
                var listener = new TcpListener(IPAddress.Any, port);
                held.Add(listener);
                listener.Start();                                               // every one opens, the others still held
            }
            Assert.Equal(ports.Length, held.Count(l => l.Server.IsBound));
        }
        finally
        {
            foreach (var listener in held) listener.Dispose();
        }
        var (a, b, c) = TestPorts.Three();
        Assert.Equal(3, new[] { a, b, c }.Distinct().Count());
    }

    [AvaloniaTheory]
    [InlineData(ControlService.WireListener)]
    [InlineData(ControlService.WebListener)]
    [InlineData(ControlService.AudienceListener)]
    public void ADeskWhosePortIsHeldOnTheAnyAddressSaysWhichListenerAndWhichPortAndOpensNone(string listener)
    {
        var (web, wire, audience) = TestPorts.Three();
        var heldPort = listener switch
        {
            ControlService.WireListener => wire,
            ControlService.WebListener => web,
            _ => audience,
        };
        // Held at the any address, as the desk binds it — the probe that picked it let it go, and something else took it.
        var held = new TcpListener(IPAddress.Any, heldPort);
        held.Start();
        var booted = TestApp.Boot("patterns-tests-startfault-", dir =>
        {
            var s = SettingsStore.Fresh();
            s.Control.Enabled = true;
            s.Control.HttpPort = web;
            s.Control.TcpPort = wire;
            s.Control.AudienceEnabled = true;
            s.Control.AudiencePort = audience;
            s.Twin.AcceptCallers = false;
            s.Watchdog.BeaconListenPort = TestPorts.Free();
            s.Watchdog.BeaconPort = s.Watchdog.BeaconListenPort;
            File.WriteAllText(Path.Combine(dir, "patterns.settings.json"), JsonUtil.Serialize(s));
        }, NodeKind.Arcade);
        try
        {
            var (services, vm, _) = booted;
            var control = services.Control;

            // The fields first: failed, nothing listening, the fault naming the listener, its port and the address asked for.
            Assert.True(control.StartFailed);
            Assert.False(control.Listening);
            Assert.False(control.AudienceListening);
            var fault = Assert.IsType<ListenerFault>(control.StartFault);
            Assert.Equal(listener, fault.Listener);
            Assert.Equal(heldPort, fault.Port);
            Assert.Equal(IPAddress.Any.ToString(), fault.Bind);
            Assert.NotEqual("", fault.Message);

            // One sentence, said by every reader of the start.
            var named = $"the {listener} listener could not open port {heldPort} on every address (0.0.0.0)";
            Assert.Contains(named, fault.Words);
            Assert.Equal(fault.Words, control.Status);
            vm.PollNow();
            Assert.Equal($"Remote: closed — {fault.Words}", vm.RemoteStatus);
            // The log's first line for it is the error, in the same words, once (the retries say a line a minute after it).
            var logged = File.ReadAllLines(Path.Combine(booted.Dir, "patterns.log")).Where(l => l.Contains("[ERROR] " + fault.Words, StringComparison.Ordinal));
            Assert.Single(logged);
            var facts = services.Metrics.GatherFacts();
            Assert.Equal(fault.Words, facts.RemoteStartProblem);
            var rows = SuperCheck.Run(facts).Rows.Where(r => r.Section == "REMOTE").ToList();
            var row = Assert.Single(rows, r => r.Item == "Remote control");
            Assert.Equal(CheckLight.Red, row.Light);
            Assert.Equal("closed — a listener could not open its port", row.Value);
            Assert.StartsWith($"FIX: {fault.Words}", row.Note);
            Assert.DoesNotContain(rows, r => r.Item == "Wire TLS");                  // no wire port to describe while it is closed
            Assert.DoesNotContain(rows, r => r.Item == "Wire reads");                // nor the reads of a wire that is not listening
            var tile = Assert.Single(HealthDashboard.Tiles(facts), t => t.Id == "remote");
            Assert.Equal(CheckLight.Red, tile.Light);
            Assert.Equal(fault.Words, tile.Detail);

            // The port let go, the desk's next ask opens all three and the fault is gone from every reader.
            held.Dispose();
            control.Reconcile();
            Assert.True(control.Listening, control.Status);
            Assert.True(control.AudienceListening, control.Status);
            Assert.Null(control.StartFault);
            Assert.DoesNotContain("failed to start", control.Status, StringComparison.Ordinal);
            var after = services.Metrics.GatherFacts();
            Assert.Equal("", after.RemoteStartProblem);
            Assert.NotEqual(CheckLight.Red, Assert.Single(SuperCheck.Run(after).Rows, r => r.Section == "REMOTE" && r.Item == "Remote control").Light);
        }
        finally
        {
            held.Dispose();
            booted.Dispose();
        }
    }
}
