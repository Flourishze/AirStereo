use crate::{RtspConnection, RtspRequest, RtspResponse};
use airplay_core::Result;
use airplay_crypto::chacha::HapStreamCipher;
use std::net::SocketAddr;
use std::time::Duration;

/// High-level RTSP session tracking CSeq, DACP-ID, Active-Remote, and Session ID.
pub struct RtspSession {
    conn: RtspConnection,
    cseq: u32,
    pub session_uri: String,
    pub dacp_id: String,
    pub active_remote: String,
    pub timeout: Duration,
}

impl RtspSession {
    pub async fn connect(addr: SocketAddr, session_id: u32) -> Result<Self> {
        let conn = RtspConnection::connect(addr, Duration::from_secs(8)).await?;
        let local_ip = conn.local_addr()?.ip();
        Ok(Self {
            conn,
            cseq: 0,
            session_uri: format!("rtsp://{local_ip}/{session_id}"),
            dacp_id: format!("{:016X}", session_id as u64 | 0xA157_0000_0000_0000),
            active_remote: (session_id | 0x1000_0000).to_string(),
            timeout: Duration::from_secs(8),
        })
    }

    pub fn enable_encryption(&mut self, cipher: HapStreamCipher) {
        self.conn.enable_encryption(cipher);
    }

    pub fn local_addr(&self) -> Result<SocketAddr> {
        self.conn.local_addr()
    }

    pub async fn request(&mut self, mut req: RtspRequest) -> Result<RtspResponse> {
        self.cseq += 1;
        req.headers.insert("CSeq".into(), self.cseq.to_string());
        req.headers
            .entry("User-Agent".into())
            .or_insert_with(|| "AirPlay/550.10".into());
        req.headers
            .entry("X-Apple-Client-Name".into())
            .or_insert_with(|| "AirStereo2".into());
        req.headers
            .entry("DACP-ID".into())
            .or_insert_with(|| self.dacp_id.clone());
        req.headers
            .entry("Client-Instance".into())
            .or_insert_with(|| self.dacp_id.clone());
        req.headers
            .entry("Active-Remote".into())
            .or_insert_with(|| self.active_remote.clone());
        self.conn.send_request(&req, self.timeout).await
    }

    pub async fn post_plist(&mut self, path: &str, value: &plist::Value) -> Result<RtspResponse> {
        let mut buf = Vec::new();
        plist::to_writer_binary(&mut buf, value).map_err(|e| airplay_core::AirPlayError::Rtsp {
            status: 0,
            message: format!("Serialize bplist failed: {e}"),
        })?;
        let req = RtspRequest::new("POST", path).body("application/x-apple-binary-plist", buf);
        self.request(req).await
    }

    pub async fn post_tlv(&mut self, path: &str, tlv: Vec<u8>) -> Result<RtspResponse> {
        let req = RtspRequest::new("POST", path)
            .header("X-Apple-HKP", "4")
            .body("application/octet-stream", tlv);
        self.request(req).await
    }

    pub async fn post_pair_pin_start(&mut self) -> Result<RtspResponse> {
        let req = RtspRequest::new("POST", "/pair-pin-start")
            .header("X-Apple-HKP", "4")
            .header("Content-Type", "application/octet-stream")
            .header("Content-Length", "0");
        self.request(req).await
    }
}
