//! One task per host: connect (ssh forward or direct), hello, `session.bootstrap`, then apply
//! events until the connection drops; reconnect with the client's token and a 2/4/8 s backoff
//! (docs/protocol.md §2.8, §3.1, §3.4).
use super::Shared;
use super::control::{CALL_TIMEOUT, ClientInfo, Control, ControlMsg, Event, Welcome};
use super::endpoint::Endpoint;
use super::link::Conn;
use super::ssh::{self, Tunnel};
use super::state::{self, ServerInfo, Session, Status};
use super::transport::{self, Base, CONTROL_PATH, CONTROL_PROTOCOL, DATA_PROTOCOL};
use anyhow::{Context, Result, anyhow};
use rexrs::api::session::{Bootstrap, BootstrapDataTicket, BootstrapResponse};
use serde_json::{Value, json};
use std::sync::Arc;
use std::time::{Duration, Instant};
use tokio::sync::{mpsc, oneshot};

pub enum HostCmd {
    Connect,
    Disconnect,
    /// The endpoint changed: drop the connection and start over.
    Restart,
    /// Close everything gracefully (data 1000, control close frame, kill ssh), then reply.
    Shutdown(oneshot::Sender<()>),
}

/// How a connection ended.
enum End {
    Shutdown(oneshot::Sender<()>),
    Disconnect,
    Restart,
    Failed { error: String, connected: bool },
}

/// What survives from one connection to the next.
#[derive(Default)]
struct Memory {
    token: Option<String>,
    remote_socket: Option<String>,
}

pub async fn run(shared: Arc<Shared>, host_id: String, mut cmds: mpsc::UnboundedReceiver<HostCmd>, connect: bool) {
    let mut want = connect;
    let mut memory = Memory::default();
    let mut ever_connected = false;
    let mut failures = 0u32;
    loop {
        if !want {
            match cmds.recv().await {
                Some(cmd @ (HostCmd::Connect | HostCmd::Restart)) => {
                    if matches!(cmd, HostCmd::Restart) {
                        restart(&shared, &host_id, &mut memory);
                    }
                    want = true;
                    failures = 0;
                    continue;
                }
                Some(HostCmd::Disconnect) => continue,
                Some(HostCmd::Shutdown(done)) => {
                    let _ = done.send(());
                    return;
                }
                None => return,
            }
        }
        let status = if ever_connected { Status::Reconnecting } else { Status::Connecting };
        shared.set_status(&host_id, status, None);
        let end = connection(&shared, &host_id, &mut cmds, &mut memory).await;
        shared.disconnected(&host_id);
        match end {
            End::Shutdown(done) => {
                let _ = done.send(());
                return;
            }
            End::Disconnect => {
                want = false;
                ever_connected = false;
                memory.token = None;
                shared.set_status(&host_id, Status::Offline, Some(None));
            }
            End::Restart => {
                failures = 0;
                ever_connected = false;
                restart(&shared, &host_id, &mut memory);
            }
            End::Failed { error, connected } => {
                shared.emitter.log("warn", "host", format!("{host_id}: {error}"));
                if connected {
                    ever_connected = true;
                    failures = 0;
                }
                failures += 1;
                if !ever_connected && failures >= 3 {
                    // Never reached: show the reason with Retry instead of trying forever.
                    shared.set_status(&host_id, Status::Error, Some(Some(error)));
                    want = false;
                    continue;
                }
                let status = if ever_connected { Status::Reconnecting } else { Status::Connecting };
                shared.set_status(&host_id, status, Some(Some(error)));
                let delay = Duration::from_secs([2, 4, 8][(failures as usize - 1).min(2)]);
                tokio::select! {
                    _ = tokio::time::sleep(delay) => {}
                    cmd = cmds.recv() => match cmd {
                        Some(HostCmd::Connect) => failures = 0,
                        Some(HostCmd::Restart) => {
                            failures = 0;
                            ever_connected = false;
                            restart(&shared, &host_id, &mut memory);
                        }
                        Some(HostCmd::Disconnect) => {
                            want = false;
                            ever_connected = false;
                            memory.token = None;
                            shared.set_status(&host_id, Status::Offline, Some(None));
                        }
                        Some(HostCmd::Shutdown(done)) => {
                            let _ = done.send(());
                            return;
                        }
                        None => return,
                    },
                }
            }
        }
    }
}

/// The endpoint changed, perhaps to another server: nothing carries over, claims included.
fn restart(shared: &Shared, host_id: &str, memory: &mut Memory) {
    *memory = Memory::default();
    shared.state.lock().forget_claims(host_id);
}

/// Open the transport and the control connection: (base for sockets, ssh tunnel, control).
async fn open(
    shared: &Arc<Shared>,
    endpoint: &str,
    memory: &mut Memory,
    sink: mpsc::UnboundedSender<ControlMsg>,
) -> Result<(Base, Option<Tunnel>, Control, Welcome)> {
    let (base, tunnel) = match Endpoint::parse(endpoint)? {
        Endpoint::Ssh(target) => {
            let ssh_path = shared.ssh_path();
            let remote = match &memory.remote_socket {
                Some(socket) => socket.clone(),
                None => ssh::resolve_socket(&ssh_path, &target).await?,
            };
            let tunnel = match Tunnel::open(&ssh_path, &target, &remote).await {
                Ok(tunnel) => tunnel,
                Err(error) => {
                    // The socket may have moved; look again next time.
                    memory.remote_socket = None;
                    return Err(error);
                }
            };
            memory.remote_socket = Some(remote);
            (Base::Loopback(tunnel.port), Some(tunnel))
        }
        Endpoint::Direct(url) => (Base::Direct(url), None),
    };
    let socket = transport::connect(&base, CONTROL_PATH, CONTROL_PROTOCOL).await?;
    let info = ClientInfo {
        name: shared.options.client_name.clone(),
        version: shared.options.client_version.clone(),
        label: shared.options.label.clone(),
    };
    let (control, welcome) = match Control::open(socket, &info, memory.token.as_deref(), sink.clone(), shared.emitter.clone()).await {
        Ok(opened) => opened,
        Err(error) if memory.token.is_some() => {
            // A stale token can be refused; start a new client instead.
            shared.emitter.log("info", "host", format!("resume refused ({error:#}); starting a new client"));
            memory.token = None;
            let socket = transport::connect(&base, CONTROL_PATH, CONTROL_PROTOCOL).await?;
            Control::open(socket, &info, None, sink, shared.emitter.clone()).await?
        }
        Err(error) => return Err(error),
    };
    Ok((base, tunnel, control, welcome))
}

async fn connection(shared: &Arc<Shared>, host_id: &str, cmds: &mut mpsc::UnboundedReceiver<HostCmd>, memory: &mut Memory) -> End {
    let Some(endpoint) = shared.state.lock().host(host_id).map(|h| h.endpoint.clone()) else {
        return End::Failed { error: "host removed".into(), connected: false };
    };
    let (sink, mut events) = mpsc::unbounded_channel();
    // Opening can take seconds (ssh); stay responsive to commands meanwhile.
    let opened = {
        let opening = open(shared, &endpoint, memory, sink);
        tokio::pin!(opening);
        loop {
            tokio::select! {
                opened = &mut opening => break opened,
                cmd = cmds.recv() => match cmd {
                    // Already connecting: this attempt is the one asked for.
                    Some(HostCmd::Connect) => continue,
                    Some(HostCmd::Shutdown(done)) => return End::Shutdown(done),
                    Some(HostCmd::Restart) => return End::Restart,
                    Some(HostCmd::Disconnect) | None => return End::Disconnect,
                },
            }
        }
    };
    let (base, mut tunnel, control, welcome) = match opened {
        Ok(opened) => opened,
        Err(error) => return End::Failed { error: format!("{error:#}"), connected: false },
    };
    memory.token = welcome.reconnect_token.clone();
    if welcome.resumed {
        shared.emitter.log("info", "host", format!("{host_id}: resumed client {}", welcome.client_id));
    }

    let floor = match bootstrap(shared, host_id, &control, &welcome).await {
        Ok(seq) => seq,
        Err(error) => {
            // Part of the setup may have happened: close and forget the token (§2.8).
            control.close();
            memory.token = None;
            return End::Failed { error: format!("session.bootstrap: {error:#}"), connected: false };
        }
    };
    {
        let mut st = shared.state.lock();
        if let Some(index) = st.host_index(host_id) {
            st.hosts[index].link.set_conn(Some(Conn { control: control.clone(), base, client_id: welcome.client_id.clone() }));
            st.hosts[index].status = Status::Connected;
            st.hosts[index].error = None;
            for block in st.hosts[index].blocks.values() {
                if let Some(link) = &block.link {
                    link.refresh_paused();
                }
            }
            state::reconcile(shared, &mut st, index);
            state::ensure_current_attached(shared, &mut st);
            state::refresh_views(shared, &mut st.hosts[index], false);
        }
    }
    shared.touch();
    read_server_version(shared, host_id, &control);

    loop {
        tokio::select! {
            message = events.recv() => match message {
                Some(ControlMsg::Event(event)) if event.seq > floor => {
                    let name = event.name.clone();
                    let handled = std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| handle_event(shared, host_id, &control, event)));
                    if handled.is_err() {
                        shared.emitter.log("error", "host", format!("{host_id}: handling {name} panicked"));
                    }
                }
                Some(ControlMsg::Event(_)) => {}
                Some(ControlMsg::Closed(reason)) => return End::Failed { error: reason, connected: true },
                None => return End::Failed { error: "control connection ended".into(), connected: true },
            },
            cmd = cmds.recv() => {
                let end = match cmd {
                    Some(HostCmd::Connect) => continue,
                    Some(HostCmd::Disconnect) | None => End::Disconnect,
                    Some(HostCmd::Restart) => End::Restart,
                    Some(HostCmd::Shutdown(done)) => End::Shutdown(done),
                };
                // Leaving on purpose: end the streams and the client cleanly, then the forward.
                graceful_close(shared, host_id, &control).await;
                if let Some(tunnel) = &mut tunnel {
                    tunnel.kill();
                }
                return end;
            }
            reason = tunnel_closed(&mut tunnel) => return End::Failed { error: reason, connected: true },
        }
    }
}

async fn tunnel_closed(tunnel: &mut Option<Tunnel>) -> String {
    match tunnel {
        Some(tunnel) => tunnel.closed().await,
        None => std::future::pending().await,
    }
}

/// `session.bootstrap` (§3.1 step 2) and apply it. Returns the event floor (response seq).
async fn bootstrap(shared: &Arc<Shared>, host_id: &str, control: &Control, welcome: &Welcome) -> Result<u64> {
    // Re-attach what we streamed before a reconnect (and the current session).
    let attach: Vec<String> = {
        let st = shared.state.lock();
        let host = st.host(host_id).context("host removed")?;
        let mut ids: Vec<String> = host.sessions.iter().filter(|s| s.attached).map(|s| s.id.clone()).collect();
        if let Some((h, s)) = &st.current
            && h == host_id
            && !ids.contains(s)
        {
            ids.push(s.clone());
        }
        ids
    };
    let mut request = Bootstrap::new("all").data_tickets([BootstrapDataTicket::new("com.superlogical.terminal", "terminal").subprotocol(DATA_PROTOCOL)]);
    if !attach.is_empty() {
        request = request.attach_session_ids(attach);
    }
    let reply = control
        .call_timeout("session.bootstrap", serde_json::to_value(&request)?, CALL_TIMEOUT)
        .await
        .map_err(|e| anyhow!("{e}"))?;
    let boot: BootstrapResponse = serde_json::from_value(reply.value).context("bootstrap result")?;

    let mut st = shared.state.lock();
    let index = st.host_index(host_id).context("host removed")?;
    let host = &mut st.hosts[index];
    host.client_id = Some(if boot.client_id.is_empty() { welcome.client_id.clone() } else { boot.client_id.clone() });
    host.server = Some(ServerInfo {
        version: host.server.as_ref().and_then(|s| s.version.clone()),
        api: welcome.api_revision,
        instance: Some(boot.server_status.instance_id.clone()),
        // The server's own name on the tailnet, when built-in Tailscale knows it.
        label: boot.server_status.tailscale.as_ref().and_then(|t| t.dns_name.as_deref()).and_then(|d| d.split('.').next()).map(str::to_owned),
    });
    // The session list, keeping views we hold for sessions that still exist.
    let mut old: Vec<Session> = std::mem::take(&mut host.sessions);
    for entry in &boot.attach_all.sessions {
        let mut session = match old.iter().position(|s| s.id == entry.session_id) {
            Some(i) => old.swap_remove(i),
            None => Session::new(entry.session_id.clone(), entry.label.clone()),
        };
        session.label = entry.label.clone();
        session.attached = false;
        session.attaching = false;
        session.fetching = 0;
        host.sessions.push(session);
    }
    for gone in old {
        host.drop_session(shared, &gone.id);
    }
    let attached = boot.selected_session.into_iter().map(|s| (s.session, s.view)).chain(boot.attached_sessions.into_iter().map(|s| (s.session, s.view)));
    for (entry, view) in attached {
        if let Some(session) = host.session_mut(&entry.session_id) {
            session.attached = true;
            // A new connection's view is authoritative even if its revision is unchanged.
            session.view = None;
            state::apply_view(session, view);
        }
    }
    let now = Instant::now();
    host.tickets = boot
        .data_tickets
        .into_iter()
        .map(|t| (t.block_id, (t.ticket, now + Duration::from_millis(t.expires_in_ms.saturating_sub(2000)))))
        .collect();
    for block in host.blocks.values_mut() {
        block.meta_read = false;
        block.owner_rev = 0;
    }
    Ok(reply.seq)
}

fn read_server_version(shared: &Arc<Shared>, host_id: &str, control: &Control) {
    let shared2 = shared.clone();
    let host_id = host_id.to_owned();
    let _ = control.send_then("server.version", json!({}), move |result| {
        let Ok(reply) = result else { return };
        let mut st = shared2.state.lock();
        if let Some(server) = st.host_mut(&host_id).and_then(|h| h.server.as_mut()) {
            server.version = reply.value["rex_version"].as_str().map(str::to_owned);
        }
        drop(st);
        shared2.touch();
    });
}

/// Server status changes (built-in Tailscale) update the host's server label.
fn read_server_status(shared: &Arc<Shared>, host_id: &str, control: &Control) {
    let shared2 = shared.clone();
    let host_id = host_id.to_owned();
    let _ = control.send_then("server.status", json!({}), move |result| {
        let Ok(reply) = result else { return };
        let label = reply.value["tailscale"]["dns_name"].as_str().and_then(|d| d.split('.').next()).map(str::to_owned);
        let mut st = shared2.state.lock();
        if let Some(server) = st.host_mut(&host_id).and_then(|h| h.server.as_mut()) {
            server.label = label;
            server.instance = reply.value["instance_id"].as_str().map(str::to_owned).or(server.instance.take());
        }
        drop(st);
        shared2.touch();
    });
}

/// Re-list sessions after `session_created` / `session_moved` (§3.4).
fn relist(shared: &Arc<Shared>, host_id: &str, control: &Control) {
    let shared2 = shared.clone();
    let host_id = host_id.to_owned();
    let _ = control.send_then("session.attach_all", json!({"event_scope": "all"}), move |result| {
        let Ok(reply) = result else { return };
        let mut st = shared2.state.lock();
        let Some(index) = st.host_index(&host_id) else { return };
        let host = &mut st.hosts[index];
        let listed: Vec<(String, String)> = reply.value["sessions"]
            .as_array()
            .into_iter()
            .flatten()
            .filter_map(|s| Some((s["session_id"].as_str()?.to_owned(), s["label"].as_str().unwrap_or_default().to_owned())))
            .collect();
        let mut old = std::mem::take(&mut host.sessions);
        for (id, label) in listed {
            let mut session = match old.iter().position(|s| s.id == id) {
                Some(i) => old.remove(i),
                None => Session::new(id, label.clone()),
            };
            session.label = label;
            host.sessions.push(session);
        }
        for gone in old {
            host.drop_session(&shared2, &gone.id);
        }
        state::refresh_views(&shared2, host, true);
        state::reconcile(&shared2, &mut st, index);
        state::ensure_current_attached(&shared2, &mut st);
        drop(st);
        shared2.touch();
    });
}

fn handle_event(shared: &Arc<Shared>, host_id: &str, control: &Control, event: Event) {
    let mut st = shared.state.lock();
    let Some(index) = st.host_index(host_id) else { return };
    let session = event.session.as_deref();
    match (event.name.as_str(), session) {
        ("session_created" | "session_moved", _) => {
            drop(st);
            relist(shared, host_id, control);
            return;
        }
        ("session_destroyed", _) => {
            let Some(id) = event.payload["session_id"].as_str().or(session) else { return };
            st.hosts[index].drop_session(shared, id);
            state::reconcile(shared, &mut st, index);
            state::ensure_current_attached(shared, &mut st);
        }
        ("session_label_changed", Some(id)) => {
            let label = event.payload["label"].as_str().unwrap_or_default().to_owned();
            if let Some(session) = st.hosts[index].session_mut(id) {
                session.label = label;
            }
        }
        ("session_view_changed", Some(id)) => {
            let revision = event.payload["revision"].as_u64().unwrap_or(0);
            state::fetch_view(shared, &mut st.hosts[index], id, revision);
            return;
        }
        ("block_owner_changed", Some(id)) => {
            let host = &mut st.hosts[index];
            let Some(block_id) = event.payload["block_id"].as_str() else { return };
            let owner = event.payload["owner"].as_str().map(str::to_owned);
            let ours = owner.is_some() && owner == host.client_id;
            let revision = host.session(id).map_or(0, Session::revision);
            if let Some(block) = host.blocks.get_mut(block_id) {
                block.owner = owner;
                block.owner_rev = block.owner_rev.max(revision);
                if let Some(link) = &block.link {
                    link.set_owned(ours);
                }
            }
        }
        ("block_closed", Some(_)) => {
            let host = &mut st.hosts[index];
            if let Some(block) = event.payload["block_id"].as_str().and_then(|b| host.blocks.get_mut(b))
                && let Some(stream) = block.stream.take()
            {
                stream.drop_stream();
            }
            return;
        }
        ("block_event", Some(id)) => {
            let notification = state::apply_block_event(&mut st, index, id, &event.payload);
            drop(st);
            if let Some(notification) = notification {
                notification.emit(shared);
            }
            shared.touch();
            return;
        }
        ("tailscale_status_changed", _) => {
            drop(st);
            read_server_status(shared, host_id, control);
            return;
        }
        ("client_perform_requested", _) => {
            // Remote control of this client is not offered: refuse within the 5 s budget.
            if let Some(call_id) = event.payload["call_id"].as_str() {
                let _ = control.fire(
                    "client.reply",
                    json!({"call_id": call_id, "error": {"code": "refused", "message": "Windex does not accept remote control"}}),
                );
            }
            return;
        }
        _ => return,
    }
    drop(st);
    shared.touch();
}

/// A deliberate disconnect: end data streams with 1000 and close the control connection with a
/// close frame, so the server forgets this client at once (its ownerships pass on).
async fn graceful_close(shared: &Arc<Shared>, host_id: &str, control: &Control) {
    let closing: Vec<_> = {
        let mut st = shared.state.lock();
        st.host_mut(host_id)
            .map(|h| h.blocks.values_mut().filter_map(|b| b.stream.take()).map(|s| s.close()).collect())
            .unwrap_or_default()
    };
    let _ = tokio::time::timeout(Duration::from_millis(800), futures_util::future::join_all(closing)).await;
    control.close();
    let deadline = Instant::now() + Duration::from_millis(500);
    while !control.is_closed() && Instant::now() < deadline {
        tokio::time::sleep(Duration::from_millis(10)).await;
    }
}

/// A command's view of a host: its control connection, or why there is none.
pub fn control_of(shared: &Shared, host_id: &str) -> Result<Control, String> {
    let st = shared.state.lock();
    let host = st.host(host_id).ok_or_else(|| format!("unknown host {host_id}"))?;
    host.link.control().ok_or_else(|| format!("{} is not connected", host.name))
}

/// Server-status for the UI's host details.
pub async fn server_status(control: &Control) -> Result<Value, String> {
    control.call("server.status", json!({})).await.map_err(|e| e.to_string())
}
