//! The C ABI (core/include/rexcore.h). Every export catches panics: unwinding into C# is UB.
use crate::core::{Core, KeyEvent, MouseEvent, Options};
use crate::events::{Callback, Emitter};
use crate::frame::{ABI_VERSION, RexFrame};
use crate::pane::PaneMsg;
use crate::search;
use ghostty_vt::terminal::CursorStyle;
use std::ffi::{CStr, CString, c_char, c_void};
use std::panic::{AssertUnwindSafe, catch_unwind};
use std::sync::Arc;

#[repr(C)]
pub struct RexKey {
    pub code: *const c_char,
    pub key: *const c_char,
    pub text: *const c_char,
    pub mods: u32,
    pub action: u32,
}

#[repr(C)]
pub struct RexMouse {
    pub action: u32,
    pub button: u32,
    pub mods: u32,
    pub x: f32,
    pub y: f32,
    pub wheel_dy: f32,
}

/// The opaque `rex_core*`: a leaked `Arc<Core>`.
type Handle = *const Core;

unsafe fn core_ref(handle: Handle) -> Option<Arc<Core>> {
    if handle.is_null() {
        return None;
    }
    // SAFETY: `handle` came from Arc::into_raw in rex_core_new and is live until rex_core_free.
    unsafe {
        Arc::increment_strong_count(handle);
        Some(Arc::from_raw(handle))
    }
}

unsafe fn str_arg<'a>(p: *const c_char) -> Option<&'a str> {
    if p.is_null() {
        return None;
    }
    unsafe { CStr::from_ptr(p) }.to_str().ok()
}

fn guard<T>(fallback: T, f: impl FnOnce() -> T) -> T {
    catch_unwind(AssertUnwindSafe(f)).unwrap_or(fallback)
}

fn into_c_string(s: Option<String>) -> *mut c_char {
    match s.and_then(|s| CString::new(s.replace('\0', "")).ok()) {
        Some(c) => c.into_raw(),
        None => std::ptr::null_mut(),
    }
}

#[unsafe(no_mangle)]
pub extern "C" fn rex_core_abi_version() -> u32 {
    ABI_VERSION
}

/// # Safety
/// `options_json` is null or a NUL-terminated UTF-8 string; `callback` and `user` stay valid
/// until `rex_core_free` returns (rexcore.h).
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rex_core_new(options_json: *const c_char, callback: Option<Callback>, user: *mut c_void) -> Handle {
    guard(std::ptr::null(), || {
        let emitter = Emitter::new(callback, user);
        let options: Options = unsafe { str_arg(options_json) }
            .filter(|s| !s.trim().is_empty())
            .map(|s| serde_json::from_str(s).unwrap_or_else(|e| {
                emitter.log("warn", "core", format!("bad options json, using defaults: {e}"));
                Options::default()
            }))
            .unwrap_or_default();
        match Core::new(options, emitter.clone()) {
            Ok(core) => Arc::into_raw(core),
            Err(error) => {
                emitter.log("error", "core", format!("rex_core_new failed: {error:#}"));
                std::ptr::null()
            }
        }
    })
}

/// # Safety
/// `core` is null or a live handle from `rex_core_new`; pointer arguments are null or valid
/// for the duration of the call, as rexcore.h specifies.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rex_core_free(core: Handle) {
    if core.is_null() {
        return;
    }
    guard((), || {
        let core = unsafe { Arc::from_raw(core) };
        if Emitter::in_callback() {
            // Called from inside an event callback, perhaps on one of the core's own threads,
            // which can neither wait for itself nor drop the runtime. The UI hears nothing more
            // from here on; the teardown finishes on a thread of its own.
            core.emitter.close();
            let _ = std::thread::Builder::new().name("rex-core-free".into()).spawn(move || core.shutdown());
            return;
        }
        core.shutdown();
        drop(core);
    })
}

/// # Safety
/// `s` is null or a string this library returned, freed at most once.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rex_string_free(s: *mut c_char) {
    if !s.is_null() {
        drop(unsafe { CString::from_raw(s) });
    }
}

/// # Safety
/// `core` is null or a live handle from `rex_core_new`; pointer arguments are null or valid
/// for the duration of the call, as rexcore.h specifies.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rex_core_command(core: Handle, json: *const c_char) -> i32 {
    guard(-2, || {
        let (Some(core), Some(json)) = (unsafe { core_ref(core) }, unsafe { str_arg(json) }) else { return -1 };
        core.command(json)
    })
}

/// # Safety
/// `core` is null or a live handle from `rex_core_new`; pointer arguments are null or valid
/// for the duration of the call, as rexcore.h specifies.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rex_pane_key(core: Handle, pane: u64, key: *const RexKey) -> i32 {
    guard(-2, || {
        let Some(core) = (unsafe { core_ref(core) }) else { return -1 };
        if key.is_null() {
            return -1;
        }
        let k = unsafe { &*key };
        let event = KeyEvent {
            code: unsafe { str_arg(k.code) }.unwrap_or("").to_owned(),
            key: unsafe { str_arg(k.key) }.map(str::to_owned),
            text: unsafe { str_arg(k.text) }.filter(|s| !s.is_empty()).map(str::to_owned),
            mods: k.mods,
            action: k.action,
        };
        core.pane_input(pane).map(|i| i.key(&event)).unwrap_or(-1)
    })
}

/// # Safety
/// `core` is null or a live handle from `rex_core_new`; pointer arguments are null or valid
/// for the duration of the call, as rexcore.h specifies.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rex_pane_text(core: Handle, pane: u64, utf8: *const c_char) -> i32 {
    guard(-2, || {
        let (Some(core), Some(text)) = (unsafe { core_ref(core) }, unsafe { str_arg(utf8) }) else { return -1 };
        core.pane_input(pane).map(|i| i.text(text)).unwrap_or(-1)
    })
}

/// # Safety
/// `core` is null or a live handle from `rex_core_new`; pointer arguments are null or valid
/// for the duration of the call, as rexcore.h specifies.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rex_pane_paste(core: Handle, pane: u64, utf8: *const c_char) -> i32 {
    guard(-2, || {
        let (Some(core), Some(text)) = (unsafe { core_ref(core) }, unsafe { str_arg(utf8) }) else { return -1 };
        core.pane_input(pane).map(|i| i.paste(text)).unwrap_or(-1)
    })
}

/// # Safety
/// `core` is null or a live handle from `rex_core_new`; pointer arguments are null or valid
/// for the duration of the call, as rexcore.h specifies.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rex_pane_mouse(core: Handle, pane: u64, mouse: *const RexMouse) -> i32 {
    guard(-2, || {
        let Some(core) = (unsafe { core_ref(core) }) else { return -1 };
        if mouse.is_null() {
            return -1;
        }
        let m = unsafe { &*mouse };
        let event = MouseEvent { action: m.action, button: m.button, mods: m.mods, x: m.x, y: m.y, wheel_dy: m.wheel_dy };
        core.pane_input(pane).map(|i| i.mouse(&event)).unwrap_or(-1)
    })
}

/// # Safety
/// `core` is null or a live handle from `rex_core_new`; pointer arguments are null or valid
/// for the duration of the call, as rexcore.h specifies.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rex_pane_resize(core: Handle, pane: u64, cols: u16, rows: u16, cell_w: u32, cell_h: u32) -> i32 {
    guard(-2, || {
        let Some(core) = (unsafe { core_ref(core) }) else { return -1 };
        let (Some(shared), Some(input)) = (core.pane(pane), core.pane_input(pane)) else { return -1 };
        if cols == 0 || rows == 0 {
            return -1;
        }
        // The local view learns the cell geometry immediately (selection, mouse); the grid of a
        // remote pane follows whatever the server decides (its resize APC).
        if !core.is_local(pane) {
            shared.cell.store((u64::from(cell_w) << 32) | u64::from(cell_h), std::sync::atomic::Ordering::Relaxed);
        }
        input.resize(cols, rows, cell_w, cell_h);
        0
    })
}

/// # Safety
/// `core` is null or a live handle from `rex_core_new`; pointer arguments are null or valid
/// for the duration of the call, as rexcore.h specifies.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rex_pane_focus(core: Handle, pane: u64, focused: i32) -> i32 {
    guard(-2, || {
        let Some(core) = (unsafe { core_ref(core) }) else { return -1 };
        core.pane_input(pane).map(|i| { i.focus(focused != 0); 0 }).unwrap_or(-1)
    })
}

/// # Safety
/// `core` is null or a live handle from `rex_core_new`; pointer arguments are null or valid
/// for the duration of the call, as rexcore.h specifies.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rex_pane_scroll(core: Handle, pane: u64, op: i32, value: i64) -> i32 {
    guard(-2, || {
        let Some(core) = (unsafe { core_ref(core) }) else { return -1 };
        match core.pane(pane) {
            Some(p) if p.send(PaneMsg::Scroll { op, value }) => 0,
            _ => -1,
        }
    })
}

/// # Safety
/// `core` is null or a live handle from `rex_core_new`; pointer arguments are null or valid
/// for the duration of the call, as rexcore.h specifies.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rex_pane_select(core: Handle, pane: u64, op: i32, clicks: i32, x: f32, y: f32) -> i32 {
    guard(-2, || {
        let Some(core) = (unsafe { core_ref(core) }) else { return -1 };
        match core.pane(pane) {
            Some(p) if p.send(PaneMsg::Select { op, clicks, x, y }) => 0,
            _ => -1,
        }
    })
}

/// # Safety
/// `core` is null or a live handle from `rex_core_new`; pointer arguments are null or valid
/// for the duration of the call, as rexcore.h specifies.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rex_pane_search(core: Handle, pane: u64, query: *const c_char, flags: u32) -> i32 {
    guard(-2, || {
        let Some(core) = (unsafe { core_ref(core) }) else { return -1 };
        let Some(shared) = core.pane(pane) else { return -1 };
        let query = if query.is_null() { "" } else {
            match unsafe { str_arg(query) } {
                Some(query) => query,
                None => return -1,
            }
        };
        // Only the syntax is checked here, so a bad pattern is reported at once; the worker
        // compiles the query (tens of milliseconds for a big pattern).
        let (query, code) = match query {
            "" => (None, 0),
            query => match search::check(query, flags) {
                Ok(()) => (Some((query.to_owned(), flags)), 0),
                // Stale highlights from the last good pattern would mislead: clear them.
                Err(_) => (None, -3),
            },
        };
        if shared.send(PaneMsg::Search(query)) { code } else { -1 }
    })
}

/// # Safety
/// `core` is null or a live handle from `rex_core_new`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rex_pane_search_step(core: Handle, pane: u64, direction: i32) -> i32 {
    guard(-2, || {
        let Some(core) = (unsafe { core_ref(core) }) else { return -1 };
        match core.pane(pane) {
            Some(p) if p.send(PaneMsg::SearchStep(direction.signum())) => 0,
            _ => -1,
        }
    })
}

/// # Safety
/// `core` is null or a live handle from `rex_core_new`.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rex_pane_set_default_cursor(core: Handle, pane: u64, shape: i32) -> i32 {
    guard(-2, || {
        let Some(core) = (unsafe { core_ref(core) }) else { return -1 };
        let style = match shape {
            0 => CursorStyle::Block,
            1 => CursorStyle::Bar,
            2 => CursorStyle::Underline,
            3 => CursorStyle::BlockHollow,
            _ => return -1,
        };
        match core.pane(pane) {
            Some(p) if p.send(PaneMsg::DefaultCursor(style)) => 0,
            _ => -1,
        }
    })
}

fn ask<T>(core: &Arc<Core>, pane: u64, make: impl FnOnce(flume::Sender<Option<T>>) -> PaneMsg) -> Option<T> {
    let shared = core.pane(pane)?;
    let (tx, rx) = flume::bounded(1);
    if !shared.send(make(tx)) {
        return None;
    }
    rx.recv_timeout(std::time::Duration::from_secs(2)).ok().flatten()
}

/// # Safety
/// `core` is null or a live handle from `rex_core_new`; pointer arguments are null or valid
/// for the duration of the call, as rexcore.h specifies.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rex_pane_selection_text(core: Handle, pane: u64) -> *mut c_char {
    guard(std::ptr::null_mut(), || {
        let Some(core) = (unsafe { core_ref(core) }) else { return std::ptr::null_mut() };
        into_c_string(ask(&core, pane, PaneMsg::SelectionText))
    })
}

/// # Safety
/// `core` is null or a live handle from `rex_core_new`; pointer arguments are null or valid
/// for the duration of the call, as rexcore.h specifies.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rex_pane_hyperlink_at(core: Handle, pane: u64, col: i32, row: i32) -> *mut c_char {
    guard(std::ptr::null_mut(), || {
        let Some(core) = (unsafe { core_ref(core) }) else { return std::ptr::null_mut() };
        if col < 0 || row < 0 {
            return std::ptr::null_mut();
        }
        into_c_string(ask(&core, pane, |reply| PaneMsg::HyperlinkAt { col: col as u16, row: row as u16, reply }))
    })
}

/// # Safety
/// `core` is null or a live handle from `rex_core_new`; pointer arguments are null or valid
/// for the duration of the call, as rexcore.h specifies.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rex_pane_frame_lock(core: Handle, pane: u64) -> *const RexFrame {
    guard(std::ptr::null(), || {
        let Some(core) = (unsafe { core_ref(core) }) else { return std::ptr::null() };
        core.frame_lock(pane).unwrap_or(std::ptr::null())
    })
}

/// # Safety
/// `core` is null or a live handle from `rex_core_new`; pointer arguments are null or valid
/// for the duration of the call, as rexcore.h specifies.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rex_pane_frame_unlock(core: Handle, pane: u64) {
    guard((), || {
        if let Some(core) = unsafe { core_ref(core) } {
            core.frame_unlock(pane)
        }
    })
}

/// # Safety
/// `core` is null or a live handle from `rex_core_new`; pointer arguments are null or valid
/// for the duration of the call, as rexcore.h specifies.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rex_local_pane_new(core: Handle, cols: u16, rows: u16, echo: i32) -> u64 {
    guard(0, || {
        let Some(core) = (unsafe { core_ref(core) }) else { return 0 };
        core.local_pane_new(cols.max(1), rows.max(1), echo != 0)
    })
}

/// # Safety
/// `core` is null or a live handle from `rex_core_new`; pointer arguments are null or valid
/// for the duration of the call, as rexcore.h specifies.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rex_local_pane_feed(core: Handle, pane: u64, bytes: *const u8, len: usize) -> i32 {
    guard(-2, || {
        let Some(core) = (unsafe { core_ref(core) }) else { return -1 };
        if bytes.is_null() && len > 0 {
            return -1;
        }
        let data = if len == 0 { &[][..] } else { unsafe { std::slice::from_raw_parts(bytes, len) } };
        core.local_pane_feed(pane, data)
    })
}

/// # Safety
/// `core` is null or a live handle from `rex_core_new`; pointer arguments are null or valid
/// for the duration of the call, as rexcore.h specifies.
#[unsafe(no_mangle)]
pub unsafe extern "C" fn rex_local_pane_close(core: Handle, pane: u64) {
    guard((), || {
        if let Some(core) = unsafe { core_ref(core) }
            && core.is_local(pane)
        {
            core.panes.unregister(pane);
        }
    })
}
