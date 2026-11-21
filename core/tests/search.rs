//! Find in terminal and the default cursor shape, through the C ABI as the UI uses them: results
//! arrive in frames (`matches`, `search_total`, `search_current`), the viewport follows the
//! current match, a bad regular expression is refused, and closing the search clears it.
use rexcore::ffi;
use rexcore::frame::{ABI_VERSION, RexFrame, RexMatch, flags};
use std::ffi::{CString, c_char, c_void};
use std::time::{Duration, Instant};

const MATCH_CASE: u32 = 1;
const REGEX: u32 = 2;

unsafe extern "C" fn on_event(_user: *mut c_void, _kind: i32, _pane: u64, _json: *const c_char, _len: usize) {}

struct Pane {
    core: *const rexcore::Core,
    pane: u64,
}

/// What a test reads from one locked frame.
#[derive(Debug)]
struct Seen {
    matches: Vec<RexMatch>,
    total: u32,
    current: u32,
    active: bool,
    busy: bool,
    error: u8,
    viewport_top: u64,
    total_rows: u64,
    rows: u16,
    cursor_shape: u8,
    /// Each row's cells' text (a spacer cell's is empty).
    cells: Vec<Vec<String>>,
    lines: Vec<String>,
}

impl Pane {
    fn new(cols: u16, rows: u16) -> Self {
        let core = unsafe { ffi::rex_core_new(std::ptr::null(), Some(on_event), std::ptr::null_mut()) };
        assert!(!core.is_null());
        assert_eq!(ffi::rex_core_abi_version(), 2);
        let pane = unsafe { ffi::rex_local_pane_new(core, cols, rows, 0) };
        assert_ne!(pane, 0);
        Self { core, pane }
    }

    fn feed(&self, bytes: &[u8]) {
        assert_eq!(unsafe { ffi::rex_local_pane_feed(self.core, self.pane, bytes.as_ptr(), bytes.len()) }, 0);
    }

    fn search(&self, query: Option<&str>, flags: u32) -> i32 {
        let query = query.map(|q| CString::new(q).unwrap());
        let ptr = query.as_ref().map_or(std::ptr::null(), |q| q.as_ptr());
        unsafe { ffi::rex_pane_search(self.core, self.pane, ptr, flags) }
    }

    fn step(&self, direction: i32) {
        assert_eq!(unsafe { ffi::rex_pane_search_step(self.core, self.pane, direction) }, 0);
    }

    fn read(&self) -> Option<Seen> {
        let frame = unsafe { ffi::rex_pane_frame_lock(self.core, self.pane) };
        if frame.is_null() {
            return None;
        }
        let f: &RexFrame = unsafe { &*frame };
        assert_eq!(f.abi, ABI_VERSION);
        if f.cols == 0 {
            // Not published yet.
            unsafe { ffi::rex_pane_frame_unlock(self.core, self.pane) };
            return None;
        }
        let matches = if f.match_count == 0 {
            Vec::new()
        } else {
            unsafe { std::slice::from_raw_parts(f.matches, f.match_count as usize) }.to_vec()
        };
        let cells = unsafe { std::slice::from_raw_parts(f.cells, usize::from(f.cols) * usize::from(f.rows)) };
        let text = unsafe { std::slice::from_raw_parts(f.text, f.text_len as usize) };
        let rows: Vec<Vec<String>> = cells
            .chunks(usize::from(f.cols))
            .map(|row| {
                row.iter()
                    .map(|c| match c.text_len {
                        _ if c.flags & flags::SPACER != 0 => String::new(),
                        0 => " ".into(),
                        n => String::from_utf16_lossy(&text[c.text_off as usize..][..usize::from(n)]),
                    })
                    .collect()
            })
            .collect();
        let lines = rows.iter().map(|row| row.concat().trim_end().to_owned()).collect();
        let seen = Seen {
            matches,
            total: f.search_total,
            current: f.search_current,
            active: f.search_active != 0,
            busy: f.search_busy != 0,
            error: f.search_error,
            viewport_top: f.viewport_top,
            total_rows: f.total_rows,
            rows: f.rows,
            cursor_shape: f.cursor_shape,
            cells: rows,
            lines,
        };
        unsafe { ffi::rex_pane_frame_unlock(self.core, self.pane) };
        Some(seen)
    }

    /// Wait (up to 10 s) for a frame that passes `test`.
    fn wait(&self, what: &str, test: impl Fn(&Seen) -> bool) -> Seen {
        let deadline = Instant::now() + Duration::from_secs(10);
        loop {
            if let Some(seen) = self.read()
                && test(&seen)
            {
                return seen;
            }
            assert!(Instant::now() < deadline, "timed out waiting for {what}: {:?}", self.read().map(|s| (s.total, s.current, s.matches)));
            std::thread::sleep(Duration::from_millis(5));
        }
    }

    /// The text under a match span.
    fn text(seen: &Seen, m: &RexMatch) -> String {
        seen.cells[usize::from(m.row)][usize::from(m.col0)..=usize::from(m.col1)].concat()
    }
}

impl Drop for Pane {
    fn drop(&mut self) {
        unsafe { ffi::rex_core_free(self.core) };
    }
}

fn seq(pane: &Pane, last: u32) {
    let mut out = String::new();
    for i in 1..=last {
        out.push_str(&format!("{i}\r\n"));
    }
    pane.feed(out.as_bytes());
}

#[test]
fn finding_scrolls_to_the_match_and_highlights_it() {
    let pane = Pane::new(40, 12);
    seq(&pane, 5000);
    pane.feed("中文 4999 ascii\r\n".as_bytes());
    pane.feed(&b"more output\r\n".repeat(30));
    pane.wait("output", |s| s.total_rows > 5030);
    assert_eq!(pane.search(Some("4999"), 0), 0);
    let seen = pane.wait("two matches", |s| s.total == 2 && !s.busy && s.current == 1);
    assert!(seen.active);
    // The newest match is current; it is on screen with its text under it.
    let current: Vec<_> = seen.matches.iter().filter(|m| m.current == 1).collect();
    assert_eq!(current.len(), 1);
    assert_eq!(Pane::text(&seen, current[0]), "4999");
    // Down from the newest wraps to the oldest (top) match, scrolled into view.
    pane.step(1);
    let seen = pane.wait("the oldest match", |s| s.current == 2 && s.viewport_top + u64::from(s.rows) < s.total_rows);
    let current: Vec<_> = seen.matches.iter().filter(|m| m.current == 1).collect();
    assert_eq!(current.len(), 1, "{seen:?}");
    assert_eq!(Pane::text(&seen, current[0]), "4999");
    assert_eq!(seen.lines[usize::from(current[0].row)], "4999");
    // Up from the oldest wraps back to the newest.
    pane.step(-1);
    let seen = pane.wait("the newest match", |s| s.current == 1 && s.matches.iter().any(|m| m.current == 1));
    let current: Vec<_> = seen.matches.iter().filter(|m| m.current == 1).collect();
    assert_eq!(seen.lines[usize::from(current[0].row)], "中文 4999 ascii");
    // Its cells: 中 and 文 are two columns each.
    assert_eq!((current[0].col0, current[0].col1), (5, 8));
}

#[test]
fn bad_regex_is_refused_and_closing_clears() {
    let pane = Pane::new(40, 10);
    pane.feed(b"error: one\r\nError: two\r\nwarning\r\n");
    assert_eq!(pane.search(Some("error"), 0), 0);
    pane.wait("two matches ignoring case", |s| s.total == 2 && s.matches.len() == 2);
    assert_eq!(pane.search(Some("error"), MATCH_CASE), 0);
    pane.wait("one match with case", |s| s.total == 1 && s.matches.len() == 1);
    assert_eq!(pane.search(Some(r"^\w+:"), REGEX), 0);
    pane.wait("regex matches", |s| s.total == 2);
    assert_eq!(pane.search(Some("err(or"), REGEX), -3);
    let seen = pane.wait("the bad pattern clears the search", |s| !s.active);
    assert!(seen.matches.is_empty());
    // A valid pattern too big to compile is refused by the worker, in frames.
    assert_eq!(pane.search(Some(r"\w{200}"), REGEX), 0);
    let seen = pane.wait("too complex", |s| s.active && s.error == 2);
    assert_eq!((seen.total, seen.current, seen.matches.len()), (0, 0, 0));
    assert_eq!(pane.search(Some(r"\w{2}or"), REGEX), 0);
    pane.wait("the next query searches again", |s| s.error == 0 && s.total == 2);
    assert_eq!(pane.search(Some("warn"), 0), 0);
    pane.wait("a match", |s| s.total == 1);
    assert_eq!(pane.search(None, 0), 0);
    let seen = pane.wait("closed", |s| !s.active);
    assert_eq!((seen.total, seen.current, seen.error, seen.matches.len()), (0, 0, 0, 0));
    // Unknown panes are refused.
    assert_eq!(unsafe { ffi::rex_pane_search(pane.core, 999_999, std::ptr::null(), 0) }, -1);
    assert_eq!(unsafe { ffi::rex_pane_search_step(pane.core, 999_999, 1) }, -1);
}

#[test]
fn highlights_follow_new_output() {
    let pane = Pane::new(30, 6);
    pane.feed(b"marker\r\n");
    assert_eq!(pane.search(Some("marker"), 0), 0);
    pane.wait("the match", |s| s.total == 1 && s.matches.len() == 1);
    for i in 0..4 {
        pane.feed(format!("line {i}\r\n").as_bytes());
        let seen = pane.wait("output", |s| s.lines.iter().any(|l| *l == format!("line {i}")));
        for m in &seen.matches {
            assert_eq!(Pane::text(&seen, m), "marker", "{seen:?}");
        }
    }
    // More output scrolls it away; a second marker arrives in view.
    seq(&pane, 20);
    pane.feed(b"marker again\r\n");
    let seen = pane.wait("two matches", |s| s.total == 2 && !s.busy);
    assert_eq!(seen.matches.len(), 1);
    assert_eq!(Pane::text(&seen, &seen.matches[0]), "marker");
    assert_eq!(seen.lines[usize::from(seen.matches[0].row)], "marker again");
}

#[test]
fn default_cursor_shape_applies_until_the_program_sets_one() {
    let pane = Pane::new(20, 4);
    pane.feed(b"$ ");
    pane.wait("a frame", |s| s.lines[0] == "$");
    assert_eq!(pane.read().unwrap().cursor_shape, 0);
    assert_eq!(unsafe { ffi::rex_pane_set_default_cursor(pane.core, pane.pane, 1) }, 0);
    pane.wait("a bar cursor", |s| s.cursor_shape == 1);
    assert_eq!(unsafe { ffi::rex_pane_set_default_cursor(pane.core, pane.pane, 2) }, 0);
    pane.wait("an underline cursor", |s| s.cursor_shape == 2);
    // A program's DECSCUSR wins (2 = steady block) ...
    pane.feed(b"\x1b[2 q");
    pane.wait("the program's block", |s| s.cursor_shape == 0);
    assert_eq!(unsafe { ffi::rex_pane_set_default_cursor(pane.core, pane.pane, 1) }, 0);
    std::thread::sleep(Duration::from_millis(50));
    assert_eq!(pane.read().unwrap().cursor_shape, 0, "the setting does not override the program");
    // ... until it resets to the default.
    pane.feed(b"\x1b[0 q");
    pane.wait("back to the setting", |s| s.cursor_shape == 1);
    assert_eq!(unsafe { ffi::rex_pane_set_default_cursor(pane.core, pane.pane, 3) }, 0);
    pane.wait("a hollow cursor", |s| s.cursor_shape == 3);
    assert_eq!(unsafe { ffi::rex_pane_set_default_cursor(pane.core, pane.pane, 7) }, -1);
}
