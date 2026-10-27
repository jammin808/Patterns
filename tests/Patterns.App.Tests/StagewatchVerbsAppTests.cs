using System.Text;
using System.Text.Json;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;
using static Patterns.App.Tests.ShowBus12AppTests;

namespace Patterns.App.Tests;

/// <summary>
/// G34, Show Bus 1.3 §13.16.6: the Stagewatch link set and followed from the wire, a deck and the menus, with TRUST,
/// ACCEPT and the key a person's on the desk alone. Real sockets against the scripted Stagewatch on loopback: a LINK from a line never
/// sends the key to a target it chose; the key binds to the Stagewatch whose first answer took it and goes to no other; BIND KEY is a
/// press on the desk; each verb is one journal row, STATE's row and STAGEWATCH STATUS say <c>follow</c>, <c>pending</c> and
/// <c>keyFor</c>, and the card's right-click entries are the wire's own lines. A key held back by its binding is set and held back in
/// the link's one record, so every reader says so; FOLLOW moves the record's light at once; LINK AGAIN ends what is held and the
/// redial's wait without a new run, and is refused, with the reason, when it would change nothing.
/// </summary>
public class StagewatchVerbsAppTests
{
    private static readonly ActionOrigin Deck = new(OriginKind.Tcp, "", "10.0.0.12:51234");

    private static JsonElement Row(AppServices services)
        => JsonDocument.Parse(new CommandRouter(services).StateJson()).RootElement.GetProperty("bus").GetProperty("stagewatch");

    private static string Wire(CommandRouter router, string line, ActionOrigin? origin = null) => TestApp.Pump(router.ExecuteAsync(ControlProtocol.Parse(line), origin ?? Deck));

    /// <summary>A hello from a Stagewatch with this id and name, as the desk's listener would hear it.</summary>
    private static void HearAs(AppServices services, string id, string name, int port)
    {
        var hello = JsonSerializer.Serialize(new { t = "main", v = 1, proto = 1, id, name, ports = new { http = port, ws = port, https = 0 }, caps = Array.Empty<string>() });
        services.Kernel.Beacon.ShowBus.Hear(Encoding.UTF8.GetBytes(hello), "127.0.0.1", DateTime.UtcNow, "not-this-process");
    }

    /// <summary>
    /// The wire sets the link to an address it names: on, at once — and the key, bound to nothing yet, goes nowhere a line chose; the
    /// public view is read, the link's record says the key is set and held back, and BIND KEY on the desk sends it there. FOLLOW moves the
    /// stage, from the wire or the Nodes page's box; FLOOR LOWER, RELEASE, LINK AGAIN on a live link nothing holds and a take that would
    /// change nothing are refused with the reason; TRUST, ACCEPT and KEY are refused off the desk with nothing of the line carried; an
    /// approval desk changes nothing; each is one journal row and the key rides none of them. A key loaded with its binding keeps it; a
    /// key typed anew starts bound to nothing.
    /// </summary>
    [AvaloniaFact]
    public void TheWireSetsAndFollowsTheLinkAndTheKeyNeverGoesToATargetALineChose()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        var sw = new FakeStagewatch(Frame, null);
        var link = services.Kernel.Stagewatch;
        var config = vm.State.Control.Stagewatch;
        var router = new CommandRouter(services);
        try
        {
            config.KeyFor = "node:a1b2c3d4";
            vm.LinkBoxes.Key = "swk_nodekey";                                                      // typed on the Nodes page
            Dispatcher.UIThread.RunJobs();
            link.Reconcile();
            Assert.Equal("", config.KeyFor);                                                      // typed anew: unbound, whatever was bound before

            Assert.Equal("OK", Wire(router, "STAGEWATCH LINK 127.0.0.1:" + sw.Port));
            var linked = services.Journal.Tail(300).Last(e => e.Kind == nameof(ShowActionKind.StagewatchLinkSet));
            Assert.Equal(("tcp 10.0.0.12:51234", "127.0.0.1:" + sw.Port, "Done"), (linked.Origin, linked.Target, linked.Outcome));
            Assert.Contains("dialling without the key", linked.Message, StringComparison.Ordinal);
            Assert.True(config.Enabled);
            Assert.Equal("127.0.0.1:" + sw.Port, config.Host);
            Assert.Equal(StagewatchKeyFor.None, config.KeyFor);
            var target = StagewatchKeyFor.Address("127.0.0.1", sw.Port);
            PumpUntil(() => services.Stage.Following.Length > 0 && link.KeyBindTarget == target);
            Assert.Empty(sw.Authorizations);                                                       // the public view, and no key
            Assert.Contains("not sent to", link.Status, StringComparison.Ordinal);
            // 13.16.2: a key set and held back — every reader takes it from the link's one record: keyless, the kid, the binding in words.
            var heldBack = link.Record;
            Assert.Contains("keyless", heldBack.Flags);
            Assert.Equal(PeerLink.Kid(PeerLink.KeyKey("swk_nodekey")), heldBack.Kid);
            Assert.Contains("this desk's key is bound to no Stagewatch and is not sent here", heldBack.Words, StringComparison.Ordinal);
            Assert.Equal(heldBack.Words, Row(services).GetProperty("link").GetProperty("words").GetString());
            Assert.StartsWith("ERR The Stagewatch link follows 127.0.0.1:" + sw.Port + " already", Wire(router, "STAGEWATCH LINK 127.0.0.1:" + sw.Port), StringComparison.Ordinal);

            // STATE and the read say the same: on, the stage following, nothing pending, the key bound to nothing.
            var row = Row(services);
            Assert.True(row.GetProperty("follow").GetBoolean());
            Assert.Equal("", row.GetProperty("pending").GetString());
            Assert.Equal(StagewatchKeyFor.None, row.GetProperty("keyFor").GetString());
            var status = Wire(router, "STAGEWATCH STATUS");
            Assert.StartsWith("OK {", status, StringComparison.Ordinal);
            using (var read = JsonDocument.Parse(status[3..]))
            {
                Assert.True(read.RootElement.GetProperty("on").GetBoolean());
                Assert.Equal(StagewatchKeyFor.None, read.RootElement.GetProperty("keyFor").GetString());
                Assert.True(read.RootElement.GetProperty("follow").GetBoolean());
            }

            // The key's binding is a person's: the wire's KEY carries nothing and is refused; the desk's press binds it and it goes.
            var keyLine = Wire(router, "STAGEWATCH KEY " + target);
            Assert.StartsWith("ERR " + StagewatchVerbs.KeyWords, keyLine, StringComparison.Ordinal);
            Assert.Equal(StagewatchKeyFor.None, config.KeyFor);
            Assert.False(services.Actions.Execute(StagewatchLinkActions.BindAction("addr:10.0.0.99:9924"), ActionOrigin.Desk).Ok);   // a press drawn for another
            var bind = services.Actions.Execute(StagewatchLinkActions.BindAction(target), ActionOrigin.Desk);
            Assert.True(bind.Ok, bind.Message);
            Assert.Equal(target, config.KeyFor);
            PumpUntil(() => sw.Authorization == "Bearer swk_nodekey");
            Assert.Equal(target, Row(services).GetProperty("keyFor").GetString());
            var binds = services.Journal.Tail(300).Where(e => e.Kind == nameof(ShowActionKind.StagewatchKeyBind)).Select(e => (e.Origin, e.Target, e.Outcome)).ToList();
            Assert.Equal(new[] { ("tcp 10.0.0.12:51234", "", "Refused"), ("desk", "addr:10.0.0.99:9924", "Refused"), ("desk", target, "Done") }, binds);

            // FOLLOW: the stage runs this desk's own timer; said again, refused with the reason.
            Assert.StartsWith("OK", Wire(router, "STAGEWATCH FOLLOW OFF"), StringComparison.Ordinal);
            Assert.False(config.FollowStage);
            Assert.False(vm.LinkBoxes.Follow);                                                     // the Nodes page's box follows a deck's change
            Assert.False(Row(services).GetProperty("follow").GetBoolean());
            Assert.StartsWith("ERR The stage runs this desk's own timer already", Wire(router, "STAGEWATCH FOLLOW OFF"), StringComparison.Ordinal);
            vm.LinkBoxes.Follow = true;                                                            // the box is the same verb, from the desk
            Assert.True(config.FollowStage);
            Assert.Equal(("desk", "ON", "Done"), services.Journal.Tail(300).Where(e => e.Kind == nameof(ShowActionKind.StagewatchFollow)).Select(e => (e.Origin, e.Target, e.Outcome)).Last());

            // Nothing held, no floor at level 0: each refused with its reason, never a silent OK — from the wire and the Nodes page alike.
            Assert.StartsWith("ERR " + StagewatchVerbs.NothingHeldWords, Wire(router, "STAGEWATCH RELEASE"), StringComparison.Ordinal);
            vm.LinkBoxes.ReleaseCommand.Execute(null);
            Assert.Equal(("desk", "Refused"), services.Journal.Tail(300).Where(e => e.Kind == nameof(ShowActionKind.StagewatchRelease)).Select(e => (e.Origin, e.Outcome)).Last());
            Assert.StartsWith("ERR " + StagewatchVerbs.NoFloorWords, Wire(router, "STAGEWATCH FLOOR LOWER"), StringComparison.Ordinal);
            Assert.False(vm.LinkBoxes.HasFloor);

            // LINK AGAIN on a live link that nothing holds would change nothing: refused with the reason, and the link is not dropped.
            PumpUntil(() => link.Record.State == "live");
            var (upgrades, attempt) = (sw.Upgrades, link.Record.Attempt);
            Assert.StartsWith("ERR The Stagewatch link is up and nothing of this desk's key is held", Wire(router, "STAGEWATCH LINK AGAIN"), StringComparison.Ordinal);
            ShowBus12AppTests.PumpFor(1500);
            Assert.Equal(upgrades, sw.Upgrades);
            Assert.True(services.Stage.Following.Length > 0);
            Assert.Equal(("live", attempt), (link.Record.State, link.Record.Attempt));

            // TRUST and ACCEPT: a person's on the desk; the wire's words carry nothing and are refused, a pad cannot learn them.
            Assert.StartsWith("ERR " + StagewatchVerbs.DeskOnlyWords, Wire(router, "STAGEWATCH TRUST sha256:" + new string('a', 64)), StringComparison.Ordinal);
            Assert.StartsWith("ERR " + StagewatchVerbs.DeskOnlyWords, Wire(router, "STAGEWATCH ACCEPT 8e8bc45b"), StringComparison.Ordinal);
            Assert.Contains(StagewatchVerbs.DeskOnlyWords, Wire(router, "MIDI LEARN STAGEWATCH ACCEPT"), StringComparison.Ordinal);
            Assert.DoesNotContain(services.Journal.Tail(300), e => e.Target.Contains("8e8bc45b", StringComparison.Ordinal) || e.Message.Contains("8e8bc45b", StringComparison.Ordinal));

            // An approval desk's approved line never repoints the link.
            var approved = Wire(router, "STAGEWATCH LINK OFF", new ActionOrigin(OriginKind.Tcp, StagewatchVerbs.ApprovalDesk));
            Assert.StartsWith("ERR " + StagewatchVerbs.ApprovalDeskWords, approved, StringComparison.Ordinal);
            Assert.True(config.Enabled);

            // LINK OFF from the wire: off, at once; said again, refused.
            Assert.Equal("OK", Wire(router, "STAGEWATCH LINK OFF"));
            Assert.False(config.Enabled);
            Assert.StartsWith("ERR The Stagewatch link is off already", Wire(router, "STAGEWATCH LINK OFF"), StringComparison.Ordinal);
            Assert.False(vm.LinkBoxes.On);
            Assert.StartsWith("ERR The Stagewatch link is off", Wire(router, "STAGEWATCH LINK AGAIN"), StringComparison.Ordinal);
            Assert.False(Row(services).GetProperty("on").GetBoolean());
            var links = services.Journal.Tail(300).Where(e => e.Kind == nameof(ShowActionKind.StagewatchLinkSet)).Select(e => (e.Target, e.Outcome)).ToList();
            Assert.Equal(new[] { ("127.0.0.1:" + sw.Port, "Done"), ("127.0.0.1:" + sw.Port, "Refused"), ("OFF", "Refused"), ("OFF", "Done"), ("OFF", "Refused") }, links);

            // A key read with its binding — the settings, a show loaded field by field — keeps it: a line's none stays none.
            config.KeyFor = StagewatchKeyFor.None;
            config.ApiKey = "swk_loadedkey";
            link.Reconcile();
            Assert.Equal(StagewatchKeyFor.None, config.KeyFor);
            // A key typed anew on the Nodes page is bound to nothing until it binds: it follows the target the settings name.
            vm.LinkBoxes.Key = "swk_otherkey";
            link.Reconcile();
            Assert.Equal("", config.KeyFor);

            // The key in no log line, journal row, STATE or context.
            TestApp.FlushFiles(services);
            foreach (var key in new[] { "swk_nodekey", "swk_loadedkey", "swk_otherkey" })
            {
                Assert.DoesNotContain(key, File.ReadAllText(Path.Combine(b.Dir, "patterns.log")), StringComparison.Ordinal);
                Assert.DoesNotContain(key, File.ReadAllText(services.Journal.Path), StringComparison.Ordinal);
                Assert.DoesNotContain(key, new CommandRouter(services).StateJson(), StringComparison.Ordinal);
                Assert.DoesNotContain(key, services.ContextJson(), StringComparison.Ordinal);
            }
        }
        finally
        {
            config.Enabled = false;
            link.Reconcile();
            Dispatcher.UIThread.RunJobs();
            sw.Dispose();
            b.Dispose();
        }
    }

    /// <summary>
    /// A key typed on the desk and a link the desk's own settings set: the key goes, and its first 2xx binds it to that Stagewatch.
    /// The desk then links to another heard Stagewatch by its id — from a line — and the key stays where it was bound: the other gets
    /// none, is read in its public view, and the link's one record says so — keyless with the kid, where the key is bound — in STATE, the
    /// context and the assistant's brief, which names the link's verbs as the operator's and proposes none. The card's menu offers BIND
    /// KEY, a press with no line, beside the wire's own lines and Release, greyed with its reason.
    /// </summary>
    [AvaloniaFact]
    public void AKeyBindsToTheStagewatchWhoseFirstAnswerTookItAndTheMenusPressTheWiresVerbs()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        var first = new FakeStagewatch(Frame, null);
        var second = new FakeStagewatch(Frame, null);
        var link = services.Kernel.Stagewatch;
        var config = vm.State.Control.Stagewatch;
        var router = new CommandRouter(services);
        try
        {
            config.ApiKey = "swk_nodekey";
            Dispatcher.UIThread.RunJobs();
            link.Reconcile();

            // The desk's own LINK: the key goes; its first answer binds it.
            var on = services.Actions.Execute(new ShowAction(ShowActionKind.StagewatchLinkSet, "", "127.0.0.1:" + first.Port), ActionOrigin.Desk);
            Assert.True(on.Ok, on.Message);
            Assert.Equal("", config.KeyFor);
            var firstTarget = StagewatchKeyFor.Address("127.0.0.1", first.Port);
            PumpUntil(() => config.KeyFor == firstTarget && first.Authorization == "Bearer swk_nodekey");
            Assert.False(first.PostedContexts.IsEmpty);

            // Another Stagewatch heard: the card's menu links to it by its id, the wire's own line; chosen, it is that line's action.
            ShowBus12AppTests.PumpUntil(() =>
            {
                HearAs(services, "swhallb2", "Hall B", second.Port);
                services.Nodes.Poll();
                return services.Nodes.ShowApps.Any(c => c.Id == "swhallb2");
            });
            var hallB = services.Nodes.ShowApps.Single(c => c.Id == "swhallb2");
            var menu = vm.MenuFor("showapp", hallB)!;
            var to = menu.Find("link.to")!;
            Assert.Equal("STAGEWATCH LINK id:swhallb2", to.Entry.Wire);
            Assert.Equal(ControlProtocol.Parse(to.Entry.Wire).Action, to.Entry.Action);
            Assert.Equal("OK", Wire(router, to.Entry.Wire));
            var viaWire = services.Journal.Tail(300).Last(e => e.Kind == nameof(ShowActionKind.StagewatchLinkSet)).Message;
            Assert.StartsWith("Stagewatch link on → Hall B (swhallb2)", viaWire, StringComparison.Ordinal);
            Assert.Contains("the key goes only to 127.0.0.1:" + first.Port, viaWire, StringComparison.Ordinal);
            Assert.Equal("id:swhallb2", config.Host);
            Assert.Equal(firstTarget, config.KeyFor);                                              // a bound key stays bound

            // Hall B is followed in its public view: no key reaches it, and the desk says where the key is bound.
            PumpUntil(() =>
            {
                HearAs(services, "swhallb2", "Hall B", second.Port);
                services.Nodes.Poll();
                return services.Stage.Following.Length > 0 && link.KeyBindTarget == "node:swhallb2" && services.Nodes.ShowApps.Any(c => c.Id == "swhallb2" && c.Linked);
            });
            Assert.Empty(second.Authorizations);
            Assert.Contains("this desk's key is bound to 127.0.0.1:" + first.Port + " — not sent to Hall B", link.Status, StringComparison.Ordinal);

            // 13.16.2–4: the key is set and held back, and every reader takes that from the link's one record.
            var record = link.Record;
            var bound = "this desk's key is bound to 127.0.0.1:" + first.Port + " and is not sent here";
            Assert.Contains("keyless", record.Flags);
            Assert.Equal(PeerLink.Kid(PeerLink.KeyKey("swk_nodekey")), record.Kid);
            Assert.StartsWith("Hall B · public view — " + bound, record.Words, StringComparison.Ordinal);
            var row = Row(services).GetProperty("link");
            Assert.Equal(record.Words, row.GetProperty("words").GetString());
            Assert.Contains("keyless", row.GetProperty("flags").EnumerateArray().Select(f => f.GetString()));
            Assert.Contains(ShowContext.Parse(services.ContextJson())!.Items, i => i.Kind == "link" && i.State.Contains(bound, StringComparison.Ordinal));
            var brief = services.Kernel.Facts().ShowNetwork;
            Assert.Contains(brief, l => l.Contains(bound, StringComparison.Ordinal));
            // The brief names the link's verbs as the operator's; no Stagewatch verb is a kind the assistant or a cue can carry.
            Assert.Contains(brief, l => l.Trim() == StagewatchVerbs.BriefWords);
            Assert.DoesNotContain(ActionSpec.CueKinds, k => StagewatchLinkActions.IsLinkVerb(k));

            // The linked card's menu: Unlink, follow, link again — the wire's lines — and BIND KEY, a press with no line.
            var linked = services.Nodes.ShowApps.Single(c => c.Id == "swhallb2");
            var card = vm.MenuFor("showapp", linked)!;
            Assert.Equal("STAGEWATCH LINK OFF", card.Find("link.off")!.Entry.Wire);
            Assert.Equal("STAGEWATCH FOLLOW OFF", card.Find("link.follow")!.Entry.Wire);
            Assert.Equal("STAGEWATCH LINK AGAIN", card.Find("link.again")!.Entry.Wire);
            foreach (var id in new[] { "link.off", "link.follow", "link.again" })
            {
                var e = card.Find(id)!.Entry;
                Assert.Equal(ControlProtocol.Parse(e.Wire).Action, e.Action);
            }
            Assert.Null(card.Find("link.floor"));                                                  // no floor at level 0
            var release = card.Find("link.release")!;
            Assert.False(release.Entry.IsEnabled);
            Assert.Equal(StagewatchVerbs.NothingHeldWords, release.Entry.Because);
            var press = card.Find("link.bind")!;
            Assert.False(press.HasWire);
            Assert.Equal(StagewatchLinkActions.BindAction("node:swhallb2"), press.Entry.Action);
            press.ChooseCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.Equal("node:swhallb2", config.KeyFor);
            PumpUntil(() =>
            {
                HearAs(services, "swhallb2", "Hall B", second.Port);
                return second.Authorization == "Bearer swk_nodekey";
            });
            Assert.Equal(("desk", "node:swhallb2", "Done"), services.Journal.Tail(300).Where(e => e.Kind == nameof(ShowActionKind.StagewatchKeyBind)).Select(e => (e.Origin, e.Target, e.Outcome)).Last());

            // Unlink from the card: the wire's line, one journal row, the link off.
            PumpUntil(() =>
            {
                HearAs(services, "swhallb2", "Hall B", second.Port);
                services.Nodes.Poll();
                return services.Nodes.ShowApps.Any(c => c.Id == "swhallb2" && c.Linked);
            });
            var unlink = vm.MenuFor("showapp", services.Nodes.ShowApps.Single(c => c.Id == "swhallb2"))!.Find("link.off")!;
            unlink.ChooseCommand.Execute(null);
            Dispatcher.UIThread.RunJobs();
            Assert.False(config.Enabled);
            Assert.Equal(("desk", "OFF", "Done"), services.Journal.Tail(300).Where(e => e.Kind == nameof(ShowActionKind.StagewatchLinkSet)).Select(e => (e.Origin, e.Target, e.Outcome)).Last());
        }
        finally
        {
            config.Enabled = false;
            link.Reconcile();
            Dispatcher.UIThread.RunJobs();
            first.Dispose();
            second.Dispose();
            b.Dispose();
        }
    }
    /// <summary>
    /// Attempts are not facts: LINK's answer says what the key's binding allows at the target it names, never that the key went — whether
    /// it rides a dial is the link's to decide there (TLS to a certificate a person pinned, or this machine), and its record says when.
    /// </summary>
    [AvaloniaFact]
    public void LinksAnswerSaysWhatTheBindingAllowsNeverThatTheKeyWent()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        var link = services.Kernel.Stagewatch;
        var config = vm.State.Control.Stagewatch;
        try
        {
            config.ApiKey = "swk_nodekey";
            config.KeyFor = StagewatchKeyFor.Address("192.0.2.10", 9924);                     // another machine, no certificate pinned
            var bound = services.Actions.Execute(new ShowAction(ShowActionKind.StagewatchLinkSet, "", "192.0.2.10:9924"), ActionOrigin.Desk);
            Assert.True(bound.Ok, bound.Message);
            Assert.EndsWith("— the key is bound to it; the link's record says when it goes.", bound.Message, StringComparison.Ordinal);
            Assert.DoesNotContain("with its key", bound.Message, StringComparison.Ordinal);
            Assert.NotEqual("live", link.Record.State);

            config.KeyFor = "";
            var unbound = services.Actions.Execute(new ShowAction(ShowActionKind.StagewatchLinkSet, "", "192.0.2.11:9924"), ActionOrigin.Desk);
            Assert.True(unbound.Ok, unbound.Message);
            Assert.Contains("the key is bound to nothing yet", unbound.Message, StringComparison.Ordinal);
            Assert.Contains("the link's record says when it goes", unbound.Message, StringComparison.Ordinal);
        }
        finally
        {
            config.Enabled = false;
            link.Reconcile();
            Dispatcher.UIThread.RunJobs();
            b.Dispose();
        }
    }

    /// <summary>A loopback port nothing listens on: a dial to it is refused at once.</summary>
    private static int ClosedPort()
    {
        var l = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        l.Start();
        var port = ((System.Net.IPEndPoint)l.LocalEndpoint).Port;
        l.Stop();
        return port;
    }

    /// <summary>
    /// 13.1, 13.16.1–2: a link whose Stagewatch does not answer is down — red while the stage follows it — and FOLLOW OFF makes it amber
    /// at once in the record STATE carries (the module's <c>stagewatch_link_light</c> reads it there), with no new run: the redial count
    /// goes on. 13.15.2–3: LINK AGAIN ends the redial's wait — the next dial is due now and is made — and the count goes on with it.
    /// </summary>
    [AvaloniaFact]
    public void FollowMovesTheLinksLightAtOnceAndLinkAgainEndsTheWaitKeepingTheCount()
    {
        var b = TestApp.Boot();
        var (services, vm) = (b.Services, b.Vm);
        var link = services.Kernel.Stagewatch;
        var config = vm.State.Control.Stagewatch;
        var router = new CommandRouter(services);
        var port = ClosedPort();
        string Light() => Row(services).GetProperty("link").GetProperty("light").GetString()!;
        try
        {
            Assert.Equal("OK", Wire(router, "STAGEWATCH LINK 127.0.0.1:" + port));
            Assert.True(config.FollowStage);
            PumpUntil(() => link.Record is { State: "down", Attempt: >= 2, NextMs: > 2000 }, 30000);
            var down = link.Record;
            Assert.Equal(("red", "red"), (down.Light, Light()));                                     // the show needs it

            Assert.StartsWith("OK", Wire(router, "STAGEWATCH FOLLOW OFF"), StringComparison.Ordinal);
            var amber = link.Record;
            Assert.Equal(("amber", "amber"), (amber.Light, Light()));
            Assert.True(amber.Attempt >= down.Attempt, "the run goes on: its count is not reset by FOLLOW");
            Assert.StartsWith("OK", Wire(router, "STAGEWATCH FOLLOW ON"), StringComparison.Ordinal);
            Assert.Equal(("red", "red"), (link.Record.Light, Light()));

            // LINK AGAIN: the wait ends and the count goes on; the dial due now is made before the wait would have let it.
            PumpUntil(() => link.Record is { State: "down", NextMs: > 2500 }, 30000);
            var before = link.Record;
            Assert.Equal("OK", Wire(router, "STAGEWATCH LINK AGAIN"));
            var now = link.Record;
            Assert.True(now.NextMs is null or 0, $"the next dial is due now, not in {now.NextMs} ms");
            Assert.True(now.Attempt >= before.Attempt, $"the count goes on: {before.Attempt} then {now.Attempt}");
            var again = services.Journal.Tail(300).Last(e => e.Kind == nameof(ShowActionKind.StagewatchLinkAgain));
            Assert.Equal(("tcp 10.0.0.12:51234", "Done"), (again.Origin, again.Outcome));
            Assert.StartsWith("Stagewatch link: the redial's wait ended", again.Message, StringComparison.Ordinal);
            PumpUntil(() => link.Record.Attempt > before.Attempt, 2000);
        }
        finally
        {
            config.Enabled = false;
            link.Reconcile();
            Dispatcher.UIThread.RunJobs();
            b.Dispose();
        }
    }
}
