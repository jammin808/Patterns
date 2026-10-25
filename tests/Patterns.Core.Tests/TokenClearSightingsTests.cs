using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 89 (PeerLink profile 13.6.9): the receiver's backstop — the pairing token seen in clear from another machine is
/// marked, once per address and per token, refusing nothing; the marks go with the token; the store is bounded and kept
/// across a restart; the Super Check's REMOTE row says it amber with the fix, and only on a paired desk.
/// </summary>
public class TokenClearSightingsTests
{
    private const string Token = "K7QM-3XWD-P9RA";
    private static readonly DateTime T0 = new(2026, 10, 25, 20, 0, 0, DateTimeKind.Utc);

    [Fact]
    public void EachAddressIsMarkedOncePerTokenAndRepeatsAddTheWayItCameAndTheCount()
    {
        var seen = new TokenClearSightings();
        Assert.True(seen.Note(Token, "10.0.0.5", "web", T0));                        // the first: the caller journals it
        Assert.False(seen.Note(Token, "10.0.0.5", "web", T0.AddSeconds(5)));         // a repeat: no second row
        Assert.False(seen.Note(" k7qm3xwd p9ra ", "10.0.0.5", "wire", T0.AddSeconds(9)));   // the same token, written another way
        Assert.True(seen.Note(Token, "10.0.0.7", "wire", T0.AddSeconds(10)));
        var rows = seen.For(Token);
        Assert.Equal(2, rows.Count);
        Assert.Equal(new ClearSighting("10.0.0.5", "web,wire", T0, T0.AddSeconds(9), 3), rows[0]);
        Assert.Equal(new ClearSighting("10.0.0.7", "wire", T0.AddSeconds(10), T0.AddSeconds(10), 1), rows[1]);
        Assert.Equal("seen in clear from 10.0.0.5, 10.0.0.7 — rotate it: NEW TOKEN", TokenClearSightings.Words(rows));
        Assert.Equal("over plain http and as plain AUTH on the wire", TokenClearSightings.HowWords(rows[0].Via));
    }

    [Fact]
    public void TheMarksGoWithTheTokenAndAnOpenDeskHasNothingToMark()
    {
        var seen = new TokenClearSightings();
        Assert.False(seen.Note("", "10.0.0.5", "web", T0));                          // no token set: nothing to rotate
        Assert.Empty(seen.For(""));
        Assert.True(seen.Note(Token, "10.0.0.5", "web", T0));
        Assert.Empty(seen.For("ABCD-EFGH-JKMN"));                                    // NEW TOKEN: none stands for the new one
        Assert.Empty(seen.For(""));
        Assert.True(seen.Note("ABCD-EFGH-JKMN", "10.0.0.5", "web", T0.AddMinutes(1)));   // the new token seen in clear is marked afresh
        Assert.Empty(seen.For(Token));                                               // and the old token's marks are gone, not kept beside it
        Assert.Equal("", TokenClearSightings.Words(Array.Empty<ClearSighting>()));
    }

    [Fact]
    public void TheStoreIsBoundedTheAddressHeardLongestAgoGivingWayAndTheWordsCountPastFour()
    {
        var seen = new TokenClearSightings();
        for (var i = 0; i < TokenClearSightings.MaxAddresses + 5; i++)
            Assert.True(seen.Note(Token, $"10.0.1.{i.ToString(System.Globalization.CultureInfo.InvariantCulture)}", "web", T0.AddSeconds(i)));
        var rows = seen.For(Token);
        Assert.Equal(TokenClearSightings.MaxAddresses, rows.Count);
        Assert.DoesNotContain(rows, r => r.Address == "10.0.1.0");                   // the oldest gave way
        Assert.Contains(rows, r => r.Address == "10.0.1.36");
        Assert.Equal($"seen in clear from 10.0.1.5, 10.0.1.6, 10.0.1.7, 10.0.1.8 and {(TokenClearSightings.MaxAddresses - TokenClearSightings.Named).ToString(System.Globalization.CultureInfo.InvariantCulture)} more — rotate it: NEW TOKEN",
            TokenClearSightings.Words(rows));
    }

    [Fact]
    public void TheMarksStandAfterARestartUntilTheTokenChangesAndTheFileHoldsNoTokenNorItsPlainHash()
    {
        var folder = Path.Combine(Path.GetTempPath(), "patterns-clearseen-" + Guid.NewGuid().ToString("N"));
        try
        {
            var before = new TokenClearSightings(folder);
            Assert.True(before.Note(Token, "10.0.0.5", "web", T0));
            Assert.False(before.Note(Token, "10.0.0.5", "wire", T0.AddSeconds(5)));            // a repeat within the minute: in memory
            Assert.True(before.Note(Token, "10.0.0.7", "wire", T0.AddSeconds(10)));             // a first sighting: written with the repeat's
            var file = Path.Combine(folder, TokenClearSightings.FileName);
            var text = File.ReadAllText(file);
            Assert.DoesNotContain(PairingToken.Normal(Token), PairingToken.Normal(text));
            var plainHash = Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(PairingToken.Normal(Token))));
            Assert.DoesNotContain(plainHash, text, StringComparison.OrdinalIgnoreCase);       // keyed by a salt of the store's own

            // A restart: the same marks, for the same token, with when and from where.
            var after = new TokenClearSightings(folder);
            Assert.Equal(before.For(Token), after.For(Token));
            Assert.Equal(new ClearSighting("10.0.0.5", "web,wire", T0, T0.AddSeconds(5), 2), after.For(Token)[0]);
            Assert.False(after.Note(Token, "10.0.0.5", "web", T0.AddMinutes(5)));               // still the first sighting's: no second journal row
            Assert.Empty(after.For("ABCD-EFGH-JKMN"));                                         // a new token: none stands
            Assert.Empty(new TokenClearSightings(folder).For("ABCD-EFGH-JKMN"));

            // A torn file: none stand, and the store keeps working.
            File.WriteAllText(file, "{\"v\":1,\"salt\":");
            var torn = new TokenClearSightings(folder);
            Assert.Empty(torn.For(Token));
            Assert.True(torn.Note(Token, "10.0.0.9", "web", T0));
            Assert.Equal("10.0.0.9", Assert.Single(new TokenClearSightings(folder).For(Token)).Address);
        }
        finally
        {
            if (Directory.Exists(folder)) Directory.Delete(folder, recursive: true);
        }
    }

    [Fact]
    public void TheSuperChecksRemoteRowSaysItAmberWithTheFixOnlyOnAPairedDesk()
    {
        static CheckRow? Row(CheckFacts f) => SuperCheck.Run(f).Rows.SingleOrDefault(r => r.Section == "REMOTE" && r.Item == "Pairing token");
        var seen = new[] { new ClearSighting("10.0.0.5", "web", T0, T0, 1), new ClearSighting("10.0.0.7", "wire", T0, T0, 1) };

        Assert.Null(Row(new CheckFacts { RemoteEnabled = true, RemoteToken = true }));
        Assert.Null(Row(new CheckFacts { RemoteEnabled = true, RemoteToken = false, TokenSeenInClear = seen }));    // open: no token to rotate
        Assert.Null(Row(new CheckFacts { RemoteEnabled = false, RemoteToken = true, TokenSeenInClear = seen }));    // remote off: its row says off
        var row = Row(new CheckFacts { RemoteEnabled = true, RemoteUrl = "http://10.0.0.1:9696/", RemoteToken = true, TokenSeenInClear = seen });
        Assert.NotNull(row);
        Assert.Equal(CheckLight.Amber, row!.Light);
        Assert.Equal("seen in clear from 10.0.0.5, 10.0.0.7 — rotate it: NEW TOKEN", row.Value);
        Assert.StartsWith("FIX:", row.Note);
        Assert.Contains("over plain http and as plain AUTH on the wire", row.Note);
        Assert.Contains("NEW TOKEN", row.Note);
        Assert.Contains(CompanionModule.Version, row.Note);
        // The dashboard's REMOTE tile says it too, amber, with the same words; green again with none.
        var tile = Assert.Single(HealthDashboard.Tiles(new CheckFacts { RemoteEnabled = true, RemoteToken = true, TokenSeenInClear = seen }), t => t.Id == "remote");
        Assert.Equal((CheckLight.Amber, "token in clear", "seen in clear from 10.0.0.5, 10.0.0.7 — rotate it: NEW TOKEN"), (tile.Light, tile.Value, tile.Detail));
        Assert.Equal(CheckLight.Green, Assert.Single(HealthDashboard.Tiles(new CheckFacts { RemoteEnabled = true, RemoteToken = true }), t => t.Id == "remote").Light);
        // The remote row itself still says what the network trusts.
        Assert.Contains(SuperCheck.Run(new CheckFacts { RemoteEnabled = true, RemoteToken = true, TokenSeenInClear = seen }).Rows,
            r => r.Item == "Remote control" && r.Light == CheckLight.Green);
    }
}
