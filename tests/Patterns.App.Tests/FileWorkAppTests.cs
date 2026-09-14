using Avalonia.Headless.XUnit;
using Avalonia.Threading;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The show's files off the desk's thread: the autosave serialises the frozen show on a worker
/// and coalesces saves a newer one overtook, the recovery record is made and written on a worker
/// with the twin still told when it moved, a show file is parsed on a worker and applied once,
/// a cue sheet the same — and the files budget says what each phase cost, with nothing on the
/// desk's thread past a frame.
/// </summary>
public class FileWorkAppTests
{
    private static void Await(Task task) => TestApp.Pump(task.ContinueWith(_ => true));

    [AvaloniaFact]
    public void TheAutosaveWritesTheFrozenShowFromAWorkerAndCoalescesTheSavesANewerOneOvertook()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            vm.State.Name = "Gala night";
            vm.State.Pattern.Kind = PatternKind.ColorBars;
            Dispatcher.UIThread.RunJobs();
            var files = services.Files;
            for (var i = 0; i < 5; i++) services.SaveInBackground();
            Await(services.PendingSaves);

            var saved = JsonUtil.Deserialize<ShowState>(File.ReadAllText(services.Store.SettingsPath))!;
            Assert.Equal("Gala night", saved.Name);
            Assert.Equal(PatternKind.ColorBars, saved.Pattern.Kind);
            var serialised = files.Of(FileBudget.SaveSerialise)!;
            var snapshots = files.Of(FileBudget.SaveSnapshot)!;
            Assert.True(snapshots.Count >= 5, $"the snapshot is taken on the desk on every ask: {snapshots.Count}");   // the boot's own timer may have asked too
            Assert.Equal(snapshots.Count, serialised.Count + files.Coalesced);       // every ask either serialised or was overtaken by a newer one
            Assert.True(serialised.Count >= 1);
            Assert.True(snapshots.WorstMs < FileBudget.SlowMs, $"the snapshot costs nothing: {snapshots.WorstMs} ms");
            Assert.Equal(serialised.Count, files.Of(FileBudget.SaveWrite)!.Count);
            Assert.Equal(0, files.SlowOnDeskThread);
            Assert.StartsWith("Files: autosave", files.Describe());

            // An edit after the save is the next save's, never the one in flight: the file holds a whole show either way.
            vm.State.Name = "Gala night, day two";
            Dispatcher.UIThread.RunJobs();
            services.SaveInBackground();
            Await(services.PendingSaves);
            Assert.Equal("Gala night, day two", JsonUtil.Deserialize<ShowState>(File.ReadAllText(services.Store.SettingsPath))!.Name);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void TheRecoveryRecordIsMadeAndWrittenOnAWorkerAndTheTwinIsToldWhenItMoved()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            var moved = new List<RecoverySnapshot?>();
            services.RecoveryMoved += r => moved.Add(r);
            vm.IsSandboxActive = false;
            vm.State.Pattern.Kind = PatternKind.Grid;
            // A record is kept while something is live, playing, or a caller has a place: a cue on standby is a place.
            CueStacks.Caller(vm.State).Cues.Add(new RunCueConfig { Number = "01.010", Name = "Doors" });
            services.CueStack.SetArmed(true, ActionOrigin.Desk);
            Dispatcher.UIThread.RunJobs();
            vm.IsSandboxActive = true;                                                  // the sandbox opens: the record carries the air, whole
            Dispatcher.UIThread.RunJobs();
            vm.State.Pattern.Kind = PatternKind.ColorBars;                              // an edit in the sandbox: the air is still the grid
            Dispatcher.UIThread.RunJobs();
            Await(services.PendingSaves);
            Dispatcher.UIThread.RunJobs();

            var record = new RecoveryStore(b.Dir).Read();
            Assert.NotNull(record);
            Assert.True(record!.Sandboxed);
            Assert.NotNull(record.Air);
            Assert.Equal(PatternKind.Grid, record.Air!.Pattern.Kind);
            Assert.Equal("01.010", record.Run!.StandbyCueId is null ? "" : "01.010");
            Assert.Contains(moved, r => r is { Sandboxed: true, Air: not null });
            var files = services.Files;
            Assert.True(files.Of(FileBudget.RecoverySerialise)!.Count >= 1);
            Assert.Equal(files.Of(FileBudget.RecoverySerialise)!.Count, files.Of(FileBudget.RecoveryWrite)!.Count);
            Assert.Equal(0, files.SlowOnDeskThread);
            Assert.Contains("recovery", files.Describe());

            // The sandbox closes: the record follows, in order behind the earlier writes, and carries no air.
            vm.IsSandboxActive = false;
            Dispatcher.UIThread.RunJobs();
            Await(services.PendingSaves);
            Dispatcher.UIThread.RunJobs();
            var after = new RecoveryStore(b.Dir).Read();
            Assert.NotNull(after);
            Assert.False(after!.Sandboxed);
            Assert.Null(after.Air);
        }
        finally
        {
            b.Dispose();
        }
    }

    [AvaloniaFact]
    public void AShowFileIsParsedOnAWorkerAndAppliedOnceAndACueSheetTheSame()
    {
        var b = TestApp.Boot();
        try
        {
            var (services, vm, _) = b;
            vm.IsSandboxActive = false;
            var other = new ShowState { Name = "Awards" };
            other.Pattern.Kind = PatternKind.TestCard;
            var path = Path.Combine(b.Dir, "awards.patshow.json");
            services.Store.SaveJsonTo(path, JsonUtil.Serialize(other));
            var words = TestApp.Pump(vm.LoadShowFromAsync(path));
            Assert.Equal("Show loaded: awards.patshow.json", words);
            Assert.Equal("Awards", vm.State.Name);
            Assert.Equal(PatternKind.TestCard, vm.State.Pattern.Kind);
            Assert.Equal(1, services.Files.Of(FileBudget.ShowLoadParse)!.Count);
            Assert.Equal("Show file could not be read.", TestApp.Pump(vm.LoadShowFromAsync(Path.Combine(b.Dir, "missing.patshow.json"))));

            var sheet = Path.Combine(b.Dir, "running-order.csv");
            File.WriteAllText(sheet, "Name\nCoffee break\nBreakout session\nLunch\n");
            var report = TestApp.Pump(vm.ImportCueSheetFromAsync(sheet, append: false));
            Assert.Contains("running-order.csv", report);
            Assert.Equal(3, CueStacks.Caller(vm.State).Cues.Count);
            Assert.Equal("Coffee break", CueStacks.Caller(vm.State).Cues[0].Name);
            Assert.Equal(1, services.Files.Of(FileBudget.CueSheetParse)!.Count);
            Assert.StartsWith("Could not read", TestApp.Pump(vm.ImportCueSheetFromAsync(Path.Combine(b.Dir, "nowhere.csv"), append: true)));

            var saveTo = Path.Combine(b.Dir, "saved-by-hand.patshow.json");
            Assert.Equal("Show saved: saved-by-hand.patshow.json", TestApp.Pump(vm.SaveShowToAsync(saveTo)));
            Assert.Equal("Awards", JsonUtil.Deserialize<ShowState>(File.ReadAllText(saveTo))!.Name);
            Assert.Equal(1, services.Files.Of(FileBudget.ShowSaveWrite)!.Count);
            Assert.Equal(0, services.Files.SlowOnDeskThread);
        }
        finally
        {
            b.Dispose();
        }
    }
}
