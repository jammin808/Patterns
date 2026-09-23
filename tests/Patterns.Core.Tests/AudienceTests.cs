using Patterns.Core.Model;
using Patterns.Core.Play;
using Xunit;

namespace Patterns.Core.Tests;

public class AudienceTests
{
    [Fact]
    public void TheAudiencePortAnswersThePlayPagesAndNothingElseAndTheControlPortNeverTheRoom()
    {
        Assert.True(AudienceRoutes.Allows("GET", "/"));
        Assert.True(AudienceRoutes.Allows("GET", "/play?room=ABCD"));
        Assert.True(AudienceRoutes.Allows("GET", "/api/play/state?token=x&since=0&rev=3"));
        foreach (var post in new[] { "/api/play/join", "/api/play/answer", "/api/play/say", "/api/play/vote", "/api/play/draughts" })
        {
            Assert.True(AudienceRoutes.Allows("POST", post), post);
            Assert.False(AudienceRoutes.Allows("GET", post), post);
            Assert.True(AudienceRoutes.AudienceOnly(post), post);
        }
        // The boundary: a command, the state, a picture, the host, the admin, the pad — none of it.
        foreach (var path in new[] { "/api/cmd", "/api/state", "/api/state?since=1", "/mv.jpg", "/pgm.jpg", "/admin", "/api/admin", "/host", "/api/play/host", "/api/play", "/api/play/feed.csv", "/pad", "/api/arcade/key", "/run", "/stage", "/timer", "/api/cues" })
        {
            Assert.False(AudienceRoutes.Allows("GET", path), path);
            Assert.False(AudienceRoutes.Allows("POST", path), path);
        }
        Assert.False(AudienceRoutes.Allows("PUT", "/play"));
        Assert.True(AudienceRoutes.AudienceOnly("/play"));
        Assert.True(AudienceRoutes.AudienceOnly("/api/play/state?rev=1"));
        Assert.False(AudienceRoutes.AudienceOnly("/host"));
        Assert.False(AudienceRoutes.AudienceOnly("/api/play"));
        Assert.False(AudienceRoutes.AudienceOnly("/api/play/feed.csv"));
        Assert.False(AudienceRoutes.AudienceOnly("/"));
    }

    [Fact]
    public void TheRateLimiterCountsASlidingWindowPerKey()
    {
        var limiter = new RateLimiter();
        var t0 = new DateTime(2026, 9, 13, 20, 0, 0, DateTimeKind.Utc);
        var window = TimeSpan.FromMinutes(1);
        for (var i = 0; i < 3; i++) Assert.True(limiter.Allow("join:10.0.0.5", 3, window, t0.AddSeconds(i)));
        Assert.False(limiter.Allow("join:10.0.0.5", 3, window, t0.AddSeconds(10)));
        Assert.True(limiter.Allow("join:10.0.0.6", 3, window, t0.AddSeconds(10)));         // another key, its own count
        Assert.True(limiter.Allow("join:10.0.0.5", 3, window, t0.AddSeconds(61)));         // the first hit fell out of the window
        Assert.False(limiter.Allow("join:10.0.0.5", 3, window, t0.AddSeconds(61)));
        Assert.True(limiter.Allow("anything", 0, window, t0));                              // no limit: always
        Assert.True(limiter.Keys >= 2);
        // Keys long quiet are pruned on the next look a minute on.
        Assert.True(limiter.Allow("join:10.0.0.7", 3, window, t0.AddMinutes(10)));
        Assert.Equal(1, limiter.Keys);
        var budget = new AudienceBudget();
        Assert.Equal(500, budget.MaxPlayers);
        Assert.True(budget.MaxConnections >= budget.MaxLongPolls);
    }

    [Fact]
    public void AFullRoomForgetsItsLongGoneBeforeItTurnsAPhoneAway()
    {
        var now = new DateTime(2026, 9, 13, 20, 0, 0, DateTimeKind.Utc);
        var room = new PlayRoom("ABCD", "Gala", now) { MaxPlayers = 3, IdleForget = TimeSpan.FromHours(1) };
        room.UtcNow = () => now;
        var a = room.TryJoin("A", null);
        var b = room.TryJoin("B", null);
        var c = room.TryJoin("C", null);
        Assert.All(new[] { a, b, c }, j => Assert.NotNull(j.Player));
        var d = room.TryJoin("D", null);
        Assert.Null(d.Player);
        Assert.Contains("the room is full (3)", d.Reason);
        Assert.Equal(3, room.PlayerCount);
        // A phone the room knows always comes back, full or not.
        var back = room.TryJoin("A again", a.Player!.Token);
        Assert.Same(a.Player, back.Player);
        Assert.False(back.Fresh);
        // Two hours on, the long-gone leave and a new phone takes a seat.
        now = now.AddHours(2);
        room.Touch(b.Player!.Token);                                                        // B is still here
        var e = room.TryJoin("E", null);
        Assert.NotNull(e.Player);
        Assert.Equal(2, room.PlayerCount);                                                   // B and E; A and C forgotten
        Assert.Null(room.Find(a.Player.Token));
        Assert.NotNull(room.Find(b.Player.Token));
        Assert.Equal(0, room.Prune(now));
    }

    [Fact]
    public void BehindAVenueNatThePerAddressBudgetsOpenToTheRoomAndThePerPhoneOnesStand()
    {
        var flat = new AudienceBudget();
        Assert.Equal(flat, flat.OnNetwork(AudienceNetwork.Flat, 500));
        var nat = flat.OnNetwork(AudienceNetwork.VenueNat, 500);
        Assert.Equal(1020, nat.JoinsPerAddressPerMinute);
        Assert.Equal(nat.MaxConnections, nat.MaxConnectionsPerAddress);
        Assert.Equal(flat.AnswersPerTokenPerMinute, nat.AnswersPerTokenPerMinute);
        Assert.Equal(flat.SaysPerTokenPerMinute, nat.SaysPerTokenPerMinute);
        Assert.Equal(flat.MaxConnections, nat.MaxConnections);
        Assert.Equal(flat.MaxPlayers, nat.MaxPlayers);
        Assert.Equal(5000, (flat with { JoinsPerAddressPerMinute = 5000 }).OnNetwork(AudienceNetwork.VenueNat, 10).JoinsPerAddressPerMinute);   // a wider budget is never narrowed
        Assert.Equal("venue-nat", AudienceBudget.Wire(AudienceNetwork.VenueNat));
        Assert.Equal("flat", AudienceBudget.Wire(AudienceNetwork.Flat));
        Assert.StartsWith("venue NAT", AudienceBudget.NetworkWords(AudienceNetwork.VenueNat));
        Assert.StartsWith("flat", AudienceBudget.NetworkWords(AudienceNetwork.Flat));
        Assert.Equal("", AudienceBudget.RefusedWords(0, 0, "", AudienceNetwork.Flat));
        Assert.Equal("7 joins refused this minute (all from 10.0.0.1) — phones behind one address? Remote page, AUDIENCE: Network → venue NAT", AudienceBudget.RefusedWords(7, 7, "10.0.0.1", AudienceNetwork.Flat));
        Assert.Equal("1 join refused this minute (all from 10.0.0.1) — the room's own joins-per-minute reached", AudienceBudget.RefusedWords(1, 1, "10.0.0.1", AudienceNetwork.VenueNat));
        Assert.Contains("(3 of them from 10.0.0.2)", AudienceBudget.RefusedWords(5, 3, "10.0.0.2", AudienceNetwork.Flat));
    }

    /// <summary>
    /// A nickname and a group are drawn on the wall with no host between the phone and the room, so they meet the word
    /// list an answer meets: a new phone with a listed word is a guest, a listed group is none, and a phone that renames
    /// itself to a listed word keeps the name it had. The list is the room's own, matched as answers are matched.
    /// </summary>
    [Fact]
    public void ANameOrGroupWithAListedWordNeverReachesTheWall()
    {
        var now = new DateTime(2026, 9, 23, 20, 0, 0, DateTimeKind.Utc);
        var room = new PlayRoom("ABCD", "Gala", now) { BlockedWords = new[] { "rude" } };
        room.UtcNow = () => now;

        var fresh = room.TryJoin("Very RUDE person", null, "rudeboys");
        Assert.StartsWith("Guest ", fresh.Player!.Nick, StringComparison.Ordinal);
        Assert.Equal("", fresh.Player.Group);

        var kept = room.TryJoin("Sam", null, "Table 4");
        Assert.Equal("Sam", kept.Player!.Nick);
        Assert.Equal("Table 4", kept.Player.Group);
        var renamed = room.TryJoin("rude Sam", kept.Player.Token, "rude table");
        Assert.Same(kept.Player, renamed.Player);
        Assert.Equal("Sam", renamed.Player!.Nick);
        Assert.Equal("", renamed.Player.Group);
        var fine = room.TryJoin("Samantha", kept.Player.Token);
        Assert.Equal("Samantha", fine.Player!.Nick);

        Assert.Equal(4, room.NamesRefused);
        Assert.DoesNotContain(room.Players, p => room.IsBlocked(p.Nick) || room.IsBlocked(p.Group));
        Assert.DoesNotContain(room.Leaderboard(10), p => room.IsBlocked(p.Nick) || room.IsBlocked(p.Group));
    }
}
