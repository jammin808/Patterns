using System.Reflection;
using Patterns.App.Services;
using Patterns.Core.Model;
using Patterns.Core.Services;
using Xunit;

namespace Patterns.App.Tests;

/// <summary>
/// The module rules, read from the compiled assemblies and the source: the show core links
/// nothing; each edge links its own native library and the core and nothing across; only the
/// App links the UI. A rule here is a build error the next developer gets for free, which is
/// what makes the modules assemblies and not folders.
/// </summary>
public class ModuleRulesTests
{
    private static readonly string[] Natives = { "SkiaSharp", "Avalonia", "Avalonia.Base", "NAudio", "LibVLCSharp", "Anthropic", "System.IO.Ports", "Microsoft.Web.WebView2.Core", "PDFtoImage" };

    /// <summary>What each module may reference among the build's assemblies and the natives; anything of those lists not named is forbidden.</summary>
    private static readonly (Assembly Module, string[] Allowed)[] Rules =
    {
        (typeof(ShowState).Assembly, Array.Empty<string>()),
        (typeof(Patterns.Rendering.PatternEngine).Assembly, new[] { "Patterns.Core", "SkiaSharp" }),
        (typeof(Patterns.Ndi.NdiInterop).Assembly, new[] { "Patterns.Core", "Patterns.Rendering", "SkiaSharp" }),
        (typeof(Patterns.Arcade.ArcadeEngine).Assembly, new[] { "Patterns.Core", "Patterns.Rendering", "SkiaSharp" }),
        (typeof(Patterns.Devices.DeviceService).Assembly, new[] { "Patterns.Core", "System.IO.Ports", "NAudio" }),
        (typeof(Patterns.Audio.AudioRing).Assembly, new[] { "Patterns.Core", "NAudio" }),
        (typeof(Patterns.Assistant.AssistantService).Assembly, new[] { "Patterns.Core", "Anthropic" }),
        (typeof(Patterns.Audience.PlayService).Assembly, new[] { "Patterns.Core", "Patterns.Rendering", "Patterns.Arcade", "SkiaSharp" }),
        (typeof(Patterns.Platform.Windows.MachineProbe).Assembly, new[] { "Patterns.Core", "NAudio" }),
    };

    [Fact]
    public void EveryModuleReferencesOnlyWhatItsRuleAllows()
    {
        var offences = new List<string>();
        foreach (var (module, allowed) in Rules)
        {
            var name = module.GetName().Name!;
            foreach (var reference in module.GetReferencedAssemblies())
            {
                var r = reference.Name!;
                var ours = r == "Patterns" || r.StartsWith("Patterns.", StringComparison.Ordinal);
                var native = Natives.Contains(r);
                if ((ours || native) && !allowed.Contains(r)) offences.Add($"{name} → {r}");
            }
        }
        Assert.True(offences.Count == 0, "A module reaches past its rule:\n" + string.Join("\n", offences));
        Assert.DoesNotContain(typeof(ShowState).Assembly.GetReferencedAssemblies(), r => !r.Name!.StartsWith("System", StringComparison.Ordinal) && r.Name != "netstandard" && r.Name != "mscorlib");   // the core: the runtime and nothing else
    }

    [Fact]
    public void TheSourceKeepsTheUiToTheAppAndTheCanvasToTheRenderSide()
    {
        var root = RepoRoot();
        if (root is null) return;                                                                 // published tests without the tree: the compiled rule above still holds
        var offences = new List<string>();
        foreach (var project in new[] { "Patterns.Core", "Patterns.Rendering", "Patterns.Ndi", "Patterns.Arcade", "Patterns.Devices", "Patterns.Audio", "Patterns.Assistant", "Patterns.Audience", "Patterns.Platform.Windows" })
        {
            var dir = Path.Combine(root, "src", project);
            foreach (var file in Directory.EnumerateFiles(dir, "*.cs", SearchOption.AllDirectories))
            {
                if (file.Contains($"{Path.DirectorySeparatorChar}obj{Path.DirectorySeparatorChar}") || file.Contains($"{Path.DirectorySeparatorChar}bin{Path.DirectorySeparatorChar}")) continue;
                var text = File.ReadAllText(file);
                if (text.Contains("using Avalonia", StringComparison.Ordinal)) offences.Add($"{project}: {Path.GetFileName(file)} names Avalonia");
                if (project is "Patterns.Core" or "Patterns.Devices" or "Patterns.Audio" or "Patterns.Assistant" && text.Contains("using SkiaSharp", StringComparison.Ordinal)) offences.Add($"{project}: {Path.GetFileName(file)} names SkiaSharp");
                if (project is "Patterns.Core" or "Patterns.Platform.Windows" && text.Contains("using Patterns.App", StringComparison.Ordinal)) offences.Add($"{project}: {Path.GetFileName(file)} names the desk");
                if (project == "Patterns.Core" && (text.Contains("using NAudio", StringComparison.Ordinal) || text.Contains("using Anthropic", StringComparison.Ordinal) || text.Contains("using LibVLCSharp", StringComparison.Ordinal) || text.Contains("using System.IO.Ports", StringComparison.Ordinal)))
                    offences.Add($"{project}: {Path.GetFileName(file)} names a native package");
            }
        }
        Assert.True(offences.Count == 0, string.Join("\n", offences));
    }

    [Fact]
    public void TheModulesMapNamesEveryAssemblyOfTheBuildOnce()
    {
        var names = Modules.Known.Select(m => m.Assembly).ToList();
        Assert.Equal(names.Count, names.Distinct().Count());
        foreach (var (module, _) in Rules) Assert.Contains(module.GetName().Name, names);
        Assert.Contains("Patterns", names);                                                       // the App itself
        var rows = Modules.Rows();
        Assert.Equal(Modules.Known.Count, rows.Count);
        Assert.True(rows.Single(r => r.Name == "Core").Loaded);                                   // this test runs on it
        Assert.Contains("Modules loaded: Core", Modules.Words());
    }

    internal static string? RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "Patterns.sln"))) return dir.FullName;
            dir = dir.Parent;
        }
        return null;
    }
}
