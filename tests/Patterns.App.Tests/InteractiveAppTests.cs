using Avalonia.Controls;
using Avalonia.Headless;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Avalonia.VisualTree;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>A wire that records what Patterns writes and lets the test speak as the device.</summary>
public sealed class FakeDeviceLink : IDeviceLink
{
    public List<string> Written { get; } = new();
    public bool Disposed { get; private set; }
    public string Status => Disposed ? "closed" : "open (fake)";
    public bool IsOpen => !Disposed;
    public event Action<string>? LineReceived;
    public void Write(string framedLine) => Written.Add(framedLine);
    public void Say(string line) => LineReceived?.Invoke(line);
    public void Dispose() => Disposed = true;
}

/// <summary>
/// The Interactive area on a live desk: a device's line firing a show command through the
/// action layer (a trigger, or the protocol as it is), the answer written back, the show's
/// facts written out as they change, a line sent to the device from the wire, a cue and the
/// page, STATE's rows, the page itself, and the link closed when the area is switched off.
/// </summary>
public class InteractiveAppTests
{
    private static void Settle(Window window)
    {
        for (var i = 0; i < 6; i++)
        {
            Dispatcher.UIThread.RunJobs();
            AvaloniaHeadlessPlatform.ForceRenderTimerTick();
        }
    }

    /// <summary>Runs the dispatcher (its timers included) until the fake has a line that satisfies <paramref name="test"/>, or two seconds pass.</summary>
    private static bool WaitFor(FakeDeviceLink fake, Func<string, bool> test)
    {
        var deadline = DateTime.UtcNow.AddSeconds(2);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            if (fake.Written.Any(test)) return true;
            Thread.Sleep(20);
        }
        return fake.Written.Any(test);
    }

    private static string Send(CommandRouter router, string line) => TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse(line)));

    [AvaloniaFact]
    public void ADeviceFiresCommandsHearsTheShowAndTakesALineFromTheWireACueAndThePage()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, window) = b;
            var router = new CommandRouter(services);
            vm.IsSandboxActive = false;
            var fake = new FakeDeviceLink();
            services.Devices.LinkFactory = _ => fake;

            // Off by default: nothing opens, a line has nowhere to go, the cue's checks say so softly.
            var device = new DeviceConfig { Name = "Arduino", Link = DeviceLink.Serial, Port = "COM3" };
            device.Triggers.Add(new DeviceTriggerConfig { Match = "BTN1", Command = "BLACKOUT ON" });
            device.Triggers.Add(new DeviceTriggerConfig { Match = "BTN2", Command = "BLACKOUT OFF" });
            vm.State.Interactive.Devices.Add(device);
            services.Devices.Reconcile();
            Assert.Equal(0, services.Devices.OpenCount);
            Assert.StartsWith("ERR", Send(router, "DEVICE Arduino PING"));
            Assert.Contains("Interactive area off", device.Status);

            // On: the link opens through the factory and the device hears everything once.
            vm.State.Interactive.Enabled = true;
            services.Devices.Reconcile();
            Assert.Equal(1, services.Devices.OpenCount);
            Assert.Same(fake, services.Devices.LinkFor(device.Id));
            Assert.Contains("open", device.Status);
            vm.State.Blackout = false;
            services.RepublishNow();
            Assert.True(WaitFor(fake, l => l == "BLACKOUT 0\n"), string.Join("|", fake.Written));
            Assert.Contains(fake.Written, l => l.StartsWith("LIVE ", StringComparison.Ordinal));

            // The device presses BTN1: the trigger fires BLACKOUT ON through the action layer, OK goes back, and the fact follows.
            fake.Written.Clear();
            fake.Say("btn1");
            Assert.True(WaitFor(fake, l => l == "OK\n"), string.Join("|", fake.Written));
            Assert.True(vm.State.Blackout);
            Assert.True(WaitFor(fake, l => l == "BLACKOUT 1\n"), string.Join("|", fake.Written));
            Assert.DoesNotContain(fake.Written, l => l.StartsWith("LIVE ", StringComparison.Ordinal));   // only what changed
            Assert.Contains("in 1 (btn1)", device.Status);

            // A line with no trigger that is a protocol command runs as it is; one that is neither gets an honest ERR.
            fake.Written.Clear();
            fake.Say("BLACKOUT OFF");
            Assert.True(WaitFor(fake, l => l == "OK\n"));
            Assert.False(vm.State.Blackout);
            fake.Written.Clear();
            fake.Say("WHATEVER");
            Assert.True(WaitFor(fake, l => l.StartsWith("ERR no trigger", StringComparison.Ordinal)));
            fake.Written.Clear();
            fake.Say("CUE GO");                                                                 // the stack is not armed: refused, and said so
            Assert.True(WaitFor(fake, l => l.StartsWith("ERR", StringComparison.Ordinal)));

            // A line to the device: the wire, a cue action, the page's SEND, and Companion's verb all write the same framed line.
            fake.Written.Clear();
            Assert.Equal("OK", Send(router, "DEVICE Arduino RELAY 1"));
            Assert.Contains("RELAY 1\n", fake.Written);
            Assert.Equal("OK", Send(router, "SEND * RELAY 0"));
            Assert.Contains("RELAY 0\n", fake.Written);
            Assert.StartsWith("ERR", Send(router, "DEVICE Nobody RELAY 1"));
            var cue = new CueActionConfig { Kind = CueActionKind.DeviceSend, Target = "arduino", Value = "SHOW 3" };
            Assert.True(services.Actions.Execute(ShowActions.ToShowAction(cue), new ActionOrigin(OriginKind.Cue, "01.010")).Ok);
            Assert.Contains("SHOW 3\n", fake.Written);
            device.TestText = "PING";
            vm.TestDeviceCommand.Execute(device);
            Assert.Contains("PING\n", fake.Written);
            Assert.Contains("out", device.Status);

            // STATE carries the devices.
            var state = System.Text.Json.JsonDocument.Parse(router.StateJson()).RootElement;
            Assert.True(state.GetProperty("interactive").GetBoolean());
            var row = state.GetProperty("devices")[0];
            Assert.Equal("Arduino", row.GetProperty("name").GetString());
            Assert.Equal("serial", row.GetProperty("link").GetString());
            Assert.True(row.GetProperty("open").GetBoolean());
            Assert.Equal("PING", row.GetProperty("lastOut").GetString());

            // The page: the block, a device card, its triggers.
            vm.SelectPage(Shell.IndexOf("Interactive"));
            Settle(window);
            Assert.Contains(window.GetVisualDescendants().OfType<TextBlock>(), t => t.Text == "DEVICES");
            Assert.Contains(window.GetVisualDescendants().OfType<Button>(), x => x.Content as string == "+ ARDUINO (SERIAL)");
            Assert.Contains(window.GetVisualDescendants().OfType<TextBox>(), x => x.Text == "BTN1");
            vm.AddIpDeviceCommand.Execute(null);
            Assert.Equal(2, vm.State.Interactive.Devices.Count);
            Assert.Equal(DeviceLink.Tcp, vm.State.Interactive.Devices[1].Link);
            vm.RemoveDeviceCommand.Execute(vm.State.Interactive.Devices[1]);
            Assert.Single(vm.State.Interactive.Devices);

            // Off again: the link closes and a line is refused.
            vm.State.Interactive.Enabled = false;
            services.Devices.Reconcile();
            Assert.Equal(0, services.Devices.OpenCount);
            Assert.True(fake.Disposed);
            Assert.StartsWith("ERR", Send(router, "DEVICE Arduino PING"));
        }
        finally
        {
            b.Dispose();
        }
    }
}
