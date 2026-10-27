using System.Text.RegularExpressions;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 65: the show's pairing token — what a remote presents before the desk runs a mutating
/// verb — and the wire's side of it: AUTH parsed, the queries a connection may send unpaired, the
/// headers that carry the token and the admin passcode, and Super Check's Remote row saying what
/// the network trusts.
/// </summary>
public class PairingTokenTests
{
    private static readonly Regex Shape = new("^[ABCDEFGHJKMNPQRSTUVWXYZ23456789]{4}-[ABCDEFGHJKMNPQRSTUVWXYZ23456789]{4}-[ABCDEFGHJKMNPQRSTUVWXYZ23456789]{4}$");

    [Fact]
    public void ANewTokenIsTwelveReadableSymbolsInThreeGroupsAndNeverTheSameTwice()
    {
        var seen = new HashSet<string>();
        for (var i = 0; i < 200; i++)
        {
            var token = PairingToken.New();
            Assert.Matches(Shape, token);
            Assert.True(seen.Add(token), $"{token} came twice");
        }
        Assert.DoesNotContain('0', PairingToken.Alphabet);
        Assert.DoesNotContain('O', PairingToken.Alphabet);
        Assert.DoesNotContain('1', PairingToken.Alphabet);
        Assert.DoesNotContain('I', PairingToken.Alphabet);
        Assert.DoesNotContain('L', PairingToken.Alphabet);
    }

    [Fact]
    public void TheCompareIgnoresDashesSpacesAndCaseAndAnOpenDeskMatchesNothing()
    {
        Assert.True(PairingToken.Matches("K7QM-3XWD-P9RA", "K7QM-3XWD-P9RA"));
        Assert.True(PairingToken.Matches("K7QM-3XWD-P9RA", " k7qm3xwd p9ra "));
        Assert.True(PairingToken.Matches("k7qm3xwdp9ra", "K7QM-3XWD-P9RA"));
        Assert.False(PairingToken.Matches("K7QM-3XWD-P9RA", "K7QM-3XWD-P9RB"));
        Assert.False(PairingToken.Matches("K7QM-3XWD-P9RA", "K7QM-3XWD-P9R"));
        Assert.False(PairingToken.Matches("K7QM-3XWD-P9RA", ""));
        Assert.False(PairingToken.Matches("", ""));            // nothing set: nothing matches — the callers ask Needed first
        Assert.False(PairingToken.Matches(null, null));
        Assert.False(PairingToken.Needed(""));
        Assert.False(PairingToken.Needed("  "));
        Assert.False(PairingToken.Needed(null));
        Assert.True(PairingToken.Needed("K7QM-3XWD-P9RA"));
        Assert.Equal("K7QM3XWDP9RA", PairingToken.Normal(" k7qm-3xwd p9ra\n"));
    }

    /// <summary>
    /// Show Bus 1.3 §13.5.1, §13.6.8: the token's two forms. <see cref="PairingToken.Normal"/> stays the key §7's
    /// legacy proof and the web header compare by — <c>-</c>, space, tab, CR and LF removed, each other unit upper-cased by
    /// its simple mapping — and <see cref="PairingToken.Canonical"/> is 13.5.1's: every separator 13.5.1 lists removed, only
    /// <c>a</c>–<c>z</c> upper-cased, letters and digits alone left, or none.
    /// </summary>
    [Fact]
    public void TheTokenHasTheLegacyKeyAndThePeerLinkCanonicalForm()
    {
        Assert.Equal("K7QM3XWDP9RA", PairingToken.Normal(" k7qm-3xwd p9ra\n"));
        Assert.Equal("K7QM3XWDP9RA", PairingToken.Canonical(" k7qm-3xwd p9ra\n"));
        Assert.Equal("K7QM\u00A03XWD", PairingToken.Normal("k7qm\u00A03xwd"));                 // the legacy key keeps what 1.1 desks kept
        Assert.Equal("K7QM3XWD", PairingToken.Canonical("k7qm\u00A03xwd"));
        Assert.Equal("BÜHNE", PairingToken.Normal("bühne"));
        Assert.Null(PairingToken.Canonical("bühne"));                                          // 13.5.1 upper-cases a to z alone, and keeps letters and digits
        Assert.Null(PairingToken.Canonical(""));
        Assert.Null(PairingToken.Canonical(new string('A', 65)));
        Assert.Equal(new string('A', 64), PairingToken.Canonical(new string('a', 64)));
        // A token NEW TOKEN makes has one form: the two keys name the same letters and digits.
        for (var i = 0; i < 50; i++)
        {
            var token = PairingToken.New();
            Assert.Equal(PairingToken.Normal(token), PairingToken.Canonical(token));
            Assert.Null(PairingToken.EntryRefusal(token));
        }
    }

    /// <summary>
    /// The Remote page takes a token only when its two forms agree on letters and digits — empty (open) taken too —
    /// and refuses the rest in the contract's words: 13.5.1's for a token of other characters, 13.6.8's for one holding an
    /// invisible character the legacy key keeps. A token an older build saved that it would not take now is kept, and the
    /// page says so beside it (<see cref="PairingToken.LinkWords"/>); the legacy proof still takes it.
    /// </summary>
    [Fact]
    public void TheRemotePageTakesATokenOnlyWhenItsTwoFormsAgree()
    {
        Assert.Null(PairingToken.EntryRefusal(""));
        Assert.Null(PairingToken.EntryRefusal(null));
        Assert.Null(PairingToken.EntryRefusal("K7QM-3XWD-P9RA"));
        Assert.Null(PairingToken.EntryRefusal("k7qm 3xwd p9ra"));
        Assert.Null(PairingToken.EntryRefusal("show2026"));
        Assert.Equal(PeerLink.CodeShapeWords, PairingToken.EntryRefusal("bühne-7"));
        Assert.Equal(PeerLink.CodeShapeWords, PairingToken.EntryRefusal("hunter_2!"));
        Assert.Equal(PeerLink.CodeShapeWords, PairingToken.EntryRefusal(new string('A', 65)));
        Assert.Equal(PeerLink.CodeShapeWords, PairingToken.EntryRefusal("k7qm\u20113xwd"));      // a non-breaking hyphen is not one 13.5.1 removes
        foreach (var invisible in new[] { "\u00A0", "\uFEFF", "\u2028", "\u3000", "\u000B", "\u0085", "\u2009" })
            Assert.Equal(PeerLink.InvisibleWords, PairingToken.EntryRefusal("K7QM" + invisible + "3XWD"));
        Assert.StartsWith("PeerLink pairs a token of letters and digits", PeerLink.CodeShapeWords, StringComparison.Ordinal);
        Assert.Equal("this token holds an invisible character — retype it", PeerLink.InvisibleWords);

        // A token an older build saved is kept as it is: the legacy proof and the web header still take it, and the page says why it reaches only an older build.
        const string saved = "hunter_2!";
        Assert.True(PairingToken.Matches(saved, "HUNTER_2!"));
        Assert.Equal(PeerLink.CodeShapeWords, PairingToken.LinkWords(saved));
        Assert.Equal("", PairingToken.LinkWords("K7QM-3XWD-P9RA"));
        Assert.Equal("", PairingToken.LinkWords(""));
        var nonce = WireProof.NewNonce();
        Assert.True(WireProof.Verify(saved, nonce, "Stagewatch", WireProof.Proof(saved, nonce, "Stagewatch")));
    }

    [Fact]
    public void TheConfigKeepsTheTokenAndTheBindTrimmed()
    {
        var cfg = new ControlConfig { Token = "  K7QM-3XWD-P9RA ", Bind = " 10.0.0.5 " };
        Assert.Equal("K7QM-3XWD-P9RA", cfg.Token);
        Assert.Equal("10.0.0.5", cfg.Bind);
        Assert.Equal("", new ControlConfig().Token);
        Assert.Equal("", new ControlConfig().Bind);
    }

    [Fact]
    public void AuthIsParsedAsItsOwnKindAndTheQueriesAreTheLinesAnUnpairedConnectionMaySend()
    {
        var auth = ControlProtocol.Parse("AUTH K7QM-3XWD-P9RA");
        Assert.Equal(RemoteCommandKind.Auth, auth.Kind);
        Assert.Equal("K7QM-3XWD-P9RA", auth.Text);
        Assert.Equal(RemoteCommandKind.Auth, ControlProtocol.Parse("auth k7qm").Kind);
        Assert.Equal(RemoteCommandKind.Unknown, ControlProtocol.Parse("AUTH").Kind);

        foreach (var query in new[] { "PING", "STATUS", "HELLO FOH deck", "AUTH x", "CUE LIST", "TWIN STATUS", "NODES", "STAGE STATUS", "MENU PGM", "MENU PAGE Looks", "NAV", "MIDI", "EYE", "what is this" })
        {
            Assert.True(ControlProtocol.IsQuery(ControlProtocol.Parse(query)), query);
        }
        // Round 75: RECORD subscribes a connection to the desk's actions and NAV DECK writes where a deck is — a standing, not a question.
        foreach (var verb in new[] { "BLACKOUT ON", "GO", "CUE GO abc", "CUE STANDBY NEXT", "STOPALL", "LOOK 5", "STAGE ACK id", "NEXT", "OUTPUTS OFF", "RECORD ON", "RECORD OFF", "NAV DECK PLAN › Looks", "NAV Looks", "LOOK SAVE Walk-in" })
        {
            Assert.False(ControlProtocol.IsQuery(ControlProtocol.Parse(verb)), verb);
        }
        Assert.Contains("AUTH <token>", ControlProtocol.NotPaired);
        Assert.Contains("X-Patterns-Token", ControlProtocol.NotPaired);
        Assert.Contains("TRUST", ControlProtocol.NotPaired);
        Assert.Contains("TRUST", ControlProtocol.WrongToken);
    }

    /// <summary>
    /// ADR-016 (round 91, scope A): what a connection may ask before it pairs is an allow-list — the handshake and the five
    /// reads the maintainer left open, and an unknown line for its own refusal — and every other kind is on the refused
    /// side, the show's state (STATUS), the Eye and the context first among them. Every kind the wire has is on one side,
    /// named: a kind added later is refused until it is put on the list here, so the fence fails closed.
    /// </summary>
    [Fact]
    public void BeforePairingTheWireAnswersTheHandshakeAndScopeAsFiveReadsAndEveryOtherKindWaits()
    {
        var open = new[]
        {
            RemoteCommandKind.Hello, RemoteCommandKind.Auth, RemoteCommandKind.AuthProof, RemoteCommandKind.Nonce, RemoteCommandKind.Ping,
            RemoteCommandKind.NavStatus, RemoteCommandKind.Menu, RemoteCommandKind.CueList, RemoteCommandKind.ArcadeStatus,
            RemoteCommandKind.PlayStatus, RemoteCommandKind.AssistantAsk, RemoteCommandKind.Unknown,
        };
        var refusedReads = new[]
        {
            RemoteCommandKind.Status, RemoteCommandKind.Context, RemoteCommandKind.TwinStatus, RemoteCommandKind.ShowLockStatus,
            RemoteCommandKind.CalibrationStatus, RemoteCommandKind.NodesStatus, RemoteCommandKind.StageStatus, RemoteCommandKind.RigDayStatus,
            RemoteCommandKind.ScreenSignal, RemoteCommandKind.ScreenEdid, RemoteCommandKind.RigStatus, RemoteCommandKind.CommissionStatus,
            RemoteCommandKind.EyeStatus, RemoteCommandKind.EyeAt, RemoteCommandKind.MidiStatus, RemoteCommandKind.StagewatchStatus,
        };
        var verbs = new[] { RemoteCommandKind.Action, RemoteCommandKind.Record, RemoteCommandKind.NavDeck };
        Assert.Equal(open, ControlProtocol.OpenKinds);
        Assert.Equal(Enum.GetValues<RemoteCommandKind>().Order(), open.Concat(refusedReads).Concat(verbs).Order());   // every kind on one side, once
        foreach (var kind in open) Assert.True(ControlProtocol.OpenBeforePairing(new RemoteCommand(kind, default)), kind.ToString());
        foreach (var kind in refusedReads.Concat(verbs)) Assert.False(ControlProtocol.OpenBeforePairing(new RemoteCommand(kind, default)), kind.ToString());
        foreach (var kind in refusedReads) Assert.True(ControlProtocol.IsQuery(new RemoteCommand(kind, default)), kind.ToString());

        // Parsed as a deck and the desk's own asks of other machines spell them.
        foreach (var line in new[] { "HELLO FOH deck", "AUTH x", "AUTH-PROOF ab", "NONCE", "PING", "PING 1760101234567", "NAV", "NAV STATUS", "NAV PAGES", "NAV RAILS",
                     "MENU PGM", "MENU PAGE Looks", "CUE LIST", "ARCADE STATUS", "ARCADE GAMES", "ARCADE SCORES pong", "PLAY STATUS", "PLAY RESULTS 3", "PLAY QUEUE",
                     "AUDIENCE STATUS", "ASSISTANT ASK what is on air", "ASSISTANT MODERATE a question", "what is this" })
        {
            Assert.True(ControlProtocol.OpenBeforePairing(ControlProtocol.Parse(line)), line);
        }
        foreach (var line in new[] { "STATUS", "EYE", "EYE STATUS", "EYE GRAPH", "EYE AT 12:00", "CONTEXT", "CONTEXT STATUS", "TWIN STATUS", "SHOWLOCK STATUS",
                     "CALIBRATE STATUS", "NODES", "STAGE STATUS", "RIGDAY STATUS", "ALIGN STATUS", "SCREEN 2 SIGNAL", "SCREEN 2 EDID", "RIG STATUS", "COMMISSION", "MIDI",
                     "BLACKOUT ON", "RECORD ON", "NAV DECK PLAN › Looks", "NAV Looks", "AUDIENCE ON", "PLAY START", "STAGEWATCH", "STAGEWATCH STATUS", "STAGEWATCH LINK 10.0.0.9", "STAGEWATCH TRUST" })
        {
            Assert.False(ControlProtocol.OpenBeforePairing(ControlProtocol.Parse(line)), line);
        }

        // The refusal's words start as a refused verb's do, say why and how to pair; the log names a read by its kind.
        Assert.StartsWith("not paired — ", ControlProtocol.StateNotPaired, StringComparison.Ordinal);
        Assert.Contains("shows its state only to a paired connection", ControlProtocol.StateNotPaired, StringComparison.Ordinal);
        Assert.Contains("AUTH-PROOF", ControlProtocol.StateNotPaired, StringComparison.Ordinal);
        Assert.Contains("TRUST", ControlProtocol.StateNotPaired, StringComparison.Ordinal);
        Assert.Equal("STATUS", ControlProtocol.ReadName(ControlProtocol.Parse("STATUS")));
        Assert.Equal("EYE AT", ControlProtocol.ReadName(ControlProtocol.Parse("EYE AT 2026-10-26T12:00:00Z")));          // the time a sender wrote is not repeated
        Assert.Equal("ALIGN STATUS", ControlProtocol.ReadName(ControlProtocol.Parse("ALIGN STATUS")));
        Assert.Equal("SCREEN SIGNAL", ControlProtocol.ReadName(ControlProtocol.Parse("SCREEN 2 SIGNAL")));
        Assert.Equal(
            new[] { "STATUS", "CONTEXT", "TWIN STATUS", "SHOWLOCK STATUS", "CALIBRATE STATUS", "NODES", "STAGE STATUS", "RIGDAY STATUS", "SCREEN SIGNAL", "SCREEN EDID", "RIG STATUS", "COMMISSION", "EYE", "EYE AT", "MIDI", "STAGEWATCH STATUS" },
            refusedReads.Select(k => ControlProtocol.ReadName(new RemoteCommand(k, default))));                    // each refused read in the wire's own words
    }

    /// <summary>
    /// ADR-016: the Super Check's REMOTE row "Wire reads" — on a paired desk only: green with the rule and what scope A leaves
    /// open while nothing was refused; amber with the count, the last read and its address, and the fix once a read was.
    /// </summary>
    [Fact]
    public void ThePairedDesksWireReadsRowSaysTheRuleAndTurnsAmberWithTheCountAndTheLastRefusedRead()
    {
        var quiet = Assert.Single(SuperCheck.Run(new CheckFacts { RemoteEnabled = true, RemoteToken = true }).Rows, r => r.Item == "Wire reads");
        Assert.Equal(("REMOTE", CheckLight.Green, "paired connections only"), (quiet.Section, quiet.Light, quiet.Value));
        Assert.Contains("hears NONCE and no STATE", quiet.Note, StringComparison.Ordinal);
        Assert.Contains(ControlProtocol.OpenBeforePairingWords, quiet.Note, StringComparison.Ordinal);

        var one = Assert.Single(SuperCheck.Run(new CheckFacts { RemoteEnabled = true, RemoteToken = true, WireReadsRefused = 1, WireReadRefusedLast = "STATUS from 10.0.0.7" }).Rows, r => r.Item == "Wire reads");
        Assert.Equal((CheckLight.Amber, "paired connections only · 1 unpaired read refused"), (one.Light, one.Value));
        Assert.StartsWith("FIX: ", one.Note, StringComparison.Ordinal);
        Assert.Contains("the last: STATUS from 10.0.0.7", one.Note, StringComparison.Ordinal);
        Assert.Contains("Remote page, TRUST", one.Note, StringComparison.Ordinal);
        Assert.Equal("paired connections only · 3 unpaired reads refused", Assert.Single(SuperCheck.Run(new CheckFacts { RemoteEnabled = true, RemoteToken = true, WireReadsRefused = 3, WireReadRefusedLast = "EYE from 10.0.0.7" }).Rows, r => r.Item == "Wire reads").Value);

        Assert.Contains($"patterns.log names each address once, at most {ControlProtocol.UnpairedReadsNamedMost} addresses", one.Note, StringComparison.Ordinal);

        // The dashboard's REMOTE tile follows the row: amber with the count and the last one while a read was refused.
        var tile = Assert.Single(HealthDashboard.Tiles(new CheckFacts { RemoteEnabled = true, RemoteToken = true, WireReadsRefused = 3, WireReadRefusedLast = "EYE from 10.0.0.7" }), t => t.Id == "remote");
        Assert.Equal((CheckLight.Amber, "reads refused"), (tile.Light, tile.Value));
        Assert.Contains("3 unpaired reads refused — the last: EYE from 10.0.0.7", tile.Detail, StringComparison.Ordinal);
        Assert.Equal(CheckLight.Green, Assert.Single(HealthDashboard.Tiles(new CheckFacts { RemoteEnabled = true, RemoteToken = true }), t => t.Id == "remote").Light);
        Assert.Equal(CheckLight.Green, Assert.Single(HealthDashboard.Tiles(new CheckFacts { RemoteEnabled = true, WireReadsRefused = 3 }), t => t.Id == "remote").Light);   // an open desk refuses no read

        // An open desk shows its state to every connection — the Remote control row says so — and a closed one shows nothing.
        Assert.DoesNotContain(SuperCheck.Run(new CheckFacts { RemoteEnabled = true }).Rows, r => r.Item == "Wire reads");
        Assert.DoesNotContain(SuperCheck.Run(new CheckFacts { RemoteEnabled = true, RemoteToken = true, RemoteBindProblem = "'10.0.0' is not an address" }).Rows, r => r.Item == "Wire reads");
        Assert.DoesNotContain(SuperCheck.Run(new CheckFacts { RemoteToken = true }).Rows, r => r.Item == "Wire reads");
    }

    /// <summary>
    /// ADR-016: a refused unpaired read names its address in the log once, for at most
    /// <see cref="ControlProtocol.UnpairedReadsNamedMost"/> addresses; the first new address past the bound writes one line
    /// that says no more are named, and nothing after it — a sender with many source addresses (an IPv6 /64) is a bounded
    /// log. The line names the read by its kind and never repeats the line a sender wrote.
    /// </summary>
    [Fact]
    public void ARefusedUnpairedReadNamesEachAddressOnceAtMostTheBoundThenOneLineSaysNoMore()
    {
        var said = new SaidOnce(ControlProtocol.UnpairedReadsNamedMost);
        var lines = new List<string>();
        for (var round = 0; round < 3; round++)
        {
            for (var i = 1; i <= 5000; i++)
            {
                var address = $"fd00::{i:x}";
                if (ControlProtocol.UnpairedReadLine(said.Note(address), address, "STATUS") is { } line) lines.Add(line);
            }
        }
        Assert.Equal(ControlProtocol.UnpairedReadsNamedMost + 1, lines.Count);
        Assert.Equal(64, lines.Take(64).Distinct(StringComparer.Ordinal).Count());
        Assert.All(lines.Take(64), l => Assert.Matches(@"^Wire: fd00::[0-9a-f]+ asked STATUS before pairing — refused: .*\(ADR-016\).* Said once for this address\.$", l));
        Assert.Equal("Wire: fd00::1 asked STATUS before pairing — refused: this desk shows its state only to a paired connection (ADR-016); a deck or a link of yours needs the pairing token (Remote page, TRUST). Said once for this address.", lines[0]);
        Assert.Equal("Wire: reads refused unpaired from more than 64 addresses — no more addresses are named in this log; the Super Check's REMOTE row counts every refusal.", lines[^1]);
        Assert.Null(ControlProtocol.UnpairedReadLine(SaidOnceAnswer.Quiet, "10.0.0.7", "EYE"));

        // The last word a connection that had paired hears when the token changes under it: refused as a verb is, and why.
        Assert.StartsWith("not paired — ", ControlProtocol.TokenChanged, StringComparison.Ordinal);
        Assert.Contains("pairing token changed", ControlProtocol.TokenChanged, StringComparison.Ordinal);
        Assert.Contains("closed", ControlProtocol.TokenChanged, StringComparison.Ordinal);
    }

    [Fact]
    public void TheTokenAndThePasscodeRideInHeadersWhateverTheirCase()
    {
        var head = HttpHead.Parse("POST /api/cmd HTTP/1.1\r\nHost: a\r\nx-patterns-token: K7QM-3XWD-P9RA\r\nX-PATTERNS-PASS: open-sesame\r\nX-Patterns-Client: phone\r\n", HttpLimits.Control);
        Assert.True(head.Ok);
        Assert.Equal("K7QM-3XWD-P9RA", head.Token);
        Assert.Equal("open-sesame", head.Pass);
        Assert.True(head.ClientHeader);
        var bare = HttpHead.Parse("GET /api/state HTTP/1.1\r\nHost: a\r\n", HttpLimits.Control);
        Assert.Equal("", bare.Token);
        Assert.Equal("", bare.Pass);
        Assert.Equal("X-Patterns-Token", HttpHead.TokenHeader);
        Assert.Equal("X-Patterns-Pass", HttpHead.PassHeader);
    }

    [Fact]
    public void TheRemoteRowSaysWhatTheNetworkTrusts()
    {
        static CheckRow Row(CheckFacts f) => SuperCheck.Run(f).Rows.Single(r => r.Section == "REMOTE" && r.Item == "Remote control");

        var off = Row(new CheckFacts { RemoteEnabled = false });
        Assert.Equal(CheckLight.Grey, off.Light);

        var open = Row(new CheckFacts { RemoteEnabled = true, RemoteUrl = "http://10.0.0.5:9696/" });
        Assert.Equal(CheckLight.Amber, open.Light);
        Assert.Contains("open on every interface", open.Value);
        Assert.StartsWith("FIX:", open.Note);
        Assert.Contains("NEW TOKEN", open.Note);

        var bound = Row(new CheckFacts { RemoteEnabled = true, RemoteUrl = "http://10.0.0.5:9696/", RemoteBind = "10.0.0.5" });
        Assert.Equal(CheckLight.Green, bound.Light);
        Assert.Contains("open on 10.0.0.5 only", bound.Value);
        Assert.Contains("TRUST", bound.Note);

        var paired = Row(new CheckFacts { RemoteEnabled = true, RemoteUrl = "http://10.0.0.5:9696/", RemoteToken = true });
        Assert.Equal(CheckLight.Green, paired.Light);
        Assert.Equal("http://10.0.0.5:9696/ · paired", paired.Value);
    }

    /// <summary>
    /// OSC has no session, so the token never covers it — an open OSC port is amber with the fix, green with the reason
    /// once the control ports are bound, and no row at all while OSC is off. On a paired desk OSC's verbs come only from
    /// this machine or OSC ALLOW (a proof grants nothing to an address, ADR-017), so the row says that rule — bound or
    /// not — and what the list names; none listed says a controller on another machine is refused until it is. Open on
    /// every interface with someone listed, it says a listed address is not a proof; an entry that is not an address is
    /// amber with the fix and named.
    /// </summary>
    [Fact]
    public void TheOscRowSaysThePortCannotPairAndWhatKeepsItToTheControlNetwork()
    {
        static CheckRow? Row(CheckFacts f) => SuperCheck.Run(f).Rows.SingleOrDefault(r => r.Section == "REMOTE" && r.Item == "OSC");

        Assert.Null(Row(new CheckFacts { RemoteEnabled = true, RemoteToken = true }));
        Assert.Null(Row(new CheckFacts { RemoteEnabled = false, OscOpen = true, OscPort = 9698 }));

        var paired = Row(new CheckFacts { RemoteEnabled = true, RemoteToken = true, OscOpen = true, OscPort = 9698 });
        Assert.NotNull(paired);
        Assert.Equal(CheckLight.Green, paired!.Light);
        Assert.Equal("port 9698 · verbs from this machine and OSC ALLOW only", paired.Value);
        Assert.Contains("cannot pair", paired.Note);
        Assert.Contains(OscGate.PairedRule, paired.Note);
        Assert.Contains(OscGate.Listed(0), paired.Note);                                    // none listed: who is refused until one is
        Assert.DoesNotContain("paired on the wire or the web", paired.Note);

        Assert.DoesNotContain("not a proof", paired.Note);                                  // nobody listed: nobody to forge

        var listed = Row(new CheckFacts { RemoteEnabled = true, RemoteToken = true, OscOpen = true, OscPort = 9698, OscAllowed = 2 });
        Assert.Equal(CheckLight.Green, listed!.Light);
        Assert.Contains("OSC ALLOW names 2 addresses", listed.Note);
        Assert.Contains(OscGate.PairedWords(2, Array.Empty<string>(), bound: false), listed.Note);
        Assert.Contains("A listed address is not a proof", listed.Note);                   // what pairing does not cover: the source a datagram claims
        Assert.Contains("bind the control ports", listed.Note);

        // An entry that is not one whole address: amber, the fix first, the entry named and never counted.
        var typo = Row(new CheckFacts { RemoteEnabled = true, RemoteToken = true, OscOpen = true, OscPort = 9698, OscAllowed = 1, OscLeftOut = new[] { "192.168.1" } });
        Assert.Equal(CheckLight.Amber, typo!.Light);
        Assert.Equal("port 9698 · verbs from this machine and OSC ALLOW only", typo.Value);
        Assert.StartsWith("FIX: write each OSC ALLOW entry as one whole address", typo.Note);
        Assert.Contains("OSC ALLOW names 1 address; '192.168.1' is not an address — left out", typo.Note);

        var open = Row(new CheckFacts { RemoteEnabled = true, RemoteToken = false, OscOpen = true, OscPort = 9698 });
        Assert.NotNull(open);
        Assert.Equal(CheckLight.Amber, open!.Light);
        Assert.Equal("port 9698 · open on every interface", open.Value);
        Assert.StartsWith("FIX:", open.Note);
        Assert.Contains("cannot pair", open.Note);
        Assert.Contains("bind", open.Note);

        var bound = Row(new CheckFacts { RemoteEnabled = true, OscOpen = true, OscPort = 9698, RemoteBind = "10.0.0.5" });
        Assert.NotNull(bound);
        Assert.Equal(CheckLight.Green, bound!.Light);
        Assert.Equal("port 9698 · open on 10.0.0.5 only", bound.Value);
        Assert.Contains("cannot pair", bound.Note);
        Assert.DoesNotContain("OSC ALLOW", bound.Note);                                    // an unpaired desk gates nothing by the list
        var unpairedTypo = Row(new CheckFacts { RemoteEnabled = true, OscOpen = true, OscPort = 9698, RemoteBind = "10.0.0.5", OscLeftOut = new[] { "192.168.1" } });
        Assert.Equal(CheckLight.Green, unpairedTypo!.Light);

        // Bound and paired: the bind's reason, and the paired desk's rule beside it.
        var boundPaired = Row(new CheckFacts { RemoteEnabled = true, RemoteToken = true, OscOpen = true, OscPort = 9698, RemoteBind = "10.0.0.5", OscAllowed = 1 });
        Assert.Equal(CheckLight.Green, boundPaired!.Light);
        Assert.Equal("port 9698 · open on 10.0.0.5 only", boundPaired.Value);
        Assert.Contains("the bind keeps it to the control network", boundPaired.Note);
        Assert.Contains(OscGate.PairedRule, boundPaired.Note);
        Assert.Contains("OSC ALLOW names 1 address", boundPaired.Note);
        Assert.DoesNotContain("not a proof", boundPaired.Note);                             // the bind keeps it to the control network
        var boundTypo = Row(new CheckFacts { RemoteEnabled = true, RemoteToken = true, OscOpen = true, OscPort = 9698, RemoteBind = "10.0.0.5", OscLeftOut = new[] { "10" } });
        Assert.Equal(CheckLight.Amber, boundTypo!.Light);
        Assert.Contains("'10' is not an address — left out", boundTypo.Note);
    }

    /// <summary>
    /// Round 79: the token is this desk's own. The Control section never travels the twin link (secrets do not,
    /// round 44), so a standby answers the token typed into it — what the papers used to call "mirrored" is not.
    /// </summary>
    [Fact]
    public void TheTokenIsThisDesksOwnAndNeverMirroredToATwin()
    {
        Assert.Contains(nameof(ShowState.Control), TwinSync.LocalSections);
        Assert.False(TwinSync.IsMirrored(nameof(ShowState.Control)));
        Assert.DoesNotContain(nameof(ShowState.Control), TwinSync.MirroredSections);
    }
}
