using System.Text;
using System.Text.Json.Nodes;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

public sealed partial class PeerLinkWireTests
{
    /// <summary>
    /// Round 97: the node device's session against the scripted peer as a level-0 line acceptor (S22's acceptor), which greets with
    /// STATE and then NONCE at connect and with STATE again once paired. The STATE before the proof is discarded and counted, never
    /// taken; the proof is §7's AUTH-PROOF on the 32-hex NONCE over the HELLO name; the STATE after it is taken; the context is asked
    /// for only once paired; each command is answered by its own id, read by 13.12.3; no form of the code crosses the wire.
    /// </summary>
    [Fact]
    public async Task ANodeDeviceIsProvedByAuthProofOnALevelZeroAcceptorAndDiscardsWhatCameBeforeTheProof()
    {
        using var files = new KitFiles();
        var code = PairingToken.New();
        await using var peer = ScriptedPeer.Start();
        var listening = await peer.Ask(new JsonObject
        {
            ["do"] = "listen", ["carrier"] = "line", ["tls"] = "plain", ["node"] = Node("playmap", "9d9d9d9d"), ["level"] = 0,
            ["keys"] = new JsonArray(new JsonObject { ["file"] = files.Write("code", code), ["kind"] = "code" }),
        });
        using var lines = await Lines.Dial(Int(listening, "port"), tls: false);
        var s = new ShowBusNodeSession(code, "FOH", "a1b2c3d4", "PlayMap");
        foreach (var frame in s.OnOpened()) await lines.Send(Encoding.UTF8.GetString(frame).TrimEnd('\n'));
        var sent = new List<string> { "@dh HELLO Patterns FOH" };
        string? nonce = null;
        while (!(s.Up && s.StateTaken > 0))
        {
            var line = await lines.Next();
            if (line.StartsWith("NONCE ", StringComparison.Ordinal)) nonce = line[6..];
            foreach (var next in s.OnReceived(line).SendNext)
            {
                var text = Encoding.UTF8.GetString(next).TrimEnd('\n');
                sent.Add(text);
                await lines.Send(text);
            }
        }
        Assert.Equal("auth-proof", Str(await peer.Wait(e => Str(e, "ev") == "paired"), "how"));
        Assert.Equal((1, 1, true), (s.StateDiscarded, s.StateTaken, s.Paired));
        Assert.Equal(new[] { "@dh HELLO Patterns FOH", "@dp AUTH-PROOF " + PeerLink.LegacyProof(code, nonce, "Patterns FOH"), "@do SUBSCRIBE OFF", "@dc SUBSCRIBE CONTEXT" }, sent);

        var command = Encoding.UTF8.GetString(Assert.Single(s.Encode("CUE GO", out _))).TrimEnd('\n');
        Assert.Equal("@d1-a1b2c3d4 CUE GO", command);
        await lines.Send(command);
        var answer = await lines.NextMatching(l => l.StartsWith("OK @d1-a1b2c3d4", StringComparison.Ordinal) || l.StartsWith("ERR @d1-a1b2c3d4", StringComparison.Ordinal));
        var reply = s.OnReceived(answer);
        Assert.Equal((true, "d1-a1b2c3d4"), (reply.Ack, reply.AckKey));
        Assert.StartsWith("applied", s.LastOutcome, StringComparison.Ordinal);
        Assert.Equal("CUE GO", Str(await peer.Wait(e => Str(e, "ev") == "command" && Str(e, "id") == "d1-a1b2c3d4"), "line"));

        Assert.DoesNotContain(peer.Events, e => Str(e, "ev") == "read" && (Str(e, "line") ?? "").StartsWith("AUTH ", StringComparison.Ordinal));
        foreach (var secret in CodeForms(code))
            Assert.DoesNotContain(peer.Events, e => Str(e, "ev") == "read" && (Str(e, "line") ?? "").Contains(secret, StringComparison.Ordinal));
        var record = s.Record(true, true, "9d9d9d9d");
        Assert.Equal(("live", 0, "legacy", "amber"), (record.State, record.Level, record.Proof, record.Light));
    }
}
