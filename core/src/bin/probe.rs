//! rexcore-probe: exercises the core through its C ABI, without the UI.
//!   rexcore-probe local                 feed sample VT output into a local pane and print the frame
//!   rexcore-probe remote <endpoint>     attach to a Rex server and run the end-to-end checks in a
//!                                       disposable session (windex-test-core-1), then kill it
//!   rexcore-probe model <endpoint>      connect and print the model summary only (read-only)
//!   rexcore-probe discover              list tailnet machines with their Rex/SSH reachability
//!   rexcore-probe transfer <endpoint>   uploads and downloads in a disposable session
//!                                       (windex-test-files-probe), checked by sha256 on both ends
use anyhow::{Context, Result, anyhow, bail, ensure};
use rexcore::ffi::{self, RexKey};
use rexcore::frame::{RexFrame, flags};
use serde_json::{Value, json};
use std::collections::HashMap;
use std::ffi::{CStr, CString, c_char, c_void};
use std::sync::mpsc;
use std::time::{Duration, Instant};

fn main() -> Result<()> {
    let mut args = std::env::args().skip(1);
    let mode = args.next().unwrap_or_else(|| "local".into());
    match mode.as_str() {
        "local" => local(),
        "remote" => remote(&args.next().context("usage: rexcore-probe remote <endpoint>")?),
        "model" => model_only(&args.next().context("usage: rexcore-probe model <endpoint>")?),
        "discover" => discover(),
        "transfer" => transfer(&args.next().context("usage: rexcore-probe transfer <endpoint>")?),
        other => bail!("unknown mode {other}"),
    }
}

fn local() -> Result<()> {
    let core: std::sync::Arc<rexcore::Core> = rexcore::Core::new(rexcore::Options::default(), rexcore::events::Emitter::none())?;
    let pane = core.local_pane_new(40, 6, false);
    core.local_pane_feed(pane, b"\x1b]0;probe title\x07hello \x1b[1;31mworld\x1b[0m\r\n\xe4\xb8\x96\xe7\x95\x8c wide \xf0\x9f\xa6\x80 crab\r\n\x1b[7minverse\x1b[0m done");
    std::thread::sleep(Duration::from_millis(100));
    let frame = core.frame_lock(pane).ok_or_else(|| anyhow!("no frame"))?;
    let meta = unsafe { &*frame };
    println!("cols={} rows={} cursor=({}, {}) gen={} total_rows={} title={:?}", meta.cols, meta.rows, meta.cursor_x, meta.cursor_y, meta.generation, meta.total_rows,
        unsafe { CStr::from_ptr(meta.title) });
    for line in frame_lines(meta) {
        println!("|{line}|");
    }
    let cells = unsafe { std::slice::from_raw_parts(meta.cells, meta.cols as usize * meta.rows as usize) };
    let w = &cells[6];
    println!("cell(6,0) fg={:06x} bg={:06x} flags={:#06x}", w.fg, w.bg, w.flags);
    core.frame_unlock(pane);
    core.shutdown();
    Ok(())
}

fn frame_lines(meta: &RexFrame) -> Vec<String> {
    let cells = unsafe { std::slice::from_raw_parts(meta.cells, meta.cols as usize * meta.rows as usize) };
    let text = unsafe { std::slice::from_raw_parts(meta.text, meta.text_len as usize) };
    (0..meta.rows as usize)
        .map(|row| {
            let mut line = String::new();
            for cell in &cells[row * meta.cols as usize..(row + 1) * meta.cols as usize] {
                if cell.flags & flags::SPACER != 0 {
                    continue;
                }
                if cell.text_len == 0 {
                    line.push(' ');
                    continue;
                }
                let start = cell.text_off as usize;
                line.push_str(&String::from_utf16_lossy(&text[start..start + usize::from(cell.text_len)]));
            }
            line.trim_end().to_owned()
        })
        .collect()
}

// ------------------------------------------------------------------ remote

struct Event {
    kind: i32,
    pane: u64,
    json: String,
}

unsafe extern "C" fn on_event(user: *mut c_void, kind: i32, pane: u64, json: *const c_char, len: usize) {
    let tx = unsafe { &*(user as *const mpsc::Sender<Event>) };
    let text = if json.is_null() || len == 0 {
        String::new()
    } else {
        String::from_utf8_lossy(unsafe { std::slice::from_raw_parts(json as *const u8, len) }).into_owned()
    };
    let _ = tx.send(Event { kind, pane, json: text });
}

/// A core driven through the C ABI, as the UI drives it.
struct Probe {
    core: *const rexcore::Core,
    rx: mpsc::Receiver<Event>,
    _tx: Box<mpsc::Sender<Event>>,
    model: Value,
    results: HashMap<u64, Value>,
    discovery: Option<Value>,
    /// LOG events (messages) seen so far.
    logs: Vec<String>,
    next_req: u64,
    started: Instant,
    /// The disposable session the checks created (killed at the end).
    test_session: Option<String>,
}

impl Probe {
    fn new() -> Result<Self> {
        Self::labelled("windex-probe")
    }

    /// A probe whose client shows as `label` on the server.
    fn labelled(label: &str) -> Result<Self> {
        let (tx, rx) = mpsc::channel();
        let tx = Box::new(tx);
        let options = CString::new(json!({"client_name": "Windex", "client_version": env!("CARGO_PKG_VERSION"), "label": label}).to_string())?;
        let core = unsafe { ffi::rex_core_new(options.as_ptr(), Some(on_event), &*tx as *const _ as *mut c_void) };
        ensure!(!core.is_null(), "rex_core_new failed");
        Ok(Self {
            core,
            rx,
            _tx: tx,
            model: json!({}),
            results: HashMap::new(),
            discovery: None,
            logs: Vec::new(),
            next_req: 0,
            started: Instant::now(),
            test_session: None,
        })
    }

    fn log(&self, what: impl AsRef<str>) {
        println!("[{:6.2}s] {}", self.started.elapsed().as_secs_f64(), what.as_ref());
    }

    /// Handle events until `timeout`, or until `done` says stop.
    fn pump(&mut self, timeout: Duration, mut done: impl FnMut(&mut Self) -> bool) -> bool {
        let deadline = Instant::now() + timeout;
        loop {
            if done(self) {
                return true;
            }
            let left = deadline.saturating_duration_since(Instant::now());
            if left.is_zero() {
                return false;
            }
            match self.rx.recv_timeout(left.min(Duration::from_millis(100))) {
                Ok(event) => self.handle(event),
                Err(mpsc::RecvTimeoutError::Timeout) => {}
                Err(mpsc::RecvTimeoutError::Disconnected) => return false,
            }
        }
    }

    fn handle(&mut self, event: Event) {
        match event.kind {
            1 => self.model = serde_json::from_str(&event.json).unwrap_or(json!({})),
            3 => {
                let value: Value = serde_json::from_str(&event.json).unwrap_or(json!({}));
                if let Some(req) = value["req"].as_u64() {
                    self.results.insert(req, value);
                }
            }
            4 => self.log(format!("NOTICE {}", event.json)),
            7 => self.log(format!("NOTIFY pane {} {}", event.pane, event.json)),
            8 => {
                let value: Value = serde_json::from_str(&event.json).unwrap_or(json!({}));
                let message = value["message"].as_str().unwrap_or("").to_owned();
                if value["level"] != "debug" {
                    self.log(format!("log {} {}: {message}", value["level"].as_str().unwrap_or(""), value["target"].as_str().unwrap_or("")));
                }
                self.logs.push(message);
            }
            9 => self.discovery = serde_json::from_str(&event.json).ok(),
            _ => {}
        }
    }

    fn command(&mut self, cmd: &str, args: Value) -> Result<Value> {
        let req = self.send(cmd, args)?;
        let result = self.result(req, Duration::from_secs(30)).with_context(|| format!("{cmd}: no RESULT within 30 s"))?;
        if result["ok"] != true {
            bail!("{cmd} failed: {}", result["error"]);
        }
        Ok(result["data"].clone())
    }

    /// Queue a command; its RESULT is read with `result`.
    fn send(&mut self, cmd: &str, mut args: Value) -> Result<u64> {
        self.next_req += 1;
        let req = self.next_req;
        args["cmd"] = json!(cmd);
        args["req"] = json!(req);
        let text = CString::new(args.to_string())?;
        let queued = unsafe { ffi::rex_core_command(self.core, text.as_ptr()) };
        ensure!(queued == 0, "{cmd}: rex_core_command returned {queued}");
        Ok(req)
    }

    /// The whole RESULT of a command (`ok`, `data` / `error`).
    fn result(&mut self, req: u64, timeout: Duration) -> Result<Value> {
        ensure!(self.pump(timeout, |p| p.results.contains_key(&req)), "no RESULT for request {req}");
        Ok(self.results.remove(&req).unwrap_or_default())
    }

    fn wait_model(&mut self, what: &str, timeout: Duration, mut test: impl FnMut(&Value) -> bool) -> Result<Value> {
        ensure!(self.pump(timeout, |p| test(&p.model)), "timed out waiting for {what}");
        Ok(self.model.clone())
    }

    /// (lines, cols, rows, default bg, paused)
    fn frame_text(&self, pane: u64) -> Option<(Vec<String>, u16, u16, u32, bool)> {
        let frame = unsafe { ffi::rex_pane_frame_lock(self.core, pane) };
        if frame.is_null() {
            return None;
        }
        let meta = unsafe { &*frame };
        let out = (frame_lines(meta), meta.cols, meta.rows, meta.bg, meta.paused != 0);
        unsafe { ffi::rex_pane_frame_unlock(self.core, pane) };
        Some(out)
    }

    fn wait_frame(&mut self, pane: u64, what: &str, timeout: Duration, test: impl Fn(&[String]) -> bool) -> Result<Vec<String>> {
        let mut last = Vec::new();
        let ok = self.pump(timeout, |p| match p.frame_text(pane) {
            Some((lines, ..)) => {
                let hit = test(&lines);
                last = lines;
                hit
            }
            None => false,
        });
        if !ok {
            print_screen(&last);
            bail!("timed out waiting for {what} in pane {pane}");
        }
        Ok(last)
    }

    fn key(&self, pane: u64, code: &str, key: &str, text: Option<&str>, mods: u32) -> Result<()> {
        let code = CString::new(code)?;
        let key = CString::new(key)?;
        let text = text.map(CString::new).transpose()?;
        for action in [1u32, 0] {
            let event = RexKey {
                code: code.as_ptr(),
                key: key.as_ptr(),
                text: text.as_ref().map_or(std::ptr::null(), |t| t.as_ptr()),
                mods,
                action,
            };
            let rc = unsafe { ffi::rex_pane_key(self.core, pane, &event) };
            ensure!(rc == 0, "rex_pane_key returned {rc}");
        }
        Ok(())
    }

    /// Type ASCII text one key at a time, as a US keyboard would.
    fn type_keys(&self, pane: u64, text: &str) -> Result<()> {
        for c in text.chars() {
            let (code, shift) = us_key(c).with_context(|| format!("no key for {c:?}"))?;
            let s = c.to_string();
            self.key(pane, &code, &s, Some(&s), if shift { 1 } else { 0 })?;
        }
        Ok(())
    }

    fn enter(&self, pane: u64) -> Result<()> {
        self.key(pane, "Enter", "Enter", Some("\r"), 0)
    }

    fn paste(&self, pane: u64, text: &str) -> Result<()> {
        let text = CString::new(text)?;
        let rc = unsafe { ffi::rex_pane_paste(self.core, pane, text.as_ptr()) };
        ensure!(rc == 0, "rex_pane_paste returned {rc}");
        Ok(())
    }

    fn mouse(&self, pane: u64, action: u32, button: u32, x: f32, y: f32, wheel_dy: f32) -> Result<()> {
        let event = ffi::RexMouse { action, button, mods: 0, x, y, wheel_dy };
        let rc = unsafe { ffi::rex_pane_mouse(self.core, pane, &event) };
        ensure!(rc == 0, "rex_pane_mouse returned {rc}");
        Ok(())
    }

    fn focus(&self, pane: u64, focused: bool) -> Result<()> {
        let rc = unsafe { ffi::rex_pane_focus(self.core, pane, i32::from(focused)) };
        ensure!(rc == 0, "rex_pane_focus returned {rc}");
        Ok(())
    }

    fn resize(&self, pane: u64, cols: u16, rows: u16) -> Result<()> {
        let rc = unsafe { ffi::rex_pane_resize(self.core, pane, cols, rows, 9, 19) };
        ensure!(rc == 0, "rex_pane_resize returned {rc}");
        Ok(())
    }
}

impl Drop for Probe {
    fn drop(&mut self) {
        unsafe { ffi::rex_core_free(self.core) };
    }
}

fn us_key(c: char) -> Option<(String, bool)> {
    Some(match c {
        'a'..='z' => (format!("Key{}", c.to_ascii_uppercase()), false),
        'A'..='Z' => (format!("Key{c}"), true),
        '0'..='9' => (format!("Digit{c}"), false),
        ' ' => ("Space".into(), false),
        '-' => ("Minus".into(), false),
        '_' => ("Minus".into(), true),
        '.' => ("Period".into(), false),
        '/' => ("Slash".into(), false),
        '=' => ("Equal".into(), false),
        '\'' => ("Quote".into(), false),
        '"' => ("Quote".into(), true),
        ';' => ("Semicolon".into(), false),
        _ => return None,
    })
}

fn print_screen(lines: &[String]) {
    let end = lines.iter().rposition(|l| !l.is_empty()).map_or(0, |i| i + 1);
    for line in &lines[..end] {
        println!("    |{line}");
    }
}

/// hosts â†’ sessions â†’ windows â†’ panes, with each window's layout.
fn print_model(model: &Value) {
    println!("model revision {} current {}", model["revision"], model["current"]);
    for host in model["hosts"].as_array().into_iter().flatten() {
        println!(
            "host {} {:?} {} [{}] server {} error {}",
            host["id"], host["name"].as_str().unwrap_or(""), host["endpoint"], host["state"].as_str().unwrap_or(""), host["server"], host["error"]
        );
        for session in host["sessions"].as_array().into_iter().flatten() {
            println!(
                "  session {} {:?} attached={} word={} active={}",
                session["id"], session["label"].as_str().unwrap_or(""), session["attached"], session["word"], session["active_window"]
            );
            for window in session["windows"].as_array().into_iter().flatten() {
                println!(
                    "    tab #{} {} label={:?} title={:?} focused={} zoomed={} word={}",
                    window["index"], window["id"], window["label"].as_str().unwrap_or(""), window["title"].as_str().unwrap_or(""), window["focused_pane"], window["zoomed_pane"], window["word"]
                );
                println!("      layout {}", layout_text(&window["layout"]));
                for pane in window["panes"].as_array().into_iter().flatten() {
                    println!(
                        "      pane {} title={:?} program={} icon={} cwd={} owned={} paused={} size={}x{} status={}",
                        pane["pane"], pane["title"].as_str().unwrap_or(""), pane["program"], pane["icon"], pane["cwd"], pane["owned"], pane["paused"],
                        pane["server_cols"], pane["server_rows"], pane["status"]
                    );
                }
            }
        }
    }
}

fn layout_text(node: &Value) -> String {
    match node["kind"].as_str() {
        Some("pane") => format!("#{}", node["pane"]),
        Some("split") => {
            let children: Vec<String> = node["children"].as_array().into_iter().flatten().map(|c| format!("{:.2}:{}", c["weight"].as_f64().unwrap_or(0.0), layout_text(&c["node"]))).collect();
            format!("{}({} {})", node["axis"].as_str().unwrap_or("?"), node["id"].as_str().unwrap_or("?"), children.join(" | "))
        }
        _ => "none".into(),
    }
}

fn host_of<'a>(model: &'a Value, id: &str) -> Option<&'a Value> {
    model["hosts"].as_array()?.iter().find(|h| h["id"] == id)
}

fn session_of<'a>(model: &'a Value, host: &str, session: &str) -> Option<&'a Value> {
    host_of(model, host)?["sessions"].as_array()?.iter().find(|s| s["id"] == session)
}

fn active_window<'a>(model: &'a Value, host: &str, session: &str) -> Option<&'a Value> {
    let s = session_of(model, host, session)?;
    s["windows"].as_array()?.iter().find(|w| w["id"] == s["active_window"])
}

fn pane_of<'a>(model: &'a Value, host: &str, session: &str, pane: u64) -> Option<&'a Value> {
    session_of(model, host, session)?["windows"].as_array()?.iter().flat_map(|w| w["panes"].as_array().into_iter().flatten()).find(|p| p["pane"] == pane)
}

const HOST: &str = "h1";
const LABEL: &str = "windex-test-core-1";

fn connect(probe: &mut Probe, endpoint: &str) -> Result<()> {
    probe.command("add_host", json!({"id": HOST, "name": "probe", "endpoint": endpoint}))?;
    probe.log(format!("add_host {endpoint}: waiting for connected"));
    probe.wait_model("host connected", Duration::from_secs(60), |m| host_of(m, HOST).is_some_and(|h| h["state"] == "connected"))?;
    // Give sidebar views a moment to arrive.
    probe.pump(Duration::from_secs(2), |_| false);
    Ok(())
}

fn model_only(endpoint: &str) -> Result<()> {
    let mut probe = Probe::new()?;
    connect(&mut probe, endpoint)?;
    print_model(&probe.model);
    Ok(())
}

fn discover() -> Result<()> {
    let mut probe = Probe::new()?;
    print_discovery(&mut probe)
}

fn print_discovery(probe: &mut Probe) -> Result<()> {
    probe.discovery = None;
    let data = probe.command("discover", json!({}))?;
    ensure!(probe.pump(Duration::from_secs(5), |p| p.discovery.is_some()), "no DISCOVERY event");
    probe.log(format!("discover: {data}"));
    for m in probe.discovery.as_ref().and_then(|d| d["machines"].as_array()).into_iter().flatten() {
        println!(
            "  {:<20} {:<32} {:<8} online={:<5} self={:<5} rex={} tailscale_ssh={}",
            m["name"].as_str().unwrap_or(""), m["dns"].as_str().unwrap_or("-"), m["os"].as_str().unwrap_or(""), m["online"], m["self"], m["rex"], m["ssh"]["tailscale_ssh"]
        );
    }
    Ok(())
}

/// The ssh.exe processes this process started (its forwards).
fn own_ssh() -> Result<Vec<u32>> {
    let script = format!("Get-CimInstance Win32_Process -Filter \"ParentProcessId={} AND Name='ssh.exe'\" | ForEach-Object {{ $_.ProcessId }}", std::process::id());
    let out = std::process::Command::new("powershell").args(["-NoProfile", "-NonInteractive", "-Command", &script]).output()?;
    Ok(String::from_utf8_lossy(&out.stdout).split_whitespace().filter_map(|pid| pid.parse().ok()).collect())
}

/// Kill forwards this process started, to force a reconnect.
fn kill_ssh(pids: &[u32]) -> Result<()> {
    for pid in pids {
        std::process::Command::new("taskkill").args(["/F", "/PID", &pid.to_string()]).output()?;
    }
    Ok(())
}

fn remote(endpoint: &str) -> Result<()> {
    let mut probe = Probe::new()?;
    println!("--- (h) unreachable host");
    unreachable_host(&mut probe)?;
    connect(&mut probe, endpoint)?;
    println!("--- (a) model summary");
    print_model(&probe.model);

    // Leftovers of an interrupted run are ours by name; anything else is never touched.
    let leftovers: Vec<String> = host_of(&probe.model, HOST)
        .and_then(|h| h["sessions"].as_array())
        .into_iter()
        .flatten()
        .filter(|s| s["label"].as_str().is_some_and(|l| l.starts_with("windex-test-core-")))
        .filter_map(|s| s["id"].as_str().map(str::to_owned))
        .collect();
    for id in leftovers {
        probe.log(format!("killing leftover test session {id}"));
        probe.command("kill_session", json!({"host": HOST, "session": id}))?;
    }

    let outcome = checks(&mut probe, endpoint);
    if let Some(session) = probe.test_session.clone().or_else(|| session_by_label(&probe.model)) {
        // Unfocused first: the session that becomes current next is not ours to claim.
        probe.command("app_focus", json!({"focused": false}))?;
        probe.log(format!("(g) kill_session {session}"));
        probe.command("kill_session", json!({"host": HOST, "session": session}))?;
        probe.wait_model("session gone", Duration::from_secs(15), |m| session_of(m, HOST, &session).is_none())?;
        probe.log("(g) session killed");
    }
    outcome?;
    print_discovery(&mut probe)?;
    probe.command("remove_host", json!({"id": HOST}))?;
    probe.log("all checks passed");
    Ok(())
}

fn session_by_label(model: &Value) -> Option<String> {
    host_of(model, HOST)?["sessions"].as_array()?.iter().find(|s| s["label"] == LABEL).and_then(|s| s["id"].as_str()).map(str::to_owned)
}

fn checks(probe: &mut Probe, endpoint: &str) -> Result<()> {
    println!("--- (b) new session");
    let created = probe.command("new_session", json!({"host": HOST, "label": LABEL}))?;
    let session = created["session"].as_str().context("no session id")?.to_owned();
    probe.test_session = Some(session.clone());
    probe.log(format!("new_session -> {session}"));
    probe.command("open_session", json!({"host": HOST, "session": session}))?;
    let model = probe.wait_model("the session's pane", Duration::from_secs(20), |m| {
        m["current"]["session"] == session.as_str() && active_window(m, HOST, &session).is_some_and(|w| w["panes"].as_array().is_some_and(|p| !p.is_empty()))
    })?;
    let window = active_window(&model, HOST, &session).context("window")?.clone();
    let window_id = window["id"].as_str().context("window id")?.to_owned();
    let pane = window["panes"][0]["pane"].as_u64().context("pane")?;
    let screen = probe.wait_frame(pane, "a shell prompt", Duration::from_secs(20), |lines| lines.iter().any(|l| !l.trim().is_empty()))?;
    probe.log(format!("pane {pane} first frame:"));
    print_screen(&screen);

    probe.command("app_focus", json!({"focused": true}))?;
    probe.resize(pane, 100, 30)?;
    probe.wait_model("server size 100x30", Duration::from_secs(10), |m| {
        pane_of(m, HOST, &session, pane).is_some_and(|p| p["server_cols"] == 100 && p["server_rows"] == 30 && p["owned"] == true)
    })?;
    ensure!(probe.pump(Duration::from_secs(5), |p| p.frame_text(pane).is_some_and(|f| (f.1, f.2) == (100, 30))), "the frame never became 100x30");
    probe.log("resize: owned, server size 100x30, frame 100x30");

    println!("--- (c) keys and paste");
    probe.type_keys(pane, "echo hello-windex")?;
    probe.enter(pane)?;
    let screen = probe.wait_frame(pane, "hello-windex output", Duration::from_secs(10), |lines| lines.iter().any(|l| l.trim() == "hello-windex"))?;
    print_screen(&screen);
    probe.paste(pane, "echo pasted-$((6*7))")?;
    probe.enter(pane)?;
    probe.wait_frame(pane, "paste output", Duration::from_secs(10), |lines| lines.iter().any(|l| l.trim() == "pasted-42"))?;
    probe.log("typed keys and a paste reached the shell");
    let started = Instant::now();
    probe.paste(pane, "seq 1 200000; echo seq-done")?;
    probe.enter(pane)?;
    probe.wait_frame(pane, "the end of a 1.3 MB burst", Duration::from_secs(60), |lines| lines.iter().any(|l| l.trim() == "seq-done"))?;
    probe.log(format!("throughput: `seq 1 200000` (1.3 MB) rendered in {:.2}s", started.elapsed().as_secs_f64()));
    // More than one control message holds (1 MiB on dasher) still arrives whole and in order.
    probe.paste(pane, "wc -c")?;
    probe.enter(pane)?;
    probe.pump(Duration::from_millis(500), |_| false);
    let big = format!("{}\n", "x".repeat(99)).repeat(16 * 1024);
    probe.paste(pane, &big)?;
    probe.key(pane, "KeyD", "d", Some("\u{4}"), 2)?; // ctrl+d: end of wc's input
    let count = big.len().to_string();
    probe.wait_frame(pane, "wc's count of a large paste", Duration::from_secs(60), |lines| lines.iter().any(|l| l.trim() == count))?;
    probe.log(format!("a {count}-byte paste reached the program whole (wc -c)"));

    println!("--- (c2) mouse, wheel, focus, ctrl+c");
    probe.paste(pane, r"printf '\e[?1000h\e[?1006h\e[?1004h'; cat -v")?;
    probe.enter(pane)?;
    probe.pump(Duration::from_millis(700), |_| false);
    probe.focus(pane, false)?;
    probe.focus(pane, true)?;
    probe.mouse(pane, 1, 2, 1.0, 1.0, 0.0)?; // right press at cell (0,0)
    probe.mouse(pane, 2, 2, 1.0, 1.0, 0.0)?;
    probe.mouse(pane, 4, 0, 9.0 * 4.0 + 1.0, 19.0 * 2.0 + 1.0, 1.0)?; // one notch up at cell (4,2)
    let screen = probe.wait_frame(pane, "mouse/focus reports", Duration::from_secs(10), |lines| {
        let all = lines.join("\n");
        all.contains("^[[O^[[I") && all.contains("^[[<2;1;1M^[[<2;1;1m") && all.contains("^[[<64;5;3M")
    })?;
    print_screen(&screen);
    probe.key(pane, "KeyC", "c", Some("\u{3}"), 2)?; // ctrl+c ends cat
    probe.wait_frame(pane, "^C", Duration::from_secs(10), |lines| lines.iter().any(|l| l.ends_with("^C")))?;
    // The tty flushes typeahead on SIGINT: let the shell come back first.
    probe.pump(Duration::from_millis(800), |_| false);
    probe.paste(pane, r"printf '\e[?1000l\e[?1006l\e[?1004l'; echo modes-off")?;
    probe.enter(pane)?;
    probe.wait_frame(pane, "ctrl+c and a prompt", Duration::from_secs(10), |lines| lines.iter().any(|l| l.trim() == "modes-off"))?;
    probe.log("mouse press/release (right), wheel, focus in/out and ctrl+c reached the program");

    println!("--- (d) layout commands");
    let split = probe.command("split", json!({"pane": pane, "direction": "right"}))?;
    let pane2 = split["pane"].as_u64().context("split pane")?;
    let model = probe.wait_model("a row split", Duration::from_secs(10), |m| {
        active_window(m, HOST, &session).is_some_and(|w| w["layout"]["kind"] == "split" && w["layout"]["axis"] == "row" && w["panes"].as_array().is_some_and(|p| p.len() == 2))
    })?;
    let layout = active_window(&model, HOST, &session).context("window")?["layout"].clone();
    probe.log(format!("split right -> pane {pane2}; layout {}", layout_text(&layout)));
    probe.wait_frame(pane2, "the new pane's prompt", Duration::from_secs(20), |lines| lines.iter().any(|l| !l.trim().is_empty()))?;
    probe.wait_frame(pane, "the first pane", Duration::from_secs(5), |lines| lines.iter().any(|l| !l.trim().is_empty()))?;
    probe.log("both panes render frames");

    let split_id = layout["id"].as_str().context("split id")?.to_owned();
    probe.command("set_split_ratio", json!({"host": HOST, "session": session, "split": split_id, "ratio": 0.3}))?;
    probe.wait_model("ratio 0.3", Duration::from_secs(10), |m| {
        active_window(m, HOST, &session).is_some_and(|w| (w["layout"]["ratio"].as_f64().unwrap_or(0.0) - 0.3).abs() < 0.01)
    })?;
    probe.log("set_split_ratio 0.3 applied");

    probe.command("zoom_pane", json!({"pane": pane, "zoom": true}))?;
    probe.wait_model("zoomed", Duration::from_secs(10), |m| active_window(m, HOST, &session).is_some_and(|w| w["zoomed_pane"] == pane))?;
    probe.command("zoom_pane", json!({"pane": pane, "zoom": false}))?;
    probe.wait_model("unzoomed", Duration::from_secs(10), |m| active_window(m, HOST, &session).is_some_and(|w| w["zoomed_pane"].is_null()))?;
    probe.log("zoom on/off applied");

    probe.command("rename_tab", json!({"host": HOST, "session": session, "window": window_id, "label": "probe-tab"}))?;
    probe.wait_model("tab label", Duration::from_secs(10), |m| active_window(m, HOST, &session).is_some_and(|w| w["label"] == "probe-tab" && w["title"] == "probe-tab"))?;
    probe.log("rename_tab applied");

    let tab = probe.command("new_tab", json!({"host": HOST, "session": session}))?;
    let tab_id = tab["window"].as_str().context("new tab id")?.to_owned();
    probe.wait_model("the new tab active", Duration::from_secs(10), |m| session_of(m, HOST, &session).is_some_and(|s| s["active_window"] == tab_id.as_str() && s["windows"].as_array().is_some_and(|w| w.len() == 2)))?;
    probe.command("select_tab", json!({"host": HOST, "session": session, "window": window_id}))?;
    probe.wait_model("the first tab active", Duration::from_secs(10), |m| session_of(m, HOST, &session).is_some_and(|s| s["active_window"] == window_id.as_str()))?;
    probe.log("new_tab + select_tab applied");

    probe.command("close_pane", json!({"pane": pane2}))?;
    probe.wait_model("one pane", Duration::from_secs(10), |m| active_window(m, HOST, &session).is_some_and(|w| w["panes"].as_array().is_some_and(|p| p.len() == 1)))?;
    probe.log("close_pane applied");

    probe.resize(pane, 90, 25)?;
    probe.wait_model("server size 90x25", Duration::from_secs(10), |m| pane_of(m, HOST, &session, pane).is_some_and(|p| p["server_cols"] == 90 && p["server_rows"] == 25))?;
    probe.log("rex_pane_resize 90x25 reached the server");

    println!("--- (d2) more commands");
    more_commands(probe, endpoint, &session, &window_id, &tab_id, pane)?;
    print_model(&probe.model);

    println!("--- (e) reconnect");
    let forwards = own_ssh()?;
    kill_ssh(&forwards)?;
    probe.log(format!("killed own ssh.exe: {forwards:?}"));
    probe.wait_model("host not connected", Duration::from_secs(15), |m| host_of(m, HOST).is_some_and(|h| h["state"] != "connected"))?;
    let state = host_of(&probe.model, HOST).map(|h| h["state"].clone()).unwrap_or_default();
    probe.log(format!("host state {state}"));
    ensure!(probe.frame_text(pane).is_some_and(|f| f.4), "frame should be paused while disconnected");
    probe.wait_model("reconnected", Duration::from_secs(60), |m| host_of(m, HOST).is_some_and(|h| h["state"] == "connected"))?;
    ensure!(probe.pump(Duration::from_secs(20), |p| p.frame_text(pane).is_some_and(|f| !f.4)), "pane still paused after reconnect");
    probe.log("reconnected; pane resumed");
    probe.type_keys(pane, "echo after-reconnect")?;
    probe.enter(pane)?;
    let screen = probe.wait_frame(pane, "output after reconnect", Duration::from_secs(10), |lines| lines.iter().any(|l| l.trim() == "after-reconnect"))?;
    print_screen(&screen);
    probe.log("frames update after reconnect");

    println!("--- (e2) disconnect_host / connect_host");
    probe.command("disconnect_host", json!({"id": HOST}))?;
    probe.wait_model("host offline", Duration::from_secs(10), |m| host_of(m, HOST).is_some_and(|h| h["state"] == "offline"))?;
    ensure!(probe.pump(Duration::from_secs(5), |p| p.frame_text(pane).is_some_and(|f| f.4)), "pane not paused while offline");
    let rc = {
        let code = CString::new("KeyA")?;
        let event = RexKey { code: code.as_ptr(), key: std::ptr::null(), text: std::ptr::null(), mods: 0, action: 1 };
        unsafe { ffi::rex_pane_key(probe.core, pane, &event) }
    };
    ensure!(rc == 1, "input while offline should return 1 (paused), got {rc}");
    probe.log("offline: pane paused, rex_pane_key returns 1");
    probe.command("connect_host", json!({"id": HOST}))?;
    probe.wait_model("host connected again", Duration::from_secs(60), |m| host_of(m, HOST).is_some_and(|h| h["state"] == "connected"))?;
    ensure!(probe.pump(Duration::from_secs(20), |p| p.frame_text(pane).is_some_and(|f| !f.4)), "pane still paused after connect_host");
    probe.type_keys(pane, "echo back-online")?;
    probe.enter(pane)?;
    probe.wait_frame(pane, "output after connect_host", Duration::from_secs(10), |lines| lines.iter().any(|l| l.trim() == "back-online"))?;
    probe.log("connect_host: a new client, fresh snapshot, typing works");

    println!("--- (e3) another client's claim survives our reconnect");
    another_client_keeps_its_claim(probe, endpoint, &session, pane)?;

    println!("--- (e4) update_host: a new endpoint starts over");
    if let Some(destination) = endpoint.strip_prefix("ssh://").filter(|d| !d.contains('/')) {
        let status = probe.command("server_status", json!({"host": HOST}))?;
        let socket = status["listeners"]
            .as_array()
            .into_iter()
            .flatten()
            .filter(|l| l["primary"] == true)
            .find_map(|l| l["endpoint"].as_str()?.strip_prefix("unix://"))
            .context("server_status lists no primary unix listener")?;
        let explicit = format!("ssh://{destination}{socket}");
        probe.command("update_host", json!({"id": HOST, "endpoint": explicit}))?;
        probe.wait_model("host not connected", Duration::from_secs(15), |m| host_of(m, HOST).is_some_and(|h| h["state"] != "connected"))?;
        probe.wait_model("connected at the new endpoint", Duration::from_secs(60), |m| {
            host_of(m, HOST).is_some_and(|h| h["state"] == "connected" && h["endpoint"] == explicit.as_str())
        })?;
        ensure!(probe.pump(Duration::from_secs(20), |p| p.frame_text(pane).is_some_and(|f| !f.4)), "pane still paused after update_host");
        // Claims belong to the old endpoint: the visible pane is claimed again while focused.
        probe.wait_model("the pane claimed again", Duration::from_secs(10), |m| pane_of(m, HOST, &session, pane).is_some_and(|p| p["owned"] == true))?;
        probe.type_keys(pane, "echo new-endpoint")?;
        probe.enter(pane)?;
        probe.wait_frame(pane, "output at the new endpoint", Duration::from_secs(10), |lines| lines.iter().any(|l| l.trim() == "new-endpoint"))?;
        probe.log(format!("update_host {explicit}: new client, pane claimed again, the first keys typed arrive"));
    }
    Ok(())
}

/// A second client takes the pane while we stay focused; our connection drops and comes back.
/// The pane stays the other client's (no claim without a focus gain or input), until we type.
fn another_client_keeps_its_claim(probe: &mut Probe, endpoint: &str, session: &str, pane: u64) -> Result<()> {
    let ours = own_ssh()?;
    let mut other = Probe::labelled("windex-probe-other")?;
    connect(&mut other, endpoint)?;
    other.command("open_session", json!({"host": HOST, "session": session}))?;
    let model = other.wait_model("the other client's pane", Duration::from_secs(20), |m| {
        active_window(m, HOST, session).is_some_and(|w| w["panes"].as_array().is_some_and(|p| !p.is_empty()))
    })?;
    let other_pane = active_window(&model, HOST, session).and_then(|w| w["panes"][0]["pane"].as_u64()).context("pane")?;
    other.command("app_focus", json!({"focused": true}))?;
    other.resize(other_pane, 70, 20)?;
    let size = |m: &Value, pane: u64| pane_of(m, HOST, session, pane).map(|p| (p["owned"].clone(), p["server_cols"].clone(), p["server_rows"].clone()));
    let theirs = Some((json!(true), json!(70), json!(20)));
    let not_ours = Some((json!(false), json!(70), json!(20)));
    other.wait_model("the other client owns 70x20", Duration::from_secs(15), |m| size(m, other_pane) == theirs)?;
    probe.wait_model("we see the other client's claim", Duration::from_secs(15), |m| size(m, pane) == not_ours)?;
    probe.log("another client took the pane (70x20)");

    kill_ssh(&ours)?;
    probe.wait_model("host not connected", Duration::from_secs(15), |m| host_of(m, HOST).is_some_and(|h| h["state"] != "connected"))?;
    probe.wait_model("reconnected", Duration::from_secs(60), |m| host_of(m, HOST).is_some_and(|h| h["state"] == "connected"))?;
    probe.pump(Duration::from_secs(4), |_| false);
    other.pump(Duration::from_secs(1), |_| false);
    ensure!(size(&probe.model, pane) == not_ours, "our reconnect took the pane back: {:?}", size(&probe.model, pane));
    ensure!(size(&other.model, other_pane) == theirs, "the other client lost the pane: {:?}", size(&other.model, other_pane));
    probe.log("after our reconnect the pane is still the other client's, at its size");
    other.command("app_focus", json!({"focused": false}))?;
    drop(other);

    // Input here claims it back, at our size (90x25 from (d)).
    probe.type_keys(pane, "echo mine-again")?;
    probe.enter(pane)?;
    probe.wait_model("ours again at 90x25", Duration::from_secs(10), |m| size(m, pane) == Some((json!(true), json!(90), json!(25))))?;
    probe.wait_frame(pane, "output after the claim", Duration::from_secs(10), |lines| lines.iter().any(|l| l.trim() == "mine-again"))?;
    probe.log("typing claimed the pane back at our size");
    Ok(())
}

/// A host that can't be reached ends in `error` with ssh's reason, without retrying forever.
fn unreachable_host(probe: &mut Probe) -> Result<()> {
    probe.command("add_host", json!({"id": "bad", "name": "nowhere", "endpoint": "ssh://nobody@windex-no-such-host.invalid"}))?;
    // Connect again while the first attempt runs: that attempt is the one asked for.
    probe.command("connect_host", json!({"id": "bad"}))?;
    let model = probe.wait_model("error state", Duration::from_secs(60), |m| host_of(m, "bad").is_some_and(|h| h["state"] == "error"))?;
    let attempts = probe.logs.iter().filter(|m| m.starts_with("bad: ")).count();
    ensure!(attempts == 3, "expected 3 failed attempts, saw {attempts}: {:?}", probe.logs);
    probe.log(format!("unreachable host -> error after 3 attempts: {}", host_of(&model, "bad").map(|h| h["error"].clone()).unwrap_or_default()));
    probe.command("remove_host", json!({"id": "bad"}))?;
    Ok(())
}

/// The rest of the contract's commands, each checked against the model or the screen.
fn more_commands(probe: &mut Probe, endpoint: &str, session: &str, window_id: &str, tab_id: &str, pane: u64) -> Result<()> {
    probe.command("rename_session", json!({"host": HOST, "session": session, "label": "windex-test-core-1b"}))?;
    probe.wait_model("session label", Duration::from_secs(10), |m| session_of(m, HOST, session).is_some_and(|s| s["label"] == "windex-test-core-1b"))?;
    probe.command("rename_session", json!({"host": HOST, "session": session, "label": LABEL}))?;
    probe.log("rename_session applied");

    probe.command("rename_pane", json!({"pane": pane, "label": "probe-pane"}))?;
    probe.wait_model("pane label", Duration::from_secs(10), |m| pane_of(m, HOST, session, pane).is_some_and(|p| p["title"] == "probe-pane"))?;
    probe.command("rename_pane", json!({"pane": pane, "label": "shell"}))?;
    probe.log("rename_pane applied");

    probe.command("send_text", json!({"pane": pane, "text": "echo sent-$((40+2))\n"}))?;
    probe.wait_frame(pane, "send_text output", Duration::from_secs(10), |lines| lines.iter().any(|l| l.trim() == "sent-42"))?;
    probe.log("send_text reached the shell");

    let listing = probe.command("list_dir", json!({"pane": pane, "path": "/"}))?;
    let usr = listing["entries"].as_array().into_iter().flatten().any(|e| e["name"] == "usr" && e["dir"] == true);
    ensure!(usr, "list_dir / has no usr directory: {listing}");
    let status = probe.command("server_status", json!({"host": HOST}))?;
    ensure!(status["pid"].is_number(), "server_status: {status}");
    probe.log(format!("list_dir / -> {} entries; server_status pid {}", listing["entries"].as_array().map_or(0, Vec::len), status["pid"]));

    // A column split to steer: focus, swap, ratio and balance.
    let below = probe.command("split", json!({"pane": pane, "direction": "down"}))?["pane"].as_u64().context("split pane")?;
    probe.wait_model("a column split", Duration::from_secs(10), |m| {
        active_window(m, HOST, session).is_some_and(|w| w["layout"]["axis"] == "column" && w["focused_pane"] == below)
    })?;
    probe.command("focus_direction", json!({"pane": below, "direction": "up"}))?;
    probe.wait_model("focus up", Duration::from_secs(10), |m| active_window(m, HOST, session).is_some_and(|w| w["focused_pane"] == pane))?;
    probe.command("focus_pane", json!({"pane": below}))?;
    probe.wait_model("focus_pane", Duration::from_secs(10), |m| active_window(m, HOST, session).is_some_and(|w| w["focused_pane"] == below))?;
    probe.command("swap_pane", json!({"pane": pane, "direction": "down"}))?;
    probe.wait_model("swapped", Duration::from_secs(10), |m| {
        active_window(m, HOST, session).is_some_and(|w| w["layout"]["children"][0]["node"]["pane"] == below)
    })?;
    let split_id = active_window(&probe.model, HOST, session).and_then(|w| w["layout"]["id"].as_str().map(str::to_owned)).context("split id")?;
    probe.command("set_split_ratio", json!({"host": HOST, "session": session, "split": split_id, "ratio": 0.25}))?;
    probe.wait_model("ratio 0.25", Duration::from_secs(10), |m| active_window(m, HOST, session).is_some_and(|w| (w["layout"]["ratio"].as_f64().unwrap_or(0.0) - 0.25).abs() < 0.01))?;
    probe.command("balance", json!({"host": HOST, "session": session, "window": window_id}))?;
    probe.wait_model("balanced", Duration::from_secs(10), |m| active_window(m, HOST, session).is_some_and(|w| (w["layout"]["ratio"].as_f64().unwrap_or(0.0) - 0.5).abs() < 0.01))?;
    probe.log("focus_direction, focus_pane, swap_pane, set_split_ratio and balance applied");

    // Ownership: a block another client creates is claimed while the app has focus.
    if let Some(destination) = endpoint.strip_prefix("ssh://") {
        let destination = destination.split('/').next().unwrap_or(destination);
        let out = std::process::Command::new(r"C:\Windows\System32\OpenSSH\ssh.exe")
            .args(["-o", "BatchMode=yes", destination, "rex", "split", "-s", session, "--json"])
            .output()?;
        ensure!(out.status.success(), "rex split over ssh failed: {}", String::from_utf8_lossy(&out.stderr));
        let model = probe.wait_model("the CLI's pane", Duration::from_secs(10), |m| {
            active_window(m, HOST, session).is_some_and(|w| w["panes"].as_array().is_some_and(|p| p.len() == 3))
        })?;
        let cli_pane = active_window(&model, HOST, session)
            .and_then(|w| w["panes"].as_array().and_then(|p| p.iter().find(|p| p["pane"] != pane && p["pane"] != below).cloned()))
            .and_then(|p| p["pane"].as_u64())
            .context("cli pane")?;
        probe.wait_model("the CLI's pane claimed", Duration::from_secs(10), |m| pane_of(m, HOST, session, cli_pane).is_some_and(|p| p["owned"] == true))?;
        probe.log(format!("pane {cli_pane} made by the rex CLI was claimed (own_block take) while focused"));
        probe.command("close_pane", json!({"pane": cli_pane}))?;
    }
    probe.command("close_pane", json!({"pane": below}))?;
    probe.wait_model("one pane again", Duration::from_secs(10), |m| active_window(m, HOST, session).is_some_and(|w| w["panes"].as_array().is_some_and(|p| p.len() == 1)))?;

    probe.command("move_tab", json!({"host": HOST, "session": session, "window": window_id, "index": 1}))?;
    probe.wait_model("tab moved", Duration::from_secs(10), |m| {
        session_of(m, HOST, session).is_some_and(|s| s["windows"][0]["id"] == tab_id && s["windows"][1]["id"] == window_id)
    })?;
    probe.log("move_tab applied");

    let theme = json!({"background": "#102030", "foreground": "#e0e0e0", "cursor": "#ff8800",
        "palette": ["#000000","#cc0000","#00cc00","#cccc00","#0000cc","#cc00cc","#00cccc","#cccccc",
                    "#555555","#ff5555","#55ff55","#ffff55","#5555ff","#ff55ff","#55ffff","#ffffff"]});
    probe.command("set_theme", json!({"theme": theme}))?;
    ensure!(probe.pump(Duration::from_secs(10), |p| p.frame_text(pane).is_some_and(|f| f.3 == 0x102030)), "theme background not applied");
    probe.log("set_theme: the server's theme APC recoloured the pane (bg #102030)");

    probe.command("clear_pane", json!({"pane": pane}))?;
    probe.wait_frame(pane, "a cleared screen", Duration::from_secs(10), |lines| !lines.iter().any(|l| l.contains("sent-42")))?;
    probe.command("reset_pane", json!({"pane": pane}))?;
    probe.log("clear_pane and reset_pane applied");

    probe.command("reconnect_pane", json!({"pane": pane}))?;
    probe.pump(Duration::from_millis(500), |_| false);
    probe.type_keys(pane, "echo after-reconnect-pane")?;
    probe.enter(pane)?;
    probe.wait_frame(pane, "output after reconnect_pane", Duration::from_secs(10), |lines| lines.iter().any(|l| l.trim() == "after-reconnect-pane"))?;
    probe.log("reconnect_pane: fresh snapshot, typing works");
    Ok(())
}

// ------------------------------------------------------------------ transfers

const FILES_LABEL: &str = "windex-test-files-probe";
const REMOTE_DIR: &str = "/tmp/windex-test-files-probe";

fn transfer(endpoint: &str) -> Result<()> {
    let mut probe = Probe::labelled("windex-probe-files")?;
    connect(&mut probe, endpoint)?;
    // Leftovers of an interrupted run are ours by name; anything else is never touched.
    let leftovers: Vec<String> = host_of(&probe.model, HOST)
        .and_then(|h| h["sessions"].as_array())
        .into_iter()
        .flatten()
        .filter(|s| s["label"] == FILES_LABEL)
        .filter_map(|s| s["id"].as_str().map(str::to_owned))
        .collect();
    for id in leftovers {
        probe.log(format!("killing leftover test session {id}"));
        probe.command("kill_session", json!({"host": HOST, "session": id}))?;
    }
    let local = LocalDir::new()?;
    let outcome = transfer_checks(&mut probe, &local);
    if let Some(session) = probe.test_session.clone() {
        probe.command("app_focus", json!({"focused": false}))?;
        probe.command("kill_session", json!({"host": HOST, "session": session}))?;
        probe.wait_model("session gone", Duration::from_secs(15), |m| session_of(m, HOST, &session).is_none())?;
        probe.log("test session killed");
    }
    outcome?;
    probe.command("remove_host", json!({"id": HOST}))?;
    probe.log("all transfer checks passed");
    Ok(())
}

/// A folder of the probe's own under %TEMP%, removed at the end.
struct LocalDir(std::path::PathBuf);

impl LocalDir {
    fn new() -> Result<Self> {
        let path = std::env::temp_dir().join(format!("rexcore-probe-files-{}", std::process::id()));
        std::fs::create_dir_all(&path)?;
        Ok(Self(path))
    }
}

impl Drop for LocalDir {
    fn drop(&mut self) {
        let _ = std::fs::remove_dir_all(&self.0);
    }
}

/// SHA-256 of a local file (PowerShell's Get-FileHash), lower case.
fn local_sha256(path: &std::path::Path) -> Result<String> {
    let script = format!("(Get-FileHash -Algorithm SHA256 -LiteralPath '{}').Hash", path.display());
    let out = std::process::Command::new("powershell").args(["-NoProfile", "-NonInteractive", "-Command", &script]).output()?;
    let hash = String::from_utf8_lossy(&out.stdout).trim().to_lowercase();
    ensure!(hash.len() == 64, "Get-FileHash {}: {hash:?}", path.display());
    Ok(hash)
}

/// Bytes that do not compress (xorshift), so a transfer moves them all.
fn noise(len: usize, seed: u64) -> Vec<u8> {
    let mut x = seed | 1;
    let mut out = Vec::with_capacity(len + 8);
    while out.len() < len {
        x ^= x << 13;
        x ^= x >> 7;
        x ^= x << 17;
        out.extend_from_slice(&x.to_le_bytes());
    }
    out.truncate(len);
    out
}

/// `sha256sum` output on screen: path â†’ hash.
fn remote_hashes(lines: &[String]) -> HashMap<String, String> {
    lines
        .iter()
        .filter_map(|l| {
            let (hash, path) = l.split_once("  ")?;
            (hash.len() == 64 && hash.chars().all(|c| c.is_ascii_hexdigit())).then(|| (path.trim().to_owned(), hash.to_owned()))
        })
        .collect()
}

/// Run a shell line in the pane after clearing the screen.
fn shell(probe: &mut Probe, pane: u64, line: &str) -> Result<()> {
    probe.paste(pane, &format!("clear; {line}"))?;
    probe.enter(pane)
}

/// Clear the screen, wait for the marker, and leave the prompt empty.
fn fresh_prompt(probe: &mut Probe, pane: u64, marker: &str) -> Result<()> {
    shell(probe, pane, &format!("echo {marker}"))?;
    probe.wait_frame(pane, "a clear prompt", Duration::from_secs(10), |lines| lines.iter().any(|l| l.trim() == marker))?;
    Ok(())
}

fn transfer_checks(probe: &mut Probe, local: &LocalDir) -> Result<()> {
    let host = host_of(&probe.model, HOST).context("host")?;
    probe.log(format!("server api {}: can_upload={} can_download={}", host["server"]["api"], host["can_upload"], host["can_download"]));
    ensure!(host["can_upload"] == true && host["can_download"] == true, "the server should allow uploads and downloads");

    let created = probe.command("new_session", json!({"host": HOST, "label": FILES_LABEL}))?;
    let session = created["session"].as_str().context("no session id")?.to_owned();
    probe.test_session = Some(session.clone());
    probe.command("open_session", json!({"host": HOST, "session": session}))?;
    let model = probe.wait_model("the session's pane", Duration::from_secs(20), |m| {
        active_window(m, HOST, &session).is_some_and(|w| w["panes"].as_array().is_some_and(|p| !p.is_empty()))
    })?;
    let pane = active_window(&model, HOST, &session).and_then(|w| w["panes"][0]["pane"].as_u64()).context("pane")?;
    probe.wait_frame(pane, "a shell prompt", Duration::from_secs(20), |lines| lines.iter().any(|l| !l.trim().is_empty()))?;
    probe.command("app_focus", json!({"focused": true}))?;
    probe.resize(pane, 320, 50)?;
    probe.wait_model("owned at 320x50", Duration::from_secs(10), |m| pane_of(m, HOST, &session, pane).is_some_and(|p| p["server_cols"] == 320 && p["owned"] == true))?;
    shell(probe, pane, &format!("mkdir -p {REMOTE_DIR} && cd {REMOTE_DIR} && echo ready-files"))?;
    probe.wait_frame(pane, "the work folder", Duration::from_secs(10), |lines| lines.iter().any(|l| l.trim() == "ready-files"))?;

    println!("--- (t1) a batch of two files: the paths are typed, sha256 matches on the server");
    let text = local.0.join("notes windex.txt");
    std::fs::write(&text, "hello from Windex\n".repeat(500))?;
    let png = local.0.join("windex-clip.png");
    let mut image = b"\x89PNG\r\n\x1a\n".to_vec();
    image.extend(noise(300_000, 7));
    std::fs::write(&png, &image)?;
    fresh_prompt(probe, pane, "batch")?;
    probe.paste(pane, "sha256sum ")?;
    let files = json!([{"path": text}, {"path": png, "name": "screenshot test.png", "temporary": true}]);
    let req = probe.send("upload", json!({"pane": pane, "id": "probe-batch", "files": files}))?;
    let mut listed = false;
    probe.pump(Duration::from_secs(60), |p| {
        listed |= p.model["transfers"].as_array().is_some_and(|t| t.iter().any(|t| t["id"] == "probe-batch" && t["count"] == 2));
        p.results.contains_key(&req)
    });
    let result = probe.result(req, Duration::from_secs(1))?;
    ensure!(result["ok"] == true, "upload failed: {result}");
    ensure!(listed, "the batch never showed in the model's transfers");
    ensure!(!png.exists(), "a temporary file outlived its upload");
    probe.pump(Duration::from_millis(300), |_| false);
    ensure!(probe.model["transfers"].as_array().is_some_and(Vec::is_empty), "a finished transfer stayed in the model");
    probe.log(format!("upload RESULT {}", result["data"]));
    // Long paths wrap: look at the screen as one text.
    let typed = probe.wait_frame(pane, "the typed paths", Duration::from_secs(10), |lines| {
        let all = lines.concat();
        all.contains("sha256sum ") && all.contains("notes") && all.contains("screenshot")
    })?;
    print_screen(&typed);
    probe.enter(pane)?;
    let screen = probe.wait_frame(pane, "two hashes", Duration::from_secs(10), |lines| remote_hashes(lines).len() >= 2)?;
    print_screen(&screen);
    let hashes = remote_hashes(&screen);
    let path_of = |name: &str| hashes.keys().find(|p| p.ends_with(name)).cloned();
    let text_path = path_of("notes windex.txt").context("no hash for notes windex.txt")?;
    let png_path = path_of("screenshot test.png").context("no hash for screenshot test.png")?;
    ensure!(hashes[&text_path] == local_sha256(&text)?, "notes windex.txt differs on the server");
    std::fs::write(&png, &image)?;
    ensure!(hashes[&png_path] == local_sha256(&png)?, "screenshot test.png differs on the server");
    probe.log(format!("sha256 matches on the server: {text_path} and {png_path}"));

    println!("--- (t2) downloading the uploads back gives the same bytes");
    for (remote, original) in [(&text_path, &text), (&png_path, &png)] {
        let dest = local.0.join(format!("back-{}", original.file_name().unwrap_or_default().to_string_lossy()));
        let data = probe.command("download", json!({"pane": pane, "path": remote, "dest": dest}))?;
        ensure!(std::fs::read(&dest)? == std::fs::read(original)?, "{remote} came back different");
        probe.log(format!("download RESULT {data}"));
    }

    println!("--- (t3) a 50 MB upload shows progress and can be canceled; nothing is typed");
    let big = local.0.join("big-50mb.bin");
    std::fs::write(&big, noise(50 << 20, 99))?;
    fresh_prompt(probe, pane, "cancel-check")?;
    let req = probe.send("upload", json!({"pane": pane, "id": "probe-big", "files": [{"path": big}]}))?;
    let mut samples: Vec<u64> = Vec::new();
    let moving = probe.pump(Duration::from_secs(60), |p| {
        if let Some(t) = p.model["transfers"].as_array().and_then(|t| t.iter().find(|t| t["id"] == "probe-big")) {
            let bytes = t["bytes"].as_u64().unwrap_or(0);
            if samples.last() != Some(&bytes) {
                samples.push(bytes);
            }
            return bytes > 12 << 20;
        }
        p.results.contains_key(&req)
    });
    ensure!(moving && !probe.results.contains_key(&req), "the big upload never got going: {samples:?}");
    probe.command("cancel_transfer", json!({"id": "probe-big"}))?;
    let result = probe.result(req, Duration::from_secs(15))?;
    ensure!(result["ok"] == false && result["error"] == "canceled", "a canceled upload should fail with canceled: {result}");
    probe.log(format!("{} progress updates before cancel (last {} bytes); RESULT {}", samples.len(), samples.last().unwrap_or(&0), result["error"]));
    ensure!(samples.len() >= 3, "progress should update while uploading: {samples:?}");
    probe.pump(Duration::from_secs(2), |_| false);
    let screen = probe.frame_text(pane).map(|f| f.0).unwrap_or_default();
    ensure!(!screen.iter().any(|l| l.contains("big-50mb")), "a canceled upload typed its path");
    let uploads = text_path.rsplit_once('/').map(|(dir, _)| dir.to_owned()).context("upload folder")?;
    shell(probe, pane, &format!("find \"$(dirname '{uploads}')\" -name 'big-50mb*' | wc -l | sed 's/^/left=/'"))?;
    let screen = probe.wait_frame(pane, "the leftover count", Duration::from_secs(10), |lines| lines.iter().any(|l| l.starts_with("left=")))?;
    ensure!(screen.iter().any(|l| l.trim() == "left=0"), "a canceled upload left a file on the server");
    probe.log("canceled: nothing typed, nothing left on the server");

    println!("--- (t4) the whole 50 MB upload");
    fresh_prompt(probe, pane, "big")?;
    probe.paste(pane, "sha256sum ")?;
    let started = Instant::now();
    let req = probe.send("upload", json!({"pane": pane, "files": [{"path": big}]}))?;
    let result = probe.result(req, Duration::from_secs(600))?;
    ensure!(result["ok"] == true, "the 50 MB upload failed: {result}");
    let seconds = started.elapsed().as_secs_f64();
    probe.log(format!("50 MiB uploaded in {seconds:.1} s ({:.1} MB/s)", 52.4288 / seconds));
    probe.wait_frame(pane, "the typed path", Duration::from_secs(10), |lines| lines.concat().contains("big-50mb.bin"))?;
    probe.enter(pane)?;
    let screen = probe.wait_frame(pane, "its hash", Duration::from_secs(30), |lines| remote_hashes(lines).keys().any(|p| p.ends_with("big-50mb.bin")))?;
    let hashes = remote_hashes(&screen);
    let big_path = hashes.keys().find(|p| p.ends_with("big-50mb.bin")).cloned().context("big path")?;
    ensure!(hashes[&big_path] == local_sha256(&big)?, "big-50mb.bin differs on the server");
    probe.log("sha256 of the 50 MiB upload matches on the server");

    println!("--- (t5) download a remote file by a path relative to the shell's folder");
    shell(probe, pane, "head -c 3000000 /dev/urandom > dl.bin && sha256sum dl.bin")?;
    let screen = probe.wait_frame(pane, "the remote hash", Duration::from_secs(10), |lines| remote_hashes(lines).contains_key("dl.bin"))?;
    let remote_hash = remote_hashes(&screen)["dl.bin"].clone();
    let dest = local.0.join("dl.bin");
    let started = Instant::now();
    let data = probe.command("download", json!({"pane": pane, "path": "dl.bin", "dest": dest}))?;
    ensure!(data["remote"] == format!("{REMOTE_DIR}/dl.bin") && data["bytes"] == 3_000_000, "download result: {data}");
    ensure!(local_sha256(&dest)? == remote_hash, "dl.bin differs after download");
    probe.log(format!("downloaded 3 MB in {:.2} s; sha256 matches ({remote_hash})", started.elapsed().as_secs_f64()));

    println!("--- (t6) cancel a big download: no file, no partial file");
    shell(probe, pane, "truncate -s 2G sparse.bin && echo sparse-ready")?;
    probe.wait_frame(pane, "a big remote file", Duration::from_secs(10), |lines| lines.iter().any(|l| l.trim() == "sparse-ready"))?;
    let dest = local.0.join("sparse.bin");
    let req = probe.send("download", json!({"pane": pane, "id": "probe-down", "path": "sparse.bin", "dest": dest}))?;
    let moving = probe.pump(Duration::from_secs(30), |p| {
        p.model["transfers"]
            .as_array()
            .and_then(|t| t.iter().find(|t| t["id"] == "probe-down"))
            .is_some_and(|t| t["bytes"].as_u64().unwrap_or(0) > 4 << 20 && t["total"] == 2u64 << 30)
    });
    ensure!(moving, "the download never got going");
    probe.command("cancel_transfer", json!({"id": "probe-down"}))?;
    let result = probe.result(req, Duration::from_secs(15))?;
    ensure!(result["error"] == "canceled", "a canceled download should fail with canceled: {result}");
    ensure!(!dest.exists() && !local.0.join("sparse.bin.partial").exists(), "a canceled download left a file behind");
    probe.log("download canceled; nothing left locally");

    println!("--- (t7) refusals");
    let folder = probe.send("upload", json!({"pane": pane, "files": [{"path": local.0}]}))?;
    let folder = probe.result(folder, Duration::from_secs(10))?;
    ensure!(folder["error"].as_str().is_some_and(|e| e.contains("is a folder")), "uploading a folder: {folder}");
    probe.log(format!("upload of a folder: {}", folder["error"]));
    for path in [REMOTE_DIR, "no-such-file.txt"] {
        let req = probe.send("download", json!({"pane": pane, "path": path, "dest": local.0.join("x")}))?;
        let refused = probe.result(req, Duration::from_secs(10))?;
        ensure!(refused["ok"] == false, "downloading {path} should fail: {refused}");
        probe.log(format!("download {path}: {}", refused["error"]));
    }
    let unknown = probe.send("cancel_transfer", json!({"id": "nope"}))?;
    ensure!(probe.result(unknown, Duration::from_secs(5))?["ok"] == false, "cancel_transfer of an unknown id should fail");
    // Download file… asks list_dir, narrowed to the name, whether a path is a file before saving.
    let listing = probe.command("list_dir", json!({"pane": pane, "path": format!("{REMOTE_DIR}/"), "prefix": "DL."}))?;
    let names: Vec<&str> = listing["entries"].as_array().into_iter().flatten().filter_map(|e| e["name"].as_str()).collect();
    ensure!(listing["status"] == "ok" && names == ["dl.bin"], "list_dir with prefix DL.: {listing}");
    let missing = probe.command("list_dir", json!({"pane": pane, "path": "/tmp/windex-no-such-dir/", "prefix": "x"}))?;
    ensure!(missing["status"] == "missing", "list_dir of a missing folder: {missing}");
    probe.log(format!("list_dir prefix DL. -> {names:?}; a missing folder -> status {}", missing["status"]));

    println!("--- (t8) a batch with a file over the server's limit fails before a byte is sent");
    let small = local.0.join("t8 small.txt");
    std::fs::write(&small, "never sent\n".repeat(200))?;
    let huge = local.0.join("t8-huge.bin");
    std::fs::File::create(&huge)?.set_len(101 << 20)?;
    fresh_prompt(probe, pane, "too-large")?;
    let req = probe.send("upload", json!({"pane": pane, "id": "probe-huge", "files": [{"path": small}, {"path": huge}]}))?;
    let mut sent = 0u64;
    probe.pump(Duration::from_secs(30), |p| {
        if let Some(t) = p.model["transfers"].as_array().and_then(|t| t.iter().find(|t| t["id"] == "probe-huge")) {
            sent = sent.max(t["bytes"].as_u64().unwrap_or(0));
        }
        p.results.contains_key(&req)
    });
    let refused = probe.result(req, Duration::from_secs(1))?;
    let error = refused["error"].as_str().unwrap_or_default();
    ensure!(refused["ok"] == false && error.contains("t8-huge.bin") && error.contains("too large"), "a batch with a 101 MiB file: {refused}");
    ensure!(sent == 0, "{sent} bytes were sent before the batch was refused");
    probe.pump(Duration::from_secs(2), |_| false);
    ensure!(!probe.frame_text(pane).map(|f| f.0).unwrap_or_default().iter().any(|l| l.contains("t8")), "a refused batch typed a path");
    shell(probe, pane, &format!("find \"$(dirname '{uploads}')\" -name 't8*' | wc -l | sed 's/^/left=/'"))?;
    let screen = probe.wait_frame(pane, "the leftover count", Duration::from_secs(10), |lines| lines.iter().any(|l| l.starts_with("left=")))?;
    ensure!(screen.iter().any(|l| l.trim() == "left=0"), "the refused batch left a file on the server");
    probe.log(format!("refused at once, 0 bytes sent, nothing typed or stored: {error}"));

    // Each upload has a folder of its own under the server's uploads.
    let folders = [&text_path, &png_path, &big_path].map(|p| format!("\"$(dirname '{p}')\"")).join(" ");
    shell(probe, pane, &format!("cd ~ && rm -rf {REMOTE_DIR} {folders} && echo cleaned"))?;
    probe.wait_frame(pane, "clean up", Duration::from_secs(10), |lines| lines.iter().any(|l| l.trim() == "cleaned"))?;
    Ok(())
}
