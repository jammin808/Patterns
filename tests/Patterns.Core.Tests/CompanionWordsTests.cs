using System.Net;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>The words between the desk and its decks: a HELLO taken apart, a deck's line with the module it runs, the Remote page's three lines, and the module version this build ships held equal to the module's own.</summary>
public class CompanionWordsTests
{
    [Fact]
    public void AHelloIsANameAndAModule()
    {
        Assert.Equal(("FOH deck", "3.0.0"), CompanionWords.ParseHello("FOH deck module=3.0.0"));
        Assert.Equal(("FOH deck", ""), CompanionWords.ParseHello("FOH deck"));
        Assert.Equal(("Stage deck two", "2.8.0"), CompanionWords.ParseHello("  Stage deck two module=2.8.0  "));
        Assert.Equal(("Companion", "3.0.0"), CompanionWords.ParseHello("module=3.0.0"));
        Assert.Equal(("", ""), CompanionWords.ParseHello(""));
    }

    [Fact]
    public void ADeckSaysWhenItsModuleIsBehind()
    {
        var since = DateTime.UtcNow;
        Assert.Equal("FOH deck (module 3.0.0, 10.0.0.5)", new WireDeck("FOH deck", "3.0.0", "10.0.0.5", since).Line);
        Assert.Equal($"Stage deck (module 2.8.0 — {CompanionModule.Version} is current, in the app's integrations folder, 10.0.0.6)", new WireDeck("Stage deck", "2.8.0", "10.0.0.6", since).Line);
        Assert.Equal("script (no module — Generic TCP or a script, 10.0.0.7)", new WireDeck("script", "", "10.0.0.7", since).Line);
        Assert.True(CompanionWords.IsOlder("2.8.0", "3.0.0"));
        Assert.True(CompanionWords.IsOlder("v2.8", "3.0.0"));
        Assert.False(CompanionWords.IsOlder("3.0.0", "3.0.0"));
        Assert.False(CompanionWords.IsOlder("3.1.0-beta", "3.0.0"));
        Assert.False(CompanionWords.IsOlder("dev", "3.0.0"));
    }

    [Fact]
    public void TheRemotePagesThreeLines()
    {
        Assert.StartsWith("Remote control is off", CompanionWords.AnnounceLine(false, true, "", "Patterns desk FOH-PC"));
        Assert.StartsWith("Not announced on the network", CompanionWords.AnnounceLine(true, false, "", "Patterns desk FOH-PC"));
        Assert.Equal("Announced on the network as \"Patterns desk FOH-PC\" — pick it under Desk on the network in Companion's connection settings.", CompanionWords.AnnounceLine(true, true, "", "Patterns desk FOH-PC"));
        Assert.Equal("port 5353 busy", CompanionWords.AnnounceLine(true, true, "port 5353 busy", "x"));
        Assert.StartsWith("No deck connected.", CompanionWords.DecksLine(Array.Empty<WireDeck>()));
        Assert.Equal("Connected: FOH deck (module 3.0.0, 10.0.0.5).", CompanionWords.DecksLine(new[] { new WireDeck("FOH deck", "3.0.0", "10.0.0.5", DateTime.UtcNow) }));
        Assert.StartsWith("No Companion heard on the network yet", CompanionWords.HeardLine(Array.Empty<MdnsPeer>()));
        var peer = new MdnsPeer("Companion (FOH-PC)", "foh-pc.local", IPAddress.Parse("10.0.0.7"), 16622, new Dictionary<string, string> { ["version"] = "5.0.3" }, DateTime.UtcNow, 4500);
        Assert.Equal("Companion on the network: Companion (FOH-PC) at 10.0.0.7:16622 · v5.0.3 — the desk drives it through a Companion device on the Interactive page (port 16759).", CompanionWords.HeardLine(new[] { peer }));
    }

    [Fact]
    public void TheModuleVersionThisBuildShipsIsTheModules()
    {
        var package = File.ReadAllText(Path.Combine(CompanionModuleContractTests.ModuleDir, "package.json"));
        Assert.Contains($"\"version\": \"{CompanionModule.Version}\"", package);
        var main = File.ReadAllText(Path.Combine(CompanionModuleContractTests.ModuleDir, "src", "main.js"));
        Assert.Contains($"MODULE_VERSION = '{CompanionModule.Version}'", main);
    }
}
