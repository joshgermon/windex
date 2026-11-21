//! `ssh://` hosts: an `ssh.exe -L 127.0.0.1:<port>:<remote socket>` forward per connection
//! (docs/protocol.md §1.6, verified live with Windows OpenSSH and Tailscale SSH).
//!
//! Remote socket discovery and the stderr reason logic are ported from Rex Rune's `src/ssh.rs`
//! (MIT, https://github.com/superlogical/rex-rune).
use super::endpoint::SshTarget;
use anyhow::{Context, Result, bail};
use parking_lot::Mutex;
use std::process::Stdio;
use std::sync::Arc;
use std::time::{Duration, Instant};
use tokio::io::AsyncReadExt;
use tokio::process::{Child, Command};

/// Windows: start console programs without a console window.
#[cfg(windows)]
const CREATE_NO_WINDOW: u32 = 0x0800_0000;

/// Options every ssh run uses: never prompt, trust a new machine's key once (refuse a changed
/// one), notice a dead link within ~45 s.
fn options(target: &SshTarget) -> Vec<String> {
    let mut args: Vec<String> = [
        "-o", "BatchMode=yes",
        "-o", "ConnectTimeout=10",
        "-o", "StrictHostKeyChecking=accept-new",
        "-o", "ServerAliveInterval=15",
        "-o", "ServerAliveCountMax=3",
    ]
    .into_iter()
    .map(str::to_owned)
    .collect();
    if let Some(port) = target.port {
        args.push("-p".into());
        args.push(port.to_string());
    }
    args
}

fn command(ssh: &str) -> Command {
    let mut cmd = Command::new(ssh);
    #[cfg(windows)]
    cmd.creation_flags(CREATE_NO_WINDOW);
    cmd.kill_on_drop(true);
    cmd
}

/// Finds the Rex socket on the remote: the path `rex server status` reports, the login user's
/// runtime directory, macOS Application Support, then the shared locations. Prints
/// `sock <path>` or `none <user>`. Runs under `sh -c '…'`, so it holds no single quote.
const RESOLVE: &str = "p=\"\"; for c in \"$(rex server status --autostart=false 2>/dev/null | grep -o \"unix:///[^[:space:]]*\\.sock\" | head -n 1 | sed \"s#^unix://##\")\" \"${XDG_RUNTIME_DIR:-/run/user/$(id -u)}/rex/server.sock\" \"$HOME/Library/Application Support/rex/server.sock\" /run/rex/server.sock /var/run/rex/server.sock; do if [ -n \"$c\" ] && [ -S \"$c\" ]; then p=\"$c\"; break; fi; done; if [ -n \"$p\" ]; then printf \"sock %s\\n\" \"$p\"; else printf \"none %s\\n\" \"$(id -un)\"; fi";

fn check_script(socket: &str) -> String {
    format!("if [ -S \"{socket}\" ]; then printf \"sock %s\\n\" \"{socket}\"; else printf \"missing %s\\n\" \"{socket}\"; fi")
}

/// Ask the remote where its Rex server listens (or whether the named socket exists).
pub async fn resolve_socket(ssh: &str, target: &SshTarget) -> Result<String> {
    if let Some(socket) = &target.socket
        && socket.contains(['"', '\'', '`', '$', '\\'])
    {
        bail!("the socket path {socket:?} has characters ssh:// endpoints don't support");
    }
    let script = target.socket.as_deref().map(check_script).unwrap_or_else(|| RESOLVE.to_owned());
    let mut child = command(ssh)
        .args(options(target))
        .arg("--")
        .arg(target.destination())
        .arg(format!("sh -c '{script}'"))
        .stdin(Stdio::null())
        .stdout(Stdio::piped())
        .stderr(Stdio::piped())
        .spawn()
        .map_err(|e| spawn_error(ssh, e))?;
    #[cfg(windows)]
    job::adopt(&child);
    // Read stderr as it comes: if ssh waits (a Tailscale SSH check asks the user to visit a
    // link), its last words are the reason to show.
    let stderr = Arc::new(Mutex::new(String::new()));
    if let Some(pipe) = child.stderr.take() {
        tokio::spawn(drain(pipe, stderr.clone()));
    }
    let mut stdout = String::new();
    let read = async {
        if let Some(mut pipe) = child.stdout.take() {
            pipe.read_to_string(&mut stdout).await?;
        }
        child.wait().await
    };
    let destination = target.destination();
    let status = match tokio::time::timeout(Duration::from_secs(30), read).await {
        Ok(status) => status.with_context(|| format!("ssh {destination}"))?,
        Err(_) => {
            let why = last_line(&stderr.lock()).unwrap_or_else(|| "no answer within 30 s".into());
            bail!("ssh {destination}: {why}");
        }
    };
    if !status.success() {
        // Let the stderr pump catch up with ssh's last words.
        tokio::time::sleep(Duration::from_millis(50)).await;
        let why = last_line(&stderr.lock()).unwrap_or_else(|| format!("exited ({status})"));
        bail!("ssh {destination}: {why}");
    }
    parse_resolve(&stdout, &destination)
}

fn parse_resolve(stdout: &str, destination: &str) -> Result<String> {
    for line in stdout.lines() {
        if let Some(path) = line.strip_prefix("sock ") {
            return Ok(path.trim().to_owned());
        }
        if let Some(user) = line.strip_prefix("none ") {
            bail!("No Rex server found for {} on {destination}. Start one there with `rex server start`.", user.trim());
        }
        if let Some(path) = line.strip_prefix("missing ") {
            bail!("{} is not a Rex socket on {destination}", path.trim());
        }
    }
    bail!("ssh {destination} gave no answer about its Rex socket")
}

/// A running forward. Dropping it kills `ssh.exe`.
pub struct Tunnel {
    child: Child,
    pub port: u16,
    stderr: Arc<Mutex<String>>,
    destination: String,
}

impl Tunnel {
    /// Forward `remote_socket` on `target` to a free loopback port and wait until it accepts.
    pub async fn open(ssh: &str, target: &SshTarget, remote_socket: &str) -> Result<Tunnel> {
        // A port taken between probing and ssh binding it fails the forward
        // (ExitOnForwardFailure); one retry on a fresh port covers that race.
        match Self::open_once(ssh, target, remote_socket).await {
            Err(error) if format!("{error:#}").contains("forwarding") => Self::open_once(ssh, target, remote_socket).await,
            other => other,
        }
    }

    async fn open_once(ssh: &str, target: &SshTarget, remote_socket: &str) -> Result<Tunnel> {
        let port = free_port()?;
        let mut child = command(ssh)
            .args(options(target))
            .args(["-N", "-T", "-o", "ExitOnForwardFailure=yes"])
            .arg("-L")
            .arg(format!("127.0.0.1:{port}:{remote_socket}"))
            .arg("--")
            .arg(target.destination())
            .stdin(Stdio::null())
            .stdout(Stdio::null())
            .stderr(Stdio::piped())
            .spawn()
            .map_err(|e| spawn_error(ssh, e))?;
        #[cfg(windows)]
        job::adopt(&child);
        let stderr = Arc::new(Mutex::new(String::new()));
        if let Some(pipe) = child.stderr.take() {
            tokio::spawn(drain(pipe, stderr.clone()));
        }
        let mut tunnel = Tunnel { child, port, stderr, destination: target.destination() };
        let started = Instant::now();
        loop {
            if let Some(status) = tunnel.child.try_wait()? {
                bail!("{}", tunnel.exit_message(Some(status)));
            }
            if tokio::net::TcpStream::connect(("127.0.0.1", port)).await.is_ok() {
                return Ok(tunnel);
            }
            if started.elapsed() > Duration::from_secs(20) {
                let why = last_line(&tunnel.stderr.lock()).unwrap_or_else(|| "the forward did not open in time".into());
                bail!("ssh {}: {why}", tunnel.destination);
            }
            tokio::time::sleep(Duration::from_millis(100)).await;
        }
    }

    /// Resolves when ssh exits, with the reason to show.
    pub async fn closed(&mut self) -> String {
        let status = self.child.wait().await.ok();
        // Let the stderr pump catch up with ssh's last words.
        tokio::time::sleep(Duration::from_millis(50)).await;
        self.exit_message(status)
    }

    pub fn kill(&mut self) {
        let _ = self.child.start_kill();
    }

    fn exit_message(&self, status: Option<std::process::ExitStatus>) -> String {
        let stderr = self.stderr.lock();
        match last_line(&stderr) {
            Some(line) => format!("ssh {}: {line}", self.destination),
            None => match status {
                Some(status) if status.success() => format!("ssh {} closed the forward", self.destination),
                Some(status) => format!("ssh {} exited ({status})", self.destination),
                None => format!("ssh {} ended", self.destination),
            },
        }
    }
}

/// A loopback port nobody listens on right now.
fn free_port() -> Result<u16> {
    let listener = std::net::TcpListener::bind(("127.0.0.1", 0)).context("pick a local port for ssh")?;
    Ok(listener.local_addr()?.port())
}

async fn drain(mut pipe: tokio::process::ChildStderr, sink: Arc<Mutex<String>>) {
    let mut buffer = [0u8; 1024];
    while let Ok(n) = pipe.read(&mut buffer).await {
        if n == 0 {
            break;
        }
        let mut text = sink.lock();
        text.push_str(&String::from_utf8_lossy(&buffer[..n]));
        if text.len() > 8192 {
            let mut cut = text.len() - 4096;
            while !text.is_char_boundary(cut) {
                cut += 1;
            }
            text.drain(..cut);
        }
    }
}

/// The last line ssh printed that explains something, without its `ssh: ` prefix. A server's
/// parting words (a Tailscale SSH refusal) beat the generic "Connection closed" after them.
pub fn last_line(stderr: &str) -> Option<String> {
    let lines: Vec<&str> = stderr
        .lines()
        .map(str::trim)
        .filter(|l| !l.is_empty() && !l.starts_with("Warning: Permanently added"))
        .collect();
    let generic = |l: &str| {
        l.starts_with("Connection closed by")
            || l.starts_with("Connection to ")
            || l.starts_with("Transferred:")
            || l.starts_with("Bytes per second:")
            || l.starts_with("Killed by signal")
    };
    lines
        .iter()
        .rfind(|l| !generic(l))
        .or(lines.last())
        .map(|l| l.strip_prefix("ssh: ").unwrap_or(l).to_owned())
}

fn spawn_error(ssh: &str, error: std::io::Error) -> anyhow::Error {
    if error.kind() == std::io::ErrorKind::NotFound {
        anyhow::anyhow!("ssh isn't installed ({ssh} not found); add the Windows OpenSSH client")
    } else {
        anyhow::Error::from(error).context(format!("run {ssh}"))
    }
}

/// A job object that kills every ssh.exe this process started when the process ends, even if
/// it crashes, so no forward outlives Windex.
#[cfg(windows)]
mod job {
    use std::sync::OnceLock;
    use windows_sys::Win32::Foundation::HANDLE;
    use windows_sys::Win32::System::JobObjects::{
        AssignProcessToJobObject, CreateJobObjectW, JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE,
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION, JobObjectExtendedLimitInformation, SetInformationJobObject,
    };

    struct Job(HANDLE);
    // SAFETY: a job handle is a process-wide kernel handle usable from any thread.
    unsafe impl Send for Job {}
    unsafe impl Sync for Job {}

    fn job() -> Option<&'static Job> {
        static JOB: OnceLock<Option<Job>> = OnceLock::new();
        JOB.get_or_init(|| unsafe {
            let handle = CreateJobObjectW(std::ptr::null(), std::ptr::null());
            if handle.is_null() {
                return None;
            }
            let mut info: JOBOBJECT_EXTENDED_LIMIT_INFORMATION = std::mem::zeroed();
            info.BasicLimitInformation.LimitFlags = JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE;
            let ok = SetInformationJobObject(
                handle,
                JobObjectExtendedLimitInformation,
                &info as *const _ as *const std::ffi::c_void,
                std::mem::size_of::<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>() as u32,
            );
            // The handle stays open for the life of the process; closing it is the kill.
            (ok != 0).then_some(Job(handle))
        })
        .as_ref()
    }

    pub fn adopt(child: &tokio::process::Child) {
        if let (Some(job), Some(process)) = (job(), child.raw_handle()) {
            unsafe { AssignProcessToJobObject(job.0, process as HANDLE) };
        }
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn resolve_answers() {
        assert_eq!(parse_resolve("sock /run/user/1000/rex/server.sock\n", "d").unwrap(), "/run/user/1000/rex/server.sock");
        assert!(format!("{:#}", parse_resolve("none joshua\n", "d").unwrap_err()).contains("No Rex server"));
        assert!(parse_resolve("missing /x.sock\n", "d").is_err());
        assert!(parse_resolve("", "d").is_err());
    }

    #[test]
    fn last_meaningful_line() {
        let stderr = "Warning: Permanently added 'dasher' (ED25519) to the list of known hosts.\r\n\
                      # Tailscale SSH requires an additional check.\r\n\
                      tailnet policy does not permit you to SSH to this node\r\n\
                      Connection closed by 100.64.0.7 port 22\r\n";
        assert_eq!(last_line(stderr).as_deref(), Some("tailnet policy does not permit you to SSH to this node"));
        assert_eq!(last_line("ssh: Could not resolve hostname nope: No such host is known.\n").as_deref(),
            Some("Could not resolve hostname nope: No such host is known."));
        assert_eq!(last_line("\n\n"), None);
    }
}
