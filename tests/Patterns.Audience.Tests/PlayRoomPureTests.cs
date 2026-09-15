using System.Text.Json;
using Patterns.Arcade;
using Patterns.Audience;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Audience.Tests;

/// <summary>The process the room runs in, as the room sees it: a show, a store, a notice, a moderation — and nothing else built.</summary>
public sealed class FakeAudienceHost : IAudienceHost, IDisposable
{
    public FakeAudienceHost()
    {
        Dir = Path.Combine(Path.GetTempPath(), "patterns-audience-" + Guid.NewGuid().ToString("N")[..8]);
        Directory.CreateDirectory(Dir);
        Store = new SettingsStore(Dir);
        State = new ShowState { Name = "Gala" };
    }
    public string Dir { get; }
    public ShowState State { get; }
    public SettingsStore Store { get; }
    public List<string> Notices { get; } = new();
    public string? ModerationReply { get; set; }
    public List<string> Moderated { get; } = new();
    public void Notify(string message) => Notices.Add(message);
    public Task<string?> ModerateAsync(string text) { Moderated.Add(text); return Task.FromResult(ModerationReply); }
    public void Dispose() { try { Directory.Delete(Dir, true); } catch { } }
}

/// <summary>The desk as the room asks of it: an edit scope, the wall's start, the door's addresses.</summary>
public sealed class FakePlayHost : IPlayHost
{
    public int WallStarts { get; private set; }
    public void BulkEdit(Action edit) => edit();
    public void StartWall() => WallStarts++;
    public IReadOnlyList<string> AudienceUrls() => new[] { "http://10.0.0.5:8098/" };
    public bool AudienceListening => true;
    public int AudienceConnections => 0;
}

/// <summary>
/// The audience room runs on the core's contracts alone — a fake process, a fake desk, no port,
/// no UI, no headless platform: the phones' door, a quiz opened from the show's vocabulary,
/// answered, scored, revealed, a message to one phone, the feed — the trust boundary as an
/// assembly, tested where it is fastest.
/// </summary>
public class PlayRoomPureTests
{
    private static ShowAction Verb(string line) => ControlProtocol.Parse(line).Action;

    [Fact]
    public void ARoomRunsForThePhonesAndTheHostWithNoDeskAtAll()
    {
        using var host = new FakeAudienceHost();
        var desk = new FakePlayHost();
        var play = new PlayService(host, desk);
        try
        {
            Assert.Matches("^[A-Z]{4}$", play.Code);
            Assert.Contains($"/play?room={play.Code}", play.JoinUrl);
            Assert.True(File.Exists(Path.Combine(host.Dir, "play-story.json")));          // the sample story, there to be edited

            // Two phones at the door — one with the wrong code.
            Assert.Contains("not this room", play.JoinJson("{\"nick\":\"Sam\",\"room\":\"ZZZZ\"}", "10.0.0.7"));
            var sam = JsonDocument.Parse(play.JoinJson($"{{\"nick\":\"Sam\",\"group\":\"Table 4\",\"room\":\"{play.Code}\"}}", "10.0.0.7")).RootElement;
            Assert.True(sam.GetProperty("ok").GetBoolean());
            var samToken = sam.GetProperty("token").GetString()!;
            var kimToken = JsonDocument.Parse(play.JoinJson("{\"nick\":\"Kim\"}", "10.0.0.8")).RootElement.GetProperty("token").GetString()!;
            Assert.Equal(2, play.Room.PlayerCount);
            Assert.Contains("2 joined", play.Words);

            // A quiz from the show's vocabulary: opened, answered, scored, closed, revealed; the wall asked for.
            Assert.True(play.Run(Verb("PLAY ADD quiz 2 + 2? | 3 | 4 | 5 | correct=2 time=20")).Ok);
            Assert.False(play.Run(Verb("PLAY ADD quiz no answer | A | B")).Ok);
            Assert.True(play.Run(Verb("PLAY OPEN")).Ok);
            Assert.Equal(PlayBoardMode.Results, play.Wall);
            Assert.Equal(1, desk.WallStarts);
            var q = play.Room.Current!;
            Assert.Contains("\"ok\":true", play.AnswerJson($"{{\"token\":\"{samToken}\",\"question\":\"{q.Id}\",\"choices\":[1]}}"));
            Assert.Contains("\"ok\":true", play.AnswerJson($"{{\"token\":\"{kimToken}\",\"question\":\"{q.Id}\",\"choices\":[0]}}"));
            Assert.Contains("already answered", play.AnswerJson($"{{\"token\":\"{samToken}\",\"question\":\"{q.Id}\",\"choices\":[1]}}"));
            var state = JsonDocument.Parse(play.StateJson(samToken, 0)).RootElement;
            Assert.Equal("Sam", state.GetProperty("nick").GetString());
            Assert.True(state.GetProperty("question").GetProperty("mine").GetProperty("correct").GetBoolean());
            Assert.True(state.GetProperty("score").GetInt32() >= 500);
            Assert.True(play.Run(Verb("PLAY CLOSE")).Ok);
            Assert.False(play.Run(Verb("PLAY CLOSE")).Ok);
            Assert.True(play.Run(Verb("PLAY REVEAL")).Ok);
            state = JsonDocument.Parse(play.StateJson(kimToken, 0)).RootElement;
            Assert.Equal(1, state.GetProperty("question").GetProperty("correct").GetInt32());
            Assert.Equal("revealed", state.GetProperty("question").GetProperty("state").GetString());
            Assert.Equal("Sam", state.GetProperty("leaderboard")[0].GetProperty("nick").GetString());

            // A message to one phone by name reaches it and no other; the feed carries the round.
            Assert.True(play.Run(Verb("PLAY MESSAGE phone:Sam your answer was right")).Ok);
            Assert.False(play.Run(Verb("PLAY MESSAGE phone:Nobody hello")).Ok);
            Assert.Contains("your answer was right", play.StateJson(samToken, 0));
            Assert.DoesNotContain("your answer was right", play.StateJson(kimToken, 0));
            var feed = play.FeedCsv();
            Assert.Contains("2 + 2?", feed);
            Assert.Contains("1. Sam", feed);
        }
        finally
        {
            play.Dispose();
        }
    }

    [Fact]
    public void TheQueueAsksTheProcessToModerateAndKeepsTheItemWhenNoAssistantAnswered()
    {
        using var host = new FakeAudienceHost();
        var play = new PlayService(host, new FakePlayHost());
        try
        {
            var token = JsonDocument.Parse(play.JoinJson($"{{\"nick\":\"Sam\",\"room\":\"{play.Code}\"}}", "10.0.0.7")).RootElement.GetProperty("token").GetString()!;
            Assert.True(play.Run(Verb("PLAY ADD words One word for today")).Ok);
            Assert.True(play.Run(Verb("PLAY NEXT")).Ok);
            Assert.Contains("queued", play.AnswerJson($"{{\"token\":\"{token}\",\"words\":\"Bright\"}}"));
            play.Tick();                                                                   // the queue's waiting item goes to the process's moderation
            Assert.Contains(host.Moderated, m => m.Contains("Bright"));
            Assert.Single(play.Room.Waiting());                                            // no answer: the host still decides
        }
        finally
        {
            play.Dispose();
        }
    }
}
