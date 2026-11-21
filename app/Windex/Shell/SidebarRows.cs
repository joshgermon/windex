// Sidebar rows (design §6.2 table), built in code and updated in place by key so the list never
// flickers on a model snapshot. Selection is drawn by the row (subtle fill + accent pill).
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windex.Core;

namespace Windex.Shell;

public abstract partial class SidebarRow : Grid
{
    private readonly Border _pill = new()
    {
        Width = 3, Height = 16, CornerRadius = new CornerRadius(1.5),
        HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Center,
        Visibility = Visibility.Collapsed, IsHitTestVisible = false,
    };
    private bool _selected;

    protected readonly Grid Body = new();

    public string Key { get; }

    protected SidebarRow(string key, double height, double indent)
    {
        Key = key;
        Height = height;
        CornerRadius = new CornerRadius(4);
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Body.Margin = new Thickness(8 + indent, 0, 4, 0);
        Children.Add(Body);
        Children.Add(_pill);
        ActualThemeChanged += (_, _) => PaintSelection();
    }

    public bool IsSelected
    {
        get => _selected;
        set { if (_selected != value) { _selected = value; PaintSelection(); } }
    }

    private void PaintSelection()
    {
        Background = _selected ? ThemeLookup.Brush(this, "RexSubtleHoverBrush") : new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _pill.Background = ThemeLookup.Brush(this, "RexAccentBrush");
        _pill.Visibility = _selected ? Visibility.Visible : Visibility.Collapsed;
    }

    protected static TextBlock Text(double size, bool semibold = false) => new()
    {
        FontSize = size,
        FontWeight = semibold ? FontWeights.SemiBold : FontWeights.Normal,
        VerticalAlignment = VerticalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        TextWrapping = TextWrapping.NoWrap,
        MaxLines = 1,
    };

    protected static FrameworkElement KindGlyph(HostKind kind, double size) => new TextBlock
    {
        Text = kind.Glyph,
        FontFamily = ProgramIcons.FontFor(kind.Source),
        FontSize = size,
        VerticalAlignment = VerticalAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        IsTextScaleFactorEnabled = false,
    }.Decorative();

    protected void Columns(params GridLength[] widths)
    {
        foreach (var w in widths) Body.ColumnDefinitions.Add(new ColumnDefinition { Width = w });
    }

    protected void Place(FrameworkElement element, int column)
    {
        SetColumn(element, column);
        Body.Children.Add(element);
    }

    protected static GridLength Auto => GridLength.Auto;
    protected static GridLength Star => new(1, GridUnitType.Star);
    protected static GridLength Px(double v) => new(v);
}

/// <summary>Host: kind glyph · name · trailing trouble status · hover More.</summary>
public sealed partial class HostRow : SidebarRow
{
    private readonly Border _glyphHost = new() { Width = 16, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _name = Text(13, semibold: true);
    private readonly ProgressRing _ring = new() { Width = 14, Height = 14, MinWidth = 14, MinHeight = 14, IsActive = false, Visibility = Visibility.Collapsed, Margin = new Thickness(6, 0, 0, 0) };
    private readonly TextBlock _status = Text(12);
    private readonly HyperlinkButton _retry = new() { Content = "Retry", FontSize = 12, Padding = new Thickness(4, 0, 4, 0), Visibility = Visibility.Collapsed, VerticalAlignment = VerticalAlignment.Center };
    private readonly Button _more = new() { Content = "\uE712", Width = 24, Height = 24, FontSize = 12, Opacity = 0, Margin = new Thickness(2, 0, 0, 0) };
    private bool _offline;

    public string HostId { get; }

    public HostRow(string hostId, Action retry, Func<FrameworkElement, MenuFlyout> menu) : base("host:" + hostId, 32, 0)
    {
        HostId = hostId;
        Columns(Px(16), Px(10), Star, Auto, Auto, Auto, Auto);
        Place(_glyphHost, 0);
        Place(_name, 2);
        Place(_ring, 3);
        _status.Margin = new Thickness(6, 0, 0, 0);
        Place(_status, 4);
        Place(_retry, 5);
        _more.Style = (Style)Application.Current.Resources["RexSubtleButtonStyle"];
        _more.FontFamily = ProgramIcons.FluentFont;
        AutomationProperties.SetName(_more, "Host actions");
        Place(_more, 6);
        _retry.Click += (_, _) => retry();
        _more.Click += (_, _) => menu(_more).ShowAt(_more);
        _more.IsHitTestVisible = false;
        PointerEntered += (_, _) => { _more.Opacity = 1; _more.IsHitTestVisible = true; if (_offline) _retry.Visibility = Visibility.Visible; };
        PointerExited += (_, _) => { _more.Opacity = 0; _more.IsHitTestVisible = false; _retry.Visibility = Visibility.Collapsed; };
    }

    public void Update(HostModel host, HostKind kind)
    {
        _glyphHost.Child = KindGlyph(kind, 16);
        ((TextBlock)_glyphHost.Child).Foreground = ThemeLookup.Brush(this, "RexSecondaryTextBrush");
        _name.Text = host.Name;

        // Quiet when healthy: words only for trouble (design §6.4).
        string? word = null; string brush = "RexTertiaryTextBrush"; bool spin = false;
        _offline = false;
        switch (host.State)
        {
            case "connecting": spin = true; break;
            case "reconnecting": spin = true; word = "Reconnecting"; brush = "RexCautionBrush"; break;
            case "offline": word = "Offline"; _offline = true; break;
            case "error": word = "Can't connect"; brush = "RexCriticalBrush"; _offline = true; break;
            case "connected" when host.Server is { Api: > 0 and < 13 }: word = "Update Rex"; brush = "RexCautionBrush"; break;
        }
        _ring.IsActive = spin;
        _ring.Visibility = spin ? Visibility.Visible : Visibility.Collapsed;
        _ring.Foreground = ThemeLookup.Brush(this, host.State == "reconnecting" ? "RexCautionBrush" : "RexAccentBrush");
        _status.Text = word ?? "";
        _status.Visibility = word is null ? Visibility.Collapsed : Visibility.Visible;
        _status.Foreground = ThemeLookup.Brush(this, brush);
        AutomationProperties.SetName(this, word is null ? host.Name : $"{host.Name}, {word}");
        ToolTipService.SetToolTip(this, host.Error is null ? host.Endpoint : $"{host.Endpoint}\n{host.Error}");
    }
}

/// <summary>Session: program tile 20 with badge · name · activity word · tab count.</summary>
public sealed partial class SessionRow : SidebarRow
{
    private readonly ProgramTile _tile = new() { TileSize = 20, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _name = Text(13);
    private readonly TextBlock _word = Text(11, semibold: true);
    private readonly TextBlock _count = Text(11);

    public string HostId { get; }
    public string SessionId { get; }
    public bool Available { get; private set; }

    public SessionRow(string hostId, string sessionId) : base($"session:{hostId}/{sessionId}", 32, 16)
    {
        HostId = hostId;
        SessionId = sessionId;
        Columns(Px(20), Px(10), Star, Auto, Auto);
        Place(_tile, 0);
        Place(_name, 2);
        _word.Margin = new Thickness(6, 0, 0, 0);
        Place(_word, 3);
        _count.Margin = new Thickness(6, 0, 2, 0);
        _count.MinWidth = 10;
        _count.TextAlignment = TextAlignment.Right;
        Place(_count, 4);
    }

    public void Update(HostModel host, SessionModel session, bool current, string colorful)
    {
        Available = host.IsConnected;
        var active = session.Windows.FirstOrDefault(w => w.Id == session.ActiveWindow) ?? session.Windows.FirstOrDefault();
        var focused = active?.Panes.FirstOrDefault(p => p.Pane == active.FocusedPane) ?? active?.Panes.FirstOrDefault();
        _tile.IconKey = focused?.Icon ?? "shell";
        _tile.Program = focused?.Program;
        _tile.Monochrome = colorful == "never" || (colorful == "active" && !current);
        var activeTone = active is null ? Tone.None : Activity.Of(active).Tone;
        var tone = Activity.ToneOf(session.Word, session.Tone);
        _tile.Badge = Activity.Priority(activeTone) > Activity.Priority(tone) ? activeTone : tone;

        _name.Text = session.Label;
        _name.FontWeight = current ? FontWeights.SemiBold : FontWeights.Normal;
        var word = Activity.VisibleWord(session.Word, session.Tone);
        _word.Text = word ?? "";
        _word.Visibility = word is null ? Visibility.Collapsed : Visibility.Visible;
        _word.Foreground = Activity.WordBrush(this, tone);
        _count.Text = session.Windows.Count > 1 ? session.Windows.Count.ToString() : "";
        _count.Foreground = ThemeLookup.Brush(this, "RexTertiaryTextBrush");
        Opacity = Available ? 1 : 0.55;
        string tabs = session.Windows.Count == 1 ? "1 tab" : $"{session.Windows.Count} tabs";
        AutomationProperties.SetName(this, word is null ? $"{session.Label}, {tabs}" : $"{session.Label}, {word}, {tabs}");
    }
}

/// <summary>Tab (under the current session): program tile 16 · label · activity word.</summary>
public sealed partial class TabRow : SidebarRow
{
    private readonly ProgramTile _tile = new() { TileSize = 16, VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _label = Text(12);
    private readonly TextBlock _word = Text(11, semibold: true);

    public string HostId { get; }
    public string SessionId { get; }
    public string WindowId { get; }

    public TabRow(string hostId, string sessionId, string windowId) : base($"tab:{hostId}/{sessionId}/{windowId}", 28, 32)
    {
        HostId = hostId; SessionId = sessionId; WindowId = windowId;
        Columns(Px(16), Px(8), Star, Auto);
        Place(_tile, 0);
        Place(_label, 2);
        _word.Margin = new Thickness(6, 0, 2, 0);
        Place(_word, 3);
    }

    public void Update(WindowModel w, string colorful, bool selected)
    {
        var focused = w.Panes.FirstOrDefault(p => p.Pane == w.FocusedPane) ?? w.Panes.FirstOrDefault();
        var (word, tone, _) = Activity.Of(w);
        _tile.IconKey = focused?.Icon ?? "shell";
        _tile.Program = focused?.Program;
        _tile.Badge = tone;
        _tile.Monochrome = colorful == "never" || (colorful == "active" && !selected);
        _label.Text = string.IsNullOrWhiteSpace(w.Title) ? w.Label : w.Title;
        var visible = Activity.VisibleWord(word, tone == Tone.Critical ? "danger" : tone == Tone.Caution ? "warning" : null);
        _word.Text = visible ?? "";
        _word.Visibility = visible is null ? Visibility.Collapsed : Visibility.Visible;
        _word.Foreground = Activity.WordBrush(this, tone);
        AutomationProperties.SetName(this, visible is null ? _label.Text : $"{_label.Text}, {visible}");
    }
}

/// <summary>"Tailnet" group header: Network glyph · label · count · chevron.</summary>
public sealed partial class TailnetRow : SidebarRow
{
    private readonly TextBlock _count = Text(11);
    private readonly FontIcon _chevron = new() { FontSize = 10, Glyph = "\uE76C", VerticalAlignment = VerticalAlignment.Center };

    public bool Expanded { get; private set; }

    public TailnetRow() : base("tailnet", 28, 0)
    {
        Margin = new Thickness(0, 8, 0, 0);
        Columns(Px(16), Px(10), Auto, Px(6), Star, Auto);
        var glyph = new FontIcon { Glyph = "\uE968", FontSize = 14, VerticalAlignment = VerticalAlignment.Center };
        Place(glyph, 0);
        var label = Text(12, semibold: true);
        label.Text = "Tailnet";
        Place(label, 2);
        Place(_count, 4);
        Place(_chevron, 5);
        ActualThemeChanged += (_, _) => Paint(glyph, label);
        Loaded += (_, _) => Paint(glyph, label);
    }

    private void Paint(FontIcon glyph, TextBlock label)
    {
        glyph.Foreground = ThemeLookup.Brush(this, "RexSecondaryTextBrush");
        label.Foreground = ThemeLookup.Brush(this, "RexSecondaryTextBrush");
        _count.Foreground = ThemeLookup.Brush(this, "RexTertiaryTextBrush");
        _chevron.Foreground = ThemeLookup.Brush(this, "RexTertiaryTextBrush");
    }

    public void Update(int count, bool expanded)
    {
        Expanded = expanded;
        _count.Text = count.ToString();
        _chevron.Glyph = expanded ? "\uE70D" : "\uE76C";
        AutomationProperties.SetName(this, $"Tailnet, {count} machines, {(expanded ? "expanded" : "collapsed")}");
    }
}

/// <summary>A discovered tailnet machine not yet added: kind glyph · name · hover "Add".</summary>
public sealed partial class MachineRow : SidebarRow
{
    private readonly Border _glyphHost = new() { Width = 16 };
    private readonly TextBlock _name = Text(12);
    private readonly HyperlinkButton _add = new() { Content = "Add", FontSize = 12, Padding = new Thickness(4, 0, 4, 0), Visibility = Visibility.Collapsed };

    public DiscoveredMachine Machine { get; private set; }

    public MachineRow(DiscoveredMachine machine, Action<DiscoveredMachine> add) : base("machine:" + machine.Name, 28, 16)
    {
        Machine = machine;
        Columns(Px(16), Px(10), Star, Auto);
        Place(_glyphHost, 0);
        Place(_name, 2);
        Place(_add, 3);
        _add.Click += (_, _) => add(Machine);
        PointerEntered += (_, _) => _add.Visibility = Visibility.Visible;
        PointerExited += (_, _) => _add.Visibility = Visibility.Collapsed;
    }

    public void Update(DiscoveredMachine machine)
    {
        Machine = machine;
        var kind = HostKinds.Get(HostKinds.Infer(null, machine.Rex?.Endpoint, machine.Os));
        var glyph = (TextBlock)KindGlyph(kind, 14);
        glyph.Foreground = ThemeLookup.Brush(this, "RexSecondaryTextBrush");
        _glyphHost.Child = glyph;
        _name.Text = machine.Name;
        string what = machine.Rex is not null ? "Rex server" : machine.Ssh is not null ? "SSH" : "no Rex found";
        ToolTipService.SetToolTip(this, $"{machine.Dns ?? machine.Name}\n{machine.Os} · {what}");
        AutomationProperties.SetName(this, $"{machine.Name}, {what}");
    }
}
