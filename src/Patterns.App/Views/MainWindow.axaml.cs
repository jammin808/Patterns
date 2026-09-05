using Avalonia;
using Avalonia.Controls;
using Avalonia.Input;
using Avalonia.Interactivity;
using Avalonia.Layout;
using Avalonia.Media;
using Patterns.App.Rendering;
using Patterns.App.Services;
using Patterns.App.ViewModels;
using Patterns.App.Views.Controls;
using Patterns.Core.Media;
using Patterns.Core.Model;
using Patterns.Core.Rendering;
using Patterns.Core.Services;
using SkiaSharp;

namespace Patterns.App.Views;

public partial class MainWindow : Window
{
    private RenderPipeline? _previewPipeline;
    private RenderPipeline? _programPipeline;
    private readonly HashSet<Key> _down = new();

    public MainWindow()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        AddHandler(KeyDownEvent, OnPreviewKeyDown, RoutingStrategies.Tunnel);
        AddHandler(KeyUpEvent, OnPreviewKeyUp, RoutingStrategies.Tunnel);
        // KEYS → PAGE: the characters a key would type go to the page, never into a desk control.
        AddHandler(TextInputEvent, (_, e) => { if (DataContext is MainViewModel { KeysToPage: true }) e.Handled = true; }, RoutingStrategies.Tunnel);
        Deactivated += (_, _) => _down.Clear(); // a key-up missed during Alt+Tab must not jam a key
        DataContextChanged += (_, _) => HookShell();
    }

    private MainViewModel? _hookedVm;

    /// <summary>
    /// The page TabControl is bound two-way to the view model's page, but a refused change (leaving
    /// Run while armed) leaves the view model where it was, and a binding does not re-publish an
    /// unchanged value: the window puts the tab back itself.
    /// </summary>
    private void HookShell()
    {
        if (DataContext is not MainViewModel vm || ReferenceEquals(_hookedVm, vm)) return;
        _hookedVm = vm;
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName != nameof(MainViewModel.SelectedPageIndex)) return;
            if (Pages.SelectedIndex != vm.SelectedPageIndex) Pages.SelectedIndex = vm.SelectedPageIndex;
        };
    }

    private void OnLoaded(object? sender, RoutedEventArgs e)
    {
        if (_previewPipeline is not null) return;
        if (DataContext is not MainViewModel vm) return;

        var services = vm.Services;
        HookDeskLayout(vm);
        HookPreviewDrag();
        Services.DirectOutputService.MarkStarted(); // the desk is up: a start with the swap chain worked

        // PREVIEW (bottom): follows the selected target (own pattern or program) and the
        // sandbox while it is open — a true miniature of that target, letterboxed to fit.
        var preview = new RenderPipeline(services.Bus, PreviewViewport(vm))
        {
            ScreenIdOverride = () => services.PreviewScreenId,
        };
        _previewPipeline = preview;
        PreviewCanvas.Pipeline = preview;

        // PROGRAM (top): what the audience sees on the selected target — never the sandbox.
        // A monitor sink, so it never wears an output's identify badge or counts as an output.
        var program = new RenderPipeline(services.Bus, ProgramViewport(vm));
        _programPipeline = program;
        PgmCanvas.Pipeline = program;

        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is not (nameof(MainViewModel.SelectedTargetSize) or nameof(MainViewModel.SelectedTargetId))) return;
            preview.Viewport = PreviewViewport(vm);
            program.Viewport = ProgramViewport(vm);
            PreviewCanvas.NotifyChanged();
            PgmCanvas.NotifyChanged();
        };

        services.SnapshotPublished += () =>
        {
            PreviewCanvas.NotifyChanged();
            PgmCanvas.NotifyChanged();
        };

        Closed += (_, _) =>
        {
            _previewPipeline?.Dispose();
            _programPipeline?.Dispose();
        };
    }

    // ---- the desk's dividers: the page column, the PROGRAM/PREVIEW share, WIDE --------------------

    private DeskLayoutConfig? _desk;
    private MainViewModel? _deskVm;
    private bool _applyingDesk;

    /// <summary>The page column's width as laid out (the show's value, held back by the window's width).</summary>
    public double EditorColumnWidth => WorkArea.ColumnDefinitions[0].Width.Value;

    /// <summary>PROGRAM's share of the panes' flexible height as laid out.</summary>
    public double ProgramShareApplied
    {
        get
        {
            var pgm = SwitcherRows.RowDefinitions[1].Height.Value;
            var pvw = SwitcherRows.RowDefinitions[6].Height.Value;
            return pgm + pvw > 0 ? pgm / (pgm + pvw) : DeskLayoutConfig.DefaultProgramShare;
        }
    }

    /// <summary>The screens are reduced to a strip and the page takes the room.</summary>
    public bool IsWideApplied => WorkArea.ColumnDefinitions[0].Width.IsStar;

    private void HookDeskLayout(MainViewModel vm)
    {
        var desk = vm.State.Desk;
        if (ReferenceEquals(_desk, desk)) return;
        _desk = desk;
        _deskVm = vm;
        desk.PropertyChanged += (_, _) => ApplyDeskLayout();
        vm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.WideWorkArea) or nameof(MainViewModel.PageWantsRoom)) ApplyDeskLayout();
        };
        ApplyDeskLayout();
    }

    /// <summary>
    /// Lays the work area out from the show's desk settings. Idempotent, and re-run when the
    /// window's size changes: the page column never pushes the wall's TAKE off the window.
    /// </summary>
    public void ApplyDeskLayout()
    {
        if (_desk is null || _applyingDesk) return;
        _applyingDesk = true;
        try
        {
            var columns = WorkArea.ColumnDefinitions;
            var rows = SwitcherRows.RowDefinitions;
            // WIDE by the operator's choice, or because the page (the machine, help) wants the room.
            if (_desk.WideWorkArea || _deskVm?.PageWantsRoom == true)
            {
                columns[0].MinWidth = DeskLayoutConfig.MinEditorWidth;
                columns[0].Width = new GridLength(1, GridUnitType.Star);
                columns[2].MinWidth = 0;
                columns[2].Width = new GridLength(DeskLayoutConfig.WideScreensWidth);
            }
            else
            {
                var room = WorkArea.Bounds.Width;
                var width = _desk.EditorWidth;
                if (room > 0) width = Math.Max(DeskLayoutConfig.MinEditorWidth, Math.Min(width, room - columns[1].Width.Value - DeskLayoutConfig.MinScreensWidth));
                columns[0].MinWidth = DeskLayoutConfig.MinEditorWidth;
                columns[0].Width = new GridLength(width);
                columns[2].MinWidth = DeskLayoutConfig.MinScreensWidth;
                columns[2].Width = new GridLength(1, GridUnitType.Star);
            }
            var share = _desk.ProgramShare;
            rows[1].Height = new GridLength(share, GridUnitType.Star);
            rows[6].Height = new GridLength(1 - share, GridUnitType.Star);
        }
        finally
        {
            _applyingDesk = false;
        }
    }

    /// <summary>The page column's width, as a drag of the divider sets it; remembered in the show.</summary>
    public void SetEditorWidth(double px)
    {
        if (_desk is null) return;
        _desk.EditorWidth = px;   // clamps; the change event re-applies
        ApplyDeskLayout();
    }

    /// <summary>PROGRAM's share of the panes, as a drag of the handle sets it; remembered in the show.</summary>
    public void SetProgramShare(double share)
    {
        if (_desk is null) return;
        _desk.ProgramShare = share;
        ApplyDeskLayout();
    }

    private void OnWorkAreaSizeChanged(object? sender, SizeChangedEventArgs e) => ApplyDeskLayout();

    private void OnColumnSplitterDragCompleted(object? sender, Avalonia.Input.VectorEventArgs e)
    {
        if (_desk is null || _desk.WideWorkArea) return;
        SetEditorWidth(WorkArea.ColumnDefinitions[0].ActualWidth);
    }

    private void OnPaneHandleDragDelta(object? sender, Avalonia.Input.VectorEventArgs e)
    {
        var rows = SwitcherRows.RowDefinitions;
        var pgm = rows[1].ActualHeight;
        var pvw = rows[6].ActualHeight;
        var total = pgm + pvw;
        if (total <= 0) return;
        var share = (pgm + e.Vector.Y) / total;
        // Live while dragging: the rows follow the pointer; the show remembers it at the end.
        share = Math.Clamp(share, DeskLayoutConfig.MinProgramShare, DeskLayoutConfig.MaxProgramShare);
        rows[1].Height = new GridLength(share, GridUnitType.Star);
        rows[6].Height = new GridLength(1 - share, GridUnitType.Star);
    }

    private void OnPaneHandleDragCompleted(object? sender, Avalonia.Input.VectorEventArgs e)
        => SetProgramShare(ProgramShareApplied);

    // ---- drag on the PREVIEW pane: a layer or an overlay goes where the pointer puts it ------

    /// <summary>The PREVIEW pane's pipeline — its last frame's boxes and maths drive the drag; tests render into it directly.</summary>
    public RenderPipeline? PreviewPipeline => _previewPipeline;

    private HitRect? _dragHit;
    private SKPoint _dragStart;
    private (double X, double Y) _dragFrom;
    private bool _dragMoved;

    private void HookPreviewDrag()
    {
        PreviewCanvas.PointerPressed += (_, e) =>
        {
            if (!e.GetCurrentPoint(PreviewCanvas).Properties.IsLeftButtonPressed) return;
            var pos = e.GetPosition(PreviewCanvas);
            // PICK ON PREVIEW: the drag draws the area of interest instead of clicking or dragging anything.
            if (BeginCropPick(pos))
            {
                e.Pointer.Capture(PreviewCanvas);
                e.Handled = true;
                return;
            }
            // A press on a web page clicks into it; Alt takes hold of a web layer's box instead.
            var alt = e.KeyModifiers.HasFlag(KeyModifiers.Alt);
            if (!alt && PreviewWebPress(pos))
            {
                e.Pointer.Capture(PreviewCanvas);
                e.Handled = true;
                return;
            }
            if (!BeginPreviewDrag(pos)) return;
            e.Pointer.Capture(PreviewCanvas);
            e.Handled = true;
        };
        PreviewCanvas.PointerMoved += (_, e) =>
        {
            var pos = e.GetPosition(PreviewCanvas);
            if (_cropPicking && ReferenceEquals(e.Pointer.Captured, PreviewCanvas))
            {
                MoveCropPick(pos);
                e.Handled = true;
                return;
            }
            if (_webPress is not null && ReferenceEquals(e.Pointer.Captured, PreviewCanvas))
            {
                PreviewWebMove(pos);
                e.Handled = true;
                return;
            }
            if (_dragHit is not null && ReferenceEquals(e.Pointer.Captured, PreviewCanvas))
            {
                MovePreviewDrag(pos);
                e.Handled = true;
                return;
            }
            PreviewWebHover(pos);
        };
        PreviewCanvas.PointerReleased += (_, e) =>
        {
            if (_cropPicking)
            {
                e.Pointer.Capture(null);
                EndCropPick(e.GetPosition(PreviewCanvas));
                e.Handled = true;
                return;
            }
            if (_webPress is not null)
            {
                e.Pointer.Capture(null);
                PreviewWebRelease(e.GetPosition(PreviewCanvas));
                e.Handled = true;
                return;
            }
            if (_dragHit is null) return;
            e.Pointer.Capture(null);
            EndPreviewDrag();
            e.Handled = true;
        };
        PreviewCanvas.PointerWheelChanged += (_, e) =>
        {
            if (PreviewWebWheel(e.GetPosition(PreviewCanvas), e.Delta.Y, e.Delta.X)) e.Handled = true;
        };
        PreviewCanvas.PointerExited += (_, _) => PreviewWebLeave();
    }

    /// <summary>Takes hold of the layer or overlay under a point on the PREVIEW pane (DIPs); false when the pointer is on the picture itself (a web page included).</summary>
    public bool BeginPreviewDrag(Point dip)
    {
        if (_previewPipeline is not { LastMap: { } map } pipeline || DataContext is not MainViewModel vm) return false;
        var device = ToDevice(dip);
        var hit = HitTester.Find(pipeline.LastHits, in map, device, includeWeb: false);
        if (hit is null) return false;
        _dragHit = hit;
        _dragStart = device;
        _dragFrom = vm.DragPlaceOf(hit.Value.Kind);
        _dragMoved = false;
        return true;
    }

    /// <summary>Moves what was taken hold of: the pointer's travel becomes a share of the canvas (or of the viewport for the PiP).</summary>
    public void MovePreviewDrag(Point dip)
    {
        if (_dragHit is not { } hit || _previewPipeline is not { LastMap: { } map } || DataContext is not MainViewModel vm) return;
        var device = ToDevice(dip);
        var delta = new SKPoint(device.X - _dragStart.X, device.Y - _dragStart.Y);
        if (!_dragMoved && Math.Abs(delta.X) + Math.Abs(delta.Y) < 3) return;
        _dragMoved = true;
        double dxPct, dyPct;
        if (hit.ViewportSpace)
        {
            var t = map.TargetDelta(delta);
            dxPct = t.X * 100.0 / Math.Max(1, map.Target.Width);
            dyPct = t.Y * 100.0 / Math.Max(1, map.Target.Height);
        }
        else
        {
            var c = map.CanvasDelta(delta);
            dxPct = c.X * 100.0 / Math.Max(1, map.Canvas.Width);
            dyPct = c.Y * 100.0 / Math.Max(1, map.Canvas.Height);
        }
        vm.DragPlace(hit.Kind, _dragFrom.X + dxPct, _dragFrom.Y + dyPct);
    }

    public void EndPreviewDrag()
    {
        if (_dragHit is { } hit && _dragMoved && DataContext is MainViewModel vm)
        {
            vm.StatusMessage = $"{MainViewModel.DragName(hit.Kind)} placed — {(vm.IsSandboxActive ? "in the preview; CUT or TAKE puts it on air" : "on air")}.";
        }
        _dragHit = null;
        _dragMoved = false;
    }

    private SKPoint ToDevice(Point dip)
    {
        var scaling = RenderScaling;
        return new SKPoint((float)(dip.X * scaling), (float)(dip.Y * scaling));
    }

    // ---- the area of interest: a box drawn on the PREVIEW pane around the part to keep --------

    private bool _cropPicking;
    private Point _cropStart;

    /// <summary>PICK ON PREVIEW: a press starts the box while the pane shows an input's picture; false otherwise (the press then does what it always did).</summary>
    public bool BeginCropPick(Point dip)
    {
        if (DataContext is not MainViewModel { CropPickActive: true } || _previewPipeline is not { LastMap: not null } pipeline) return false;
        if (!pipeline.LastHits.Any(h => h.Kind == HitKind.MediaPicture)) return false;
        _cropPicking = true;
        _cropStart = dip;
        ShowCropBand(dip, dip);
        return true;
    }

    public void MoveCropPick(Point dip)
    {
        if (_cropPicking) ShowCropBand(_cropStart, dip);
    }

    /// <summary>The box becomes the area of interest — its sides as shares of the picture as the pane showed it; a box too small to mean anything is ignored. True when applied.</summary>
    public bool EndCropPick(Point dip)
    {
        if (!_cropPicking) return false;
        _cropPicking = false;
        CropBand.IsVisible = false;
        if (_previewPipeline is not { LastMap: { } map } pipeline || DataContext is not MainViewModel vm) return false;
        var hit = pipeline.LastHits.LastOrDefault(h => h.Kind == HitKind.MediaPicture);
        if (hit.Rect.Width <= 0 || hit.Rect.Height <= 0) return false;
        var a = map.ToCanvas(ToDevice(_cropStart));
        var b = map.ToCanvas(ToDevice(dip));
        var left = Math.Clamp((Math.Min(a.X, b.X) - hit.Rect.Left) / hit.Rect.Width, 0, 1);
        var right = Math.Clamp((Math.Max(a.X, b.X) - hit.Rect.Left) / hit.Rect.Width, 0, 1);
        var top = Math.Clamp((Math.Min(a.Y, b.Y) - hit.Rect.Top) / hit.Rect.Height, 0, 1);
        var bottom = Math.Clamp((Math.Max(a.Y, b.Y) - hit.Rect.Top) / hit.Rect.Height, 0, 1);
        if (right - left < 0.02 || bottom - top < 0.02)
        {
            vm.StatusMessage = "Too small a box to keep — drag around the part of the picture you want.";
            return false;
        }
        vm.ApplyCropBand(left, top, right, bottom);
        return true;
    }

    private void ShowCropBand(Point a, Point b)
    {
        Canvas.SetLeft(CropBand, Math.Min(a.X, b.X));
        Canvas.SetTop(CropBand, Math.Min(a.Y, b.Y));
        CropBand.Width = Math.Abs(a.X - b.X);
        CropBand.Height = Math.Abs(a.Y - b.Y);
        CropBand.IsVisible = true;
    }

    // ---- web pages on the PREVIEW pane: clicks, drags and the wheel go to the page ----------

    private HitRect? _webPress;
    private string _webHoverKey = "";

    private HitRect? WebHitAt(Point dip, out PaneMap map)
    {
        map = default;
        if (_previewPipeline is not { LastMap: { } m } pipeline) return null;
        map = m;
        var hit = HitTester.Find(pipeline.LastHits, in m, ToDevice(dip));
        return hit is { Kind: HitKind.WebPage } ? hit : null;
    }

    private static IWebSource? SourceOf(in HitRect hit) => InputBus.For(hit.Key) as IWebSource;

    /// <summary>A press on a page: the page gets the click and keeps the pointer until the release. False when nothing web is under the point.</summary>
    public bool PreviewWebPress(Point dip)
    {
        if (WebHitAt(dip, out var map) is not { } hit || SourceOf(in hit) is not { } page) return false;
        var at = WebPointerMap.ToPageUnbounded(in hit, map.ToCanvas(ToDevice(dip)));
        _webPress = hit;
        _webHoverKey = hit.Key;
        page.PointerDown(at.X, at.Y);
        if (DataContext is MainViewModel vm) vm.NoteWebPage(hit.Key);
        return true;
    }

    /// <summary>The pointer moving while a page holds it — a drag on a slider or a map keeps going past the box's edge.</summary>
    public void PreviewWebMove(Point dip)
    {
        if (_webPress is not { } hit || _previewPipeline is not { LastMap: { } map } || SourceOf(in hit) is not { } page) return;
        var at = WebPointerMap.ToPageUnbounded(in hit, map.ToCanvas(ToDevice(dip)));
        page.PointerMove(at.X, at.Y);
    }

    public void PreviewWebRelease(Point dip)
    {
        if (_webPress is { } hit && _previewPipeline is { LastMap: { } map } && SourceOf(in hit) is { } page)
        {
            var at = WebPointerMap.ToPageUnbounded(in hit, map.ToCanvas(ToDevice(dip)));
            page.PointerUp(at.X, at.Y);
        }
        _webPress = null;
    }

    /// <summary>The pointer passing over the pane: a page under it sees the move (hover effects, the drawn pointer); leaving a page tells it so.</summary>
    public bool PreviewWebHover(Point dip)
    {
        if (WebHitAt(dip, out var map) is { } hit && SourceOf(in hit) is { } page)
        {
            if (_webHoverKey.Length > 0 && _webHoverKey != hit.Key) (InputBus.For(_webHoverKey) as IWebSource)?.PointerLeave();
            _webHoverKey = hit.Key;
            var at = WebPointerMap.ToPageUnbounded(in hit, map.ToCanvas(ToDevice(dip)));
            page.PointerMove(at.X, at.Y);
            return true;
        }
        PreviewWebLeave();
        return false;
    }

    public void PreviewWebLeave()
    {
        if (_webHoverKey.Length == 0) return;
        (InputBus.For(_webHoverKey) as IWebSource)?.PointerLeave();
        _webHoverKey = "";
    }

    /// <summary>A wheel step over a page scrolls it (a notch up is +1, as the desk's wheel reports it).</summary>
    public bool PreviewWebWheel(Point dip, double deltaY, double deltaX)
    {
        if (WebHitAt(dip, out var map) is not { } hit || SourceOf(in hit) is not { } page) return false;
        var at = WebPointerMap.ToPageUnbounded(in hit, map.ToCanvas(ToDevice(dip)));
        if (Math.Abs(deltaY) > 0.001) page.Wheel(at.X, at.Y, (float)deltaY, horizontal: false);
        if (Math.Abs(deltaX) > 0.001) page.Wheel(at.X, at.Y, (float)deltaX, horizontal: true);
        return true;
    }

    // ---- ? TIPS: the page's explanations behind one button ---------------------------------

    /// <summary>The current page's tips — the group's line, then its prose hints under their headings.</summary>
    public IReadOnlyList<PageTip> CurrentPageTips()
    {
        if (DataContext is not MainViewModel vm) return Array.Empty<PageTip>();
        return PageTips.Collect(Pages.SelectedContent as Visual ?? Pages, vm.GroupHint);
    }

    private void OnTipsClick(object? sender, RoutedEventArgs e)
    {
        if (sender is not Control anchor || DataContext is not MainViewModel vm) return;
        var tips = CurrentPageTips();
        var header = vm.PageStrip.FirstOrDefault(c => c.IsCurrent)?.Header ?? "This page";
        var panel = new StackPanel { MaxWidth = 560 };
        panel.Children.Add(new TextBlock
        {
            Text = $"{header} — tips",
            FontSize = 16,
            FontWeight = FontWeight.SemiBold,
            Margin = new Thickness(0, 0, 0, 4),
        });
        if (tips.Count == 0)
        {
            panel.Children.Add(new TextBlock { Classes = { "tipText" }, Text = "Nothing to explain here. The Help page has the whole guide." });
        }
        var heading = "";
        foreach (var tip in tips)
        {
            if (tip.Heading.Length > 0 && tip.Heading != heading)
            {
                heading = tip.Heading;
                panel.Children.Add(new TextBlock { Classes = { "tipHead" }, Text = tip.Heading });
            }
            panel.Children.Add(new TextBlock { Classes = { "tipText" }, Text = tip.Text });
        }

        var flyout = new Flyout { Placement = PlacementMode.BottomEdgeAlignedRight };

        // The catalogue topics this page belongs to: one press opens the Help page on that card.
        var related = vm.HelpTopicsFor(header);
        if (related.Count > 0)
        {
            panel.Children.Add(new TextBlock { Classes = { "tipHead" }, Text = "IN HELP" });
            var links = new WrapPanel();
            foreach (var topic in related)
            {
                var id = topic.Id;
                var link = new Button { Classes = { "mini" }, Content = topic.Title, Margin = new Thickness(0, 0, 6, 6) };
                link.Click += (_, _) =>
                {
                    flyout.Hide();
                    vm.OpenHelpTopic(id);
                };
                links.Children.Add(link);
            }
            panel.Children.Add(links);
        }

        var footer = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 12, Margin = new Thickness(0, 12, 0, 4) };
        var inline = new CheckBox { Content = "Show hints on the pages", IsChecked = vm.ShowHints, VerticalAlignment = VerticalAlignment.Center };
        inline.IsCheckedChanged += (_, _) => vm.ShowHints = inline.IsChecked == true;
        footer.Children.Add(inline);
        var help = new Button { Classes = { "mini" }, Content = "Open Help", VerticalAlignment = VerticalAlignment.Center };
        help.Click += (_, _) =>
        {
            flyout.Hide();
            vm.SelectPage(Shell.IndexOf("Help"));
        };
        footer.Children.Add(help);
        panel.Children.Add(footer);

        flyout.Content = new ScrollViewer { Content = panel, MaxHeight = 640, Padding = new Thickness(4, 2, 12, 2) };
        flyout.ShowAt(anchor);
    }

    private static PipelineViewport ProgramViewport(MainViewModel vm)
        => PipelineViewport.Monitor(vm.SelectedTargetId, vm.SelectedTargetSize, "PGM", previewSide: false);

    private static PipelineViewport PreviewViewport(MainViewModel vm)
        => PipelineViewport.Preview with { ReferenceSize = vm.SelectedTargetSize, FitReference = true };

    private void OnPreviewKeyUp(object? sender, KeyEventArgs e) => _down.Remove(e.Key);

    /// <summary>
    /// One physical press, one action: Avalonia reports no repeat flag, so a held key arrives
    /// as a stream of KeyDowns. Only the keys this handler acts on are latched.
    /// </summary>
    private bool Latch(Key key)
    {
        if (_down.Contains(key)) return false;
        _down.Add(key);
        return true;
    }

    private void OnPreviewKeyDown(object? sender, KeyEventArgs e)
    {
        if (DataContext is not MainViewModel vm) return;
        var actions = vm.Services.Actions;

        // KEYS → PAGE: the keyboard belongs to the web page the desk drives — F5 starts a PowerPoint,
        // the arrows move a deck, k plays a YouTube video — until Ctrl+Alt+K or the chip ends it. Every
        // press goes, repeats included: a held arrow keeps a page scrolling the way a keyboard would.
        if (vm.KeysToPage)
        {
            if (e.Key == Key.K && e.KeyModifiers == (KeyModifiers.Control | KeyModifiers.Alt))
            {
                e.Handled = true;
                vm.KeysToPage = false;
                return;
            }
            if (WebKeyboard.ChordFor(e.Key, e.KeyModifiers) is { } chord)
            {
                e.Handled = true;
                vm.SendKeyToPage(chord);
                return;
            }
        }

        // The transport KeyBindings (Shift+F5–F8) fire on every repeat of a held key; the
        // first press is latched here and the repeats are swallowed before they reach them.
        if (e.KeyModifiers == KeyModifiers.Shift && e.Key is >= Key.F5 and <= Key.F8)
        {
            if (!Latch(e.Key)) e.Handled = true;
            return;
        }

        // Plain F1–F12 recall looks (transport lives on Shift+F5–F8).
        if (e.Key is >= Key.F1 and <= Key.F12 && e.KeyModifiers == KeyModifiers.None)
        {
            if (!Latch(e.Key))
            {
                e.Handled = true;
                return;
            }
            if (actions.ApplyLookHotkey(e.Key - Key.F1 + 1, ActionOrigin.Keyboard)) e.Handled = true;
            return;
        }

        var typing = FocusManager?.GetFocusedElement() is TextBox or NumericUpDown or ComboBox or AutoCompleteBox;

        // The caller's keys, only on the Run surface and never in a text box: Enter is GO on
        // the caller's stack (the gate refuses it unarmed), ↑ ↓ move standby without touching
        // output, Esc cancels a pending confirm and a second Esc within a second is STOP ALL.
        if (vm.IsRunLayout && !typing && e.KeyModifiers == KeyModifiers.None)
        {
            if (e.Key is Key.Return or Key.Enter)
            {
                e.Handled = true;
                if (Latch(e.Key)) vm.Run.Go(ActionOrigin.Keyboard);
                return;
            }
            if (e.Key is Key.Up or Key.Down)
            {
                e.Handled = true;
                if (Latch(e.Key)) vm.Services.CueStack.StandbyMove(e.Key == Key.Up ? -1 : +1);
                return;
            }
            if (e.Key == Key.Escape)
            {
                e.Handled = true;
                if (Latch(e.Key)) vm.Run.EscapePressed();
                return;
            }
        }

        // Presenter clicker: USB presentation remotes send Page Down / Page Up (and often
        // the arrow keys — those only count when the operator isn't in a control). A deck on
        // air is a click-through of its own, armed list or not.
        if ((vm.ClickerArmed || vm.DeckOnAir) && e.KeyModifiers == KeyModifiers.None)
        {
            var forward = e.Key is Key.PageDown || (!typing && e.Key is Key.Right);
            var back = e.Key is Key.PageUp || (!typing && e.Key is Key.Left);
            if (forward || back)
            {
                if (!Latch(e.Key))
                {
                    e.Handled = true;
                    return;
                }
                if (actions.PresenterAdvance(forward ? +1 : -1, ActionOrigin.Clicker)) e.Handled = true;
                return;
            }
        }

        // D is the live duck — everything but a VOG makes way for the room — latched like Space.
        if (e.Key == Key.D && !typing && e.KeyModifiers == KeyModifiers.None)
        {
            e.Handled = true;
            if (Latch(e.Key)) actions.Execute(ShowActionKind.DuckToggle, ActionOrigin.Keyboard);
            return;
        }

        // Space toggles blackout — operator muscle memory — but never while typing.
        if (e.Key != Key.Space || typing) return;
        e.Handled = true;
        if (!Latch(e.Key)) return;
        actions.Execute(ShowActionKind.BlackoutToggle, ActionOrigin.Keyboard);
    }
}
