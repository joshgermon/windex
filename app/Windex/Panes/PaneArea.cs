// The pane area: the current window's split tree as pane cards with 6 epx gutters, 8 epx from
// the edges (design §5, amendment 2: cards sit directly on the ground). Cards are kept for every
// window of the current session so switching tabs never recreates pane content; cards of other
// tabs are collapsed, and keyboard focus is never left in (or handed to) one of them. Divider
// drags re-lay out live and send set_split_ratio (throttled). Files dragged from Explorer onto a
// card show a drop overlay on that card and are uploaded to its pane (docs/contract.md "Transfers");
// dragged text is pasted into it; other drags (a tab pill) pass over the cards untouched.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Markup;
using Microsoft.UI.Xaml.Media;
using Windex.Core;
using Windex.Terminal;
using Windows.ApplicationModel.DataTransfer;
using Windows.Foundation;
using Windows.Storage;

namespace Windex.Panes;

/// <summary>Look-and-feel inputs that come from settings and window state.</summary>
public readonly record struct PaneAreaOptions(bool WindowActive, string DimMode, bool Highlight, bool ShowHeaders, bool Animate);

/// <summary>What a drop of files on a pane would do: upload to it (Title "Upload to dasher", Detail its folder), or why not.</summary>
public readonly record struct DropOffer(bool Accepts, string Title, string? Detail);

/// <summary>Where dropped files go (the window: it uploads them).</summary>
public interface IPaneDropTarget
{
    DropOffer OfferFor(ulong pane);
    void DropItems(ulong pane, IReadOnlyList<IStorageItem> items);
    void DropPicture(ulong pane, Windows.Storage.Streams.RandomAccessStreamReference picture);
}

public sealed partial class PaneArea : Panel
{
    private const double Padding = 8;
    /// <summary>Z order: cards, then the focused card (its shadow over its neighbours), then dividers, then the drop overlay.</summary>
    private const int FocusedCardLayer = 1, DividerLayer = 2, DropLayer = 3;
    private static readonly TimeSpan SendInterval = TimeSpan.FromMilliseconds(80);

    private readonly Dictionary<ulong, PaneCard> _cards = new();
    /// <summary>The panes on screen: the current window's, or only its zoomed pane.</summary>
    private readonly HashSet<ulong> _shown = new();
    private readonly Dictionary<string, PaneSplitter> _splitters = new();
    private readonly Dictionary<string, double> _overrides = new();
    private readonly Dictionary<string, DateTime> _overrideReleased = new();
    private DateTime _lastSent;
    private ICore? _core;
    private IPaneHost? _host;
    private string? _sessionKey;
    private WindowModel? _window;
    private PaneGeometry _geometry = new();
    private bool _animate = true;
    private IPaneDropTarget? _dropTarget;
    private readonly Grid _dropOverlay;
    private readonly FontFamily _pathFont = new("Cascadia Mono, Consolas");
    /// <summary>The card the drop overlay covers (null: hidden).</summary>
    private ulong? _dropPane;
    /// <summary>What the current drag carries, read once when it enters (null: nothing to upload).</summary>
    private DragContents? _drag;
    private DataPackageView? _dragData;

    public PaneArea()
    {
        GettingFocus += OnGettingFocus;
        // Ctrl+wheel zooms the terminal font; TerminalView leaves it unhandled for us.
        PointerWheelChanged += OnPointerWheelChanged;
        _dropOverlay = CreateDropOverlay();
        Canvas.SetZIndex(_dropOverlay, DropLayer);
        Children.Add(_dropOverlay);
        AllowDrop = true;
        DragEnter += OnDragEnter;
        DragOver += OnDragOver;
        DragLeave += (_, _) => HideDropOverlay();
        Drop += OnDrop;
    }

    public void Attach(ICore core, IPaneHost host)
    {
        _core = core;
        _host = host;
        _dropTarget = host as IPaneDropTarget;
        App.Settings.Changed += (_, key) =>
        {
            if (key != "terminal") return;
            foreach (var card in _cards.Values)
                if (card.PaneContent is TerminalView view) ApplySettings(view);
        };
    }

    public PaneCard? Card(ulong pane) => _cards.GetValueOrDefault(pane);

    /// <summary>The pane's card is on screen (in the current window, and not hidden by a zoom).</summary>
    public bool IsShown(ulong pane) => _shown.Contains(pane);

    /// <summary>The one place pane content is created: a terminal over the core's pane.</summary>
    private TerminalView CreatePaneContent(ulong pane)
    {
        var view = new TerminalView { Core = _core, Pane = pane, PasteHandler = _host as IPasteHandler };
        ApplySettings(view);
        return view;
    }

    private static void ApplySettings(TerminalView view)
    {
        var s = App.Settings;
        view.TerminalFontFamily = s.TerminalFontFamily;
        view.TerminalFontSize = s.TerminalFontSize;
        view.TerminalLineHeight = s.TerminalLineHeight;
        view.CursorBlinks = s.CursorBlink;
        view.CopyOnSelect = s.CopyOnSelect;
        view.DefaultCursorShape = s.CursorShape switch
        {
            "bar" => Interop.RexCursorShape.Bar,
            "underline" => Interop.RexCursorShape.Underline,
            "hollow" => Interop.RexCursorShape.Hollow,
            _ => Interop.RexCursorShape.Block,
        };
    }

    private void OnPointerWheelChanged(object sender, PointerRoutedEventArgs e)
    {
        if (!e.KeyModifiers.HasFlag(Windows.System.VirtualKeyModifiers.Control)) return;
        int delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        if (delta == 0) return;
        _host?.AdjustFontSize(delta > 0 ? 1 : -1);
        e.Handled = true;
    }

    /// <summary>Show a window of a session (null window: show nothing).</summary>
    public void Show(string hostId, SessionModel session, WindowModel? window, PaneAreaOptions options)
    {
        string sessionKey = hostId + "/" + session.Id;
        if (sessionKey != _sessionKey)
        {
            foreach (var card in _cards.Values) DropCard(card);
            _cards.Clear();
            _sessionKey = sessionKey;
        }

        // Cards for every pane of the session; drop cards whose pane is gone.
        var alive = new HashSet<ulong>();
        foreach (var w in session.Windows)
            foreach (var p in w.Panes)
            {
                alive.Add(p.Pane);
                if (!_cards.ContainsKey(p.Pane)) _cards[p.Pane] = NewCard(p.Pane);
            }
        foreach (var gone in _cards.Keys.Where(k => !alive.Contains(k)).ToList())
        {
            DropCard(_cards[gone]);
            _cards.Remove(gone);
        }

        _window = window;
        var visible = window?.Layout?.PaneIds().ToHashSet() ?? new HashSet<ulong>();
        bool single = visible.Count <= 1;
        _shown.Clear();
        foreach (var p in window?.Panes ?? [])
            if (visible.Contains(p.Pane) && (window!.ZoomedPane is null || window.ZoomedPane == p.Pane)) _shown.Add(p.Pane);

        // Show the new cards before hiding the old ones, and move keyboard focus across first:
        // collapsing the focused card makes WinUI hand focus to whatever tab stop comes next.
        foreach (var (pane, card) in _cards)
            if (_shown.Contains(pane)) card.Visibility = Visibility.Visible;
        if (FocusedCard() is { } holder && !_shown.Contains(holder.Pane) && FocusTargetCard() is { } next)
            FocusContent(next.Pane);
        foreach (var (pane, card) in _cards)
            if (!_shown.Contains(pane)) card.Visibility = Visibility.Collapsed;

        foreach (var p in window?.Panes ?? [])
        {
            // The focused card draws above its neighbours so its shadow is not covered.
            Canvas.SetZIndex(_cards[p.Pane], window!.FocusedPane == p.Pane ? FocusedCardLayer : 0);
            _cards[p.Pane].Update(p, new PaneCardState(
                Focused: window.FocusedPane == p.Pane,
                Single: single,
                Zoomed: window.ZoomedPane == p.Pane,
                WindowActive: options.WindowActive,
                DimMode: options.DimMode,
                Highlight: options.Highlight,
                ShowHeader: options.ShowHeaders,
                Animate: options.Animate));
        }

        _animate = options.Animate;
        SyncSplitters(window);
        Relayout();
        RunTestDrop();
    }

    /// <summary>Debug builds: the --test-drop hook (PaneArea.TestDrop.cs). Release builds leave the call out.</summary>
    partial void RunTestDrop();

    /// <summary>Forget everything (no session on screen).</summary>
    public void Clear()
    {
        foreach (var card in _cards.Values) DropCard(card);
        _cards.Clear();
        _shown.Clear();
        foreach (var s in _splitters.Values) Children.Remove(s);
        _splitters.Clear();
        _sessionKey = null;
        _window = null;
        InvalidateMeasure();
    }

    /// <summary>Move keyboard focus into a shown pane's content; false when the pane is not on screen.</summary>
    public bool FocusContent(ulong pane)
    {
        if (!_shown.Contains(pane) || !_cards.TryGetValue(pane, out var card)) return false;
        var content = card.PaneContent;
        if (content.IsLoaded) return content.Focus(FocusState.Programmatic);
        // A card created in this pass: focus once it is in the tree.
        void OnLoaded(object sender, RoutedEventArgs e)
        {
            content.Loaded -= OnLoaded;
            if (_shown.Contains(pane)) content.Focus(FocusState.Programmatic);
        }
        content.Loaded += OnLoaded;
        return true;
    }

    /// <summary>
    /// Whether a directional focus move (which wraps) has somewhere to go: another pane on screen
    /// shares the pane's row (left/right) or column (up/down).
    /// </summary>
    public bool HasNeighbor(ulong pane, string direction)
    {
        if (!_geometry.Panes.TryGetValue(pane, out var from)) return false;
        bool horizontal = direction is "left" or "right";
        foreach (var (other, r) in _geometry.Panes)
        {
            if (other == pane || !_shown.Contains(other)) continue;
            if (horizontal ? r.Top < from.Bottom - 1 && r.Bottom > from.Top + 1 : r.Left < from.Right - 1 && r.Right > from.Left + 1)
                return true;
        }
        return false;
    }

    /// <summary>The split a keyboard resize (Alt+Shift+Arrow) moves, with its new ratio.</summary>
    public (string SplitId, double Ratio)? ResizeTarget(ulong pane, string direction)
    {
        if (_window?.Layout is not { } root) return null;
        string axis = direction is "left" or "right" ? "row" : "column";
        double step = direction is "right" or "down" ? 0.05 : -0.05;
        var path = new List<LayoutNode>();
        if (!PathTo(root, pane, path)) return null;
        for (int i = path.Count - 1; i >= 0; i--)
        {
            var node = path[i];
            if (node.Axis == axis && node.Children is { Count: 2 } && node.Id is not null)
            {
                double ratio = _overrides.TryGetValue(node.Id, out var o) ? o : node.Ratio;
                return (node.Id, Math.Clamp(ratio + step, 0.1, 0.9));
            }
        }
        return null;

        static bool PathTo(LayoutNode node, ulong pane, List<LayoutNode> path)
        {
            if (!node.IsSplit) return node.Pane == pane;
            path.Add(node);
            foreach (var c in node.Children!)
                if (PathTo(c.Node, pane, path)) return true;
            path.RemoveAt(path.Count - 1);
            return false;
        }
    }

    private PaneCard NewCard(ulong pane)
    {
        var view = CreatePaneContent(pane);
        var card = new PaneCard(pane, view, _host!);
        view.GotPaneFocus += (_, _) => OnContentFocused(pane);
        card.ActiveChanged += (_, active) => view.IsActive = active;
        Children.Add(card);
        return card;
    }

    /// <summary>
    /// A pane's content took keyboard focus. Only a click or Tab into a pane on screen is the user
    /// choosing it: focus WinUI handed on while cards were being hidden (Unfocused state) must not
    /// become focus_pane, which on another tab's pane switches the server's tab. GotFocus arrives
    /// asynchronously, so an event whose pane no longer holds focus is stale and ignored; acting
    /// on it would move focus back and forth between two panes.
    /// </summary>
    private void OnContentFocused(ulong pane)
    {
        if (!_shown.Contains(pane) || XamlRoot is null) return;
        var focused = FocusManager.GetFocusedElement(XamlRoot) as UIElement;
        if (focused is { FocusState: FocusState.Pointer or FocusState.Keyboard } && CardOf(focused)?.Pane == pane)
            _host?.FocusPane(pane);
    }

    /// <summary>Focus heading into a hidden card goes to the card on screen instead.</summary>
    private void OnGettingFocus(UIElement sender, GettingFocusEventArgs args)
    {
        if (CardOf(args.NewFocusedElement) is not { } card || _shown.Contains(card.Pane)) return;
        if (FocusTargetCard() is { } target && FocusableIn(target) is { } element && args.TrySetNewFocusedElement(element)) return;
        args.TryCancel();
    }

    /// <summary>The card keyboard focus belongs in: the window's focused pane, else the first one shown.</summary>
    private PaneCard? FocusTargetCard()
    {
        if (_window is { } w && _shown.Contains(w.FocusedPane) && _cards.TryGetValue(w.FocusedPane, out var card)) return card;
        return _cards.Values.FirstOrDefault(c => _shown.Contains(c.Pane));
    }

    private static DependencyObject? FocusableIn(PaneCard card) =>
        card.PaneContent is Control { IsTabStop: true } control ? control : FocusManager.FindFirstFocusableElement(card.PaneContent);

    private PaneCard? FocusedCard() =>
        XamlRoot is null ? null : CardOf(FocusManager.GetFocusedElement(XamlRoot) as DependencyObject);

    private PaneCard? CardOf(DependencyObject? element)
    {
        for (var n = element; n is not null; n = VisualTreeHelper.GetParent(n))
            if (n is PaneCard card && ReferenceEquals(card.Parent, this)) return card;
        return null;
    }

    private void DropCard(PaneCard card)
    {
        Children.Remove(card);
        (card.PaneContent as IDisposable)?.Dispose();
    }

    private void SyncSplitters(WindowModel? window)
    {
        var wanted = new Dictionary<string, bool>();
        if (window?.Layout is { } root && window.ZoomedPane is null) Collect(root, wanted);
        foreach (var id in _splitters.Keys.Where(k => !wanted.ContainsKey(k)).ToList())
        {
            Children.Remove(_splitters[id]);
            _splitters.Remove(id);
            _overrides.Remove(id);
        }
        foreach (var (id, horizontal) in wanted)
        {
            if (!_splitters.TryGetValue(id, out var splitter))
            {
                splitter = new PaneSplitter(id) { PositionReference = this };
                Canvas.SetZIndex(splitter, DividerLayer);
                splitter.Dragged += (_, at) => OnDrag(id, at);
                splitter.DragCompleted += (_, _) => OnDragCompleted(id);
                splitter.Equalize += (_, _) => { _overrides[id] = 0.5; _overrideReleased[id] = DateTime.UtcNow; _host?.SetSplitRatio(id, 0.5); Relayout(); };
                _splitters[id] = splitter;
                Children.Add(splitter);
            }
            splitter.Horizontal = horizontal;
        }

        // Drop drag overrides the server has caught up with (or that are stale).
        if (window?.Layout is { } layout)
            foreach (var id in _overrides.Keys.ToList())
            {
                if (_splitters.TryGetValue(id, out var s) && s.IsDragging) continue;
                double? model = RatioOf(layout, id);
                bool caughtUp = model is { } m && Math.Abs(m - _overrides[id]) < 0.005;
                bool stale = _overrideReleased.TryGetValue(id, out var released) && DateTime.UtcNow - released > TimeSpan.FromSeconds(2);
                if (caughtUp || stale || model is null) { _overrides.Remove(id); _overrideReleased.Remove(id); }
            }

        static void Collect(LayoutNode node, Dictionary<string, bool> into)
        {
            if (!node.IsSplit) return;
            if (node.Children!.Count == 2 && node.Id is not null) into[node.Id] = node.Axis != "column";
            foreach (var c in node.Children) Collect(c.Node, into);
        }

        static double? RatioOf(LayoutNode node, string id)
        {
            if (!node.IsSplit) return null;
            if (node.Id == id) return node.Ratio;
            foreach (var c in node.Children!)
                if (RatioOf(c.Node, id) is { } r) return r;
            return null;
        }
    }

    private void OnDrag(string id, Point at)
    {
        var split = _geometry.Splits.FirstOrDefault(s => s.Id == id);
        if (split is null) return;
        double ratio = PaneGeometry.RatioAt(split, at);
        _overrides[id] = ratio;
        _overrideReleased.Remove(id);
        Relayout();
        if (DateTime.UtcNow - _lastSent >= SendInterval)
        {
            _lastSent = DateTime.UtcNow;
            _host?.SetSplitRatio(id, ratio);
        }
    }

    private void OnDragCompleted(string id)
    {
        if (!_overrides.TryGetValue(id, out var ratio)) return;
        _overrideReleased[id] = DateTime.UtcNow;
        _host?.SetSplitRatio(id, ratio);
    }

    // ------------------------------------------------------------------ file drops

    /// <summary>
    /// What a drag carries that a pane takes: files (any folder refuses the whole drop) or a picture
    /// (no files, not text) to upload, else text to paste.
    /// </summary>
    private sealed record DragContents(int Files, int Folders, bool Text)
    {
        /// <summary>Null when the drag holds none of these (a tab pill being reordered): panes ignore it.</summary>
        public static async Task<DragContents?> ReadAsync(DataPackageView data)
        {
            if (data.Contains(StandardDataFormats.StorageItems))
            {
                var items = await data.GetStorageItemsAsync();
                int folders = items.Count(i => i.IsOfType(StorageItemTypes.Folder));
                return new DragContents(items.Count - folders, folders, false);
            }
            if (data.Contains(StandardDataFormats.Bitmap)) return new DragContents(0, 0, false);
            return data.Contains(StandardDataFormats.Text) ? new DragContents(0, 0, true) : null;
        }
    }

    private async void OnDragEnter(object sender, DragEventArgs e)
    {
        // Entering each card raises this again; the data is read once per drag.
        if (ReferenceEquals(e.DataView, _dragData)) { UpdateDrop(e); return; }
        _dragData = e.DataView;
        _drag = null;
        var deferral = e.GetDeferral();
        try { _drag = await DragContents.ReadAsync(e.DataView); }
        catch (Exception ex) when (ex is System.Runtime.InteropServices.COMException or UnauthorizedAccessException) { _drag = null; }
        finally { deferral.Complete(); }
        UpdateDrop(e);
    }

    private void OnDragOver(object sender, DragEventArgs e) => UpdateDrop(e);

    /// <summary>
    /// Over a card: files and pictures show the drop overlay (upload, or why not) and Windows' drag
    /// caption; text only gets the caption, as in Windows Terminal. A drag of anything else is left
    /// alone (not handled, no overlay).
    /// </summary>
    private void UpdateDrop(DragEventArgs e)
    {
        if (_drag is not { } drag || PaneAt(e.GetPosition(this)) is not { } pane)
        {
            HideDropOverlay();
            return;
        }
        string caption;
        if (drag.Text)
        {
            HideDropOverlay();
            e.AcceptedOperation = DataPackageOperation.Copy;
            caption = "Paste";
        }
        else
        {
            var offer = Offer(pane);
            ShowDropOverlay(pane, offer);
            e.AcceptedOperation = offer.Accepts ? DataPackageOperation.Copy : DataPackageOperation.None;
            caption = offer.Accepts && drag.Files > 1 ? $"{offer.Title} ({drag.Files} files)" : offer.Title;
        }
        e.DragUIOverride.Caption = caption;
        e.DragUIOverride.IsCaptionVisible = true;
        e.DragUIOverride.IsGlyphVisible = true;
        e.Handled = true;
    }

    private async void OnDrop(object sender, DragEventArgs e)
    {
        var drag = _drag;
        var pane = drag is null ? null : PaneAt(e.GetPosition(this));
        HideDropOverlay();
        _dragData = null;
        if (drag is null || pane is not { } target || !(drag.Text || Offer(target).Accepts)) return;
        e.AcceptedOperation = DataPackageOperation.Copy;
        e.Handled = true;
        // The data is read while the drop is deferred; a paste that asks first runs after it ends,
        // so the app the drag came from is never kept waiting on a dialog.
        string? text = null;
        var deferral = e.GetDeferral();
        try
        {
            if (drag.Text) text = await e.DataView.GetTextAsync();
            else await AcceptDropAsync(target, e.DataView);
        }
        catch (System.Runtime.InteropServices.COMException) { return; }
        finally { deferral.Complete(); }
        if (text is not null) PasteDropped(target, text);
    }

    /// <summary>Text dropped on a card is pasted into its terminal, which is focused.</summary>
    private void PasteDropped(ulong pane, string text)
    {
        if (!_cards.TryGetValue(pane, out var card) || card.PaneContent is not TerminalView view) return;
        _host?.FocusPane(pane);
        view.PasteText(text);
    }

    /// <summary>Hand dropped data to the window, which uploads it, and focus the pane the files went to.</summary>
    private async Task AcceptDropAsync(ulong pane, DataPackageView data)
    {
        if (_dropTarget is not { } target) return;
        _host?.FocusPane(pane);
        if (data.Contains(StandardDataFormats.StorageItems)) target.DropItems(pane, await data.GetStorageItemsAsync());
        else if (data.Contains(StandardDataFormats.Bitmap)) target.DropPicture(pane, await data.GetBitmapAsync());
    }

    /// <summary>The shown pane under a point of this panel.</summary>
    private ulong? PaneAt(Point at)
    {
        foreach (var (pane, rect) in _geometry.Panes)
            if (_shown.Contains(pane) && rect.Contains(at)) return pane;
        return null;
    }

    /// <summary>What dropping the dragged files or picture on a pane does, or why it can't.</summary>
    private DropOffer Offer(ulong pane) => _drag switch
    {
        { Folders: 1, Files: 0 } => new DropOffer(false, "Folders can't be uploaded", "Drop files instead."),
        { Folders: > 0 } => new DropOffer(false, "Folders can't be uploaded", "Drop only files."),
        _ => _dropTarget?.OfferFor(pane) ?? new DropOffer(false, "Can't upload here", null),
    };

    /// <summary>Cover a card with the drop overlay: what dropping there does, or why it can't.</summary>
    private void ShowDropOverlay(ulong pane, DropOffer offer)
    {
        void Set(string name, Visibility visibility) => ((UIElement)_dropOverlay.FindName(name)).Visibility = visibility;
        var yes = offer.Accepts ? Visibility.Visible : Visibility.Collapsed;
        var no = offer.Accepts ? Visibility.Collapsed : Visibility.Visible;
        Set("FillAccept", yes); Set("EdgeAccept", yes); Set("BadgeAccept", yes);
        Set("FillRefuse", no); Set("EdgeRefuse", no); Set("BadgeRefuse", no);
        ((TextBlock)_dropOverlay.FindName("DropTitle")).Text = offer.Title;
        var detail = (TextBlock)_dropOverlay.FindName("DropDetail");
        detail.Text = offer.Detail ?? "";
        // The pane's folder reads as a path; a reason reads as a sentence.
        detail.FontFamily = offer.Accepts ? _pathFont : ((TextBlock)_dropOverlay.FindName("DropTitle")).FontFamily;
        detail.Visibility = offer.Detail is null ? Visibility.Collapsed : Visibility.Visible;
        Microsoft.UI.Xaml.Automation.AutomationProperties.SetName(_dropOverlay, offer.Detail is null ? offer.Title : $"{offer.Title} · {offer.Detail}");
        _dropOverlay.OpacityTransition = _animate ? new ScalarTransition { Duration = TimeSpan.FromMilliseconds(120) } : null;
        _dropOverlay.Visibility = Visibility.Visible;
        _dropOverlay.Opacity = 1;
        if (_dropPane != pane)
        {
            _dropPane = pane;
            InvalidateMeasure();
            InvalidateArrange();
        }
    }

    private void HideDropOverlay()
    {
        // Collapsed, not just transparent: out of hit testing and the accessibility tree.
        _dropOverlay.Opacity = 0;
        _dropOverlay.Visibility = Visibility.Collapsed;
        _dropPane = null;
    }

    /// <summary>The overlay's place: over its card.</summary>
    private Rect DropRect() =>
        _dropPane is { } pane && _geometry.Panes.TryGetValue(pane, out var r) ? r : new Rect(0, 0, 0, 0);

    private static Grid CreateDropOverlay()
    {
        const string xaml = """
            <Grid xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
                  xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml"
                  IsHitTestVisible="False" Opacity="0" Visibility="Collapsed">
                <Rectangle x:Name="FillAccept" RadiusX="8" RadiusY="8" Fill="{ThemeResource AccentFillColorDefaultBrush}" Opacity="0.12" />
                <Rectangle x:Name="FillRefuse" RadiusX="8" RadiusY="8" Fill="{ThemeResource SystemFillColorCriticalBrush}" Opacity="0.10" />
                <Rectangle x:Name="EdgeAccept" RadiusX="8" RadiusY="8" StrokeThickness="2" StrokeDashArray="3,2" Stroke="{ThemeResource AccentFillColorDefaultBrush}" />
                <Rectangle x:Name="EdgeRefuse" RadiusX="8" RadiusY="8" StrokeThickness="2" StrokeDashArray="3,2" Stroke="{ThemeResource SystemFillColorCriticalBrush}" />
                <Border HorizontalAlignment="Center" VerticalAlignment="Center" Margin="16" Padding="24,18" MinWidth="220"
                        CornerRadius="8" BorderThickness="1"
                        Background="{ThemeResource AcrylicInAppFillColorDefaultBrush}"
                        BorderBrush="{ThemeResource SurfaceStrokeColorFlyoutBrush}">
                    <StackPanel Spacing="6">
                        <Grid HorizontalAlignment="Center" Margin="0,0,0,4">
                            <Border x:Name="BadgeAccept" Width="44" Height="44" CornerRadius="22" Background="{ThemeResource AccentFillColorDefaultBrush}">
                                <FontIcon Glyph="&#xE898;" FontSize="20" Foreground="{ThemeResource TextOnAccentFillColorPrimaryBrush}" />
                            </Border>
                            <Border x:Name="BadgeRefuse" Width="44" Height="44" CornerRadius="22" Background="{ThemeResource SystemFillColorCriticalBackgroundBrush}">
                                <FontIcon Glyph="&#xE7BA;" FontSize="20" Foreground="{ThemeResource SystemFillColorCriticalBrush}" />
                            </Border>
                        </Grid>
                        <TextBlock x:Name="DropTitle" FontSize="15" FontWeight="SemiBold" HorizontalAlignment="Center"
                                   TextAlignment="Center" TextWrapping="Wrap" Foreground="{ThemeResource TextFillColorPrimaryBrush}" />
                        <TextBlock x:Name="DropDetail" FontSize="12" HorizontalAlignment="Center" TextAlignment="Center"
                                   TextWrapping="Wrap" MaxLines="2" TextTrimming="CharacterEllipsis" MaxWidth="360"
                                   Foreground="{ThemeResource TextFillColorSecondaryBrush}" />
                    </StackPanel>
                </Border>
            </Grid>
            """;
        return (Grid)XamlReader.Load(xaml);
    }

    // ------------------------------------------------------------------ layout

    /// <summary>Re-run measure and arrange now (live divider drags: no easing, one layout per move).</summary>
    private void Relayout()
    {
        InvalidateMeasure();
        InvalidateArrange();
    }

    protected override Size MeasureOverride(Size availableSize)
    {
        double w = double.IsInfinity(availableSize.Width) ? 0 : availableSize.Width;
        double h = double.IsInfinity(availableSize.Height) ? 0 : availableSize.Height;
        var area = new Rect(Padding, Padding, Math.Max(0, w - 2 * Padding), Math.Max(0, h - 2 * Padding));
        _geometry = PaneGeometry.Compute(_window?.Layout, area, _window?.ZoomedPane, _overrides);
        foreach (var card in _cards.Values)
            card.Measure(_geometry.Panes.TryGetValue(card.Pane, out var r) ? new Size(r.Width, r.Height) : new Size(0, 0));
        foreach (var splitter in _splitters.Values)
        {
            var g = _geometry.Splits.FirstOrDefault(s => s.Id == splitter.SplitId);
            splitter.Measure(g is null ? new Size(0, 0) : HitRect(g).ToSize());
        }
        _dropOverlay.Measure(DropRect().ToSize());
        return new Size(w, h);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var area = new Rect(Padding, Padding, Math.Max(0, finalSize.Width - 2 * Padding), Math.Max(0, finalSize.Height - 2 * Padding));
        _geometry = PaneGeometry.Compute(_window?.Layout, area, _window?.ZoomedPane, _overrides);
        foreach (var card in _cards.Values)
            card.Arrange(_geometry.Panes.TryGetValue(card.Pane, out var r) ? r : new Rect(0, 0, 0, 0));
        foreach (var splitter in _splitters.Values)
        {
            var g = _geometry.Splits.FirstOrDefault(s => s.Id == splitter.SplitId);
            splitter.Visibility = g is null ? Visibility.Collapsed : Visibility.Visible;
            splitter.Arrange(g is null ? new Rect(0, 0, 0, 0) : HitRect(g));
        }
        _dropOverlay.Arrange(DropRect());
        return finalSize;
    }

    /// <summary>The 10 epx hit area centred on a 6 epx gutter.</summary>
    private static Rect HitRect(SplitGeometry g)
    {
        double extra = (PaneSplitter.HitWidth - PaneGeometry.Gap) / 2;
        return g.Horizontal
            ? new Rect(g.Gutter.X - extra, g.Gutter.Y, g.Gutter.Width + 2 * extra, g.Gutter.Height)
            : new Rect(g.Gutter.X, g.Gutter.Y - extra, g.Gutter.Width, g.Gutter.Height + 2 * extra);
    }
}

internal static class RectExtensions
{
    public static Size ToSize(this Rect r) => new(r.Width, r.Height);
}
