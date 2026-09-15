using Patterns.Core.Menus;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 62: the RUN surface's monitor — one screen drawn large between the wall and the history,
/// the caller's eye. A verb of the one vocabulary (RUN MONITOR n / PGM / MAIN / OFF), so the
/// wire, OSC, a cue and the right-click menu all say the same thing; it changes where the desk
/// looks and never the air, and the show remembers it.
/// </summary>
public class RunMonitorTests
{
    [Fact]
    public void TheWireAndOscCarryTheMonitorWords()
    {
        var lines = new (string Address, string? Arg, string Line, ShowActionKind Kind, string Target)[]
        {
            ("/patterns/run/monitor", "2", "RUN MONITOR 2", ShowActionKind.RunMonitor, "2"),
            ("/patterns/run/monitor/3", null, "RUN MONITOR 3", ShowActionKind.RunMonitor, "3"),
            ("/patterns/run/monitor/pgm", null, "RUN MONITOR PGM", ShowActionKind.RunMonitor, "PGM"),
            ("/patterns/run/monitor", "main", "RUN MONITOR MAIN", ShowActionKind.RunMonitor, "MAIN"),
            ("/patterns/run/monitor", null, "RUN MONITOR MAIN", ShowActionKind.RunMonitor, "MAIN"),
            ("/patterns/run/monitor/off", null, "RUN MONITOR OFF", ShowActionKind.RunMonitorOff, ""),
        };
        foreach (var (address, arg, line, kind, target) in lines)
        {
            var message = arg is null ? OscMessage.Of(address) : OscMessage.Of(address, arg);
            Assert.Equal(line, OscMap.ToLine(message));
            var cmd = ControlProtocol.Parse(line);
            Assert.True(cmd.IsAction, line);
            Assert.Equal(kind, cmd.Action.Kind);
            Assert.Equal(target, cmd.Action.Target);
        }
        Assert.Equal(ShowActionKind.RunMonitor, ControlProtocol.Parse("RUN MONITOR").Action.Kind);
        Assert.Equal("MAIN", ControlProtocol.Parse("run monitor").Action.Target);
        Assert.Equal("PGM", ControlProtocol.Parse("RUN MONITOR PROGRAMME").Action.Target);
        Assert.Equal("a+b", ControlProtocol.Parse("RUN MONITOR a+b").Action.Target);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("RUN").Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("RUN DANCE").Kind);
        Assert.Null(OscMap.ToLine(OscMessage.Of("/patterns/run/dance")));
    }

    [Fact]
    public void ACueMayCarryItAndTheChecksAndTheSummaryReadIt()
    {
        Assert.Contains(ShowActionKind.RunMonitor, ActionSpec.CueKinds);
        Assert.Contains(ShowActionKind.RunMonitorOff, ActionSpec.CueKinds);
        Assert.Equal((TargetKind.Monitor, ValueKind.None), ActionSpec.For(ShowActionKind.RunMonitor));
        Assert.False(ActionSpec.ChangesContent(ShowActionKind.RunMonitor));

        var state = new ShowState();
        state.Output.Placements.Add(new ScreenPlacement { ScreenId = "a", Enabled = true, Role = ScreenRole.Main });
        state.Output.Placements.Add(new ScreenPlacement { ScreenId = "b", Enabled = true });
        Assert.Equal("RUN monitor: the main screen", CueSummary.DescribeAction(state, new CueActionConfig { Kind = ShowActionKind.RunMonitor }));
        Assert.Equal("RUN monitor: the programme", CueSummary.DescribeAction(state, new CueActionConfig { Kind = ShowActionKind.RunMonitor, Target = "PGM" }));
        Assert.Contains("screen", CueSummary.DescribeAction(state, new CueActionConfig { Kind = ShowActionKind.RunMonitor, Target = "b" }));
        Assert.Equal("RUN monitor off", CueSummary.DescribeAction(state, new CueActionConfig { Kind = ShowActionKind.RunMonitorOff }));

        var ctx = CueValidationContext.Default;
        Assert.Equal(0, CueValidator.ValidateOne(state, Cue(ShowActionKind.RunMonitor, ""), ctx).BrokenCount);
        Assert.Equal(0, CueValidator.ValidateOne(state, Cue(ShowActionKind.RunMonitor, "PGM"), ctx).BrokenCount);
        Assert.Equal(0, CueValidator.ValidateOne(state, Cue(ShowActionKind.RunMonitor, "b"), ctx).BrokenCount);
        Assert.Equal(0, CueValidator.ValidateOne(state, Cue(ShowActionKind.RunMonitorOff, ""), ctx).BrokenCount);
        Assert.Equal(1, CueValidator.ValidateOne(state, Cue(ShowActionKind.RunMonitor, "nowhere"), ctx).BrokenCount);
    }

    [Fact]
    public void TheMenuOffersEveryScreenTheProgrammeAndHideAndTicksWhatItShows()
    {
        var facts = new DeskFacts();
        var choices = new[] { new MonitorChoice("a", "1", "1 · Main wall"), new MonitorChoice("b", "2", "2 · Stage left"), new MonitorChoice("a+b", "", "A · Main wall") };
        var menu = DeskMenus.Monitor(facts, new MonitorFacts { Current = "b", MainTargetId = "a", MainTitle = "1 · Main wall", Choices = choices });
        Assert.Equal("monitor", menu.Kind);
        Assert.True(menu.Find("monitor.main")!.IsEnabled);
        Assert.False(menu.Find("monitor.main")!.IsOn);
        Assert.True(menu.Find("monitor.b")!.IsOn);
        Assert.Equal("RUN MONITOR 2", menu.Find("monitor.b")!.Wire);
        Assert.Equal("RUN MONITOR a+b", menu.Find("monitor.a+b")!.Wire);
        Assert.Equal("RUN MONITOR PGM", menu.Find("monitor.pgm")!.Wire);
        Assert.Equal("RUN MONITOR OFF", menu.Find("monitor.off")!.Wire);
        Assert.Equal(ShowActionKind.RunMonitorOff, menu.Find("monitor.off")!.Action!.Value.Kind);
        Assert.Equal("Screens", menu.Find("go.screens")!.Route!.Page);
        Assert.Equal("b", menu.Find("go.screens")!.Route!.Item);
        Assert.Contains("2 · Stage left", menu.Subtitle);

        var hidden = DeskMenus.Monitor(facts, new MonitorFacts { Current = "OFF", MainTargetId = "a", Choices = choices });
        Assert.True(hidden.Find("monitor.off")!.IsOn);
        Assert.Contains("hidden", hidden.Subtitle);
        var main = DeskMenus.Monitor(facts, new MonitorFacts { MainTargetId = "a", MainTitle = "1 · Main wall", Choices = choices });
        Assert.True(main.Find("monitor.main")!.IsOn);
        Assert.Equal("a", main.Find("go.screens")!.Route!.Item);
        var empty = DeskMenus.Monitor(facts, new MonitorFacts());
        Assert.False(empty.Find("monitor.main")!.IsEnabled);
    }

    private static RunCueConfig Cue(ShowActionKind kind, string target)
    {
        var cue = new RunCueConfig { Number = "1", Name = "Watch" };
        cue.Actions.Add(new CueActionConfig { Kind = kind, Target = target });
        return cue;
    }
}
