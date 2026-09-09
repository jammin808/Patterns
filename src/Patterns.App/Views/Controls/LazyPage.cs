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
        ["Audio"] = () => Scroll(new AudioSection()),
        ["NDI"] = () => Scroll(new NdiSection()),
        ["Stream"] = () => Scroll(new StreamSection()),
        ["Remote"] = () => Scroll(new WebSection()),
        ["Interactive"] = () => Scroll(new InteractiveSection()),
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
        UiFaults.Guard(() => Content = factory(), $"building the {Page} page");
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        Build();   // the page the rail shows, the moment it shows
    }

    /// <summary>Every lazy page under a window, in the rail's order (the page on show is reached by two logical paths and counted once).</summary>
    public static List<LazyPage> In(Window window) => window.GetLogicalDescendants().OfType<LazyPage>().Distinct().ToList();

    /// <summary>How many of a window's pages are built.</summary>
    public static int BuiltIn(Window window) => In(window).Count(p => p.IsBuilt);

    /// <summary>
    /// Builds the pages not yet built, one per idle turn of the dispatcher (below input and
    /// rendering), so the first frame is not held and a click on the rail a few seconds after the
    /// start finds its page ready.
    /// </summary>
    public static void WarmUp(Window window)
    {
        var pending = new Queue<LazyPage>(In(window).Where(p => !p.IsBuilt));
        void Next()
        {
            while (pending.Count > 0)
            {
                var page = pending.Dequeue();
                if (page.IsBuilt) continue;
                page.Build();
                Dispatcher.UIThread.Post(Next, DispatcherPriority.Background);
                return;
            }
        }
        Dispatcher.UIThread.Post(Next, DispatcherPriority.Background);
    }
}
