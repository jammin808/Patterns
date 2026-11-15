using System.Net.Sockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Show Bus 1.3 §13.5 at this desk's own wire: app keys the operator makes on the Nodes page, proved by the scripted peer as Stagewatch
/// would, from loopback with this machine's trust off (restored in finally by the desk). S17 — a key revoked while its link is live is
/// refused at once and the connection ended; S18 — a key bound to another connector is a binding refusal, never counted, and KEY BIND
/// lets it speak for the right one (and ends the link it moved away from); S21 — a code proved over TLS enrols, over plain it does not,
/// and a new code revokes the keys enrolled from the old one. The P-scopes map on a live link, the KEY verbs refused from the wire, the
/// key seen in clear, and no key in any reader.
/// </summary>
public sealed partial class PeerLinkWireTests
{
    /// <summary>KEY MAKE from the desk, as the Nodes page's MAKE KEY presses it; the key it shows once.</summary>
    private static string MakeKey(AcceptorDesk desk, string bound, string scope)
    {
        var result = desk.Services.Actions.Execute(new ShowAction(ShowActionKind.AppKeyMake, bound, scope), ActionOrigin.Desk);
        Assert.Equal(ActionStatus.Done, result.Status);
        var key = desk.Services.Kernel.AppKeys.TakeShown();
        Assert.NotNull(key);
        Assert.Null(desk.Services.Kernel.AppKeys.TakeShown());                                   // shown once
        Assert.DoesNotContain(key!, result.Message, StringComparison.Ordinal);
        return key!;
    }

    private static JsonObject LiveRefusal(AcceptorDesk desk, int conn)
        => TestApp.Pump(desk.Peer.Wait(e => Str(e, "ev") == "refused" && Str(e, "role") == "connector" && Int(e, "conn") == conn && e["live"]?.GetValue<bool>() == true));

    private static JsonElement Keys(AcceptorDesk desk)
    {
        using var doc = JsonDocument.Parse(new CommandRouter(desk.Services).StateJson());
        return doc.RootElement.GetProperty("control").GetProperty("keys").Clone();
    }

    [AvaloniaFact]
    public void AKeyMadeOnTheDeskLinksAtItsScopeAndRevokedWhileLiveIsRefusedAtOnceItsConnectionEnded()
    {
        using var files = new KitFiles();
        using var desk = new AcceptorDesk();
        var key = MakeKey(desk, "main:" + StagewatchId, "drive");
        var kid = LinkAcceptor.Key(key, "main:" + StagewatchId, "drive")!.Kid;

        // The key proves over TLS; the verdict says key and drive, and STATE lists it by its kid — never the key.
        var (conn, linked) = Dialled(desk, DialDesk(desk, tls: true, files.Write("key", key), "key"));
        Assert.Equal(("link", "key", "drive", kid), (Str(linked, "ev"), Str(linked, "verified"), Str(linked, "scope"), Str(linked, "kid")));
        ReadOn(desk, conn, l => l.StartsWith("STATE {", StringComparison.Ordinal));
        var row = Assert.Single(Keys(desk).EnumerateArray());
        Assert.Equal((kid, "main:" + StagewatchId, "drive", "issued"), (row.GetProperty("kid").GetString(), row.GetProperty("bound").GetString(), row.GetProperty("scope").GetString(), row.GetProperty("made").GetString()));
        PumpWhile(() => desk.Services.Kernel.AppKeys.Keys.Single().LastUseUtc is null, 5000);

        // The P-scopes map on the live link: a read and a drive verb run; the air's switch is operate's and is refused in its words.
        Send(desk, conn, "@q1 STATUS");
        Assert.StartsWith("OK @q1 {", Str(ReadOn(desk, conn, l => l.Contains("@q1", StringComparison.Ordinal)), "line"), StringComparison.Ordinal);
        Send(desk, conn, "@s1-11aa22bb STAGE MESSAGE Wrap up");
        Assert.StartsWith("OK @s1-11aa22bb", Str(ReadOn(desk, conn, l => l.Contains("@s1-11aa22bb", StringComparison.Ordinal)), "line"), StringComparison.Ordinal);
        foreach (var (rid, line) in new[] { ("@s2-11aa22bb", "BLACKOUT ON"), ("@s3-11aa22bb", "OUTPUTS ON"), ("@s4-11aa22bb", "SCREEN 1 OFF"), ("@s5-11aa22bb", "AUDIENCE ON") })
        {
            Send(desk, conn, rid + " " + line);
            Assert.Equal("ERR " + rid + " " + LinkAcceptor.ScopeWords("drive", desk.Services.Kernel.Beacon.MachineName),
                Str(ReadOn(desk, conn, l => l.Contains(rid, StringComparison.Ordinal)), "line"));
        }
        Assert.False(desk.Services.State.Blackout);

        // A KEY verb is operate's on the map, so a drive key's link hears its scope; it makes nothing.
        Send(desk, conn, "@k1 KEY MAKE main:a1b2c3d4 operate");
        Assert.Equal("ERR @k1 " + LinkAcceptor.ScopeWords("drive", desk.Services.Kernel.Beacon.MachineName), Str(ReadOn(desk, conn, l => l.Contains("@k1", StringComparison.Ordinal)), "line"));
        Assert.Single(desk.Services.Kernel.AppKeys.Keys);

        // S17: revoked on the desk while live — refused at once in 13.5.5's words with no id, the connection ended by the desk.
        var revoke = desk.Services.Actions.Execute(new ShowAction(ShowActionKind.AppKeyRevoke, kid), ActionOrigin.Desk);
        Assert.Equal(ActionStatus.Done, revoke.Status);
        var refused = LiveRefusal(desk, conn);
        Assert.Equal(("refused", AppKeyStore.RevokedWords), (Str(refused, "class"), Str(refused, "words")));
        ReadOn(desk, conn, l => l == "ERR LINK refused — " + AppKeyStore.RevokedWords);
        ClosedByDesk(desk, conn);
        PumpWhile(() => desk.Control.AppLinks != 0, 5000);
        Assert.Empty(Keys(desk).EnumerateArray());
        Assert.Contains(Rows(desk.Services, "KeyLinksEnded"), r => r.Target == kid && r.Message.Contains("1 live link refused", StringComparison.Ordinal));
        Assert.Contains(Rows(desk.Services, nameof(ShowActionKind.AppKeyMake)), r => r.Target == "main:" + StagewatchId && r.Outcome == "Done");
        Assert.Contains(Rows(desk.Services, nameof(ShowActionKind.AppKeyRevoke)), r => r.Target == kid && r.Outcome == "Done");

        // Dialled again, the revoked key is an unknown kid: S2's words, never counted.
        var (_, again) = Dialled(desk, DialDesk(desk, tls: true, files.Write("key2", key), "key"));
        Assert.Equal(("refused", PeerLink.ProofWords), (Str(again, "class"), Str(again, "words")));
        Assert.Equal(0, desk.Control.WrongProofPairs);

        // S19: the key and its K are in no reader.
        TestApp.FlushFiles(desk.Services);
        var readers = new[] { File.ReadAllText(Path.Combine(desk.Booted.Dir, "patterns.log")), Journal(desk.Services), new CommandRouter(desk.Services).StateJson(), desk.Services.ContextJson() };
        foreach (var canary in new[] { key, Convert.ToHexStringLower(PeerLink.KeyKey(key)) })
            Assert.All(readers, r => Assert.DoesNotContain(canary, r, StringComparison.OrdinalIgnoreCase));
        // Kept with the machine: the file never holds the key, and no K in clear on a sealing platform.
        var kept = File.ReadAllText(Path.Combine(desk.Booted.Dir, AppKeys.FileName));
        Assert.DoesNotContain(key, kept, StringComparison.Ordinal);
    }

    [AvaloniaFact]
    public void AKeyBoundToAnotherConnectorIsABindingRefusalNeverCountedAndKeyBindLetsItSpeakForTheRightOne()
    {
        using var files = new KitFiles();
        using var desk = new AcceptorDesk();
        var key = MakeKey(desk, "main:a1b2c3d4e5f6", "follow");
        var kid = desk.Services.Kernel.AppKeys.Keys.Single().Kid;
        var file = files.Write("key", key);

        // S18: proved by another connector — refused for its binding, the connection ended, never counted.
        var (other, binding) = Dialled(desk, DialDesk(desk, tls: false, file, "key"));
        Assert.Equal(("binding", "this key speaks for Stagewatch a1b2c3d4e5f6"), (Str(binding, "class"), Str(binding, "words")));
        ClosedByDesk(desk, other);
        Assert.Equal((0, 0), (desk.Control.WrongProofPairs, desk.Control.AppLinks));

        // KEY BIND from the wire — on a link the pairing token proved, the operator's own credential — is refused in the desk's words
        // and binds nothing; from the desk it binds, and the link it proves is the key's.
        var (operatorLink, _) = Dialled(desk, DialDesk(desk, tls: false, files.Write("code", desk.Token), "code"));
        Send(desk, operatorLink, $"@k1 KEY BIND {kid} main:{StagewatchId}");
        Assert.StartsWith("ERR @k1 " + KeyVerbs.DeskOnlyWords, Str(ReadOn(desk, operatorLink, l => l.Contains("@k1", StringComparison.Ordinal)), "line"), StringComparison.Ordinal);
        Send(desk, operatorLink, "@k2 KEY MAKE main:" + StagewatchId + " operate");
        Assert.StartsWith("ERR @k2 " + KeyVerbs.DeskOnlyWords, Str(ReadOn(desk, operatorLink, l => l.Contains("@k2", StringComparison.Ordinal)), "line"), StringComparison.Ordinal);
        Send(desk, operatorLink, $"@k3 KEY REVOKE {kid}");
        Assert.StartsWith("ERR @k3 " + KeyVerbs.DeskOnlyWords, Str(ReadOn(desk, operatorLink, l => l.Contains("@k3", StringComparison.Ordinal)), "line"), StringComparison.Ordinal);
        var kept = Assert.Single(desk.Services.Kernel.AppKeys.Keys);
        Assert.Equal("main:a1b2c3d4e5f6", kept.Bound);
        Assert.Null(desk.Services.Kernel.AppKeys.TakeShown());
        var bound = desk.Services.Actions.Execute(new ShowAction(ShowActionKind.AppKeyBind, kid, "main:" + StagewatchId + " post"), ActionOrigin.Desk);
        Assert.Equal(ActionStatus.Done, bound.Status);
        var (conn, linked) = Dialled(desk, DialDesk(desk, tls: false, files.Write("key2", key), "key"));
        Assert.Equal(("link", "key", "post"), (Str(linked, "ev"), Str(linked, "verified"), Str(linked, "scope")));

        // Bound elsewhere while live: the link it moved from ends at once.
        Assert.Equal(ActionStatus.Done, desk.Services.Actions.Execute(new ShowAction(ShowActionKind.AppKeyBind, kid, "playmap:0a1b2c3d"), ActionOrigin.Desk).Status);
        Assert.Equal("refused", Str(LiveRefusal(desk, conn), "class"));
        ClosedByDesk(desk, conn);
        // A bind that changes nothing is refused with the reason, and journalled.
        var same = desk.Services.Actions.Execute(new ShowAction(ShowActionKind.AppKeyBind, kid, "playmap:0a1b2c3d"), ActionOrigin.Desk);
        Assert.Equal(ActionStatus.Refused, same.Status);
        Assert.Contains("already", same.Message, StringComparison.Ordinal);
        Assert.Contains(Rows(desk.Services, nameof(ShowActionKind.AppKeyBind)), r => r.Target == kid && r.Outcome == "Refused");
    }

    [AvaloniaFact]
    public void ACodeOverTlsEnrolsAKeyAtFollowOverPlainItDoesNotAndANewCodeRevokesItAndEndsItsLink()
    {
        using var files = new KitFiles();
        using var desk = new AcceptorDesk();
        var code = files.Write("code", desk.Token);

        // S21, over plain: the code links at operate and enrols nothing.
        var (plain, plainLink) = Dialled(desk, DialDesk(desk, tls: false, code, "code"));
        Assert.Equal(("code", "operate"), (Str(plainLink, "verified"), Str(plainLink, "scope")));
        Assert.Equal(["final", "receipts"], plainLink["can"]!.AsArray().Select(n => n!.GetValue<string>()));   // no enrol on plain, and no stuck or expire
        Assert.DoesNotContain(desk.Peer.Events, e => Str(e, "ev") == "enrolled");
        Assert.Empty(desk.Services.Kernel.AppKeys.Keys);
        Send(desk, plain, "@x1 PING 1");
        ReadOn(desk, plain, l => l.StartsWith("OK @x1", StringComparison.Ordinal));

        // Over TLS: the verdict names the key it enrolled, the connector derives the same one, and the desk keeps it at follow.
        var (tls, tlsLink) = Dialled(desk, DialDesk(desk, tls: true, code, "code"));
        Assert.Equal(["final", "receipts", "enrol"], tlsLink["can"]!.AsArray().Select(n => n!.GetValue<string>()));
        var enrolled = TestApp.Pump(desk.Peer.Wait(e => Str(e, "ev") == "enrolled" && Str(e, "role") == "connector" && Int(e, "conn") == tls));
        Assert.True(enrolled["derived"]!.GetValue<bool>());
        var key = Assert.Single(desk.Services.Kernel.AppKeys.Keys);
        Assert.Equal((Str(enrolled, "kid"), "main:" + StagewatchId, "follow", AppKeys.Enrolled), (key.Kid, key.Bound, key.Scope, key.Made));
        Assert.Contains(Rows(desk.Services, "KeyEnrolled"), r => r.Target == "main:" + StagewatchId && r.Message.Contains(key.Kid, StringComparison.Ordinal));

        // From then on the connector proves with the enrolled key: verified link, at follow — a read runs, a verb is refused.
        var (enrolledConn, viaKey) = Dialled(desk, DialDesk(desk, tls: true, code, "code"));
        Assert.Equal(("link", key.Kid, "follow"), (Str(viaKey, "verified"), Str(viaKey, "kid"), Str(viaKey, "scope")));
        ReadOn(desk, enrolledConn, l => l.StartsWith("STATE {", StringComparison.Ordinal));
        Send(desk, enrolledConn, "@s1-11aa22bb CUE GO");
        Assert.Equal("ERR @s1-11aa22bb " + LinkAcceptor.ScopeWords("follow", desk.Services.Kernel.Beacon.MachineName),
            Str(ReadOn(desk, enrolledConn, l => l.Contains("@s1-11aa22bb", StringComparison.Ordinal)), "line"));

        // NEW TOKEN: the code's links and the enrolled key's end in 13.5.5's words, and the enrolled key is revoked with the old code.
        desk.Booted.Vm.State.Control.Token = PairingToken.New();
        Dispatcher.UIThread.RunJobs();
        foreach (var c in new[] { plain, tls, enrolledConn })
        {
            var r = LiveRefusal(desk, c);
            Assert.Equal(("refused", AppKeyStore.ReplacedWords), (Str(r, "class"), Str(r, "words")));
            ClosedByDesk(desk, c);
        }
        Assert.Empty(desk.Services.Kernel.AppKeys.Keys);
        Assert.Contains(Rows(desk.Services, "KeysRevokedByNewToken"), r => r.Message.Contains(key.Kid, StringComparison.Ordinal));
    }

    [AvaloniaFact]
    public void AKeySentInClearIsMarkedOnItsRowInStateAndTheSuperCheckAndOpensNothing()
    {
        using var desk = new AcceptorDesk();
        var key = MakeKey(desk, "main:" + StagewatchId, "operate");
        using (var plain = new TcpClient())
        {
            TestApp.Pump(plain.ConnectAsync("127.0.0.1", desk.Port).ContinueWith(_ => true, TaskScheduler.Default));
            var stream = plain.GetStream();
            stream.Write(Encoding.UTF8.GetBytes("AUTH " + key + "\nSTATUS\n"));
            using var reader = new StreamReader(stream, Encoding.UTF8);
            // Read off the desk's thread, pumped here, so the desk's own work goes on while the test waits for its answer.
            string? Next()
            {
                var line = TestApp.Pump(reader.ReadLineAsync());
                while (line is not null && line.StartsWith("NONCE ", StringComparison.Ordinal)) line = TestApp.Pump(reader.ReadLineAsync());   // the greeting of a connection not paired
                return line;
            }
            Assert.Equal("ERR " + WireProof.UseProof, Next());
            Assert.Equal("ERR " + ControlProtocol.StateNotPaired, Next());                           // a key in clear opens nothing
        }
        PumpWhile(() => desk.Services.Kernel.AppKeys.Keys.Single().ClearSeen is null, 5000);
        Dispatcher.UIThread.RunJobs();
        var mark = Keys(desk).EnumerateArray().Single().GetProperty("clearSeen");
        Assert.Equal(("127.0.0.1", "wire", 1), (mark.GetProperty("address").GetString(), mark.GetProperty("via").GetString(), mark.GetProperty("count").GetInt32()));
        var row = Assert.Single(SuperCheck.Run(desk.Services.Metrics.GatherFacts()).Rows, r => r.Item == "App keys");
        Assert.Equal(CheckLight.Amber, row.Light);
        Assert.Single(Rows(desk.Services, "KeySeenInClear"));
        TestApp.FlushFiles(desk.Services);
        Assert.DoesNotContain(key, Journal(desk.Services), StringComparison.Ordinal);
        Assert.DoesNotContain(key, File.ReadAllText(Path.Combine(desk.Booted.Dir, "patterns.log")), StringComparison.Ordinal);
    }

    /// <summary>
    /// 13.6.9's web leg: an app key in X-Patterns-Token on the plain web listener, from what this desk counts as another machine (its
    /// trust of loopback off), opens no route, and is marked on its row (via web), in STATE and on the Super Check, amber — never written
    /// to the journal or the log.
    /// </summary>
    [AvaloniaFact]
    public void AKeySentInClearOnThePlainWebListenerIsMarkedViaWebAndOpensNothing()
    {
        using var desk = new AcceptorDesk();
        TestListeners.Up(desk.Control, desk.Booted.Vm.State.Control, () => desk.Control.Listening, "the desk's listeners");
        var key = MakeKey(desk, "main:" + StagewatchId, "operate");
        var web = desk.Booted.Vm.State.Control.HttpPort;
        string status;
        using (var plain = new TcpClient())
        {
            TestApp.Pump(plain.ConnectAsync("127.0.0.1", web).ContinueWith(_ => true, TaskScheduler.Default));
            var stream = plain.GetStream();
            stream.Write(Encoding.ASCII.GetBytes($"GET /api/state HTTP/1.1\r\nHost: 127.0.0.1:{web}\r\nX-Patterns-Token: {key}\r\nConnection: close\r\n\r\n"));
            using var reader = new StreamReader(stream, Encoding.UTF8);
            status = TestApp.Pump(reader.ReadLineAsync()) ?? "";
        }
        Assert.StartsWith("HTTP/1.1 4", status, StringComparison.Ordinal);                            // a key opens no route on the web remote
        PumpWhile(() => desk.Services.Kernel.AppKeys.Keys.Single().ClearSeen is null, 5000);
        Dispatcher.UIThread.RunJobs();
        var mark = Keys(desk).EnumerateArray().Single().GetProperty("clearSeen");
        Assert.Equal(("127.0.0.1", "web", 1), (mark.GetProperty("address").GetString(), mark.GetProperty("via").GetString(), mark.GetProperty("count").GetInt32()));
        var row = Assert.Single(SuperCheck.Run(desk.Services.Metrics.GatherFacts()).Rows, r => r.Item == "App keys");
        Assert.Equal(CheckLight.Amber, row.Light);
        Assert.Single(Rows(desk.Services, "KeySeenInClear"));
        TestApp.FlushFiles(desk.Services);
        Assert.DoesNotContain(key, Journal(desk.Services), StringComparison.Ordinal);
        Assert.DoesNotContain(key, File.ReadAllText(Path.Combine(desk.Booted.Dir, "patterns.log")), StringComparison.Ordinal);
    }

    /// <summary>
    /// 13.5.7 on a node: the pairing token proved over TLS links at operate, and nothing enrols — the verdict offers no enrol and names
    /// no key, the connector derives none, and the node keeps none, for a node has no Nodes page to list an enrolment or revoke it from.
    /// </summary>
    [AvaloniaFact]
    public void ACodeProvedOverTlsToANodeLinksAndEnrolsNothing()
    {
        var trusted = ControlService.TrustLoopback;
        ControlService.TrustLoopback = false;
        var dir = TempRun.Folder("patterns-node-keys-").Path;
        NodeHost? host = null;
        ScriptedPeer? peer = null;
        using var files = new KitFiles();
        try
        {
            var (wire, http, audience) = TestPorts.Three();
            var s = SettingsStore.Fresh();
            s.Control.Enabled = true;
            s.Control.TcpPort = wire;
            s.Control.HttpPort = http;
            s.Control.AudiencePort = audience;
            s.Control.Token = PairingToken.New();
            s.Twin.AcceptCallers = false;
            s.Watchdog.BeaconListenPort = TestPorts.Free();
            s.Watchdog.BeaconPort = s.Watchdog.BeaconListenPort;
            File.WriteAllText(Path.Combine(dir, "patterns.settings.json"), JsonUtil.Serialize(s));
            host = NodeHost.Build(NodeKind.Arcade, new SettingsStore(dir));
            Assert.False(host.Kernel.IsDesk);
            host.Start();
            TestListeners.Up(host.Control, host.State.Control, () => host.Control.Listening, "the node's wire");
            peer = ScriptedPeer.Start();
            var dial = new JsonObject
            {
                ["do"] = "dial", ["carrier"] = "line", ["port"] = wire, ["tls"] = true, ["pin"] = host.Kernel.Tls.Fingerprint["sha256:".Length..],
                ["node"] = Node("main", StagewatchId), ["key"] = new JsonObject { ["file"] = files.Write("code", s.Control.Token), ["kind"] = "code" },
                ["meant"] = Node("patterns", host.Kernel.ShowBusId), ["name"] = "Stagewatch",
            };
            var conn = Int(TestApp.Pump(peer.Ask(dial)), "conn");
            var linked = TestApp.Pump(peer.Wait(e => Str(e, "ev") is "link" or "refused" && Str(e, "role") == "connector" && Int(e, "conn") == conn));
            Assert.Equal(("link", "code", "operate"), (Str(linked, "ev"), Str(linked, "verified"), Str(linked, "scope")));
            Assert.Equal(["receipts"], linked["can"]!.AsArray().Select(n => n!.GetValue<string>()));       // a node keeps receipts and enrols nothing; it has no cue history to say a final from
            Assert.DoesNotContain(peer.Events, e => Str(e, "ev") == "enrolled");
            Assert.Empty(host.Kernel.AppKeys.Keys);
        }
        finally
        {
            if (peer is not null) TestApp.Pump(peer.DisposeAsync().AsTask().ContinueWith(_ => true, TaskScheduler.Default));
            host?.Dispose();
            TempRun.Remove(dir);
            ControlService.TrustLoopback = trusted;
        }
    }

    /// <summary>
    /// 13.5.5 across a restart: a key enrolled from one token, kept while the desk is closed and the token changed, is revoked as the desk
    /// starts — one journal row naming it, nothing hidden left in the list — and an issued key stays.
    /// </summary>
    [AvaloniaFact]
    public void AKeyEnrolledFromATokenThatChangedWhileTheDeskWasClosedIsRevokedAtStart()
    {
        var dir = TempRun.Folder("patterns-keys-restart-").Path;
        try
        {
            string enrolledKid;
            string issuedKid;
            var first = TestApp.Boot(dir: dir);
            try
            {
                var store = first.Services.Kernel.AppKeys;
                var enrolled = store.Enrol(RandomNumberGenerator.GetBytes(32), "main:" + StagewatchId, "Stagewatch", PairingToken.New(), DateTime.UtcNow);
                Assert.NotNull(enrolled);
                enrolledKid = enrolled.Value.Key.Kid;
                var issued = store.Make("playmap:0a1b2c3d", "drive", DateTime.UtcNow).Made;
                Assert.NotNull(issued);
                issuedKid = issued.Kid;
                Assert.Equal(2, store.Keys.Count);
            }
            finally
            {
                first.Dispose();
            }
            // The token changes while the desk is closed: the settings name another one.
            var second = TestApp.Boot(dir: dir, prepare: d =>
            {
                var s = SettingsStore.Fresh();
                s.Control.Token = PairingToken.New();
                File.WriteAllText(Path.Combine(d, "patterns.settings.json"), JsonUtil.Serialize(s));
            });
            try
            {
                var kept = second.Services.Kernel.AppKeys.Keys;
                Assert.Equal(issuedKid, Assert.Single(kept).Kid);
                Assert.Contains(Rows(second.Services, "KeysRevokedByNewToken"), r => r.Message.Contains(enrolledKid, StringComparison.Ordinal) && r.Message.Contains("while the desk was closed", StringComparison.Ordinal));
            }
            finally
            {
                second.Dispose();
            }
        }
        finally
        {
            TempRun.Remove(dir);
        }
    }

    /// <summary>
    /// Kept at rest: where the seal is required and refuses (DPAPI on Windows), nothing is written — never K in plain — the file an older
    /// list was kept in goes, and the store says why (the Super Check's amber); off Windows, the owner-only file is created owner-only.
    /// </summary>
    [Fact]
    public void KeysTheSealRefusesAreWrittenNowhereAndAnOwnerOnlyFileIsBornOwnerOnly()
    {
        using var folder = TempRun.Folder("patterns-keys-seal-");
        var path = Path.Combine(folder.Path, AppKeys.FileName);
        var plain = new AppKeyStore(folder.Path, static _ => null, sealRequired: false);
        var made = plain.Make("main:" + StagewatchId, "drive", DateTime.UtcNow).Made!;
        Assert.True(File.Exists(path));
        Assert.Equal("", plain.Problem);
        if (!OperatingSystem.IsWindows()) Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite, File.GetUnixFileMode(path));
        Assert.Empty(Directory.GetFiles(folder.Path, "*.tmp"));

        // A store whose seal must hold and refuses: it reads the older list, and its next change writes nothing and removes that file.
        var refused = new AppKeyStore(folder.Path, static _ => null, sealRequired: true);
        Assert.Equal(made.Kid, Assert.Single(refused.Keys).Kid);
        var second = refused.Make("playmap:0a1b2c3d", "follow", DateTime.UtcNow).Made!;
        Assert.False(File.Exists(path));
        Assert.Equal(AppKeyStore.UnsealedWords, refused.Problem);
        Assert.Contains("this run only", refused.Protection, StringComparison.Ordinal);
        Assert.Equal(2, refused.Keys.Count);                                                          // held in memory for this run
        foreach (var file in Directory.GetFiles(folder.Path))
        {
            var text = File.ReadAllText(file);
            Assert.DoesNotContain(Convert.ToHexStringLower(made.K), text, StringComparison.Ordinal);
            Assert.DoesNotContain(Convert.ToHexStringLower(second.K), text, StringComparison.Ordinal);
        }
    }
}
