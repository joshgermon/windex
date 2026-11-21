//! Tailnet discovery (docs/protocol.md §1.4): `tailscale status --json`, then a probe of each
//! online machine for a Rex server on 443 and 7297 (control upgrade + hello, never attaching).
//! Emits one DISCOVERY event. Parsing ported from Rex Rune's `src/tailnet.rs` (MIT).
use super::Shared;
use super::control::{ClientInfo, Control};
use super::transport::{self, Base, CONTROL_PATH, CONTROL_PROTOCOL};
use crate::events::EventKind;
use anyhow::{Context, Result};
use serde::Deserialize;
use serde_json::{Value, json};
use std::collections::HashMap;
use std::time::Duration;

#[derive(Deserialize)]
#[serde(rename_all = "PascalCase")]
struct Status {
    #[serde(rename = "Self")]
    this: Option<Node>,
    #[serde(default)]
    peer: HashMap<String, Node>,
}

#[derive(Deserialize, Default)]
#[serde(rename_all = "PascalCase", default)]
struct Node {
    host_name: String,
    #[serde(rename = "DNSName")]
    dns_name: String,
    #[serde(rename = "OS")]
    os: String,
    online: bool,
    #[serde(rename = "sshHostKeys")]
    ssh_host_keys: Option<Vec<String>>,
    #[serde(rename = "TailscaleIPs")]
    tailscale_ips: Option<Vec<String>>,
}

#[derive(Debug, Clone, PartialEq)]
struct Machine {
    name: String,
    /// MagicDNS name (no trailing dot), or the first IP without MagicDNS.
    address: String,
    dns: Option<String>,
    ips: Vec<String>,
    os: String,
    online: bool,
    this: bool,
    tailscale_ssh: bool,
}

fn machines(json: &str) -> Result<Vec<Machine>> {
    let status: Status = serde_json::from_str(json).context("read tailscale status")?;
    let convert = |node: Node, this: bool| -> Option<Machine> {
        let dns = node.dns_name.trim_end_matches('.').to_owned();
        let ips = node.tailscale_ips.unwrap_or_default();
        let ip = ips.iter().find(|ip| !ip.contains(':')).or(ips.first()).cloned();
        let (name, address) = if dns.is_empty() {
            let ip = ip?;
            let name = if node.host_name.is_empty() || node.host_name == "localhost" { ip.clone() } else { node.host_name.clone() };
            (name, ip)
        } else {
            (dns.split('.').next().unwrap_or_default().to_owned(), dns.clone())
        };
        Some(Machine {
            name,
            address,
            dns: (!dns.is_empty()).then_some(dns),
            ips,
            os: node.os,
            online: this || node.online,
            this,
            tailscale_ssh: node.ssh_host_keys.is_some_and(|k| !k.is_empty()),
        })
    };
    let mut peers: Vec<Machine> = status
        .peer
        .into_values()
        .filter(|n| n.os != "iOS")
        .filter_map(|n| convert(n, false))
        .collect();
    peers.sort_by(|a, b| a.name.to_lowercase().cmp(&b.name.to_lowercase()).then_with(|| a.address.cmp(&b.address)));
    Ok(status.this.and_then(|n| convert(n, true)).into_iter().chain(peers).collect())
}

/// Endpoints a tailnet Rex server may answer at, in the order clients prefer them.
fn rex_endpoints(address: &str) -> [String; 3] {
    let host = if address.contains(':') { format!("[{address}]") } else { address.to_owned() };
    [format!("https://{host}"), format!("https://{host}:7297"), format!("http://{host}:7297")]
}

/// Whether a Rex server answers at `endpoint`: upgrade, hello, WELCOME, close.
async fn probe_endpoint(shared: &Shared, endpoint: &str) -> Result<()> {
    let super::endpoint::Endpoint::Direct(url) = super::endpoint::Endpoint::parse(endpoint)? else {
        anyhow::bail!("not a direct endpoint");
    };
    let socket = transport::connect(&Base::Direct(url), CONTROL_PATH, CONTROL_PROTOCOL).await?;
    let info = ClientInfo {
        name: format!("{} (discovery)", shared.options.client_name),
        version: shared.options.client_version.clone(),
        label: shared.options.label.clone(),
    };
    let (sink, _events) = tokio::sync::mpsc::unbounded_channel();
    let (control, _) = Control::open(socket, &info, None, sink, shared.emitter.clone()).await?;
    control.close();
    Ok(())
}

/// The first endpoint (in preference order) that answers, probing all at once.
async fn probe(shared: &Shared, machine: &Machine) -> Option<String> {
    let endpoints = rex_endpoints(&machine.address);
    let checks = endpoints.iter().map(|e| async move {
        tokio::time::timeout(Duration::from_secs(4), probe_endpoint(shared, e)).await.ok().and_then(Result::ok).map(|_| e.clone())
    });
    futures_util::future::join_all(checks).await.into_iter().flatten().next()
}

pub async fn run(shared: &Shared) -> Result<Value, String> {
    let output = tokio::process::Command::new(shared.tailscale_path())
        .args(["status", "--json"])
        .stdin(std::process::Stdio::null())
        .kill_on_drop(true)
        .creation_flags_no_window()
        .output()
        .await
        .map_err(|e| {
            if e.kind() == std::io::ErrorKind::NotFound {
                "Tailscale isn't installed on this machine".to_owned()
            } else {
                format!("tailscale status: {e}")
            }
        })?;
    if !output.status.success() {
        let stderr = String::from_utf8_lossy(&output.stderr);
        return Err(format!("tailscale status: {}", stderr.lines().last().unwrap_or("failed").trim()));
    }
    let list = machines(&String::from_utf8_lossy(&output.stdout)).map_err(|e| format!("{e:#}"))?;
    let probes = list.iter().map(|m| async move {
        if m.online && !m.this { probe(shared, m).await } else { None }
    });
    let found = futures_util::future::join_all(probes).await;
    let machines: Vec<Value> = list
        .iter()
        .zip(found)
        .map(|(m, rex)| {
            json!({
                "name": m.name,
                "dns": m.dns,
                "ips": m.ips,
                "os": m.os,
                "online": m.online,
                "self": m.this,
                "rex": rex.map(|endpoint| json!({"endpoint": endpoint, "reachable": true})),
                "ssh": {"tailscale_ssh": m.tailscale_ssh, "endpoint": format!("ssh://{}", m.address)},
            })
        })
        .collect();
    let count = machines.len();
    shared.emitter.emit_value(EventKind::Discovery, 0, &json!({ "machines": machines }));
    Ok(json!({ "machines": count }))
}

/// `CREATE_NO_WINDOW` for console tools on Windows.
trait NoWindow {
    fn creation_flags_no_window(&mut self) -> &mut Self;
}

impl NoWindow for tokio::process::Command {
    fn creation_flags_no_window(&mut self) -> &mut Self {
        #[cfg(windows)]
        self.creation_flags(0x0800_0000);
        self
    }
}

#[cfg(test)]
mod tests {
    use super::*;

    #[test]
    fn parses_status() {
        let json = r#"{"Self":{"HostName":"desk","DNSName":"desk.tail1234.ts.net.","OS":"windows","Online":true,"TailscaleIPs":["100.1.1.1","fd7a::1"]},
            "Peer":{"k1":{"HostName":"dasher","DNSName":"dasher.tail1234.ts.net.","OS":"linux","Online":true,"sshHostKeys":["ssh-ed25519 x"],"TailscaleIPs":["100.64.0.7"]},
                    "k2":{"HostName":"phone","DNSName":"phone.tail1234.ts.net.","OS":"iOS","Online":true},
                    "k3":{"HostName":"Box","DNSName":"","OS":"linux","Online":false,"TailscaleIPs":["fd7a::9","100.2.2.2"]}}}"#;
        let m = machines(json).unwrap();
        assert_eq!(m.len(), 3, "iOS skipped");
        assert!(m[0].this && m[0].name == "desk");
        assert_eq!((m[1].name.as_str(), m[1].address.as_str(), m[1].tailscale_ssh), ("Box", "100.2.2.2", false));
        assert_eq!((m[2].name.as_str(), m[2].dns.as_deref(), m[2].tailscale_ssh), ("dasher", Some("dasher.tail1234.ts.net"), true));
        assert_eq!(rex_endpoints("dasher.ts.net")[1], "https://dasher.ts.net:7297");
        assert_eq!(rex_endpoints("fd7a::1")[2], "http://[fd7a::1]:7297");
    }
}
