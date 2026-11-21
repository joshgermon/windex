//! One terminal pane: a worker thread that owns a libghostty-vt `Terminal` (handles are
//! `!Send`), parses PTY bytes into it, and publishes flattened frames for the UI.
//!
//! The worker is transport-agnostic. Remote panes feed it bytes from a Rex data channel and
//! receive its outbound effects (query responses, mouse reports) through [`PaneOutput`].
use crate::events::{Emitter, EventKind};
use crate::frame::{FrameBuf, FrameBuilder, FrameInputs};
use crate::search::{self, QueryError, Search};
use crate::selection::{self, Controller as Selection, Geometry, Input as SelInput, Phase};
use anyhow::Result;
use ghostty_vt::{
    Terminal,
    terminal::{CursorStyle, ScrollViewport},
};
use parking_lot::Mutex;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::thread::JoinHandle;
use std::time::{Duration, Instant};

/// Where a pane's outbound bytes and effects go.
pub trait PaneOutput: Send + 'static {
    /// Bytes the terminal emulator itself answers with (DA/DSR replies, size reports...).
    fn pty_write(&mut self, bytes: Vec<u8>);
    /// The terminal title or working directory changed.
    fn title_changed(&mut self, _title: &str, _pwd: &str) {}
    /// OSC 9;4 progress (state: 0 remove, 1 set, 2 error, 3 indeterminate, 4 pause).
    fn progress(&mut self, _state: u8, _percent: Option<u8>) {}
    /// OSC 9 / 777 desktop notification.
    fn notification(&mut self, _title: &str, _body: &str) {}
}

/// Output sink that discards everything (local preview panes).
pub struct NullOutput;
impl PaneOutput for NullOutput {
    fn pty_write(&mut self, _bytes: Vec<u8>) {}
}

pub enum PaneMsg {
    /// PTY output to parse.
    Bytes(Vec<u8>),
    /// Replace the terminal (a snapshot restore). Built on the worker thread by the closure; on
    /// an error the old terminal stays, and the closure is where its sender learns of it.
    Install(Box<dyn FnOnce() -> Result<Terminal> + Send>),
    Resize { cols: u16, rows: u16, cell_w: u32, cell_h: u32 },
    Scroll { op: i32, value: i64 },
    Select { op: i32, clicks: i32, x: f32, y: f32 },
    SelectionText(flume::Sender<Option<String>>),
    HyperlinkAt { col: u16, row: u16, reply: flume::Sender<Option<String>> },
    Paused(bool),
    /// Find in terminal: search for this query with these `rex_search_flags`, its syntax already
    /// checked (`None`: stop searching).
    Search(Option<(String, u32)>),
    /// Move the current match: > 0 the next one down, < 0 the next one up.
    SearchStep(i32),
    /// The cursor shape while the program has not chosen one (DECSCUSR).
    DefaultCursor(CursorStyle),
    /// Run a closure against the terminal on the worker thread (encoders etc.).
    With(Box<dyn FnOnce(&mut Terminal) + Send>),
    Close,
}

/// The shared half of a pane, held by the core and the FFI.
pub struct PaneShared {
    pub id: u64,
    pub frame: Mutex<FrameBuf>,
    /// True while a PANE_DIRTY event is outstanding (cleared by `rex_pane_frame_lock`).
    pub dirty_sent: AtomicBool,
    pub has_terminal: AtomicBool,
    pub paused: AtomicBool,
    pub cols: AtomicU64,
    pub rows: AtomicU64,
    pub cell: AtomicU64, // (w << 32) | h
    tx: flume::Sender<PaneMsg>,
    /// Set by `close`: the worker stops without working through what is still queued.
    closing: AtomicBool,
    worker: Mutex<Option<JoinHandle<()>>>,
}

impl PaneShared {
    pub fn send(&self, msg: PaneMsg) -> bool {
        self.tx.send(msg).is_ok()
    }

    /// Stop the worker; whatever is still queued is dropped.
    pub fn close(&self) {
        self.closing.store(true, Ordering::Release);
        let _ = self.tx.send(PaneMsg::Close);
    }

    fn closing(&self) -> bool {
        self.closing.load(Ordering::Acquire)
    }

    /// Wait until `deadline` for the worker thread to end (after `close`); a worker still busy
    /// then is left to finish on its own.
    pub fn join(&self, deadline: Instant) {
        let Some(handle) = self.worker.lock().take() else { return };
        while !handle.is_finished() {
            if Instant::now() >= deadline {
                return;
            }
            std::thread::sleep(Duration::from_millis(1));
        }
        let _ = handle.join();
    }

    pub fn cell_size(&self) -> (u32, u32) {
        let v = self.cell.load(Ordering::Relaxed);
        ((v >> 32) as u32, v as u32)
    }
}

pub struct PaneConfig {
    pub id: u64,
    pub cols: u16,
    pub rows: u16,
    /// Start with an empty terminal (local panes) instead of waiting for a snapshot.
    pub create_terminal: bool,
    pub emitter: Emitter,
}

/// Spawn a pane worker. Returns the shared handle; the thread ends on `close` or when every
/// sender is dropped.
pub fn spawn(config: PaneConfig, output: Box<dyn PaneOutput>) -> Arc<PaneShared> {
    let (tx, rx) = flume::unbounded();
    let shared = Arc::new(PaneShared {
        id: config.id,
        frame: Mutex::new(FrameBuf::default()),
        dirty_sent: AtomicBool::new(false),
        has_terminal: AtomicBool::new(false),
        paused: AtomicBool::new(false),
        cols: AtomicU64::new(u64::from(config.cols)),
        rows: AtomicU64::new(u64::from(config.rows)),
        cell: AtomicU64::new((8u64 << 32) | 16),
        tx,
        closing: AtomicBool::new(false),
        worker: Mutex::new(None),
    });
    let worker_shared = shared.clone();
    let handle = std::thread::Builder::new()
        .name(format!("rex-pane-{}", config.id))
        .spawn(move || {
            let emitter = config.emitter.clone();
            if let Err(error) = run(worker_shared, rx, config, output) {
                emitter.log("error", "pane", format!("pane worker ended: {error:#}"));
            }
        })
        .expect("spawn pane worker");
    *shared.worker.lock() = Some(handle);
    shared
}

const FRAME_INTERVAL: Duration = Duration::from_millis(7);
/// BELs closer together than this ring once: a program printing many (`cat` of a binary file)
/// must not flood the UI with one event per byte.
const BELL_INTERVAL: Duration = Duration::from_millis(100);

struct Worker {
    shared: Arc<PaneShared>,
    emitter: Emitter,
    output: Box<dyn PaneOutput>,
    terminal: Option<Terminal>,
    builder: FrameBuilder,
    back: FrameBuf,
    selection: Selection,
    sel_revision: u64,
    generation: u64,
    dirty: bool,
    last_publish: Instant,
    title: String,
    pwd: String,
    render_hold: bool,
    hold_since: Option<Instant>,
    /// A BEL arrived that the UI has not been told about yet.
    bell_pending: bool,
    last_bell: Option<Instant>,
    search: Option<Search>,
    /// Why the query cannot be searched for (`search` is `None`).
    search_error: Option<QueryError>,
    /// Applied to every terminal this pane gets (snapshots replace it).
    default_cursor: Option<CursorStyle>,
}

fn new_terminal(cols: u16, rows: u16) -> Result<Terminal> {
    let mut terminal = Terminal::new(ghostty_vt::TerminalOptions {
        cols: cols.max(1),
        rows: rows.max(1),
        max_scrollback: 10_000_000,
    })?;
    terminal.set_clipboard_write_max_bytes(Some(1 << 20))?;
    Ok(terminal)
}

fn run(
    shared: Arc<PaneShared>,
    rx: flume::Receiver<PaneMsg>,
    config: PaneConfig,
    output: Box<dyn PaneOutput>,
) -> Result<()> {
    let mut worker = Worker {
        shared: shared.clone(),
        emitter: config.emitter.clone(),
        output,
        terminal: None,
        builder: FrameBuilder::new()?,
        back: FrameBuf::default(),
        selection: Selection::new()?,
        sel_revision: 0,
        generation: 0,
        dirty: false,
        last_publish: Instant::now() - FRAME_INTERVAL,
        title: String::new(),
        pwd: String::new(),
        render_hold: false,
        hold_since: None,
        bell_pending: false,
        last_bell: None,
        search: None,
        search_error: None,
        default_cursor: None,
    };
    if config.create_terminal {
        worker.terminal = Some(new_terminal(config.cols, config.rows)?);
        shared.has_terminal.store(true, Ordering::Release);
        worker.dirty = true;
    }
    while !shared.closing() {
        // Wait for work; while dirty, wake in time for the next frame (or the autoscroll tick,
        // a held-back bell, or more searching).
        let mut timeout = if worker.dirty || worker.selection_autoscrolling() || worker.searching() {
            FRAME_INTERVAL.saturating_sub(worker.last_publish.elapsed())
        } else {
            Duration::from_millis(250)
        };
        if let Some(due) = worker.bell_due_in() {
            timeout = timeout.min(due);
        }
        match rx.recv_timeout(timeout.max(Duration::from_millis(1))) {
            Ok(PaneMsg::Close) | Err(flume::RecvTimeoutError::Disconnected) => break,
            Ok(msg) => {
                worker.handle(msg)?;
                // Drain whatever else is queued before rendering (batch bursts of output).
                let started = Instant::now();
                while !shared.closing()
                    && let Ok(msg) = rx.try_recv()
                {
                    if matches!(msg, PaneMsg::Close) {
                        return Ok(());
                    }
                    worker.handle(msg)?;
                    if started.elapsed() > Duration::from_millis(12) {
                        break;
                    }
                }
            }
            Err(flume::RecvTimeoutError::Timeout) => {}
        }
        if shared.closing() {
            break;
        }
        worker.search_step();
        worker.tick()?;
        worker.ring();
        if worker.dirty && worker.last_publish.elapsed() >= FRAME_INTERVAL {
            worker.publish()?;
        }
    }
    Ok(())
}

impl Worker {
    fn selection_autoscrolling(&self) -> bool {
        self.terminal.as_ref().is_some_and(|t| self.selection.autoscrolling(t))
    }

    /// A search has reading to do.
    fn searching(&self) -> bool {
        self.terminal.is_some() && self.search.as_ref().is_some_and(Search::busy)
    }

    /// The terminal's content changed: the search reads it again.
    fn content_changed(&mut self) {
        if let Some(search) = &mut self.search {
            search.touch();
        }
    }

    /// One bounded step of searching (between frames, so output and input keep flowing).
    fn search_step(&mut self) {
        let (Some(search), Some(terminal)) = (&mut self.search, &mut self.terminal) else { return };
        if !search.busy() {
            return;
        }
        match search.step(terminal) {
            Ok(changed) => self.dirty |= changed,
            Err(error) => self.search_failed(error),
        }
    }

    /// A search that cannot go on ends (its highlights with it); the pane and its terminal live on.
    fn search_failed(&mut self, error: anyhow::Error) {
        self.emitter.log("warn", "pane", format!("pane {}: find stopped: {error:#}", self.shared.id));
        self.search = None;
        self.dirty = true;
    }

    fn apply_default_cursor(&mut self) {
        if let (Some(style), Some(terminal)) = (self.default_cursor, &mut self.terminal)
            && let Err(error) = terminal.set_default_cursor_style(Some(style))
        {
            self.emitter.log("warn", "pane", format!("pane {}: default cursor not set: {error}", self.shared.id));
        }
    }

    fn handle(&mut self, msg: PaneMsg) -> Result<()> {
        match msg {
            PaneMsg::Bytes(bytes) => {
                if let Some(terminal) = &mut self.terminal {
                    terminal.vt_write(&bytes);
                    self.drain_events();
                    self.content_changed();
                    self.dirty = true;
                }
            }
            PaneMsg::Install(make) => {
                // A snapshot that fails to decode keeps the old terminal; the closure has told
                // its stream, which starts over with a fresh one.
                let terminal = match make() {
                    Ok(terminal) => terminal,
                    Err(error) => {
                        self.emitter.log("warn", "pane", format!("pane {}: snapshot not installed: {error:#}", self.shared.id));
                        return Ok(());
                    }
                };
                self.title = terminal.title().unwrap_or_default().to_owned();
                self.pwd = terminal.pwd().unwrap_or_default().to_owned();
                self.terminal = Some(terminal);
                self.selection = Selection::new()?;
                self.shared.has_terminal.store(true, Ordering::Release);
                self.apply_default_cursor();
                if let Some(search) = &mut self.search {
                    search.replace_terminal();
                }
                self.drain_events();
                self.dirty = true;
            }
            PaneMsg::Resize { cols, rows, cell_w, cell_h } => {
                self.shared.cols.store(u64::from(cols), Ordering::Relaxed);
                self.shared.rows.store(u64::from(rows), Ordering::Relaxed);
                self.shared.cell.store((u64::from(cell_w) << 32) | u64::from(cell_h), Ordering::Relaxed);
                if let Some(terminal) = &mut self.terminal
                    && cols > 0
                    && rows > 0
                {
                    terminal.resize(cols, rows, cell_w, cell_h)?;
                    self.drain_events();
                    self.content_changed();
                    self.dirty = true;
                }
            }
            PaneMsg::Scroll { op, value } => {
                if let Some(terminal) = &mut self.terminal {
                    let scroll = match op {
                        1 => ScrollViewport::Top,
                        2 => ScrollViewport::Bottom,
                        3 => ScrollViewport::DeltaPrompt(value as isize),
                        4 => ScrollViewport::Row(value.max(0) as usize),
                        _ => ScrollViewport::Delta(value as isize),
                    };
                    terminal.scroll_viewport(scroll);
                    self.selection.after_scroll(terminal)?;
                    self.dirty = true;
                }
            }
            PaneMsg::Select { op, clicks, x, y } => self.select(op, clicks, x, y)?,
            PaneMsg::SelectionText(reply) => {
                let text = match &self.terminal {
                    Some(terminal) => {
                        let mut buffer = Vec::new();
                        selection::text(terminal, &mut buffer).ok().flatten().map(|s| s.to_string())
                    }
                    None => None,
                };
                let _ = reply.send(text);
            }
            PaneMsg::HyperlinkAt { col, row, reply } => {
                let _ = reply.send(self.hyperlink_at(col, row));
            }
            PaneMsg::Paused(paused) => {
                self.shared.paused.store(paused, Ordering::Relaxed);
                self.dirty = true;
            }
            PaneMsg::Search(query) => {
                // A changed query (typing on, an option toggled) carries on from where the last
                // one was. Compiled here, off the caller's thread: a big pattern takes a while.
                let place = self.search.take().and_then(Search::into_place);
                self.search_error = None;
                match query.map(|(query, flags)| search::compile(&query, flags)) {
                    Some(Ok(regex)) => self.search = Some(Search::new(regex, place)),
                    Some(Err(error)) => self.search_error = Some(error),
                    None => {}
                }
                self.dirty = true;
            }
            PaneMsg::SearchStep(direction) => {
                if let (Some(search), Some(terminal)) = (&mut self.search, &mut self.terminal) {
                    match search.select(terminal, direction) {
                        Ok(()) => self.selection.after_scroll(terminal)?,
                        Err(error) => self.search_failed(error),
                    }
                    self.dirty = true;
                }
            }
            PaneMsg::DefaultCursor(style) => {
                self.default_cursor = Some(style);
                self.apply_default_cursor();
                self.dirty = true;
            }
            PaneMsg::With(f) => {
                if let Some(terminal) = &mut self.terminal {
                    f(terminal);
                    self.drain_events();
                    self.content_changed();
                    self.dirty = true;
                }
            }
            PaneMsg::Close => {}
        }
        Ok(())
    }

    fn select(&mut self, op: i32, clicks: i32, x: f32, y: f32) -> Result<()> {
        let Some(terminal) = &self.terminal else { return Ok(()) };
        let (cw, ch) = self.shared.cell_size();
        let (cw, ch) = (cw.max(1), ch.max(1));
        let cols = terminal.cols()?;
        let rows = terminal.rows()?;
        let col = (x / cw as f32).floor().clamp(0.0, f32::from(cols.saturating_sub(1))) as u16;
        let row = (y / ch as f32).floor().clamp(0.0, f32::from(rows.saturating_sub(1))) as u16;
        self.sel_revision += 1;
        let phase = match op {
            1 => Phase::Down(clicks.max(1) as u32),
            2 => Phase::Move,
            3 => Phase::Up,
            4 => Phase::All,
            _ => Phase::Clear,
        };
        let input = SelInput {
            generation: self.generation,
            revision: self.sel_revision,
            phase,
            at: (col, row),
            position: (f64::from(x), f64::from(y)),
            geometry: Geometry {
                columns: u32::from(cols),
                cell_width: cw,
                padding_left: 0,
                screen_height: u32::from(rows) * ch,
            },
        };
        self.selection.apply(terminal, input)?;
        self.dirty = true;
        Ok(())
    }

    fn hyperlink_at(&self, col: u16, row: u16) -> Option<String> {
        use ghostty_vt::terminal::{Point, PointCoordinate};
        let terminal = self.terminal.as_ref()?;
        let at = terminal.grid_ref(Point::Viewport(PointCoordinate { x: col, y: u32::from(row) })).ok()?;
        let mut buf = vec![0u8; 4096];
        let n = at.hyperlink_uri(&mut buf).ok()?;
        if n > 0 {
            return String::from_utf8(buf[..n].to_vec()).ok();
        }
        // Fall back to a plain-text URL under the pointer. The UI may hold the front frame
        // locked; never wait on it for long.
        let frame = self.shared.frame.try_lock_for(Duration::from_millis(20))?;
        let line = frame.row_text(usize::from(row));
        drop(frame);
        find_url_at(&line, usize::from(col))
    }

    fn tick(&mut self) -> Result<()> {
        if let Some(terminal) = &mut self.terminal
            && self.selection.tick(terminal)?
        {
            self.dirty = true;
        }
        // Synchronized output (mode 2026) holds rendering for at most a second.
        if self.render_hold && self.hold_since.is_some_and(|t| t.elapsed() > Duration::from_secs(1)) {
            self.render_hold = false;
            self.hold_since = None;
        }
        Ok(())
    }

    /// How long until a pending bell may ring (`None`: no bell pending).
    fn bell_due_in(&self) -> Option<Duration> {
        self.bell_pending
            .then(|| self.last_bell.map_or(Duration::ZERO, |rang| BELL_INTERVAL.saturating_sub(rang.elapsed())))
    }

    /// Tell the UI about pending BELs, at most once per `BELL_INTERVAL`.
    fn ring(&mut self) {
        if self.bell_due_in() == Some(Duration::ZERO) {
            self.bell_pending = false;
            self.last_bell = Some(Instant::now());
            self.emitter.signal(EventKind::Bell, self.shared.id);
        }
    }

    fn drain_events(&mut self) {
        let Some(terminal) = &mut self.terminal else { return };
        let mut title_changed = false;
        let events: Vec<ghostty_vt::Event> = terminal.events().collect();
        for event in events {
            match event {
                ghostty_vt::Event::PtyWrite(bytes) => {
                    if !self.shared.paused.load(Ordering::Relaxed) {
                        self.output.pty_write(bytes)
                    }
                }
                ghostty_vt::Event::Bell => self.bell_pending = true,
                ghostty_vt::Event::TitleChanged(title) => {
                    self.title = title;
                    title_changed = true;
                }
                ghostty_vt::Event::PwdChanged(pwd) => {
                    self.pwd = pwd;
                    title_changed = true;
                }
                ghostty_vt::Event::ClipboardWrite(write) => {
                    if let Some(content) = write.contents.iter().find(|c| c.mime.starts_with("text/plain")).or(write.contents.first())
                        && let Ok(text) = String::from_utf8(content.data.clone())
                    {
                        self.emitter.emit_value(EventKind::Clipboard, self.shared.id, &serde_json::json!({ "text": text }));
                    }
                }
                ghostty_vt::Event::DesktopNotification { title, body } => self.output.notification(&title, &body),
                ghostty_vt::Event::ProgressReport { state, progress } => {
                    self.output.progress(state as i32 as u8, progress);
                }
                ghostty_vt::Event::RenderHold(hold) => {
                    self.render_hold = hold;
                    self.hold_since = hold.then(Instant::now);
                }
            }
        }
        if title_changed {
            self.output.title_changed(&self.title, &self.pwd);
        }
    }

    fn publish(&mut self) -> Result<()> {
        if self.render_hold {
            return Ok(());
        }
        let Some(terminal) = &self.terminal else { return Ok(()) };
        self.generation += 1;
        self.builder.build(
            terminal,
            &mut self.back,
            FrameInputs {
                paused: self.shared.paused.load(Ordering::Relaxed),
                generation: self.generation,
                title: &self.title,
                pwd: &self.pwd,
                search: self.search.as_ref(),
                search_error: self.search_error,
            },
        )?;
        {
            let mut front = self.shared.frame.lock();
            std::mem::swap(&mut *front, &mut self.back);
        }
        self.dirty = false;
        self.last_publish = Instant::now();
        if !self.shared.dirty_sent.swap(true, Ordering::AcqRel) {
            self.emitter.signal(EventKind::PaneDirty, self.shared.id);
        }
        Ok(())
    }
}

/// A bare http(s)/ftp/mailto URL covering character column `col` of `line`.
pub fn find_url_at(line: &str, col: usize) -> Option<String> {
    let chars: Vec<char> = line.chars().collect();
    if col >= chars.len() || chars[col].is_whitespace() {
        return None;
    }
    let mut start = col;
    while start > 0 && !chars[start - 1].is_whitespace() && !"\"'<>()[]{}`".contains(chars[start - 1]) {
        start -= 1;
    }
    let mut end = col;
    while end + 1 < chars.len() && !chars[end + 1].is_whitespace() && !"\"'<>`".contains(chars[end + 1]) {
        end += 1;
    }
    let word: String = chars[start..=end].iter().collect();
    let word = word.trim_end_matches(['.', ',', ';', ':', ')', ']', '}', '!', '?']);
    ["https://", "http://", "ftp://", "mailto:"]
        .iter()
        .any(|p| word.starts_with(p))
        .then(|| word.to_owned())
}
