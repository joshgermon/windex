// The palette's rows (switcher and commands, design §9.1) and the Activity flyout (§9.8).
using System.Numerics;
using CommunityToolkit.WinUI.Controls;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Media;
using Windex.Core;
using Windex.Services;
using Windex.Shell;
using Windex.Surfaces;

namespace Windex;

public sealed partial class MainWindow : IPaletteSource
{
    private RexPalette? _palette;
    private readonly Dictionary<string, (Tone Tone, DateTime Since)> _activitySince = new();

    // ------------------------------------------------------------------ palette popup

    private void ShowPalette(PaletteMode mode, bool anchored)
    {
        if (_dialogOpen) return;
        if (_palette is null)
        {
            _palette = new RexPalette();
            _palette.CloseRequested += (_, _) => PalettePopup.IsOpen = false;
            PalettePopup.Closed += (_, _) => { if (FocusedPane is { } pane) PaneArea.FocusContent(pane); };
            PalettePopup.Child = _palette;
        }
        if (PalettePopup.IsOpen && _palette.Mode == mode) { PalettePopup.IsOpen = false; return; }

        double width = anchored ? 360 : Math.Min(600, Root.ActualWidth - 32);
        _palette.Width = width;
        _palette.MaxHeight = Math.Min(520, Root.ActualHeight * 0.7);
        if (anchored && IdentityChip.ActualWidth > 0)
        {
            var at = IdentityChip.TransformToVisual(Root).TransformPoint(new Windows.Foundation.Point(0, IdentityChip.ActualHeight + 4));
            PalettePopup.HorizontalOffset = at.X;
            PalettePopup.VerticalOffset = at.Y;
        }
        else
        {
            PalettePopup.HorizontalOffset = Math.Max(16, (Root.ActualWidth - width) / 2);
            PalettePopup.VerticalOffset = 56;
        }

        var surface = (Border)_palette.Content;
        surface.Shadow ??= new ThemeShadow();
        bool motion = Motion.Enabled;
        surface.OpacityTransition = null;
        surface.TranslationTransition = null;
        surface.Opacity = motion ? 0 : 1;
        surface.Translation = new Vector3(0, motion ? -8 : 0, 32);
        _palette.RequestedTheme = Root.ActualTheme;
        PalettePopup.IsOpen = true;
        _palette.Open(mode, this);
        if (motion)
            DispatcherQueue.TryEnqueue(() =>
            {
                surface.OpacityTransition = new ScalarTransition { Duration = Motion.Duration(Motion.Fast) };
                surface.TranslationTransition = new Vector3Transition { Duration = Motion.Duration(Motion.Fast) };
                surface.Opacity = 1;
                surface.Translation = new Vector3(0, 0, 32);
            });
    }

    // ------------------------------------------------------------------ rows (IPaletteSource)

    public IReadOnlyList<PaletteGroup> Build(PaletteMode mode, string query) =>
        mode == PaletteMode.Switcher ? SwitcherRows(query) : CommandRows(query);

    private IReadOnlyList<PaletteGroup> SwitcherRows(string query)
    {
        var groups = new List<PaletteGroup>();
        var current = _model.Current;
        var targetHost = CurrentHost is { IsConnected: true } ch ? ch : _model.Hosts.FirstOrDefault(h => h.IsConnected);

        if (query.Length > 0 && targetHost is not null
            && !targetHost.Sessions.Any(s => string.Equals(s.Label, query, StringComparison.OrdinalIgnoreCase)))
        {
            string label = query;
            var host = targetHost;
            groups.Add(new PaletteGroup("", null,
            [
                new PaletteItem($"Create session \"{label}\" on {host.Name}", () => _ = CreateSessionAsync(host.Id, label, null))
                    { Glyph = "\uE710", IsCreate = true, Keys = new KeyChord(Windows.System.VirtualKeyModifiers.Control, Windows.System.VirtualKey.Enter) },
            ]));
        }

        foreach (var host in _model.Hosts)
        {
            var h = host;
            var kind = HostKinds.Get(_hosts.IconFor(h));
            bool hostMatches = query.Length > 0 && RexPalette.Matches(h.Name, query);
            var rows = new List<PaletteItem>();
            foreach (var session in h.Sessions)
            {
                var s = session;
                if (!hostMatches && !RexPalette.Matches(s.Label, query)) continue;
                var active = s.Windows.FirstOrDefault(w => w.Id == s.ActiveWindow) ?? s.Windows.FirstOrDefault();
                var focused = active?.Panes.FirstOrDefault(p => p.Pane == active.FocusedPane) ?? active?.Panes.FirstOrDefault();
                var tone = Activity.ToneOf(s.Word, s.Tone);
                string tabs = s.Windows.Count == 1 ? "1 tab" : $"{s.Windows.Count} tabs";
                string? word = h.IsConnected ? Activity.VisibleWord(s.Word, s.Tone) : h.State switch { "reconnecting" => "Reconnecting", "connecting" => "Connecting", _ => "Offline" };
                rows.Add(new PaletteItem(s.Label, () => _ = OpenSessionAsync(h.Id, s.Id))
                {
                    Secondary = $"{tabs} · {h.Name}",
                    Word = word,
                    Tone = h.IsConnected ? tone : Tone.Caution,
                    IconKey = focused?.Icon ?? "shell",
                    Program = focused?.Program,
                    Badge = tone,
                    Current = current?.Host == h.Id && current.Session == s.Id,
                    Enabled = h.IsConnected,
                });
            }
            if (rows.Count > 0) groups.Add(new PaletteGroup(h.Name, kind, rows));
        }

        if (query.Length > 0)
        {
            var tabs = AllWindows()
                .Where(x => x.Host.IsConnected && RexPalette.Matches(TabLabel(x.Window), query))
                .Select(x =>
                {
                    var (h, s, w) = x;
                    var focused = w.Panes.FirstOrDefault(p => p.Pane == w.FocusedPane) ?? w.Panes.FirstOrDefault();
                    var (word, tone, _) = Activity.Of(w);
                    return new PaletteItem(TabLabel(w), () => _ = SelectTabAsync(h.Id, s.Id, w.Id))
                    {
                        Secondary = $"{s.Label} · {h.Name}",
                        IconKey = focused?.Icon ?? "shell",
                        Program = focused?.Program,
                        Badge = tone,
                        Word = Activity.VisibleWord(word, tone == Tone.Caution ? "warning" : tone == Tone.Critical ? "danger" : null),
                        Tone = tone,
                    };
                }).ToList();
            if (tabs.Count > 0) groups.Add(new PaletteGroup("Tabs", null, tabs));
        }

        var fixedRows = new List<PaletteItem>
        {
            new("New session…", () => _ = NewSessionAsync(null)) { Glyph = "\uE8A9", Keys = Shortcuts.Primary("new-session"), Enabled = _model.Hosts.Any(h => h.IsConnected) },
            new("Add host…", () => ShowAddHost(null)) { Glyph = "\uE968" },
        };
        groups.Add(new PaletteGroup(query.Length == 0 ? "" : "Actions", null, fixedRows.Where(r => RexPalette.Matches(r.Label, query)).ToList()));
        return groups;
    }

    private IReadOnlyList<PaletteGroup> CommandRows(string query)
    {
        bool session = CurrentSession is not null && CurrentHost?.IsConnected == true;
        bool window = CurrentWindow is not null && session;
        bool pane = FocusedPane is not null && window;
        bool multi = CurrentWindow is { Panes.Count: > 1 } && window;
        string? hostName = CurrentHost?.Name;

        PaletteItem Cmd(string label, string glyph, string action, bool enabled = true, bool chevron = false, string? path = null) =>
            new(label, () => Run(action)) { Glyph = glyph, PathKey = path, Keys = Shortcuts.Primary(action), Enabled = enabled, Chevron = chevron };

        var groups = new List<PaletteGroup>
        {
            new("Session", null,
            [
                Cmd("Switch session…", "\uE8AB", "switch-session", chevron: true),
                Cmd("New session…", "\uE8A9", "new-session", _model.Hosts.Any(h => h.IsConnected)),
                Cmd("Rename session…", "\uE8AC", "rename-session", session),
                Cmd("Next session", "\uE76C", "next-session", _model.Hosts.Sum(h => h.Sessions.Count) > 1),
                Cmd("Previous session", "\uE76B", "prev-session", _model.Hosts.Sum(h => h.Sessions.Count) > 1),
                Cmd("Close session…", "\uE711", "close-session", session),
            ]),
            new("Tab", null,
            [
                Cmd("New tab", "\uE710", "new-tab", session),
                Cmd("Rename tab", "\uE8AC", "rename-tab", window),
                Cmd("Next tab", "\uE76C", "next-tab", CurrentSession is { Windows.Count: > 1 }),
                Cmd("Previous tab", "\uE76B", "prev-tab", CurrentSession is { Windows.Count: > 1 }),
                Cmd("Close tab…", "\uE711", "close-tab", window),
            ]),
            new("Pane", null,
            [
                Cmd("Split right", "", "split-right", pane, path: "RexSplitRightPath"),
                Cmd("Split down", "", "split-down", pane, path: "RexSplitDownPath"),
                Cmd(CurrentWindow?.ZoomedPane is not null ? "Restore pane" : "Zoom pane", CurrentWindow?.ZoomedPane is not null ? "\uE73F" : "\uE740", "zoom-pane", multi),
                Cmd("Balance panes", "\uE9A6", "balance-panes", multi),
                Cmd("Find in terminal…", "\uE721", "find", pane),
                Cmd("Rename pane…", "\uE8AC", "rename-pane", pane),
                Cmd("Close pane…", "\uE711", "close-pane", pane),
            ]),
            new("Host", null,
            [
                Cmd("Add host…", "\uE968", "add-host"),
                Cmd(hostName is null ? "Reconnect host" : $"Reconnect {hostName}", "\uE72C", "reconnect-host", CurrentHost is not null),
                Cmd("Refresh hosts", "\uE895", "refresh-hosts"),
            ]),
            new("View", null,
            [
                Cmd("Toggle sidebar", "\uE90C", "toggle-sidebar"),
                Cmd("Activity", "\uEA8F", "activity"),
                Cmd("Full screen", "\uE740", "fullscreen"),
            ]),
            new("App", null,
            [
                Cmd("Settings", "\uE713", "settings"),
                new PaletteItem("Keyboard shortcuts", () => OpenSettings("shortcuts")) { Glyph = "\uEDA7" },
                new PaletteItem("About Windex", () => OpenSettings("about")) { Glyph = "\uE946" },
            ]),
        };
        if (query.Length == 0) return groups;
        return groups.Select(g => g with { Items = g.Items.Where(i => RexPalette.Matches(i.Label, query) || RexPalette.Matches(g.Header + " " + i.Label, query)).ToList() }).ToList();
    }

    // ------------------------------------------------------------------ activity flyout (§9.8)

    /// <summary>Track when each tab entered its current state (elapsed times in Activity).</summary>
    private void TrackActivity()
    {
        var seen = new HashSet<string>();
        foreach (var (h, s, w) in AllWindows())
        {
            string key = $"{h.Id}/{s.Id}/{w.Id}";
            seen.Add(key);
            var tone = Activity.Of(w).Tone;
            if (!_activitySince.TryGetValue(key, out var entry) || entry.Tone != tone) _activitySince[key] = (tone, DateTime.UtcNow);
        }
        foreach (var gone in _activitySince.Keys.Where(k => !seen.Contains(k)).ToList()) _activitySince.Remove(gone);
    }

    private void ShowActivity(FrameworkElement anchor)
    {
        var flyout = new Flyout { Placement = FlyoutPlacementMode.BottomEdgeAlignedRight };
        var presenterStyle = new Style(typeof(FlyoutPresenter));
        presenterStyle.Setters.Add(new Setter(FlyoutPresenter.PaddingProperty, new Thickness(0)));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.MaxWidthProperty, 420.0));
        presenterStyle.Setters.Add(new Setter(FrameworkElement.MaxHeightProperty, 480.0));
        presenterStyle.Setters.Add(new Setter(FlyoutPresenter.CornerRadiusProperty, Application.Current.Resources["OverlayCornerRadius"]));
        flyout.FlyoutPresenterStyle = presenterStyle;

        bool bySession = false;
        string filter = "";
        var list = new ListView { SelectionMode = ListViewSelectionMode.None, IsItemClickEnabled = true, Padding = new Thickness(4, 0, 4, 4) };
        var empty = new TextBlock { Text = "Nothing needs you.", FontSize = 13, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(0, 24, 0, 28), Foreground = ThemeLookup.Brush(Root, "RexSecondaryTextBrush") };
        var segmented = new Segmented { SelectedIndex = 0 };
        segmented.Items.Add(new SegmentedItem { Content = "By priority" });
        segmented.Items.Add(new SegmentedItem { Content = "By session" });
        var search = new AutoSuggestBox { PlaceholderText = "Filter", QueryIcon = new SymbolIcon(Symbol.Filter), Margin = new Thickness(12, 0, 12, 8) };
        AutomationProperties.SetName(search, "Filter activity");

        void Fill()
        {
            list.Items.Clear();
            var rows = AllWindows()
                .Select(x => (x.Host, x.Session, x.Window, State: Activity.Of(x.Window)))
                .Where(x => x.State.Tone != Tone.None)
                .Where(x => filter.Length == 0 || RexPalette.Matches($"{TabLabel(x.Window)} {x.Session.Label} {x.Host.Name}", filter))
                .ToList();
            IEnumerable<IGrouping<string, (HostModel Host, SessionModel Session, WindowModel Window, (string? Word, Tone Tone, string? Message) State)>> groups = bySession
                ? rows.GroupBy(r => $"{r.Session.Label} · {r.Host.Name}")
                : rows.OrderByDescending(r => Activity.Priority(r.State.Tone)).GroupBy(r => r.State.Tone switch
                {
                    Tone.Caution => "Needs you",
                    Tone.Critical => "Error",
                    Tone.Success => "Done",
                    _ => "Working",
                });
            foreach (var group in groups)
            {
                list.Items.Add(new ListViewItem
                {
                    Content = new TextBlock { Text = group.Key, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = ThemeLookup.Brush(Root, "RexSecondaryTextBrush") },
                    IsHitTestVisible = false, IsTabStop = false, MinHeight = 28, Padding = new Thickness(12, 6, 12, 0),
                });
                foreach (var row in group) list.Items.Add(ActivityRow(row.Host, row.Session, row.Window, row.State, () => flyout.Hide()));
            }
            empty.Visibility = rows.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        }

        segmented.SelectionChanged += (_, _) => { bySession = segmented.SelectedIndex == 1; Fill(); };
        search.TextChanged += (_, _) => { filter = search.Text.Trim(); Fill(); };
        list.ItemClick += (_, e) => ((e.ClickedItem as ListViewItem)?.Tag as Action)?.Invoke();

        var header = new Grid { Padding = new Thickness(16, 12, 12, 10) };
        header.Children.Add(new TextBlock { Text = "Activity", FontSize = 14, FontWeight = FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center });
        segmented.HorizontalAlignment = HorizontalAlignment.Right;
        header.Children.Add(segmented);

        var root = new Grid { Width = 400, RequestedTheme = Root.ActualTheme };
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        root.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        root.Children.Add(header);
        Grid.SetRow(search, 1); root.Children.Add(search);
        Grid.SetRow(list, 2); root.Children.Add(list);
        Grid.SetRow(empty, 2); root.Children.Add(empty);
        flyout.Content = root;
        Fill();
        flyout.ShowAt(anchor);
    }

    private ListViewItem ActivityRow(HostModel h, SessionModel s, WindowModel w, (string? Word, Tone Tone, string? Message) state, Action close)
    {
        var focused = w.Panes.FirstOrDefault(p => p.Pane == w.FocusedPane) ?? w.Panes.FirstOrDefault();
        var grid = new Grid { Height = 52, ColumnSpacing = 10 };
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        grid.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        grid.Children.Add(new ProgramTile { IconKey = focused?.Icon ?? "shell", Program = focused?.Program, TileSize = 20, Badge = state.Tone, VerticalAlignment = VerticalAlignment.Center });

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        text.Children.Add(new TextBlock { Text = TabLabel(w), FontSize = 13, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 });
        text.Children.Add(new TextBlock { Text = state.Message is { Length: > 0 } m ? m : $"{s.Label} · {h.Name}", FontSize = 12, Foreground = ThemeLookup.Brush(Root, "RexSecondaryTextBrush"), TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1 });
        Grid.SetColumn(text, 1);
        grid.Children.Add(text);

        string word = state.Tone switch { Tone.Working => "Working", _ => state.Word ?? "" };
        var since = _activitySince.TryGetValue($"{h.Id}/{s.Id}/{w.Id}", out var entry) ? Activity.Ago(DateTime.UtcNow - entry.Since) : "now";
        var right = new StackPanel { VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Right };
        right.Children.Add(new TextBlock { Text = word, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Activity.WordBrush(Root, state.Tone), HorizontalAlignment = HorizontalAlignment.Right });
        right.Children.Add(new TextBlock { Text = since, FontSize = 11, Foreground = ThemeLookup.Brush(Root, "RexTertiaryTextBrush"), HorizontalAlignment = HorizontalAlignment.Right });
        Grid.SetColumn(right, 2);
        grid.Children.Add(right);

        var item = new ListViewItem
        {
            Content = grid,
            Padding = new Thickness(12, 0, 12, 0),
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            // Seeing a row does not mark it seen; clicking lands on the tab and its pane.
            Tag = (Action)(() => { close(); _ = LandOnAsync(h.Id, s.Id, w.Id, w.FocusedPane); }),
        };
        AutomationProperties.SetName(item, $"{TabLabel(w)}, {word}, {s.Label} on {h.Name}, {since}");
        return item;
    }
}
