using System.Text.RegularExpressions;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// Round 69.4 (ADR-012): the desk is one composition root and MVVM — services built once in
/// <c>AppServices</c>' constructor order and handed to the view models; code-behind kept to layout
/// and input plumbing; the ambient <c>AppServices.Instance</c> reached only where Avalonia constructs
/// the object itself. The seams that remain are listed here by file and count, so a new reach, a new
/// handler or heavy work in a view's code-behind is a decision made in this file, not a drift —
/// the same rule the module test keeps for the assemblies.
/// </summary>
public class ArchitectureFenceTests
{
    /// <summary>Where the ambient service may be reached, and how often: objects Avalonia constructs itself, or code that runs before the desk exists.</summary>
    private static readonly Dictionary<string, int> AmbientReaches = new(StringComparer.Ordinal)
    {
        ["Program.cs"] = 1,                                   // is the desk already up, at a second launch
        ["App.axaml.cs"] = 1,                                 // the root sets it, once, after the kernel is built
        ["Views/Controls/LazyPage.cs"] = 3,                   // the switch's build count, the warm-up's headroom, the node's profile
        ["Views/Controls/MonitorTileControl.cs"] = 4,         // a tile's pipeline on the bus, attached and detached by Avalonia
        ["Services/UiFaults.cs"] = 1,                         // the crash note's folder, before anything else is up
        ["Converters/Converters.cs"] = 1,                     // a XAML converter reads the state
    };

    /// <summary>The code-behind handlers there are, by file: divider drags, the windows' key latches, the Media list's row drag, two copy buttons, two Loaded hooks.</summary>
    private static readonly Dictionary<string, int> CodeBehindHandlers = new(StringComparer.Ordinal)
    {
        ["Views/MainWindow.axaml.cs"] = 8,
        ["Views/NodeWindow.axaml.cs"] = 2,
        ["Views/RunWindow.axaml.cs"] = 1,
        ["Views/Controls/RunView.axaml.cs"] = 1,
        ["Views/Sections/AdminSection.axaml.cs"] = 2,
        ["Views/Sections/LowerThirdsSection.axaml.cs"] = 1,
        ["Views/Sections/MediaSection.axaml.cs"] = 3,
    };

    /// <summary>What a view's code-behind never does: files, threads, the network, serialisation, or an engine reached directly — every action goes through <c>ShowActions.Execute</c> with an origin.</summary>
    private static readonly (Regex Pattern, string Words)[] HeavyWork =
    {
        (new Regex(@"\bFile\.|\bDirectory\.", RegexOptions.Compiled), "reads or writes files"),
        (new Regex(@"\bTask\.Run\(|\bnew Thread\(|\bThread\.Sleep\(", RegexOptions.Compiled), "starts or blocks a thread"),
        (new Regex(@"\bHttpClient\b|\bProcess\.Start\(", RegexOptions.Compiled), "reaches the network or another process"),
        (new Regex(@"\bJsonSerializer\b|\bJsonUtil\b", RegexOptions.Compiled), "serialises"),
        (new Regex(@"\bServices\.(Video|WebIn|DeckIn|NdiIn|AudioGraph|Outputs|Persistence|Store|Twin|Metrics)\.(Open|Mount|Unmount|Reconcile|Save|Load|Start|Stop|Close|Apply|Poll)\(", RegexOptions.Compiled), "drives an engine directly"),
    };

    private static readonly Regex Handler = new(@"private\s+(?:async\s+)?void\s+\w+\(object\??\s+\w+,\s*[\w.]*EventArgs\s+\w+\)", RegexOptions.Compiled);

    private static IEnumerable<(string Relative, string Text)> AppSources(string root)
    {
        var dir = Path.Combine(root, "src", "Patterns.App");
        foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
        {
            if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
            yield return (Path.GetRelativePath(dir, file).Replace('\\', '/'), File.ReadAllText(file));
        }
    }

    [Fact]
    public void TheAmbientServiceIsReachedOnlyWhereAvaloniaConstructsTheObject()
    {
        var root = ModuleRulesTests.RepoRoot();
        if (root is null) return;                                                                 // published tests without the tree
        var found = new Dictionary<string, int>(StringComparer.Ordinal);
        foreach (var (relative, text) in AppSources(root))
        {
            if (relative == "Services/AppServices.cs") continue;                                   // the definition
            var n = Regex.Matches(text, @"\bAppServices\.Instance\b").Count;
            if (n > 0) found[relative] = n;
        }
        var offences = new List<string>();
        foreach (var (file, n) in found)
            if (!AmbientReaches.TryGetValue(file, out var allowed)) offences.Add($"{file} reaches AppServices.Instance ({n}×) — hand the services in instead, or name the seam here with why");
            else if (n != allowed) offences.Add($"{file} reaches AppServices.Instance {n}× (the fence says {allowed})");
        foreach (var file in AmbientReaches.Keys.Except(found.Keys)) offences.Add($"{file} no longer reaches AppServices.Instance — take it off the fence");
        Assert.True(offences.Count == 0, string.Join("\n", offences));
    }

    [Fact]
    public void CodeBehindIsLayoutAndInputPlumbingOnly()
    {
        var root = ModuleRulesTests.RepoRoot();
        if (root is null) return;
        var offences = new List<string>();
        var handlers = new Dictionary<string, int>(StringComparer.Ordinal);
        var files = 0;
        foreach (var (relative, text) in AppSources(root))
        {
            if (!relative.EndsWith(".axaml.cs", StringComparison.Ordinal) || relative == "App.axaml.cs") continue;   // the root builds the kernel: not a view
            files++;
            foreach (var (pattern, words) in HeavyWork)
                foreach (Match m in pattern.Matches(text))
                    offences.Add($"{relative} {words}: {m.Value}");
            var n = Handler.Matches(text).Count;
            if (n > 0) handlers[relative] = n;
        }
        Assert.True(files >= 40, $"the scan saw {files} code-behind files: the tree is not where the test thinks");
        foreach (var (file, n) in handlers)
            if (!CodeBehindHandlers.TryGetValue(file, out var allowed)) offences.Add($"{file} gained {n} code-behind handler(s) — bind a command on the view model, or name it here with why");
            else if (n != allowed) offences.Add($"{file} has {n} code-behind handlers (the fence says {allowed})");
        foreach (var file in CodeBehindHandlers.Keys.Except(handlers.Keys)) offences.Add($"{file} has no code-behind handlers any more — take it off the fence");
        Assert.True(offences.Count == 0, string.Join("\n", offences));
    }

    [Fact]
    public void TheEnginesAreBuiltInTheCompositionRootAlone()
    {
        var root = ModuleRulesTests.RepoRoot();
        if (root is null) return;
        var engines = new[] { "VideoEngine", "WebEngine", "DeckEngine", "NdiInputEngine", "AudioGraphService", "SystemMetricsService", "ResidencyService", "EyeService", "WebVideoService", "OutputService" };
        var offences = new List<string>();
        foreach (var (relative, text) in AppSources(root))
        {
            if (relative == "Services/AppServices.cs") continue;
            foreach (var engine in engines)
                if (Regex.IsMatch(text, $@"\bnew\s+{engine}\(|\b{engine}\s+\w+\s*=\s*new\(")) offences.Add($"{relative} builds {engine} itself — the kernel builds it once");
        }
        Assert.True(offences.Count == 0, string.Join("\n", offences));
    }
}
