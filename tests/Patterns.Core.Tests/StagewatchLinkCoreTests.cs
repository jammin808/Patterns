using System.Globalization;
using System.Text.Json;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// What the Stagewatch link keeps on this desk's PeerLink core, below the carrier: a refused key's hold kept by target and kid
/// beside the settings (13.15.3), the §11 results it owes on 13.12.9's schedule and the ids it ran once, and the core's level-0
/// rules the link drives — the redial count back to 0 only after a settled link stayed up <c>peerlink.stable.ms</c> (13.15.2),
/// the bearer only to a local link or a certificate a person pinned (13.6.8), a refused key's public view and its proof again on
/// a fresh socket, matched pongs, another inst's empty clock — and the pin word a person's Trust writes.
/// </summary>
public sealed class StagewatchLinkCoreTests
{
    private const string Kid = "e63c317763b14546";
    private const string OtherKid = "0123456789abcdef";
    private const string Target = "node:swdesk01";
    private const long Wall = 1_760_000_000_000;

    // ---- the holds -----------------------------------------------------------------------------------------------------

    [Fact]
    public void AHoldIsKeptByTargetAndKidBeforeTheCallerGoesOnAndOutlastsARestart()
    {
        using var dir = TempRun.Folder("patterns-holds-");
        var holds = new LinkHolds(dir.Path);
        holds.Hold(Target, Kid, Wall + 90_000, "key", Wall);

        // Written before Hold returned: the file names the target, the kid, the wall end and the word — and nothing else.
        var path = Path.Combine(dir.Path, LinkHolds.FileName);
        Assert.True(File.Exists(path));
        using (var doc = JsonDocument.Parse(File.ReadAllText(path)))
        {
            var row = Assert.Single(doc.RootElement.GetProperty("holds").EnumerateArray());
            Assert.Equal(new[] { "target", "kid", "until", "word" }, row.EnumerateObject().Select(p => p.Name));
            Assert.Equal(Target, row.GetProperty("target").GetString());
            Assert.Equal(Kid, row.GetProperty("kid").GetString());
            Assert.Equal(Wall + 90_000, row.GetProperty("until").GetInt64());
        }

        // A process started inside it reads it back, by target and kid; another target or kid holds nothing.
        var again = new LinkHolds(dir.Path);
        Assert.Equal(new LinkHold(Target, Kid, Wall + 90_000, "key"), again.For(Target, Kid, Wall + 30_000));
        Assert.Null(again.For(Target, OtherKid, Wall + 30_000));
        Assert.Null(again.For("addr:10.0.0.5:9924", Kid, Wall + 30_000));
        Assert.StartsWith("refused (key) — held until ", LinkHolds.Words(again.For(Target, Kid, Wall + 30_000)!), StringComparison.Ordinal);

        // At its end it is gone, and the file says so for the next start.
        Assert.Null(again.For(Target, Kid, Wall + 90_000));
        Assert.Empty(new LinkHolds(dir.Path).All(Wall));
    }

    [Fact]
    public void AHoldIsDroppedWhenTheKeyChangesOrAWordTakesItAndTheStoreIsBounded()
    {
        using var dir = TempRun.Folder("patterns-holds-");
        var holds = new LinkHolds(dir.Path);
        holds.Hold(Target, Kid, Wall + 60_000, "key", Wall);
        holds.Hold("addr:10.0.0.5:9924", Kid, Wall + 60_000, "key", Wall);
        holds.KeyIs(OtherKid, Wall + 1);                                                 // the key changed: every hold of the old one is over
        Assert.Empty(new LinkHolds(dir.Path).All(Wall + 1));

        holds.Hold(Target, OtherKid, Wall + 60_000, "key", Wall);
        holds.Taken(Target, OtherKid, Wall);                                             // a later word took the key
        Assert.Empty(new LinkHolds(dir.Path).All(Wall));

        // Not a kid, or ended already: not kept.
        holds.Hold(Target, "swk_not_a_kid", Wall + 60_000, "key", Wall);
        holds.Hold(Target, Kid, Wall, "key", Wall);
        Assert.Empty(holds.All(Wall));

        // Bounded: past MostTargets the one that ends soonest gives way.
        for (var i = 0; i < LinkHolds.MostTargets + 8; i++)
            holds.Hold("node:sw" + i.ToString("D4", CultureInfo.InvariantCulture), Kid, Wall + 60_000 + i, "key", Wall);
        var kept = new LinkHolds(dir.Path).All(Wall);
        Assert.Equal(LinkHolds.MostTargets, kept.Count);
        Assert.DoesNotContain(kept, h => h.Target == "node:sw0000");
        Assert.Contains(kept, h => h.Target == "node:sw0039");
    }

    [Fact]
    public void AStoredHoldIsNeverShortenedKeepsTheRefusalsWordAndTheViewCodesNoPublicViewAndATakeLeavesItsRateLimit()
    {
        using var dir = TempRun.Folder("patterns-holds-");
        var holds = new LinkHolds(dir.Path);

        // A refusal, then a rate limit inside it that ends later: one hold to the later end, the refusal's word, the rate limit's end kept.
        holds.Hold(Target, Kid, Wall + 60_000, "key", Wall);
        holds.Hold(Target, Kid, Wall + 90_000, "busy", Wall + 1_000);
        Assert.Equal(new LinkHold(Target, Kid, Wall + 90_000, "key", true, Wall + 90_000), new LinkHolds(dir.Path).For(Target, Kid, Wall + 2_000));

        // A later, shorter refusal never shortens it; a word that takes the key leaves the rate limit to its own end.
        holds.Hold(Target, Kid, Wall + 20_000, "binding", Wall + 2_000);
        Assert.Equal((Wall + 90_000, "binding"), (holds.For(Target, Kid, Wall + 3_000)!.UntilWallMs, holds.For(Target, Kid, Wall + 3_000)!.Word));
        holds.Taken(Target, Kid, Wall + 3_000);
        Assert.Equal(new LinkHold(Target, Kid, Wall + 90_000, "busy", true, Wall + 90_000), new LinkHolds(dir.Path).For(Target, Kid, Wall + 3_000));
        holds.Taken(Target, Kid, Wall + 4_000);                                          // a rate limit alone: a take lifts nothing
        Assert.NotNull(holds.For(Target, Kid, Wall + 4_000));

        // A viewing code's refusal lets nothing be dialled inside it: kept with its hold, and read back after a restart.
        holds.Hold("node:sw0002", Kid, Wall + 60_000, "key", Wall, publicView: false);
        holds.Hold("node:sw0002", Kid, Wall + 10_000, "busy", Wall);
        using (var doc = JsonDocument.Parse(File.ReadAllText(Path.Combine(dir.Path, LinkHolds.FileName))))
        {
            var row = doc.RootElement.GetProperty("holds").EnumerateArray().Single(r => r.GetProperty("target").GetString() == "node:sw0002");
            Assert.Equal(new[] { "target", "kid", "until", "word", "public", "busy" }, row.EnumerateObject().Select(p => p.Name));
        }
        Assert.False(new LinkHolds(dir.Path).For("node:sw0002", Kid, Wall + 1_000)!.Public);
    }

    /// <summary>
    /// <i>Link again now</i> (13.15.3, 13.16.6): every hold of the key ends under whatever target it was kept — one the run has not
    /// dialled yet among them — a rate limit's with it, and the store is written before the dial; a hold of another key stays, and an
    /// ended one is gone.
    /// </summary>
    [Fact]
    public void LinkAgainLiftsEveryHoldOfTheKeyUnderAnyTargetAndLeavesAnotherKeys()
    {
        using var dir = TempRun.Folder("patterns-holds-");
        var holds = new LinkHolds(dir.Path);
        holds.Hold(Target, Kid, Wall + 60_000, "key", Wall);
        holds.Hold("addr:10.0.0.5:9924", Kid, Wall + 30_000, "busy", Wall);
        holds.Hold("node:swdesk02", OtherKid, Wall + 60_000, "key", Wall);
        holds.Hold("node:swdesk03", OtherKid, Wall + 1_000, "key", Wall);
        Assert.True(holds.Holding(Kid, Wall + 2_000));

        Assert.Equal(2, holds.Lift(Kid, Wall + 2_000));
        Assert.False(holds.Holding(Kid, Wall + 2_000));
        var kept = new LinkHolds(dir.Path).All(Wall + 2_000);                              // written before the caller dials
        var left = Assert.Single(kept);
        Assert.Equal(("node:swdesk02", OtherKid), (left.Target, left.Kid));
        Assert.DoesNotContain(Kid, File.ReadAllText(Path.Combine(dir.Path, LinkHolds.FileName)), StringComparison.Ordinal);
        Assert.Equal(0, holds.Lift(Kid, Wall + 3_000));                                       // nothing more of it to end
        Assert.True(holds.Holding(OtherKid, Wall + 3_000));
    }

    [Fact]
    public void AHoldsFileThatCannotBeReadStartsTheStoreEmpty()
    {
        using var dir = TempRun.Folder("patterns-holds-");
        File.WriteAllText(Path.Combine(dir.Path, LinkHolds.FileName), "{ not json");
        Assert.Empty(new LinkHolds(dir.Path).All(Wall));
        File.WriteAllText(Path.Combine(dir.Path, LinkHolds.FileName), "{\"v\":1,\"holds\":[{\"target\":\"node:a\",\"kid\":\"" + Kid + "\",\"until\":" + (Wall + 5000).ToString(CultureInfo.InvariantCulture) + ",\"word\":\"made-up\"}]}");
        Assert.Empty(new LinkHolds(dir.Path).All(Wall));                                // a word no verdict class names is not taken
    }

    // ---- what the link owes --------------------------------------------------------------------------------------------

    [Fact]
    public void AnOwedResultIsRetriedFromThreeSecondsDoublingToThirtyForHalfAnHour()
    {
        Assert.Equal(TimeSpan.FromMilliseconds(2400), OwedReports.Wait(0, 0));
        Assert.Equal(TimeSpan.FromMilliseconds(3600), OwedReports.Wait(0, 1));
        Assert.Equal(TimeSpan.FromMilliseconds(6000), OwedReports.Wait(1, 0.5));
        Assert.Equal(TimeSpan.FromMilliseconds(24000), OwedReports.Wait(3, 0.5));
        Assert.Equal(TimeSpan.FromMilliseconds(30000), OwedReports.Wait(4, 0.5));        // 48 s capped at report.retry.max.ms
        Assert.Equal(TimeSpan.FromMilliseconds(36000), OwedReports.Wait(40, 1));

        var owed = new OwedReports();
        Assert.Null(owed.Owe("sw", "p1", "{\"ok\":true}", 0));
        Assert.Equal(new[] { "p1" }, owed.Due("sw", 0).Select(d => d.Id));             // due at once
        owed.Tried("sw", "p1", 0, 0.5);
        Assert.Empty(owed.Due("sw", 2_999));
        Assert.Single(owed.Due("sw", 3_000));
        owed.Tried("sw", "p1", 3_000, 0.5);
        Assert.Empty(owed.Due("sw", 8_999));
        Assert.Single(owed.Due("sw", 9_000));
        Assert.Equal(2, owed.TriesOf("sw", "p1"));

        // Owed again while owed: the schedule stands, the body is the new one.
        Assert.Null(owed.Owe("sw", "p1", "{\"ok\":false}", 9_000));
        Assert.Equal(("p1", "{\"ok\":false}"), owed.Due("sw", 9_000).Single());
        Assert.Equal(2, owed.TriesOf("sw", "p1"));
        Assert.Empty(owed.Due("another", 9_000));

        // Past report.retry.for.ms it is let go and named; a settled one is owed no longer.
        Assert.Null(owed.Owe("sw", "p2", "{}", 10_000));
        Assert.Empty(owed.Expired((long)OwedReports.RetryFor.TotalMilliseconds - 1));
        Assert.Equal(new[] { "p1" }, owed.Expired((long)OwedReports.RetryFor.TotalMilliseconds));
        owed.Settle("sw", "p2");
        Assert.Equal(0, owed.Count);
        Assert.Equal(-1, owed.TriesOf("sw", "p2"));

        // Bounded: past Most the one owed longest is let go and named.
        for (var i = 0; i < OwedReports.Most; i++) Assert.Null(owed.Owe("sw", "q" + i.ToString(CultureInfo.InvariantCulture), "{}", i));
        Assert.Equal("q0", owed.Owe("sw", "q-last", "{}", 200));
        Assert.Equal(OwedReports.Most, owed.Count);
    }

    [Fact]
    public void AnApprovedIdRunsOnceAndTheOldestIsLetGoFirst()
    {
        var ran = new RecentIds();
        Assert.True(ran.Add("a"));
        Assert.False(ran.Add("a"));
        for (var i = 0; i < RecentIds.Kept; i++) ran.Add("id" + i.ToString(CultureInfo.InvariantCulture));
        Assert.Equal(RecentIds.Kept, ran.Count);
        Assert.True(ran.Add("a"));                                                       // let go first, so it is new again
        Assert.False(ran.Add("id" + (RecentIds.Kept - 1).ToString(CultureInfo.InvariantCulture)));
    }

    // ---- the core at level 0 -------------------------------------------------------------------------------------------

    /// <summary>A link with no wait, hold or mark set dials at once, however long this process has run: no unset mark is subtracted.</summary>
    [Fact]
    public void AFreshLinkDialsAtOnceWhateverItsClockReads()
    {
        var at = new LinkNow(123_456_789, Wall);
        var m = new PeerLinkMachine(Settings(local: true), null, at, () => 0.5);
        Assert.Equal(0, m.DialIn(at));
        Assert.Equal(0, m.Record(at).NextMs);
        Assert.NotNull(m.Dial(at));
    }

    [Fact]
    public void AtLevelZeroTheRedialCountReturnsToZeroOnlyAfterASettledLinkStaysUpTenSeconds()
    {
        var m = new PeerLinkMachine(Settings(local: true), null, 0, () => 0.5);
        long t = 0;
        Fail(m, ref t);
        Fail(m, ref t);
        Assert.Equal(2, m.Attempt);

        // Settled — its verdict took the key — and dropped at 9.9 s: the count goes on.
        Up(m, ref t);
        Assert.Equal(new[] { "bearer" }, m.Drain().Select(w => w.Credential).OfType<string>());
        m.Settled(t);
        var from = t;
        for (var at = from + 1000; at < from + 9_900; at += 1000) m.Frame(at);
        m.Closed(from + 9_900);
        t = from + 9_900;
        Assert.Equal(3, m.Attempt);

        // Up 20 s, heard every second, but never settled (a key refused, or none asked yet): nothing resets.
        Up(m, ref t);
        from = t;
        for (var at = from + 1000; at <= from + 20_000; at += 1000) m.Frame(at);
        Assert.Equal(3, m.Attempt);
        m.Closed(from + 20_000);
        t = from + 20_000;
        Assert.Equal(4, m.Attempt);

        // Settled and up for peerlink.stable.ms, never down: back to 0, and its drop is the first failed dial again.
        Up(m, ref t);
        m.Settled(t);
        from = t;
        for (var at = from + 1000; at < from + (long)PeerLink.Stable.TotalMilliseconds; at += 1000) m.Frame(at);
        Assert.Equal(4, m.Attempt);
        m.Frame(from + (long)PeerLink.Stable.TotalMilliseconds);
        Assert.Equal(0, m.Attempt);
        m.Closed(from + 10_500);
        Assert.Equal(1, m.Attempt);
    }

    [Fact]
    public void AtLevelZeroTheBearerGoesToALocalLinkOrAPersonsPinAndNeverToAFirstPin()
    {
        const string cert = "8e8bc45bb14f9c7413604ef78eb1c42b465437cf700ae04a8f9553ffb0286edc";

        // Another machine, no pin: read keyless, its certificate pinned first.
        var off = new PeerLinkMachine(Settings(local: false), null, 0, () => 0.5);
        long t = 0;
        Assert.NotNull(off.Dial(t));
        off.Certificate(t, cert);
        off.FirstMessage(t, frame: true);
        Assert.DoesNotContain(off.Drain(), w => w.Credential is not null);
        Assert.Equal(new LinkPin("swdesk01", cert, PinTrust.First), off.Kept.Pin);

        // The same address reached at loopback (13.1): the bearer goes, over TLS or plain.
        var local = new PeerLinkMachine(Settings(local: false), null, 0, () => 0.5);
        Assert.NotNull(local.Dial(t));
        local.Reached(t, local: true);
        local.Certificate(t, cert);
        local.FirstMessage(t, frame: false);
        Assert.Equal(new[] { "bearer" }, local.Drain().Select(w => w.Credential).OfType<string>());

        // Another machine whose certificate a person pinned: the bearer goes over it.
        var trusted = new PeerLinkMachine(Settings(local: false), new LinkKept(new LinkPin("swdesk01", cert, PinTrust.Person), null, null, null), 0, () => 0.5);
        Assert.NotNull(trusted.Dial(t));
        trusted.Certificate(t, cert);
        trusted.FirstMessage(t, frame: true);
        Assert.Equal(new[] { "bearer" }, trusted.Drain().Select(w => w.Credential).OfType<string>());

        // Another machine whose certificate this desk pinned at first use, kept from before: the same certificate shown
        // again is compared, and still carries no key until a person's Trust.
        var first = new PeerLinkMachine(Settings(local: false), new LinkKept(new LinkPin("swdesk01", cert, PinTrust.First), null, null, null), 0, () => 0.5);
        Assert.NotNull(first.Dial(t));
        first.Certificate(t, cert);
        first.FirstMessage(t, frame: true);
        Assert.DoesNotContain(first.Drain(), w => w.Credential is not null);
        Assert.Equal(new LinkPin("swdesk01", cert, PinTrust.First), first.Kept.Pin);

        // Another machine with no TLS on offer: keyless.
        var plain = new PeerLinkMachine(Settings(local: false), null, 0, () => 0.5);
        Assert.NotNull(plain.Dial(t));
        plain.NoTls(t);
        plain.FirstMessage(t, frame: true);
        Assert.DoesNotContain(plain.Drain(), w => w.Credential is not null);
    }

    [Fact]
    public void ARefusedKeyKeepsThePublicViewAndIsPresentedAgainOnAFreshSocketAtItsEnd()
    {
        var m = new PeerLinkMachine(Settings(local: true), null, new LinkNow(0, Wall), () => 0.5);
        long t = 0;
        Up(m, ref t);
        m.Drain();
        m.KeyRefused(new LinkNow(t, Wall + t), "key", 60_000);
        Assert.Equal(Wall + t + 60_000, m.HeldUntilWall(new LinkNow(t + 1, Wall + t + 1)));
        Assert.Equal(("key", Wall + t + 60_000), (m.Kept.HeldRefusal, m.Kept.HeldUntilWall!.Value));
        Assert.True(m.KeepsConnection(new LinkNow(t + 59_999, Wall + t + 59_999)));        // the public frames go on
        Assert.False(m.KeepsConnection(new LinkNow(t + 60_000, Wall + t + 60_000)));       // the key is proved again on a fresh socket
        Assert.Equal(0, m.DialIn(new LinkNow(t + 60_000, Wall + t + 60_000)));

        // A later word takes the key: the hold ends and the socket carries the key again.
        var taken = new PeerLinkMachine(Settings(local: true), null, new LinkNow(0, Wall), () => 0.5);
        t = 0;
        Up(taken, ref t);
        taken.KeyRefused(new LinkNow(t, Wall + t), "key", 60_000);
        taken.KeyTaken(new LinkNow(t + 1_000, Wall + t + 1_000));
        Assert.Null(taken.HeldUntilWall(new LinkNow(t + 1_001, Wall + t + 1_001)));
        Assert.True(taken.KeepsConnection(new LinkNow(t + 60_000, Wall + t + 60_000)));

        // A rate limit holds the dial, never the socket; a refusal that let no keyless link read dials nothing inside it.
        var busy = new PeerLinkMachine(Settings(local: true), null, new LinkNow(0, Wall), () => 0.5);
        t = 0;
        Up(busy, ref t);
        busy.KeyRefused(new LinkNow(t, Wall + t), "busy", 5_000);
        Assert.True(busy.KeepsConnection(new LinkNow(t + 30_000, Wall + t + 30_000)));
        var shut = new PeerLinkMachine(Settings(local: true), null, new LinkNow(0, Wall), () => 0.5);
        t = 0;
        Up(shut, ref t);
        shut.KeyRefused(new LinkNow(t, Wall + t), "key", 60_000, publicView: false);
        shut.Closed(new LinkNow(t, Wall + t));
        Assert.Null(shut.PublicDialDue(new LinkNow(t + 1_000, Wall + t + 1_000)));
        Assert.True(shut.DialIn(new LinkNow(t + 1_000, Wall + t + 1_000)) >= 59_000);
    }

    [Fact]
    public void TheMachinesHoldIsNeverShortenedATakeLeavesItsRateLimitAndAViewCodesHoldOutlastsARestart()
    {
        static LinkNow At(long ms) => new(ms, Wall + ms);
        var m = new PeerLinkMachine(Settings(local: true), null, At(0), () => 0.5);
        long t = 0;
        Up(m, ref t);
        m.Drain();
        m.KeyRefused(At(t), "key", 60_000);
        m.KeyRefused(At(t + 1_000), "busy", 5_000);                                       // a rate limit inside the refusal's hold
        m.KeyRefused(At(t + 2_000), "key", 10_000);                                       // a shorter refusal
        Assert.Equal(("key", Wall + t + 60_000), (m.Kept.HeldRefusal, m.Kept.HeldUntilWall!.Value));

        // A rate limit ending after the refusal: the refusal's word while both stand; a word that takes the key lifts only the refusal.
        var busy = new PeerLinkMachine(Settings(local: true), null, At(0), () => 0.5);
        t = 0;
        Up(busy, ref t);
        busy.KeyRefused(At(t), "busy", 90_000);
        busy.KeyRefused(At(t + 1), "key", 60_000);
        Assert.Equal(("key", Wall + t + 90_000), (busy.Kept.HeldRefusal, busy.Kept.HeldUntilWall!.Value));
        busy.KeyTaken(At(t + 2));
        Assert.Equal(("busy", Wall + t + 90_000), (busy.Kept.HeldRefusal, busy.Kept.HeldUntilWall!.Value));
        Assert.True(busy.KeepsConnection(At(t + 30_000)));                                 // the socket carries the key again; the rate limit holds the dial

        // A viewing code's refusal lets nothing be dialled inside it, and a machine made from what was kept says the same.
        var shut = new PeerLinkMachine(Settings(local: true), null, At(0), () => 0.5);
        t = 0;
        Up(shut, ref t);
        shut.KeyRefused(At(t), "key", 60_000, publicView: false);
        shut.Closed(At(t));
        Assert.False(shut.Kept.HeldPublic);
        var again = new PeerLinkMachine(Settings(local: true), shut.Kept, At(t + 1_000), () => 0.5);
        Assert.Null(again.PublicDialDue(At(t + 1_000)));
        Assert.Null(again.Dial(At(t + 1_000)));
        Assert.True(again.DialIn(At(t + 1_000)) >= 58_000);
    }

    /// <summary>
    /// One link under another name (a hello that names its address one moment and not the next): the machine made for the new name
    /// goes on with the redial count, the wait it gave and the key's hold, the hold's word and its no-public-view with it — so the
    /// key is not presented, and the backoff is not reset, by a flip of the name.
    /// </summary>
    [Fact]
    public void AMachineMadeForAnotherNameOfTheSameLinkGoesOnWithItsRedialCountAndItsHold()
    {
        static LinkNow At(long ms) => new(ms, Wall + ms);
        var before = new PeerLinkMachine(Settings(local: true), null, At(0), () => 0.5);
        long t = 0;
        Fail(before, ref t);
        Fail(before, ref t);
        Up(before, ref t);
        before.Drain();
        before.KeyRefused(At(t), "binding", 60_000, publicView: false);
        before.Closed(At(t + 10));
        Assert.Equal(3, before.Attempt);

        var after = new PeerLinkMachine(Settings(local: true) with { NodeId = null, Address = "localhost:9924" }, null, At(t + 20), () => 0.5);
        Assert.Equal(0, after.Attempt);
        after.Continue(At(t + 20), before);
        Assert.Equal(3, after.Attempt);
        Assert.Equal(("binding", Wall + t + 60_000, false), (after.Kept.HeldRefusal, after.Kept.HeldUntilWall!.Value, after.Kept.HeldPublic));
        Assert.Null(after.Dial(At(t + 20)));                                              // no keyed dial inside the hold
        Assert.Null(after.PublicDialDue(At(t + 20)));                                     // and, after a viewing code's refusal, no public one
        Assert.Equal(before.Record(At(t + 20)).NextMs, after.Record(At(t + 20)).NextMs);
    }

    [Fact]
    public void OnlyAPongThatAnswersOneOfTheLastPingsIsASampleAndAnotherInstEmptiesTheClock()
    {
        var m = new PeerLinkMachine(Settings(local: true), null, new LinkNow(0, Wall), () => 0.5);
        long t = 0;
        Up(m, ref t);
        m.PeerInst(new LinkNow(t, Wall + t), "aaaa1111");
        m.Ping(new LinkNow(1_000, Wall + 1_000), Wall + 1_000);
        m.Pong(new LinkNow(1_020, Wall + 1_020), Wall + 999, Wall + 1_010, Wall + 1_010, stuck: false);   // a t1 never sent: forged
        Assert.Equal(0, m.Clock.Samples);
        Assert.Equal(1, m.Record(new LinkNow(1_020, Wall + 1_020)).Counts.Unmatched);
        m.Pong(new LinkNow(1_030, Wall + 1_030), Wall + 1_000, Wall + 1_015, Wall + 1_015, stuck: false);
        Assert.Equal(1, m.Clock.Samples);
        m.Pong(new LinkNow(1_040, Wall + 1_040), Wall + 1_000, Wall + 1_020, Wall + 1_020, stuck: false);  // answered once already
        Assert.Equal(1, m.Clock.Samples);
        Assert.Equal(2, m.Record(new LinkNow(1_040, Wall + 1_040)).Counts.Unmatched);

        m.PeerInst(new LinkNow(1_050, Wall + 1_050), "aaaa1111");
        Assert.Equal(1, m.Clock.Samples);
        m.PeerInst(new LinkNow(1_060, Wall + 1_060), "bbbb2222");                         // another process answers now
        Assert.Equal(0, m.Clock.Samples);

        // 13.13.2: frames counting from below the last connection's are another run of it — the clock emptied the same way.
        m.Ping(new LinkNow(2_000, Wall + 2_000), Wall + 2_000);
        m.Pong(new LinkNow(2_010, Wall + 2_010), Wall + 2_000, Wall + 2_005, Wall + 2_005, stuck: false);
        Assert.Equal(1, m.Clock.Samples);
        m.PeerRestarted(new LinkNow(2_020, Wall + 2_020));
        Assert.Equal(0, m.Clock.Samples);
    }

    // ---- the pin a person's Trust writes -------------------------------------------------------------------------------

    [Fact]
    public void APinWithNoWordReadsFirstAndOnlyAPinSomeoneMadeMovesTheLink()
    {
        var fp = "sha256:" + new string('a', 64);
        Assert.Equal(PinTrust.First, new CertificatePin("node:a", fp, DateTime.UnixEpoch).Trust);       // kept before Trust was a word
        Assert.Equal(PinTrust.Person, new CertificatePin("node:a", fp, DateTime.UnixEpoch, "person").Trust);
        Assert.Equal(PinTrust.Proof, new CertificatePin("node:a", fp, DateTime.UnixEpoch, "proof").Trust);
        Assert.Equal(PinTrust.First, new CertificatePin("node:a", fp, DateTime.UnixEpoch, "anything").Trust);

        var config = new StagewatchLinkConfig();
        config.SetPin("node:a", fp);
        Assert.Equal("first", Assert.Single(config.Pins).Made);
        Assert.Equal("", config.PinsKey);                                               // a first pin starts no dial again
        config.SetPin("node:a", fp, PinTrust.Person);
        Assert.Equal("person", Assert.Single(config.Pins).Made);
        Assert.Equal($"node:a={fp}=person", config.PinsKey);

        // The desk's first pin that lands after a person's Trust never takes its place; on another key it is kept.
        Assert.False(config.PinFirst("node:a", fp));
        Assert.Equal("person", Assert.Single(config.Pins).Made);
        Assert.True(config.PinFirst("node:b", fp));
        Assert.Equal("first", config.Pins.Single(p => p.Key == "node:b").Made);
    }

    /// <summary>
    /// G34, 13.16.2: a key held back from this Stagewatch by its binding is a key set and held back — the bearer goes nowhere, on a local
    /// link too, the flag is <c>keyless</c>, and the words say where the key is bound; the record names the kid. Where nothing else is
    /// amber (a link that offered LINK), it is the held-back key that makes the light amber: with no key set the same link is green,
    /// keyless only as information.
    /// </summary>
    [Fact]
    public void AKeyHeldBackByItsBindingIsKeylessAmberAndSaysWhereItIsBound()
    {
        const string why = "this desk's key is bound to Stagewatch a1b2c3d4 and is not sent here; BIND KEY on the Nodes page sends it here";
        var held = new PeerLinkMachine(Settings(local: true) with { KeyHeldBack = why }, null, 0, () => 0.5);
        long t = 0;
        Up(held, ref t);
        Assert.DoesNotContain(held.Drain(), w => w.Credential is not null);                 // a local link, and still no bearer
        var record = held.Record(t);
        Assert.Contains("keyless", record.Flags);
        Assert.Equal(("amber", Kid), (record.Light, record.Kid));
        Assert.Equal("Hall A · public view — " + why, record.Words);

        var offered = new PeerLinkMachine(Settings(local: true) with { KeyHeldBack = why }, null, 0, () => 0.5);
        t = 0;
        Offered(offered, ref t);
        Assert.DoesNotContain(offered.Drain(), w => w.Credential is not null);              // no proof of a key held back either
        var amber = offered.Record(t);
        Assert.Equal(("live", "amber", "△"), (amber.State, amber.Light, amber.Mark));
        Assert.Contains("keyless", amber.Flags);
        Assert.DoesNotContain("legacy", amber.Flags);

        var none = new PeerLinkMachine(Settings(local: true) with { Key = null }, null, 0, () => 0.5);
        t = 0;
        Offered(none, ref t);
        var plain = none.Record(t);
        Assert.Contains("keyless", plain.Flags);
        Assert.Equal(("live", "green", "Hall A · public view"), (plain.State, plain.Light, plain.Words));
    }

    /// <summary>
    /// 13.1, 13.16.1–2: the show's need moves only the light — a link down is red while the show needs it and amber once it does not —
    /// and the redial count, the wait and the state stay as they were.
    /// </summary>
    [Fact]
    public void TheShowsNeedMovesTheLightAndNothingElse()
    {
        var m = new PeerLinkMachine(Settings(local: true) with { ShowNeeds = true }, null, 0, () => 0.5);
        long t = 0;
        Fail(m, ref t);
        Fail(m, ref t);
        var before = m.Record(t);
        Assert.Equal(("down", "red", 2), (before.State, before.Light, before.Attempt));
        m.ShowNeedsLink(false);
        var after = m.Record(t);
        Assert.Equal(("down", "amber", 2, before.NextMs), (after.State, after.Light, after.Attempt, after.NextMs));
        m.ShowNeedsLink(true);
        Assert.Equal("red", m.Record(t).Light);
    }

    /// <summary>13.15.2–3: <i>Link again now</i> ends the redial's wait — the next dial is due at once — and the count goes on.</summary>
    [Fact]
    public void LinkAgainNowEndsTheWaitAndKeepsTheRedialCount()
    {
        var m = new PeerLinkMachine(Settings(local: true), null, new LinkNow(0, Wall), () => 0.5);
        long t = 0;
        Fail(m, ref t);
        Fail(m, ref t);
        Fail(m, ref t);
        Assert.True(m.DialIn(new LinkNow(t, Wall + t)) > 0);
        m.LinkAgainNow(new LinkNow(t, Wall + t));
        Assert.Equal(0, m.DialIn(new LinkNow(t, Wall + t)));
        Assert.Equal(3, m.Attempt);
        Assert.NotNull(m.Dial(new LinkNow(t, Wall + t)));
        m.Closed(new LinkNow(t + 1, Wall + t + 1));
        Assert.Equal(4, m.Attempt);                                                           // a dial that fails after it is the fourth
    }

    [Fact]
    public void AKeyHeldBackForTrustIsAmberAndSaysWhatToCompare()
    {
        var key = new StagewatchKey { TrustFor = "8e8b c45b b14f 9c74" };
        Assert.True(key.Withheld);
        Assert.True(key.Amber);
        Assert.False(key.Fault);
        Assert.Equal("it cannot prove itself yet (an older Stagewatch) — compare its fingerprint 8e8b c45b b14f 9c74 and press Trust to send the key", key.Line);
        Assert.False(StagewatchKey.Unknown.Amber);
    }

    // ---- helpers -------------------------------------------------------------------------------------------------------

    private static LinkSettings Settings(bool local)
        => new() { Carrier = LinkCarrier.Ws, Key = Kid, Name = "Hall A", Kind = "main", NodeId = "swdesk01", Address = local ? "127.0.0.1" : "10.0.0.5", Local = local, Inst = "11aa22bb", IdPrefix = "s" };

    /// <summary>A dial that fails when its wait is over.</summary>
    private static void Fail(PeerLinkMachine m, ref long t)
    {
        t += m.DialIn(t) ?? throw new InvalidOperationException("no dial is due");
        Assert.NotNull(m.Dial(t));
        m.Closed(t + 1);
        t++;
    }

    /// <summary>A dial that comes up when its wait is over: plain ws, its first message the acceptor's LINK offer, then a frame.</summary>
    private static void Offered(PeerLinkMachine m, ref long t)
    {
        t += m.DialIn(t) ?? throw new InvalidOperationException("no dial is due");
        Assert.NotNull(m.Dial(t));
        m.NoTls(t);
        m.LinkOffered(t, new LinkOffer(PeerLink.Version, "main", "swdesk01", new string('0', 32), "plain"));
        m.Frame(t);
    }

    /// <summary>A dial that comes up when its wait is over: plain ws, its first message a frame — level 0.</summary>
    private static void Up(PeerLinkMachine m, ref long t)
    {
        t += m.DialIn(t) ?? throw new InvalidOperationException("no dial is due");
        Assert.NotNull(m.Dial(t));
        m.NoTls(t);
        m.FirstMessage(t, frame: true);
    }
}
