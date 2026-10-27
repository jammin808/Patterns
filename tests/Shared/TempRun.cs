global using Patterns.Tests;

using System.Collections.Concurrent;
using System.Globalization;
using System.Runtime.CompilerServices;
using Xunit;
using Xunit.Abstractions;
using Xunit.Sdk;

namespace Patterns.Tests;

/// <summary>
/// The suite's temporary files, held to one rule: a test removes every folder and file it makes under the temp path
/// when it ends. Compiled into every suite (the root <c>Directory.Build.props</c> links it into each <c>*.Tests</c>
/// project with <see cref="TempFenceLast"/> as the suite's collection orderer).
/// <para>
/// At the module's first touch the process's temp path is pointed at a folder of this run's own,
/// <c>patterns-run-&lt;suite&gt;-&lt;id&gt;</c> under the machine's (TMPDIR on Linux and macOS, TMP and TEMP on Windows),
/// so every <see cref="Path.GetTempPath"/> of the suite, of the code under test and of a child process a test
/// starts lands there. Suites that run at once — <c>dotnet test</c> runs them side by side, as many at once as the
/// machine has processors — never read each other's, and nothing outside this run's folder is ever read or removed:
/// earlier runs' leftovers in the machine's temp path stay as they are.
/// </para>
/// <para>
/// The fence is each suite's last test (<c>TempFenceTests</c>, in the collection <see cref="FenceCollection"/>,
/// which <see cref="TempFenceLast"/> orders after every other): it lists the run's folder and fails on anything left in
/// it, naming the test that made it where a test made it through <see cref="Folder"/> or <see cref="FilePath"/>. The
/// process's exit then removes the folder whatever it holds (unless <see cref="KeepVariable"/> is 1), so a failed or
/// filtered run leaves nothing either — the fence, not that removal, is what holds each test to its own.
/// </para>
/// </summary>
internal static class TempRun
{
    /// <summary>The fence's collection: run after every other collection of the suite and never beside one.</summary>
    public const string FenceCollection = "The run's temporary folder, judged last";

    /// <summary>Set to 1 to keep the run's folder at the process's exit, for a look at what the fence named.</summary>
    public const string KeepVariable = "PATTERNS_TEST_KEEP_TEMP";

    /// <summary>
    /// How a removal waits for a process that still holds a file — on Windows an open file blocks its folder's removal,
    /// and a log or a socket closes a moment after its owner was disposed: ten tries, 50 ms apart, half a second at
    /// most. No measurement sets it; past it the removal keeps what held the path, and the fence tries it again at the
    /// run's end and says so.
    /// </summary>
    private const int Attempts = 10;
    private const int PauseMs = 50;

    /// <summary>
    /// What a child .NET process's runtime puts in its temp path by itself — its diagnostics socket and its debugger
    /// pipes — and leaves there when its test kills it: the runtime's, not a test's, so said and not judged; the run's
    /// folder takes it at the exit. Only these two names: a named pipe code opens (<c>CoreFxPipe_</c> on Linux and
    /// macOS) is code's, product's or test's, and is judged like any other entry.
    /// </summary>
    private static readonly string[] RuntimeOwn = { "dotnet-diagnostic-", "clr-debug-pipe-" };

    private static readonly ConcurrentDictionary<string, Entry> Made = new(StringComparer.Ordinal);
    private static string[]? s_order;

    /// <summary>This run's temporary folder: the process's temp path from the module's first touch to its exit.</summary>
    public static string Root { get; private set; } = "";

    [ModuleInitializer]
    internal static void Begin()
    {
        var machine = Path.GetTempPath();
        var suite = typeof(TempRun).Assembly.GetName().Name!.Replace("Patterns.", "", StringComparison.Ordinal)
                                                         .Replace(".Tests", "", StringComparison.Ordinal).ToLowerInvariant();
        Root = Path.Combine(machine, $"patterns-run-{suite}-{Guid.NewGuid().ToString("N")[..12]}");
        Directory.CreateDirectory(Root);
        if (OperatingSystem.IsWindows())
        {
            Environment.SetEnvironmentVariable("TMP", Root);
            Environment.SetEnvironmentVariable("TEMP", Root);
        }
        else
        {
            Environment.SetEnvironmentVariable("TMPDIR", Root);
        }
        AppDomain.CurrentDomain.ProcessExit += (_, _) => End();
    }

    /// <summary>
    /// Makes a fresh folder under the run's temp path, named <paramref name="prefix"/> and a new id. Its test removes it
    /// when it ends: a <c>using</c> on the folder, or <see cref="Remove"/> by its path in a <c>finally</c>.
    /// </summary>
    public static TempFolder Folder(string prefix, [CallerFilePath] string file = "", [CallerMemberName] string member = "")
    {
        var path = Path.Combine(Root, prefix + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(path);
        Made[path] = new Entry(Maker(file, member));
        return new TempFolder(path);
    }

    /// <summary>A path for a file under the run's temp path, a new id before <paramref name="name"/> (the file is not made); its test removes it with <see cref="Remove"/> when it ends.</summary>
    public static string FilePath(string name, [CallerFilePath] string file = "", [CallerMemberName] string member = "")
    {
        var path = Path.Combine(Root, Guid.NewGuid().ToString("N") + "-" + name);
        Made[path] = new Entry(Maker(file, member));
        return path;
    }

    /// <summary>
    /// Removes a folder (with everything in it) or a file a test made, and nothing when it is not there. A process that
    /// still holds a file is waited for (<see cref="Attempts"/>); past that what held it is kept, and the fence tries
    /// the path again at the run's end and says it in its output, with the test that made it. Never throws: a test's own
    /// failure is never hidden behind its clean-up's.
    /// </summary>
    /// <returns>Whether the path is gone.</returns>
    public static bool Remove(string path)
    {
        Exception? last = null;
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                if (last is UnauthorizedAccessException) Writable(path);
                if (Directory.Exists(path)) Directory.Delete(path, recursive: true);
                else if (File.Exists(path)) File.Delete(path);
                Made.AddOrUpdate(path, _ => new Entry("") { Removed = true }, (_, e) => e with { Removed = true, Held = null });
                return true;
            }
            catch (IOException ex)
            {
                last = ex;
            }
            catch (UnauthorizedAccessException ex)
            {
                last = ex;
            }
            if (attempt < Attempts) Thread.Sleep(PauseMs);
        }
        var held = $"{last!.GetType().Name}: {last.Message}";
        Made.AddOrUpdate(path, _ => new Entry("") { Held = held }, (_, e) => e with { Held = held });
        return false;
    }

    /// <summary>The order <see cref="TempFenceLast"/> gave the suite's collections, which the fence reads to know it ran last.</summary>
    internal static void Ordered(IEnumerable<string> collections) => s_order = collections.ToArray();

    /// <summary>
    /// The fence's reading at the run's end: what is still in the run's folder, each entry with what made it and what
    /// became of it, and whether this ran after every other collection. A path a test could not remove because a
    /// process held it is tried again first; gone now, it is said and not counted.
    /// </summary>
    /// <returns>One line per fault; none when the run left nothing of its own making.</returns>
    public static IReadOnlyList<string> Judge(ITestOutputHelper output)
    {
        var faults = new List<string>();
        var order = s_order;
        if (order is null || order.Length == 0)
            faults.Add($"the suite's collections were not ordered by {nameof(TempFenceLast)}, so this fence cannot know it ran after every other test (the root Directory.Build.props names it for every *.Tests project)");
        else if (!string.Equals(order[^1], FenceCollection, StringComparison.Ordinal))
            faults.Add($"the fence's collection ran before \"{order[^1]}\", so a test after it could leave a folder unread");

        foreach (var (path, entry) in Made.Where(e => e.Value.Held is not null).OrderBy(e => e.Key, StringComparer.Ordinal).ToArray())
        {
            var gone = Remove(path);
            output.WriteLine($"{Path.GetFileName(path)} (made by {entry.Maker}) was held at its test's end ({entry.Held}); "
                           + (gone ? "removed at the run's end." : "still held at the run's end."));
        }

        if (!Directory.Exists(Root))
        {
            faults.Add($"the run's folder {Root} is gone: something removed the whole temp path");
            return faults;
        }
        foreach (var path in Directory.EnumerateFileSystemEntries(Root).Order(StringComparer.Ordinal))
        {
            var name = Path.GetFileName(path);
            if (RuntimeOwn.Any(p => name.StartsWith(p, StringComparison.Ordinal)))
            {
                output.WriteLine($"{name}: the runtime's own, left by a child process its test ended; not judged, and removed with the run's folder.");
                continue;
            }
            faults.Add(Describe(path));
        }
        var made = Made.Count(e => e.Value.Maker.Length > 0);
        output.WriteLine(string.Create(CultureInfo.InvariantCulture,
            $"The run's temporary folder {Root}: {made} made through {nameof(TempRun)}, {Made.Count(e => e.Value.Maker.Length > 0 && e.Value.Removed)} removed by their tests, {faults.Count} fault(s)."));
        foreach (var fault in faults) output.WriteLine(fault);
        return faults;
    }

    private static string Describe(string path)
    {
        var name = Path.GetFileName(path);
        var isFolder = Directory.Exists(path);
        var what = isFolder ? "folder" : "file";
        var inside = "";
        if (isFolder)
        {
            var files = Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories).Take(1000).ToArray();
            var bytes = files.Sum(f => new FileInfo(f).Length);
            var first = string.Join(", ", files.Take(4).Select(f => Path.GetRelativePath(path, f)));
            inside = string.Create(CultureInfo.InvariantCulture, $", {files.Length} file(s), {bytes} bytes{(first.Length > 0 ? ": " + first : "")}");
        }
        var story = Made.TryGetValue(path, out var entry)
            ? entry.Removed ? $"made by {Who(entry)}, removed at its test's end and written again after — by something that outlived its test"
            : entry.Held is not null ? $"made by {Who(entry)}, held past the run's end ({entry.Held})"
            : $"made by {Who(entry)}, never removed"
            : $"not made through {nameof(TempRun)} — a test or the code under test called Path.GetTempPath() itself, and nothing removed it";
        return $"{name} ({what}{inside}): {story}";
    }

    private static string Who(Entry entry) => entry.Maker.Length > 0 ? entry.Maker : "a test that removed it by path";

    private static string Maker(string file, string member) => $"{Path.GetFileNameWithoutExtension(file)}.{member}";

    /// <summary>Gives the owner back write and search on every folder under <paramref name="path"/> and clears read-only files, so a test that locked its own folder can still be removed.</summary>
    private static void Writable(string path)
    {
        if (!Directory.Exists(path))
        {
            if (File.Exists(path)) File.SetAttributes(path, FileAttributes.Normal);
            return;
        }
        if (!OperatingSystem.IsWindows())
        {
            const UnixFileMode Owner = UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute;
            File.SetUnixFileMode(path, File.GetUnixFileMode(path) | Owner);
            foreach (var dir in Directory.EnumerateDirectories(path, "*", SearchOption.AllDirectories))
                File.SetUnixFileMode(dir, File.GetUnixFileMode(dir) | Owner);
        }
        foreach (var file in Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            File.SetAttributes(file, FileAttributes.Normal);
    }

    /// <summary>The process's exit: the run's folder goes, whatever the fence found (said on standard error when it cannot).</summary>
    private static void End()
    {
        if (string.Equals(Environment.GetEnvironmentVariable(KeepVariable), "1", StringComparison.Ordinal))
        {
            Console.Error.WriteLine($"{KeepVariable}=1: the run's temporary folder is kept at {Root}");
            return;
        }
        Exception? last = null;
        for (var attempt = 1; attempt <= Attempts; attempt++)
        {
            try
            {
                if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
                return;
            }
            catch (IOException ex)
            {
                last = ex;
            }
            catch (UnauthorizedAccessException ex)
            {
                last = ex;
            }
            if (attempt < Attempts) Thread.Sleep(PauseMs);
        }
        Console.Error.WriteLine($"The run's temporary folder {Root} was not removed at the exit ({last!.GetType().Name}: {last.Message}).");
    }

    /// <summary>What the run knows of a path a test made: who made it, whether its test removed it, and what held it if it could not.</summary>
    private sealed record Entry(string Maker)
    {
        public bool Removed { get; init; }
        public string? Held { get; init; }
    }
}

/// <summary>
/// A folder a test made under the run's temp path (<see cref="TempRun.Folder"/>), removed when the test disposes it;
/// it reads as its path wherever a path is asked for.
/// </summary>
internal sealed class TempFolder : IDisposable
{
    internal TempFolder(string path) => Path = path;

    public string Path { get; }

    public void Dispose() => TempRun.Remove(Path);

    public override string ToString() => Path;

    public static implicit operator string(TempFolder folder) => folder.Path;
}

/// <summary>The fence's collection (each suite's <c>TempFenceTests</c>): after every other collection of the suite, never beside one.</summary>
[CollectionDefinition(TempRun.FenceCollection, DisableParallelization = true)]
public sealed class TempFenceCollection
{
}

/// <summary>
/// The suite's collection orderer: the runtime's own order (unstable from run to run, as xUnit's default is, so an
/// order dependence still shows), with the fence's collection moved to the end. In a suite that runs one test at a
/// time that makes the fence the last test; in one that runs collections in parallel the fence's collection also
/// disables parallelization, which runs it after every parallel collection has finished.
/// </summary>
public sealed class TempFenceLast : ITestCollectionOrderer
{
    private readonly DefaultTestCollectionOrderer _runtime = new();

    public IEnumerable<ITestCollection> OrderTestCollections(IEnumerable<ITestCollection> testCollections)
    {
        var all = _runtime.OrderTestCollections(testCollections).ToList();
        var ordered = all.Where(c => !IsFence(c)).Concat(all.Where(IsFence)).ToList();
        TempRun.Ordered(ordered.Select(c => c.DisplayName));
        return ordered;
    }

    private static bool IsFence(ITestCollection collection) => string.Equals(collection.DisplayName, TempRun.FenceCollection, StringComparison.Ordinal);
}
