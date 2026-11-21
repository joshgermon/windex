# rexcore notes (core track)

The Rex protocol client inside `rexcore.dll`. The authority for shapes is `docs/contract.md`
(core↔UI) and `docs/protocol.md` (wire). This file says how the core is put together, what has been
checked against a real server, and what the server does that you would not guess.

## Module map

```
core/src/
  core.rs        Core: options, the tokio runtime, the Panes registry, command entry point
  ffi.rs         C ABI (include/rexcore.h); every export catches panics
  pane.rs        pane worker thread: one libghostty-vt Terminal, frame publishing, BEL coalescing
  frame.rs       Terminal → rex_frame cell buffer
  selection.rs   local selection on the pane's terminal
  search.rs      find in terminal: logical lines → regex matches, incremental scrollback reading,
                 pruning-aware positions, the current match and its viewport spans
  events.rs      Emitter: the UI callback, its shutdown gate, LOG level filter (options.log_level)
  remote/
    mod.rs       Shared state, Remote (command/shutdown), coalesced MODEL publisher (≤30/s)
    endpoint.rs  ssh:// | https:// wss:// | http:// ws:// (bare `user@host` = ssh)
    ssh.rs       ssh.exe -N -T -L 127.0.0.1:<free port>:<remote socket>, socket discovery,
                 stderr reason, CREATE_NO_WINDOW, kill-on-close job object
    transport.rs WebSocket open (TCP, rustls for wss, keepalive), subprotocol echo check
    control.rs   control channel: hello/WELCOME, pipelined requests matched by id, events
    host.rs      one task per host: connect → bootstrap → events → reconnect (2/4/8 s)
    state.rs     hosts/sessions/views/blocks; reconcile (handles, workers, streams, claims)
    model.rs     Model JSON; server layout → binary split tree
    link.rs      HostLink (current connection) + PaneLink/RemoteInput (PaneInput impl)
    data.rs      data channel task per streamed block: ticket/HELLO, RESUME, close codes
    stream.rs    snapshot boundary scan + Rex APC filter (pure, unit-tested)
    keys.rs      W3C code → Rex key names, mods/consumed_mods/text/unshifted_codepoint
    activity.rs  titles (terminal or "program - ~/dir"), program + icon key, status words,
                 progress, bell/done "unseen" (ported from Rex Rune, MIT)
    commands.rs  every contract command → one RESULT
    transfer.rs  uploads / downloads over rex.data.bytes.v1 (upload_begin, download_begin), the model's
                 `transfers`, cancel (ported from Rex Rune src/upload.rs and its API 13 download notes)
    discover.rs  tailscale status --json + Rex probes (443, 7297) → DISCOVERY
  bin/probe.rs   rexcore-probe: drives the DLL through the C ABI (local / model / remote / discover / transfer)
tests/lifecycle.rs  C ABI tests: no callback after rex_core_free (also when freed from inside a
                    callback), BEL coalescing
tests/search.rs     C ABI tests: find in terminal through frames (scroll to the match, steps that
                    wrap, case/regex, a refused pattern, closing clears, highlights through new
                    output) and the default cursor shape against DECSCUSR
```

Ported code (Rex Rune, MIT): socket discovery script and stderr reason (`ssh.rs`), snapshot/APC
parsing (`stream.rs`), key naming (`keys.rs`), activity/title rules (`activity.rs`), tailnet parsing
(`discover.rs`). Each file says so in its header. The vendored `rexrs` SDK supplies the typed
`session.bootstrap` request, `Layout` builder and the view/bootstrap/split/window response types;
its one-request-at-a-time client is not used (see below).

## Threading

- **FFI threads** never wait on the network. `rex_core_command` parses and either answers at once
  (host bookkeeping) or spawns a task; its RESULT arrives later. Input (`rex_pane_key/text/paste/
  mouse/focus/resize`) builds a JSON request and pushes it onto the control connection's unbounded
  writer queue; it returns 1 when the pane is paused (no live control connection or data stream).
- **tokio runtime** (2 workers, owned by `Core`): per host one actor task (`host::run`), per
  control connection a reader and a writer task, per streamed block a data task, the MODEL
  publisher, command tasks.
- **Pane workers** (one OS thread per registered pane, from `pane.rs`) own the libghostty
  `Terminal`. Data tasks send them `Install(snapshot decoder closure)`, `Bytes`, `With(resize/theme)`
  and `Paused`. Snapshots are decoded on the worker (Terminal is `!Send`).
- **State** (`Shared::state`, one `parking_lot::Mutex`) is never held across an await and no UI
  callback is invoked while it is held (MODEL is serialized under the lock, emitted after).
  Lock order: state → panes registry. FFI input paths take neither.
- **Control requests are pipelined.** The SDK's client is strictly one-in-flight; typing over SSH
  would then pay one RTT per key. Here every request gets an id; the server runs the ordered methods
  (`client.key`, `block.event`, focus/layout methods, `own_block`) in arrival order (§2.4), so a
  claim followed by a key press arrives in that order. Responses complete a waiter or a callback on
  the reader task; events go to the host actor in wire order.
- **Shutdown** (`rex_core_free`): each host actor ends its streams with close 1000, closes control
  with a close frame and kills `ssh.exe` (waits ≤2 s); frames the UI left locked are unlocked; every
  pane worker is told to stop — it drops whatever is still queued — and joined (≤1 s together).
  Last, the emitter's gate closes: it waits for callbacks in flight and no callback starts after
  it, so nothing reaches the UI once `rex_core_free` returns, even from a worker that outlived the
  join. A Windows job object with KILL_ON_JOB_CLOSE kills any ssh.exe left behind if the process
  dies.
- **Freeing from inside a callback** (any thread): the gate closes at once (it cannot wait for the
  callback it is in) and the teardown runs on a thread of its own, since a core runtime thread can
  neither block on the runtime nor drop it.
- **Callback threads.** Events come from runtime threads and pane workers, and a command that is
  answered without the network (`ping`, `add_host`, `app_focus`, ...) emits its RESULT on the
  caller's thread before `rex_core_command` returns.

## Behaviour worth knowing

- **Panes.** Every block in any known view gets a stable handle (`(host, block)` → u64, kept for
  the host's lifetime, never reused). Terminal blocks of attached sessions — and of the current
  session even before its attach completes, so the UI can size them — get a pane worker. Blocks
  visible in the current session's active window (zoom honoured) get a data stream once the session
  is attached; streams of hidden blocks stay open until the block leaves the views.
- **Sidebar.** Views are fetched for every session (not only attached ones) and kept fresh through
  `session_view_changed`; title/process/program_status are read once per block per connection.
- **Current** = `open_session`'s choice, else the first session of the first connected host. A
  host that is reconnecting keeps its selection. Nothing is claimed before the first
  `app_focus {focused:true}`, so the UI can restore its last session (`open_session`) before it
  reports focus; the app does exactly that (MainWindow `TryRestoreSession` → `FinishRestore`).
- **Ownership.** `app_focus {focused:true}` (on a focus *gain*) claims each visible pane once with
  `own_block take`; newly visible panes are claimed once while focused. Another client taking a pane
  back is left alone until the next focus gain or input here (as Rex Rune). Press input (key press/
  repeat, paste, IME text, mouse press) claims first. The claimed set survives reconnects: a resumed
  client gets its blocks back from the server unless someone took them meanwhile, and a pane taken
  during a network blip stays taken. `update_host` to a new endpoint and `remove_host` forget the
  host's claims (Rex Rune: a new endpoint is a new host).
- **Input return codes.** 0 queued; 1 paused (no live control connection or data stream); −1 when a
  request is refused locally for another reason (logged).
- **Paste** (and IME text) goes out as `block.event paste`; a paste whose JSON is bigger than the
  server's `max_message_bytes` (1 MiB on dasher) is sent in ordered pieces, each ending after a
  newline where one falls in its second half. With bracketed paste on, the program sees consecutive
  pastes.
- **Bell.** BELs parsed from the PTY stream are coalesced per pane: the first rings at once, more
  within 100 ms ring once at the end of that window (one REX_EV_BELL, not one per byte).
- **Snapshots.** A snapshot that fails to decode leaves the pane's old terminal (or none) in place
  and its stream starts over with a fresh ticket and snapshot (1, 2, 4, 8 s apart while it keeps
  failing).
- **connect_host** while a connection attempt is already running is a no-op (the attempt goes on);
  during a retry backoff it retries at once; in `error`/`offline` it starts over with 3 attempts.
- **Size.** `rex_pane_resize` stores the wanted grid; `…terminal.resize` goes out only when we own
  the block, its stream is live and the size changed (re-sent after every stream (re)connect). Panes
  render the server's size from the resize APC; `server_cols/rows` in the model come from it (or from
  `size_changed` events for unstreamed blocks).
- **Theme.** `set_theme` converts the UI theme (16-colour palette) to `set_theme` args (`i=#rrggbb`,
  `scheme` from background luminance) and sends it to every owned block, and to blocks as they become
  ours. The pane is recoloured only by the server's theme APC.
- **Query replies.** The pane worker's `PtyWrite` (DA/DSR answers) is discarded for remote panes:
  the server's own terminal answers the program; answering here would double-reply.
- **Reconnect.** Control loss → host `reconnecting`, panes paused, data tasks drop their sockets;
  the actor retries after 2/4/8 s with the reconnect token (bootstrap re-attaches the sessions we had
  attached; every other session's view is re-read, events were missed), data tasks RESUME with their
  token and byte offset as soon as the new connection is up; a refused RESUME → fresh ticket and
  snapshot (new terminal). A host never reached fails 3 times, then shows `error` with ssh's last
  stderr line (a Tailscale SSH "visit this link" check shows up there) until `connect_host`.
  `disconnect_host`, an endpoint change and shutdown end data streams with 1000 and close control
  with a close frame (the server forgets the client), so the next connect is a new client.
- **Resilience.** A panic in a response callback or an event handler is caught and logged; it never
  takes the connection or the host task down. Nothing panics across the FFI (`ffi.rs` guards).
- **Notifications.** NOTIFY `done` / `needs_input` / `bell` only for panes out of sight (not in the
  current session's active window, or the app unfocused); `osc9` / `osc777` always. BELL (coalesced)
  and CLIPBOARD come from the pane worker parsing the PTY bytes.
- **Transfers** (`transfer.rs`, contract "Transfers"). One task per `upload`/`download` command with
  its own `/api/data` socket (`rex.data.bytes.v1`, same HELLO/WELCOME as terminal streams), so a big
  file never holds up a pane's stream. An upload batch first reserves every file (`upload_begin`
  with `batch`/`index`/`count`) and opens each file's socket right after its reservation (tickets
  live 60 s), so a file the server refuses (over 100 MiB) fails the batch before a byte is sent;
  then it streams the files one after another, 64 KiB messages and one empty message each. Every
  socket is read by a task of its own (pings are answered) until the server closes every file of
  the batch with 1000 after typing the paths. A socket opened and left idle for 75 s before its
  file was sent still worked (live, 2026-11-18), so a slow first file does not cost the later ones. A
  download writes `<dest>.partial` through a 256 KiB buffer and renames it over `dest` only after the
  empty message and close 1000. Cancel (a `watch` flag every await of the transfer races) closes the
  streams with 1000 and removes the partial file; RAII guards remove partial and `temporary` files and
  the model entry also when the task is dropped at shutdown, and `Remote::shutdown` cancels transfers
  first. Progress: byte counters are atomics read under the state lock when the model is built; a
  transfer touches the model at most every 100 ms. `ref`s and batch ids are random v4 UUIDs (std's
  SipHash keys; no extra crate). Server close codes become sentences (`4000` → the pane was closed,
  `1001 …canceled` → the connection or session ended, `1011` → the server could not read or store it).
- **Find in terminal** (`search.rs`, ABI 2). Not Ghostty's own search: that one matches only
  byte-exact text with ASCII case folding, and the find bar needs match case and regular
  expressions too. Text is read cell by cell through grid refs (active rows from the bottom of the
  page list, history through the screen point space); about 1.1 M cells (10 MB of scrollback at
  120 columns) read in ~0.16 s in a debug build, spread over 4 ms steps between frames. Positions
  are absolute rows (screen row + rows pruned since the search started); a tracked grid ref on the
  newest scrollback row read measures pruning, and losing it (ED 3, RIS) restarts the search.
  A second tracked grid ref (the "place") sits on the current match's first cell; it follows
  reflow, so a restart after a resize re-picks the same match, and a replacing query is handed it
  (the next query starts there wherever the viewport went). The calling thread only parses the
  pattern (`regex-syntax`, under 1 ms even for `\w{200}`), so a bad pattern is reported at once
  (-3); the worker compiles it (`\w{50}` takes ~15 ms, `\w{200}` ~45 ms to hit the 4 MB size
  limit, which frames report as `REX_SEARCH_TOO_COMPLEX`). A step that fails ends the search with
  a LOG warning (the highlights go); the pane carries on.
- **Default cursor shape.** `rex_pane_set_default_cursor` is libghostty's default cursor style:
  it shows while the program has not chosen a shape and again after `CSI 0 SP q`; a program's
  DECSCUSR wins. The worker keeps it and applies it to every snapshot that replaces the terminal
  (snapshots carry the server terminal's "cursor is default" state).
- **Icons.** `pane.icon` is a docs/design.md §8.3 key (`shell`, `neovim`, `node`, `claude`, ...):
  status-report app → foreground program → interpreter → `agent` (unknown program reporting OSC 7501)
  → `shell`. `pane.program` is the raw program name.

## Verified (2026-11-18, against dasher: Rex 0.0.1794517219-g7ec4d1, API 13, ssh://joshua@dasher)

`rexcore-probe remote ssh://joshua@dasher` (release build, ~35 s, all through the C ABI) checks:

| | Check |
|---|---|
| h | unreachable `ssh://nobody@windex-no-such-host.invalid`, plus a `connect_host` during the first attempt → exactly 3 attempts, `error` with ssh's reason |
| a | connect through ssh.exe (remote socket discovered), model summary hosts → sessions → tabs → panes + layout |
| b | `new_session windex-test-core-1`, `open_session`, first frame shows the zsh prompt; `app_focus` + `rex_pane_resize 100x30` → owned, server size and frame 100×30 |
| c | `echo hello-windex` typed key by key + Enter → output line; `rex_pane_paste` + Enter → `pasted-42`; `seq 1 200000` (1.3 MB) rendered in 0.26 s; a 1,638,400-byte paste (more than one 1 MiB message) into `wc -c` → `1638400` |
| c2 | with `cat -v` and DECSET 1000/1006/1004: focus out/in → `^[[O^[[I`, right press/release → `^[[<2;1;1M^[[<2;1;1m`, wheel notch → `^[[<64;5;3M`; Ctrl+C ends `cat` |
| d | split right → row split, 2 panes, both render; `set_split_ratio 0.3`; zoom on/off; `rename_tab`; `new_tab` + `select_tab`; `close_pane`; resize 90×25 reaches the server |
| d2 | `rename_session`, `rename_pane`, `send_text`, `list_dir /`, `server_status`, split down + `focus_direction` + `focus_pane` + `swap_pane` + `set_split_ratio` + `balance`, a pane made by `rex split` (CLI) is claimed while focused, `move_tab`, `set_theme` (theme APC turns the frame background #102030), `clear_pane`, `reset_pane`, `reconnect_pane` (fresh snapshot, typing works) |
| e | kill our ssh.exe → `reconnecting`, frame `paused=1` → reconnected in ~2.5 s (client resumed, data RESUMEd, no new snapshot) → new output renders |
| e2 | `disconnect_host` → `offline`, frame paused, `rex_pane_key` returns 1 → `connect_host` → new client, fresh snapshots, typing works |
| e3 | a second client (second core in the probe) opens the session, focuses and takes the pane at 70×20; only our ssh.exe is killed; after our reconnect the pane is still the other client's at 70×20 (we stay focused, send nothing); then typing here claims it back at our 90×25 |
| e4 | `update_host` to the explicit socket path (`ssh://joshua@dasher/run/user/1000/rex/server.sock`) → new client; the visible pane is claimed again while focused; the first keys typed arrive |
| g | `app_focus false`, then `kill_session` on the probe's session; leftovers named `windex-test-core-*` from interrupted runs are killed first; nothing else is touched |
| f | `discover`: 10 tailnet machines with OS, online, Tailscale SSH; `joshs-macbook-pro-rex` answers Rex over https (TLS + hello verified) |

Also: `cargo test` (48 unit tests: keys, layout normalization, APC filter/snapshot boundary,
endpoints, control framing, activity/titles/icons, balance/swap geometry, visible blocks, view
revisions, current-session choice, discovery parsing, paste pieces/JSON escaping, a snapshot that
fails to decode is reported to its stream (nothing installed); 3 C ABI tests in
`tests/lifecycle.rs`: a UI callback stalled past the worker join with output still queued → 0
callbacks after `rex_core_free`; `rex_core_free` from inside a callback; 20 000 BELs → ≤6 BELL
events, a lone BEL later rings at once), `cargo clippy --all-targets -- -D warnings` clean,
`rexcore-probe local` unchanged. The lifecycle test fails (1 late callback) with the gate and the
worker stop flag reverted.

The independent review harness (Python over the C ABI), re-run against this build, also passes:
OSC 7501 status words, progress, program icons, hidden-tab bell and OSC 9 notifications, IME text,
multi-line bracketed paste, a second client's ownership and sizes, reconnect while another client
owns, `rex_core_free` while connecting and mid-burst (0 late callbacks), `remove_host` while
connected.

A direct endpoint (`http://127.0.0.1:<port>` over a hand-made `ssh -L` to the server socket, no
ssh inside the core): connect, open a disposable session, type; killing the forward →
`reconnecting`, pane paused; forward back → client resumed with its token, typing works.

`rexcore-probe transfer ssh://joshua@dasher` (release, ~13 s, disposable session
`windex-test-files-probe`, 2026-11-18):

| | Check |
|---|---|
| t1 | host `can_upload`/`can_download` (API 13); a two-file batch (`notes windex.txt`, a 300 KB PNG stored as `screenshot test.png`, `temporary`) is listed in `transfers` with `count 2`, leaves the model when done, its temporary file is deleted; the server typed both paths (`…/uploads/<server>/<block>/<uuid>/notes\ windex.txt …`) after `sha256sum ` and the hashes match the local files |
| t2 | downloading both typed paths back gives identical bytes |
| t3 | a 50 MiB upload: 6 progress updates in the model, `cancel_transfer` at ~14 MB → RESULT `canceled`, nothing typed, no `big-50mb*` left under the server's uploads |
| t4 | the whole 50 MiB upload (1.3 s, ~40 MB/s over Tailscale SSH), sha256 matches on the server |
| t5 | `download dl.bin` (relative to the shell's folder; 3 MB of urandom): `remote` is the absolute path, sha256 matches |
| t6 | a 2 GiB sparse download canceled once 4 MB arrived: RESULT `canceled`, no file and no `.partial` left |
| t7 | uploading a folder, downloading a folder or a missing file, and canceling an unknown id all fail with a reason; `list_dir` with `prefix: "DL."` lists only `dl.bin` (case ignored), a missing folder has `status: "missing"` |
| t8 | a batch of a small file and a 101 MiB file fails at once with `t8-huge.bin was not accepted: too large: …`; 0 bytes were sent, nothing typed, nothing stored under the server's uploads |

`cargo test` adds 13 transfer tests: id format, hostile server names made safe to show, only regular
files, temporaries removed, the download end rule (empty message then 1000), only 1000 means typed,
and against a local server speaking the bytes protocol: an upload streamed whole and finished by
1000, a refused upload's reason, canceling an upload closes with 1000, a download replaces its
destination only when complete, a cut-short download leaves it alone, canceling a download removes
the partial file, a dropped partial file is removed.

Not exercised against a live session: a full `https://` attach (only the TLS hello of discovery),
`client_perform_requested` (refused automatically), OSC 52 clipboard.

## Server quirks (this build, 7ec4d1)

- **Left-button mouse reports are dropped** by the server (protocol.md §5.5): press/release
  `left` never reach the PTY; middle, right, motion and wheel do. Windex still sends them.
- `session.set_block_label ""` does not restore the default `shell` label: the server names the
  block `Block 1a2b3c4d`, which then shows as the pane title.
- `…terminal.title` often has no `owner` (a title set before the server tracked it), so the shell's
  `user@host:~` title shows instead of the generated `zsh - ~` until the shell sets a new title.
- A session can have no windows at all (`amber-sierra` on dasher: revision 274, `windows` absent).
- After a user disconnect (close frame) every data RESUME is refused with reason 3 — by design, the
  client is gone — so a fresh snapshot follows; after a dropped connection RESUME works.
- A REJECT reason 3 also comes for a block closed while its stream was opening.
- `server.status` (the API) names the socket under `listeners[] {endpoint: "unix://…", primary}`;
  the CLI's `rex server status --json` also has a top-level `endpoint`.
- A 1.5 MiB single-line paste into zsh's line editor works but keeps zsh busy for a long time
  (it redraws the whole line); that is zsh, not the transport.
- The resize APC reaches the model slightly before the pane worker has resized its terminal; a UI
  comparing `server_cols` with `frame.cols` must expect a frame or two of lag.
- **Uploads** land in `~/.local/state/rex/uploads/<server instance>/<block id>/<uuid>/<name>`; they
  outlive the client that sent them and go when the block closes. The paths are typed with
  backslash-escaped spaces. `upload_begin` refuses a file over 100 MiB: `too large: the file is
  419430400 bytes, more than the 104857600 a terminal accepts` (code `too_large`).
- `download_begin` errors read well as they are (`not found: /tmp/x does not exist`, `not a regular
  file: /tmp is a directory`), so the core passes them through.

## Contract notes

- Extra fields beyond docs/contract.md (additive, the UI may ignore them): DISCOVERY
  `ssh.endpoint` (`ssh://<dns>`), NOTIFY `pane`.
- rexcore.h says the callback runs on core worker threads; RESULTs of commands answered without
  the network also arrive on the caller's thread, inside `rex_core_command` (the UI marshals every
  event to its dispatcher, so this is harmless).
- `zoom_pane` takes `true`, `false` or anything else (e.g. `"toggle"`) to toggle.
- `move_tab.index` is the tab's final position. `focus_direction` first focuses `pane` on the server.
- `new_session` registers the session at once (no wait for `session_created`), so `open_session` can
  follow immediately; it does not change `current`.
- Not implemented (optional in v1): the keymap layer (`client.keymap`, Lua bindings), floating
  layers, conditional downloads (`if_none_match`), a local Windows named-pipe server (`unix://` and `local` endpoints are
  refused with a message).

## Running

```powershell
. .\scripts\env.ps1
cd core
cmd /c "cargo test 2>&1"
cmd /c "cargo clippy --all-targets -- -D warnings 2>&1"
cmd /c "cargo build --release 2>&1"                 # target\release\rexcore.dll
.\target\release\rexcore-probe.exe local
.\target\release\rexcore-probe.exe model ssh://joshua@dasher      # read-only summary
.\target\release\rexcore-probe.exe remote ssh://joshua@dasher     # end-to-end, disposable session
.\target\release\rexcore-probe.exe discover
.\target\release\rexcore-probe.exe transfer ssh://joshua@dasher  # uploads/downloads, disposable session
```
