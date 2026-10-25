using System.Net;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 89: the show's secrets on the twin's link, pure — the one rule (a standby, SEND SECRETS on, a link on this
/// machine), what makes a link this machine's, the SECRETS and LACKS words, what a standby is without, the words both
/// desks say, and the Super Check's and the dashboard's twin light.
/// </summary>
public class TwinSecretsTests
{
    [Fact]
    public void TheSecretsStayHomeByDefaultAndRideOnlyToAStandbyOnThisMachineWithTheSettingOn()
    {
        Assert.False(new TwinConfig().SendSecrets);                                              // off by default
        Assert.False(new ShowState().Twin.SendSecrets);

        Assert.Equal(TwinSecrets.Ride, TwinSecrets.Why(sendSecrets: true, sameMachine: true));
        Assert.Equal(TwinSecrets.Off, TwinSecrets.Why(sendSecrets: false, sameMachine: true));
        Assert.Equal(TwinSecrets.Plain, TwinSecrets.Why(sendSecrets: true, sameMachine: false));     // refused whatever the setting
        Assert.Equal(TwinSecrets.Plain, TwinSecrets.Why(sendSecrets: false, sameMachine: false));

        Assert.True(TwinSecrets.Rides(sendSecrets: true, sameMachine: true, follower: false));
        Assert.False(TwinSecrets.Rides(sendSecrets: true, sameMachine: true, follower: true));      // a caller or a stage timer never takes the show
        Assert.False(TwinSecrets.Rides(sendSecrets: false, sameMachine: true, follower: false));
        Assert.False(TwinSecrets.Rides(sendSecrets: true, sameMachine: false, follower: false));
    }

    [Fact]
    public void ALinkIsThisMachinesOnlyWhenBothEndsAreThisMachinesAddress()
    {
        var lan = IPAddress.Parse("10.0.0.5");
        Assert.True(TwinSecrets.SameMachine(IPAddress.Loopback, lan));
        Assert.True(TwinSecrets.SameMachine(IPAddress.Parse("127.0.0.2"), null));
        Assert.True(TwinSecrets.SameMachine(IPAddress.IPv6Loopback, null));
        Assert.True(TwinSecrets.SameMachine(IPAddress.Parse("::ffff:127.0.0.1"), null));            // a dual-stack socket's loopback
        Assert.True(TwinSecrets.SameMachine(lan, lan));                                            // a standby here that dialled the bound address
        Assert.True(TwinSecrets.SameMachine(IPAddress.Parse("::ffff:10.0.0.5"), lan));
        Assert.False(TwinSecrets.SameMachine(IPAddress.Parse("10.0.0.6"), lan));                   // another machine on the same network
        Assert.False(TwinSecrets.SameMachine(lan, null));
        Assert.False(TwinSecrets.SameMachine(null, lan));
        Assert.False(TwinSecrets.SameMachine(IPAddress.Any, IPAddress.Any));
        Assert.True(TwinSecrets.SameMachine(new IPEndPoint(IPAddress.Loopback, 50000), new IPEndPoint(IPAddress.Loopback, 9699)));
        Assert.False(TwinSecrets.SameMachine(new IPEndPoint(IPAddress.Parse("10.0.0.6"), 50000), new IPEndPoint(lan, 9699)));
        Assert.False(TwinSecrets.SameMachine(null, new IPEndPoint(lan, 9699)));
    }

    [Fact]
    public void TheWordsAreTheTwinsOwnAndCarryNoSecret()
    {
        Assert.Equal(TwinWord.Secrets, TwinMessage.Parse("SECRETS {\"Why\":\"off\"}").Word);
        Assert.Equal(TwinWord.Lacks, TwinMessage.Parse("lacks {}").Word);

        var show = new ShowState();
        show.Weather.ApiKey = "wx-key";
        show.Interactive.Devices.Add(new DeviceConfig { Id = "proj-b", Name = "Proj B", Secret = "pjpass" });
        show.Interactive.Devices.Add(new DeviceConfig { Id = "proj-a", Name = "Proj A", Secret = "other" });
        show.Interactive.Devices.Add(new DeviceConfig { Id = "switcher", Name = "Switcher" });

        var withheld = TwinSecretsOffer.For(show, TwinSecrets.Plain);
        Assert.Equal(new[] { "proj-a", "proj-b" }, withheld.Devices);                            // the ids of the boxes with a password, never the password
        Assert.True(withheld.Weather);
        Assert.False(withheld.Rides);
        var json = withheld.ToJson();
        Assert.DoesNotContain("pjpass", json, StringComparison.Ordinal);
        Assert.DoesNotContain("other", json, StringComparison.Ordinal);
        Assert.DoesNotContain("wx-key", json, StringComparison.Ordinal);
        var back = TwinSecretsOffer.Parse(json)!;
        Assert.Equal(TwinSecrets.Plain, back.Why);
        Assert.Equal(withheld.Devices, back.Devices);

        var rides = TwinSecretsOffer.For(show, TwinSecrets.Ride);
        Assert.True(rides.Rides);
        Assert.Empty(rides.Devices);                                                              // nothing withheld: nothing named
        Assert.False(rides.Weather);

        Assert.Null(TwinSecretsOffer.Parse("{\"Why\":\"maybe\",\"Devices\":[],\"Weather\":false}"));   // an unknown reason is refused, never read as riding
        Assert.Null(TwinSecretsOffer.Parse("not json"));
        Assert.Null(TwinSecretsOffer.Parse(null));

        Assert.Equal(new TwinSecretsLack(2, true, 1), TwinSecretsLack.Parse(new TwinSecretsLack(2, true, 1).ToJson()));
        Assert.Equal(new TwinSecretsLack(0, false, 0), TwinSecretsLack.Parse("{\"Devices\":-3,\"Weather\":false,\"Kept\":-1}"));
        Assert.Null(TwinSecretsLack.Parse("[]"));
    }

    [Fact]
    public void AStandbyIsWithoutWhatTheMainWithholdsAndItHasNotTypedItself()
    {
        var offer = new TwinSecretsOffer(TwinSecrets.Plain, new[] { "proj-a", "proj-b", "gone" }, Weather: true);
        var mine = new ShowState();
        mine.Interactive.Devices.Add(new DeviceConfig { Id = "proj-a", Name = "Proj A" });
        mine.Interactive.Devices.Add(new DeviceConfig { Id = "proj-b", Name = "Proj B", Secret = "typed-here" });
        mine.Interactive.Devices.Add(new DeviceConfig { Id = "switcher", Name = "Switcher" });

        var lack = TwinSecretsLack.Of(offer, mine, keptNotHere: 2);
        Assert.Equal(new TwinSecretsLack(1, true, 2), lack);                                      // proj-b typed here; "gone" is not in this show
        Assert.True(lack.Any);
        Assert.Equal("1 device password, the weather key and 2 kept pages", lack.Words);
        Assert.Equal("WITHOUT 1 PASSWORD, WEATHER KEY, 2 KEPT PAGES", lack.Glance);

        mine.Weather.ApiKey = "typed-too";
        Assert.Equal(new TwinSecretsLack(1, false, 0), TwinSecretsLack.Of(offer, mine, keptNotHere: 0));
        Assert.Equal("1 device password", new TwinSecretsLack(1, false, 0).Words);
        Assert.Equal("2 device passwords and 1 kept page", new TwinSecretsLack(2, false, 1).Words);

        // Before the main said, or while they ride, nothing is missing on the twin's account.
        Assert.Equal(TwinSecretsLack.None, TwinSecretsLack.Of(null, mine, keptNotHere: 3));
        Assert.Equal(TwinSecretsLack.None, TwinSecretsLack.Of(new TwinSecretsOffer(TwinSecrets.Ride, Array.Empty<string>(), false), mine, keptNotHere: 3));
        Assert.False(TwinSecretsLack.None.Any);
        Assert.Equal("", TwinSecretsLack.None.Words);
        Assert.Equal("", TwinSecretsLack.None.Glance);
    }

    [Fact]
    public void BothDesksSayWhatTheStandbyIsWithoutWhyAndWhatToDo()
    {
        var lack = new TwinSecretsLack(2, false, 1);
        Assert.Equal("WITHOUT 2 device passwords and 1 kept page — MAIN-DESK keeps them: the twin's link to another machine is plain; type the passwords here (Interactive page) — this desk keeps what is typed on it; a kept page opens black here until pasted again once this desk has the show",
            TwinSecrets.OnStandby(lack, TwinSecrets.Plain, "MAIN-DESK"));
        Assert.StartsWith("WITHOUT the weather key — SEND SECRETS is off on the main (its Machine page, TWIN); type the weather key here (Weather)",
            TwinSecrets.OnStandby(new TwinSecretsLack(0, true, 0), TwinSecrets.Off, ""));
        Assert.Equal("", TwinSecrets.OnStandby(TwinSecretsLack.None, TwinSecrets.Plain, "MAIN-DESK"));

        Assert.Equal("Backup is WITHOUT 2 device passwords and 1 kept page — they stay on this desk: the twin's link to another machine is plain; type the passwords on Backup; a kept page opens black there until pasted again after a takeover.",
            TwinSecrets.OnMain("Backup", lack, TwinSecrets.Plain));
        Assert.Equal("Backup is WITHOUT 1 device password and the weather key — SEND SECRETS is off: tick it (Machine page, TWIN) to send them to this machine's standby, or type the password and the weather key there.",
            TwinSecrets.OnMain("Backup", new TwinSecretsLack(1, true, 0), TwinSecrets.Off));
        Assert.Equal("", TwinSecrets.OnMain("Backup", TwinSecretsLack.None, TwinSecrets.Plain));
        Assert.Contains("SEND SECRETS is refused for Hall B — it is on another machine and the twin's link is plain", TwinSecrets.Refused("Hall B"), StringComparison.Ordinal);

        // The lines carry the words: the main's after its standbys, the standby's after its own state, through a takeover.
        var now = new DateTime(2026, 10, 25, 19, 0, 0, DateTimeKind.Utc);
        var onMain = TwinSecrets.OnMain("Backup", lack, TwinSecrets.Plain);
        Assert.Equal("MAIN — standby Backup in step (heard just now) · 3 sections sent. " + onMain,
            TwinWatch.DescribeMain(9699, new[] { ("Backup", now) }, 3, now, without: new[] { onMain }));
        Assert.Equal("MAIN — listening for a standby on port 9699 at 10.0.0.5 only; none connected.",
            TwinWatch.DescribeMain(9699, Array.Empty<(string, DateTime)>(), 0, now, at: " at 10.0.0.5 only"));
        var onStandby = TwinSecrets.OnStandby(lack, TwinSecrets.Plain, "MAIN-DESK");
        Assert.EndsWith(" · TAKE OVER is yours. · " + onStandby, TwinWatch.DescribeStandby(TwinPhase.InStep, "MAIN-DESK", now, 4, false, now, without: onStandby));
        Assert.EndsWith(" · " + onStandby, TwinWatch.DescribeStandby(TwinPhase.MainSilent, "MAIN-DESK", now.AddSeconds(-7), 4, false, now, without: onStandby));
        Assert.EndsWith("STAND BY AGAIN once MAIN-DESK is back. · " + onStandby, TwinWatch.DescribeStandby(TwinPhase.TookOver, "MAIN-DESK", null, 4, false, now, without: onStandby));
        Assert.DoesNotContain("WITHOUT", TwinWatch.DescribeStandby(TwinPhase.Refused, "MAIN-DESK", null, 0, false, now, "wrong key", without: onStandby), StringComparison.Ordinal);
    }

    [Fact]
    public void TheTwinRowIsAmberForAStandbyWithoutTheSecretsAndRedForAMainWhosePortIsClosed()
    {
        static CheckRow Twin(CheckFacts f) => SuperCheck.Run(f).Rows.Single(r => r.Item == "Twin");

        var inStep = new CheckFacts { TwinRole = TwinRole.Main, TwinPhase = TwinPhase.Listening, TwinWords = "MAIN — standby Backup in step" };
        Assert.Equal(CheckLight.Green, Twin(inStep).Light);
        var without = new CheckFacts { TwinRole = TwinRole.Main, TwinPhase = TwinPhase.Listening, TwinWithout = "Backup: 2 device passwords", TwinWords = "MAIN — … Backup is WITHOUT 2 device passwords — …" };
        Assert.Equal(CheckLight.Amber, Twin(without).Light);
        Assert.Equal("main · without secrets", Twin(without).Value);
        Assert.Contains("WITHOUT 2 device passwords", Twin(without).Note, StringComparison.Ordinal);

        var standby = new CheckFacts { TwinRole = TwinRole.Standby, TwinPhase = TwinPhase.InStep, TwinWithout = "1 kept page", TwinWords = "STANDBY …" };
        Assert.Equal(CheckLight.Amber, Twin(standby).Light);
        var silent = new CheckFacts { TwinRole = TwinRole.Standby, TwinPhase = TwinPhase.MainSilent, TwinWithout = "1 kept page", TwinWords = "MAIN … SILENT" };
        Assert.Equal(CheckLight.Red, Twin(silent).Light);                                          // a silent main outranks it

        var closed = new CheckFacts { TwinRole = TwinRole.Main, TwinPhase = TwinPhase.Off, TwinPortClosed = true, TwinWords = "Twin: the main's port is closed — '10.0.0' is not an address" };
        Assert.Equal(CheckLight.Red, Twin(closed).Light);
        Assert.Equal("port closed", Twin(closed).Value);

        // A main on its way — its key being made, the role not yet taken — is off for a moment, never closed: the red is
        // the twin service's fact, not a phase.
        var onItsWay = new CheckFacts { TwinRole = TwinRole.Main, TwinPhase = TwinPhase.Off, TwinWords = "Twin off." };
        Assert.Equal(CheckLight.Grey, Twin(onItsWay).Light);
        Assert.NotEqual("port closed", Twin(onItsWay).Value);

        // The dashboard's tile says the same, ahead of the watchdog's own words.
        var tile = HealthDashboard.Tiles(without).Single(t => t.Id == "watchdog");
        Assert.Equal(CheckLight.Amber, tile.Light);
        Assert.Equal("main · without secrets", tile.Value);
        Assert.Equal(CheckLight.Red, HealthDashboard.Tiles(closed).Single(t => t.Id == "watchdog").Light);
    }

    private static readonly string[] MachinesOwn = { "twin-canary", "token-canary", "swk-canary", "passcode-canary", "mgmt-canary" };
    private static readonly string[] ShowsOwn = { "wx-canary", "pj-canary" };

    [Fact]
    public void TheAirRecordTravelsByTheShowLinesRuleAndLandsKeepingThisDesksOwn()
    {
        // The air record holds a whole show while EDIT SAFE is open or a look is pinned — every secret the show has.
        var air = new ShowState { Name = "On air" };
        air.Twin.Key = "twin-canary";
        air.Control.Token = "token-canary";
        air.Control.Stagewatch.ApiKey = "swk-canary";
        air.Install.AdminPasscode = "passcode-canary";
        air.Install.ManagementToken = "mgmt-canary";
        air.Weather.ApiKey = "wx-canary";
        air.Interactive.Devices.Add(new DeviceConfig { Id = "proj", Name = "Proj", Secret = "pj-canary" });
        var record = new RecoverySnapshot(true, false, DateTime.UtcNow, Sandboxed: true, Air: air, AirLabel: "Walk-in");

        // Withheld: none of them. Riding (a standby on this machine, SEND SECRETS on): the boxes' passwords and the weather
        // key, never the machine's own — the twin's key, the pairing token, the Stagewatch key, the passcode, the management token.
        var plain = TwinSync.WireAirJson(record, sendSecrets: false);
        foreach (var canary in MachinesOwn.Concat(ShowsOwn)) Assert.DoesNotContain(canary, plain, StringComparison.Ordinal);
        var rides = TwinSync.WireAirJson(record, sendSecrets: true);
        foreach (var canary in MachinesOwn) Assert.DoesNotContain(canary, rides, StringComparison.Ordinal);
        foreach (var canary in ShowsOwn) Assert.Contains(canary, rides, StringComparison.Ordinal);
        Assert.DoesNotContain("\"Twin\":", plain, StringComparison.Ordinal);
        Assert.Equal("null", TwinSync.WireAirJson(null, sendSecrets: false));
        var bare = JsonUtil.Deserialize<RecoverySnapshot>(TwinSync.WireAirJson(record with { Air = null }, sendSecrets: false))!;
        Assert.Null(bare.Air);
        Assert.Equal("Walk-in", bare.AirLabel);

        // Landed for a takeover: the air's mirrored sections as they came, this desk's own sections and the secrets typed
        // here where the wire was blank; the record that came is never changed.
        var came = JsonUtil.Deserialize<RecoverySnapshot>(plain)!;
        Assert.True(came.Sandboxed);
        Assert.Equal("On air", came.Air!.Name);
        var mine = new ShowState();
        mine.Twin.Role = TwinRole.Standby;
        mine.Twin.Key = "my-key";
        mine.Control.Token = "my-token";
        mine.Weather.ApiKey = "my-wx";
        mine.Interactive.Devices.Add(new DeviceConfig { Id = "proj", Name = "Proj", Secret = "typed-here" });
        var landed = TwinSync.LandAir(came, mine)!;
        Assert.Equal("On air", landed.Air!.Name);
        Assert.Equal("Walk-in", landed.AirLabel);
        Assert.Equal(TwinRole.Standby, landed.Air.Twin.Role);
        Assert.Equal("my-key", landed.Air.Twin.Key);
        Assert.Equal("my-token", landed.Air.Control.Token);
        Assert.Equal("my-wx", landed.Air.Weather.ApiKey);
        Assert.Equal("typed-here", landed.Air.Interactive.Devices.Single().Secret);
        Assert.Equal("", came.Air.Twin.Key);
        Assert.Equal("", came.Air.Interactive.Devices.Single().Secret);

        // What rode wins over what was typed here, as a SHOW landing; the machine's own stay this desk's.
        var whole = TwinSync.LandAir(JsonUtil.Deserialize<RecoverySnapshot>(rides), mine)!;
        Assert.Equal("pj-canary", whole.Air!.Interactive.Devices.Single().Secret);
        Assert.Equal("wx-canary", whole.Air.Weather.ApiKey);
        Assert.Equal("my-key", whole.Air.Twin.Key);
        Assert.Null(TwinSync.LandAir(null, mine));
        Assert.Null(TwinSync.LandAir(bare, mine)!.Air);
    }
}
