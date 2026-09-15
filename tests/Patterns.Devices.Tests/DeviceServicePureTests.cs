using Patterns.Core.Model;
using Patterns.Core.Services;
using Patterns.Devices;
using Xunit;

namespace Patterns.Devices.Tests;

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

/// <summary>The desk as the device transports see it — a show, its moves, a router — with nothing else built.</summary>
public sealed class FakeDeviceHost : IDeviceHost
{
    public ShowState State { get; } = new();
    public FakeRouter Wire { get; } = new();
    public IRouter Router => Wire;
    public string ExecutionInHand { get; set; } = "";
    public event Action? SnapshotPublished;
    public event Action? RuntimeChanged;
    public void Publish() => SnapshotPublished?.Invoke();
    public void MoveRuntime() => RuntimeChanged?.Invoke();
}

/// <summary>The wire's router as a device sees it: every line a device's trigger becomes, and the facts STATE would carry.</summary>
public sealed class FakeRouter : IRouter
{
    public List<string> Executed { get; } = new();
    public string State { get; set; } = "{\"live\":true}";
    public Func<long>? Rev { get; set; }
    public Task<string> ExecuteAsync(RemoteCommand cmd, ActionOrigin? origin = null)
    {
        Executed.Add(cmd.Action.Kind.ToString());
        return Task.FromResult("OK");
    }
    public string StateJson() => State;
    public Task<string> StateJsonAsync() => Task.FromResult(State);
    public Task<string> CueListJsonAsync() => Task.FromResult("[]");
}

/// <summary>
/// The device transports run on the core's contracts alone — a fake host, a fake wire, the inline
/// dispatch — with no desk, no UI thread and no headless platform: the proof that the edge is an
/// assembly and not a folder, and the fastest place to test a box's protocol.
/// </summary>
public class DeviceServicePureTests
{
    private static (DeviceService Devices, FakeDeviceHost Host, FakeDeviceLink Link, DeviceConfig Device) Companion()
    {
        var host = new FakeDeviceHost();
        var link = new FakeDeviceLink();
        var device = DeviceProfiles.Preset(DeviceProfile.Companion, 1);
        device.Port = "10.0.0.5";
        device.Surface = "streamdeck:abc";
        device.ConfirmTimeoutMs = 400;
        host.State.Interactive.Devices.Add(device);
        host.State.Interactive.Enabled = true;
        var devices = new DeviceService(host) { LinkFactory = _ => link };
        devices.Reconcile();
        return (devices, host, link, device);
    }

    [Fact]
    public void ACompanionIsDrivenAndAnsweredWithNoDeskAtAll()
    {
        Assert.Same(InlineDispatch.Instance, Dispatch.Provider);                            // nothing in this process installed a UI
        var (devices, host, link, device) = Companion();
        try
        {
            Assert.True(devices.Send("Companion", "PAGE 3").Ok);
            Assert.Contains("SURFACE streamdeck:abc PAGE-SET 3\n", link.Written);
            link.Say("+OK");                                                                  // the drain is posted to the desk's thread: inline, so it ran
            Assert.Contains("OK", device.Status);
            Assert.Null(devices.LastFailed);

            Assert.True(devices.Send("Companion", "PRESS 9/9/9").Ok);
            link.Say("-ERR Location not found");
            Assert.NotNull(devices.LastFailed);
            Assert.Contains("Companion refused: Location not found", devices.LastFailed!.Answer);
            Assert.StartsWith("DEVICE: Companion: PRESS 9/9/9 — rejected", devices.HealthWords);

            var refused = devices.Send("Companion", "DANCE");
            Assert.False(refused.Ok);
            Assert.DoesNotContain(link.Written, w => w.Contains("DANCE"));
        }
        finally
        {
            devices.Dispose();
        }
        Assert.True(link.Disposed);
    }

    [Fact]
    public void TheAreaSwitchedOffClosesTheLinkOnTheNextReconcile()
    {
        var (devices, host, link, _) = Companion();
        Assert.True(link.IsOpen);
        host.State.Interactive.Enabled = false;
        devices.Reconcile();
        Assert.True(link.Disposed);
        devices.Dispose();
    }
}
