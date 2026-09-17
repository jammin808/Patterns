using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// Round 74: the wire written back. Every verb of the vocabulary table, parsed to its action and
/// written again, parses to the same action — so a deck recording what the desk does receives a
/// line that reproduces it exactly, and the writer can never drift from the parser without a
/// test saying so. The kinds the wire has no line for write nothing.
/// </summary>
public class WireWriterTests
{
    [Fact]
    public void EveryVerbOfTheWireRoundTripsThroughTheWriter()
    {
        foreach (var (line, action) in WireVocabularyTests.Verbs)
        {
            var written = WireWriter.Line(action);
            Assert.False(written.Length == 0, $"{line}: {action.Kind} has a line");
            var back = ControlProtocol.Parse(written);
            Assert.True(back.IsAction, $"{line} → '{written}' is a line of the wire");
            Assert.Equal(action, back.Action);
        }
    }

    [Fact]
    public void TheKindsAWireCannotSayWriteNothing()
    {
        Assert.Equal("", WireWriter.Line(new ShowAction(ShowActionKind.Take)));
        Assert.Equal("", WireWriter.Line(new ShowAction(ShowActionKind.Cut)));
        Assert.Equal("", WireWriter.Line(new ShowAction(ShowActionKind.Note, "", "a note")));
        Assert.Equal("", WireWriter.Line(new ShowAction(ShowActionKind.Unknown)));
    }

    [Fact]
    public void TheWriterSpellsTheNavigatorAndTheBuildVerbs()
    {
        Assert.Equal("NAV Cues 03.020", WireWriter.Line(new ShowAction(ShowActionKind.NavPage, "Cues", "03.020")));
        Assert.Equal("NAV Looks", WireWriter.Line(new ShowAction(ShowActionKind.NavPage, "Looks")));
        Assert.Equal("NAV BACK", WireWriter.Line(new ShowAction(ShowActionKind.NavBack)));
        Assert.Equal("NAV HOME", WireWriter.Line(new ShowAction(ShowActionKind.NavHome)));
        Assert.Equal("NAV SETTINGS TOGGLE", WireWriter.Line(new ShowAction(ShowActionKind.NavSettings, "", "TOGGLE")));
        Assert.Equal("LOOK SAVE Walk-in", WireWriter.Line(new ShowAction(ShowActionKind.LookSave, "", "Walk-in")));
        Assert.Equal("LOOK UPDATE", WireWriter.Line(new ShowAction(ShowActionKind.LookUpdate)));
        Assert.Equal("CUE ADD Doors", WireWriter.Line(new ShowAction(ShowActionKind.CueAdd, "", "Doors")));
        Assert.Equal("CUE ADD", WireWriter.Line(new ShowAction(ShowActionKind.CueAdd)));
        Assert.Equal("PRESET SAVE Bars", WireWriter.Line(new ShowAction(ShowActionKind.PresetSave, "", "Bars")));
        Assert.Equal("LT NEW Keynote FROM Neon", WireWriter.Line(new ShowAction(ShowActionKind.LowerThirdNew, "Neon", "Keynote")));
        Assert.Equal("LT NEW Keynote", WireWriter.Line(new ShowAction(ShowActionKind.LowerThirdNew, "", "Keynote")));
    }
}
