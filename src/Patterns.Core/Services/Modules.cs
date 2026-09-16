using System.Reflection;

namespace Patterns.Core.Services;

/// <summary>One assembly of the build: what it is for, what it carries that is native, and the name it loads under.</summary>
public sealed record ModuleInfo(string Name, string Assembly, string Native, string Purpose);

/// <summary>
/// The build's modules as the desk, the wire and the assistant name them — the map of the
/// assemblies, and which of them this process has loaded. The rules between them are tested
/// (the module rules test); this is the runtime's view: a timer node that never drew has
/// not loaded the render module, and says so on its Machine strip, in STATE and in a support
/// ticket, which is how a "why is it slow" starts from what is actually running.
/// </summary>
public static class Modules
{
    public static readonly IReadOnlyList<ModuleInfo> Known = new ModuleInfo[]
    {
        new("Core", "Patterns.Core", "none", "the show: the model, the snapshot, the clock, the cues, the actions, the budgets, the words"),
        new("Rendering", "Patterns.Rendering", "SkiaSharp", "everything that paints: the engine, the generators, the effects, the pools, the fence"),
        new("Ndi", "Patterns.Ndi", "the NDI runtime", "NDI in and out"),
        new("Arcade", "Patterns.Arcade", "none", "the arcade engine, the games and the audience's board"),
        new("Devices", "Patterns.Devices", "System.IO.Ports, NAudio (MIDI)", "the boxes' transports, OSC, DNS-SD and the beacon"),
        new("Audio", "Patterns.Audio", "NAudio (WASAPI)", "the DSP and the sample providers"),
        new("Assistant", "Patterns.Assistant", "the Anthropic SDK", "the assistant's brain and its model client"),
        new("Audience", "Patterns.Audience", "none", "the audience room"),
        new("Platform", "Patterns.Platform.Windows", "Windows: DXGI, QueryDisplayConfig, the registry, powrprof, WASAPI", "what Windows says about the machine — display modes, the observed signal, the EDID, the adapters, the audio endpoints, the power plan"),
        new("App", "Patterns", "Avalonia, libVLC, WebView2, PDFtoImage", "the desk, the nodes, the wire and the pages"),
    };

    /// <summary>One module as the desk reads it now: loaded or not, and the version it loaded at.</summary>
    public sealed record Row(string Name, string Version, string Native, bool Loaded, string Purpose);

    /// <summary>The names of the build's assemblies this process has loaded so far.</summary>
    public static IReadOnlySet<string> LoadedAssemblies()
    {
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            var n = a.GetName().Name;
            if (n is not null && (n == "Patterns" || n.StartsWith("Patterns.", StringComparison.Ordinal)) && !n.EndsWith(".Tests", StringComparison.Ordinal)) names.Add(n);
        }
        return names;
    }

    public static IReadOnlyList<Row> Rows()
    {
        var loaded = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        foreach (var a in AppDomain.CurrentDomain.GetAssemblies())
        {
            var n = a.GetName().Name;
            if (n is not null) loaded[n] = a;
        }
        var rows = new List<Row>(Known.Count);
        foreach (var m in Known)
        {
            var isLoaded = loaded.TryGetValue(m.Assembly, out var a);
            rows.Add(new Row(m.Name, isLoaded ? a!.GetName().Version?.ToString() ?? "dev" : "", m.Native, isLoaded, m.Purpose));
        }
        return rows;
    }

    /// <summary>"Modules loaded: Core, Rendering, Devices, Assistant, Audience, App · not loaded: Ndi, Arcade, Audio".</summary>
    public static string Words()
    {
        var rows = Rows();
        var on = rows.Where(r => r.Loaded).Select(r => r.Name);
        var off = rows.Where(r => !r.Loaded).Select(r => r.Name).ToList();
        return "Modules loaded: " + string.Join(", ", on) + (off.Count > 0 ? " · not loaded: " + string.Join(", ", off) : "");
    }
}
