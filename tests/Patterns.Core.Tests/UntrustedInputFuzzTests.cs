using System.Globalization;
using System.Text;
using System.Text.RegularExpressions;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Every parser that reads what arrives from outside the desk — the wire's line, an HTTP head, the twin link before and
/// after the key is proved, a management reply, a beacon, an OSC datagram and the line it becomes, an mDNS packet, an
/// EDID — answers any input with a value or a refusal and never an exception. Mutations of real inputs and plain noise
/// from a fixed seed, so a failure names the same input on every machine. A fuzz of this shape found the wire's one throw
/// (PLAN SHIFT past a TimeSpan) and the wrap and the infinity behind it; this keeps the class closed.
/// </summary>
public class UntrustedInputFuzzTests
{
    /// <summary>xorshift64 from a fixed seed: the same sequence on every machine and every run.</summary>
    private sealed class Noise
    {
        private ulong _s;

        public Noise(ulong seed) => _s = seed == 0 ? 0x9E3779B97F4A7C15UL : seed;

        public int Next(int max)
        {
            _s ^= _s << 13;
            _s ^= _s >> 7;
            _s ^= _s << 17;
            return (int)(_s % (ulong)max);
        }

        /// <summary>Text a UTF-8 socket could deliver: printable ASCII, the separators the parsers split on, and non-ASCII outside the surrogates.</summary>
        public string Text(int max)
        {
            const string separators = " \t\r\n\0|=\"{}[]:,/+-.#";
            var length = Next(max);
            var sb = new StringBuilder(length);
            for (var i = 0; i < length; i++)
            {
                var pick = Next(100);
                sb.Append(pick < 70 ? (char)(32 + Next(95)) : pick < 85 ? separators[Next(separators.Length)] : (char)(0xA0 + Next(0xD7FF - 0xA0)));
            }
            return sb.ToString();
        }

        public string Mutate(string seed)
        {
            var sb = new StringBuilder(seed);
            for (var edits = 1 + Next(5); edits > 0; edits--)
            {
                var at = sb.Length == 0 ? 0 : Next(sb.Length);
                switch (Next(6))
                {
                    case 0: sb.Insert(at, Text(12)); break;
                    case 1:
                        if (sb.Length > 0) sb.Remove(at, Math.Min(1 + Next(5), sb.Length - at));
                        break;
                    case 2: sb.Insert(at, new string((char)(32 + Next(95)), 1 + Next(3000))); break;
                    case 3: sb.Insert(at, Hostile[Next(Hostile.Length)]); break;
                    case 4:
                        if (sb.Length > 0) sb[at] = (char)(32 + Next(95));
                        break;
                    default: sb.Insert(at, sb.ToString(0, Math.Min(sb.Length, Next(40)))); break;
                }
            }
            return sb.ToString();
        }

        public byte[] Bytes(int max)
        {
            var b = new byte[Next(max)];
            for (var i = 0; i < b.Length; i++) b[i] = (byte)Next(256);
            return b;
        }

        public byte[] Mutate(byte[] seed)
        {
            var b = seed.ToList();
            for (var edits = 1 + Next(8); edits > 0; edits--)
            {
                var at = b.Count == 0 ? 0 : Next(b.Count);
                switch (Next(4))
                {
                    case 0:
                        if (b.Count > 0) b[at] = (byte)Next(256);
                        break;
                    case 1: b.Insert(at, (byte)Next(256)); break;
                    case 2:
                        if (b.Count > 0) b.RemoveAt(at);
                        break;
                    default:
                        for (var k = 0; k < 4 && at + k < b.Count; k++) b[at + k] = Next(2) == 0 ? (byte)0xFF : (byte)0x7F;
                        break;
                }
            }
            return b.ToArray();
        }
    }

    /// <summary>Numbers past what a clock, an int or the show file can hold, and the words .NET reads as numbers.</summary>
    private static readonly string[] Hostile =
    {
        "1e308", "-1e308", "Infinity", "+Infinity", "-Infinity", "NaN", "99999999999999999999", "+99999999999999999999",
        "2147483648", "-2147483649", "1193047:00:00", "+1193047:00:00", "35791395:00", "9e18", "1e308m", "0x7fffffff",
    };

    private static string[] OracleLines()
    {
        var path = Path.Combine(CompanionModuleContractTests.ModuleDir, "test", "lines.txt");
        return File.ReadAllLines(path).Where(l => l.Trim().Length > 0).ToArray();
    }

    /// <summary>Runs <paramref name="parse"/> over <paramref name="inputs"/>; the failures, the first of each exception type with its input.</summary>
    private static List<string> Throws<T>(string parser, IEnumerable<T> inputs, Action<T> parse)
    {
        var failures = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var input in inputs)
        {
            try
            {
                parse(input);
            }
            catch (Exception ex)
            {
                if (seen.Add(ex.GetType().Name)) failures.Add($"{parser} threw {ex.GetType().Name} ({ex.Message}) on {Describe(input)}");
            }
        }
        return failures;
    }

    private static string Describe(object? input) => input switch
    {
        string s => $"\"{(s.Length > 90 ? s[..90] + "…" : s).Replace("\r", "\\r", StringComparison.Ordinal).Replace("\n", "\\n", StringComparison.Ordinal)}\" ({s.Length} chars)",
        byte[] b => $"{Convert.ToHexString(b.AsSpan(0, Math.Min(48, b.Length)))} ({b.Length} bytes)",
        _ => input?.ToString() ?? "null",
    };

    [Fact]
    public void TheWireLineRefusesOrParsesEveryInputAndNeverThrows()
    {
        var noise = new Noise(808);
        var oracle = OracleLines();
        Assert.True(oracle.Length > 200, "the Companion module's lines were not found");
        var inputs = Enumerable.Range(0, 6000).Select(i => i % 5 == 0 ? noise.Text(400) : noise.Mutate(oracle[noise.Next(oracle.Length)]));
        var failures = Throws("ControlProtocol.Parse", inputs, line => ControlProtocol.Parse(line));
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>
    /// Every number in every line the Companion module can send, replaced with one past what a clock can hold: the line
    /// is refused or parses, and a slip or a nudge that parses still reads back as a finite span no longer than a week.
    /// </summary>
    [Fact]
    public void EveryModuleLineWithAHostileNumberIsRefusedOrStaysInsideAWeek()
    {
        var number = new Regex(@"(?<![A-Za-z])[+\-]?\d+(?:[.:]\d+)*", RegexOptions.None, TimeSpan.FromSeconds(1));
        var failures = new List<string>();
        var variants = 0;
        foreach (var line in OracleLines())
        {
            var matches = number.Matches(line);
            foreach (var hostile in Hostile)
            {
                var forms = new List<string> { line + " " + hostile };
                forms.AddRange(matches.Select(m => line[..m.Index] + hostile + line[(m.Index + m.Length)..]));
                foreach (var form in forms)
                {
                    variants++;
                    RemoteCommand cmd;
                    try
                    {
                        cmd = ControlProtocol.Parse(form);
                    }
                    catch (Exception ex)
                    {
                        failures.Add($"\"{form}\" threw {ex.GetType().Name}");
                        continue;
                    }
                    var within = cmd.Action.Kind switch
                    {
                        ShowActionKind.PlanShift => CueTiming.ParseDelta(cmd.Action.Value) is { } d && d.Duration().TotalSeconds <= StageTimer.MaxWireSeconds,
                        ShowActionKind.TimerAdd => StageTimer.ParseSeconds(cmd.Action.Value) is { } s && double.IsFinite(s) && Math.Abs(s) <= StageTimer.MaxWireSeconds,
                        _ => true,
                    };
                    if (!within) failures.Add($"\"{form}\" became {cmd.Action.Kind} '{cmd.Action.Value}'");
                }
            }
        }
        Assert.True(variants > 5000, $"too few variants ({variants})");
        Assert.True(failures.Count == 0, string.Join("\n", failures.Take(20)));
    }

    [Fact]
    public void AnHttpHeadIsParsedOrRefusedAndNeverThrowsOrCarriesANegativeLength()
    {
        var noise = new Noise(9696);
        string[] seeds =
        {
            "POST /api/cmd HTTP/1.1\r\nHost: desk\r\nContent-Length: 11\r\nX-Patterns-Token: K7QM-3XWD-P9RA\r\nX-Patterns-Client: deck\r\n\r\n",
            "GET /api/state?since=12 HTTP/1.1\r\nHost: x\r\n\r\n",
            "POST /api/play/join HTTP/1.1\r\nContent-Length: 30\r\nContent-Length: 5\r\n\r\n",
        };
        var inputs = Enumerable.Range(0, 4000).Select(i => i % 5 == 0 ? noise.Text(600) : noise.Mutate(seeds[noise.Next(seeds.Length)]));
        var failures = Throws("HttpHead.Parse", inputs, head =>
        {
            foreach (var limits in new[] { HttpLimits.Control, HttpLimits.Audience })
            {
                var parsed = HttpHead.Parse(head, limits);
                if (parsed.Ok && parsed.ContentLength < 0) throw new InvalidDataException($"accepted a content length of {parsed.ContentLength}");
                HttpHead.Parse(Encoding.UTF8.GetBytes(head), limits);
            }
        });
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void TheTwinLinkAndAManagementReplyNeverThrow()
    {
        var noise = new Noise(9699);
        var join = "{\"proto\":3,\"name\":\"standby\",\"machine\":\"FOH-2\",\"nonce\":\"a1b2c3\",\"instance\":\"x9\",\"tookOver\":false}";
        string[] lines = { "JOIN " + join, "LIVE {\"on\":true}", "PROOF deadbeef", "SECTION Looks {\"a\":1}", "CHALLENGE {\"nonce\":\"n\",\"proof\":\"p\"}" };
        var twin = Enumerable.Range(0, 3000).Select(i => i % 5 == 0 ? noise.Text(300) : noise.Mutate(lines[noise.Next(lines.Length)]));
        var failures = Throws("TwinMessage.Parse", twin, line => TwinMessage.Parse(line));
        failures.AddRange(Throws("TwinJoin.Parse", Enumerable.Range(0, 2000).Select(_ => noise.Mutate(join)), json => TwinJoin.Parse(json)));
        failures.AddRange(Throws("TwinChallenge.Parse", Enumerable.Range(0, 1000).Select(_ => noise.Mutate("{\"nonce\":\"n1\",\"proof\":\"p1\"}")), json => TwinChallenge.Parse(json)));

        var reply = "{\"token\":\"t\",\"commands\":[\"BLACKOUT ON\",\"LOOK Walk-in\"],\"update\":{\"url\":\"https://x/y.zip\",\"version\":\"1.2\",\"sha256\":\"" + new string('a', 64) + "\"},\"apply\":false,\"restart\":false}";
        failures.AddRange(Throws("CheckIn.Parse", Enumerable.Range(0, 2000).Select(i => i % 5 == 0 ? noise.Text(300) : noise.Mutate(reply)), json => CheckIn.Parse(json, "t")));
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    [Fact]
    public void ADatagramFromTheNetworkNeverThrows()
    {
        var noise = new Noise(9698);
        var osc = new byte[] { (byte)'/', (byte)'p', (byte)'/', (byte)'l', 0, 0, 0, 0, (byte)',', (byte)'i', (byte)'s', (byte)'b', 0, 0, 0, 0, 0, 0, 0, 5, (byte)'h', (byte)'i', 0, 0, 0, 0, 0, 4, 1, 2, 3, 4 };
        var bundle = Encoding.ASCII.GetBytes("#bundle\0").Concat(new byte[8]).Concat(new byte[] { 0, 0, 0, 32 }).Concat(osc).ToArray();
        var failures = Throws("OscCodec.Decode", Enumerable.Range(0, 4000).Select(i => i % 4 == 0 ? noise.Bytes(96) : noise.Mutate(i % 2 == 0 ? osc : bundle)), bytes => OscCodec.Decode(bytes));

        var dns = Convert.FromHexString("000084000000000100000000055F70617474045F746370056C6F63616C00000C0001000011940009066465736B2D3100C00C");
        var selfPointer = Convert.FromHexString("000000000001000000000000C00C00010001");
        failures.AddRange(Throws("DnsPacket.Parse", new[] { selfPointer }.Concat(Enumerable.Range(0, 4000).Select(i => i % 4 == 0 ? noise.Bytes(128) : noise.Mutate(dns))), bytes => DnsPacket.Parse(bytes)));

        var beacon = Encoding.UTF8.GetBytes("{\"v\":1,\"name\":\"desk\",\"host\":\"10.0.0.2\",\"port\":9697,\"kind\":\"desk\"}");
        failures.AddRange(Throws("Beacon.Parse", Enumerable.Range(0, 3000).Select(i => i % 3 == 0 ? noise.Bytes(96) : noise.Mutate(beacon)), bytes => Beacon.Parse(bytes)));

        var edid = new byte[256];
        new byte[] { 0, 255, 255, 255, 255, 255, 255, 0 }.CopyTo(edid, 0);
        edid[126] = 1;
        edid[128] = 0x02;
        edid[129] = 3;
        failures.AddRange(Throws("Edid.Parse", Enumerable.Range(0, 3000).Select(i => i % 4 == 0 ? noise.Bytes(300) : noise.Mutate(edid)), bytes => Edid.Parse(bytes)));
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }

    /// <summary>An OSC message on every verb the map knows, with arguments no controller should send, maps and parses without a throw.</summary>
    [Fact]
    public void AnyOscMessageMapsAndParsesWithoutAThrow()
    {
        var noise = new Noise(4242);
        var verbs = OscMap.Reference.Select(r => r.Address.Split(' ')[0]).Where(a => a.StartsWith(OscMap.Prefix, StringComparison.Ordinal))
            .Select(a => a[OscMap.Prefix.Length..].Split('/')[0]).Distinct(StringComparer.Ordinal).ToArray();
        string[] words = { "", "1", "0", "-1", "toggle", "index", "bank", "on", "off", "3", "1:2:3:4", "..", "%", new string('9', 400) };
        object?[] args = { null, 1, -1, int.MaxValue, int.MinValue, 1e30f, float.NaN, float.PositiveInfinity, float.NegativeInfinity, 0.5f, "Walk-in", "", new string('x', 5000), long.MaxValue, 1e308, double.NaN };
        words = words.Concat(Hostile).ToArray();
        args = args.Concat(Hostile).ToArray();
        var messages = Enumerable.Range(0, 20000).Select(_ =>
        {
            var address = OscMap.Prefix + verbs[noise.Next(verbs.Length)];
            for (var k = noise.Next(3); k > 0; k--) address += "/" + words[noise.Next(words.Length)];
            var carried = new object?[noise.Next(3)];
            for (var k = 0; k < carried.Length; k++) carried[k] = args[noise.Next(args.Length)];
            return OscMessage.Of(address, carried);
        });
        var failures = Throws("OscMap.ToLine → ControlProtocol.Parse", messages, m =>
        {
            if (OscMap.ToLine(m) is { } line) ControlProtocol.Parse(line);
        });
        Assert.True(verbs.Length > 40, $"the map's verbs were not found ({verbs.Length})");
        Assert.True(failures.Count == 0, string.Join("\n", failures));
    }
}
