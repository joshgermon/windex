//! File transfers over `rex.data.bytes.v1` (docs/protocol.md §4.6, docs/contract.md
//! "Transfers").
//!
//! - **Upload** (`com.superlogical.terminal.upload_begin`, API revision 11). The files of one drop
//!   or paste are one batch. Every file of the batch is reserved, and its stream opened while its
//!   ticket is fresh (a ticket lives 60 s), before any is sent, so the server refuses a file it
//!   won't take before bytes are spent on the others. Then the files are streamed one after
//!   another in 64 KiB messages, each ended with one empty message. Once every file of the batch
//!   has arrived the server types their paths into the terminal as one paste and closes each
//!   stream with 1000; any other close means nothing was typed. A finished file's stream stays
//!   open, and is read, while the rest of the batch is sent.
//! - **Download** (`download_begin`, API revision 13). The server sends the file in messages of up
//!   to 32 KiB, then one empty message, then close 1000, which is the only success. The bytes go to
//!   `<destination>.partial`, which replaces the destination on success and is removed otherwise.
//!
//! Every transfer is a task with its own data socket, so it never holds up a pane's stream. The
//! model lists the transfers in flight (`transfers`), refreshed at most every 100 ms. Cancel closes
//! the transfer's streams with 1000 and the server drops what it had. Nothing is retried. The
//! command's RESULT arrives when the transfer ends.
//!
//! Ported from Rex Rune (MIT): the upload batch reservation, ids and end-of-upload rule
//! (`src/upload.rs`) and the download end rule (`docs/server-api13.md`).
use super::Shared;
use super::control::CALL_TIMEOUT;
use super::data;
use super::link::Conn;
use super::transport::{self, Base, DATA_PATH, Socket};
use base64::Engine;
use base64::engine::general_purpose::URL_SAFE_NO_PAD;
use futures_util::stream::{SplitSink, SplitStream};
use futures_util::{SinkExt, StreamExt};
use parking_lot::Mutex;
use serde_json::{Value, json};
use std::future::Future;
use std::path::{Path, PathBuf};
use std::sync::Arc;
use std::sync::atomic::{AtomicU8, AtomicU64, Ordering};
use std::time::{Duration, Instant};
use tokio::io::{AsyncReadExt, AsyncWriteExt, BufWriter};
use tokio::sync::watch;
use tokio::task::JoinHandle;
use tokio_tungstenite::tungstenite::Message;
use tokio_tungstenite::tungstenite::protocol::CloseFrame;
use tokio_tungstenite::tungstenite::protocol::frame::coding::CloseCode;

pub const BYTES_PROTOCOL: &str = "rex.data.bytes.v1";
/// The first API revision with `upload_begin`.
pub const UPLOAD_API: u32 = 11;
/// The first API revision with `download_begin`.
pub const DOWNLOAD_API: u32 = 13;
const CHUNK: usize = 64 * 1024;
/// Progress reaches the model at most this often.
const PROGRESS_INTERVAL: Duration = Duration::from_millis(100);
/// How long the server may take to type a batch's paths once its last byte is sent.
const FINISH_TIMEOUT: Duration = Duration::from_secs(60);
/// How long a download may go without a message from the server.
const IDLE_TIMEOUT: Duration = Duration::from_secs(60);
const WRITE_BUFFER: usize = 256 * 1024;

#[derive(Clone, Copy, Debug, PartialEq, Eq)]
pub enum Kind {
    Upload,
    Download,
}

/// Where a transfer is: reserving and opening, moving bytes, or waiting for the server's word
/// (an upload's typed paths) or the local file (a download's rename).
#[derive(Clone, Copy, Debug, PartialEq, Eq)]
#[repr(u8)]
enum Phase {
    Starting = 0,
    Sending = 1,
    Finishing = 2,
}

/// A transfer in flight, as the model shows it.
pub struct Transfer {
    pub id: String,
    kind: Kind,
    host: String,
    pane: u64,
    count: usize,
    /// A download's local destination.
    local: Option<String>,
    /// The file being moved now: its index in the batch and its name.
    current: Mutex<(usize, String)>,
    /// A download's absolute remote path, once the server named it.
    remote: Mutex<Option<String>>,
    bytes: AtomicU64,
    total: AtomicU64,
    phase: AtomicU8,
    cancel: watch::Sender<bool>,
}

impl Transfer {
    fn new(id: String, kind: Kind, target: &Target, count: usize, name: String, total: u64, local: Option<String>) -> Self {
        Self {
            id,
            kind,
            host: target.host.clone(),
            pane: target.pane,
            count,
            local,
            current: Mutex::new((0, name)),
            remote: Mutex::new(None),
            bytes: AtomicU64::new(0),
            total: AtomicU64::new(total),
            phase: AtomicU8::new(Phase::Starting as u8),
            cancel: watch::channel(false).0,
        }
    }

    /// Stop the transfer: its task closes its streams and reports `canceled`.
    pub fn cancel(&self) {
        self.cancel.send_replace(true);
    }

    pub fn json(&self) -> Value {
        let (index, name) = self.current.lock().clone();
        let state = match self.phase.load(Ordering::Acquire) {
            0 => "starting",
            1 => "sending",
            _ => "finishing",
        };
        json!({
            "id": self.id,
            "kind": match self.kind { Kind::Upload => "upload", Kind::Download => "download" },
            "host": self.host,
            "pane": self.pane,
            "name": name,
            "index": index,
            "count": self.count,
            "bytes": self.bytes.load(Ordering::Relaxed),
            "total": self.total.load(Ordering::Relaxed),
            "state": state,
            "remote": *self.remote.lock(),
            "local": self.local,
        })
    }
}

/// Why a transfer did not finish.
#[derive(Debug, PartialEq, Eq)]
enum Failure {
    Canceled,
    Failed(String),
}

impl From<String> for Failure {
    fn from(message: String) -> Self {
        Failure::Failed(message)
    }
}

impl From<&str> for Failure {
    fn from(message: &str) -> Self {
        Failure::Failed(message.to_owned())
    }
}

impl Failure {
    fn message(self) -> String {
        match self {
            Failure::Canceled => "canceled".into(),
            Failure::Failed(message) => message,
        }
    }
}

/// Run `work` unless the transfer is canceled first.
async fn or_cancel<T>(cancel: &mut watch::Receiver<bool>, work: impl Future<Output = T>) -> Result<T, Failure> {
    tokio::select! {
        biased;
        _ = cancel.wait_for(|canceled| *canceled) => Err(Failure::Canceled),
        out = work => Ok(out),
    }
}

/// A transfer in the model's list for as long as this lives (whatever ends the task).
struct Listed {
    shared: Arc<Shared>,
    transfer: Arc<Transfer>,
    touched: Instant,
}

impl Listed {
    fn new(shared: &Arc<Shared>, transfer: Transfer) -> Result<Self, String> {
        let transfer = Arc::new(transfer);
        let mut st = shared.state.lock();
        if st.transfers.iter().any(|t| t.id == transfer.id) {
            return Err(format!("transfer {} is already running", transfer.id));
        }
        st.transfers.push(transfer.clone());
        drop(st);
        shared.touch();
        Ok(Self { shared: shared.clone(), transfer, touched: Instant::now() })
    }

    fn set_phase(&mut self, phase: Phase) {
        self.transfer.phase.store(phase as u8, Ordering::Release);
        self.touch();
    }

    /// Now moving file `index` of the batch.
    fn at(&mut self, index: usize, name: &str) {
        *self.transfer.current.lock() = (index, name.to_owned());
        self.set_phase(Phase::Starting);
    }

    fn advance(&mut self, bytes: usize) {
        self.transfer.bytes.fetch_add(bytes as u64, Ordering::Relaxed);
        if self.touched.elapsed() >= PROGRESS_INTERVAL {
            self.touch();
        }
    }

    fn touch(&mut self) {
        self.touched = Instant::now();
        self.shared.touch();
    }
}

impl Drop for Listed {
    fn drop(&mut self) {
        self.shared.state.lock().transfers.retain(|t| !Arc::ptr_eq(t, &self.transfer));
        self.shared.touch();
    }
}

/// The pane a transfer goes to or comes from.
struct Target {
    host: String,
    host_name: String,
    pane: u64,
    session: String,
    block: String,
    conn: Conn,
}

fn target(shared: &Shared, v: &Value, needs: u32, what: &str) -> Result<Target, String> {
    let pane = v["pane"].as_u64().ok_or("missing \"pane\"")?;
    let st = shared.state.lock();
    let (host, block_id, block) = st.find_pane(pane).ok_or_else(|| format!("unknown pane {pane}"))?;
    let conn = host.link.conn().ok_or_else(|| format!("{} is not connected", host.name))?;
    let api = host.server.as_ref().map_or(0, |s| s.api);
    if api < needs {
        return Err(format!("the Rex server on {} is too old to {what} (API {api}, needs {needs}); update Rex there", host.name));
    }
    Ok(Target {
        host: host.id.clone(),
        host_name: host.name.clone(),
        pane,
        session: block.session.clone(),
        block: block_id.to_owned(),
        conn,
    })
}

fn transfer_id(v: &Value) -> String {
    static NEXT: AtomicU64 = AtomicU64::new(1);
    v["id"]
        .as_str()
        .filter(|id| !id.is_empty())
        .map(str::to_owned)
        .unwrap_or_else(|| format!("transfer:{}", NEXT.fetch_add(1, Ordering::Relaxed)))
}

pub fn cancel(shared: &Shared, v: &Value) -> Result<Value, String> {
    let id = v["id"].as_str().ok_or("missing \"id\"")?;
    let st = shared.state.lock();
    let transfer = st.transfers.iter().find(|t| t.id == id).ok_or_else(|| format!("no transfer {id}"))?;
    transfer.cancel();
    Ok(Value::Null)
}

/// Open a bytes stream with a ticket (HELLO, WELCOME; §4.3).
async fn open_bytes(base: &Base, ticket: &str) -> anyhow::Result<Socket> {
    let raw = URL_SAFE_NO_PAD.decode(ticket).map_err(|_| anyhow::anyhow!("the server sent an invalid transfer ticket"))?;
    anyhow::ensure!(raw.len() == 16, "the server sent an invalid transfer ticket");
    let mut socket = transport::connect(base, DATA_PATH, BYTES_PROTOCOL).await?;
    let mut hello = vec![1u8];
    hello.extend(raw);
    data::handshake(&mut socket, hello).await?;
    Ok(socket)
}

/// Why the server closed a bytes stream, in words.
fn closed_by_server(code: u16, reason: &str) -> String {
    match (code, reason) {
        (4000, _) => "the pane was closed during the transfer".into(),
        (1001, r) if r.contains("canceled") => "Rex canceled the transfer (the connection or the session ended)".into(),
        (1011, _) => "the server could not read or store the file".into(),
        (code, "") => format!("Rex refused the transfer ({code})"),
        (code, reason) => format!("{reason} ({code})"),
    }
}

// ------------------------------------------------------------------- uploads

/// A local file to upload and the name it is stored under.
struct Source {
    path: PathBuf,
    name: String,
}

/// Files the UI wrote for this upload (a pasted image): removed when the upload ends, however
/// it ends.
struct Temporary(Vec<PathBuf>);

impl Drop for Temporary {
    fn drop(&mut self) {
        for path in &self.0 {
            let _ = std::fs::remove_file(path);
        }
    }
}

/// `files: [{path, name?, temporary?}]`.
fn sources(v: &Value) -> Result<(Vec<Source>, Temporary), String> {
    let entries = v["files"].as_array().filter(|f| !f.is_empty()).ok_or("missing \"files\"")?;
    let mut sources = Vec::with_capacity(entries.len());
    let mut temporary = Temporary(Vec::new());
    for entry in entries {
        let path = PathBuf::from(entry["path"].as_str().ok_or("every file needs a \"path\"")?);
        if entry["temporary"].as_bool() == Some(true) {
            temporary.0.push(path.clone());
        }
        let name = entry["name"]
            .as_str()
            .filter(|n| !n.trim().is_empty())
            .map(str::to_owned)
            .or_else(|| path.file_name().map(|n| n.to_string_lossy().into_owned()))
            .unwrap_or_else(|| "file".into());
        sources.push(Source { path, name });
    }
    Ok((sources, temporary))
}

/// The exact size to declare. Only regular files can be uploaded.
fn size_of(source: &Source) -> Result<u64, String> {
    let meta = std::fs::metadata(&source.path).map_err(|e| match e.kind() {
        std::io::ErrorKind::NotFound => format!("{} no longer exists", source.name),
        _ => format!("{} can't be read: {e}", source.name),
    })?;
    if meta.is_dir() {
        return Err(format!("{} is a folder; only files can be uploaded", source.name));
    }
    if !meta.is_file() {
        return Err(format!("{} is not a regular file; only files can be uploaded", source.name));
    }
    Ok(meta.len())
}

/// `upload {pane, files, id?}`: every file is one batch; RESULT when the paths are typed.
pub async fn upload(shared: Arc<Shared>, v: Value) -> Result<Value, String> {
    let (sources, _temporary) = sources(&v)?;
    let target = target(&shared, &v, UPLOAD_API, "receive files")?;
    let sizes = sources.iter().map(size_of).collect::<Result<Vec<_>, _>>()?;
    let total: u64 = sizes.iter().sum();
    let transfer = Transfer::new(transfer_id(&v), Kind::Upload, &target, sources.len(), sources[0].name.clone(), total, None);
    let mut listed = Listed::new(&shared, transfer)?;
    let id = listed.transfer.id.clone();
    let log = |level: &str, what: String| shared.emitter.log(level, "transfer", format!("{id}: {what}"));
    log("info", format!("uploading {} file(s), {total} bytes, to pane {} on {}", sources.len(), target.pane, target.host_name));
    let mut cancel = listed.transfer.cancel.subscribe();
    let mut streams = Vec::new();
    let outcome = send_batch(&mut listed, &target, &sources, &sizes, &mut streams, &mut cancel).await;
    if outcome.is_err() {
        for stream in streams {
            stream.close().await;
        }
    }
    match outcome {
        Ok(()) => {
            log("info", "uploaded; the server typed the paths".into());
            let names: Vec<&str> = sources.iter().map(|s| s.name.as_str()).collect();
            Ok(json!({"id": id, "host": target.host, "pane": target.pane, "files": names, "bytes": total}))
        }
        Err(failure) => {
            let message = failure.message();
            log("warn", format!("upload failed: {message}"));
            Err(message)
        }
    }
}

async fn send_batch(
    listed: &mut Listed,
    target: &Target,
    sources: &[Source],
    sizes: &[u64],
    streams: &mut Vec<Upstream>,
    cancel: &mut watch::Receiver<bool>,
) -> Result<(), Failure> {
    // Every file is reserved before a byte is sent, so a file the server won't take (too large)
    // fails the batch at once rather than after the files before it were sent. Each stream is
    // opened as soon as its file is reserved, which spends the ticket before it can expire.
    let batch = (sources.len() > 1).then(new_uuid);
    for (index, (source, &size)) in sources.iter().zip(sizes).enumerate() {
        let mut args = json!({"ref": format!("upload:{}", new_uuid()), "name": source.name, "size": size});
        if let Some(batch) = &batch {
            args["batch"] = json!(batch);
            args["index"] = json!(index);
            args["count"] = json!(sources.len());
        }
        let payload = json!({"session_id": target.session, "block_id": target.block, "args": args});
        let call = target.conn.control.call_timeout("com.superlogical.terminal.upload_begin", payload, CALL_TIMEOUT);
        let reply = or_cancel(cancel, call).await?.map_err(|e| format!("{} was not accepted: {e}", source.name))?;
        let ticket = reply.value["ticket"].as_str().ok_or("upload_begin returned no ticket")?;
        let socket = or_cancel(cancel, open_bytes(&target.conn.base, ticket)).await?.map_err(|e| format!("{e:#}"))?;
        streams.push(Upstream::new(socket));
    }
    for (index, ((source, &size), stream)) in sources.iter().zip(sizes).zip(streams.iter_mut()).enumerate() {
        listed.at(index, &source.name);
        listed.set_phase(Phase::Sending);
        send_file(listed, stream, source, size, cancel).await?;
    }
    listed.set_phase(Phase::Finishing);
    let deadline = Instant::now() + FINISH_TIMEOUT;
    for stream in streams.iter_mut() {
        stream.finished(deadline, cancel).await?;
    }
    Ok(())
}

/// Stream exactly `size` bytes of a file, then the empty message that ends it.
async fn send_file(listed: &mut Listed, stream: &mut Upstream, source: &Source, size: u64, cancel: &mut watch::Receiver<bool>) -> Result<(), Failure> {
    let unreadable = |e: std::io::Error| Failure::Failed(format!("{} can't be read: {e}", source.name));
    let file = or_cancel(cancel, tokio::fs::File::open(&source.path)).await?.map_err(unreadable)?;
    let mut reader = file.take(size);
    let mut sent = 0u64;
    loop {
        let mut chunk = vec![0u8; CHUNK];
        let n = or_cancel(cancel, reader.read(&mut chunk)).await?.map_err(unreadable)?;
        if n == 0 {
            break;
        }
        chunk.truncate(n);
        stream.send(chunk, cancel).await?;
        sent += n as u64;
        listed.advance(n);
    }
    if sent != size {
        return Err(format!("{} changed while it was uploading", source.name).into());
    }
    stream.send(Vec::new(), cancel).await
}

/// How a bytes stream ended, as its reader saw it.
#[derive(Clone, Debug, PartialEq, Eq)]
enum Closed {
    Frame(u16, String),
    Error(String),
    Ended,
}

/// The server's verdict on an uploaded file: only a 1000 close means its batch was typed.
fn upload_outcome(closed: &Closed) -> Result<(), String> {
    match closed {
        Closed::Frame(1000, _) => Ok(()),
        Closed::Frame(code, reason) => Err(closed_by_server(*code, reason)),
        Closed::Error(error) => Err(format!("the upload connection failed: {error}")),
        Closed::Ended => Err("the upload connection ended early".into()),
    }
}

/// One file's upload stream. Its incoming half is read by a task of its own until the server
/// closes, so pings are answered while other files of the batch are sent.
struct Upstream {
    sink: SplitSink<Socket, Message>,
    closed: watch::Receiver<Option<Closed>>,
    reader: JoinHandle<()>,
}

impl Upstream {
    fn new(socket: Socket) -> Self {
        let (sink, stream) = socket.split();
        let (tx, closed) = watch::channel(None);
        let reader = tokio::spawn(async move {
            let end = read_to_close(stream).await;
            tx.send_replace(Some(end));
        });
        Self { sink, closed, reader }
    }

    fn early_close(&self) -> Option<Failure> {
        let closed = self.closed.borrow().clone()?;
        Some(Failure::Failed(upload_outcome(&closed).err().unwrap_or_else(|| "Rex ended the upload before all of it was sent".into())))
    }

    async fn send(&mut self, data: Vec<u8>, cancel: &mut watch::Receiver<bool>) -> Result<(), Failure> {
        if let Some(failure) = self.early_close() {
            return Err(failure);
        }
        match or_cancel(cancel, self.sink.send(Message::Binary(data.into()))).await? {
            Ok(()) => Ok(()),
            Err(error) => Err(self.early_close().unwrap_or_else(|| Failure::Failed(format!("the upload connection failed: {error}")))),
        }
    }

    /// Wait for the server's close after the last byte of the batch; 1000 means the paths are typed.
    async fn finished(&mut self, deadline: Instant, cancel: &mut watch::Receiver<bool>) -> Result<(), Failure> {
        let wait = tokio::time::timeout_at(deadline.into(), self.closed.wait_for(Option::is_some));
        let closed = match or_cancel(cancel, wait).await? {
            Err(_) => return Err("Rex did not finish the upload in time".into()),
            Ok(Err(_)) => Closed::Ended,
            Ok(Ok(closed)) => closed.clone().unwrap_or(Closed::Ended),
        };
        upload_outcome(&closed)?;
        // Answer the close so the server lets go of the connection.
        let _ = tokio::time::timeout(Duration::from_millis(500), self.sink.close()).await;
        Ok(())
    }

    /// Give up on the stream: close with 1000, so the server drops the file.
    async fn close(mut self) {
        let frame = CloseFrame { code: CloseCode::Normal, reason: "".into() };
        let _ = tokio::time::timeout(Duration::from_secs(1), self.sink.send(Message::Close(Some(frame)))).await;
        let _ = tokio::time::timeout(Duration::from_millis(500), self.closed.wait_for(Option::is_some)).await;
    }
}

impl Drop for Upstream {
    fn drop(&mut self) {
        // The reader holds the other half of the socket: stop it so the connection ends.
        self.reader.abort();
    }
}

async fn read_to_close(mut stream: SplitStream<Socket>) -> Closed {
    loop {
        match stream.next().await {
            Some(Ok(Message::Close(frame))) => {
                return frame.map_or(Closed::Frame(1005, String::new()), |f| Closed::Frame(u16::from(f.code), f.reason.to_string()));
            }
            Some(Ok(_)) => {}
            Some(Err(error)) => return Closed::Error(error.to_string()),
            None => return Closed::Ended,
        }
    }
}

// ----------------------------------------------------------------- downloads

/// `download {pane, path, dest, id?}`: RESULT once the file is in place.
pub async fn download(shared: Arc<Shared>, v: Value) -> Result<Value, String> {
    let path = v["path"].as_str().map(str::trim).filter(|p| !p.is_empty()).ok_or("missing \"path\"")?.to_owned();
    let dest = PathBuf::from(v["dest"].as_str().filter(|d| !d.is_empty()).ok_or("missing \"dest\"")?);
    if !dest.is_absolute() || dest.file_name().is_none() {
        return Err("the destination must be an absolute file path".into());
    }
    if dest.is_dir() {
        return Err(format!("{} is a folder", dest.display()));
    }
    let target = target(&shared, &v, DOWNLOAD_API, "send files")?;
    let transfer = Transfer::new(transfer_id(&v), Kind::Download, &target, 1, display_name(remote_name(&path)), 0, Some(dest.display().to_string()));
    let mut listed = Listed::new(&shared, transfer)?;
    let id = listed.transfer.id.clone();
    let log = |level: &str, what: String| shared.emitter.log(level, "transfer", format!("{id}: {what}"));
    log("info", format!("downloading {path} from pane {} on {} to {}", target.pane, target.host_name, dest.display()));
    let mut cancel = listed.transfer.cancel.subscribe();
    match receive(&mut listed, &target, &path, &dest, &mut cancel).await {
        Ok((remote, name, bytes)) => {
            log("info", format!("downloaded {bytes} bytes"));
            Ok(json!({"id": id, "host": target.host, "pane": target.pane, "path": dest.display().to_string(), "name": name, "remote": remote, "bytes": bytes}))
        }
        Err(failure) => {
            let message = failure.message();
            log("warn", format!("download failed: {message}"));
            Err(message)
        }
    }
}

/// (remote path, display name, bytes written).
async fn receive(listed: &mut Listed, target: &Target, path: &str, dest: &Path, cancel: &mut watch::Receiver<bool>) -> Result<(String, String, u64), Failure> {
    let args = json!({"ref": format!("download:{}", new_uuid()), "path": path});
    let payload = json!({"session_id": target.session, "block_id": target.block, "args": args});
    let call = target.conn.control.call_timeout("com.superlogical.terminal.download_begin", payload, CALL_TIMEOUT);
    let reply = or_cancel(cancel, call).await?.map_err(|e| e.to_string())?.value;
    let ticket = reply["ticket"].as_str().ok_or("download_begin returned no ticket")?;
    let remote = reply["path"].as_str().unwrap_or(path).to_owned();
    let name = display_name(reply["name"].as_str().unwrap_or_else(|| remote_name(&remote)));
    *listed.transfer.current.lock() = (0, name.clone());
    *listed.transfer.remote.lock() = Some(remote.clone());
    listed.transfer.total.store(reply["size"].as_u64().unwrap_or(0), Ordering::Relaxed);
    listed.touch();

    let mut partial = Partial::create(dest).await?;
    let outcome = async {
        let mut socket = or_cancel(cancel, open_bytes(&target.conn.base, ticket)).await?.map_err(|e| format!("{e:#}"))?;
        listed.set_phase(Phase::Sending);
        let pumped = pump_download(&mut socket, &mut partial, listed, cancel).await;
        if pumped == Err(Failure::Canceled) {
            let frame = CloseFrame { code: CloseCode::Normal, reason: "".into() };
            let _ = tokio::time::timeout(Duration::from_secs(1), socket.close(Some(frame))).await;
        } else {
            // Answer the server's close (flushes the reply tungstenite queued) so it lets go of
            // the connection.
            let _ = tokio::time::timeout(Duration::from_millis(500), SinkExt::close(&mut socket)).await;
        }
        pumped
    }
    .await;
    match outcome {
        Ok(()) => {
            listed.set_phase(Phase::Finishing);
            let bytes = partial.commit().await?;
            Ok((remote, name, bytes))
        }
        Err(failure) => {
            partial.discard().await;
            Err(failure)
        }
    }
}

/// Read a download stream into the partial file until the server's verdict.
async fn pump_download(socket: &mut Socket, partial: &mut Partial, listed: &mut Listed, cancel: &mut watch::Receiver<bool>) -> Result<(), Failure> {
    let mut end = DownloadEnd::default();
    loop {
        let message = or_cancel(cancel, tokio::time::timeout(IDLE_TIMEOUT, socket.next()))
            .await?
            .map_err(|_| "Rex stopped sending the file")?;
        match end.next(message)? {
            Step::Data(bytes) => {
                partial.write(&bytes).await?;
                listed.advance(bytes.len());
            }
            Step::Wait => {}
            Step::Done => return Ok(()),
        }
    }
}

#[derive(Debug)]
enum Step {
    Data(Vec<u8>),
    Wait,
    Done,
}

/// The end of a download (docs/server-api13.md "How it ends"): complete only after the empty
/// message and then a 1000 close; any other close, or none, is a failure whatever arrived.
#[derive(Default)]
struct DownloadEnd {
    ended: bool,
}

impl DownloadEnd {
    fn next(&mut self, message: Option<Result<Message, tokio_tungstenite::tungstenite::Error>>) -> Result<Step, String> {
        match message {
            Some(Ok(Message::Binary(bytes))) if bytes.is_empty() => {
                self.ended = true;
                Ok(Step::Wait)
            }
            Some(Ok(Message::Binary(_))) if self.ended => Err("Rex sent data after the end of the file".into()),
            Some(Ok(Message::Binary(bytes))) => Ok(Step::Data(bytes.to_vec())),
            Some(Ok(Message::Close(frame))) => {
                let (code, reason) = frame.map_or((1005, String::new()), |f| (u16::from(f.code), f.reason.to_string()));
                match code {
                    1000 if self.ended => Ok(Step::Done),
                    1000 => Err("the download ended before the whole file arrived".into()),
                    _ => Err(closed_by_server(code, &reason)),
                }
            }
            Some(Ok(_)) => Ok(Step::Wait),
            Some(Err(error)) => Err(format!("the download connection failed: {error}")),
            None => Err("the download connection ended early".into()),
        }
    }
}

/// `<destination>.partial`: becomes the destination on success, is removed otherwise (also when
/// the task is dropped at shutdown).
struct Partial {
    path: PathBuf,
    dest: PathBuf,
    file: Option<BufWriter<tokio::fs::File>>,
    written: u64,
}

impl Partial {
    async fn create(dest: &Path) -> Result<Self, String> {
        let mut name = dest.file_name().unwrap_or_default().to_os_string();
        name.push(".partial");
        let path = dest.with_file_name(name);
        let file = tokio::fs::File::create(&path).await.map_err(|e| format!("{} can't be written: {e}", path.display()))?;
        Ok(Self { path, dest: dest.to_owned(), file: Some(BufWriter::with_capacity(WRITE_BUFFER, file)), written: 0 })
    }

    async fn write(&mut self, bytes: &[u8]) -> Result<(), String> {
        let file = self.file.as_mut().ok_or("the download file is closed")?;
        file.write_all(bytes).await.map_err(|e| format!("{} can't be written: {e}", self.path.display()))?;
        self.written += bytes.len() as u64;
        Ok(())
    }

    /// Flush, then move over the destination. The bytes written.
    async fn commit(mut self) -> Result<u64, String> {
        let failed = |e: std::io::Error| format!("{} can't be written: {e}", self.dest.display());
        let mut file = self.file.take().ok_or("the download file is closed")?;
        file.flush().await.map_err(failed)?;
        let file = file.into_inner();
        file.sync_all().await.map_err(failed)?;
        drop(file.into_std().await);
        if let Err(error) = tokio::fs::rename(&self.path, &self.dest).await {
            let _ = tokio::fs::remove_file(&self.path).await;
            return Err(failed(error));
        }
        Ok(self.written)
    }

    /// Close and remove the partial file.
    async fn discard(mut self) {
        if let Some(file) = self.file.take() {
            // Waits for writes in flight, so the file is closed before it is removed.
            drop(file.into_inner().into_std().await);
        }
        let _ = tokio::fs::remove_file(&self.path).await;
    }
}

impl Drop for Partial {
    fn drop(&mut self) {
        // Neither committed nor discarded: the task ended early (shutdown).
        let file = self.file.take();
        let open = file.is_some();
        drop(file);
        if open {
            let _ = std::fs::remove_file(&self.path);
        }
    }
}

/// The last element of a remote path as typed (`~/a/b.txt` → `b.txt`).
fn remote_name(path: &str) -> &str {
    let trimmed = path.trim_end_matches('/');
    trimmed.rsplit('/').next().filter(|n| !n.is_empty()).unwrap_or(trimmed)
}

/// A file name from the server made safe to show: `download_begin` returns names verbatim
/// (docs/server-api13.md), so control and bidirectional-formatting characters become U+FFFD.
pub fn display_name(name: &str) -> String {
    let shown: String = name
        .chars()
        .map(|c| match c {
            c if c.is_control() => '\u{FFFD}',
            '\u{200B}'..='\u{200F}' | '\u{202A}'..='\u{202E}' | '\u{2066}'..='\u{2069}' | '\u{FEFF}' => '\u{FFFD}',
            c => c,
        })
        .collect();
    if shown.trim().is_empty() { "file".into() } else { shown }
}

/// `upload:` / `download:` refs and batch ids: a random version 4 UUID in lower-case canonical
/// form (Rex Rune `upload::new_uuid`). The randomness is std's per-process SipHash keys.
pub fn new_uuid() -> String {
    let mut b = [0u8; 16];
    b[..8].copy_from_slice(&random_u64().to_le_bytes());
    b[8..].copy_from_slice(&random_u64().to_le_bytes());
    b[6] = (b[6] & 0x0f) | 0x40;
    b[8] = (b[8] & 0x3f) | 0x80;
    let h: String = b.iter().map(|x| format!("{x:02x}")).collect();
    format!("{}-{}-{}-{}-{}", &h[..8], &h[8..12], &h[12..16], &h[16..20], &h[20..])
}

fn random_u64() -> u64 {
    use std::hash::{BuildHasher, Hasher};
    static COUNTER: AtomicU64 = AtomicU64::new(0);
    let mut hasher = std::collections::hash_map::RandomState::new().build_hasher();
    hasher.write_u64(COUNTER.fetch_add(1, Ordering::Relaxed));
    hasher.write_u128(std::time::SystemTime::now().duration_since(std::time::UNIX_EPOCH).map_or(0, |d| d.as_nanos()));
    hasher.finish()
}

#[cfg(test)]
mod tests {
    use super::*;
    use crate::core::{Options, Panes};
    use crate::events::Emitter;
    use crate::remote::state::State;
    use tokio::net::TcpListener;
    use tokio_tungstenite::tungstenite::handshake::server::{ErrorResponse, Request, Response};

    #[test]
    fn ids_are_lower_case_version_4_uuids() {
        let id = new_uuid();
        assert_eq!(id.len(), 36);
        assert!(id.split('-').map(str::len).eq([8, 4, 4, 4, 12]));
        assert_eq!(&id[14..15], "4");
        assert!("89ab".contains(&id[19..20]));
        assert!(id.chars().all(|c| c == '-' || c.is_ascii_digit() || ('a'..='f').contains(&c)));
        let many: std::collections::HashSet<String> = (0..1000).map(|_| new_uuid()).collect();
        assert_eq!(many.len(), 1000);
    }

    #[test]
    fn hostile_names_are_shown_safely() {
        assert_eq!(display_name("report.pdf"), "report.pdf");
        assert_eq!(display_name("esc\u{1b}[31mred.txt"), "esc\u{FFFD}[31mred.txt");
        assert_eq!(display_name("a\nb\tc"), "a\u{FFFD}b\u{FFFD}c");
        assert_eq!(display_name("evil\u{202E}txt.exe"), "evil\u{FFFD}txt.exe");
        assert_eq!(display_name("caf\u{e9} \u{1F980}.md"), "caf\u{e9} \u{1F980}.md");
        assert_eq!(display_name(""), "file");
        assert_eq!(remote_name("~/src/site/index.html"), "index.html");
        assert_eq!(remote_name("/var/log/"), "log");
        assert_eq!(remote_name("notes.txt"), "notes.txt");
    }

    #[test]
    fn only_regular_files_can_be_uploaded() {
        let dir = std::env::temp_dir();
        let folder = Source { path: dir.clone(), name: "temp".into() };
        assert!(size_of(&folder).unwrap_err().contains("is a folder"));
        let missing = Source { path: dir.join("windex-no-such-file-7c1e"), name: "gone.txt".into() };
        assert_eq!(size_of(&missing).unwrap_err(), "gone.txt no longer exists");
        let scratch = Scratch::new("size");
        let file = scratch.file("a b.txt", b"hello");
        assert_eq!(size_of(&Source { path: file, name: "a b.txt".into() }), Ok(5));
    }

    #[test]
    fn files_take_their_names_and_temporaries_are_removed() {
        let scratch = Scratch::new("sources");
        let shot = scratch.file("windex-clip-1.png", b"png");
        let kept = scratch.file("notes.txt", b"n");
        let v = json!({"files": [
            {"path": shot.to_str(), "name": "screenshot-20261118-161502.png", "temporary": true},
            {"path": kept.to_str()},
        ]});
        let (sources, temporary) = sources(&v).unwrap();
        assert_eq!(sources[0].name, "screenshot-20261118-161502.png");
        assert_eq!(sources[1].name, "notes.txt");
        drop(temporary);
        assert!(!shot.exists(), "the temporary file is gone");
        assert!(kept.exists(), "the user's file stays");
        assert!(super::sources(&json!({"files": []})).is_err());
    }

    #[test]
    fn a_download_ends_only_after_the_empty_message_and_1000() {
        let close = |code: u16, reason: &str| Some(Ok(Message::Close(Some(CloseFrame { code: CloseCode::from(code), reason: reason.to_owned().into() }))));
        let binary = |b: &[u8]| Some(Ok(Message::Binary(b.to_vec().into())));

        let mut end = DownloadEnd::default();
        assert!(matches!(end.next(binary(b"hello")), Ok(Step::Data(d)) if d == b"hello"));
        assert!(matches!(end.next(Some(Ok(Message::Ping(Vec::new().into())))), Ok(Step::Wait)));
        assert!(matches!(end.next(binary(b"")), Ok(Step::Wait)));
        assert!(matches!(end.next(close(1000, "")), Ok(Step::Done)));

        // 1000 without the empty message: the file may be cut short.
        let mut end = DownloadEnd::default();
        end.next(binary(b"x")).ok();
        assert!(end.next(close(1000, "")).unwrap_err().contains("before the whole file"));
        // Data after the end, another close, no close at all.
        let mut end = DownloadEnd::default();
        end.next(binary(b"")).ok();
        assert!(end.next(binary(b"late")).is_err());
        assert_eq!(DownloadEnd::default().next(close(4000, "block closed")).unwrap_err(), "the pane was closed during the transfer");
        assert_eq!(DownloadEnd::default().next(close(1011, "data provider failed")).unwrap_err(), "the server could not read or store the file");
        assert!(DownloadEnd::default().next(None).is_err());
    }

    #[test]
    fn only_a_1000_close_means_the_paths_were_typed() {
        assert_eq!(upload_outcome(&Closed::Frame(1000, String::new())), Ok(()));
        assert_eq!(upload_outcome(&Closed::Frame(1008, "data handshake rejected".into())).unwrap_err(), "data handshake rejected (1008)");
        assert_eq!(upload_outcome(&Closed::Frame(1002, String::new())).unwrap_err(), "Rex refused the transfer (1002)");
        assert!(upload_outcome(&Closed::Ended).is_err());
    }

    // ---- against a local server speaking the bytes protocol (HELLO, WELCOME, then messages)

    /// A folder of the test's own under the temp dir, removed when the test ends.
    struct Scratch(PathBuf);

    impl Scratch {
        fn new(what: &str) -> Self {
            let path = std::env::temp_dir().join(format!("rexcore-transfer-{what}-{}", new_uuid()));
            std::fs::create_dir(&path).unwrap();
            Self(path)
        }

        fn file(&self, name: &str, bytes: &[u8]) -> PathBuf {
            let path = self.0.join(name);
            std::fs::write(&path, bytes).unwrap();
            path
        }
    }

    impl Drop for Scratch {
        fn drop(&mut self) {
            let _ = std::fs::remove_dir_all(&self.0);
        }
    }

    fn shared() -> Arc<Shared> {
        Arc::new(Shared {
            emitter: Emitter::none(),
            options: Options::default(),
            panes: Arc::new(Panes::default()),
            runtime: tokio::runtime::Handle::current(),
            state: Mutex::new(State::default()),
            theme: Arc::new(Mutex::new(None)),
            dirty: tokio::sync::Notify::new(),
        })
    }

    fn listed(shared: &Arc<Shared>, kind: Kind, total: u64) -> Listed {
        let transfer = Transfer {
            id: new_uuid(),
            kind,
            host: "h1".into(),
            pane: 7,
            count: 1,
            local: None,
            current: Mutex::new((0, "f".into())),
            remote: Mutex::new(None),
            bytes: AtomicU64::new(0),
            total: AtomicU64::new(total),
            phase: AtomicU8::new(Phase::Sending as u8),
            cancel: watch::channel(false).0,
        };
        Listed::new(shared, transfer).unwrap()
    }

    /// Accept one bytes-protocol connection: check the ticket's HELLO, send WELCOME, then hand
    /// the socket to `serve`.
    async fn server<F, Fut, T>(serve: F) -> (Base, JoinHandle<T>)
    where
        F: FnOnce(tokio_tungstenite::WebSocketStream<tokio::net::TcpStream>) -> Fut + Send + 'static,
        Fut: Future<Output = T> + Send,
        T: Send + 'static,
    {
        let listener = TcpListener::bind("127.0.0.1:0").await.unwrap();
        let port = listener.local_addr().unwrap().port();
        let task = tokio::spawn(async move {
            let (tcp, _) = listener.accept().await.unwrap();
            let mut socket = tokio_tungstenite::accept_hdr_async(tcp, echo_protocol).await.unwrap();
            let hello = match socket.next().await {
                Some(Ok(Message::Binary(b))) => b,
                other => panic!("expected HELLO, got {other:?}"),
            };
            assert_eq!(hello.len(), 17);
            assert_eq!((hello[0], &hello[1..]), (1, &[7u8; 16][..]));
            let mut welcome = vec![0x81];
            welcome.extend([9u8; 16]);
            welcome.extend([0, 0, 0]);
            welcome.extend(60000u32.to_le_bytes());
            welcome.extend(42u64.to_le_bytes());
            socket.send(Message::Binary(welcome.into())).await.unwrap();
            serve(socket).await
        });
        (Base::Loopback(port), task)
    }

    /// The upgrade answers with the bytes subprotocol. The error type is tungstenite's callback API.
    #[allow(clippy::result_large_err)]
    fn echo_protocol(_: &Request, mut response: Response) -> Result<Response, ErrorResponse> {
        response.headers_mut().insert("Sec-WebSocket-Protocol", BYTES_PROTOCOL.parse().unwrap());
        Ok(response)
    }

    fn ticket() -> String {
        URL_SAFE_NO_PAD.encode([7u8; 16])
    }

    async fn close_with(socket: &mut tokio_tungstenite::WebSocketStream<tokio::net::TcpStream>, code: u16) {
        let frame = CloseFrame { code: CloseCode::from(code), reason: "".into() };
        let _ = socket.send(Message::Close(Some(frame))).await;
        // Read the client's answer.
        while let Some(Ok(message)) = socket.next().await {
            if message.is_close() {
                break;
            }
        }
    }

    /// Everything the client streams, until its empty message.
    async fn received(socket: &mut tokio_tungstenite::WebSocketStream<tokio::net::TcpStream>) -> Vec<u8> {
        let mut got = Vec::new();
        while let Some(Ok(message)) = socket.next().await {
            match message {
                Message::Binary(b) if b.is_empty() => break,
                Message::Binary(b) => got.extend_from_slice(&b),
                _ => {}
            }
        }
        got
    }

    #[tokio::test]
    async fn an_upload_streams_the_file_and_succeeds_on_1000() {
        let scratch = Scratch::new("up");
        let content: Vec<u8> = (0..200_000u32).map(|i| (i % 251) as u8).collect();
        let path = scratch.file("data.bin", &content);
        let (base, server) = server(|mut socket| async move {
            let got = received(&mut socket).await;
            close_with(&mut socket, 1000).await;
            got
        })
        .await;
        let shared = shared();
        let mut listed = listed(&shared, Kind::Upload, content.len() as u64);
        let mut cancel = listed.transfer.cancel.subscribe();
        let mut stream = Upstream::new(open_bytes(&base, &ticket()).await.unwrap());
        let source = Source { path, name: "data.bin".into() };
        send_file(&mut listed, &mut stream, &source, content.len() as u64, &mut cancel).await.unwrap();
        stream.finished(Instant::now() + Duration::from_secs(5), &mut cancel).await.unwrap();
        assert_eq!(server.await.unwrap(), content);
        assert_eq!(listed.transfer.json()["bytes"], content.len());
        assert_eq!(shared.state.lock().transfers.len(), 1);
        drop(listed);
        assert!(shared.state.lock().transfers.is_empty(), "a finished transfer leaves the list");
    }

    #[tokio::test]
    async fn an_upload_the_server_refuses_fails_with_its_reason() {
        let scratch = Scratch::new("refused");
        let path = scratch.file("a.txt", b"abc");
        let (base, server) = server(|mut socket| async move {
            received(&mut socket).await;
            let frame = CloseFrame { code: CloseCode::from(1011), reason: "data provider failed".into() };
            let _ = socket.send(Message::Close(Some(frame))).await;
        })
        .await;
        let shared = shared();
        let mut listed = listed(&shared, Kind::Upload, 3);
        let mut cancel = listed.transfer.cancel.subscribe();
        let mut stream = Upstream::new(open_bytes(&base, &ticket()).await.unwrap());
        send_file(&mut listed, &mut stream, &Source { path, name: "a.txt".into() }, 3, &mut cancel).await.unwrap();
        let failure = stream.finished(Instant::now() + Duration::from_secs(5), &mut cancel).await.unwrap_err();
        assert_eq!(failure, Failure::Failed("the server could not read or store the file".into()));
        server.await.unwrap();
    }

    #[tokio::test]
    async fn canceling_an_upload_closes_its_stream_with_1000() {
        let scratch = Scratch::new("cancel-up");
        let size = 8 * 1024 * 1024;
        let path = scratch.file("big.bin", &vec![1u8; size]);
        let (base, server) = server(|mut socket| async move {
            // Read slowly; report how the client ended the stream.
            loop {
                match socket.next().await {
                    Some(Ok(Message::Close(frame))) => return frame.map(|f| u16::from(f.code)),
                    Some(Ok(Message::Binary(b))) if b.is_empty() => panic!("a canceled upload must not end normally"),
                    Some(Ok(_)) => tokio::time::sleep(Duration::from_millis(2)).await,
                    _ => return None,
                }
            }
        })
        .await;
        let shared = shared();
        let mut listed = listed(&shared, Kind::Upload, size as u64);
        let transfer = listed.transfer.clone();
        let mut cancel = transfer.cancel.subscribe();
        let mut stream = Upstream::new(open_bytes(&base, &ticket()).await.unwrap());
        let canceler = tokio::spawn(async move {
            tokio::time::sleep(Duration::from_millis(150)).await;
            transfer.cancel();
        });
        let failure = send_file(&mut listed, &mut stream, &Source { path, name: "big.bin".into() }, size as u64, &mut cancel).await.unwrap_err();
        assert_eq!(failure, Failure::Canceled);
        assert!(listed.transfer.bytes.load(Ordering::Relaxed) < size as u64);
        stream.close().await;
        assert_eq!(server.await.unwrap(), Some(1000));
        canceler.await.unwrap();
    }

    async fn serve_download(chunks: Vec<Vec<u8>>, end: bool, code: u16) -> (Base, JoinHandle<()>) {
        server(move |mut socket| async move {
            for chunk in chunks {
                socket.send(Message::Binary(chunk.into())).await.unwrap();
            }
            if end {
                socket.send(Message::Binary(Vec::new().into())).await.unwrap();
            }
            close_with(&mut socket, code).await;
        })
        .await
    }

    #[tokio::test]
    async fn a_download_replaces_the_destination_only_when_complete() {
        let scratch = Scratch::new("down");
        let dest = scratch.file("report.txt", b"old contents");
        let (base, server) = serve_download(vec![b"hello ".to_vec(), vec![b'x'; 40_000], b" bye".to_vec()], true, 1000).await;
        let shared = shared();
        let mut listed = listed(&shared, Kind::Download, 40_010);
        let mut cancel = listed.transfer.cancel.subscribe();
        let mut socket = open_bytes(&base, &ticket()).await.unwrap();
        let mut partial = Partial::create(&dest).await.unwrap();
        pump_download(&mut socket, &mut partial, &mut listed, &mut cancel).await.unwrap();
        let _ = SinkExt::close(&mut socket).await;
        assert_eq!(partial.commit().await.unwrap(), 40_010);
        let got = std::fs::read(&dest).unwrap();
        assert_eq!((got.len(), &got[..6], &got[got.len() - 4..]), (40_010, &b"hello "[..], &b" bye"[..]));
        assert!(!scratch.0.join("report.txt.partial").exists());
        server.await.unwrap();
    }

    #[tokio::test]
    async fn a_cut_short_download_leaves_the_destination_alone() {
        let scratch = Scratch::new("short");
        let dest = scratch.file("keep.txt", b"keep me");
        // 1000 without the empty message first.
        let (base, server) = serve_download(vec![b"partial".to_vec()], false, 1000).await;
        let shared = shared();
        let mut listed = listed(&shared, Kind::Download, 100);
        let mut cancel = listed.transfer.cancel.subscribe();
        let mut socket = open_bytes(&base, &ticket()).await.unwrap();
        let mut partial = Partial::create(&dest).await.unwrap();
        let failure = pump_download(&mut socket, &mut partial, &mut listed, &mut cancel).await.unwrap_err();
        assert_eq!(failure, Failure::Failed("the download ended before the whole file arrived".into()));
        let _ = SinkExt::close(&mut socket).await;
        partial.discard().await;
        assert_eq!(std::fs::read(&dest).unwrap(), b"keep me");
        assert!(!scratch.0.join("keep.txt.partial").exists());
        server.await.unwrap();
    }

    #[tokio::test]
    async fn canceling_a_download_removes_the_partial_file() {
        let scratch = Scratch::new("cancel-down");
        let dest = scratch.0.join("never.bin");
        let (base, server) = server(|mut socket| async move {
            socket.send(Message::Binary(vec![5u8; 1000].into())).await.unwrap();
            // Wait for the client to give up.
            while let Some(Ok(message)) = socket.next().await {
                if let Message::Close(frame) = message {
                    return frame.map(|f| u16::from(f.code));
                }
            }
            None
        })
        .await;
        let shared = shared();
        let mut listed = listed(&shared, Kind::Download, 1_000_000);
        let transfer = listed.transfer.clone();
        let mut cancel = transfer.cancel.subscribe();
        let mut socket = open_bytes(&base, &ticket()).await.unwrap();
        let mut partial = Partial::create(&dest).await.unwrap();
        assert!(scratch.0.join("never.bin.partial").exists());
        tokio::spawn(async move {
            tokio::time::sleep(Duration::from_millis(100)).await;
            transfer.cancel();
        });
        let failure = pump_download(&mut socket, &mut partial, &mut listed, &mut cancel).await.unwrap_err();
        assert_eq!(failure, Failure::Canceled);
        let frame = CloseFrame { code: CloseCode::Normal, reason: "".into() };
        let _ = socket.close(Some(frame)).await;
        partial.discard().await;
        assert!(!scratch.0.join("never.bin.partial").exists());
        assert!(!dest.exists());
        assert_eq!(server.await.unwrap(), Some(1000));
    }

    #[test]
    fn a_dropped_partial_file_is_removed() {
        let scratch = Scratch::new("drop");
        let dest = scratch.0.join("x.bin");
        let runtime = tokio::runtime::Builder::new_current_thread().enable_all().build().unwrap();
        let partial = runtime.block_on(async {
            let mut partial = Partial::create(&dest).await.unwrap();
            partial.write(b"abc").await.unwrap();
            partial
        });
        drop(runtime);
        assert!(scratch.0.join("x.bin.partial").exists());
        drop(partial);
        assert!(!scratch.0.join("x.bin.partial").exists());
    }
}
