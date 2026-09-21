using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 80: the master rate follows the displays. A show never asks a display for more frames than
/// it refreshes: with the follow on, the slowest display behind an enabled screen leads when it is
/// slower than the setting, and every reader — the Super Check's row, the Eye's desk words, the
/// assistant's brief — says which display and what was set. The ledger's row L11.
/// </summary>
public class MasterRateTests
{
    private static readonly (string Label, int Hz)[] MainAndLobby = { ("Main", 60), ("Lobby", 50) };

    [Fact]
    public void ASlowerDisplayLeadsWhenTheFollowIsOn()
    {
        var m = OutputRate.Master(60, true, MainAndLobby);
        Assert.Equal(50, m.Effective);
        Assert.True(m.Followed);
        Assert.False(m.Overasks);
        Assert.Equal("Lobby", m.SlowestLabel);
        Assert.Equal(50, m.SlowestHz);
        Assert.Equal("50 fps — following Lobby (50 Hz); set 60", m.Words);
    }

    [Fact]
    public void TheSlowestOfSeveralLeads()
    {
        var m = OutputRate.Master(60, true, new[] { ("Main", 60), ("Lobby", 50), ("Foyer", 30) });
        Assert.Equal(30, m.Effective);
        Assert.Equal("Foyer", m.SlowestLabel);
    }

    [Fact]
    public void TheFollowOffHoldsTheSettingAndNamesTheOverAskedDisplay()
    {
        var m = OutputRate.Master(60, false, MainAndLobby);
        Assert.Equal(60, m.Effective);
        Assert.False(m.Followed);
        Assert.True(m.Overasks);
        Assert.Equal("60 fps — Lobby refreshes at 50 Hz and is not followed", m.Words);
    }

    [Theory]
    [InlineData(0, 60, 0, "every display's own refresh")]      // unlimited stays unlimited: every output presents at its own display
    [InlineData(30, 60, 30, "30 fps")]                           // never up: the setting stands under faster displays
    [InlineData(60, 59, 60, "60 fps")]                           // 59 under 60 is one family, not a slower display
    [InlineData(60, 0, 60, "60 fps")]                            // a display not yet seen does not count
    [InlineData(60, 120, 60, "60 fps")]                          // a faster display never leads
    public void TheSettingStandsWhenNoDisplayIsSlower(int set, int hz, int effective, string words)
    {
        var m = OutputRate.Master(set, true, new[] { ("Main", hz) });
        Assert.Equal(effective, m.Effective);
        Assert.False(m.Followed);
        Assert.False(m.Overasks);
        Assert.Equal(words, m.Words);
    }

    [Fact]
    public void TheRuleOverTheShowReadsOnlyTheScreensThatAreHere()
    {
        var output = new OutputConfig { MasterFps = 60 };
        output.Placements.Add(new ScreenPlacement { ScreenId = "a", Enabled = true, DisplayHz = 60 });
        output.Placements.Add(new ScreenPlacement { ScreenId = "b", CustomLabel = "Lobby", Enabled = true, DisplayHz = 50 });
        output.Placements.Add(new ScreenPlacement { ScreenId = "off", Enabled = false, DisplayHz = 25 });                     // a disabled screen has no output
        output.Placements.Add(new ScreenPlacement { ScreenId = "planned", Enabled = true, Planned = true, DisplayHz = 24 });  // a planned screen has no display yet
        output.Placements.Add(new ScreenPlacement { ScreenId = "lost", Enabled = true, DisplayHz = 30, LostAtUtc = new DateTime(2026, 9, 21, 9, 0, 0, DateTimeKind.Utc) });   // a lost display leads nothing until it is back
        Assert.True(output.FollowDisplays);   // the default: a show saved before this round follows
        var m = OutputRate.Master(output);
        Assert.Equal(50, m.Effective);
        Assert.Equal("Lobby", m.SlowestLabel);
        Assert.Equal(50, OutputRate.EffectiveMaster(output));

        output.FollowDisplays = false;
        Assert.Equal(60, OutputRate.EffectiveMaster(output));
        Assert.True(OutputRate.Master(output).Overasks);
    }

    [Fact]
    public void TheSuperCheckRowSaysWhatLeadsAndWhatWasSet()
    {
        var followed = SuperCheck.Run(new CheckFacts { Master = OutputRate.Master(60, true, MainAndLobby) });
        var row = followed.Rows.Single(r => r.Item == "Master rate");
        Assert.Equal(CheckLight.Green, row.Light);
        Assert.Equal("50 fps — following Lobby (50 Hz); set 60", row.Value);
        Assert.Contains("switch the follow off", row.Note);

        var held = SuperCheck.Run(new CheckFacts { Master = OutputRate.Master(60, false, MainAndLobby) });
        row = held.Rows.Single(r => r.Item == "Master rate");
        Assert.Equal(CheckLight.Amber, row.Light);
        Assert.Contains("Lobby refreshes at 50 Hz but the show asks for 60", row.Note);
        Assert.Contains("switch the follow on", row.Note);

        var unlimited = SuperCheck.Run(new CheckFacts { Master = OutputRate.Master(0, true, MainAndLobby) });
        Assert.Equal(CheckLight.Grey, unlimited.Rows.Single(r => r.Item == "Master rate").Light);

        Assert.DoesNotContain(SuperCheck.Run(new CheckFacts()).Rows, r => r.Item == "Master rate");   // a bare fact set: no row
    }

    [Fact]
    public void TheDisplayRowGoesGreenOnceTheShowFollowsItDown()
    {
        var lobby = new CheckDisplay("Lobby", 1920, 1080, 1, false, true, false, 50);
        var held = SuperCheck.Run(new CheckFacts { Displays = new[] { lobby }, TargetFps = 60 });
        Assert.Equal(CheckLight.Amber, held.Rows.Single(r => r.Item == "Lobby").Light);
        Assert.Contains("let it follow the displays", held.Rows.Single(r => r.Item == "Lobby").Note);
        var followed = SuperCheck.Run(new CheckFacts { Displays = new[] { lobby }, TargetFps = 50 });   // the target is the rate in force
        Assert.Equal(CheckLight.Green, followed.Rows.Single(r => r.Item == "Lobby").Light);
    }

    [Fact]
    public void TheEyeAndTheBriefCarryTheWords()
    {
        var words = OutputRate.Master(60, true, MainAndLobby).Words;
        var g = EyeGraph.Build(new EyeFacts { MachineName = "PC", MasterRate = words });
        Assert.Contains(g.Find(EyeGraph.DeskId)!.Words, w => w == "Master rate " + words);
        Assert.DoesNotContain(EyeGraph.Build(new EyeFacts { MachineName = "PC" }).Find(EyeGraph.DeskId)!.Words, w => w.StartsWith("Master rate", StringComparison.Ordinal));
    }
}
