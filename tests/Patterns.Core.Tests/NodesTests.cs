using System.Net;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>The nodes: the beacon's kind and ports, the registry's cards and words, the caller's join and words, the plan's diff and merge, the stage timer's arithmetic.</summary>
public class NodesTests
{
    [Fact]
    public void TheBeaconCarriesItsKindAndPortsAndAnOlderBeaconReadsAsADesk()
    {
        var caller = new Beacon { Machine = "CALLER-PC", Instance = "c1", Kind = "caller", Http = 9696, Wire = 9697, Link = 0, Show = "Gala" };
        var back = Beacon.Parse(caller.ToJson())!;
        Assert.Equal("caller", back.Kind);
        Assert.Equal(9696, back.Http);
        Assert.Equal(NodeKind.Caller, NodeKinds.Parse(back.Kind));

        var old = Beacon.Parse("{\"patterns\":1,\"machine\":\"OLD-PC\",\"instance\":\"o1\",\"seq\":3}")!;
        Assert.Equal("", old.Kind);
        Assert.Equal(NodeKind.Desk, NodeKinds.Parse(old.Kind));

        Assert.Equal(NodeKind.Caller, NodeKinds.ParseLaunch("caller"));
        Assert.Equal(NodeKind.Arcade, NodeKinds.ParseLaunch("ARCADE"));
        Assert.Null(NodeKinds.ParseLaunch("toaster"));
        Assert.Null(NodeKinds.ParseLaunch(""));
        Assert.Equal("caller", NodeKinds.Wire(NodeKind.Caller));
        Assert.Contains("never opens outputs", NodeKinds.HoldWords(NodeKind.Caller));
        Assert.Equal("", NodeKinds.HoldWords(NodeKind.Desk));
        Assert.Null(NodeKinds.Pages(NodeKind.Desk));
        Assert.Equal(new[] { "Run", "Cues", "Stage", "Nodes" }, NodeKinds.Pages(NodeKind.Caller));
        Assert.Equal(new[] { "Stage", "Nodes" }, NodeKinds.Pages(NodeKind.Timer));
    }

    [Fact]
    public void TheRegistryOrdersTheCardsSaysWhenEachWasHeardAndWordsTheRail()
    {
        var now = new DateTime(2026, 9, 13, 10, 0, 0, DateTimeKind.Utc);
        var from = IPAddress.Parse("10.0.0.12");
        var desk = NodeRegistry.Card(new Beacon { Instance = "d1", Kind = "desk", Machine = "FOH-PC", Show = "Gala", Live = true, Link = 9699, Http = 9696, Health = "OK" }, from, now.AddSeconds(-1), now);
        var caller = NodeRegistry.Card(new Beacon { Instance = "c1", Kind = "caller", Machine = "CALLER-PC", Show = "Gala" }, IPAddress.Parse("10.0.0.20"), now.AddSeconds(-4), now);
        var arcade = NodeRegistry.Card(new Beacon { Instance = "a1", Kind = "arcade", Machine = "HUB-PC", Http = 9696 }, IPAddress.Parse("10.0.0.30"), now.AddSeconds(-40), now);
        var gone = NodeRegistry.Card(new Beacon { Instance = "g1", Kind = "desk", Machine = "OLD-PC" }, null, now.AddMinutes(-5), now);

        var cards = NodeRegistry.Order(new[] { arcade, caller, desk, gone });
        Assert.Equal(new[] { "FOH-PC", "CALLER-PC", "HUB-PC" }, cards.Select(c => c.Name));      // desks first, then callers, arcades; the forgotten one gone
        Assert.True(desk.Fresh);
        Assert.True(caller.Fresh);
        Assert.False(arcade.Fresh);
        Assert.Equal("Desk FOH-PC — Gala · live · heard just now", desk.Line);
        Assert.Equal("Caller CALLER-PC — Gala · heard 4 s ago", caller.Line);
        Assert.EndsWith("heard 40 s ago · GONE?", arcade.Line);
        Assert.Equal("http://10.0.0.12:9696/", desk.PagesUrl);
        Assert.Equal("", caller.PagesUrl);

        Assert.Equal("NONE", NodeRegistry.RailWord(0, 0));
        Assert.Equal("2 NEAR", NodeRegistry.RailWord(2, 0));
        Assert.Equal("1 LINKED", NodeRegistry.RailWord(2, 1));
        Assert.StartsWith("No other Patterns heard", NodeRegistry.RailLine(Array.Empty<NodeCard>(), 0));
        Assert.Equal("1 desk, 1 caller on the beacon · 1 linked to this desk.", NodeRegistry.RailLine(cards, 1));
    }

    [Fact]
    public void ACallerJoinsAsACallerAndItsLineSaysWhereItStands()
    {
        var join = new TwinJoin("Caller", "CALLER-PC", "c1", "hunter2", Kind: "caller");
        Assert.True(join.IsCaller);
        var back = TwinJoin.Parse(join.ToJson())!;
        Assert.True(back.IsCaller);
        Assert.False(TwinJoin.Parse(new TwinJoin("Backup", "PC", "s1", "hunter2").ToJson())!.IsCaller);   // the default is a standby
        Assert.False(TwinJoin.Parse("{\"Name\":\"Old\",\"Machine\":\"PC\",\"Instance\":\"x\",\"Key\":\"k\"}")!.IsCaller);   // and so is a join from before nodes

        var live = TwinLive.Parse(new TwinLive("cue-2", "cue-1", true, false, false, "Walk-in", true, false, "ON TIME", 7).ToJson())!;
        Assert.Equal(("cue-2", "cue-1", true, "Walk-in", 7L), (live.Standby, live.Last, live.Armed, live.AirLabel, live.Seq));

        var now = new DateTime(2026, 9, 13, 19, 0, 0, DateTimeKind.Utc);
        Assert.Equal("CALLER — planning alone; LINK on the Nodes page joins a desk.", TwinWatch.DescribeCaller(TwinPhase.Off, "", null, 0, now));
        Assert.Equal("CALLER — connecting to FOH-PC…", TwinWatch.DescribeCaller(TwinPhase.Connecting, "FOH-PC", null, 0, now));
        Assert.Equal("CALLER for FOH-PC — in step, heard just now, 3 sections mirrored · on air there: Walk-in · GO, STANDBY and HOLD from here run there.",
            TwinWatch.DescribeCaller(TwinPhase.InStep, "FOH-PC", now.AddSeconds(-1), 3, now, airLabel: "Walk-in"));
        Assert.Equal("DESK FOH-PC SILENT for 8 s — calling waits; the cues stay here.", TwinWatch.DescribeCaller(TwinPhase.MainSilent, "FOH-PC", now.AddSeconds(-8), 3, now));
        Assert.StartsWith("CALLER — FOH-PC refused the link: wrong key.", TwinWatch.DescribeCaller(TwinPhase.Refused, "FOH-PC", null, 0, now, "wrong key"));

        Assert.True(TwinSync.IsCallerSection(nameof(ShowState.Stacks)));
        Assert.False(TwinSync.IsCallerSection(nameof(ShowState.Pattern)));
        Assert.False(TwinSync.IsCallerSection(nameof(ShowState.Twin)));
    }

    [Fact]
    public void EveryTwinWordReadsBackOffTheWire()
    {
        // A word Format writes that Parse does not know is a message the peer silently drops — so every word round-trips.
        foreach (var word in Enum.GetValues<TwinWord>())
        {
            if (word == TwinWord.Unknown) continue;
            var line = TwinMessage.Format(word, "{\"a\":1}", word == TwinWord.Section ? "Stacks" : "");
            var back = TwinMessage.Parse(line);
            Assert.Equal(word, back.Word);
            Assert.Equal("{\"a\":1}", back.Payload);
        }
        Assert.Equal(TwinWord.Plan, TwinMessage.Parse("PLAN [ ]").Word);
        Assert.Equal(TwinWord.Live, TwinMessage.Parse("live {}").Word);
        Assert.Equal(TwinWord.Act, TwinMessage.Parse("ACT {\"Kind\":\"CueGo\"}").Word);
    }

    [Fact]
    public void ThePlanIsDiffedAgainstTheDeskAndMergedOntoIt()
    {
        var desk = new ShowState();
        var mine = CueStacks.Caller(desk);
        var same = new RunCueConfig { Number = "01", Name = "Walk-in" };
        var changed = new RunCueConfig { Number = "02", Name = "Welcome" };
        var onlyMine = new RunCueConfig { Number = "03", Name = "Break" };
        mine.Cues.Add(same);
        mine.Cues.Add(changed);
        mine.Cues.Add(onlyMine);

        var home = new ShowState();
        var plan = CueStacks.Caller(home);
        plan.Scratchpad = "Doors 18:30";
        plan.Cues.Add(new RunCueConfig { Id = same.Id, Number = "01", Name = "Walk-in" });
        plan.Cues.Add(new RunCueConfig { Id = changed.Id, Number = "02", Name = "Welcome and safety" });
        plan.Cues.Add(new RunCueConfig { Number = "04", Name = "Keynote" });
        Assert.Equal("3 cues in 1 stack", CuePlan.Count(home.Stacks));

        var diff = CuePlan.Diff(desk.Stacks, home.Stacks);
        Assert.Equal((1, 1, 1, 1), (diff.Added, diff.Changed, diff.Removed, diff.Unchanged));
        Assert.Equal($"1 cue to add, 1 changed, 1 the desk has that the plan does not — in {plan.Name}", diff.Words);
        Assert.False(diff.IsEmpty);
        Assert.True(CuePlan.Diff(desk.Stacks, desk.Stacks).IsEmpty);
        Assert.Equal("the same 3 cues the desk has", CuePlan.Diff(desk.Stacks, desk.Stacks).Words);

        // Over the wire and back, then onto the desk: the plan's stack replaces the desk's match, the pad with it.
        var parsed = CuePlan.Parse(CuePlan.Json(home))!;
        Assert.Equal(1, CuePlan.Merge(desk, parsed));
        Assert.Equal(new[] { "Walk-in", "Welcome and safety", "Keynote" }, CueStacks.Caller(desk).Cues.Select(c => c.Name));
        Assert.Equal("Doors 18:30", CueStacks.Caller(desk).Scratchpad);
        Assert.Same(mine, CueStacks.Caller(desk));                                       // the same stack object: the desk's runtime and bindings keep it
        Assert.Null(CuePlan.Parse("not a plan"));

        // A plan with two lists of one role: the one the desk knows (by name) lands on it; the other
        // is added beside it — the role alone is no match when the plan carries two of them.
        var extra = new ShowState();
        CueStacks.Caller(extra).Cues.Add(new RunCueConfig { Id = same.Id, Number = "01", Name = "Walk-in" });
        extra.Stacks.Add(new CueStackConfig { Name = "Lighting", Role = StackRole.Caller });
        extra.Stacks[^1].Cues.Add(new RunCueConfig { Number = "1", Name = "Preset" });
        Assert.Equal(2, CuePlan.Merge(desk, extra.Stacks));
        Assert.Equal(2, desk.Stacks.Count(s => s.Role == StackRole.Caller));
        Assert.Same(mine, CueStacks.Caller(desk));
        Assert.Equal(new[] { "Walk-in" }, mine.Cues.Select(c => c.Name));
        Assert.Contains(desk.Stacks, s => s.Name == "Lighting" && s.Cues.Count == 1);
    }

    [Fact]
    public void TheStageTimerColoursPausesResumesAndNudgesOnTheCountdownsClock()
    {
        var utc = new DateTime(2026, 9, 13, 18, 0, 0, DateTimeKind.Utc);
        var local = utc.ToLocalTime();
        var countdown = new CountdownConfig { Enabled = true, TargetKind = CountdownTargetKind.Duration, DurationMinutes = 10, ArmedAtUtc = utc };
        var stage = new StageConfig();

        Assert.Equal(StageTime.Idle, StageTimer.Evaluate(new CountdownConfig(), stage, local, utc));
        var t = StageTimer.Evaluate(countdown, stage, local, utc);
        Assert.Equal(StageTimerPhase.Running, t.Phase);
        Assert.Equal(600, t.RemainingSeconds, 0.5);
        Assert.Equal("green", t.Colour);
        Assert.Equal("10:00", t.Text);
        Assert.Equal("amber", StageTimer.Evaluate(countdown, stage, local.AddSeconds(500), utc.AddSeconds(500)).Colour);   // 100 s left
        Assert.Equal("red", StageTimer.Evaluate(countdown, stage, local.AddSeconds(570), utc.AddSeconds(570)).Colour);     // 30 s left
        var over = StageTimer.Evaluate(countdown, stage, local.AddSeconds(642), utc.AddSeconds(642));
        Assert.Equal(StageTimerPhase.Over, over.Phase);
        Assert.Equal(-42, over.RemainingSeconds, 0.5);
        Assert.Equal("-0:42", over.Text);
        Assert.Equal("red", over.Colour);

        // PAUSE at 7:00 left keeps 7:00 and stops the clock; RESUME runs 7:00 from then.
        Assert.True(StageTimer.Pause(countdown, stage, local.AddSeconds(180), utc.AddSeconds(180)));
        Assert.True(stage.Paused);
        Assert.False(countdown.Enabled);
        Assert.Equal(420, stage.PausedRemainingSeconds, 0.5);
        Assert.Equal("7:00", StageTimer.Evaluate(countdown, stage, local.AddSeconds(900), utc.AddSeconds(900)).Text);   // still 7:00 an hour later
        Assert.False(StageTimer.Pause(countdown, stage, local, utc));                                                      // nothing running to pause
        Assert.True(StageTimer.Add(countdown, stage, 60, local, utc));                                                     // +1 min while paused
        Assert.Equal(480, stage.PausedRemainingSeconds, 0.5);
        Assert.True(StageTimer.Resume(countdown, stage, utc.AddSeconds(1000)));
        Assert.False(stage.Paused);
        Assert.True(countdown.Enabled);
        Assert.Equal("8:00", StageTimer.Evaluate(countdown, stage, local.AddSeconds(1000), utc.AddSeconds(1000)).Text);
        Assert.False(StageTimer.Resume(countdown, stage, utc));

        // A nudge on a running duration, and on a time of day.
        Assert.True(StageTimer.Add(countdown, stage, -30, local.AddSeconds(1000), utc.AddSeconds(1000)));
        Assert.Equal("7:30", StageTimer.Evaluate(countdown, stage, local.AddSeconds(1000), utc.AddSeconds(1000)).Text);
        var clock = new CountdownConfig { Enabled = true, TargetKind = CountdownTargetKind.TimeOfDay, TargetTime = "20:00" };
        Assert.True(StageTimer.Add(clock, stage, 90, local, utc));
        Assert.Equal("20:01", clock.TargetTime);

        Assert.Equal(60, StageTimer.ParseSeconds("+60"));
        Assert.Equal(-30, StageTimer.ParseSeconds("-30"));
        Assert.Equal(120, StageTimer.ParseSeconds("2m"));
        Assert.Equal(-600, StageTimer.ParseSeconds("-10 min"));
        Assert.Equal(45, StageTimer.ParseSeconds("45s"));
        Assert.Null(StageTimer.ParseSeconds("soon"));
        Assert.Equal("1:02:03", StageTimer.Format(3723));
    }
}
