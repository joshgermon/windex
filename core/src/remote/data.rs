//! Terminal data channels (docs/protocol.md §4): one task per streamed block. It opens
//! `/api/data` with a ticket (HELLO) or the stream's token (RESUME), splits the stream into the
//! snapshot, PTY output and Rex APCs, and feeds the pane worker. A dropped connection resumes
//! from the bytes received so far; a refused resume starts over with a fresh snapshot; close
//! 4000 (block closed) ends it.
use super::Shared;
use super::control::CALL_TIMEOUT;
use super::link::{Conn, PaneLink};
use super::stream::{Chunk, StreamDecoder};
use super::transport::{self, DATA_PATH, DATA_PROTOCOL, Socket};
use crate::pane::{PaneMsg, PaneShared};
use anyhow::{Context, Result, anyhow, bail, ensure};
use base64::Engine;
use base64::engine::general_purpose::URL_SAFE_NO_PAD;
use futures_util::{SinkExt, StreamExt};
use ghostty_vt::Terminal;
use ghostty_vt::style::RgbColor;
use serde_json::{Value, json};
use std::sync::Arc;
use std::time::Duration;
use tokio::sync::{mpsc, watch};
use tokio_tungstenite::tungstenite::Message;
use tokio_tungstenite::tungstenite::protocol::CloseFrame;
use tokio_tungstenite::tungstenite::protocol::frame::coding::CloseCode;

/// How a stream task is told to stop.
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
enum Stop {
    /// Drop the connection (the block is gone or no longer streamed).
    Drop,
    /// Close with 1000, ending the stream on the server (shutdown).
    Close,
}

#[derive(Clone, Copy, Debug, Default)]
struct Signal {
    stop: Option<Stop>,
    /// Bumped by `reconnect_pane`: start over with a fresh ticket and snapshot.
    restarts: u64,
}

pub struct StreamHandle {
    signal: watch::Sender<Signal>,
    task: tokio::task::JoinHandle<()>,
}

impl StreamHandle {
    /// Stop and forget the stream.
    pub fn drop_stream(self) {
        self.signal.send_modify(|s| s.stop = Some(Stop::Drop));
    }

    /// End the stream with a close frame; the handle resolves when the task is done.
    pub fn close(self) -> tokio::task::JoinHandle<()> {
        self.signal.send_modify(|s| s.stop = Some(Stop::Close));
        self.task
    }

    pub fn restart(&self) {
        self.signal.send_modify(|s| s.restarts += 1);
    }

    pub fn is_finished(&self) -> bool {
        self.task.is_finished()
    }
}

pub fn spawn(shared: Arc<Shared>, link: Arc<PaneLink>) -> StreamHandle {
    let (signal, signal_rx) = watch::channel(Signal::default());
    let task = shared.runtime.spawn(run(shared.clone(), link, signal_rx));
    StreamHandle { signal, task }
}

#[derive(Debug)]
struct Rejected(u8);
impl std::fmt::Display for Rejected {
    fn fmt(&self, f: &mut std::fmt::Formatter<'_>) -> std::fmt::Result {
        write!(f, "the server refused the data handshake (reason {})", self.0)
    }
}
impl std::error::Error for Rejected {}

/// A stream's position, kept across connections so it can be resumed.
struct Position {
    token: [u8; 16],
    nonce: u64,
    received: u64,
    decoder: StreamDecoder,
}

enum End {
    Stopped(Stop),
    Restart,
    /// 4000 `block closed`, or 1000: never reconnect.
    Finished(String),
    /// Resume impossible (provider canceled, reconnect required, a decode failure).
    Fresh(String),
    /// Connection lost: resume.
    Lost(String),
    /// The host's control connection changed (a new tunnel): resume on the new one at once.
    Moved,
}

async fn run(shared: Arc<Shared>, link: Arc<PaneLink>, mut signal: watch::Receiver<Signal>) {
    let mut conns = link.host.subscribe();
    let mut position: Option<Position> = None;
    let mut restarts = 0u64;
    let mut failures = 0u32;
    // Fresh starts in a row (a snapshot that never decodes must not loop at full speed).
    let mut fresh_starts = 0u32;
    loop {
        // Wait for a live control connection.
        let conn = loop {
            if signal.borrow().stop.is_some() {
                return;
            }
            if let Some(conn) = link.host.conn() {
                break conn;
            }
            tokio::select! {
                changed = conns.changed() => if changed.is_err() { return },
                changed = signal.changed() => if changed.is_err() { return },
            }
        };
        let wanted = signal.borrow().restarts;
        if wanted != restarts {
            restarts = wanted;
            position = None;
        }
        let (mut socket, mut pos, fresh) = match open(&shared, &link, &conn, position.take()).await {
            Ok(opened) => opened,
            Err((error, kept)) => {
                position = kept;
                failures += 1;
                shared.emitter.log("warn", "data", format!("pane {}: {error:#}", link.pane));
                if !sleep_or_stop(&mut signal, backoff(failures)).await {
                    return;
                }
                continue;
            }
        };
        failures = 0;
        if fresh {
            shared.emitter.log("debug", "data", format!("pane {}: stream opened", link.pane));
        }
        if link.set_stream_live(true) {
            shared.touch();
        }
        let end = pump(&shared, &link, &mut socket, &mut pos, &mut signal, restarts, &mut conns).await;
        if link.set_stream_live(false) {
            shared.touch();
        }
        let delay = match end {
            End::Stopped(Stop::Close) => {
                let frame = CloseFrame { code: CloseCode::Normal, reason: "".into() };
                let _ = tokio::time::timeout(Duration::from_millis(500), socket.close(Some(frame))).await;
                return;
            }
            End::Stopped(Stop::Drop) => return,
            End::Restart => {
                fresh_starts = 0;
                continue;
            }
            End::Finished(why) => {
                shared.emitter.log("info", "data", format!("pane {}: stream ended ({why})", link.pane));
                return;
            }
            End::Fresh(why) => {
                fresh_starts += 1;
                shared.emitter.log("info", "data", format!("pane {}: starting over ({why})", link.pane));
                backoff(fresh_starts)
            }
            End::Lost(why) => {
                fresh_starts = 0;
                shared.emitter.log("info", "data", format!("pane {}: connection lost ({why}); resuming", link.pane));
                position = Some(pos);
                Duration::from_secs(1)
            }
            End::Moved => {
                fresh_starts = 0;
                position = Some(pos);
                continue;
            }
        };
        drop(socket);
        if !sleep_or_stop(&mut signal, delay).await {
            return;
        }
    }
}

fn backoff(failures: u32) -> Duration {
    Duration::from_secs(match failures {
        0 | 1 => 1,
        2 => 2,
        3 => 4,
        _ => 8,
    })
}

/// Sleep, unless told to stop first (false); a restart cuts the wait short.
async fn sleep_or_stop(signal: &mut watch::Receiver<Signal>, delay: Duration) -> bool {
    tokio::select! {
        _ = tokio::time::sleep(delay) => true,
        changed = signal.changed() => changed.is_ok() && signal.borrow().stop.is_none(),
    }
}

/// Connect: RESUME where possible, else a ticket and HELLO. On failure the position is handed
/// back so a later attempt can still resume.
async fn open(
    shared: &Arc<Shared>,
    link: &PaneLink,
    conn: &Conn,
    position: Option<Position>,
) -> Result<(Socket, Position, bool), (anyhow::Error, Option<Position>)> {
    if let Some(mut pos) = position {
        let mut socket = match transport::connect(&conn.base, DATA_PATH, DATA_PROTOCOL).await {
            Ok(socket) => socket,
            Err(error) => return Err((error, Some(pos))),
        };
        let mut resume = vec![2u8];
        resume.extend(pos.token);
        resume.extend(pos.received.to_le_bytes());
        match handshake(&mut socket, resume).await {
            Ok((token, nonce)) if nonce == pos.nonce => {
                pos.token = token;
                return Ok((socket, pos, false));
            }
            Ok(_) => shared.emitter.log("warn", "data", format!("pane {}: resume changed the stream nonce", link.pane)),
            Err(error) if error.downcast_ref::<Rejected>().is_some() => {
                shared.emitter.log("info", "data", format!("pane {}: resume refused ({error}); fresh snapshot", link.pane));
            }
            Err(error) => return Err((error, Some(pos))),
        }
    }
    fresh(shared, link, conn).await.map_err(|e| (e, None))
}

async fn fresh(shared: &Arc<Shared>, link: &PaneLink, conn: &Conn) -> Result<(Socket, Position, bool)> {
    let pooled = shared.take_ticket(&link.host.id, &link.block);
    let from_pool = pooled.is_some();
    let ticket = match pooled {
        Some(ticket) => ticket,
        None => connect_ticket(link, conn).await?,
    };
    match hello(conn, &ticket).await {
        Err(error) if from_pool && error.downcast_ref::<Rejected>().is_some() => {
            // A pooled ticket can go stale; one fresh ticket from data.connect.
            let ticket = connect_ticket(link, conn).await?;
            hello(conn, &ticket).await
        }
        other => other,
    }
}

async fn hello(conn: &Conn, ticket: &str) -> Result<(Socket, Position, bool)> {
    let raw = URL_SAFE_NO_PAD.decode(ticket).context("invalid data ticket")?;
    ensure!(raw.len() == 16, "invalid data ticket length");
    let mut socket = transport::connect(&conn.base, DATA_PATH, DATA_PROTOCOL).await?;
    let mut message = vec![1u8];
    message.extend(raw);
    let (token, nonce) = handshake(&mut socket, message).await?;
    Ok((socket, Position { token, nonce, received: 0, decoder: StreamDecoder::new(nonce) }, true))
}

async fn connect_ticket(link: &PaneLink, conn: &Conn) -> Result<String> {
    let reply = conn
        .control
        .call_timeout(
            "data.connect",
            json!({"session_id": link.session, "block_id": link.block, "data_ref_id": "terminal", "subprotocol": DATA_PROTOCOL}),
            CALL_TIMEOUT,
        )
        .await
        .map_err(|e| anyhow!("data.connect: {e}"))?;
    reply.value["ticket"].as_str().map(str::to_owned).context("data.connect returned no ticket")
}

/// Send HELLO/RESUME and read WELCOME (§4.3): token and nonce. Bytes streams (transfers) open
/// with the same handshake.
pub(super) async fn handshake(socket: &mut Socket, message: Vec<u8>) -> Result<([u8; 16], u64)> {
    socket.send(Message::Binary(message.into())).await?;
    let reply = tokio::time::timeout(CALL_TIMEOUT, async {
        loop {
            match socket.next().await {
                Some(Ok(Message::Binary(bytes))) => return Ok(bytes),
                Some(Ok(Message::Close(frame))) => bail!("data connection closed during the handshake ({:?})", frame.map(|f| f.reason.to_string())),
                Some(Ok(_)) => continue,
                Some(Err(error)) => return Err(anyhow::Error::from(error)),
                None => bail!("data connection ended during the handshake"),
            }
        }
    })
    .await
    .context("data WELCOME timed out")??;
    if reply.first() == Some(&0x82) {
        return Err(Rejected(reply.get(1).copied().unwrap_or(0)).into());
    }
    ensure!(reply.len() == 32 && reply[0] == 0x81, "invalid data WELCOME");
    let nonce = u64::from_le_bytes(reply[24..32].try_into()?);
    ensure!(nonce != 0, "invalid data channel nonce");
    Ok((reply[1..17].try_into()?, nonce))
}

/// Read the stream into the pane until it ends, the connection changes, or we're told to stop.
async fn pump(
    shared: &Arc<Shared>,
    link: &Arc<PaneLink>,
    socket: &mut Socket,
    pos: &mut Position,
    signal: &mut watch::Receiver<Signal>,
    restarts: u64,
    conns: &mut watch::Receiver<Option<Conn>>,
) -> End {
    conns.mark_unchanged();
    // A snapshot the pane worker could not decode: start over with a fresh one.
    let (failed, mut install_failures) = mpsc::unbounded_channel();
    loop {
        tokio::select! {
            Some(error) = install_failures.recv() => return End::Fresh(format!("snapshot not installed: {error}")),
            changed = signal.changed() => {
                let now = *signal.borrow();
                match now.stop {
                    Some(stop) => return End::Stopped(stop),
                    None if changed.is_err() => return End::Stopped(Stop::Drop),
                    None if now.restarts != restarts => return End::Restart,
                    None => {}
                }
            }
            // A new control connection (or none) means a new tunnel: this socket is stale.
            _ = conns.changed() => return End::Moved,
            message = socket.next() => match message {
                Some(Ok(Message::Binary(bytes))) => {
                    pos.received += bytes.len() as u64;
                    match pos.decoder.feed(&bytes) {
                        Ok(chunks) => deliver(shared, link, chunks, &failed),
                        Err(error) => return End::Fresh(format!("{error:#}")),
                    }
                }
                Some(Ok(Message::Close(frame))) => {
                    let (code, reason) = frame.map(|f| (u16::from(f.code), f.reason.to_string())).unwrap_or((1005, String::new()));
                    return match code {
                        4000 => {
                            link.set_exited();
                            shared.touch();
                            End::Finished(format!("{code} {reason}"))
                        }
                        1000 => End::Finished(format!("{code} {reason}")),
                        1001 if reason.contains("canceled") => End::Fresh(format!("{code} {reason}")),
                        1008 | 1011 | 1013 => End::Fresh(format!("{code} {reason}")),
                        _ => End::Lost(format!("{code} {reason}")),
                    };
                }
                Some(Ok(_)) => {}
                Some(Err(error)) => return End::Lost(error.to_string()),
                None => return End::Lost("connection ended".into()),
            }
        }
    }
}

fn deliver(shared: &Shared, link: &PaneLink, chunks: Vec<Chunk>, failed: &mpsc::UnboundedSender<String>) {
    for chunk in chunks {
        match chunk {
            Chunk::Snapshot(bytes) => install_snapshot(&link.terminal, bytes, failed.clone()),
            Chunk::Output(bytes) => {
                link.terminal.send(PaneMsg::Bytes(bytes));
            }
            Chunk::Meta(value) => match value["type"].as_str() {
                Some("resize") => {
                    let dim = |key: &str| value[key].as_u64().filter(|v| (1..=65535).contains(v)).map(|v| v as u16);
                    let (Some(cols), Some(rows)) = (dim("cols"), dim("rows")) else { continue };
                    let cell_w = value["cell_width_px"].as_u64().unwrap_or(0) as u32;
                    let cell_h = value["cell_height_px"].as_u64().unwrap_or(0) as u32;
                    link.set_server_size(cols, rows);
                    link.terminal.send(PaneMsg::With(Box::new(move |terminal| {
                        let _ = terminal.resize(cols, rows, cell_w, cell_h);
                    })));
                    shared.touch();
                }
                Some("theme") => {
                    link.terminal.send(PaneMsg::With(Box::new(move |terminal| apply_theme(terminal, &value))));
                }
                Some("child_exited") => {
                    link.set_exited();
                    shared.touch();
                }
                _ => {}
            },
        }
    }
}

/// Replace the pane's terminal with a snapshot, decoded on its worker thread (`Terminal` is
/// `!Send`). A decode failure is reported on `failed`.
fn install_snapshot(pane: &PaneShared, bytes: Vec<u8>, failed: mpsc::UnboundedSender<String>) {
    pane.send(PaneMsg::Install(Box::new(move || {
        decode_snapshot(&bytes).inspect_err(|error| {
            let _ = failed.send(format!("{error:#}"));
        })
    })));
}

/// Decode a complete snapshot into a terminal (runs on the pane worker thread).
fn decode_snapshot(bytes: &[u8]) -> Result<Terminal> {
    let mut decoder = ghostty_vt::snapshot::Decoder::from_slice(bytes);
    decoder.ready().context("snapshot metadata")?;
    while decoder.next_record().context("snapshot record")? {}
    ensure!(decoder.source_offset()? == Some(bytes.len()), "snapshot boundary mismatch");
    let mut terminal = decoder.into_terminal().context("snapshot has no terminal")?;
    terminal.set_clipboard_write_max_bytes(Some(1 << 20))?;
    Ok(terminal)
}

fn parse_color(value: &Value) -> Option<RgbColor> {
    let hex = value.as_str()?.trim_start_matches('#');
    if hex.len() != 6 {
        return None;
    }
    let v = u32::from_str_radix(hex, 16).ok()?;
    Some(RgbColor { r: (v >> 16) as u8, g: (v >> 8) as u8, b: v as u8 })
}

/// The `theme` APC: default colours and palette; each theme replaces the previous one.
fn apply_theme(terminal: &mut Terminal, theme: &Value) {
    let _ = terminal.set_default_fg_color(parse_color(&theme["foreground"]));
    let _ = terminal.set_default_bg_color(parse_color(&theme["background"]));
    let _ = terminal.set_default_cursor_color(parse_color(&theme["cursor"]));
    let _ = terminal.set_default_color_palette(None);
    let Some(entries) = theme["palette"].as_array() else { return };
    let Ok(mut palette) = terminal.color_palette() else { return };
    for entry in entries.iter().filter_map(Value::as_str) {
        if let Some((index, color)) = entry.split_once('=')
            && let (Ok(index), Some(color)) = (index.trim().parse::<u8>(), parse_color(&json!(color)))
        {
            palette.0[usize::from(index)] = color;
        }
    }
    let _ = terminal.set_default_color_palette(Some(palette));
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::events::Emitter;
    use crate::pane::{self, NullOutput, PaneConfig};
    use std::sync::atomic::Ordering;
    use std::time::Instant;

    #[test]
    fn a_snapshot_that_does_not_decode_is_reported_to_its_stream() {
        let config = PaneConfig { id: 1, cols: 80, rows: 24, create_terminal: false, emitter: Emitter::none() };
        let pane = pane::spawn(config, Box::new(NullOutput));
        let (failed, mut failures) = mpsc::unbounded_channel();
        install_snapshot(&pane, b"not a snapshot".to_vec(), failed);
        let deadline = Instant::now() + Duration::from_secs(5);
        let error = loop {
            match failures.try_recv() {
                Ok(error) => break error,
                Err(_) if Instant::now() < deadline => std::thread::sleep(Duration::from_millis(5)),
                Err(_) => panic!("the failure was not reported"),
            }
        };
        assert!(!error.is_empty());
        assert!(!pane.has_terminal.load(Ordering::Acquire), "nothing was installed");
        pane.close();
    }
}
