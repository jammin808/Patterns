using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>The files budget: phases counted with their worst, saves coalesced, hitches on the desk's thread counted, the words.</summary>
public class FileBudgetTests
{
    [Fact]
    public void PhasesAreCountedWithTheirLastAndWorstAndTheWordsReadThem()
    {
        var b = new FileBudget();
        Assert.Equal("Files: nothing saved, loaded or imported yet.", b.Describe());
        b.Record(FileBudget.SaveSnapshot, 0.02, onDeskThread: true);
        b.Record(FileBudget.SaveSerialise, 4.1);
        b.Record(FileBudget.SaveWrite, 2.3);
        b.Record(FileBudget.SaveSnapshot, 0.01, onDeskThread: true);
        b.Record(FileBudget.SaveSerialise, 3.0);
        b.Record(FileBudget.SaveWrite, 1.0);
        b.CoalescedOne();
        b.Record(FileBudget.RecoverySerialise, 6.0);
        b.Record(FileBudget.RecoveryWrite, 1.1);
        b.Record(FileBudget.ShowLoadParse, 18.4);
        b.Record(FileBudget.CueSheetParse, 3.2);

        var serialise = b.Of(FileBudget.SaveSerialise)!;
        Assert.Equal(2, serialise.Count);
        Assert.Equal(3.0, serialise.LastMs);
        Assert.Equal(4.1, serialise.WorstMs);
        Assert.Equal(7.1, serialise.TotalMs, 6);
        Assert.Equal("autosave serialise 2, worst 4.1 ms", serialise.Words);
        Assert.Equal(1, b.Coalesced);
        Assert.Equal(0, b.SlowOnDeskThread);
        Assert.Null(b.Of(FileBudget.ShowSaveWrite));
        Assert.Equal(new[] { FileBudget.SaveSnapshot, FileBudget.SaveSerialise, FileBudget.SaveWrite, FileBudget.RecoverySerialise, FileBudget.RecoveryWrite, FileBudget.ShowLoadParse, FileBudget.CueSheetParse }, b.Phases().Select(p => p.Name));

        var words = b.Describe();
        Assert.Equal("Files: autosave 2 (1 coalesced) — snapshot 0.0 ms, serialise 4.1 ms, write 2.3 ms worst · recovery 1 — serialise 6.0 ms, write 1.1 ms worst · show load parse 18 ms · cue sheet parse 3 ms · nothing on the desk's thread past 16 ms", words);
    }

    [Fact]
    public void AHitchOnTheDesksThreadIsCountedAndNamedAndAWorkersIsNot()
    {
        var b = new FileBudget();
        b.Record(FileBudget.ShowSaveSerialise, FileBudget.SlowMs + 4, onDeskThread: true);
        b.Record(FileBudget.ShowSaveWrite, FileBudget.SlowMs + 40);                              // a worker's slow write holds nobody
        Assert.Equal(1, b.SlowOnDeskThread);
        Assert.Contains("1 on the desk's thread past 16 ms", b.Describe());
        Assert.StartsWith("Files: show save 1 — serialise 20.0 ms, write 56.0 ms worst", b.Describe());
        b.Reset();
        Assert.Equal(0, b.SlowOnDeskThread);
        Assert.Empty(b.Phases());
        Assert.Equal(0, b.Coalesced);
    }

    [Fact]
    public void RecordingIsSafeFromManyThreadsAtOnce()
    {
        var b = new FileBudget();
        Parallel.For(0, 2000, i => b.Record(i % 2 == 0 ? FileBudget.SaveWrite : FileBudget.RecoveryWrite, i % 7));
        Assert.Equal(1000, b.Of(FileBudget.SaveWrite)!.Count);
        Assert.Equal(1000, b.Of(FileBudget.RecoveryWrite)!.Count);
        Assert.Equal(6, b.Of(FileBudget.SaveWrite)!.WorstMs);
    }
}
