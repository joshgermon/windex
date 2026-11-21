// VT byte streams for the demos: everything is fed to local panes as if a program printed it.
using System.Text;

namespace TerminalLab;

internal static class DemoContent
{
    private const string Esc = "\x1b";
    private static string Sgr(string p) => $"{Esc}[{p}m";
    private const string Reset = "\x1b[0m";

    /// <summary>
    /// Colours, SGR styles, Unicode, box drawing, blocks, Powerline and Nerd Font icons. The alignment
    /// rows span <paramref name="cols"/> columns, so a wide grid shows whether anything drifts.
    /// </summary>
    public static string Showcase(int cols)
    {
        var s = new StringBuilder();
        // A program-set title (OSC 2) and a steady block cursor (DECSCUSR 2).
        s.Append($"{Esc}]2;showcase — OSC 2 title{Esc}\\{Esc}[2 q{Esc}[H{Esc}[2J");
        Heading(s, "16 colours");
        for (int i = 0; i < 8; i++) s.Append(Sgr($"4{i}")).Append($" {i,2} ");
        s.Append(Reset).Append("  ");
        for (int i = 0; i < 8; i++) s.Append(Sgr($"3{i}")).Append($"fg{i} ");
        s.Append(Reset).Append("\r\n");
        for (int i = 0; i < 8; i++) s.Append(Sgr($"10{i}")).Append($" {i + 8,2} ");
        s.Append(Reset).Append("  ");
        for (int i = 0; i < 8; i++) s.Append(Sgr($"9{i}")).Append($"fg{i + 8} ");
        s.Append(Reset).Append("\r\n");

        Heading(s, "256 colours and truecolor");
        for (int row = 0; row < 2; row++)
        {
            for (int i = 0; i < 108; i++) { int n = 16 + row * 108 + i; if (n < 232) s.Append(Sgr($"48;5;{n}")).Append(' '); }
            s.Append(Reset).Append("\r\n");
        }
        for (int i = 232; i < 256; i++) s.Append(Sgr($"48;5;{i}")).Append("  ");
        s.Append(Reset).Append("\r\n");
        for (int i = 0; i < 96; i++)
        {
            var (r, g, b) = Hue(i / 96.0);
            s.Append(Sgr($"48;2;{r};{g};{b}")).Append(' ');
        }
        s.Append(Reset).Append("\r\n");
        const string gradient = "Truecolor text shades smoothly from one end of the line to the other end";
        for (int i = 0; i < gradient.Length; i++)
        {
            var (r, g, b) = Hue(i / (double)gradient.Length);
            s.Append(Sgr($"38;2;{r};{g};{b}")).Append(gradient[i]);
        }
        s.Append(Reset).Append("\r\n");

        Heading(s, "Styles");
        s.Append(Sgr("1")).Append("bold").Append(Reset).Append("  ")
         .Append(Sgr("2")).Append("faint").Append(Reset).Append("  ")
         .Append(Sgr("3")).Append("italic").Append(Reset).Append("  ")
         .Append(Sgr("1;3")).Append("bold italic").Append(Reset).Append("  ")
         .Append(Sgr("9")).Append("strike").Append(Reset).Append("  ")
         .Append(Sgr("53")).Append("overline").Append(Reset).Append("  ")
         .Append(Sgr("7")).Append("inverse").Append(Reset).Append("  ")
         .Append(Sgr("5")).Append("blink").Append(Reset).Append("  ")
         .Append("hidden[").Append(Sgr("8")).Append("secret").Append(Reset).Append("]\r\n");
        s.Append(Sgr("4")).Append("single").Append(Reset).Append("  ")
         .Append(Sgr("4:2")).Append("double").Append(Reset).Append("  ")
         .Append(Sgr("4:3")).Append("curly").Append(Reset).Append("  ")
         .Append(Sgr("4:4")).Append("dotted").Append(Reset).Append("  ")
         .Append(Sgr("4:5")).Append("dashed").Append(Reset).Append("  ")
         .Append(Sgr("4:3;58;2;255;90;90")).Append("red curly (spelling)").Append(Reset).Append("  ")
         .Append(Sgr("4;58;5;45")).Append("cyan underline").Append(Reset).Append("  ")
         .Append(Sgr("1;4;31")).Append("bold red underlined").Append(Reset).Append("\r\n");

        Heading(s, "Unicode");
        s.Append("CJK 中文字符 日本語 한국어  Emoji 😀 🎉 👍🏽 🇦🇺 ❤️ ✨ 🦖  Combining e\u0301 n\u0303 a\u0308 o\u0302\u0323  ");
        s.Append("Greek αβγδ  Cyrillic Жжщ  Math ∀∂∈ℝ≤≠  Arrows ←↑→↓ ★ ✓\r\n");

        Heading(s, "Box drawing, blocks and alignment");
        string[] boxes =
        [
            "┌──────┬──────┐ ╔══════╦══════╗ ╭──────────╮ ┏━━━━━━┳━━━━━━┓ ┄┄┄┄ ┅┅┅┅ ╱╲╱╲ ▁▂▃▄▅▆▇█ █▉▊▋▌▍▎▏",
            "│ left │ right│ ║ dbl  ║ dbl  ║ │ rounded  │ ┃ heavy┃ cell ┃ ┈┈┈┈ ╌╌╌╌ ╲╱╲╱ ░░▒▒▓▓██ ▖▗▘▙▚▛▜▝▞▟",
            "├──────┼──────┤ ╠══════╬══════╣ │ ┆ dashed │ ┣━━━━━━╋━━━━━━┫ ┊┊┊┊ ╎╎╎╎ ╳╳╳╳ ▀▀▄▄▐▐▌▌ ▔▔▕▕▏▏▕▕",
            "└──────┴──────┘ ╚══════╩══════╝ ╰──────────╯ ┗━━━━━━┻━━━━━━┛ ┋┋┋┋ ╏╏╏╏ ┼┼┼┼ ▛▀▀▜ ▙▄▄▟ ████",
        ];
        foreach (var line in boxes) s.Append(Sgr("38;2;160;170;200")).Append(line).Append(Reset).Append("\r\n");
        // Every 10 columns a bar: ASCII, then wide CJK + emoji + combining marks, then sprites.
        int segments = Math.Max(1, cols / 10);
        s.Append(string.Concat(Enumerable.Range(1, segments).Select(i => $"{i % 10}23456789|"))).Append("\r\n");
        s.Append(string.Concat(Enumerable.Repeat("中文e\u0301😀ab|", segments))).Append("\r\n");
        s.Append(string.Concat(Enumerable.Repeat("─────────┼", segments))).Append("\r\n");

        Heading(s, "Powerline and Nerd Font icons");
        Powerline(s);
        s.Append("  \ue0a0 branch  \uf07b folder  \uf113 github  \ue7a8 rust  \uf120 terminal  \uf0c2 cloud  \ue70f windows  \uf17c linux  \uf179 apple  \uf1d3 git  \ue718 node  \uf308 docker\r\n");

        Heading(s, "Links");
        s.Append($"{Esc}]8;;https://github.com/ghostty-org/ghostty{Esc}\\OSC 8 link to Ghostty{Esc}]8;;{Esc}\\   plain URL https://example.com/rex?x=1 (Ctrl+click)\r\n");
        s.Append("\r\n").Append(Sgr("1;32")).Append("joshua@dasher").Append(Reset).Append(':').Append(Sgr("1;34")).Append("~/src/windex").Append(Reset).Append("$ ");
        return s.ToString();
    }

    private static void Heading(StringBuilder s, string text) =>
        s.Append(Sgr("1;38;2;138;180;255")).Append("▌ ").Append(text).Append(Reset).Append("\r\n");

    private static void Powerline(StringBuilder s)
    {
        (string Text, (int R, int G, int B) Bg)[] segments =
        [
            ("  joshua@dasher ", (108, 176, 255)),
            ("  ~/src/windex ", (199, 155, 255)),
            ("  main ±2 ", (95, 211, 141)),
        ];
        for (int i = 0; i < segments.Length; i++)
        {
            var (text, (r, g, b)) = segments[i];
            s.Append(Sgr($"38;2;27;26;31;48;2;{r};{g};{b}")).Append(text);
            if (i + 1 < segments.Length)
            {
                var n = segments[i + 1].Bg;
                s.Append(Sgr($"38;2;{r};{g};{b};48;2;{n.R};{n.G};{n.B}")).Append('\ue0b0');
            }
            else s.Append(Reset).Append(Sgr($"38;2;{r};{g};{b}")).Append('\ue0b0').Append(Reset);
        }
        s.Append(" \ue0b1 ").Append(Sgr("38;2;232;194;102")).Append('\ue0b2').Append(Sgr("30;48;2;232;194;102")).Append(" 12:04 ").Append(Reset)
         .Append(Sgr("38;2;79;209;217")).Append('\ue0b6').Append(Sgr("30;48;2;79;209;217")).Append(" rounded ").Append(Reset).Append(Sgr("38;2;79;209;217")).Append('\ue0b4').Append(Reset)
         .Append(" \ue0b3 \ue0b8\ue0ba\ue0bc\ue0be\r\n");
    }

    /// <summary>A narrow session (fits ~70 columns): a prompt, a coloured listing, a table and a status line.</summary>
    public static string Listing()
    {
        var s = new StringBuilder();
        s.Append($"{Esc}]2;src — listing{Esc}\\{Esc}[2 q{Esc}[H{Esc}[2J");
        Powerline(s);
        s.Append("$ ls -la\r\n");
        (string Perm, string Size, string Name, string Sgr)[] files =
        [
            ("drwxr-xr-x", "4.0K", "app/", "1;34"), ("drwxr-xr-x", "4.0K", "core/", "1;34"), ("drwxr-xr-x", "4.0K", "docs/", "1;34"),
            ("-rw-r--r--", " 63", ".gitignore", "0"), ("-rwxr-xr-x", "1.2K", "build.ps1", "1;32"), ("-rw-r--r--", "9.8K", "README.md", "0"),
            ("lrwxrwxrwx", "  12", "latest -> app/Windex", "1;36"), ("-rw-r--r--", "2.1M", "rexcore.dll", "33"),
        ];
        foreach (var (perm, size, name, sgr) in files)
            s.Append(Sgr("2")).Append(perm).Append(Reset).Append($"  joshua  {size,5}  Nov 18 12:04  ").Append(Sgr(sgr)).Append(name).Append(Reset).Append("\r\n");
        s.Append("\r\n$ git status --short\r\n")
         .Append(Sgr("32")).Append(" M").Append(Reset).Append(" app/Windex/Terminal/TerminalView.cs\r\n")
         .Append(Sgr("31")).Append("??").Append(Reset).Append(" app/TerminalLab/\r\n\r\n");
        string[] table =
        [
            "╭──────────┬─────────┬──────────╮",
            "│ pane     │ cells   │ draw     │",
            "├──────────┼─────────┼──────────┤",
            "│ left     │ 73×37   │ 0.4 ms   │",
            "│ right    │ 73×37   │ 0.3 ms   │",
            "╰──────────┴─────────┴──────────╯",
        ];
        foreach (var line in table) s.Append(Sgr("38;2;160;170;200")).Append(line).Append(Reset).Append("\r\n");
        s.Append("\r\nEmoji 😀 🎉 ✨  CJK 中文 日本語  ").Append(Sgr("4:3;58;2;255;90;90")).Append("misspeled").Append(Reset).Append("\r\n\r\n");
        s.Append(Sgr("1;32")).Append("joshua@dasher").Append(Reset).Append(':').Append(Sgr("1;34")).Append("~/src/windex").Append(Reset).Append("$ ");
        return s.ToString();
    }

    /// <summary>10,000 numbered lines of history.</summary>
    public static string Scrollback(int lines = 10_000)
    {
        var s = new StringBuilder(lines * 80);
        s.Append($"{Esc}[4 q"); // steady underline cursor
        string[] words = ["alpha", "bravo", "charlie", "delta", "echo", "foxtrot", "golf", "hotel", "india", "juliet"];
        for (int i = 1; i <= lines; i++)
        {
            s.Append(Sgr("2")).Append($"{i,6}").Append(Reset).Append("  ")
             .Append(Sgr($"3{1 + i % 6}")).Append(words[i % words.Length]).Append(Reset)
             .Append(" line ").Append(i).Append(" of ").Append(lines).Append(" — scroll with the wheel, drag the scrollbar, select across lines")
             .Append("\r\n");
        }
        s.Append(Sgr("1")).Append("end of history").Append(Reset).Append(" $ ");
        return s.ToString();
    }

    /// <summary>Full-width program backgrounds on the frame's first and last rows (--non-owner): they
    /// must stop at the frame's edges, not stretch across the rest of a larger view.</summary>
    public static string NonOwnerEdges(int cols, int rows) =>
        $"{Esc}7{Esc}[1;1H{Esc}[30;42m non-owner: this frame is {cols}x{rows}, smaller than the view{Esc}[K{Esc}[0m" +
        $"{Esc}[{rows};1H{Esc}[97;41m last row of the frame{Esc}[K{Esc}[0m{Esc}8";

    public static string TypingPadIntro() =>
        $"{Esc}[5 q" + // blinking bar cursor
        Sgr("1;38;2;138;180;255") + "Typing pad" + Reset + " — keys echo here; the panel on the right lists what reached the core.\r\n" +
        Sgr("2") + "Try dead keys and AltGr on an international layout, an IME (Win+Space), the emoji panel (Win+.), paste (Ctrl+V)." + Reset + "\r\n\r\n" +
        Sgr("1;32") + "pad" + Reset + " › ";

    public static (int R, int G, int B) Hue(double t)
    {
        double h = t * 6, x = 1 - Math.Abs(h % 2 - 1);
        var (r, g, b) = (int)h switch { 0 => (1d, x, 0d), 1 => (x, 1d, 0d), 2 => (0d, 1d, x), 3 => (0d, x, 1d), 4 => (x, 0d, 1d), _ => (1d, 0d, x) };
        return ((int)(r * 200 + 40), (int)(g * 200 + 40), (int)(b * 200 + 40));
    }
}
