using System.Text.Json;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>Round 65.12: every secret the show holds leaves through one list — masked in the bundle, a bare Key never touched, the twin's key by its place.</summary>
public class SecretsTests
{
    [Fact]
    public void TheBundleMasksEverySecretAndNothingElse()
    {
        var state = new ShowState();
        state.Control.Token = "ABCD-EFGH-JKLM";
        state.Twin.Key = "twin-word-7";
        state.Weather.ApiKey = "weather-key-9";
        state.Interactive.Devices.Add(new DeviceConfig { Name = "Projector", Profile = DeviceProfile.PjLink, Secret = "pj-password", InputScreen = "2", InputPattern = @"IRES=(?<w>\d+)x(?<h>\d+)" });
        var json = JsonUtil.Serialize(state);
        var redacted = Secrets.Redact(json);
        foreach (var secret in new[] { "ABCD-EFGH-JKLM", "twin-word-7", "weather-key-9", "pj-password" })
        {
            Assert.True(Secrets.Carries(json, secret), secret);
            Assert.False(Secrets.Carries(redacted, secret), secret);
        }
        using var doc = JsonDocument.Parse(redacted);
        var root = doc.RootElement;
        Assert.Equal(Secrets.Mask, root.GetProperty("Control").GetProperty("Token").GetString());
        Assert.Equal(Secrets.Mask, root.GetProperty("Twin").GetProperty("Key").GetString());
        Assert.Equal(Secrets.Mask, root.GetProperty("Weather").GetProperty("ApiKey").GetString());
        var device = root.GetProperty("Interactive").GetProperty("Devices")[0];
        Assert.Equal(Secrets.Mask, device.GetProperty("Secret").GetString());
        Assert.Equal(@"IRES=(?<w>\d+)x(?<h>\d+)", device.GetProperty("InputPattern").GetString());        // not a secret, untouched
        Assert.Equal("Projector", device.GetProperty("Name").GetString());
        // An empty secret stays empty; a bare Key that is an identity is never touched.
        Assert.Equal("", root.GetProperty("Install").GetProperty("AdminPasscode").GetString());
        Assert.Equal(json.Contains("\"MemberKey\"", StringComparison.Ordinal), redacted.Contains("\"MemberKey\"", StringComparison.Ordinal));
        Assert.Equal(redacted, SupportBundle.Redact(json));                       // the bundle reads the same list
        Assert.False(Secrets.Carries(SupportBundle.Redact(json), "pj-password"));
    }

    [Fact]
    public void TextThatIsNotJsonFallsBackToTheNamedProperties()
    {
        var text = "{ broken \"Token\": \"abc\", \"Secret\": \"pw\", \"Key\": \"ident\"";
        var redacted = Secrets.Redact(text);
        Assert.DoesNotContain("abc", redacted);
        Assert.DoesNotContain("\"pw\"", redacted);
        Assert.Contains("\"Key\": \"ident\"", redacted);
        Assert.Equal("", Secrets.Redact(""));
    }

    [Fact]
    public void TheTwinBlanksRatherThanMasksAndCountsWhatItBlanked()
    {
        var root = System.Text.Json.Nodes.JsonNode.Parse("{\"Weather\":{\"ApiKey\":\"k\"},\"Interactive\":{\"Devices\":[{\"Secret\":\"a\"},{\"Secret\":\"\"}]},\"Twin\":{\"Key\":\"w\"},\"Input\":{\"Key\":\"id\"}}")!.AsObject();
        Assert.Equal(3, Secrets.Blank(root, ""));
        Assert.Equal("", root["Weather"]!["ApiKey"]!.GetValue<string>());
        Assert.Equal("", root["Interactive"]!["Devices"]![0]!["Secret"]!.GetValue<string>());
        Assert.Equal("", root["Twin"]!["Key"]!.GetValue<string>());
        Assert.Equal("id", root["Input"]!["Key"]!.GetValue<string>());
    }
}
