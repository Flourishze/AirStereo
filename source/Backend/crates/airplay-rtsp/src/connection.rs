use crate::{RtspRequest, RtspResponse};
use airplay_core::{AirPlayError, Result};
use airplay_crypto::chacha::HapStreamCipher;
use std::net::SocketAddr;
use std::time::Duration;
use tokio::io::{AsyncReadExt, AsyncWriteExt};
use tokio::net::TcpStream;

/// Async RTSP/1.0 TCP transport that transparently upgrades to HAP ChaCha20-Poly1305 framing after pairing.
pub struct RtspConnection {
    stream: TcpStream,
    cipher: Option<HapStreamCipher>,
    read_buf: Vec<u8>,
}

impl RtspConnection {
    pub async fn connect(addr: SocketAddr, timeout: Duration) -> Result<Self> {
        let stream = tokio::time::timeout(timeout, TcpStream::connect(addr))
            .await
            .map_err(|_| AirPlayError::Timeout(format!("TCP connect to {addr} timed out")))??;
        let _ = stream.set_nodelay(true);
        Ok(Self {
            stream,
            cipher: None,
            read_buf: Vec::with_capacity(8192),
        })
    }

    pub fn enable_encryption(&mut self, cipher: HapStreamCipher) {
        self.cipher = Some(cipher);
    }

    pub fn local_addr(&self) -> Result<SocketAddr> {
        Ok(self.stream.local_addr()?)
    }

    pub async fn send_request(&mut self, req: &RtspRequest, timeout: Duration) -> Result<RtspResponse> {
        let raw = req.encode();
        let wire = match &mut self.cipher {
            Some(c) => c.encrypt_frame(&raw)?,
            None => raw,
        };
        tokio::time::timeout(timeout, self.stream.write_all(&wire))
            .await
            .map_err(|_| AirPlayError::Timeout(format!("RTSP write {} timed out", req.method)))??;
        tokio::time::timeout(timeout, self.read_response())
            .await
            .map_err(|_| AirPlayError::Timeout(format!("RTSP read {} timed out", req.method)))?
    }

    async fn read_more(&mut self) -> Result<usize> {
        if let Some(cipher) = &mut self.cipher {
            let mut len_hdr = [0u8; 2];
            self.stream.read_exact(&mut len_hdr).await?;
            let ct_len = u16::from_le_bytes(len_hdr) as usize + 16;
            let mut ct = vec![0u8; ct_len];
            self.stream.read_exact(&mut ct).await?;
            let pt = cipher.decrypt_chunk(len_hdr, &ct)?;
            let n = pt.len();
            self.read_buf.extend_from_slice(&pt);
            Ok(n)
        } else {
            let mut tmp = [0u8; 4096];
            let n = self.stream.read(&mut tmp).await?;
            if n == 0 {
                return Err(AirPlayError::Disconnected("RTSP peer closed connection (EOF)".into()));
            }
            self.read_buf.extend_from_slice(&tmp[..n]);
            Ok(n)
        }
    }

    async fn read_response(&mut self) -> Result<RtspResponse> {
        let header_end = loop {
            if let Some(pos) = find_header_end(&self.read_buf) {
                break pos;
            }
            self.read_more().await?;
        };
        let head = self.read_buf[..header_end].to_vec();
        self.read_buf.drain(..header_end + 4);
        let mut resp = RtspResponse::parse_head(&head)?;
        let content_len = resp.content_length();
        while self.read_buf.len() < content_len {
            self.read_more().await?;
        }
        if content_len > 0 {
            resp.body = self.read_buf.drain(..content_len).collect();
        }
        Ok(resp)
    }

    /// Send raw bytes (encrypted if cipher enabled) — used for Event Channel RTSP 200 OK replies.
    pub async fn write_raw(&mut self, data: &[u8]) -> Result<()> {
        let wire = match &mut self.cipher {
            Some(c) => c.encrypt_frame(data)?,
            None => data.to_vec(),
        };
        self.stream.write_all(&wire).await?;
        Ok(())
    }

    /// Read one incoming RTSP message (request or event) from the receiver on the Event Channel.
    pub async fn read_incoming_message(
        &mut self,
        timeout: Duration,
    ) -> Result<Option<(String, Option<String>, Vec<u8>)>> {
        let header_end = loop {
            if let Some(pos) = find_header_end(&self.read_buf) {
                break pos;
            }
            match tokio::time::timeout(timeout, self.read_more()).await {
                Ok(Ok(_)) => {}
                Ok(Err(e)) => return Err(e),
                Err(_) => return Ok(None),
            }
        };
        let head = self.read_buf[..header_end].to_vec();
        self.read_buf.drain(..header_end + 4);
        let head_str = String::from_utf8_lossy(&head);
        let mut lines = head_str.split("\r\n");
        let first_line = lines.next().unwrap_or("").to_string();
        let mut cseq = None;
        let mut content_len: usize = 0;
        for line in lines {
            if let Some((k, v)) = line.split_once(':') {
                if k.trim().eq_ignore_ascii_case("cseq") {
                    cseq = Some(v.trim().to_string());
                } else if k.trim().eq_ignore_ascii_case("content-length") {
                    content_len = v.trim().parse::<usize>().unwrap_or(0).min(65_536);
                }
            }
        }
        while self.read_buf.len() < content_len {
            self.read_more().await?;
        }
        let body = if content_len > 0 {
            self.read_buf.drain(..content_len).collect()
        } else {
            Vec::new()
        };
        Ok(Some((first_line, cseq, body)))
    }
}

fn find_header_end(buf: &[u8]) -> Option<usize> {
    buf.windows(4).position(|w| w == b"\r\n\r\n")
}
