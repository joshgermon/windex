//! The contract's commands (docs/contract.md "Commands"). Each replies with exactly one RESULT.
//! Anything that talks to a server runs as a task; creation requests are sent once and never
//! retried (docs/protocol.md §7.1).
use super::control::Control;
use super::host::{self, HostCmd};
use super::link::HostLink;
use super::state::{self, Host, Status, active_window, tiled_blocks};
use super::{Shared, discover, transfer};
use rexrs::api::Layout;
use rexrs::api::session::{NewSplitResponse, NewWindowResponse, ViewLayoutNode, ViewResponse, Window};
use serde_json::{Value, json};
use std::future::Future;
use std::sync::Arc;
use tokio::sync::mpsc;

type Outcome = Result<Value, String>;

pub fn dispatch(shared: &Arc<Shared>, req: u64, cmd: &str, v: &Value) -> bool {
    let s = shared.clone();
    let v = v.clone();
    match cmd {
        "add_host" => reply(shared, req, add_host(shared, &v)),
        "update_host" => reply(shared, req, update_host(shared, &v)),
        "remove_host" => reply(shared, req, remove_host(shared, &v)),
        "connect_host" => reply(shared, req, send_host(shared, &v, HostCmd::Connect)),
        "disconnect_host" => reply(shared, req, send_host(shared, &v, HostCmd::Disconnect)),
        "app_focus" => reply(shared, req, app_focus(shared, &v)),
        "set_theme" => reply(shared, req, set_theme(shared, &v)),
        "reconnect_pane" => reply(shared, req, reconnect_pane(shared, &v)),
        "open_session" => spawn(shared, req, open_session(s, v)),
        "new_session" => spawn(shared, req, new_session(s, v)),
        "rename_session" => spawn(shared, req, async move {
            let (_, session, control) = session_target(&s, &v)?;
            call(&control, "session.set_label", json!({"session_id": session, "label": text(&v, "label")?})).await
        }),
        "kill_session" => spawn(shared, req, async move {
            let (_, session, control) = session_target(&s, &v)?;
            call(&control, "session.destroy", json!({"session_id": session})).await
        }),
        "new_tab" => spawn(shared, req, new_tab(s, v)),
        "select_tab" => spawn(shared, req, async move {
            let (_, session, control) = session_target(&s, &v)?;
            call(&control, "session.focus_window", json!({"session_id": session, "window_id": text(&v, "window")?})).await
        }),
        "rename_tab" => spawn(shared, req, async move {
            let (_, session, control) = session_target(&s, &v)?;
            let payload = json!({"session_id": session, "window_id": text(&v, "window")?, "label": text(&v, "label")?});
            call(&control, "session.set_window_label", payload).await
        }),
        "close_tab" => spawn(shared, req, async move {
            let (_, session, control) = session_target(&s, &v)?;
            call(&control, "session.close_window", json!({"session_id": session, "window_id": text(&v, "window")?})).await
        }),
        "move_tab" => spawn(shared, req, move_tab(s, v)),
        "split" => spawn(shared, req, split(s, v)),
        "close_pane" => spawn(shared, req, async move {
            let t = pane_target(&s, &v)?;
            call(&t.control, "block.close", json!({"session_id": t.session, "block_id": t.block})).await
        }),
        "focus_pane" => spawn(shared, req, async move {
            let t = pane_target(&s, &v)?;
            call(&t.control, "session.focus_block", json!({"session_id": t.session, "block_id": t.block})).await
        }),
        "focus_direction" => spawn(shared, req, async move {
            let t = pane_target(&s, &v)?;
            let direction = direction(&v)?;
            // Start from the pane the UI has focused, which the server may not know yet.
            let _ = t.control.fire("session.focus_block", json!({"session_id": t.session, "block_id": t.block}));
            call(&t.control, "session.focus_direction", json!({"session_id": t.session, "direction": direction, "wrap": true})).await
        }),
        "zoom_pane" => spawn(shared, req, async move {
            let t = pane_target(&s, &v)?;
            let mut payload = json!({"session_id": t.session, "block_id": t.block});
            if let Some(zoom) = v["zoom"].as_bool() {
                payload["zoom"] = json!(zoom);
            }
            call(&t.control, "session.zoom_block", payload).await
        }),
        "set_split_ratio" => spawn(shared, req, set_split_ratio(s, v)),
        "balance" => spawn(shared, req, balance(s, v)),
        "swap_pane" => spawn(shared, req, swap_pane(s, v)),
        "send_text" => spawn(shared, req, async move {
            let t = pane_target(&s, &v)?;
            let args = json!({"data": text(&v, "text")?});
            call(&t.control, "com.superlogical.terminal.write", json!({"session_id": t.session, "block_id": t.block, "args": args})).await
        }),
        "clear_pane" | "reset_pane" => {
            let method = if cmd == "clear_pane" { "com.superlogical.terminal.clear" } else { "com.superlogical.terminal.reset" };
            spawn(shared, req, async move {
                let t = pane_target(&s, &v)?;
                call(&t.control, method, json!({"session_id": t.session, "block_id": t.block, "args": {}})).await
            })
        }
        "rename_pane" => spawn(shared, req, async move {
            let t = pane_target(&s, &v)?;
            let payload = json!({"session_id": t.session, "block_id": t.block, "label": text(&v, "label")?});
            call(&t.control, "session.set_block_label", payload).await
        }),
        "list_dir" => spawn(shared, req, list_dir(s, v)),
        "upload" => spawn(shared, req, transfer::upload(s, v)),
        "download" => spawn(shared, req, transfer::download(s, v)),
        "cancel_transfer" => reply(shared, req, transfer::cancel(shared, &v)),
        "server_status" => spawn(shared, req, async move {
            let control = host::control_of(&s, &text(&v, "host")?)?;
            host::server_status(&control).await
        }),
        "discover" => spawn(shared, req, async move { discover::run(&s).await }),
        _ => return false,
    }
    true
}

fn reply(shared: &Shared, req: u64, outcome: Outcome) {
    shared.emitter.result(req, outcome);
}

fn spawn(shared: &Arc<Shared>, req: u64, work: impl Future<Output = Outcome> + Send + 'static) {
    let s = shared.clone();
    shared.runtime.spawn(async move {
        let outcome = work.await;
        s.emitter.result(req, outcome);
    });
}

fn text(v: &Value, key: &str) -> Result<String, String> {
    v[key].as_str().map(str::to_owned).ok_or_else(|| format!("missing \"{key}\""))
}

async fn call(control: &Control, method: &str, payload: Value) -> Outcome {
    control.call(method, payload).await.map(|_| Value::Null).map_err(|e| e.to_string())
}

// ------------------------------------------------------------------- hosts

fn add_host(shared: &Arc<Shared>, v: &Value) -> Outcome {
    let id = text(v, "id")?;
    let endpoint = text(v, "endpoint")?;
    let name = v["name"].as_str().filter(|n| !n.is_empty()).map(str::to_owned).unwrap_or_else(|| endpoint.clone());
    let connect = v["connect"].as_bool().unwrap_or(true);
    let (tx, rx) = mpsc::unbounded_channel();
    {
        let mut st = shared.state.lock();
        if st.host(&id).is_some() {
            return Err(format!("host {id} already exists"));
        }
        st.hosts.push(Host {
            id: id.clone(),
            name,
            endpoint,
            status: if connect { Status::Connecting } else { Status::Offline },
            error: None,
            server: None,
            client_id: None,
            sessions: Vec::new(),
            handles: Default::default(),
            blocks: Default::default(),
            tickets: Default::default(),
            link: Arc::new(HostLink::new(id.clone(), shared.theme.clone(), shared.emitter.clone())),
            tx,
        });
    }
    shared.runtime.spawn(host::run(shared.clone(), id, rx, connect));
    shared.touch();
    Ok(Value::Null)
}

fn update_host(shared: &Shared, v: &Value) -> Outcome {
    let id = text(v, "id")?;
    let mut st = shared.state.lock();
    let host = st.host_mut(&id).ok_or_else(|| format!("unknown host {id}"))?;
    if let Some(name) = v["name"].as_str().filter(|n| !n.is_empty()) {
        host.name = name.to_owned();
    }
    if let Some(endpoint) = v["endpoint"].as_str().filter(|e| *e != host.endpoint) {
        host.endpoint = endpoint.to_owned();
        let _ = host.tx.send(HostCmd::Restart);
    }
    drop(st);
    shared.touch();
    Ok(Value::Null)
}

fn remove_host(shared: &Arc<Shared>, v: &Value) -> Outcome {
    let id = text(v, "id")?;
    let mut st = shared.state.lock();
    let index = st.host_index(&id).ok_or_else(|| format!("unknown host {id}"))?;
    let mut host = st.hosts.remove(index);
    // The task closes the connection gracefully in the background.
    let (done, _) = tokio::sync::oneshot::channel();
    let _ = host.tx.send(HostCmd::Shutdown(done));
    host.retire_all(shared);
    st.forget_claims(&id);
    st.settle_current();
    drop(st);
    shared.touch();
    Ok(Value::Null)
}

fn send_host(shared: &Shared, v: &Value, cmd: HostCmd) -> Outcome {
    let id = text(v, "id")?;
    let st = shared.state.lock();
    let host = st.host(&id).ok_or_else(|| format!("unknown host {id}"))?;
    host.tx.send(cmd).map_err(|_| "the host task has stopped".to_owned())?;
    Ok(Value::Null)
}

fn app_focus(shared: &Arc<Shared>, v: &Value) -> Outcome {
    let focused = v["focused"].as_bool().ok_or("missing \"focused\"")?;
    let mut st = shared.state.lock();
    if focused && !st.app_focused {
        // Coming forward claims the visible panes again (once each).
        st.claimed.clear();
    }
    st.app_focused = focused;
    if focused
        && let Some(index) = st.current.as_ref().and_then(|(h, _)| st.host_index(h))
    {
        state::reconcile(shared, &mut st, index);
    }
    drop(st);
    shared.touch();
    Ok(Value::Null)
}

/// The UI's theme → the server's `set_theme` arguments, sent to every block we own.
fn set_theme(shared: &Shared, v: &Value) -> Outcome {
    let theme = &v["theme"];
    if !theme.is_object() {
        return Err("missing \"theme\"".into());
    }
    let mut args = json!({});
    for key in ["foreground", "background", "cursor"] {
        if let Some(color) = theme[key].as_str() {
            args[key] = json!(color);
        }
    }
    if let Some(palette) = theme["palette"].as_array() {
        let entries: Vec<String> = palette.iter().enumerate().filter_map(|(i, c)| Some(format!("{i}={}", c.as_str()?))).collect();
        args["palette"] = json!(entries);
    }
    if let Some(scheme) = theme["background"].as_str().and_then(scheme_of) {
        args["scheme"] = json!(scheme);
    }
    *shared.theme.lock() = Some(args);
    let st = shared.state.lock();
    for link in st.hosts.iter().flat_map(|h| h.blocks.values()).filter_map(|b| b.link.as_ref()) {
        link.push_theme();
    }
    Ok(Value::Null)
}

/// `light` or `dark` from a background colour's luminance.
fn scheme_of(color: &str) -> Option<&'static str> {
    let hex = color.trim_start_matches('#');
    let v = u32::from_str_radix(hex, 16).ok().filter(|_| hex.len() == 6)?;
    let channel = |shift: u32| f64::from((v >> shift) & 0xff) / 255.0;
    let luminance = 0.2126 * channel(16) + 0.7152 * channel(8) + 0.0722 * channel(0);
    Some(if luminance < 0.5 { "dark" } else { "light" })
}

fn reconnect_pane(shared: &Arc<Shared>, v: &Value) -> Outcome {
    let pane = v["pane"].as_u64().ok_or("missing \"pane\"")?;
    let mut st = shared.state.lock();
    let (host_id, block_id) = st.find_pane(pane).map(|(h, b, _)| (h.id.clone(), b.to_owned())).ok_or("unknown pane")?;
    let index = st.host_index(&host_id).ok_or("unknown pane")?;
    let block = st.hosts[index].blocks.get_mut(&block_id).ok_or("unknown pane")?;
    match &block.stream {
        Some(stream) if !stream.is_finished() => stream.restart(),
        _ => {
            block.stream = None;
            state::reconcile(shared, &mut st, index);
        }
    }
    Ok(Value::Null)
}

// ---------------------------------------------------------------- sessions

/// (host, session id, control) for commands naming `host` and `session` (id or label).
fn session_target(shared: &Shared, v: &Value) -> Result<(String, String, Control), String> {
    let host_id = text(v, "host")?;
    let key = text(v, "session")?;
    let st = shared.state.lock();
    let host = st.host(&host_id).ok_or_else(|| format!("unknown host {host_id}"))?;
    let session = host.find_session(&key).ok_or_else(|| format!("no session {key} on {}", host.name))?;
    let control = host.link.control().ok_or_else(|| format!("{} is not connected", host.name))?;
    Ok((host_id, session.id.clone(), control))
}

async fn open_session(s: Arc<Shared>, v: Value) -> Outcome {
    let (host_id, session_id, control) = session_target(&s, &v)?;
    let attach = {
        let mut st = s.state.lock();
        st.current = Some((host_id.clone(), session_id.clone()));
        st.chosen = true;
        let index = st.host_index(&host_id).ok_or("unknown host")?;
        let session = st.hosts[index].session_mut(&session_id).ok_or("session gone")?;
        let attach = !session.attached && !session.attaching;
        session.attaching |= attach;
        state::reconcile(&s, &mut st, index);
        attach
    };
    s.touch();
    if !attach {
        return Ok(Value::Null);
    }
    let result = control.call("session.attach", json!({"session_id": session_id})).await;
    let mut st = s.state.lock();
    let index = st.host_index(&host_id).ok_or("unknown host")?;
    let session = st.hosts[index].session_mut(&session_id).ok_or("session gone")?;
    session.attaching = false;
    let value = result.map_err(|e| e.to_string())?;
    session.attached = true;
    if let Ok(view) = serde_json::from_value::<ViewResponse>(value["view"].clone()) {
        state::apply_view(session, view);
    }
    state::reconcile(&s, &mut st, index);
    drop(st);
    s.touch();
    Ok(Value::Null)
}

/// A shell layout, starting in `cwd` when known.
fn shell(cwd: Option<String>) -> Value {
    let layout = match cwd {
        Some(cwd) => Layout::shell().cwd(cwd),
        None => Layout::shell(),
    };
    serde_json::to_value(layout).unwrap_or(Value::Null)
}

async fn new_session(s: Arc<Shared>, v: Value) -> Outcome {
    let host_id = text(&v, "host")?;
    let control = host::control_of(&s, &host_id)?;
    let mut payload = json!({"initial_windows": [{"window_label": "Shell", "layout": shell(v["cwd"].as_str().map(str::to_owned))}]});
    if let Some(label) = v["label"].as_str().filter(|l| !l.is_empty()) {
        payload["label"] = json!(label);
    }
    let created = control.call("session.create", payload).await.map_err(|e| e.to_string())?;
    let session = created["session_id"].as_str().ok_or("session.create returned no session")?.to_owned();
    // Known at once, so the UI can open it before `session_created` arrives.
    let mut st = s.state.lock();
    if let Some(host) = st.host_mut(&host_id)
        && host.session(&session).is_none()
    {
        let label = created["label"].as_str().or(v["label"].as_str()).unwrap_or_default().to_owned();
        host.sessions.push(state::Session::new(session.clone(), label));
        state::fetch_view(&s, host, &session, 1);
    }
    drop(st);
    s.touch();
    Ok(json!({"session": session}))
}

/// The working directory of the focused pane of a session's active window.
fn session_cwd(shared: &Shared, host_id: &str, session_id: &str) -> Option<String> {
    let st = shared.state.lock();
    let host = st.host(host_id)?;
    let window = active_window(host.session(session_id)?.view.as_ref()?)?;
    let block = window.focused_block_id.as_ref()?;
    host.blocks.get(block)?.activity.working_directory().map(str::to_owned)
}

async fn new_tab(s: Arc<Shared>, v: Value) -> Outcome {
    let (host_id, session, control) = session_target(&s, &v)?;
    let cwd = v["cwd"].as_str().map(str::to_owned).or_else(|| session_cwd(&s, &host_id, &session));
    let payload = json!({"session_id": session, "window_label": "Shell", "layout": shell(cwd), "focus": true});
    let created = control.call("session.new_window", payload).await.map_err(|e| e.to_string())?;
    let created: NewWindowResponse = serde_json::from_value(created).map_err(|e| e.to_string())?;
    Ok(json!({"window": created.window_id}))
}

/// The session's view (cloned) for layout computations.
fn view_of(shared: &Shared, host_id: &str, session_id: &str) -> Result<ViewResponse, String> {
    let st = shared.state.lock();
    st.host(host_id)
        .and_then(|h| h.session(session_id))
        .and_then(|s| s.view.clone())
        .ok_or_else(|| "the session's layout is not known yet".to_owned())
}

async fn move_tab(s: Arc<Shared>, v: Value) -> Outcome {
    let (host_id, session, control) = session_target(&s, &v)?;
    let window = text(&v, "window")?;
    let index = v["index"].as_u64().ok_or("missing \"index\"")? as usize;
    let view = view_of(&s, &host_id, &session)?;
    let others: Vec<&str> = view.windows.iter().filter_map(|w| w.window_id.as_deref()).filter(|w| *w != window).collect();
    let mut payload = json!({"session_id": session, "window_id": window});
    if let Some(before) = others.get(index) {
        payload["before_window_id"] = json!(before);
    }
    call(&control, "session.move_window", payload).await
}

// ------------------------------------------------------------------- panes

struct PaneTarget {
    host: String,
    session: String,
    block: String,
    cwd: Option<String>,
    control: Control,
}

fn pane_target(shared: &Shared, v: &Value) -> Result<PaneTarget, String> {
    let pane = v["pane"].as_u64().ok_or("missing \"pane\"")?;
    let st = shared.state.lock();
    let (host, block_id, block) = st.find_pane(pane).ok_or_else(|| format!("unknown pane {pane}"))?;
    let control = host.link.control().ok_or_else(|| format!("{} is not connected", host.name))?;
    Ok(PaneTarget {
        host: host.id.clone(),
        session: block.session.clone(),
        block: block_id.to_owned(),
        cwd: block.activity.working_directory().map(str::to_owned),
        control,
    })
}

fn direction(v: &Value) -> Result<&'static str, String> {
    match v["direction"].as_str() {
        Some("left") => Ok("left"),
        Some("right") => Ok("right"),
        Some("up") => Ok("up"),
        Some("down") => Ok("down"),
        other => Err(format!("invalid direction {other:?}")),
    }
}

async fn split(s: Arc<Shared>, v: Value) -> Outcome {
    let t = pane_target(&s, &v)?;
    let (direction, side) = match direction(&v)? {
        "right" => ("horizontal", "after"),
        "left" => ("horizontal", "before"),
        "down" => ("vertical", "after"),
        _ => ("vertical", "before"),
    };
    let payload = json!({
        "session_id": t.session, "anchor_id": t.block, "direction": direction, "side": side,
        "layout": shell(t.cwd.clone()), "focus": true,
    });
    let created = t.control.call("session.new_split", payload).await.map_err(|e| e.to_string())?;
    let created: NewSplitResponse = serde_json::from_value(created).map_err(|e| e.to_string())?;
    let block = created.block_ids.into_iter().flatten().next().ok_or("session.new_split created no block")?;
    // The handle is fixed now, before the view that places the block arrives.
    let mut st = s.state.lock();
    let host = st.host_mut(&t.host).ok_or("host removed")?;
    let pane = host.handle(&block, || s.panes.allocate());
    Ok(json!({"pane": pane}))
}

/// Find a split by id in a window's tiled layout.
fn find_split<'a>(node: &'a ViewLayoutNode, id: &str) -> Option<&'a ViewLayoutNode> {
    let split = node.split.as_ref()?;
    if split.split_id.as_deref() == Some(id) {
        return Some(node);
    }
    [&split.before, &split.after].into_iter().flatten().find_map(|n| find_split(n, id))
}

fn first_block(node: &ViewLayoutNode) -> Option<&str> {
    if let Some(block) = &node.block_id {
        return Some(block);
    }
    let split = node.split.as_ref()?;
    [&split.before, &split.after].into_iter().flatten().find_map(|n| first_block(n))
}

fn tiled_layout(window: &Window) -> Option<&ViewLayoutNode> {
    window.layers.iter().find(|l| l.kind == "tiled").or(window.layers.first())?.layout.as_ref()
}

/// `session.resize_pane` arguments for a split node at `ratio` (the border is named by the
/// first block on each side).
fn resize_payload(session: &str, node: &ViewLayoutNode, ratio: f64) -> Option<Value> {
    let split = node.split.as_ref()?;
    let direction = match split.direction {
        rexrs::api::Direction::Vertical => "vertical",
        _ => "horizontal",
    };
    Some(json!({
        "session_id": session,
        "direction": direction,
        "before_block_id": first_block(split.before.as_deref()?)?,
        "after_block_id": first_block(split.after.as_deref()?)?,
        "ratio": ratio,
    }))
}

async fn set_split_ratio(s: Arc<Shared>, v: Value) -> Outcome {
    let (host_id, session, control) = session_target(&s, &v)?;
    let split = text(&v, "split")?;
    let ratio = v["ratio"].as_f64().filter(|r| *r > 0.0 && *r < 1.0).ok_or("ratio must be between 0 and 1")?;
    let view = view_of(&s, &host_id, &session)?;
    let payload = view
        .windows
        .iter()
        .filter_map(tiled_layout)
        .find_map(|root| find_split(root, &split))
        .and_then(|node| resize_payload(&session, node, ratio))
        .ok_or_else(|| format!("no split {split}"))?;
    call(&control, "session.resize_pane", payload).await
}

/// How many panes a node holds along `direction` (nested splits the other way count as one).
fn span(node: &ViewLayoutNode, direction: rexrs::api::Direction) -> u32 {
    match &node.split {
        Some(split) if split.direction == direction => {
            split.before.as_deref().map_or(0, |n| span(n, direction)) + split.after.as_deref().map_or(0, |n| span(n, direction))
        }
        _ => 1,
    }
}

/// Equal-share ratios for every split of a layout (Rex Rune `balance`): each pane in a run of
/// same-direction splits gets the same space.
fn balanced(session: &str, node: &ViewLayoutNode, out: &mut Vec<Value>) {
    let Some(split) = &node.split else { return };
    let (Some(before), Some(after)) = (split.before.as_deref(), split.after.as_deref()) else { return };
    let (b, a) = (span(before, split.direction), span(after, split.direction));
    let ratio = f64::from(b) / f64::from(a + b);
    if (ratio - split.ratio).abs() > 0.001
        && let Some(payload) = resize_payload(session, node, ratio)
    {
        out.push(payload);
    }
    balanced(session, before, out);
    balanced(session, after, out);
}

async fn balance(s: Arc<Shared>, v: Value) -> Outcome {
    let (host_id, session, control) = session_target(&s, &v)?;
    let window = text(&v, "window")?;
    let view = view_of(&s, &host_id, &session)?;
    let root = view
        .windows
        .iter()
        .find(|w| w.window_id.as_deref() == Some(&window))
        .and_then(tiled_layout)
        .ok_or_else(|| format!("no window {window}"))?;
    let mut changes = Vec::new();
    balanced(&session, root, &mut changes);
    for payload in changes {
        call(&control, "session.resize_pane", payload).await?;
    }
    Ok(Value::Null)
}

/// Each block's rectangle `[x, y, w, h]` (window fractions) in a layout.
fn rects(node: &ViewLayoutNode, rect: [f64; 4], out: &mut Vec<(String, [f64; 4])>) {
    if let Some(block) = &node.block_id {
        out.push((block.clone(), rect));
        return;
    }
    let Some(split) = &node.split else { return };
    let r = if split.ratio > 0.0 && split.ratio < 1.0 { split.ratio } else { 0.5 };
    let [x, y, w, h] = rect;
    let (first, second) = match split.direction {
        rexrs::api::Direction::Vertical => ([x, y, w, h * r], [x, y + h * r, w, h * (1.0 - r)]),
        _ => ([x, y, w * r, h], [x + w * r, y, w * (1.0 - r), h]),
    };
    if let Some(before) = split.before.as_deref() {
        rects(before, first, out);
    }
    if let Some(after) = split.after.as_deref() {
        rects(after, second, out);
    }
}

/// The nearest block beside `from` in `direction`, overlapping it across the other axis.
fn neighbor(all: &[(String, [f64; 4])], from: &str, direction: &str) -> Option<String> {
    let [x, y, w, h] = all.iter().find(|(b, _)| b == from)?.1;
    const EPS: f64 = 1e-6;
    all.iter()
        .filter(|(b, _)| b != from)
        .filter_map(|(b, [cx, cy, cw, ch])| {
            let overlap = |a0: f64, a1: f64, b0: f64, b1: f64| (a1.min(b1) - a0.max(b0)).max(0.0);
            let (gap, shared) = match direction {
                "right" => (cx - (x + w), overlap(y, y + h, *cy, cy + ch)),
                "left" => (x - (cx + cw), overlap(y, y + h, *cy, cy + ch)),
                "down" => (cy - (y + h), overlap(x, x + w, *cx, cx + cw)),
                _ => (y - (cy + ch), overlap(x, x + w, *cx, cx + cw)),
            };
            (gap >= -EPS && shared > EPS).then_some((b, gap, shared))
        })
        .min_by(|a, b| a.1.total_cmp(&b.1).then(b.2.total_cmp(&a.2)))
        .map(|(b, ..)| b.clone())
}

async fn swap_pane(s: Arc<Shared>, v: Value) -> Outcome {
    let t = pane_target(&s, &v)?;
    let direction = direction(&v)?;
    let view = view_of(&s, &t.host, &t.session)?;
    let root = view
        .windows
        .iter()
        .find(|w| tiled_blocks(w).any(|b| b.block_id.as_deref() == Some(&t.block)))
        .and_then(tiled_layout)
        .ok_or("the pane is not in a tiled layout")?;
    let mut all = Vec::new();
    rects(root, [0.0, 0.0, 1.0, 1.0], &mut all);
    let other = neighbor(&all, &t.block, direction).ok_or_else(|| format!("no pane to the {direction}"))?;
    call(&t.control, "session.swap_blocks", json!({"session_id": t.session, "block_id": t.block, "other_block_id": other})).await
}

async fn list_dir(s: Arc<Shared>, v: Value) -> Outcome {
    let t = pane_target(&s, &v)?;
    let mut args = json!({"path": v["path"].as_str().unwrap_or_default(), "files": true});
    // Narrow a big folder to the names that start with `prefix` (ignoring case), as the server does.
    if let Some(prefix) = v["prefix"].as_str().filter(|p| !p.is_empty()) {
        args["prefix"] = json!(prefix);
    }
    let listing = t
        .control
        .call("com.superlogical.terminal.list_dir", json!({"session_id": t.session, "block_id": t.block, "args": args}))
        .await
        .map_err(|e| e.to_string())?;
    let entries: Vec<Value> = listing["entries"]
        .as_array()
        .into_iter()
        .flatten()
        .filter_map(|e| Some(json!({"name": e["name"].as_str()?, "dir": !e["file"].as_bool().unwrap_or(false)})))
        .collect();
    Ok(json!({
        "path": listing["path"],
        "status": listing["status"],
        "truncated": listing["truncated"],
        "entries": entries,
    }))
}

#[cfg(test)]
mod tests {
    use super::*;

    fn node(value: Value) -> ViewLayoutNode {
        serde_json::from_value(value).unwrap()
    }

    /// a | (b / c) with the outer split at 0.5.
    fn sample() -> ViewLayoutNode {
        node(json!({"split":{"split_id":"split:o","direction":"horizontal","ratio":0.5,
            "before":{"block_id":"a"},
            "after":{"split":{"split_id":"split:i","direction":"vertical","ratio":0.5,
                "before":{"block_id":"b"},"after":{"block_id":"c"}}}}}))
    }

    #[test]
    fn split_ratio_names_the_border_by_first_blocks() {
        let root = sample();
        let inner = find_split(&root, "split:i").unwrap();
        assert_eq!(
            resize_payload("s", inner, 0.3).unwrap(),
            json!({"session_id":"s","direction":"vertical","before_block_id":"b","after_block_id":"c","ratio":0.3})
        );
        let outer = find_split(&root, "split:o").unwrap();
        assert_eq!(resize_payload("s", outer, 0.7).unwrap()["after_block_id"], "b");
        assert!(find_split(&root, "split:x").is_none());
    }

    #[test]
    fn balance_gives_equal_shares_along_a_run() {
        // a | b | c as a | (b | c): the outer split must give a a third.
        let root = node(json!({"split":{"split_id":"o","direction":"horizontal","ratio":0.5,
            "before":{"block_id":"a"},
            "after":{"split":{"split_id":"i","direction":"horizontal","ratio":0.5,"before":{"block_id":"b"},"after":{"block_id":"c"}}}}}));
        let mut out = Vec::new();
        balanced("s", &root, &mut out);
        assert_eq!(out.len(), 1);
        assert!((out[0]["ratio"].as_f64().unwrap() - 1.0 / 3.0).abs() < 1e-9);
        // a | (b / c): already balanced.
        let mut out = Vec::new();
        balanced("s", &sample(), &mut out);
        assert!(out.is_empty());
    }

    #[test]
    fn neighbors_by_geometry() {
        let mut all = Vec::new();
        rects(&sample(), [0.0, 0.0, 1.0, 1.0], &mut all);
        assert_eq!(neighbor(&all, "a", "right").as_deref(), Some("b"));
        assert_eq!(neighbor(&all, "c", "left").as_deref(), Some("a"));
        assert_eq!(neighbor(&all, "b", "down").as_deref(), Some("c"));
        assert_eq!(neighbor(&all, "c", "up").as_deref(), Some("b"));
        assert_eq!(neighbor(&all, "a", "left"), None);
    }

    #[test]
    fn schemes() {
        assert_eq!(scheme_of("#011627"), Some("dark"));
        assert_eq!(scheme_of("fbf7f0"), Some("light"));
        assert_eq!(scheme_of("nope"), None);
    }
}
