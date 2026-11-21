// Static terminal screens for MockCore's panes. Markup: "§x" switches the colour role until the
// next switch; roles: f fg, d dim, k bright-black, r g y b m c (normal), R G Y B M C W (bright,
// bold), "§." resets; "█" marks the cursor. ToAnsi turns a screen into VT output for a real
// terminal (MockCore feeds it to a local libghostty pane, so the demo uses the real renderer).
using System.Text;
using System.Text.RegularExpressions;

namespace Windex.Core;

public static class MockScreens
{
    public static string Get(string? id) => id switch
    {
        "codex" => Codex,
        "zsh-git" => ZshGit,
        "bun-dev" => BunDev,
        "git-log" => GitLog,
        "htop" => Htop,
        "fish" => Fish,
        "snake" => Snake,
        "claude" => Claude,
        "deploy" => Deploy,
        "cargo" => Cargo,
        _ => ZshNew,
    };

    private static readonly string Codex = Box(51,
        "§W>_ OpenAI Codex§k (v0.58.0)",
        "",
        "§dmodel:     §fgpt-5-codex high   §c/model§d to change",
        "§ddirectory: §f~/src/windex") + """


§d›§f Fix the reconnect backoff so a dropped SSH forward
  recovers without replaying input.

§G•§W Ran §bgit§f diff §d--§f core/src/remote.rs §d&& §bcargo§f test §d-p§f rexcore
  §d└ diff --git a/core/src/remote.rs b/core/src/remote.rs
    test result: §Gok§d. 42 passed; 0 failed; finished in 3.1s

§G•§W Edited §fcore/src/remote.rs §G+18 §R-4
    §d212 §f    fn reconnect(&mut self) -> Result<()> {
    §d213 §G+       self.backoff.reset_after(Duration::from_secs(30));
    §d214 §G+       self.pause_inputs();
    §d215 §f        let stream = self.dial()?;

§Y▌ Allow Codex to run §W`cargo build --release`§Y?
§Y▌ §f  §W1.§f Yes, run it  §d  2. Yes, and don't ask again  §d  3. No
§Y▌
§d  Press enter to confirm or esc to cancel
""";

    private const string ZshGit = """
§Bjoshua§d@§Bdasher §c~/src/windex §d(§mmain§d)
§G❯ §fgit status §d--short
§r M§f core/src/remote.rs
§r M§f core/src/pane.rs
§g??§f docs/notes/backoff.md

§Bjoshua§d@§Bdasher §c~/src/windex §d(§mmain§d)
§G❯ §fcargo check §d-q
§d    Finished §f`dev` profile [unoptimized + debuginfo] target(s) in 4.21s

§Bjoshua§d@§Bdasher §c~/src/windex §d(§mmain§d)
§G❯ §f█
""";

    private const string BunDev = """
§d$ §fbun run dev
§W$ §fvite dev

  §GVITE §gv6.0.3§d  ready in §W412§d ms

  §G➜§W  Local:§f   §chttp://localhost:§C5173§c/
  §G➜§W  Network:§f §chttp://100.84.12.7:§C5173§c/
  §d➜  press §Wh + enter§d to show help

§d10:42:13 §c[vite]§f page reload §dsrc/routes/+page.svelte
§d10:42:31 §c[vite]§f hmr update §d/src/lib/Card.svelte
§d10:43:02 §c[vite]§f hmr update §d/src/lib/Sidebar.svelte §k(x2)
§d10:44:18 §y[vite]§f optimized dependencies changed. reloading
§d10:44:19 §c[vite]§f ✨ new dependencies optimized: §Y@fontsource/inter
""";

    private const string GitLog = """
§r*§f §y3f9c2ab§y (§CHEAD§y -> §Gmain§y, §Rorigin/main§y)§f Tabs: glass pill restyle
§r*§f §y81d07e4§f Sidebar: trouble-only host words
§r*§f §y5b1e9f0§f Panes: draggable gutters with live resize
§r*§f §y0c47d22§f Palette: grouped switcher and commands
§r*§g\§f   §y9a1b3c4§f Merge branch 'feat/add-host'
§r|§g *§f §ye77f210§f Add host: tailnet discovery list
§r|§g *§f §y42d9a8b§f Add host: endpoint validation
§r*§g/§f  §ycc01f5e§f Settings: window material and tint
§r*§f §y7b2e690§f Core: resize APC and ownership
§r*§f §y16fd3a2§f Initial commit
§d(END)
""";

    private const string Htop = """
  §C0§d[§G||||||||||||||§R|||       §f38.2%§d]   §CTasks: §W142§d, §W611§d thr; §G2§d running
  §C1§d[§G||||||||§R|               §f21.7%§d]   §CLoad average: §W0.84 §f0.71 §d0.66
  §C2§d[§G||||||||||||||||§R|||||  §f52.0%§d]   §CUptime: §W12 days, 04:13:55
  §C3§d[§G|||                     §f7.9%§d]
  §CMem§d[§G|||||||||||§b||||§y||     §f5.12G/15.5G§d]
  §CSwp§d[                        §f0K/4.00G§d]

§k    PID USER       PRI  NI  VIRT   RES   SHR S CPU% MEM%   TIME+  Command
§f  48121 joshua      20   0 1.2G  212M 48.1M §GR§f 31.4  1.3  4:12.07 §Wbun §fdev
§f  48170 joshua      20   0  880M  96M 31.0M S  8.2  0.6  0:41.88 §Wnode §fvite
§f   2210 joshua      20   0  412M  58M 12.4M S  2.1  0.4  1:02.55 §Wrex §fserver
§f  51002 joshua      20   0 23.1M  9.1M 4.2M S  0.7  0.1  0:00.34 §Whtop
§f    801 root        20   0  301M  24M 10.0M S  0.3  0.2  3:18.09 tailscaled
""";

    private const string Fish = """
§fWelcome to fish, the friendly interactive shell
§dType §chelp§d for instructions on how to use fish
§Bjoshua§f@§Bdasher §G~/s/site§f> bun test
§dbun test §fv1.1.38

§fsrc/lib/format.test.ts:
§G✓§f formatBytes §d> §frounds to one decimal §d[0.21ms]
§G✓§f formatBytes §d> §fhandles zero §d[0.03ms]
§G✓§f relativeTime §d> §fsays "now" under a minute §d[0.05ms]

§G 24 pass
§d  0 fail
§f Ran 24 tests across 6 files. §d[312.00ms]
§Bjoshua§f@§Bdasher §G~/s/site§f> █
""";

    private const string Snake = """
§CS N A K E   §CScore: §W43
§c+ - - - - - - - - - - - - - - - - - - - - - - - - - +
§c|                                                   |
§c|    §r●                                              §c|
§c|                                         §r●         §c|
§c|                    §r●                              §c|
§c|                                           §r●       §c|
§c|                                                   |
§c|                    §G■ ■ ■ ■ ■ ■                    §c|
§c|                    §G■         ■                    §c|
§c|                §G■ ■           ■              §r●     §c|
§c|                                §G■                  §c|
§c|                                §G◆                  §c|
§c|      §r●                                            §c|
§c|            §r●       §r●                 §r●            §c|
§c|                                                   |
§c+ - - - - - - - - - - - - - - - - - - - - - - - - - +
§dPaused · Space resume · R restart · Ctrl+C quit
""";

    private static readonly string Claude = Box(46,
        "§M✻§W Welcome to Claude Code!",
        "",
        "  §d/help for help, /status for your setup",
        "",
        "  §dcwd: §f~/work/api") + """


§d> §fMove settings loading into one module and keep
  the env overrides working.

§G●§f I'll read the current loader first.

§G●§W Read§f(src/config/load.ts)
  §d⎿  Read 142 lines

§G●§W Update§f(src/config/load.ts)
  §d⎿  Updated src/config/load.ts with §G31 additions§d and §R12 removals

§M✻ §MRefactoring settings loader… §d(48s · ↓ 2.1k tokens · esc to interrupt)
""";

    private const string Deploy = """
§d$ §f./deploy.sh production
§c==>§W Building image §fapi:2026.11.18-3
§d    [+] Building 41.2s (14/14) FINISHED
§c==>§W Pushing §fregistry.internal/api:2026.11.18-3
§d    pushed 6 layers
§c==>§W Rolling out §fdeployment/api §d(4 replicas)
§d    replica 1/4 §Gready
§d    replica 2/4 §Gready
§d    replica 3/4 §Gready
§d    replica 4/4 §RCrashLoopBackOff
§R==> Rollout failed: §fhealth check /healthz returned 503
§d    rolling back to api:2026.11.17-1 …
§Y    rolled back.
§d$ §f█
""";

    private const string Cargo = """
§d$ §fcargo build §d--release
§G   Compiling§f proc-macro2 v1.0.92
§G   Compiling§f unicode-ident v1.0.14
§G   Compiling§f libc v0.2.169
§G   Compiling§f serde v1.0.217
§G   Compiling§f tokio v1.43.0
§G   Compiling§f engine-core v0.4.0 §d(/home/joshua/src/engine/core)
§C    Building§f [=============>            ] 212/471: engine-render
""";

    private const string ZshNew = """
§Bjoshua§d@§Bdasher §c~
§G❯ §f█
""";
    /// <summary>
    /// A screen as VT output: roles become SGR (palette colours, so the declared theme applies),
    /// the cursor goes where "█" was (hidden when there is none).
    /// </summary>
    public static string ToAnsi(string markup)
    {
        var sb = new StringBuilder("\x1b[H\x1b[2J");
        (int Row, int Col)? cursor = null;
        var lines = markup.Replace("\r", "").TrimEnd('\n').Split('\n');
        for (int row = 0; row < lines.Length; row++)
        {
            if (row > 0) sb.Append("\r\n");
            sb.Append("\x1b[0m");
            string line = lines[row];
            int col = 0;
            for (int i = 0; i < line.Length; i++)
            {
                char c = line[i];
                if (c == '§' && i + 1 < line.Length) { sb.Append(Sgr(line[++i])); continue; }
                if (c == '█') { cursor = (row, col); continue; }
                sb.Append(c);
                if (!char.IsLowSurrogate(c)) col++;
            }
        }
        sb.Append("\x1b[0m");
        sb.Append(cursor is { } at ? $"\x1b[{at.Row + 1};{at.Col + 1}H\x1b[?25h" : "\x1b[?25l");
        return sb.ToString();
    }

    private static string Sgr(char role) => role switch
    {
        'd' => "\x1b[0;2m",
        'k' => "\x1b[0;90m",
        'r' => "\x1b[0;31m", 'g' => "\x1b[0;32m", 'y' => "\x1b[0;33m", 'b' => "\x1b[0;34m", 'm' => "\x1b[0;35m", 'c' => "\x1b[0;36m",
        'R' => "\x1b[0;1;91m", 'G' => "\x1b[0;1;92m", 'Y' => "\x1b[0;1;93m", 'B' => "\x1b[0;1;94m", 'M' => "\x1b[0;1;95m", 'C' => "\x1b[0;1;96m",
        'W' => "\x1b[0;1m",
        _ => "\x1b[0m",
    };

    /// <summary>A rounded box whose right border lines up whatever markup the lines carry.</summary>
    private static string Box(int width, params string[] lines)
    {
        var box = new StringBuilder();
        box.Append("§k╭").Append('─', width).Append("╮\n");
        foreach (var line in lines)
        {
            int visible = Regex.Replace(line, "§.", "").Length;
            box.Append("§k│ ").Append(line).Append(' ', Math.Max(0, width - 1 - visible)).Append("§k│\n");
        }
        box.Append("§k╰").Append('─', width).Append('╯');
        return box.ToString();
    }
}
