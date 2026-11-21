// Program icon system (design §8). Brand-agnostic and clearly licensed glyph sources only:
//   Fluent  — Segoe Fluent Icons (system font, referenced, not redistributed)
//   Nerd    — the bundled Symbols Nerd Font (MIT; Devicons MIT, Codicons/Font Awesome CC BY 4.0)
//   Monogram — two letters on the hue tile
// Hues are our own choices. The process-name aliases follow Rex Rune's mapping logic (MIT).
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace Windex.Shell;

public enum GlyphSource { Fluent, Nerd, Monogram }

public sealed record ProgramIconSpec(string Key, GlyphSource Source, string Glyph, Color Hue, Color Fg)
{
    /// <summary>Top stop of the tile gradient: the hue lightened 12%.</summary>
    public Color HueLight => ColorMath.Lighten(Hue, 0.12);
    /// <summary>Glyph-mode tint on a dark card (relative luminance ~0.60).</summary>
    public Color GlyphOnDark => ColorMath.WithLuminance(Hue, 0.60, lighten: true);
    /// <summary>Glyph-mode tint on a light card (relative luminance ~0.18).</summary>
    public Color GlyphOnLight => ColorMath.WithLuminance(Hue, 0.18, lighten: false);
}

public static class ProgramIcons
{
    public const string NerdFontUri = "ms-appx:///Assets/Fonts/SymbolsNerdFont-Regular.ttf#Symbols Nerd Font";

    private static FontFamily? _nerd, _fluent, _monogram;
    public static FontFamily NerdFont => _nerd ??= new FontFamily(NerdFontUri);
    public static FontFamily FluentFont => _fluent ??= (FontFamily)Application.Current.Resources["SymbolThemeFontFamily"];
    public static FontFamily MonogramFont => _monogram ??= new FontFamily("Segoe UI Variable Display");

    public static FontFamily FontFor(GlyphSource source) => source switch
    {
        GlyphSource.Nerd => NerdFont,
        GlyphSource.Fluent => FluentFont,
        _ => MonogramFont,
    };

    private static readonly Color White = Color.FromArgb(255, 255, 255, 255);

    private static ProgramIconSpec F(string key, string glyph, string hue, string? fg = null) =>
        new(key, GlyphSource.Fluent, glyph, ColorMath.Hex(hue), fg is null ? White : ColorMath.Hex(fg));
    private static ProgramIconSpec N(string key, string glyph, string hue, string? fg = null) =>
        new(key, GlyphSource.Nerd, glyph, ColorMath.Hex(hue), fg is null ? White : ColorMath.Hex(fg));
    private static ProgramIconSpec M(string key, string letters, string hue, string? fg = null) =>
        new(key, GlyphSource.Monogram, letters, ColorMath.Hex(hue), fg is null ? White : ColorMath.Hex(fg));

    /// <summary>Icon key → spec. Keys are what the core reports in pane.icon, plus our own groups.</summary>
    private static readonly Dictionary<string, ProgramIconSpec> Specs = new[]
    {
        N("shell", "\uF120", "#2EA043"),              // fa-terminal ">_"
        N("fish", "\uEE41", "#1F9E9A"),               // fa-fish
        N("pwsh", "\uE86C", "#2F6BD8"),               // dev-powershell
        F("cmd", "\uE756", "#5F6368"),
        N("wsl", "\uF17C", "#E95420"),                 // fa-linux
        N("node", "\uE718", "#5FA04E"),                // dev-nodejs_small
        N("npm", "\uE71E", "#CB3837"),                 // dev-npm
        N("pnpm", "\uE865", "#E8892B"),                // dev-pnpm
        N("yarn", "\uE8EC", "#2C8EBB"),                // dev-yarn
        N("bun", "\uE76F", "#F6DECE", "#3B2A20"),      // dev-bun
        N("deno", "\uE7C0", "#1F1F1F"),                // dev-denojs
        N("python", "\uE73C", "#3776AB"),              // dev-python
        N("ruby", "\uE739", "#CC342D"),                // dev-ruby
        N("php", "\uE73D", "#777BB4"),                 // dev-php
        N("perl", "\uE769", "#39457E"),                // dev-perl
        N("go", "\uE627", "#00ADD8"),                  // seti-go
        N("rust", "\uE7A8", "#B7410E"),                // dev-rust
        N("zig", "\uE6A9", "#F7A41D", "#2B1D05"),      // seti-zig
        N("dotnet", "\uE77F", "#512BD4"),              // dev-dotnet
        N("java", "\uE738", "#E76F00"),                // dev-java
        N("kotlin", "\uE634", "#7F52FF"),              // custom-kotlin
        N("gradle", "\uE660", "#02303A"),              // seti-gradle
        N("scala", "\uE737", "#DC322F"),               // dev-scala
        N("clojure", "\uE768", "#5881D8"),             // dev-clojure
        N("haskell", "\uE777", "#5E5086"),             // dev-haskell
        N("ocaml", "\uE67A", "#EE6A1A"),               // seti-ocaml
        N("elixir", "\uE62D", "#4B275F"),              // custom-elixir
        N("julia", "\uE624", "#9558B2"),               // seti-julia
        N("r", "\uE68A", "#276DC3"),                   // seti-r
        N("dart", "\uE798", "#0175C2"),                // dev-dart
        N("lua", "\uE826", "#2C2D72"),                 // dev-lua
        N("swift", "\uE755", "#F05138"),               // dev-swift
        N("nix", "\uF313", "#5277C3"),                 // linux-nixos
        F("build", "\uE90F", "#5A6B8C"),               // Repair
        F("test", "\uF196", "#8E44AD"),                // Beaker
        N("vim", "\uE7C5", "#1E8C3F"),                 // dev-vim
        N("neovim", "\uE6AE", "#3F9F6F"),              // custom-neovim
        M("helix", "Hx", "#7A5FD1"),
        N("emacs", "\uE632", "#7F5AB6"),               // custom-emacs
        F("editor", "\uE70F", "#6B7A8F"),              // Edit
        N("vscode", "\uE8DA", "#2B7BD0"),              // dev-vscode
        N("git", "\uE702", "#F05033"),                 // dev-git
        N("github", "\uF09B", "#24292F"),              // fa-github
        F("monitor", "\uE9D9", "#22A06B"),             // Diagnostic
        N("container", "\uE7B0", "#1D63ED"),           // dev-docker
        N("kube", "\uE81D", "#326CE5"),                // dev-kubernetes
        F("ssh", "\uE8AF", "#5B5FC7"),                 // Remote
        F("tailscale", "\uE968", "#4B5563"),           // Network
        F("pager", "\uE736", "#A0712A"),               // ReadingMode
        F("manual", "\uE82D", "#A0712A"),              // Dictionary
        F("logs", "\uE8FD", "#6B7280"),                // BulletedList
        F("wait", "\uE917", "#6B7280"),                // Clock
        F("files", "\uE8B7", "#E3A21A", "#3A2A05"),    // Folder
        F("search", "\uE721", "#0F6CBD"),              // Search
        N("json", "\uE60B", "#CBA74B", "#2E2208"),     // seti-json
        F("disk", "\uEDA2", "#5C6B7A"),                // HardDrive
        F("media", "\uE714", "#C239B3"),               // Video
        F("music", "\uEC4F", "#E5484D"),               // MusicNote
        F("mail", "\uE715", "#2E77D0"),                // Mail
        F("chat", "\uE8BD", "#4F6BED"),                // Message
        N("postgres", "\uE76E", "#336791"),            // dev-postgresql
        N("mysql", "\uE704", "#00758F"),               // dev-mysql
        N("redis", "\uE76D", "#D82C20"),               // dev-redis
        N("sqlite", "\uE7C4", "#0F80CC"),              // dev-sqlite
        N("database", "\uF1C0", "#336791"),            // fa-database
        F("http", "\uE774", "#0E8A8A"),                // Globe
        N("infra", "\uE8BD", "#7B42BC"),               // dev-terraform
        N("tmux", "\uE94C", "#1BB91F"),                // dev-tmux
        M("rex", "Rx", "#6E56CF"),
        N("claude", "\uEC82", "#D97757"),              // cod-claude
        N("codex", "\uEC81", "#F4F4F5", "#111113"),    // cod-openai
        N("gemini", "\uF51B", "#5B7FD8"),              // oct-sparkle_fill
        M("opencode", "oc", "#3A3F4B"),
        N("copilot", "\uF4B8", "#8250DF"),             // oct-copilot
        M("cursor", "Cu", "#111111"),
        M("aider", "Ai", "#14B866"),
        M("amp", "Am", "#F34E3F"),
        F("llm", "\uE99A", "#4B5563"),                 // Robot
        N("agent", "\uEC20", "#0067C0"),               // cod-robot
        F("fun", "\uE7FC", "#E3008C"),                 // Game
    }.ToDictionary(s => s.Key);

    /// <summary>Every icon, in catalogue order (Settings shows them as a sheet).</summary>
    public static IEnumerable<ProgramIconSpec> All => Specs.Values;

    /// <summary>The process names that show an icon, for its tooltip.</summary>
    public static IEnumerable<string> NamesFor(string key) => Aliases.Where(a => a.Value == key && a.Key != key).Select(a => a.Key);

    /// <summary>Process names and core icon keys → spec keys.</summary>
    private static readonly Dictionary<string, string> Aliases = BuildAliases();

    private static Dictionary<string, string> BuildAliases()
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        void Add(string key, params string[] names) { foreach (var n in names) map[n] = key; }
        Add("shell", "shell", "sh", "bash", "zsh", "dash", "ksh", "tcsh", "csh", "nu", "nushell", "xonsh", "elvish", "login");
        Add("fish", "fish");
        Add("pwsh", "pwsh", "powershell");
        Add("cmd", "cmd");
        Add("wsl", "wsl", "wslhost");
        Add("node", "node", "nodejs", "npx", "tsx", "ts-node", "nodemon", "vite");
        Add("npm", "npm");
        Add("pnpm", "pnpm", "pnpx");
        Add("yarn", "yarn");
        Add("bun", "bun", "bunx");
        Add("deno", "deno");
        Add("python", "python", "ipython", "pip", "pytest", "uv", "uvx", "poetry", "jupyter", "py");
        Add("ruby", "ruby", "irb", "bundle", "rails", "rake", "gem");
        Add("php", "php", "composer", "artisan");
        Add("perl", "perl", "cpan", "cpanm");
        Add("go", "go", "gopls");
        Add("rust", "rust", "cargo", "rustc", "rustup", "rust-analyzer", "bacon");
        Add("zig", "zig");
        Add("dotnet", "dotnet", "msbuild");
        Add("java", "java", "javac", "jshell", "mvn");
        Add("kotlin", "kotlin", "kotlinc");
        Add("gradle", "gradle", "gradlew");
        Add("scala", "scala", "sbt", "scala-cli");
        Add("clojure", "clojure", "clj", "lein");
        Add("haskell", "haskell", "ghc", "ghci", "stack", "cabal");
        Add("ocaml", "ocaml", "utop", "dune", "opam");
        Add("elixir", "elixir", "iex", "mix");
        Add("julia", "julia");
        Add("r", "r", "rscript", "radian");
        Add("dart", "dart", "flutter");
        Add("lua", "lua", "luajit");
        Add("swift", "swift", "swiftc");
        Add("nix", "nix", "nix-shell", "nix-build", "nixos-rebuild", "home-manager", "devenv");
        Add("build", "build", "make", "gmake", "cmake", "ninja", "meson", "gcc", "g++", "cc", "clang", "cl", "bazel", "just", "mise");
        Add("test", "test", "jest", "vitest", "playwright", "mocha");
        Add("vim", "vim", "vi", "gvim");
        Add("neovim", "neovim", "nvim");
        Add("helix", "helix", "hx");
        Add("emacs", "emacs", "emacsclient");
        Add("editor", "editor", "nano", "micro", "pico", "edit");
        Add("vscode", "vscode", "code", "code-insiders");
        Add("git", "git", "lazygit", "gitui", "tig", "jj");
        Add("github", "github", "gh");
        Add("monitor", "monitor", "htop", "btop", "top", "btm", "glances", "nvtop");
        Add("container", "container", "docker", "docker-compose", "lazydocker", "podman");
        Add("kube", "kube", "kubectl", "k9s", "helm", "kubectx");
        Add("ssh", "ssh", "mosh", "mosh-client", "et", "autossh");
        Add("tailscale", "tailscale");
        Add("pager", "pager", "less", "more", "bat", "most");
        Add("manual", "manual", "man", "tldr", "info");
        Add("logs", "logs", "tail", "journalctl", "lnav", "multitail", "dmesg");
        Add("wait", "wait", "sleep", "watch", "timeout");
        Add("files", "files", "ranger", "yazi", "nnn", "lf", "mc", "vifm", "broot", "xplr", "superfile", "spf");
        Add("search", "search", "fzf", "rg", "grep", "fd", "find", "ag", "ack", "sk", "television", "tv");
        Add("json", "json", "jq", "yq", "fx", "jless", "gron");
        Add("disk", "disk", "ncdu", "du", "df", "dust", "duf", "gdu", "diskonaut");
        Add("media", "media", "mpv", "ffmpeg", "ffplay", "yt-dlp", "vlc", "cvlc");
        Add("music", "music", "cmus", "ncspot", "spotify_player", "mpd", "ncmpcpp", "musikcube", "termusic");
        Add("mail", "mail", "neomutt", "mutt", "aerc", "himalaya", "alpine");
        Add("chat", "chat", "weechat", "irssi", "senpai", "gomuks", "iamb");
        Add("postgres", "postgres", "psql", "pgcli");
        Add("mysql", "mysql", "mariadb");
        Add("redis", "redis", "redis-cli");
        Add("sqlite", "sqlite", "sqlite3", "duckdb");
        Add("database", "database", "mongosh");
        Add("http", "http", "curl", "wget", "xh");
        Add("infra", "infra", "terraform", "tofu", "pulumi", "ansible", "az", "aws", "gcloud");
        Add("tmux", "tmux", "zellij", "screen");
        Add("rex", "rex");
        Add("claude", "claude", "claude-code");
        Add("codex", "codex");
        Add("gemini", "gemini");
        Add("opencode", "opencode");
        Add("copilot", "copilot", "gh-copilot");
        Add("cursor", "cursor", "cursor-agent");
        Add("aider", "aider");
        Add("amp", "amp");
        Add("llm", "llm", "ollama", "llama-server", "llama-cli");
        Add("agent", "agent");
        Add("fun", "fun", "sl", "cmatrix", "cowsay", "lolcat", "asciiquarium");
        return map;
    }

    /// <summary>Resolve the icon for a pane: core icon key first, then the program name, else Shell.</summary>
    public static ProgramIconSpec Resolve(string? icon, string? program = null)
    {
        var byKey = Lookup(icon);
        if (byKey is not null && byKey.Key != "shell") return byKey;
        return Lookup(NormalizeProgram(program)) ?? byKey ?? Specs["shell"];
    }

    private static ProgramIconSpec? Lookup(string? name) =>
        string.IsNullOrEmpty(name) ? null
        : Specs.TryGetValue(name, out var spec) ? spec
        : Aliases.TryGetValue(name, out var key) ? Specs[key]
        : null;

    /// <summary>Lower-case, strip the path, .exe/.cmd/.bat/.ps1 and a version suffix (python3.12 → python).</summary>
    public static string? NormalizeProgram(string? program)
    {
        if (string.IsNullOrWhiteSpace(program)) return null;
        var name = program.Trim().Split(' ')[0];
        name = name[(name.LastIndexOfAny(['/', '\\']) + 1)..].ToLowerInvariant();
        foreach (var ext in new[] { ".exe", ".cmd", ".bat", ".ps1" })
            if (name.EndsWith(ext)) name = name[..^ext.Length];
        if (name.StartsWith('-')) name = name[1..]; // login shells: "-zsh"
        int end = name.Length;
        while (end > 0 && (char.IsDigit(name[end - 1]) || name[end - 1] == '.')) end--;
        return end > 0 && end < name.Length && Aliases.ContainsKey(name[..end]) ? name[..end] : name;
    }
}

public static class ColorMath
{
    public static Color Hex(string hex)
    {
        hex = hex.TrimStart('#');
        uint v = Convert.ToUInt32(hex, 16);
        return hex.Length == 8
            ? Color.FromArgb((byte)(v >> 24), (byte)(v >> 16), (byte)(v >> 8), (byte)v)
            : Color.FromArgb(255, (byte)(v >> 16), (byte)(v >> 8), (byte)v);
    }

    public static Color WithAlpha(Color c, double alpha) => Color.FromArgb((byte)Math.Round(Math.Clamp(alpha, 0, 1) * 255), c.R, c.G, c.B);

    public static Color Lighten(Color c, double amount)
    {
        var (h, s, l) = ToHsl(c);
        return FromHsl(h, s, Math.Clamp(l + amount, 0, 1), c.A);
    }

    public static double Luminance(Color c)
    {
        static double Lin(byte v) { double s = v / 255.0; return s <= 0.03928 ? s / 12.92 : Math.Pow((s + 0.055) / 1.055, 2.4); }
        return 0.2126 * Lin(c.R) + 0.7152 * Lin(c.G) + 0.0722 * Lin(c.B);
    }

    /// <summary>Move lightness until the relative luminance reaches the target (only in one direction).</summary>
    public static Color WithLuminance(Color c, double target, bool lighten)
    {
        var (h, s, l) = ToHsl(c);
        double lum = Luminance(c);
        if (lighten ? lum >= target : lum <= target) return c;
        double lo = lighten ? l : 0, hi = lighten ? 1 : l;
        for (int i = 0; i < 24; i++)
        {
            double mid = (lo + hi) / 2;
            double m = Luminance(FromHsl(h, s, mid, 255));
            if (m < target) lo = mid; else hi = mid;
        }
        return FromHsl(h, s, (lo + hi) / 2, c.A);
    }

    private static (double H, double S, double L) ToHsl(Color c)
    {
        double r = c.R / 255.0, g = c.G / 255.0, b = c.B / 255.0;
        double max = Math.Max(r, Math.Max(g, b)), min = Math.Min(r, Math.Min(g, b));
        double l = (max + min) / 2, h = 0, s = 0;
        if (max != min)
        {
            double d = max - min;
            s = l > 0.5 ? d / (2 - max - min) : d / (max + min);
            h = max == r ? (g - b) / d + (g < b ? 6 : 0) : max == g ? (b - r) / d + 2 : (r - g) / d + 4;
            h /= 6;
        }
        return (h, s, l);
    }

    private static Color FromHsl(double h, double s, double l, byte a)
    {
        double r, g, b;
        if (s == 0) r = g = b = l;
        else
        {
            static double Hue(double p, double q, double t)
            {
                if (t < 0) t += 1; if (t > 1) t -= 1;
                if (t < 1.0 / 6) return p + (q - p) * 6 * t;
                if (t < 1.0 / 2) return q;
                if (t < 2.0 / 3) return p + (q - p) * (2.0 / 3 - t) * 6;
                return p;
            }
            double q = l < 0.5 ? l * (1 + s) : l + s - l * s, p = 2 * l - q;
            r = Hue(p, q, h + 1.0 / 3); g = Hue(p, q, h); b = Hue(p, q, h - 1.0 / 3);
        }
        return Color.FromArgb(a, (byte)Math.Round(r * 255), (byte)Math.Round(g * 255), (byte)Math.Round(b * 255));
    }
}
