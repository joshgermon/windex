//! Events from the core to the UI, delivered through the C callback.
use parking_lot::RwLock;
use std::cell::Cell;
use std::ffi::{CString, c_char, c_void};
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, Ordering};

pub type Callback =
    unsafe extern "C" fn(user: *mut c_void, kind: i32, pane: u64, json: *const c_char, len: usize);

#[repr(i32)]
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum EventKind {
    Model = 1,
    PaneDirty = 2,
    Result = 3,
    Notice = 4,
    Bell = 5,
    Clipboard = 6,
    Notify = 7,
    Log = 8,
    Discovery = 9,
}

/// A cheap, cloneable handle on the UI's callback. The UI promises the callback and its
/// `user` pointer stay valid until `rex_core_free` returns, so `rex_core_free` closes the
/// gate: no callback starts after that, wherever a clone of the emitter lives on.
#[derive(Clone)]
pub struct Emitter {
    callback: Option<Callback>,
    /// The UI's `user` pointer, as an integer: rexcore.h lets the callback run on any core thread.
    user: usize,
    /// LOG events below this level are dropped (0 debug, 1 info, 2 warn, 3 error).
    min_level: u8,
    gate: Arc<Gate>,
}

struct Gate {
    open: AtomicBool,
    /// Held shared by every callback in flight; `close` takes it exclusively to wait them out.
    in_flight: RwLock<()>,
}

thread_local! {
    /// How many UI callbacks this thread is inside (the UI may call into the core from one).
    static CALLBACK_DEPTH: Cell<u32> = const { Cell::new(0) };
}

/// Marks the current thread as inside a callback while alive.
struct InCallback;

impl InCallback {
    fn enter() -> Self {
        CALLBACK_DEPTH.with(|d| d.set(d.get() + 1));
        Self
    }
}

impl Drop for InCallback {
    fn drop(&mut self) {
        CALLBACK_DEPTH.with(|d| d.set(d.get() - 1));
    }
}

fn level_rank(level: &str) -> u8 {
    match level {
        "debug" | "trace" => 0,
        "info" => 1,
        "warn" | "warning" => 2,
        _ => 3,
    }
}

impl Emitter {
    pub fn new(callback: Option<Callback>, user: *mut c_void) -> Self {
        let gate = Arc::new(Gate { open: AtomicBool::new(true), in_flight: RwLock::new(()) });
        Self { callback, user: user as usize, min_level: 1, gate }
    }

    /// An emitter that drops everything (tests, probes).
    pub fn none() -> Self {
        Self::new(None, std::ptr::null_mut())
    }

    /// Only LOG events at `level` or above (`debug`, `info`, `warn`, `error`).
    pub fn with_log_level(self, level: &str) -> Self {
        Self { min_level: level_rank(level), ..self }
    }

    pub fn emit(&self, kind: EventKind, pane: u64, json: &str) {
        let Some(callback) = self.callback else { return };
        // serde_json never emits a raw NUL; anything else is a bug we must not crash on.
        let Ok(text) = CString::new(json) else { return };
        // Recursive: the UI may call into the core, and so emit, from inside a callback.
        let _in_flight = self.gate.in_flight.read_recursive();
        if !self.gate.open.load(Ordering::Acquire) {
            return;
        }
        let _depth = InCallback::enter();
        unsafe { callback(self.user as *mut c_void, kind as i32, pane, text.as_ptr(), json.len()) }
    }

    /// Stop calling the UI for good. Waits for callbacks running on other threads, so none is
    /// in progress or starts once this returns — unless it is called from inside a callback,
    /// which cannot wait for itself (see `rex_core_free`).
    pub fn close(&self) {
        self.gate.open.store(false, Ordering::Release);
        if !Self::in_callback() {
            drop(self.gate.in_flight.write());
        }
    }

    /// True on a thread that is inside the UI's callback right now.
    pub fn in_callback() -> bool {
        CALLBACK_DEPTH.with(|d| d.get() > 0)
    }

    pub fn emit_value(&self, kind: EventKind, pane: u64, value: &serde_json::Value) {
        self.emit(kind, pane, &value.to_string());
    }

    pub fn signal(&self, kind: EventKind, pane: u64) {
        self.emit(kind, pane, "");
    }

    pub fn log(&self, level: &str, target: &str, message: impl AsRef<str>) {
        if level_rank(level) < self.min_level {
            return;
        }
        self.emit_value(
            EventKind::Log,
            0,
            &serde_json::json!({ "level": level, "target": target, "message": message.as_ref() }),
        );
    }

    pub fn notice(&self, level: &str, title: &str, message: impl AsRef<str>, host: Option<&str>) {
        self.emit_value(
            EventKind::Notice,
            0,
            &serde_json::json!({ "level": level, "title": title, "message": message.as_ref(), "host": host }),
        );
    }

    pub fn result(&self, req: u64, outcome: Result<serde_json::Value, String>) {
        let value = match outcome {
            Ok(data) => serde_json::json!({ "req": req, "ok": true, "data": data }),
            Err(error) => serde_json::json!({ "req": req, "ok": false, "error": error }),
        };
        self.emit_value(EventKind::Result, 0, &value);
    }
}
