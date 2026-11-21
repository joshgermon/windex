//! The control channel (docs/protocol.md §2): hello/WELCOME, then pipelined JSON requests matched
//! to responses by id, and server events delivered in wire order.
//!
//! Unlike the vendored SDK client (one request in flight), any number of requests may be
//! outstanding: keys, pastes and mouse events go out as fast as the user produces them and the
//! server runs the ordered methods in arrival order (§2.4). Sending never blocks: requests are
//! queued to a writer task, so the FFI caller never waits on the network.
use super::transport::Socket;
use crate::events::Emitter;
use anyhow::{Context, Result, bail, ensure};
use futures_util::{SinkExt, StreamExt};
use parking_lot::Mutex;
use serde_json::{Value, json};
use std::collections::HashMap;
use std::sync::Arc;
use std::sync::atomic::{AtomicBool, AtomicU64, Ordering};
use std::time::Duration;
use tokio::sync::{Notify, mpsc, oneshot};
use tokio_tungstenite::tungstenite::Message;
use tokio_tungstenite::tungstenite::protocol::CloseFrame;
use tokio_tungstenite::tungstenite::protocol::frame::coding::CloseCode;

pub const API_REVISION: u32 = 13;
pub const MIN_API_REVISION: u32 = 10;
/// Ordinary requests: 8 s, never retried (§1.3).
pub const CALL_TIMEOUT: Duration = Duration::from_secs(8);
/// We ping every 30 s; the server answers. Silence for this long means the link is dead.
const IDLE_LIMIT: Duration = Duration::from_secs(75);
const PING_EVERY: Duration = Duration::from_secs(30);

/// A server event, unwrapped from its `session_event` envelope when it has one.
#[derive(Debug, Clone)]
pub struct Event {
    pub seq: u64,
    pub session: Option<String>,
    pub name: String,
    pub payload: Value,
}

#[derive(Debug, Clone)]
pub struct Reply {
    pub seq: u64,
    pub value: Value,
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct CallError {
    /// The server's error code (`not_found`, `conflict`, ...) or a local one (`disconnected`,
    /// `timeout`, `too_large`).
    pub code: String,
    pub message: String,
}

impl CallError {
    fn local(code: &str, message: impl Into<String>) -> Self {
        Self { code: code.into(), message: message.into() }
    }
}

impl std::fmt::Display for CallError {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        f.write_str(if self.message.is_empty() { &self.code } else { &self.message })
    }
}

impl std::error::Error for CallError {}

pub type CallResult = Result<Reply, CallError>;

/// What the reader hands the host task, in wire order.
pub enum ControlMsg {
    Event(Event),
    Closed(String),
}

enum Pending {
    Waiter(oneshot::Sender<CallResult>),
    Callback(Box<dyn FnOnce(CallResult) + Send>),
}

enum Outgoing {
    Text(String),
    Close,
}

/// Who we are, for the hello.
#[derive(Debug, Clone)]
pub struct ClientInfo {
    pub name: String,
    pub version: String,
    pub label: String,
}

#[derive(Debug, Clone)]
pub struct Welcome {
    pub client_id: String,
    pub reconnect_token: Option<String>,
    pub resumed: bool,
    pub api_revision: u32,
}

/// A live control connection. Cheap to clone; every clone sends on the same socket.
#[derive(Clone)]
pub struct Control {
    inner: Arc<Inner>,
}

struct Inner {
    out: mpsc::UnboundedSender<Outgoing>,
    pending: Mutex<HashMap<u64, Pending>>,
    next_id: AtomicU64,
    closed: AtomicBool,
    stop: Notify,
    max_message_bytes: usize,
    emitter: Emitter,
}

fn hello(info: &ClientInfo, token: Option<&str>) -> String {
    // The server drops labels over 64 bytes; keep whole characters.
    let mut label = info.label.clone();
    while label.len() > 64 {
        label.pop();
    }
    let mut hello = json!({
        "type": "hello",
        "api_revision": API_REVISION,
        "api_min_revision": MIN_API_REVISION,
        "client": {"kind": "app", "platform": "windows", "name": info.name, "version": info.version, "label": label},
    });
    if let Some(token) = token {
        hello["reconnect_token"] = json!(token);
    }
    hello.to_string()
}

fn parse_welcome(text: &str) -> Result<(Welcome, usize)> {
    let value: Value = serde_json::from_str(text).context("invalid WELCOME")?;
    ensure!(value["type"] == "welcome", "the server's first message was not a welcome");
    ensure!(value["protocol"] == "rex.control.v1", "unexpected control protocol {}", value["protocol"]);
    let api_revision = value["api_revision"].as_u64().unwrap_or(0) as u32;
    ensure!(
        (MIN_API_REVISION..=API_REVISION).contains(&api_revision),
        "the server negotiated API revision {api_revision}; Windex supports {MIN_API_REVISION}–{API_REVISION}"
    );
    let client = &value["client"];
    let welcome = Welcome {
        client_id: client["id"].as_str().unwrap_or_default().to_owned(),
        reconnect_token: client["reconnect_token"].as_str().map(str::to_owned),
        resumed: client["resumed"].as_bool().unwrap_or(false),
        api_revision,
    };
    let limit = value["limits"]["max_message_bytes"].as_u64().unwrap_or(65536).max(65536) as usize;
    Ok((welcome, limit))
}

fn close_reason(frame: Option<CloseFrame>) -> String {
    match frame {
        Some(frame) if frame.reason.is_empty() => format!("closed by the server ({})", u16::from(frame.code)),
        Some(frame) => format!("closed by the server ({}: {})", u16::from(frame.code), frame.reason),
        None => "closed by the server".into(),
    }
}

impl Control {
    /// Say hello on a fresh control socket and start its reader and writer. Messages for the
    /// host task go to `sink`.
    pub async fn open(
        mut socket: Socket,
        info: &ClientInfo,
        token: Option<&str>,
        sink: mpsc::UnboundedSender<ControlMsg>,
        emitter: Emitter,
    ) -> Result<(Control, Welcome)> {
        socket.send(Message::Text(hello(info, token).into())).await.context("send hello")?;
        let first = tokio::time::timeout(CALL_TIMEOUT, async {
            loop {
                match socket.next().await {
                    Some(Ok(Message::Text(text))) => return Ok(text.to_string()),
                    Some(Ok(Message::Close(frame))) => bail!("{}", close_reason(frame)),
                    Some(Ok(_)) => continue,
                    Some(Err(error)) => return Err(anyhow::Error::from(error)),
                    None => bail!("the server closed the connection before WELCOME"),
                }
            }
        })
        .await
        .context("Rex control WELCOME timed out")??;
        let (welcome, max_message_bytes) = parse_welcome(&first)?;
        let (out, out_rx) = mpsc::unbounded_channel();
        let inner = Arc::new(Inner {
            out,
            pending: Mutex::new(HashMap::new()),
            next_id: AtomicU64::new(0),
            closed: AtomicBool::new(false),
            stop: Notify::new(),
            max_message_bytes,
            emitter,
        });
        let (write, read) = socket.split();
        tokio::spawn(writer(write, out_rx, inner.clone()));
        tokio::spawn(reader(read, inner.clone(), sink));
        Ok((Control { inner }, welcome))
    }

    pub fn is_closed(&self) -> bool {
        self.inner.closed.load(Ordering::Acquire)
    }

    /// The largest request the server accepts, from WELCOME `limits.max_message_bytes`.
    pub fn max_message_bytes(&self) -> usize {
        self.inner.max_message_bytes
    }

    fn send(&self, method: &str, payload: &Value, pending: Pending) -> Result<(), CallError> {
        if self.is_closed() {
            return Err(CallError::local("disconnected", "not connected to the Rex server"));
        }
        let id = self.inner.next_id.fetch_add(1, Ordering::Relaxed) + 1;
        let text = json!({"type": "request", "id": id.to_string(), "method": method, "payload": payload}).to_string();
        if text.len() > self.inner.max_message_bytes {
            return Err(CallError::local("too_large", format!("{method} is larger than the server accepts")));
        }
        self.inner.pending.lock().insert(id, pending);
        if self.inner.out.send(Outgoing::Text(text)).is_err() {
            self.inner.pending.lock().remove(&id);
            return Err(CallError::local("disconnected", "not connected to the Rex server"));
        }
        Ok(())
    }

    /// Call and wait for the result (8 s).
    pub async fn call(&self, method: &str, payload: Value) -> Result<Value, CallError> {
        self.call_timeout(method, payload, CALL_TIMEOUT).await.map(|r| r.value)
    }

    pub async fn call_timeout(&self, method: &str, payload: Value, timeout: Duration) -> CallResult {
        let (tx, rx) = oneshot::channel();
        self.send(method, &payload, Pending::Waiter(tx))?;
        match tokio::time::timeout(timeout, rx).await {
            Ok(Ok(result)) => result,
            Ok(Err(_)) => Err(CallError::local("disconnected", "the connection to the Rex server was lost")),
            Err(_) => Err(CallError::local("timeout", format!("{method} timed out"))),
        }
    }

    /// Send without waiting; `then` runs on the reader task with the outcome.
    pub fn send_then(&self, method: &str, payload: Value, then: impl FnOnce(CallResult) + Send + 'static) -> Result<(), CallError> {
        self.send(method, &payload, Pending::Callback(Box::new(then)))
    }

    /// Send without waiting; a failure is logged.
    pub fn fire(&self, method: &str, payload: Value) -> Result<(), CallError> {
        let emitter = self.inner.emitter.clone();
        let name = method.to_owned();
        self.send_then(method, payload, move |result| {
            if let Err(error) = result {
                emitter.log("warn", "control", format!("{name} failed: {error}"));
            }
        })
    }

    /// Close with a close frame: the server forgets this client at once.
    pub fn close(&self) {
        let _ = self.inner.out.send(Outgoing::Close);
    }
}

impl Inner {
    fn finish(&self) {
        self.closed.store(true, Ordering::Release);
        self.stop.notify_waiters();
        let pending: Vec<Pending> = self.pending.lock().drain().map(|(_, p)| p).collect();
        for entry in pending {
            let lost = Err(CallError::local("disconnected", "the connection to the Rex server was lost"));
            match entry {
                Pending::Waiter(tx) => {
                    let _ = tx.send(lost);
                }
                Pending::Callback(f) => run_callback(&self.emitter, f, lost),
            }
        }
    }
}

async fn writer(
    mut write: futures_util::stream::SplitSink<Socket, Message>,
    mut out: mpsc::UnboundedReceiver<Outgoing>,
    inner: Arc<Inner>,
) {
    let mut ping = tokio::time::interval(PING_EVERY);
    ping.tick().await;
    loop {
        let stopped = inner.stop.notified();
        tokio::pin!(stopped);
        if inner.closed.load(Ordering::Acquire) {
            break;
        }
        let result = tokio::select! {
            message = out.recv() => match message {
                Some(Outgoing::Text(text)) => write.send(Message::Text(text.into())).await,
                Some(Outgoing::Close) | None => {
                    let frame = CloseFrame { code: CloseCode::Normal, reason: "".into() };
                    let _ = write.send(Message::Close(Some(frame))).await;
                    break;
                }
            },
            _ = ping.tick() => write.send(Message::Ping(Default::default())).await,
            _ = &mut stopped => break,
        };
        if result.is_err() {
            break;
        }
    }
    let _ = write.close().await;
    inner.finish();
}

async fn reader(
    mut read: futures_util::stream::SplitStream<Socket>,
    inner: Arc<Inner>,
    sink: mpsc::UnboundedSender<ControlMsg>,
) {
    let reason = loop {
        let stopped = inner.stop.notified();
        tokio::pin!(stopped);
        if inner.closed.load(Ordering::Acquire) {
            break "connection closed".to_owned();
        }
        let next = tokio::select! {
            next = tokio::time::timeout(IDLE_LIMIT, read.next()) => next,
            _ = &mut stopped => break "connection closed".to_owned(),
        };
        match next {
            Err(_) => break "the Rex server stopped answering".to_owned(),
            Ok(None) => break "the connection to the Rex server ended".to_owned(),
            Ok(Some(Err(error))) => break format!("connection error: {error}"),
            Ok(Some(Ok(Message::Close(frame)))) => break close_reason(frame),
            Ok(Some(Ok(Message::Text(text)))) => dispatch(&inner, &sink, &text),
            Ok(Some(Ok(_))) => {}
        }
    };
    inner.finish();
    let _ = sink.send(ControlMsg::Closed(reason));
}

/// Responses complete their requests right here (callbacks run on this task); events go to
/// the host task.
fn dispatch(inner: &Inner, sink: &mpsc::UnboundedSender<ControlMsg>, text: &str) {
    let Ok(mut message) = serde_json::from_str::<Value>(text) else {
        inner.emitter.log("warn", "control", "unparseable control message");
        return;
    };
    match message["type"].as_str() {
        Some("response") => {
            let Some(id) = message["id"].as_str().and_then(|s| s.parse::<u64>().ok()) else { return };
            let Some(pending) = inner.pending.lock().remove(&id) else { return };
            let seq = message["seq"].as_u64().unwrap_or(0);
            let result = match message.get("error").filter(|e| !e.is_null()) {
                Some(error) => Err(CallError {
                    code: error["code"].as_str().unwrap_or("internal").to_owned(),
                    message: error["message"].as_str().unwrap_or_default().to_owned(),
                }),
                None => Ok(Reply {
                    seq,
                    value: match message["payload"].take() {
                        Value::Null => json!({}),
                        value => value,
                    },
                }),
            };
            match pending {
                Pending::Waiter(tx) => {
                    let _ = tx.send(result);
                }
                Pending::Callback(f) => run_callback(&inner.emitter, f, result),
            }
        }
        Some("event") => {
            if let Some(event) = parse_event(&mut message) {
                let _ = sink.send(ControlMsg::Event(event));
            }
        }
        _ => {}
    }
}

/// A callback that panics must not take the reader (and with it the connection) down.
fn run_callback(emitter: &Emitter, f: Box<dyn FnOnce(CallResult) + Send>, result: CallResult) {
    if std::panic::catch_unwind(std::panic::AssertUnwindSafe(|| f(result))).is_err() {
        emitter.log("error", "control", "a response handler panicked");
    }
}

fn parse_event(message: &mut Value) -> Option<Event> {
    let seq = message["seq"].as_u64()?;
    let mut outer = message["payload"].take();
    let name = outer["type"].as_str()?.to_owned();
    if name == "session_event" {
        let mut inner = outer["payload"].take();
        let session = inner["session_id"].as_str().map(str::to_owned);
        let mut event = inner["event"].take();
        return Some(Event { seq, session, name: event["type"].as_str()?.to_owned(), payload: event["payload"].take() });
    }
    Some(Event { seq, session: None, name, payload: outer["payload"].take() })
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn hello_shape() {
        let info = ClientInfo { name: "Windex".into(), version: "0.1.0".into(), label: "é".repeat(40) };
        let value: Value = serde_json::from_str(&hello(&info, Some("tok"))).unwrap();
        assert_eq!(value["api_revision"], 13);
        assert_eq!(value["api_min_revision"], 10);
        assert_eq!(value["client"]["platform"], "windows");
        assert_eq!(value["client"]["kind"], "app");
        assert!(value["client"]["label"].as_str().unwrap().len() <= 64);
        assert_eq!(value["reconnect_token"], "tok");
        assert!(serde_json::from_str::<Value>(&hello(&info, None)).unwrap().get("reconnect_token").is_none());
    }

    #[test]
    fn welcome() {
        let (w, limit) = parse_welcome(r#"{"type":"welcome","protocol":"rex.control.v1","limits":{"max_message_bytes":1048576},
            "client":{"id":"client:1","reconnect_token":"t","resumed":true},"api_revision":13,"api_server_revision":13}"#).unwrap();
        assert_eq!((w.client_id.as_str(), w.reconnect_token.as_deref(), w.resumed, w.api_revision, limit), ("client:1", Some("t"), true, 13, 1 << 20));
        assert!(parse_welcome(r#"{"type":"welcome","protocol":"rex.control.v1","limits":{},"api_revision":9}"#).is_err());
        assert!(parse_welcome(r#"{"type":"event"}"#).is_err());
    }

    #[test]
    fn events_unwrap_session_envelopes() {
        let mut raw: Value = serde_json::from_str(r#"{"type":"event","seq":42,"payload":{"type":"session_event","payload":{
            "session_id":"session:a","event":{"type":"window_label_changed","payload":{"window_id":"window:b","label":"x"}}}}}"#).unwrap();
        let e = parse_event(&mut raw).unwrap();
        assert_eq!((e.seq, e.session.as_deref(), e.name.as_str()), (42, Some("session:a"), "window_label_changed"));
        assert_eq!(e.payload["label"], "x");
        let mut raw: Value = serde_json::from_str(r#"{"type":"event","seq":3,"payload":{"type":"session_created","payload":{"session_id":"s"}}}"#).unwrap();
        let e = parse_event(&mut raw).unwrap();
        assert_eq!((e.session, e.name.as_str()), (None, "session_created"));
    }
}
