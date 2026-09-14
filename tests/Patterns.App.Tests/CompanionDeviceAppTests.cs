using System.Net;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The desk drives Companion: a Companion device on the Interactive page turns its words into
/// Companion's own lines, +OK lands as an accepted receipt and -ERR as a refusal the health line
/// names, the wire's DEVICE verb reaches it, words that are not Companion's are refused before
/// they leave, and the page's chip adds it with the address of the Companion heard on the network.
/// </summary>
public class CompanionDeviceAppTests
{
    private static string Send(CommandRouter router, string line) => TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse(line)));

    private static bool WaitFor(Func<bool> test)
    {
        var deadline = DateTime.UtcNow.AddSeconds(3);
        while (DateTime.UtcNow < deadline)
        {
            Dispatcher.UIThread.RunJobs();
            if (test()) return true;
            Thread.Sleep(20);
        }
        return test();
    }

    [AvaloniaFact]
    public void ACompanionDeviceTurnsTheDeckFromACueTheWireAndThePage()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var fake = new FakeDeviceLink();
            services.Devices.LinkFactory = _ => fake;
            var device = DeviceProfiles.Preset(DeviceProfile.Companion, 1);
            device.Port = "10.0.0.5";
            device.Surface = "streamdeck:abc";
            device.ConfirmTimeoutMs = 400;
            vm.State.Interactive.Devices.Add(device);
            vm.State.Interactive.Enabled = true;
            services.Devices.Reconcile();
            Assert.Equal(ConfirmLevel.Accepted, DeviceConfirmation.Effective(device));

            // PAGE 3 is the surface's page-set; Companion's +OK is the accepted receipt.
            Assert.True(services.Devices.Send("Companion", "PAGE 3").Ok);
            Assert.Contains("SURFACE streamdeck:abc PAGE-SET 3\n", fake.Written);
            fake.Say("+OK");
            Assert.True(WaitFor(() => device.Status.Contains("OK")), device.Status);
            Assert.Null(services.Devices.LastFailed);

            // A refusal is a no the health line names, until the next yes.
            Assert.True(services.Devices.Send("Companion", "PRESS 9/9/9").Ok);
            Assert.Contains("LOCATION 9/9/9 PRESS\n", fake.Written);
            fake.Say("-ERR Location not found");
            Assert.True(WaitFor(() => services.Devices.LastFailed is not null), "the no lands");
            Assert.Contains("Companion refused: Location not found", services.Devices.LastFailed!.Answer);
            Assert.StartsWith("DEVICE: Companion: PRESS 9/9/9 — rejected", services.Devices.HealthWords);

            // The wire's DEVICE verb reaches it, and words that are not Companion's never leave.
            var router = new CommandRouter(services);
            Assert.StartsWith("OK", Send(router, "DEVICE Companion VAR speaker Jane Doe"));
            Assert.Contains("CUSTOM-VARIABLE speaker SET-VALUE Jane Doe\n", fake.Written);
            fake.Say("+OK");
            Assert.True(WaitFor(() => services.Devices.LastFailed is null), "the yes clears it");
            var refused = Send(router, "DEVICE Companion DANCE");
            Assert.StartsWith("ERR", refused);
            Assert.Contains("not a Companion command", refused);
            Assert.DoesNotContain(fake.Written, w => w.Contains("DANCE"));

            // STATE's device row carries the profile.
            var state = System.Text.Json.JsonDocument.Parse(router.StateJson()).RootElement;
            var row = state.GetProperty("devices").EnumerateArray().Single(d => d.GetProperty("name").GetString() == "Companion");
            Assert.Equal("companion", row.GetProperty("profile").GetString());

            // The chip: a Companion heard announcing itself fills in its address.
            var instance = new[] { "Companion (FOH-PC)", "_companion-satellite-tcp", "_tcp", "local" };
            var heard = DnsPacket.Response(new[]
            {
                DnsRecord.Ptr(DnsSd.Labels(CompanionModule.SatelliteServiceType + ".local"), instance, 4500),
                DnsRecord.Srv(instance, new[] { "foh-pc", "local" }, 16622, 120),
                DnsRecord.Txt(instance, new[] { "version=5.0.3" }, 4500),
                DnsRecord.A(new[] { "foh-pc", "local" }, IPAddress.Parse("10.0.0.7"), 120),
            });
            services.Kernel.Mdns.Handle(heard.ToBytes(), new IPEndPoint(IPAddress.Parse("10.0.0.7"), 5353));
            vm.AddEndpointCommand.Execute("Companion");
            var added = vm.State.Interactive.Devices.Last();
            Assert.Equal(DeviceProfile.Companion, added.Profile);
            Assert.Equal("Companion 2", added.Name);
            Assert.Equal("10.0.0.7", added.Port);
            Assert.Equal(16759, added.NetPort);
            Assert.Contains("Companion (FOH-PC)'s address (10.0.0.7)", vm.StatusMessage);
            Assert.Contains("Companion on the network: Companion (FOH-PC) at 10.0.0.7:16622 · v5.0.3", CompanionWords.HeardLine(services.Kernel.Mdns.Companions));
        }
        finally
        {
            b.Dispose();
        }
    }
}
