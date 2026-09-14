using System.Runtime.CompilerServices;
using Avalonia;
using Avalonia.Controls;
using Avalonia.LogicalTree;
using Avalonia.Threading;
using Patterns.App.Services;
using Patterns.App.Views.Sections;

namespace Patterns.App.Views.Controls;

/// <summary>
/// A page of the desk built when it is first needed rather than when the window is. The window
/// used to build all twenty-three pages in its own constructor — four hundred kilobytes of XAML,
/// thousands of controls, before the first frame; now it builds the shell and the page on the
/// rail, draws, and then builds the rest one at a time in idle time (<see cref="WarmUp"/>), so a
/// page is still never built on entry in practice — a click during the first seconds after a
/// start is the one exception, and that click builds it, guarded. Page names are the rail's
/// headers (<see cref="ViewModels.Shell"/>).
/// </summary>
public sealed class LazyPage : ContentControl
{
    public static readonly StyledProperty<string> PageProperty = AvaloniaProperty.Register<LazyPage, string>(nameof(Page), "");

    /// <summary>The rail header of the page this control builds.</summary>
    public string Page
    {
        get => GetValue(PageProperty);
        set => SetValue(PageProperty, value);
    }

    /// <summary>Off in tests that watch the pages being built; on for the desk.</summary>
    public static bool AutoWarmUp { get; set; } = true;

    private static readonly Dictionary<string, Func<Control>> Factories = new()
    {
        ["Panel"] = () => Scroll(new ShowSection()),
        ["Cues"] = () => Scroll(new CuesSection()),
        ["Looks"] = () => Scroll(new LooksSection()),
        ["Install"] = () => Scroll(new InstallSection()),
        ["Pattern"] = () => Scroll(new PatternSection()),
        ["Media"] = () => Scroll(new MediaSection()),
        ["Overlays"] = () => Scroll(new OverlaysSection()),
        ["Lower thirds"] = () => new LowerThirdsSection(),   // pins its own preview above a scrolling page
        ["Countdown"] = () => Scroll(new CountdownSection()),
        ["Particles"] = () => Scroll(new ParticlesSection()),
        ["Fractals"] = () => Scroll(new FractalsSection()),
        ["Reactive"] = () => Scroll(new ReactiveSection()),
        ["Branding"] = () => Scroll(new BrandingSection()),
        ["Layers"] = () => Scroll(new LayersSection()),
        ["Library"] = () => new LibrarySection(),             // its own scrolling grid
        ["Assistant"] = () => Scroll(new AssistantSection()),
        ["Screens"] = () => Scroll(new OutputsSection()),
        ["Multiview"] = () => Scroll(new MultiviewSection()),
        ["Audio"] = () => Scroll(new AudioSection()),
        ["NDI"] = () => Scroll(new NdiSection()),
        ["Stream"] = () => Scroll(new StreamSection()),
        ["Remote"] = () => Scroll(new WebSection()),
        ["Interactive"] = () => Scroll(new InteractiveSection()),
        ["Nodes"] = () => Scroll(new NodesSection()),
        ["Arcade"] = () => new ArcadeSection(),               // the game fills the page; nothing scrolls
        ["Machine"] = () => Scroll(new AdminSection()),
        ["Help"] = () => Scroll(new HelpSection()),
    };

    private static Control Scroll(Control page) => new ScrollViewer { Content = page };

    /// <summary>The pages this control can build — every header on the rail but Run, which is inline.</summary>
    public static IReadOnlyCollection<string> Known => Factories.Keys;

    /// <summary>True once the page's controls exist.</summary>
    public bool IsBuilt => Content is not null;

    /// <summary>Builds the page now if it is not built: a fault in its XAML is logged and contained, and the page stays empty.</summary>
    public void Build()
    {
        if (Content is not null) return;
        if (!Factories.TryGetValue(Page, out var factory))
        {
            Content = new TextBlock { Text = $"No such page: {Page}" };
            return;
        }
        var watch = System.Diagnostics.Stopwatch.StartNew();
        UiFaults.Guard(() => Content = factory(), $"building the {Page} page");
        var ms = watch.Elapsed.TotalMilliseconds;
        BuildMs[Page] = ms;                                                              // remembered: the Machine page names the slowest, the tests read it
        AppServices.Instance?.Switches.Built(Page, ms);   // counted into the switch it was built on, if one is open; the warm-up's builds are nobody's
        PageBuilt?.Invoke(Page);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Build();   // the page the rail shows, the moment it shows
    }

    /// <summary>
    /// The pages by the logical root they hang under, kept as they attach: the window's pages are
    /// a handful, and the desk's tick reads them for its pages line, so nobody walks the window's
    /// thousands of controls to count them. The root is held weakly: a closed window and its
    /// pages go when it goes.
    /// </summary>
    private static readonly ConditionalWeakTable<ILogical, List<LazyPage>> ByRoot = new();

    protected override void OnAttachedToLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        base.OnAttachedToLogicalTree(e);
        var pages = ByRoot.GetOrCreateValue(e.Root);
        if (!pages.Contains(this)) pages.Add(this);
    }

    protected override void OnDetachedFromLogicalTree(LogicalTreeAttachmentEventArgs e)
    {
        base.OnDetachedFromLogicalTree(e);
        if (ByRoot.TryGetValue(e.Root, out var pages)) pages.Remove(this);
    }

    /// <summary>Every lazy page under a window, in the rail's order — a copy of the register, no walk of the tree.</summary>
    public static List<LazyPage> In(Window window) => ByRoot.TryGetValue(window, out var pages) ? new List<LazyPage>(pages) : new List<LazyPage>();

    /// <summary>How many of a window's pages are built.</summary>
    public static int BuiltIn(Window window) => In(window).Count(p => p.IsBuilt);

    /// <summary>How long each page took to build, ms, by name — the warm-up's and the rail's builds alike (the Machine page's line and the tests read it).</summary>
    public static IReadOnlyDictionary<string, double> BuildTimes => BuildMs;

    private static readonly Dictionary<string, double> BuildMs = new(StringComparer.Ordinal);

    /// <summary>How many times the warm-up waited for headroom instead of building.</summary>
    public static int WarmUpPauses { get; private set; }

    /// <summary>Pages the warm-up left to demand on a small machine.</summary>
    public static int WarmUpLeftToDemand { get; private set; }

    /// <summary>The machine's memory in gigabytes, for the plan: a small machine builds the likely pages alone.</summary>
    public static double MachineGB { get; set; } = GC.GetGCMemoryInfo().TotalAvailableMemoryBytes / (1024.0 * 1024 * 1024);

    /// <summary>Tests: the headroom question answered by hand instead of by the desk.</summary>
    public static Func<bool>? PauseOverride { get; set; }

    /// <summary>A page was built — the warm-up's or the rail's — by name, in the order it happened.</summary>
    public static event Action<string>? PageBuilt;

    private static readonly Queue<LazyPage> Pending = new();

    /// <summary>"Pages: 24 of 27 built · slowest Fractals 38 ms · warm-up paused twice · 3 left to demand".</summary>
    public static string Words(Window window)
    {
        var pages = In(window);
        var built = pages.Count(p => p.IsBuilt);
        var parts = new List<string> { $"Pages: {built} of {pages.Count} built" };
        var slowest = BuildMs.OrderByDescending(kv => kv.Value).FirstOrDefault();
        if (slowest.Key is not null) parts.Add($"slowest {slowest.Key} {slowest.Value:0} ms");
        if (WarmUpPauses > 0) parts.Add($"warm-up paused {(WarmUpPauses == 1 ? "once" : WarmUpPauses == 2 ? "twice" : $"{WarmUpPauses} times")}");
        if (WarmUpLeftToDemand > 0) parts.Add($"{WarmUpLeftToDemand} left to demand");
        return string.Join(" · ", parts);
    }

    /// <summary>Whether the desk has the headroom to build a page now: not while the show is on (armed with the outputs live), not with a page switch in flight, not on a stressed tick.</summary>
    private static bool ShouldPause()
    {
        if (PauseOverride is { } over) return over();
        var s = AppServices.Instance;
        if (s is null) return false;
        var armedAndLive = (s.CueStack?.Armed ?? false) && s.Outputs.IsLive;
        return Patterns.Core.Services.WarmUpPlan.ShouldPause(armedAndLive, s.Switches.IsOpen, s.DeskTick.LastMs);
    }

    /// <summary>
    /// Builds the pages not yet built, one per idle turn of the dispatcher (below input and
    /// rendering), so the first frame is not held and a click on the rail a few seconds after the
    /// start finds its page ready — the pages a show reaches for first, then the rest; a build
    /// waits a second whenever the desk has no headroom (the show on, a switch in flight, a
    /// stressed tick), and a small machine builds the likely pages alone and leaves the rest to
    /// the click that wants them.
    /// </summary>
    public static void WarmUp(Window window)
    {
        var profile = AppServices.Instance?.Profile ?? Patterns.Core.Model.NodeKind.Desk;
        var visible = In(window).Where(p => !p.IsBuilt && ViewModels.Shell.IsVisible(profile, p.Page)).ToList();   // a node never builds the pages it does not show
        var byName = visible.GroupBy(p => p.Page).ToDictionary(g => g.Key, g => g.First(), StringComparer.Ordinal);
        var order = Patterns.Core.Services.WarmUpPlan.Order(visible.Select(p => p.Page).Distinct());
        Pending.Clear();
        foreach (var name in order)
        {
            if (!Patterns.Core.Services.WarmUpPlan.BuildAhead(name, MachineGB))
            {
                WarmUpLeftToDemand++;
                continue;
            }
            Pending.Enqueue(byName[name]);
        }
        Dispatcher.UIThread.Post(ContinueWarmUp, DispatcherPriority.Background);
    }

    /// <summary>
    /// The warm-up's next step: one page built and the step after posted below input and
    /// rendering — or, with no headroom, a wait of a second before looking again. Public so a
    /// test can carry the warm-up on without the wait.
    /// </summary>
    public static void ContinueWarmUp()
    {
        while (Pending.Count > 0)
        {
            if (ShouldPause())
            {
                WarmUpPauses++;
                DispatcherTimer.RunOnce(ContinueWarmUp, Patterns.Core.Services.WarmUpPlan.Pause, DispatcherPriority.Background);
                return;
            }
            var page = Pending.Dequeue();
            if (page.IsBuilt) continue;
            page.Build();
            Dispatcher.UIThread.Post(ContinueWarmUp, DispatcherPriority.Background);
            return;
        }
    }

    /// <summary>Pages the warm-up still has to build.</summary>
    public static int WarmUpPending => Pending.Count;
}

