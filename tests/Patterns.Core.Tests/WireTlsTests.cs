using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Show Bus 1.3 §13.8.5 on the wire's port: a connection's first byte says whether it is TLS (0x16), a line (printable ASCII and a
/// line's whitespace) or neither, and the second waits within the contract's handshake time; the readers' words say whether the
/// wire serves TLS with the certificate the hello names, or plain only and why; the Super Check's "Wire TLS" row and the REMOTE tile
/// say it the same way.
/// </summary>
public class WireTlsTests
{
    private const string Fingerprint = "sha256:03e9b152ca42518c28128f36def699be8aea5eec11bb9d5afc18a314f8c3eb04";

    [Theory]
    [InlineData(0x16, WireOpening.Tls)]
    [InlineData((byte)'H', WireOpening.Plain)]                 // a deck's HELLO
    [InlineData((byte)'@', WireOpening.Plain)]                 // a link's request id
    [InlineData((byte)'P', WireOpening.Plain)]
    [InlineData((byte)'a', WireOpening.Plain)]
    [InlineData((byte)'~', WireOpening.Plain)]
    [InlineData((byte)' ', WireOpening.Plain)]
    [InlineData((byte)'\n', WireOpening.Plain)]               // a blank line, which the wire ignores
    [InlineData((byte)'\r', WireOpening.Plain)]
    [InlineData((byte)'\t', WireOpening.Plain)]
    [InlineData(0x00, WireOpening.Neither)]
    [InlineData(0x15, WireOpening.Neither)]                    // a TLS alert record
    [InlineData(0x17, WireOpening.Neither)]                    // TLS application data
    [InlineData(0x14, WireOpening.Neither)]                    // a change-cipher-spec record
    [InlineData(0x7F, WireOpening.Neither)]
    [InlineData(0x80, WireOpening.Neither)]                    // an SSLv2 hello
    [InlineData(0xEF, WireOpening.Neither)]                    // a UTF-8 byte-order mark
    [InlineData(0xFF, WireOpening.Neither)]
    public void TheFirstByteSaysTlsALineOrNeither(byte first, WireOpening opens)
        => Assert.Equal(opens, WireTls.Opening(first));

    [Fact]
    public void EveryByteIsOneOfTheThreeAndOnlyTheHandshakeRecordIsTls()
    {
        var tls = Enumerable.Range(0, 256).Where(b => WireTls.Opening((byte)b) == WireOpening.Tls).ToList();
        Assert.Equal(new[] { 0x16 }, tls);
        var plain = Enumerable.Range(0, 256).Count(b => WireTls.Opening((byte)b) == WireOpening.Plain);
        Assert.Equal(95 + 3, plain);                                                             // printable ASCII and tab, LF, CR
    }

    [Fact]
    public void TheFirstByteWaitsTheContractsHandshakeTime()
        => Assert.Equal(PeerLink.Handshake.TotalSeconds, WireLimits.Default.FirstByteSeconds);

    [Fact]
    public void TheLineSaysTlsWithTheCertificateOrPlainOnlyAndWhy()
    {
        var on = WireTls.Line(9697, Fingerprint, "", 3, 2, 0);
        Assert.Equal($"wire 9697: TLS or plain, told by the first byte — certificate {TlsFingerprint.Short(Fingerprint)}, the web remote's and the hello's · 3 over TLS, 2 plain since start · decks stay plain with their proof", on);
        var off = WireTls.Line(9697, "", "no certificate could be made or read: denied", 0, 4, 1);
        Assert.Equal("wire 9697: plain only — no certificate could be made or read: denied; a TLS client is closed unanswered · 0 over TLS, 4 plain since start, 1 closed unanswered", off);
        Assert.StartsWith("wire 9697: plain only — no certificate yet;", WireTls.Line(9697, "not-a-fingerprint", "", 0, 0, 0), StringComparison.Ordinal);
    }

    [Fact]
    public void StatesWordsAreTheLineWithoutTheCountsSoAConnectionNeverMovesThem()
    {
        Assert.Equal($"wire 9697: TLS or plain, told by the first byte — certificate {TlsFingerprint.Short(Fingerprint)}, the web remote's and the hello's · decks stay plain with their proof",
            WireTls.Words(9697, Fingerprint, ""));
        Assert.Equal("wire 9697: plain only — no certificate could be made or read: denied; a TLS client is closed unanswered",
            WireTls.Words(9697, "", "no certificate could be made or read: denied"));
        Assert.DoesNotContain("since start", WireTls.Words(9697, Fingerprint, ""), StringComparison.Ordinal);
    }

    [Fact]
    public void TheLogWordsNameTheAddressAndTheCauseAndNoSecret()
    {
        Assert.Equal("Wire: 10.0.0.7 sent nothing within 10 s — a connector speaks first on this port (its HELLO, or its TLS handshake: Show Bus 1.3 §13.8.5); closed unanswered.", WireTls.SilentWords("10.0.0.7", 10));
        Assert.Equal("Wire: 10.0.0.7 opened with byte 0x80, which starts neither a line nor a TLS handshake — closed unanswered.", WireTls.NeitherWords("10.0.0.7", 0x80));
        Assert.Contains("has no certificate (none made yet)", WireTls.NoCertificateWords("10.0.0.7", ""), StringComparison.Ordinal);
        Assert.Contains("(timed out)", WireTls.HandshakeFailedWords("10.0.0.7", "timed out"), StringComparison.Ordinal);
    }

    [Fact]
    public void TheDoorsAreCountedEachWayAndClosedUnanswered()
    {
        var doors = new WireDoorCounts();
        doors.CountTls();
        doors.CountPlain();
        doors.CountPlain();
        doors.CountUnanswered();
        Assert.Equal((1L, 2L, 1L), (doors.OverTls, doors.Plain, doors.Unanswered));
    }

    private static CheckFacts Facts(string fingerprint, int wire = 9697, string problem = "") => new()
    {
        RemoteEnabled = true, RemoteUrl = "http://10.0.0.5:9696/", RemoteToken = true,
        WirePort = wire, WireTlsFingerprint = fingerprint, WireTlsProblem = problem, WireOverTls = 2, WirePlain = 5, WireUnanswered = 1,
    };

    [Fact]
    public void TheSuperCheckSaysWhetherTheWireServesTls()
    {
        var on = Assert.Single(SuperCheck.Run(Facts(Fingerprint)).Rows, r => r.Section == "REMOTE" && r.Item == "Wire TLS");
        Assert.Equal(CheckLight.Green, on.Light);
        Assert.Equal($"port 9697 · TLS or plain by the first byte · certificate {TlsFingerprint.Short(Fingerprint)}", on.Value);
        Assert.Contains("2 connection(s) over TLS and 5 plain since start; 1 closed unanswered", on.Note, StringComparison.Ordinal);
        Assert.Contains("decks stay plain with their proof", on.Note, StringComparison.Ordinal);

        var off = Assert.Single(SuperCheck.Run(Facts("", problem: "no certificate could be made or read: denied")).Rows, r => r.Section == "REMOTE" && r.Item == "Wire TLS");
        Assert.Equal(CheckLight.Amber, off.Light);
        Assert.Equal("port 9697 · plain only — no certificate", off.Value);
        Assert.StartsWith("FIX: no certificate could be made or read: denied", off.Note, StringComparison.Ordinal);

        // A facts set that names no wire, a desk with remote control off and a bind that is not an address say no such row.
        Assert.DoesNotContain(SuperCheck.Run(Facts(Fingerprint, wire: 0)).Rows, r => r.Item == "Wire TLS");
        Assert.DoesNotContain(SuperCheck.Run(new CheckFacts { RemoteEnabled = false, WirePort = 9697 }).Rows, r => r.Item == "Wire TLS");
        Assert.DoesNotContain(SuperCheck.Run(new CheckFacts { RemoteEnabled = true, WirePort = 9697, RemoteBindProblem = BindAddress.Problem("10.0.0") }).Rows, r => r.Item == "Wire TLS");
    }

    [Fact]
    public void TheRemoteTileIsAmberWhenTheWireCanServeNoTls()
    {
        var off = HealthDashboard.Tiles(Facts("", problem: "denied")).Single(t => t.Id == "remote");
        Assert.Equal((CheckLight.Amber, "wire plain only"), (off.Light, off.Value));
        Assert.Contains("port 9697 serves no TLS — denied", off.Detail, StringComparison.Ordinal);
        var on = HealthDashboard.Tiles(Facts(Fingerprint)).Single(t => t.Id == "remote");
        Assert.Equal((CheckLight.Green, "on"), (on.Light, on.Value));
        Assert.EndsWith(" · wire 9697 TLS or plain by the first byte", on.Detail, StringComparison.Ordinal);
        Assert.Equal(CheckLight.Green, HealthDashboard.Tiles(Facts("", wire: 0)).Single(t => t.Id == "remote").Light);
    }
}
