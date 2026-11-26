use airplay_core::Result;
use std::net::Ipv4Addr;
use std::sync::Arc;
use std::time::{SystemTime, UNIX_EPOCH};
use tokio::net::UdpSocket;

const NTP_EPOCH_OFFSET: u64 = 2_208_988_800;

/// Convert Unix epoch nanoseconds to 64-bit NTP timestamp (32-bit seconds + 32-bit fraction).
pub fn unix_to_ntp_timestamp(unix_nanos: u128) -> u64 {
    let secs = (unix_nanos / 1_000_000_000) as u64 + NTP_EPOCH_OFFSET;
    let sub_ns = (unix_nanos % 1_000_000_000) as u64;
    let frac = ((sub_ns << 32) / 1_000_000_000) & 0xFFFF_FFFF;
    (secs << 32) | frac
}

pub fn now_ntp_timestamp() -> u64 {
    let nanos = SystemTime::now()
        .duration_since(UNIX_EPOCH)
        .unwrap_or_default()
        .as_nanos();
    unix_to_ntp_timestamp(nanos)
}

/// Classic RAOP / AirPlay 1 UDP NTP timing responder (handles 0x52 Timing Request -> 0x53 Timing Response).
pub struct NtpTimingServer {
    pub local_port: u16,
    stop_tx: tokio::sync::watch::Sender<bool>,
}

impl NtpTimingServer {
    pub async fn start() -> Result<Self> {
        let sock = Arc::new(UdpSocket::bind((Ipv4Addr::UNSPECIFIED, 0)).await?);
        let local_port = sock.local_addr()?.port();
        let (stop_tx, mut stop_rx) = tokio::sync::watch::channel(false);

        tokio::spawn(async move {
            let mut buf = [0u8; 64];
            loop {
                tokio::select! {
                    _ = stop_rx.changed() => break,
                    res = sock.recv_from(&mut buf) => {
                        let Ok((n, src)) = res else { continue };
                        if n < 32 { continue; }
                        if (buf[1] & 0x7F) != 0x52 { continue; }
                        let recv_ntp = now_ntp_timestamp();
                        let mut resp = [0u8; 32];
                        resp[0] = 0x80;
                        resp[1] = 0xD3; // Timing Response (0x53 | 0x80)
                        resp[2..4].copy_from_slice(&[0x00, 0x07]);
                        // Copy reference timestamp (client's transmit timestamp bytes 24..32 -> bytes 8..16)
                        resp[8..16].copy_from_slice(&buf[24..32]);
                        resp[16..24].copy_from_slice(&recv_ntp.to_be_bytes());
                        let send_ntp = now_ntp_timestamp();
                        resp[24..32].copy_from_slice(&send_ntp.to_be_bytes());
                        let _ = sock.send_to(&resp, src).await;
                    }
                }
            }
        });

        Ok(Self { local_port, stop_tx })
    }

    pub fn stop(&self) {
        let _ = self.stop_tx.send(true);
    }
}
