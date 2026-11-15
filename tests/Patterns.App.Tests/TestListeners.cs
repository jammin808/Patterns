using System.Globalization;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Waits on a desk's control listeners by the desk's own facts. The three — the wire, the web remote and the audience
/// listener — open in one start, and a start that throws closes all three and sets <see cref="ControlService.StartFailed"/>
/// with <see cref="ControlService.StartFault"/> naming the listener, its port and the address it asked for; the desk's retry
/// every 5 s asks for the same ports, so a port held for the whole wait would hold the wait to its end. These helpers read the
/// fact instead: up, or the start failed and the test fails at once with the status and the ports it asked for.
/// </summary>
internal static class TestListeners
{
    /// <summary>
    /// Pumps the dispatcher until <paramref name="up"/> holds (the audience listener open, the wire open) or the desk's last
    /// start failed. A failed start fails the test at once with the status — which names the listener and its port — and the
    /// three ports the desk was given; it does not move to other ports as <see cref="WireOpen"/> does: these tests built their
    /// clients on the ports they picked, and a start that fails after one <see cref="TestPorts.Distinct"/> pick is the sighting
    /// worth reading, not a wait to absorb. Neither up nor failed by the deadline is a timeout that says the status too.
    /// </summary>
    public static void Up(ControlService control, ControlConfig asked, Func<bool> up, string what, int timeoutMs = 15000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!up())
        {
            if (control.StartFailed) Assert.Fail($"{what} never opened: the desk's start failed — {control.Status} ({Asked(asked)})");
            Dispatcher.UIThread.RunJobs();
            Thread.Sleep(5);
            if (Environment.TickCount64 > deadline) throw new TimeoutException($"{what} neither opened nor failed to start in {timeoutMs} ms: {control.Status} ({Asked(asked)})");
        }
    }

    /// <summary>The ports a desk was asked to open, for a failure's words.</summary>
    public static string Asked(ControlConfig c)
        => string.Create(CultureInfo.InvariantCulture,
            $"asked for: wire {c.TcpPort}, web remote {c.HttpPort}{(c.AudienceEnabled ? $", audience {c.AudiencePort}" : "")} at '{c.Bind}'");

    /// <summary>
    /// The hub's control listeners open — the wire and the web remote, which <see cref="ControlService.Listening"/> needs both of
    /// — and the wire's port. A port <see cref="TestPorts"/> found free is free only until its probe lets it go, and anything on
    /// the machine may take it before the hub binds it; the hub's start then fails, and its retry every 5 s fails for as long as
    /// the port is held, so no wait is long enough. The hub's own fact says which, and both are set before the boot returns (the
    /// window's opening starts the listeners): it listens, or its start failed. A start that failed moves every listener to
    /// other ports — three distinct picks — a bounded count of tries and never a clock; the status, which names the listener
    /// that failed and its port, is in the failure's words.
    /// </summary>
    public static int WireOpen(TestApp.Booted hub, int port)
    {
        var control = hub.Services.Control;
        for (var tries = 0; !control.Listening; tries++)
        {
            Assert.True(control.StartFailed, $"the hub's control listeners neither listen nor failed to start: {control.Status}");
            Assert.True(tries < 8, $"the hub's control listeners could not bind in {tries} tries: {control.Status}");
            var cfg = hub.Vm.State.Control;
            var (wire, web, audience) = TestPorts.Three();
            port = wire;
            cfg.TcpPort = wire;
            cfg.HttpPort = web;
            if (cfg.AudienceEnabled) cfg.AudiencePort = audience;
            Dispatcher.UIThread.RunJobs();
            control.Reconcile();
        }
        return port;
    }
}
