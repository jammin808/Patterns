using System.IO.Compression;
using Patterns.Core.Effects;
using Patterns.Core.LowerThirds;
using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;
using Xunit;

namespace Patterns.Core.Tests;

/// <summary>
/// The round-14 crash: exit -1073741819 (0xC0000005, an access violation) twice in one evening,
/// the log stopping, the watchdog bringing the show back. What the next start knows about it, what
/// a debugger gets, and what the run after it does differently.
/// </summary>
public class CrashHardeningTests
{
    private static readonly DateTime T0 = new(2026, 9, 6, 22, 30, 0, DateTimeKind.Utc);

    [Fact]
    public void ExitCodesTellANativeFaultFromTheRest()
    {
        Assert.Equal(-1073741819, ExitCodes.AccessViolation);                 // the watchdog log's number
        Assert.True(ExitCodes.IsNativeFault(ExitCodes.AccessViolation));
        Assert.True(ExitCodes.IsNativeFault(ExitCodes.HeapCorruption));
        Assert.True(ExitCodes.IsNativeFault(ExitCodes.StackBufferOverrun));
        Assert.False(ExitCodes.IsNativeFault(ExitCodes.ClrException));
        Assert.False(ExitCodes.IsNativeFault(0));
        Assert.False(ExitCodes.IsNativeFault(SupervisorPolicy.RestartRequestExitCode));
        Assert.False(ExitCodes.IsNativeFault(SupervisorPolicy.UpdateRequestExitCode));
        Assert.Equal("0xC0000005", ExitCodes.Hex(ExitCodes.AccessViolation));
        Assert.Equal("0x00000003", ExitCodes.Hex(3));

        Assert.Contains("access violation", ExitCodes.Describe(-1073741819));
        Assert.Contains("native fault", ExitCodes.Describe(-1073741819));
        Assert.Equal("a clean close", ExitCodes.Describe(0));
        Assert.Contains("Machine page", ExitCodes.Describe(SupervisorPolicy.RestartRequestExitCode));
        Assert.Contains("update", ExitCodes.Describe(SupervisorPolicy.UpdateRequestExitCode));
        Assert.Contains(".NET exception", ExitCodes.Describe(ExitCodes.ClrException));
        Assert.Equal("exit code 3 (0x00000003)", ExitCodes.Describe(3));
    }

    [Fact]
    public void TheCrashNoteRoundTripsOnceAndJunkIsIgnored()
    {
        var dir = TempDir("patterns-crashnote-");
        try
        {
            Assert.Null(CrashMarker.Peek(dir));
            Assert.Null(CrashMarker.ReadAndClear(dir));

            var note = new CrashNote(ExitCodes.AccessViolation, ExitCodes.Describe(ExitCodes.AccessViolation), true, false, T0, 4174, "", 1);
            CrashMarker.Write(dir, note);
            Assert.True(File.Exists(Path.Combine(dir, CrashMarker.FileName)));
            Assert.Equal(note, CrashMarker.Peek(dir));                          // a peek leaves it
            Assert.True(File.Exists(Path.Combine(dir, CrashMarker.FileName)));
            Assert.Equal(note, CrashMarker.ReadAndClear(dir));                  // a read consumes it
            Assert.False(File.Exists(Path.Combine(dir, CrashMarker.FileName)));
            Assert.Null(CrashMarker.ReadAndClear(dir));                         // one run only

            File.WriteAllText(Path.Combine(dir, CrashMarker.FileName), "{ not json");
            Assert.Null(CrashMarker.ReadAndClear(dir));                         // junk never stops a start
            Assert.False(File.Exists(Path.Combine(dir, CrashMarker.FileName)));
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TheCrashSentenceSaysWhatWhenAndHowLong()
    {
        var withDump = new CrashNote(ExitCodes.AccessViolation, ExitCodes.Describe(ExitCodes.AccessViolation), true, false, T0, 4174, @"C:\Show\crashes\patterns-11032-1.dmp", 1).Sentence;
        Assert.Contains("ended in an access violation", withDump);
        Assert.Contains("after 1.2 h", withDump);
        Assert.Contains(@"a mini-dump is at C:\Show\crashes\patterns-11032-1.dmp", withDump);

        var withoutDump = new CrashNote(ExitCodes.AccessViolation, ExitCodes.Describe(ExitCodes.AccessViolation), true, false, T0, 2469, "", 2).Sentence;
        Assert.Contains("after 41 min", withoutDump);
        Assert.Contains("no mini-dump was written (createdump.exe is not beside Patterns.exe)", withoutDump);

        var hung = new CrashNote(1, ExitCodes.Describe(1), false, true, T0, 30, "", 0).Sentence;
        Assert.Contains("a hung UI thread (the watchdog ended it)", hung);
        Assert.Contains("after 30 s", hung);
        Assert.DoesNotContain("mini-dump", hung);                                  // a hang is not a native fault

        var managed = new CrashNote(ExitCodes.ClrException, ExitCodes.Describe(ExitCodes.ClrException), false, false, T0, 7200, "", 0).Sentence;
        Assert.Contains("an unhandled .NET exception", managed);
        Assert.Contains("after 2 h", managed);
        Assert.DoesNotContain("mini-dump", managed);
    }

    [Fact]
    public void TheDumpEnvironmentAndTheSweepKeepTheNewestThree()
    {
        var dir = TempDir("patterns-dumps-");
        try
        {
            var env = CrashDumps.Environment(dir);
            Assert.Equal("1", env["DOTNET_DbgEnableMiniDump"]);
            Assert.Equal("1", env["DOTNET_DbgMiniDumpType"]);                    // the smallest dump: the faulting thread and the stacks
            Assert.StartsWith(CrashDumps.DirectoryFor(dir), env["DOTNET_DbgMiniDumpName"]);
            Assert.EndsWith("patterns-%p-%t.dmp", env["DOTNET_DbgMiniDumpName"]);
            Assert.Equal("0", env["DOTNET_CreateDumpDiagnostics"]);

            // The first sweep makes the folder; there is nothing in it.
            Assert.Empty(CrashDumps.Sweep(dir));
            Assert.True(Directory.Exists(CrashDumps.DirectoryFor(dir)));
            Assert.Equal("", CrashDumps.NewestSince(dir, T0));

            for (var i = 1; i <= 5; i++)
            {
                var path = Path.Combine(CrashDumps.DirectoryFor(dir), $"patterns-{i}-{i}.dmp");
                File.WriteAllText(path, "dump " + i);
                File.SetLastWriteTimeUtc(path, T0.AddMinutes(i));
            }
            var kept = CrashDumps.Sweep(dir);
            Assert.Equal(new[] { "patterns-5-5.dmp", "patterns-4-4.dmp", "patterns-3-3.dmp" }, kept.Select(Path.GetFileName));
            Assert.Equal(CrashDumps.Keep, Directory.GetFiles(CrashDumps.DirectoryFor(dir), "*.dmp").Length);

            Assert.EndsWith("patterns-5-5.dmp", CrashDumps.NewestSince(dir, T0));
            Assert.EndsWith("patterns-5-5.dmp", CrashDumps.NewestSince(dir, T0.AddMinutes(5)));   // written at the run's start counts (5 s of grace)
            Assert.Equal("", CrashDumps.NewestSince(dir, T0.AddMinutes(6)));                       // nothing since the next run started
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Theory]
    [InlineData(VideoDecodingKind.Auto, false, true)]
    [InlineData(VideoDecodingKind.Auto, true, false)]
    [InlineData(VideoDecodingKind.Hardware, false, true)]
    [InlineData(VideoDecodingKind.Hardware, true, true)]
    [InlineData(VideoDecodingKind.Software, false, false)]
    [InlineData(VideoDecodingKind.Software, true, false)]
    public void VideoDecodingFollowsTheChoiceAndTheSafeRun(VideoDecodingKind kind, bool safeRun, bool hardware)
    {
        Assert.Equal(hardware, VideoDecodingChoice.UseHardware(kind, safeRun));
        var words = VideoDecodingChoice.Words(kind, safeRun);
        Assert.Contains(hardware ? "graphics card" : "software", words);
        if (kind == VideoDecodingKind.Auto && safeRun) Assert.Contains("safe run", words);
        if (kind == VideoDecodingKind.Auto && !safeRun) Assert.StartsWith("Auto:", words);
    }

    [Fact]
    public void TheSupportBundleCarriesTheCrashNoteAndTheNewestSmallDump()
    {
        var dir = TempDir("patterns-bundle-crash-");
        try
        {
            CrashMarker.Write(dir, new CrashNote(ExitCodes.AccessViolation, "an access violation", true, false, T0, 100, "", 1));
            var crashes = CrashDumps.DirectoryFor(dir);
            Directory.CreateDirectory(crashes);
            var older = Path.Combine(crashes, "patterns-1-1.dmp");
            var newer = Path.Combine(crashes, "patterns-2-2.dmp");
            File.WriteAllText(older, "older dump");
            File.WriteAllText(newer, "newer dump");
            File.SetLastWriteTimeUtc(older, T0);
            File.SetLastWriteTimeUtc(newer, T0.AddMinutes(1));

            var zipPath = Path.Combine(dir, SupportBundle.FileNameFor(T0));
            var entries = SupportBundle.Build(dir, zipPath, "Site: Lobby");
            Assert.Contains(CrashMarker.FileName, entries);
            Assert.Contains("crashes/patterns-2-2.dmp", entries);
            Assert.DoesNotContain("crashes/patterns-1-1.dmp", entries);            // the newest only
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                using var info = new StreamReader(zip.GetEntry("bundle-info.txt")!.Open());
                var text = info.ReadToEnd();
                Assert.Contains("Mini-dumps of native crashes on disk (2):", text);
                Assert.Contains("patterns-2-2.dmp", text);
                Assert.Contains("patterns-1-1.dmp", text);                          // every one is named
                Assert.Contains("in the zip as crashes/patterns-2-2.dmp", text);
                Assert.Equal("newer dump", new StreamReader(zip.GetEntry("crashes/patterns-2-2.dmp")!.Open()).ReadToEnd());
            }

            // A dump too big to send stays on disk and the note says where; nothing else changes.
            var huge = Path.Combine(crashes, "patterns-3-3.dmp");
            using (var stream = new FileStream(huge, FileMode.CreateNew, FileAccess.Write))
            {
                stream.SetLength(CrashDumps.BundleMaxBytes + 1);
            }
            File.SetLastWriteTimeUtc(huge, T0.AddMinutes(2));
            entries = SupportBundle.Build(dir, zipPath, "Site: Lobby");
            Assert.DoesNotContain(entries, e => e.StartsWith("crashes/", StringComparison.Ordinal));
            using (var zip = ZipFile.OpenRead(zipPath))
            {
                using var info = new StreamReader(zip.GetEntry("bundle-info.txt")!.Open());
                var text = info.ReadToEnd();
                Assert.Contains("larger than 60 MB and is not in this zip", text);
                Assert.Contains(huge, text);
            }
        }
        finally
        {
            Directory.Delete(dir, recursive: true);
        }
    }

    [Fact]
    public void TheFractalRasterIsTheSamePictureAtAnyParallelism()
    {
        Assert.True(FractalRaster.DefaultParallelism >= 1);
        Assert.True(FractalRaster.DefaultParallelism <= Math.Max(1, Environment.ProcessorCount / 2));
        var before = FractalRaster.Parallelism;
        try
        {
            var o = new FractalOptions { Kind = FractalKind.Julia, Iterations = 48, Zoom = 1.2 };
            var palette = new[] { SKColors.Red, SKColors.Gold, SKColors.Teal };
            var view = FractalView.Of(o, 2.5, AudioLevelFrame.Zero);
            var size = new SKSizeI(96, 54);

            FractalRaster.Parallelism = 1;
            using var one = FractalRaster.Render(null, size, o.Kind, palette, view);
            var single = (int[])one.Pixels.Clone();

            FractalRaster.Parallelism = 4;
            using var four = FractalRaster.Render(null, size, o.Kind, palette, view);
            Assert.Equal(single, four.Pixels);

            FractalRaster.Parallelism = 0;                                             // never below one
            Assert.Equal(1, FractalRaster.Parallelism);
            using var clamped = FractalRaster.Render(null, size, o.Kind, palette, view);
            Assert.Equal(single, clamped.Pixels);
            Assert.Contains(single, p => p != single[0]);                              // and it is a picture, not a flat fill
        }
        finally
        {
            FractalRaster.Parallelism = before;
        }
    }

    private static string TempDir(string prefix)
    {
        var dir = Path.Combine(Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(dir);
        return dir;
    }
}

/// <summary>A lower third's fractal element on the CPU path: a new frame 25 times a second, the same picture between.</summary>
[Collection("InputBus")]
public class LowerThirdFractalThrottleTests
{
    [Fact]
    public void ALowerThirdFractalGetsANewFrameTwentyFiveTimesASecond()
    {
        var state = RenderTestHarness.State(s => s.Pattern.Kind = PatternKind.FlatField);
        var d = new LowerThirdDesign { Name = "Wave", Width = 1200, Height = 300, InMs = 100, OutMs = 100 };
        var fractal = new LowerThirdElement { Name = "Wave", Kind = LowerThirdElementKind.Fractal, X = 0, Y = 0, W = 1200, H = 300 };
        fractal.Fractal.Quality = FractalQuality.Fast;
        fractal.Fractal.Iterations = 16;
        d.Elements.Add(fractal);
        state.LowerThirds.Designs.Add(d);
        state.LowerThirds.Show(d, ShowClock.UtcAt(1));

        var engine = new PatternEngine();
        using var sink = new SinkState();
        var snap = RenderTestHarness.Snap(state);
        var info = new SKImageInfo(640, 360, SKColorType.Bgra8888, SKAlphaType.Premul);
        using var surface = SKSurface.Create(info);
        void Draw(double time)
        {
            var ctx = new RenderContext
            {
                ViewportSize = new SKSizeI(640, 360), ReferenceSize = new SKSizeI(640, 360), Time = time,
                Now = new DateTime(2026, 9, 6, 12, 0, 0), UtcNow = RenderTestHarness.FixedUtcNow, Sink = SinkKind.Output, SinkIndex = 1, SinkLabel = "test",
            };
            engine.Render(surface.Canvas, snap, in ctx, sink);
        }

        Draw(1.5);
        var cache = sink.LowerThirds[fractal.Id];
        Assert.NotNull(cache.Fractal);
        Assert.NotNull(cache.FractalImage);
        Assert.Equal(1, cache.FractalFrames);

        Draw(1.51);                                   // 10 ms on: the same frame is drawn again
        Draw(1.52);
        Assert.Equal(1, cache.FractalFrames);

        Draw(1.5 + LowerThirdRenderer.FractalFrameSeconds);   // the next frame is due
        Assert.Equal(2, cache.FractalFrames);

        Draw(1.2);                                    // time went back (a fresh show): a frame at once
        Assert.Equal(3, cache.FractalFrames);

        fractal.Fractal.ColorsCsv = "#FF0000,#00FF00";  // a new palette draws at once, whatever the clock
        snap = RenderTestHarness.Snap(state, version: 2);
        Draw(1.205);
        Assert.Equal(4, cache.FractalFrames);
        Assert.Equal(-1, cache.FailedVersion);
        Assert.Equal(25, LowerThirdRenderer.FractalFps);
    }
}
