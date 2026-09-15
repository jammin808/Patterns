using Avalonia;
using Avalonia.VisualTree;
using Patterns.App.Rendering;
using Patterns.App.Services;

namespace Patterns.App.Views.Controls;

/// <summary>
/// One miniature of one content target on the wall: a <see cref="SkiaCanvasControl"/> whose
/// pipeline follows the <see cref="Viewport"/> it is given (PGM or PVW side, true size,
/// scaled to fit) and redraws on every publish.
/// </summary>
public sealed class MonitorTileControl : SkiaCanvasControl
{
    public static readonly StyledProperty<PipelineViewport?> ViewportProperty =
        AvaloniaProperty.Register<MonitorTileControl, PipelineViewport?>(nameof(Viewport));

    private Action? _published;

    public PipelineViewport? Viewport
    {
        get => GetValue(ViewportProperty);
        set => SetValue(ViewportProperty, value);
    }

    static MonitorTileControl()
    {
        ViewportProperty.Changed.AddClassHandler<MonitorTileControl>((c, _) => c.Rebuild());
    }

    /// <summary>
    /// The viewport changed: the pipeline it has follows it, as an output window's does — its
    /// sink state, its caches and its running crossfade all stay. Disposing and remaking the
    /// pipeline here was the cost of every retitle (a label typed on the Screens page reaches the
    /// tile per keystroke) and of every wall rebuild.
    /// <para>
    /// A tile off the surface has no pipeline. A pipeline attaches its frame budget to the
    /// process-wide registry the glance line and the GO's clock read, and only its disposal
    /// detaches it; a tile that is not in a visual tree will never be detached from one, so a
    /// pipeline made for it here would live for the process — and hold its bus, and the whole
    /// desk behind the bus. That was the RUN monitor's tile: it sits in the Run layout, whose
    /// content is laid out — and so joins the visual tree — only when the layout is first shown,
    /// and its viewport's binding arrives at boot. Every desk the test host booted kept its Run
    /// layout closed, made one pipeline off the tree, and stayed alive on that one budget until
    /// the host was killed. The tile's pipeline is made on attach and disposed on detach, and
    /// nowhere else.
    /// </para>
    /// </summary>
    private void Rebuild()
    {
        var vp = Viewport;
        if (vp is null || AppServices.Instance is null || !this.IsAttachedToVisualTree())
        {
            Pipeline?.Dispose();
            Pipeline = null;
            return;
        }
        if (Pipeline is { } existing)
        {
            existing.Viewport = vp;
            InvalidateVisual();
            return;
        }
        Pipeline = new RenderPipeline(AppServices.Instance.Bus, vp);
    }

    protected override void OnAttachedToVisualTree(VisualTreeAttachmentEventArgs e)
    {
        base.OnAttachedToVisualTree(e);
        if (Pipeline is null) Rebuild();
        if (AppServices.Instance is { } services)
        {
            _published = NotifyChanged;
            services.SnapshotPublished += _published;
        }
    }

    protected override void OnDetachedFromVisualTree(VisualTreeAttachmentEventArgs e)
    {
        if (AppServices.Instance is { } services && _published is not null)
        {
            services.SnapshotPublished -= _published;
            _published = null;
        }
        Pipeline?.Dispose();
        Pipeline = null;
        base.OnDetachedFromVisualTree(e);
    }
}
