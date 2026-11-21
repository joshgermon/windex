//! WebSocket connections to a Rex server (docs/protocol.md §1–§2.1): TCP (+ rustls for wss),
//! the upgrade with the required subprotocol, and a check that the server echoed it.
use anyhow::{Context, Result, ensure};
use std::time::Duration;
use tokio::net::TcpStream;
use tokio_tungstenite::tungstenite::client::IntoClientRequest;
use tokio_tungstenite::tungstenite::protocol::WebSocketConfig;
use tokio_tungstenite::{MaybeTlsStream, WebSocketStream};

pub type Socket = WebSocketStream<MaybeTlsStream<TcpStream>>;

pub const CONTROL_PATH: &str = "/api/control";
pub const CONTROL_PROTOCOL: &str = "rex.control.v1";
pub const DATA_PATH: &str = "/api/data";
pub const DATA_PROTOCOL: &str = "rex.data.terminal.v1";

/// Where a connection's sockets go: the loopback end of an ssh forward, or the server itself.
#[derive(Debug, Clone, PartialEq, Eq)]
pub enum Base {
    Loopback(u16),
    /// `ws://` or `wss://` origin.
    Direct(url::Url),
}

impl Base {
    fn url(&self, path: &str) -> Result<url::Url> {
        let mut url = match self {
            Base::Loopback(port) => url::Url::parse(&format!("ws://127.0.0.1:{port}"))?,
            Base::Direct(url) => url.clone(),
        };
        url.set_path(path);
        url.set_query(None);
        Ok(url)
    }
}

/// Open a WebSocket to `path` with `protocol`, within 10 s.
pub async fn connect(base: &Base, path: &str, protocol: &str) -> Result<Socket> {
    tokio::time::timeout(Duration::from_secs(10), open(base, path, protocol))
        .await
        .context("the Rex WebSocket connection timed out")?
}

async fn open(base: &Base, path: &str, protocol: &str) -> Result<Socket> {
    let url = base.url(path)?;
    let host = url.host_str().context("endpoint needs a host")?.trim_start_matches('[').trim_end_matches(']').to_owned();
    let port = url.port_or_known_default().context("endpoint needs a port")?;
    let tcp = TcpStream::connect((host.as_str(), port)).await.with_context(|| format!("connect to {host}:{port}"))?;
    tcp.set_nodelay(true)?;
    if matches!(base, Base::Direct(_)) {
        // A server that vanishes with the network never sends a reset; probe idle links so a
        // pending read fails and the host reconnects.
        let keepalive = socket2::TcpKeepalive::new()
            .with_time(Duration::from_secs(15))
            .with_interval(Duration::from_secs(5))
            .with_retries(3);
        socket2::SockRef::from(&tcp).set_tcp_keepalive(&keepalive)?;
    }
    let mut request = url.as_str().into_client_request()?;
    request.headers_mut().insert("Sec-WebSocket-Protocol", protocol.parse()?);
    // Control messages are at most 1 MiB (the server's limit); data messages at most 64 KiB.
    let config = WebSocketConfig::default().max_message_size(Some(16 << 20)).max_frame_size(Some(16 << 20));
    let (socket, response) = tokio_tungstenite::client_async_tls_with_config(request, tcp, Some(config), None)
        .await
        .with_context(|| format!("WebSocket upgrade at {url}"))?;
    let echoed = response.headers().get("Sec-WebSocket-Protocol").and_then(|h| h.to_str().ok());
    ensure!(echoed == Some(protocol), "the server did not negotiate {protocol}; is this a Rex server?");
    Ok(socket)
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn urls() {
        assert_eq!(Base::Loopback(47297).url(CONTROL_PATH).unwrap().as_str(), "ws://127.0.0.1:47297/api/control");
        let direct = Base::Direct(url::Url::parse("wss://rex.example.ts.net:443/").unwrap());
        assert_eq!(direct.url(DATA_PATH).unwrap().as_str(), "wss://rex.example.ts.net/api/data");
    }
}
