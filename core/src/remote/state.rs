//! Everything the core knows about its hosts: sessions, views, blocks and their panes. Guarded
//! by one mutex in [`Shared`]; never held across an await.
//!
//! [`reconcile`] brings panes in line with the views: every block in a view gets a stable pane
//! handle; terminal blocks of attached sessions get a pane worker; the blocks visible in the
//! current session's active window get a data stream (docs/protocol.md §3.5), and are claimed
//! while the app has focus.
use super::Shared;
use super::activity::{Activity, Effect, TERMINAL_CREATOR};
use super::data::{self, StreamHandle};
use super::host::HostCmd;
use super::link::{HostLink, PaneLink, RemoteInput};
use crate::core::PaneEntry;
use crate::events::EventKind;
use crate::pane::{self, NullOutput, PaneConfig};
use rexrs::api::session::{LayerBlock, ViewResponse, Window};
use serde_json::{Value, json};
use std::collections::{HashMap, HashSet};
use std::sync::Arc;
use std::time::Instant;
use tokio::sync::mpsc;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Status {
    Connecting,
    Connected,
    Reconnecting,
    Offline,
    Error,
}

impl Status {
    pub fn as_str(self) -> &'static str {
        match self {
            Status::Connecting => "connecting",
            Status::Connected => "connected",
            Status::Reconnecting => "reconnecting",
            Status::Offline => "offline",
            Status::Error => "error",
        }
    }
}

#[derive(Clone, Debug, Default)]
pub struct ServerInfo {
    pub version: Option<String>,
    pub api: u32,
    pub instance: Option<String>,
    pub label: Option<String>,
}

pub struct Session {
    pub id: String,
    pub label: String,
    /// This client called `session.attach` (or bootstrap attached it) on this connection.
    pub attached: bool,
    pub attaching: bool,
    pub view: Option<ViewResponse>,
    /// The highest revision a `session.view` was sent for.
    pub fetching: u64,
}

impl Session {
    pub fn new(id: String, label: String) -> Self {
        Self { id, label, attached: false, attaching: false, view: None, fetching: 0 }
    }

    pub fn revision(&self) -> u64 {
        self.view.as_ref().map_or(0, |v| v.revision)
    }
}

pub struct Block {
    pub pane: u64,
    pub session: String,
    pub terminal: bool,
    pub owner: Option<String>,
    /// The view revision `owner` is known at: an event sets it to the current one, so only a
    /// newer view overrides what the event said.
    pub owner_rev: u64,
    pub activity: Activity,
    /// Size from `size_changed` events, for blocks without a stream.
    pub server_size: Option<(u16, u16)>,
    pub link: Option<Arc<PaneLink>>,
    pub stream: Option<StreamHandle>,
    /// title/process/program_status were read on this connection.
    pub meta_read: bool,
}

impl Block {
    /// Stop the stream and the pane worker.
    fn retire(&mut self, shared: &Shared) {
        if let Some(stream) = self.stream.take() {
            stream.drop_stream();
        }
        if self.link.take().is_some() {
            shared.panes.unregister(self.pane);
        }
    }
}

pub struct Host {
    pub id: String,
    pub name: String,
    pub endpoint: String,
    pub status: Status,
    pub error: Option<String>,
    pub server: Option<ServerInfo>,
    pub client_id: Option<String>,
    /// In the server's order (the sidebar order).
    pub sessions: Vec<Session>,
    /// Pane handles by block id: stable for the host's lifetime.
    pub handles: HashMap<String, u64>,
    pub blocks: HashMap<String, Block>,
    /// Data tickets from bootstrap: block → (ticket, expiry).
    pub tickets: HashMap<String, (String, Instant)>,
    pub link: Arc<HostLink>,
    pub tx: mpsc::UnboundedSender<HostCmd>,
}

impl Host {
    pub fn session(&self, id: &str) -> Option<&Session> {
        self.sessions.iter().find(|s| s.id == id)
    }

    pub fn session_mut(&mut self, id: &str) -> Option<&mut Session> {
        self.sessions.iter_mut().find(|s| s.id == id)
    }

    /// A session by id or exact unique label.
    pub fn find_session(&self, key: &str) -> Option<&Session> {
        self.session(key).or_else(|| {
            let mut matches = self.sessions.iter().filter(|s| s.label == key);
            match (matches.next(), matches.next()) {
                (Some(one), None) => Some(one),
                _ => None,
            }
        })
    }

    pub fn handle(&mut self, block: &str, allocate: impl FnOnce() -> u64) -> u64 {
        *self.handles.entry(block.to_owned()).or_insert_with(allocate)
    }

    pub fn connected(&self) -> bool {
        self.status == Status::Connected
    }

    /// Forget all blocks of a session (destroyed or gone).
    pub fn drop_session(&mut self, shared: &Shared, session: &str) {
        self.sessions.retain(|s| s.id != session);
        self.blocks.retain(|id, block| {
            if block.session == session {
                block.retire(shared);
                self.handles.remove(id);
                false
            } else {
                true
            }
        });
    }

    /// Stop every pane of the host (host removed).
    pub fn retire_all(&mut self, shared: &Shared) {
        for block in self.blocks.values_mut() {
            block.retire(shared);
        }
        self.blocks.clear();
    }
}

#[derive(Default)]
pub struct State {
    pub hosts: Vec<Host>,
    /// (host, session) the window shows.
    pub current: Option<(String, String)>,
    /// The UI chose the current session (`open_session`). A session `settle_current` picks by
    /// itself is shown and streamed but never claimed: the UI may be about to open another one.
    pub chosen: bool,
    pub app_focused: bool,
    /// (host, block) claimed since the app last gained focus: each visible pane is taken once
    /// per focus, so another client taking it back is left alone until then (or input here).
    /// Reconnects keep it; a new endpoint or removing the host drops the host's entries.
    pub claimed: HashSet<(String, String)>,
    pub revision: u64,
    /// File transfers in flight (the model's `transfers`).
    pub transfers: Vec<Arc<super::transfer::Transfer>>,
}

impl State {
    pub fn host(&self, id: &str) -> Option<&Host> {
        self.hosts.iter().find(|h| h.id == id)
    }

    pub fn host_mut(&mut self, id: &str) -> Option<&mut Host> {
        self.hosts.iter_mut().find(|h| h.id == id)
    }

    pub fn host_index(&self, id: &str) -> Option<usize> {
        self.hosts.iter().position(|h| h.id == id)
    }

    /// Forget which of a host's panes were claimed (it now points at another server, or is gone).
    pub fn forget_claims(&mut self, host: &str) {
        self.claimed.retain(|(h, _)| h != host);
    }

    /// The host and block behind a pane handle.
    pub fn find_pane(&self, pane: u64) -> Option<(&Host, &str, &Block)> {
        self.hosts.iter().find_map(|h| {
            h.blocks.iter().find(|(_, b)| b.pane == pane).map(|(id, b)| (h, id.as_str(), b))
        })
    }

    /// Make sure something is current: keep a current session that still exists, else the
    /// first session of the first connected host.
    pub fn settle_current(&mut self) {
        if let Some((host, session)) = &self.current {
            match self.host(host) {
                Some(h) if h.session(session).is_some() => return,
                // A host that is reconnecting keeps its selection until its list arrives.
                Some(h) if !h.connected() => return,
                _ => self.current = None,
            }
        }
        self.chosen = false;
        self.current = self
            .hosts
            .iter()
            .filter(|h| h.connected())
            .find_map(|h| h.sessions.first().map(|s| (h.id.clone(), s.id.clone())));
    }

    fn is_current(&self, host: &str, session: &str) -> bool {
        self.current.as_ref().is_some_and(|(h, s)| h == host && s == session)
    }
}

/// The window a view shows: `active_window_id`, else the first.
pub fn active_window(view: &ViewResponse) -> Option<&Window> {
    view.windows
        .iter()
        .find(|w| w.window_id.is_some() && w.window_id == view.active_window_id)
        .or(view.windows.first())
}

/// The tiled layer's blocks of a window, in layout order.
pub fn tiled_blocks(window: &Window) -> impl Iterator<Item = &LayerBlock> {
    window.layers.iter().find(|l| l.kind == "tiled").or(window.layers.first()).into_iter().flat_map(|l| l.blocks.iter())
}

pub fn is_terminal(block: &LayerBlock) -> bool {
    block.creator_name.as_deref() == Some(TERMINAL_CREATOR)
}

/// The terminal blocks shown on screen for a view (§3.5): the active window's tiled blocks, or
/// only the zoomed one.
pub fn visible_blocks(view: &ViewResponse) -> Vec<String> {
    let Some(window) = active_window(view) else { return Vec::new() };
    tiled_blocks(window)
        .filter(|b| is_terminal(b))
        .filter_map(|b| b.block_id.clone())
        .filter(|id| window.zoomed_block_id.as_ref().is_none_or(|z| z == id))
        .collect()
}

/// Bring host `index`'s panes in line with its views (see the module docs).
pub fn reconcile(shared: &Arc<Shared>, st: &mut State, index: usize) {
    st.settle_current();
    let current = st.current.clone();
    let focused = st.app_focused;
    // Only a session the UI chose is claimed (resized to this window); see `State::chosen`.
    let claim = focused && st.chosen;
    let claimed = &mut st.claimed;
    let host = &mut st.hosts[index];
    let control = host.link.control();

    // Every block in a view, with its session, the view's revision and the block's entry.
    let mut present: HashMap<String, (String, u64, &LayerBlock)> = HashMap::new();
    for session in &host.sessions {
        let Some(view) = &session.view else { continue };
        for layer in view.windows.iter().flat_map(|w| w.layers.iter()) {
            for block in &layer.blocks {
                if let Some(id) = &block.block_id {
                    present.insert(id.clone(), (session.id.clone(), view.revision, block));
                }
            }
        }
    }
    let attached: Vec<String> = host.sessions.iter().filter(|s| s.attached).map(|s| s.id.clone()).collect();
    // Panes exist for attached sessions and for the current one even before its attach
    // completes, so the UI can size them as soon as the model shows them.
    let current_here = current.as_ref().filter(|(h, _)| *h == host.id).map(|(_, s)| s.clone());
    let wanted = |session: &String| attached.contains(session) || current_here.as_ref() == Some(session);
    host.blocks.retain(|id, block| {
        let keep = present.contains_key(id);
        if !keep {
            block.retire(shared);
        }
        keep
    });
    let client_id = host.client_id.clone();
    for (id, (session, revision, layer_block)) in &present {
        let pane = *host.handles.entry(id.clone()).or_insert_with(|| shared.panes.allocate());
        let block = host.blocks.entry(id.clone()).or_insert_with(|| Block {
            pane,
            session: session.clone(),
            terminal: is_terminal(layer_block),
            owner: None,
            owner_rev: 0,
            activity: Activity::default(),
            server_size: None,
            link: None,
            stream: None,
            meta_read: false,
        });
        if *revision > block.owner_rev {
            block.owner = layer_block.owner.clone();
            block.owner_rev = *revision;
        }
        let owned = block.owner.is_some() && block.owner == client_id;
        if let Some(link) = &block.link {
            link.set_owned(owned);
        }
        if block.terminal && block.link.is_none() && wanted(session) {
            let terminal = pane::spawn(
                PaneConfig { id: pane, cols: 80, rows: 24, create_terminal: false, emitter: shared.emitter.clone() },
                Box::new(NullOutput),
            );
            let link = PaneLink::new(pane, host.link.clone(), session.clone(), id.clone(), terminal.clone(), owned);
            shared.panes.register(pane, PaneEntry { shared: terminal, input: Arc::new(RemoteInput(link.clone())), local: false });
            // A block we created is ours from the start: declare the theme now.
            link.push_theme();
            block.link = Some(link);
        }
        if block.terminal
            && !block.meta_read
            && let Some(control) = &control
        {
            block.meta_read = true;
            read_metadata(shared, control, &host.id, session, id);
        }
    }

    // Streams for what is on screen (once attached); claims while the app has focus; seen
    // activity.
    let Some(current_session) = current_here else { return };
    let Some(session) = host.session(&current_session) else { return };
    let streamable = session.attached;
    let Some(view) = session.view.as_ref() else { return };
    for id in visible_blocks(view) {
        let Some(block) = host.blocks.get_mut(&id) else { continue };
        if focused {
            block.activity.bell_unseen = false;
            block.activity.done_unseen = false;
        }
        let Some(link) = block.link.clone() else { continue };
        if streamable && block.stream.as_ref().is_none_or(StreamHandle::is_finished) && !link.exited() {
            block.stream = Some(data::spawn(shared.clone(), link.clone()));
        }
        if claim
            && let Some(control) = &control
            && claimed.insert((host.id.clone(), id.clone()))
        {
            link.claim(control);
        }
    }
}

/// The current session's attachment, when it isn't attached yet: `session.attach`, then its view.
pub fn ensure_current_attached(shared: &Arc<Shared>, st: &mut State) {
    let Some((host_id, session_id)) = st.current.clone() else { return };
    let Some(host) = st.host_mut(&host_id) else { return };
    let Some(control) = host.link.control() else { return };
    let Some(session) = host.session_mut(&session_id) else { return };
    if session.attached || session.attaching {
        return;
    }
    session.attaching = true;
    let shared2 = shared.clone();
    let sent = control.send_then("session.attach", json!({"session_id": session_id}), move |result| {
        let mut st = shared2.state.lock();
        let Some(index) = st.host_index(&host_id) else { return };
        let host = &mut st.hosts[index];
        let Some(session) = host.session_mut(&session_id) else { return };
        session.attaching = false;
        match result.map(|r| r.value) {
            Ok(value) => {
                session.attached = true;
                if let Ok(view) = serde_json::from_value::<ViewResponse>(value["view"].clone()) {
                    apply_view(session, view);
                }
                reconcile(&shared2, &mut st, index);
                drop(st);
                shared2.touch();
            }
            Err(error) => {
                drop(st);
                shared2.emitter.log("warn", "remote", format!("session.attach {session_id}: {error}"));
            }
        }
    });
    if sent.is_err() {
        session.attaching = false;
    }
}

/// Keep the newer view (§2.6: compare revisions).
pub fn apply_view(session: &mut Session, view: ViewResponse) -> bool {
    if session.view.as_ref().is_some_and(|v| v.revision >= view.revision) {
        return false;
    }
    if let Some(label) = &view.label {
        session.label = label.clone();
    }
    session.view = Some(view);
    true
}

/// Fetch a session's view when `revision` is newer than what we hold or are fetching. When the
/// last request in flight completes, `fetching` falls back to what we hold, so a later event for
/// a revision the reply didn't reach still fetches.
pub fn fetch_view(shared: &Arc<Shared>, host: &mut Host, session_id: &str, revision: u64) {
    let Some(control) = host.link.control() else { return };
    let host_id = host.id.clone();
    let Some(session) = host.session_mut(session_id) else { return };
    if revision <= session.revision() || revision <= session.fetching {
        return;
    }
    session.fetching = revision;
    let shared2 = shared.clone();
    let session_id = session_id.to_owned();
    let sent = control.send_then("session.view", json!({"session_id": session_id}), move |result| {
        let view = result.ok().and_then(|reply| serde_json::from_value::<ViewResponse>(reply.value).ok());
        let mut st = shared2.state.lock();
        let Some(index) = st.host_index(&host_id) else { return };
        let Some(session) = st.hosts[index].session_mut(&session_id) else { return };
        let changed = view.is_some_and(|view| apply_view(session, view));
        if session.fetching == revision {
            session.fetching = session.revision();
        }
        if changed {
            reconcile(&shared2, &mut st, index);
            drop(st);
            shared2.touch();
        }
    });
    if sent.is_err() {
        session.fetching = session.revision();
    }
}

/// Read the views of sessions bootstrap didn't return: all of them after a reconnect (events
/// were missed), or only those without one (`only_missing`).
pub fn refresh_views(shared: &Arc<Shared>, host: &mut Host, only_missing: bool) {
    let stale: Vec<(String, u64)> = host
        .sessions
        .iter()
        .filter(|s| !s.attached && (s.view.is_none() || !only_missing))
        .map(|s| (s.id.clone(), s.revision() + 1))
        .collect();
    for (session, next) in stale {
        fetch_view(shared, host, &session, next);
    }
}

/// Initial title/process/status reads for a terminal block (§8.1).
fn read_metadata(shared: &Arc<Shared>, control: &super::control::Control, host: &str, session: &str, block: &str) {
    for method in ["title", "process", "program_status"] {
        let shared2 = shared.clone();
        let (host, block2) = (host.to_owned(), block.to_owned());
        let _ = control.send_then(
            &format!("com.superlogical.terminal.{method}"),
            json!({"session_id": session, "block_id": block, "args": {}}),
            move |result| {
                let Ok(reply) = result else { return };
                let mut st = shared2.state.lock();
                let Some(block) = st.host_mut(&host).and_then(|h| h.blocks.get_mut(&block2)) else { return };
                match method {
                    "title" => block.activity.set_title(&reply.value),
                    "process" => block.activity.set_process(&reply.value),
                    _ => {
                        for record in reply.value["records"].as_array().into_iter().flatten() {
                            let id = record["id"].as_str().unwrap_or_default().to_owned();
                            block.activity.records.insert(id, super::activity::Record::parse(record));
                        }
                    }
                }
                shared2.touch();
            },
        );
    }
}

/// A desktop notification to raise once the state lock is released.
pub struct Notification {
    pub pane: u64,
    pub json: Value,
}

impl Notification {
    pub fn emit(self, shared: &Shared) {
        shared.emitter.emit_value(EventKind::Notify, self.pane, &self.json);
    }
}

/// Apply a terminal `block_event` to a block's activity. Returns the notification it raises:
/// finishes, waits for input and bells only for panes out of sight; explicit OSC 9 / 777
/// requests always (the UI decides how to show them).
pub fn apply_block_event(st: &mut State, host_index: usize, session: &str, payload: &Value) -> Option<Notification> {
    if payload["creator_name"] != TERMINAL_CREATOR {
        return None;
    }
    let (Some(block_id), Some(name)) = (payload["block_id"].as_str(), payload["name"].as_str()) else { return None };
    let in_front = st.app_focused
        && st.is_current(&st.hosts[host_index].id, session)
        && st.hosts[host_index]
            .session(session)
            .and_then(|s| s.view.as_ref())
            .is_some_and(|v| visible_blocks(v).iter().any(|b| b == block_id));
    let host = &mut st.hosts[host_index];
    let host_id = host.id.clone();
    let window = host
        .session(session)
        .and_then(|s| s.view.as_ref())
        .and_then(|v| v.windows.iter().find(|w| tiled_blocks(w).any(|b| b.block_id.as_deref() == Some(block_id))))
        .and_then(|w| w.window_id.clone());
    let block = host.blocks.get_mut(block_id)?;
    let body = &payload["payload"];
    if name == "size_changed"
        && let (Some(cols), Some(rows)) = (body["columns"].as_u64(), body["rows"].as_u64())
    {
        block.server_size = Some((cols.min(65535) as u16, rows.min(65535) as u16));
        return None;
    }
    let effect = block.activity.apply_event(name, body);
    let pane = block.pane;
    let title = block.activity.shown_title().unwrap_or_default();
    let (kind, title, body) = match effect {
        Effect::None => return None,
        Effect::Bell if in_front || block.activity.bell_unseen => return None,
        Effect::Bell => {
            block.activity.bell_unseen = true;
            ("bell", title, "Bell".to_owned())
        }
        Effect::Finished { .. } if in_front => return None,
        Effect::Finished { app } => {
            block.activity.done_unseen = true;
            ("done", app.unwrap_or(title), "Done".to_owned())
        }
        Effect::NeedsInput { .. } if in_front => return None,
        Effect::NeedsInput { word, message, app } => ("needs_input", app.unwrap_or(title), message.unwrap_or_else(|| word.into())),
        Effect::Notification { title, body } => (if title.is_empty() { "osc9" } else { "osc777" }, title, body),
    };
    Some(Notification {
        pane,
        json: json!({"kind": kind, "title": title, "body": body, "host": host_id, "session": session, "window": window, "pane": pane}),
    })
}

#[cfg(test)]
mod tests {
    use super::*;

    fn view(value: Value) -> ViewResponse {
        serde_json::from_value(value).unwrap()
    }

    fn terminal(id: &str) -> Value {
        json!({"block_id": id, "label": "shell", "creator_name": TERMINAL_CREATOR, "flavor": "com.superlogical.terminal.shell"})
    }

    #[test]
    fn visible_blocks_follow_the_active_window_and_zoom() {
        let mut v = json!({"revision": 3, "session_id": "s", "active_window_id": "w2", "windows": [
            {"window_id": "w1", "layers": [{"kind": "tiled", "blocks": [terminal("a")]}]},
            {"window_id": "w2", "layers": [
                {"kind": "tiled", "blocks": [terminal("b"), terminal("c"),
                    {"block_id": "o", "creator_name": "com.superlogical.onboarding"}]},
                {"kind": "floating", "blocks": [terminal("f")]}]}]});
        assert_eq!(visible_blocks(&view(v.clone())), ["b", "c"], "tiled terminals of the active window only");
        v["windows"][1]["zoomed_block_id"] = json!("c");
        assert_eq!(visible_blocks(&view(v.clone())), ["c"]);
        v["active_window_id"] = json!("gone");
        assert_eq!(visible_blocks(&view(v)), ["a"], "an unknown active window falls back to the first");
    }

    fn host(id: &str, status: Status, sessions: &[(&str, &str)]) -> Host {
        Host {
            id: id.into(),
            name: id.into(),
            endpoint: format!("ssh://{id}"),
            status,
            error: None,
            server: None,
            client_id: None,
            sessions: sessions.iter().map(|(id, label)| Session::new((*id).into(), (*label).into())).collect(),
            handles: HashMap::new(),
            blocks: HashMap::new(),
            tickets: HashMap::new(),
            link: Arc::new(HostLink::new(id.into(), Arc::default(), crate::events::Emitter::none())),
            tx: mpsc::unbounded_channel().0,
        }
    }

    #[test]
    fn sessions_by_id_or_unique_label() {
        let h = host("h", Status::Connected, &[("s1", "work"), ("s2", "dup"), ("s3", "dup")]);
        assert_eq!(h.find_session("s2").map(|s| s.id.as_str()), Some("s2"));
        assert_eq!(h.find_session("work").map(|s| s.id.as_str()), Some("s1"));
        assert!(h.find_session("dup").is_none(), "an ambiguous label names nothing");
        assert!(h.find_session("nope").is_none());
    }

    #[test]
    fn current_defaults_to_the_first_connected_session() {
        let mut st = State {
            hosts: vec![host("a", Status::Error, &[("x", "x")]), host("b", Status::Connected, &[("s1", "one"), ("s2", "two")])],
            ..State::default()
        };
        st.settle_current();
        assert_eq!(st.current, Some(("b".into(), "s1".into())));
        assert!(!st.chosen, "the core's own pick is never chosen");
        st.current = Some(("b".into(), "s2".into()));
        st.chosen = true;
        st.settle_current();
        assert_eq!(st.current, Some(("b".into(), "s2".into())), "a chosen session stays");
        assert!(st.chosen);
        st.hosts[1].sessions.remove(1);
        st.settle_current();
        assert_eq!(st.current, Some(("b".into(), "s1".into())), "a destroyed session falls back");
        assert!(!st.chosen, "the fallback is not claimed until the UI opens it");
        st.hosts[1].status = Status::Reconnecting;
        st.current = Some(("b".into(), "gone".into()));
        st.settle_current();
        assert_eq!(st.current, Some(("b".into(), "gone".into())), "kept while its host reconnects");
    }

    #[test]
    fn newer_views_win() {
        let mut session = Session::new("s".into(), "old".into());
        assert!(apply_view(&mut session, view(json!({"revision": 5, "label": "work"}))));
        assert_eq!((session.revision(), session.label.as_str()), (5, "work"));
        assert!(!apply_view(&mut session, view(json!({"revision": 4, "label": "stale"}))));
        assert!(!apply_view(&mut session, view(json!({"revision": 5}))));
        assert_eq!(session.label, "work");
    }
}
