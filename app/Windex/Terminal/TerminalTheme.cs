// Terminal colour themes (docs/design.md §7.6). The UI never recolours terminal output: a theme
// reaches the terminal by being declared to it (set_theme for server panes, OSC 4/10/11/12 for
// local panes). TerminalView itself only uses the parts a frame does not carry: the selection
// colours and the colour of text under a filled block cursor.
using System.Globalization;
using System.Text;
using Windows.UI;

namespace Windex.Terminal;

public sealed record TerminalTheme
{
    public required string Name { get; init; }
    public required Color Foreground { get; init; }
    public required Color Background { get; init; }
    public required Color Cursor { get; init; }
    /// <summary>Text drawn under a filled block cursor.</summary>
    public required Color CursorText { get; init; }
    public required Color SelectionBackground { get; init; }
    /// <summary>Null keeps each cell's own foreground inside the selection (the Rex default).</summary>
    public Color? SelectionForeground { get; init; }
    /// <summary>The 16 ANSI colours: normal 0–7, then bright 8–15.</summary>
    public required IReadOnlyList<Color> Palette { get; init; }
    public bool IsDark { get; init; }

    /// <summary>Rex Dusk, the dark default.</summary>
    public static TerminalTheme RexDusk { get; } = new()
    {
        Name = "Rex Dusk",
        IsDark = true,
        Foreground = Hex("#E7E4EC"),
        Background = Hex("#1B1A1F"),
        Cursor = Hex("#8AB4FF"),
        CursorText = Hex("#1B1A1F"),
        SelectionBackground = Hex("#3A4B70"),
        Palette = Palette16(
            "#2A2830", "#FF7B7B", "#5FD38D", "#E8C266", "#6CB0FF", "#C79BFF", "#4FD1D9", "#C9C5D0",
            "#6B6774", "#FFA3A0", "#86E3A8", "#F4D88E", "#97C8FF", "#DDBFFF", "#7FE3E8", "#F7F5FA"),
    };

    /// <summary>Rex Dawn, the light default.</summary>
    public static TerminalTheme RexDawn { get; } = new()
    {
        Name = "Rex Dawn",
        IsDark = false,
        Foreground = Hex("#24212B"),
        Background = Hex("#FBFAFC"),
        Cursor = Hex("#0067C0"),
        CursorText = Hex("#FFFFFF"),
        SelectionBackground = Hex("#C9DDFB"),
        Palette = Palette16(
            "#2E2B36", "#C4314B", "#1A7F37", "#9A6700", "#0A63C9", "#8250DF", "#137C86", "#6E7781",
            "#6E6A78", "#A51D2D", "#116329", "#7D4E00", "#2F81F7", "#6639BA", "#0F5F67", "#8C959F"),
    };

    public static TerminalTheme ForAppTheme(bool dark) => dark ? RexDusk : RexDawn;

    /// <summary>The <c>set_theme</c> command's <c>theme</c> object (docs/contract.md).</summary>
    public Dictionary<string, object> ToCoreTheme() => new()
    {
        ["background"] = ToHex(Background),
        ["foreground"] = ToHex(Foreground),
        ["cursor"] = ToHex(Cursor),
        ["selection_background"] = ToHex(SelectionBackground),
        ["palette"] = Palette.Select(ToHex).ToArray(),
    };

    /// <summary>OSC 4/10/11/12 sequences that declare this theme to a terminal (local panes).</summary>
    public string ToOscSequence()
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Palette.Count; i++)
            sb.Append($"\x1b]4;{i};{ToXParse(Palette[i])}\x1b\\");
        sb.Append($"\x1b]10;{ToXParse(Foreground)}\x1b\\");
        sb.Append($"\x1b]11;{ToXParse(Background)}\x1b\\");
        sb.Append($"\x1b]12;{ToXParse(Cursor)}\x1b\\");
        return sb.ToString();
    }

    public static Color Hex(string hex)
    {
        var s = hex.AsSpan().TrimStart('#');
        uint v = uint.Parse(s, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
        return s.Length == 8
            ? Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : Color.FromArgb(0xFF, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public static string ToHex(Color c) => $"#{c.R:X2}{c.G:X2}{c.B:X2}";

    private static string ToXParse(Color c) => $"rgb:{c.R:x2}/{c.G:x2}/{c.B:x2}";

    private static Color[] Palette16(params string[] hex) => hex.Select(Hex).ToArray();
}
