using System.Text.Json;
using Patterns.Core.Services;

namespace Patterns.Core.Play;

public sealed record PathOption(string Text, string Next);

public sealed record PathScene(string Id, string Text, IReadOnlyList<PathOption> Options);

/// <summary>
/// The path: an adventure told on the wall in scenes, the room voting each fork on their phones.
/// The branches are written for the event (a JSON file in the node's folder, the assistant's
/// proposals edited by the host); the sample is here so the first night has a story.
/// </summary>
public sealed class PathStory
{
    private readonly Dictionary<string, int> _votes = new();
    private readonly List<string> _history = new();

    public PathStory(string title, string start, IReadOnlyList<PathScene> scenes)
    {
        Title = title;
        Start = start;
        Scenes = scenes;
        CurrentId = start;
    }

    public string Title { get; }
    public string Start { get; }
    public IReadOnlyList<PathScene> Scenes { get; }
    public string CurrentId { get; private set; }
    public bool VotingOpen { get; private set; }
    public string LastChoice { get; private set; } = "";
    public IReadOnlyList<string> History => _history;
    public PathScene? Current => Scenes.FirstOrDefault(s => s.Id == CurrentId);
    public bool IsEnd => Current is null || Current.Options.Count == 0;
    public int VoteCount => _votes.Count;

    public bool Open()
    {
        if (IsEnd) return false;
        _votes.Clear();
        VotingOpen = true;
        return true;
    }

    public string Vote(string? token, int option)
    {
        if (string.IsNullOrEmpty(token)) return "who?";
        if (!VotingOpen || Current is null) return "no vote is open";
        if (option < 0 || option >= Current.Options.Count) return "not an option";
        _votes[token] = option;
        return "ok";
    }

    public int[] VoteCounts()
    {
        var counts = new int[Current?.Options.Count ?? 0];
        foreach (var v in _votes.Values) if (v >= 0 && v < counts.Length) counts[v]++;
        return counts;
    }

    /// <summary>The vote closes: the option with the most votes (the first on a tie) takes the story on.</summary>
    public PathOption? Close()
    {
        if (!VotingOpen || Current is null) return null;
        VotingOpen = false;
        var counts = VoteCounts();
        var best = 0;
        for (var i = 1; i < counts.Length; i++) if (counts[i] > counts[best]) best = i;
        var chosen = Current.Options[best];
        LastChoice = chosen.Text;
        _history.Add(CurrentId);
        CurrentId = chosen.Next;
        _votes.Clear();
        return chosen;
    }

    public void Reset()
    {
        CurrentId = Start;
        VotingOpen = false;
        LastChoice = "";
        _votes.Clear();
        _history.Clear();
    }

    public string Json() => JsonSerializer.Serialize(new { Title, Start, Scenes }, JsonUtil.Options);

    private sealed class OptionDto
    {
        public string? Text { get; set; }
        public string? Next { get; set; }
    }

    private sealed class SceneDto
    {
        public string? Id { get; set; }
        public string? Text { get; set; }
        public List<OptionDto>? Options { get; set; }
    }

    private sealed class StoryDto
    {
        public string? Title { get; set; }
        public string? Start { get; set; }
        public List<SceneDto>? Scenes { get; set; }
    }

    /// <summary>A story from its file; null for one this build cannot read, or one with no scenes.</summary>
    public static PathStory? Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json)) return null;
        try
        {
            var dto = JsonSerializer.Deserialize<StoryDto>(json, JsonUtil.Options);
            if (dto?.Scenes is not { Count: > 0 }) return null;
            var scenes = dto.Scenes
                .Where(sc => !string.IsNullOrEmpty(sc.Id))
                .Select(sc => new PathScene(sc.Id!, sc.Text ?? "", (sc.Options ?? new List<OptionDto>()).Select(o => new PathOption(o.Text ?? "", o.Next ?? "")).ToList()))
                .ToList();
            if (scenes.Count == 0) return null;
            var start = string.IsNullOrEmpty(dto.Start) ? scenes[0].Id : dto.Start;
            if (scenes.All(sc => sc.Id != start)) return null;
            return new PathStory(dto.Title ?? "The path", start, scenes);
        }
        catch (JsonException)
        {
            return null;
        }
    }

    /// <summary>The first night's story: a venue after hours, the room choosing the way.</summary>
    public static PathStory Sample() => new("After hours", "doors", new[]
    {
        new PathScene("doors", "The last session is over and the house lights are still low. A door at the back of the hall is open that was locked all day. Where does the room go?", new[]
        {
            new PathOption("Through the door", "corridor"),
            new PathOption("Up to the gallery", "gallery"),
        }),
        new PathScene("corridor", "A service corridor, humming. Two ways: a stair going down to the plant room, or a lit window at the far end.", new[]
        {
            new PathOption("Down the stair", "plant"),
            new PathOption("To the window", "window"),
        }),
        new PathScene("gallery", "The gallery looks over the whole hall. On the rail, a torch and a folded note. Read the note, or take the torch and go on?", new[]
        {
            new PathOption("Read the note", "note"),
            new PathOption("Take the torch", "plant"),
        }),
        new PathScene("plant", "The plant room: a wall of breakers, one of them off. A hand on that switch and something in the hall wakes. Throw it?", new[]
        {
            new PathOption("Throw the switch", "finale"),
            new PathOption("Leave it", "window"),
        }),
        new PathScene("window", "The window opens onto the loading dock, a van idling, its doors open on cases of gear — and one case that is not gear at all.", new[]
        {
            new PathOption("Open the case", "finale"),
            new PathOption("Close the doors and walk away", "end-quiet"),
        }),
        new PathScene("note", "The note says: 'Whoever finds this — the show is not over. The last cue is yours.' Below it, a single word: GO.", new[]
        {
            new PathOption("GO", "finale"),
            new PathOption("Put it back", "end-quiet"),
        }),
        new PathScene("finale", "The wall lights up, every screen at once, and the room's own names roll across it. The last cue was the room's. That is the show.", Array.Empty<PathOption>()),
        new PathScene("end-quiet", "The hall goes dark, the way it does every night, and the room goes home with a story that did not quite happen. Next time.", Array.Empty<PathOption>()),
    });
}
