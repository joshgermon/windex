//! Rex server connections: hosts, sessions, views and the data channels that feed remote panes.
//!
//! Module map:
//! - `endpoint`  — endpoint URLs (`ssh://`, `https://`, ...)
//! - `ssh`       — `ssh.exe -L` loopback forwards, remote socket discovery
//! - `transport` — WebSocket connections (TCP / TLS, subprotocol check)
//! - `control`   — the pipelined control channel (hello, requests, events)
//! - `host`      — one task per host: connect, bootstrap, events, reconnect
//! - `state`     — hosts/sessions/blocks, pane reconciliation
//! - `model`     — the Model JSON for the UI, layout normalization
//! - `link`      — per-pane input/ownership/size bridge (`PaneInput` for server blocks)
//! - `data`      — terminal data channels (tickets, handshake, resume)
//! - `stream`    — snapshot boundary + Rex APC filter
//! - `keys`      — W3C key codes → Rex `client.key` payloads
//! - `activity`  — titles, program icons, status words, progress
//! - `commands`  — the contract's commands
//! - `transfer`  — uploads and downloads over `rex.data.bytes.v1`
//! - `discover`  — tailnet discovery
//!
//! Threading: everything runs on the core's tokio runtime. The FFI thread only queues: input
//! goes to a control connection's writer through an unbounded channel, commands spawn tasks.
//! State lives behind one mutex that is never held across an await.
mod activity;
mod commands;
mod control;
mod data;
mod discover;
mod endpoint;
mod host;
mod keys;
mod link;
mod model;
mod ssh;
mod state;
mod stream;
mod transfer;
mod transport;

use crate::core::{Options, Panes};
use crate::events::{Emitter, EventKind};
use parking_lot::Mutex;
use serde_json::Value;
use state::{State, Status};
use std::sync::Arc;
use std::time::{Duration, Instant};
use tokio::sync::{Notify, oneshot};

/// At most this many model snapshots per second reach the UI.
const MODEL_INTERVAL: Duration = Duration::from_millis(33);

/// What every remote task shares.
pub(crate) struct Shared {
    pub emitter: Emitter,
    pub options: Options,
    pub panes: Arc<Panes>,
    pub runtime: tokio::runtime::Handle,
    pub state: Mutex<State>,
    /// The terminal theme the UI declared, shared by every host's links.
    pub theme: Arc<Mutex<Option<Value>>>,
    dirty: Notify,
}

impl Shared {
    /// The model changed: a snapshot goes out soon (coalesced).
    pub fn touch(&self) {
        self.dirty.notify_one();
    }

    /// Set a host's status; `error`: `None` keeps the current one.
    pub fn set_status(&self, host: &str, status: Status, error: Option<Option<String>>) {
        let mut st = self.state.lock();
        if let Some(h) = st.host_mut(host) {
            h.status = status;
            if let Some(error) = error {
                h.error = error;
            }
        }
        drop(st);
        self.touch();
    }

    /// A host's connection ended: its panes pause, its streams wait for the next one. Claims
    /// stay: the server gives a resumed client its blocks back unless another client took them
    /// meanwhile, and then they are that client's until a focus gain or input here.
    pub fn disconnected(&self, host: &str) {
        let mut st = self.state.lock();
        if let Some(h) = st.host_mut(host) {
            h.link.set_conn(None);
            h.tickets.clear();
            for session in &mut h.sessions {
                session.attaching = false;
            }
            for block in h.blocks.values() {
                if let Some(link) = &block.link {
                    link.refresh_paused();
                }
            }
        }
        drop(st);
        self.touch();
    }

    /// A pooled bootstrap ticket for a block, if one is still valid (single use).
    pub fn take_ticket(&self, host: &str, block: &str) -> Option<String> {
        let mut st = self.state.lock();
        let (ticket, expires) = st.host_mut(host)?.tickets.remove(block)?;
        (Instant::now() < expires).then_some(ticket)
    }

    pub fn ssh_path(&self) -> String {
        self.options.ssh_path.clone().unwrap_or_else(|| r"C:\Windows\System32\OpenSSH\ssh.exe".into())
    }

    pub fn tailscale_path(&self) -> String {
        self.options.tailscale_path.clone().unwrap_or_else(|| r"C:\Program Files\Tailscale\tailscale.exe".into())
    }
}

/// The remote half of the core (`Core::remote`).
pub struct Remote {
    shared: Arc<Shared>,
}

impl Remote {
    pub fn new(emitter: Emitter, options: Options, panes: Arc<Panes>, runtime: tokio::runtime::Handle) -> Self {
        let shared = Arc::new(Shared {
            emitter,
            options,
            panes,
            runtime: runtime.clone(),
            state: Mutex::new(State::default()),
            theme: Arc::new(Mutex::new(None)),
            dirty: Notify::new(),
        });
        runtime.spawn(publish_models(shared.clone()));
        Self { shared }
    }

    /// Handle a contract command; false if it isn't one of ours.
    pub fn command(&self, req: u64, cmd: &str, value: &Value) -> bool {
        commands::dispatch(&self.shared, req, cmd, value)
    }

    /// Disconnect every host: data streams end with 1000, control connections close with a
    /// close frame, ssh forwards are killed. Waits up to 2 s.
    pub fn shutdown(&self) {
        let waits: Vec<oneshot::Receiver<()>> = {
            let st = self.shared.state.lock();
            // Transfers close their streams with 1000 while the hosts shut down.
            for transfer in &st.transfers {
                transfer.cancel();
            }
            st.hosts
                .iter()
                .filter_map(|h| {
                    let (tx, rx) = oneshot::channel();
                    h.tx.send(host::HostCmd::Shutdown(tx)).ok().map(|_| rx)
                })
                .collect()
        };
        if !waits.is_empty() {
            self.shared
                .runtime
                .block_on(async { tokio::time::timeout(Duration::from_secs(2), futures_util::future::join_all(waits)).await })
                .ok();
        }
        let mut st = self.shared.state.lock();
        for host in &mut st.hosts {
            host.link.set_conn(None);
            host.retire_all(&self.shared);
        }
        st.hosts.clear();
    }
}

/// Emit the model whenever it changed, at most every `MODEL_INTERVAL`.
async fn publish_models(shared: Arc<Shared>) {
    loop {
        shared.dirty.notified().await;
        let json = {
            let mut st = shared.state.lock();
            st.revision += 1;
            model::build(&st).to_string()
        };
        shared.emitter.emit(EventKind::Model, 0, &json);
        tokio::time::sleep(MODEL_INTERVAL).await;
    }
}
