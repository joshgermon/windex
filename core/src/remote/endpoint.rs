//! Host endpoints: where a Rex server listens and how to reach it (docs/protocol.md §1.2).
//!
//! - `ssh://[user@]host[:port][/socket]` — an `ssh.exe -L` forward of the remote Unix socket
//!   to a loopback TCP port. Without a path the socket is discovered on the remote.
//! - `https://host[:port]` / `wss://` — TCP + TLS straight to the server.
//! - `http://host[:port]` / `ws://` — plain TCP.
//! - `user@host` or a bare `host` (no scheme) means `ssh://`.
use anyhow::{Context, Result, bail};
use percent_encoding::percent_decode_str;

#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Endpoint {
    Ssh(SshTarget),
    /// A WebSocket base URL (`ws://` or `wss://`, no path) reached directly.
    Direct(url::Url),
}

#[derive(Debug, Clone, PartialEq, Eq)]
pub struct SshTarget {
    pub user: Option<String>,
    /// Host name or address (IPv6 without brackets).
    pub host: String,
    pub port: Option<u16>,
    /// Absolute path of the server's Unix socket on the remote, if the URL names one.
    pub socket: Option<String>,
}

impl SshTarget {
    /// `user@host` as ssh takes it.
    pub fn destination(&self) -> String {
        match &self.user {
            Some(user) => format!("{user}@{}", self.host),
            None => self.host.clone(),
        }
    }
}

impl Endpoint {
    pub fn parse(text: &str) -> Result<Self> {
        let text = text.trim();
        if text.is_empty() {
            bail!("the endpoint is empty");
        }
        let with_scheme = if text.contains("://") { text.to_owned() } else { format!("ssh://{text}") };
        let url = url::Url::parse(&with_scheme).with_context(|| format!("invalid endpoint {text:?}"))?;
        let host = url.host_str().filter(|h| !h.is_empty()).with_context(|| format!("endpoint {text:?} names no host"))?;
        let host = host.trim_start_matches('[').trim_end_matches(']').to_owned();
        match url.scheme() {
            "ssh" => {
                let user = Some(url.username())
                    .filter(|u| !u.is_empty())
                    .map(|u| percent_decode_str(u).decode_utf8_lossy().into_owned());
                let path = percent_decode_str(url.path()).decode_utf8_lossy().into_owned();
                let socket = Some(path).filter(|p| p.len() > 1 && p.starts_with('/'));
                if url.password().is_some() {
                    bail!("ssh endpoints take no password; use ssh-agent or Tailscale SSH");
                }
                Ok(Endpoint::Ssh(SshTarget { user, host, port: url.port(), socket }))
            }
            scheme @ ("https" | "wss" | "http" | "ws") => {
                let ws = if matches!(scheme, "https" | "wss") { "wss" } else { "ws" };
                let port = url.port_or_known_default().context("endpoint needs a port")?;
                let host = if host.contains(':') { format!("[{host}]") } else { host };
                let base = url::Url::parse(&format!("{ws}://{host}:{port}"))?;
                Ok(Endpoint::Direct(base))
            }
            "unix" => bail!("unix:// endpoints are not supported on Windows; use ssh:// to reach a remote socket"),
            other => bail!("unsupported endpoint scheme {other}://"),
        }
    }

    /// The model's `kind`: `ssh`, `https` or `http`.
    pub fn kind(&self) -> &'static str {
        match self {
            Endpoint::Ssh(_) => "ssh",
            Endpoint::Direct(url) if url.scheme() == "wss" => "https",
            Endpoint::Direct(_) => "http",
        }
    }
}

/// The model `kind` of an endpoint string, `ssh` when it can't be parsed.
pub fn kind_of(endpoint: &str) -> &'static str {
    Endpoint::parse(endpoint).map(|e| e.kind()).unwrap_or("ssh")
}

#[cfg(test)]
mod tests {
    use super::*;

    fn ssh(text: &str) -> SshTarget {
        match Endpoint::parse(text).unwrap() {
            Endpoint::Ssh(target) => target,
            other => panic!("not ssh: {other:?}"),
        }
    }

    #[test]
    fn ssh_urls() {
        let t = ssh("ssh://joshua@dasher");
        assert_eq!((t.user.as_deref(), t.host.as_str(), t.port, t.socket.as_deref()), (Some("joshua"), "dasher", None, None));
        assert_eq!(t.destination(), "joshua@dasher");

        let t = ssh("ssh://dasher:2222/run/user/1000/rex/server.sock");
        assert_eq!((t.user, t.port, t.socket.as_deref()), (None, Some(2222), Some("/run/user/1000/rex/server.sock")));

        let t = ssh("ssh://dasher/");
        assert_eq!(t.socket, None);

        let t = ssh("ssh://first.last%40corp@box.tail1234.ts.net");
        assert_eq!(t.user.as_deref(), Some("first.last@corp"));
        assert_eq!(t.host, "box.tail1234.ts.net");

        let t = ssh("ssh://me@[fd7a:115c::1]:22");
        assert_eq!((t.host.as_str(), t.port), ("fd7a:115c::1", Some(22)));
        assert_eq!(t.destination(), "me@fd7a:115c::1");
    }

    #[test]
    fn schemeless_means_ssh() {
        assert_eq!(ssh("joshua@dasher").destination(), "joshua@dasher");
        assert_eq!(ssh("  dasher ").destination(), "dasher");
    }

    #[test]
    fn direct_urls() {
        let direct = |text: &str| match Endpoint::parse(text).unwrap() {
            Endpoint::Direct(url) => url.to_string(),
            other => panic!("not direct: {other:?}"),
        };
        assert_eq!(direct("https://rex.tail1234.ts.net"), "wss://rex.tail1234.ts.net/");
        assert_eq!(direct("https://dasher:7297/ignored?x=1"), "wss://dasher:7297/");
        assert_eq!(direct("http://dasher:7297"), "ws://dasher:7297/");
        assert_eq!(direct("ws://127.0.0.1"), "ws://127.0.0.1/");
        assert_eq!(direct("http://[::1]:7297"), "ws://[::1]:7297/");
        assert_eq!(Endpoint::parse("https://x").unwrap().kind(), "https");
        assert_eq!(Endpoint::parse("http://x").unwrap().kind(), "http");
        assert_eq!(kind_of("ssh://x"), "ssh");
    }

    #[test]
    fn rejects() {
        for bad in ["", "unix:///run/rex.sock", "ftp://x", "ssh://", "ssh://u:pw@host"] {
            assert!(Endpoint::parse(bad).is_err(), "{bad:?} should fail");
        }
    }
}
