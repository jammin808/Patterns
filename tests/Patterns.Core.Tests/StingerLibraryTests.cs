using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>One stinger resolver for the desk, a cue and the remote, and the references a delete must respect.</summary>
public class StingerLibraryTests
{
    [Fact]
    public void OneResolverForTheDeskTheCueAndTheRemote()
    {
        var state = new ShowState();
        var a = new StingerItemConfig { Id = "sting-a", Name = "Take your seats", Path = "C:/show/seats.wav" };
        var b = new StingerItemConfig { Id = "sting-b", Path = "C:/show/Winner Sting.mp4" }; // named by its file
        state.Stingers.Items.Add(a);
        state.Stingers.Items.Add(b);

        Assert.Same(a, StingerLibrary.Find(state, "1"));
        Assert.Same(b, StingerLibrary.Find(state, " 2 "));
        Assert.Null(StingerLibrary.Find(state, "3"));
        Assert.Null(StingerLibrary.Find(state, "0"));
        Assert.Same(a, StingerLibrary.Find(state, "sting-a"));
        Assert.Same(a, StingerLibrary.Find(state, "take YOUR seats"));
        Assert.Same(b, StingerLibrary.Find(state, b.DisplayName.ToUpperInvariant()));
        Assert.Null(StingerLibrary.Find(state, ""));
        Assert.Null(StingerLibrary.Find(state, null));
        Assert.Same(StingerLibrary.Find(state, "sting-a"), CueSummary.FindStinger(state, "sting-a"));
    }

    [Fact]
    public void ReferencesNameTheCuesThatFireAStinger()
    {
        var state = new ShowState();
        var a = new StingerItemConfig { Id = "sting-a", Name = "Take your seats", Path = "C:/show/seats.wav" };
        state.Stingers.Items.Add(a);
        var stack = CueStacks.Caller(state);
        stack.Cues.Add(new RunCueConfig { Number = "03.020", Name = "Five-minute call", Actions = { new CueActionConfig { Kind = CueActionKind.StingerFire, Target = "sting-a" } } });
        stack.Cues.Add(new RunCueConfig { Number = "03.030", Name = "By name", Actions = { new CueActionConfig { Kind = CueActionKind.StingerFire, Target = "take your seats" } } });
        stack.Cues.Add(new RunCueConfig { Number = "03.040", Name = "Something else", Actions = { new CueActionConfig { Kind = CueActionKind.ApplyLook, Target = "x" } } });

        var refs = StingerLibrary.References(state, a);
        Assert.Equal(2, refs.Count);
        Assert.Contains(refs, r => r.Contains("03.020 Five-minute call"));
        Assert.Contains(refs, r => r.Contains("03.030 By name"));
        Assert.Empty(StingerLibrary.References(state, new StingerItemConfig { Id = "other", Path = "C:/x.wav" }));
    }
}
