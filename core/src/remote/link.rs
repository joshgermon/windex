//! The bridge between a server block and its pane: input from the UI (keys, text, paste,
//! mouse, focus, size) goes out on the host's control connection; ownership, stream liveness and
//! the server's size come back in. Everything here is callable from the UI thread and never
//! waits on the network (docs/protocol.md §5, §6).
use super::control::{CallError, CallResult, Control};
use super::keys;
use super::transport::Base;
use crate::core::{KeyEvent, MouseEvent, PaneInput, mods};
use crate::events::Emitter;
use crate::pane::{PaneMsg, PaneShared};
use parking_lot::Mutex;
use serde_json::{Value, json};
use std::collections::HashSet;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicU32, Ordering};
use tokio::sync::watch;

/// A live control connection of a host, as its panes and streams see it.
#[derive(Clone)]
pub struct Conn {
    pub control: Control,
    pub base: Base,
    pub client_id: String,
}

/// Per-host state shared with every pane and stream of the host.
pub struct HostLink {
    pub id: String,
    conn: watch::Sender<Option<Conn>>,
    /// The terminal theme the UI declared (`set_theme`), sent to blocks this client owns.
    pub theme: Arc<Mutex<Option<Value>>>,
    pub emitter: Emitter,
}

impl HostLink {
    pub fn new(id: String, theme: Arc<Mutex<Option<Value>>>, emitter: Emitter) -> Self {
        Self { id, conn: watch::channel(None).0, theme, emitter }
    }

    pub fn conn(&self) -> Option<Conn> {
        self.conn.borrow().clone().filter(|c| !c.control.is_closed())
    }

    pub fn control(&self) -> Option<Control> {
        self.conn().map(|c| c.control)
    }

    pub fn set_conn(&self, conn: Option<Conn>) {
        self.conn.send_replace(conn);
    }

    pub fn subscribe(&self) -> watch::Receiver<Option<Conn>> {
        self.conn.subscribe()
    }
}

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
struct Size {
    cols: u16,
    rows: u16,
    cell_w: u32,
    cell_h: u32,
}

#[derive(Default)]
struct Sizes {
    desired: Option<Size>,
    sent: Option<Size>,
}

#[derive(Default)]
struct InputState {
    /// Keys whose press reached the server; only their releases are sent.
    pressed: HashSet<&'static str>,
    /// The button held since a press that was sent, for drag motion.
    button: Option<&'static str>,
    /// Last cell a motion report went out for.
    motion_cell: Option<(u16, u16)>,
    /// Fractional wheel notches not yet sent.
    wheel: f32,
    focused: Option<bool>,
}

/// One server terminal block shown as a pane.
pub struct PaneLink {
    pub pane: u64,
    pub host: Arc<HostLink>,
    pub session: String,
    pub block: String,
    pub terminal: Arc<PaneShared>,
    owned: AtomicBool,
    claiming: AtomicBool,
    stream_live: AtomicBool,
    paused: AtomicBool,
    exited: AtomicBool,
    /// The authoritative PTY size from the resize APC, `cols << 16 | rows`; 0 = unknown.
    server_size: AtomicU32,
    sizes: Mutex<Sizes>,
    input: Mutex<InputState>,
}

impl PaneLink {
    pub fn new(pane: u64, host: Arc<HostLink>, session: String, block: String, terminal: Arc<PaneShared>, owned: bool) -> Arc<Self> {
        let link = Arc::new(Self {
            pane,
            host,
            session,
            block,
            terminal,
            owned: AtomicBool::new(owned),
            claiming: AtomicBool::new(false),
            stream_live: AtomicBool::new(false),
            paused: AtomicBool::new(true),
            exited: AtomicBool::new(false),
            server_size: AtomicU32::new(0),
            sizes: Mutex::new(Sizes::default()),
            input: Mutex::new(InputState::default()),
        });
        link.terminal.send(PaneMsg::Paused(true));
        link
    }

    pub fn owned(&self) -> bool {
        self.owned.load(Ordering::Acquire)
    }

    pub fn paused(&self) -> bool {
        self.paused.load(Ordering::Acquire)
    }

    pub fn exited(&self) -> bool {
        self.exited.load(Ordering::Acquire)
    }

    pub fn set_exited(&self) {
        self.exited.store(true, Ordering::Release);
    }

    pub fn server_size(&self) -> Option<(u16, u16)> {
        let v = self.server_size.load(Ordering::Acquire);
        (v != 0).then_some(((v >> 16) as u16, v as u16))
    }

    pub fn set_server_size(&self, cols: u16, rows: u16) {
        self.server_size.store((u32::from(cols) << 16) | u32::from(rows), Ordering::Release);
    }

    pub fn stream_live(&self) -> bool {
        self.stream_live.load(Ordering::Acquire)
    }

    /// The data stream (re)connected or dropped. Returns whether the paused state changed.
    pub fn set_stream_live(&self, live: bool) -> bool {
        self.stream_live.store(live, Ordering::Release);
        if live {
            // A new stream starts from the server's idea of our size: say it again.
            self.sizes.lock().sent = None;
            self.push_size();
        }
        self.refresh_paused()
    }

    /// Input works only with a live control connection and a live stream. Returns whether the
    /// paused state changed.
    pub fn refresh_paused(&self) -> bool {
        let paused = !(self.stream_live() && self.host.control().is_some());
        let changed = self.paused.swap(paused, Ordering::AcqRel) != paused;
        if changed {
            self.terminal.send(PaneMsg::Paused(paused));
        }
        changed
    }

    /// The control connection, when input can flow.
    fn live_control(&self) -> Option<Control> {
        if !self.stream_live() {
            return None;
        }
        self.host.control()
    }

    /// Ownership changed (view, event or a claim's result).
    pub fn set_owned(&self, owned: bool) {
        let was = self.owned.swap(owned, Ordering::AcqRel);
        if owned && !was {
            self.push_size();
            self.push_theme();
        }
    }

    /// Take ownership (`own_block take`) unless we have it or a claim is on its way. Sent
    /// before the press it precedes, on the same ordered connection (§5.8).
    pub fn claim(self: &Arc<Self>, control: &Control) {
        if self.owned() || self.claiming.swap(true, Ordering::AcqRel) {
            return;
        }
        let link = self.clone();
        let sent = control.send_then(
            "session.own_block",
            json!({"session_id": self.session, "block_ids": [self.block], "mode": "take"}),
            move |result: CallResult| {
                link.claiming.store(false, Ordering::Release);
                match result {
                    Ok(reply) => {
                        let client = link.host.conn().map(|c| c.client_id).unwrap_or_default();
                        let ours = reply.value["blocks"]
                            .as_array()
                            .into_iter()
                            .flatten()
                            .any(|b| b["block_id"] == link.block.as_str() && b["owner"] == client.as_str());
                        if ours {
                            link.set_owned(true);
                        }
                    }
                    Err(error) => link.host.emitter.log("warn", "remote", format!("own_block {} failed: {error}", link.block)),
                }
            },
        );
        if sent.is_err() {
            self.claiming.store(false, Ordering::Release);
        }
    }

    fn block_call(&self, control: &Control, method: &str, args: Value) {
        let _ = control.fire(method, json!({"session_id": self.session, "block_id": self.block, "args": args}));
    }

    fn block_event(&self, control: &Control, kind: &str, payload: Value) -> i32 {
        let request = json!({"session_id": self.session, "block_id": self.block, "event": {"type": kind, "payload": payload}});
        self.input_status(control.fire("block.event", request))
    }

    /// rexcore.h's return code for input: 0 sent, 1 paused (the connection is gone), <0 refused.
    fn input_status(&self, sent: Result<(), CallError>) -> i32 {
        match sent {
            Ok(()) => 0,
            Err(error) if error.code == "disconnected" => 1,
            Err(error) => {
                self.host.emitter.log("warn", "remote", format!("input for {} not sent: {error}", self.block));
                -1
            }
        }
    }

    /// Send our desired size if we own the block, its stream is live and it changed (§6).
    pub fn push_size(&self) {
        if !self.owned() || !self.stream_live() {
            return;
        }
        let Some(control) = self.host.control() else { return };
        let size = {
            let mut sizes = self.sizes.lock();
            match sizes.desired {
                Some(desired) if sizes.sent != Some(desired) => {
                    sizes.sent = Some(desired);
                    desired
                }
                _ => return,
            }
        };
        let mut args = json!({"columns": size.cols, "rows": size.rows});
        if size.cell_w > 0 && size.cell_h > 0 {
            args["cell_width_px"] = json!(size.cell_w.min(65535));
            args["cell_height_px"] = json!(size.cell_h.min(65535));
        }
        self.block_call(&control, "com.superlogical.terminal.resize", args);
    }

    /// Declare the UI's theme to an owned block.
    pub fn push_theme(&self) {
        if !self.owned() {
            return;
        }
        let Some(theme) = self.host.theme.lock().clone() else { return };
        if let Some(control) = self.host.control() {
            self.block_call(&control, "com.superlogical.terminal.set_theme", theme);
        }
    }

    /// A pixel position in the grid → a 0-based cell, clamped to the server's grid.
    fn cell_at(&self, x: f32, y: f32) -> (u16, u16) {
        let (cw, ch) = self.terminal.cell_size();
        let (cols, rows) = self.server_size().unwrap_or((u16::MAX, u16::MAX));
        let col = (x / cw.max(1) as f32).floor().clamp(0.0, f32::from(cols.saturating_sub(1)));
        let row = (y / ch.max(1) as f32).floor().clamp(0.0, f32::from(rows.saturating_sub(1)));
        (col as u16, row as u16)
    }
}

/// Room for a request's envelope around a paste's text (id, method, session and block ids).
const REQUEST_OVERHEAD: usize = 1024;

/// Split `text` into pieces whose JSON-escaped form fits in `budget` bytes. A piece ends after
/// its last newline when that falls in its second half, else between two characters.
fn paste_pieces(text: &str, budget: usize) -> Vec<&str> {
    // Every character fits on its own (the longest escape, `\u001b`, is 6 bytes).
    let budget = budget.max(6);
    let mut pieces = Vec::new();
    let mut rest = text;
    while !rest.is_empty() {
        let (mut end, mut size, mut newline) = (rest.len(), 0, None);
        for (i, c) in rest.char_indices() {
            let len = escaped_len(c);
            if size + len > budget {
                end = i;
                break;
            }
            size += len;
            if c == '\n' {
                newline = Some((i + 1, size));
            }
        }
        if end < rest.len()
            && let Some((after, at)) = newline
            && at * 2 >= budget
        {
            end = after;
        }
        let (piece, tail) = rest.split_at(end);
        pieces.push(piece);
        rest = tail;
    }
    pieces
}

/// The bytes `c` takes inside a JSON string, as serde_json writes it.
fn escaped_len(c: char) -> usize {
    match c {
        '"' | '\\' | '\u{8}' | '\t' | '\n' | '\u{c}' | '\r' => 2,
        '\0'..='\u{1f}' => 6,
        c => c.len_utf8(),
    }
}

/// `PaneInput` for a server block.
pub struct RemoteInput(pub Arc<PaneLink>);

impl PaneInput for RemoteInput {
    fn key(&self, event: &KeyEvent) -> i32 {
        let link = &self.0;
        let Some(control) = link.live_control() else { return 1 };
        let Some(payload) = keys::payload(event) else { return 0 };
        let name = keys::code_name(&event.code).unwrap_or("unidentified");
        match event.action {
            0 => {
                if !link.input.lock().pressed.remove(name) {
                    return 0;
                }
            }
            action => {
                if action == 1 {
                    link.input.lock().pressed.insert(name);
                }
                link.claim(&control);
                // Typing returns the view to the live screen.
                link.terminal.send(PaneMsg::Scroll { op: 2, value: 0 });
            }
        }
        let request = json!({"session_id": link.session, "block_id": link.block, "key": payload});
        link.input_status(control.fire("client.key", request))
    }

    /// Committed IME text: a paste of the text (§5.2).
    fn text(&self, text: &str) -> i32 {
        self.paste(text)
    }

    fn paste(&self, text: &str) -> i32 {
        let link = &self.0;
        let Some(control) = link.live_control() else { return 1 };
        if text.is_empty() {
            return 0;
        }
        link.claim(&control);
        link.terminal.send(PaneMsg::Scroll { op: 2, value: 0 });
        // More than one control message holds goes out in pieces, in order (§2.4).
        let budget = control.max_message_bytes().saturating_sub(REQUEST_OVERHEAD);
        for piece in paste_pieces(text, budget) {
            let status = link.block_event(&control, "paste", json!({"text": piece}));
            if status != 0 {
                return status;
            }
        }
        0
    }

    fn mouse(&self, mouse: &MouseEvent) -> i32 {
        let link = &self.0;
        let Some(control) = link.live_control() else { return 1 };
        let (x, y) = link.cell_at(mouse.x, mouse.y);
        // Shift is the local-selection override; never report it (§5.5).
        let mods_name = keys::mods_name(mouse.mods & !mods::SHIFT);
        let button = match mouse.button {
            1 => Some("left"),
            2 => Some("right"),
            3 => Some("middle"),
            _ => None,
        };
        match mouse.action {
            1 => {
                let Some(button) = button else { return 0 };
                link.claim(&control);
                {
                    let mut input = link.input.lock();
                    input.button = Some(button);
                    input.motion_cell = Some((x, y));
                }
                link.block_event(&control, "mouse", json!({"action": "press", "button": button, "x": x, "y": y, "mods": mods_name}))
            }
            2 => {
                let held = link.input.lock().button.take();
                let Some(button) = button.or(held) else { return 0 };
                link.block_event(&control, "mouse", json!({"action": "release", "button": button, "x": x, "y": y, "mods": mods_name}))
            }
            3 => {
                let held = {
                    let mut input = link.input.lock();
                    if input.motion_cell == Some((x, y)) {
                        return 0;
                    }
                    input.motion_cell = Some((x, y));
                    input.button
                };
                let mut payload = json!({"action": "motion", "x": x, "y": y, "mods": mods_name});
                if let Some(button) = held {
                    payload["button"] = json!(button);
                }
                link.block_event(&control, "mouse", payload)
            }
            4 => {
                // One report per notch; a precise touchpad accumulates (§5.6).
                let notches = {
                    let mut input = link.input.lock();
                    input.wheel += mouse.wheel_dy;
                    let whole = input.wheel.trunc();
                    input.wheel -= whole;
                    whole as i32
                };
                for _ in 0..notches.unsigned_abs().min(8) {
                    let delta = if notches > 0 { -1 } else { 1 };
                    if link.block_event(&control, "wheel", json!({"x": x, "y": y, "delta_x": 0, "delta_y": delta})) != 0 {
                        return 1;
                    }
                }
                0
            }
            _ => 0,
        }
    }

    fn resize(&self, cols: u16, rows: u16, cell_w: u32, cell_h: u32) {
        let link = &self.0;
        link.sizes.lock().desired = Some(Size { cols, rows, cell_w, cell_h });
        link.push_size();
    }

    fn focus(&self, focused: bool) {
        let link = &self.0;
        let Some(control) = link.live_control() else { return };
        if link.input.lock().focused.replace(focused) == Some(focused) {
            return;
        }
        link.block_event(&control, "focus", json!({"action": if focused { "in" } else { "out" }}));
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    fn json_len(piece: &str) -> usize {
        serde_json::to_string(piece).unwrap().len() - 2
    }

    #[test]
    fn escaped_lengths_match_serde_json() {
        for c in ['a', 'é', '世', '🦀', '"', '\\', '\n', '\r', '\t', '\u{8}', '\u{c}', '\0', '\u{1b}', '\u{1f}', '\u{7f}'] {
            assert_eq!(escaped_len(c), json_len(&c.to_string()), "{c:?}");
        }
    }

    #[test]
    fn small_pastes_stay_whole() {
        assert_eq!(paste_pieces("echo hi\n", 100), ["echo hi\n"]);
        assert!(paste_pieces("", 100).is_empty());
    }

    #[test]
    fn large_pastes_split_within_the_budget_and_rejoin() {
        let line = "x\u{1b}\"é🦀 some text\t\n";
        let text = line.repeat(500);
        for budget in [6, 7, 64, 1000, 4096] {
            let pieces = paste_pieces(&text, budget);
            assert!(pieces.len() > 1, "budget {budget}");
            assert!(pieces.iter().all(|p| !p.is_empty() && json_len(p) <= budget), "budget {budget}");
            assert_eq!(pieces.concat(), text, "budget {budget}");
        }
        // With room for many lines, pieces end at a line end.
        assert!(paste_pieces(&text, 4096).iter().rev().skip(1).all(|p| p.ends_with('\n')));
        // A single long line is cut between characters.
        let long = "é".repeat(1000);
        let pieces = paste_pieces(&long, 101);
        assert!(pieces.iter().all(|p| p.len() <= 100));
        assert_eq!(pieces.concat(), long);
    }
}
