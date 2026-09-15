using System.Diagnostics;
using Patterns.Core.LowerThirds;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;
using Xunit.Abstractions;

namespace Patterns.Core.Tests;

/// <summary>
/// What a publish costs on a corporate-sized show, with and without section sharing — the
/// numbers go to the test output; the assertion is only that a shared publish really shares.
/// </summary>
public class PublishCostTests
{
    private readonly ITestOutputHelper _out;

    public PublishCostTests(ITestOutputHelper output) => _out = output;

    /// <summary>Twelve looks with their payloads, forty cues, a stinger library, a twenty-item playlist, twenty people.</summary>
    public static ShowState CorporateShow()
    {
        var s = new ShowState();
        for (var i = 0; i < 4; i++)
        {
            s.Output.Placements.Add(new ScreenPlacement { ScreenId = $"p{i}", Planned = true, X = i * 1920, PlannedWidth = 1920, PlannedHeight = 1080 });
        }
        for (var i = 0; i < 20; i++)
        {
            s.Pattern.Media.Playlist.Sections.Add(new PlaylistSectionConfig { Name = $"Part {i}" });
            s.Pattern.Media.Playlist.Sections[^1].Items.Add(new PlaylistItemConfig { Path = $"C:\\show\\clip-{i:00}.mp4" });
        }
        for (var i = 0; i < 12; i++)
        {
            s.Pattern.Kind = (PatternKind)(i % 6);
            s.LooksAndCues.Looks.Add(new LookConfig { Name = $"Look {i}", Hotkey = i + 1, Json = LookService.Capture(s) });
        }
        var caller = CueStacks.Caller(s);
        for (var i = 0; i < 40; i++)
        {
            var cue = new RunCueConfig { Number = $"{i + 1}", Name = $"Cue {i + 1}" };
            cue.Actions.Add(new CueActionConfig { Kind = ShowActionKind.ApplyLook, Target = $"Look {i % 12}" });
            caller.Cues.Add(cue);
        }
        for (var i = 0; i < 6; i++)
        {
            s.Stingers.Items.Add(new StingerItemConfig { Path = $"C:\\show\\sting-{i}.mp4", Name = $"Sting {i}" });
        }
        for (var i = 0; i < 20; i++)
        {
            s.LowerThirds.Entries.Add(new LowerThirdEntry { Name = $"Person {i}", Role = "Speaker" });
        }
        return s;
    }

    [Fact]
    public void ASharedPublishCopiesOnlyThePattern()
    {
        var state = CorporateShow();
        var bus = new SnapshotBus(state, () => 1);
        var changes = new ChangeTracker(state, () => { }, trackSections: true);
        bus.Publish(state, changes);

        // The pointer move: a layer box, one property at a time, two hundred times.
        var full = Time(200, i =>
        {
            state.Pattern.Layer1.XPct = i % 50;
            bus.Publish(state);           // as it was: the whole show every time
        });
        var shared = Time(200, i =>
        {
            state.Pattern.Layer1.XPct = i % 50;
            bus.Publish(state, changes);  // now: the pattern section, the rest shared
        });
        var before = bus.Current;
        state.Pattern.Layer1.XPct = 3;
        bus.Publish(state, changes);
        var sharedSections = SnapshotClone.SharedSections(before.State, bus.Current.State);

        _out.WriteLine($"whole-show clone: {full:0.000} ms per publish");
        _out.WriteLine($"section sharing:  {shared:0.000} ms per publish");
        _out.WriteLine($"shared sections:  {sharedSections.Count} of {sharedSections.Count + 1} ({string.Join(", ", sharedSections.Take(6))}…)");
        Assert.Contains(nameof(ShowState.LooksAndCues), sharedSections);
        Assert.DoesNotContain(nameof(ShowState.Pattern), sharedSections);
    }

    private static double Time(int n, Action<int> work)
    {
        for (var i = 0; i < 20; i++) work(i); // warm
        var sw = Stopwatch.StartNew();
        for (var i = 0; i < n; i++) work(i);
        return sw.Elapsed.TotalMilliseconds / n;
    }
}
