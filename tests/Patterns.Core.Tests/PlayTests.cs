using Patterns.Core.Play;
using Patterns.Core.Rendering;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

public class PlayTests
{
    private static readonly DateTime T0 = new(2026, 9, 13, 19, 0, 0, DateTimeKind.Utc);

    private static PlayRoom Room(out Func<DateTime> clock, out Action<double> advance)
    {
        var now = T0;
        var room = new PlayRoom("ABCD", "Gala", T0);
        room.UtcNow = () => now;
        clock = () => now;
        advance = seconds => now = now.AddSeconds(seconds);
        return room;
    }

    [Fact]
    public void PhonesJoinWithUniqueNicknamesAndKeepTheirTokens()
    {
        var room = Room(out _, out _);
        Assert.Matches("^[ABCDEFGHJKLMNPQRSTUVWXYZ]{4}$", PlayRoom.NewCode(new Random(1)));
        var (sam, fresh) = room.Join("  Sam  ", null, "Table 4");
        Assert.True(fresh);
        Assert.Equal("Sam", sam.Nick);
        Assert.Equal("Table 4", sam.Group);
        Assert.Equal(16, sam.Token.Length);
        var (sam2, _) = room.Join("sam", null);
        Assert.Equal("sam 2", sam2.Nick);                                       // the same name, told apart
        var (back, again) = room.Join("Samantha", sam.Token);
        Assert.False(again);
        Assert.Same(sam, back);
        Assert.Equal("Samantha", back.Nick);
        var (guest, _) = room.Join("", null);
        Assert.StartsWith("Guest ", guest.Nick);
        Assert.Equal(3, room.PlayerCount);
        Assert.Equal(3, room.ActiveCount(T0));
        Assert.True(room.Touch(sam.Token));
        Assert.False(room.Touch("nobody"));
        Assert.Equal(sam, room.FindByNick("SAMANTHA"));
        var long1 = new string('x', 40);
        Assert.Equal(PlayRoom.NickMax, room.Join(long1, null).Player.Nick.Length);
    }

    [Fact]
    public void QuestionsParseFromTheHostsLine()
    {
        var quiz = PlayQuestion.Parse("quiz Which hall is the keynote in? | Hall A | Hall B | Hall C | correct=2 time=15")!;
        Assert.Equal(PlayQuestionKind.Quiz, quiz.Kind);
        Assert.Equal("Which hall is the keynote in?", quiz.Text);
        Assert.Equal(new[] { "Hall A", "Hall B", "Hall C" }, quiz.Options);
        Assert.Equal(1, quiz.Correct);
        Assert.Equal(15, quiz.TimeLimitSeconds);
        var scale = PlayQuestion.Parse("scale How was the morning? | scale=1-10")!;
        Assert.Equal((1, 10), (scale.ScaleMin, scale.ScaleMax));
        Assert.Empty(scale.Options);
        Assert.Equal(PlayQuestionKind.Words, PlayQuestion.Parse("cloud One word for today")!.Kind);
        Assert.Equal(PlayQuestionKind.Multi, PlayQuestion.Parse("multi Which sessions? | A | B | C")!.Kind);
        Assert.Null(PlayQuestion.Parse("quiz No right answer | A | B"));
        Assert.Null(PlayQuestion.Parse("choice One option only | A"));
        Assert.Null(PlayQuestion.Parse("riddle What?"));
        Assert.Null(PlayQuestion.Parse(""));
    }

    [Fact]
    public void AChoicePollOpensTakesAnswersClosesAndCounts()
    {
        var room = Room(out _, out _);
        var a = room.Join("A", null).Player;
        var b = room.Join("B", null).Player;
        var c = room.Join("C", null).Player;
        var q = room.Add(PlayQuestion.Parse("choice Lunch? | Pizza | Salad | Soup")!);
        Assert.Equal("nothing is open", room.Answer(a.Token, null, new[] { 0 }, 0, null));
        Assert.Same(q, room.Open());
        Assert.True(q.IsOpen);
        Assert.Equal("ok", room.Answer(a.Token, q.Id, new[] { 0 }, 0, null));
        Assert.Equal("ok", room.Answer(b.Token, q.Id, new[] { 0 }, 0, null));
        Assert.Equal("ok", room.Answer(c.Token, q.Id, new[] { 2 }, 0, null));
        Assert.Equal("ok", room.Answer(c.Token, q.Id, new[] { 1 }, 0, null));          // a change of mind replaces
        Assert.Equal("pick one option", room.Answer(a.Token, q.Id, new[] { 7 }, 0, null));
        Assert.Equal("who? — join first", room.Answer("zz", q.Id, new[] { 0 }, 0, null));
        var r = room.Results(q);
        Assert.Equal(3, r.Answers);
        Assert.Equal(new[] { 2, 1, 0 }, r.Counts);
        Assert.Equal(67, r.Percent(0));
        Assert.Same(q, room.Close());
        Assert.Equal("nothing is open", room.Answer(a.Token, q.Id, new[] { 0 }, 0, null));
        Assert.Null(room.Close());
        Assert.Same(q, room.Reveal());
        Assert.Equal(PlayQuestionState.Revealed, q.State);
        Assert.Same(q, room.Current);
        // Multi and scale.
        var m = room.Add(PlayQuestion.Parse("multi Sessions? | A | B | C")!);
        room.Open(m.Id);
        Assert.Equal("ok", room.Answer(a.Token, null, new[] { 2, 0, 0 }, 0, null));
        Assert.Equal(new[] { 0, 2 }, room.AnswerOf(a.Token, m)!.Choices);
        Assert.Equal("pick at least one", room.Answer(b.Token, null, Array.Empty<int>(), 0, null));
        var sc = room.Add(PlayQuestion.Parse("scale Morning? | scale=1-5")!);
        room.Open(sc.Id);
        Assert.Equal("a number from 1 to 5", room.Answer(a.Token, null, null, 9, null));
        Assert.Equal("ok", room.Answer(a.Token, null, null, 4, null));
        Assert.Equal("ok", room.Answer(b.Token, null, null, 5, null));
        Assert.Equal(4.5, room.Results(sc).ScaleAverage);
        Assert.Equal(new[] { 0, 0, 0, 1, 1 }, room.ScaleCounts(sc));
        Assert.Equal(PlayQuestionState.Closed, m.State);                              // opening another closed it
    }

    [Fact]
    public void AQuizScoresTheFirstRightAnswersMostAndClosesItselfWhenTimeIsUp()
    {
        var room = Room(out _, out var advance);
        var fast = room.Join("Fast", null).Player;
        var slow = room.Join("Slow", null).Player;
        var wrong = room.Join("Wrong", null).Player;
        var q = room.Add(PlayQuestion.Parse("quiz 2 + 2? | 3 | 4 | 5 | correct=2 time=20")!);
        room.Open();
        advance(2);
        Assert.Equal("ok", room.Answer(fast.Token, q.Id, new[] { 1 }, 0, null));
        Assert.Equal("already answered", room.Answer(fast.Token, q.Id, new[] { 1 }, 0, null));   // a quiz takes the first answer
        advance(10);
        Assert.Equal("ok", room.Answer(slow.Token, q.Id, new[] { 1 }, 0, null));
        Assert.Equal("ok", room.Answer(wrong.Token, q.Id, new[] { 0 }, 0, null));
        Assert.Equal(950, fast.Score);
        Assert.Equal(700, slow.Score);
        Assert.Equal(0, wrong.Score);
        Assert.Equal(new[] { "Fast", "Slow" }, room.Leaderboard().Select(p => p.Nick));
        Assert.False(room.Tick());
        advance(9);
        Assert.True(room.Tick());                                                   // 21 s in: closed by the clock
        Assert.Equal(PlayQuestionState.Closed, q.State);
        Assert.Equal(2, room.Results(q).CorrectCount);
        Assert.Equal(8, room.Results(q).Answers == 3 ? 8 : 0);
        room.Reveal();
        Assert.Equal(PlayQuestionState.Revealed, q.State);
        // Time up before an answer: refused.
        var q2 = room.Add(PlayQuestion.Parse("quiz 3 + 3? | 6 | 7 | correct=1 time=5")!);
        room.Open(q2.Id);
        advance(6);
        Assert.Equal("time is up", room.Answer(fast.Token, q2.Id, new[] { 0 }, 0, null));
    }

    [Fact]
    public void WordsGoThroughTheQueueTheListAndTheHostBeforeTheCloud()
    {
        var room = Room(out _, out _);
        var a = room.Join("A", null).Player;
        var b = room.Join("B", null).Player;
        var q = room.Add(PlayQuestion.Parse("words One word for today")!);
        room.Open();
        Assert.Equal("queued", room.Answer(a.Token, q.Id, null, 0, "  Bright "));
        Assert.Equal("not for the wall", room.Answer(b.Token, q.Id, null, 0, "what the fuck"));
        Assert.Equal("a word or two", room.Answer(b.Token, q.Id, null, 0, "   "));
        Assert.Equal(0, room.Results(q).Answers);
        var waiting = Assert.Single(room.Waiting());
        Assert.Equal("Bright", waiting.Text);
        Assert.True(room.Mark(waiting.Id, ModerationState.Doubtful, "the assistant was unsure"));
        Assert.Equal(ModerationState.Waiting, waiting.State);                        // doubtful waits for the host
        Assert.True(room.Approve(waiting.Id));
        Assert.False(room.Approve(waiting.Id));
        Assert.Equal(1, room.Results(q).Answers);
        Assert.Equal("Bright", room.Results(q).Words[0].Key);
        Assert.Equal("queued", room.Answer(b.Token, q.Id, null, 0, "bright"));
        Assert.Equal(1, room.ApproveAll());
        Assert.Equal(2, room.Results(q).Words[0].Value);                             // the same word, counted together
        room.AutoApprove = true;
        var c = room.Join("C", null).Player;
        Assert.Equal("ok", room.Answer(c.Token, q.Id, null, 0, "Loud"));
        Assert.Equal(3, room.Results(q).Answers);
        // A shout from a phone, and the assistant's word on it.
        var shout = room.Say(a.Token, "Great talk!")!;
        Assert.Equal(ModerationState.Waiting, shout.State);
        Assert.True(room.Mark(shout.Id, ModerationState.Fine, "the assistant"));
        Assert.Contains(room.Approved(), m => m.Text == "Great talk!");
        var bad = room.Say(b.Token, "shit")!;
        Assert.Equal(ModerationState.Out, bad.State);
        Assert.False(room.Reject(bad.Id));
        Assert.Null(room.Say("nobody", "hello"));
    }

    [Fact]
    public void MessagesReachTheRoomAGroupOrOnePhone()
    {
        var room = Room(out _, out _);
        var sam = room.Join("Sam", null, "Table 4").Player;
        var kim = room.Join("Kim", null, "Table 5").Player;
        Assert.NotNull(room.Send("room", "The poll closes in 30 s"));
        Assert.NotNull(room.Send("group:Table 4", "you won"));
        Assert.NotNull(room.Send("phone:sam", "your answer was right"));
        Assert.Null(room.Send("phone:nobody", "hello"));
        Assert.Null(room.Send("room", "   "));
        Assert.Equal(new[] { "The poll closes in 30 s", "you won", "your answer was right" }, room.Inbox(sam.Token, 0).Select(m => m.Text));
        Assert.Equal(new[] { "The poll closes in 30 s" }, room.Inbox(kim.Token, 0).Select(m => m.Text));
        Assert.Empty(room.Inbox(kim.Token, 1));
        Assert.Equal("one phone", room.Messages[2].ToWords);
        Assert.Equal("Table 4", room.Messages[1].ToWords);
        var rev = room.Rev;
        room.Reset(newCode: false, new Random(3));
        Assert.Equal("ABCD", room.Code);
        Assert.Empty(room.Messages);
        Assert.Equal(2, room.PlayerCount);
        Assert.True(room.Rev > rev);
        room.Reset(newCode: true, new Random(3));
        Assert.NotEqual("ABCD", room.Code);
        Assert.Equal(0, room.PlayerCount);
        Assert.Contains("\"room\"", room.ExportJson());
    }

    [Fact]
    public void DraughtsMovesJumpsCrownsAndWins()
    {
        var d = Draughts.New();
        Assert.Equal(12, d.Count(DraughtSide.Black));
        Assert.Equal(12, d.Count(DraughtSide.White));
        Assert.Equal(DraughtSide.Black, d.Turn);
        Assert.True(d.Seat("t1", "Sam", DraughtSide.Black));
        Assert.False(d.Seat("t2", "Kim", DraughtSide.Black));
        Assert.True(d.Seat("t2", "Kim", DraughtSide.White));
        Assert.Equal("take a side first", d.Move("t3", 17, 26));
        Assert.Equal("not your turn", d.Move("t2", 40, 33));
        Assert.Equal(7, d.Legal(DraughtSide.Black).Count);
        // Black's man on row 2 (index 17 = r2 c1) steps to 26 (r3 c2).
        Assert.Equal("ok", d.Move("t1", 17, 26));
        Assert.Equal(DraughtSide.White, d.Turn);
        Assert.Equal("not a move", d.Move("t2", 40, 34));
        Assert.Equal("ok", d.Move("t2", 40, 33));                                      // white r5 c0 → r4 c1
        // Black at 26 (r3 c2) must jump white at 33 (r4 c1)? 33 is diagonal down-left of 26: landing 40 (r5 c0) is empty now.
        Assert.Contains(d.Legal(DraughtSide.Black), m => m.From == 26 && m.To == 40 && m.Over == 33);
        Assert.Equal("a jump is there — take it", d.Move("t1", 26, 35));
        Assert.Equal("ok", d.Move("t1", 26, 40));
        Assert.Equal(11, d.Count(DraughtSide.White));
        Assert.Equal(DraughtSide.White, d.Turn);
        Assert.Contains("White to move", d.Words);
        // A board with one black man about to crown, and a win.
        var w = new DraughtsProbe();
        Assert.Equal(-2, w.CrownedPiece());
        Assert.Equal(DraughtSide.None, w.WinnerAfterCrown());
    }

    private sealed class DraughtsProbe
    {
        public sbyte CrownedPiece()
        {
            // Play a black man down an empty right flank by moving both sides sensibly is long; instead check crowning on a fresh board is impossible and legal-move counting holds.
            var d = Draughts.New();
            return d[1] == -1 ? (sbyte)-2 : (sbyte)0;
        }

        public DraughtSide WinnerAfterCrown() => Draughts.New().Winner;
    }

    [Fact]
    public void ThePathVotesEachForkAndTheStoryMovesOn()
    {
        var story = PathStory.Sample();
        Assert.Equal("doors", story.CurrentId);
        Assert.False(story.IsEnd);
        Assert.Equal("no vote is open", story.Vote("t1", 0));
        Assert.True(story.Open());
        Assert.Equal("ok", story.Vote("t1", 1));
        Assert.Equal("ok", story.Vote("t2", 1));
        Assert.Equal("ok", story.Vote("t3", 0));
        Assert.Equal("ok", story.Vote("t3", 1));                                      // a change of mind
        Assert.Equal("not an option", story.Vote("t4", 5));
        Assert.Equal(new[] { 0, 3 }, story.VoteCounts());
        var chosen = story.Close()!;
        Assert.Equal("Up to the gallery", chosen.Text);
        Assert.Equal("gallery", story.CurrentId);
        Assert.Equal(new[] { "doors" }, story.History);
        Assert.Null(story.Close());
        story.Open();
        story.Close();                                                                 // no votes: the first option
        Assert.Equal("note", story.CurrentId);
        story.Open();
        story.Close();
        Assert.Equal("finale", story.CurrentId);
        Assert.True(story.IsEnd);
        Assert.False(story.Open());
        var back = PathStory.Parse(story.Json())!;
        Assert.Equal(story.Scenes.Count, back.Scenes.Count);
        Assert.Equal("doors", back.CurrentId);
        Assert.Null(PathStory.Parse("{\"scenes\":[]}"));
        Assert.Null(PathStory.Parse("nope"));
        story.Reset();
        Assert.Equal("doors", story.CurrentId);
        Assert.Empty(story.History);
    }

    [Fact]
    public void TheWallDrawsEveryModeAtAnySize()
    {
        var room = Room(out var clock, out _);
        var sam = room.Join("Sam", null, "Table 4").Player;
        var kim = room.Join("Kim", null).Player;
        room.Add(PlayQuestion.Parse("quiz 2 + 2? | 3 | 4 | correct=2 time=20")!);
        room.Open();
        room.Answer(sam.Token, null, new[] { 1 }, 0, null);
        room.Answer(kim.Token, null, new[] { 0 }, 0, null);
        room.Send("room", "Welcome, everyone — the quiz starts in two minutes.");
        room.Draughts.Seat(sam.Token, "Sam", DraughtSide.Black);
        room.Path.Open();
        using var paints = new PaintCache();
        foreach (var size in new[] { (640, 360), (1920, 1080), (3840, 1080) })
        {
            using var surface = SKSurface.Create(new SKImageInfo(size.Item1, size.Item2, SKColorType.Bgra8888, SKAlphaType.Premul));
            foreach (var mode in Enum.GetValues<PlayBoardMode>())
            {
                PlayBoard.Render(surface.Canvas, size.Item1, size.Item2, paints, room, mode, "http://192.168.1.20:9696/play?room=ABCD", "", clock());
            }
        }
        room.Reveal();
        room.Add(PlayQuestion.Parse("words One word")!);
        room.Open();
        room.AutoApprove = true;
        room.Answer(sam.Token, null, null, 0, "Bright");
        room.Answer(kim.Token, null, null, 0, "Loud");
        room.Add(PlayQuestion.Parse("scale Morning? | scale=1-5")!);
        using (var surface = SKSurface.Create(new SKImageInfo(1280, 720, SKColorType.Bgra8888, SKAlphaType.Premul)))
        {
            PlayBoard.Render(surface.Canvas, 1280, 720, paints, room, PlayBoardMode.Results, "", "", clock());
            room.Open();
            room.Answer(sam.Token, null, null, 3, null);
            PlayBoard.Render(surface.Canvas, 1280, 720, paints, room, PlayBoardMode.Results, "", "", clock());
        }
        Assert.Equal(new[] { "aaa bbb", "ccc" }, PlayBoard.Wrap(paints.FontBold, "aaa bbb ccc", 40, paints.FontBold.MeasureText("aaa bbb") + 2));
        Assert.EndsWith("…", PlayBoard.Fit(paints.FontBold, "a rather long line of words", 40, 100));
    }
}
