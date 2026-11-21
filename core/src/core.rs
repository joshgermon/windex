//! The core object behind `rex_core*`: pane registry, input routing and command dispatch.
use crate::events::{Emitter, EventKind};
use crate::pane::{self, NullOutput, PaneConfig, PaneMsg, PaneShared};
use parking_lot::{Mutex, RwLock};
use serde::Deserialize;
use std::collections::HashMap;
use std::sync::Arc;
use std::sync::atomic::{AtomicU64, Ordering};
use std::time::{Duration, Instant};

/// Options passed to `rex_core_new` as JSON.
#[derive(Debug, Clone, Deserialize)]
#[serde(default)]
pub struct Options {
    pub client_name: String,
    pub client_version: String,
    pub label: String,
    pub state_dir: Option<String>,
    pub ssh_path: Option<String>,
    pub tailscale_path: Option<String>,
    pub log_level: String,
}

impl Default for Options {
    fn default() -> Self {
        Self {
            client_name: "Windex".into(),
            client_version: env!("CARGO_PKG_VERSION").into(),
            label: std::env::var("COMPUTERNAME").unwrap_or_else(|_| "windows".into()),
            state_dir: None,
            ssh_path: None,
            tailscale_path: None,
            log_level: "info".into(),
        }
    }
}

/// A key event from the UI (see `rex_key` in rexcore.h).
#[derive(Debug, Clone, Default)]
pub struct KeyEvent {
    pub code: String,
    pub key: Option<String>,
    pub text: Option<String>,
    pub mods: u32,
    /// 0 release, 1 press, 2 repeat.
    pub action: u32,
}

pub mod mods {
    pub const SHIFT: u32 = 1;
    pub const CTRL: u32 = 2;
    pub const ALT: u32 = 4;
    pub const SUPER: u32 = 8;
}

#[derive(Debug, Clone, Copy, Default)]
pub struct MouseEvent {
    /// 1 press, 2 release, 3 move, 4 wheel.
    pub action: u32,
    /// 0 none, 1 left, 2 right, 3 middle.
    pub button: u32,
    pub mods: u32,
    pub x: f32,
    pub y: f32,
    pub wheel_dy: f32,
}

/// Where a pane's input goes. Return codes follow rexcore.h: 0 ok, 1 paused, <0 error.
pub trait PaneInput: Send + Sync {
    fn key(&self, key: &KeyEvent) -> i32;
    fn text(&self, text: &str) -> i32;
    fn paste(&self, text: &str) -> i32;
    fn mouse(&self, _mouse: &MouseEvent) -> i32 {
        0
    }
    fn resize(&self, _cols: u16, _rows: u16, _cell_w: u32, _cell_h: u32) {}
    fn focus(&self, _focused: bool) {}
}

pub struct PaneEntry {
    pub shared: Arc<PaneShared>,
    pub input: Arc<dyn PaneInput>,
    /// Local panes are closed with `rex_local_pane_close`; remote ones by their host.
    pub local: bool,
}

/// Every pane the UI can address, local or remote. Shared with the remote tasks, which register
/// and close the panes of server blocks.
#[derive(Default)]
pub struct Panes {
    map: RwLock<HashMap<u64, PaneEntry>>,
    next: AtomicU64,
}

impl Panes {
    /// A new handle: non-zero and never reused within the process.
    pub fn allocate(&self) -> u64 {
        self.next.fetch_add(1, Ordering::Relaxed) + 1
    }

    pub fn register(&self, id: u64, entry: PaneEntry) {
        self.map.write().insert(id, entry);
    }

    /// Forget a pane and stop its worker.
    pub fn unregister(&self, id: u64) -> Option<PaneEntry> {
        let entry = self.map.write().remove(&id);
        if let Some(entry) = &entry {
            entry.shared.close();
        }
        entry
    }

    pub fn get(&self, id: u64) -> Option<Arc<PaneShared>> {
        self.map.read().get(&id).map(|e| e.shared.clone())
    }

    pub fn input(&self, id: u64) -> Option<Arc<dyn PaneInput>> {
        self.map.read().get(&id).map(|e| e.input.clone())
    }

    pub fn is_local(&self, id: u64) -> bool {
        self.map.read().get(&id).is_some_and(|e| e.local)
    }

    /// Stop every worker and wait (bounded) for their threads to end (shutdown).
    pub fn clear(&self) {
        let entries: Vec<PaneEntry> = self.map.write().drain().map(|(_, entry)| entry).collect();
        for entry in &entries {
            entry.shared.close();
        }
        let deadline = Instant::now() + WORKER_EXIT_WAIT;
        for entry in &entries {
            entry.shared.join(deadline);
        }
    }
}

/// How long shutdown waits for pane workers to finish what they are doing (one message at most).
const WORKER_EXIT_WAIT: Duration = Duration::from_secs(1);

pub struct Core {
    pub emitter: Emitter,
    pub options: Options,
    pub panes: Arc<Panes>,
    /// Rex server connections. Declared before `runtime` so its tasks' state drops first.
    pub remote: crate::remote::Remote,
    pub runtime: tokio::runtime::Runtime,
    /// Panes whose front frame the UI currently holds (keeps them alive until unlock).
    locked: Mutex<HashMap<u64, Arc<PaneShared>>>,
}

impl Core {
    pub fn new(options: Options, emitter: Emitter) -> anyhow::Result<Arc<Self>> {
        let emitter = emitter.with_log_level(&options.log_level);
        let _ = rustls::crypto::ring::default_provider().install_default();
        let runtime = tokio::runtime::Builder::new_multi_thread()
            .worker_threads(2)
            .thread_name("rex-core")
            .enable_all()
            .build()?;
        let panes = Arc::new(Panes::default());
        let remote = crate::remote::Remote::new(emitter.clone(), options.clone(), panes.clone(), runtime.handle().clone());
        Ok(Arc::new(Self { emitter, options, panes, remote, runtime, locked: Mutex::new(HashMap::new()) }))
    }

    /// Disconnect every host (closing sockets and ssh forwards), stop every pane worker, then
    /// stop calling the UI: nothing reaches its callback once this returns. Called once, from
    /// `rex_core_free`.
    pub fn shutdown(&self) {
        self.remote.shutdown();
        // A frame the UI left locked would block its worker's next publish.
        for (_, pane) in self.locked.lock().drain() {
            // SAFETY: frame_lock forgot exactly one guard for this pane; the UI is done with it.
            unsafe { pane.frame.force_unlock() };
        }
        self.panes.clear();
        self.emitter.close();
    }

    pub fn pane(&self, id: u64) -> Option<Arc<PaneShared>> {
        self.panes.get(id)
    }

    pub fn pane_input(&self, id: u64) -> Option<Arc<dyn PaneInput>> {
        self.panes.input(id)
    }

    pub fn is_local(&self, id: u64) -> bool {
        self.panes.is_local(id)
    }

    // ------------------------------------------------------------------ frames

    pub fn frame_lock(&self, id: u64) -> Option<*const crate::frame::RexFrame> {
        let pane = self.pane(id)?;
        if !pane.has_terminal.load(Ordering::Acquire) {
            return None;
        }
        let mut locked = self.locked.lock();
        if locked.contains_key(&id) {
            // Already locked by the UI: hand back the same frame.
            let ptr = unsafe { &(*pane.frame.data_ptr()).meta as *const _ };
            return Some(ptr);
        }
        let guard = pane.frame.lock();
        let ptr = &guard.meta as *const _;
        std::mem::forget(guard);
        pane.dirty_sent.store(false, Ordering::Release);
        locked.insert(id, pane);
        Some(ptr)
    }

    pub fn frame_unlock(&self, id: u64) {
        if let Some(pane) = self.locked.lock().remove(&id) {
            // SAFETY: frame_lock forgot exactly one guard for this pane.
            unsafe { pane.frame.force_unlock() };
        }
    }

    // ------------------------------------------------------------- local panes

    pub fn local_pane_new(self: &Arc<Self>, cols: u16, rows: u16, echo: bool) -> u64 {
        let id = self.panes.allocate();
        let shared = pane::spawn(
            PaneConfig { id, cols, rows, create_terminal: true, emitter: self.emitter.clone() },
            Box::new(NullOutput),
        );
        let input: Arc<dyn PaneInput> = Arc::new(LocalInput { pane: shared.clone(), echo });
        self.panes.register(id, PaneEntry { shared, input, local: true });
        id
    }

    pub fn local_pane_feed(&self, id: u64, bytes: &[u8]) -> i32 {
        match self.pane(id) {
            Some(pane) if pane.send(PaneMsg::Bytes(bytes.to_vec())) => 0,
            _ => -1,
        }
    }

    // ---------------------------------------------------------------- commands

    /// Queue a JSON command (docs/contract.md); its outcome arrives as a RESULT event. Never
    /// blocks on the network. Returns <0 for malformed JSON.
    pub fn command(&self, json: &str) -> i32 {
        let value: serde_json::Value = match serde_json::from_str(json) {
            Ok(v) => v,
            Err(_) => return -1,
        };
        let req = value.get("req").and_then(|v| v.as_u64()).unwrap_or(0);
        let cmd = value.get("cmd").and_then(|v| v.as_str()).unwrap_or("").to_owned();
        match cmd.as_str() {
            "ping" => self.emitter.result(req, Ok(serde_json::json!({ "pong": true, "version": env!("CARGO_PKG_VERSION") }))),
            _ => {
                if !self.remote.command(req, &cmd, &value) {
                    self.emitter.result(req, Err("unknown command".into()));
                }
            }
        }
        0
    }

    pub fn emit(&self, kind: EventKind, pane: u64, json: &str) {
        self.emitter.emit(kind, pane, json);
    }
}

/// Input for local panes: discarded, or echoed back into the terminal like a tty in cooked mode.
struct LocalInput {
    pane: Arc<PaneShared>,
    echo: bool,
}

impl LocalInput {
    fn echo_bytes(&self, text: &str) -> i32 {
        if self.echo {
            let bytes = text.replace('\r', "\r\n").into_bytes();
            self.pane.send(PaneMsg::Bytes(bytes));
        }
        0
    }
}

impl PaneInput for LocalInput {
    fn key(&self, key: &KeyEvent) -> i32 {
        if key.action == 0 {
            return 0;
        }
        match key.code.as_str() {
            "Enter" | "NumpadEnter" => self.echo_bytes("\r"),
            "Backspace" => self.echo_bytes("\x08 \x08"),
            "Tab" => self.echo_bytes("\t"),
            _ if key.mods & (mods::CTRL | mods::ALT) != 0 => 0,
            _ => key.text.as_deref().map(|t| self.echo_bytes(t)).unwrap_or(0),
        }
    }
    fn text(&self, text: &str) -> i32 {
        self.echo_bytes(text)
    }
    fn paste(&self, text: &str) -> i32 {
        self.echo_bytes(text)
    }
    fn resize(&self, cols: u16, rows: u16, cell_w: u32, cell_h: u32) {
        self.pane.send(PaneMsg::Resize { cols, rows, cell_w, cell_h });
    }
}
