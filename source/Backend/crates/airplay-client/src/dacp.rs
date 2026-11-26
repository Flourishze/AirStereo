use crate::events_channel::{map_remote_command_str, normalize_volume_percent, ReceiverEvent};
use airplay_core::Result;
use parking_lot::Mutex;
use std::collections::HashMap;
use std::net::{IpAddr, Ipv4Addr};
use std::sync::Arc;
use std::time::Duration;
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::TcpListener;
use tokio::sync::{mpsc, watch};

/// Local DACP HTTP/1.1 reverse-control server matching decompiled `dacp.c`
/// (`0x1403bffd0`, `0x1400667e0`, `0x140068870`):
/// - Registers `(active_remote, ip)` peers
/// - Authenticates `Active-Remote` header (403 Forbidden if unregistered)
/// - Dispatches `/ctrl-int/1/{play,pause,playpause,nextitem,previtem,stop,setproperty}`
/// - Responds `HTTP/1.1 204 No Content\r\nDAAP-Server: AirStereo2\r\n\r\n`
pub struct DacpServer {
    pub port: u16,
    registry: Arc<Mutex<HashMap<String, IpAddr>>>,
    stop_tx: watch::Sender<bool>,
}

impl DacpServer {
    pub async fn start(event_tx: mpsc::UnboundedSender<ReceiverEvent>) -> Result<Self> {
        let listener = TcpListener::bind((Ipv4Addr::UNSPECIFIED, 0)).await?;
        let port = listener.local_addr()?.port();
        let registry: Arc<Mutex<HashMap<String, IpAddr>>> = Arc::new(Mutex::new(HashMap::new()));
        let (stop_tx, mut stop_rx) = watch::channel(false);

        let reg_c = registry.clone();
        tokio::spawn(async move {
            loop {
                tokio::select! {
                    _ = stop_rx.changed() => break,
                    res = listener.accept() => {
                        let (mut stream, peer_addr) = match res {
                            Ok(v) => v,
                            Err(_) => {
                                tokio::time::sleep(Duration::from_millis(100)).await;
                                continue;
                            }
                        };
                        let reg = reg_c.clone();
                        let tx = event_tx.clone();
                        tokio::spawn(async move {
                            let mut buf = Vec::with_capacity(1024);
                            let mut chunk = [0u8; 1024];
                            loop {
                                let n = match tokio::time::timeout(Duration::from_secs(30), stream.read(&mut chunk)).await {
                                    Ok(Ok(0)) | Err(_) => break,
                                    Ok(Ok(n)) => n,
                                    Ok(Err(_)) => break,
                                };
                                buf.extend_from_slice(&chunk[..n]);
                                if buf.len() > 8192 {
                                    let _ = stream.write_all(
                                        b"HTTP/1.1 400 Bad Request\r\nContent-Length: 0\r\nConnection: close\r\n\r\n"
                                    ).await;
                                    break;
                                }
                                while let Some(hdr_end) = buf.windows(4).position(|w| w == b"\r\n\r\n") {
                                    let head = String::from_utf8_lossy(&buf[..hdr_end]).to_string();
                                    buf.drain(..hdr_end + 4);
                                    let resp = handle_dacp_request(&head, peer_addr.ip(), &reg, &tx);
                                    if stream.write_all(resp).await.is_err() {
                                        return;
                                    }
                                }
                            }
                        });
                    }
                }
            }
        });

        Ok(Self {
            port,
            registry,
            stop_tx,
        })
    }

    pub fn register_peer(&self, active_remote: String, ip: IpAddr) {
        let mut reg = self.registry.lock();
        reg.retain(|_, v| *v != ip);
        reg.insert(active_remote, ip);
    }

    pub fn stop(&self) {
        let _ = self.stop_tx.send(true);
    }
}

impl Drop for DacpServer {
    fn drop(&mut self) {
        self.stop();
    }
}

fn handle_dacp_request(
    head: &str,
    fallback_ip: IpAddr,
    registry: &Arc<Mutex<HashMap<String, IpAddr>>>,
    tx: &mpsc::UnboundedSender<ReceiverEvent>,
) -> &'static [u8] {
    let mut lines = head.split("\r\n");
    let first = lines.next().unwrap_or("");
    let uri = first.split_whitespace().nth(1).unwrap_or("");

    let mut active_remote: Option<String> = None;
    for line in lines {
        if let Some((k, v)) = line.split_once(':') {
            if k.trim().eq_ignore_ascii_case("active-remote") {
                active_remote = Some(v.trim().to_string());
                break;
            }
        }
    }

    let ip = {
        let reg = registry.lock();
        match active_remote.as_deref().and_then(|ar| reg.get(ar).copied()) {
            Some(ip) => ip,
            None => {
                // Allow if registry is empty only for local debug; otherwise enforce 403 Forbidden (dacp.c L2109)
                if !reg.is_empty() {
                    return b"HTTP/1.1 403 Forbidden\r\nContent-Length: 0\r\n\r\n";
                }
                fallback_ip
            }
        }
    };

    let (path, query) = uri.split_once('?').unwrap_or((uri, ""));
    let path_low = path.to_ascii_lowercase();
    if let Some(cmd_name) = path_low.strip_prefix("/ctrl-int/1/") {
        if cmd_name == "setproperty" {
            for pair in query.split('&') {
                if let Some((k, v)) = pair.split_once('=') {
                    if k.eq_ignore_ascii_case("dmcp.volume") {
                        if let Ok(fv) = v.parse::<f32>() {
                            let pct = fv.clamp(0.0, 100.0).round() as u8;
                            let _ = tx.send(ReceiverEvent::Volume(ip, pct));
                        }
                    } else if k.eq_ignore_ascii_case("dmcp.device-volume") {
                        if let Ok(db) = v.parse::<f32>() {
                            let pct = normalize_volume_percent(db);
                            let _ = tx.send(ReceiverEvent::Volume(ip, pct));
                        }
                    }
                }
            }
        } else if let Some(cmd) = map_remote_command_str(cmd_name) {
            let _ = tx.send(ReceiverEvent::RemoteCommand(ip, cmd));
        }
    }

    b"HTTP/1.1 204 No Content\r\nDAAP-Server: AirStereo2\r\n\r\n"
}
