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
