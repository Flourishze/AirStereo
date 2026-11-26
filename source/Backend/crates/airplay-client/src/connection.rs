use crate::events_channel::{parse_event_remote_command, parse_event_volume, ReceiverEvent};
use airplay_audio::{alac_magic_cookie, RtpCipherMode, RtpTarget};
use airplay_core::{AirPlayError, AudioCodec, AudioFormat, DeviceDescriptor, Result};
use airplay_crypto::chacha::HapStreamCipher;
use airplay_crypto::hkdf_sha512_32;
use airplay_pairing::pair_setup::PairSetupClient;
use airplay_pairing::HapSessionKeys;
use airplay_rtsp::{RtspConnection, RtspRequest, RtspSession};
use plist::{Dictionary, Value};
use std::net::{IpAddr, SocketAddr};
use std::time::{Duration, Instant};

/// Lightweight plaintext `GET /info` probe returning `(model, external_active)` without interrupting playback.
pub async fn probe_receiver_info(ip: IpAddr, port: u16) -> Result<(String, bool)> {
    let mut conn = RtspConnection::connect(SocketAddr::new(ip, port), Duration::from_millis(1500)).await?;
    let req = RtspRequest::new("GET", "/info")
        .header("CSeq", "1")
        .header("User-Agent", "AirPlay/700.1");
    let resp = conn.send_request(&req, Duration::from_millis(1500)).await?;
    if resp.status_code != 200 {
        return Err(AirPlayError::Rtsp {
            status: resp.status_code,
            message: format!("GET /info probe returned {}", resp.status_code),
        });
    }
    let plist_val = resp.body_as_plist()?;
    let dict = plist_val.as_dictionary();
    let model = dict
        .and_then(|d| d.get("model"))
        .and_then(|v| v.as_string())
        .unwrap_or("")
        .to_string();
    let status_flags = dict
        .and_then(|d| d.get("statusFlags"))
        .and_then(|v| v.as_unsigned_integer())
        .unwrap_or(0);
    // HomePod OS 27 flips bit 22 (0x400000) when an external AirPlay / SharedGroup session is active.
    let external_active = (status_flags & 0x400000) != 0;
    tracing::debug!(
        "GET /info probe {ip}:{port}: model={model} statusFlags=0x{status_flags:X} external_active={external_active}"
    );
    Ok((model, external_active))
}

/// Lightweight plaintext `GET /info` probe to check whether an AirPlay 2 receiver (e.g. HomePod)
/// is currently playing an external session (`statusFlags & 0x400000`, bit 22).
pub async fn probe_receiver_external_active(ip: IpAddr, port: u16) -> Result<bool> {
    probe_receiver_info(ip, port).await.map(|(_, active)| active)
}

#[derive(Debug, Clone)]
pub struct AirPlay2SetupResult {
    pub rtp_target: RtpTarget,
    pub latency_min_samples: u32,
    pub event_port: u16,
}

/// Full AirPlay 2 RTSP/HAP/PTP client matching Apple HomePod (`AudioAccessory*`, os 27+)
/// and AirPlay 2 receiver handshake, supporting both 44.1 kHz and 48 kHz.
pub struct AirPlay2Connection {
    pub device: DeviceDescriptor,
    pub ip: IpAddr,
    session: RtspSession,
    keys: HapSessionKeys,
    pub shared_audio_key: [u8; 32],
    pub ssrc: u32,
    pub session_uuid: String,
    event_stop_tx: Option<tokio::sync::watch::Sender<bool>>,
}

impl AirPlay2Connection {
    pub async fn connect_and_pair(device: DeviceDescriptor, ip: IpAddr, pin: Option<&str>) -> Result<Self> {
        let port = device.airplay_port;
        let ssrc: u32 = rand::random();
        let mut session = RtspSession::connect(SocketAddr::new(ip, port), ssrc).await?;

        // 1. GET /info
        let info_resp = session.request(RtspRequest::new("GET", "/info")).await?;
        tracing::info!("AirPlay2 GET /info {ip}:{port} -> {}", info_resp.status_code);
        let live_pw_required = info_resp
            .body_as_plist()
            .ok()
            .and_then(|v| {
                let d = v.as_dictionary()?;
                let sf = d.get("statusFlags").and_then(|x| x.as_unsigned_integer()).unwrap_or(0);
                Some((sf & 0x80) != 0)
            })
            .unwrap_or(device.password_required);

        // 2. POST /pair-pin-start (required before transient/PIN /pair-setup on HomePod)
        let _ = session.post_pair_pin_start().await;

        // 3. HAP Transient Pair-Setup (or PIN / Password Pair-Setup when credential supplied)
        let mut ps = match pin {
            Some(p) => PairSetupClient::new_with_pin(p, false, b"AirStereo2-Client".to_vec())?,
            None => PairSetupClient::new_transient()?,
        };
        let m1 = ps.build_m1();
        let m2_resp = session.post_tlv("/pair-setup", m1).await?;
        if m2_resp.status_code == 401 || m2_resp.status_code == 403 || m2_resp.status_code == 470 {
            if live_pw_required {
                return Err(AirPlayError::AuthRequired("PASSWORD".into()));
            }
            return Err(AirPlayError::AuthRequired("PIN".into()));
        }
        if m2_resp.status_code != 200 {
            return Err(AirPlayError::Rtsp {
                status: m2_resp.status_code,
                message: format!("Pair-Setup M1 failed: {}", m2_resp.reason),
            });
        }
        let m3 = ps.process_m2_build_m3(&m2_resp.body)?;
        let m4_resp = session.post_tlv("/pair-setup", m3).await?;
        if m4_resp.status_code != 200 {
            return Err(AirPlayError::Rtsp {
                status: m4_resp.status_code,
                message: format!("Pair-Setup M3 failed: {}", m4_resp.reason),
            });
        }

        let keys = if pin.is_some() {
            let m5 = ps.build_m5(&m4_resp.body)?;
            let m6_resp = session.post_tlv("/pair-setup", m5).await?;
            if m6_resp.status_code != 200 {
                return Err(AirPlayError::AuthFailed("Pair-Setup M5 rejected".into()));
            }
            let (_ltpk, k) = ps.process_m6(&m6_resp.body)?;
            k
        } else {
            ps.process_m4_transient(&m4_resp.body)?
        };
        session.enable_encryption(keys.into_stream_cipher());
        tracing::info!("AirPlay2 paired with {ip}:{port}, control encryption enabled");

        let shared_audio_key = hkdf_sha512_32(
            &keys.shared_secret,
            b"Events-Salt",
            b"Events-Write-Encryption-Key",
        )?;
        let session_uuid = random_uuid_upper();

        Ok(Self {
            device,
            ip,
            session,
            keys,
            shared_audio_key,
            ssrc,
            session_uuid,
            event_stop_tx: None,
        })
    }

    #[allow(clippy::too_many_arguments)]
    pub async fn setup_realtime_ptp(
        &mut self,
        format: AudioFormat,
        ptp_clock_id: u64,
        group_uuid: &str,
        peer_ips: &[IpAddr],
        local_control_port: u16,
        latency_min_samples: u32,
        latency_max_samples: u32,
        start_seq: u16,
        start_rtptime: u32,
        target_id: u32,
        event_tx: Option<tokio::sync::mpsc::UnboundedSender<ReceiverEvent>>,
    ) -> Result<AirPlay2SetupResult> {
        let local_ip = self.session.local_addr()?.ip().to_string();
        let uri = self.session.session_uri.clone();

        // Phase 1 SETUP: session + PTP timingPeerInfo / timingPeerList
        let mut peer_info = Dictionary::new();
        peer_info.insert("ID".into(), Value::String(random_uuid_upper()));
        peer_info.insert("DeviceType".into(), Value::Integer(0.into()));
        peer_info.insert("ClockID".into(), Value::Integer((ptp_clock_id as i64).into()));
        peer_info.insert("SupportsClockPortMatchingOverride".into(), Value::Boolean(false));
        peer_info.insert(
            "Addresses".into(),
            Value::Array(vec![Value::String(local_ip.clone())]),
        );

        let mut p1 = Dictionary::new();
        p1.insert("deviceID".into(), Value::String("02:57:32:41:50:01".into()));
        p1.insert("macAddress".into(), Value::String("02:57:32:41:50:01".into()));
        p1.insert("name".into(), Value::String("AirStereo2".into()));
        p1.insert("sessionUUID".into(), Value::String(self.session_uuid.clone()));
        p1.insert("groupUUID".into(), Value::String(group_uuid.to_string()));
        p1.insert("timingProtocol".into(), Value::String("PTP".into()));
        p1.insert("isMultiSelectAirPlay".into(), Value::Boolean(true));
        p1.insert("groupContainsGroupLeader".into(), Value::Boolean(false));
        p1.insert("senderSupportsRelay".into(), Value::Boolean(false));
        p1.insert("timingPeerInfo".into(), Value::Dictionary(peer_info.clone()));
        p1.insert(
            "timingPeerList".into(),
            Value::Array(vec![Value::Dictionary(peer_info)]),
        );

        let mut buf1 = Vec::new();
        plist::to_writer_binary(&mut buf1, &Value::Dictionary(p1)).map_err(|e| AirPlayError::Rtsp {
            status: 0,
            message: format!("Serialize SETUP1 plist failed: {e}"),
        })?;
        let req1 = RtspRequest::new("SETUP", &uri).body("application/x-apple-binary-plist", buf1);
        let resp1 = self.session.request(req1).await?;
        if resp1.status_code != 200 {
            return Err(AirPlayError::Rtsp {
                status: resp1.status_code,
                message: format!("SETUP Phase 1 failed: {} {}", resp1.status_code, resp1.reason),
            });
        }
        let event_port = resp1
            .body_as_plist()
            .ok()
            .and_then(|v| v.as_dictionary()?.get("eventPort")?.as_unsigned_integer())
            .unwrap_or(0) as u16;
        tracing::info!("AirPlay2 SETUP1 OK for {} (eventPort={event_port})", self.ip);

        // SETPEERS before SETUP Phase 2 (including local IP!)
        let mut peers: Vec<Value> = peer_ips
            .iter()
            .map(|p| Value::String(p.to_string()))
            .collect();
        if !peers.iter().any(|v| v.as_string() == Some(local_ip.as_str())) {
            peers.push(Value::String(local_ip));
        }
        let mut pbuf = Vec::new();
        if plist::to_writer_binary(&mut pbuf, &Value::Array(peers)).is_ok() {
            let sp_req = RtspRequest::new("SETPEERS", &uri)
                .body("application/x-apple-binary-plist", pbuf);
            let sp_resp = self.session.request(sp_req).await?;
            tracing::info!("AirPlay2 SETPEERS {} -> {}", self.ip, sp_resp.status_code);
        }

        // Phase 2 SETUP: realtime audio stream (type=96, isMedia=true, asc=24B MagicCookie for ALAC)
        let mut stream = Dictionary::new();
        stream.insert("type".into(), Value::Integer(96.into()));
        stream.insert("audioMode".into(), Value::String("default".into()));
        stream.insert("isMedia".into(), Value::Boolean(true));
        stream.insert("ct".into(), Value::Integer(format.codec.airplay2_ct().into()));
        stream.insert(
            "audioFormat".into(),
            Value::Integer(format.codec.airplay2_audio_format(format.sample_rate).into()),
        );
        stream.insert("sr".into(), Value::Integer(format.sample_rate.hz().into()));
        stream.insert("spf".into(), Value::Integer(format.frames_per_packet.into()));
        stream.insert("shk".into(), Value::Data(self.shared_audio_key.to_vec()));
        stream.insert("controlPort".into(), Value::Integer((local_control_port as u64).into()));
        stream.insert("latencyMin".into(), Value::Integer((latency_min_samples as u64).into()));
        stream.insert(
            "latencyMax".into(),
            Value::Integer((latency_max_samples.max(latency_min_samples) as u64).into()),
        );
        stream.insert("supportsDynamicStreamID".into(), Value::Boolean(false));
        stream.insert("streamConnectionID".into(), Value::Integer((self.ssrc as u64).into()));
        if format.codec == AudioCodec::Alac {
            stream.insert(
                "asc".into(),
                Value::Data(alac_magic_cookie(format.sample_rate).to_vec()),
            );
        }

        let mut p2 = Dictionary::new();
        p2.insert(
            "streams".into(),
            Value::Array(vec![Value::Dictionary(stream)]),
        );
        let mut buf2 = Vec::new();
        plist::to_writer_binary(&mut buf2, &Value::Dictionary(p2)).map_err(|e| AirPlayError::Rtsp {
            status: 0,
            message: format!("Serialize SETUP2 plist failed: {e}"),
        })?;
        let req2 = RtspRequest::new("SETUP", &uri).body("application/x-apple-binary-plist", buf2);
        let resp2 = self.session.request(req2).await?;
        if resp2.status_code == 415 || resp2.status_code == 400 {
            return Err(AirPlayError::CodecUnsupported(format!(
                "Receiver rejected {} @ {} Hz (status {})",
                format.codec,
                format.sample_rate.hz(),
                resp2.status_code
            )));
        }
        if resp2.status_code != 200 {
            return Err(AirPlayError::Rtsp {
                status: resp2.status_code,
                message: format!("SETUP Phase 2 failed: {} {}", resp2.status_code, resp2.reason),
            });
        }
        let plist2 = resp2.body_as_plist()?;
        let streams = plist2
            .as_dictionary()
            .and_then(|d| d.get("streams"))
            .and_then(|s| s.as_array())
            .and_then(|a| a.first())
            .and_then(|v| v.as_dictionary())
            .ok_or_else(|| AirPlayError::Rtsp {
                status: 200,
                message: "SETUP Phase 2 response missing streams[0]".into(),
            })?;
        let data_port = streams
            .get("dataPort")
            .and_then(|v| v.as_unsigned_integer())
            .ok_or_else(|| AirPlayError::Rtsp {
                status: 200,
                message: "SETUP Phase 2 response missing dataPort".into(),
            })? as u16;
        let control_port = streams
            .get("controlPort")
            .and_then(|v| v.as_unsigned_integer())
            .unwrap_or(0) as u16;
        let latency_min_samples = streams
            .get("latencyMin")
            .and_then(|v| v.as_unsigned_integer())
            .unwrap_or(latency_min_samples as u64) as u32;
        tracing::info!(
            "AirPlay2 SETUP2 OK for {}: dataPort={data_port} controlPort={control_port} latencyMin={latency_min_samples}",
            self.ip
        );

        // Open encrypted Event Channel BEFORE RECORD (required by HomePod!)
        if event_port > 0 {
            self.start_event_channel(event_port, event_tx).await?;
        }

        // RECORD + FLUSH with Range and RTP-Info
        let rtp_info = format!("seq={start_seq};rtptime={start_rtptime}");
        let rec_req = RtspRequest::new("RECORD", &uri)
            .header("Range", "npt=0-")
            .header("RTP-Info", &rtp_info);
        let rec_resp = self.session.request(rec_req).await?;
        if rec_resp.status_code != 200 {
            return Err(AirPlayError::Rtsp {
                status: rec_resp.status_code,
                message: format!("RECORD failed: {} {}", rec_resp.status_code, rec_resp.reason),
            });
        }
        let flush_req = RtspRequest::new("FLUSH", &uri)
            .header("Range", "npt=0-")
            .header("RTP-Info", &rtp_info);
        let _ = self.session.request(flush_req).await;
        tracing::info!("AirPlay2 RECORD + FLUSH OK for {}", self.ip);

        Ok(AirPlay2SetupResult {
            rtp_target: RtpTarget {
                id: target_id,
                data_addr: SocketAddr::new(self.ip, data_port),
                control_addr: if control_port > 0 {
                    Some(SocketAddr::new(self.ip, control_port))
                } else {
                    None
                },
                cipher: RtpCipherMode::AirPlay2ChaCha20Poly1305 {
                    key: self.shared_audio_key,
                },
                ssrc: self.ssrc,
            },
            latency_min_samples,
            event_port,
        })
    }

    async fn start_event_channel(
        &mut self,
        event_port: u16,
        event_tx: Option<tokio::sync::mpsc::UnboundedSender<ReceiverEvent>>,
    ) -> Result<()> {
        let mut ev_conn = RtspConnection::connect(SocketAddr::new(self.ip, event_port), Duration::from_secs(5)).await?;
        // Event channel direction: receiver writes with Events-Write, reads with Events-Read ->
        // sender writes with Events-Read and reads with Events-Write!
        let (events_write, events_read) = self.keys.derive_events_keys()?;
        ev_conn.enable_encryption(HapStreamCipher::new(events_read, events_write));

        let (tx, mut rx) = tokio::sync::watch::channel(false);
        self.event_stop_tx = Some(tx);
        let ip = self.ip;
        tokio::spawn(async move {
            loop {
                tokio::select! {
                    _ = rx.changed() => break,
                    res = ev_conn.read_incoming_message(Duration::from_secs(15)) => {
                        match res {
                            Ok(Some((first_line, cseq, body))) => {
                                // Exact reply matching decompiled `events_channel.c 0x140057830`
                                let mut reply = String::from("RTSP/1.0 200 OK\r\nServer: AirTunes/550.10\r\n");
                                if let Some(cs) = cseq {
                                    reply.push_str(&format!("CSeq: {cs}\r\n"));
                                }
                                reply.push_str("\r\n");
                                let _ = ev_conn.write_raw(reply.as_bytes()).await;

                                if let Some(etx) = &event_tx {
                                    if let Some(vol_pct) = parse_event_volume(&body) {
                                        let _ = etx.send(ReceiverEvent::Volume(ip, vol_pct));
                                    }
                                    if let Some(cmd) = parse_event_remote_command(&first_line, &body) {
                                        let _ = etx.send(ReceiverEvent::RemoteCommand(ip, cmd));
                                    }
                                }

                                if first_line.starts_with("TEARDOWN") {
                                    tracing::warn!("Event channel received TEARDOWN from {ip}");
                                    if let Some(etx) = &event_tx {
                                        let _ = etx.send(ReceiverEvent::Preempted(ip));
                                    }
                                    break;
                                }
                            }
                            Ok(None) => continue,
                            Err(e) => {
                                if !*rx.borrow() {
                                    tracing::warn!("Event channel closed unexpectedly for {ip} ({e})");
                                    if let Some(etx) = &event_tx {
                                        let _ = etx.send(ReceiverEvent::Preempted(ip));
                                    }
                                }
                                break;
                            }
                        }
                    }
                }
            }
        });
        Ok(())
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

    pub async fn get_volume_percent(&mut self) -> Result<Option<i32>> {
        let uri = self.session.session_uri.clone();
        let req = RtspRequest::new("GET_PARAMETER", &uri)
            .body("text/parameters", b"volume\r\n".to_vec());
        let resp = self.session.request(req).await?;
        if resp.status_code != 200 {
            return Ok(None);
        }
        let text = String::from_utf8_lossy(&resp.body);
        for line in text.lines() {
            if let Some(val) = line.strip_prefix("volume:") {
                if let Ok(db) = val.trim().parse::<f32>() {
                    let pct = if db <= -140.0 {
                        0
                    } else {
                        (((db + 30.0) / 30.0).clamp(0.0, 1.0) * 100.0).round() as i32
                    };
                    return Ok(Some(pct));
                }
            }
        }
        Ok(None)
    }

    pub async fn feedback_rtt_ms(&mut self) -> Result<f64> {
        let t0 = Instant::now();
        let req = RtspRequest::new("POST", "/feedback");
        let resp = self.session.request(req).await?;
        let rtt_ms = t0.elapsed().as_secs_f64() * 1000.0;
        if resp.status_code != 200 {
            return Err(AirPlayError::Rtsp {
                status: resp.status_code,
                message: format!("Feedback returned {}", resp.status_code),
            });
        }
        Ok(rtt_ms)
    }

    pub async fn teardown(&mut self) -> Result<()> {
        if let Some(tx) = self.event_stop_tx.take() {
            let _ = tx.send(true);
        }
        let uri = self.session.session_uri.clone();
        let req = RtspRequest::new("TEARDOWN", &uri);
        let _ = self.session.request(req).await;
        Ok(())
    }
}

fn random_uuid_upper() -> String {
    format!(
        "{:08X}-{:04X}-4{:03X}-8{:03X}-{:012X}",
        rand::random::<u32>(),
        rand::random::<u16>(),
        rand::random::<u16>() & 0x0FFF,
        rand::random::<u16>() & 0x0FFF,
        rand::random::<u64>() & 0xFFFF_FFFF_FFFF
    )
}
