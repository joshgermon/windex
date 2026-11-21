// A pane card as docs/design.md §5 describes it: 8 epx corners, a translucent surface, a 36 epx
// header and the terminal body. The focused card gets an accent stroke and a lift; unfocused cards
// a more transparent surface and a content dim. Built here only to check that the terminal
// composites inside such a card.
using System.Numerics;
using Microsoft.UI;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windex.Terminal;
using Windows.UI;

namespace TerminalLab;

internal sealed partial class PaneCard : Grid
{
    private readonly Border _card;
    private readonly Rectangle _dim;
    private readonly TextBlock _title;
    private bool _dark = true;
    private bool _focused;
    private bool _single = true;

    public TerminalView Terminal { get; }

    public PaneCard(TerminalView terminal, string title)
    {
        Terminal = terminal;
        _title = new TextBlock { Text = title, FontSize = 13, FontWeight = Microsoft.UI.Text.FontWeights.SemiBold, VerticalAlignment = VerticalAlignment.Center, TextTrimming = TextTrimming.CharacterEllipsis };
        var header = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 8, Padding = new Thickness(12, 0, 12, 0), Height = 36 };
        header.Children.Add(new FontIcon { Glyph = "", FontSize = 16, VerticalAlignment = VerticalAlignment.Center });
        header.Children.Add(_title);

        _dim = new Rectangle { IsHitTestVisible = false, Opacity = 0, OpacityTransition = new ScalarTransition { Duration = TimeSpan.FromMilliseconds(167) } };
        var body = new Grid();
        Grid.SetRow(body, 1);
        body.Children.Add(terminal);
        body.Children.Add(_dim);

        var layout = new Grid { RowDefinitions = { new RowDefinition { Height = new GridLength(36) }, new RowDefinition() } };
        layout.Children.Add(header);
        layout.Children.Add(body);
        _card = new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            Child = layout,
            BackgroundTransition = new BrushTransition { Duration = TimeSpan.FromMilliseconds(167) },
            Shadow = new ThemeShadow(),
        };
        Children.Add(_card);
        terminal.TitleChanged += (_, t) => _title.Text = string.IsNullOrWhiteSpace(t) ? title : t;
        Apply();
    }

    public void SetLook(bool dark, bool focused, bool single)
    {
        (_dark, _focused, _single) = (dark, focused, single);
        Apply();
    }

    private void Apply()
    {
        bool showFocus = _focused && !_single;
        var accent = (Color)Application.Current.Resources[_dark ? "SystemAccentColorLight2" : "SystemAccentColorDark1"];
        _card.Background = new SolidColorBrush(_focused || _single
            ? (_dark ? Color.FromArgb(0xE6, 0x1F, 0x1E, 0x24) : Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF))
            : (_dark ? Color.FromArgb(0xA6, 0x1F, 0x1E, 0x24) : Color.FromArgb(0xA6, 0xFF, 0xFF, 0xFF)));
        _card.BorderBrush = new SolidColorBrush(showFocus
            ? Color.FromArgb((byte)(_dark ? 0xB3 : 0xCC), accent.R, accent.G, accent.B)
            : _dark ? Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF) : Color.FromArgb(0x0F, 0, 0, 0));
        _card.Translation = showFocus ? new Vector3(0, 0, 8) : Vector3.Zero;
        _dim.Fill = new SolidColorBrush(_dark ? Color.FromArgb(0x47, 0, 0, 0) : Color.FromArgb(0x4D, 0xFA, 0xF9, 0xFC));
        _dim.Opacity = _focused || _single ? 0 : 1;
        // Text tokens (design §14), chosen here because app resources resolve against the app's theme, not the card's.
        bool primary = _focused || _single;
        _title.Foreground = new SolidColorBrush(_dark
            ? (primary ? Colors.White : Color.FromArgb(0xC5, 0xFF, 0xFF, 0xFF))
            : (primary ? Color.FromArgb(0xE4, 0, 0, 0) : Color.FromArgb(0x9E, 0, 0, 0)));
        Terminal.IsActive = _focused || _single;
    }
}
