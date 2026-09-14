using System.Text.RegularExpressions;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// One colour language on both sides: the module's src/palette.js and the desk's CompanionPalette
/// name the same colours with the same numbers and dress the same kinds in the same states, read
/// from the file itself so neither can drift on its own.
/// </summary>
public class CompanionPaletteTests
{
    private static string PaletteJs => File.ReadAllText(Path.Combine(CompanionModuleContractTests.ModuleDir, "src", "palette.js"));

    [Fact]
    public void TheModulesColoursAreTheDesks()
    {
        var js = PaletteJs;
        var block = js[js.IndexOf("export const COLOURS", StringComparison.Ordinal)..];
        block = block[..block.IndexOf("\n}", StringComparison.Ordinal)];
        var found = new Dictionary<string, (byte, byte, byte)>();
        foreach (Match m in Regex.Matches(block, @"^\s*(\w+):\s*\[(\d+),\s*(\d+),\s*(\d+)\]", RegexOptions.Multiline))
        {
            found[m.Groups[1].Value] = (byte.Parse(m.Groups[2].Value), byte.Parse(m.Groups[3].Value), byte.Parse(m.Groups[4].Value));
        }
        Assert.True(found.Count > 20, "the module's colour table was not read");
        Assert.Equal(CompanionPalette.Colours.Keys.OrderBy(k => k), found.Keys.OrderBy(k => k));
        foreach (var (name, rgb) in found) Assert.Equal(CompanionPalette.Colours[name], rgb);
    }

    [Fact]
    public void TheModulesStatesAreTheDesks()
    {
        var js = PaletteJs;
        var block = js[js.IndexOf("export const STATES", StringComparison.Ordinal)..];
        block = block[..block.IndexOf("\n}", StringComparison.Ordinal)];
        var found = new Dictionary<string, Dictionary<string, string>>();
        foreach (Match row in Regex.Matches(block, @"^\s*(\w+):\s*\{([^}]*)\}", RegexOptions.Multiline))
        {
            var states = new Dictionary<string, string>();
            foreach (Match pair in Regex.Matches(row.Groups[2].Value, @"(\w+):\s*'(\w+)'")) states[pair.Groups[1].Value] = pair.Groups[2].Value;
            found[row.Groups[1].Value] = states;
        }
        Assert.True(found.Count > 15, "the module's state table was not read");
        Assert.Equal(CompanionPalette.States.Keys.OrderBy(k => k), found.Keys.OrderBy(k => k));
        foreach (var (kind, states) in found)
        {
            Assert.Equal(CompanionPalette.States[kind].OrderBy(p => p.Key), states.OrderBy(p => p.Key));
            foreach (var colour in states.Values) Assert.True(CompanionPalette.Colours.ContainsKey(colour), $"{kind} names {colour}");
        }
    }

    [Fact]
    public void TheHuesReadTheSameOnTheDesk()
    {
        Assert.Equal("#1E9E5A", CompanionPalette.Hex("green"));
        Assert.Equal("#1E9E5A", CompanionPalette.Hex("look", "air"));
        Assert.Equal("#8250DC", CompanionPalette.NodeHue(NodeKind.Caller, fresh: true));
        Assert.Equal("#6E2828", CompanionPalette.NodeHue(NodeKind.Caller, fresh: false));
        Assert.Equal("#008CC8", CompanionPalette.NodeHue(NodeKind.Desk, fresh: true));
        Assert.Equal("#FFC24D", CompanionPalette.StageHue("amber"));
        Assert.Equal(CompanionPalette.Hex("dark"), CompanionPalette.Hex("nothing"));
        Assert.Equal(CompanionPalette.Hex("dark"), CompanionPalette.Hex("look", "nothing"));
    }
}
