using Avalonia.Headless.XUnit;
using Patterns.Core.Model;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>The run list's rows follow the cues in place: a cue still there keeps its row; a new one comes in where it sits; a gone one goes.</summary>
public class RunListRowsTests
{
    [AvaloniaFact]
    public void TheRowsFollowTheCuesInPlace()
    {
        var b = TestApp.Boot();
        try
        {
            var stack = b.Services.CueStack.Stack;
            var lights = new RunCueConfig { Number = "01.010", Name = "Lights" };
            var walkIn = new RunCueConfig { Number = "01.020", Name = "Walk-in" };
            stack.Cues.Add(lights);
            stack.Cues.Add(walkIn);
            var run = b.Vm.Run;
            run.Refresh();
            Assert.Equal(2, run.Rows.Count);
            var lightsRow = run.Rows[0];
            var walkInRow = run.Rows[1];
            var raised = new List<string>();
            lightsRow.PropertyChanged += (_, e) => raised.Add(e.PropertyName ?? "");
            var changes = 0;
            run.Rows.CollectionChanged += (_, _) => changes++;

            lights.Name = "Lights down";                                            // edited in place
            var music = new RunCueConfig { Number = "01.015", Name = "Music" };
            stack.Cues.Insert(1, music);                                           // a cue comes in between
            run.Refresh();

            Assert.Same(lightsRow, run.Rows[0]);                                   // the rows the caller had are the rows still there
            Assert.Same(walkInRow, run.Rows[2]);
            Assert.Equal("Music", run.Rows[1].Name);
            Assert.Equal("Lights down", lightsRow.Name);
            Assert.Contains("Name", raised);                                       // what reads through was raised
            Assert.Equal(1, changes);                                              // one insert, nothing else touched

            stack.Cues.Remove(lights);
            run.Refresh();
            Assert.Equal(2, run.Rows.Count);
            Assert.Same(walkInRow, run.Rows[1]);
            Assert.Equal(2, changes);                                              // one removal
        }
        finally
        {
            b.Dispose();
        }
    }
}
