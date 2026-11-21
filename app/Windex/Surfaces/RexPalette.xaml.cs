// RexPalette: the session switcher (Ctrl+Shift+K) and command palette (Ctrl+Shift+P). The window
// supplies the rows (IPaletteSource); this control filters by keyboard, draws grouped rows with
// tiles and keycaps, and invokes the chosen row.
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windex.Services;
using Windex.Shell;
using Windows.System;

namespace Windex.Surfaces;

public enum PaletteMode { Switcher, Commands }

public sealed record PaletteItem(string Label, Action Invoke)
{
    public string? Secondary { get; init; }
    public string? Word { get; init; }
    public Tone Tone { get; init; }
    /// <summary>Program tile (20 epx) for session and tab rows.</summary>
    public string? IconKey { get; init; }
    public string? Program { get; init; }
    public Tone Badge { get; init; }
    /// <summary>Segoe glyph (16 epx, secondary) for command rows.</summary>
    public string? Glyph { get; init; }
    /// <summary>Authored path resource key (split icons).</summary>
    public string? PathKey { get; init; }
    public KeyChord? Keys { get; init; }
    public bool Enabled { get; init; } = true;
    public bool Current { get; init; }
    /// <summary>Opens a second step (ChevronRight).</summary>
    public bool Chevron { get; init; }
    /// <summary>The row Ctrl+Enter runs (create session from the query).</summary>
    public bool IsCreate { get; init; }
}

public sealed record PaletteGroup(string Header, HostKind? Kind, IReadOnlyList<PaletteItem> Items);

public interface IPaletteSource
{
    IReadOnlyList<PaletteGroup> Build(PaletteMode mode, string query);
}

public sealed partial class RexPalette : UserControl
{
    private IPaletteSource? _source;
    private PaletteMode _mode;
    private bool _switchingMode;

    public event EventHandler? CloseRequested;

    public RexPalette()
    {
        InitializeComponent();
    }

    public PaletteMode Mode => _mode;

    public void Open(PaletteMode mode, IPaletteSource source)
    {
        _source = source;
        SetMode(mode);
        _switchingMode = true;
        Query.Text = "";
        _switchingMode = false;
        Rebuild();
        // The popup's content may not be in the tree yet on the first open.
        if (Query.IsLoaded) Query.Focus(FocusState.Programmatic);
        else Query.Loaded += FocusOnLoad;
    }

    private void FocusOnLoad(object sender, RoutedEventArgs e)
    {
        Query.Loaded -= FocusOnLoad;
        Query.Focus(FocusState.Programmatic);
    }

    private void SetMode(PaletteMode mode)
    {
        _mode = mode;
        Query.PlaceholderText = mode == PaletteMode.Switcher ? "Switch session, or type a name to create one" : "Type a command";
        Hints.Children.Clear();
        foreach (var (keys, label) in mode == PaletteMode.Switcher
                     ? new[] { (new[] { "Enter" }, "Open"), (new[] { "Ctrl", "Enter" }, "Create"), (new[] { "Esc" }, "Close") }
                     : new[] { (new[] { "Enter" }, "Run"), (new[] { "Esc" }, "Close") })
        {
            var hint = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4 };
            foreach (var k in keys) hint.Children.Add(Keycap(k));
            hint.Children.Add(new TextBlock { Text = label, FontSize = 12, VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeLookup.Brush(this, "RexTertiaryTextBrush"), Margin = new Thickness(2, 0, 0, 0) });
            Hints.Children.Add(hint);
        }
    }

    public static Border Keycap(string text) => new()
    {
        Style = (Style)Application.Current.Resources["RexKeycapStyle"],
        Child = new TextBlock { Text = text, Style = (Style)Application.Current.Resources["RexKeycapTextStyle"] },
    };

    private void Rebuild()
    {
        if (_source is null) return;
        string query = Query.Text;
        if (_mode == PaletteMode.Switcher && query.StartsWith('>'))
        {
            SetMode(PaletteMode.Commands);
            _switchingMode = true;
            Query.Text = query[1..].TrimStart();
            Query.SelectionStart = Query.Text.Length;
            _switchingMode = false;
            query = Query.Text;
        }
        var groups = _source.Build(_mode, query.Trim());
        List.Items.Clear();
        foreach (var group in groups)
        {
            if (group.Items.Count == 0) continue;
            if (group.Header.Length > 0) List.Items.Add(HeaderItem(group));
            foreach (var item in group.Items) List.Items.Add(RowItem(item));
        }
        EmptyText.Visibility = List.Items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        SelectFirst();
    }

    /// <summary>
    /// Highlight the first row Enter should run. The "Create session" row heads the list but is
    /// only the default when nothing else matches: Enter opens, Ctrl+Enter creates.
    /// </summary>
    private void SelectFirst()
    {
        int create = -1;
        for (int i = 0; i < List.Items.Count; i++)
        {
            if (List.Items[i] is not ListViewItem { Tag: PaletteItem { Enabled: true } item }) continue;
            if (!item.IsCreate) { Select(i); return; }
            if (create < 0) create = i;
        }
        if (create >= 0) Select(create);
        else List.SelectedIndex = -1;
    }

    private void Select(int index)
    {
        List.SelectedIndex = index;
        if (index >= 0) List.ScrollIntoView(List.Items[index]);
    }

    private void Move(int delta)
    {
        int count = List.Items.Count;
        if (count == 0) return;
        int i = List.SelectedIndex;
        for (int step = 0; step < count; step++)
        {
            i = (i + delta + count) % count;
            if (List.Items[i] is ListViewItem { Tag: PaletteItem { Enabled: true } }) { Select(i); return; }
        }
    }

    private void Query_TextChanged(object sender, TextChangedEventArgs e)
    {
        if (!_switchingMode) Rebuild();
    }

    private void Query_PreviewKeyDown(object sender, KeyRoutedEventArgs e)
    {
        bool ctrl = Microsoft.UI.Input.InputKeyboardSource.GetKeyStateForCurrentThread(VirtualKey.Control).HasFlag(Windows.UI.Core.CoreVirtualKeyStates.Down);
        switch (e.Key)
        {
            case VirtualKey.Down: Move(+1); e.Handled = true; break;
            case VirtualKey.Up: Move(-1); e.Handled = true; break;
            case VirtualKey.PageDown: Move(+8); e.Handled = true; break;
            case VirtualKey.PageUp: Move(-8); e.Handled = true; break;
            case VirtualKey.Escape: CloseRequested?.Invoke(this, EventArgs.Empty); e.Handled = true; break;
            case VirtualKey.Back when _mode == PaletteMode.Commands && Query.Text.Length == 0:
                SetMode(PaletteMode.Switcher); Rebuild(); e.Handled = true; break;
            case VirtualKey.Enter when ctrl:
                var create = List.Items.OfType<ListViewItem>().Select(i => i.Tag).OfType<PaletteItem>().FirstOrDefault(i => i.IsCreate);
                if (create is not null) Run(create);
                e.Handled = true;
                break;
            case VirtualKey.Enter:
                if ((List.SelectedItem as ListViewItem)?.Tag is PaletteItem item) Run(item);
                e.Handled = true;
                break;
        }
    }

    private void List_ItemClick(object sender, ItemClickEventArgs e)
    {
        if ((e.ClickedItem as ListViewItem)?.Tag is PaletteItem { Enabled: true } item) Run(item);
    }

    private void Run(PaletteItem item)
    {
        if (!item.Enabled) return;
        CloseRequested?.Invoke(this, EventArgs.Empty);
        item.Invoke();
    }

    // ------------------------------------------------------------------ rows

    private ListViewItem HeaderItem(PaletteGroup group)
    {
        var panel = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        var brush = ThemeLookup.Brush(this, "RexSecondaryTextBrush");
        if (group.Kind is { } kind)
            panel.Children.Add(new TextBlock { Text = kind.Glyph, FontFamily = ProgramIcons.FontFor(kind.Source), FontSize = 13, Foreground = brush, VerticalAlignment = VerticalAlignment.Center }.Decorative());
        panel.Children.Add(new TextBlock { Text = group.Header, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = brush, VerticalAlignment = VerticalAlignment.Center });
        var item = new ListViewItem { Content = panel, Height = 28, MinHeight = 28, IsHitTestVisible = false, IsTabStop = false, Padding = new Thickness(10, 4, 10, 0) };
        AutomationProperties.SetAccessibilityView(item, Microsoft.UI.Xaml.Automation.Peers.AccessibilityView.Raw);
        return item;
    }

    private ListViewItem RowItem(PaletteItem p)
    {
        var grid = new Grid { Height = 36, ColumnSpacing = 0 };
        foreach (var w in new[] { GridLength.Auto, new GridLength(1, GridUnitType.Star), GridLength.Auto, GridLength.Auto, new GridLength(22) })
            grid.ColumnDefinitions.Add(new ColumnDefinition { Width = w });

        FrameworkElement icon;
        if (p.IconKey is not null)
            icon = new ProgramTile { IconKey = p.IconKey, Program = p.Program, TileSize = 20, Badge = p.Badge, Margin = new Thickness(0, 0, 10, 0), VerticalAlignment = VerticalAlignment.Center };
        else if (p.PathKey is not null)
            icon = new PathIcon
            {
                Data = (Geometry)Microsoft.UI.Xaml.Markup.XamlBindingHelper.ConvertValue(typeof(Geometry), Application.Current.Resources[p.PathKey]),
                Width = 16, Height = 16, Margin = new Thickness(2, 0, 12, 0), Foreground = ThemeLookup.Brush(this, "RexSecondaryTextBrush"),
                VerticalAlignment = VerticalAlignment.Center,
            };
        else
            icon = new FontIcon { Glyph = p.Glyph ?? "\uE756", FontSize = 16, Width = 20, Margin = new Thickness(0, 0, 10, 0), Foreground = ThemeLookup.Brush(this, "RexSecondaryTextBrush"), VerticalAlignment = VerticalAlignment.Center };
        grid.Children.Add(icon);

        var labels = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, VerticalAlignment = VerticalAlignment.Center };
        labels.Children.Add(new TextBlock { Text = p.Label, FontSize = 14, TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, VerticalAlignment = VerticalAlignment.Center });
        if (p.Secondary is { Length: > 0 })
            labels.Children.Add(new TextBlock { Text = p.Secondary, FontSize = 12, Foreground = ThemeLookup.Brush(this, "RexTertiaryTextBrush"), TextTrimming = TextTrimming.CharacterEllipsis, MaxLines = 1, VerticalAlignment = VerticalAlignment.Center });
        Grid.SetColumn(labels, 1);
        grid.Children.Add(labels);

        if (p.Word is { Length: > 0 })
        {
            var word = new TextBlock { Text = p.Word, FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = Activity.WordBrush(this, p.Tone), VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(8, 0, 0, 0) };
            Grid.SetColumn(word, 2);
            grid.Children.Add(word);
        }
        if (p.Keys is { } keys)
        {
            var caps = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 4, VerticalAlignment = VerticalAlignment.Center, Margin = new Thickness(12, 0, 0, 0) };
            foreach (var k in keys.Caps) caps.Children.Add(Keycap(k));
            Grid.SetColumn(caps, 3);
            grid.Children.Add(caps);
        }
        if (p.Current || p.Chevron)
        {
            var mark = new FontIcon { Glyph = p.Current ? "\uE73E" : "\uE76C", FontSize = p.Current ? 14 : 12, HorizontalAlignment = HorizontalAlignment.Right, VerticalAlignment = VerticalAlignment.Center, Foreground = ThemeLookup.Brush(this, p.Current ? "RexAccentBrush" : "RexTertiaryTextBrush") };
            Grid.SetColumn(mark, 4);
            grid.Children.Add(mark);
        }

        var item = new ListViewItem { Content = grid, Tag = p, IsEnabled = p.Enabled, Opacity = p.Enabled ? 1 : 0.5 };
        string name = p.Label;
        if (p.Secondary is { Length: > 0 }) name += ", " + p.Secondary;
        if (p.Word is { Length: > 0 }) name += ", " + p.Word;
        if (p.Current) name += ", current";
        AutomationProperties.SetName(item, name);
        if (p.Keys is { } shortcut) AutomationProperties.SetAcceleratorKey(item, shortcut.ToString());
        return item;
    }

    /// <summary>Case-insensitive match: substring first, then letters in order (fuzzy).</summary>
    public static bool Matches(string text, string query)
    {
        if (query.Length == 0) return true;
        if (text.Contains(query, StringComparison.OrdinalIgnoreCase)) return true;
        int q = 0;
        foreach (char c in text)
            if (q < query.Length && char.ToLowerInvariant(c) == char.ToLowerInvariant(query[q])) q++;
        return q == query.Length;
    }
}
