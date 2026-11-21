// The title row (design §3.2–3.6): identity chip, glass pill tabs (TabView), actions, the
// passthrough regions that keep the empty space draggable, responsive rules and full screen.
using Microsoft.UI.Input;
using Microsoft.UI.Windowing;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windex.Core;
using Windex.Services;
using Windex.Shell;
using Windex.Surfaces;
using Windows.Foundation;
using Windows.Graphics;

namespace Windex;

public sealed partial class MainWindow
{
    private readonly Dictionary<string, TabViewItem> _tabItems = new();
    private bool _syncingTabs;
    private RectInt32[] _passthrough = [];
    private bool _fullScreen;
    private Microsoft.UI.Dispatching.DispatcherQueueTimer? _revealTimer;

    private void SetupTitleBar()
    {
        ExtendsContentIntoTitleBar = true;
        AppWindow.TitleBar.PreferredHeightOption = TitleBarHeightOption.Tall;
        SetTitleBar(TitleRow);
        TitleRow.LayoutUpdated += (_, _) => { UpdateDragRegions(); UpdateTabTrack(); };
        Tabs.TabItemsChanged += Tabs_TabItemsChanged;
        BuildNewItemMenu();
    }

    private void RenderTitleRow(HostModel? host, SessionModel? session, WindowModel? window)
    {
        RenderIdentityChip(host, session);
        SyncTabs(host, session);
        Tabs.IsAddTabButtonVisible = session is not null && host?.IsConnected == true;
        // With no session the identity chip's chevron is the way in; a second chevron beside it reads as a duplicate.
        NewItemMenu.Visibility = session is null ? Visibility.Collapsed : Visibility.Visible;
    }

    // ------------------------------------------------------------------ identity chip (§3.3)

    private void RenderIdentityChip(HostModel? host, SessionModel? session)
    {
        var kind = host is null ? HostKinds.Get("server") : HostKinds.Get(_hosts.IconFor(host));
        ChipGlyphHost.Child = new TextBlock
        {
            Text = kind.Glyph,
            FontFamily = ProgramIcons.FontFor(kind.Source),
            FontSize = 16,
            Foreground = ThemeLookup.Brush(Root, "RexSecondaryTextBrush"),
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            IsTextScaleFactorEnabled = false,
        }.Decorative();
        ChipSession.Text = session?.Label ?? (host is null ? "No session" : "No session open");
        string trouble = host?.State switch
        {
            "reconnecting" => "Reconnecting…",
            "connecting" => "Connecting…",
            "offline" => "Offline",
            "error" => "Can't connect",
            _ => "",
        };
        if (host is null)
        {
            ChipHost.Text = _hosts.Hosts.Count == 0 ? "Add a host to begin" : "Choose a session";
            ChipHost.Foreground = ThemeLookup.Brush(Root, "RexSecondaryTextBrush");
        }
        else if (trouble.Length > 0)
        {
            ChipHost.Text = trouble;
            ChipHost.Foreground = ThemeLookup.Brush(Root, host.State == "error" ? "RexCriticalBrush" : "RexCautionBrush");
        }
        else
        {
            ChipHost.Text = $"{host.Name} · {HostDescription(host)}";
            ChipHost.Foreground = ThemeLookup.Brush(Root, "RexSecondaryTextBrush");
        }
        string name = session is null ? "No session" : host is null ? $"Session {session.Label}" : $"Session {session.Label} on {host.Name}";
        AutomationProperties.SetName(IdentityChip, name);
        ToolTipService.SetToolTip(IdentityChip, Root.ActualWidth < 720 && session is not null ? $"{session.Label} · {host?.Name}\nSwitch session (Ctrl+Shift+K)" : "Switch session (Ctrl+Shift+K)");
    }

    /// <summary>"Linux", "macOS" (from the tailnet) or the transport, for the chip's second line.</summary>
    private string HostDescription(HostModel host)
    {
        var os = _tailnet.FirstOrDefault(m => string.Equals(m.Name, host.Name, StringComparison.OrdinalIgnoreCase))?.Os;
        if (!string.IsNullOrEmpty(os)) return os.ToLowerInvariant() switch
        {
            "linux" => "Linux",
            "macos" or "darwin" => "macOS",
            "windows" => "Windows",
            _ => os,
        };
        return HostKinds.Get(_hosts.IconFor(host)).Key switch
        {
            "linux" => "Linux",
            "mini" => "macOS",
            _ => host.Kind switch { "ssh" => "SSH", "https" => "HTTPS", "http" => "HTTP", "local" or "unix" => "This PC", _ => host.Kind },
        };
    }

    private void IdentityChip_Click(object sender, RoutedEventArgs e) => ShowPalette(PaletteMode.Switcher, anchored: true);

    // ------------------------------------------------------------------ tabs (§3.4, amendment 1)

    private void SyncTabs(HostModel? host, SessionModel? session)
    {
        _syncingTabs = true;
        try
        {
            string prefix = host is null || session is null ? "" : $"{host.Id}/{session.Id}/";
            var windows = session?.Windows.OrderBy(w => w.Index).ToList() ?? [];
            var wanted = windows.Select(w => prefix + w.Id).ToHashSet();
            foreach (var stale in _tabItems.Keys.Where(k => !wanted.Contains(k)).ToList())
            {
                Tabs.TabItems.Remove(_tabItems[stale]);
                _tabItems.Remove(stale);
            }
            for (int i = 0; i < windows.Count; i++)
            {
                var w = windows[i];
                string key = prefix + w.Id;
                if (!_tabItems.TryGetValue(key, out var item))
                {
                    item = NewTabItem(host!.Id, session!.Id, w.Id);
                    _tabItems[key] = item;
                    Tabs.TabItems.Insert(Math.Min(i, Tabs.TabItems.Count), item);
                }
                else if (Tabs.TabItems.IndexOf(item) is var at && at != i)
                {
                    Tabs.TabItems.RemoveAt(at);
                    Tabs.TabItems.Insert(Math.Min(i, Tabs.TabItems.Count), item);
                }
                bool selected = w.Id == session!.ActiveWindow;
                var header = (TabPillHeader)item.Header;
                header.Update(w, session.Label, host!.Name, _settings.TabIconCount, _settings.ColorfulIcons, selected);
                AutomationProperties.SetName(item, header.AutomationName);
            }
            var active = session?.ActiveWindow is { } id && _tabItems.TryGetValue(prefix + id, out var a) ? a : null;
            if (!ReferenceEquals(Tabs.SelectedItem, active)) Tabs.SelectedItem = active;
        }
        finally { _syncingTabs = false; }
        UpdateTabSeparators();
    }

    /// <summary>
    /// Rex Mac's hairline between two unselected pills; next to the selected pill (or after the
    /// last one) there is none. Hover and selection hide it in the template's visual states.
    /// </summary>
    private void UpdateTabSeparators()
    {
        for (int i = 0; i < Tabs.TabItems.Count; i++)
        {
            if (Tabs.TabItems[i] is not TabViewItem item || FindNamed(item, "Separator") is not { } separator) continue;
            bool between = i < Tabs.TabItems.Count - 1 && !item.IsSelected && Tabs.TabItems[i + 1] is TabViewItem { IsSelected: false };
            separator.Opacity = between ? 1 : 0;
        }
    }

    private Rect _trackBounds;
    /// <summary>The TabView's scrolling list of tabs (a template part).</summary>
    private FrameworkElement? _tabStrip;

    /// <summary>The track spans the pills on screen: from the first to the last, clipped to the scrolling strip.</summary>
    private void UpdateTabTrack()
    {
        var items = Tabs.TabItems.OfType<TabViewItem>().Where(i => i.ActualWidth > 0).ToList();
        if (items.Count == 0 || Tabs.ActualWidth <= 0)
        {
            TabTrack.Visibility = Visibility.Collapsed;
            return;
        }
        // Each pill is inset 1 epx inside its item (the template's padding).
        double left = items[0].TransformToVisual(Tabs).TransformPoint(new Point(1, 0)).X;
        double right = items[^1].TransformToVisual(Tabs).TransformPoint(new Point(items[^1].ActualWidth - 1, 0)).X;
        _tabStrip ??= FindNamed(Tabs, "TabListView");
        if (_tabStrip is { ActualWidth: > 0 } strip)
        {
            var visible = strip.TransformToVisual(Tabs).TransformBounds(new Rect(0, 0, strip.ActualWidth, strip.ActualHeight));
            left = Math.Max(left, visible.Left);
            right = Math.Min(right, visible.Right);
        }
        var bounds = new Rect(Math.Round(left), 0, Math.Max(0, Math.Round(right - left)), 32);
        if (bounds == _trackBounds && TabTrack.Visibility == Visibility.Visible) return;
        _trackBounds = bounds;
        TabTrack.Margin = new Thickness(bounds.X, 0, 0, 0);
        TabTrack.Width = bounds.Width;
        TabTrack.Visibility = bounds.Width > 0 ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>A named element inside a control's template (depth-first).</summary>
    private static FrameworkElement? FindNamed(DependencyObject root, string name)
    {
        int count = VisualTreeHelper.GetChildrenCount(root);
        for (int i = 0; i < count; i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is FrameworkElement { Name: var n } element && n == name) return element;
            if (FindNamed(child, name) is { } found) return found;
        }
        return null;
    }

    private TabViewItem NewTabItem(string hostId, string sessionId, string windowId)
    {
        var header = new TabPillHeader();
        header.Renamed += (_, label) => _ = RenameTabAsync(hostId, sessionId, windowId, label);
        var item = new TabViewItem
        {
            Header = header,
            Tag = (hostId, sessionId, windowId),
            Style = (Style)Application.Current.Resources["RexPillTabStyle"],
            IsClosable = true,
            ContextFlyout = TabMenu(hostId, sessionId, windowId),
        };
        // The separator lives in the template, which exists only once the item is loaded.
        item.Loaded += (_, _) => UpdateTabSeparators();
        return item;
    }

    private static (string Host, string Session, string Window)? TabTag(object? item) =>
        (item as TabViewItem)?.Tag is ValueTuple<string, string, string> t ? t : null;

    private void Tabs_SelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        UpdateTabSeparators();
        if (_syncingTabs || TabTag(Tabs.SelectedItem) is not { } t) return;
        if (CurrentSession?.ActiveWindow == t.Window) return;
        _ = SelectTabAsync(t.Host, t.Session, t.Window);
    }

    private void Tabs_TabCloseRequested(TabView sender, TabViewTabCloseRequestedEventArgs args)
    {
        if (TabTag(args.Tab) is { } t) _ = CloseTabAsync(t.Host, t.Session, t.Window);
    }

    private void Tabs_AddTabButtonClick(TabView sender, object args) => _ = NewTabAsync();

    private void Tabs_TabDragCompleted(TabView sender, TabViewTabDragCompletedEventArgs args) => CommitTabOrder(args.Tab);

    private void Tabs_TabItemsChanged(TabView sender, Windows.Foundation.Collections.IVectorChangedEventArgs args)
    {
        if (_syncingTabs || args.CollectionChange != Windows.Foundation.Collections.CollectionChange.ItemInserted) return;
        if ((int)args.Index < Tabs.TabItems.Count) CommitTabOrder(Tabs.TabItems[(int)args.Index] as TabViewItem);
    }

    /// <summary>A tab was dragged to a new position: tell the server (move_tab).</summary>
    private void CommitTabOrder(TabViewItem? item)
    {
        if (item is null || TabTag(item) is not { } t) return;
        int index = Tabs.TabItems.IndexOf(item);
        var model = CurrentSession?.Windows.FirstOrDefault(w => w.Id == t.Window);
        if (index < 0 || model is null || model.Index == index) return;
        _ = Command("move_tab", new { host = t.Host, session = t.Session, window = t.Window, index });
    }

    // ------------------------------------------------------------------ actions cluster (§3.5)

    private void RenderActivityBadge()
    {
        int needs = 0, errors = 0;
        foreach (var (host, session, window) in AllWindows())
        {
            if (IsVisibleNow(session, window)) continue;
            var tone = Activity.Of(window).Tone;
            if (tone == Tone.Caution) needs++;
            else if (tone == Tone.Critical) errors++;
        }
        if (needs + errors == 0) { ActivityBadge.Visibility = Visibility.Collapsed; return; }
        ActivityBadge.Style = (Style)Application.Current.Resources[needs > 0 ? "CautionValueInfoBadgeStyle" : "CriticalValueInfoBadgeStyle"];
        ActivityBadge.Value = needs > 0 ? needs : errors;
        ActivityBadge.Visibility = Visibility.Visible;
        AutomationProperties.SetName(ActivityButton, needs > 0 ? $"Activity, {needs} need you" : $"Activity, {errors} with errors");
    }

    /// <summary>
    /// The taskbar button (design §3.6): the most urgent progress among the current session's tabs
    /// (error, then paused, then the least advanced percentage, then indeterminate), and a badge
    /// while a tab out of sight needs you (else has failed).
    /// </summary>
    private void RenderTaskbar(SessionModel? session)
    {
        if (!_settings.TaskbarProgress)
        {
            _taskbar.SetProgress(TaskbarState.None, 0);
            _taskbar.SetBadge(TaskbarBadge.None, "");
            return;
        }
        var progress = session?.Windows.Select(w => w.Progress).OfType<ProgressModel>().ToList() ?? [];
        var error = progress.FirstOrDefault(p => p.State == "error");
        var paused = progress.FirstOrDefault(p => p.State == "paused");
        var percent = progress.Where(p => p.Percent is not null && p.State is "working" or "indeterminate").Select(p => p.Percent!.Value).DefaultIfEmpty(-1).Min();
        if (error is not null) _taskbar.SetProgress(TaskbarState.Error, error.Percent ?? 100);
        else if (paused is not null) _taskbar.SetProgress(TaskbarState.Paused, paused.Percent ?? 100);
        else if (percent >= 0) _taskbar.SetProgress(TaskbarState.Normal, percent);
        else if (progress.Count > 0) _taskbar.SetProgress(TaskbarState.Indeterminate, 0);
        else _taskbar.SetProgress(TaskbarState.None, 0);

        int needs = 0, errors = 0;
        foreach (var (_, s, w) in AllWindows())
        {
            if (IsVisibleNow(s, w)) continue;
            var tone = Activity.Of(w).Tone;
            if (tone == Tone.Caution) needs++;
            else if (tone == Tone.Critical) errors++;
        }
        if (needs > 0) _taskbar.SetBadge(TaskbarBadge.NeedsYou, needs == 1 ? "A tab needs you" : $"{needs} tabs need you");
        else if (errors > 0) _taskbar.SetBadge(TaskbarBadge.Error, errors == 1 ? "A tab has an error" : $"{errors} tabs have errors");
        else _taskbar.SetBadge(TaskbarBadge.None, "");
    }

    private IEnumerable<(HostModel Host, SessionModel Session, WindowModel Window)> AllWindows() =>
        from h in _model.Hosts from s in h.Sessions from w in s.Windows select (h, s, w);

    private bool IsVisibleNow(SessionModel session, WindowModel window) =>
        _windowActive && _model.Current?.Session == session.Id && session.ActiveWindow == window.Id;

    private void ActivityButton_Click(object sender, RoutedEventArgs e) => ShowActivity(ActivityButton);
    private void ActivityMenuItem_Click(object sender, RoutedEventArgs e) => ShowActivity(MoreActionsButton);
    private void SearchButton_Click(object sender, RoutedEventArgs e) => ShowPalette(PaletteMode.Commands, anchored: false);
    private void SettingsButton_Click(object sender, RoutedEventArgs e) => OpenSettings(null);
    private void SidebarToggle_Click(object sender, RoutedEventArgs e) => ToggleSidebar();
    private void AddHost_Click(object sender, RoutedEventArgs e) => ShowAddHost(null);

    private void BuildNewItemMenu()
    {
        NewItemFlyout.Items.Clear();
        NewItemFlyout.Items.Add(MenuItem("New tab", "\uE710", "new-tab", () => _ = NewTabAsync(), () => CurrentSession is not null));
        NewItemFlyout.Items.Add(MenuItem("New session…", "\uE8A9", "new-session", () => _ = NewSessionAsync(null)));
        NewItemFlyout.Items.Add(new MenuFlyoutSeparator());
        NewItemFlyout.Items.Add(MenuItem("Split right", null, "split-right", () => SplitFocused("right"), () => FocusedPane is not null, path: "RexSplitRightPath"));
        NewItemFlyout.Items.Add(MenuItem("Split down", null, "split-down", () => SplitFocused("down"), () => FocusedPane is not null, path: "RexSplitDownPath"));
        NewItemFlyout.Items.Add(new MenuFlyoutSeparator());
        NewItemFlyout.Items.Add(MenuItem("Add host…", "\uE968", null, () => ShowAddHost(null)));
        NewItemFlyout.Opening += (_, _) =>
        {
            foreach (var item in NewItemFlyout.Items.OfType<MenuFlyoutItem>())
                if (item.Tag is Func<bool> enabled) item.IsEnabled = enabled();
        };
    }

    /// <summary>A menu item with a Segoe glyph (or authored path), its shortcut text and an enablement check.</summary>
    private static MenuFlyoutItem MenuItem(string text, string? glyph, string? shortcut, Action click, Func<bool>? enabled = null, string? path = null)
    {
        var item = new MenuFlyoutItem { Text = text, Tag = enabled };
        if (glyph is not null) item.Icon = new FontIcon { Glyph = glyph };
        else if (path is not null)
            item.Icon = new PathIcon { Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), Application.Current.Resources[path]) };
        if (shortcut is not null && Shortcuts.Text(shortcut) is { } keys) item.KeyboardAcceleratorTextOverride = keys;
        item.Click += (_, _) => click();
        return item;
    }

    // ------------------------------------------------------------------ drag regions (§3.2)

    /// <summary>Every interactive rectangle in the title row is passthrough; the rest drags the window.</summary>
    private void UpdateDragRegions()
    {
        if (Content.XamlRoot is not { } root) return;
        double scale = root.RasterizationScale;
        var rects = new List<RectInt32>();
        void Add(Rect r)
        {
            if (r.Width <= 0 || r.Height <= 0) return;
            rects.Add(new RectInt32((int)Math.Round(r.X * scale), (int)Math.Round(r.Y * scale), (int)Math.Round(r.Width * scale), (int)Math.Round(r.Height * scale)));
        }
        Rect Bounds(FrameworkElement e) => e.Visibility == Visibility.Visible && e.ActualWidth > 0
            ? e.TransformToVisual(null).TransformBounds(new Rect(0, 0, e.ActualWidth, e.ActualHeight))
            : Rect.Empty;

        if (TitleRow.Visibility == Visibility.Visible)
        {
            Add(Bounds(SidebarToggle));
            Add(Bounds(IdentityChip));
            // Tabs, scroll buttons, + and the new-item menu: from the strip's left edge to the menu's right edge.
            var tabs = Bounds(Tabs);
            var menu = Bounds(NewItemMenu);
            if (!tabs.IsEmpty && !menu.IsEmpty) Add(new Rect(tabs.X, tabs.Y, Math.Max(0, menu.Right - tabs.X), tabs.Height));
            Add(Bounds(Actions));
        }
        var array = rects.ToArray();
        if (!array.SequenceEqual(_passthrough))
        {
            _passthrough = array;
            InputNonClientPointerSource.GetForWindowId(AppWindow.Id).SetRegionRects(NonClientRegionKind.Passthrough, array);
        }
        // RightInset is briefly bogus while the window changes state (minimize, presenter swaps).
        double inset = _fullScreen ? 0 : AppWindow.TitleBar.RightInset / scale;
        if (double.IsFinite(inset) && inset >= 0 && Math.Abs(CaptionInset.Width.Value - inset) > 0.5) CaptionInset.Width = new GridLength(inset);
    }

    // ------------------------------------------------------------------ responsive rules (§3.2)

    private void ApplyResponsive()
    {
        double width = Root.ActualWidth;
        ChipHost.Visibility = width >= 1000 ? Visibility.Visible : Visibility.Collapsed;
        bool iconOnly = width < 720;
        ChipText.Visibility = iconOnly ? Visibility.Collapsed : Visibility.Visible;
        IdentityChip.MinWidth = iconOnly ? 40 : 140;
        IdentityChip.Width = iconOnly ? 40 : double.NaN;
        ChipGlyphHost.Margin = new Thickness(0, 0, iconOnly ? 0 : 8, 0);
        ActivityButton.Visibility = iconOnly ? Visibility.Collapsed : Visibility.Visible;
        SearchButton.Visibility = iconOnly ? Visibility.Collapsed : Visibility.Visible;
        MoreActionsButton.Visibility = iconOnly ? Visibility.Visible : Visibility.Collapsed;
        RenderIdentityChip(CurrentHost, CurrentSession);
    }

    // ------------------------------------------------------------------ full screen (§3.6)

    public void ToggleFullScreen()
    {
        _fullScreen = !_fullScreen;
        AppWindow.SetPresenter(_fullScreen ? AppWindowPresenterKind.FullScreen : AppWindowPresenterKind.Overlapped);
        TitleRowDefinition.Height = new GridLength(_fullScreen ? 0 : 48);
        HideTitleOverlay();
        TitleRow.Visibility = _fullScreen ? Visibility.Collapsed : Visibility.Visible;
        FullScreenHotZone.Visibility = _fullScreen ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FullScreenHotZone_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        var timer = DispatcherQueue.CreateTimer();
        timer.Interval = TimeSpan.FromMilliseconds(300);
        timer.IsRepeating = false;
        timer.Tick += (_, _) => { if (_revealTimer == timer) ShowTitleOverlay(); };
        _revealTimer = timer;
        timer.Start();
    }

    private void FullScreenHotZone_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if (TitleRow.Visibility != Visibility.Visible) _revealTimer = null;
    }

    private void ShowTitleOverlay()
    {
        if (!_fullScreen) return;
        Grid.SetRow(TitleRow, 1);
        TitleRow.VerticalAlignment = VerticalAlignment.Top;
        TitleRow.Background = ThemeLookup.Brush(Root, "RexOverlayBrush");
        TitleRow.Visibility = Visibility.Visible;
        TitleRow.PointerExited += TitleOverlay_PointerExited;
    }

    private void TitleOverlay_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        var p = e.GetCurrentPoint(TitleRow).Position;
        if (p.Y < TitleRow.ActualHeight - 1 && p.Y > 0) return;
        HideTitleOverlay();
        if (_fullScreen) TitleRow.Visibility = Visibility.Collapsed;
    }

    private void HideTitleOverlay()
    {
        TitleRow.PointerExited -= TitleOverlay_PointerExited;
        Grid.SetRow(TitleRow, 0);
        TitleRow.VerticalAlignment = VerticalAlignment.Stretch;
        TitleRow.Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _revealTimer = null;
    }

}
