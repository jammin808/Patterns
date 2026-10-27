using Patterns.Core.Menus;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// G34 (Show Bus 1.3 §13.16.6): the Stagewatch link's verbs on the wire, a deck and the menus — LINK, LINK AGAIN, FOLLOW, FLOOR LOWER,
/// RELEASE and the STATUS read — with TRUST, ACCEPT and the key a person's on the desk alone: their words on a line parse to the
/// desk-only kinds with nothing carried, the writer spells none of them, no cue or assistant reaches any link verb, and the key goes
/// only where a person bound it.
/// </summary>
public class StagewatchVerbsTests
{
    private const string Pending = "sha256:8e8bc45bb14f9c74a1b2c3d4e5f60718293a4b5c6d7e8f901122334455667788";

    [Theory]
    [InlineData("STAGEWATCH LINK 10.0.0.9", ShowActionKind.StagewatchLinkSet, "10.0.0.9:9924")]
    [InlineData("stagewatch link 10.0.0.9:9930", ShowActionKind.StagewatchLinkSet, "10.0.0.9:9930")]
    [InlineData("STAGEWATCH LINK Stage-PC", ShowActionKind.StagewatchLinkSet, "Stage-PC:9924")]
    [InlineData("STAGEWATCH LINK fe80::1", ShowActionKind.StagewatchLinkSet, "[fe80::1]:9924")]
    [InlineData("STAGEWATCH LINK [fe80::1]:9930", ShowActionKind.StagewatchLinkSet, "[fe80::1]:9930")]
    [InlineData("STAGEWATCH LINK id:a1b2c3d4", ShowActionKind.StagewatchLinkSet, "id:a1b2c3d4")]
    [InlineData("STAGEWATCH LINK ID:a1b2c3d4", ShowActionKind.StagewatchLinkSet, "id:a1b2c3d4")]
    [InlineData("STAGEWATCH LINK auto", ShowActionKind.StagewatchLinkSet, "AUTO")]
    [InlineData("STAGEWATCH LINK on", ShowActionKind.StagewatchLinkSet, "ON")]
    [InlineData("STAGEWATCH LINK Off", ShowActionKind.StagewatchLinkSet, "OFF")]
    [InlineData("STAGEWATCH LINK again", ShowActionKind.StagewatchLinkAgain, "")]
    [InlineData("STAGEWATCH FOLLOW on", ShowActionKind.StagewatchFollow, "ON")]
    [InlineData("STAGEWATCH FOLLOW OFF", ShowActionKind.StagewatchFollow, "OFF")]
    [InlineData("STAGEWATCH FLOOR lower", ShowActionKind.StagewatchFloorLower, "")]
    [InlineData("STAGEWATCH RELEASE", ShowActionKind.StagewatchRelease, "")]
    public void TheLinksVerbsParseToTheirActionsInTheirWrittenForm(string line, ShowActionKind kind, string value)
    {
        var cmd = ControlProtocol.Parse(line);
        Assert.True(cmd.IsAction, line);
        Assert.Equal(new ShowAction(kind, "", value), cmd.Action);
        var written = WireWriter.Line(cmd.Action);
        Assert.Equal(cmd.Action, ControlProtocol.Parse(written).Action);                       // the written line says the same verb
        Assert.DoesNotContain(kind, ActionSpec.CueKinds);
        Assert.NotNull(ActionSpec.DeskOnly(kind));                                               // never a cue's, so never the assistant's
        Assert.Null(CueSheet.ParseKind(kind.ToString()));
    }

    [Theory]
    [InlineData("STAGEWATCH LINK")]
    [InlineData("STAGEWATCH LINK 10.0.0.9 10.0.0.8")]
    [InlineData("STAGEWATCH LINK http://10.0.0.9")]
    [InlineData("STAGEWATCH LINK key@10.0.0.9")]
    [InlineData("STAGEWATCH LINK 10.0.0.9:0")]
    [InlineData("STAGEWATCH LINK 10.0.0.9:70000")]
    [InlineData("STAGEWATCH LINK id:ab")]
    [InlineData("STAGEWATCH LINK id:a1b2/c3d4")]
    [InlineData("STAGEWATCH LINK id:")]
    [InlineData("STAGEWATCH FOLLOW")]
    [InlineData("STAGEWATCH FOLLOW maybe")]
    [InlineData("STAGEWATCH FLOOR")]
    [InlineData("STAGEWATCH FLOOR RAISE")]
    [InlineData("STAGEWATCH RELEASE blackout")]
    [InlineData("STAGEWATCH STATUS now")]
    [InlineData("STAGEWATCH PIN sha256:00")]
    [InlineData("STAGEWATCH UNLINK")]
    public void AWordTheGrammarDoesNotHaveIsRefusedNeverDefaulted(string line)
    {
        var cmd = ControlProtocol.Parse(line);
        Assert.Equal(RemoteCommandKind.Unknown, cmd.Kind);
        Assert.Null(StagewatchVerbs.Parse(line["STAGEWATCH".Length..]));
    }

    [Fact]
    public void TheReadAnswersAsAQueryOnlyToAPairedConnection()
    {
        foreach (var line in new[] { "STAGEWATCH", "STAGEWATCH STATUS", "stagewatch status" })
        {
            var cmd = ControlProtocol.Parse(line);
            Assert.Equal(RemoteCommandKind.StagewatchStatus, cmd.Kind);
            Assert.True(ControlProtocol.IsQuery(cmd), line);
            Assert.False(ControlProtocol.OpenBeforePairing(cmd), line);
            Assert.Equal("STAGEWATCH STATUS", ControlProtocol.ReadName(cmd));
        }
    }

    /// <summary>
    /// TRUST, ACCEPT and KEY on a line parse to their desk-only kinds with nothing after the word carried — a fingerprint, typed hex or a
    /// key goes nowhere — and the writer spells none of them, so no deck records, replays or offers them.
    /// </summary>
    [Theory]
    [InlineData("STAGEWATCH TRUST", ShowActionKind.StagewatchTrust)]
    [InlineData("STAGEWATCH TRUST sha256:8e8bc45bb14f9c74a1b2c3d4e5f60718293a4b5c6d7e8f901122334455667788", ShowActionKind.StagewatchTrust)]
    [InlineData("STAGEWATCH ACCEPT 8e8bc45b", ShowActionKind.StagewatchAccept)]
    [InlineData("stagewatch accept", ShowActionKind.StagewatchAccept)]
    [InlineData("STAGEWATCH KEY swk_live_k7qm3xwdp9ra", ShowActionKind.StagewatchKeyBind)]
    [InlineData("STAGEWATCH KEY node:a1b2c3d4", ShowActionKind.StagewatchKeyBind)]
    public void TrustAcceptAndTheKeyParseToTheDesksOwnKindsCarryingNothing(string line, ShowActionKind kind)
    {
        var cmd = ControlProtocol.Parse(line);
        Assert.True(cmd.IsAction, line);
        Assert.Equal(new ShowAction(kind), cmd.Action);                                          // no target, no value: nothing of the line rides
        Assert.Equal("", WireWriter.Line(cmd.Action));
        Assert.Equal("", WireWriter.Line(new ShowAction(kind, "", "8e8bc45b")));
        Assert.Contains(kind, WireWriter.Unsayable);
        Assert.NotNull(ActionSpec.DeskOnly(kind));
        Assert.DoesNotContain(kind, ActionSpec.CueKinds);
    }

    [Fact]
    public void TheWordsSayWhoseChoiceItIsAndTheBriefNamesTheVerbsWithoutProposingThem()
    {
        Assert.Equal("TRUST and ACCEPT are a person's choice on the desk, beside the fingerprint", StagewatchVerbs.DeskOnlyWords);
        Assert.Contains("never rides a line", StagewatchVerbs.KeyWords, StringComparison.Ordinal);
        Assert.Contains("never proposed", StagewatchVerbs.BriefWords, StringComparison.Ordinal);
        Assert.Contains("STAGEWATCH LINK", StagewatchVerbs.BriefWords, StringComparison.Ordinal);
        Assert.Contains("§13.17", StagewatchVerbs.NothingHeldWords, StringComparison.Ordinal);
        Assert.Contains("§13.9.3", StagewatchVerbs.NoFloorWords, StringComparison.Ordinal);
    }

    [Fact]
    public void LinkValueWritesOneFormForEachWayOfSayingIt()
    {
        Assert.Equal("10.0.0.9:9924", StagewatchVerbs.LinkValue(" 10.0.0.9 "));
        Assert.Equal("[::1]:9924", StagewatchVerbs.LinkValue("::1"));
        Assert.Equal("id:a1b2c3d4", StagewatchVerbs.LinkValue("Id:a1b2c3d4"));
        Assert.Equal("AUTO", StagewatchVerbs.LinkValue("Auto"));
        Assert.Null(StagewatchVerbs.LinkValue(""));
        Assert.Null(StagewatchVerbs.LinkValue("id:no"));                                       // an id too short is no id, and no host either
        Assert.Equal("10.0.0.9:9924", StagewatchVerbs.AddressWords("10.0.0.9", 9924));
        Assert.Equal("[fe80::1]:9924", StagewatchVerbs.AddressWords("fe80::1", 9924));
    }

    /// <summary>
    /// Show Bus 1.3 §13.8.3: ACCEPT takes at least 8 hex of the waiting certificate, typed — spaces dropped, either case, as the core and
    /// Stagewatch's own desk read it — and refuses fewer, other characters (a colon among them), another certificate's hex and more than
    /// there are; with nothing waiting it accepts nothing.
    /// </summary>
    [Fact]
    public void AcceptTakesAtLeastEightHexOfTheWaitingCertificateAndNothingElse()
    {
        Assert.Null(StagewatchVerbs.AcceptRefusal("8e8bc45b", Pending, "Hall A"));
        Assert.Null(StagewatchVerbs.AcceptRefusal("8E8B C45B", Pending, "Hall A"));
        Assert.Contains("at least 8 hex", StagewatchVerbs.AcceptRefusal("8e:8b:c4:5b:b1", Pending, "Hall A"), StringComparison.Ordinal);
        Assert.Null(StagewatchVerbs.AcceptRefusal("8e8b c45b b14f", Pending, "Hall A"));     // the short fingerprint's own spacing
        Assert.Null(StagewatchVerbs.AcceptRefusal(Pending["sha256:".Length..], Pending, "Hall A"));

        Assert.Equal("No changed certificate is waiting.", StagewatchVerbs.AcceptRefusal("8e8bc45b", "", "Hall A"));
        Assert.Contains("at least 8 hex", StagewatchVerbs.AcceptRefusal("8e8bc45", Pending, "Hall A"), StringComparison.Ordinal);
        Assert.Contains("at least 8 hex", StagewatchVerbs.AcceptRefusal("8e8bc45z", Pending, "Hall A"), StringComparison.Ordinal);
        Assert.Contains("at least 8 hex", StagewatchVerbs.AcceptRefusal("", Pending, "Hall A"), StringComparison.Ordinal);
        var other = StagewatchVerbs.AcceptRefusal("00112233", Pending, "Hall A")!;
        Assert.Contains(TlsFingerprint.Short(Pending), other, StringComparison.Ordinal);       // the waiting one's start, said beside the refusal
        Assert.Contains("Nothing is accepted", other, StringComparison.Ordinal);
        Assert.NotNull(StagewatchVerbs.AcceptRefusal(Pending["sha256:".Length..] + "0", Pending, "Hall A"));
    }

    /// <summary>
    /// G34: a key typed anew on the Nodes page starts bound to nothing, whatever the key before it was bound to; the same key typed again
    /// keeps its binding; and a key and its binding read together — the settings at start, a show loaded — stay together, so a binding a
    /// line left at <c>none</c> is never turned back into "the target the settings name" by a load.
    /// </summary>
    [Fact]
    public void ATypedKeyStartsUnboundAndALoadedKeyKeepsItsBinding()
    {
        var c = new StagewatchLinkConfig { ApiKey = "swk_one", KeyFor = "node:a1b2c3d4" };
        c.TypeKey(" swk_one ");
        Assert.Equal(("swk_one", "node:a1b2c3d4"), (c.ApiKey, c.KeyFor));
        c.TypeKey("swk_two");
        Assert.Equal(("swk_two", ""), (c.ApiKey, c.KeyFor));
        c.KeyFor = StagewatchKeyFor.None;
        c.ApiKey = "swk_three";                                                                   // a loaded pair, copied field by field
        Assert.Equal(StagewatchKeyFor.None, c.KeyFor);
        c.TypeKey("");
        Assert.Equal(("", ""), (c.ApiKey, c.KeyFor));
    }

    /// <summary>
    /// G34: the key goes only where it is bound — unbound, to whatever the desk's own settings name; bound to a node, to that node
    /// at any address; bound to an address, to that address (or a node reached by typing it); <c>none</c> or an unknown word, nowhere.
    /// A line that sets the link first turns an unbound key to <c>none</c>, and leaves a bound one bound.
    /// </summary>
    [Fact]
    public void TheKeyGoesOnlyToTheStagewatchItIsBoundTo()
    {
        var node = StagewatchKeyFor.Node("a1b2c3d4");
        var addr = StagewatchKeyFor.Address("Stage-PC", 9924);
        Assert.Equal("node:a1b2c3d4", node);
        Assert.Equal("addr:stage-pc:9924", addr);

        Assert.True(StagewatchKeyFor.Goes("", "node:ffff0000"));
        Assert.True(StagewatchKeyFor.Goes(node, "node:a1b2c3d4"));
        Assert.False(StagewatchKeyFor.Goes(node, "node:ffff0000"));
        Assert.False(StagewatchKeyFor.Goes(node, "addr:stage-pc:9924", "addr:stage-pc:9924"));
        Assert.True(StagewatchKeyFor.Goes(addr, "addr:STAGE-PC:9924"));
        Assert.True(StagewatchKeyFor.Goes(addr, "node:a1b2c3d4", "addr:stage-pc:9924"));       // the node reached by typing the bound address
        Assert.False(StagewatchKeyFor.Goes(addr, "node:a1b2c3d4"));                            // the same node found by id: not what was bound
        Assert.False(StagewatchKeyFor.Goes(addr, "addr:stage-pc:9930"));
        Assert.False(StagewatchKeyFor.Goes(StagewatchKeyFor.None, "node:a1b2c3d4"));
        Assert.False(StagewatchKeyFor.Goes("anything", "node:a1b2c3d4"));

        Assert.Equal(StagewatchKeyFor.None, StagewatchKeyFor.AfterLine(""));
        Assert.Equal(node, StagewatchKeyFor.AfterLine(node));
        Assert.Equal(StagewatchKeyFor.None, StagewatchKeyFor.AfterLine(StagewatchKeyFor.None));

        Assert.Equal("Stagewatch a1b2c3d4", StagewatchKeyFor.Words(node));
        Assert.Equal("stage-pc:9924", StagewatchKeyFor.Words(addr));
        Assert.StartsWith("nothing", StagewatchKeyFor.Words(StagewatchKeyFor.None), StringComparison.Ordinal);
        Assert.Contains("own settings", StagewatchKeyFor.Words(""), StringComparison.Ordinal);
    }

    private static DeskFacts Desk(MenuStagewatch sw, MenuTrust? trust = null) => new() { AssistantReady = true, Stagewatch = sw, StagewatchTrust = trust };

    /// <summary>
    /// The LINK group on a Stagewatch's card and its Eye node: each verb the wire's own line, so a deck and the menu press the same
    /// action; Trust and the key's binding a person's press with no line; Accept a route to the Nodes page where the hex is typed,
    /// never pre-filled; Release greyed with the reason nothing is held; Floor only while one is recorded; another heard Stagewatch
    /// linked to by its id.
    /// </summary>
    [Fact]
    public void TheLinkGroupIsTheNodesCardsOwnVerbsAndAPersonsPressesCarryNoLine()
    {
        var linked = new MenuStagewatch
        {
            On = true, LinkedId = "a1b2c3d4", LinkedName = "Hall A", Follow = true, Heard = new[] { "a1b2c3d4", "e5f6a7b8" },
            Pending = Pending, Floor = true, BindTarget = "node:a1b2c3d4", BindName = "Stagewatch Hall A",
        };
        var trust = new MenuTrust("a1b2c3d4", "Hall A", Pending);
        var menu = DeskMenus.ShowApp(Desk(linked, trust), new MenuShowApp("a1b2c3d4", "Stagewatch", "Hall A", "linked", true));
        var group = Assert.Single(menu.Groups, g => g.Heading == "LINK");
        Assert.Equal(new[] { "link.off", "link.follow", "link.again", "link.release", "link.trust", "link.accept", "link.floor", "link.bind" }, group.Entries.Select(e => e.Id));
        Assert.Equal("STAGEWATCH LINK OFF", menu.Find("link.off")!.Wire);
        Assert.Equal(new ShowAction(ShowActionKind.StagewatchLinkSet, "", "OFF"), menu.Find("link.off")!.Action);
        Assert.Equal("STAGEWATCH FOLLOW OFF", menu.Find("link.follow")!.Wire);
        Assert.Equal(new ShowAction(ShowActionKind.StagewatchFollow, "", "OFF"), menu.Find("link.follow")!.Action);
        Assert.True(menu.Find("link.follow")!.IsOn);
        Assert.Equal("STAGEWATCH LINK AGAIN", menu.Find("link.again")!.Wire);
        Assert.Equal("STAGEWATCH FLOOR LOWER", menu.Find("link.floor")!.Wire);
        foreach (var e in group.Entries.Where(e => e.HasWire))
        {
            Assert.Equal(e.Action, ControlProtocol.Parse(e.Wire).Action);
        }

        var trustEntry = menu.Find("link.trust")!;
        Assert.False(trustEntry.HasWire);
        Assert.Equal(new ShowAction(ShowActionKind.StagewatchTrust, "", Pending), trustEntry.Action);
        Assert.Contains(TlsFingerprint.Short(Pending), trustEntry.Text, StringComparison.Ordinal);
        var accept = menu.Find("link.accept")!;
        Assert.Equal((MenuScope.Go, "Nodes"), (accept.Scope, accept.Route!.Page));
        Assert.Null(accept.Action);                                                                // typed on the page, never pre-filled from the menu
        Assert.False(accept.HasWire);
        var bind = menu.Find("link.bind")!;
        Assert.False(bind.HasWire);
        Assert.Equal(new ShowAction(ShowActionKind.StagewatchKeyBind, "", "node:a1b2c3d4"), bind.Action);
        var release = menu.Find("link.release")!;
        Assert.Equal(("STAGEWATCH RELEASE", new ShowAction(ShowActionKind.StagewatchRelease)), (release.Wire, release.Action));
        Assert.False(release.IsEnabled);                                                           // nothing of a Stagewatch's is ever held here
        Assert.Equal(StagewatchVerbs.NothingHeldWords, release.Because);
        Assert.Contains("never proposed", group.Note, StringComparison.Ordinal);

        // Follow off: the entry offers it back on. No floor, no pending, no binding held back: those entries are not offered.
        var plain = DeskMenus.ShowApp(Desk(linked with { Follow = false, Floor = false, Pending = "", BindTarget = "" }), new MenuShowApp("a1b2c3d4", "Stagewatch", "Hall A", "linked", true));
        Assert.Equal(new[] { "link.off", "link.follow", "link.again", "link.release" }, plain.Groups.Single(g => g.Heading == "LINK").Entries.Select(e => e.Id));
        Assert.Equal("STAGEWATCH FOLLOW ON", plain.Find("link.follow")!.Wire);
        Assert.False(plain.Find("link.follow")!.IsOn);

        // Another Stagewatch heard: linked to by its show network id.
        var other = DeskMenus.ShowApp(Desk(linked), new MenuShowApp("e5f6a7b8", "Stagewatch", "Hall B", "heard", false));
        var to = other.Find("link.to")!;
        Assert.Equal("STAGEWATCH LINK id:e5f6a7b8", to.Wire);
        Assert.Equal(new ShowAction(ShowActionKind.StagewatchLinkSet, "", "id:e5f6a7b8"), to.Action);
        Assert.Contains("Hall A", to.Detail, StringComparison.Ordinal);                             // what it would replace
        Assert.Null(other.Find("link.off"));
        Assert.Null(other.Find("link.trust"));

        // An app that is no Stagewatch has no LINK group.
        var vmix = DeskMenus.ShowApp(Desk(linked), new MenuShowApp("v1v1v1v1", "vMix", "Studio", "heard", false));
        Assert.DoesNotContain(vmix.Groups, g => g.Heading == "LINK");

        // The Eye's node of the linked Stagewatch carries the same group.
        var g = EyeGraph.Build(new EyeFacts { ShowApps = new[] { new EyeShowApp("a1b2c3d4", "Stagewatch", "Hall A", ShowBusState.Live, true, "") } });
        var eye = EyeMenus.For(Desk(linked, trust), g, g.Find("app:a1b2c3d4")!);
        Assert.Equal(group.Entries.Select(e => (e.Id, e.Wire, e.Action)), eye.Groups.Single(x => x.Heading == "LINK").Entries.Select(e => (e.Id, e.Wire, e.Action)));
    }
}
