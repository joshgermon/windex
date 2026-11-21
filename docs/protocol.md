# Rex client protocol — implementation spec for Windex

Status: implementation-ready. Written 2026-11-18 from three sources, cross-checked against each other:

| Tag | Source |
| --- | --- |
| **LLMS:n** | The server's own API document, `GET /llms.txt`, fetched live from dasher (API revision 13). Copy: `_research/probe/out/llms.txt`. `GET /api/help` (JSON schemas) copy: `_research/probe/out/api_help.json`. |
| **RR:path:n** | Reference client rex-rune, `_research/rex-rune/` at `2612ce6` (2026-11-16). Paths are relative to it. |
| **SDK:path:n** | Vendored SDK `_research/rex-rune/vendor/rexrs/`. |
| **LIVE** | Observed on dasher 2026-11-18 through `ssh.exe -L 127.0.0.1:47297:/run/user/1000/rex/server.sock` with stdlib-Python and Bun clients (`_research/probe/*.py`, `probe_bun.ts`; raw captures in `_research/probe/out/`). |
| **UNVERIFIED** | Not observed live; taken from code or docs only. |

Live server: `rex 0.0.1794517219-g7ec4d1`, commit `7ec4d14d…`, built 2026-11-12, `api_revision 13`, speaks revisions 0–13 (LIVE `server.version`). The `rex` CLI installed on dasher is newer (`0.0.1794779081-g2d5402`, API 16); it talks to the API-13 server fine. Every shape below is revision 13.

Conventions: integers on the binary data channel are little-endian unsigned. IDs are opaque strings with a type prefix (`session:`, `window:`, `block:`, `layer:`, `split:`, `client:` + UUIDv7). Never parse them. Unknown JSON fields, event types, message types, enum values and APC types must be ignored (LLMS:78, 195, 441, 631).

---

## 1. Endpoints and transports

### 1.1 HTTP routes (same listener for everything) — LLMS:52-58, LIVE `/api/help` `http` block

| Route | Use |
| --- | --- |
| `GET /api/control` | Control WebSocket, subprotocol `rex.control.v1` |
| `GET /api/data` | Data WebSockets, subprotocol `rex.data.terminal.v1` (terminal) or `rex.data.bytes.v1` (upload/download) |
| `GET /api/help` | Every method with JSON Schemas (310 KB) |
| `GET /llms.txt`, `GET /llms.html` | The API document |

A caller the listener refuses gets HTTP 403 with JSON body `{"error":"..."}` on every route (LLMS:49-50).

### 1.2 Endpoint forms a client accepts (RR:docs/hosts.md:14-17, RR:src/transport.rs:22-34,71-109)

| Endpoint | Socket | WS URL rex-rune requests |
| --- | --- | --- |
| `unix:///abs/path.sock` | Unix socket (any `Host` works) | `ws://localhost/api/...` |
| `https://host[:port]` / `wss://` | TCP + TLS (rustls, OS root store, TLS1.2+; no client cert, no `Authorization`) — RR:Cargo.toml:65,71 | `wss://host[:port]/api/...` |
| `http://host[:port]` / `ws://` | plain TCP | `ws://host[:port]/api/...` |
| `ssh://[user@]host[:port][/socket]` | `ssh -L` forward of the remote Unix socket (§1.5) | as `unix://` (rex-rune) or `ws://127.0.0.1:<port>` (Windex, §1.6) |

Path and query of the endpoint URL are replaced by `/api/control` / `/api/data` (RR:src/transport.rs:91-92). Default ports: 443 for https, 80 for http (`port_or_known_default`). A local Windows Rex server listens on a **named pipe** (LLMS:44, `principal.kind = windows_peer`); rex-rune does not support it (RR:src/platform/local.rs:19-26,46-50) — UNVERIFIED pipe name.

### 1.3 Socket options and timeouts (rex-rune)

| What | Value | Ref |
| --- | --- | --- |
| TCP_NODELAY | on | RR:src/transport.rs:31 |
| TCP keepalive | idle 15 s, interval 5 s, 3 probes | RR:src/transport.rs:40-47 |
| Unacked-data limit (TCP_USER_TIMEOUT) | 30 s (Linux/macOS only) | RR:src/transport.rs:48 |
| Connect + WS upgrade | 10 s | RR:src/transport.rs:63-69 |
| hello → WELCOME | 8 s | RR:src/transport.rs:240-249 |
| Ordinary request | 8 s, never retried | RR:src/connection.rs:920-926 |
| `client.action` | 35 s (server budget 30 s) | RR:src/connection/actions.rs:7 |
| Data HELLO/RESUME → WELCOME | 8 s | RR:src/wire.rs:43-44 |
| Host reconnect backoff | 2 s, 4 s, then 8 s | RR:src/connection.rs:1800-1801 |
| Data reconnect delay | 1 s | RR:src/connection.rs:2447-2448 |

Server keepalive: the server pings every 120 s, waits 60 s per pong, closes `1001 keepalive timeout` after 3 misses; data connections use the same schedule (LLMS:493-497, 671-672). Answer pings; ping the server yourself on the same schedule to detect a dead peer. LIVE: no server ping arrived in ~22 s sessions (expected with a 120 s interval).

### 1.4 Tailscale discovery (RR:src/tailnet.rs)

1. Run `tailscale status --json` (Windows: `C:\Program Files\Tailscale\tailscale.exe`; LIVE works). Read `Self` and `Peer{}` nodes: `HostName`, `DNSName` (strip trailing `.`), `OS`, `Online`, `sshHostKeys` (non-empty ⇒ Tailscale SSH on), `TailscaleIPs` (RR:src/tailnet.rs:84-145,174-175). Skip self and `OS == "iOS"`.
2. For each online machine probe, **in parallel**, `https://<dns>` (443: built-in Tailscale or `tailscale serve`), `https://<dns>:7297`, `http://<dns>:7297`; first **in that order** that answers wins (RR:src/tailnet.rs:59-65, 313-345). Probe = control upgrade + hello + `server.status` (2 s) + close; never attach (RR:src/tailnet.rs:270-310). Connection refused = nothing there; other errors = "couldn't tell".
3. Otherwise offer `ssh://user@<dns>`.

UNVERIFIED live: https/wss/7297 (dasher has `tailscale.state = "disabled"` and only Unix listeners).

### 1.5 `ssh://` in rex-rune (Unix only; refused on Windows — RR:src/platform/local.rs:54-58)

Socket discovery on the remote (RR:src/ssh.rs:196-201, run as `ssh … -- dest "sh -c '<script>'"`, 30 s timeout, RR:src/ssh.rs:390-408). First existing socket wins:

1. path printed by `rex server status --autostart=false` (`unix:///…\.sock`)
2. `${XDG_RUNTIME_DIR:-/run/user/$(id -u)}/rex/server.sock`
3. `$HOME/Library/Application Support/rex/server.sock`
4. `/run/rex/server.sock`
5. `/var/run/rex/server.sock`

Output `sock <path>` or `none <user>` (→ "No Rex server found", setup offered, never auto-started). With an explicit `/socket` it only checks `[ -S socket ]`.

Common ssh options (RR:src/ssh.rs:154-176): `-o ControlMaster=no -o ControlPath=none -o BatchMode=yes -o ConnectTimeout=10 -o StrictHostKeyChecking=accept-new -o ServerAliveInterval=15 -o ServerAliveCountMax=3 [-p port]`.
Forward (RR:src/ssh.rs:245-264): `ssh <opts> -T -o ExitOnForwardFailure=yes -o StreamLocalBindUnlink=yes -L <local.sock>:<remote.sock> -- <dest> cat` with stdin piped and held open (remote `cat` dies when the client dies, so no orphan forward). Ready = local socket accepts within 20 s. Forward exit ⇒ host disconnect; last meaningful stderr line shown (RR:src/ssh.rs:358-378).

### 1.6 Windex SSH transport — **VERIFIED LIVE end-to-end**

Command (Windows OpenSSH 9.5p2, `C:\Windows\System32\OpenSSH\ssh.exe`):

```
ssh.exe -N -T -o BatchMode=yes -o ExitOnForwardFailure=yes -o ServerAliveInterval=15
        -o ServerAliveCountMax=3 -o ConnectTimeout=10
        -L 127.0.0.1:<port>:/run/user/1000/rex/server.sock joshua@dasher
```

Result: `Authenticated to dasher ([100.64.0.7]:22) using "none".` (Tailscale SSH), `Local forwarding listening on 127.0.0.1 port 47297`. 47 `direct-streamlocal@openssh.com` channels opened over the run, none failed. Through it LIVE: control upgrade, hello/WELCOME, `session.bootstrap` with bundled data tickets, data handshake, `GHOSTSNP` snapshot, resize/theme APCs, live PTY bytes, RESUME, control reconnect with `reconnect_token`, all mutations in §7.

Gotchas (all LIVE unless marked):

| # | Gotcha |
| --- | --- |
| 1 | Tailscale SSH **does** allow streamlocal (TCP→remote Unix socket) forwarding. Use `127.0.0.1:<port>` explicitly (not `localhost`, which may bind `::1`). Pick a free port per forward; `ExitOnForwardFailure=yes` makes a taken port fatal instead of silent. |
| 2 | Admission is **peer credentials** on the remote socket: the server sees `principal {"kind":"peer","transport":"unix","uid":1000,"pid":<sshd child>,"remote_addr":"@"}` (`server.whoami` → `account: "joshua"`). The SSH login user must be the server's user, or the connection is refused. |
| 3 | `Host` header is ignored: `localhost`, `127.0.0.1:47297`, `dasher`, `example.invalid` all upgrade (101). |
| 4 | Missing `Sec-WebSocket-Protocol`: server still answers 101 **without** echoing a subprotocol, then closes `1002 "protocol error"` after the hello. Always send it and check the echo (rex-rune: RR:src/transport.rs:100-107). The server also echoes an unknown subprotocol (RR:docs/server-api13.md:367). |
| 5 | Server offers `permessage-deflate; server_no_context_takeover; client_no_context_takeover` on **both** control and data when the client asks (Bun did; LIVE data channel still worked). Simplest: don't offer it. Never compress the hello (LLMS:225-228). |
| 6 | Windows OpenSSH has no ControlMaster/streamlocal-bind-unlink concerns for a TCP local end; kill the `ssh.exe` process to end the forward (exit 255 is normal then). |
| 7 | Git-Bash's `/usr/bin/ssh` (OpenSSH 10.3) also exists on this box; use the System32 binary explicitly. |
| 8 | Remote socket discovery: run the §1.5 script once over `ssh.exe … sh -c '…'` before forwarding, or ask the user. Default for a Linux login: `/run/user/<uid>/rex/server.sock`. |
| 9 | `BatchMode=yes` means no prompts; a key with a passphrase needs ssh-agent. Show ssh's last stderr line on failure (Tailscale SSH refusals arrive as a banner). |

### 1.7 Login server / `https://user@host` — UNVERIFIED

dasher exposes a second listener `login-server:///run/user/1000/rex/login-server.sock` (`admission: "login_server"`) for the multi-account login server (`rex login-server`). rex-rune sends no credentials and ignores URL userinfo (RR:docs/parity-2026-11/code/c04-hosts-remote-updates.md:92). Not needed for SSH or Tailscale-identity attach.

---

## 2. Control channel

### 2.1 Upgrade (LIVE)

```
GET /api/control HTTP/1.1
Host: localhost
Upgrade: websocket
Connection: Upgrade
Sec-WebSocket-Key: <random>
Sec-WebSocket-Version: 13
Sec-WebSocket-Protocol: rex.control.v1

HTTP/1.1 101 Switching Protocols
Connection: Upgrade
Sec-Websocket-Accept: <...>
Sec-Websocket-Protocol: rex.control.v1
Upgrade: websocket
```

Every message, both directions: **one JSON object per WebSocket text message**. Binary from client ⇒ close 1003; invalid UTF-8 ⇒ 1007 (LLMS:190, 528-530).

### 2.2 HELLO (client → server, first message, within 5 s, < 512 bytes) — LLMS:202-228, SDK:src/protocol.rs:85-93,241-265

| Field | Type | Notes |
| --- | --- | --- |
| `type` | `"hello"` | required |
| `api_revision` | int | newest revision client speaks; omitted = 0. Windex: **13** |
| `api_min_revision` | int | oldest usable; omitted = 0. rex-rune: **10** (RR:src/transport.rs:200-201) |
| `client` | object | `kind` (`app`\|`cli`\|`agent`\|`other`), `platform` (`macos`\|`ios`\|`linux`\|`windows`\|`freebsd`\|`web`\|`other`), `name`, `version`, `label` (≤64 bytes, server trims/drops if invalid) |
| `reconnect_token` | string | only to resume a client; never log it |

Windex hello (as sent LIVE):

```json
{"type":"hello","api_revision":13,"api_min_revision":10,
 "client":{"kind":"app","platform":"windows","name":"Windex","version":"0.1.0","label":"<computer name>"}}
```

rex-rune sends `name:"Rex Rune"`, `kind:"app"`, `label: hostname` (RR:src/transport.rs:206-236).

### 2.3 WELCOME (server → client, always first) — LLMS:230-261, SDK:src/protocol.rs:96-153

LIVE:

```json
{"type":"welcome","protocol":"rex.control.v1","limits":{"max_message_bytes":1048576},
 "client":{"id":"client:01a1ec1a-64df-7c4e-b51a-52feb19daa7f","reconnect_token":"<22 chars>"},
 "api_revision":13,"api_server_revision":13}
```

| Field | Notes |
| --- | --- |
| `protocol` | must equal `rex.control.v1` |
| `limits.max_message_bytes` | 1 MiB on dasher; ≥ 65536 always. Send nothing larger (request too large ⇒ close 1009). |
| `client.id` | stable across reconnects |
| `client.reconnect_token` | new on every welcome; old one dies |
| `client.resumed` | true only when the hello's token was accepted (absent ⇒ false) |
| `api_revision` | negotiated; all later messages use it. Check it is within your range (RR:src/transport.rs:250-254) |
| `api_server_revision`, `api_min_revision` | server range (`api_min_revision` omitted = 0) |
| `features` | optional strings; ignore unknown. (Absent LIVE) |
| `carriers` | optional alternate transports (e.g. `quic`) — ignore |

No common revision ⇒ close **1008**, LIVE reason: `"no common API revision: server speaks 0 to 13, client needs 99 to 99; update the server"`. No hello within 5 s ⇒ drop without close frame (1006).

### 2.4 Requests and responses — LLMS:288-331, SDK:src/protocol.rs:175-231

```json
{"type":"request","id":"7","method":"session.view","payload":{"session_id":"session:…"}}
{"type":"response","id":"7","seq":12,"payload":{ … }}
{"type":"response","id":"8","seq":12,"error":{"code":"not_found","message":"block not found: block:…","status":404}}
```

- `id`: any non-empty **string**, unique among in-flight requests (a number ⇒ close 1002). rex-rune uses `"1","2",…` plus `"detached:N"` for fire-and-forget (RR:src/transport.rs:122-138).
- `payload` omitted for no-arg methods; a successful response with no fields may omit `payload` ⇒ treat as `{}`.
- `seq` (required): seq of the last event queued **before** the request started (§2.6).
- Responses may arrive **out of order**. Exception — these run serially per connection in arrival order (LLMS:338-340): `block.close`, `block.event`, `client.action`, `client.key`, `session.close_layer`, `session.close_window`, `session.detach_block`, `session.disown_block`, `session.float_block`, `session.focus_block`, `session.focus_direction`, `session.focus_next_pane`, `session.focus_next_window`, `session.focus_previous_pane`, `session.focus_previous_window`, `session.focus_window`, `session.move_block`, `session.move_window`, `session.new_layer`, `session.new_split`, `session.new_window`, `session.own_block`, `session.set_layer_state`, `session.swap_blocks`, `session.zoom_block`. Everything else (incl. all block methods such as `…terminal.write`, `…terminal.resize`, `session.resize_pane`, `session.set_*_label`) may overtake them.
- rex-rune is strictly one-request-at-a-time per host (SDK:src/async_client.rs:53-55); Windex may pipeline but must match by `id`.

Error codes (LLMS:356-371; branch on `code`, fall back to `status`):

| code | status | meaning |
| --- | --- | --- |
| `bad_request` | 400 | invalid args |
| `not_found` | 404 | named thing missing |
| `method_not_found` | 404 | server older than client (old servers send `not_found`) |
| `session_not_found` | 404 | unknown/destroyed session |
| `conflict` | 409 | not attached, limit reached, state race — may succeed later |
| `not_supported` | 400 | client can't perform action |
| `refused` | 403 | client declined action |
| `client_unavailable` | 409 | target client away |
| `too_large` | 413 | result > message limit (connection stays open) |
| `revision_too_low` | 400 | method needs newer negotiated revision |
| `timeout` | 504 | Lua/action ran too long |
| `internal` | 500 | anything else |

LIVE oddity: the nil UUID `session:00000000-0000-0000-0000-000000000000` is treated as *missing* (`bad_request "session_id is required"`); a random unknown ID gives `session_not_found` / `not_found` as documented.

### 2.5 Events — LLMS:400-454, SDK:src/protocol.rs:186-195

Server-level event:

```json
{"type":"event","seq":23,"payload":{"type":"client_joined","payload":{ … }}}
```

Session event (everything that happens inside a session) is wrapped:

```json
{"type":"event","seq":42,"payload":{"type":"session_event","payload":{
  "session_id":"session:…","event":{"type":"window_label_changed","payload":{"window_id":"window:…","label":"Renamed tab"}}}}}
```

Block event (inside a session event): `event.type == "block_event"`, `event.payload = {block_id, creator_name, name, payload}`.

What a connection receives (LLMS:443-452):

| Attached with | Receives |
| --- | --- |
| nothing | `keymap_changed`, `hosts_changed`, `client_call_requested`, `client_perform_requested` |
| `session.attach` | + every event of that session incl. `block_event`, and its `session_destroyed` |
| `attach_all` scope `session` | + `session_created/destroyed/moved`, `tailscale_status_changed`, every session's events **except** `block_event` (attached sessions still send theirs) |
| `attach_all` scope `all` | + all `block_event`s of all sessions, `client_joined/left/changed`, `client_performed`, `client_script_events_dropped` |

`session.bootstrap` = `attach_all(event_scope)` + `session.attach` for each attached session. **rex-rune uses `event_scope: "all"`** (needs other sessions' `block_event`s for activity badges, RR:src/connection.rs:704). For a client that only shows the attached session, `"session"` is enough.

### 2.6 Ordering results against events (`seq`) — LLMS:456-477

- Event `seq` starts at 1 per connection, +1, no gaps (restarts at 1 after reconnect).
- Apply a result, then only events with `seq > response.seq`; buffer events that arrive before the response.
- Apply idempotently; for views compare `revision` (only re-fetch when `session_view_changed.revision > held revision`; those events can arrive out of revision order).
- rex-rune keeps three floors (RR:src/connection.rs:649-665): `bootstrap_seq`; `list_seq` (for `session_created|moved|destroyed|label_changed`, updated by each `attach_all` re-list); `view_seq[session]` (updated by every `session.attach`/`session.view`; not applied to `session_destroyed` or clipboard `block_event`s). An event passes if `seq > max(applicable floors)`.
- Backlog > 8 MiB ⇒ server closes `1008 "event backlog exceeded"`; an event bigger than the limit ⇒ `1009 "event exceeds message limit"`. Reconnect and re-read state.

### 2.7 Close codes from the server (LLMS:525-536)

| Code | Reason |
| --- | --- |
| 1001 | `keepalive timeout` |
| 1002 | `protocol error` (no subprotocol, first message not hello, bad JSON/type/id/method, duplicate in-flight id) |
| 1003 | binary message |
| 1007 | invalid UTF-8 |
| 1008 | `no common API revision: …` / `event backlog exceeded` |
| 1009 | `read limited at …` / `event exceeds message limit` |

### 2.8 Reconnect / resume — LLMS:499-521; LIVE verified

- Connection lost **without** a close frame: server keeps the client 60 s (attachments, ownerships, scripts). Closing **with** a close frame ends the client at once (SDK:src/async_client.rs:113-120).
- New connection + hello with `reconnect_token` ⇒ welcome `client.resumed: true`, same `client.id`, new token. LIVE: after abrupt TCP close + reconnect, `resumed=true`, same id, `session.view` and `data.connect` worked without re-attaching.
- Events during the gap are lost; responses to in-flight requests are lost (they may have run). **Never replay input.** Re-read state (rex-rune re-bootstraps every time: RR:src/connection.rs:1482-1534).
- If `session.bootstrap` fails, close the socket and **drop the token** (partial setup) before retrying (RR:src/connection.rs:1520-1532; LLMS:3000).
- While disconnected rex-rune: discards queued input commands, keeps size/visibility/selection state, refuses navigation (RR:src/connection.rs:1803-1866).

---

## 3. Attach flow (GUI client)

### 3.1 Sequence per host (rex-rune `run_host`, RR:src/connection.rs:1445-1590)

1. (ssh) open forward. Open control WS, hello (with saved token if any), WELCOME → store `client.id`, token, `api_revision`.
2. **`session.bootstrap`** as the first request (8 s). Request built by RR:src/connection.rs:693-714:
   ```json
   {"event_scope":"all",
    "attach_session_ids":["session:…"],
    "data_tickets":[{"creator_name":"com.superlogical.terminal","data_ref_id":"terminal","subprotocol":"rex.data.terminal.v1"}]}
   ```
   - `attach_session_ids` is sent only if a session is wanted (CLI `--session` with a full `session:<uuid>`, a session being created, or sessions already streaming before a reconnect). Omitted ⇒ server attaches `preferred_session_id` or the first session and returns it in `selected_session`. `[]` ⇒ attaches none (LLMS:2996-2997, RR:vendor/rexrs/REX-RUNE.md "explicit-empty").
   - `data_tickets`: ≤ 8 entries; server returns one ticket per **visible** matching block of every returned view (LLMS:2900, 2984).
3. Apply result (RR:src/connection.rs:1254-1281): `server_status` (§9), `attach_all.sessions` (ordered list = sidebar order), each `attached_sessions[].view` and `selected_session.view`, ticket pool keyed `(session_id, block_id)` with expiry `now + expires_in_ms` (RR:src/connection.rs:604-625).
4. (optional) `session.create` if the user asked to create one, then re-list with `session.attach_all {"event_scope":"all"}` (RR:src/connection.rs:1535-1541, 995-1014).
5. Choose the selected session locally: by exact `session_id` or exact unique `label`; ambiguous or missing ⇒ none; no preference ⇒ first in list (RR:src/connection.rs:668-677). **The server has no per-client "current session"; selection is client state.**
6. `session.attach {"session_id"}` for the selected session if not already attached by bootstrap (RR:src/connection.rs:937-952, 1557-1561).
7. Process events that queued during setup (with the seq floors), mark connected (RR:src/connection.rs:1566-1571).
8. Reconcile panes (§3.5), open data channels, send sizes (`…terminal.resize`) for panes you own, declare theme, then per-block metadata reads (§8.1), then `client.keymap` (§7.3), then `server.version` (+ `server.status` on `tailscale_status_changed`).
9. Event loop.

### 3.2 Bootstrap response (structure; full LIVE capture in Appendix A.3)

| Field | Content |
| --- | --- |
| `client_id` | this client |
| `server_status` | as `server.status` (§9) |
| `attach_all.sessions[]` | `{session_id, label}` in server order |
| `selected_session` | `{session, view}` or `null` (null when `attach_session_ids` sent or no sessions) |
| `attached_sessions[]` | `{session, view}` in requested order; unknown IDs skipped |
| `data_tickets[]` | `{session_id, block_id, data_ref_id, subprotocol, ticket, expires_in_ms}` (60000 LIVE) |

### 3.3 The view (`session.view` result; same object inside attach/bootstrap) — LLMS:4729-4777, 2757-2788

```
view { revision:int, session_id, label, active_window_id?, windows:[window] }
window { window_id, label, focused_block_id?, zoomed_block_id?, layers:[layer] }   // windows = tabs, in tab order
layer { layer_id, kind:"tiled"|"floating", bounds:[x,y,w,h], state?:"minimized", layout:node|null, blocks:[block] }
node  { rect:[x,y,w,h], block_id? , split? }                     // exactly one of block_id / split
split { split_id, direction:"horizontal"|"vertical", ratio:0..1, before:node, after:node }
block { block_id, label, creator_name, flavor, host_id, owner? }
```

- `layers[0]` is the tiled layer filling the window; later layers are floating, back→front.
- `horizontal` = before **left**, after **right**; `vertical` = before **top**, after **bottom**. Unknown direction ⇒ horizontal; unknown kind ⇒ floating; unknown state ⇒ normal.
- `rect` and `bounds` are **fractions of the whole window** (not of the parent) — LIVE nested example in Appendix A.5 (`[0,0.5,0.6,0.5]`).
- `zoomed_block_id`: draw only that block over the whole window; the layout keeps its sizes.
- `owner`: client id that owns the block; absent ⇒ none. Compare to your `client.id`.
- Only terminal blocks have `creator_name == "com.superlogical.terminal"`; other creators (e.g. `com.superlogical.onboarding`) exist — don't stream them.

Geometry (rex-rune, RR:src/layout.rs:90-175): if zoomed → one placement `[0,0,1,1]`; else for each layer not `minimized`, walk `layout` within `bounds` splitting by `ratio`; emit dividers keyed by the **first block** of each side (needed for `session.resize_pane`). Using each node's `rect` directly is equivalent.

### 3.4 Keeping the model current

| Event (inner type) | Action | Ref |
| --- | --- | --- |
| `session_created`, `session_moved` | re-list: `session.attach_all {"event_scope": <same>}` (or apply locally: created → append; moved → `before_session_id`) | RR:src/connection.rs:1311-1313 |
| `session_destroyed` `{session_id}` | drop session, its views, streams | RR:1314-1321 |
| `session_label_changed` `{label}` | update label | RR:1322-1332 |
| `session_view_changed` `{revision}` | if `revision >` held: `session.view` and replace (keep higher revision only, RR:953-960) | RR:1333-1353 |
| `window_created` `{window_id,label}`, `window_closed` `{window_id}`, `window_label_changed`, `active_window_changed` `{window_id?}` | informational; always followed by `session_view_changed` (LLMS:4774) | — |
| `block_created` `{block_id}` | informational; placement arrives via `session_view_changed` | — |
| `block_closed` `{block_id}` | stop its data channel, forget metadata, then `session.view` | RR:1354-1374 |
| `block_owner_changed` `{block_id, owner?, previous?, reason}` | set owner (reasons: `create`,`take`,`request`,`disown`,`lost`,`disconnect`,`restore`) | RR:1426-1430 |
| `block_event` | §8 | RR:1375-1424 |
| `client_connected` / `client_disconnected` / `client_connection_changed` | presence only | — |
| `keymap_changed` `{generation, hash}` | re-request `client.keymap` if `hash` or `config.instance_id` changed (ignore generation) | RR:src/connection/keymap.rs:21-31 |
| `client_perform_requested` | answer with `client.reply` ≤ 5 s (§7.4) | RR:src/connection/perform.rs:96-133 |
| `tailscale_status_changed` | re-read `server.status` | RR:src/connection.rs:1599,1685 |
| `creator_changed`, `hosts_changed`, `client_*` | optional | — |

Event payload fields: LLMS:742-1134 (complete reference). LIVE samples: Appendix A.6.

Switching sessions: `session.attach` the target (returns fresh view), select it locally, stream its active window (RR:src/connection.rs:2057-2064). rex-rune never detaches the old one (its streams stay cached). `session.detach {"session_id"}` exists (owned blocks pass to the next client in line).

Switching tabs: `session.focus_window {"session_id","window_id"}` — tab selection is **server state** shared by all clients (`active_window_id`).

### 3.5 Which blocks to stream (RR:src/connection.rs:1016-1118)

Visible = blocks of the **active window** of the **selected session** with `creator_name == "com.superlogical.terminal"`, minus non-zoomed blocks while a block is zoomed. Open a data channel per visible block; keep existing channels of hidden blocks open (just stop rendering) until their block or session disappears from the view (that is authoritative even if `block_closed` was missed).

---

## 4. Terminal data channel

### 4.1 Tickets

| Source | Call | Notes |
| --- | --- | --- |
| Bootstrap | `data_tickets` in `session.bootstrap` | one per visible terminal block of returned views |
| Per block | `data.connect {"session_id","block_id","data_ref_id":"terminal","subprotocol":"rex.data.terminal.v1"}` → `{"ticket":"<22 b64url>","expires_in_ms":60000}` | requires `session.attach`/`bootstrap` attachment (`attach_all` ⇒ `conflict`) — LLMS:1945-1989 |

Ticket = 16 bytes, base64url **without padding**, single use (even on failed handshake), 60 s, same user only (LLMS:562-564). rex-rune: take from pool (removed when taken, expiry checked), else `data.connect`; if a pooled ticket gets REJECT, retry once via `data.connect` (RR:src/connection.rs:627-633, 2112-2136, 2454-2478). Pool is dropped with its control socket.

### 4.2 Socket

`GET /api/data`, `Sec-WebSocket-Protocol: rex.data.terminal.v1`, check echo. Binary messages only. **The client sends nothing after its HELLO/RESUME** (server ignores anything). LIVE.

### 4.3 Handshake messages (LLMS:579-608, RR:src/wire.rs:26-59)

| Msg | Dir | Bytes | Layout |
| --- | --- | --- | --- |
| HELLO | C→S | 17 | `01` + ticket[16] |
| RESUME | C→S | 25 | `02` + token[16] + offset u64le |
| WELCOME | S→C | 32 | `81` + token[16] + `00 00 00` + retention_ms u32le + nonce u64le (≠0) |
| REJECT | S→C | 2 | `82` + reason, then close `data handshake rejected` |

Send within 5 s. LIVE WELCOME: `81 30f95bbbde1eaee0149aa5ad4c75d7f2 000000 60ea0000 ef56faad4682c7fa` → retention 60000 ms, nonce `18070555269925590767`.

| REJECT | Meaning |
| --- | --- |
| `01` | ticket/token unknown, expired, used, other user, or session gone (LIVE: RESUME with zero token → `82 01` + close `1008`) |
| `02` | this client already has a connection to this block's data source (LIVE: second `data.connect`+HELLO for the same block) — send RESUME instead |
| `03` | stream unavailable (block closed, or RESUME offset too small) |
| `04` | limit (not sent yet) |
| `05` | malformed/late handshake, or RESUME offset too large |
| `06` | server failure |
| `07` | ticket issued for another subprotocol |

### 4.4 Stream content

After WELCOME every binary message is stream bytes (1–65536 bytes each); **message boundaries mean nothing** — concatenate (LLMS:571-573).

```
stream := snapshot , { pty_bytes | rex_apc }
snapshot := "GHOSTSNP" 0x01 0x00 , record* , finish_record
record := tag u16le , len u32le , crc u32le , payload[len]
```

- Envelope: 10 bytes `47 48 4F 53 54 53 4E 50 01 00` (RR:src/wire.rs:110-118).
- Record header 10 bytes. `crc` = **CRC-32C (Castagnoli) over tag‖len‖payload** (LIVE: matched all 7 records). Tag **6 = FINISH** (len 0) ends the snapshot (RR:src/wire.rs:120-147). LIVE record sequence for a fresh shell: tags `1:961, 2:77, 3:466, 7:0, 5:0, 4:6, 6:0` (1590 bytes). Other tags are Ghostty-internal; never interpret them — hand the whole snapshot (envelope through FINISH) to libghostty-vt's decoder (`ghostty_snapshot_decoder_new_buf` → `ready` → `next`* → `decode`/terminal; RR:vendor/ghostty-vt/src/snapshot.rs:108-260, crate `ghostty-vt 0.8.0+ghostty.1.4.0-main.83fb956`). Images are included (Kitty graphics).
- rex-rune caps a snapshot at 256 MiB (RR:src/wire.rs:104-108) and verifies decoder `source_offset == end` (RR:src/wire.rs:137-140).
- Bytes after FINISH in the same message are already live output.
- Live output = raw PTY bytes → `vt_write` into the restored terminal, **except** Rex APCs:

```
ESC _ rex1 ; <nonce decimal> ; <json> ESC \          (0x1B 0x5F … 0x1B 0x5C)
```

Only an APC whose nonce equals the WELCOME nonce is metadata; anything else (incl. look-alikes a program printed) is terminal output. The parser must hold partial prefixes across messages and reconnects; rex-rune caps an APC at 1 MiB (RR:src/terminal.rs:27-67).

| APC `type` | Fields | Client action (RR:src/terminal.rs:157-196) |
| --- | --- | --- |
| `resize` | `cols`,`rows` (1–65535), `cell_width_px`,`cell_height_px` (0 = unknown), `owner` (client id or null) | resize the local Ghostty terminal to exactly this; it is the authoritative PTY size. First APC right after the snapshot; then on every size change (owner change alone sends none) |
| `theme` | `foreground`,`background`,`cursor` (`RRGGBB`/`#RRGGBB`), `palette` [`"idx=RRGGBB"`], `scheme` (`light`/`dark`) | set default fg/bg/cursor; reset palette then apply entries; each theme replaces the previous; omitted colour = built-in. Follows the first resize unless all built-in |
| `child_exited` | `exit_code` | program ended; stream stays open |

LIVE APCs:

```
{"type":"resize","cols":80,"rows":24,"cell_width_px":8,"cell_height_px":16,"owner":"client:01a1ec18-d718-…"}
{"type":"resize","cols":100,"rows":30,"cell_width_px":9,"cell_height_px":19,"owner":"client:01a1ec1a-64df-…"}
{"type":"theme","foreground":"#d6deeb","background":"#011627","cursor":"#80a4c2","palette":["1=#ef5350"]}
{"type":"resize","cols":100,"rows":30,"cell_width_px":9,"cell_height_px":19,"owner":null}
```

### 4.5 Offsets, resume, close (LLMS:636-672; RR:src/connection.rs:2346-2452) — LIVE verified

- `received` = count of **every stream byte after WELCOME, snapshot included**, across all connections of the stream (RR:src/terminal.rs:135). Don't count WELCOME; don't count bytes buffered but not consumed.
- Drop without close ⇒ server keeps the stream 60 s (`retention_ms`). New WS + `RESUME(latest token, received)` ⇒ WELCOME with **same nonce, new token**; stream continues at the next byte, no new snapshot. LIVE: RESUME at offset 9695 → same nonce, rotated token, next output (`RESUMED`) arrived, no `GHOSTSNP`.
- Wrong offset ⇒ REJECT `03`/`05` and the stream ends. Refused RESUME ⇒ new ticket + HELLO ⇒ **fresh snapshot ⇒ build a new terminal** (rex-rune bumps `restore_generation`, resets selection/search, RR:src/connection.rs:2357-2368). rex-rune asserts the nonce is unchanged on resume (2352).
- Client close with **1000** ends the stream; any other close/drop leaves it resumable.

| Close | Reason | Do |
| --- | --- | --- |
| 1000 | – | stream ended |
| 1001 | `keepalive timeout` | resume |
| 1001 | `data provider canceled` | server stopped it (shutdown, or the client that opened it left) — new ticket |
| 1008 | `data provider unavailable` | wrong data_ref/subprotocol |
| 1011 | `data provider failed` | new ticket later |
| 1013 | `data reconnect required` | new ticket + HELLO (output overflowed retention) |
| **4000** | `block closed` | **stop; don't reconnect** (LIVE on `block.close`) — RR:src/connection.rs:2440-2446 |
| 1002/1008/1011/1013 | `data handshake rejected` | see REJECT reason |

### 4.6 Bytes streams (`rex.data.bytes.v1`, uploads/downloads) — brief

Same `/api/data`, HELLO, WELCOME. Upload (`com.superlogical.terminal.upload_begin {ref:"upload:<uuid>", name, size, batch?, index?, count?}` → ticket): client sends file as binary messages, then one **empty** binary message, waits for server close 1000 (RR:src/upload.rs:86-131). Download (`download_begin {ref:"download:<uuid>", path, if_none_match?}`, API 13): server sends ≤32 KiB messages, then an empty message, then close 1000; anything else is failure (RR:docs/server-api13.md:358-421). Not resumable.

---

## 5. Input

All input goes over the **control** channel. Server-side Ghostty encodes keys/mouse/paste/focus according to the PTY program's modes; **the client never encodes escape sequences itself** (LIVE table below).

### 5.1 Keys: `client.key` (ordered) — LLMS:1491-1552, RR:src/app/keys.rs:148-185, RR:src/connection.rs:2257-2267

```json
{"session_id":"session:…","block_id":"block:…",
 "key":{"key":"key_a","mods":"ctrl","consumed_mods":"","text":"","unshifted_codepoint":97,"composing":false,"action":"press"},
 "prefix":["ctrl+b"]}
```
→ `{"handled": false}` (true = a Lua binding took it).

| Field | Rule |
| --- | --- |
| `key` | **physical** key, W3C `KeyboardEvent.code` → snake_case: `KeyA`→`key_a`, `Digit1`→`digit_1`, `ArrowUp`→`arrow_up`, `Numpad0`→`numpad_0`, `F12`→`f12`, `BracketLeft`→`bracket_left`, `Enter`, `Backspace`, `Tab`, `Escape`, `Space`, `Delete`, `Home`, `End`, `PageUp`→`page_up`, `Insert`, … full list LLMS:1502; unknown ⇒ `unidentified` (rex-rune drops keys it can't name). Algorithm: insert `_` before an uppercase letter following lowercase/digit and before a digit following a letter, lowercase; `F<n>` → `f<n>` (RR:src/app/keys.rs:12-36). Windows: map scan code → W3C code. |
| `mods` | `+`-joined in order `shift`,`ctrl`,`alt`,`super`,`caps_lock`,`num_lock`,`shift_side`,`ctrl_side`,`alt_side`,`super_side`; `""` none. `*_side` = right-hand key held. rex-rune sends only the first four (RR:src/app/keys.rs:92-106). Windows key = `super`. |
| `consumed_mods` | modifiers the layout used to produce `text` (e.g. `shift` for `A`, `!`; `alt` for AltGr/Option-composed text). Same spelling as `mods`, which still includes them (RR:src/app/keys.rs:128-139, 188-205) |
| `text` | text typed; `""` for non-text keys, for releases, and when ctrl/super held. rex-rune sends `"\n"` for Enter and `"\t"` for Tab (RR:src/app/keys.rs:108-119) — LIVE: Enter with `"\n"` or `""` both produce CR |
| `unshifted_codepoint` | codepoint the key types without shift in the current layout (`a`=97); 0 if none. Used for layout-following bindings |
| `composing` | true for a dead key / unfinished composition |
| `action` | `press` \| `repeat` \| `release`. Send a **release only if its press was sent** to the PTY (RR:src/app/input.rs:379-404) |
| `block_id` | target; omitted ⇒ server's focused block of the session (read before Lua runs) |
| `prefix` | earlier keys of a completed key sequence, spelled like keymap keys (`"ctrl+b"`) |

LIVE server encoding (terminal `stty raw`, `cat -v`):

| Sent | PTY received |
| --- | --- |
| `arrow_up` | `ESC [ A`; with DECCKM on: `ESC O A` |
| `f1` | `ESC O P` |
| `key_a` mods `ctrl` | `0x01` |
| `key_b` mods `alt` text `b` | `ESC b` |
| `key_a` mods `shift` consumed `shift` text `A` | `A` |
| `enter` (text `"\n"` or `""`) | `CR` |
| `tab` text `\t` / `backspace` / `escape` | `HT` / `DEL (0x7f)` / `ESC` |
| `key_e` text `é` | UTF-8 `C3 A9` |
| `key_c` mods `ctrl` | `0x03` (SIGINT with isig) |
| `block.event` key `arrow_down` (DECCKM on) | `ESC O B` |

`block.event {"type":"key","payload":<same key object>}` sends a key **bypassing Lua bindings**; rex-rune uses it only for explicit `pane.send_key` (RR:src/connection.rs:2268-2269).

### 5.2 Text input / IME (RR:src/app/input.rs:406-428)

- IME preedit: draw locally only.
- IME commit (and any text not tied to a physical key): `block.event` **paste** with the committed text.

### 5.3 Paste — `block.event` (ordered) — LLMS:1187-1234

```json
{"session_id":"…","block_id":"…","event":{"type":"paste","payload":{"text":"echo hi"}}}
```

The server adds bracketed-paste wrappers when the program enabled mode 2004 — LIVE: `ESC[200~hi ESC[201~`; without 2004: raw `hi`. **Never wrap yourself.** It never appends Enter.

### 5.4 Raw writes — `com.superlogical.terminal.write {"args":{"data":"…"}}`

Types bytes as if typed (escape sequences allowed). **Not ordered** with `client.key`/`block.event` (block methods run concurrently). Use for automation only, not interactive typing. Result `{"bytes_written":n}`.

### 5.5 Mouse — `block.event` `mouse` (ordered)

Client sends **semantic** events; the server encodes per the program's tracking mode and format, and **drops** them when the program isn't tracking (LIVE: nothing reached the PTY without DECSET 1000).

```json
{"type":"mouse","payload":{"action":"press","button":"left","x":2,"y":3,"mods":""}}
```

| Field | Values |
| --- | --- |
| `action` | `press`, `release`, `motion` |
| `button` | `left`, `middle`, `right`; omitted for motion with no button |
| `x`,`y` | **0-based** cell column/row of the terminal grid (includes the visible-viewport offset when the pane shows fewer rows than the terminal; RR:src/app/term.rs:445-454) |
| `mods` | as key `mods`; rex-rune always strips `shift` (Shift = local selection override, RR:src/app/term.rs:456-470) |

Who decides (rex-rune, RR:src/app/panes.rs:446-583): if the local Ghostty frame reports mouse tracking (DEC 9/1000/1002/1003) and Shift is not held → send to server; else do local selection/scrollback on the client's own terminal copy. Ctrl+click opens links locally. Right-click opens the pane menu; Shift+right-click sends press+release right. Motion is sent only for the focused pane and only when the cell changes.

LIVE (SGR 1006 + 1002 on):

| Sent | PTY received |
| --- | --- |
| motion, button left, x5 y3 | `ESC[<32;6;4M` |
| press right, mods ctrl, x0 y0 | `ESC[<18;1;1M` |
| press/release right x0 y0 | `ESC[<2;1;1M` / `ESC[<2;1;1m` |
| press middle x1 y1 | `ESC[<1;2;2M` |
| press/release **left** (any mods, any extra field) | **nothing** |

⚠ **Open issue (LIVE, this server build `7ec4d1`)**: left-button press/release are not delivered to the PTY, while left-drag motion, middle and right are. rex-rune's acceptance script expects `ESC[<0;21;4M ESC[<0;21;4m` for the identical payload against server `757adf` (RR:scripts/font-metrics-x11.py:161). Treat as a probable server regression; Windex should still send the payload above. No `selection_copied`/`url_clicked` events appeared either.

### 5.6 Wheel — `block.event` `wheel`

```json
{"type":"wheel","payload":{"x":4,"y":4,"delta_x":0,"delta_y":-1}}
```

`delta_y` < 0 = up. LIVE: `-1` → `ESC[<64;5;5M`, `+1` → `ESC[<65;5;5M`, `+3` → **one** report. So send **one event per line** with `delta_y = ±1` (rex-rune: up to 8 per gesture, RR:src/app/term.rs:473-480). When the program is not tracking the mouse (or Shift held), scroll the **local** scrollback (`ghostty` viewport) instead of sending (RR:src/app/panes.rs:613-629).

### 5.7 Focus — `block.event` `focus`

`{"type":"focus","payload":{"action":"in"}}` / `"out"` → LIVE `ESC[I` / `ESC[O` when mode 1004 is set. rex-rune never sends focus events (optional for Windex; send on window/pane focus change).

### 5.8 Ownership side-effect of input (RR:src/connection.rs:2187-2229)

Before a **press** (key press/repeat, paste, mouse press) on a block this client doesn't own, rex-rune calls `session.own_block {"session_id","block_ids":[block],"mode":"take"}` and updates the owner. Releases, motion and wheel never claim. Input is dropped if the pane's data stream or control is down. Typing/paste/click also scroll the local viewport to the bottom.

---

## 6. Sizing, ownership, theme

- **Owner** = the client whose size/theme the block uses (LLMS:37-39, 4363-4414). Claim: `session.own_block {"session_id","block_ids":[…]|omitted=all,"mode":"take"|"request"}` → `{"blocks":[{"block_id","owner"}]}`. Release: `session.disown_block`. Both need `session.attach`-level attachment. Owners return on reconnect unless changed meanwhile.
- rex-rune claims (`take`) all blocks of the visible window once per pane when its window gains focus (RR:src/app/workspace.rs:701-725) and on input (§5.8). Navigation alone does not claim (RR:src/bin/workspace_probe.rs:119).
- **Resize** (block method, not ordered):
  ```json
  {"session_id":"…","block_id":"…","args":{"columns":100,"rows":30,"cell_width_px":9,"cell_height_px":19}}
  ```
  `columns = floor(pane_px_w / cell_w)`, `rows = floor(pane_px_h / cell_h)`, clamp 1–65535; cell px = `ceil(cell size)`; send cell sizes both or neither (RR:src/app/term.rs:398-421, RR:src/connection.rs:2311-2322). Send **only when owner and the stream is live, and only when changed**; re-send last sizes after reconnect (RR:src/connection.rs:1573-1575). A non-owner's request is stored and applied when it becomes owner.
- The authoritative size is the `resize` APC (§4.4); a non-owner renders the server size inside its pane (cropped/padded). `size_changed` block_event and `…terminal.size` (`{columns,rows,cell_width_px,cell_height_px,owner,desired[]}`) report it too.
- **Theme**: `com.superlogical.terminal.set_theme {"args":{foreground,background,cursor,palette:["1=#ef5350"],scheme}}` applies if owner or no owner, else when it becomes owner (LLMS:5437-5455). New blocks can carry it in the layout: `{"block":{"flavor":"com.superlogical.terminal.shell","options":{"theme":{…}}}}` (RR:src/layout.rs:11-20). Empty `{}` = Rex defaults. `method_not_found`/`not_supported` ⇒ server doesn't take themes; stop sending (RR:src/connection.rs:1179-1227). The client never recolours output locally.

---

## 7. Mutations

All take `session_id`; the connection must be attached. **Result `revision`** = session revision after the call; the change still arrives as `session_view_changed` (use it, not the result, to refresh). ✅ = executed LIVE on dasher.

### 7.1 Method table

| UI action | Method + payload (besides `session_id`) | Ref |
| --- | --- | --- |
| New session ✅(CLI) | `session.create {"label"?, "initial_windows":[{"window_label":"Shell","layout":{"block":{"flavor":"com.superlogical.terminal.shell"}}}]}` → `{session_id, initial_windows:[{window_id,block_ids,revision}]}`; then `session.attach`. Creator owns the blocks; server picks a label if omitted | RR:src/connection.rs:2071-2090 |
| New tab ✅ | `session.new_window {"window_label":"Shell","layout":<layout>,"focus":true}` → `{window_id, block_ids, revision}` | RR:src/app/workspace.rs:1533-1540 |
| Close tab ✅ | `session.close_window {"window_id"}` (omit = active) | RR:src/app/tabs.rs:475 |
| Rename tab ✅ | `session.set_window_label {"window_id","label"}` (`""` = default) | RR:src/app/workspace.rs:2218 |
| Move tab ✅ | `session.move_window {"window_id","before_window_id"?}` (omit = end) | RR:src/app/tabs.rs:434 |
| Switch tab ✅ | `session.focus_window {"window_id"}`; `session.focus_next_window`/`focus_previous_window {"wrap":true}` | RR:src/connection.rs:2043-2049 |
| Rename session ✅ | `session.set_label {"label"}` (`""` = server's original) | RR:src/app/workspace.rs:2212 |
| Reorder sessions | `session.move {"before_session_id"?}` | RR:src/app/workspace.rs:1780 |
| Kill session | `session.destroy {}` (session_id) | RR:src/app/workspace.rs:1801 |
| Switch session | `session.attach` + local selection (§3.4) | RR:src/connection.rs:2057-2064 |
| Split ✅ | `session.new_split {"anchor_id":<block>,"direction":D,"side":S,"layout":<layout>,"focus":true,"ratio"?}` → `{anchor_id, block_ids, split_ids, revision}` | RR:src/app/splits.rs:74-87 |
| Close pane ✅ | `block.close {"block_id"}` (or `window_id` = its focused block; `keep_last:true` to refuse the last) → `{block_id, revision}` | RR:src/app/workspace.rs:1855-1866 |
| Focus pane ✅ | `session.focus_block {"block_id"}`; `session.focus_direction {"direction":"left\|right\|up\|down","wrap":true}`; `session.focus_next_pane`/`focus_previous_pane {"wrap":true}` | RR:src/connection.rs:205-212, RR:src/app/splits.rs:91-98 |
| Zoom ✅ | `session.zoom_block {"block_id","zoom":true\|false}` (omit `zoom` = toggle) → `{block_id, zoomed, revision}`. ⚠ RR:src/bin/workspace_probe.rs:153-155 sends `zoomed`, which the server ignores (toggles) | LLMS:4845-4890 |
| Swap panes ✅ | `session.swap_blocks {"block_id","other_block_id"}` (focus follows) | RR:src/app/pane_moves.rs:31-38 |
| Move pane | `session.move_block {"block_id","anchor_block_id","direction","side","ratio"?}` (across windows too) | RR:src/app/compatibility.rs:341 |
| Resize divider ✅ | `session.resize_pane {"direction":"horizontal\|vertical","before_block_id","after_block_id","ratio":0<r<1}` — ids = first block on each side of the divider; always bumps revision | RR:src/app/workspace.rs:2322-2336 |
| Balance | client computes equal ratios per same-direction chain, sends one `resize_pane` per divider; never installs the layout locally | RR:src/layout.rs:180-, RR:src/app/compatibility.rs:213-229 |
| Rename pane ✅ | `session.set_block_label {"block_id","label"}` | RR:src/app/workspace.rs:2224 |
| Clear ✅ / Reset terminal | `com.superlogical.terminal.clear` / `.reset` `{"block_id","args":{}}`, or `client.action {"action":"com.superlogical.terminal.clear","session_id","block_id","origin":"palette"}` | RR:src/connection.rs:2173-2186 |
| Floating layers | `session.float_block`, `new_layer`, `resize_layer`, `raise_layer`, `lower_layer`, `set_layer_state`, `close_layer` | LLMS:3078-4652 |

Split direction mapping (RR:src/app/splits.rs:58-69):

| UI | `direction` | `side` |
| --- | --- | --- |
| Split right | `horizontal` | `after` |
| Split left | `horizontal` | `before` |
| Split down | `vertical` | `after` |
| Split up | `vertical` | `before` |
| Best fit | taller than wide ⇒ down, else right | |

Layout for new terminals (RR:src/layout.rs:11-28): `{"block":{"flavor":"com.superlogical.terminal.shell","label"?, "options":{"shell"?,"cwd"?,"theme"?,"command"?,"size"?}}}` (options schema: `block_creator.list`; `shell:"none"` + `command` runs directly). Splits inside a layout: `{"split":{"direction","before":<node>,"after":<node>,"ratio"?}}`. Inherit cwd like rex-rune: `…terminal.process` (1 s timeout) → `child.cwd` (or the shell below `login` on macOS) → `options.cwd` (RR:src/connection.rs:930-935, 2096-2111, RR:src/connection/activity.rs:294-310).

Every creation request is sent **once**; on timeout/disconnect, re-read state, never resend (RR:src/connection.rs:1536, 2108).

### 7.2 Simplest correct way to do "split right", "new tab", …

Call the `session.*` method directly from the UI (table above). `client.action` cannot run client UI actions: LIVE `client.action {"action":"pane.split"}` → `not_found "the server cannot perform \"pane.split\" … (client actions are performed by the client)"`. Use `client.action` only for **server** actions (`kind: "lua"` config actions, `kind: "block"` creator actions such as `com.superlogical.terminal.reset`).

### 7.3 Keymap layer (optional for v1) — LLMS:1554-1683, RR:src/connection/keymap.rs, RR:src/app/input.rs:35-195

1. After attach send `client.keymap`:
   ```json
   {"actions":[{"name":"pane.split","kind":"client","title":"Split right", "args":{…schema…},"repeats":false}, …],
    "server_actions":"all",
    "defaults":[{"name":"windows","bindings":[{"keys":["ctrl+shift+d"],"action":"pane.split","args":{"direction":"right"}}, …]}],
    "overrides":[{"name":"settings","bindings":[…],"removals":[…]}]}
   ```
   rex-rune's defaults (Control set for Linux/Windows): RR:src/keymap.rs:406-499 (e.g. `ctrl+shift+t` `client.tab.new`, `ctrl+shift+d` `pane.split {direction:right}`, `ctrl+alt+d` `pane.split {direction:down}`, `ctrl+alt+<arrow>` `pane.focus`, `ctrl+shift+enter` `pane.zoom`, `ctrl+shift+w` `client.close`, `ctrl+k` `session.switch`).
2. Result `bindings[]` (resolved, top first), `diagnostics[]`, `actions[]` (server's: `kind` `lua`/`block`), `modes[]`, `config{path,exists,generation,instance_id,hash}`. LIVE (no init.lua): 0 bindings, 20 block actions.
3. On each key press, find the first matching binding whose action is available (RR:src/app/actions.rs:819-842):
   - `client.key.forward` ⇒ send the key via `client.key` (with `prefix` for sequences);
   - one of **your** declared actions ⇒ perform locally (e.g. `pane.split` ⇒ `session.new_split`);
   - a server action (`kind` `lua`/`block`) ⇒ `client.action {"action","args"?,"origin":"key","session_id","block_id"?}` → `{result?, client_actions:[{action,args?}], ordered_after?}`; perform each `client_actions` entry locally.
   - no match ⇒ `client.key`.
   Sequences/modes are client state (server keeps none).
4. Re-request on `keymap_changed` with a new `hash`/`instance_id`.

### 7.4 Remote control (`client.perform`) — RR:src/connection/perform.rs, RR:docs/shortcuts.md#remote-control

Other clients may call `client.perform {client_id: <you>, action, args}`; you receive `client_perform_requested {call_id, action, args?, origin?, session_id?, block_id?, requester{client_id, info, principal}}` and must answer `client.reply {"call_id", "result": null}` or `{"call_id","error":{"code":"refused|not_supported|bad_request|not_found|internal","message"}}` within 5 s (rex-rune default: refuse; per-host opt-in, 8 view-only actions). Declared `actions` in `client.keymap` are what `client.inspect` advertises. `client_call_requested` (cross-server relay for `client.action servers[]`) — rex-rune never sends `servers`; ignore.

---

## 8. Titles, program status, activity

### 8.1 Initial reads per terminal block of the selected session (once per connection; RR:src/connection.rs:1133-1172)

| Method (`{"session_id","block_id","args":{}}`) | Result (LIVE) |
| --- | --- |
| `com.superlogical.terminal.program_status` | `{"records":[{"id"?,"state","kind"?,"progress"?,"app"?,"effective_app"?,"title"?,"msg"?,"updated_at","owner"?:{pid,name}}]}` |
| `com.superlogical.terminal.title` | `{"title":"joshua@dasher:~","owner":{"pid":1253935,"name":"zsh"}}` |
| `com.superlogical.terminal.process` (1 s timeout) | `{"last_exit":null,"child":{…},"foreground":{"pid","user_id","user","name","path","invoked_path","argv0","script"?,"cwd"},"ancestors"?:[…],"home":"/home/joshua"}` |

Afterwards everything is pushed as `block_event`s (only for attached sessions, or all sessions with `event_scope:"all"`).

### 8.2 Terminal `block_event` names (creator `com.superlogical.terminal`; schemas in `block_creator.list`, copy `_research/probe/out/block_creator_list.json`)

| `name` | `payload` (LIVE) | Use |
| --- | --- | --- |
| `title_changed` | `{"title":"windex-title","owner":{"pid":…,"name":"zsh"}}` (owner absent when unknown) | pane/tab title (OSC 0/2) |
| `process_changed` | `{"generation":3,"foreground":{…},"ancestors":[…]}` | program name/icon, cwd |
| `pwd_changed` | `{"pwd":"file://dasher/home/joshua","owner":{…}}` | cwd (OSC 7/9/1337) |
| `bell` | `{}` | bell |
| `progress_report` | `{"state":"set","progress":40}`; states `set`,`indeterminate`,`pause`,`error`,`remove` | OSC 9;4 progress bar |
| `program_status_changed` | `{"id":"","record":{"state":"blocked","kind":"question","app":"windex","effective_app":"windex","updated_at":"…","owner":{…}},"reason":"report"}` | OSC 7501 |
| `program_status_removed` | `{"ids":[""],"reason":"clear\|child_exited\|reset\|prompt\|foreground\|evicted"}` | remove records |
| `desktop_notification` | OSC 9: `{"title":"","body":"hello from windex"}`; OSC 777: `{"title":"Title","body":"Body"}` | notifications |
| `clipboard_written` | `{"text":"hello clip","id":1,"bytes":10}` (text absent if large ⇒ `…terminal.clipboard_read {"args":{"id","offset","max_bytes"}}` → `{id,data,total,eof}`) | OSC 52 copy |
| `size_changed` | `{"columns":100,"rows":30,"cell_width_px":9,"cell_height_px":19,"owner":"client:…","principal":{…}}` | size |
| `child_exited` | `{"exit_code":0,"error"?}` | exit |
| `url_clicked`, `selection_copied` | server-side link/selection (not seen LIVE) | optional |

OSC 7501 grammar (program side): `ESC ] 7501 ; key=value:key=value BEL`. LIVE accepted keys: `state` (`idle|working|done|blocked|error`), `kind` (`permission|question|auth`), `app`, `progress` (0–100). Reports containing `msg=` or `title=` (and `id=…` with `title=`) produced **no event** in LIVE tests — exact grammar UNVERIFIED (consume events only; don't depend on it). Unknown `state` ⇒ `idle`.

### 8.3 How rex-rune derives UI from them

- **Title** shown while valid: a program's title counts only while its `owner.pid` is in the block's current foreground chain (`foreground` + `ancestors` pids) (RR:src/connection.rs:447-456). A user label (`block.label` set via `set_block_label`) wins; window label `"Shell"` is the default tab label meaning "show the program's title" (RR:src/connection.rs:27-28). Generated titles `program - ~/dir` from `process` (RR:src/connection/titles.rs).
- **Program name** = basename of first non-empty of `foreground.script`, `argv0`, `name`, without leading `-` (RR:src/connection/activity.rs:140-166). **Icon** lookup: status record `app`/program name → script path suffix table → executable (`name`) → Shell; case-insensitive, strip path, `.exe`, version digits (`python3.12`) (RR:src/app/program_icons.rs:262-301; aliases RR:src/app/program_icons.rs:31+). Clear progress when the foreground program changes (RR:src/connection.rs:373-397).
- **Tab word** priority (RR:src/app/activity.rs:219-237, 274-281): `blocked` → "Needs approval" (permission) / "Question" / "Needs sign-in" (auth) / "Needs input" (no kind); `error` → "Error"; unseen bell → "Bell"; progress error → "Error"; paused → "Paused n%"; unseen done → "Done". Progress bar from `progress_report` (set/indeterminate accent, pause warning, error danger). Record state priority: blocked > error > working > done > idle (RR:src/connection/activity.rs:13-35).
- Activity for unattached sessions: rex-rune sweeps `program_status` + background `session.view` for other sessions (≤3 reads in flight) (RR:src/connection/status_sweep.rs) — optional.
- OSC 52: apply only from the focused pane of the visible session while the window has focus; ignore reads; cap 8 MiB (RR:src/connection/clipboard.rs:7-80, RR:docs/terminal.md:83-107).

---

## 9. Server info, clients, discovery, pairing

| Method | LIVE result |
| --- | --- |
| `server.version` | `{"rex_version":"0.0.1794517219-g7ec4d1","platform":"linux_amd64","commit":"7ec4d14d30723cf85ad42a8da53a650d24d6669c","built_at":"2026-11-12T21:00:19Z","dirty":false,"api_revision":13}` |
| `server.status` | `{"pid":1008,"instance_id":"8648a00f-…","host_id":"d093ac64-…","started_at":"…","exit_empty":false,"session_count":2,"tailscale":{"state":"disabled","allow_tailnet_members":false},"api_revision":13,"config":{"generation":1,"hash":""},"listeners":[{"endpoint":"unix:///run/user/1000/rex/server.sock","addresses":["/run/user/1000/rex/server.sock"],"transport":"unix","admission":"peer_credentials","primary":true},{"endpoint":"login-server:///run/user/1000/rex/login-server.sock","addresses":[…],"transport":"unix","admission":"login_server"}]}` |
| `server.whoami` | `{"principal":{"kind":"peer","transport":"unix","display":"uid 1000","remote_addr":"@","uid":1000,"gid":1000,"pid":1254196},"account":"joshua"}` |
| `client.list {"kinds":["app"]}` | `{"clients":[{"client_id","info":{kind,platform,name,version,label},"principal":{…},"connected_at","connection_state":"connected","session_ids":[…]}]}` |
| `session.list_clients` | clients attached to the session |

- `tailscale.state`: `disabled|starting|needs_login|needs_machine_approval|running`; `https.url` (e.g. `https://rex.tail1234.ts.net`) once `https.state == "available"` — that URL is the tailnet endpoint (LLMS:2915-2928).
- `listeners[].admission`: `peer_credentials`, `anonymous`, `tailscale_owner`, `tailscale_identified`, `login_server` (show unknown verbatim). Built-in Tailscale is not in `listeners`.
- **Pairing**: rex-rune's "Pair device" only shows a QR code of the server's `tailscale.https.url` (origin, no trailing slash); no protocol exchange (RR:src/app/pair_device.rs:28-41). There is no pairing/token API.
- `server.stop {"if_idle"?}` exists; attachment must never call it.

---

## 10. Minimum viable client checklist

From nothing to interactive panes for one session over SSH:

1. `ssh.exe -N -T -o BatchMode=yes -o ExitOnForwardFailure=yes -o ServerAliveInterval=15 -o ServerAliveCountMax=3 -L 127.0.0.1:<P>:<remote.sock> <user>@<host>`; wait until `127.0.0.1:<P>` accepts (≤20 s); watch the process for exit.
2. WS `ws://127.0.0.1:<P>/api/control`, protocol `rex.control.v1`, verify echo. Send hello (§2.2, `reconnect_token` if you have one). Read WELCOME; check `protocol`, `api_revision ∈ [10,13]`; keep `client.id`, token, `max_message_bytes`.
3. `session.bootstrap {"event_scope":"all","attach_session_ids":[<wanted>] /*or omit*/,"data_tickets":[{"creator_name":"com.superlogical.terminal","data_ref_id":"terminal","subprotocol":"rex.data.terminal.v1"}]}`. Store `seq`, sessions, views, tickets (expiry). Buffer events that arrived meanwhile; apply those with `seq > response.seq`.
4. Pick the session (attached view). If it isn't attached: `session.attach`.
5. Window = `active_window_id`. Panes = terminal blocks in its layers (honour zoom/minimized). Lay out from node `rect`s.
6. Per pane: ticket from pool or `data.connect`; WS `/api/data` `rex.data.terminal.v1`; send `01+ticket`; read 32-byte WELCOME (keep token, nonce). Accumulate bytes until snapshot FINISH (tag 6); decode with libghostty-vt snapshot decoder → Terminal; feed the rest through the APC filter (nonce) → `vt_write`; apply `resize`/`theme`/`child_exited`. Count `received`.
7. `session.own_block {"block_ids":[visible…],"mode":"take"}` when the window is focused; send `…terminal.resize` per owned pane with its cols/rows/cell px; resend on change.
8. Keys → `client.key` (press/repeat/release), IME commit/paste → `block.event paste`, mouse/wheel → `block.event` when the frame tracks the mouse, else local selection/scrollback.
9. Event loop: `session_view_changed` (revision ↑) → `session.view` → re-layout → open/close data channels; `block_closed` → close channel; `block_owner_changed` → owner flag (resize only when owner); `block_event` → titles/activity; `session_*` → sidebar.
10. Tab click → `session.focus_window`; split/close/zoom/rename → §7. Don't mutate local layout; wait for the view.
11. On control drop: reconnect with token (backoff 2/4/8 s), re-bootstrap (`attach_session_ids` = sessions you stream), drop queued input. On data drop: RESUME with latest token + `received`; on refusal new ticket + HELLO + new Terminal; on close 4000 stop.
12. On exit: close data sockets with 1000, close control with a close frame (ends client immediately), kill `ssh.exe`.

---

## Appendix A — LIVE captures (dasher, 2026-11-18, API 13)

Disposable session `windex-probe` (`session:01a1ec18-d71c-7dfd-9537-c599c22ffb01`), created with `rex new windex-probe --window Shell --json`, split with `rex split -s … --json`, mutated through the API, and killed with `rex kill windex-probe` afterwards. Tokens/tickets redacted. Full raw files: `_research/probe/out/` (`04_calls.json` every request/response, `05_events.json`, `06_transcript.json`, `*.bin` raw data streams, `11_rex_events_cli.jsonl`).

### A.1 Transport checks

```json
{"host_header":{"localhost":"101 rex.control.v1","127.0.0.1:47297":"101 rex.control.v1","dasher":"101 rex.control.v1","example.invalid":"101 rex.control.v1"},
 "no_subprotocol":{"status":"101","Sec-WebSocket-Protocol":null,"close":[1002,"protocol error"]},
 "hello api 99..99":{"close":[1008,"no common API revision: server speaks 0 to 13, client needs 99 to 99; update the server"]}}
```

Bun's stock `WebSocket` (`new WebSocket(url, ["rex.control.v1"])`) also worked end-to-end (control, bootstrap, data WELCOME, snapshot, APC), negotiating `permessage-deflate; server_no_context_takeover; client_no_context_takeover` on both sockets (`out/13_bun_probe.txt`).

### A.2 Welcome

```json
{"type":"welcome","protocol":"rex.control.v1","limits":{"max_message_bytes":1048576},
 "client":{"id":"client:01a1ec1a-64df-7c4e-b51a-52feb19daa7f","reconnect_token":"<redacted 22 chars>"},
 "api_revision":13,"api_server_revision":13}
```

After abrupt TCP close + hello with the token: `"client":{"id":"client:01a1ec1a-64df-…" (same),"reconnect_token":"<new>","resumed":true}`.

### A.3 `session.bootstrap`

Request:

```json
{"type":"request","id":"1","method":"session.bootstrap","payload":{"event_scope":"all",
 "attach_session_ids":["session:01a1ec18-d71c-7dfd-9537-c599c22ffb01"],
 "data_tickets":[{"creator_name":"com.superlogical.terminal","data_ref_id":"terminal","subprotocol":"rex.data.terminal.v1"}]}}
```

Response:

```json
{"type":"response","id":"1","seq":0,"payload":{
 "client_id":"client:01a1ec1a-64df-7c4e-b51a-52feb19daa7f",
 "server_status":{"pid":1008,"instance_id":"8648a00f-dc66-48b4-9dd9-4abd38d21ce4","host_id":"d093ac64-316e-4a23-b8e7-d28559568b60",
   "started_at":"2026-11-15T07:03:06.298088741Z","exit_empty":false,"session_count":2,
   "tailscale":{"state":"disabled","allow_tailnet_members":false},"api_revision":13,"config":{"generation":1,"hash":""},
   "listeners":[{"endpoint":"unix:///run/user/1000/rex/server.sock","addresses":["/run/user/1000/rex/server.sock"],"transport":"unix","admission":"peer_credentials","primary":true},
                {"endpoint":"login-server:///run/user/1000/rex/login-server.sock","addresses":["/run/user/1000/rex/login-server.sock"],"transport":"unix","admission":"login_server"}]},
 "attach_all":{"sessions":[{"session_id":"session:01a1dd8e-4ab2-7b0f-bf2d-46387a4ad4c4","label":"amber-sierra"},
                           {"session_id":"session:01a1ec18-d71c-7dfd-9537-c599c22ffb01","label":"windex-probe"}]},
 "selected_session":null,
 "attached_sessions":[{"session":{"session_id":"session:01a1ec18-d71c-7dfd-9537-c599c22ffb01","label":"windex-probe"},
   "view":{"revision":1,"session_id":"session:01a1ec18-d71c-7dfd-9537-c599c22ffb01","label":"windex-probe",
     "active_window_id":"window:01a1ec18-d73d-78fe-82f8-726e1d495b26",
     "windows":[{"window_id":"window:01a1ec18-d73d-78fe-82f8-726e1d495b26","label":"Shell",
       "focused_block_id":"block:01a1ec18-d71d-706d-aae1-4747c0eb131f",
       "layers":[{"layer_id":"layer:01a1ec18-d73d-790a-b32f-6b101054e199","kind":"tiled","bounds":[0,0,1,1],
         "layout":{"rect":[0,0,1,1],"block_id":"block:01a1ec18-d71d-706d-aae1-4747c0eb131f"},
         "blocks":[{"block_id":"block:01a1ec18-d71d-706d-aae1-4747c0eb131f","label":"shell","creator_name":"com.superlogical.terminal",
                    "flavor":"com.superlogical.terminal.shell","host_id":"d093ac64-316e-4a23-b8e7-d28559568b60",
                    "owner":"client:01a1ec18-d718-7c0b-a721-c2a788bf0c12"}]}]}]}}],
 "data_tickets":[{"session_id":"session:01a1ec18-d71c-7dfd-9537-c599c22ffb01","block_id":"block:01a1ec18-d71d-706d-aae1-4747c0eb131f",
   "data_ref_id":"terminal","subprotocol":"rex.data.terminal.v1","ticket":"<redacted 22 chars>","expires_in_ms":60000}]}}
```

Note: the block's `owner` is still the (already exited) `rex new` CLI client that created it; ownership is not dropped when a creator that never attached leaves.

### A.4 `session.list`

```json
{"type":"response","id":"5","seq":1,"payload":{"sessions":[
 {"session_id":"session:01a1dd8e-4ab2-7b0f-bf2d-46387a4ad4c4","label":"amber-sierra"},
 {"session_id":"session:01a1ec18-d71c-7dfd-9537-c599c22ffb01","label":"windex-probe"}]}}
```

### A.5 Views with splits

After `rex split -s <session> --json` (CLI result `{"anchor_id":"block:01a1ec18-d71d-…","block_ids":["block:01a1ec1a-8a0b-…"],"split_ids":["split:01a1ec1a-8a2d-…"],"revision":3}`):

```json
{"revision":4,"session_id":"session:01a1ec18-d71c-7dfd-9537-c599c22ffb01","label":"windex-probe",
 "active_window_id":"window:01a1ec18-d73d-78fe-82f8-726e1d495b26",
 "windows":[{"window_id":"window:01a1ec18-d73d-78fe-82f8-726e1d495b26","label":"Shell",
   "focused_block_id":"block:01a1ec1a-8a0b-7e4c-8bf3-6a46facce76a",
   "layers":[{"layer_id":"layer:01a1ec18-d73d-790a-b32f-6b101054e199","kind":"tiled","bounds":[0,0,1,1],
     "layout":{"rect":[0,0,1,1],"split":{"split_id":"split:01a1ec1a-8a2d-73e9-b44e-5b1c9613c3dd","direction":"horizontal","ratio":0.5,
        "before":{"rect":[0,0,0.5,1],"block_id":"block:01a1ec18-d71d-706d-aae1-4747c0eb131f"},
        "after":{"rect":[0.5,0,0.5,1],"block_id":"block:01a1ec1a-8a0b-7e4c-8bf3-6a46facce76a"}}},
     "blocks":[{"block_id":"block:01a1ec18-d71d-706d-aae1-4747c0eb131f","label":"shell","creator_name":"com.superlogical.terminal","flavor":"com.superlogical.terminal.shell","host_id":"d093ac64-316e-4a23-b8e7-d28559568b60","owner":"client:01a1ec1a-64df-7c4e-b51a-52feb19daa7f"},
               {"block_id":"block:01a1ec1a-8a0b-7e4c-8bf3-6a46facce76a","label":"shell","creator_name":"com.superlogical.terminal","flavor":"com.superlogical.terminal.shell","host_id":"d093ac64-316e-4a23-b8e7-d28559568b60"}]}]}]}
```

After `session.new_split {"anchor_id":"block:01a1ec18-d71d-…","direction":"vertical","side":"after","layout":{"block":{"flavor":"com.superlogical.terminal.shell","label":"api-split"}},"focus":true}` (→ `{"anchor_id":…,"block_ids":["block:01a1ec1a-92a5-…"],"split_ids":["split:01a1ec1a-92de-…"],"revision":5}`), renames, `resize_pane` 0.6, a second window moved first (revision 20; second window abbreviated):

```json
{"revision":20,"active_window_id":"window:01a1ec18-d73d-78fe-82f8-726e1d495b26","windows":[
 {"window_id":"window:01a1ec1a-9ff1-7066-b725-86e86f74b6c0","label":"Second","focused_block_id":"block:01a1ec1a-9fdd-…","layers":[{"kind":"tiled","bounds":[0,0,1,1],"layout":{"rect":[0,0,1,1],"block_id":"block:01a1ec1a-9fdd-…"},"blocks":[…]}]},
 {"window_id":"window:01a1ec18-d73d-78fe-82f8-726e1d495b26","label":"Renamed tab","focused_block_id":"block:01a1ec18-d71d-706d-aae1-4747c0eb131f",
  "layers":[{"layer_id":"layer:01a1ec18-d73d-790a-b32f-6b101054e199","kind":"tiled","bounds":[0,0,1,1],
   "layout":{"rect":[0,0,1,1],"split":{"split_id":"split:01a1ec1a-8a2d-73e9-b44e-5b1c9613c3dd","direction":"horizontal","ratio":0.6,
     "before":{"rect":[0,0,0.6,1],"split":{"split_id":"split:01a1ec1a-92de-73e6-a5a5-300d4f66b8d4","direction":"vertical","ratio":0.5,
        "before":{"rect":[0,0,0.6,0.5],"block_id":"block:01a1ec18-d71d-706d-aae1-4747c0eb131f"},
        "after":{"rect":[0,0.5,0.6,0.5],"block_id":"block:01a1ec1a-92a5-7087-9c9e-9d199d69348d"}}},
     "after":{"rect":[0.6,0,0.4,1],"block_id":"block:01a1ec1a-8a0b-7e4c-8bf3-6a46facce76a"}}},
   "blocks":[{"block_id":"block:01a1ec18-d71d-706d-aae1-4747c0eb131f","label":"main","owner":"client:01a1ec1a-64df-…", "…":"…"},
             {"block_id":"block:01a1ec1a-92a5-7087-9c9e-9d199d69348d","label":"api-split","owner":"client:01a1ec1a-64df-…"},
             {"block_id":"block:01a1ec1a-8a0b-7e4c-8bf3-6a46facce76a","label":"shell"}]}]}]}
```

### A.6 Events (raw envelopes, in order)

```json
{"type":"event","seq":3,"payload":{"type":"session_event","payload":{"session_id":"session:01a1ec18-…","event":{"type":"block_owner_changed","payload":{"block_id":"block:01a1ec18-d71d-…","owner":"client:01a1ec1a-64df-…","previous":"client:01a1ec18-d718-…","reason":"take"}}}}}
{"type":"event","seq":4,"payload":{"type":"session_event","payload":{"session_id":"session:01a1ec18-…","event":{"type":"session_view_changed","payload":{"revision":2}}}}}
{"type":"event","seq":5,"payload":{"type":"session_event","payload":{"session_id":"session:01a1ec18-…","event":{"type":"block_event","payload":{"block_id":"block:01a1ec18-d71d-…","creator_name":"com.superlogical.terminal","name":"size_changed","payload":{"columns":100,"rows":30,"cell_width_px":9,"cell_height_px":19,"owner":"client:01a1ec1a-64df-…","principal":{"kind":"peer","transport":"unix","display":"uid 1000","remote_addr":"@","uid":1000,"gid":1000,"pid":1254196}}}}}}}
{"type":"event","seq":13,"payload":{"type":"session_event","payload":{"session_id":"session:01a1ec18-…","event":{"type":"block_event","payload":{"block_id":"block:01a1ec18-d71d-…","creator_name":"com.superlogical.terminal","name":"progress_report","payload":{"state":"set","progress":40}}}}}}
{"type":"event","seq":17,"payload":{"type":"session_event","payload":{"session_id":"session:01a1ec18-…","event":{"type":"block_event","payload":{"block_id":"block:01a1ec18-d71d-…","creator_name":"com.superlogical.terminal","name":"process_changed","payload":{"generation":3,"foreground":{"pid":1254258,"user_id":1000,"user":"joshua","name":"sleep","path":"/usr/bin/sleep","invoked_path":"/usr/bin/sleep","argv0":"sleep","cwd":"/home/joshua"},"ancestors":[{"pid":1253935,"user_id":1000,"user":"joshua","name":"zsh","path":"/usr/bin/zsh","invoked_path":"/usr/bin/zsh","argv0":"-zsh","cwd":"/home/joshua"}]}}}}}}
{"type":"event","seq":18,"payload":{"type":"session_event","payload":{"session_id":"session:01a1ec18-…","event":{"type":"block_event","payload":{"block_id":"block:01a1ec18-d71d-…","creator_name":"com.superlogical.terminal","name":"program_status_changed","payload":{"id":"","record":{"state":"blocked","kind":"question","app":"windex","effective_app":"windex","updated_at":"2026-11-18T11:42:00.330975909+11:00","owner":{"pid":1253935,"name":"zsh"}},"reason":"report"}}}}}}
{"type":"event","seq":23,"payload":{"type":"client_joined","payload":{"client":{"client_id":"client:01a1ec1a-8a00-…","info":{"kind":"cli","platform":"linux","name":"rex","version":"0.0.1794779081-g2d5402"},"principal":{"kind":"peer","transport":"unix","display":"uid 1000","remote_addr":"@","uid":1000,"gid":1000,"pid":1254347},"connected_at":"2026-11-18T11:42:04.672253795+11:00","connection_state":"connected"}}}}
{"type":"event","seq":25,"payload":{"type":"session_event","payload":{"session_id":"session:01a1ec18-…","event":{"type":"block_created","payload":{"block_id":"block:01a1ec1a-8a0b-…"}}}}}
{"type":"event","seq":26,"payload":{"type":"session_event","payload":{"session_id":"session:01a1ec18-…","event":{"type":"block_owner_changed","payload":{"block_id":"block:01a1ec1a-8a0b-…","owner":"client:01a1ec1a-8a00-…","reason":"create"}}}}}
{"type":"event","seq":28,"payload":{"type":"session_event","payload":{"session_id":"session:01a1ec18-…","event":{"type":"session_view_changed","payload":{"revision":3}}}}}
{"type":"event","seq":42,"payload":{"type":"session_event","payload":{"session_id":"session:01a1ec18-…","event":{"type":"window_label_changed","payload":{"window_id":"window:01a1ec18-d73d-…","label":"Renamed tab"}}}}}
{"type":"event","seq":45,"payload":{"type":"session_event","payload":{"session_id":"session:01a1ec18-…","event":{"type":"session_label_changed","payload":{"label":"windex-probe-renamed"}}}}}
{"type":"event","seq":59,"payload":{"type":"session_event","payload":{"session_id":"session:01a1ec18-…","event":{"type":"window_created","payload":{"window_id":"window:01a1ec1a-9ff1-…","label":"Second"}}}}}
{"type":"event","seq":60,"payload":{"type":"session_event","payload":{"session_id":"session:01a1ec18-…","event":{"type":"active_window_changed","payload":{"window_id":"window:01a1ec1a-9ff1-…"}}}}}
{"type":"event","seq":68,"payload":{"type":"session_event","payload":{"session_id":"session:01a1ec18-…","event":{"type":"window_closed","payload":{"window_id":"window:01a1ec1a-9ff1-…"}}}}}
{"type":"event","seq":70,"payload":{"type":"session_event","payload":{"session_id":"session:01a1ec18-…","event":{"type":"block_closed","payload":{"block_id":"block:01a1ec1a-9fdd-…"}}}}}
```

Order observed for one `session.new_window`: `block_created` → `block_owner_changed(create)` → `window_created` → `active_window_changed` → `session_view_changed` → block `process_changed`. For `rex split` by another client: `client_joined`, `client_connected`, `block_created`, `block_owner_changed(create)`, `process_changed`, `session_view_changed`, then when the CLI leaves `block_owner_changed(reason: disconnect, no owner)`, `session_view_changed`, `client_disconnected`, `client_left`.

`rex events --json -s <session>` prints the **inner** event object (no `seq`), one per line, e.g.:

```json
{"type":"session_event","payload":{"session_id":"session:01a1ec18-d71c-7dfd-9537-c599c22ffb01","event":{"type":"block_owner_changed","payload":{"block_id":"block:01a1ec1c-63f1-77b3-9d79-f7597a680623","previous":"client:01a1ec1c-63ed-7241-bb85-fb388521a521","reason":"disconnect"}}}}
{"type":"session_event","payload":{"session_id":"session:01a1ec18-d71c-7dfd-9537-c599c22ffb01","event":{"type":"window_label_changed","payload":{"window_id":"window:01a1ec18-d73d-78fe-82f8-726e1d495b26","label":"Probe tab"}}}}
{"type":"session_event","payload":{"session_id":"session:01a1ec18-d71c-7dfd-9537-c599c22ffb01","event":{"type":"block_closed","payload":{"block_id":"block:01a1ec1c-63f1-77b3-9d79-f7597a680623"}}}}
```

(`rex events` rejects `--all` together with `-s`.)

### A.7 Data channel bytes

```
WELCOME   81 30f95bbbde1eaee0149aa5ad4c75d7f2 000000 60ea0000 ef56faad4682c7fa   (retention 60000, nonce 18070555269925590767)
message 1 (1749 bytes) = "GHOSTSNP\x01\x00"
          + rec(tag 1, len 961, crc 0x200b7606) + rec(2, 77) + rec(3, 466) + rec(7, 0) + rec(5, 0) + rec(4, 6) + rec(6, 0)   // 1590 bytes
          + "\x1b_rex1;18070555269925590767;{\"type\":\"resize\",\"cols\":80,\"rows\":24,\"cell_width_px\":8,\"cell_height_px\":16,\"owner\":\"client:01a1ec18-d718-…\"}\x1b\\"
RESUME    02 <token> df25000000000000 (offset 9695) → WELCOME same nonce, new token; stream continued without snapshot
block.close of a streamed block → data close 4000 "block closed"
```

### A.8 Representative call results

```json
{"method":"session.own_block","payload":{"session_id":"…","block_ids":["block:01a1ec18-d71d-…"],"mode":"take"},
 "result":{"blocks":[{"block_id":"block:01a1ec18-d71d-706d-aae1-4747c0eb131f","owner":"client:01a1ec1a-64df-7c4e-b51a-52feb19daa7f"}]}}
{"method":"com.superlogical.terminal.size","result":{"columns":100,"rows":30,"cell_width_px":9,"cell_height_px":19,"owner":"client:01a1ec1a-64df-…","desired":[{"client_id":"client:01a1ec1a-64df-…","columns":100,"rows":30,"updated_at":"2026-11-18T11:41:56.866734502+11:00"}]}}
{"method":"client.key","result":{"handled":false}}
{"method":"session.zoom_block","payload":{"block_id":"block:01a1ec18-d71d-…","zoom":true},"result":{"block_id":"block:01a1ec18-d71d-706d-aae1-4747c0eb131f","zoomed":true,"revision":12}}
{"method":"client.action","payload":{"action":"com.superlogical.terminal.clear","origin":"palette","session_id":"…","block_id":"…"},"result":{"result":{},"client_actions":[],"ordered_after":"83"}}
{"method":"client.keymap","payload":{},"result":{"bindings":[],"diagnostics":[],"actions":["…20 block actions/methods…"],"config":{"path":"/home/joshua/.config/rex/init.lua","exists":false,"generation":1,"instance_id":"8648a00f-dc66-48b4-9dd9-4abd38d21ce4","hash":""}}}
{"method":"no.such_method","error":{"code":"method_not_found","message":"netapi: no handler for method \"no.such_method\"","status":404}}
{"method":"session.view","payload":{"session_id":"session:<random uuid>"},"error":{"code":"session_not_found","message":"session session:e78a37ec-… not found","status":404}}
{"method":"session.zoom_block","payload":{"block_id":"…","window_id":"…"},"error":{"code":"bad_request","message":"block_id and window_id are mutually exclusive","status":400}}
```

## Appendix B — Probe scripts

`_research/probe/` (run with `py -I <script> …`; Python 3.14 is installed via the `py` launcher — the bare `python` command is the Microsoft Store stub; Bun at `%USERPROFILE%\.bun\bin\bun.exe`):

| File | What |
| --- | --- |
| `rexws.py` | stdlib WebSocket client (upgrade, framing, ping/pong, control request/event loop, data HELLO/RESUME/WELCOME, snapshot boundary) |
| `probe_live.py` | transport checks, bootstrap, data channel, ownership/resize/theme, typing, OSC events, CLI split, all layout mutations, errors, data RESUME, control reconnect |
| `probe_input.py`, `probe_input2.py` | server-side key/paste/mouse/wheel/focus encoding, OSC 7501/52, `rex events --json` |
| `probe_mouse*.py` | raw-mode PTY reader for exact mouse reports (left-button issue §5.5) |
| `probe_bun.ts` | the same attach path with Bun's stock `WebSocket` |
