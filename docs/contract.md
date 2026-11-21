# Windex core ↔ UI contract

`rexcore.dll` (Rust, `core/`) owns everything protocol-, network- and terminal-emulation-shaped.
The WinUI app (`app/Windex`) owns everything pixel-, input-device- and settings-shaped.
The ABI is `core/include/rexcore.h`; its C# mirror is `app/Windex/Interop/RexCoreNative.cs`.
Change all three together.

```
 UI thread / render ──P/Invoke──▶ rexcore.dll ──WebSocket──▶ Rex server (local, https, or ssh -L forward)
        ▲                            │  per-host tokio tasks: control channel, data channels
        └──── event callback ◀───────┘  per-pane worker thread: libghostty-vt Terminal + frame builder
```

## Principles

- **The server owns layout and terminal content.** The core never invents layout; it normalizes the
  server's layout into the tree below. The UI never recolours terminal output.
- **Attachment never creates or destroys** sessions, tabs or panes. Only explicit commands do.
- **Failed input is never replayed.** A transport failure pauses affected panes (`frame.paused = 1`,
  `pane.paused = true` in the model) until they reconnect.
- **Full-snapshot model.** On any structural change the core emits the whole `Model` (it is small).
  The UI diffs by id. Terminal content never goes through JSON: it is the `rex_frame` cell buffer.
- **Pane handles** (`u64`, non-zero, never reused) identify a terminal across the ABI. The model maps
  `(host, session, block)` → `pane`. A block keeps its pane handle across reconnects.

## Events (callback `kind`)

| kind | pane | json |
|---|---|---|
| 1 MODEL | 0 | `Model` (below) |
| 2 PANE_DIRTY | pane | – (coalesced: at most one until the UI calls `rex_pane_frame_lock`) |
| 3 RESULT | 0 | `{"req":7,"ok":true,"data":{...}}` / `{"req":7,"ok":false,"error":"..."}` |
| 4 NOTICE | 0 | `{"level":"info|warning|error","title":"...","message":"...","host":"h1"?}` |
| 5 BELL | pane | – |
| 6 CLIPBOARD | pane | `{"text":"..."}` (OSC 52 copy request from the program; UI decides) |
| 7 NOTIFY | pane? | `{"kind":"done|needs_input|bell|osc9|osc777","title":"...","body":"...","host":"h1","session":"s","window":"w"}` |
| 8 LOG | 0 | `{"level":"debug|info|warn|error","target":"...","message":"..."}` |
| 9 DISCOVERY | 0 | `{"machines":[{"name":"dasher","dns":"dasher.tail1234.ts.net","ips":["100.x"],"os":"linux","online":true,"self":false,"rex":{"endpoint":"https://dasher","reachable":true}|null,"ssh":{"tailscale_ssh":true}}]}` |

## Model

```jsonc
{
  "type": "model",
  "revision": 42,                       // increments every emission
  "current": { "host": "h1", "session": "S_abc" } | null,   // what the window shows
  "hosts": [{
    "id": "h1",                          // UI-assigned id (from add_host), stable
    "name": "dasher",                    // display name
    "endpoint": "ssh://joshua@dasher",
    "kind": "ssh|https|http|unix|local",
    "state": "connecting|connected|reconnecting|offline|error",
    "error": "tailnet policy does not permit ..." | null,   // last failure, shown with Retry
    "server": { "version": "0.0.179...", "api": 13, "instance": "...", "label": "dasher" } | null,
    "can_upload": true, "can_download": true,   // the server's API revision takes them (11+ / 13+); see Transfers
    "sessions": [{
      "id": "S_abc", "label": "work", "attached": true,
      "word": "Done" | null, "tone": "accent|warning|danger" | null,   // folded tab activity
      "active_window": "W_1",
      "windows": [{                      // a Rex window == a tab in the UI
        "id": "W_1", "label": "Shell", "title": "zsh ~/src",   // title = what the tab should show
        "index": 0,
        "focused_pane": 17,              // pane handle of the server-focused block
        "zoomed_pane": 17 | null,
        "word": "Needs input" | null, "tone": "warning" | null,
        "progress": { "state": "working|paused|error|indeterminate", "percent": 40 } | null,
        "layout": Node,                  // see below
        "panes": [{
          "pane": 17, "block": "B_x",
          "title": "nvim main.rs",        // terminal title or generated program/dir title
          "program": "nvim",             // foreground program name (for the icon) or null
          "cwd": "/home/joshua/src" | null,
          "icon": "neovim",              // icon key the UI maps to a glyph (see design.md); "shell" default
          "status": { "word": "Needs approval", "tone": "warning", "message": "..." } | null,
          "progress": {...} | null,
          "paused": false,               // input paused (reconnecting)
          "owned": true,                 // this client owns the block (its size/theme apply)
          "server_cols": 120, "server_rows": 40,   // authoritative PTY size (resize APC)
          "exited": false
        }]
      }]
    }]
  }],
  "transfers": [{                       // file transfers in flight (see Transfers); gone once they end
    "id": "upload:4f2a…",                // the command's `id` (or "transfer:N" when it gave none)
    "kind": "upload|download",
    "host": "h1", "pane": 17,
    "name": "screenshot-20261118-161502.png",   // the file being moved now (a download's: the server's name, made safe to show)
    "index": 0, "count": 2,              // an upload batch: file index of count
    "bytes": 1048576, "total": 3145728,  // a download's total is the size when the server opened the file (a hint)
    "state": "starting|sending|finishing",   // finishing: an upload waits for the server to type the paths
    "remote": "/home/joshua/report.pdf" | null,   // download: the server's absolute path
    "local": "C:\\Users\\…\\report.pdf" | null    // download: where it is saved
  }]
}
```

Layout `Node` (normalized from the server's representation):

```jsonc
{ "kind": "pane", "pane": 17 }
{ "kind": "split", "id": "split:…",          // the server split_id (for set_split_ratio)
  "axis": "row|column",   // row = children left→right (Rex "horizontal"), column = top→bottom ("vertical")
  "ratio": 0.5,            // share of the first child
  "children": [ { "weight": 0.5, "node": Node }, { "weight": 0.5, "node": Node } ] }
```

Rex splits are binary (before/after); `weights` = `[ratio, 1-ratio]`. Floating layers are
ignored in v1 (only the tiled layer is laid out).

Weights in a split sum to 1. The UI lays children out proportionally, inserts gutters, and reports the
resulting grid size per pane with `rex_pane_resize`. A zoomed pane fills the window.

## Commands (`rex_core_command`, JSON; every command carries `"req": <u64>`)

| cmd | fields | result `data` |
|---|---|---|
| `add_host` | `id`, `name`, `endpoint`, `connect`(bool, default true) | – |
| `update_host` | `id`, `name`?, `endpoint`? (reconnects if endpoint changed) | – |
| `remove_host` | `id` (disconnects, forgets) | – |
| `connect_host` / `disconnect_host` | `id` | – |
| `open_session` | `host`, `session` (id or label) — attach + make current | – |
| `new_session` | `host`, `label`?, `cwd`? (explicit user action) | `{"session":"S_.."}` |
| `rename_session` | `host`, `session`, `label` | – |
| `kill_session` | `host`, `session` (explicit, confirmed by UI) | – |
| `new_tab` | `host`, `session`, `cwd`? | `{"window":"W_.."}` |
| `select_tab` | `host`, `session`, `window` | – |
| `rename_tab` | `host`, `session`, `window`, `label` | – |
| `close_tab` | `host`, `session`, `window` | – |
| `move_tab` | `host`, `session`, `window`, `index` | – |
| `split` | `pane`, `direction`: `right|left|down|up` | `{"pane":N}` |
| `close_pane` | `pane` | – |
| `focus_pane` | `pane` (server-side focus; UI also calls `rex_pane_focus`) | – |
| `focus_direction` | `pane`, `direction` | – |
| `zoom_pane` | `pane`, `zoom`: true/false/"toggle" | – |
| `set_split_ratio` | `host`, `session`, `split` (split id from the layout), `ratio` (0<r<1) — divider drag | – |
| `balance` | `host`, `session`, `window` | – |
| `app_focus` | `focused` (bool) — window activation; on gain the core claims (`own_block take`) the visible blocks | – |
| `clear_pane` / `reset_pane` | `pane` | – |
| `rename_pane` | `pane`, `label` | – |
| `swap_pane` | `pane`, `direction` | – |
| `send_text` | `pane`, `text` (no Enter added) | – |
| `reconnect_pane` | `pane` | – |
| `set_theme` | `theme`: `{ "background":"#rrggbb", "foreground":..., "cursor":..., "selection_background":..., "palette":[16 hex] }` | – |
| `discover` | – (runs `tailscale status --json` + probes; emits DISCOVERY) | – |
| `list_dir` | `pane`, `path`, `prefix`? (only names starting with it, ignoring case) | `{"entries":[{"name","dir"}],"status","path","truncated"}` (`status`: `ok`, `missing`, `not_dir`, `denied`) |
| `server_status` | `host` | raw server.status |
| `upload` | `pane`, `files`: `[{"path":"C:\\…","name"?,"temporary"?}]`, `id`? — one batch; RESULT when the server has typed the paths | `{"id","host","pane","files":[names],"bytes"}` |
| `download` | `pane`, `path` (remote: absolute, `~/…`, or relative to the pane's program's folder), `dest` (absolute local file), `id`? — RESULT once the file is in place | `{"id","host","pane","path":dest,"name","remote","bytes"}` |
| `cancel_transfer` | `id` (answered at once; the transfer's own RESULT then fails with `"canceled"`) | – |

Unknown commands fail with `ok:false, error:"unknown command"`.

## Transfers

Uploads and downloads move bytes over `rex.data.bytes.v1` streams (docs/protocol.md §4.6), each on
a task and data socket of its own, so they never hold up a pane. Progress is in the model's
`transfers` (refreshed at most every 100 ms); the outcome is the command's RESULT.

- **Upload** (`can_upload`: API revision 11+). The files of one command are one batch: once every
  file has arrived, the server types their paths (shell-quoted, space-separated) into the pane as one
  paste, as if the user had pasted them. Only regular files are taken; a folder fails the command
  (`"<name> is a folder; only files can be uploaded"`) before anything is sent. `name` defaults to the
  file's own name; `temporary: true` hands the file to the core, which deletes it when the upload ends
  however it ends (the UI uses it for a pasted picture saved as PNG). The server stores the files
  under `~/.local/state/rex/uploads/<server>/<block>/<uuid>/<name>` and removes them when the block
  closes; it refuses a file over 100 MiB (`too large: … more than the 104857600 a terminal accepts`).
  Every file of the batch is reserved before any is sent, so such a refusal fails the whole command
  at once (`"<name> was not accepted: <server's reason>"`) and nothing of the batch is sent or kept.
- **Download** (`can_download`: API revision 13+). Bytes go to `<dest>.partial`, which replaces `dest`
  only after the server's end-of-file mark and close 1000; anything else (and cancel) removes it and
  leaves `dest` as it was. A folder or a missing file fails with the server's reason.
- **Errors** arrive as the RESULT's `error` (`"canceled"` after `cancel_transfer`). Cancel closes the
  streams with 1000; nothing is typed and the server keeps nothing. A transfer needs the control
  connection that started it (the server cancels its stream when that connection closes, Rex Rune's
  API 13 notes), so a reconnect fails it with `Rex canceled the transfer …`. Nothing is retried.

## Frames

See `rex_frame`/`rex_cell` in the header. Rules for the renderer:

- Cell `(col,row)` is `cells[row * cols + col]`. Text is UTF-16 in `text[text_off .. text_off+text_len]`.
- `REX_CELL_SPACER` cells draw nothing (the preceding `WIDE` cell's glyph covers both columns).
- `REX_CELL_DEFAULT_BG`: the cell's background equals the terminal default; the UI may skip painting it
  so the pane card's surface (and its translucency) shows through.
- Colours are final (inverse applied, palette resolved). Faint = draw fg at ~60% opacity.
- `cursor_x/cursor_y` are `-1` when hidden or scrolled away. Blinking is a hint; terminal content never animates.
- `viewport_top`, `total_rows` and `rows` give the scrollbar: at bottom when `viewport_top + rows == total_rows`.
- `paused` → draw an "Input paused — reconnecting" strip; input calls return 1.
- `cursor_shape` is what to draw: the program's DECSCUSR choice, else the pane's default from
  `rex_pane_set_default_cursor` (the Cursor shape setting; block until set). The UI does not
  second-guess it.
- **Find in terminal** (ABI 2). `matches[0..match_count]` are the matches on the viewport, one
  `rex_match` per row a match touches (`row`, inclusive `col0..col1`, a wide glyph's two cells
  included), top to bottom; `current = 1` marks the current match's spans. `search_total` counts
  every match in the screen and scrollback found so far, `search_current` is the current match's
  place counting from the newest (1 = the bottom-most; 0 = none), `search_busy` means scrollback is
  still being read (counts may grow), `search_active` that a query is set, `search_error` why it
  cannot be searched for (`rex_search_error`; no matches then). All zero without a query.

## Find in terminal

`rex_pane_search(pane, query, flags)` searches on the pane's worker thread; it never blocks the
caller or the network. `flags`: `MATCH_CASE` (1), `REGEX` (2, Rust `regex` syntax); a literal,
case-insensitive search (Unicode case folding) otherwise. The call only checks the pattern's
syntax (well under a millisecond) and returns -3 for an invalid regular expression (the pane's
search is cleared). The worker compiles it (a big pattern takes tens of milliseconds); one too big
to compile (over 4 MB of automata) shows in frames as `search_error = REX_SEARCH_TOO_COMPLEX`.
`NULL`/`""` stops searching and clears the highlights (the UI does this when the find bar closes).

- Matches stay within a logical line (soft-wrapped rows joined, up to 64 rows) and never cross a
  hard line break; trailing blanks are not text. Scrollback is read once, newest first, a few ms
  per worker iteration, then kept up to date as output arrives (rows scrolling out of the screen,
  pruned history); the screen itself is re-read after every change. A resize, a screen switch, a
  reset or a cleared scrollback starts the reading over; the current match survives a resize or
  reflow (a tracked cell follows it), so the count keeps the user's place.
- A new query makes the last match at or above the viewport's bottom row current (else the first
  one below) and scrolls it into view. A query that replaces another (typing on, toggling an
  option) starts from where the previous one's current match was, wherever the viewport is: the
  first match on that row at or after it, else the last one before it.
- `rex_pane_search_step(pane, direction)`: `< 0` the next match up (older output), `> 0` the next
  one down; both wrap. The viewport scrolls to centre the match unless it is entirely visible.
  The UI's Find next (Enter, F3) is up, Find previous (Shift+Enter, Shift+F3) down.

## Input mapping (UI side)

- Keys: the UI maps Windows virtual keys / scan codes to W3C `code` names (physical), fills `key` and
  `text` from `CharacterReceived`/`ToUnicode`, and sends press/repeat/release. App shortcuts are consumed
  by the UI first and never reach the core.
- IME: composition is shown by the UI; committed text goes through `rex_pane_text`.
- Mouse: when `frame.mouse_tracking` and Shift is not held, pointer events go to `rex_pane_mouse` with
  grid-relative pixels; otherwise the UI drives selection with `rex_pane_select` (PRESS with the click count 1/2/3,
  DRAG while the button is held — also outside the grid for autoscroll — RELEASE, CLEAR, ALL) using
  grid-relative pixels, and copies with `rex_pane_selection_text`.
- Wheel without tracking scrolls the viewport (`rex_pane_scroll` DELTA, 3 rows per notch).
