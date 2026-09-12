using System.Text.Json;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>The twin link, pure: its words, the mirror section by section, what never travels, the watch's timing and lines.</summary>
public class TwinTests
{
    [Fact]
    public void TheWordsRoundTripAndAStrayLineIsNotOne()
    {
        Assert.Equal(new TwinMessage(TwinWord.Beat, "", "12"), TwinMessage.Parse("BEAT 12"));
        Assert.Equal(new TwinMessage(TwinWord.Bye, "", ""), TwinMessage.Parse("bye"));
        Assert.Equal(new TwinMessage(TwinWord.Section, "LooksAndCues", "{\"a\":1}"), TwinMessage.Parse("SECTION LooksAndCues {\"a\":1}"));
        Assert.Equal(new TwinMessage(TwinWord.Section, "Name", ""), TwinMessage.Parse("SECTION Name"));
        Assert.Equal(new TwinMessage(TwinWord.Show, "", "{}"), TwinMessage.Parse("  SHOW {}  "));
        Assert.Equal(new TwinMessage(TwinWord.Air, "", "null"), TwinMessage.Parse("AIR null"));
        Assert.Equal(new TwinMessage(TwinWord.Refused, "", "wrong key"), TwinMessage.Parse("REFUSED wrong key"));
        Assert.Equal(TwinWord.Unknown, TwinMessage.Parse("STATE {\"live\":true}").Word);
        Assert.Equal("STATE", TwinMessage.Parse("STATE {\"live\":true}").Name);
        Assert.Equal(TwinWord.Unknown, TwinMessage.Parse("").Word);
        Assert.Equal(TwinWord.Unknown, TwinMessage.Parse(null).Word);

        Assert.Equal("SECTION Stacks [1]", TwinMessage.Format(TwinWord.Section, "[1]", "Stacks"));
        Assert.Equal("BEAT 3", TwinMessage.Format(TwinWord.Beat, "3"));
        Assert.Equal("BYE", TwinMessage.Format(TwinWord.Bye));
        var join = new TwinJoin("Backup desk", "BACKUP-PC", "abcd1234", "hunter2");
        Assert.Equal(join, TwinJoin.Parse(TwinMessage.Parse(TwinMessage.Format(TwinWord.Join, join.ToJson())).Payload));
        Assert.Null(TwinJoin.Parse("not json"));
        var welcome = new TwinWelcome("Main desk", "MAIN-PC", "ef012345", 4242, 637_000_000_000_000_000, @"C:\Patterns\Patterns.exe", "Gala");
        Assert.Equal(welcome, TwinWelcome.Parse(welcome.ToJson()));
        Assert.Equal(TwinMessage.Proto, welcome.Proto);
        Assert.DoesNotContain('\n', TwinSync.ShowJson(new ShowState { Name = "one\nline" }));   // one line on the wire, always
    }

    [Fact]
    public void EverySectionOfTheFileIsNamedAndTheMachinesOwnNeverTravel()
    {
        // The names the publish reports dirty are the root's own properties: every one of them is a section here.
        var roots = typeof(ShowState).GetProperties().Where(p => p.GetCustomAttributes(typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), true).Length == 0).Select(p => p.Name).ToList();
        Assert.Equal(roots, TwinSync.SectionNames);
        Assert.Contains(nameof(ShowState.LooksAndCues), TwinSync.MirroredSections);
        Assert.Contains(nameof(ShowState.Name), TwinSync.MirroredSections);
        Assert.Contains(nameof(ShowState.Stacks), TwinSync.MirroredSections);
        foreach (var local in TwinSync.LocalSections)
        {
            Assert.Contains(local, TwinSync.SectionNames);          // a real section, spelled right
            Assert.DoesNotContain(local, TwinSync.MirroredSections);
            Assert.False(TwinSync.IsMirrored(local));
        }
        Assert.False(TwinSync.IsMirrored("NoSuchSection"));
        // A dirty set comes back mirrored-only, once each, in the file's order.
        Assert.Equal(new[] { nameof(ShowState.Name), nameof(ShowState.LooksAndCues), nameof(ShowState.Stacks) },
            TwinSync.Mirrored(new[] { "Stacks", "Twin", "LooksAndCues", "Stacks", "Name", "Watchdog", "Bogus" }));
    }

    [Fact]
    public void ASectionLandsInPlaceAValueAListAndAnObservable()
    {
        var main = new ShowState { Name = "Gala" };
        main.LooksAndCues.Looks.Add(new LookConfig { Name = "Walk-in" });
        main.Stacks.Add(new CueStackConfig { Name = "Caller" });
        main.Tone.FrequencyHz = 440;
        main.Pattern.Grid.CellSize = 77;

        var standby = new ShowState { Name = "Empty" };
        var looksBefore = standby.LooksAndCues;
        var toneBefore = standby.Tone;
        Assert.True(TwinSync.ApplySection(standby, nameof(ShowState.Name), TwinSync.SectionJson(main, nameof(ShowState.Name))));
        Assert.True(TwinSync.ApplySection(standby, nameof(ShowState.LooksAndCues), TwinSync.SectionJson(main, nameof(ShowState.LooksAndCues))));
        Assert.True(TwinSync.ApplySection(standby, nameof(ShowState.Stacks), TwinSync.SectionJson(main, nameof(ShowState.Stacks))));
        Assert.True(TwinSync.ApplySection(standby, nameof(ShowState.Tone), TwinSync.SectionJson(main, nameof(ShowState.Tone))));
        Assert.Equal("Gala", standby.Name);
        Assert.Same(looksBefore, standby.LooksAndCues);              // in place: the bindings keep their objects
        Assert.Equal("Walk-in", Assert.Single(standby.LooksAndCues.Looks).Name);
        Assert.NotSame(main.LooksAndCues.Looks[0], standby.LooksAndCues.Looks[0]);   // a detached clone, never the main's own row
        Assert.Equal("Caller", Assert.Single(standby.Stacks).Name);
        Assert.Same(toneBefore, standby.Tone);
        Assert.Equal(440, standby.Tone.FrequencyHz);
        Assert.Equal(0, standby.Pattern.Grid.CellSize == 77 ? 1 : 0);  // only the sections sent moved
    }

    [Fact]
    public void WhatCannotLandIsRefusedAndNothingIsTouched()
    {
        var standby = new ShowState { Name = "Mine" };
        standby.Twin.Role = TwinRole.Standby;
        standby.Twin.Key = "k";
        Assert.False(TwinSync.ApplySection(standby, nameof(ShowState.Twin), "{\"Role\":\"Main\"}"));   // the machine's own: never
        Assert.Equal(TwinRole.Standby, standby.Twin.Role);
        Assert.False(TwinSync.ApplySection(standby, "Bogus", "{}"));
        Assert.False(TwinSync.ApplySection(standby, nameof(ShowState.LooksAndCues), "not json"));
        Assert.False(TwinSync.ApplySection(standby, nameof(ShowState.LooksAndCues), "null"));
        Assert.Empty(standby.LooksAndCues.Looks);
        Assert.False(TwinSync.ApplyShow(standby, "{{{"));
        Assert.Equal("Mine", standby.Name);
    }

    [Fact]
    public void TheWholeShowLandsButTheMachinesOwnSectionsStay()
    {
        var main = new ShowState { Name = "Gala", Mode = ShowMode.Show };
        main.Twin.Role = TwinRole.Main;
        main.Twin.Key = "hunter2";
        main.Watchdog.BeaconEnabled = true;
        main.Control.TcpPort = 9123;
        main.Admin.Graphics.AdapterName = "The main's card";
        main.LooksAndCues.Looks.Add(new LookConfig { Name = "Walk-in" });
        main.Spotify.Items.Add(new SpotifyItemConfig { Uri = "spotify:playlist:A" });

        var standby = new ShowState { Name = "Empty", Mode = ShowMode.Prep };
        standby.Twin.Role = TwinRole.Standby;
        standby.Twin.MainHost = "10.0.0.5";
        standby.Watchdog.BeaconListen = true;
        standby.Control.TcpPort = 9697;
        Assert.True(TwinSync.ApplyShow(standby, TwinSync.ShowJson(main)));

        Assert.Equal("Gala", standby.Name);
        Assert.Equal(ShowMode.Show, standby.Mode);
        Assert.Equal("Walk-in", Assert.Single(standby.LooksAndCues.Looks).Name);
        Assert.Single(standby.Spotify.Items);
        Assert.Equal(TwinRole.Standby, standby.Twin.Role);            // still the standby
        Assert.Equal("10.0.0.5", standby.Twin.MainHost);
        Assert.Equal("", standby.Twin.Key);
        Assert.False(standby.Watchdog.BeaconEnabled);                 // not sending the main's beacon
        Assert.True(standby.Watchdog.BeaconListen);
        Assert.Equal(9697, standby.Control.TcpPort);
        Assert.Equal("", standby.Admin.Graphics.AdapterName);
    }

    [Fact]
    public void TheWatchCountsFiveMissedBeatsAsGoneAndTakesOverOnlyWhenTold()
    {
        var now = new DateTime(2026, 9, 12, 19, 0, 0, DateTimeKind.Utc);
        Assert.False(TwinWatch.IsSilent(null, now));
        Assert.False(TwinWatch.IsSilent(now.AddSeconds(-4), now));
        Assert.True(TwinWatch.IsSilent(now.AddSeconds(-6), now));
        Assert.False(TwinWatch.ShouldTakeOver(autoTakeOver: false, TwinPhase.MainSilent, now.AddSeconds(-30), now));
        Assert.False(TwinWatch.ShouldTakeOver(autoTakeOver: true, TwinPhase.InStep, now.AddSeconds(-2), now));
        Assert.True(TwinWatch.ShouldTakeOver(autoTakeOver: true, TwinPhase.InStep, now.AddSeconds(-6), now));
        Assert.True(TwinWatch.ShouldTakeOver(autoTakeOver: true, TwinPhase.MainSilent, now.AddSeconds(-6), now));
        Assert.False(TwinWatch.ShouldTakeOver(autoTakeOver: true, TwinPhase.TookOver, now.AddSeconds(-60), now));   // once is enough
        Assert.False(TwinWatch.ShouldTakeOver(autoTakeOver: true, TwinPhase.Connecting, null, now));            // never joined: nothing to take
    }

    [Fact]
    public void TheLinesSayWhereTheLinkStandsOnEitherSide()
    {
        var now = new DateTime(2026, 9, 12, 19, 0, 0, DateTimeKind.Utc);
        Assert.Equal("Twin off.", TwinWatch.DescribeStandby(TwinPhase.Off, "", null, 0, false, now));
        Assert.Equal("STANDBY — connecting to MAIN-DESK…", TwinWatch.DescribeStandby(TwinPhase.Connecting, "MAIN-DESK", null, 0, false, now));
        Assert.StartsWith("STANDBY — waiting for a main", TwinWatch.DescribeStandby(TwinPhase.Connecting, "", null, 0, false, now));
        Assert.Equal("STANDBY for MAIN-DESK — in step, heard just now, 4 sections mirrored · outputs held closed · TAKE OVER is yours.",
            TwinWatch.DescribeStandby(TwinPhase.InStep, "MAIN-DESK", now.AddSeconds(-1), 4, false, now));
        Assert.EndsWith("1 section mirrored · outputs held closed · takes over on silence.", TwinWatch.DescribeStandby(TwinPhase.InStep, "MAIN-DESK", now, 1, true, now));
        Assert.Equal("MAIN MAIN-DESK SILENT for 7 s — TAKE OVER?", TwinWatch.DescribeStandby(TwinPhase.MainSilent, "MAIN-DESK", now.AddSeconds(-7), 4, false, now));
        Assert.Equal("MAIN MAIN-DESK SILENT for 7 s — taking over…", TwinWatch.DescribeStandby(TwinPhase.MainSilent, "MAIN-DESK", now.AddSeconds(-7), 4, true, now));
        Assert.Equal("TOOK OVER from MAIN-DESK at 19:00:00 — this desk runs the show now. STAND BY AGAIN once MAIN-DESK is back.",
            TwinWatch.DescribeStandby(TwinPhase.TookOver, "MAIN-DESK", null, 4, false, now, "at 19:00:00"));
        Assert.Equal("STANDBY — MAIN-DESK refused the link: wrong key. Check the key on both machines.",
            TwinWatch.DescribeStandby(TwinPhase.Refused, "MAIN-DESK", null, 0, false, now, "wrong key"));

        Assert.Equal("MAIN — listening for a standby on port 9699; none connected.", TwinWatch.DescribeMain(9699, Array.Empty<(string, DateTime)>(), 0, now));
        Assert.Equal("MAIN — standby Backup in step (heard just now) · 12 sections sent.",
            TwinWatch.DescribeMain(9699, new[] { ("Backup", now) }, 12, now));
        Assert.Equal("MAIN — standby Backup in step (heard 2 s ago) · standby Spare SILENT for 9 s · 1 section sent.",
            TwinWatch.DescribeMain(9699, new[] { ("Backup", now.AddSeconds(-2)), ("Spare", now.AddSeconds(-9)) }, 1, now));
    }

    [Fact]
    public void TheVerbsAreTheStandbysOwnAndTheWireKnowsThem()
    {
        Assert.NotNull(ActionSpec.DeskOnly(ShowActionKind.TwinTakeOver));
        Assert.NotNull(ActionSpec.DeskOnly(ShowActionKind.TwinStandBy));
        Assert.DoesNotContain(ShowActionKind.TwinTakeOver, ActionSpec.CueKinds);
        Assert.Equal(new ShowAction(ShowActionKind.TwinTakeOver), ControlProtocol.Parse("TWIN TAKEOVER").Action);
        Assert.Equal(new ShowAction(ShowActionKind.TwinTakeOver), ControlProtocol.Parse("twin take over").Action);
        Assert.Equal(new ShowAction(ShowActionKind.TwinStandBy), ControlProtocol.Parse("TWIN STANDBY").Action);
        Assert.NotNull(ActionSpec.DeskOnly(ShowActionKind.TwinTakeBack));
        Assert.Equal(new ShowAction(ShowActionKind.TwinTakeBack), ControlProtocol.Parse("TWIN TAKE BACK").Action);
        Assert.Equal(RemoteCommandKind.TwinStatus, ControlProtocol.Parse("TWIN STATUS").Kind);
        Assert.Equal(RemoteCommandKind.TwinStatus, ControlProtocol.Parse("TWIN").Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("TWIN DANCE").Kind);
    }

    [Fact]
    public void TheBeaconNamesTheTwinPortAndAnOlderBeaconReadsAsNone()
    {
        var b = new Beacon { Machine = "MAIN-DESK", Instance = "abc", Twin = 9699 };
        Assert.Equal(9699, Beacon.Parse(b.ToJson())!.Twin);
        var older = JsonSerializer.Serialize(new { patterns = 1, machine = "OLD", instance = "x" });
        Assert.Equal(0, Beacon.Parse(older)!.Twin);
    }

    [Fact]
    public void TheCheckRowAndTheTileReadTheTwin()
    {
        Assert.Equal(CheckLight.Green, SuperCheck.TwinLight(TwinPhase.InStep));
        Assert.Equal(CheckLight.Red, SuperCheck.TwinLight(TwinPhase.MainSilent));
        Assert.Equal(CheckLight.Amber, SuperCheck.TwinLight(TwinPhase.TookOver));
        Assert.Equal("MAIN SILENT", SuperCheck.TwinValue(TwinRole.Standby, TwinPhase.MainSilent));
        var facts = new CheckFacts { TwinRole = TwinRole.Standby, TwinPhase = TwinPhase.MainSilent, TwinWords = "MAIN X SILENT for 7 s — TAKE OVER?" };
        var tile = HealthDashboard.Tiles(facts).Single(t => t.Id == "watchdog");
        Assert.Equal(CheckLight.Red, tile.Light);
        Assert.Equal("MAIN SILENT", tile.Value);
        Assert.Equal(12, HealthDashboard.Tiles(facts).Count);                       // the wall keeps its twelve tiles
        var inStep = new CheckFacts { TwinRole = TwinRole.Standby, TwinPhase = TwinPhase.InStep, TwinWords = "STANDBY for X — in step" };
        Assert.Equal("in step", HealthDashboard.Tiles(inStep).Single(t => t.Id == "watchdog").Value);
        Assert.Contains(SuperCheck.Run(inStep).Rows, r => r.Item == "Twin" && r.Light == CheckLight.Green);
        Assert.DoesNotContain(SuperCheck.Run(new CheckFacts()).Rows, r => r.Item == "Twin");
    }
}
