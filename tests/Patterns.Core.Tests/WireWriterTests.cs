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
            if (ActionSpec.CarriesSecret(action.Kind))
            {
                Assert.Equal("", written);   // round 75: the passcode rides the target, and the writer never spells it back
                continue;
            }
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

    /// <summary>
    /// Round 79: the fence over the whole vocabulary. Every kind either writes a line for at least one of the shapes
    /// its arm takes, carries a secret (and writes nothing), or is named in <see cref="WireWriter.Unsayable"/> with
    /// its reason — so a verb added to the enum without a line fails here, not on a deck that recorded nothing.
    /// The nine verbs a recording deck never heard until this round are among the lines now.
    /// </summary>
    [Fact]
    public void EveryKindWritesALineOrIsNamedAsOneTheWireCannotSay()
    {
        var probes = new (string Target, string Value)[] { ("", ""), ("1", "1"), ("caller", "on"), ("a", "b"), ("Info HDMI", "REPLACE"), ("FOCUSED", "Grid") };
        var unnamed = new List<string>();
        foreach (var kind in Enum.GetValues<ShowActionKind>())
        {
            var lines = probes.Select(p => WireWriter.Line(new ShowAction(kind, p.Target, p.Value))).Where(l => l.Length > 0).ToList();
            if (ActionSpec.CarriesSecret(kind) || WireWriter.Unsayable.Contains(kind))
            {
                Assert.Empty(lines);
                continue;
            }
            if (lines.Count == 0) unnamed.Add(kind.ToString());
        }
        Assert.True(unnamed.Count == 0, "No wire line and not named in WireWriter.Unsayable: " + string.Join(", ", unnamed));

        foreach (var kind in new[] { ShowActionKind.AudioRouting, ShowActionKind.AudioRoute, ShowActionKind.AudioUnroute, ShowActionKind.AudioVogMode, ShowActionKind.CountdownToggle, ShowActionKind.CountdownFollow, ShowActionKind.PlanShift, ShowActionKind.PlanResume, ShowActionKind.PlanCatchUp })
        {
            Assert.DoesNotContain(kind, WireWriter.Unsayable);
        }
        Assert.Equal("COUNTDOWN START", WireWriter.Line(new ShowAction(ShowActionKind.CountdownStart)));          // a bare COUNTDOWN would read as the toggle
        Assert.Equal(ShowActionKind.CountdownStart, ControlProtocol.Parse(WireWriter.Line(new ShowAction(ShowActionKind.CountdownStart))).Action.Kind);
        Assert.Equal("AUDIO UNROUTE music FROM Info HDMI", WireWriter.Line(new ShowAction(ShowActionKind.AudioUnroute, "music", "Info HDMI")));
    }

    [Fact]
    public void TheAdminVerbsWriteNothingSoNoFeedCarriesThePasscode()
    {
        foreach (var kind in new[] { ShowActionKind.UpdateApply, ShowActionKind.Restart })
        {
            Assert.True(ActionSpec.CarriesSecret(kind));
            var line = WireWriter.Line(new ShowAction(kind, "hunter2-9931"));
            Assert.Equal("", line);
            Assert.False(Secrets.Carries(line, "hunter2-9931"));
        }
        Assert.False(ActionSpec.CarriesSecret(ShowActionKind.ApplyLook));
        Assert.False(ActionSpec.CarriesSecret(ShowActionKind.LookSave));
        Assert.False(ActionSpec.CarriesSecret(ShowActionKind.VideoRestart));   // a restart of the clip, no passcode on it
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
