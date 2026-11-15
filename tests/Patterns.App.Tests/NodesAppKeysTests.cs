using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Show Bus 1.3 §13.5 and §13.16.6 on the desk: the Nodes page's APP KEYS box and a key row's right-click menu press the same KEY verb,
/// one journal row each from the desk; the key is shown once and hidden for good; every origin but the desk is refused in words; the
/// assistant's brief names each key by its kid and says the verbs are the operator's, never the key.
/// </summary>
public class NodesAppKeysTests
{
    [AvaloniaFact]
    public void MakeKeyShowsTheKeyOnceAndTheRowsMenuRevokesItInStepWithItsButton()
    {
        var b = TestApp.Boot();
        try
        {
            Run(b.Vm, b.Services);
        }
        finally
        {
            b.Dispose();
        }
    }

    private static void Run(MainViewModel vm, AppServices services)
    {
        var panel = vm.AppKeys;
        panel.MakeFor = "main:3f9c2a1b7d0e";
        panel.MakeScope = "drive";
        panel.MakeCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.True(panel.HasShown);
        var key = panel.Shown;
        Assert.True(LinkAcceptor.IsKeyShape(key), key);
        Assert.Null(services.Kernel.AppKeys.TakeShown());                                       // the box took it; the store holds it no more
        var row = Assert.Single(panel.Rows);
        Assert.Equal(("main:3f9c2a1b7d0e", "drive", "✓"), (row.Bound, row.Scope, row.Mark));
        Assert.DoesNotContain(key, row.Words, StringComparison.Ordinal);
        Assert.DoesNotContain(key, vm.StatusMessage ?? "", StringComparison.Ordinal);
        panel.HideShownCommand.Execute(null);
        Assert.False(panel.HasShown);
        Assert.Equal("", panel.Shown);

        // The brief names the key by its kid and the verbs as the operator's; never the key.
        var facts = services.GatherFacts().ShowNetwork;
        Assert.Contains(facts, l => l.StartsWith("App key " + row.Kid, StringComparison.Ordinal));
        Assert.Contains(facts, l => l.Contains(KeyVerbs.BriefWords, StringComparison.Ordinal));
        Assert.All(facts, l => Assert.DoesNotContain(key, l, StringComparison.Ordinal));

        // Every origin but the desk is refused, and nothing changes.
        foreach (var origin in new[] { new ActionOrigin(OriginKind.Companion, "FOH deck"), new ActionOrigin(OriginKind.Tcp, StagewatchVerbs.ApprovalDesk), ActionOrigin.Schedule, new ActionOrigin(OriginKind.Osc, "", "10.0.0.9") })
        {
            var refused = services.Actions.Execute(new ShowAction(ShowActionKind.AppKeyRevoke, row.Kid), origin);
            Assert.Equal(ActionStatus.Refused, refused.Status);
            Assert.StartsWith(KeyVerbs.DeskOnlyWords, refused.Message, StringComparison.Ordinal);
        }
        Assert.Single(services.Kernel.AppKeys.Keys);

        // The row's menu: its scope raised, then Revoke — the button's own action, one journal row each from the desk.
        var menu = vm.MenuFor("appkey", row)!;
        Assert.Equal("appkey", menu.Menu.Kind);
        menu.Find("key.scope.operate")!.ChooseCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal("operate", services.Kernel.AppKeys.Keys.Single().Scope);
        vm.MenuFor("appkey", Assert.Single(panel.Rows))!.Find("key.revoke")!.ChooseCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Empty(services.Kernel.AppKeys.Keys);
        panel.Look();
        Assert.Empty(panel.Rows);
        TestApp.FlushFiles(services);
        var rows = services.Journal.Tail(50).Where(e => e.Kind.StartsWith("AppKey", StringComparison.Ordinal)).Select(e => (e.Kind, e.Origin, e.Outcome)).ToList();
        Assert.Contains((nameof(ShowActionKind.AppKeyMake), "desk", "Done"), rows);
        Assert.Contains((nameof(ShowActionKind.AppKeyBind), "desk", "Done"), rows);
        Assert.Contains((nameof(ShowActionKind.AppKeyRevoke), "desk", "Done"), rows);
        Assert.Equal(4, rows.Count(r => r.Outcome == "Refused"));
        Assert.DoesNotContain(key, File.ReadAllText(services.Journal.Path), StringComparison.Ordinal);

        // Speak for another connector opens the BIND boxes with the key in them, as the row's BIND… press does.
        panel.MakeFor = "playmap:0a1b2c3d";
        panel.MakeCommand.Execute(null);
        panel.HideShownCommand.Execute(null);
        var made = Assert.Single(panel.Rows);
        vm.MenuFor("appkey", made)!.Find("key.bind")!.ChooseCommand.Execute(null);
        Dispatcher.UIThread.RunJobs();
        Assert.Equal((made.Kid, "playmap:0a1b2c3d"), (panel.BindKid, panel.BindFor));
        panel.BindFor = "main:3f9c2a1b7d0e";
        panel.BindCommand.Execute(null);
        Assert.Equal("main:3f9c2a1b7d0e", services.Kernel.AppKeys.Keys.Single().Bound);
    }
}
