use airplay_audio::{RtpCipherMode, RtpTarget};
use airplay_core::{AirPlayError, AudioFormat, DeviceDescriptor, Result};
use airplay_rtsp::{build_raop_announce_sdp, RtspRequest, RtspSession};
use std::net::{IpAddr, SocketAddr};
use std::time::Instant;

#[derive(Debug, Clone)]
pub struct RaopSetupResult {
    pub rtp_target: RtpTarget,
    pub latency_samples: u32,
}

/// Classic RAOP (AirPlay 1) client supporting unencrypted ALAC & PCM receivers
/// (e.g. Yamaha AV receivers, Denon/Marantz, Shairport-sync, third-party AirPlay 1 speakers).
/// Rejects Apple AirPlay 2-only devices (HomePod / modern Apple TV) that silently drop unencrypted RAOP audio.
pub struct RaopConnection {
    pub device: DeviceDescriptor,
    pub ip: IpAddr,
    session: RtspSession,
    session_id: u32,
}

impl RaopConnection {
    pub async fn connect(device: DeviceDescriptor, ip: IpAddr) -> Result<Self> {
        if device.is_homepod() || device.is_apple_tv() {
            return Err(AirPlayError::Unsupported(format!(
                "{} 仅支持加密 AirPlay 2 协议，不支持无加密 AirPlay 1 (RAOP)",
                if device.model.is_empty() { "此 Apple 设备" } else { &device.model }
            )));
        }
        let port = device.raop_port.unwrap_or(device.airplay_port);
        let session_id: u32 = rand::random();
        let mut session = RtspSession::connect(SocketAddr::new(ip, port), session_id).await?;

        // Probe GET /info when available to catch HomePod / Apple TV even when descriptor stripped model/ft
        if let Ok(info_resp) = session.request(RtspRequest::new("GET", "/info")).await {
            if info_resp.status_code == 200 {
                if let Ok(plist_val) = info_resp.body_as_plist() {
                    if let Some(model) = plist_val
                        .as_dictionary()
                        .and_then(|d| d.get("model"))
                        .and_then(|v| v.as_string())
                    {
                        if model.starts_with("AudioAccessory") || model.starts_with("AppleTV") {
                            return Err(AirPlayError::Unsupported(format!(
                                "{model} 仅支持加密 AirPlay 2 (HAP/FairPlay)，不支持无加密 AirPlay 1 (RAOP)"
                            )));
                        }
                    }
                }
            }
        }

        // Check if receiver explicitly advertises encryption types without unencrypted mode (0)
        if !device.encryption_types.is_empty() && !device.encryption_types.contains(&0) {
            return Err(AirPlayError::Unsupported(
                "此设备要求 FairPlay/RSA 加密握手，不支持无加密 Classic AirPlay 1 (RAOP)".into(),
            ));
        }

        Ok(Self {
            device,
            ip,
            session,
            session_id,
        })
    }

    pub async fn setup(
        &mut self,
        format: AudioFormat,
        local_control_port: u16,
        local_timing_port: u16,
        start_seq: u16,
        start_rtptime: u32,
        target_id: u32,
    ) -> Result<RaopSetupResult> {
        let local_ip = self.session.local_addr()?.ip().to_string();
        let remote_ip = self.ip.to_string();
        let uri = self.session.session_uri.clone();

        // 1. OPTIONS
        let _ = self.session.request(RtspRequest::new("OPTIONS", "*")).await;

        // 2. ANNOUNCE (SDP)
        let sdp = build_raop_announce_sdp(self.session_id, &local_ip, &remote_ip, format, None, None);
        let ann_req = RtspRequest::new("ANNOUNCE", &uri).body("application/sdp", sdp.into_bytes());
        let ann_resp = self.session.request(ann_req).await?;
        if ann_resp.status_code == 401 || ann_resp.status_code == 403 {
            return Err(AirPlayError::AuthRequired("RAOP receiver requires authentication".into()));
        }
        if ann_resp.status_code != 200 {
            return Err(AirPlayError::Rtsp {
                status: ann_resp.status_code,
                message: format!("RAOP ANNOUNCE failed: {} {}", ann_resp.status_code, ann_resp.reason),
            });
        }

        // 3. SETUP (standard UDP Transport without TCP interleaved=0-1)
        let transport = format!(
            "RTP/AVP/UDP;unicast;mode=record;control_port={local_control_port};timing_port={local_timing_port}"
        );
        let setup_req = RtspRequest::new("SETUP", &uri).header("Transport", transport);
        let setup_resp = self.session.request(setup_req).await?;
        if setup_resp.status_code != 200 {
            return Err(AirPlayError::Rtsp {
                status: setup_resp.status_code,
                message: format!("RAOP SETUP failed: {} {}", setup_resp.status_code, setup_resp.reason),
            });
        }
        let resp_transport = setup_resp.header("transport").ok_or_else(|| AirPlayError::Rtsp {
            status: 200,
            message: "RAOP SETUP response missing Transport header".into(),
        })?;
        let server_port = parse_transport_port(resp_transport, "server_port").ok_or_else(|| {
            AirPlayError::Rtsp {
                status: 200,
                message: format!("RAOP Transport missing server_port: {resp_transport}"),
            }
        })?;
        let control_port = parse_transport_port(resp_transport, "control_port");

        // 4. RECORD
        let rec_req = RtspRequest::new("RECORD", &uri)
            .header("Range", "npt=0-")
            .header("RTP-Info", format!("seq={start_seq};rtptime={start_rtptime}"));
        let rec_resp = self.session.request(rec_req).await?;
        if rec_resp.status_code != 200 {
            return Err(AirPlayError::Rtsp {
                status: rec_resp.status_code,
                message: format!("RAOP RECORD failed: {} {}", rec_resp.status_code, rec_resp.reason),
            });
        }
        let latency_samples = rec_resp
            .header("audio-latency")
            .and_then(|s| s.trim().parse::<u32>().ok())
            .unwrap_or(11025);

        Ok(RaopSetupResult {
            rtp_target: RtpTarget {
                id: target_id,
                data_addr: SocketAddr::new(self.ip, server_port),
                control_addr: control_port.map(|p| SocketAddr::new(self.ip, p)),
                cipher: RtpCipherMode::None,
                ssrc: 0,
            },
            latency_samples,
        })
    }

    pub async fn keepalive_rtt_ms(&mut self) -> Result<f64> {
        let t0 = Instant::now();
        let resp = self.session.request(RtspRequest::new("OPTIONS", "*")).await?;
        let rtt = t0.elapsed().as_secs_f64() * 1000.0;
        if resp.status_code != 200 {
            return Err(AirPlayError::Rtsp {
                status: resp.status_code,
                message: format!("RAOP keepalive OPTIONS returned {}", resp.status_code),
            });
        }
        Ok(rtt)
    }

    pub async fn set_volume(&mut self, volume_0_to_1: f32) -> Result<()> {
        let db = if volume_0_to_1 <= 0.001 {
            -144.0f32
        } else {
            -30.0 + volume_0_to_1.clamp(0.0, 1.0) * 30.0
        };
        let uri = self.session.session_uri.clone();
        let req = RtspRequest::new("SET_PARAMETER", &uri)
            .body("text/parameters", format!("volume: {db:.6}\r\n").into_bytes());
        let _ = self.session.request(req).await?;
        Ok(())
    }

    pub async fn teardown(&mut self) -> Result<()> {
        let uri = self.session.session_uri.clone();
        let _ = self.session.request(RtspRequest::new("TEARDOWN", &uri)).await;
        Ok(())
    }
}

fn parse_transport_port(header: &str, key: &str) -> Option<u16> {
    for part in header.split(';') {
        if let Some((k, v)) = part.trim().split_once('=') {
            if k.trim().eq_ignore_ascii_case(key) {
                return v.trim().parse::<u16>().ok();
            }
        }
    }
    None
}
