using System.Text;
using System.Text.Json;
using Patterns.Core.Menus;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Show Bus 1.3 §13.5 on this desk, pure: the <c>ptk_</c> keys it mints, the operator's KEY MAKE, KEY BIND and KEY REVOKE on the kept
/// list, enrolment from the pairing token over TLS held to the contract's own vector, a new code's revocation of the keys enrolled from
/// the old one, the seen-in-clear mark, the kept form (fail closed, never a key), the owner's P-scopes map for every verb family, the
/// KEY verbs as the wire spells them and refuses them, the key row's menu and the Super Check's row.
/// </summary>
public class AppKeysTests
{
    private static readonly DateTime T0 = new(2026, 11, 15, 10, 0, 0, DateTimeKind.Utc);

    // The contract's first proof row: Patterns 9f8e7d6c, Stagewatch 3f9c2a1b7d0e, the code k7qm-3xwd p9ra, over TLS.
    private const string Server = "9f8e7d6c";
    private const string Client = "3f9c2a1b7d0e";
    private const string SNonce = "00112233445566778899aabbccddeeff";
    private const string CNonce = "ffeeddccbbaa99887766554433221100";
    private const string TlsBind = "tls:sha256:03e9b152ca42518c28128f36def699be8aea5eec11bb9d5afc18a314f8c3eb04";
    private const string CodeClientProof = "a0622441e11e24be7577abcff6599b5b8c5031a06739a2774e94307d70b5c09f";

    private static byte[] Salt => Enumerable.Range(0, 32).Select(i => (byte)i).ToArray();

    [Fact]
    public void AKeyIsPtkAnd32SymbolsOf160RandomBitsAndNoTwoAreAlike()
    {
        var keys = Enumerable.Range(0, 200).Select(_ => AppKeys.NewKey()).ToList();
        Assert.All(keys, k =>
        {
            Assert.StartsWith(AppKeys.Prefix, k, StringComparison.Ordinal);
            Assert.Equal(AppKeys.Prefix.Length + 32, k.Length);
            Assert.True(LinkAcceptor.IsKeyShape(k), k);
        });
        Assert.Equal(keys.Count, keys.Distinct(StringComparer.Ordinal).Count());
        Assert.Equal(160, AppKeys.KeyBits);
        Assert.Equal(AppKeys.KeyBits, 32 * 5);                                                  // 32 symbols of a 32-symbol alphabet
        // Every symbol of the alphabet turns up, so no bit of the 160 is stuck.
        Assert.Equal(32, keys.SelectMany(k => k[AppKeys.Prefix.Length..]).Distinct().Count());
    }

    [Fact]
    public void KeyMakeBindsOneConnectorAtAScopeAndRefusesWhatIsNoneWithTheReason()
    {
        var (keys, secret, made, words) = AppKeys.Make([], "main:" + Client, "drive", T0);
        Assert.NotNull(secret);
        var key = Assert.Single(keys);
        Assert.Same(made, key);
        Assert.Equal(("main:" + Client, "drive", AppKeys.Issued, false), (key.Bound, key.Scope, key.Made, key.Enrolled));
        Assert.Equal(Convert.ToHexStringLower(PeerLink.KeyKey(secret!)), Convert.ToHexStringLower(key.K));
        Assert.Equal(PeerLink.Kid(key.K), key.Kid);
        Assert.Equal(("key", "drive", "main:" + Client), (key.Credential.Verified, key.Credential.Scope, key.Credential.Bound));
        Assert.DoesNotContain(secret!, words, StringComparison.Ordinal);
        Assert.Contains("shown once", words, StringComparison.Ordinal);

        // A kind in capitals is the connector's kind; anything not kind:id, or not on the ladder, is refused and makes nothing.
        Assert.Equal("main:" + Client, AppKeys.Make([], "MAIN:" + Client, "follow", T0).Made!.Bound);
        foreach (var (bound, scope) in new[] { ("3f9c2a1b7d0e", "follow"), ("main:abc", "follow"), ("main:" + Client, "admin"), ("main:" + Client, "") })
        {
            var refused = AppKeys.Make(keys, bound, scope, T0);
            Assert.Null(refused.Secret);
            Assert.Same(keys, refused.Keys);
        }
        // A bound on the list: one past it is refused with words, never a key dropped.
        IReadOnlyList<AppKey> full = [];
        for (var i = 0; i < AppKeys.MaxKeys; i++) full = AppKeys.Make(full, "main:" + Client, "follow", T0).Keys;
        var over = AppKeys.Make(full, "main:" + Client, "follow", T0);
        Assert.Null(over.Made);
        Assert.Equal(AppKeys.MaxKeys, over.Keys.Count);
        Assert.Contains("at most", over.Words, StringComparison.Ordinal);
    }

    [Fact]
    public void KeyBindMovesOrLowersEndingLiveLinksAndRaisingDoesNotAndNoChangeIsRefused()
    {
        var keys = AppKeys.Make([], "main:" + Client, "drive", T0).Keys;
        var kid = keys[0].Kid;

        var moved = AppKeys.Bind(keys, kid, "playmap:0a1b2c3d", null);
        Assert.True(moved.Ok);
        Assert.True(moved.EndsLinks);
        Assert.Equal(("playmap:0a1b2c3d", "drive"), (moved.Keys[0].Bound, moved.Keys[0].Scope));

        var lowered = AppKeys.Bind(keys, kid, "main:" + Client, "follow");
        Assert.True(lowered.Ok && lowered.EndsLinks);
        var raised = AppKeys.Bind(keys, kid, "main:" + Client, "operate");
        Assert.True(raised.Ok);
        Assert.False(raised.EndsLinks);                                                         // a link at drive keeps running at drive until it links again

        var same = AppKeys.Bind(keys, kid, "main:" + Client, "drive");
        Assert.False(same.Ok);
        Assert.Contains("already", same.Words, StringComparison.Ordinal);
        Assert.False(AppKeys.Bind(keys, "0011223344556677", "main:" + Client, null).Ok);
        Assert.False(AppKeys.Bind(keys, kid, "nonsense", null).Ok);

        var revoked = AppKeys.Revoke(keys, kid);
        Assert.True(revoked.Ok);
        Assert.Empty(revoked.Keys);
        Assert.False(AppKeys.Revoke(revoked.Keys, kid).Ok);
    }

    [Fact]
    public void ACodeProvedOverTlsEnrolsTheContractsKeyAndTheVerdictNamesItAndOverPlainItNeverDoes()
    {
        var code = LinkAcceptor.Code("k7qm-3xwd p9ra")!;
        var ask = new LinkAsk(1, "main", Client, CNonce, "code");
        var d = LinkAcceptor.Decide(ask, CodeClientProof, Server, SNonce, TlsBind, [code], _ => 0, enrol: true);
        Assert.True(d.Ok);
        Assert.Equal("fb7d7f2a95b74ba83c4dd61e4382fad70056f92f7267d5921471df4905c5dd44", Convert.ToHexStringLower(d.EnrolK!));
        Assert.Equal("936086ee6bc93cef", PeerLink.Kid(d.EnrolK!));
        Assert.DoesNotContain(Convert.ToHexStringLower(d.EnrolK!), d.ToString(), StringComparison.Ordinal);

        // 13.6.4's example verdict, byte for byte, enrolled last.
        Assert.Equal("paired {\"v\":1,\"verified\":\"code\",\"scope\":\"operate\",\"proof\":\"a628a0aa5691349ced0d2e1d8b9b13203b55bbd3d994a9e45925b74fc1369e02\",\"lid\":\"c1bb791379aa366c\",\"level\":1,\"can\":[\"final\",\"receipts\",\"enrol\"],\"inst\":\"a41f09c2\",\"beatMs\":2000,\"enrolled\":\"936086ee6bc93cef\"}",
            LinkAcceptor.Verdict(code, d.Proof!, d.Lid!, ["final", "receipts", "enrol"], "a41f09c2", 2000, PeerLink.Kid(d.EnrolK!)));

        // Not offered, or over plain: no key enrols (13.5.7: a code proved over a plain carrier MUST NOT enrol).
        Assert.Null(LinkAcceptor.Decide(ask, CodeClientProof, Server, SNonce, TlsBind, [code], _ => 0).EnrolK);
        var plainProof = PeerLink.ClientProof(code.K, PeerLink.Transcript("line", 1, 1, "patterns:" + Server, "main:" + Client, "code", SNonce, CNonce, "plain")!);
        var plain = LinkAcceptor.Decide(ask, plainProof, Server, SNonce, "plain", [code], _ => 0, enrol: true);
        Assert.True(plain.Ok);
        Assert.Null(plain.EnrolK);

        // The enrolled key, kept: bound to the connector, at follow, verified as a link key from then on.
        var mark = AppKeys.CodeMark(Salt, "k7qm-3xwd p9ra");
        var (keys, enrolled) = AppKeys.Enrol([], d.EnrolK!, "main:" + Client, "Hall A", mark, T0);
        Assert.NotNull(enrolled);
        Assert.Equal(("936086ee6bc93cef", "follow", AppKeys.Enrolled, "link"), (enrolled!.Kid, enrolled.Scope, enrolled.Made, enrolled.Credential.Verified));
        // A second enrolment for the same connector replaces the first; another connector's stays.
        var again = AppKeys.Enrol(keys, Enumerable.Repeat((byte)7, 32).ToArray(), "main:" + Client, "Hall A", mark, T0).Keys;
        Assert.Single(again);
        Assert.NotEqual(enrolled.Kid, again[0].Kid);
        var two = AppKeys.Enrol(again, Enumerable.Repeat((byte)9, 32).ToArray(), "main:a1b2c3d4", "Hall B", mark, T0).Keys;
        Assert.Equal(2, two.Count);
    }

    /// <summary>
    /// A connector that proves the code again renews its enrolled key: the new one keeps the scope the operator raised the old one to
    /// (from the same code; from another it starts at follow). An enrolled key's scope moves with KEY BIND, its connector never.
    /// </summary>
    [Fact]
    public void ARenewedEnrolmentKeepsItsScopeAndAnEnrolledKeyIsNeverBoundToAnotherConnector()
    {
        var mark = AppKeys.CodeMark(Salt, "k7qm-3xwd p9ra");
        var keys = AppKeys.Enrol([], Enumerable.Repeat((byte)7, 32).ToArray(), "main:" + Client, "Hall A", mark, T0).Keys;
        var kid = keys[0].Kid;
        var raised = AppKeys.Bind(keys, kid, "main:" + Client, "drive");
        Assert.True(raised.Ok);
        Assert.False(raised.EndsLinks);
        var renewed = AppKeys.Enrol(raised.Keys, Enumerable.Repeat((byte)8, 32).ToArray(), "main:" + Client, "Hall A", mark, T0);
        Assert.Equal(("drive", 1), (renewed.Enrolled!.Scope, renewed.Keys.Count));
        Assert.NotEqual(kid, renewed.Enrolled.Kid);
        // From another code, the scope does not carry: the new code's enrolment starts at follow.
        var other = AppKeys.Enrol(raised.Keys, Enumerable.Repeat((byte)8, 32).ToArray(), "main:" + Client, "Hall A", AppKeys.CodeMark(Salt, "Z9Z9Z9Z9Z9Z9"), T0);
        Assert.Equal(AppKeys.EnrolScope, other.Enrolled!.Scope);

        // Bound elsewhere: refused with the reason, nothing changed; its scope still moves.
        var moved = AppKeys.Bind(raised.Keys, kid, "playmap:0a1b2c3d", null);
        Assert.False(moved.Ok);
        Assert.Contains("only it holds that key", moved.Words, StringComparison.Ordinal);
        Assert.Same(raised.Keys, moved.Keys);
        Assert.True(AppKeys.Bind(raised.Keys, kid, "main:" + Client, "follow").Ok);
    }

    [Fact]
    public void ANewCodeRevokesEveryKeyEnrolledFromTheOldOneAndLeavesTheIssuedOnes()
    {
        var mark = AppKeys.CodeMark(Salt, "k7qm-3xwd p9ra");
        var keys = AppKeys.Make([], "main:" + Client, "drive", T0).Keys;
        keys = AppKeys.Enrol(keys, Enumerable.Repeat((byte)7, 32).ToArray(), "main:a1b2c3d4", "", mark, T0).Keys;
        Assert.Equal(2, AppKeys.InForce(keys, mark).Count);

        // Under another code, the enrolled key is out of force at once, whatever restarts; the issued key stays.
        var other = AppKeys.CodeMark(Salt, "Z9Z9Z9Z9Z9Z9");
        Assert.NotEqual(mark, other);
        Assert.Single(AppKeys.InForce(keys, other), k => !k.Enrolled);
        Assert.Single(AppKeys.InForce(keys, ""), k => !k.Enrolled);                               // no code: none enrolled is in force

        var (after, revoked) = AppKeys.RevokeEnrolled(keys, other);
        Assert.Single(revoked);
        Assert.Single(after, k => k.Made == AppKeys.Issued);
        Assert.Empty(AppKeys.RevokeEnrolled(after, other).Revoked);
        // The same code revokes nothing.
        Assert.Empty(AppKeys.RevokeEnrolled(keys, mark).Revoked);
        // A code of another shape marks nothing; a code mark never carries the code or its K.
        Assert.Equal("", AppKeys.CodeMark(Salt, "K7QM-3XWD-P9RÄ"));
        Assert.DoesNotContain(Convert.ToHexStringLower(LinkAcceptor.Code("k7qm-3xwd p9ra")!.K), mark, StringComparison.Ordinal);
    }

    [Fact]
    public void AKeySeenInClearIsKnownByItsKAndMarkedOnceWithWhereAndHowAndTheRepeatsCounted()
    {
        var (keys, secret, made, _) = AppKeys.Make([], "main:" + Client, "post", T0);
        Assert.Same(made, AppKeys.OfPresented(keys, " " + secret + " "));
        Assert.Null(AppKeys.OfPresented(keys, AppKeys.NewKey()));
        Assert.Null(AppKeys.OfPresented(keys, "K7QM-3XWD-P9RA"));

        var (marked, first) = AppKeys.MarkClear(keys, made!.Kid, "10.0.0.7", "wire", T0);
        Assert.True(first);
        var (again, second) = AppKeys.MarkClear(marked, made.Kid, "10.0.0.8", "web", T0.AddMinutes(1));
        Assert.False(second);
        var mark = again[0].ClearSeen!;
        Assert.Equal(("10.0.0.7", "wire,web", 2L, T0, T0.AddMinutes(1)), (mark.Address, mark.Via, mark.Count, mark.FirstUtc, mark.LastUtc));
        var words = AppKeys.Words(again[0]);
        Assert.Contains("△ seen in clear from 10.0.0.7", words, StringComparison.Ordinal);
        Assert.Contains("replace this key", words, StringComparison.Ordinal);
        Assert.DoesNotContain(secret!, words, StringComparison.Ordinal);
        Assert.Contains("1 seen in clear", AppKeys.Summary(again), StringComparison.Ordinal);
        Assert.False(AppKeys.MarkClear(again, "0011223344556677", "10.0.0.7", "wire", T0).First);
    }

    [Fact]
    public void TheKeptFormCarriesKAndNoKeyAndReadsBackFailingClosedRowByRow()
    {
        var (keys, secret, _, _) = AppKeys.Make([], "main:" + Client, "drive", T0);
        keys = AppKeys.Enrol(keys, Enumerable.Repeat((byte)7, 32).ToArray(), "playmap:0a1b2c3d", "Stage left", AppKeys.CodeMark(Salt, "k7qm-3xwd p9ra"), T0).Keys;
        keys = AppKeys.MarkClear(keys, keys[0].Kid, "10.0.0.7", "web", T0).Keys;
        var bytes = AppKeys.Write(keys, Salt);
        var text = Encoding.UTF8.GetString(bytes);
        Assert.DoesNotContain(secret!, text, StringComparison.Ordinal);

        var back = AppKeys.Read(bytes)!.Value;
        Assert.Equal(0, back.Dropped);
        Assert.Equal(Salt, back.Salt);
        Assert.Equal(keys.Select(k => (k.Kid, Convert.ToHexStringLower(k.K), k.Bound, k.Scope, k.Made, k.CodeMark, k.ClearSeen?.Address)),
            back.Keys.Select(k => (k.Kid, Convert.ToHexStringLower(k.K), k.Bound, k.Scope, k.Made, k.CodeMark, k.ClearSeen?.Address)));
        // A record's print never says K.
        Assert.All(back.Keys, k => Assert.DoesNotContain(Convert.ToHexStringLower(k.K), k.ToString(), StringComparison.Ordinal));

        // Fail closed: a row whose kid is not its K's, or whose scope or way of being made is unknown, is left out and counted.
        using var doc = JsonDocument.Parse(bytes);
        var rows = doc.RootElement.GetProperty("keys").EnumerateArray().Select(r => r.GetRawText()).ToList();
        var forged = rows[0].Replace(keys[0].Kid, "0011223344556677", StringComparison.Ordinal);
        var badScope = rows[0].Replace("\"drive\"", "\"root\"", StringComparison.Ordinal);
        var badMade = rows[1].Replace("\"enrolled\"", "\"guessed\"", StringComparison.Ordinal);
        var doc2 = $"{{\"v\":1,\"salt\":\"{Convert.ToHexStringLower(Salt)}\",\"keys\":[{forged},{badScope},{badMade},{rows[1]}]}}";
        var read = AppKeys.Read(Encoding.UTF8.GetBytes(doc2))!.Value;
        Assert.Equal(3, read.Dropped);
        Assert.Single(read.Keys);
        Assert.Null(AppKeys.Read(Encoding.UTF8.GetBytes("{\"v\":2}")));
        Assert.Null(AppKeys.Read(Encoding.UTF8.GetBytes("not json")));
    }

    /// <summary>
    /// The owner's P-scopes on this desk's verbs, one family a line: what follow, post, drive and operate each may and may not run. No
    /// OUTPUTS, TAKE or CUT, SCREEN or setting below operate.
    /// </summary>
    [Theory]
    [InlineData("STATUS", "follow")]
    [InlineData("CONTEXT", "follow")]
    [InlineData("EYE", "follow")]
    [InlineData("CUE LIST", "follow")]
    [InlineData("NODES", "follow")]
    [InlineData("RECORD ON", "follow")]
    [InlineData("VIDEO PLAY", "drive")]
    [InlineData("VIDEO PAUSE", "drive")]
    [InlineData("VIDEO STOP", "drive")]
    [InlineData("VIDEO RESTART", "drive")]
    [InlineData("CUE GO", "drive")]
    [InlineData("CUE STANDBY NEXT", "drive")]
    [InlineData("SECTION 2", "drive")]
    [InlineData("STAGE MESSAGE Wrap up", "drive")]
    [InlineData("OUTPUTS ON", "operate")]
    [InlineData("GO", "operate")]
    [InlineData("SCREEN 1 ON", "operate")]
    [InlineData("SCREEN 1 TAKE", "operate")]
    [InlineData("SCREEN 1 CUT", "operate")]
    [InlineData("BLACKOUT ON", "operate")]
    [InlineData("LOOK Walk-in", "operate")]
    [InlineData("CUE HOLD ON", "operate")]
    [InlineData("STAGE CLEAR", "operate")]
    [InlineData("AUDIENCE ON", "operate")]
    [InlineData("SHOWLOCK ON", "operate")]
    [InlineData("STAGEWATCH LINK OFF", "operate")]
    [InlineData("NAV DECK Looks", "operate")]
    [InlineData("CONTEXT POST {\"v\":1}", "post")]
    public void EachVerbFamilyNeedsItsScopeAndIsRefusedBelowIt(string line, string needed)
    {
        var cmd = ControlProtocol.Parse(line);
        Assert.NotEqual(RemoteCommandKind.Unknown, cmd.Kind);
        Assert.Equal(needed, AppKeyScopes.Needed(cmd));
        foreach (var scope in LinkAcceptor.Scopes)
            Assert.Equal(LinkAcceptor.Rank(scope) >= LinkAcceptor.Rank(needed), AppKeyScopes.Allows(scope, cmd));
        // post runs what follow runs, and CONTEXT POST (§6) is the one line kind that is post's alone.
        if (!AppKeyScopes.PostKinds.Contains(cmd.Kind)) Assert.Equal(AppKeyScopes.Allows("follow", cmd), AppKeyScopes.Allows("post", cmd));
        else Assert.True(AppKeyScopes.Allows("post", cmd) && !AppKeyScopes.Allows("follow", cmd));
    }

    [Fact]
    public void NoOutputsTakeCutScreenOrSettingIsReachedBelowOperateWhateverTheVocabularyGains()
    {
        foreach (var kind in Enum.GetValues<ShowActionKind>())
        {
            var needed = AppKeyScopes.Needed(RemoteCommand.Of(new ShowAction(kind)));
            Assert.Equal(AppKeyScopes.DriveKinds.Contains(kind) ? "drive" : "operate", needed);
            var name = kind.ToString();
            if (name.StartsWith("Outputs", StringComparison.Ordinal) || name.StartsWith("Screen", StringComparison.Ordinal) || kind is ShowActionKind.Take or ShowActionKind.Cut
                || name.StartsWith("Audience", StringComparison.Ordinal) || name.StartsWith("ShowLock", StringComparison.Ordinal) || name.StartsWith("AppKey", StringComparison.Ordinal))
                Assert.Equal("operate", needed);
        }
    }

    /// <summary>
    /// Every line kind of the wire is named in exactly one of the scope map's lists (follow, post, operate), so a kind the wire gains later fails here until it
    /// is placed; the ones named operate need operate whatever action they carry.
    /// </summary>
    [Fact]
    public void EveryLineKindIsNamedFollowOrOperateAndNoneIsBoth()
    {
        foreach (var kind in Enum.GetValues<RemoteCommandKind>())
        {
            var follow = AppKeyScopes.FollowKinds.Contains(kind);
            var post = AppKeyScopes.PostKinds.Contains(kind);
            var operate = AppKeyScopes.OperateKinds.Contains(kind);
            Assert.True((follow ? 1 : 0) + (post ? 1 : 0) + (operate ? 1 : 0) == 1, $"{kind} must be named in exactly one of FollowKinds, PostKinds and OperateKinds");
            var cmd = new RemoteCommand(kind, new ShowAction(ShowActionKind.BlackoutOn));
            Assert.Equal(follow ? "follow" : post ? "post" : "operate", AppKeyScopes.Needed(cmd));
        }
        Assert.Equal("operate", AppKeyScopes.Needed(ControlProtocol.Parse("ASSISTANT ASK what is on screen 2")));
        Assert.Equal("drive", AppKeyScopes.Needed(new RemoteCommand(RemoteCommandKind.Action, new ShowAction(ShowActionKind.CueGo))));
        // A drive action carried on any kind but Action is no action and runs nothing a drive key may send.
        Assert.Equal("operate", AppKeyScopes.Needed(new RemoteCommand(RemoteCommandKind.NavDeck, new ShowAction(ShowActionKind.CueGo))));
    }

    [Theory]
    [InlineData("KEY MAKE main:3f9c2a1b7d0e", ShowActionKind.AppKeyMake, "main:3f9c2a1b7d0e", "follow")]
    [InlineData("KEY MAKE main:3f9c2a1b7d0e DRIVE", ShowActionKind.AppKeyMake, "main:3f9c2a1b7d0e", "drive")]
    [InlineData("KEY BIND bb3f97d01c274e15 playmap:0a1b2c3d", ShowActionKind.AppKeyBind, "bb3f97d01c274e15", "playmap:0a1b2c3d")]
    [InlineData("KEY BIND bb3f97d01c274e15 playmap:0a1b2c3d operate", ShowActionKind.AppKeyBind, "bb3f97d01c274e15", "playmap:0a1b2c3d operate")]
    [InlineData("key revoke bb3f97d01c274e15", ShowActionKind.AppKeyRevoke, "bb3f97d01c274e15", "")]
    public void TheKeyVerbsParseAsTheirKindsAndTheWriterSpellsNoneOfThem(string line, ShowActionKind kind, string target, string value)
    {
        var cmd = ControlProtocol.Parse(line);
        Assert.Equal(new ShowAction(kind, target, value), cmd.Action);
        Assert.Equal("", WireWriter.Line(cmd.Action));
        Assert.Contains(kind, WireWriter.Unsayable);
        Assert.NotNull(ActionSpec.DeskOnly(kind));
    }

    [Theory]
    [InlineData("KEY")]
    [InlineData("KEY LIST")]
    [InlineData("KEY MAKE")]
    [InlineData("KEY REVOKE ptk_3vq8xk2m9tz7w4hc6r5n2b8d7f4g9j3k")]
    [InlineData("KEY BIND notakid main:3f9c2a1b7d0e")]
    [InlineData("KEY REVOKE bb3f97d01c274e15 extra")]
    public void AnyOtherKeyLineIsUnknownNeverDefaulted(string line)
        => Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse(line).Kind);

    [Fact]
    public void AKeyRowsMenuRevokesChangesItsScopeAndOpensTheBindBoxWithNoWireLine()
    {
        var menu = DeskMenus.AppKey(new DeskFacts(), new MenuAppKey("bb3f97d01c274e15", "main:" + Client, "drive", AppKeys.Issued, ClearSeen: true));
        Assert.Equal(("appkey", "bb3f97d01c274e15"), (menu.Kind, menu.Subject));
        Assert.Contains("seen in clear", menu.Subtitle, StringComparison.Ordinal);
        var entries = menu.Flatten().ToList();
        Assert.Equal(new ShowAction(ShowActionKind.AppKeyRevoke, "bb3f97d01c274e15"), entries.Single(e => e.Id == "key.revoke").Action);
        Assert.Equal(["key.scope.follow", "key.scope.post", "key.scope.operate"], entries.Where(e => e.Id.StartsWith("key.scope.", StringComparison.Ordinal)).Select(e => e.Id));
        Assert.Equal(new ShowAction(ShowActionKind.AppKeyBind, "bb3f97d01c274e15", "main:" + Client + " follow"), entries.Single(e => e.Id == "key.scope.follow").Action);
        Assert.Equal(new MenuRoute("Nodes", "bb3f97d01c274e15"), entries.Single(e => e.Id == "key.bind").Route);
        Assert.All(entries, e => Assert.Equal("", e.Wire));
        Assert.All(entries.Where(e => e.Action is not null), e => Assert.Equal("", e.Because));
        // An enrolled key's row offers its scope and Revoke, never another connector: only the one that enrolled it holds it.
        var enrolled = DeskMenus.AppKey(new DeskFacts(), new MenuAppKey("bb3f97d01c274e15", "main:" + Client, "follow", AppKeys.Enrolled, false)).Flatten().ToList();
        Assert.DoesNotContain(enrolled, e => e.Id == "key.bind");
        Assert.Contains(enrolled, e => e.Id == "key.scope.drive");
        Assert.Contains(enrolled, e => e.Id == "key.revoke");
        // On a node the key's presses are greyed with their reason: keys are the desk's.
        Assert.All(DeskMenus.AppKey(new DeskFacts { IsNode = true }, new MenuAppKey("bb3f97d01c274e15", "main:" + Client, "drive", AppKeys.Issued, false)).Flatten().Where(e => e.Action is not null),
            e => Assert.NotEqual("", e.Because));
    }

    [Fact]
    public void TheSuperChecksAppKeysRowCountsTheKeysAndTurnsAmberWhileOneWasSeenInClear()
    {
        var keys = AppKeys.Make([], "main:" + Client, "drive", T0).Keys;
        CheckFacts Facts(IReadOnlyList<AppKey> k) => new() { RemoteEnabled = true, RemoteToken = true, WirePort = 9697, AppKeys = k, AppKeysProtection = "the keys sealed to this Windows user (DPAPI)" };
        Assert.DoesNotContain(SuperCheck.Run(Facts([])).Rows, r => r.Item == "App keys");
        var green = Assert.Single(SuperCheck.Run(Facts(keys)).Rows, r => r.Item == "App keys");
        Assert.Equal((CheckLight.Green, "1 app key (1 issued, 0 enrolled)"), (green.Light, green.Value));
        Assert.Contains("DPAPI", green.Note, StringComparison.Ordinal);
        var marked = AppKeys.MarkClear(keys, keys[0].Kid, "10.0.0.7", "wire", T0).Keys;
        var amber = Assert.Single(SuperCheck.Run(Facts(marked)).Rows, r => r.Item == "App keys");
        Assert.Equal(CheckLight.Amber, amber.Light);
        Assert.StartsWith("FIX: " + keys[0].Kid + " seen in clear from 10.0.0.7", amber.Note, StringComparison.Ordinal);
        // Keys the seal refused hold for this run only: amber, with why, whatever else is true of them.
        var unsealed = Assert.Single(SuperCheck.Run(new CheckFacts { RemoteEnabled = true, RemoteToken = true, WirePort = 9697, AppKeys = keys, AppKeysProtection = "the keys held for this run only", AppKeysProblem = "DPAPI refused to seal the app keys" }).Rows, r => r.Item == "App keys");
        Assert.Equal(CheckLight.Amber, unsealed.Light);
        Assert.StartsWith("FIX: DPAPI refused to seal the app keys", unsealed.Note, StringComparison.Ordinal);
    }
}
