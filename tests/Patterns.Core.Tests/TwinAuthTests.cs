using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 44: the key is proved, never sent; the wire carries the mirrored sections and nothing of
/// the machine's own; the show's credentials travel only when told, and a landing never wipes the
/// ones typed on the standby.
/// </summary>
public class TwinAuthTests
{
    [Fact]
    public void NoncesAreFreshAndProofsBindTheKeyTheNonceAndTheName()
    {
        var a = TwinAuth.NewNonce();
        var b = TwinAuth.NewNonce();
        Assert.Equal(48, a.Length);
        Assert.NotEqual(a, b);
        var proof = TwinAuth.Proof("hunter2", a, "abcd1234");
        Assert.Equal(64, proof.Length);
        Assert.Equal(proof, TwinAuth.Proof("hunter2", a, "abcd1234"));
        Assert.True(TwinAuth.Verify("hunter2", a, "abcd1234", proof));
        Assert.True(TwinAuth.Verify("hunter2", a, "abcd1234", proof.ToUpperInvariant() + " "));
        Assert.False(TwinAuth.Verify("hunter3", a, "abcd1234", proof));            // another key
        Assert.False(TwinAuth.Verify("hunter2", b, "abcd1234", proof));            // another nonce: a replay answers nothing
        Assert.False(TwinAuth.Verify("hunter2", a, "zzzz", proof));                // another instance
        Assert.False(TwinAuth.Verify("", a, "abcd1234", TwinAuth.Proof("", a, "abcd1234")));   // no key proves nothing
        Assert.False(TwinAuth.Verify("hunter2", a, "abcd1234", ""));
        Assert.False(TwinAuth.Verify("hunter2", a, "abcd1234", "not hex"));
    }

    [Fact]
    public void TheHandshakeWordsRoundTripAndTheJoinCarriesANonceInsteadOfTheKey()
    {
        var challenge = new TwinChallenge(TwinAuth.NewNonce(), "abc");
        var line = TwinMessage.Format(TwinWord.Challenge, challenge.ToJson());
        Assert.StartsWith("CHALLENGE {", line);
        var back = TwinMessage.Parse(line);
        Assert.Equal(TwinWord.Challenge, back.Word);
        Assert.Equal(challenge, TwinChallenge.Parse(back.Payload));
        Assert.Null(TwinChallenge.Parse("{ not json"));
        var proof = TwinMessage.Parse(TwinMessage.Format(TwinWord.Proof, "deadbeef"));
        Assert.Equal(TwinWord.Proof, proof.Word);
        Assert.Equal("deadbeef", proof.Payload);

        var join = new TwinJoin("Backup desk", "BACKUP-PC", "abcd1234", "", Nonce: "n1");
        var parsed = TwinJoin.Parse(join.ToJson());
        Assert.Equal("", parsed!.Key);
        Assert.Equal("n1", parsed.Nonce);
        Assert.Equal(2, TwinMessage.Proto);
        Assert.Equal("", TwinJoin.Parse("{\"Name\":\"Old\",\"Machine\":\"PC\",\"Instance\":\"x\",\"Key\":\"k\",\"Proto\":1}")!.Nonce);   // a version-1 joiner: refused for its version
    }

    [Fact]
    public void TheWireCarriesTheMirroredSectionsOnlyAndCredentialsOnlyWhenTold()
    {
        var state = new ShowState { Name = "Gala" };
        state.Twin.Key = "hunter2";
        state.Install.AdminPasscode = "1234";
        state.Install.ManagementToken = "tok";
        state.Weather.ApiKey = "wx-key";
        state.Interactive.Devices.Add(new DeviceConfig { Id = "proj", Name = "Proj", Profile = DeviceProfile.PjLink, Secret = "pjpass" });

        var withSecrets = TwinSync.WireJson(state, sendSecrets: true);
        Assert.Contains("\"Name\":\"Gala\"", withSecrets);
        Assert.Contains("\"Secret\":\"pjpass\"", withSecrets);
        Assert.Contains("\"ApiKey\":\"wx-key\"", withSecrets);
        Assert.DoesNotContain("hunter2", withSecrets);                              // the twin's key never travels
        Assert.DoesNotContain("1234", withSecrets);
        Assert.DoesNotContain("\"tok\"", withSecrets);
        Assert.DoesNotContain("\"Twin\":", withSecrets);
        Assert.DoesNotContain("\"Admin\":", withSecrets);
        Assert.DoesNotContain("\"Control\":", withSecrets);
        Assert.DoesNotContain("\"Watchdog\":", withSecrets);
        Assert.Equal(withSecrets, TwinSync.ShowJson(state));

        var without = TwinSync.WireJson(state, sendSecrets: false);
        Assert.Contains("\"Secret\":\"\"", without);
        Assert.Contains("\"ApiKey\":\"\"", without);
        Assert.DoesNotContain("pjpass", without);
        Assert.DoesNotContain("wx-key", without);
        Assert.Contains("\"Name\":\"Proj\"", without);                               // the box itself travels; its password does not

        Assert.DoesNotContain("pjpass", TwinSync.WireSectionJson(state, nameof(ShowState.Interactive), sendSecrets: false));
        Assert.Contains("pjpass", TwinSync.WireSectionJson(state, nameof(ShowState.Interactive), sendSecrets: true));
        Assert.DoesNotContain("wx-key", TwinSync.WireSectionJson(state, nameof(ShowState.Weather), sendSecrets: false));
        Assert.Equal(TwinSync.SectionJson(state, nameof(ShowState.LooksAndCues)), TwinSync.WireSectionJson(state, nameof(ShowState.LooksAndCues), sendSecrets: false));
        Assert.True(TwinSync.CarriesSecrets(nameof(ShowState.Interactive)));
        Assert.False(TwinSync.CarriesSecrets(nameof(ShowState.Stacks)));
    }

    [Fact]
    public void ALandingKeepsTheCredentialsTypedOnTheStandbyWhereTheWireIsBlank()
    {
        var main = new ShowState { Name = "Gala" };
        main.Weather.ApiKey = "wx-main";
        main.Interactive.Devices.Add(new DeviceConfig { Id = "proj", Name = "Proj", Profile = DeviceProfile.PjLink, Secret = "pj-main" });
        main.Interactive.Devices.Add(new DeviceConfig { Id = "enc", Name = "Encoder", Link = DeviceLink.Http, Port = "http://10.0.0.9" });

        var standby = new ShowState { Name = "Old" };
        standby.Weather.ApiKey = "wx-standby";
        standby.Interactive.Devices.Add(new DeviceConfig { Id = "proj", Name = "Proj", Profile = DeviceProfile.PjLink, Secret = "pj-standby" });

        // Blank on the wire: the standby's own stand.
        Assert.True(TwinSync.ApplyShow(standby, TwinSync.WireJson(main, sendSecrets: false)));
        Assert.Equal("Gala", standby.Name);
        Assert.Equal("wx-standby", standby.Weather.ApiKey);
        Assert.Equal("pj-standby", standby.Interactive.Devices.First(d => d.Id == "proj").Secret);
        Assert.Equal("", standby.Interactive.Devices.First(d => d.Id == "enc").Secret);   // a box the standby never had a password for

        // Sent on the wire: the main's land.
        Assert.True(TwinSync.ApplyShow(standby, TwinSync.WireJson(main, sendSecrets: true)));
        Assert.Equal("wx-main", standby.Weather.ApiKey);
        Assert.Equal("pj-main", standby.Interactive.Devices.First(d => d.Id == "proj").Secret);

        // A section alone, blank on the wire, keeps them too.
        standby.Interactive.Devices.First(d => d.Id == "proj").Secret = "pj-typed";
        Assert.True(TwinSync.ApplySection(standby, nameof(ShowState.Interactive), TwinSync.WireSectionJson(main, nameof(ShowState.Interactive), sendSecrets: false)));
        Assert.Equal("pj-typed", standby.Interactive.Devices.First(d => d.Id == "proj").Secret);
        Assert.Equal("Encoder", standby.Interactive.Devices.First(d => d.Id == "enc").Name);

        // And the machine's own sections are not in a wire show at all, so a landing cannot touch them.
        standby.Twin.Key = "mine";
        Assert.True(TwinSync.ApplyShow(standby, TwinSync.WireJson(main, sendSecrets: true)));
        Assert.Equal("mine", standby.Twin.Key);
    }
}
