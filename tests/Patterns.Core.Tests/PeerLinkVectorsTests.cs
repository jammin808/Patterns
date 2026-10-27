using System.Globalization;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Show Bus 1.3 §13.20's <c>```peerlink</c> block, read from this tree's copy (<c>docs/SHOWBUS.md</c>), every row run on this
/// desk's PeerLink core: the crypto rows through its canonical form, K, kid, transcript, proofs, lid, S and enrolled key, the
/// proofs an acceptor refuses, the link a proof row's carrier and bind make, and 13.6.8's legacy proof — the four rows it does
/// not prove among them; the parse rows through what an acceptor reads LINK and a side call's session with; the pure rows
/// through liveness, the redial wait, <see cref="LinkClock"/> and 13.12.3's table; the machine rows through
/// <see cref="PeerLinkMachine"/> on injected time — each at j = 0, ½ and just under 1, so a wait's range is reached at both
/// ends — and the acceptor's through <see cref="ReceiptBook"/>. A row of a kind this core does not run fails here until it
/// does. Below the rows, what no row reaches: LINK end to end over the first proof row, CERTIFICATE CHANGED and a person's
/// typed hex, the Trust press on a first pin, matched pongs and the clock's schedule, level 0's NONCE rule, the final answer,
/// the receipt record's bounds, a hold across a restart on two clocks, and the record's shape and secrets.
/// </summary>
public sealed class PeerLinkVectorsTests
{
    /// <summary>Every kind of row 13.20 names; a row of another kind fails <see cref="EveryRowIsOfAKindThisCoreRuns"/>.</summary>
    private static readonly string[] Kinds = { "canon", "key", "proof", "refuse", "legacy", "parse", "liveness", "redial", "errms", "clock", "outcome", "machine" };

    /// <summary>The j a machine row runs at: 13.15.1's ends and its middle.</summary>
    private static readonly double[] Js = { 0, 0.5, 0.9999999 };

    private const string Node = "Hall A";

    private static readonly Lazy<List<string>> Block = new(() => Fenced("peerlink"));

    public static TheoryData<int, string> Rows()
    {
        var data = new TheoryData<int, string>();
        var n = 0;
        foreach (var row in Block.Value) data.Add(++n, row);
        return data;
    }

    [Fact]
    public void EveryRowIsOfAKindThisCoreRuns()
    {
        Assert.NotEmpty(Block.Value);
        var kinds = Block.Value.Select(r => Str(Parse(r), "case") ?? "").ToList();
        Assert.All(kinds, k => Assert.Contains(k, Kinds));
        Assert.All(Kinds, k => Assert.Contains(k, kinds));
        // The block is ASCII: an invisible character can reach a row only as a JSON \u escape (13.20).
        Assert.All(Block.Value, r => Assert.True(r.All(char.IsAscii), r));
    }

    [Theory]
    [MemberData(nameof(Rows))]
    public void EveryRowHoldsOnThisDesksCore(int n, string row)
    {
        Assert.True(n > 0);
        var r = Parse(row);
        switch (Str(r, "case"))
        {
            case "canon": Canon(r); break;
            case "key": Key(r); break;
            case "proof": Proof(r); break;
            case "refuse": Refuse(r); break;
            case "legacy": Legacy(r); break;
            case "parse": ParseRow(r); break;
            case "liveness":
                Assert.Equal(Str(r, "state"), LinkRecord.StateWord(PeerLink.Liveness(TimeSpan.FromMilliseconds(Int(r, "silenceMs")!.Value))));
                break;
            case "redial":
                var wait = ShowBusLiveness.Redial((int)Int(r, "n")!.Value, Num(r, "j")!.Value).TotalMilliseconds;
                Assert.InRange(wait, Int(r, "ms")!.Value - Num(r, "tol")!.Value, Int(r, "ms")!.Value + Num(r, "tol")!.Value);
                break;
            case "errms":
                Assert.Equal(Int(r, "errMs"), LinkClock.ErrMs(Int(r, "rttMs")!.Value));
                break;
            case "clock": Clock(r); break;
            case "outcome": Outcome(r); break;
            case "machine": Machine(r); break;
            default: Assert.Fail("a row of a kind this core does not run: " + row); break;
        }
    }

    // ---- crypto rows ---------------------------------------------------------------------------------------------------

    private static void Canon(JsonElement r)
    {
        var canonical = PeerLink.Canon(Str(r, "code"));
        Assert.Equal(Str(r, "canon"), canonical);
        if (Str(r, "K") is { } k)
        {
            Assert.Equal(k, Hex(PeerLink.CodeKey(canonical!)));
            Assert.Equal("code", Str(r, "kid"));
        }
    }

    private static void Key(JsonElement r)
    {
        var k = PeerLink.KeyKey(Str(r, "key")!);
        Assert.Equal(Str(r, "K"), Hex(k));
        Assert.Equal(Str(r, "kid"), PeerLink.Kid(k));
    }

    /// <summary>T and all that is made from it; each end takes the other's proof and no other; the link the row's carrier and bind make carries the row's flags.</summary>
    private static void Proof(JsonElement r)
    {
        var t = Transcript(r, null, default);
        Assert.NotNull(t);
        var k = Convert.FromHexString(Str(r, "K")!);
        if (Str(r, "Tsha256") is { } th) Assert.Equal(th, Hex(SHA256.HashData(Encoding.UTF8.GetBytes(t))));
        var kid = Str(r, "kid")!;
        if (kid == "code") Assert.True(Str(r, "code") is not { } code || Hex(PeerLink.CodeKey(PeerLink.Canon(code)!)) == Str(r, "K"));
        else Assert.Equal(kid, PeerLink.Kid(k));

        var client = PeerLink.ClientProof(k, t);
        var server = PeerLink.ServerProof(k, t);
        Assert.Equal(Str(r, "clientProof"), client);
        Assert.Equal(Str(r, "serverProof"), server);
        Assert.Equal(Str(r, "lid"), PeerLink.Lid(t));
        Assert.True(PeerLink.Verify(client, Str(r, "clientProof")));
        Assert.True(PeerLink.Verify(server, Str(r, "serverProof")));
        Assert.False(PeerLink.Verify(client, Str(r, "serverProof")));
        Assert.False(PeerLink.Verify(client, Str(r, "clientProof")!.ToUpperInvariant()));            // hex is lowercase: another shape is a wrong proof
        if (Str(r, "session") is { } s)
        {
            Assert.Equal("ws", Str(r, "carrier"));
            Assert.Equal(s, PeerLink.Session(k, t));
        }

        var bind = Str(r, "bind")!;
        if (Str(r, "enrolK") is { } enrolK)
        {
            var link = PeerLink.EnrolKey(k, t);
            Assert.True(PeerLink.MayEnrol(kid, bind));
            Assert.Equal(enrolK, Hex(link));
            Assert.Equal(Str(r, "enrolKid"), PeerLink.Kid(link));
        }
        if (r.TryGetProperty("enrol", out var enrol))
        {
            Assert.Equal(JsonValueKind.False, enrol.ValueKind);
            Assert.False(PeerLink.MayEnrol(kid, bind));
        }
        if (Str(r, "carrier") == "line")
        {
            var (ct, cid) = Split(Str(r, "client")!);
            var ask = PeerLink.ReadLinkLine($"@l1 LINK {Int(r, "offer")} {ct} {cid} {Str(r, "cnonce")} {kid}").Ask;
            Assert.Equal(new LinkAsk(Int(r, "offer")!.Value, ct, cid, Str(r, "cnonce")!, kid), ask);
            Assert.Equal(client, PeerLink.ReadLinkLine("@l2 LINK PROOF " + client).Proof);
        }

        // The link the row's carrier and bind make, verified: live, pinned over TLS, with the row's flags — plain and code-plain exactly.
        var m = new PeerLinkMachine(Settings(Str(r, "carrier") == "ws" ? LinkCarrier.Ws : LinkCarrier.Line, kid), null, 0, () => 0.5);
        if (bind == "plain") m.NoTls(0);
        else m.Certificate(0, bind["tls:sha256:".Length..]);
        m.Verified(0);
        var flags = m.Flags(0);
        var want = Strings(r, "flags");
        Assert.All(want, f => Assert.Contains(f, flags));
        Assert.Equal(want.Contains("plain"), flags.Contains("plain"));
        Assert.Equal(want.Contains("code-plain"), flags.Contains("code-plain"));
        Assert.Equal(LinkState.Live, m.State(0));
        Assert.Equal(bind == "plain" ? "none" : "pinned", m.Tls());
    }

    /// <summary>A refuse row's proof is the first proof row's with one field changed; over the first row's T the acceptor refuses it.</summary>
    private static void Refuse(JsonElement r)
    {
        var first = FirstProof("line");
        var t = Transcript(first, null, default)!;
        var k = Convert.FromHexString(Str(first, "K")!);
        var given = Str(r, "clientProof");
        Assert.Equal("refused", Str(r, "expect"));
        Assert.False(PeerLink.Verify(PeerLink.ClientProof(k, t), given));
        var why = Str(r, "why")!;
        if (why == "role")
        {
            Assert.Equal(PeerLink.ServerProof(k, t), given);
            return;
        }
        var changed = Transcript(first, why, r);
        Assert.NotNull(changed);
        Assert.Equal(given, PeerLink.ClientProof(k, changed));
    }

    /// <summary>13.6.8's legacy proof as the core makes it — and the four rows it does not make: an invisible character in the token, a NONCE not 32 lowercase hex.</summary>
    private static void Legacy(JsonElement r)
    {
        var nonce = Str(r, "nonce")!;
        var code = Str(r, "code");
        if (Str(r, "proof") is { } proof)
        {
            Assert.Equal(proof, PeerLink.LegacyProof(code, nonce, Str(r, "name")!));
            Assert.Equal(proof, WireProof.Proof(code!, nonce, Str(r, "name")!));            // the key the desk's 9697 checks the proof with is the same
            return;
        }
        Assert.Equal(JsonValueKind.False, r.GetProperty("prove").ValueKind);
        if (code is not null)
        {
            Assert.True(PeerLink.IsProvableNonce(nonce));
            Assert.Null(PeerLink.LegacyKey(code));
            Assert.Null(PeerLink.LegacyProof(code, nonce, "Stagewatch"));
            Assert.Equal(PeerLink.InvisibleWords, PairingToken.EntryRefusal(code));           // and the Remote page does not take it
        }
        else
        {
            Assert.False(PeerLink.IsProvableNonce(nonce));
            Assert.Null(PeerLink.LegacyProof("k7qm-3xwd p9ra", nonce, "Stagewatch"));
        }
    }

    // ---- parse rows ----------------------------------------------------------------------------------------------------

    private static void ParseRow(JsonElement r)
    {
        var expect = r.GetProperty("expect");
        switch (Str(r, "carrier"))
        {
            case "line":
                var line = PeerLink.ReadLinkLine(Str(r, "line")!);
                Assert.Null(line.Ask);
                Assert.Null(line.Proof);
                Assert.Equal(expect.GetString(), "ERR LINK " + line.Refused);
                break;
            case "ws":
                var message = PeerLink.ReadLinkMessage(Str(r, "query")!, r.GetProperty("message"));
                Assert.False(Bool(expect, "ok"));
                Assert.Null(message.Ask);
                Assert.Equal(Str(expect, "class"), message.Refused);
                break;
            case "http":
                // A side call's Authorization against the first ws proof row's live link (13.20): clear is a call in clear from
                // another machine, ends the lid whose link the acceptor ends; a row naming no ends carries a header 13.7.1 does not take.
                var ws = FirstProof("ws");
                var header = Str(r, "authorization");
                var check = PeerLink.CheckSession(header, lid => lid == Str(ws, "lid") ? Str(ws, "session") : null, Bool(r, "clear") == true);
                var names = expect.TryGetProperty("ends", out var ends);
                if (!names) Assert.Null(PeerLink.ReadSessionHeader(header));
                Assert.Equal(Int(expect, "status"), check.Status);
                Assert.Equal(Bool(expect, "relink"), check.Relink);
                Assert.Equal(names && ends.ValueKind == JsonValueKind.String ? ends.GetString() : null, check.Ends);
                break;
            default:
                Assert.Fail("a parse row's carrier is line, ws or http");
                break;
        }
    }

    // ---- pure rows -----------------------------------------------------------------------------------------------------

    private static void Clock(JsonElement r)
    {
        var tol = Num(r, "tol") ?? 0;
        if (r.TryGetProperty("samples", out var samples))
        {
            var clock = new LinkClock();
            var refused = samples.EnumerateArray().Count(s => !clock.Sample(Ticks(s[0]), Ticks(s[1]), Ticks(s[2]), Ticks(s[3])));
            Assert.Equal(Int(r, "refused") ?? 0, refused);
            Assert.Equal(refused, clock.Refused);
            Assert.Equal(samples.GetArrayLength() - refused, clock.Samples);
            if (Num(r, "offsetMs") is { } offset) Assert.InRange(clock.Offset.TotalMilliseconds, offset - tol, offset + tol);
            if (Int(r, "rttMs") is { } rtt) Assert.Equal(rtt, (long)clock.Delay.TotalMilliseconds);
            if (Int(r, "errMs") is { } err) Assert.InRange(clock.Error.TotalMilliseconds, err - tol, err + tol);
            if (Bool(r, "apart") is { } apart) Assert.Equal(apart, clock.IsApart);
            return;
        }
        var best = r.GetProperty("best");
        var offsetMs = TimeSpan.FromMilliseconds(Num(best, "offsetMs")!.Value);
        var errMs = TimeSpan.FromMilliseconds(Int(best, "errMs")!.Value);
        if (Bool(r, "apart") is { } a) Assert.Equal(a, LinkClock.Apart(offsetMs, errMs));
        if (Int(best, "ageMs") is { } ageMs)
        {
            var age = TimeSpan.FromMilliseconds(ageMs);
            if (Bool(r, "fresh") is { } fresh) Assert.Equal(fresh, LinkClock.Fresh(age));
            foreach (var fit in r.EnumerateObject().Where(p => p.Name.StartsWith("fitFor", StringComparison.Ordinal)))
                Assert.Equal(fit.Value.ValueKind == JsonValueKind.True, LinkClock.FitFor(age, errMs, TimeSpan.FromMilliseconds(int.Parse(fit.Name["fitFor".Length..], CultureInfo.InvariantCulture))));
        }
    }

    private static void Outcome(JsonElement r)
    {
        var reading = Reading(r.GetProperty("answer"));
        Assert.NotNull(reading);
        Assert.Equal(Str(r, "outcome"), reading.Value.Outcome.Word());
        if (Str(r, "words") is { } words) Assert.Equal(words, reading.Value.Words);
    }

    /// <summary>An outcome row's answer, read by the core's 13.12.3.</summary>
    private static LinkReading? Reading(JsonElement a)
    {
        if (Str(a, "line") is { } line) return PeerLink.ReadLine(line)?.Reading;
        if (a.TryGetProperty("ws", out var ws)) return PeerLink.ReadReceipt(ws);
        if (Int(a, "http") is { } status) return PeerLink.ReadHttp((int)status, a.TryGetProperty("body", out var body) ? body.GetRawText() : null);
        if (Bool(a, "written") is { } written)
            return PeerLink.ReadUnanswered(written, TimeSpan.FromMilliseconds(Int(a, "silenceMs") ?? 0), written && Str(a, "link") == "down", Node,
                Str(a, "link") is { } link ? "the link is " + link : "not sent");
        return null;
    }

    // ---- machine rows --------------------------------------------------------------------------------------------------

    private static void Machine(JsonElement r)
    {
        if (Str(r, "role") == "acceptor")
        {
            Acceptor(r);
            return;
        }
        var waits = new List<(long At, long Wait)>();
        foreach (var j in Js) Connector(r, j, waits);
        foreach (var x in Items(r, "expect").Where(e => e[1].TryGetProperty("waitMs", out _)))
        {
            var range = x[1].GetProperty("waitMs");
            var seen = waits.Where(w => w.At == x[0].GetInt64()).Select(w => w.Wait).ToList();
            Assert.True(seen.Contains(range[0].GetInt64()) && seen.Contains(range[1].GetInt64()),
                $"{Str(r, "name")}: j at 0 and just under 1 reach both ends of {range.GetRawText()} at {x[0].GetInt64()} (waits: {string.Join(", ", seen)})");
        }
    }

    private static void Connector(JsonElement r, double j, List<(long At, long Wait)> waits)
    {
        var name = Str(r, "name");
        var config = r.GetProperty("config");
        var settings = Settings(Str(config, "carrier") == "ws" ? LinkCarrier.Ws : LinkCarrier.Line, Str(config, "key"), Strings(config, "can")) with { ShowNeeds = Bool(config, "needs") == true };
        var pinned = Str(FirstProof("line"), "bind")!["tls:sha256:".Length..];
        var pin = Str(config, "pin") is { } p && p.StartsWith("node:", StringComparison.Ordinal) ? new LinkPin(p[5..], pinned, PinTrust.Proof) : null;
        var floor = config.TryGetProperty("floor", out var f) ? new LinkFloor(f.GetProperty("tls").GetBoolean(), f.GetProperty("level").GetInt32(), f.GetProperty("proof").GetString()!) : null;
        var m = new PeerLinkMachine(settings, new LinkKept(pin, floor, null, null), 0, () => j);
        var log = new List<LinkWrite>();
        var from = 0;
        var sends = new Dictionary<string, string>(StringComparer.Ordinal);
        var events = Items(r, "events");
        var next = 0;

        void Apply(JsonElement e)
        {
            var t = e[0].GetInt64();
            var a = e.GetArrayLength() > 2 ? e[2] : default;
            switch (e[1].GetString())
            {
                case "dial": m.Dial(t); break;
                case "connected": from = log.Count; m.Connected(t); break;
                case "answer":
                    if (Bool(a, "tls") == false) m.NoTls(t);
                    if (Str(a, "cert") is { } cert) m.Certificate(t, cert);
                    if (Str(a, "line") is { } line) m.LineAnswer(t, line);
                    if (Str(a, "first") == "frame") m.FirstFrame(t);
                    break;
                case "verified": from = log.Count; m.Verified(t, Str(a, "inst")); break;
                case "refused": m.Refused(t, Str(a, "class")!, (int)(Int(a, "retry") ?? 0)); break;
                case "closed": m.Closed(t); break;
                case "heard": m.Heard(t); break;
                case "frame": m.Frame(t); break;
                case "beat": m.Beat(t, true); break;
                case "beat-stuck": m.Beat(t, false); break;
                case "ping": m.Ping(t, Int(a, "t1")!.Value); break;
                case "pong" or "pong-stuck": m.Pong(t, Int(a, "t1"), Int(a, "t2"), Int(a, "t3"), e[1].GetString() == "pong-stuck"); break;
                case "evaluate": m.Evaluate(t); break;
                case "hello": m.Hello(t, Str(a, "id")!, Str(a, "tls"), Str(a, "inst"), Str(a, "host")); break;
                case "send":
                    for (var i = 0; i < Int(a, "count"); i++)
                    {
                        var id = m.NextId();
                        sends[(sends.Count + 1).ToString(CultureInfo.InvariantCulture)] = id;
                        m.Send(t, id);
                    }
                    break;
                case "written": m.Written(t, Str(a, "id")!); break;
                case "receipts":
                    m.Receipts(t, a.EnumerateObject().ToDictionary(x => x.Name, x => x.Value.ValueKind == JsonValueKind.Object ? PeerLink.ReadReceipt(x.Value) : (LinkReading?)null));
                    break;
                case "side-call": m.SideCall(t, Reading(a)!.Value); break;
                case "restart": m = new PeerLinkMachine(settings, m.Kept, t, () => j); break;
                default: Assert.Fail($"{name}: an event 13.20 does not name ({e[1].GetString()})"); break;
            }
            log.AddRange(m.Drain());
        }

        foreach (var x in Items(r, "expect"))
        {
            var at = x[0].GetInt64();
            while (next < events.Count && events[next][0].GetInt64() <= at) Apply(events[next++]);
            var where = $"{name} at {at} (j {j.ToString(CultureInfo.InvariantCulture)})";
            foreach (var field in x[1].EnumerateObject())
            {
                var v = field.Value;
                var rec = m.Record(at);
                var (ok, got) = field.Name switch
                {
                    "state" => (rec.State == v.GetString(), rec.State),
                    "light" => (rec.Light == v.GetString(), rec.Light),
                    "show" => (rec.Show == v.GetString(), rec.Show),
                    "refusal" => (rec.Refusal == v.GetString(), rec.Refusal),
                    "tls" => (rec.Tls == v.GetString(), rec.Tls),
                    "flags" => (v.EnumerateArray().All(s => rec.Flags.Contains(s.GetString()!)), string.Join(",", rec.Flags)),
                    "samples" => (rec.Clock.Samples == v.GetInt32(), rec.Clock.Samples.ToString(CultureInfo.InvariantCulture)),
                    "counts" => (v.EnumerateObject().All(c => Count(rec.Counts, c.Name) == c.Value.GetInt32()), rec.Counts.ToString()),
                    "waitMs" => (m.WaitMs >= v[0].GetInt64() && m.WaitMs <= v[1].GetInt64(), m.WaitMs.ToString(CultureInfo.InvariantCulture)),
                    "heldUntil" => (m.HeldUntilWall(at) == v.GetInt64(), m.HeldUntilWall(at)?.ToString(CultureInfo.InvariantCulture) ?? "none"),
                    "dial" => (m.DialDue(at) == (v.ValueKind == JsonValueKind.Null ? null : v.GetString()), m.DialDue(at) ?? "null"),
                    "pinged" => ((m.PingedAt == at) == v.GetBoolean(), m.PingedAt?.ToString(CultureInfo.InvariantCulture) ?? "never"),
                    "follows" => (m.Follows(at) == v.GetBoolean(), m.Follows(at).ToString()),
                    "sent" => (log.Skip(from).Where(w => w.Verb is not null).Select(w => w.Verb!).SequenceEqual(v.EnumerateArray().Select(s => s.GetString()!)),
                        string.Join(",", log.Skip(from).Select(w => w.Verb))),
                    "credentials" => (log.Skip(from).Where(w => w.Credential is not null).Select(w => w.Credential!).ToHashSet().SetEquals(v.EnumerateArray().Select(s => s.GetString()!)),
                        string.Join(",", log.Skip(from).Select(w => w.Credential))),
                    "inFlight" => (m.InFlight == v.GetInt32(), m.InFlight.ToString(CultureInfo.InvariantCulture)),
                    "outcome" => (v.EnumerateObject().All(o => m.OutcomeOf(sends.GetValueOrDefault(o.Name, o.Name))?.Word() == o.Value.GetString()),
                        string.Join(",", v.EnumerateObject().Select(o => m.OutcomeOf(sends.GetValueOrDefault(o.Name, o.Name))?.Word() ?? "waiting"))),
                    _ => (false, "a field 13.20 does not name"),
                };
                Assert.True(ok, $"{where}: {field.Name} {v.GetRawText()} (the core: {got})");
            }
            if (x[1].TryGetProperty("waitMs", out _)) waits.Add((at, m.WaitMs));
        }
        while (next < events.Count) Apply(events[next++]);
    }

    private static int Count(LinkCounts c, string name) => name switch
    {
        "unconfirmed" => c.Unconfirmed,
        "late" => c.Late,
        "replayed" => c.Replayed,
        "oversize" => c.Oversize,
        "malformed" => c.Malformed,
        "unmatched" => c.Unmatched,
        _ => -1,
    };

    /// <summary>The acceptor's rows: commands read on a verified link, run to their end or still running, each run once at most while the record holds its id; RECEIPTS asks answered from it.</summary>
    private static void Acceptor(JsonElement r)
    {
        var keeps = Strings(r.GetProperty("config"), "can").Contains("receipts");
        var book = new ReceiptBook();
        string? connector = null;
        string? last = null;
        var runs = 0;
        var events = Items(r, "events");
        var next = 0;
        foreach (var x in Items(r, "expect"))
        {
            var at = x[0].GetInt64();
            for (; next < events.Count && events[next][0].GetInt64() <= at; next++)
            {
                var e = events[next];
                var t = e[0].GetInt64();
                var kind = e[1].GetString();
                if (kind == "verified")
                {
                    connector = "main:3f9c2a1b7d0e";
                    continue;
                }
                Assert.NotNull(connector);
                if (kind == "ask")
                {
                    Assert.True(keeps);
                    last = book.Answer(connector, Strings(e[2], "ids"), "a41f09c2", t);
                    continue;
                }
                Assert.True(kind is "command" or "running", $"an acceptor event 13.20 names: {kind}");
                var id = Str(e[2], "id")!;
                if ((keeps ? book.Begin(connector, id, t) : null) is { } held)
                {
                    last = held;
                    continue;
                }
                runs++;
                if (kind == "running") continue;
                last = "{\"ok\":true,\"stage\":\"applied\",\"effect\":\"\"}";
                if (keeps) book.Keep(connector, id, last, t);
            }
            if (Int(x[1], "runs") is { } want) Assert.Equal(want, runs);
            if (x[1].TryGetProperty("answer", out var answer))
            {
                var got = JsonNode.Parse(last ?? "{}")!.AsObject();
                foreach (var field in answer.EnumerateObject())
                    Assert.True(JsonNode.DeepEquals(JsonNode.Parse(field.Value.GetRawText()), got[field.Name]), $"{Str(r, "name")} at {at}: {field.Name} {field.Value.GetRawText()} (the record: {last})");
            }
        }
    }

    // ---- beyond the rows -----------------------------------------------------------------------------------------------

    /// <summary>
    /// LINK end to end over the first proof row, the machine on one side and the core's acceptor checks on the other: the
    /// machine proves once, only after an offer naming the id it meant over the certificate it was shown; a verdict whose
    /// proof the carrier checked is taken — bound, pinned by proof, floored — and one whose proof does not verify ends the
    /// connection with nothing taken.
    /// </summary>
    [Fact]
    public void LinkEndToEndProvesOnceAndTakesOnlyAVerifiedVerdict()
    {
        var row = FirstProof("line");
        var t = Transcript(row, null, default)!;
        var k = Convert.FromHexString(Str(row, "K")!);
        var cert = Str(row, "bind")!["tls:sha256:".Length..];
        var server = PeerLink.ServerProof(k, t);
        var offer = $"OK @l1 LINK 1 patterns 9f8e7d6c {Str(row, "snonce")} {Str(row, "bind")}";
        var verdict = $"OK @l2 paired {{\"v\":1,\"verified\":\"code\",\"scope\":\"operate\",\"proof\":\"{server}\",\"lid\":\"{Str(row, "lid")}\",\"level\":1,\"can\":[\"final\",\"receipts\"],\"inst\":\"a41f09c2\"}}";

        PeerLinkMachine Dialled()
        {
            var m = new PeerLinkMachine(Settings(LinkCarrier.Line, "code") with { NodeId = "9f8e7d6c", Kind = "patterns" }, null, 0, () => 0.5);
            Assert.Equal("tls", m.Dial(0));
            m.Certificate(10, cert);
            Assert.Equal(new[] { "HELLO", "LINK" }, m.Drain().Select(w => w.Verb));
            return m;
        }

        var good = Dialled();
        good.LineAnswer(20, offer);
        var proofs = good.Drain();
        Assert.Equal(new[] { ("LINK", "proof") }, proofs.Select(w => (w.Verb!, w.Credential!)));
        Assert.False(good.Follows(20));                                                           // nothing taken before the verdict
        good.LineAnswer(30, offer);                                                               // a second offer on one connection is malformed
        Assert.False(good.KeepsConnection(30));

        var linked = Dialled();
        linked.LineAnswer(20, offer);
        linked.LineAnswer(30, verdict, p => PeerLink.Verify(server, p));
        Assert.Equal(LinkState.Live, linked.State(30));
        Assert.True(linked.Follows(30));
        Assert.Equal("pinned", linked.Tls());
        Assert.Equal(new LinkPin("9f8e7d6c", cert, PinTrust.Proof), linked.Kept.Pin);
        Assert.Equal(new LinkFloor(true, 1, "mutual"), linked.Kept.Floor);
        Assert.Equal(("9f8e7d6c", "patterns"), (linked.Kept.NodeId, linked.Kept.NodeKind));
        Assert.Equal("mutual", linked.Record(30).Proof);
        Assert.Equal("operate", linked.Record(30).Scope);

        var forged = Dialled();
        forged.LineAnswer(20, offer);
        forged.LineAnswer(30, verdict, p => PeerLink.Verify(PeerLink.ServerProof(new byte[32], t), p));
        Assert.Equal(LinkState.Down, forged.State(30));
        Assert.False(forged.Follows(30));
        Assert.Null(forged.Kept.Pin);
        Assert.Null(forged.Kept.Floor);
        Assert.StartsWith(Node + ": " + PeerLink.ProofWords, forged.Record(30).Words, StringComparison.Ordinal);

        // An offer over another certificate than the one shown is another machine between us: no proof is made.
        var between = Dialled();
        between.LineAnswer(20, offer.Replace(cert, new string('e', 64), StringComparison.Ordinal));
        Assert.Empty(between.Drain());
        Assert.False(between.KeepsConnection(20));
    }

    /// <summary>13.8.3: a pinned node showing another certificate gets nothing — no dial, no write — until a person types at least 8 hex of the new one.</summary>
    [Fact]
    public void AChangedCertificateIsAcceptedOnlyByAPersonTypingEightHexOfIt()
    {
        var pinned = Str(FirstProof("line"), "bind")!["tls:sha256:".Length..];
        var other = "8e8bc45bb14f9c7413604ef78eb1c42b465437cf700ae04a8f9553ffb0286edc";
        var m = new PeerLinkMachine(Settings(LinkCarrier.Line, "code"), new LinkKept(new LinkPin("9f8e7d6c", pinned, PinTrust.Proof), null, null, null), 0);
        m.Certificate(0, other);
        Assert.Equal(LinkState.Refused, m.State(0));
        Assert.Equal("certificate", m.Refusal(0));
        Assert.Empty(m.Drain());
        Assert.Null(m.DialDue(0));
        Assert.StartsWith("CERTIFICATE CHANGED: Hall A now shows 8e8b c45b … — nothing sent", m.Record(0).Words, StringComparison.Ordinal);
        Assert.False(m.AcceptCertificate(other[..7]));
        Assert.False(m.AcceptCertificate(pinned[..8]));
        Assert.True(m.AcceptCertificate(other[..4].ToUpperInvariant() + " " + other[4..8]));
        Assert.NotEqual(LinkState.Refused, m.State(1));
        Assert.Equal(new LinkPin("9f8e7d6c", other, PinTrust.Person), m.Kept.Pin);
    }

    /// <summary>13.6.8 on <c>ws</c>: a first pin carries no key; one Trust press beside its fingerprint makes it a person's, and the next connection over that certificate sends the bearer.</summary>
    [Fact]
    public void AFirstPinCarriesNoKeyUntilAPersonTrustsIt()
    {
        var cert = "8e8bc45bb14f9c7413604ef78eb1c42b465437cf700ae04a8f9553ffb0286edc";
        var m = new PeerLinkMachine(Settings(LinkCarrier.Ws, "e63c317763b14546"), null, 0, () => 0.5);
        Assert.False(m.TrustCertificate());                                                       // nothing pinned yet: nothing to trust
        m.Certificate(0, cert);
        m.FirstFrame(0);
        Assert.DoesNotContain(m.Drain(), w => w.Credential is not null);
        Assert.Equal("first", m.Tls());
        Assert.Contains("compare its fingerprint 8e8b c45b b14f 9c74 and press Trust to send the key", m.Record(0).Words, StringComparison.Ordinal);
        Assert.Equal(CheckLight.Amber, m.Light(0));
        Assert.True(m.TrustCertificate());
        Assert.Equal(PinTrust.Person, m.Kept.Pin!.Trust);
        m.Closed(100);
        m.Certificate(700, cert);
        m.FirstFrame(700);
        Assert.Equal(new[] { "bearer" }, m.Drain().Select(w => w.Credential).OfType<string>());
        Assert.Equal("pinned", m.Tls());
        // Another certificate than the person's pin is CERTIFICATE CHANGED: no key goes to it.
        m.Closed(800);
        m.Certificate(1500, new string('a', 64));
        Assert.Equal(LinkState.Refused, m.State(1500));
        Assert.Empty(m.Drain());
    }

    /// <summary>13.10.2 and 13.11.6: a matched pong is a sample; the clock schedules only fresh and over TLS or a local link; a plain clock off this machine never does; a new inst empties it.</summary>
    [Fact]
    public void AMatchedPongIsASampleAndOnlyAFreshClockOverTlsSchedules()
    {
        var pinned = Str(FirstProof("line"), "bind")!["tls:sha256:".Length..];
        PeerLinkMachine Clocked(bool tls)
        {
            var c = new PeerLinkMachine(Settings(LinkCarrier.Line, "bb3f97d01c274e15") with { TimedToleranceMs = 300 }, null, 0);
            if (tls) c.Certificate(1000, pinned);
            else c.NoTls(1000);
            c.Verified(1000, "a41f09c2");
            c.Ping(1000, 1000);
            c.Pong(1012, 999, 1505, 1506, false);                                                 // answers no ping of this link's
            c.Pong(1012, 1000, 1505, 1506, false);
            return c;
        }
        var overTls = Clocked(true);
        var record = overTls.Record(1012);
        Assert.Equal(new LinkRecordClock(499.5, 7, 0, 1, false), record.Clock);
        Assert.Equal(1, record.Counts.Unmatched);
        Assert.True(overTls.CanSchedule(1012, 300));
        Assert.DoesNotContain("unfit", overTls.Flags(1012));
        Assert.False(overTls.CanSchedule(11013, 300));                                             // past clock.fresh.ms
        Assert.Contains("unfit", overTls.Flags(11013));
        overTls.Verified(11014, "b7c3d9e1");
        Assert.Equal(0, overTls.Clock.Samples);                                                    // 13.11.7: a new inst empties the window

        var overPlain = Clocked(false);
        Assert.Equal(1, overPlain.Clock.Samples);
        Assert.False(overPlain.CanSchedule(1012, 300));
        Assert.Contains("unfit", overPlain.Flags(1012));
        Assert.Contains("plain", overPlain.Flags(1012));
    }

    /// <summary>13.6.8 on <c>line</c>: only a NONCE of 32 lowercase hex is proved, once, and nothing is taken before OK paired; then the link is legacy and amber.</summary>
    [Fact]
    public void AtLevelZeroOnlyA32HexNonceIsProvedAndTheLinkIsLegacyAndAmber()
    {
        var m = new PeerLinkMachine(Settings(LinkCarrier.Line, "code"), null, 0);
        m.LineAnswer(0, "ERR @l1 unknown command 'LINK'");
        m.Nonce(1, "3F9A0C5E7B2D4F6A8C1E3B5D7F9A0C2E");
        m.Nonce(1, "3f9a0c5e7b2d4f6a8c1e3b5d7f9a0c2");
        var unproved = m.Drain();
        Assert.Equal(new[] { "HELLO", "LINK" }, unproved.Select(w => w.Verb));
        Assert.All(unproved, w => Assert.Null(w.Credential));
        m.Nonce(2, "3f9a0c5e7b2d4f6a8c1e3b5d7f9a0c2e");
        m.Nonce(2, "3f9a0c5e7b2d4f6a8c1e3b5d7f9a0c2e");
        Assert.Equal(new[] { ("AUTH-PROOF", "auth-proof") }, m.Drain().Select(w => (w.Verb!, w.Credential!)));
        Assert.False(m.Follows(2));
        m.Frame(2);                                                                               // a frame before OK paired is discarded and counted
        Assert.Equal(1, m.Record(2).Counts.Malformed);
        m.LineAnswer(3, "OK @pa1 paired");
        Assert.Equal(LinkState.Live, m.State(3));
        Assert.Contains("legacy", m.Flags(3));
        Assert.Equal(CheckLight.Amber, m.Light(3));
        Assert.True(m.Follows(3));
        Assert.Equal("legacy", m.Record(3).Proof);
        Assert.Equal(0, m.Record(3).Level);
    }

    /// <summary>13.12.4: an answer at <c>receipt.final.ms</c> still counts; past it a written command is unconfirmed; an accepted one waits for its final answer only from an acceptor that sends one.</summary>
    [Fact]
    public void AFinalAnswerAtTheLimitCountsAndPastItTheCommandIsUnconfirmed()
    {
        var final = new PeerLinkMachine(Settings(LinkCarrier.Line, "code", new[] { "final" }), null, 0);
        final.Verified(0);
        var a = final.NextId();
        var b = final.NextId();
        Assert.Equal(("s1-11aa22bb", "s2-11aa22bb"), (a, b));
        final.Send(0, a);
        final.Send(0, b);
        final.Answer(10, b, PeerLink.ReadLine("OK @" + b + " {\"stage\":\"accepted\",\"effect\":\"queued\"}")!.Value.Reading);
        Assert.Equal(2, final.InFlight);
        Assert.Equal(LinkOutcome.Accepted, final.OutcomeOf(b));
        final.Answer(20, b, PeerLink.ReadLine("OK @" + b + " final {\"stage\":\"applied\"}")!.Value.Reading);
        Assert.Equal(LinkOutcome.Applied, final.OutcomeOf(b));
        var limit = (long)PeerLink.ReceiptFinal.TotalMilliseconds;
        Assert.Equal(0, final.Record(limit).Counts.Unconfirmed);
        Assert.Null(final.OutcomeOf(a));
        Assert.Equal(1, final.Record(limit + 1).Counts.Unconfirmed);
        Assert.Equal(LinkOutcome.Unconfirmed, final.OutcomeOf(a));
        Assert.StartsWith("no answer in 30 s", final.WordsOf(a), StringComparison.Ordinal);

        var once = new PeerLinkMachine(Settings(LinkCarrier.Line, "code"), null, 0);
        once.Verified(0);
        once.Send(0, "s1-11aa22bb");
        once.Answer(5, "s1-11aa22bb", PeerLink.ReadHttp(200, "{\"ok\":true}"));
        Assert.Equal(0, once.InFlight);
        Assert.Equal(LinkOutcome.Accepted, once.OutcomeOf("s1-11aa22bb"));
    }

    /// <summary>13.15.3: a refused key's hold is kept with its end in wall time, so a restart whose monotonic clock starts anew still waits it out.</summary>
    [Fact]
    public void AHoldOutlastsARestartOnItsWallClockEnd()
    {
        var m = new PeerLinkMachine(Settings(LinkCarrier.Ws, "e63c317763b14546"), null, new LinkNow(5_000, 1_760_000_000_000), () => 0.5);
        m.Refused(new LinkNow(5_000, 1_760_000_000_000), "refused", 90);
        Assert.Equal(1_760_000_090_000, m.HeldUntilWall(new LinkNow(5_001, 1_760_000_000_001)));
        var kept = m.Kept;
        Assert.Equal(("key", 1_760_000_090_000L), (kept.HeldRefusal, kept.HeldUntilWall!.Value));
        // The process restarts: its monotonic clock reads 7 ms, the wall clock 30 s later.
        var again = new PeerLinkMachine(Settings(LinkCarrier.Ws, "e63c317763b14546"), kept, new LinkNow(7, 1_760_000_030_000), () => 0.5);
        Assert.Equal(LinkState.Held, again.State(new LinkNow(7, 1_760_000_030_000)));
        Assert.Null(again.DialDue(new LinkNow(60_006, 1_760_000_089_999)));
        Assert.Equal("tls", again.DialDue(new LinkNow(60_007, 1_760_000_090_000)));
        Assert.Null(new PeerLinkMachine(Settings(LinkCarrier.Ws, "e63c317763b14546"), kept, new LinkNow(7, 1_760_000_090_000)).Kept.HeldUntilWall);   // a hold over by the restart is no hold
    }

    /// <summary>13.12.6's record: per connector, the last receipts.kept ids for receipts.kept.ms; a running id answers accepted, never unknown.</summary>
    [Fact]
    public void TheReceiptRecordKeepsItsBoundsPerConnector()
    {
        var book = new ReceiptBook();
        const string conn = "patterns:9f8e7d6c";
        Assert.Null(book.Begin(conn, "s17-11aa22bb", 0));
        Assert.Equal("{\"ok\":true,\"stage\":\"accepted\",\"effect\":\"\",\"repeat\":true}", book.Begin(conn, "s17-11aa22bb", 1));
        Assert.Equal("accepted", JsonNode.Parse(book.Answer(conn, new[] { "s17-11aa22bb" }, "a41f09c2", 1))!["receipts"]!["s17-11aa22bb"]!["stage"]!.GetValue<string>());
        book.Keep(conn, "s17-11aa22bb", "{\"ok\":true,\"stage\":\"applied\",\"effect\":\"cut\"}", 2);
        var answer = JsonNode.Parse(book.Answer(conn, new[] { "s17-11aa22bb", "s18-11aa22bb" }, "a41f09c2", 3))!.AsObject();
        Assert.Equal(new[] { "inst", "receipts" }, answer.Select(p => p.Key));
        Assert.Equal("applied", answer["receipts"]!["s17-11aa22bb"]!["stage"]!.GetValue<string>());
        Assert.Equal("unknown", answer["receipts"]!["s18-11aa22bb"]!.GetValue<string>());
        Assert.Null(book.Begin("main:3f9c2a1b7d0e", "s17-11aa22bb", 4));                          // each connector its own record
        for (var i = 0; i < PeerLink.ReceiptsKept; i++) book.Keep(conn, "x" + i.ToString(CultureInfo.InvariantCulture), "{\"ok\":true,\"stage\":\"applied\"}", 5);
        Assert.Null(book.Begin(conn, "s17-11aa22bb", 6));                                          // pushed out by the last receipts.kept
        Assert.NotNull(book.Begin(conn, "x255", 7));
        Assert.Null(book.Begin(conn, "x255", 5 + (long)PeerLink.ReceiptsKeptFor.TotalMilliseconds + 1));
        var many = Enumerable.Range(0, 40).Select(i => "q" + i.ToString(CultureInfo.InvariantCulture)).ToList();
        Assert.Equal(PeerLink.ReceiptsAskMost, JsonNode.Parse(book.Answer(conn, many, "a41f09c2", 8))!["receipts"]!.AsObject().Count);
    }

    /// <summary>
    /// 13.12.6's record answers a receipt byte for byte as it was kept, <c>"repeat":true</c> appended at its end, and holds a
    /// book only for a connector with an id inside <c>receipts.kept.ms</c>: many connectors proving one code leave nothing
    /// once their receipts expire, and asking for receipts makes no book.
    /// </summary>
    [Fact]
    public void TheReceiptRecordAnswersAsKeptAndDropsABookOnceItsIdsExpire()
    {
        var book = new ReceiptBook();
        const string conn = "main:3f9c2a1b7d0e";
        const string kept = "{\"ok\":true,\"stage\":\"applied\",\"effect\":\"Bühne — cut \\u00e9\"}";
        Assert.Null(book.Begin(conn, "s1-11aa22bb", 0));
        book.Keep(conn, "s1-11aa22bb", kept, 0);
        Assert.Equal(kept[..^1] + ",\"repeat\":true}", book.Begin(conn, "s1-11aa22bb", 1));
        var answer = book.Answer(conn, new[] { "s1-11aa22bb", "s2-11aa22bb" }, "a41f09c2", 2);
        Assert.Equal("{\"inst\":\"a41f09c2\",\"receipts\":{\"s1-11aa22bb\":" + kept + ",\"s2-11aa22bb\":\"unknown\"}}", answer);
        Assert.NotNull(JsonNode.Parse(answer));
        Assert.Equal("{\"repeat\":true}", ReceiptBook.Repeat("{}"));

        for (var i = 0; i < 300; i++) book.Keep("main:conn" + i.ToString("000000", CultureInfo.InvariantCulture), "s1-11aa22bb", kept, 3);
        Assert.Equal(301, book.Connectors);
        var expired = 3 + (long)PeerLink.ReceiptsKeptFor.TotalMilliseconds + 1;
        Assert.Contains("\"unknown\"", book.Answer("main:nobody", new[] { "s1-11aa22bb" }, "a41f09c2", expired), StringComparison.Ordinal);
        Assert.Equal(301, book.Connectors);                                                          // an ask makes no book
        Assert.Null(book.Begin("patterns:9f8e7d6c", "p1-a1b2c3d4", expired));
        Assert.Equal(1, book.Connectors);                                                            // a new connector: every expired book dropped
        Assert.Null(book.Begin(conn, "s1-11aa22bb", expired));                                       // its id expired with its book: run as new
    }

    /// <summary>
    /// 13.6.4's classes are a closed set: a verdict refusing with any other word is read as <c>malformed</c> and counted, on
    /// <c>line</c> (the connection ends, a failed dial) and on <c>ws</c> (a public viewer, its key proved again after the
    /// wait), and the word itself reaches no field of the record. A class 13.6.4 names is recorded as itself, uncounted.
    /// </summary>
    [Fact]
    public void ARefusalOfAClassSection1364DoesNotNameIsReadAsMalformedAndCounted()
    {
        var line = new PeerLinkMachine(Settings(LinkCarrier.Line, "code"), null, 0, () => 0.5);
        line.Connected(0);
        line.LineAnswer(1, "ERR @l1 LINK frobnicate — a word no class of 13.6.4");
        var record = line.Record(1);
        Assert.Equal(("down", "malformed", 1), (record.State, record.Refusal, record.Counts.Malformed));
        Assert.DoesNotContain("frobnicate", record.ToJson(), StringComparison.Ordinal);
        Assert.Null(line.DialDue(1));                                                               // 13.15.1's wait, as a malformed verdict waits

        var ws = new PeerLinkMachine(Settings(LinkCarrier.Ws, "e63c317763b14546"), null, 0, () => 0.5);
        ws.Refused(0, "frobnicate", 0);
        record = ws.Record(1);
        Assert.Equal(("malformed", 1), (record.Refusal, record.Counts.Malformed));
        Assert.Contains("keyless", record.Flags);
        Assert.Contains("malformed", record.Flags);
        Assert.DoesNotContain("frobnicate", record.ToJson(), StringComparison.Ordinal);

        var named = new PeerLinkMachine(Settings(LinkCarrier.Ws, "e63c317763b14546"), null, 0, () => 0.5);
        named.Refused(0, "version", 0);
        Assert.Equal(("version", 0), (named.Record(1).Refusal, named.Record(1).Counts.Malformed));
    }

    /// <summary>
    /// 13.6.3: nothing is taken from a connection before the acceptor's proof verifies. A command an earlier connection left
    /// unconfirmed is not moved by an answer or a RECEIPTS answer that comes during the next connection's handshake — each
    /// discarded and counted — nor by one on a socket kept as a public viewer; once the link is verified again, the RECEIPTS
    /// answer to the ask it writes settles it.
    /// </summary>
    [Fact]
    public void NoAnswerIsTakenBeforeTheLinkIsUpAgain()
    {
        var m = new PeerLinkMachine(Settings(LinkCarrier.Line, "code", new[] { "receipts" }), null, 0, () => 0.5);
        m.Verified(0, "a41f09c2");
        var id = m.NextId();
        Assert.Null(m.Send(0, id));
        m.Closed(10);
        Assert.Equal(LinkOutcome.Unconfirmed, m.OutcomeOf(id));
        m.Drain();

        using var applied = JsonDocument.Parse("{\"ok\":true,\"stage\":\"applied\",\"effect\":\"cut\"}");
        var reading = PeerLink.ReadReceipt(applied.RootElement);
        m.Connected(5_000);                                                                       // the next connection: LINK not finished
        m.Answer(5_001, id, reading);
        m.Receipts(5_002, new Dictionary<string, LinkReading?> { [id] = reading });
        Assert.Equal(LinkOutcome.Unconfirmed, m.OutcomeOf(id));
        Assert.Equal(2, m.Record(5_002).Counts.Malformed);

        m.Verified(5_003, "a41f09c2");
        Assert.Contains(m.Drain(), w => w.Verb == "RECEIPTS" && w.Detail == id);
        m.Receipts(5_004, new Dictionary<string, LinkReading?> { [id] = reading });
        Assert.Equal(LinkOutcome.Applied, m.OutcomeOf(id));

        var viewer = new PeerLinkMachine(Settings(LinkCarrier.Ws, "e63c317763b14546", new[] { "receipts" }), null, 0, () => 0.5);
        viewer.Verified(0, "a41f09c2");
        var sent = viewer.NextId();
        viewer.Send(0, sent);
        viewer.Refused(1, "refused", 60);                                                         // a public viewer now, its key held
        viewer.Answer(2, sent, reading);
        Assert.NotEqual(LinkOutcome.Applied, viewer.OutcomeOf(sent));
        Assert.Equal(1, viewer.Record(2).Counts.Malformed);
    }

    /// <summary>13.12.1: a request id is a prefix of 1–4 letters, a counter from 1 and the process's inst, within request.id.chars.</summary>
    [Fact]
    public void ARequestIdIsAPrefixACounterAndTheInst()
    {
        Assert.Equal("p7-a41f09c2", PeerLink.RequestId("p", 7, "a41f09c2"));
        Assert.Throws<ArgumentException>(() => PeerLink.RequestId("", 1, "a41f09c2"));
        Assert.Throws<ArgumentException>(() => PeerLink.RequestId("abcde", 1, "a41f09c2"));
        Assert.Throws<ArgumentOutOfRangeException>(() => PeerLink.RequestId("p", 0, "a41f09c2"));
        Assert.Throws<ArgumentException>(() => PeerLink.RequestId("p", 1, new string('a', WireReceipt.MaxId)));
    }

    /// <summary>13.16.3: the record's fields, nested ones too, are the copy's example's in its order; it carries no key, code, K, proof, S or nonce.</summary>
    [Fact]
    public void TheRecordIsSection13163sInItsOrderAndCarriesNoSecret()
    {
        var example = "{\"peer\"" + Between(Between(Copy(), "\n### 13.16 ", "\n### "), "\n```\n{\"peer\"", "\n```");
        var top = Regex.Matches(example, "\"([A-Za-z]+)\":").Select(x => x.Groups[1].Value).ToList();
        var nested = Regex.Matches(example, "\"([A-Za-z]+)\":\\{([^}]*)\\}")
            .ToDictionary(x => x.Groups[1].Value, x => Regex.Matches(x.Groups[2].Value, "\"([A-Za-z]+)\"").Select(y => y.Groups[1].Value).ToList());
        Assert.True(top.Count > 20 && nested.Count == 4, example);

        var ws = FirstProof("ws");
        var m = new PeerLinkMachine(Settings(LinkCarrier.Ws, Str(ws, "kid")) with { Kind = "main", NodeId = "3f9c2a1b7d0e" }, null, 0);
        m.Certificate(0, Str(ws, "bind")!["tls:sha256:".Length..]);
        m.Verified(0, "a41f09c2", "post");
        var json = m.Record(10).ToJson();
        var root = JsonNode.Parse(json)!.AsObject();
        Assert.Equal(top, root.Select(p => p.Key));
        foreach (var (name, fields) in nested) Assert.Equal(fields, root[name]!.AsObject().Select(p => p.Key));
        Assert.Equal(("live", "green", "✓", "pinned", "post", 10L), ((string)root["state"]!, (string)root["light"]!, (string)root["mark"]!, (string)root["tls"]!, (string)root["scope"]!, (long)root["heardMs"]!));
        foreach (var secret in new[] { "K", "clientProof", "serverProof", "session", "snonce", "cnonce" })
            Assert.DoesNotContain(Str(ws, secret)!, json, StringComparison.Ordinal);
        var forbidden = new[] { "Key", "Code", "K", "Session", "S", "ClientProof", "ServerProof", "Nonce", "SNonce", "CNonce", "Token" };
        Assert.DoesNotContain(typeof(LinkRecord).GetProperties(), p => forbidden.Contains(p.Name));
    }

    // ---- reading the copy ----------------------------------------------------------------------------------------------

    private static LinkSettings Settings(LinkCarrier carrier, string? key, IReadOnlyList<string>? can = null)
        => new() { Carrier = carrier, Key = key, Can = can ?? Array.Empty<string>(), Name = Node, Address = "10.0.0.5", Inst = "11aa22bb", IdPrefix = "s" };

    /// <summary>13.6.1's T for a proof row — or with <paramref name="field"/> taken from <paramref name="changed"/>, as a refuse row changes one.</summary>
    private static string? Transcript(JsonElement r, string? field, JsonElement changed)
    {
        string S(string name) => field == name ? Str(changed, name)! : Str(r, name)!;
        long N(string name) => field == name ? Int(changed, name)!.Value : Int(r, name)!.Value;
        return PeerLink.Transcript(S("carrier"), N("offer"), N("speaks"), S("server"), S("client"), S("kid"), S("snonce"), S("cnonce"), S("bind"));
    }

    private static JsonElement FirstProof(string carrier)
        => Block.Value.Select(Parse).First(r => Str(r, "case") == "proof" && Str(r, "carrier") == carrier);

    private static (string T, string Id) Split(string node) => (node[..node.IndexOf(':', StringComparison.Ordinal)], node[(node.IndexOf(':', StringComparison.Ordinal) + 1)..]);

    private static long Ticks(JsonElement ms) => ms.GetInt64() * TimeSpan.TicksPerMillisecond;

    private static string Hex(byte[] b) => Convert.ToHexStringLower(b);

    private static JsonElement Parse(string row)
    {
        using var doc = JsonDocument.Parse(row);
        return doc.RootElement.Clone();
    }

    private static List<JsonElement> Items(JsonElement r, string name) => r.GetProperty(name).EnumerateArray().ToList();

    private static string? Str(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static long? Int(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) ? n : null;

    private static double? Num(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Number ? v.GetDouble() : null;

    private static bool? Bool(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : null;

    private static List<string> Strings(JsonElement e, string name)
        => e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.Array
            ? v.EnumerateArray().Select(x => x.GetString()!).ToList() : new List<string>();

    private static string Between(string text, string start, string end)
    {
        var at = text.IndexOf(start, StringComparison.Ordinal);
        Assert.True(at >= 0, $"'{start.Trim()}' is not in docs/SHOWBUS.md");
        var to = text.IndexOf(end, at + start.Length, StringComparison.Ordinal);
        return to < 0 ? text[(at + start.Length)..] : text[(at + start.Length)..to];
    }

    private static string Copy() => File.ReadAllText(Path.Combine(Root(), "docs", "SHOWBUS.md"), new UTF8Encoding(false)).Replace("\r\n", "\n", StringComparison.Ordinal);

    /// <summary>A fenced block of the copy (<c>```name</c> to the next <c>```</c>), once.</summary>
    private static List<string> Fenced(string name)
    {
        var lines = Copy().Split('\n');
        var opens = lines.Select((l, i) => (l, i)).Where(x => x.l == "```" + name).Select(x => x.i).ToList();
        Assert.True(opens.Count == 1, $"docs/SHOWBUS.md has {opens.Count} ```{name} blocks, not one");
        return lines.Skip(opens[0] + 1).TakeWhile(l => l != "```").ToList();
    }

    /// <summary>The tree this test was built from: the folder above the test binary that holds Patterns.sln.</summary>
    private static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "Patterns.sln"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("no Patterns.sln above " + AppContext.BaseDirectory);
    }
}
