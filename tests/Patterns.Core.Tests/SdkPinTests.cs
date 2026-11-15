using System.Text.Json;
using System.Text.RegularExpressions;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The tree builds on the SDK global.json names, and nowhere else: global.json pins a full SDK
/// version with patch updates only and no previews, every actions/setup-dotnet step in every
/// workflow installs from that file and names no version of its own, and the papers that say which
/// SDK builds the tree name the same one. A step that names its own version lets the runner image's
/// newest SDK decide which analysis rule fails the build; a global.json without its roll-forward lets
/// a machine's SDK of any band build it. Either fails here, before a run on CI does. The fence reads the
/// setup-dotnet steps of .github/workflows and .github/actions and refuses any workflow line that runs
/// dotnet-install; an SDK another installer puts on a machine is still refused by global.json outside
/// the pinned band.
/// </summary>
public class SdkPinTests
{
    private static string Root => CompanionModuleContractTests.RepoRoot()
        ?? throw new InvalidOperationException("The repository root was not found above the test binary.");

    private static string GlobalJsonPath => Path.Combine(Root, "global.json");

    // setup-dotnet refuses a global.json whose rollForward names no full version (major.minor.feature-patch).
    private static readonly Regex FullSdkVersion = new(@"^\d+\.\d+\.[1-9]\d{2}$", RegexOptions.CultureInvariant, TimeSpan.FromSeconds(1));

    private static JsonElement Sdk()
    {
        Assert.True(File.Exists(GlobalJsonPath), "global.json is missing at the tree's root.");
        using var doc = JsonDocument.Parse(File.ReadAllText(GlobalJsonPath));
        Assert.True(doc.RootElement.TryGetProperty("sdk", out var sdk) && sdk.ValueKind == JsonValueKind.Object,
            "global.json has no sdk object.");
        return sdk.Clone();
    }

    private static string PinnedVersion()
    {
        var sdk = Sdk();
        Assert.True(sdk.TryGetProperty("version", out var version) && version.ValueKind == JsonValueKind.String,
            "global.json's sdk names no version.");
        return version.GetString()!;
    }

    [Fact]
    public void GlobalJsonPinsAFullSdkVersionWithPatchUpdatesOnly()
    {
        var sdk = Sdk();
        var version = PinnedVersion();
        Assert.Matches(FullSdkVersion, version);

        Assert.True(sdk.TryGetProperty("rollForward", out var rollForward) && rollForward.ValueKind == JsonValueKind.String,
            "global.json's sdk has lost its rollForward: without it the pin's patch policy is the SDK's default, not the tree's.");
        Assert.Equal("latestPatch", rollForward.GetString());

        Assert.True(sdk.TryGetProperty("allowPrerelease", out var prerelease), "global.json's sdk does not say allowPrerelease.");
        Assert.Equal(JsonValueKind.False, prerelease.ValueKind);
    }

    [Fact]
    public void EverySetupDotnetStepInstallsTheSdkGlobalJsonNames()
    {
        var workflows = Directory.GetFiles(Path.Combine(Root, ".github", "workflows"))
            .Where(IsYaml)
            .OrderBy(f => f, StringComparer.Ordinal)
            .ToList();
        Assert.NotEmpty(workflows);
        var actions = Path.Combine(Root, ".github", "actions");
        if (Directory.Exists(actions))
            workflows.AddRange(Directory.GetFiles(actions, "*", SearchOption.AllDirectories).Where(IsYaml).OrderBy(f => f, StringComparer.Ordinal));

        var steps = 0;
        foreach (var file in workflows)
        {
            var name = Path.GetFileName(file);
            var lines = File.ReadAllLines(file);
            for (var i = 0; i < lines.Length; i++)
            {
                Assert.False(Key(lines[i]) == "dotnet-version",
                    $"{name}:{i + 1} names an SDK version of its own; the SDK is the one global.json names (global-json-file: global.json).");
                Assert.False(!Content(lines[i]).StartsWith('#') && Content(lines[i]).Contains("dotnet-install", StringComparison.Ordinal),
                    $"{name}:{i + 1} installs an SDK with dotnet-install; the SDK is installed by setup-dotnet from global.json.");
                if (Key(lines[i]) != "uses" || !Value(lines[i]).StartsWith("actions/setup-dotnet@", StringComparison.Ordinal)) continue;

                steps++;
                var with = StepKeys(lines, i);
                Assert.True(with.TryGetValue("global-json-file", out var json) && json == "global.json",
                    $"{name}:{i + 1}: a setup-dotnet step that does not install from global-json-file: global.json.");
            }
        }
        Assert.True(steps > 0, "No workflow has an actions/setup-dotnet step; the fence would hold nothing.");
    }

    [Theory]
    [InlineData("README.md")]
    [InlineData("docs/ANALYSIS.md")]
    public void ThePapersNameThePinnedSdk(string paper)
    {
        var text = File.ReadAllText(Path.Combine(Root, paper));
        Assert.Contains("global.json", text, StringComparison.Ordinal);
        var version = PinnedVersion();
        Assert.Contains(version, text, StringComparison.Ordinal);
        // The band the papers name ("10.0.1xx") is the pinned patch's: a pin moved to another band
        // leaves the band sentences stale unless they move with it.
        Assert.Contains(Band(version), text, StringComparison.Ordinal);
    }

    [Fact]
    public void TheBandIsThePatchFieldsFirstDigit()
    {
        Assert.Equal("10.0.1xx", Band("10.0.115"));
        Assert.Equal("10.0.4xx", Band("10.0.404"));
        Assert.Equal("11.0.1xx", Band("11.0.100"));
    }

    /// <summary>The feature band of a full SDK version: major.minor and the patch field's first digit, then "xx".</summary>
    private static string Band(string version)
    {
        var lastDot = version.LastIndexOf('.');
        return version[..(lastDot + 2)] + "xx";
    }

    private static bool IsYaml(string file) =>
        file.EndsWith(".yml", StringComparison.Ordinal) || file.EndsWith(".yaml", StringComparison.Ordinal);

    /// <summary>The keys and values of the step a line of it starts or sits in, read by indentation.</summary>
    private static Dictionary<string, string> StepKeys(string[] lines, int at)
    {
        var start = at;
        while (start > 0 && !lines[start].TrimStart().StartsWith("- ", StringComparison.Ordinal)) start--;
        var indent = Indent(lines[start]);
        var keys = new Dictionary<string, string>(StringComparer.Ordinal) { [Key(lines[start])] = Value(lines[start]) };
        for (var i = start + 1; i < lines.Length; i++)
        {
            var trimmed = lines[i].Trim();
            if (trimmed.Length == 0 || trimmed.StartsWith('#')) continue;
            if (Indent(lines[i]) <= indent) break;
            keys[Key(lines[i])] = Value(lines[i]);
        }
        return keys;
    }

    private static int Indent(string line) => line.Length - line.TrimStart(' ').Length;

    private static string Content(string line)
    {
        var text = line.Trim();
        if (text.StartsWith("- ", StringComparison.Ordinal)) text = text[2..].TrimStart();
        var comment = text.IndexOf(" #", StringComparison.Ordinal);
        return comment >= 0 ? text[..comment].TrimEnd() : text;
    }

    private static string Key(string line)
    {
        var text = Content(line);
        if (text.StartsWith('#')) return "";
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 ? text[..colon].Trim() : "";
    }

    private static string Value(string line)
    {
        var text = Content(line);
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        return colon > 0 ? text[(colon + 1)..].Trim().Trim('"', '\'') : "";
    }
}
