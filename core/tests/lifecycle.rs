//! The core's promises about its UI callback, checked through the C ABI as the UI uses it:
//! nothing is called once `rex_core_free` returns, and BELs don't flood the UI.
use rexcore::ffi;
use std::ffi::{CString, c_char, c_void};
use std::sync::atomic::{AtomicBool, AtomicUsize, Ordering};
use std::time::{Duration, Instant};

const BELL: i32 = 5;
const RESULT: i32 = 3;

/// What a test's callback saw. `user` points at one of these.
#[derive(Default)]
struct Seen {
    freed: AtomicBool,
    after_free: AtomicUsize,
    bells: AtomicUsize,
    /// When set, the first BELL callback blocks for this long (a UI that is slow to return).
    stall_ms: AtomicUsize,
    stalled: AtomicBool,
    /// When set, the callback frees this core on the first RESULT (from inside the callback).
    free_from_callback: AtomicUsize,
}

unsafe extern "C" fn on_event(user: *mut c_void, kind: i32, _pane: u64, _json: *const c_char, _len: usize) {
    let seen = unsafe { &*(user as *const Seen) };
    if seen.freed.load(Ordering::SeqCst) {
        seen.after_free.fetch_add(1, Ordering::SeqCst);
    }
    if kind == BELL {
        seen.bells.fetch_add(1, Ordering::SeqCst);
        let stall = seen.stall_ms.swap(0, Ordering::SeqCst);
        if stall > 0 {
            seen.stalled.store(true, Ordering::SeqCst);
            std::thread::sleep(Duration::from_millis(stall as u64));
        }
    }
    let core = seen.free_from_callback.swap(0, Ordering::SeqCst);
    if kind == RESULT && core != 0 {
        unsafe { ffi::rex_core_free(core as *const rexcore::Core) };
        seen.freed.store(true, Ordering::SeqCst);
    }
}

fn new_core(seen: &Seen) -> *const rexcore::Core {
    let core = unsafe { ffi::rex_core_new(std::ptr::null(), Some(on_event), seen as *const Seen as *mut c_void) };
    assert!(!core.is_null());
    core
}

fn feed(core: *const rexcore::Core, pane: u64, bytes: &[u8]) {
    assert_eq!(unsafe { ffi::rex_local_pane_feed(core, pane, bytes.as_ptr(), bytes.len()) }, 0);
}

#[test]
fn no_callback_runs_after_free() {
    let seen = Seen::default();
    let core = new_core(&seen);
    // Output with a BEL on every line, as `cat` of a binary file gives, queued in many messages.
    let chunk = b"some output with a bell \x07 and more text after it\r\n".repeat(2_000);
    let panes: Vec<u64> = (0..4).map(|_| unsafe { ffi::rex_local_pane_new(core, 120, 40, 0) }).collect();
    for &pane in &panes {
        for _ in 0..40 {
            feed(core, pane, &chunk);
        }
    }
    let waiting = Instant::now();
    for &pane in &panes {
        feed(core, pane, b"\x07");
    }
    // One worker is held inside the UI's callback past the time free waits for workers, with
    // more output still queued behind it.
    seen.stall_ms.store(1500, Ordering::SeqCst);
    while !seen.stalled.load(Ordering::SeqCst) {
        assert!(waiting.elapsed() < Duration::from_secs(5), "no BELL arrived");
        std::thread::sleep(Duration::from_millis(1));
    }
    let started = Instant::now();
    unsafe { ffi::rex_core_free(core) };
    let took = started.elapsed();
    seen.freed.store(true, Ordering::SeqCst);
    std::thread::sleep(Duration::from_millis(500));
    assert_eq!(seen.after_free.load(Ordering::SeqCst), 0, "callbacks after rex_core_free returned");
    assert!(took < Duration::from_secs(3), "rex_core_free took {took:?}");
}

#[test]
fn free_from_inside_a_callback_silences_the_core() {
    let seen = Seen::default();
    let core = new_core(&seen);
    let pane = unsafe { ffi::rex_local_pane_new(core, 80, 24, 0) };
    feed(core, pane, &b"busy \x07\r\n".repeat(50_000));
    seen.free_from_callback.store(core as usize, Ordering::SeqCst);
    // `ping` answers synchronously, so the callback (and the free inside it) runs right here.
    let ping = CString::new(r#"{"cmd":"ping","req":1}"#).unwrap();
    assert_eq!(unsafe { ffi::rex_core_command(core, ping.as_ptr()) }, 0);
    assert!(seen.freed.load(Ordering::SeqCst), "the callback freed the core");
    std::thread::sleep(Duration::from_millis(500));
    assert_eq!(seen.after_free.load(Ordering::SeqCst), 0, "callbacks after rex_core_free returned");
}

#[test]
fn bells_are_coalesced() {
    let seen = Seen::default();
    let core = new_core(&seen);
    let pane = unsafe { ffi::rex_local_pane_new(core, 80, 24, 0) };
    feed(core, pane, &b"\x07".repeat(20_000));
    std::thread::sleep(Duration::from_millis(400));
    let burst = seen.bells.load(Ordering::SeqCst);
    assert!((1..=6).contains(&burst), "20000 BELs in a burst rang {burst} times");
    // A later, lone BEL still rings, at once.
    std::thread::sleep(Duration::from_millis(150));
    feed(core, pane, b"\x07");
    std::thread::sleep(Duration::from_millis(60));
    assert_eq!(seen.bells.load(Ordering::SeqCst), burst + 1);
    unsafe { ffi::rex_core_free(core) };
}
