// A pane card: header (program tile, title, activity, split/zoom/more/close) over the pane's
// content. Focus follows design §5.1 and Rex Mac: the focused card is the denser glass, lifted on a
// soft shadow and lit by a brighter hairline (an accent outline only with "Highlight focused
// pane"); the others are thinner glass with a dimmed body; a single-pane tab shows none of it.
// Pane headers may be docked (Always), float over the body on hover (On hover) or be hidden; the
// Compact pane style tiles the cards into one frame with hairline gutters.
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windex.Core;
using Windex.Services;
using Windex.Shell;
using Windows.System;

namespace Windex.Panes;

/// <summary>What a card needs from its window.</summary>
public interface IPaneHost
{
    void FocusPane(ulong pane);
    void SplitPane(ulong pane, string direction);
    void ToggleZoom(ulong pane);
    void ClosePane(ulong pane);
    void ShowPaneMenu(ulong pane, FrameworkElement anchor, Windows.Foundation.Point? position);
    void RenamePane(ulong pane, string label);
    void ReconnectPane(ulong pane);
    void SetSplitRatio(string splitId, double ratio);
    /// <summary>Ctrl+wheel over a pane: +1 bigger, -1 smaller (design §11).</summary>
    void AdjustFontSize(int step);
}

/// <summary>How a card should look right now.</summary>
public readonly record struct PaneCardState(
    bool Focused, bool Single, bool Zoomed, bool WindowActive,
    string DimMode, bool Highlight, bool ShowHeader, bool Animate);

public sealed partial class PaneCard : UserControl
{
    private const double Radius = 12, HeaderHeight = 36, CompactHeaderHeight = 30;
    /// <summary>Terminal inset: the text lines up with the header's tile (12 epx), as on Rex Mac.</summary>
    private const double PadX = 12, PadTopUnderHeader = 4, PadTop = 10, PadBottom = 8;

    private readonly IPaneHost _host;
    private readonly CardShadow _shadow;
    private readonly Microsoft.UI.Dispatching.DispatcherQueueTimer _revealTimer;

    private PaneModel? _model;
    private PaneCardState _state;
    private bool _hover;
    private bool _renaming;
    private bool? _lastActive;
    /// <summary>Pane headers: On hover. The header floats over the body and shows after a short rest at the top.</summary>
    private bool _floating;
    private bool _headerShown = true;
    private bool _compact;
    private CornerRadius _corners = new(Radius);

    public ulong Pane { get; }
    public FrameworkElement PaneContent { get; }

    /// <summary>Raised when the card's focus/activity look changes (content cursor follows it).</summary>
    public event EventHandler<bool>? ActiveChanged;

    public PaneCard(ulong pane, FrameworkElement content, IPaneHost host)
    {
        Pane = pane;
        PaneContent = content;
        _host = host;
        InitializeComponent();
        ContentHost.Content = content;
        _shadow = new CardShadow(ShadowHost, (float)Radius);
        ShadowHost.SizeChanged += (_, e) => _shadow.Resize(new System.Numerics.Vector2((float)e.NewSize.Width, (float)e.NewSize.Height));

        _revealTimer = DispatcherQueue.CreateTimer();
        _revealTimer.Interval = TimeSpan.FromMilliseconds(90);
        _revealTimer.IsRepeating = false;
        _revealTimer.Tick += (_, _) => RevealHeader(true);

        // Any press inside the card focuses its pane, even when the content handles the press
        // (except in the rename box, which keeps keyboard focus while the name is edited).
        AddHandler(PointerPressedEvent, new PointerEventHandler((_, e) =>
        {
            if (e.OriginalSource is DependencyObject source && IsInside(source, RenameBox)) return;
            _host.FocusPane(Pane);
        }), handledEventsToo: true);
        // The terminal handles pointer moves itself; the floating header still needs to see them.
        AddHandler(PointerMovedEvent, new PointerEventHandler(OnPointerMoved), handledEventsToo: true);
        Body.RightTapped += (_, e) =>
        {
            if (e.Handled) return;
            e.Handled = true;
            _host.ShowPaneMenu(Pane, Body, e.GetPosition(Body));
        };
        PointerEntered += (_, _) => { _hover = true; ApplyButtons(); };
        PointerExited += (_, _) =>
        {
            _hover = false;
            ApplyButtons();
            _revealTimer.Stop();
            if (!_renaming) RevealHeader(false);
        };
        SizeChanged += (_, _) => ApplyButtonFit();
        LayoutUpdated += (_, _) => { if (_compact) ApplyCorners(CompactCorners()); };
        ActualThemeChanged += (_, _) => { ApplyHeaderSurface(); ApplyLook(); };
    }

    public void Update(PaneModel model, PaneCardState state)
    {
        _model = model;
        _state = state;
        _compact = App.Settings.PaneStyle == "compact";
        bool wasFloating = _floating;
        _floating = state.ShowHeader && App.Settings.PaneHeaders == "hover";

        var (word, tone) = Activity.Of(model);
        Tile.IconKey = model.Icon;
        Tile.Program = model.Program;
        Tile.Monochrome = App.Settings.ColorfulIcons == "never";
        TitleText.Text = model.Title;
        AutomationProperties.SetName(this, model.Title);

        ZoomChip.Visibility = state.Zoomed ? Visibility.Visible : Visibility.Collapsed;
        ZoomButton.Content = state.Zoomed ? "\uE73F" : "\uE740";
        AutomationProperties.SetName(ZoomButton, state.Zoomed ? "Restore pane" : "Zoom pane");
        ToolTipService.SetToolTip(ZoomButton, state.Single ? "Zoom needs another pane in this tab" : state.Zoomed ? "Restore (Ctrl+Shift+Enter)" : "Zoom pane (Ctrl+Shift+Enter)");
        ZoomButton.IsEnabled = !state.Single;
        ZoomButton.Opacity = state.Single ? 0.35 : 1;

        if (model.Paused)
        {
            WordText.Text = "Input paused · Reconnecting…";
            WordText.Visibility = Visibility.Visible;
            RetryLink.Visibility = Visibility.Visible;
            tone = Tone.Caution;
        }
        else
        {
            var visible = Activity.VisibleWord(word, model.Status?.Tone) ?? (tone is Tone.Caution or Tone.Critical ? word : null);
            WordText.Text = visible ?? "";
            WordText.Visibility = visible is null ? Visibility.Collapsed : Visibility.Visible;
            RetryLink.Visibility = Visibility.Collapsed;
        }
        WordText.Foreground = Activity.WordBrush(this, tone);
        ToolTipService.SetToolTip(WordText, model.Status?.Message);

        if (model.Progress is { } progress)
        {
            Progress.Visibility = Visibility.Visible;
            Progress.IsIndeterminate = progress.Percent is null && progress.State is "working" or "indeterminate";
            Progress.Value = progress.Percent ?? 100;
            Progress.ShowPaused = progress.State == "paused";
            Progress.ShowError = progress.State == "error";
        }
        else Progress.Visibility = Visibility.Collapsed;

        double headerHeight = _compact ? CompactHeaderHeight : HeaderHeight;
        _headerShown = state.ShowHeader;
        HeaderRow.Height = new GridLength(_headerShown && !_floating ? headerHeight : 0);
        HeaderSurface.Height = headerHeight;
        HeaderSurface.Visibility = _headerShown ? Visibility.Visible : Visibility.Collapsed;
        Grid.SetRow(HeaderSurface, _floating ? 1 : 0);
        HeaderSurface.VerticalAlignment = VerticalAlignment.Top;
        if (PaneContent is Control content)
            content.Padding = new Thickness(PadX, _headerShown && !_floating ? PadTopUnderHeader : PadTop, PadX, PadBottom);

        Card.BackgroundTransition = state.Animate ? new BrushTransition { Duration = Motion.Duration(Motion.Fast) } : null;
        Dim.OpacityTransition = state.Animate ? new ScalarTransition { Duration = Motion.Duration(Motion.Fast) } : null;
        Buttons.OpacityTransition = state.Animate ? new ScalarTransition { Duration = Motion.Duration(Motion.Faster) } : null;
        HeaderSurface.OpacityTransition = state.Animate ? new ScalarTransition { Duration = Motion.Duration(Motion.Faster) } : null;

        ApplyCorners(_compact ? CompactCorners() : new CornerRadius(Radius), force: true);
        ApplyHeaderSurface();
        if (_floating && !wasFloating) RevealHeader(_renaming);
        ApplyLook();
        ApplyButtons();
        ApplyButtonFit();

        // Always tell the content on the first update (a terminal view starts out active).
        bool isActive = state.Focused && state.WindowActive;
        if (isActive != _lastActive) { _lastActive = isActive; ActiveChanged?.Invoke(this, isActive); }
    }

    private void ApplyLook()
    {
        var s = _state;
        bool distinguish = !s.Single && !s.Zoomed;
        bool dimSurface = distinguish && !s.Focused && s.DimMode is "surface" or "both";
        bool dimContent = distinguish && !s.Focused && s.DimMode is "content" or "both";
        Card.Background = ThemeLookup.Brush(this, dimSurface ? "RexPaneCardInactiveBrush" : "RexPaneCardBrush");
        bool accent = distinguish && s.Focused && s.Highlight && s.WindowActive;
        if (_compact)
        {
            // One frame of tiles: hairline gutters, no strokes or lift; focus is an accent top line.
            Card.BorderThickness = new Thickness(0);
            FocusLine.Visibility = accent ? Visibility.Visible : Visibility.Collapsed;
            _shadow.Show(lifted: false, dark: ActualTheme == ElementTheme.Dark, animate: s.Animate);
        }
        else
        {
            FocusLine.Visibility = Visibility.Collapsed;
            string stroke = accent ? "RexPaneFocusStrokeBrush"
                : distinguish && s.Focused && s.WindowActive ? "RexPaneFocusedStrokeBrush"
                : "RexPaneStrokeBrush";
            Card.BorderBrush = ThemeLookup.Brush(this, stroke);
            Card.BorderThickness = new Thickness(accent && ThemeLookup.HighContrast ? 2 : 1);
            // The lift stays while the window is inactive (only the stroke falls back); no shadows in high contrast.
            _shadow.Show(lifted: distinguish && s.Focused && !ThemeLookup.HighContrast,
                dark: ActualTheme == ElementTheme.Dark, animate: s.Animate);
        }
        Dim.Opacity = dimContent ? 1 : 0;
        TitleText.Foreground = ThemeLookup.Brush(this, s.Focused ? "RexPrimaryTextBrush" : "RexSecondaryTextBrush");
    }

    /// <summary>A docked header is part of the card; a floating one is a frosted strip over the body's top.</summary>
    private void ApplyHeaderSurface()
    {
        if (_floating)
        {
            double inner = Math.Max(0, _corners.TopLeft - 1);
            HeaderSurface.Background = ThemeLookup.Brush(this, "RexFloatingHeaderBrush");
            HeaderSurface.BorderBrush = ThemeLookup.Brush(this, "RexPaneStrokeBrush");
            HeaderSurface.BorderThickness = new Thickness(0, 0, 0, 1);
            HeaderSurface.CornerRadius = new CornerRadius(inner, Math.Max(0, _corners.TopRight - 1), 0, 0);
        }
        else
        {
            _revealTimer.Stop();
            HeaderSurface.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
            HeaderSurface.BorderThickness = new Thickness(0);
            HeaderSurface.CornerRadius = new CornerRadius(0);
            HeaderSurface.Opacity = 1;
            HeaderSurface.IsHitTestVisible = true;
        }
    }

    private void RevealHeader(bool show)
    {
        if (!_floating) return;
        HeaderSurface.Opacity = show ? 1 : 0;
        HeaderSurface.IsHitTestVisible = show;
    }

    /// <summary>On hover: resting 90 ms in the card's top band shows the header; leaving the band hides it.</summary>
    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_floating || !_headerShown) return;
        double y = e.GetCurrentPoint(Card).Position.Y;
        bool inBand = y >= 0 && y < HeaderSurface.Height;
        if (inBand)
        {
            if (HeaderSurface.Opacity < 1 && !_revealTimer.IsRunning) _revealTimer.Start();
        }
        else
        {
            _revealTimer.Stop();
            if (!_renaming) RevealHeader(false);
        }
    }

    private void ApplyButtons()
    {
        bool show = _state.Focused || _hover;
        Buttons.Opacity = !show ? 0 : _hover ? 1 : 0.75;
        Buttons.IsHitTestVisible = show;
    }

    /// <summary>Narrow headers keep only Zoom and Close (&lt; 220 epx), then only Close (&lt; 160).</summary>
    private void ApplyButtonFit()
    {
        double width = ActualWidth;
        var full = width >= 220 ? Visibility.Visible : Visibility.Collapsed;
        SplitRightButton.Visibility = full;
        SplitDownButton.Visibility = full;
        MoreButton.Visibility = full;
        ZoomButton.Visibility = width >= 160 ? Visibility.Visible : Visibility.Collapsed;
        Tile.Visibility = width >= 120 ? Visibility.Visible : Visibility.Collapsed;
    }

    // ------------------------------------------------------------------ corners

    /// <summary>Compact: only the corners on the outside of the tiled frame are rounded.</summary>
    private CornerRadius CompactCorners()
    {
        if (Parent is not Panel panel || ActualWidth <= 0) return _corners;
        double left = double.MaxValue, top = double.MaxValue, right = double.MinValue, bottom = double.MinValue;
        foreach (var child in panel.Children)
        {
            if (child is not PaneCard { Visibility: Visibility.Visible, ActualWidth: > 0 } card) continue;
            var o = card.ActualOffset;
            left = Math.Min(left, o.X); top = Math.Min(top, o.Y);
            right = Math.Max(right, o.X + card.ActualWidth); bottom = Math.Max(bottom, o.Y + card.ActualHeight);
        }
        var me = ActualOffset;
        const double Slack = 0.5;
        bool l = me.X <= left + Slack, t = me.Y <= top + Slack;
        bool r = me.X + ActualWidth >= right - Slack, b = me.Y + ActualHeight >= bottom - Slack;
        return new CornerRadius(l && t ? Radius : 0, r && t ? Radius : 0, r && b ? Radius : 0, l && b ? Radius : 0);
    }

    private void ApplyCorners(CornerRadius corners, bool force = false)
    {
        if (!force && corners == _corners) return;
        _corners = corners;
        Card.CornerRadius = corners;
        static double Inner(double r) => Math.Max(0, r - 1);
        Dim.CornerRadius = new CornerRadius(Inner(corners.TopLeft), Inner(corners.TopRight), Inner(corners.BottomRight), Inner(corners.BottomLeft));
        if (_floating) ApplyHeaderSurface();
    }

    // ------------------------------------------------------------------ inline rename (§9.5)

    public void BeginRename()
    {
        if (_model is null || !_state.ShowHeader) return;
        _renaming = true;
        RevealHeader(true);
        RenameBox.Text = _model.Title;
        TitleText.Visibility = Visibility.Collapsed;
        RenameBox.Visibility = Visibility.Visible;
        RenameBox.Focus(FocusState.Programmatic);
        RenameBox.SelectAll();
    }

    private void EndRename(bool commit)
    {
        if (!_renaming) return;
        _renaming = false;
        RenameBox.Visibility = Visibility.Collapsed;
        TitleText.Visibility = Visibility.Visible;
        if (commit && _model is not null && RenameBox.Text.Trim() is var label && label != _model.Title)
            _host.RenamePane(Pane, label);
        if (!_hover) RevealHeader(false);
        PaneContent.Focus(FocusState.Programmatic);
    }

    private void Header_DoubleTapped(object sender, DoubleTappedRoutedEventArgs e)
    {
        if (e.OriginalSource is DependencyObject source && IsInside(source, Buttons)) return;
        BeginRename();
    }

    private void RenameBox_KeyDown(object sender, KeyRoutedEventArgs e)
    {
        if (e.Key == VirtualKey.Enter) { EndRename(commit: true); e.Handled = true; }
        else if (e.Key == VirtualKey.Escape) { EndRename(commit: false); e.Handled = true; }
    }

    private void RenameBox_LostFocus(object sender, RoutedEventArgs e) => EndRename(commit: true);

    private static bool IsInside(DependencyObject node, DependencyObject container)
    {
        for (var n = node; n is not null; n = VisualTreeHelper.GetParent(n))
            if (n == container) return true;
        return false;
    }

    // ------------------------------------------------------------------ header buttons

    private void SplitRight_Click(object sender, RoutedEventArgs e) => _host.SplitPane(Pane, "right");
    private void SplitDown_Click(object sender, RoutedEventArgs e) => _host.SplitPane(Pane, "down");
    private void Zoom_Click(object sender, RoutedEventArgs e) => _host.ToggleZoom(Pane);
    private void More_Click(object sender, RoutedEventArgs e) => _host.ShowPaneMenu(Pane, MoreButton, null);
    private void Close_Click(object sender, RoutedEventArgs e) => _host.ClosePane(Pane);
    private void RetryLink_Click(object sender, RoutedEventArgs e) => _host.ReconnectPane(Pane);
}
