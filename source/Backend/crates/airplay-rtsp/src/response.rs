use airplay_core::{AirPlayError, Result};
use std::collections::HashMap;

#[derive(Debug, Clone)]
pub struct RtspResponse {
    pub status_code: u16,
    pub reason: String,
    pub headers: HashMap<String, String>,
    pub body: Vec<u8>,
}

impl RtspResponse {
    pub fn header(&self, name: &str) -> Option<&str> {
        let lower = name.to_ascii_lowercase();
        self.headers.get(&lower).map(|s| s.as_str())
    }

    pub fn content_length(&self) -> usize {
        self.header("content-length")
            .and_then(|v| v.trim().parse::<usize>().ok())
            .unwrap_or(0)
    }

    pub fn parse_head(head_bytes: &[u8]) -> Result<Self> {
        let text = std::str::from_utf8(head_bytes)
            .map_err(|e| AirPlayError::Rtsp { status: 0, message: format!("Invalid UTF-8 RTSP headers: {e}") })?;
        let mut lines = text.split("\r\n");
        let status_line = lines.next().ok_or_else(|| AirPlayError::Rtsp {
            status: 0,
            message: "Empty RTSP status line".into(),
        })?;
        let mut parts = status_line.splitn(3, ' ');
        let _proto = parts.next();
        let status_code = parts
            .next()
            .and_then(|s| s.parse::<u16>().ok())
            .ok_or_else(|| AirPlayError::Rtsp {
                status: 0,
                message: format!("Malformed RTSP status line: {status_line}"),
            })?;
        let reason = parts.next().unwrap_or("").to_string();
        let mut headers = HashMap::new();
        for line in lines {
            if line.is_empty() {
                continue;
            }
            if let Some((k, v)) = line.split_once(':') {
                headers.insert(k.trim().to_ascii_lowercase(), v.trim().to_string());
            }
        }
        Ok(Self {
            status_code,
            reason,
            headers,
            body: Vec::new(),
        })
    }

    pub fn body_as_plist(&self) -> Result<plist::Value> {
        plist::from_bytes(&self.body)
            .map_err(|e| AirPlayError::Rtsp { status: self.status_code, message: format!("Invalid bplist body: {e}") })
    }
}
